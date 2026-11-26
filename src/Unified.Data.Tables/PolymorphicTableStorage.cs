using System.Diagnostics.CodeAnalysis;
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
/// <para>
/// Beside <see cref="IPolymorphicStorage{TBase}"/> the store implements the side interfaces
/// <see cref="IAtomicInsert{TBase}"/> (an all-or-nothing insert) and <see cref="IStorageInfo"/>, and
/// answers the capability probe (<see cref="IStorageCapabilities"/>); reach them through
/// <see cref="StorageCapabilityExtensions"/>.
/// </para>
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
public sealed class PolymorphicTableStorage<TBase> : IPolymorphicStorage<TBase>, IAtomicInsert<TBase>, IStorageInfo, IStorageCapabilities
    where TBase : class
{
    private static readonly string[] ReservedCells = ["PartitionKey", "RowKey", "Timestamp", "odata.etag"];

    // Counting and partition-deletion never need the payload; projecting keys only turns a full-row
    // scan into a keys scan, which matters because Azure Tables has no server-side count.
    private static readonly string[] KeysOnly = ["PartitionKey", "RowKey"];

    private readonly TableServiceClient serviceClient;
    private readonly TableClient client;
    private readonly TableInitializer initializer;
    private readonly ITypeDiscriminator discriminator;
    private readonly ILogger logger;
    private readonly string tableName;
    private readonly StorageInfo info;

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
        var opts = options ?? new UnifiedTableStorageOptions();
        discriminator = opts.ResolveTypeDiscriminator();
        this.serviceClient = serviceClient;
        client = serviceClient.GetTableClient(tableName);
        initializer = new TableInitializer(client);
        // This store has no read cache: a read never returns a row as it was before another process's write.
        info = AzureStorageInfo.Describe(serviceClient, tableName, opts, readCacheEnabled: false);
    }

    /// <summary>
    /// What the store states about itself: the provider (<c>"AzureTables"</c>), its table, the limits the
    /// library plans and refuses by, that it has no read cache, and how durable an acknowledged write is
    /// (<see cref="Durability.Replicated"/> on an Azure Storage table host, <see cref="Durability.Unknown"/>
    /// on any other, or what <see cref="UnifiedTableStorageOptions.DeclaredDurability"/> declares). Calls
    /// nothing and never throws.
    /// </summary>
    /// <returns>The store's info; the same value on every call.</returns>
    public StorageInfo GetInfo() => info;

#pragma warning disable UDT0001 // the experimental raw-table API is handed out here, not used
    private AzureRawTable? rawTable;
    private AzureTableStore? tableStore;

    /// <summary>
    /// The store's implementation of the side interface <typeparamref name="TCap"/>: the store itself for
    /// <see cref="IAtomicInsert{TBase}"/> and <see cref="IStorageInfo"/>; the untyped view of its table
    /// for the experimental <see cref="IRawTable"/> (it shares the store's lazy table creation); the
    /// tables of its storage account for the experimental <see cref="ITableStore"/>.
    /// </summary>
    /// <typeparam name="TCap">The side interface asked for.</typeparam>
    /// <param name="capability">The implementation to call, or null when the store does not support it.</param>
    /// <returns>True when the store supports <typeparamref name="TCap"/>.</returns>
    public bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
    {
        capability = this as TCap;
        if (capability is null && typeof(TCap) == typeof(IRawTable))
            capability = (rawTable ??= new AzureRawTable(client, tableName, initializer.EnsureAsync)) as TCap;
        if (capability is null && typeof(TCap) == typeof(ITableStore))
            capability = (tableStore ??= new AzureTableStore(serviceClient)) as TCap;
        return capability is not null;
    }
