using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Data.Tables;

namespace Unified.Data.Tables;

/// <summary>
/// <see cref="ITableStore"/> over an Azure Tables account: lists, tests, creates and deletes its tables
/// and opens one as untyped rows (<see cref="IRawTable"/>). Registered by <c>AddUnifiedTableStorage</c>
/// over the registered <see cref="TableServiceClient"/>; construct it directly over any client.
/// </summary>
/// <remarks>
/// <b>Experimental (UDT0001).</b> The shape may change in any release before 1.0. Failures are the
/// provider-neutral ones of the typed stores, through the same mapping: the SDK's exception is always the
/// inner exception. Table names are compared by the service, which ignores their case.
/// </remarks>
[Experimental(RawTableDiagnostics.Id)]
public sealed class AzureTableStore : ITableStore
{
    private readonly TableServiceClient serviceClient;

    /// <summary>Creates the store over an account's client. No request is sent.</summary>
    /// <param name="serviceClient">The Azure Tables service client.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceClient"/> is null.</exception>
    public AzureTableStore(TableServiceClient serviceClient)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        this.serviceClient = serviceClient;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> ListTablesAsync(CancellationToken ct = default) =>
        AzureStorageErrors.GuardAsync(ListAsync(ct), ex => AzureStorageErrors.Map(ex, "Tables", "Tables", id: null, write: false), ct);

