using System.Runtime.CompilerServices;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables;

// Large values (TableValueSplit): writes put a split value's primary row and its continuation rows
// ({RowKey}~0001...) in ONE entity-group transaction, and reads hide continuation rows and reassemble
// the value before deserializing. Every branch here is reached only for a split-aware T (one with a
// [TableValueSplit] member), except the IsPartRow skip on reads, which is a no-op for any other type.
public partial class TableStorage<T>
{
    /// <summary>True when <typeparamref name="T"/> has a member that may be stored as split cells.</summary>
    private static readonly bool LargeValueAware = TypeMetadataCache.IsLargeValueAware(typeof(T));

    /// <summary>How many times a write that lost its condition to a concurrent writer re-reads and retries.</summary>
    private const int MaxLargeWriteAttempts = 3;

    // _SplitParts says how many continuation rows the stored row owns, _SplitOf marks the row itself as
    // one; the __blob reference columns are selected now so the offload tier can read them from the same
    // point read.
    private static readonly string[] PriorSelect =
    [
        SystemColumnNames.SplitParts,
        SystemColumnNames.SplitOf,
        "odata.etag",
        .. TypeMetadataCache.LargeValueColumns(typeof(T)).Select(c => c + SplitCodec.BlobSuffix),
    ];

    private static readonly string[] KeysOnly = ["PartitionKey", "RowKey"];

    private static readonly string[] PartKeys = ["PartitionKey", "RowKey", SystemColumnNames.SplitOf];

    /// <summary>
    /// The version a split-aware write produced, whether the written entity may be cached under it, and
    /// the service time of the commit (its response <c>Date</c>; null when unknown). A transaction's
    /// version comes from a separate re-read that may already see another writer's row, so only a single
    /// call's own response version is cacheable. The cleanup of released blobs is timed by the commit,
    /// not by that later re-read or the client clock.
    /// </summary>
    private readonly record struct LargeWriteResult(ETag ETag, bool Cacheable, DateTimeOffset? CommittedAt);

    /// <summary>
    /// What a write or delete needs to know about the stored row: its version, its continuation rows and
    /// the blobs it references.
    /// </summary>
    private sealed record PriorRow(ETag ETag, int Parts, IReadOnlyList<string> Blobs);

    private enum LargeWriteKind
    {
        Create,
        Upsert,
        Replace,
    }

    /// <summary>One point read of the stored row's ETag and part count; null when the row does not exist.</summary>
    private async Task<PriorRow?> ReadPriorAsync(string pk, string rk, CancellationToken ct)
    {
        var response = await client.GetEntityIfExistsAsync<TableEntity>(pk, rk, PriorSelect, ct);
        // A continuation row is not an entity: addressing one by id finds nothing.
        if (!response.HasValue || response.Value is null || SplitRowLayout.IsPartRow(response.Value))
            return null;
        return new PriorRow(response.Value.ETag, SplitRowLayout.PartCount(response.Value), BlobPaths(response.Value));
    }

    // The blob names a stored row references; a malformed reference is logged and skipped.
    private IReadOnlyList<string> BlobPaths(TableEntity row) =>
        LargeValueTable.BlobPaths(row, (column, ex) =>
            logger.LogWarning(ex, "{Type} {PartitionKey}|{RowKey}: ignoring a malformed blob reference in {Column}",
                typeName, row.PartitionKey, row.RowKey, column));

    /// <summary>
    /// Writes a split-aware entity: one SDK call when the value fits one row and no continuation rows
    /// must go, otherwise one transaction holding the primary, its parts and the deletes of stale parts.
    /// The blobs the row references are uploaded (create-only) before the entity write; once the write
    /// succeeded, the blobs the replaced row referenced and the new one does not are deleted (best
    /// effort). A failed write deletes no blob. Returns the new ETag (<c>default</c> when it could not be
    /// read back) and whether it is the write's own response version.
    /// </summary>
    private async Task<LargeWriteResult> WriteLargeAsync(T entity, LargeWriteKind kind, ETag ifMatch, CancellationToken ct)
    {
        var (pk, rk) = GetEntityKeys(entity.Id);
        SplitRowLayout.ValidateRowKey(rk);

        try
        {
            // Hash every TableBlobContent once (a non-seekable one is copied to a temporary file).
            await large.PrepareAsync(entity, ct);
            return await WritePreparedAsync(entity, kind, ifMatch, pk, rk, ct);
        }
        finally
        {
            // A content the write did not get to bind to its blob gives its temporary copy back now.
            LargeValueTable.ReleasePrepared(entity);
        }
    }

