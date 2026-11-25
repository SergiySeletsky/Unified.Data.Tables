#pragma warning disable UDT0001 // the experimental raw-table API is what the reference implements
#pragma warning disable CA1001 // a test double that lives for one scenario: its semaphore is never contended across scopes

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Azure.Data.Tables;
using Unified.Data.Tables.Conformance;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Tests.ConformanceKit;

/// <summary>
/// A small reference implementation of every 0.10.0 capability, over the current
/// <see cref="InMemoryStorage{T}"/>: conditional delete, atomic batch, storage info, RowKey range bounds,
/// the raw table and the table store. No provider implements them yet (the Azure, in-memory and SQLite
/// packages of 0.10.0 do), so this is what proves the capability tier of the kit can be passed, and that
/// each of its scenarios fails for the mistake it is about (see <c>ReferenceConformanceTests</c>).
/// </summary>
/// <remarks>
/// Written for clarity, not for use: one lock around every capability call instead of the row-level
/// atomicity a provider has, and the whole partition read for a range.
/// </remarks>
internal sealed class ReferenceStorage<T> : DelegatingStorage<T>, IConditionalDelete<T>, IAtomicBatch<T>, IStorageInfo
    where T : class, IEntity, new()
{
    private readonly InMemoryStorage<T> memory;
    private readonly IdNormalization idNormalization;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ReferenceRawTable<T> rawTable;
    private readonly ReferenceTableStore tableStore = new();

    public ReferenceStorage(InMemoryStorage<T> memory, IdNormalization idNormalization)
        : base(memory)
    {
        this.memory = memory;
        this.idNormalization = idNormalization;
        rawTable = new ReferenceRawTable<T>(typeof(T).Name, () => memory);
    }

    /// <summary>What a test switches off to see the kit catch it.</summary>
    public ReferenceFlaws Flaws { get; init; }

    private string Normalize(string id) => idNormalization == IdNormalization.Normalized ? EntityId.Normalize(id) : id;

    public override bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
    {
        capability = rawTable as TCap ?? tableStore as TCap;
        return capability is not null || base.TryGetCapability(out capability);
    }

    // ── IStorageInfo ──────────────────────────────────────────────────────

    public StorageInfo GetInfo() => new()
    {
        ProviderName = "Reference",
        TableName = typeof(T).Name,
        Durability = Durability.Volatile,
        MaxBatchRows = BatchPlanner.DefaultMaxCount,
        MaxBatchBytes = BatchPlanner.DefaultMaxBytes,
        MaxCellBytes = 64 * 1024,
        MaxRowBytes = 1024 * 1024,
        ReadCacheMayServeStaleRows = false,
        SinglePartitionTransactions = true,
    };

    // ── IConditionalDelete<T> ─────────────────────────────────────────────

    public async Task<DeleteStatus> DeleteIfExistsAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await gate.WaitAsync(ct);
        try
        {
            if (!await memory.ExistsAsync(id, ct))
                return DeleteStatus.NotFound;
            await memory.DeleteAsync(id, ct);
            return DeleteStatus.Deleted;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DeleteStatus> DeleteIfMatchAsync(string id, string etag, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(etag) || etag == "*")
            throw new ArgumentException("A conditional delete needs the row's ETag.", nameof(etag));
        ct.ThrowIfCancellationRequested();
        await gate.WaitAsync(ct);
        try
        {
            var keys = EntityId.Split(Normalize(id));
            if (memory.PhysicalVersion(keys.PartitionKey, keys.RowKey) is not { } version || !await memory.ExistsAsync(id, ct))
                return DeleteStatus.NotFound;
            if (version.ETag != etag && !Flaws.HasFlag(ReferenceFlaws.DeleteIfMatchIgnoresTheETag))
                throw new ConcurrencyConflictException(typeof(T).Name, Normalize(id), null);
            await memory.DeleteAsync(id, ct);
            return DeleteStatus.Deleted;
        }
        finally
        {
            gate.Release();
        }
    }

    // ── IAtomicBatch<T> ───────────────────────────────────────────────────

    public Task CreateAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => WriteAtomicAsync(entities, create: true, ct);

    public Task UpsertAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => WriteAtomicAsync(entities, create: false, ct);

    private async Task WriteAtomicAsync(IReadOnlyCollection<T> entities, bool create, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return;
        ct.ThrowIfCancellationRequested();

        // One transaction or none: sized and checked before anything is written.
        var keys = entities.Select(e => EntityId.Split(Normalize(e.Id))).ToList();
        if (keys.Count > BatchPlanner.DefaultMaxCount)
            throw new BatchNotAtomicException($"{keys.Count} rows do not fit one transaction of {BatchPlanner.DefaultMaxCount}.");
        if (keys.Select(k => k.PartitionKey).Distinct(StringComparer.Ordinal).Count() > 1 && !Flaws.HasFlag(ReferenceFlaws.AtomicBatchSplitsPartitions))
            throw new BatchNotAtomicException("The batch spans more than one partition.");
        if (keys.Distinct().Count() != keys.Count)
            throw new BatchNotAtomicException("The batch names a row twice.");
        var bytes = entities.Zip(keys, (entity, key) =>
            TableRequestSize.EstimateEntityBytes(LargeValueRows.ToPlainTableEntity(entity, key.PartitionKey, key.RowKey, typeof(T)))).Sum();
        if (bytes > BatchPlanner.DefaultMaxBytes)
            throw new BatchNotAtomicException($"{bytes:N0} bytes do not fit one transaction of {BatchPlanner.DefaultMaxBytes:N0}.");

        await gate.WaitAsync(ct);
        try
        {
            if (create)
            {
                foreach (var entity in entities)
                {
                    if (await memory.ExistsAsync(entity.Id, ct))
                        throw new DuplicateKeyException(typeof(T).Name, Normalize(entity.Id), null);
                }
                await memory.CreateBatchAsync(entities, ct);
            }
            else
            {
                await memory.UpsertBatchAsync(entities, ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // ── RowKey range bounds ───────────────────────────────────────────────

    public override async Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.HasRowKeyRange)
            return await base.QueryAsync(options, ct);
        if (options.ContinuationToken is not null)
            throw new ArgumentException("ContinuationToken is only honored by QueryPageAsync.", nameof(options));
        return await RangeAsync(options, after: null, options.Take, ct);
    }

    public override IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null, CancellationToken ct = default)
    {
        if (options is null || !options.HasRowKeyRange)
            return base.QueryStreamAsync(options, ct);
        options.ValidateRowKeyRange();
        return StreamAsync(options, ct);
    }

    private async IAsyncEnumerable<T> StreamAsync(QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var entity in await RangeAsync(options, after: null, options.Take, ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return entity;
        }
    }

    public override async Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.HasRowKeyRange)
            return await base.QueryPageAsync(options, ct);
        options.ValidateRowKeyRange();
        if (options.Take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.Take, "Take (page size) must be positive.");

        var pageSize = options.Take is int take ? Math.Min(take, 1000) : 100;
        var fingerprint = PageCursor.Fingerprint(
            Normalize(options.Partition!), null, pageSize,
            Bound(options.FromRowKey), options.FromInclusive, Bound(options.ToRowKey), options.ToInclusive);
        var after = options.ContinuationToken is null ? null : PageCursor.Decode(options.ContinuationToken, fingerprint);

        var rows = await RangeAsync(options, after, pageSize + 1, ct);
        if (rows.Count <= pageSize)
            return new EntityPage<T>(rows, null);
        var page = rows.Take(pageSize).ToList();
        return new EntityPage<T>(page, PageCursor.Encode(fingerprint, EntityId.Split(page[^1].Id).RowKey));
    }

    private string? Bound(string? bound) => bound is null ? null : Normalize(bound);

    private async Task<IReadOnlyList<T>> RangeAsync(QueryOptions options, string? after, int? take, CancellationToken ct)
    {
        options.ValidateRowKeyRange();
        if (take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), take, "Take must be positive.");
        var from = Bound(options.FromRowKey);
        var to = Bound(options.ToRowKey);

        // The partition in key order (the store's own, already normalized, scan), cut to the range.
        var rows = (await base.QueryAsync(new QueryOptions { Partition = options.Partition, Select = options.Select }, ct))
            .Where(entity =>
            {
                var rowKey = EntityId.Split(entity.Id).RowKey;
                if (after is not null && string.CompareOrdinal(rowKey, after) <= 0)
                    return false;
                if (Flaws.HasFlag(ReferenceFlaws.RangeIgnoresTheBounds))
                    return true;
                if (from is not null && (options.FromInclusive ? string.CompareOrdinal(rowKey, from) < 0 : string.CompareOrdinal(rowKey, from) <= 0))
                    return false;
                return to is null || (options.ToInclusive ? string.CompareOrdinal(rowKey, to) <= 0 : string.CompareOrdinal(rowKey, to) < 0);
            });
        return (take is int limit ? rows.Take(limit) : rows).ToList();
    }
}

