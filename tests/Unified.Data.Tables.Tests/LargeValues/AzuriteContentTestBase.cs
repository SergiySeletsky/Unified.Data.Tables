using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Base of the <see cref="TableBlobContent"/> tests that run against a LOCAL Azurite (tables on :10002,
/// blobs on :10000) through <see cref="TableStorage{T}"/> and the real Azure SDK clients. As in
/// <see cref="AzuriteLargeValueIntegrationTests"/>, every test works in its own partition
/// <c>lv-{guid}</c> and its own blob container <c>lv-{guid}</c> and deletes both when it ends; no other
/// row, partition or container is touched, and the tests skip only through
/// <see cref="TestSupport.AzuriteProbe"/> (an unreachable emulator; with
/// <c>UNIFIED_TABLES_REQUIRE_AZURITE=1</c> that fails the run instead).
/// </summary>
public abstract class AzuriteContentTestBase : IAsyncLifetime
{
    /// <summary>Overrides the storage connection string the tests use (the same variable the other Azurite tests read).</summary>


    /// <summary>Why a test did not run.</summary>
    protected const string SkipReason = "Azurite (tables and blobs) is not reachable; set " + TestSupport.AzuriteConnection.Variable + " or start Azurite";

    private static readonly string ConnectionString = TestSupport.AzuriteConnection.ConnectionString;

    // Once per test run: skips only when the emulator is not there at all (see AzuriteProbe).
    private static readonly Lazy<Task<bool>> Probe =
        new(() => TestSupport.AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, TestSupport.AzuriteProbe.Required));

    private readonly HashSet<string> tables = new(StringComparer.Ordinal);
    private readonly List<MemoryCache> caches = [];

    /// <summary>This test's partition.</summary>
    protected string Pk { get; } = "lv-" + Guid.NewGuid().ToString("N");

    /// <summary>This test's blob container.</summary>
    protected string ContainerName { get; } = "lv-" + Guid.NewGuid().ToString("N");

    /// <summary>True when Azurite answered; a test starts with <c>Assert.SkipUnless(Available, SkipReason)</c>.</summary>
    protected bool Available { get; private set; }

    protected TableServiceClient TableService { get; private set; } = default!;

    protected BlobServiceClient BlobService { get; private set; } = default!;

    protected BlobContainerClient Container { get; private set; } = default!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Available = await Probe.Value;
        if (!Available) return;
        TableService = new TableServiceClient(ConnectionString);
        BlobService = new BlobServiceClient(ConnectionString);
        Container = BlobService.GetBlobContainerClient(ContainerName);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cache in caches) cache.Dispose();
        if (Available)
            await CleanUpAsync();
        GC.SuppressFinalize(this);
    }

    // Deletes this test's rows (of every table it used) and its container; nothing else.
    private async Task CleanUpAsync()
    {
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

    /// <summary>Blob offload into this test's container.</summary>
    protected BlobOffloadOptions Offload(TimeSpan? grace = null)
    {
        var options = new BlobOffloadOptions { ContainerName = ContainerName };
        if (grace is { } g) options.CleanupGrace = g;
        return options;
    }

    /// <summary>A new store (with its own cache) over this test's container; <paramref name="blobs"/> replaces the blob client.</summary>
    protected TableStorage<T> Store<T>(BlobOffloadOptions? offload = null, BlobServiceClient? blobs = null) where T : class, IEntity, new()
    {
        tables.Add(typeof(T).Name);
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        return new TableStorage<T>(TableService, cache, NullLogger<TableStorage<T>>.Instance,
            authorizer: null, new UnifiedTableStorageOptions { BlobOffload = offload ?? Offload() }, blobs ?? BlobService);
    }

    /// <summary>A blob client for the same account whose pipeline runs <paramref name="policy"/> on every request.</summary>
    protected static BlobServiceClient BlobServiceWith(Azure.Core.Pipeline.HttpPipelinePolicy policy)
    {
        var options = new BlobClientOptions();
        options.AddPolicy(policy, HttpPipelinePosition.PerCall);
        return new BlobServiceClient(ConnectionString, options);
    }

    /// <summary>The id of the row <paramref name="rk"/> in this test's partition.</summary>
    protected string Id(string rk) => Pk + "|" + rk;

    /// <summary>The blobs under this test's partition of <typeparamref name="T"/>'s table (none when the container does not exist).</summary>
    protected async Task<List<BlobItem>> BlobsAsync<T>()
    {
        var items = new List<BlobItem>();
        if (!(await Container.ExistsAsync(Ct)).Value)
            return items;
        await foreach (var item in Container.GetBlobsAsync(new GetBlobsOptions { Prefix = $"{typeof(T).Name}/{Pk}/", Traits = BlobTraits.Metadata }, Ct))
            items.Add(item);
        return items;
    }

    /// <summary>Every stored row of this test's partition of <typeparamref name="T"/>'s table.</summary>
    protected async Task<List<TableEntity>> RawRowsAsync<T>()
    {
        var rows = new List<TableEntity>();
        try
        {
            var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Pk}");
            await foreach (var row in TableService.GetTableClient(typeof(T).Name).QueryAsync<TableEntity>(filter, cancellationToken: Ct))
                rows.Add(row);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // The table does not exist yet: nothing was written.
        }
        return rows.OrderBy(r => r.RowKey, StringComparer.Ordinal).ToList();
    }
}
