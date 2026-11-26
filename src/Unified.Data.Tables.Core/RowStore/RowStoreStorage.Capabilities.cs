using System.Diagnostics.CodeAnalysis;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Providers;

// The side interfaces of the store (0.10.0), as TableStorage<T> implements them: the capability probe,
// conditional delete, all-or-nothing batches and what the store states about itself. None of them is a
// member of IStorage<T>; callers reach them through the extension methods of StorageCapabilityExtensions.
internal sealed partial class RowStoreStorage<T> : IStorageCapabilities, IConditionalDelete<T>, IAtomicBatch<T>, IStorageInfo
{
    // What the store states about itself, fixed when it is constructed (see RowStoreInfo).
    private readonly StorageInfo info;

#pragma warning disable UDT0001 // the experimental raw-table API is handed out here, not used
    // The raw view of this store's table and the tables of its account, built when first asked for.
    private RowStoreRawTable? rawTable;
    private RowStoreTableStore? tableStore;

    /// <summary>
    /// The store's implementation of the side interface <typeparamref name="TCap"/>: the store itself for
    /// <see cref="IConditionalDelete{T}"/>, <see cref="IAtomicBatch{T}"/>, <see cref="IStorageInfo"/> and
    /// <see cref="IOrphanBlobSweeper"/>; the untyped view of its own table for the experimental
    /// <see cref="IRawTable"/> (it shares the store's lazy table creation, and bypasses its read cache);
    /// the tables of its account for the experimental <see cref="ITableStore"/>.
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
    public Task<DeleteStatus> DeleteIfExistsAsync(string id, CancellationToken ct = default)
    {
        cache.BeginWrite();
        return Written(DeleteCoreAsync(id, ifMatch: null, ct), id);
    }

    /// <inheritdoc />
    public Task<DeleteStatus> DeleteIfMatchAsync(string id, string etag, CancellationToken ct = default)
    {
        ETagCondition.Require(etag, nameof(etag));
        cache.BeginWrite();
        return Written(DeleteCoreAsync(id, etag, ct), id);
    }

    /// <inheritdoc />
    public Task CreateAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
    {
        cache.BeginWrite();
        return Written(WriteAtomicCoreAsync(entities, upsert: false, ct), id: null);
    }

    /// <inheritdoc />
    public Task UpsertAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
    {
        cache.BeginWrite();
        return Written(WriteAtomicCoreAsync(entities, upsert: true, ct), id: null);
    }

    // One commit of the row store, planned as TableStorage<T> plans the one entity-group transaction:
    // one partition, at most 100 rows, each row once, the payload budget of BatchPlanner. For a type with
    // large values the continuation rows of an entity are rows of the transaction, and a value that would
    // be stored in the blob store is refused: a blob is written outside the transaction. Like the other
    // batch writes it bypasses [ProtectedProperty] enforcement and leaves each entity's ETag null.
    private async Task<bool> WriteAtomicCoreAsync(IReadOnlyCollection<T> entities, bool upsert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return false;
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
            var key = EntityId.Split(id);
            if (LargeValueAware)
                LargeValueTable.ValidateKeys(key.PartitionKey, key.RowKey);
            ids.Add(id);
            keys.Add(key);
        }
        AtomicBatches.RequireOnePartitionEachRowOnce(keys, "batch", "row");

        await EnsureTableAsync(ct);

        var before = entities.Select(e => (Entity: e, e.Id, e.CreatedAt, e.UpdatedAt, e.Timestamp, e.ETag)).ToList();
        var now = clock.GetUtcNow();
        var index = 0;
        foreach (var entity in entities)
        {
            if (!upsert || entity.CreatedAt == default)
                entity.CreatedAt = now;
            entity.UpdatedAt = now;
            entity.Timestamp = null;
            entity.ETag = null; // as after any batch: the row's version is read before an optimistic update
            entity.Id = ids[index++];
        }

        var atomic = new AtomicWrite();
        try
        {
            if (LargeValueAware)
                await WriteAtomicLargeAsync([.. entities], upsert, atomic, ct);
            else
                await WriteAtomicRowsAsync([.. entities], keys, upsert, atomic, ct);
        }
        catch (Exception) when (!atomic.Sent)
        {
            // Refused or failed before anything was handed to the row store (a batch that is not one
            // transaction, an entity the serializer or the service's rules refuse): nothing was written,
            // and the caller's entities are as they were.
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
        return true;
    }

    // A type without large values: one row an entity, sized as the batch writes size it.
    private async Task WriteAtomicRowsAsync(
        List<T> entities, List<(string PartitionKey, string RowKey)> keys, bool upsert, AtomicWrite atomic, CancellationToken ct)
    {
        var serialized = new List<TableEntity>(entities.Count);
        for (var i = 0; i < entities.Count; i++)
            serialized.Add(Serialize(keys[i], entities[i]));
        AtomicBatches.RequireOneTransaction([.. serialized.Select(TableRowSize.Estimate)], "batch");

        var writes = new List<RowWrite>(entities.Count);
        for (var i = 0; i < entities.Count; i++)
        {
            if ((ServiceErrors.RefusedKey(keys[i].PartitionKey, keys[i].RowKey) ?? ServiceErrors.RefusedRow(serialized[i])) is { } refused)
                throw ServiceErrors.Neutral(ServiceErrors.InTransaction(refused), typeName, entities[i].Id);
            var row = RowCells.Copy(serialized[i]);
            writes.Add(upsert ? RowWrite.Upsert(row) : RowWrite.Insert(row));
        }

        atomic.Sent = true;
        var result = await rows.CommitAsync(keys[0].PartitionKey, writes, ct);
        if (!result.Committed)
            throw StoredKey(entities[result.FailedIndex].Id);
    }

    // A type with large values: each entity's rows (primary, continuation rows, stale-part deletes) are
    // one unit, and every unit must fit the one transaction (CommitBatchUnitsAsync refuses otherwise,
    // also when a lost race makes it plan again). Nothing is uploaded: a plan that needs a blob is refused.
    private async Task WriteAtomicLargeAsync(List<T> entities, bool upsert, AtomicWrite atomic, CancellationToken ct)
    {
        var kind = upsert ? LargeWriteKind.Upsert : LargeWriteKind.Create;
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
            await CommitBatchUnitsAsync(units, kind, attempt: 1, takeBack: null, released, atomic, ct);

            // Committed: the blobs only the replaced rows referenced can go (best effort, as after a batch).
            foreach (var row in released)
                await large.DeleteReleasedBlobsAsync(row.Keys.PartitionKey, row.Keys.RowKey, row.Paths, row.CommittedAt,
                    onForeign: null, onFailure: null, ct);
        }
        finally
        {
            foreach (var entity in enlisted)
                LargeValueTable.ReleasePrepared(entity);
        }
    }

    // The all-or-nothing rule of a large-value batch, applied to every plan of it before its transaction
    // is committed, with the messages of TableStorage<T>.
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