/// <summary>Mistakes the reference can be told to make, one per negative test.</summary>
[Flags]
public enum ReferenceFlaws
{
    None = 0,

    /// <summary>What an implementer written against 0.9.2 does: the bounds are not looked at.</summary>
    RangeIgnoresTheBounds = 1,

    /// <summary>A conditional delete that deletes whatever the version.</summary>
    DeleteIfMatchIgnoresTheETag = 2,

    /// <summary>An "atomic" batch that quietly spans partitions (two transactions).</summary>
    AtomicBatchSplitsPartitions = 4,
}

/// <summary>
/// The raw view of one in-memory table, through the store's physical-row test hooks: rows as stored, typed
/// cells, and a single-partition all-or-nothing transaction with the limits of an Azure Tables one.
/// </summary>
internal sealed class ReferenceRawTable<TRow>(string tableName, Func<InMemoryStorage<TRow>?> table) : IRawTable
    where TRow : class, IEntity, new()
{
    private readonly object gate = new();

    public string TableName { get; } = tableName;

    private InMemoryStorage<TRow> Table() =>
        table() ?? throw new TableNotFoundException($"The table '{TableName}' does not exist.");

    public Task<RawRow?> GetAsync(string partitionKey, string rowKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Read(Table(), partitionKey, rowKey));
    }

    public IAsyncEnumerable<RawRow> QueryAsync(RawQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), query.Take, "Take must be positive.");
        return Rows(query, ct);
    }

    private async IAsyncEnumerable<RawRow> Rows(RawQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var rows = Table();
        var yielded = 0;
        foreach (var rowKey in rows.PhysicalRowKeys(query.PartitionKey))
        {
            ct.ThrowIfCancellationRequested();
            if (query.FromRowKey is { } from && (query.FromInclusive ? string.CompareOrdinal(rowKey, from) < 0 : string.CompareOrdinal(rowKey, from) <= 0))
                continue;
            if (query.ToRowKey is { } to && (query.ToInclusive ? string.CompareOrdinal(rowKey, to) > 0 : string.CompareOrdinal(rowKey, to) >= 0))
                break;
            if (Read(rows, query.PartitionKey, rowKey) is { } row)
            {
                yield return row;
                if (query.Take is int take && ++yielded >= take)
                    yield break;
            }
        }
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<string?>> SubmitAsync(IReadOnlyList<RawOperation> operations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ct.ThrowIfCancellationRequested();
        if (operations.Count == 0)
            return Task.FromResult<IReadOnlyList<string?>>([]);

        // One transaction or none: checked before the table is looked at.
        if (operations.Count > BatchPlanner.DefaultMaxCount)
            throw new BatchNotAtomicException($"{operations.Count} operations do not fit one transaction of {BatchPlanner.DefaultMaxCount}.");
        if (operations.Select(o => o.Row.PartitionKey).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new BatchNotAtomicException("The transaction spans more than one partition.");
        if (operations.Select(o => o.Row.RowKey).Distinct(StringComparer.Ordinal).Count() != operations.Count)
            throw new BatchNotAtomicException("The transaction names a row twice.");

        var rows = Table();
        lock (gate)
        {
            // Every operation is checked against the table as it is (each names another row), then all are applied.
            foreach (var operation in operations)
            {
                var id = operation.Row.PartitionKey + "|" + operation.Row.RowKey;
                var stored = rows.PhysicalVersion(operation.Row.PartitionKey, operation.Row.RowKey);
                if (operation.Kind == RawOperationKind.Insert)
                {
                    if (stored is not null)
                        throw new DuplicateKeyException(TableName, id, null);
                    continue;
                }
                if (stored is null)
                    throw new EntityNotFoundException(TableName, id, null);
                if (operation.IfMatch is not null && operation.IfMatch != stored.Value.ETag)
                    throw new ConcurrencyConflictException(TableName, id, null);
            }

            var etags = new List<string?>(operations.Count);
            foreach (var operation in operations)
            {
                var (partitionKey, rowKey) = (operation.Row.PartitionKey, operation.Row.RowKey);
                if (operation.Kind == RawOperationKind.Delete)
                {
                    rows.RemovePhysicalRow(partitionKey, rowKey);
                    etags.Add(null);
                    continue;
                }
                // A merge keeps the cells it does not name; an insert and a replace start from none.
                var row = operation.Kind == RawOperationKind.Merge ? rows.PhysicalRow(partitionKey, rowKey)! : new TableEntity(partitionKey, rowKey);
                foreach (var cell in operation.Row.Cells)
                    row[cell.Key] = cell.Value.Value;
                rows.SeedPhysicalRow(row);
                etags.Add(rows.PhysicalVersion(partitionKey, rowKey)!.Value.ETag);
            }
            return Task.FromResult<IReadOnlyList<string?>>(etags);
        }
    }

    private static RawRow? Read(InMemoryStorage<TRow> rows, string partitionKey, string rowKey)
    {
        if (rows.PhysicalRow(partitionKey, rowKey) is not { } row || rows.PhysicalVersion(partitionKey, rowKey) is not { } version)
            return null;
        var cells = new Dictionary<string, RawCell>(StringComparer.Ordinal);
        foreach (var cell in AzuriteConformanceFactory.Cells(row))
        {
            cells[cell.Key] = cell.Value switch
            {
                string value => RawCell.FromString(value),
                byte[] value => RawCell.FromBinary(value),
                bool value => RawCell.FromBoolean(value),
                DateTimeOffset value => RawCell.FromDateTime(value),
                DateTime value => RawCell.FromDateTime(value),
                double value => RawCell.FromDouble(value),
                Guid value => RawCell.FromGuid(value),
                int value => RawCell.FromInt32(value),
                long value => RawCell.FromInt64(value),
                var other => throw new NotSupportedException($"A cell of type {other.GetType().Name} is not a table cell."),
            };
        }
        return new RawRow(partitionKey, rowKey, cells) { ETag = version.ETag, Timestamp = version.Timestamp };
    }
}

/// <summary>An in-memory account of raw tables: created, found, listed and deleted by name.</summary>
internal sealed class ReferenceTableStore : ITableStore
{
    private readonly Dictionary<string, InMemoryStorage<ConformanceItem>> tables = new(StringComparer.Ordinal);

    public async IAsyncEnumerable<string> ListTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        List<string> names;
        lock (tables)
            names = [.. tables.Keys.Order(StringComparer.Ordinal)];
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            yield return name;
        }
        await Task.CompletedTask;
    }

    public Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
    {
        lock (tables)
            return Task.FromResult(tables.ContainsKey(tableName));
    }

    public Task<bool> CreateTableAsync(string tableName, CancellationToken ct = default)
    {
        lock (tables)
            return Task.FromResult(tables.TryAdd(tableName, new InMemoryStorage<ConformanceItem>()));
    }

    public Task<bool> DeleteTableAsync(string tableName, CancellationToken ct = default)
    {
        lock (tables)
            return Task.FromResult(tables.Remove(tableName));
    }

    public IRawTable GetTable(string tableName) =>
        new ReferenceRawTable<ConformanceItem>(tableName, () =>
        {
            lock (tables)
                return tables.GetValueOrDefault(tableName);
        });
}

