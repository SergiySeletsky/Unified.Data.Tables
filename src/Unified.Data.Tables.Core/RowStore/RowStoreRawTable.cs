using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Providers;

/// <summary>
/// <see cref="ITableStore"/> over the account of a row store: lists, tests, creates and deletes its
/// tables and opens one as untyped rows (<see cref="IRawTable"/>), as <c>AzureTableStore</c> does over an
/// Azure Tables account. Table names are compared ignoring case, as the service compares them.
/// </summary>
[Experimental(RawTableDiagnostics.Id)]
internal sealed partial class RowStoreTableStore(IRowStoreAccount account) : ITableStore
{
    // What the Table service accepts as a table name: a letter, then 2 to 62 letters and digits.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{2,62}\z")]
    private static partial Regex TableNameRule();

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var name in account.ListTablesAsync(ct).WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return name;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ct.ThrowIfCancellationRequested();
        return await account.TableExistsAsync(tableName, ct);
    }

    /// <inheritdoc />
    public async Task<bool> CreateTableAsync(string tableName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ct.ThrowIfCancellationRequested();
        // A name the service refuses is refused here, so a table that can be created locally can be
        // created on Azure. (A typed store's own table is created under whatever name it resolves.) The
        // service reserves the name "tables", in any case.
        if (!TableNameRule().IsMatch(tableName) || string.Equals(tableName, "tables", StringComparison.OrdinalIgnoreCase))
            throw ServiceErrors.Neutral(
                new Azure.RequestFailedException(400, "The specified resource name contains invalid characters.", "InvalidResourceName", null),
                tableName, id: null);
        return await account.CreateTableAsync(tableName, ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteTableAsync(string tableName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ct.ThrowIfCancellationRequested();
        return await account.DeleteTableAsync(tableName, ct);
    }

    /// <inheritdoc />
    public IRawTable GetTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return new RowStoreRawTable(account.GetTable(tableName), tableName, ensureTable: null);
    }
}

/// <summary>
/// <see cref="IRawTable"/> over the rows of one table of a row store: physical rows with typed cells, no
/// serializer, no key normalization, no read cache. A transaction is one commit of the row store, planned
/// as <c>AzureRawTable</c> plans its entity-group transaction (count, partition, repeated rows, bytes) and
/// refused by the service's rules for keys and sizes.
/// </summary>
/// <param name="rows">The rows of the table.</param>
/// <param name="tableName">The physical table name.</param>
/// <param name="ensureTable">
/// The lazy creation of the table, for the raw view a typed store hands out of its own table; null for a
/// table opened by name (<see cref="ITableStore.GetTable"/>), which creates nothing.
/// </param>
[Experimental(RawTableDiagnostics.Id)]
internal sealed class RowStoreRawTable(IRowStore rows, string tableName, Func<CancellationToken, ValueTask>? ensureTable) : IRawTable
{
    /// <inheritdoc />
    public string TableName { get; } = tableName;

    // A version no row has: the condition of a merge that found no row, so a row that appears before the
    // commit is merged again instead of being replaced by the merge's cells alone.
    private const string NoVersion = "\0";

    // How many times in all a transaction whose unconditional merge lost to another writer is computed and committed.
    private const int MaxMergeAttempts = 1000;

    private ValueTask EnsureAsync(CancellationToken ct) => ensureTable?.Invoke(ct) ?? default;

    /// <inheritdoc />
    public async Task<RawRow?> GetAsync(string partitionKey, string rowKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentNullException.ThrowIfNull(rowKey);
        ct.ThrowIfCancellationRequested();
        try
        {
            await EnsureAsync(ct);
            return await rows.GetAsync(partitionKey, rowKey, ct) is { } row ? ToRawRow(row) : null;
        }
        catch (RowStoreTableMissingException ex)
        {
            throw ServiceErrors.TableMissing(TableName, ex);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RawRow> QueryAsync(RawQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), query.Take, "Take must be positive.");
        return RowsAsync(query, ct);
    }

