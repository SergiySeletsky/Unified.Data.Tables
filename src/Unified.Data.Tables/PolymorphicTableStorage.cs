using System.Runtime.CompilerServices;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace Unified.Data.Tables;

/// <summary>
/// Azure Table Storage implementation of <see cref="IPolymorphicStorage{TBase}"/>: many concrete
/// types in one table, discriminated by <see cref="SystemColumnNames.TypeName"/>.
/// </summary>
/// <remarks>
/// The table name is supplied explicitly rather than derived from <typeparamref name="TBase"/>: a
/// base type says nothing about which of several tables holds it, and one base commonly addresses
/// more than one table.
/// <para>
/// There is no cache. <c>TableStorage&lt;T&gt;</c> keys its cache on <c>typeof(T).FullName</c>, so
/// two stores over one table would never invalidate each other, and its snapshot round-trips
/// through the base-typed read — silently downcasting a derived instance and dropping its data.
/// Rather than mitigate three coupled hazards, this store has none, which also suits the
/// append-only fact tables it is designed for.
/// </para>
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
public sealed class PolymorphicTableStorage<TBase> : IPolymorphicStorage<TBase>
    where TBase : class
{
    private static readonly string[] ReservedCells = ["PartitionKey", "RowKey", "Timestamp", "odata.etag"];

    // Counting and partition-deletion never need the payload; projecting keys only turns a full-row
    // scan into a keys scan, which matters because Azure Tables has no server-side count.
    private static readonly string[] KeysOnly = ["PartitionKey", "RowKey"];

    private readonly TableClient client;
    private readonly TableInitializer initializer;
    private readonly ITypeDiscriminator discriminator;
    private readonly ILogger logger;
    private readonly string tableName;

    /// <summary>Creates a store over one named table.</summary>
    /// <param name="serviceClient">The Azure Tables service client.</param>
    /// <param name="tableName">The table this store owns.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Options; null selects the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceClient"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is null, empty or whitespace.</exception>
    public PolymorphicTableStorage(
        TableServiceClient serviceClient,
        string tableName,
        ILogger<PolymorphicTableStorage<TBase>> logger,
        UnifiedTableStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        this.tableName = tableName;
        this.logger = logger;
        discriminator = (options ?? new UnifiedTableStorageOptions()).ResolveTypeDiscriminator();
        client = serviceClient.GetTableClient(tableName);
        initializer = new TableInitializer(client);
    }

    /// <inheritdoc />
    public Task EnsureCreatedAsync(CancellationToken ct = default) => initializer.EnsureAsync(ct);

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>> InsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = ToRow(write);
        await initializer.EnsureAsync(ct);

        Response response;
        try
        {
            response = await client.AddEntityAsync(row, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw new DuplicateKeyException(tableName, write.Key.ToId(), ex);
        }

        row.ETag = ETagOf(response, row);
        return ToEntry(row);
    }

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>> UpsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = ToRow(write);
        await initializer.EnsureAsync(ct);
        var response = await client.UpsertEntityAsync(row, TableUpdateMode.Replace, ct);
        row.ETag = ETagOf(response, row);
        return ToEntry(row);
    }

    /// <inheritdoc />
    public async Task<int> InsertBatchAsync(
        IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return 0;

        // Build every row first, so a validation failure costs nothing rather than surfacing after
        // earlier partitions have already committed.
        var rows = new List<TableEntity>(writes.Count);
        foreach (var write in writes)
            rows.Add(ToRow(write));

        await initializer.EnsureAsync(ct);

        var written = 0;

        // Azure requires an Entity Group Transaction to be single-partition, so partition first and
        // chunk within each group.
        foreach (var group in rows.GroupBy(r => r.PartitionKey, StringComparer.Ordinal))
        {
            var groupRows = group.ToList();
            var plan = BatchPlanner.Plan([.. groupRows.Select(TableRowSize.Estimate)]);

            foreach (var range in plan)
            {
                var actions = new List<TableTransactionAction>(range.Count);
                for (var i = range.Start; i < range.Start + range.Count; i++)
                    actions.Add(new TableTransactionAction(TableTransactionActionType.Add, groupRows[i]));

                try
                {
                    await client.SubmitTransactionAsync(actions, ct);
                }
                catch (TableTransactionFailedException ex)
                    when (ex.Status == 409 || string.Equals(ex.ErrorCode, "InvalidDuplicateRow", StringComparison.Ordinal))
                {
                    // 409 = a row already exists in the table; 400 InvalidDuplicateRow = the same key
                    // twice WITHIN this transaction. Both are duplicate keys, both are what a single
                    // InsertAsync surfaces as DuplicateKeyException, and the in-memory fake raises
                    // DuplicateKeyException for both — leaving the raw transaction exception here
                    // would make a green test against the fake say nothing about Azure. Ported
                    // verbatim from TableStorage<T>.WriteBatchAsync, including the "{partition}|?"
                    // fallback for a service response that names no failing action.
                    var duplicate = ex.FailedTransactionActionIndex is int i && i >= 0 && i < range.Count
                        ? new TableKey(
                            groupRows[range.Start + i].PartitionKey,
                            groupRows[range.Start + i].RowKey).ToId()
                        : new TableKey(group.Key, "?").ToId();

                    throw new DuplicateKeyException(tableName, duplicate, ex);
                }

                written += actions.Count;
            }
        }

        return written;
    }

    /// <inheritdoc />
    public async Task MergeColumnsAsync(
        TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "A merge with no columns would be a network round trip that changes nothing.",
                nameof(columns));
        }

        var patch = new TableEntity(key.PartitionKey, key.RowKey);
        foreach (var column in columns)
        {
            ValidateSystemColumn(column.Key);
            patch[column.Key] = column.Value;
        }

        await initializer.EnsureAsync(ct);

        // Wildcard ETag and Merge mode: blind, unconditional, and no prior read. A sentinel flip is
        // idempotent and order-independent, so optimistic concurrency here would only manufacture
        // conflicts the caller would have to retry through.
        await client.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct);
    }

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default)
    {
        ValidateKey(key);
        await initializer.EnsureAsync(ct);

        var response = await client.GetEntityIfExistsAsync<TableEntity>(
            key.PartitionKey, key.RowKey, cancellationToken: ct);

        return response.HasValue ? ToEntry(response.Value!) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(
        string? partition = null, int? take = null, CancellationToken ct = default)
    {
        var results = new List<PolymorphicEntry<TBase>>();
        await foreach (var entry in QueryStreamAsync(partition, take, ct))
            results.Add(entry);

        return results;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(
        string? partition = null,
        int? take = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (take is <= 0)
            yield break;

        await initializer.EnsureAsync(ct);

        var yielded = 0;

        // AsyncPageable follows continuation tokens internally. That is the whole reason streaming is
        // the primitive here: a hand-rolled single-segment read silently truncates at one page, and
        // the truncation looks exactly like an empty tail.
        await foreach (var row in client.QueryAsync<TableEntity>(
                           PartitionFilter(partition), maxPerPage: null, select: null, ct))
        {
            yield return ToEntry(row);

            if (take is { } limit && ++yielded >= limit)
                yield break;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(TableKey key, CancellationToken ct = default)
    {
        ValidateKey(key);
        await initializer.EnsureAsync(ct);

        try
        {
            await client.DeleteEntityAsync(key.PartitionKey, key.RowKey, ETag.All, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Idempotent by contract: deleting what is already gone is the caller's desired state.
            logger.LogDebug("Delete of missing row {Key} in {Table} treated as a no-op.", key, tableName);
        }
    }

    /// <inheritdoc />
    public async Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(partition);
        await initializer.EnsureAsync(ct);

        var rows = new List<TableEntity>();
        await foreach (var row in client.QueryAsync<TableEntity>(
                           PartitionFilter(partition), maxPerPage: null, KeysOnly, ct))
        {
            rows.Add(row);
        }

        if (rows.Count == 0)
            return 0;

        var deleted = 0;

        // Already single-partition by construction, so one plan covers it.
        foreach (var range in BatchPlanner.Plan([.. rows.Select(TableRowSize.Estimate)]))
        {
            var actions = new List<TableTransactionAction>(range.Count);
            for (var i = range.Start; i < range.Start + range.Count; i++)
                actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, rows[i], ETag.All));

            await client.SubmitTransactionAsync(actions, ct);
            deleted += actions.Count;
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(string? partition = null, CancellationToken ct = default)
    {
        await initializer.EnsureAsync(ct);

        var count = 0;
        await foreach (var _ in client.QueryAsync<TableEntity>(
                           PartitionFilter(partition), maxPerPage: null, KeysOnly, ct))
        {
            count++;
        }

        return count;
    }

    // Build the row: serialize the object with persistType FALSE, then stamp the discriminator
    // ourselves. Composing this way rather than extending ToTableEntity is what gives the
    // ITypeDiscriminator seam for free, with zero change to the existing write path.
    private TableEntity ToRow(PolymorphicWrite<TBase> write)
    {
        ValidateKey(write.Key);

        var row = write.Item is null
            ? new TableEntity(write.Key.PartitionKey, write.Key.RowKey)
            : write.Item.ToTableEntity(write.Key.PartitionKey, write.Key.RowKey);

        if (write.Item is not null)
            row[SystemColumnNames.TypeName] = discriminator.ToDiscriminator(write.Item.GetType());

        if (write.SystemColumns is null)
            return row;

        foreach (var column in write.SystemColumns)
        {
            ValidateSystemColumn(column.Key);
            row[column.Key] = column.Value;
        }

        return row;
    }

    // The written row's version comes from the RESPONSE headers, not from the entity we handed the
    // SDK: AddEntityAsync/UpsertEntityAsync do not mutate it, so ToEntry on the locally-built row
    // returned PolymorphicEntry.ETag == null on every Azure write while the in-memory fake returned a
    // real one. Copied from TableStorage<T>.CreateAsync, including the try/catch — a substitute
    // Response whose Headers struct has no backing store throws on the property read.
    private static ETag ETagOf(Response? response, TableEntity row)
    {
        ETag? fromHeaders = null;
        try
        {
            fromHeaders = response?.Headers.ETag;
        }
        catch
        {
            // Test doubles may return a Response whose Headers struct has no backing store; fall
            // through to the entity's own ETag.
        }

        return fromHeaders ?? row.ETag;
    }

    private PolymorphicEntry<TBase> ToEntry(TableEntity row)
    {
        string? storedDiscriminator = null;
        if (row.TryGetValue(SystemColumnNames.TypeName, out var raw)
            && raw is string token
            && token.Length > 0)
        {
            storedDiscriminator = token;
        }

        // Throws for a discriminator that is present but broken; returns false only for a row that
        // never carried one.
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
            row.ETag == default ? null : row.ETag.ToString(),
            row.Timestamp,
            columns);
    }

    // OData string literals escape an apostrophe by doubling it. Without this a partition key like
    // "O'Brien" produces a malformed filter that the service rejects — or, worse, one that parses
    // into a different query.
    private static string? PartitionFilter(string? partition) =>
        partition is null
            ? null
            : $"PartitionKey eq '{partition.Replace("'", "''", StringComparison.Ordinal)}'";

    private static void ValidateKey(TableKey key)
    {
        if (string.IsNullOrEmpty(key.PartitionKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.PartitionKey)), nameof(key));
        if (string.IsNullOrEmpty(key.RowKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.RowKey)), nameof(key));
    }

    private static void ValidateSystemColumn(string columnName)
    {
        if (!SystemColumnNames.IsSystemColumn(columnName))
            throw new ArgumentException(PolymorphicMessages.NotSystemColumn(columnName), nameof(columnName));

        if (string.Equals(columnName, SystemColumnNames.TypeName, StringComparison.Ordinal))
            throw new ArgumentException(PolymorphicMessages.TypeNameNotMergeable(), nameof(columnName));
    }
}