    private async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var table in serviceClient.QueryAsync(cancellationToken: ct).WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return table.Name;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Asked of the table itself (the first row of it, keys only), not of the account's table list: it
    /// needs no more than the right to read the table, and answers as the service resolves the name.
    /// </remarks>
    public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        try
        {
            var firstRow = serviceClient.GetTableClient(tableName).QueryAsync<TableEntity>(
                filter: (string?)null, maxPerPage: 1, select: KeysOnly, cancellationToken: ct);
            await using var pages = firstRow.AsPages(pageSizeHint: 1).GetAsyncEnumerator(ct);
            await pages.MoveNextAsync();
            return true;
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            return false;
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw AzureStorageErrors.Map(ex, tableName, tableName, id: null, write: false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CreateTableAsync(string tableName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        try
        {
            // The SDK returns the 409 of an existing table instead of throwing it.
            var response = await serviceClient.CreateTableIfNotExistsAsync(tableName, ct);
            var raw = response?.GetRawResponse();
            if (raw is null || raw.Status != 409)
                return true;
            // Any other 409 (a table of that name is still being deleted) is not "it exists".
            if (raw.Headers.TryGetValue("x-ms-error-code", out var code) && !string.Equals(code, "TableAlreadyExists", StringComparison.Ordinal))
                throw new RequestFailedException(raw);
            return false;
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw AzureStorageErrors.Map(ex, tableName, tableName, id: null, write: true);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteTableAsync(string tableName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        try
        {
            // The SDK returns the 404 of a table that is not there instead of throwing it.
            var response = await serviceClient.DeleteTableAsync(tableName, ct);
            return response is null || response.Status != 404;
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            return false;
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw AzureStorageErrors.Map(ex, tableName, tableName, id: null, write: true);
        }
    }

    /// <inheritdoc />
    public IRawTable GetTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return new AzureRawTable(serviceClient.GetTableClient(tableName), tableName, ensureTable: null);
    }

    private static readonly string[] KeysOnly = ["PartitionKey"];

    // A table that is not there: the service's TableNotFound, or a 404 for the table resource itself.
    private static bool IsTableMissing(RequestFailedException ex) =>
        ex.Status == 404 && (string.IsNullOrEmpty(ex.ErrorCode) || ex.ErrorCode is "TableNotFound" or "ResourceNotFound");
}

/// <summary>
/// <see cref="IRawTable"/> over one Azure table: physical rows with typed cells, no serializer, no key
/// normalization, no read cache.
/// </summary>
/// <param name="client">The client of the table.</param>
/// <param name="tableName">The physical table name.</param>
/// <param name="ensureTable">
/// The lazy creation of the table, for the raw view a typed store hands out of its own table (the store
/// creates its table on first use, through whichever view is used first); null for a table opened by
/// name (<see cref="ITableStore.GetTable"/>), which creates nothing.
/// </param>
[Experimental(RawTableDiagnostics.Id)]
internal sealed class AzureRawTable(TableClient client, string tableName, Func<CancellationToken, Task>? ensureTable) : IRawTable
{
    /// <inheritdoc />
    public string TableName { get; } = tableName;

    private StorageException Failed(Exception ex, bool write, string? id = null) =>
        AzureStorageErrors.Map(ex, TableName, TableName, id, write, write ? RequestAttempts.Current : null);

    private Task EnsureAsync(CancellationToken ct) => ensureTable?.Invoke(ct) ?? Task.CompletedTask;

    /// <inheritdoc />
    public async Task<RawRow?> GetAsync(string partitionKey, string rowKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentNullException.ThrowIfNull(rowKey);
        try
        {
            await EnsureAsync(ct);
            var response = await client.GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct);
            if (response.HasValue && response.Value is { } row)
                return ToRawRow(row);
            // The SDK reads every 404 as "no row", the 404 of a missing table too: told apart here.
            var raw = response.GetRawResponse();
            if (raw is not null && raw.Headers.TryGetValue("x-ms-error-code", out var code)
                && string.Equals(code, "TableNotFound", StringComparison.Ordinal))
                throw new RequestFailedException(raw);
            return null;
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: false, partitionKey + EntityId.Separator + rowKey);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RawRow> QueryAsync(RawQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), query.Take, "Take must be positive.");
        return AzureStorageErrors.GuardAsync(RowsAsync(query, ct), ex => Failed(ex, write: false), ct);
    }

    private async IAsyncEnumerable<RawRow> RowsAsync(RawQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureAsync(ct);

        // Keys as stored: no normalization, and the bounds compared by the service in its key order.
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {query.PartitionKey}");
        if (query.FromRowKey is { } from)
            filter += " and " + (query.FromInclusive
                ? TableClient.CreateQueryFilter($"RowKey ge {from}")
                : TableClient.CreateQueryFilter($"RowKey gt {from}"));
        if (query.ToRowKey is { } to)
            filter += " and " + (query.ToInclusive
                ? TableClient.CreateQueryFilter($"RowKey le {to}")
                : TableClient.CreateQueryFilter($"RowKey lt {to}"));

        var maxPerPage = query.Take is int take ? Math.Min(take, 1000) : (int?)null;
        var yielded = 0;
        await foreach (var row in client.QueryAsync<TableEntity>(filter, maxPerPage, select: null, ct).WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return ToRawRow(row);
            if (query.Take is int limit && ++yielded >= limit)
                yield break;
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
        // anything is sent, and never split.
        var actions = Plan(operations);

        RequestAttempts.Begin();
        try
        {
            await EnsureAsync(ct);
            Response<IReadOnlyList<Response>> committed;
            try
            {
                committed = await client.SubmitTransactionAsync(actions, ct);
            }
            catch (TableTransactionFailedException ex)
                when (ex.FailedTransactionActionIndex is int i && i >= 0 && i < operations.Count)
            {
                // Nothing was applied; the failure is the one of the operation the service names.
                var failed = operations[i].Row;
                throw Failed(ex, write: true, failed.PartitionKey + EntityId.Separator + failed.RowKey);
            }

            var etags = new string?[operations.Count];
            var responses = committed?.Value;
            for (var i = 0; i < operations.Count; i++)
            {
                if (operations[i].Kind != RawOperationKind.Delete && responses is not null && i < responses.Count)
                    etags[i] = ETagOf(responses[i]);
            }
            return etags;
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true);
        }
    }

    private static List<TableTransactionAction> Plan(IReadOnlyList<RawOperation> operations)
    {
        var keys = new List<(string PartitionKey, string RowKey)>(operations.Count);
        foreach (var operation in operations)
        {
            if (operation is null)
                throw new ArgumentException("A transaction cannot hold a null operation.", nameof(operations));
            keys.Add((operation.Row.PartitionKey, operation.Row.RowKey));
        }
        AtomicBatches.RequireOnePartitionEachRowOnce(keys, "transaction", "row");

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
                    row[cell.Key] = cell.Value.Value;
            }
            sizes[i] = TableRowSize.Estimate(row);
            var condition = operation.IfMatch is null ? ETag.All : new ETag(operation.IfMatch);
            actions.Add(operation.Kind switch
            {
                RawOperationKind.Insert => new TableTransactionAction(TableTransactionActionType.Add, row),
                RawOperationKind.Replace => new TableTransactionAction(TableTransactionActionType.UpdateReplace, row, condition),
                RawOperationKind.Merge => new TableTransactionAction(TableTransactionActionType.UpdateMerge, row, condition),
                RawOperationKind.Delete => new TableTransactionAction(TableTransactionActionType.Delete, row, condition),
                _ => throw new ArgumentException($"Unknown operation kind {operation.Kind}.", nameof(operations)),
            });
        }
        AtomicBatches.RequireOneTransaction(sizes, "transaction");
        // The request as it goes on the wire (base64, JSON and the batch envelope) must fit as well.
        if (!TableRequestSize.FitsOneTransaction(actions))
            throw new BatchNotAtomicException(
                "The transaction cannot be sent as one request: its encoded payload exceeds what the service accepts. Nothing was written.");
        return actions;
    }

    private static string? ETagOf(Response? response)
    {
#pragma warning disable CA1031 // a sub-response without readable headers (a test double) reports no version
        try
        {
            return response?.Headers.ETag?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    // The row as stored: every column a typed cell, without the keys, the timestamp and the ETag.
    private static RawRow ToRawRow(TableEntity row)
    {
        var cells = new Dictionary<string, RawCell>(StringComparer.Ordinal);
        foreach (var cell in row)
        {
            if (cell.Key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag" || cell.Value is null)
                continue;
            cells[cell.Key] = cell.Value switch
            {
                string value => RawCell.FromString(value),
                byte[] value => RawCell.FromBinary(value),
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
        var etag = row.ETag.ToString();
        return new RawRow(row.PartitionKey, row.RowKey, cells)
        {
            ETag = string.IsNullOrEmpty(etag) ? null : etag,
            Timestamp = row.Timestamp,
        };
    }
}
