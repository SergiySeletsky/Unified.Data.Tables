using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Providers;

/// <summary>
/// <see cref="IPolymorphicStorage{TBase}"/> over a row store (<see cref="IRowStore"/>): the one engine
/// every local provider runs. Rows round-trip through the real <see cref="TableEntitySerializer"/>.
/// </summary>
/// <remarks>
/// Semantics mirror <c>PolymorphicTableStorage&lt;TBase&gt;</c>: keys used verbatim and
/// case-sensitively, 409 on duplicate insert AND 400 <c>InvalidDuplicateRow</c> on a key repeated
/// within one batch both surfaced as <see cref="DuplicateKeyException"/>,
/// <see cref="EntityNotFoundException"/> on merging a missing row, idempotent delete, lexical
/// (PartitionKey, RowKey) ordering, a null <see cref="PolymorphicEntry{TBase}.Timestamp"/> on the write
/// paths, cancellation observed on every operation, and byte-identical validation and duplicate-key
/// messages (which name the table).
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
internal sealed class RowStorePolymorphicStorage<TBase> : IPolymorphicStorage<TBase>, IAtomicInsert<TBase>, IStorageInfo, IStorageCapabilities
    where TBase : class
{
    private static readonly string[] ReservedCells = ["PartitionKey", "RowKey", "Timestamp", "odata.etag"];

    private readonly IRowStoreAccount account;
    private readonly IRowStore rows;
    private readonly ITypeDiscriminator discriminator;
    private readonly string tableName;
    private readonly StorageInfo info;
    private volatile bool tableEnsured;

    /// <summary>Creates the store over one named table of <paramref name="account"/>. Touches nothing: the table is created on first use.</summary>
    /// <param name="account">The account that holds the table.</param>
    /// <param name="tableName">The table; also what the store's errors name.</param>
    /// <param name="options">Options; null selects the defaults.</param>
    public RowStorePolymorphicStorage(IRowStoreAccount account, string tableName, StorageOptions? options)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        this.account = account;
        this.tableName = tableName;
        rows = account.GetTable(tableName);
        discriminator = (options ?? new StorageOptions()).ResolveTypeDiscriminator();
        // A polymorphic store has no read cache.
        info = RowStoreInfo.Describe(account, tableName, readCacheEnabled: false);
    }

    private ValueTask EnsureTableAsync(CancellationToken ct) => tableEnsured ? default : CreateTableOnceAsync(ct);

    private async ValueTask CreateTableOnceAsync(CancellationToken ct)
    {
        await account.CreateTableAsync(tableName, ct);
        tableEnsured = true;
    }

    private async Task Run(Task work)
    {
        try
        {
            await work;
        }
        catch (RowStoreTableMissingException ex)
        {
            throw ServiceErrors.TableMissing(tableName, ex);
        }
    }

    private async Task<TResult> Run<TResult>(Task<TResult> work)
    {
        try
        {
            return await work;
        }
        catch (RowStoreTableMissingException ex)
        {
            throw ServiceErrors.TableMissing(tableName, ex);
        }
    }

#pragma warning disable UDT0001 // the experimental raw-table API is handed out here, not used
    private RowStoreRawTable? rawTable;
    private RowStoreTableStore? tableStore;

    /// <summary>
    /// The store's implementation of the side interface <typeparamref name="TCap"/>: the store itself for
    /// <see cref="IAtomicInsert{TBase}"/> and <see cref="IStorageInfo"/>; the untyped view of its table
    /// for the experimental <see cref="IRawTable"/>; the tables of its account for the experimental
    /// <see cref="ITableStore"/>.
    /// </summary>
    public bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
    {
        capability = this as TCap;
        if (capability is null && typeof(TCap) == typeof(IRawTable))
            capability = (rawTable ??= new RowStoreRawTable(rows, tableName, EnsureTableAsync)) as TCap;
        if (capability is null && typeof(TCap) == typeof(ITableStore))
            capability = (tableStore ??= new RowStoreTableStore(account)) as TCap;
        return capability is not null;
    }
