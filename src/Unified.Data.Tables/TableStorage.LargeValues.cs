using System.Globalization;
using Azure;
using Azure.Data.Tables;

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

    // _SplitParts says how many continuation rows the stored row owns; the __blob reference columns are
    // selected now so the offload tier can read them from the same point read.
    private static readonly string[] PriorSelect =
    [
        SystemColumnNames.SplitParts,
        "odata.etag",
        .. TypeMetadataCache.LargeValueColumns(typeof(T)).Select(c => c + SplitCodec.BlobSuffix),
    ];

    private static readonly string[] KeysOnly = ["PartitionKey", "RowKey"];

    /// <summary>What a write or delete needs to know about the stored row: its version and its continuation rows.</summary>
    private sealed record PriorRow(ETag ETag, int Parts);

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
        if (!response.HasValue || response.Value is null)
            return null;
        return new PriorRow(response.Value.ETag, SplitRowLayout.PartCount(response.Value));
    }

    /// <summary>
    /// Writes a split-aware entity: one SDK call when the value fits one row and no continuation rows
    /// must go, otherwise one transaction holding the primary, its parts and the deletes of stale parts.
    /// Returns the new ETag, or <c>default</c> when it could not be read back.
    /// </summary>
    private async Task<ETag> WriteLargeAsync(T entity, LargeWriteKind kind, ETag ifMatch, CancellationToken ct)
    {
        var (pk, rk) = GetEntityKeys(entity.Id);
        SplitRowLayout.ValidateRowKey(rk);

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

            var row = TableEntitySerializer.ToTableEntity(entity, pk, rk, ValueWriteContext.Public, []);
            var plan = SplitRowLayout.Plan(row);
            var staleFrom = plan.Parts.Count + 1;
            var staleTo = prior?.Parts ?? 0;
            var staleCount = Math.Max(0, staleTo - staleFrom + 1);
            // null = insert (no stored row); otherwise replace conditional on the version just read.
            ETag? condition = prior is null ? null : strict ? ifMatch : prior.ETag;

            try
            {
                if (plan.Parts.Count == 0 && staleCount == 0)
                {
                    var response = condition is ETag c
                        ? await client.UpdateEntityAsync(plan.Primary, c, TableUpdateMode.Replace, ct)
                        : await client.AddEntityAsync(plan.Primary, ct);
                    return HeaderETag(response) ?? plan.Primary.ETag;
                }

                if (!SplitRowLayout.FitsOneTransaction(plan, staleCount))
                    throw new OversizedValueException(
                        TypeMetadataCache.LargeValueColumns(typeof(T))[0],
                        plan.Parts.Sum(TableRowSize.Estimate) + TableRowSize.Estimate(plan.Primary),
                        "the row and its continuation rows do not fit one entity-group transaction and blob offload is not configured");

                var actions = new List<TableTransactionAction>(1 + plan.Parts.Count + staleCount)
                {
                    condition is ETag c2
                        ? new TableTransactionAction(TableTransactionActionType.UpdateReplace, plan.Primary, c2)
                        : new TableTransactionAction(TableTransactionActionType.Add, plan.Primary),
                };
                foreach (var part in plan.Parts)
                    actions.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, part));
                for (var i = staleFrom; i <= staleTo; i++)
                    actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(pk, PartRowKey(rk, i)), ETag.All));

                await client.SubmitTransactionAsync(actions, ct);
                // Transaction sub-responses are not correlated back to their actions: re-read the version.
                return await ReadETagAsync(pk, rk, ct);
            }
            catch (RequestFailedException ex) when (LostCondition(ex, kind))
            {
                if (kind == LargeWriteKind.Create)
                    throw new DuplicateKeyException(typeName, entity.Id, ex);
                // A strict replace may not retry; a vanished row surfaces as the service's own 404.
                if (strict)
                    throw IsPrimaryFailure(ex) && ex.Status == 404 ? ex : new ConcurrencyConflictException(typeName, entity.Id, ex);
                lost = ex;
            }
        }

        throw new ConcurrencyConflictException(typeName, entity.Id, lost);
    }

    // The failures a concurrent writer causes: the insert found a row (409), the conditional replace
    // found another version (412), or a row the write expected (the primary or a stale part) is gone (404).
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

    private static string PartRowKey(string rk, int index) => rk + "~" + index.ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>
    /// Stamps the outcome of a split-aware write on <paramref name="entity"/> and keeps the caches
    /// coherent. An unknown version (the row vanished after the write) is never cached.
    /// </summary>
    private void CommitLargeWrite(T entity, string partitionKey, ETag etag)
    {
        if (etag == default)
        {
            entity.ETag = null;
            cache.Remove(EntityCacheKey(entity.Id));
        }
        else
        {
            entity.ETag = etag.ToString();
            CacheEntity(entity.Id, entity, etag);
        }
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

        var pk = primary.PartitionKey;
        var rk = primary.RowKey;
        var lower = SplitRowLayout.PartRangeLower(rk);
        var upper = SplitRowLayout.PartRangeUpper(rk);
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {pk} and RowKey ge {lower} and RowKey lt {upper} and _SplitOf eq {rk}");

        var parts = new List<TableEntity>();
        await foreach (var part in client.QueryAsync<TableEntity>(filter, maxPerPage: null, select: null, ct).WithCancellation(ct))
            parts.Add(part);
        return SplitRowLayout.Assemble(primary, parts);
    }

    /// <summary>
    /// Deletes a split-aware entity and its continuation rows: one conditional delete when it has none,
    /// otherwise one transaction. A missing row is a no-op; a lost race re-reads and retries.
    /// </summary>
    private async Task DeleteLargeAsync(string pk, string rk, CancellationToken ct)
    {
        RequestFailedException? lost = null;
        for (var attempt = 1; attempt <= MaxLargeWriteAttempts; attempt++)
        {
            var prior = await ReadPriorAsync(pk, rk, ct);
            if (prior is null)
                return;

            try
            {
                if (prior.Parts == 0)
                {
                    await client.DeleteEntityAsync(pk, rk, prior.ETag, ct);
                    return;
                }

                var actions = new List<TableTransactionAction>(1 + prior.Parts)
                {
                    new(TableTransactionActionType.Delete, new TableEntity(pk, rk), prior.ETag),
                };
                for (var i = 1; i <= prior.Parts; i++)
                    actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(pk, PartRowKey(rk, i)), ETag.All));
                await client.SubmitTransactionAsync(actions, ct);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status is 412 or 404)
            {
                lost = ex;
            }
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
            var etag = await WriteLargeAsync(entity, kind, ETag.All, ct);
            entity.ETag = etag == default ? null : etag.ToString();
        }
    }
}
