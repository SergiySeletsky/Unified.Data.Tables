using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Plumbing of the blob-cleanup suites that run against a LOCAL Azurite (tables on :10002, blobs on
/// :10000) through <see cref="TableStorage{T}"/> and the real Azure SDK clients, following
/// <see cref="AzuriteLargeValueIntegrationTests"/>: every test works in its own partition
/// <c>lv-{guid}</c> and its own blob container <c>lv-{guid}</c> and deletes both when it ends; no other
/// row, partition or container is touched, and no table is deleted. The tests skip only when the emulator
/// is not reachable (see <see cref="TestSupport.AzuriteProbe"/>). Stores are built with a small offload
/// threshold, so a 40,000-character value is already a blob, and with the cache disabled, so every read
/// goes to the emulator.
/// </summary>
public abstract class AzuriteCleanupTestBase : IAsyncLifetime
{
    /// <summary>Above this many stored bytes a split value is offloaded in these suites.</summary>
    protected const long SmallThreshold = 1_000;

    private const string SkipReason = "Azurite (tables and blobs) is not reachable; set "
        + TestSupport.AzuriteConnection.Variable + " or start Azurite";

    private static readonly string ConnectionString = TestSupport.AzuriteConnection.ConnectionString;

    // Once per test run: skips only when the emulator is not there at all.
    private static readonly Lazy<Task<bool>> Probe =
        new(() => TestSupport.AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, TestSupport.AzuriteProbe.Required));

    private readonly HashSet<string> tables = new(StringComparer.Ordinal);
    private readonly List<MemoryCache> caches = [];
    private bool available;

    /// <summary>This test's partition.</summary>
    protected string Pk { get; } = "lv-" + Guid.NewGuid().ToString("N");

    /// <summary>This test's blob container.</summary>
    protected string ContainerName { get; } = "lv-" + Guid.NewGuid().ToString("N");

    protected TableServiceClient TableService { get; private set; } = default!;

    protected BlobServiceClient BlobService { get; private set; } = default!;

    protected BlobContainerClient Container { get; private set; } = default!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        available = await Probe.Value;
        if (!available) return;
        TableService = new TableServiceClient(ConnectionString);
        BlobService = new BlobServiceClient(ConnectionString);
        Container = BlobService.GetBlobContainerClient(ContainerName);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        foreach (var cache in caches) cache.Dispose();
        if (!available) return;
        foreach (var table in tables)
        {
            var client = TableService.GetTableClient(table);
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
        await Container.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
    }

    protected void SkipUnlessAvailable() => Assert.SkipUnless(available, SkipReason);

    /// <summary>The id of the row <paramref name="rk"/> in this test's partition.</summary>
    protected string Id(string rk) => Pk + "|" + rk;

    /// <summary>A table client with <paramref name="policies"/> run once per call (to observe or interleave requests).</summary>
    protected static TableServiceClient TableServiceWith(params HttpPipelinePolicy[] policies)
    {
        var options = new TableClientOptions();
        foreach (var policy in policies)
            options.AddPolicy(policy, HttpPipelinePosition.PerCall);
        return new TableServiceClient(ConnectionString, options);
    }

    /// <summary>
    /// A store of <typeparamref name="T"/> over this test's container: <paramref name="grace"/> is its
    /// <see cref="BlobOffloadOptions.CleanupGrace"/> (the default when null), <paramref name="table"/>
    /// the table it reads and writes (the type's name when null), <paramref name="service"/> the table
    /// client (the plain one when null).
    /// </summary>
    protected TableStorage<T> Store<T>(
        TimeSpan? grace = null, string? table = null, TableServiceClient? service = null, ILogger<TableStorage<T>>? logger = null)
        where T : class, IEntity, new()
    {
        var offload = new BlobOffloadOptions { ContainerName = ContainerName, ThresholdBytes = SmallThreshold };
        if (grace is { } g) offload.CleanupGrace = g;
        var options = new UnifiedTableStorageOptions { BlobOffload = offload, Cache = CachePolicy.Disabled };
        if (table is not null) options.TableNameResolver = _ => table;
        tables.Add(table ?? typeof(T).Name);
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        return new TableStorage<T>(service ?? TableService, cache, logger ?? NullLogger<TableStorage<T>>.Instance,
            authorizer: null, options, BlobService);
    }

    /// <summary>
    /// A store of <typeparamref name="T"/> without blob offload: a value too large for one row is split
    /// onto continuation rows (never offloaded). <paramref name="service"/> is the table client (the
    /// plain one when null).
    /// </summary>
    protected TableStorage<T> SplitOnlyStore<T>(TableServiceClient? service = null) where T : class, IEntity, new()
    {
        tables.Add(typeof(T).Name);
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        return new TableStorage<T>(service ?? TableService, cache, NullLogger<TableStorage<T>>.Instance,
            authorizer: null, new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled });
    }

    /// <summary>The client of <paramref name="table"/>; its rows in this test's partition are deleted at the end.</summary>
    protected TableClient Table(string table)
    {
        tables.Add(table);
        return TableService.GetTableClient(table);
    }

    /// <summary>Every stored row of this test's partition in <paramref name="table"/>, continuation rows included, in key order.</summary>
    protected async Task<List<TableEntity>> RawRowsAsync(string table)
    {
        var rows = new List<TableEntity>();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Pk}");
        await foreach (var row in TableService.GetTableClient(table).QueryAsync<TableEntity>(filter, cancellationToken: Ct))
            rows.Add(row);
        return rows.OrderBy(r => r.RowKey, StringComparer.Ordinal).ToList();
    }

    /// <summary>The names of every blob in this test's container, in ordinal order (none when it does not exist).</summary>
    protected async Task<List<string>> BlobNamesAsync()
    {
        var names = new List<string>();
        if (!(await Container.ExistsAsync(Ct)).Value)
            return names;
        await foreach (var item in Container.GetBlobsAsync(cancellationToken: Ct))
            names.Add(item.Name);
        return names.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Copies the stored row <paramref name="rk"/> of <paramref name="sourceTable"/> cell for cell to the
    /// row <paramref name="newRk"/> of <paramref name="targetTable"/>, as a table copy tool or a re-keying
    /// script does: the copy still holds the source row's blob references.
    /// </summary>
    protected async Task CopyRowRawAsync(string sourceTable, string rk, string targetTable, string newRk)
    {
        var source = (await TableService.GetTableClient(sourceTable).GetEntityAsync<TableEntity>(Pk, rk, cancellationToken: Ct)).Value;
        var copy = new TableEntity(Pk, newRk);
        foreach (var (key, value) in source)
        {
            if (key is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag"))
                copy[key] = value;
        }
        var target = Table(targetTable);
        await target.CreateIfNotExistsAsync(Ct);
        await target.AddEntityAsync(copy, Ct);
    }

    /// <summary>
    /// Waits until the blob service's clock (the <c>Date</c> of its responses, whole seconds like
    /// <c>Last-Modified</c>) is at least <paramref name="age"/> past the newest blob of this test's container.
    /// </summary>
    protected async Task BlobsOlderThanAsync(TimeSpan age)
    {
        var newest = DateTimeOffset.MinValue;
        await foreach (var item in Container.GetBlobsAsync(cancellationToken: Ct))
        {
            var modified = item.Properties.LastModified ?? throw new InvalidOperationException($"'{item.Name}' has no Last-Modified.");
            if (modified > newest) newest = modified;
        }
        var deadline = DateTimeOffset.UtcNow + age + TimeSpan.FromSeconds(30);
        while (true)
        {
            var serviceNow = (await Container.GetPropertiesAsync(cancellationToken: Ct)).GetRawResponse().Headers.Date
                ?? throw new InvalidOperationException("The blob service sent no Date header.");
            if (serviceNow - newest > age)
                return;
            Assert.True(DateTimeOffset.UtcNow < deadline,
                $"The blob service clock ({serviceNow:O}) did not pass the newest blob's Last-Modified ({newest:O}) by {age} in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }
}