    private async IAsyncEnumerable<RawRow> RowsAsync(RawQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Keys as stored: no normalization, and the bounds compared in the store's key order.
        var range = new RowRange(query.PartitionKey, query.FromRowKey, query.FromInclusive, query.ToRowKey, query.ToInclusive);
        var pageSize = query.Take is int take ? Math.Min(take, 1000) : 1000;
        var yielded = 0;
        while (true)
        {
            IReadOnlyList<StoredRow> page;
            try
            {
                await EnsureAsync(ct);
                page = await rows.ReadAsync(range, pageSize, ct);
            }
            catch (RowStoreTableMissingException ex)
            {
                throw ServiceErrors.TableMissing(TableName, ex);
            }
            foreach (var row in page)
            {
                ct.ThrowIfCancellationRequested();
                yield return ToRawRow(row);
                if (query.Take is int limit && ++yielded >= limit)
                    yield break;
            }
            if (page.Count < pageSize)
                yield break;
            range = range with { After = (page[page.Count - 1].PartitionKey, page[page.Count - 1].RowKey) };
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string?>> SubmitAsync(IReadOnlyList<RawOperation> operations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
            return [];
        ct.ThrowIfCancellationRequested();

        // One transaction or none: planned by row count, partition, repeated rows and bytes before
        // anything is written, and never split.
        var planned = Plan(operations);
        var partition = operations[0].Row.PartitionKey;
        try
        {
            await EnsureAsync(ct);
            for (var attempt = 1; ; attempt++)
            {
                // A merge is the stored row with the named cells laid over it, written on condition of
                // the version it was computed from; a row another writer changed meanwhile is merged again.
                var writes = new List<RowWrite>(operations.Count);
                for (var i = 0; i < operations.Count; i++)
                    writes.Add(await WriteOfAsync(operations[i], planned[i], ct));

                var result = await rows.CommitAsync(partition, writes, ct);
                if (result.Committed)
                    return result.ETags;
                var failed = operations[result.FailedIndex];
                // Bounded: a merge only loses to a writer that committed meanwhile, so a store that keeps
                // refusing it is not making progress, and the caller hears of it instead of a spinning call.
                if (result.Failure == RowCommitFailure.VersionMismatch && failed is { Kind: RawOperationKind.Merge, IfMatch: null }
                    && attempt < MaxMergeAttempts)
                    continue;

                // Nothing was applied; the failure is the one of the operation the store names.
                var id = failed.Row.PartitionKey + EntityId.Separator + failed.Row.RowKey;
                throw ServiceErrors.Neutral(ServiceErrors.InTransaction(result.Failure switch
                {
                    RowCommitFailure.AlreadyExists => ServiceErrors.AlreadyExists(id),
                    RowCommitFailure.NotFound => ServiceErrors.NotFound(id),
                    _ => ServiceErrors.ConditionNotSatisfied(),
                }), TableName, id);
            }
        }
        catch (RowStoreTableMissingException ex)
        {
            throw ServiceErrors.TableMissing(TableName, ex);
        }
    }

    private async ValueTask<RowWrite> WriteOfAsync(RawOperation operation, TableEntity row, CancellationToken ct)
    {
        switch (operation.Kind)
        {
            case RawOperationKind.Insert:
                return RowWrite.Insert(row);
            case RawOperationKind.Replace:
                return RowWrite.Replace(row, operation.IfMatch);
            case RawOperationKind.Delete:
                return RowWrite.Delete(row.RowKey, operation.IfMatch);
            default:
                var stored = await rows.GetAsync(row.PartitionKey, row.RowKey, ct);
                // A row that is not there, or is not the version the caller named: the commit reports it.
                if (stored is null || (operation.IfMatch is not null && !string.Equals(stored.ETag, operation.IfMatch, StringComparison.Ordinal)))
                    return RowWrite.Replace(row, operation.IfMatch ?? NoVersion);
                var merged = RowCells.Copy(stored.Data);
                foreach (var cell in row)
                {
                    if (cell.Key is not ("PartitionKey" or "RowKey" or "odata.etag" or "Timestamp"))
                        merged[cell.Key] = cell.Value;
                }
                if (ServiceErrors.RefusedRow(merged) is { } refused)
                    throw ServiceErrors.Neutral(ServiceErrors.InTransaction(refused), TableName, row.PartitionKey + EntityId.Separator + row.RowKey);
                return RowWrite.Replace(merged, stored.ETag);
        }
    }

    // The row of each operation as the store keeps it (a private copy of the caller's cells), after the
    // checks AzureRawTable makes before it sends anything, and the service's own for keys and sizes.
    private List<TableEntity> Plan(IReadOnlyList<RawOperation> operations)
    {
        var keys = new List<(string PartitionKey, string RowKey)>(operations.Count);
        foreach (var operation in operations)
        {
            if (operation is null)
                throw new ArgumentException("A transaction cannot hold a null operation.", nameof(operations));
            keys.Add((operation.Row.PartitionKey, operation.Row.RowKey));
        }
        AtomicBatches.RequireOnePartitionEachRowOnce(keys, "transaction", "row");

        var planned = new List<TableEntity>(operations.Count);
        var actions = new List<TableTransactionAction>(operations.Count);
        var sizes = new long[operations.Count];
        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            var row = new TableEntity(operation.Row.PartitionKey, operation.Row.RowKey);
            if (operation.Kind != RawOperationKind.Delete)
            {
                RawRow.RequireNoReservedCell(operation.Row.Cells, nameof(operations));
                foreach (var cell in operation.Row.Cells)
                    row[cell.Key] = RowCells.Detached(cell.Value.Value);
            }
            sizes[i] = TableRowSize.Estimate(row);
            planned.Add(row);
            actions.Add(new TableTransactionAction(operation.Kind switch
            {
                RawOperationKind.Insert => TableTransactionActionType.Add,
                RawOperationKind.Replace => TableTransactionActionType.UpdateReplace,
                RawOperationKind.Merge => TableTransactionActionType.UpdateMerge,
                RawOperationKind.Delete => TableTransactionActionType.Delete,
                _ => throw new ArgumentException($"Unknown operation kind {operation.Kind}.", nameof(operations)),
            }, row));
        }
        AtomicBatches.RequireOneTransaction(sizes, "transaction");
        // The request as it would go on the wire (base64, JSON and the batch envelope) must fit as well.
        if (!TableRequestSize.FitsOneTransaction(actions))
            throw new BatchNotAtomicException(
                "The transaction cannot be sent as one request: its encoded payload exceeds what the service accepts. Nothing was written.");

        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i].Kind != RawOperationKind.Delete
                && (ServiceErrors.RefusedKey(planned[i].PartitionKey, planned[i].RowKey) ?? ServiceErrors.RefusedRow(planned[i])) is { } refused)
                throw ServiceErrors.Neutral(ServiceErrors.InTransaction(refused), TableName,
                    planned[i].PartitionKey + EntityId.Separator + planned[i].RowKey);
        }
        return planned;
    }

    // The row as stored: every column a typed cell, without the keys, the timestamp and the ETag.
    private static RawRow ToRawRow(StoredRow stored)
    {
        var cells = new Dictionary<string, RawCell>(StringComparer.Ordinal);
        foreach (var cell in stored.Data)
        {
            if (cell.Key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag" || cell.Value is null)
                continue;
            cells[cell.Key] = cell.Value switch
            {
                string value => RawCell.FromString(value),
                byte[] value => RawCell.FromBinary((byte[])value.Clone()),
                BinaryData value => RawCell.FromBinary(value.ToArray()),
                bool value => RawCell.FromBoolean(value),
                DateTimeOffset value => RawCell.FromDateTime(value),
                DateTime value => RawCell.FromDateTime(value),
                double value => RawCell.FromDouble(value),
                Guid value => RawCell.FromGuid(value),
                int value => RawCell.FromInt32(value),
                long value => RawCell.FromInt64(value),
                var other => throw new NotSupportedException(
                    $"The column '{cell.Key}' holds a {other.GetType().Name}, which is not one of the eight types of a table cell."),
            };
        }
        return new RawRow(stored.PartitionKey, stored.RowKey, cells)
        {
            ETag = stored.ETag,
            Timestamp = stored.Timestamp,
        };
    }
}
