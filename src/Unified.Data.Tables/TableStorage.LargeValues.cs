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
//
// Whose row is it? A continuation row is the entity's own when the stored primary declares it, or when
// its _SplitOf cell names the entity (a leftover: the primary was deleted or replaced by a writer that
// does not know continuation rows, a partition delete stopped halfway, or a delete raced a write). A
// write replaces its own rows and deletes the ones it no longer needs; any other row at a continuation
// key is foreign and is never overwritten. Leftovers are looked for only when one is in the way.
public partial class TableStorage<T>
{
    /// <summary>True when <typeparamref name="T"/> has a member that may be stored as split cells.</summary>
    private static readonly bool LargeValueAware = TypeMetadataCache.IsLargeValueAware(typeof(T));

    /// <summary>How many times a write that lost its condition to a concurrent writer re-reads and retries.</summary>
    private const int MaxLargeWriteAttempts = 3;

    // _SplitParts says how many continuation rows the stored row owns, _SplitOf marks the row itself as
    // one; the __blob reference columns are selected now so the offload tier can read them from the same
    // point read. The row's version is not a column and is never selected: the service returns it with
    // every entity (and as the response's ETag header), and may refuse a $select that names odata.etag.
    private static readonly string[] PriorSelect =
    [
        SystemColumnNames.SplitParts,
        SystemColumnNames.SplitOf,
        .. TypeMetadataCache.LargeValueColumns(typeof(T)).Select(c => c + SplitCodec.BlobSuffix),
    ];

    private static readonly string[] PartKeys = ["PartitionKey", "RowKey", SystemColumnNames.SplitOf];

    /// <summary>
    /// The version a split-aware write produced (<c>default</c> when its response did not carry one),
    /// whether the written entity is cached under it, and the service time of the commit (its response
    /// <c>Date</c>; null when unknown). The version is always the write's own: the ETag of the single
    /// call's response, or of the transaction's sub-response for the primary row. It is never read back
    /// afterwards, because a later read can already return another writer's version. An entity written
    /// through a transaction is not cached (the next read fetches it). The cleanup of released blobs is
    /// timed by the commit, not by the client clock.
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

    /// <summary>
    /// The row at an entity's keys as the point read before a write or delete returns it (projected onto
    /// <see cref="PriorSelect"/>), with its version. A continuation row has none here: nothing is ever
    /// written or deleted on condition of it.
    /// </summary>
    private readonly record struct StoredRow(TableEntity Row, ETag ETag);

    /// <summary>
    /// One point read of the row at these keys; null when there is none. An entity row's version is its
    /// own ETag (the service sends it with every entity, whatever the projection) or, when the row
    /// carries none, the ETag header of the response.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The row exists and neither it nor the response carries a version: a replace or delete of it could
    /// only be unconditional, so nothing is written.
    /// </exception>
    private async Task<StoredRow?> ReadStoredAsync(string pk, string rk, CancellationToken ct)
    {
        var response = await client.GetEntityIfExistsAsync<TableEntity>(pk, rk, PriorSelect, ct);
        if (!response.HasValue || response.Value is not { } row)
            return null;
        if (SplitRowLayout.IsPartRow(row))
            return new StoredRow(row, default);

        var etag = row.ETag;
        if (!IsVersion(etag))
            etag = HeaderETag(RawResponse(response)) ?? default;
        if (!IsVersion(etag))
            throw new InvalidOperationException(
                $"The stored version (ETag) of {typeName} '{pk}{Separator}{rk}' could not be read: neither the row nor the response "
                + "of its point read carried one. Nothing was written: a replace or delete without a version would be unconditional.");
        return new StoredRow(row, etag);
    }

