using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// One face over the two stores a large-value test runs against, so the same test body proves a
/// behaviour on both: <see cref="InMemoryStorage{T}"/>, and <see cref="TableStorage{T}"/> over the LOCAL
/// Azurite through the real Azure SDK clients.
/// <para>
/// Every instance works in its own partition <c>lv-{guid}</c> and, on Azurite, its own blob container
/// <c>lv-{guid}</c>; disposing it deletes the partition's rows and the container, and nothing else is
/// touched. The Azurite face is unavailable (<see cref="OpenAsync"/> returns null, the test skips) only
/// when <see cref="TestSupport.AzuriteProbe"/> says the emulator is not there.
/// </para>
/// </summary>
internal abstract class LargeValueStores : IAsyncDisposable
{
    /// <summary>The store kind of <see cref="InMemoryStorage{T}"/>.</summary>
    internal const string Memory = "memory";

    /// <summary>The store kind of <see cref="TableStorage{T}"/> over the local Azurite.</summary>
    internal const string Azurite = "azurite";

    internal const string SkipReason = "Azurite (tables and blobs) is not reachable; set "
        + AzuriteLargeValueIntegrationTests.ConnectionStringVariable + " or start Azurite";

    private const string DevelopmentStorage = "UseDevelopmentStorage=true";

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable(AzuriteLargeValueIntegrationTests.ConnectionStringVariable) is { Length: > 0 } configured
            ? configured
            : DevelopmentStorage;