#pragma warning restore UDT0001

    /// <inheritdoc />
    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        try
        {
            await initializer.EnsureAsync(ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true);
        }
    }

    // The provider-neutral exception for an SDK failure that is leaving a member of this store (see
    // AzureStorageErrors for what maps to what). write: the member may change stored data, so a transient
    // failure may have committed and a duplicate may be the call's own retried write, unless the attempts
    // the call saw of its requests (RequestAttempts) show otherwise.
    private StorageException Failed(Exception ex, bool write, string? id = null) =>
        AzureStorageErrors.Map(ex, tableName, tableName, id, write, write ? RequestAttempts.Current : null);

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>> InsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        RequestAttempts.Begin();
        try
        {
            return await InsertCoreAsync(write, ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true, write.Key.ToId());
        }
    }

    private async Task<PolymorphicEntry<TBase>> InsertCoreAsync(PolymorphicWrite<TBase> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = ToRow(write);
        await initializer.EnsureAsync(ct);

        Response response;
        var sent = RequestAttempts.MarkCurrent();
        try
        {
            response = await client.AddEntityAsync(row, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // The SDK may have retried the insert, so the row in the way can be this call's own: it is
            // reported as one when an earlier attempt was sent, or the attempts could not be observed.
            throw AzureStorageErrors.Duplicate(tableName, write.Key.ToId(), ex, RequestAttempts.MayBeOwnWriteSince(sent));
        }

        row.ETag = ETagOf(response, row);
        return ToEntry(row);
    }

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>> UpsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        RequestAttempts.Begin();
        try
        {
            return await UpsertCoreAsync(write, ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true, write.Key.ToId());
        }
    }

    private async Task<PolymorphicEntry<TBase>> UpsertCoreAsync(PolymorphicWrite<TBase> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = ToRow(write);
        // The upsert names the key in its URL (RefusedKeyCharacters); an insert or a batch carries it in its body.
        RefusedKeyCharacters.ThrowIfIn(row.PartitionKey, row.RowKey);
        await initializer.EnsureAsync(ct);
        var response = await client.UpsertEntityAsync(row, TableUpdateMode.Replace, ct);
        row.ETag = ETagOf(response, row);
        return ToEntry(row);
    }

    /// <inheritdoc />
    public async Task<int> InsertBatchAsync(
        IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default)
    {
        RequestAttempts.Begin();
        try
        {
            return await InsertBatchCoreAsync(writes, ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// One entity-group transaction, planned before anything is sent: one partition, at most 100 rows,
    /// each key once, and the payload budget of <see cref="BatchPlanner"/>. A batch that does not fit is
    /// refused with <see cref="BatchNotAtomicException"/>; it is never split.
    /// </remarks>
    public async Task InsertAtomicAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default)
    {
        RequestAttempts.Begin();
        try
        {
            await InsertAtomicCoreAsync(writes, ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true);
        }
    }

    private async Task InsertAtomicCoreAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return;
        ct.ThrowIfCancellationRequested();

        // Every row is built (and validated) first, then the batch is checked as one transaction: both
        // before the table is touched.
        var rows = new List<TableEntity>(writes.Count);
        foreach (var write in writes)
            rows.Add(ToRow(write ?? throw new ArgumentException("A batch cannot hold a null write.", nameof(writes))));
        AtomicBatches.RequireOnePartitionEachRowOnce([.. rows.Select(r => (r.PartitionKey, r.RowKey))], "batch", "key");
        AtomicBatches.RequireOneTransaction([.. rows.Select(TableRowSize.Estimate)], "batch");

        await initializer.EnsureAsync(ct);

        var actions = rows.Select(row => new TableTransactionAction(TableTransactionActionType.Add, row));
        var sent = RequestAttempts.MarkCurrent();
        try
        {
            await client.SubmitTransactionAsync(actions, ct);
        }
        catch (TableTransactionFailedException ex)
            when (ex.Status == 409 || string.Equals(ex.ErrorCode, "InvalidDuplicateRow", StringComparison.Ordinal))
        {
            // A row already exists at one of the keys: nothing of the transaction was applied.
            var duplicate = ex.FailedTransactionActionIndex is int i && i >= 0 && i < rows.Count
                ? new TableKey(rows[i].PartitionKey, rows[i].RowKey).ToId()
                : new TableKey(rows[0].PartitionKey, "?").ToId();
            throw AzureStorageErrors.Duplicate(tableName, duplicate, ex, RequestAttempts.MayBeOwnWriteSince(sent));
        }
    }

    private async Task<int> InsertBatchCoreAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct)
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

                var sent = RequestAttempts.MarkCurrent();
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

                    throw AzureStorageErrors.Duplicate(tableName, duplicate, ex, RequestAttempts.MayBeOwnWriteSince(sent));
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
        RequestAttempts.Begin();
        try
        {
            await MergeColumnsCoreAsync(key, columns, ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true, key.ToId());
        }
    }

    private async Task MergeColumnsCoreAsync(TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct)
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
        RefusedKeyCharacters.ThrowIfIn(key.PartitionKey, key.RowKey);

        await initializer.EnsureAsync(ct);

        // Wildcard ETag and Merge mode: blind, unconditional, and no prior read. A sentinel flip is
        // idempotent and order-independent, so optimistic concurrency here would only manufacture
        // conflicts the caller would have to retry through.
        await client.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct);
    }

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default)
    {
        try
        {
            return await GetCoreAsync(key, ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: false, key.ToId());
        }
    }

    private async Task<PolymorphicEntry<TBase>?> GetCoreAsync(TableKey key, CancellationToken ct)
    {
        ValidateKey(key);
        await initializer.EnsureAsync(ct);
        // No row has a key the service refuses; the request would only change its URL (RefusedKeyCharacters).
        if (RefusedKeyCharacters.In(key.PartitionKey, key.RowKey))
            return null;

        var response = await client.GetEntityIfExistsAsync<TableEntity>(
            key.PartitionKey, key.RowKey, cancellationToken: ct);
        AzureStorageErrors.ThrowIfTableMissing(response);

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
    public IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(
        string? partition = null, int? take = null, CancellationToken ct = default) =>
        AzureStorageErrors.GuardAsync(StreamAsync(partition, take, ct), ex => Failed(ex, write: false), ct);

    private async IAsyncEnumerable<PolymorphicEntry<TBase>> StreamAsync(
        string? partition, int? take, [EnumeratorCancellation] CancellationToken ct)
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
            // The SDK looks at the token only when it asks for the next page: a token cancelled between two
            // rows of one page ends the stream at the next row, as it does on every other provider.
            ct.ThrowIfCancellationRequested();
            yield return ToEntry(row);

            if (take is { } limit && ++yielded >= limit)
                yield break;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(TableKey key, CancellationToken ct = default)
    {
        RequestAttempts.Begin();
        try
        {
            await DeleteCoreAsync(key, ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true, key.ToId());
        }
    }

    private async Task DeleteCoreAsync(TableKey key, CancellationToken ct)
    {
        ValidateKey(key);
        await initializer.EnsureAsync(ct);
        if (RefusedKeyCharacters.In(key.PartitionKey, key.RowKey))
            return; // no row has such a key: nothing to delete (see GetAsync)

        Response? response;
        try
        {
            response = await client.DeleteEntityAsync(key.PartitionKey, key.RowKey, ETag.All, ct);
        }
        catch (RequestFailedException ex) when (AzureStorageErrors.IsMissingRow(ex))
        {
            // Idempotent by contract: deleting what is already gone is the caller's desired state.
            logger.LogDebug("Delete of missing row {Key} in {Table} treated as a no-op.", key, tableName);
            return;
        }
        // Only a missing ROW is a no-op. The SDK returns a delete's 404 instead of throwing it, whatever
        // its code: a 404 that names the table (TableNotFound) is the table's failure and leaves as
        // TableNotFoundException.
        AzureStorageErrors.ThrowIfTableMissing(response);
    }

    /// <inheritdoc />
    public async Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default)
    {
        RequestAttempts.Begin();
        try
        {
            return await DeletePartitionCoreAsync(partition, ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true);
        }
    }

    private async Task<int> DeletePartitionCoreAsync(string partition, CancellationToken ct)
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

            var sent = RequestAttempts.MarkCurrent();
            try
            {
                await client.SubmitTransactionAsync(actions, ct);
                deleted += actions.Count;
            }
            catch (RequestFailedException ex) when (AzureStorageErrors.IsMissingRow(ex))
            {
                // A delete of a missing row fails its whole transaction, and nothing of it is applied:
                // another writer deleted one of the listed rows meanwhile, or an earlier attempt of this
                // very transaction was applied and its response lost. Neither is a failure: the rows of
                // this transaction are deleted one by one, and only the ones this call removed count.
                logger.LogDebug(ex, "A row of partition {Partition} in {Table} was deleted after the listing; its transaction's rows are deleted one by one.",
                    partition, tableName);
                var chunk = rows.GetRange(range.Start, range.Count);
                deleted += chunk.Count - await PartitionDeleteLostRace.DeleteOneByOneAsync(client, chunk, sent, ct);
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(string? partition = null, CancellationToken ct = default)
    {
        try
        {
            return await CountCoreAsync(partition, ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: false);
        }
    }

    private async Task<int> CountCoreAsync(string? partition, CancellationToken ct)
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

        // A TableBlobContent needs a blob upload this store does not perform: refuse it before serializing.
        if (write.Item is not null && TableEntitySerializer.CarriesBlobContent(write.Item))
            throw new NotSupportedException(TableEntitySerializer.PolymorphicWriteRefusal(write.Item.GetType()));

        var row = write.Item is null
            ? new TableEntity(write.Key.PartitionKey, write.Key.RowKey)
            : write.Item.ToTableEntity(write.Key.PartitionKey, write.Key.RowKey);

        // A split value spans cells (and, through IStorage<T>, continuation rows and blobs) that this
        // store neither writes atomically nor reassembles on read, so refuse it rather than store half.
        if (write.Item is not null && TableEntitySerializer.HasLargeValueCells(row))
            throw new NotSupportedException(TableEntitySerializer.PolymorphicWriteRefusal(write.Item.GetType()));

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

            // A null system column is not stored (the service drops a null property), so the entry an
            // insert returns from the row it built leaves it out too, as a later read and the local
            // providers do.
            if (cell.Value is null)
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