#pragma warning restore UDT0001

    /// <summary>The store's info; the same value on every call. Calls nothing and never throws.</summary>
    public StorageInfo GetInfo() => info;

    /// <inheritdoc />
    public Task EnsureCreatedAsync(CancellationToken ct = default) => Run(EnsureTableAsync(ct).AsTask());

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>> InsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default) =>
        Run(WriteAsync(write, insert: true, ct));

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>> UpsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default) =>
        Run(WriteAsync(write, insert: false, ct));

    private async Task<PolymorphicEntry<TBase>> WriteAsync(PolymorphicWrite<TBase> write, bool insert, CancellationToken ct)
    {
        Guard.NotNull(write, nameof(write));
        var row = ToRow(write);

        // The Azure store observes the token on its lazy table-create, AFTER argument validation and
        // row building, so a cancelled operation fails here as it fails there.
        ct.ThrowIfCancellationRequested();
        ServiceErrors.ThrowIfRefusedKey((write.Key.PartitionKey, write.Key.RowKey), tableName);
        ServiceErrors.ThrowIfRefusedRow(row, tableName);

        await EnsureTableAsync(ct);
        var result = await rows.CommitAsync(write.Key.PartitionKey, [insert ? RowWrite.Insert(row) : RowWrite.Upsert(row)], ct);
        if (!result.Committed)
            throw Duplicate(write.Key, ServiceErrors.AlreadyExists(write.Key.ToId()));
        // A write response carries no service timestamp, so the entry's is null, as on Azure.
        return ToEntry(new StoredRow(row, result.ETags[0]!, result.Timestamp), timestampKnown: false);
    }

    /// <inheritdoc />
    public Task<int> InsertBatchAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default) =>
        Run(InsertBatchCoreAsync(writes, ct));

    private async Task<int> InsertBatchCoreAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct)
    {
        Guard.NotNull(writes, nameof(writes));
        if (writes.Count == 0)
            return 0;

        // Build every row before the first transaction, so a validation failure costs nothing.
        var built = new List<(TableKey Key, TableEntity Row)>(writes.Count);
        foreach (var write in writes)
            built.Add((write.Key, ToRow(write)));

        ct.ThrowIfCancellationRequested();
        await EnsureTableAsync(ct);

        // As PolymorphicTableStorage sends it: per partition (in the order the partitions first appear),
        // the rows packed into the same entity-group transactions (BatchPlanner), each one commit of the
        // row store; a transaction that fails applies nothing, and the ones before it stay committed.
        // The same key twice in one transaction is the service's 400 InvalidDuplicateRow, a stored key its
        // 409 EntityAlreadyExists, both surfaced as DuplicateKeyException over the transaction failure; a
        // key the service refuses is its 400 InvalidInput.
        foreach (var group in built.GroupBy(b => b.Key.PartitionKey, StringComparer.Ordinal))
        {
            var groupRows = group.ToList();
            var plan = BatchPlanner.Plan([.. groupRows.Select(b => TableRequestSize.EstimateEntityBytes(b.Row))]);
            foreach (var range in plan)
            {
                ct.ThrowIfCancellationRequested();
                var commit = new List<RowWrite>(range.Count);
                var inTransaction = new HashSet<(string, string)>();
                StorageException? refusal = null;
                for (var i = range.Start; i < range.Start + range.Count && refusal is null; i++)
                {
                    var key = groupRows[i].Key;
                    if ((ServiceErrors.RefusedKey(key.PartitionKey, key.RowKey) ?? ServiceErrors.RefusedRow(groupRows[i].Row)) is { } refused)
                        refusal = ServiceErrors.Neutral(ServiceErrors.InTransaction(refused), tableName, key.ToId());
                    else if (!inTransaction.Add((key.PartitionKey, key.RowKey)))
                        refusal = Duplicate(key, ServiceErrors.InTransaction(ServiceErrors.DuplicateRow(key.ToId())));
                    else
                        commit.Add(RowWrite.Insert(groupRows[i].Row));
                }
                if (refusal is not null)
                {
                    // The rows before the refused one are looked at first: a stored key among them is the
                    // failure of the transaction.
                    for (var k = 0; k < commit.Count; k++)
                    {
                        if (await rows.GetAsync(group.Key, commit[k].RowKey, ct) is not null)
                            throw StoredKey(groupRows[range.Start + k].Key);
                    }
                    throw refusal;
                }

                var result = await rows.CommitAsync(group.Key, commit, ct);
                if (!result.Committed)
                    throw StoredKey(groupRows[range.Start + result.FailedIndex].Key);
            }
        }

        return built.Count;
    }

    private DuplicateKeyException StoredKey(TableKey key) =>
        Duplicate(key, ServiceErrors.InTransaction(ServiceErrors.AlreadyExists(key.ToId())));

    /// <inheritdoc />
    public Task InsertAtomicAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default) =>
        Run(InsertAtomicCoreAsync(writes, ct));

    // One commit of the row store, planned as PolymorphicTableStorage plans its one entity-group
    // transaction: one partition, at most 100 rows, each key once, the payload budget of BatchPlanner.
    private async Task InsertAtomicCoreAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return;
        ct.ThrowIfCancellationRequested();

        // Every row is built (and validated) first, then the batch is checked as one transaction: both
        // before the table is touched.
        var built = new List<TableEntity>(writes.Count);
        foreach (var write in writes)
            built.Add(ToRow(write ?? throw new ArgumentException("A batch cannot hold a null write.", nameof(writes))));
        AtomicBatches.RequireOnePartitionEachRowOnce([.. built.Select(r => (r.PartitionKey, r.RowKey))], "batch", "key");
        AtomicBatches.RequireOneTransaction([.. built.Select(TableRowSize.Estimate)], "batch");
        foreach (var row in built)
        {
            if ((ServiceErrors.RefusedKey(row.PartitionKey, row.RowKey) ?? ServiceErrors.RefusedRow(row)) is { } refused)
                throw ServiceErrors.Neutral(ServiceErrors.InTransaction(refused), tableName, new TableKey(row.PartitionKey, row.RowKey).ToId());
        }

        await EnsureTableAsync(ct);
        var result = await rows.CommitAsync(built[0].PartitionKey, [.. built.Select(RowWrite.Insert)], ct);
        // A row already exists at one of the keys: nothing of the transaction was applied.
        if (!result.Committed)
            throw StoredKey(new TableKey(built[result.FailedIndex].PartitionKey, built[result.FailedIndex].RowKey));
    }

    /// <inheritdoc />
    public Task MergeColumnsAsync(TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct = default) =>
        Run(MergeCoreAsync(key, columns, ct));

    private async Task MergeCoreAsync(TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct)
    {
        ValidateKey(key);
        Guard.NotNull(columns, nameof(columns));
        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "A merge with no columns would be a network round trip that changes nothing.",
                nameof(columns));
        }

        foreach (var column in columns)
            ValidateSystemColumn(column.Key);

        ct.ThrowIfCancellationRequested();
        ServiceErrors.ThrowIfRefusedKey((key.PartitionKey, key.RowKey), tableName);

        await EnsureTableAsync(ct);
        while (true)
        {
            var existing = await rows.GetAsync(key.PartitionKey, key.RowKey, ct);
            if (existing is null)
                throw ServiceErrors.Neutral(
                    new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null), tableName, key.ToId());

            // Server-side Merge overlays the supplied cells onto a copy and leaves the rest alone. It is
            // written on condition of the version it was computed from; a row another writer changed
            // meanwhile is merged again.
            var merged = CopyOf(existing.Data);
            foreach (var column in columns)
                merged[column.Key] = column.Value is byte[] bytes ? bytes.Clone() : column.Value;
            ServiceErrors.ThrowIfRefusedRow(merged, tableName);

            if ((await rows.CommitAsync(key.PartitionKey, [RowWrite.Replace(merged, existing.ETag)], ct)).Committed)
                return;
        }
    }

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default) => Run(GetCoreAsync(key, ct));

    private async Task<PolymorphicEntry<TBase>?> GetCoreAsync(TableKey key, CancellationToken ct)
    {
        ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        await EnsureTableAsync(ct);
        return await rows.GetAsync(key.PartitionKey, key.RowKey, ct) is { } stored ? ToEntry(stored) : null;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(string? partition = null, int? take = null, CancellationToken ct = default)
    {
        // take <= 0 short-circuits BEFORE the token is looked at, because PolymorphicTableStorage's
        // QueryStreamAsync yields break before its first (cancellable) service call on that path.
        if (take is <= 0)
            return Task.FromResult<IReadOnlyList<PolymorphicEntry<TBase>>>([]);
        return Run(ListAsync(partition, take, ct));
    }

    private async Task<IReadOnlyList<PolymorphicEntry<TBase>>> ListAsync(string? partition, int? take, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entries = new List<PolymorphicEntry<TBase>>();
        await foreach (var entry in EntriesAsync(partition, take, ct))
            entries.Add(entry);
        return entries;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(
        string? partition = null,
        int? take = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (take is <= 0)
            yield break;

        // Azure observes the token on its lazy table-create, before the first row. The per-item check
        // below never runs for an empty result, so without this an empty read ignores cancellation.
        ct.ThrowIfCancellationRequested();

        await using var entries = EntriesAsync(partition, take, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            PolymorphicEntry<TBase> entry;
            try
            {
                if (!await entries.MoveNextAsync())
                    yield break;
                entry = entries.Current;
            }
            catch (RowStoreTableMissingException ex)
            {
                throw ServiceErrors.TableMissing(tableName, ex);
            }
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    // The rows of the partition (null: of the table) in key order, pulled a page at a time.
    private async IAsyncEnumerable<PolymorphicEntry<TBase>> EntriesAsync(string? partition, int? take, [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        var range = new RowRange(partition);
        var pageSize = take is int limit ? Math.Min(limit, 1000) : 1000;
        var yielded = 0;
        while (true)
        {
            var page = await rows.ReadAsync(range, pageSize, ct);
            foreach (var row in page)
            {
                yield return ToEntry(row);
                if (take is int max && ++yielded >= max)
                    yield break;
            }
            if (page.Count < pageSize)
                yield break;
            range = range with { After = (page[page.Count - 1].PartitionKey, page[page.Count - 1].RowKey) };
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(TableKey key, CancellationToken ct = default) => Run(DeleteCoreAsync(key, ct));

    private async Task DeleteCoreAsync(TableKey key, CancellationToken ct)
    {
        ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        await EnsureTableAsync(ct);
        // Deleting a missing row is a no-op.
        await rows.CommitAsync(key.PartitionKey, [RowWrite.DeleteIfExists(key.RowKey)], ct);
    }

    /// <inheritdoc />
    public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default) => Run(DeletePartitionCoreAsync(partition, ct));

    private async Task<int> DeletePartitionCoreAsync(string partition, CancellationToken ct)
    {
        // Not Guard.NotNull: PolymorphicTableStorage uses ArgumentException.ThrowIfNullOrEmpty, so an
        // EMPTY partition throws here as it throws there. Same API, same message.
        ArgumentException.ThrowIfNullOrEmpty(partition);
        ct.ThrowIfCancellationRequested();
        await EnsureTableAsync(ct);

        // Listed, then deleted a transaction (at most 100 rows) at a time; a row another writer removed
        // since the listing makes its transaction apply nothing, and its rows are then deleted one by
        // one, counting the ones this call removed.
        var listed = new List<string>();
        var range = new RowRange(partition);
        while (true)
        {
            var page = await rows.ReadAsync(range, 1000, ct);
            listed.AddRange(page.Select(r => r.RowKey));
            if (page.Count < 1000)
                break;
            range = range with { After = (partition, page[page.Count - 1].RowKey) };
        }
        var deleted = 0;
        foreach (var chunk in listed.Chunk(BatchPlanner.DefaultMaxCount))
        {
            if ((await rows.CommitAsync(partition, [.. chunk.Select(rowKey => RowWrite.Delete(rowKey))], ct)).Committed)
            {
                deleted += chunk.Length;
                continue;
            }
            foreach (var rowKey in chunk)
            {
                if ((await rows.CommitAsync(partition, [RowWrite.Delete(rowKey)], ct)).Committed)
                    deleted++;
            }
        }
        return deleted;
    }

    /// <inheritdoc />
    public Task<int> CountAsync(string? partition = null, CancellationToken ct = default) => Run(CountCoreAsync(partition, ct));

    private async Task<int> CountCoreAsync(string? partition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureTableAsync(ct);
        var count = 0;
        var range = new RowRange(partition);
        while (true)
        {
            var page = await rows.ReadAsync(range, 1000, ct);
            count += page.Count;
            if (page.Count < 1000)
                return count;
            range = range with { After = (page[page.Count - 1].PartitionKey, page[page.Count - 1].RowKey) };
        }
    }

    // The row of a write as the store keeps it: nothing of it is shared with the caller's item.
    private TableEntity ToRow(PolymorphicWrite<TBase> write)
    {
        ValidateKey(write.Key);

        // Parity with PolymorphicTableStorage: a TableBlobContent is refused before serializing.
        if (write.Item is not null && TableEntitySerializer.CarriesBlobContent(write.Item))
            throw new NotSupportedException(TableEntitySerializer.PolymorphicWriteRefusal(write.Item.GetType()));

        var row = write.Item is null
            ? new TableEntity(write.Key.PartitionKey, write.Key.RowKey)
            : write.Item.ToTableEntity(write.Key.PartitionKey, write.Key.RowKey);

        // Parity with PolymorphicTableStorage: split values are refused, not stored half.
        if (write.Item is not null && TableEntitySerializer.HasLargeValueCells(row))
            throw new NotSupportedException(TableEntitySerializer.PolymorphicWriteRefusal(write.Item.GetType()));

        if (write.Item is not null)
            row[SystemColumnNames.TypeName] = discriminator.ToDiscriminator(write.Item.GetType());

        if (write.SystemColumns is not null)
        {
            foreach (var column in write.SystemColumns)
            {
                ValidateSystemColumn(column.Key);
                row[column.Key] = column.Value;
            }
        }

        return CopyOf(row);
    }

    // Duplicate-key errors name the TABLE, exactly as PolymorphicTableStorage does. The inner failure is
    // the service's, with its status and error code (see ServiceErrors).
    private DuplicateKeyException Duplicate(TableKey key, RequestFailedException failure) =>
        ServiceErrors.Coded(new DuplicateKeyException(tableName, key.ToId(), failure));

    // timestampKnown: false on the WRITE paths. A write response carries no service timestamp, so
    // PolymorphicTableStorage returns a null one.
    private PolymorphicEntry<TBase> ToEntry(StoredRow stored, bool timestampKnown = true)
    {
        var row = CopyOf(stored.Data);
        row.Timestamp = stored.Timestamp;

        string? storedDiscriminator = null;
        if (row.TryGetValue(SystemColumnNames.TypeName, out var raw)
            && raw is string token
            && token.Length > 0)
        {
            storedDiscriminator = token;
        }

        var item = row.TryFromTableEntity<TBase>(discriminator, out var materialized) ? materialized : null;

        var columns = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var cell in row)
        {
            if (Array.IndexOf(ReservedCells, cell.Key) >= 0)
                continue;

            columns[cell.Key] = cell.Value;
        }

        return new PolymorphicEntry<TBase>(
            new TableKey(row.PartitionKey, row.RowKey),
            item,
            storedDiscriminator,
            stored.ETag,
            timestampKnown ? stored.Timestamp : null,
            columns);
    }

    // Isolation: a caller mutating a returned row must not reach into the store, and a stored row
    // must not change under a caller who is still holding the object they wrote. A byte[] cell is the
    // one mutable cell value, so each array is copied too, on the way in and on the way out.
    private static TableEntity CopyOf(TableEntity source)
    {
        var copy = new TableEntity(source.PartitionKey, source.RowKey);
        foreach (var cell in source)
        {
            if (Array.IndexOf(ReservedCells, cell.Key) >= 0)
                continue;

            copy[cell.Key] = cell.Value is byte[] bytes ? bytes.Clone() : cell.Value;
        }

        return copy;
    }

    // The same check and message as PolymorphicTableStorage<TBase>.ValidateKey: the two stores throw
    // byte-identical messages (PolymorphicMessages).
    private static void ValidateKey(TableKey key)
    {
        if (string.IsNullOrEmpty(key.PartitionKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.PartitionKey)), nameof(key));
        if (string.IsNullOrEmpty(key.RowKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.RowKey)), nameof(key));
    }

    // As PolymorphicTableStorage<TBase>.ValidateSystemColumn; the paramName is deliberately
    // nameof(columnName) in both, so a caller comparing ex.ParamName sees the two stores agree.
    private static void ValidateSystemColumn(string columnName)
    {
        if (!SystemColumnNames.IsSystemColumn(columnName))
            throw new ArgumentException(PolymorphicMessages.NotSystemColumn(columnName), nameof(columnName));

        if (string.Equals(columnName, SystemColumnNames.TypeName, StringComparison.Ordinal))
            throw new ArgumentException(PolymorphicMessages.TypeNameNotMergeable(), nameof(columnName));
    }
}