    // Once per test run: skips only when the emulator is not there at all (see AzuriteProbe).
    private static readonly Lazy<Task<bool>> Probe =
        new(() => TestSupport.AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, TestSupport.AzuriteProbe.Required));

    private protected LargeValueStores(BlobOffloadOptions? offload) => Offload = offload;

    /// <summary>This instance's partition.</summary>
    internal string Pk { get; } = "lv-" + Guid.NewGuid().ToString("N");

    /// <summary>The blob offload settings every store of this instance uses; null when offload is not configured.</summary>
    internal BlobOffloadOptions? Offload { get; }

    /// <summary>The store kind: <see cref="Memory"/> or <see cref="Azurite"/>.</summary>
    internal abstract string Kind { get; }

    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Opens the stores of <paramref name="kind"/>; null when Azurite is asked for and is not reachable.
    /// </summary>
    /// <param name="kind"><see cref="Memory"/> or <see cref="Azurite"/>.</param>
    /// <param name="offload">Whether blob offload is configured.</param>
    /// <param name="cleanupGrace">The cleanup grace (the default 5 minutes when null).</param>
    /// <param name="thresholdBytes">The offload threshold (the default 3 MiB when null).</param>
    internal static async Task<LargeValueStores?> OpenAsync(
        string kind, bool offload = true, TimeSpan? cleanupGrace = null, long? thresholdBytes = null)
    {
        if (kind == Memory)
            return new MemoryStores(Options(offload, cleanupGrace, thresholdBytes, containerName: null));
        if (kind != Azurite)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown store kind.");
        if (!await Probe.Value)
            return null;
        var container = "lv-" + Guid.NewGuid().ToString("N");
        return new AzuriteStores(Options(offload, cleanupGrace, thresholdBytes, container), container);
    }

    private static BlobOffloadOptions? Options(bool offload, TimeSpan? cleanupGrace, long? thresholdBytes, string? containerName)
    {
        if (!offload)
            return null;
        var options = new BlobOffloadOptions();
        if (containerName is not null) options.ContainerName = containerName;
        if (cleanupGrace is { } grace) options.CleanupGrace = grace;
        if (thresholdBytes is { } threshold) options.ThresholdBytes = threshold;
        return options;
    }

    /// <summary>The id of the entity at <paramref name="rowKey"/> in this instance's partition.</summary>
    internal string Id(string rowKey) => Pk + "|" + rowKey;

    /// <summary>The table <typeparamref name="T"/> is stored in: <paramref name="table"/>, or the type's name.</summary>
    internal static string TableOf<T>(string? table) => table ?? typeof(T).Name;

    private protected UnifiedTableStorageOptions StorageOptions(string? table) => new()
    {
        BlobOffload = Offload,
        TableNameResolver = table is null ? null : _ => table,
    };

    /// <summary>
    /// A store of <typeparamref name="T"/>. On Azurite every call returns a new store with an empty cache,
    /// so each operation reaches the service; in memory the one store that holds the rows.
    /// </summary>
    /// <param name="table">The table name, to store several types in one table; the type's name when null.</param>
    internal abstract IStorage<T> Store<T>(string? table = null) where T : class, IEntity, new();

    /// <summary>Every stored row of this instance's partition, continuation rows included, in row-key order.</summary>
    internal abstract Task<IReadOnlyList<TableEntity>> RawRowsAsync<T>(string? table = null) where T : class, IEntity, new();

    /// <summary>The names of the blobs stored for this instance's partition of the table, in ordinal order.</summary>
    internal abstract Task<IReadOnlyList<string>> BlobNamesAsync<T>(string? table = null) where T : class, IEntity, new();

    /// <summary>The bytes of a stored blob.</summary>
    internal abstract Task<byte[]> BlobBytesAsync<T>(string name, string? table = null) where T : class, IEntity, new();

    /// <summary>
    /// Makes what <typeparamref name="TFrom"/> wrote to <paramref name="table"/> visible to the store of
    /// <typeparamref name="TTo"/>: a real table is shared by every type mapped to it; the in-memory stores
    /// each hold their own rows and blobs, which are copied.
    /// </summary>
    internal abstract Task ShareTableAsync<TFrom, TTo>(string table)
        where TFrom : class, IEntity, new()
        where TTo : class, IEntity, new();

    /// <summary>
    /// Waits until the blob service's clock (one-second resolution) is past the newest blob of the
    /// table's partition, so a cleanup that follows judges every one of them old enough to delete.
    /// </summary>
    internal abstract Task PastTheNewestBlobAsync<T>(string? table = null) where T : class, IEntity, new();

    /// <summary>
    /// Runs <paramref name="write"/> and reports, in order, what it did to storage: <c>blob PUT {name}</c>,
    /// <c>blob DELETE {name}</c> and <c>table WRITE</c> (the row write committing). Blob reads and
    /// metadata calls are not listed.
    /// </summary>
    internal abstract Task<IReadOnlyList<string>> StorageWritesAsync<T>(IStorage<T> store, Func<Task> write) where T : class, IEntity, new();

    /// <summary>Runs <paramref name="read"/> and returns how many times it opened a blob for reading.</summary>
    internal abstract Task<int> BlobReadsAsync<T>(IStorage<T> store, Func<Task> read) where T : class, IEntity, new();

    public abstract ValueTask DisposeAsync();

    /// <summary>The chunk cells of <paramref name="column"/> on <paramref name="row"/>.</summary>
    internal static IEnumerable<string> ChunkKeys(TableEntity row, string column) =>
        row.Keys.Where(k => SplitCodec.TryParseLargeValueColumn(k, out var owner, out var kind, out _)
            && owner == column && kind == LargeValueCellKind.Chunk);

    // ── InMemoryStorage ────────────────────────────────────────────────────

    private sealed class MemoryStores(BlobOffloadOptions? offload) : LargeValueStores(offload)
    {
        private readonly Dictionary<(Type Type, string Table), object> stores = [];

        internal override string Kind => Memory;

        private InMemoryStorage<T> Of<T>(string? table) where T : class, IEntity, new()
        {
            var key = (typeof(T), TableOf<T>(table));
            if (!stores.TryGetValue(key, out var store))
                stores[key] = store = new InMemoryStorage<T>(null, StorageOptions(table));
            return (InMemoryStorage<T>)store;
        }

        internal override IStorage<T> Store<T>(string? table = null) => Of<T>(table);

        internal override Task<IReadOnlyList<TableEntity>> RawRowsAsync<T>(string? table = null)
        {
            var store = Of<T>(table);
            IReadOnlyList<TableEntity> rows = store.PhysicalRowKeys(Pk).Select(rk => store.PhysicalRow(Pk, rk)!).ToList();
            return Task.FromResult(rows);
        }

        internal override Task<IReadOnlyList<string>> BlobNamesAsync<T>(string? table = null) =>
            Task.FromResult(Of<T>(table).OffloadedBlobNames());

        internal override async Task<byte[]> BlobBytesAsync<T>(string name, string? table = null)
        {
            await using var stream = await Of<T>(table).Blobs.OpenReadAsync("test", name, Ct);
            return await OffloadTestSupport.ReadAllAsync(stream);
        }

        internal override async Task ShareTableAsync<TFrom, TTo>(string table)
        {
            var from = Of<TFrom>(table);
            var to = Of<TTo>(table);
            foreach (var rowKey in from.PhysicalRowKeys(Pk))
                to.SeedPhysicalRow(from.PhysicalRow(Pk, rowKey)!);
            foreach (var name in from.Blobs.Names)
            {
                var state = await from.Blobs.GetStateAsync(name, Ct);
                await using var stream = await from.Blobs.OpenReadAsync("test", name, Ct);
                to.Blobs.Seed(name, await OffloadTestSupport.ReadAllAsync(stream), state!.Sha256, state.LastModified);
            }
        }

        // The in-memory blob clock is strictly increasing: every blob is already older than "now".
        internal override Task PastTheNewestBlobAsync<T>(string? table = null) => Task.CompletedTask;

        internal override async Task<IReadOnlyList<string>> StorageWritesAsync<T>(IStorage<T> store, Func<Task> write)
        {
            var memory = (InMemoryStorage<T>)store;
            var log = new List<string>();
            var before = memory.Blobs.Names.ToHashSet(StringComparer.Ordinal);
            // The uploads precede the commit; the deletes of released blobs follow it.
            memory.Blobs.BeforeUpload = (name, _) =>
            {
                log.Add("blob PUT " + name);
                return Task.CompletedTask;
            };
            memory.AfterLargeCommit = () =>
            {
                log.AddRange(before.Except(memory.Blobs.Names).OrderBy(n => n, StringComparer.Ordinal).Select(n => "blob DELETE " + n));
                before = memory.Blobs.Names.ToHashSet(StringComparer.Ordinal);
                log.Add("table WRITE");
            };
            try
            {
                await write();
            }
            finally
            {
                memory.Blobs.BeforeUpload = null;
                memory.AfterLargeCommit = null;
            }
            log.AddRange(before.Except(memory.Blobs.Names).OrderBy(n => n, StringComparer.Ordinal).Select(n => "blob DELETE " + n));
            return log;
        }

        internal override async Task<int> BlobReadsAsync<T>(IStorage<T> store, Func<Task> read)
        {
            var memory = (InMemoryStorage<T>)store;
            var reads = 0;
            memory.Blobs.BeforeOpenRead = (_, _) =>
            {
                reads++;
                return Task.CompletedTask;
            };
            try
            {
                await read();
            }
            finally
            {
                memory.Blobs.BeforeOpenRead = null;
            }
            return reads;
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── TableStorage over Azurite ──────────────────────────────────────────

    private sealed class AzuriteStores : LargeValueStores
    {
        private readonly string containerName;
        private readonly List<(string Service, string Method, string Path)> requests = [];
        private readonly HashSet<string> tables = new(StringComparer.Ordinal);
        private readonly List<MemoryCache> caches = [];
        private readonly TableServiceClient tableService;
        private readonly BlobServiceClient blobService;
        private readonly BlobContainerClient container;

        internal AzuriteStores(BlobOffloadOptions? offload, string containerName)
            : base(offload)
        {
            this.containerName = containerName;
            var tableOptions = new TableClientOptions();
            tableOptions.AddPolicy(new RequestRecorder("table", requests), HttpPipelinePosition.PerCall);
            var blobOptions = new BlobClientOptions();
            blobOptions.AddPolicy(new RequestRecorder("blob", requests), HttpPipelinePosition.PerCall);
            tableService = new TableServiceClient(ConnectionString, tableOptions);
            blobService = new BlobServiceClient(ConnectionString, blobOptions);
            container = blobService.GetBlobContainerClient(containerName);
        }

        internal override string Kind => Azurite;

        internal override IStorage<T> Store<T>(string? table = null)
        {
            lock (tables) tables.Add(TableOf<T>(table));
            var cache = new MemoryCache(new MemoryCacheOptions());
            lock (caches) caches.Add(cache);
            return new TableStorage<T>(tableService, cache, NullLogger<TableStorage<T>>.Instance,
                authorizer: null, StorageOptions(table), Offload is null ? null : blobService);
        }

        internal override async Task<IReadOnlyList<TableEntity>> RawRowsAsync<T>(string? table = null)
        {
            var rows = new List<TableEntity>();
            var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Pk}");
            try
            {
                await foreach (var row in tableService.GetTableClient(TableOf<T>(table)).QueryAsync<TableEntity>(filter, cancellationToken: Ct))
                    rows.Add(row);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // The table was never created: nothing is stored.
            }
            return rows.OrderBy(r => r.RowKey, StringComparer.Ordinal).ToList();
        }

        private async Task<List<BlobItem>> BlobsAsync<T>(string? table) where T : class, IEntity, new()
        {
            var items = new List<BlobItem>();
            if (!(await container.ExistsAsync(Ct)).Value)
                return items;
            var prefix = $"{TableOf<T>(table)}/{Pk}/";
            await foreach (var item in container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, Ct))
                items.Add(item);
            return items;
        }

        internal override async Task<IReadOnlyList<string>> BlobNamesAsync<T>(string? table = null) =>
            (await BlobsAsync<T>(table)).Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        internal override async Task<byte[]> BlobBytesAsync<T>(string name, string? table = null) =>
            (await container.GetBlobClient(name).DownloadContentAsync(Ct)).Value.Content.ToArray();

        // Both types are mapped to the same real table and container.
        internal override Task ShareTableAsync<TFrom, TTo>(string table) => Task.CompletedTask;

        // A cleanup deletes a blob only when it is not newer than the commit's service time less the grace,
        // and both have one-second resolution: poll the service until its clock is past the newest blob.
        internal override async Task PastTheNewestBlobAsync<T>(string? table = null)
        {
            var blobs = await BlobsAsync<T>(table);
            if (blobs.Count == 0)
                return;
            var newest = blobs.Max(b => b.Properties.LastModified ?? throw new InvalidOperationException($"'{b.Name}' has no Last-Modified."));
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                var serviceNow = (await container.GetPropertiesAsync(cancellationToken: Ct)).GetRawResponse().Headers.Date
                    ?? throw new InvalidOperationException("The blob service sent no Date header.");
                if (serviceNow > newest)
                    return;
                Assert.True(DateTimeOffset.UtcNow < deadline,
                    $"The blob service clock ({serviceNow:O}) did not pass the newest blob's Last-Modified ({newest:O}) within 30 s.");
                await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
            }
        }

        internal override async Task<IReadOnlyList<string>> StorageWritesAsync<T>(IStorage<T> store, Func<Task> write)
        {
            lock (requests) requests.Clear();
            await write();
            var blobPrefix = "/" + containerName + "/";
            var log = new List<string>();
            lock (requests)
            {
                foreach (var (service, method, path) in requests)
                {
                    var query = path.IndexOf('?', StringComparison.Ordinal);
                    var resource = Uri.UnescapeDataString(query < 0 ? path : path[..query]);
                    var at = resource.IndexOf(blobPrefix, StringComparison.Ordinal);
                    // An upload is a PUT of the blob itself, or the block list that completes a staged one;
                    // a staged block and a touch (PUT ?comp=metadata) are not uploads.
                    var upload = method == "PUT"
                        && (!path.Contains("comp=", StringComparison.Ordinal) || path.Contains("comp=blocklist", StringComparison.Ordinal));
                    if (service == "blob" && at >= 0 && (upload || method == "DELETE"))
                        log.Add($"blob {method} {resource[(at + blobPrefix.Length)..]}");
                    else if (service == "table" && method is "PUT" or "POST" or "PATCH" or "MERGE"
                        && !resource.EndsWith("/Tables", StringComparison.Ordinal))
                        log.Add("table WRITE");
                }
            }
            return log;
        }

        internal override async Task<int> BlobReadsAsync<T>(IStorage<T> store, Func<Task> read)
        {
            lock (requests) requests.Clear();
            await read();
            lock (requests) return requests.Count(r => r.Service == "blob" && r.Method == "GET");
        }

        public override async ValueTask DisposeAsync()
        {
            lock (caches)
            {
                foreach (var cache in caches) cache.Dispose();
            }
            foreach (var table in tables)
            {
                var client = tableService.GetTableClient(table);
                try
                {
                    var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Pk}");
                    var keys = new List<string>();
                    await foreach (var row in client.QueryAsync<TableEntity>(filter, select: ["RowKey"], cancellationToken: CancellationToken.None))
                        keys.Add(row.RowKey);
                    foreach (var rk in keys)
                        await client.DeleteEntityAsync(Pk, rk, ETag.All, CancellationToken.None);
                }
                catch (RequestFailedException)
                {
                    // The table was never created, or a row is already gone.
                }
            }
            await container.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
        }

        /// <summary>Records every request's service, method and path (with its query).</summary>
        private sealed class RequestRecorder(string service, List<(string Service, string Method, string Path)> log) : HttpPipelineSynchronousPolicy
        {
            public override void OnSendingRequest(HttpMessage message)
            {
                lock (log) log.Add((service, message.Request.Method.Method, message.Request.Uri.Path + message.Request.Uri.Query));
            }
        }
    }
}