/// <summary>
/// The reference atomic insert, over the current <see cref="InMemoryPolymorphicStorage{TBase}"/>: every
/// member forwards, and <see cref="InsertAtomicAsync"/> is one transaction or none.
/// </summary>
internal sealed class ReferencePolymorphicStorage<TBase>(InMemoryPolymorphicStorage<TBase> inner, string tableName)
    : IPolymorphicStorage<TBase>, IAtomicInsert<TBase>, IStorageInfo
    where TBase : class
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public StorageInfo GetInfo() => new() { ProviderName = "Reference", TableName = tableName, Durability = Durability.Volatile };

    public async Task InsertAtomicAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return;
        ct.ThrowIfCancellationRequested();
        if (writes.Count > BatchPlanner.DefaultMaxCount)
            throw new BatchNotAtomicException($"{writes.Count} rows do not fit one transaction of {BatchPlanner.DefaultMaxCount}.");
        if (writes.Select(w => w.Key.PartitionKey).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new BatchNotAtomicException("The batch spans more than one partition.");
        if (writes.Select(w => w.Key).Distinct().Count() != writes.Count)
            throw new BatchNotAtomicException("The batch names a key twice.");

        await gate.WaitAsync(ct);
        try
        {
            foreach (var write in writes)
            {
                if (await inner.GetAsync(write.Key, ct) is not null)
                    throw new DuplicateKeyException(tableName, write.Key.ToId(), null);
            }
            await inner.InsertBatchAsync(writes, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task EnsureCreatedAsync(CancellationToken ct = default) => inner.EnsureCreatedAsync(ct);

    public Task<PolymorphicEntry<TBase>> InsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default) => inner.InsertAsync(write, ct);

    public Task<PolymorphicEntry<TBase>> UpsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default) => inner.UpsertAsync(write, ct);

    public Task<int> InsertBatchAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default) => inner.InsertBatchAsync(writes, ct);

    public Task MergeColumnsAsync(TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct = default) =>
        inner.MergeColumnsAsync(key, columns, ct);

    public Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default) => inner.GetAsync(key, ct);

    public Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(string? partition = null, int? take = null, CancellationToken ct = default) =>
        inner.QueryAsync(partition, take, ct);

    public IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(string? partition = null, int? take = null, CancellationToken ct = default) =>
        inner.QueryStreamAsync(partition, take, ct);

    public Task DeleteAsync(TableKey key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);

    public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default) => inner.DeletePartitionAsync(partition, ct);

    public Task<int> CountAsync(string? partition = null, CancellationToken ct = default) => inner.CountAsync(partition, ct);
}