    /// <summary>
    /// One point read of the stored row's version, part count and blob references; null when the row
    /// does not exist.
    /// </summary>
    private async Task<PriorRow?> ReadPriorAsync(string pk, string rk, CancellationToken ct)
    {
        // A continuation row is not an entity: addressing one by id finds nothing.
        if (await ReadStoredAsync(pk, rk, ct) is not { } stored || SplitRowLayout.IsPartRow(stored.Row))
            return null;
        return new PriorRow(stored.ETag, SplitRowLayout.PartCount(stored.Row), BlobPaths(stored.Row, pk, rk));
    }

    // A version a conditional write can name: not empty, and not the "any version" wildcard.
    private static bool IsVersion(ETag etag) => etag != ETag.All && !string.IsNullOrEmpty(etag.ToString());

    // The blob names a stored row references; a malformed reference is logged and skipped. The keys are
    // passed in: the projected row does not carry them.
    private IReadOnlyList<string> BlobPaths(TableEntity row, string pk, string rk) =>
        LargeValueTable.BlobPaths(row, (column, ex) =>
            logger.LogWarning(ex, "{Type} {PartitionKey}|{RowKey}: ignoring a malformed blob reference in {Column}",
                typeName, pk, rk, column));

    /// <summary>
    /// Writes a split-aware entity: one SDK call when the value fits one row and no continuation rows
    /// must go, otherwise one transaction holding the primary, its parts and the deletes of stale parts.
    /// The blobs the row references are uploaded (create-only) before the entity write; once the write
    /// succeeded, the blobs the replaced row referenced and the new one does not are deleted (best
    /// effort). A failed write deletes no blob. Returns the version the write itself produced
    /// (<c>default</c> when its response did not carry one) and whether the entity is cached under it.
    /// </summary>
    private async Task<LargeWriteResult> WriteLargeAsync(T entity, LargeWriteKind kind, ETag ifMatch, CancellationToken ct)
    {
        var (pk, rk) = GetEntityKeys(entity.Id);
        SplitRowLayout.ValidateKeys(pk, rk);

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
        // Once the key of a new continuation row turned out to be held by a leftover continuation row of
        // this very entity, every attempt is planned over the entity's own continuation rows as they are
        // then (ownParts: the rows just read for the next attempt, read again for any later one).
        var takeBack = false;
        Dictionary<string, ETag>? ownParts = null;

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

            var own = ownParts ?? (takeBack ? await OwnPartsAsync(pk, rk, ct) : null);
            ownParts = null;
            var plan = own is null
                ? await large.PlanAsync(entity, pk, rk, prior?.Parts ?? 0, c => ExistingPartKeysAsync(pk, rk, c), ct)
                : large.PlanOverOwnParts(entity, pk, rk, prior?.Parts ?? 0, own.Keys);
            // Every blob exists (uploaded, or touched when already there) before the row that references it.
            await large.UploadAsync(plan, ct);
            // null = insert (no stored row); otherwise replace conditional on the version just read.
            ETag? condition = prior is null ? null : strict ? ifMatch : prior.ETag;
            var actions = TransactionActions(plan, condition, own, pk);

            LargeWriteResult result;
            try
            {
                result = actions is null ? await WriteRowAsync(plan.Primary, condition, ct) : await SubmitAsync(actions, ct);
            }
            catch (RequestFailedException ex) when (actions is not null && TakenPartKey(ex, actions) is { } taken)
            {
                // A row holds the key of a new continuation row. A continuation row of this very entity
                // is its own, left behind: the write is planned again over the entity's own rows. Any
                // other row is foreign: the write is refused and that row is left as it is.
                ownParts = await OwnPartsAsync(pk, rk, ct);
                if (!ownParts.ContainsKey(taken))
                    throw LargeValueTable.ContinuationKeyTaken(pk, rk, taken, ex);
                takeBack = true;
                lost = ex;
                continue;
            }
            catch (RequestFailedException ex) when (LostRace(ex, kind))
            {
                if (IsPrimaryFailure(ex))
                {
                    if (kind == LargeWriteKind.Create)
                        throw new DuplicateKeyException(typeName, entity.Id, ex);
                    // A strict replace may not retry its own condition; a vanished row surfaces as the
                    // service's own 404.
                    if (strict)
                        throw ex.Status == 404 ? ex : new ConcurrencyConflictException(typeName, entity.Id, ex);
                }
                // Any write plans again when a continuation row it counted on is gone or has changed.
                lost = ex;
                continue;
            }

            // The swap committed: the blobs only the replaced row referenced can go (the same content
            // written again maps to the same name, which the new row still references). A committed write
            // is never reported as failed: a cancelled token only bounds how long its cleanup may take.
            if (prior is { Blobs.Count: > 0 })
                await DeleteBlobsAsync(pk, rk, prior.Blobs.Except(plan.BlobPaths, StringComparer.Ordinal), result.CommittedAt, ct);
            return result;
        }

