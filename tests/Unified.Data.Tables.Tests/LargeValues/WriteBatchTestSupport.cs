using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A <see cref="TableStorage{T}"/> over the suite's in-memory <see cref="RacingTableClient"/> (real
/// versions, conditions and atomic transactions) with an in-memory blob store and a real
/// <see cref="MemoryCache"/>, so the store's own code runs unchanged and a test can count requests and act
/// between two of them. Several stores over the same rows and blobs stand for several processes.
/// </summary>
internal sealed class WriteBatchRig<T> : IDisposable where T : class, IEntity, new()
{
    private readonly List<MemoryCache> caches = [];
    private readonly BlobOffloadOptions? offload;

    public WriteBatchRig(BlobOffloadOptions? offload = null)
    {
        this.offload = offload;
        Store = NewStore();
    }

    public RacingTableClient Table { get; } = new();

    public InMemoryOffloadBlobStore Blobs { get; } = new();

    /// <summary>The store under test (it has a cache of its own).</summary>
    public TableStorage<T> Store { get; }

    /// <summary>Another store over the same table and blobs, with its own cache ("another process").</summary>
    public TableStorage<T> NewStore()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(T).Name).Returns(Table);
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null,
            new UnifiedTableStorageOptions { BlobOffload = offload }, Blobs)
        {
            // A lost race is retried at once: the tests are about what is written, not about the pause.
            LargeValueRetryDelay = (_, _) => Task.CompletedTask,
        };
    }

    public void Dispose()
    {
        foreach (var cache in caches)
            cache.Dispose();
    }
}

/// <summary>
/// The LOCAL Azurite through the real SDK clients. Every instance works in its own partition
/// <c>lv-{guid}</c>, its own blob container <c>lv-{guid}</c> and its own tables (<c>Wb{hint}{guid}</c>);
/// disposing it deletes those tables and that container and nothing else.
/// </summary>
internal sealed class WriteBatchAzurite : IAsyncDisposable
{
    internal const string SkipReason = "Azurite (tables and blobs) is not reachable; set "
        + TestSupport.AzuriteConnection.Variable + " or start Azurite";

    private static readonly string ConnectionString = TestSupport.AzuriteConnection.ConnectionString;

    private static readonly Lazy<Task<bool>> Probe =
        new(() => TestSupport.AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, TestSupport.AzuriteProbe.Required));

    private readonly List<string> tables = [];
    private readonly List<MemoryCache> caches = [];

    private WriteBatchAzurite()
    {
        Tables = new TableServiceClient(ConnectionString);
        BlobService = new BlobServiceClient(ConnectionString);
        Container = BlobService.GetBlobContainerClient(ContainerName);
    }

    public string Pk { get; } = "lv-" + Guid.NewGuid().ToString("N");

    public string ContainerName { get; } = "lv-" + Guid.NewGuid().ToString("N");

    public TableServiceClient Tables { get; }

    public BlobServiceClient BlobService { get; }

    public BlobContainerClient Container { get; }

    /// <summary>Null when the emulator is not there (the test then skips).</summary>
    public static async Task<WriteBatchAzurite?> OpenAsync() => await Probe.Value ? new WriteBatchAzurite() : null;

    public string Id(string rowKey) => Pk + "|" + rowKey;

    /// <summary>A table name of this instance only; the table is deleted when the instance is disposed.</summary>
    public string NewTable(string hint)
    {
        var name = "Wb" + hint + Guid.NewGuid().ToString("N")[..16];
        lock (tables) tables.Add(name);
        return name;
    }

    public BlobOffloadOptions Offload(TimeSpan? grace = null, long? thresholdBytes = null)
    {
        var options = new BlobOffloadOptions { ContainerName = ContainerName };
        if (grace is { } g) options.CleanupGrace = g;
        if (thresholdBytes is { } t) options.ThresholdBytes = t;
        return options;
    }

    /// <summary>A new store with a cache of its own over <paramref name="table"/> and this instance's container.</summary>
    public TableStorage<T> Store<T>(string table, BlobOffloadOptions? offload, bool cache = true)
        where T : class, IEntity, new()
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        lock (caches) caches.Add(memory);
        var options = new UnifiedTableStorageOptions { BlobOffload = offload, TableNameResolver = _ => table };
        if (!cache) options.Cache = CachePolicy.Disabled;
        return new TableStorage<T>(Tables, memory, NullLogger<TableStorage<T>>.Instance,
            authorizer: null, options, offload is null ? null : BlobService);
    }

    public async Task<List<string>> BlobNamesAsync()
    {
        var names = new List<string>();
        if (!(await Container.ExistsAsync()).Value)
            return names;
        await foreach (var item in Container.GetBlobsAsync(new GetBlobsOptions()))
            names.Add(item.Name);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Waits until every blob of the container is older than <paramref name="age"/> on the blob service's clock.</summary>
    public async Task EveryBlobIsOlderThanAsync(TimeSpan age, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var newest = DateTimeOffset.MinValue;
            await foreach (var item in Container.GetBlobsAsync(new GetBlobsOptions(), ct))
            {
                if (item.Properties.LastModified is { } modified && modified > newest)
                    newest = modified;
            }
            var serviceNow = (await Container.GetPropertiesAsync(cancellationToken: ct)).GetRawResponse().Headers.Date
                ?? throw new InvalidOperationException("The blob service sent no Date header.");
            if (serviceNow - newest > age)
                return;
            Assert.True(DateTimeOffset.UtcNow < deadline, "The blobs did not get older than " + age + " within 60 s.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (caches)
        {
            foreach (var cache in caches) cache.Dispose();
        }
        string[] mine;
        lock (tables) mine = [.. tables];
        foreach (var table in mine)
        {
            try
            {
                await Tables.DeleteTableAsync(table, CancellationToken.None);
            }
            catch (RequestFailedException)
            {
                // Never created.
            }
        }
        await Container.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
    }
}