    private async Task<LargeWriteResult> WritePreparedAsync(T entity, LargeWriteKind kind, ETag ifMatch, string pk, string rk, CancellationToken ct)
    {
        // A Replace with a caller ETag is strict: a different stored version is a conflict, never a retry.
        // A Replace without one (last-writer-wins) and an Upsert retry on a lost race instead.
        var strict = kind == LargeWriteKind.Replace && ifMatch != ETag.All;
        RequestFailedException? lost = null;

        for (var attempt = 1; attempt <= MaxLargeWriteAttempts; attempt++)
        {
            var prior = kind == LargeWriteKind.Create ? null : await ReadPriorAsync(pk, rk, ct);
            if (kind == LargeWriteKind.Replace)
            {
                if (prior is null)
                    throw new RequestFailedException(404, $"The specified resource does not exist. Id: {entity.Id}");
                if (strict && prior.ETag != ifMatch)
                    throw new ConcurrencyConflictException(typeName, entity.Id, null);
            }

            var plan = await large.PlanAsync(entity, pk, rk, prior?.Parts ?? 0, c => ExistingPartKeysAsync(pk, rk, c), ct);
            // Every blob exists (uploaded, or touched when already there) before the row that references it.
            await large.UploadAsync(plan, ct);
            // null = insert (no stored row); otherwise replace conditional on the version just read.
            ETag? condition = prior is null ? null : strict ? ifMatch : prior.ETag;

            LargeWriteResult result;
            try
            {
                result = await SubmitPlanAsync(plan, condition, pk, rk, ct);
            }
            catch (RequestFailedException ex) when (LostCondition(ex, kind))
            {
                if (kind == LargeWriteKind.Create)
                    throw new DuplicateKeyException(typeName, entity.Id, ex);
                // A strict replace may not retry its own condition; a vanished row surfaces as the service's
                // own 404. A stale part removed meanwhile (404 past action 0) is re-planned by any write.
                if (strict && IsPrimaryFailure(ex))
                    throw ex.Status == 404 ? ex : new ConcurrencyConflictException(typeName, entity.Id, ex);
                lost = ex;
                continue;
            }

            // The swap committed: the blobs only the replaced row referenced can go (the same content
            // written again maps to the same name, which the new row still references). The caller's
            // token no longer applies: a committed write is never reported as failed, nor left uncleaned.
            if (prior is { Blobs.Count: > 0 })
                await DeleteBlobsAsync(prior.Blobs.Except(plan.BlobPaths, StringComparer.Ordinal), result.CommittedAt, CancellationToken.None);
            return result;
        }

        throw new ConcurrencyConflictException(typeName, entity.Id, lost);
    }

    /// <summary>The row keys of the continuation rows of <paramref name="rk"/> that exist now (one range query).</summary>
    private async Task<IReadOnlyList<string>> ExistingPartKeysAsync(string pk, string rk, CancellationToken ct) =>
        (await QueryPartsAsync(pk, rk, PartKeys, ct)).Select(p => p.RowKey).ToList();