        throw new ConcurrencyConflictException(typeName, entity.Id, lost);
    }

    /// <summary>The row keys of the continuation rows of <paramref name="rk"/> that exist now (one range query).</summary>
    private async Task<IReadOnlyList<string>> ExistingPartKeysAsync(string pk, string rk, CancellationToken ct) =>
        (await QueryPartsAsync(pk, rk, PartKeys, ct)).Select(p => p.RowKey).ToList();

    /// <summary>
    /// The entity's own continuation rows that exist now, whether its primary declares them or not (one
    /// range query: the rows in its continuation-row key range whose <c>_SplitOf</c> names it), each with
    /// the version it was read at.
    /// </summary>
    private async Task<Dictionary<string, ETag>> OwnPartsAsync(string pk, string rk, CancellationToken ct)
    {
        var own = new Dictionary<string, ETag>(StringComparer.Ordinal);
        foreach (var part in await QueryPartsAsync(pk, rk, PartKeys, ct))
            own[part.RowKey] = part.ETag;
        return own;
    }

    /// <summary>
    /// The actions of the transaction that applies <paramref name="plan"/>: the primary first (so its
    /// sub-response is the first), then its continuation rows and the deletes of the ones it no longer
    /// needs. Null when one SDK call does it: no continuation row to write and none to delete.
    /// </summary>
    private static List<TableTransactionAction>? TransactionActions(
        LargeValueWritePlan plan, ETag? condition, Dictionary<string, ETag>? own, string pk)
    {
        if (plan.Parts.Count == 0 && plan.StalePartRowKeys.Count == 0)
            return null;
        var actions = new List<TableTransactionAction>(1 + plan.Parts.Count + plan.StalePartRowKeys.Count)
        {
            condition is ETag c
                ? new TableTransactionAction(TableTransactionActionType.UpdateReplace, plan.Primary, c)
                : new TableTransactionAction(TableTransactionActionType.Add, plan.Primary),
        };
        AddPartActions(actions, plan, own, pk);
        return actions;
    }

    // One SDK call: the insert, or the replace on condition of the version read.
    private async Task<LargeWriteResult> WriteRowAsync(TableEntity primary, ETag? condition, CancellationToken ct)
    {
        var response = condition is ETag c
            ? await client.UpdateEntityAsync(primary, c, TableUpdateMode.Replace, ct)
            : await client.AddEntityAsync(primary, ct);
        return new LargeWriteResult(HeaderETag(response) ?? primary.ETag, Cacheable: true, HeaderDate(response));
    }

    private async Task<LargeWriteResult> SubmitAsync(List<TableTransactionAction> actions, CancellationToken ct)
    {
        var committed = await client.SubmitTransactionAsync(actions, ct);
        // The version is the one this transaction gave the primary: its own sub-response. The row is
        // not read again: by then another writer may have committed, and its version on this writer's
        // content would let the next optimistic update replace that writer's row without a conflict.
        return new LargeWriteResult(PrimaryETag(committed), Cacheable: false, HeaderDate(RawResponse(committed)));
    }

    // The ETag of a committed transaction's sub-response for its first action, the primary row
    // (sub-responses are in action order); default when the response carries none (a test double).
    private static ETag PrimaryETag(Response<IReadOnlyList<Response>>? committed)
    {
        try
        {
            return committed?.Value is { Count: > 0 } responses ? HeaderETag(responses[0]) ?? default : default;
        }
        catch
        {
            return default;
        }
    }

    // Appends a plan's continuation-row actions: the rows it writes, in order, then the deletes of the
    // ones it no longer needs. A continuation row the stored row declares is the entity's own and is
    // replaced. A new one is an insert, so a row already at its key fails the transaction instead of
    // being lost. When the plan was made over the entity's own rows as they were read (own), each of
    // those is replaced or deleted on condition of the version it was read at: a row that changed in
    // between (it may be another writer's by now) fails the transaction instead of being overwritten.
    private static void AddPartActions(
        List<TableTransactionAction> actions, LargeValueWritePlan plan, Dictionary<string, ETag>? own, string pk)
    {
        for (var i = 0; i < plan.Parts.Count; i++)
        {
            var part = plan.Parts[i];
            actions.Add(ReadVersion(own, part.RowKey) is ETag version
                ? new TableTransactionAction(TableTransactionActionType.UpdateReplace, part, version)
                : new TableTransactionAction(
                    plan.IsNewPart(i) ? TableTransactionActionType.Add : TableTransactionActionType.UpsertReplace, part));
        }
        foreach (var partKey in plan.StalePartRowKeys)
        {
            actions.Add(new TableTransactionAction(
                TableTransactionActionType.Delete, new TableEntity(pk, partKey), ReadVersion(own, partKey) ?? ETag.All));
        }
    }

    // The version one of the entity's own continuation rows was read at, when the plan was made over them.
    private static ETag? ReadVersion(Dictionary<string, ETag>? own, string partKey) =>
        own is not null && own.TryGetValue(partKey, out var version) && IsVersion(version) ? version : null;

    // The key of the new continuation row a transaction failed on with 409 (a row already holds it); null
    // for any other failure. Only a primary or a new part is an insert, and a primary is not a part row.
    private static string? TakenPartKey(RequestFailedException ex, List<TableTransactionAction> actions) =>
        ex.Status == 409
        && ex is TableTransactionFailedException { FailedTransactionActionIndex: int i }
        && i > 0 && i < actions.Count
        && actions[i].ActionType == TableTransactionActionType.Add
        && SplitRowLayout.IsPartRow((TableEntity)actions[i].Entity)
            ? actions[i].Entity.RowKey
            : null;

    /// <summary>
    /// Deletes the blobs the row <paramref name="pk"/>|<paramref name="rk"/> no longer references, after
    /// the write or delete that released them has committed, each on condition that it was not modified
    /// within the cleanup grace (a concurrent writer that reuses the same content touches the blob before
    /// committing its row; the orphan sweep collects a blob that really is unreferenced). Only the row's
    /// own blobs go: a path outside its <c>{table}/{pk}/{rk}/</c> prefix (the row was copied from another
    /// table or key and still referenced the source row's blob) is left alone. Best effort: a failure is
    /// logged, never thrown. A cancelled token gives it a few more seconds, then leaves the rest to the
    /// orphan sweep.
    /// </summary>
    private Task DeleteBlobsAsync(string pk, string rk, IEnumerable<string> paths, DateTimeOffset? committedAt, CancellationToken ct) =>
        large.DeleteReleasedBlobsAsync(pk, rk, paths, committedAt,
            path => logger.LogDebug(
                "{Type} {PartitionKey}|{RowKey}: blob {Blob} is not under this row's own prefix (the row was copied from another table or key); it is left alone",
                typeName, pk, rk, path),
            LogBlobDeleteFailure, ct);

    private void LogBlobDeleteFailure(string path, Exception ex) =>
        logger.LogWarning(ex, "{Type}: could not delete blob {Blob}; an orphan sweep will remove it", typeName, path);

    /// <summary>
    /// After a partition's rows are deleted, deletes the blobs stored under it that no row of the
    /// partition references now (best effort): the rows that are there once the deletes are done are
    /// read again, so a row another writer created meanwhile keeps its blob however long the deletes
    /// took. Each delete is on condition that the blob was modified neither within the cleanup grace nor
    /// since it was listed, so a blob a concurrent write has just touched for a new row survives too. The
    /// deletion committed, so a cancelled token does not fail it: cleanup gets a few more seconds (the
    /// rows are read with the cleanup's own token), then leaves the rest to the orphan sweep.
    /// </summary>
    private Task DeletePartitionBlobsAsync(string partition, CancellationToken ct)
    {
        if (!LargeValueAware)
            return Task.CompletedTask;
        return large.DeletePartitionBlobsAsync(partition, typeof(T), (select, c) => PartitionRowsAsync(partition, select, c),
            ex => logger.LogWarning(ex, "{Type}: could not find the unreferenced blobs of partition {Partition}; an orphan sweep will remove them", typeName, partition),
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

    // The failures a concurrent writer causes. On the primary (a single SDK call, or action 0): the
    // insert found a row (409), the conditional replace found another version (412) or no row (404). On
    // any other action: a continuation row the write counted on is gone (404: a stale one another writer
    // removed, or one of the entity's own it was replacing or deleting) or has changed since it was read
    // (412). A retry reads the primary and the continuation rows again, so a row that is gone is not
    // required again.
    private static bool LostRace(RequestFailedException ex, LargeWriteKind kind) =>
        IsPrimaryFailure(ex)
            ? kind == LargeWriteKind.Create ? ex.Status == 409 : ex.Status is 404 or 409 or 412
            : ex.Status is 404 or 412;

    // A transaction fails at one action; the primary is action 0, and a single SDK call is always the primary.
    private static bool IsPrimaryFailure(RequestFailedException ex) =>
        ex is not TableTransactionFailedException tx || tx.FailedTransactionActionIndex is null or 0;

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

    private static Response? RawResponse(NullableResponse<TableEntity>? response)
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
    private static ETag? HeaderETag(Response? response)
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

    /// <summary>
    /// For a split-aware <typeparamref name="T"/>, refuses a row key reserved for continuation rows, and a
    /// key that is not well-formed UTF-16 (it cannot name a blob), before any SDK call.
    /// </summary>
    private void RefuseReservedRowKey(string id)
    {
        if (!LargeValueAware)
            return;
        var (pk, rk) = GetEntityKeys(NormalizeId(id));
        SplitRowLayout.ValidateKeys(pk, rk);
    }

    /// <summary>
    /// Stamps the outcome of a split-aware write on <paramref name="entity"/> and keeps the caches
    /// coherent. The entity gets the version the write itself produced, or null when that is unknown
    /// (re-read before an optimistic update, as after a batch). Only a single call's result is cached; a
    /// transaction's result and an unknown version evict the entry instead, so the next read goes to the
    /// store.
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
    /// declares a part already gone can still be deleted). When there is no row at the id, the entity's
    /// leftover continuation rows (if any) are deleted, so nothing of it stays behind; a continuation row
    /// addressed by its own id is not an entity and is left alone. A lost race re-reads and retries. Any
    /// row key is accepted. Once the entity is deleted, the blobs it referenced are deleted (best effort).
    /// </summary>
    private async Task DeleteLargeAsync(string pk, string rk, CancellationToken ct)
    {
        // No row-key check: a row written before the type opted in may carry any key and stays deletable.
        RequestFailedException? lost = null;
        for (var attempt = 1; attempt <= MaxLargeWriteAttempts; attempt++)
        {
            var stored = await ReadStoredAsync(pk, rk, ct);
            // A continuation row addressed by its own id is not an entity: nothing is deleted.
            if (stored is { } found && SplitRowLayout.IsPartRow(found.Row))
                return;

            IReadOnlyList<string> blobs = [];
            DateTimeOffset? committedAt = null;
            try
            {
                if (stored is not { } row)
                {
                    // No primary row: all that can be left of the entity are continuation rows nothing
                    // declares. They go too (one range query, in this case only), so the key is clean.
                    await DeleteLeftoverPartsAsync(pk, rk, ct);
                    return;
                }

                blobs = BlobPaths(row.Row, pk, rk);
                var parts = SplitRowLayout.PartCount(row.Row) == 0 ? [] : await QueryPartsAsync(pk, rk, PartKeys, ct);
                if (parts.Count == 0)
                {
                    committedAt = HeaderDate(await client.DeleteEntityAsync(pk, rk, row.ETag, ct));
                }
                else
                {
                    var actions = new List<TableTransactionAction>(1 + parts.Count)
                    {
                        new(TableTransactionActionType.Delete, new TableEntity(pk, rk), row.ETag),
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

            // The entity is gone: the blobs it referenced follow (best effort; a cancelled token only bounds it).
            await DeleteBlobsAsync(pk, rk, blobs, committedAt, ct);
            return;
        }

        throw new ConcurrencyConflictException(typeName, $"{pk}{Separator}{rk}", lost);
    }

    /// <summary>
    /// Deletes the continuation rows of an entity that has no primary row (what a writer that does not
    /// know continuation rows, an interrupted partition delete or a delete that raced a write left of
    /// it), in transactions of at most 100. Each row is deleted on condition of the version it was listed
    /// at: a writer that is taking the same rows back for a new value at that moment commits as a whole or
    /// not at all, and so does this delete (the caller reads again and retries when it loses). A table
    /// that is not there has nothing to delete, as for any other type.
    /// </summary>
    private async Task DeleteLeftoverPartsAsync(string pk, string rk, CancellationToken ct)
    {
        List<TableEntity> leftovers;
        try
        {
            leftovers = await QueryPartsAsync(pk, rk, PartKeys, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // The point read of a missing table reads as "no row"; only the query says the table is gone.
            return;
        }

        foreach (var chunk in leftovers.Chunk(BatchPlanner.DefaultMaxCount))
        {
            var actions = chunk.Select(part => new TableTransactionAction(
                TableTransactionActionType.Delete, new TableEntity(pk, part.RowKey), IsVersion(part.ETag) ? part.ETag : ETag.All));
            await client.SubmitTransactionAsync(actions, ct);
        }
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

    /// <summary>
    /// One entity of a split-aware batch: the stored row it was planned against, its rows and, when it was
    /// planned over the entity's own continuation rows, those rows with the versions they were read at.
    /// </summary>
    private sealed record BatchUnit(T Entity, string Pk, string Rk, PriorRow? Prior, LargeValueWritePlan Plan, Dictionary<string, ETag>? OwnParts);

    /// <summary>
    /// A split-aware batch, with the plain batch's contract: per partition, each entity's rows (primary,
    /// continuation rows, stale-part deletes) are one indivisible unit, and the units are packed in order
    /// into entity-group transactions (<see cref="LargeValueTable.PlanTransactions"/>), each atomic. The
    /// blobs a transaction's rows reference are uploaded (create-only) just before that transaction, so a
    /// blob waits for its row no longer than in a single write; after each transaction commits, the blobs
    /// only the replaced rows referenced are deleted (best effort). A
    /// key repeated in a create batch throws <see cref="DuplicateKeyException"/> before anything is
    /// written; in an upsert batch its last occurrence is written. An existing key fails its create
    /// transaction (<see cref="DuplicateKeyException"/>); earlier transactions stay committed. An upsert
    /// transaction that lost a race to another writer is planned again from the re-read rows and retried;
    /// after three lost races it throws <see cref="ConcurrencyConflictException"/>. A transaction that
    /// finds the key of a new continuation row held by a leftover continuation row of the same entity is
    /// planned again over that entity's own rows (they are replaced or deleted in the retried
    /// transaction); a foreign row at such a key fails the transaction, and is left as it is.
    /// </summary>
    private async Task WriteBatchLargeAsync(IReadOnlyCollection<T> entities, TableTransactionActionType actionType, CancellationToken ct)
    {
        var kind = actionType == TableTransactionActionType.Add ? LargeWriteKind.Create : LargeWriteKind.Upsert;
        // Refuse a reserved or unsplittable key before the first entity is written, not halfway through.
        foreach (var entity in entities)
        {
            var (pk, rk) = GetEntityKeys(entity.Id);
            SplitRowLayout.ValidateKeys(pk, rk);
        }
        var writes = DistinctBatchKeys(entities, kind);

        try
        {
            // Hash every TableBlobContent once (a non-seekable one is copied to a temporary file).
            foreach (var entity in writes)
                await large.PrepareAsync(entity, ct);
            foreach (var group in writes.GroupBy(e => GetEntityKeys(e.Id).PartitionKey, StringComparer.Ordinal))
            {
                var units = await PlanBatchUnitsAsync([.. group], kind, takeBack: null, ct);
                await SubmitBatchUnitsAsync(units, kind, attempt: 1, takeBack: null, ct);
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

    /// <summary>
    /// Plans the entities of one partition. <paramref name="takeBack"/> names (by row key) the entities
    /// that are planned over their own continuation rows because a leftover held a key of theirs: each
    /// with the rows just read for it, used once, or null to read them now.
    /// </summary>
    private async Task<List<BatchUnit>> PlanBatchUnitsAsync(
        List<T> entities, LargeWriteKind kind, Dictionary<string, Dictionary<string, ETag>?>? takeBack, CancellationToken ct)
    {
        var units = new List<BatchUnit>(entities.Count);
        foreach (var entity in entities)
        {
            var (pk, rk) = GetEntityKeys(entity.Id);
            var prior = kind == LargeWriteKind.Create ? null : await ReadPriorAsync(pk, rk, ct);
            Dictionary<string, ETag>? own = null;
            if (takeBack is not null && takeBack.TryGetValue(rk, out var read))
            {
                own = read ?? await OwnPartsAsync(pk, rk, ct);
                takeBack[rk] = null; // a later attempt reads them again
            }
            var plan = own is null
                ? await large.PlanAsync(entity, pk, rk, prior?.Parts ?? 0, c => ExistingPartKeysAsync(pk, rk, c), ct)
                : large.PlanOverOwnParts(entity, pk, rk, prior?.Parts ?? 0, own.Keys);
            units.Add(new BatchUnit(entity, pk, rk, prior, plan, own));
        }
        return units;
    }

    private async Task SubmitBatchUnitsAsync(
        List<BatchUnit> units, LargeWriteKind kind, int attempt, Dictionary<string, Dictionary<string, ETag>?>? takeBack, CancellationToken ct)
    {
        foreach (var range in LargeValueTable.PlanTransactions(units.Select(u => u.Plan).ToList()))
        {
            var chunk = units.GetRange(range.Start, range.Count);
            // Every blob exists (uploaded, or touched when already there) before the rows that reference
            // it, and is uploaded just before its own transaction: an orphan sweep measures a blob's age
            // from its upload, so a later transaction's blob must not wait through the earlier ones.
            foreach (var unit in chunk)
                await large.UploadAsync(unit.Plan, ct);
            var (actions, owners) = BatchActions(chunk);
            DateTimeOffset? committedAt;
            try
            {
                committedAt = HeaderDate(RawResponse(await client.SubmitTransactionAsync(actions, ct)));
            }
            catch (RequestFailedException ex) when (TakenPartKey(ex, actions) is { } taken)
            {
                // A row holds the key of a new continuation row of one entity. Its own leftover: the
                // transaction (nothing of it committed) is planned again with that entity over its own
                // continuation rows. A foreign row: refused, and that row is left as it is.
                var unit = chunk[owners[((TableTransactionFailedException)ex).FailedTransactionActionIndex!.Value]];
                var own = await OwnPartsAsync(unit.Pk, unit.Rk, ct);
                if (!own.ContainsKey(taken))
                    throw LargeValueTable.ContinuationKeyTaken(unit.Pk, unit.Rk, taken, ex);
                if (attempt >= MaxLargeWriteAttempts)
                    throw new ConcurrencyConflictException(typeName, unit.Entity.Id, ex);
                var taking = takeBack is null
                    ? new Dictionary<string, Dictionary<string, ETag>?>(StringComparer.Ordinal)
                    : new Dictionary<string, Dictionary<string, ETag>?>(takeBack, StringComparer.Ordinal);
                taking[unit.Rk] = own;
                var replanned = await PlanBatchUnitsAsync(chunk.ConvertAll(u => u.Entity), kind, taking, ct);
                await SubmitBatchUnitsAsync(replanned, kind, attempt + 1, taking, ct);
                continue;
            }
            catch (RequestFailedException ex) when (kind == LargeWriteKind.Create && ex.Status == 409 && IsBatchPrimaryFailure(ex, owners))
            {
                throw new DuplicateKeyException(typeName, FailedId(ex, owners, chunk), ex);
            }
            catch (RequestFailedException ex) when (BatchLostRace(ex, kind, owners))
            {
                // The whole transaction failed: nothing of it committed, so it is planned again from the
                // rows as they are now. A continuation row removed meanwhile (404) is not required again.
                if (attempt >= MaxLargeWriteAttempts)
                    throw new ConcurrencyConflictException(typeName, FailedId(ex, owners, chunk), ex);
                var replanned = await PlanBatchUnitsAsync(chunk.ConvertAll(u => u.Entity), kind, takeBack, ct);
                await SubmitBatchUnitsAsync(replanned, kind, attempt + 1, takeBack, ct);
                continue;
            }

            // The transaction committed: the blobs only the replaced rows referenced can go. A committed
            // write is never reported as failed: a cancelled token only bounds how long cleanup may take.
            foreach (var unit in chunk)
            {
                if (unit.Prior is { Blobs.Count: > 0 })
                    await DeleteBlobsAsync(unit.Pk, unit.Rk, unit.Prior.Blobs.Except(unit.Plan.BlobPaths, StringComparer.Ordinal), committedAt, ct);
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
            var partsFrom = actions.Count;
            AddPartActions(actions, unit.Plan, unit.OwnParts, unit.Pk);
            for (var p = partsFrom; p < actions.Count; p++)
                owners.Add(i);
        }
        return (actions, owners);
    }

    // A failure the service attributes to no action, or to a primary (an insert or conditional replace).
    private static bool IsBatchPrimaryFailure(RequestFailedException ex, List<int> owners) =>
        ex is not TableTransactionFailedException { FailedTransactionActionIndex: int i }
        || i < 0 || i >= owners.Count
        || owners[i] < 0;

    // As LostRace, for a batch transaction. On a primary (or a failure the service attributes to no
    // action) only an upsert loses a race: a create's 409 is a duplicate key. On any other action a
    // continuation row the transaction counted on is gone (404) or has changed since it was read (412).
    private static bool BatchLostRace(RequestFailedException ex, LargeWriteKind kind, List<int> owners) =>
        IsBatchPrimaryFailure(ex, owners)
            ? kind == LargeWriteKind.Upsert && ex.Status is 404 or 409 or 412
            : ex.Status is 404 or 412;

    // The id of the entity whose action failed ("{partition}|?" when the service did not say).
    private string FailedId(RequestFailedException ex, List<int> owners, List<BatchUnit> chunk)
    {
        if (ex is TableTransactionFailedException { FailedTransactionActionIndex: int i } && i >= 0 && i < owners.Count)
            return chunk[owners[i] < 0 ? ~owners[i] : owners[i]].Entity.Id;
        return chunk[0].Pk + Separator + "?";
    }
}
