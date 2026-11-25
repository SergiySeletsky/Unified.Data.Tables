using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables;

// The side interfaces of the store (0.10.0): the capability probe, conditional delete, all-or-nothing
// batches and what the store states about itself. None of them is a member of IStorage<T>; callers
// reach them through the extension methods of StorageCapabilityExtensions.
public partial class TableStorage<T> : IStorageCapabilities, IConditionalDelete<T>, IAtomicBatch<T>, IStorageInfo
{
    // What the store states about itself, fixed when it is constructed (see AzureStorageInfo).
    private readonly StorageInfo info;

#pragma warning disable UDT0001 // the experimental raw-table API is handed out here, not used
    // The raw view of this store's table, built when first asked for.
    private AzureRawTable? rawTable;

    /// <summary>
    /// The store's implementation of the side interface <typeparamref name="TCap"/>: the store itself for
    /// <see cref="IConditionalDelete{T}"/>, <see cref="IAtomicBatch{T}"/>, <see cref="IStorageInfo"/> and
    /// <see cref="IOrphanBlobSweeper"/>; the untyped view of its own table for the experimental
    /// <see cref="IRawTable"/> (it shares the store's lazy table creation, and bypasses its read cache);
    /// the tables of its storage account for the experimental <see cref="ITableStore"/>.
    /// </summary>
    /// <typeparam name="TCap">The side interface asked for.</typeparam>
    /// <param name="capability">The implementation to call, or null when the store does not support it.</param>
    /// <returns>True when the store supports <typeparamref name="TCap"/>.</returns>
    public bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
    {
        capability = this as TCap;
        if (capability is null && typeof(TCap) == typeof(IRawTable))
            capability = (rawTable ??= new AzureRawTable(client, tableName, EnsureTableAsync)) as TCap;
        if (capability is null && typeof(TCap) == typeof(ITableStore))
            capability = new AzureTableStore(serviceClient) as TCap;
        return capability is not null;
    }
#pragma warning restore UDT0001

    /// <summary>
    /// What the store states about itself: the provider (<c>"AzureTables"</c>), its table, the limits the
    /// library plans and refuses by (100 rows and 3,000,000 bytes a transaction, 64 KiB a cell, 1 MB a
    /// row), whether its read cache is enabled for <typeparamref name="T"/>, and how durable an
    /// acknowledged write is: <see cref="Durability.Replicated"/> when the endpoint's host is an Azure
    /// Storage table host, <see cref="Durability.Unknown"/> for any other host (Azurite, a custom domain,
    /// Cosmos DB), or what <see cref="UnifiedTableStorageOptions.DeclaredDurability"/> declares. Calls
    /// nothing and never throws.
    /// </summary>
    /// <returns>The store's info; the same value on every call.</returns>
    public StorageInfo GetInfo() => info;