/// <summary>The reference implementation as the conformance kit sees it.</summary>
internal sealed class ReferenceConformanceFactory(ReferenceFlaws flaws = ReferenceFlaws.None, params string[] leniencies) : IConformanceStoreFactory
{
    public string Name => flaws == ReferenceFlaws.None ? "Reference" : $"Reference ({flaws})";

    public IReadOnlyCollection<string> Leniencies { get; } = [.. leniencies];

    public Task<ConformanceStore<T>> CreateStoreAsync<T>(ConformanceStoreSettings settings, CancellationToken ct = default)
        where T : class, IEntity, new()
    {
        var memory = InMemoryConformanceFactory.Create<T>(settings);
        return Task.FromResult(new ConformanceStore<T>(
            new ReferenceStorage<T>(memory, settings.IdNormalization) { Flaws = flaws }, InMemoryConformanceFactory.RawRows(memory)));
    }

    public Task<ConformancePolymorphicStore<TBase>> CreatePolymorphicStoreAsync<TBase>(CancellationToken ct = default) where TBase : class =>
        Task.FromResult(new ConformancePolymorphicStore<TBase>(
            new ReferencePolymorphicStorage<TBase>(new InMemoryPolymorphicStorage<TBase>("ConformanceEvents"), "ConformanceEvents")));
}
