using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.Conformance;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.ConformanceKit;

/// <summary>
/// The in-memory provider as the conformance kit sees it: a new <see cref="InMemoryStorage{T}"/> (or
/// <see cref="InMemoryPolymorphicStorage{TBase}"/>) per scenario, with its physical rows readable.
/// </summary>
internal sealed class InMemoryConformanceFactory : IConformanceStoreFactory
{
    public string Name => "InMemory";

    // What the in-memory provider does differently: nothing the kit has a leniency for. (The wildcard
    // ETag on a whole-entity update, B5 of the 0.10.0 plan, is refused since the Azure provider refuses it.)
    public IReadOnlyCollection<string> Leniencies { get; } = [];

    public Task<ConformanceStore<T>> CreateStoreAsync<T>(ConformanceStoreSettings settings, CancellationToken ct = default)
        where T : class, IEntity, new()
    {
        var store = Create<T>(settings);
        return Task.FromResult(new ConformanceStore<T>(store, RawRows(store)));
    }

    public Task<ConformancePolymorphicStore<TBase>> CreatePolymorphicStoreAsync<TBase>(CancellationToken ct = default)
        where TBase : class =>
        Task.FromResult(new ConformancePolymorphicStore<TBase>(new InMemoryPolymorphicStorage<TBase>("ConformanceEvents")));

    internal static InMemoryStorage<T> Create<T>(ConformanceStoreSettings settings) where T : class, IEntity, new() =>
        new(null, new StorageOptions
        {
            IdNormalization = settings.IdNormalization,
            Offload = settings.BlobOffload ? new OffloadOptions { CleanupGrace = TimeSpan.Zero } : null,
        });

    internal static ConformanceRawRowReader RawRows<T>(InMemoryStorage<T> store) where T : class, IEntity, new() =>
        (partitionKey, _) =>
        {
            IReadOnlyList<ConformanceRawRow> rows = store.PhysicalRowKeys(partitionKey)
                .Select(rowKey => store.PhysicalRow(partitionKey, rowKey))
                .Where(row => row is not null)
                .Select(row => new ConformanceRawRow(row!.PartitionKey, row.RowKey, AzuriteConformanceFactory.Cells(row)))
                .ToList();
            return Task.FromResult(rows);
        };
}

/// <summary>
/// The Azure provider as the conformance kit sees it, over the local Azurite: every scenario store is a
/// <see cref="TableStorage{T}"/> (or <see cref="PolymorphicTableStorage{TBase}"/>) on a table of its own,
/// deleted when the scenario ends, with the default read cache. When Azurite is not there the factory
/// refuses to build a store, which the kit reports as "not supported" and the xUnit adapter as a skipped
/// test, like the other Azurite suites (with <c>UNIFIED_TABLES_REQUIRE_AZURITE=1</c> the probe throws
/// instead and every test fails).
/// </summary>
internal sealed class AzuriteConformanceFactory : IConformanceStoreFactory
{
    internal const string SkipReason = "Azurite (tables and blobs) is not reachable; set "
        + TestSupport.AzuriteConnection.Variable + " or start Azurite";

    private static readonly string ConnectionString = TestSupport.AzuriteConnection.ConnectionString;

    // Once per test run: skips only when the emulator is not there at all (see AzuriteProbe).
    private static readonly Lazy<Task<bool>> Probe =
        new(() => TestSupport.AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, TestSupport.AzuriteProbe.Required));

    // Built as AddUnifiedTableStorage(connectionString) builds its client: with the retry observation, so
    // a conditional delete of a row that is not there answers NotFound, not Indeterminate.
    private static readonly Lazy<TableServiceClient> Tables =
        new(() => new TableServiceClient(ConnectionString, new TableClientOptions().AddRetryObservation()));
    private static readonly Lazy<BlobServiceClient> Blobs = new(() => new BlobServiceClient(ConnectionString));

    public string Name => "AzureTables on Azurite";

    // What differs on the Azure provider, or on the emulator it runs against here: nothing.
    public IReadOnlyCollection<string> Leniencies { get; } = [];

    /// <summary>Whether Azurite answers; the probe runs once per test run.</summary>
    internal static Task<bool> AvailableAsync() => Probe.Value;

    private static async Task RequireAzuriteAsync()
    {
        if (!await Probe.Value)
            throw new NotSupportedException(SkipReason);
    }

    private static string NewTableName() => "ck" + Guid.NewGuid().ToString("N");

    public async Task<ConformanceStore<T>> CreateStoreAsync<T>(ConformanceStoreSettings settings, CancellationToken ct = default)
        where T : class, IEntity, new()
    {
        await RequireAzuriteAsync();
        var table = NewTableName();
        var container = "ck-" + Guid.NewGuid().ToString("N");
        var cache = new MemoryCache(new MemoryCacheOptions());
        var options = new UnifiedTableStorageOptions
        {
            IdNormalization = settings.IdNormalization,
            TableNameResolver = _ => table,
            BlobOffload = settings.BlobOffload ? new BlobOffloadOptions { ContainerName = container, CleanupGrace = TimeSpan.Zero } : null,
        };
        var store = new TableStorage<T>(Tables.Value, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, options,
            settings.BlobOffload ? Blobs.Value : null);

        return new ConformanceStore<T>(store, (partitionKey, token) => RawRowsAsync(table, partitionKey, token), async () =>
        {
            cache.Dispose();
            await DropTableAsync(table);
            if (settings.BlobOffload)
                await Blobs.Value.GetBlobContainerClient(container).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
        });
    }

    public async Task<ConformancePolymorphicStore<TBase>> CreatePolymorphicStoreAsync<TBase>(CancellationToken ct = default)
        where TBase : class
    {
        await RequireAzuriteAsync();
        var table = NewTableName();
        var store = new PolymorphicTableStorage<TBase>(Tables.Value, table, NullLogger<PolymorphicTableStorage<TBase>>.Instance);
        return new ConformancePolymorphicStore<TBase>(store, () => new ValueTask(DropTableAsync(table)));
    }

    private static async Task DropTableAsync(string table)
    {
        try
        {
            await Tables.Value.DeleteTableAsync(table, CancellationToken.None);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // The scenario never wrote: the table was never created.
        }
    }

    private static async Task<IReadOnlyList<ConformanceRawRow>> RawRowsAsync(string table, string partitionKey, CancellationToken ct)
    {
        var rows = new List<ConformanceRawRow>();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partitionKey}");
        try
        {
            await foreach (var row in Tables.Value.GetTableClient(table).QueryAsync<TableEntity>(filter, cancellationToken: ct))
                rows.Add(new ConformanceRawRow(row.PartitionKey, row.RowKey, Cells(row)));
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // The table was never created: nothing is stored.
        }
        return rows;
    }

    /// <summary>The columns of a stored row, without the keys, the timestamp and the ETag.</summary>
    internal static Dictionary<string, object> Cells(TableEntity row) =>
        row.Where(cell => cell.Key is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag") && cell.Value is not null)
            .ToDictionary(cell => cell.Key, cell => cell.Value, StringComparer.Ordinal);
}