    /// <inheritdoc />
    /// <remarks>
    /// One request for a type without large values: whether there was a row is read from the response.
    /// A type with large values is read first (its continuation rows and blobs go with it), and a row
    /// another writer changed between that read and the delete is read again, as in
    /// <see cref="DeleteAsync"/>. <see cref="DeleteStatus.Indeterminate"/> is returned for a row that is
    /// gone when the SDK sent the delete more than once, and when the attempts cannot be observed (a
    /// client built without <see cref="TableClientOptionsExtensions.AddRetryObservation"/>): the first
    /// attempt may have been the one that removed it.
    /// </remarks>
    public async Task<DeleteStatus> DeleteIfExistsAsync(string id, CancellationToken ct = default)
    {
        RequestAttempts.Begin();
        try
        {
            ct.ThrowIfCancellationRequested();
            return await DeleteCoreAsync(id, ifMatch: null, ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true, id);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The delete is sent with the caller's ETag as its condition and is never repeated on another
    /// version: a 412 is the <see cref="ConcurrencyConflictException"/>. A type with large values is read
    /// first, and a version other than <paramref name="etag"/> found by that read is the same conflict
    /// with no request sent. <see cref="DeleteStatus.Indeterminate"/> as in <see cref="DeleteIfExistsAsync"/>.
    /// </remarks>
    public async Task<DeleteStatus> DeleteIfMatchAsync(string id, string etag, CancellationToken ct = default)
    {
        ETagCondition.Require(etag, nameof(etag));
        RequestAttempts.Begin();
        try
        {
            ct.ThrowIfCancellationRequested();
            return await DeleteCoreAsync(id, new ETag(etag), ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true, id);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// One entity-group transaction, planned before anything is sent: one partition, at most 100 rows,
    /// each row once, and the payload budget of <see cref="BatchPlanner"/>. For a type with large values
    /// the continuation rows of an entity are rows of the transaction, and a value that would be stored
    /// in Blob storage is refused with <see cref="BatchNotAtomicException"/>: a blob is written outside
    /// the transaction. Like the other batch writes it bypasses [ProtectedProperty] enforcement and
    /// leaves each entity's <see cref="IEntity.ETag"/> null (read the row before an optimistic update).
    /// </remarks>
    public Task CreateAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) =>
        WriteAtomicAsync(entities, TableTransactionActionType.Add, ct);

    /// <inheritdoc />
    /// <remarks>
    /// One entity-group transaction, planned before anything is sent (see <see cref="CreateAtomicAsync"/>
    /// for the limits and for large values). For a type with large values the stored rows are read first
    /// and replaced on condition of the versions read, so a concurrent writer makes the transaction fail
    /// as a whole and it is planned again; after five lost races it throws
    /// <see cref="ConcurrencyConflictException"/>, with nothing written.
    /// </remarks>
    public Task UpsertAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) =>
        WriteAtomicAsync(entities, TableTransactionActionType.UpsertReplace, ct);

    private async Task WriteAtomicAsync(IReadOnlyCollection<T> entities, TableTransactionActionType actionType, CancellationToken ct)
    {
        RequestAttempts.Begin();
        try
        {
            await WriteAtomicCoreAsync(entities, actionType, ct);
        }
        catch (Exception ex) when (AzureStorageErrors.IsProviderFailure(ex, ct))
        {
            throw Failed(ex, write: true);
        }
    }

    private async Task WriteAtomicCoreAsync(IReadOnlyCollection<T> entities, TableTransactionActionType actionType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return;
        ct.ThrowIfCancellationRequested();

        // What needs no serializing is checked first, before an entity is stamped: a row count, a second
        // partition and a row named twice are refused with the caller's entities as they were.
        var ids = new List<string>(entities.Count);
        var keys = new List<(string PartitionKey, string RowKey)>(entities.Count);
        foreach (var entity in entities)
        {
            if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
                throw new ArgumentException("Every batched entity must be non-null and have an Id.", nameof(entities));
            var id = NormalizeId(entity.Id);
            var key = GetEntityKeys(id);
            if (LargeValueAware)
                SplitRowLayout.ValidateKeys(key.PartitionKey, key.RowKey);
            ids.Add(id);
            keys.Add(key);
        }
        AtomicBatches.RequireOnePartitionEachRowOnce(keys, "batch", "row");

        await EnsureTableAsync(ct);

        var partition = keys[0].PartitionKey;
        var before = entities.Select(e => (Entity: e, e.Id, e.CreatedAt, e.UpdatedAt, e.Timestamp, e.ETag)).ToList();
        var now = clock.GetUtcNow();
        var index = 0;
        foreach (var entity in entities)
        {
            if (actionType == TableTransactionActionType.Add || entity.CreatedAt == default)
                entity.CreatedAt = now;
            entity.UpdatedAt = now;
            entity.Timestamp = null;
            entity.ETag = null; // as after any batch: the row's version is read before an optimistic update
            entity.Id = ids[index++];
        }

        try
        {
            if (LargeValueAware)
                await WriteAtomicLargeAsync([.. entities], actionType, ct);
            else
                await WriteAtomicRowsAsync([.. entities], keys, actionType, ct);
        }
        catch (BatchNotAtomicException)
        {
            // Refused before anything was sent: the caller's entities are as they were.
            foreach (var (entity, id, createdAt, updatedAt, timestamp, etag) in before)
            {
                entity.Id = id;
                entity.CreatedAt = createdAt;
                entity.UpdatedAt = updatedAt;
                entity.Timestamp = timestamp;
                entity.ETag = etag;
            }
            throw;
        }
        finally
        {
            // Committed or not (a failure after the request was sent may have committed): nothing of
            // the partition is answered from the cache.
            InvalidateQueryCache(partition);
            foreach (var id in ids)
                cache.Remove(EntityCacheKey(id));
        }

        logger.LogInformation("Atomic batch-{Action} {Count} {Type} entities in partition {Partition}",
            actionType, ids.Count, typeName, partition);
    }

    // A type without large values: one row an entity, sized as the batch writes size it.
    private async Task WriteAtomicRowsAsync(
        List<T> entities, List<(string PartitionKey, string RowKey)> keys, TableTransactionActionType actionType, CancellationToken ct)
    {
        var rows = new List<TableEntity>(entities.Count);
        for (var i = 0; i < entities.Count; i++)
            rows.Add(TableEntitySerializer.ToPlainTableEntity(entities[i], keys[i].PartitionKey, keys[i].RowKey, typeof(T)));
        AtomicBatches.RequireOneTransaction([.. rows.Select(TableRowSize.Estimate)], "batch");

        var actions = rows.Select(row => new TableTransactionAction(actionType, row, ETag.All));
        var sent = RequestAttempts.MarkCurrent();
        try
        {
            await client.SubmitTransactionAsync(actions, ct);
        }
        catch (TableTransactionFailedException ex)
            when (actionType == TableTransactionActionType.Add && (ex.Status == 409 || ex.ErrorCode == "InvalidDuplicateRow"))
        {
            var duplicateId = ex.FailedTransactionActionIndex is int i && i >= 0 && i < entities.Count
                ? entities[i].Id
                : keys[0].PartitionKey + Separator + "?";
            throw Duplicate(duplicateId, ex, sent);
        }
    }

    // A type with large values: each entity's rows (primary, continuation rows, stale-part deletes) are
    // one unit, and every unit must fit the one transaction (SubmitBatchUnitsAsync refuses otherwise,
    // also when a lost race makes it plan again). Nothing is uploaded: a plan that needs a blob is refused.
    private async Task WriteAtomicLargeAsync(List<T> entities, TableTransactionActionType actionType, CancellationToken ct)
    {
        var kind = actionType == TableTransactionActionType.Add ? LargeWriteKind.Create : LargeWriteKind.Upsert;
        var enlisted = new List<T>(entities.Count);
        try
        {
            foreach (var entity in entities)
            {
                enlisted.Add(entity);
                await large.PrepareAsync(entity, ct);
            }
            var released = new List<ReleasedBlobs>();
            var units = await PlanBatchUnitsAsync(entities, kind, takeBack: null, ct);
            await SubmitBatchUnitsAsync(units, kind, attempt: 1, takeBack: null, released, ct, atomic: true);

            // Committed: the blobs only the replaced rows referenced can go (best effort, as after a batch).
            foreach (var row in released)
                await DeleteBlobsAsync(row.Pk, row.Rk, row.Paths, row.CommittedAt, ct);
        }
        finally
        {
            foreach (var entity in enlisted)
                LargeValueTable.ReleasePrepared(entity);
        }
    }

    // The all-or-nothing rule of a large-value batch, applied to every plan of it before its transaction
    // is sent (the first plan, and each one made again after a lost race, none of which wrote anything).
    private void RequireOneLargeTransaction(List<BatchUnit> units, IReadOnlyList<BatchRange> transactions)
    {
        foreach (var unit in units)
        {
            if (unit.Plan.BlobPaths.Count > 0)
                throw new BatchNotAtomicException(
                    $"The batch cannot be written as one transaction: a value of {typeName} '{unit.Entity.Id}' would be stored in Blob storage, " +
                    "which is written outside the table transaction. Nothing was written. Write such entities one by one, or with CreateBatchAsync/UpsertBatchAsync.");
        }
        if (transactions.Count != 1)
            throw new BatchNotAtomicException(
                $"The batch cannot be written as one transaction: with their continuation rows the {units.Count} entities need {transactions.Count} transactions " +
                $"(one holds at most {BatchPlanner.DefaultMaxCount} rows and the payload budget of {BatchPlanner.DefaultMaxBytes:N0} bytes). Nothing was written.");
    }
}

/// <summary>
/// The checks every all-or-nothing write makes before it sends anything: what Azure Tables accepts as
/// one entity-group transaction.
/// </summary>
internal static class AtomicBatches
{
    /// <summary>
    /// Refuses more rows than one transaction holds, rows of more than one partition and a row named twice.
    /// </summary>
    /// <param name="keys">The key of each row, in order.</param>
    /// <param name="what">What is being written, for the message (<c>"batch"</c>, <c>"transaction"</c>).</param>
    /// <param name="row">What one entry is called, for the message (<c>"row"</c>, <c>"key"</c>).</param>
    /// <exception cref="BatchNotAtomicException">The rows cannot be one transaction.</exception>
    internal static void RequireOnePartitionEachRowOnce(IReadOnlyList<(string PartitionKey, string RowKey)> keys, string what, string row)
    {
        if (keys.Count > BatchPlanner.DefaultMaxCount)
            throw new BatchNotAtomicException(
                $"The {what} cannot be written as one transaction: it holds {keys.Count} {row}s and one transaction holds at most {BatchPlanner.DefaultMaxCount}. Nothing was written.");
        var seen = new HashSet<(string, string)>();
        foreach (var key in keys)
        {
            if (!string.Equals(key.PartitionKey, keys[0].PartitionKey, StringComparison.Ordinal))
                throw new BatchNotAtomicException(
                    $"The {what} cannot be written as one transaction: it spans more than one partition ('{keys[0].PartitionKey}' and '{key.PartitionKey}'). Nothing was written.");
            if (!seen.Add(key))
                throw new BatchNotAtomicException(
                    $"The {what} cannot be written as one transaction: it names the {row} '{key.PartitionKey}{EntityId.Separator}{key.RowKey}' twice. Nothing was written.");
        }
    }

    /// <summary>Refuses rows whose summed size exceeds the payload budget of one transaction (<see cref="BatchPlanner"/>).</summary>
    /// <param name="sizes">The size of each row, as the batch writes estimate it.</param>
    /// <param name="what">What is being written, for the message.</param>
    /// <exception cref="BatchNotAtomicException">The rows do not fit one transaction.</exception>
    internal static void RequireOneTransaction(IReadOnlyList<long> sizes, string what)
    {
        var bytes = sizes.Sum();
        if (bytes > BatchPlanner.DefaultMaxBytes)
            throw new BatchNotAtomicException(
                $"The {what} cannot be written as one transaction: its rows hold {bytes:N0} bytes and one transaction is planned for at most {BatchPlanner.DefaultMaxBytes:N0}. Nothing was written.");
    }
}