    private async Task<LargeWriteResult> SubmitPlanAsync(
        LargeValueWritePlan plan, ETag? condition, string pk, string rk, CancellationToken ct)
    {
        var stale = plan.StalePartRowKeys;
        if (plan.Parts.Count == 0 && stale.Count == 0)
        {
            var response = condition is ETag c
                ? await client.UpdateEntityAsync(plan.Primary, c, TableUpdateMode.Replace, ct)
                : await client.AddEntityAsync(plan.Primary, ct);
            return new LargeWriteResult(HeaderETag(response) ?? plan.Primary.ETag, Cacheable: true, HeaderDate(response));
        }

        var actions = new List<TableTransactionAction>(1 + plan.Parts.Count + stale.Count)
        {
            condition is ETag c2
                ? new TableTransactionAction(TableTransactionActionType.UpdateReplace, plan.Primary, c2)
                : new TableTransactionAction(TableTransactionActionType.Add, plan.Primary),
        };
        foreach (var part in plan.Parts)
            actions.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, part));
        foreach (var partKey in stale)
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(pk, partKey), ETag.All));

        var committed = await client.SubmitTransactionAsync(actions, ct);
        // Taken from the commit itself, before the (slower) re-read below.
        var committedAt = HeaderDate(RawResponse(committed));
        // Transaction sub-responses are not correlated back to their actions: re-read the version. The
        // write has committed, so the caller's token no longer applies.
        return new LargeWriteResult(await ReadETagAfterCommitAsync(pk, rk), Cacheable: false, committedAt);
    }

    /// <summary>
    /// Deletes blobs no row references any more, after the write or delete that released them has
    /// committed, each on condition that it was not modified within the cleanup grace (a concurrent
    /// writer that reuses the same content touches the blob before committing its row; the orphan sweep
    /// collects a blob that really is unreferenced). Best effort: a failure is logged, never thrown.
    /// </summary>
    private Task DeleteBlobsAsync(IEnumerable<string> paths, DateTimeOffset? committedAt, CancellationToken ct) =>
        large.DeleteReleasedBlobsAsync(paths, committedAt, LogBlobDeleteFailure, ct);

    private void LogBlobDeleteFailure(string path, Exception ex) =>
        logger.LogWarning(ex, "{Type}: could not delete blob {Blob}; an orphan sweep will remove it", typeName, path);

    /// <summary>
    /// After a partition's rows are deleted, deletes every blob stored under it (best effort), each on
    /// condition that it was modified neither within the cleanup grace nor since it was listed, so a blob
    /// a concurrent write has just touched for a new row survives. Once rows were deleted, the caller
    /// passes <see cref="CancellationToken.None"/>: the deletion committed, so cleanup runs regardless.
    /// </summary>
    private Task DeletePartitionBlobsAsync(string partition, CancellationToken ct)
    {
        if (!LargeValueAware)
            return Task.CompletedTask;
        return large.DeletePartitionBlobsAsync(partition,
            ex => logger.LogWarning(ex, "{Type}: could not list the blobs of partition {Partition}; an orphan sweep will remove them", typeName, partition),
            LogBlobDeleteFailure, ct);
    }

    /// <inheritdoc />
    public Task<int> SweepOrphanBlobsAsync(string partitionKey, TimeSpan olderThan, CancellationToken ct = default)
    {
        LargeValueTable.ValidateSweep(partitionKey, olderThan);
        var partition = NormalizeId(partitionKey); // match stored (normalized) PartitionKeys, as DeletePartitionAsync
        return large.SweepOrphanBlobsAsync(partition, olderThan, typeof(T),
            (select, c) => PartitionRowsAsync(partition, select, c), ct);
    }

    /// <inheritdoc />
    public Task<int> SweepOrphanBlobsAsync(TimeSpan olderThan, CancellationToken ct = default) =>
        // The listed partitions are stored keys already: they are not normalized again.
        large.SweepTableOrphanBlobsAsync(olderThan, typeof(T), PartitionRowsAsync, ct);

    // Every stored row of the partition (continuation rows included), in one query projected onto select.
    private async IAsyncEnumerable<TableEntity> PartitionRowsAsync(
        string partition, IReadOnlyList<string>? select, [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partition}");
        await foreach (var row in client.QueryAsync<TableEntity>(filter, maxPerPage: null, select, ct).WithCancellation(ct))
            yield return row;
    }

    /// <summary>
    /// The entity a stored (assembled) row holds: its offloaded values downloaded and verified, its
    /// <see cref="TableBlobContent"/> values lazy. A plain <typeparamref name="T"/> deserializes as before.
    /// </summary>
    private async Task<T> ToEntityAsync(TableEntity row, CancellationToken ct)
    {
        if (!LargeValueAware)
            return row.FromTableEntity<T>();
        return await large.ReadAsync<T>(row, ct);
    }

    /// <summary>True when the row references a blob (a value that cannot be deserialized from the row alone).</summary>
    private static bool HasBlobReference(TableEntity row)
    {
        foreach (var key in row.Keys)
        {
            if (SplitCodec.TryParseLargeValueColumn(key, out _, out var kind, out _) && kind == LargeValueCellKind.BlobReference)
                return true;
        }
        return false;
    }

    // The failures a concurrent writer causes: the insert found a row (409), the conditional replace
    // found another version (412), or a row the write expected (the primary or a stale part) is gone (404);
    // a retry re-reads the primary and re-queries the parts, so a part that is gone is not required again.
    private static bool LostCondition(RequestFailedException ex, LargeWriteKind kind) => kind switch
    {
        LargeWriteKind.Create => ex.Status == 409 && IsPrimaryFailure(ex),
        _ => ex.Status == 404 || (IsPrimaryFailure(ex) && ex.Status is 409 or 412),
    };

    // A transaction fails at one action. Only the primary (action 0) is an insert or conditional
    // replace, so a 409/412 is the primary's; a single SDK call is always the primary.
    private static bool IsPrimaryFailure(RequestFailedException ex) =>
        ex is not TableTransactionFailedException tx || tx.FailedTransactionActionIndex is null or 0;

    /// <summary>
    /// The version of a row a transaction has just committed, read without the caller's token; any
    /// failure of the read (a transient error, a timeout) yields <c>default</c>, an unknown version,
    /// which the write already reports as a null ETag and never caches.
    /// </summary>
    private async Task<ETag> ReadETagAfterCommitAsync(string pk, string rk)
    {
        try
        {
            var response = await client.GetEntityIfExistsAsync<TableEntity>(pk, rk, KeysOnly, CancellationToken.None);
            return response is { HasValue: true, Value: not null } ? response.Value.ETag : default;
        }
#pragma warning disable CA1031 // the write has committed: failing to read its version must not report it as failed
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogWarning(ex, "{Type} {PartitionKey}|{RowKey}: the write committed but its version could not be read back",
                typeName, pk, rk);
            return default;
        }
    }

    // The service time of a response (its Date header), tolerating test doubles that have none.
    private static DateTimeOffset? HeaderDate(Response? response)
    {
        try
        {
            return response?.Headers.Date;
        }
        catch
        {
            return null;
        }
    }

    private static Response? RawResponse(Response<IReadOnlyList<Response>>? response)
    {
        try
        {
            return response?.GetRawResponse();
        }
        catch
        {
            return null;
        }
    }

    // Same header-first, tolerate-test-doubles read the single-row Create and Upsert paths use.
    private static ETag? HeaderETag(Response response)
    {
        try
        {
            return response?.Headers.ETag;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The continuation rows of <paramref name="rk"/> that exist now (one range query).</summary>
    private async Task<List<TableEntity>> QueryPartsAsync(string pk, string rk, IEnumerable<string>? select, CancellationToken ct)
    {
        var lower = SplitRowLayout.PartRangeLower(rk);
        var upper = SplitRowLayout.PartRangeUpper(rk);
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {pk} and RowKey ge {lower} and RowKey lt {upper} and _SplitOf eq {rk}");

        var parts = new List<TableEntity>();
        await foreach (var part in client.QueryAsync<TableEntity>(filter, maxPerPage: null, select, ct).WithCancellation(ct))
        {
            // Guard against a row in the range that belongs elsewhere (the filter already demands _SplitOf == rk).
            if (part.TryGetValue(SystemColumnNames.SplitOf, out var of) && string.Equals(of as string, rk, StringComparison.Ordinal))
                parts.Add(part);
        }
        return parts;
    }

    /// <summary>For a split-aware <typeparamref name="T"/>, refuses a row key reserved for continuation rows, before any SDK call.</summary>
    private void RefuseReservedRowKey(string id)
    {
        if (LargeValueAware)
            SplitRowLayout.ValidateRowKey(GetEntityKeys(NormalizeId(id)).RowKey);
    }

    /// <summary>
    /// Stamps the outcome of a split-aware write on <paramref name="entity"/> and keeps the caches
    /// coherent. Only a single call's own response version is cached; a transaction's re-read version
    /// (which may already be another writer's) and an unknown version evict the entry instead, so the
    /// next read goes to the store.
    /// </summary>
    private void CommitLargeWrite(T entity, string partitionKey, LargeWriteResult result)
    {
        entity.ETag = result.ETag == default ? null : result.ETag.ToString();
        if (result.Cacheable && result.ETag != default)
            CacheEntity(entity.Id, entity, result.ETag);
        else
            cache.Remove(EntityCacheKey(entity.Id));
        InvalidateQueryCache(partitionKey);
    }

    /// <summary>
    /// The primary merged with its continuation rows (one range query), or <paramref name="primary"/>
    /// itself when it has none. Missing parts throw <see cref="TableValueIntegrityException"/>.
    /// </summary>
    private async Task<TableEntity> AssembleAsync(TableEntity primary, CancellationToken ct)
    {
        if (SplitRowLayout.PartCount(primary) == 0)
            return primary;

        var parts = await QueryPartsAsync(primary.PartitionKey, primary.RowKey, select: null, ct);
        return SplitRowLayout.Assemble(primary, parts);
    }

    /// <summary>How many times a read whose parts or blobs belonged to another version of the row is attempted in all.</summary>
    private const int MaxLargeReadAttempts = 3;

    /// <summary>
    /// The logical row and entity <paramref name="primary"/> holds: continuation rows reassembled,
    /// offloaded values downloaded and verified. The primary is read before its parts and blobs, so a
    /// writer committing in between can make them belong to another version and fail verification. On
    /// such a failure the primary is read again: an unchanged version is a genuine integrity failure and
    /// throws; a changed one retries the whole read from the new version (at most
    /// <see cref="MaxLargeReadAttempts"/> attempts); a row deleted meanwhile returns null. Data of two
    /// versions is never combined.
    /// </summary>
    private async Task<(TableEntity Row, T Entity)?> ReadLargeAsync(TableEntity primary, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var row = await AssembleAsync(primary, ct);
                return (row, await ToEntityAsync(row, ct));
            }
            catch (TableValueIntegrityException) when (attempt < MaxLargeReadAttempts)
            {
                var current = await client.GetEntityIfExistsAsync<TableEntity>(primary.PartitionKey, primary.RowKey, cancellationToken: ct);
                if (!current.HasValue || current.Value is null || SplitRowLayout.IsPartRow(current.Value))
                    return null;
                if (current.Value.ETag == primary.ETag)
                    throw;
                logger.LogDebug("{Type} {PartitionKey}|{RowKey}: the row changed while it was read; reading it again",
                    typeName, primary.PartitionKey, primary.RowKey);
                primary = current.Value;
            }
        }
    }

    /// <summary>
    /// Deletes a split-aware entity and its continuation rows: one conditional delete when it has none,
    /// otherwise one transaction over the parts that exist (found by a range query, so a primary that
    /// declares a part already gone can still be deleted). A missing row is a no-op; a lost race
    /// re-reads and retries. Any row key is accepted. Once the entity is deleted, the blobs it referenced are deleted (best effort).
    /// </summary>
    private async Task DeleteLargeAsync(string pk, string rk, CancellationToken ct)
    {
        // No row-key check: a row written before the type opted in may carry any key and stays
        // deletable; a continuation row addressed by id reads as missing below, so it is never deleted.
        RequestFailedException? lost = null;
        for (var attempt = 1; attempt <= MaxLargeWriteAttempts; attempt++)
        {
            var prior = await ReadPriorAsync(pk, rk, ct);
            if (prior is null)
                return;

            DateTimeOffset? committedAt;
            try
            {
                var parts = prior.Parts == 0 ? [] : await QueryPartsAsync(pk, rk, PartKeys, ct);
                if (parts.Count == 0)
                {
                    committedAt = HeaderDate(await client.DeleteEntityAsync(pk, rk, prior.ETag, ct));
                }
                else
                {
                    var actions = new List<TableTransactionAction>(1 + parts.Count)
                    {
                        new(TableTransactionActionType.Delete, new TableEntity(pk, rk), prior.ETag),
                    };
                    foreach (var part in parts)
                        actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(pk, part.RowKey), ETag.All));
                    committedAt = HeaderDate(RawResponse(await client.SubmitTransactionAsync(actions, ct)));
                }
            }
            catch (RequestFailedException ex) when (ex.Status is 412 or 404)
            {
                lost = ex;
                continue;
            }

            // The entity is gone: the blobs it referenced follow (best effort, whatever the caller's token).
            await DeleteBlobsAsync(prior.Blobs, committedAt, CancellationToken.None);
            return;
        }

        throw new ConcurrencyConflictException(typeName, $"{pk}{Separator}{rk}", lost);
    }

    /// <summary>
    /// True when a projection names a large-value column, or an object that owns one: such a value
    /// spans cells and continuation rows, so the whole row is fetched and assembled instead.
    /// </summary>
    private static bool SelectsLargeValue(IReadOnlyCollection<string> requested)
    {
        var columns = TypeMetadataCache.LargeValueColumns(typeof(T));
        foreach (var name in requested)
        {
            foreach (var column in columns)
            {
                if (string.Equals(name, column, StringComparison.Ordinal)
                    || column.StartsWith(name + TableEntityValue.PathDelimiter, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Columns a split-aware projection adds so continuation rows can be recognised and skipped.</summary>
    private static readonly string[] SplitSystemColumns =
        [SystemColumnNames.SplitOf, SystemColumnNames.SplitIndex, SystemColumnNames.SplitParts];

    /// <summary>One entity of a split-aware batch: the stored row it was planned against and its rows.</summary>
    private sealed record BatchUnit(T Entity, string Pk, string Rk, PriorRow? Prior, LargeValueWritePlan Plan);

    /// <summary>
    /// A split-aware batch, with the plain batch's contract: per partition, each entity's rows (primary,
    /// continuation rows, stale-part deletes) are one indivisible unit, and the units are packed in order
    /// into entity-group transactions (<see cref="LargeValueTable.PlanTransactions"/>), each atomic. The
    /// blobs a partition's rows reference are uploaded (create-only) before its first transaction; after
    /// each transaction commits, the blobs only the replaced rows referenced are deleted (best effort). A
    /// key repeated in a create batch throws <see cref="DuplicateKeyException"/> before anything is
    /// written; in an upsert batch its last occurrence is written. An existing key fails its create
    /// transaction (<see cref="DuplicateKeyException"/>); earlier transactions stay committed. An upsert
    /// transaction that lost a race to another writer is planned again from the re-read rows and retried.
    /// </summary>
    private async Task WriteBatchLargeAsync(IReadOnlyCollection<T> entities, TableTransactionActionType actionType, CancellationToken ct)
    {
        var kind = actionType == TableTransactionActionType.Add ? LargeWriteKind.Create : LargeWriteKind.Upsert;
        // Refuse a reserved row key before the first entity is written, not halfway through.
        foreach (var entity in entities)
            SplitRowLayout.ValidateRowKey(GetEntityKeys(entity.Id).RowKey);
        var writes = DistinctBatchKeys(entities, kind);

        try
        {
            // Hash every TableBlobContent once (a non-seekable one is copied to a temporary file).
            foreach (var entity in writes)
                await large.PrepareAsync(entity, ct);
            foreach (var group in writes.GroupBy(e => GetEntityKeys(e.Id).PartitionKey, StringComparer.Ordinal))
            {
                var units = await PlanBatchUnitsAsync([.. group], kind, ct);
                // Every blob exists (uploaded, or touched when already there) before the rows that reference it.
                foreach (var unit in units)
                    await large.UploadAsync(unit.Plan, ct);
                await SubmitBatchUnitsAsync(units, kind, attempt: 1, ct);
            }
        }
        finally
        {
            foreach (var entity in writes)
                LargeValueTable.ReleasePrepared(entity);
        }
    }

    // A create batch refuses a repeated key up front (the same DuplicateKeyException the in-memory store
    // throws); an upsert batch writes each key once, with its last occurrence (last writer wins).
    private List<T> DistinctBatchKeys(IReadOnlyCollection<T> entities, LargeWriteKind kind)
    {
        if (kind == LargeWriteKind.Create)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entity in entities)
            {
                if (!seen.Add(entity.Id))
                    throw new DuplicateKeyException(typeName, entity.Id,
                        new RequestFailedException(400, $"InvalidDuplicateRow: duplicate key within the batch. Id: {entity.Id}"));
            }
            return [.. entities];
        }

        var list = entities.ToList();
        var last = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
            last[list[i].Id] = i;
        return list.Where((e, i) => last[e.Id] == i).ToList();
    }

    private async Task<List<BatchUnit>> PlanBatchUnitsAsync(List<T> entities, LargeWriteKind kind, CancellationToken ct)
    {
        var units = new List<BatchUnit>(entities.Count);
        foreach (var entity in entities)
        {
            var (pk, rk) = GetEntityKeys(entity.Id);
            var prior = kind == LargeWriteKind.Create ? null : await ReadPriorAsync(pk, rk, ct);
            var plan = await large.PlanAsync(entity, pk, rk, prior?.Parts ?? 0, c => ExistingPartKeysAsync(pk, rk, c), ct);
            units.Add(new BatchUnit(entity, pk, rk, prior, plan));
        }
        return units;
    }

    private async Task SubmitBatchUnitsAsync(List<BatchUnit> units, LargeWriteKind kind, int attempt, CancellationToken ct)
    {
        foreach (var range in LargeValueTable.PlanTransactions(units.Select(u => u.Plan).ToList()))
        {
            var chunk = units.GetRange(range.Start, range.Count);
            var (actions, owners) = BatchActions(chunk);
            DateTimeOffset? committedAt;
            try
            {
                committedAt = HeaderDate(RawResponse(await client.SubmitTransactionAsync(actions, ct)));
            }
            catch (RequestFailedException ex) when (kind == LargeWriteKind.Create && ex.Status == 409 && IsBatchPrimaryFailure(ex, owners))
            {
                throw new DuplicateKeyException(typeName, FailedId(ex, owners, chunk), ex);
            }
            catch (RequestFailedException ex) when (kind == LargeWriteKind.Upsert
                && (ex.Status == 404 || (ex.Status is 409 or 412 && IsBatchPrimaryFailure(ex, owners))))
            {
                // The whole transaction failed: nothing of it committed, so it is planned again from the
                // rows as they are now. A stale part removed meanwhile (404) is not required again.
                if (attempt >= MaxLargeWriteAttempts)
                    throw new ConcurrencyConflictException(typeName, FailedId(ex, owners, chunk), ex);
                var replanned = await PlanBatchUnitsAsync(chunk.ConvertAll(u => u.Entity), kind, ct);
                foreach (var unit in replanned)
                    await large.UploadAsync(unit.Plan, ct);
                await SubmitBatchUnitsAsync(replanned, kind, attempt + 1, ct);
                continue;
            }

            // The transaction committed: the blobs only the replaced rows referenced can go, whatever
            // happens to the caller's token now (a committed write is never reported as failed).
            foreach (var unit in chunk)
            {
                if (unit.Prior is { Blobs.Count: > 0 })
                    await DeleteBlobsAsync(unit.Prior.Blobs.Except(unit.Plan.BlobPaths, StringComparer.Ordinal), committedAt, CancellationToken.None);
            }
        }
    }

    // The actions of one transaction and, per action, its unit's index (bitwise-complemented for a
    // primary), so a failed action maps back to its entity.
    private static (List<TableTransactionAction> Actions, List<int> Owners) BatchActions(List<BatchUnit> chunk)
    {
        var actions = new List<TableTransactionAction>();
        var owners = new List<int>();
        for (var i = 0; i < chunk.Count; i++)
        {
            var unit = chunk[i];
            actions.Add(unit.Prior is null
                ? new TableTransactionAction(TableTransactionActionType.Add, unit.Plan.Primary)
                : new TableTransactionAction(TableTransactionActionType.UpdateReplace, unit.Plan.Primary, unit.Prior.ETag));
            owners.Add(~i);
            foreach (var part in unit.Plan.Parts)
            {
                actions.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, part));
                owners.Add(i);
            }
            foreach (var partKey in unit.Plan.StalePartRowKeys)
            {
                actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(unit.Pk, partKey), ETag.All));
                owners.Add(i);
            }
        }
        return (actions, owners);
    }

    // A failure the service attributes to no action, or to a primary (an insert or conditional replace).
    private static bool IsBatchPrimaryFailure(RequestFailedException ex, List<int> owners) =>
        ex is not TableTransactionFailedException { FailedTransactionActionIndex: int i }
        || i < 0 || i >= owners.Count
        || owners[i] < 0;

    // The id of the entity whose action failed ("{partition}|?" when the service did not say).
    private string FailedId(RequestFailedException ex, List<int> owners, List<BatchUnit> chunk)
    {
        if (ex is TableTransactionFailedException { FailedTransactionActionIndex: int i } && i >= 0 && i < owners.Count)
            return chunk[owners[i] < 0 ? ~owners[i] : owners[i]].Entity.Id;
        return chunk[0].Pk + Separator + "?";
    }
}
