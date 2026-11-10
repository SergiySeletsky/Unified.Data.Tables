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
    /// The version a split-aware write produced, and whether the written entity may be cached under it.
    /// A transaction's version comes from a separate re-read that may already see another writer's row,
    /// so only a single call's own response version is cacheable.
    /// </summary>
    private readonly record struct LargeWriteResult(ETag ETag, bool Cacheable);

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

    // The blob names a stored row references. A malformed reference cannot name a blob to clean up, and
    // must not block a write that replaces it, so it is logged and skipped.
    private List<string> BlobPaths(TableEntity row)
    {
        var paths = new List<string>();
        foreach (var (key, value) in row)
        {
            if (!SplitCodec.TryParseLargeValueColumn(key, out var column, out var kind, out _) || kind != LargeValueCellKind.BlobReference)
                continue;
            try
            {
                paths.Add(BlobReference.Parse(column, value as string ?? string.Empty).Path);
            }
            catch (TableValueIntegrityException ex)
            {
                logger.LogWarning(ex, "{Type} {PartitionKey}|{RowKey}: ignoring a malformed blob reference in {Column}",
                    typeName, row.PartitionKey, row.RowKey, column);
            }
        }
        return paths;
    }

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
            await offload.PrepareAsync(entity, ct);
            return await WritePreparedAsync(entity, kind, ifMatch, pk, rk, ct);
        }
        finally
        {
            // A content the write did not get to bind to its blob gives its temporary copy back now.
            ValueOffloadCoordinator.ReleasePrepared(entity);
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

            var (row, plan, blobs, stale) = await PlanWriteAsync(entity, pk, rk, prior, ct);
            // Every blob exists (uploaded, or touched when already there) before the row that references it.
            await offload.UploadAsync(blobs, ct);
            // null = insert (no stored row); otherwise replace conditional on the version just read.
            ETag? condition = prior is null ? null : strict ? ifMatch : prior.ETag;

            LargeWriteResult result;
            try
            {
                result = await SubmitPlanAsync(plan, stale, condition, pk, rk, ct);
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
            // written again maps to the same name, which the new row still references).
            if (prior is { Blobs.Count: > 0 })
                await DeleteBlobsAsync(prior.Blobs.Except(BlobPaths(row), StringComparer.Ordinal), ct);
            return result;
        }

        throw new ConcurrencyConflictException(typeName, entity.Id, lost);
    }

    /// <summary>
    /// Serializes <paramref name="entity"/> onto a primary and its parts, offloading values to blobs
    /// until the rows fit one transaction together with the deletes of the stored row's stale parts.
    /// </summary>
    private async Task<(TableEntity Row, SplitRowPlan Plan, IReadOnlyList<PendingBlob> Blobs, List<TableEntity> Stale)> PlanWriteAsync(
        T entity, string pk, string rk, PriorRow? prior, CancellationToken ct)
    {
        var (row, plan, blobs) = offload.Serialize(entity, pk, rk, extraActions: 0);
        // A shrink deletes the continuation rows that actually exist beyond the new plan, found by the
        // range query rather than trusted from _SplitParts, so a part already gone is never required.
        if (prior is null || prior.Parts <= plan.Parts.Count)
            return (row, plan, blobs, []);

        var existing = await QueryPartsAsync(pk, rk, PartKeys, ct);
        var stale = StaleParts(existing, plan);
        if (!SplitRowLayout.FitsOneTransaction(plan, stale.Count))
        {
            // Budget for deleting every existing part: the stale ones are a subset whatever the new plan.
            (row, plan, blobs) = offload.Serialize(entity, pk, rk, extraActions: existing.Count);
            stale = StaleParts(existing, plan);
        }
        return (row, plan, blobs, stale);
    }

    private async Task<LargeWriteResult> SubmitPlanAsync(
        SplitRowPlan plan, List<TableEntity> stale, ETag? condition, string pk, string rk, CancellationToken ct)
    {
        if (plan.Parts.Count == 0 && stale.Count == 0)
        {
            var response = condition is ETag c
                ? await client.UpdateEntityAsync(plan.Primary, c, TableUpdateMode.Replace, ct)
                : await client.AddEntityAsync(plan.Primary, ct);
            return new LargeWriteResult(HeaderETag(response) ?? plan.Primary.ETag, Cacheable: true);
        }

        var actions = new List<TableTransactionAction>(1 + plan.Parts.Count + stale.Count)
        {
            condition is ETag c2
                ? new TableTransactionAction(TableTransactionActionType.UpdateReplace, plan.Primary, c2)
                : new TableTransactionAction(TableTransactionActionType.Add, plan.Primary),
        };
        foreach (var part in plan.Parts)
            actions.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, part));
        foreach (var part in stale)
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(pk, part.RowKey), ETag.All));

        await client.SubmitTransactionAsync(actions, ct);
        // Transaction sub-responses are not correlated back to their actions: re-read the version.
        return new LargeWriteResult(await ReadETagAsync(pk, rk, ct), Cacheable: false);
    }

    /// <summary>
    /// Deletes blobs no row references any more, after the write or delete that released them has
    /// committed. Each delete is conditional on the blob not having been modified within the cleanup
    /// grace: a concurrent writer that reuses the same content touches the blob before committing its
    /// row, so such a blob survives (the orphan sweep collects it if it really is unreferenced). Best
    /// effort: a failure is logged, never thrown.
    /// </summary>
    private async Task DeleteBlobsAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        if (offloadBlobs is null)
            return;
        var unmodifiedSince = CleanupCutoff();
        foreach (var path in paths)
            await DeleteBlobAsync(path, unmodifiedSince, ct);
    }

    // Blobs modified after this instant may be about to be referenced by a concurrent write: keep them.
    private DateTimeOffset CleanupCutoff() => DateTimeOffset.UtcNow - cleanupGrace;

    private async Task DeleteBlobAsync(string path, DateTimeOffset? unmodifiedSince, CancellationToken ct)
    {
        try
        {
            await offloadBlobs!.DeleteIfUnmodifiedAsync(path, unmodifiedSince, ct);
        }
#pragma warning disable CA1031 // the row change already committed: a cleanup failure must not turn it into an error
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogWarning(ex, "{Type}: could not delete blob {Blob}; an orphan sweep will remove it", typeName, path);
        }
    }

    /// <summary>
    /// After a partition's rows are deleted, deletes every blob stored under it (best effort), each on
    /// condition that it was modified neither within the cleanup grace nor since it was listed, so a blob
    /// a concurrent write has just touched for a new row survives.
    /// </summary>
    private async Task DeletePartitionBlobsAsync(string partition, CancellationToken ct)
    {
        if (!LargeValueAware || offloadBlobs is null)
            return;
        var prefix = Uri.EscapeDataString(tableName) + "/" + Uri.EscapeDataString(partition) + "/";
        var listed = new List<(string Name, DateTimeOffset LastModified)>();
        try
        {
            await foreach (var blob in offloadBlobs.ListAsync(prefix, ct))
                listed.Add(blob);
        }
#pragma warning disable CA1031 // the rows are already deleted: a cleanup failure must not turn that into an error
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogWarning(ex, "{Type}: could not list the blobs of partition {Partition}; an orphan sweep will remove them", typeName, partition);
            return;
        }
        var cutoff = CleanupCutoff();
        foreach (var (name, lastModified) in listed)
            await DeleteBlobAsync(name, lastModified < cutoff ? lastModified : cutoff, ct);
    }

    /// <summary>
    /// The entity a stored (assembled) row holds: its offloaded values downloaded and verified, its
    /// <see cref="TableBlobContent"/> values lazy. A plain <typeparamref name="T"/> deserializes as before.
    /// </summary>
    private async Task<T> ToEntityAsync(TableEntity row, CancellationToken ct)
    {
        if (!LargeValueAware)
            return row.FromTableEntity<T>();
        var ctx = await offload.ResolveAsync(row, typeof(T), projectedColumns: null, ct);
        return TableEntitySerializer.FromTableEntity<T>(row, ctx);
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

    private async Task<ETag> ReadETagAsync(string pk, string rk, CancellationToken ct)
    {
        var response = await client.GetEntityIfExistsAsync<TableEntity>(pk, rk, KeysOnly, ct);
        return response is { HasValue: true, Value: not null } ? response.Value.ETag : default;
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

    // The existing continuation rows the new plan no longer writes.
    private static List<TableEntity> StaleParts(List<TableEntity> existing, SplitRowPlan plan)
    {
        var kept = new HashSet<string>(plan.Parts.Select(p => p.RowKey), StringComparer.Ordinal);
        return existing.Where(p => !kept.Contains(p.RowKey)).ToList();
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

    /// <summary>
    /// Deletes a split-aware entity and its continuation rows: one conditional delete when it has none,
    /// otherwise one transaction over the parts that exist (found by a range query, so a primary that
    /// declares a part already gone can still be deleted). A missing row is a no-op; a lost race
    /// re-reads and retries. Once the entity is deleted, the blobs it referenced are deleted (best effort).
    /// </summary>
    private async Task DeleteLargeAsync(string pk, string rk, CancellationToken ct)
    {
        SplitRowLayout.ValidateRowKey(rk);
        RequestFailedException? lost = null;
        for (var attempt = 1; attempt <= MaxLargeWriteAttempts; attempt++)
        {
            var prior = await ReadPriorAsync(pk, rk, ct);
            if (prior is null)
                return;

            try
            {
                var parts = prior.Parts == 0 ? [] : await QueryPartsAsync(pk, rk, PartKeys, ct);
                if (parts.Count == 0)
                {
                    await client.DeleteEntityAsync(pk, rk, prior.ETag, ct);
                }
                else
                {
                    var actions = new List<TableTransactionAction>(1 + parts.Count)
                    {
                        new(TableTransactionActionType.Delete, new TableEntity(pk, rk), prior.ETag),
                    };
                    foreach (var part in parts)
                        actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(pk, part.RowKey), ETag.All));
                    await client.SubmitTransactionAsync(actions, ct);
                }
            }
            catch (RequestFailedException ex) when (ex.Status is 412 or 404)
            {
                lost = ex;
                continue;
            }

            // The entity is gone: the blobs it referenced follow (best effort).
            await DeleteBlobsAsync(prior.Blobs, ct);
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

    // Split-aware: per-entity writes, each atomic with its own continuation rows (no per-chunk atomicity).
    private async Task WriteBatchLargeAsync(IEnumerable<T> entities, TableTransactionActionType actionType, CancellationToken ct)
    {
        var kind = actionType == TableTransactionActionType.Add ? LargeWriteKind.Create : LargeWriteKind.Upsert;
        // Refuse a reserved row key before the first entity is written, not halfway through.
        foreach (var entity in entities)
            SplitRowLayout.ValidateRowKey(GetEntityKeys(entity.Id).RowKey);
        foreach (var entity in entities)
        {
            var result = await WriteLargeAsync(entity, kind, ETag.All, ct);
            entity.ETag = result.ETag == default ? null : result.ETag.ToString();
        }
    }
}
