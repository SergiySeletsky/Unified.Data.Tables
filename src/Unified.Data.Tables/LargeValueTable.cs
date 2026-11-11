using System.ComponentModel;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Providers;

/// <summary>
/// Storage-provider SPI, not for application code: the large-value rules of one table (split rows,
/// continuation rows and blob offload) as <c>TableStorage&lt;T&gt;</c> applies them, so another
/// <see cref="IStorage{T}"/> implementation (such as the in-memory store) runs the same planning,
/// upload, read and cleanup code instead of a copy of it. The caller owns the rows: it reads the stored
/// row, applies the plan atomically and deletes the blobs released afterwards. Its shape may change in
/// any minor version.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class LargeValueTable
{
    private readonly ValueOffloadCoordinator coordinator;
    private readonly IOffloadBlobStore? blobs;
    private readonly TimeSpan cleanupGrace;
    private readonly string tableName;

    /// <summary>
    /// Storage-provider SPI, not for application code. How much further back a cleanup or sweep cutoff
    /// is set when no service time is available and the client's clock is used instead: that clock may
    /// run ahead of the one that stamps blob <c>Last-Modified</c> times.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static readonly TimeSpan ClientClockMargin = TimeSpan.FromMinutes(5);

    /// <summary>The client clock, used only when the service time is unknown (replaced by tests).</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// How much longer the blob cleanup that follows a committed change keeps going once the caller's token
    /// is cancelled (replaced by tests). Then it stops, and the orphan sweep removes what is left.
    /// </summary>
    internal TimeSpan CancelledCleanupBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Storage-provider SPI, not for application code. Creates the large-value rules of the table
    /// <paramref name="tableName"/>.
    /// </summary>
    /// <param name="tableName">The store's resolved table name, the first segment of every blob name.</param>
    /// <param name="options">The blob offload settings; null when blob offload is not configured.</param>
    /// <param name="blobs">The blob store; ignored (treated as null) when <paramref name="options"/> is null.</param>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is null or whitespace.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public LargeValueTable(string tableName, BlobOffloadOptions? options, IOffloadBlobStore? blobs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        this.tableName = tableName;
        this.blobs = options is null ? null : blobs;
        cleanupGrace = options?.CleanupGrace ?? TimeSpan.Zero;
        coordinator = new ValueOffloadCoordinator(this.blobs, options, tableName);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. True when <paramref name="entityType"/> has a
    /// member that may be stored as split cells, continuation rows or a blob.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <returns>Whether the type needs the large-value write and read paths.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool IsLargeValueAware(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return TypeMetadataCache.IsLargeValueAware(entityType);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Refuses a row key that would collide with, or
    /// overflow, the continuation-row key space.
    /// </summary>
    /// <param name="rowKey">The row key of a split-aware entity.</param>
    /// <exception cref="ArgumentException">
    /// The key ends with <c>'~'</c> followed by exactly four digits (a continuation row's shape) or is too long.
    /// </exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void ValidateRowKey(string rowKey)
    {
        ArgumentNullException.ThrowIfNull(rowKey);
        SplitRowLayout.ValidateRowKey(rowKey);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Refuses the keys of a large-value write: the row
    /// key as <see cref="ValidateRowKey"/> does, and either key when it is not well-formed UTF-16 (an
    /// unpaired surrogate would name the same blobs as a key with U+FFFD in its place).
    /// </summary>
    /// <param name="partitionKey">The partition key of a split-aware entity.</param>
    /// <param name="rowKey">The row key of a split-aware entity.</param>
    /// <exception cref="ArgumentException">A key has an unpaired surrogate, or the row key is reserved or too long.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void ValidateKeys(string partitionKey, string rowKey)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentNullException.ThrowIfNull(rowKey);
        SplitRowLayout.ValidateKeys(partitionKey, rowKey);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. The error of a write that needs a new continuation
    /// row at a key another row already holds (one written before the type opted in, or by a type sharing
    /// the table): the write is refused rather than overwrite that row.
    /// </summary>
    /// <param name="partitionKey">The entity's partition key.</param>
    /// <param name="rowKey">The entity's row key.</param>
    /// <param name="partRowKey">The continuation row's key that is taken.</param>
    /// <param name="inner">The store's failure (the conflict), if any.</param>
    /// <returns>The exception to throw.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static InvalidOperationException ContinuationKeyTaken(string partitionKey, string rowKey, string partRowKey, Exception? inner) =>
        new($"The row '{partRowKey}' in partition '{partitionKey}' is not a continuation row of '{rowKey}', but '{rowKey}' needs that key for one: "
            + "the write was refused and nothing of it was stored. The row was written before the type opted into split values, or by another "
            + "type sharing the table; move or delete it, or give the entity another key.", inner);

    /// <summary>
    /// Storage-provider SPI, not for application code. True when <paramref name="row"/> is a
    /// continuation row (not an entity of its own).
    /// </summary>
    /// <param name="row">A stored row.</param>
    /// <returns>Whether the row is a continuation row.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool IsPartRow(TableEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return SplitRowLayout.IsPartRow(row);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. True when <paramref name="row"/> is a
    /// continuation row of the entity at <paramref name="rowKey"/> in the same partition.
    /// </summary>
    /// <param name="row">A stored row.</param>
    /// <param name="partitionKey">The entity's partition key.</param>
    /// <param name="rowKey">The entity's row key.</param>
    /// <returns>Whether the row belongs to that entity as one of its continuation rows.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool IsPartOf(TableEntity row, string partitionKey, string rowKey)
    {
        ArgumentNullException.ThrowIfNull(row);
        return string.Equals(row.PartitionKey, partitionKey, StringComparison.Ordinal)
            && string.CompareOrdinal(row.RowKey, SplitRowLayout.PartRangeLower(rowKey)) >= 0
            && string.CompareOrdinal(row.RowKey, SplitRowLayout.PartRangeUpper(rowKey)) < 0
            && row.TryGetValue(SystemColumnNames.SplitOf, out var of)
            && string.Equals(of as string, rowKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. The number of continuation rows a stored
    /// primary row declares (0 when none).
    /// </summary>
    /// <param name="primary">A stored primary row.</param>
    /// <returns>The declared part count.</returns>
    /// <exception cref="TableValueIntegrityException">The part count cell is malformed.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static int PartCount(TableEntity primary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        return SplitRowLayout.PartCount(primary);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Merges a stored primary row with its
    /// continuation rows into the logical row, or returns <paramref name="primary"/> when it has none.
    /// </summary>
    /// <param name="primary">The stored primary row.</param>
    /// <param name="candidateParts">Rows that may be its continuation rows (others are ignored).</param>
    /// <returns>The logical row.</returns>
    /// <exception cref="TableValueIntegrityException">A declared continuation row is missing.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TableEntity Assemble(TableEntity primary, IEnumerable<TableEntity> candidateParts)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(candidateParts);
        return SplitRowLayout.PartCount(primary) == 0 ? primary : SplitRowLayout.Assemble(primary, candidateParts);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. The blob names a stored row references. A
    /// reference cell that is null is no reference (the service returns a selected property the row does
    /// not have as null): it is skipped silently. A malformed reference (a cell that holds something
    /// that is not a reference) cannot name a blob and must not block a write that replaces it: it is
    /// reported to <paramref name="onMalformed"/> and skipped.
    /// </summary>
    /// <param name="row">A stored (primary or logical) row.</param>
    /// <param name="onMalformed">Called with the column and the error of each malformed reference.</param>
    /// <returns>The referenced blob names.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IReadOnlyList<string> BlobPaths(TableEntity row, Action<string, TableValueIntegrityException>? onMalformed = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        var paths = new List<string>();
        foreach (var (column, cell) in ValueOffloadCoordinator.ReferenceCells(row))
        {
            try
            {
                paths.Add(ValueOffloadCoordinator.ParseReference(column, cell).Path);
            }
            catch (TableValueIntegrityException ex)
            {
                onMalformed?.Invoke(column, ex);
            }
        }
        return paths;
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Seals every <see cref="TableBlobContent"/> of
    /// <paramref name="entity"/> (hashing it; a non-seekable source is copied to a temporary file).
    /// Pair it with <see cref="ReleasePrepared"/> in a <c>finally</c>.
    /// </summary>
    /// <param name="entity">The entity about to be written.</param>
    /// <param name="ct">Cancels reading the content.</param>
    /// <returns>A task that completes when every content is sealed.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Task PrepareAsync(object entity, CancellationToken ct) => coordinator.PrepareAsync(entity, ct);

    /// <summary>
    /// Storage-provider SPI, not for application code. Releases the temporary copies
    /// <see cref="PrepareAsync"/> made for contents the write did not bind to a blob.
    /// </summary>
    /// <param name="entity">The entity that was being written.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void ReleasePrepared(object entity) => ValueOffloadCoordinator.ReleasePrepared(entity);

    /// <summary>
    /// Storage-provider SPI, not for application code. Serializes <paramref name="entity"/> onto a
    /// primary row and its continuation rows, offloading values to blobs until the rows fit one
    /// entity-group transaction together with the deletes of the stored row's stale continuation rows.
    /// The existing continuation rows are asked for only when the stored row declares more than the new
    /// plan writes.
    /// </summary>
    /// <param name="entity">The entity to write (prepared with <see cref="PrepareAsync"/>).</param>
    /// <param name="partitionKey">Its partition key.</param>
    /// <param name="rowKey">Its row key.</param>
    /// <param name="storedPartCount">The part count of the stored row (0 when there is none).</param>
    /// <param name="existingPartRowKeys">Returns the row keys of the continuation rows that exist now.</param>
    /// <param name="ct">Cancels the part lookup.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="OversizedValueException">The entity cannot be stored within the limits.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async Task<LargeValueWritePlan> PlanAsync(
        object entity, string partitionKey, string rowKey, int storedPartCount,
        Func<CancellationToken, Task<IReadOnlyList<string>>> existingPartRowKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(existingPartRowKeys);
        var (row, plan, pending) = coordinator.Serialize(entity, partitionKey, rowKey, extraActions: 0);
        // A shrink deletes the continuation rows that actually exist beyond the new plan, found by the
        // caller rather than trusted from the part count, so a part already gone is never required.
        if (storedPartCount <= plan.Parts.Count)
            return new LargeValueWritePlan(row, plan, pending, [], storedPartCount);

        var existing = await existingPartRowKeys(ct);
        var stale = StaleParts(existing, plan);
        if (!SplitRowLayout.FitsOneTransaction(plan, stale.Count))
        {
            // Budget for deleting every existing part: the stale ones are a subset whatever the new plan.
            (row, plan, pending) = coordinator.Serialize(entity, partitionKey, rowKey, extraActions: existing.Count);
            stale = StaleParts(existing, plan);
        }
        return new LargeValueWritePlan(row, plan, pending, stale, storedPartCount);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Packs the plans of a batch, in order, into
    /// entity-group transactions, as a plain batch is packed: each plan (its primary, continuation rows and
    /// stale-part deletes) is indivisible, so an entity's rows never straddle two transactions; a
    /// transaction holds one partition only, at most <see cref="BatchPlanner.DefaultMaxCount"/> actions and
    /// <see cref="BatchPlanner.DefaultMaxBytes"/> of payload, and stays within the service's 4 MiB request
    /// limit as the SDK sends it (escaped text, base64 binary). Every plan from <see cref="PlanAsync"/>
    /// fits one transaction on its own.
    /// </summary>
    /// <param name="plans">The plans, grouped by partition by the caller (a change of partition starts a new transaction).</param>
    /// <returns>The transactions, as contiguous ranges over <paramref name="plans"/>.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IReadOnlyList<BatchRange> PlanTransactions(IReadOnlyList<LargeValueWritePlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        return SplitRowLayout.PackTransactions(plans.Select(p => (p.Plan, p.StalePartRowKeys.Count)).ToList());
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Makes every blob <paramref name="plan"/>
    /// references exist (create-only upload, or a touch of an identical one) and binds its contents.
    /// Call it before the row write; a failed row write must not delete these blobs.
    /// </summary>
    /// <param name="plan">The plan from <see cref="PlanAsync"/>.</param>
    /// <param name="ct">Cancels the uploads.</param>
    /// <returns>A task that completes when every blob exists.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Task UploadAsync(LargeValueWritePlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return coordinator.UploadAsync(plan.Pending, ct);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Deserializes a logical (assembled) row: its
    /// offloaded values downloaded and verified, its <see cref="TableBlobContent"/> values lazy.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="row">The logical row.</param>
    /// <param name="ct">Cancels downloads.</param>
    /// <returns>The entity.</returns>
    /// <exception cref="TableValueIntegrityException">A blob is missing, malformed, or fails verification.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async Task<T> ReadAsync<T>(TableEntity row, CancellationToken ct) where T : new()
    {
        ArgumentNullException.ThrowIfNull(row);
        var ctx = await coordinator.ResolveAsync(row, typeof(T), projectedColumns: null, ct);
        return TableEntitySerializer.FromTableEntity<T>(row, ctx);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Deletes the blobs the row
    /// (<paramref name="partitionKey"/>, <paramref name="rowKey"/>) no longer references, after the write
    /// or delete that released them has committed. Only the row's own blobs are deleted: names of the
    /// exact offload shape (<see cref="OffloadBlobNames.IsOffloadBlobName"/>) under
    /// <c>{table}/{partitionKey}/{rowKey}/</c> of this table. A row copied cell for cell from another
    /// table, or to another row key, still references the source row's blobs; such a path belongs to the
    /// source row, so it goes to <paramref name="onForeign"/> and is left alone. Each delete is
    /// conditional on the blob not having been modified within
    /// <see cref="BlobOffloadOptions.CleanupGrace"/> before <paramref name="serviceNow"/>, so a blob a
    /// concurrent writer has just touched survives. Best effort: a failure goes to
    /// <paramref name="onFailure"/>.
    /// </summary>
    /// <param name="partitionKey">The partition key of the row that was written or deleted.</param>
    /// <param name="rowKey">The row key of the row that was written or deleted.</param>
    /// <param name="paths">The blob names the row referenced and no longer does.</param>
    /// <param name="serviceNow">
    /// The storage service's time of the commit that released them (its response <c>Date</c>), or null
    /// to ask the blob store (<see cref="IOffloadBlobStore.GetServiceTimeAsync"/>); only when neither is
    /// known is the client clock used, less <see cref="ClientClockMargin"/>.
    /// </param>
    /// <param name="onForeign">Called with each path that is not the row's own blob; it is not deleted.</param>
    /// <param name="onFailure">
    /// Called with the blob name and the error of each failed delete; when cleanup stops after the caller
    /// cancelled, called once more, with the first blob not deleted and an
    /// <see cref="OperationCanceledException"/> that says how many are left.
    /// </param>
    /// <param name="ct">
    /// The caller's token. The change has committed, so cancelling it does not stop cleanup at once: it
    /// gets a few more seconds, then stops and leaves the remaining blobs to the orphan sweep. Never thrown.
    /// </param>
    /// <returns>A task that completes when every delete was attempted, or cleanup stopped.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async Task DeleteReleasedBlobsAsync(
        string partitionKey, string rowKey, IEnumerable<string> paths, DateTimeOffset? serviceNow,
        Action<string>? onForeign, Action<string, Exception>? onFailure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentNullException.ThrowIfNull(rowKey);
        ArgumentNullException.ThrowIfNull(paths);
        if (blobs is null)
            return;
        var released = OwnBlobs(partitionKey, rowKey, paths, onForeign);
        if (released.Count == 0)
            return;
        using var budget = new CleanupBudget(ct, CancelledCleanupBudget);
        var next = 0;
        try
        {
            var unmodifiedSince = (serviceNow ?? await ServiceNowAsync(budget.Token)) - cleanupGrace;
            for (; next < released.Count; next++)
                await DeleteBlobAsync(released[next], unmodifiedSince, onFailure, budget.Token);
        }
        catch (OperationCanceledException ex) when (budget.Token.IsCancellationRequested)
        {
            onFailure?.Invoke(released[next], CleanupStopped(released.Count - next, ex));
        }
    }

    // The paths that are blobs of the row itself: offload-shaped names under {table}/{pk}/{rk}/. Every
    // blob a write of the row uploads has such a name, so anything else was not written for this row: a
    // row copied raw from another table, or to another row key, still references the source row's
    // blobs. Those are the source's to release, so they are reported and never deleted here. A key that
    // is not well-formed UTF-16 names no blob at all (writes refuse one).
    private List<string> OwnBlobs(string partitionKey, string rowKey, IEnumerable<string> paths, Action<string>? onForeign)
    {
        string? partitionPrefix = null;
        string? rowPrefix = null;
        if (SplitCodec.IsWellFormedUtf16(partitionKey) && SplitCodec.IsWellFormedUtf16(rowKey))
        {
            partitionPrefix = ValueOffloadCoordinator.PartitionPrefix(tableName, partitionKey);
            rowPrefix = ValueOffloadCoordinator.RowPrefix(tableName, partitionKey, rowKey);
        }

        var own = new List<string>();
        foreach (var path in paths)
        {
            if (rowPrefix is not null
                && path.StartsWith(rowPrefix, StringComparison.Ordinal)
                && OffloadBlobNames.IsOffloadBlobName(path, partitionPrefix!))
                own.Add(path);
            else
                onForeign?.Invoke(path);
        }
        return own;
    }

    // Cleanup after a committed change ignores the caller's token until CancelledCleanupBudget has passed
    // since it was cancelled; an uncancelled caller's cleanup runs to the end, however long that takes.
    private sealed class CleanupBudget : IDisposable
    {
        private readonly CancellationTokenSource source = new();
        private readonly CancellationTokenRegistration registration;

        internal CleanupBudget(CancellationToken caller, TimeSpan budget) =>
            registration = caller.Register(static state =>
            {
                var (source, budget) = ((CancellationTokenSource, TimeSpan))state!;
                try
                {
                    source.CancelAfter(budget);
                }
                catch (ObjectDisposedException)
                {
                    // Cleanup already finished.
                }
            }, (source, budget));

        internal CancellationToken Token => source.Token;

        public void Dispose()
        {
            registration.Dispose();
            source.Dispose();
        }
    }

    private static OperationCanceledException CleanupStopped(int left, OperationCanceledException ex) =>
        new($"Blob cleanup stopped after the caller cancelled: {left} blob(s), this one included, are left for the orphan sweep.", ex, ex.CancellationToken);

    /// <summary>
    /// Storage-provider SPI, not for application code. After a partition's rows are deleted, deletes
    /// every blob stored under it, each on condition that it was modified neither within the cleanup
    /// grace nor since it was listed. Only names of the exact offload shape
    /// (<see cref="OffloadBlobNames.IsOffloadBlobName"/>) are deleted. Best effort: a failure goes to the callbacks.
    /// </summary>
    /// <param name="partitionKey">The deleted partition.</param>
    /// <param name="onListFailure">Called when the blobs cannot be listed.</param>
    /// <param name="onFailure">
    /// Called with the blob name and the error of each failed delete; when cleanup stops after the caller
    /// cancelled, called once more, with the first blob not deleted and an
    /// <see cref="OperationCanceledException"/> that says how many are left.
    /// </param>
    /// <param name="ct">
    /// The caller's token. The rows are deleted, so cancelling it does not stop cleanup at once: it gets a
    /// few more seconds, then stops and leaves the remaining blobs to the orphan sweep. Never thrown.
    /// </param>
    /// <returns>A task that completes when every delete was attempted, or cleanup stopped.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async Task DeletePartitionBlobsAsync(
        string partitionKey, Action<Exception>? onListFailure, Action<string, Exception>? onFailure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        // No blob is ever named after a key that is not well-formed UTF-16 (writes refuse one).
        if (blobs is null || !SplitCodec.IsWellFormedUtf16(partitionKey))
            return;
        var prefix = ValueOffloadCoordinator.PartitionPrefix(tableName, partitionKey);
        using var budget = new CleanupBudget(ct, CancelledCleanupBudget);
        var token = budget.Token;
        var doomed = new List<(string Name, DateTimeOffset UnmodifiedSince)>();
        var next = 0;
        try
        {
            // The service time before the listing: no later than any time the listing could report.
            var cutoff = await ServiceNowAsync(token) - cleanupGrace;
            try
            {
                await foreach (var (name, lastModified) in blobs.ListAsync(prefix, token).WithCancellation(token))
                {
                    if (OffloadBlobNames.IsOffloadBlobName(name, prefix))
                        doomed.Add((name, lastModified < cutoff ? lastModified : cutoff));
                }
            }
#pragma warning disable CA1031 // the rows are already deleted: a cleanup failure must not turn that into an error
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
#pragma warning restore CA1031
            {
                onListFailure?.Invoke(ex);
                return;
            }
            for (; next < doomed.Count; next++)
                await DeleteBlobAsync(doomed[next].Name, doomed[next].UnmodifiedSince, onFailure, token);
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            if (next < doomed.Count)
                onFailure?.Invoke(doomed[next].Name, CleanupStopped(doomed.Count - next, ex));
            else
                onListFailure?.Invoke(ex);
        }
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. The core of
    /// <see cref="IOrphanBlobSweeper.SweepOrphanBlobsAsync(string, TimeSpan, CancellationToken)"/>: deletes the blobs offloaded under
    /// <c>{table}/{partitionKey}/</c> that no row of the partition references and whose Last-Modified
    /// is older than <paramref name="olderThan"/>, each on condition that it was not modified since it
    /// was listed. The blobs are listed first; the rows are read (once, through
    /// <paramref name="partitionRows"/>) only when some blob is old enough. Rows are never changed.
    /// <see cref="BlobOffloadOptions.CleanupGrace"/> does not apply: <paramref name="olderThan"/> is the age.
    /// </summary>
    /// <param name="partitionKey">The partition, already normalized as the store stores it.</param>
    /// <param name="olderThan">The minimum age of a deleted blob; not negative.</param>
    /// <param name="entityType">The store's entity type (its large-value columns are always read).</param>
    /// <param name="partitionRows">
    /// Returns every stored row of the partition, continuation rows included, projected onto the given
    /// columns, or whole when the projection is null.
    /// </param>
    /// <param name="ct">Cancels the listing, the read and the deletes.</param>
    /// <returns>The number of blobs deleted; 0 when blob offload is not configured.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="partitionKey"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="olderThan"/> is negative.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Task<int> SweepOrphanBlobsAsync(
        string partitionKey, TimeSpan olderThan, Type entityType,
        Func<IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TableEntity>> partitionRows, CancellationToken ct)
    {
        ValidateSweep(partitionKey, olderThan);
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(partitionRows);
        // No blob is ever named after a key that is not well-formed UTF-16 (writes refuse one).
        if (blobs is null || !SplitCodec.IsWellFormedUtf16(partitionKey))
            return Task.FromResult(0);
        return SweepCoreAsync(partitionKey, olderThan, entityType, partitionRows, ct);
    }

    private async Task<int> SweepCoreAsync(
        string partitionKey, TimeSpan olderThan, Type entityType,
        Func<IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TableEntity>> partitionRows, CancellationToken ct)
    {
        var cutoff = await SweepCutoffAsync(olderThan, ct);
        return await coordinator.SweepAsync(partitionKey, cutoff, TypeMetadataCache.LargeValueColumns(entityType), partitionRows, ct);
    }

    // Ages are measured on the service clock, read before the listing (so never later than it).
    private async Task<DateTimeOffset> SweepCutoffAsync(TimeSpan olderThan, CancellationToken ct)
    {
        var now = await ServiceNowAsync(ct);
        // An age beyond the start of the calendar makes no blob old enough (instead of overflowing).
        return olderThan >= now - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : now - olderThan;
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. The core of
    /// <see cref="IOrphanBlobSweeper.SweepOrphanBlobsAsync(TimeSpan, CancellationToken)"/>: lists the
    /// partitions that hold blobs (<c>{table}/</c> with the delimiter <c>/</c>), unescapes each listed
    /// partition segment exactly once (a segment this library does not write is skipped), and sweeps
    /// each partition as the partition sweep does, with one cutoff for the whole table taken from the
    /// service clock before the listing. A partition that fails does not stop the others: once every
    /// partition was tried, the failures are thrown together as an <see cref="AggregateException"/>.
    /// Cancellation is not a partition failure: it ends the sweep.
    /// </summary>
    /// <param name="olderThan">The minimum age of a deleted blob; not negative.</param>
    /// <param name="entityType">The store's entity type (its large-value columns are always read).</param>
    /// <param name="partitionRows">
    /// Returns every stored row of the given (stored) partition, continuation rows included, projected
    /// onto the given columns, or whole when the projection is null.
    /// </param>
    /// <param name="ct">Cancels the listings, the reads and the deletes.</param>
    /// <returns>The number of blobs deleted; 0 when blob offload is not configured.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="olderThan"/> is negative.</exception>
    /// <exception cref="AggregateException">
    /// One or more partitions could not be swept (one inner exception each); the message says how many
    /// blobs the other partitions' sweeps deleted.
    /// </exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Task<int> SweepTableOrphanBlobsAsync(
        TimeSpan olderThan, Type entityType,
        Func<string, IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TableEntity>> partitionRows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(partitionRows);
        if (blobs is null)
            return Task.FromResult(0);
        return SweepTableCoreAsync(olderThan, entityType, partitionRows, ct);
    }

    private async Task<int> SweepTableCoreAsync(
        TimeSpan olderThan, Type entityType,
        Func<string, IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TableEntity>> partitionRows, CancellationToken ct)
    {
        var cutoff = await SweepCutoffAsync(olderThan, ct);
        var tablePrefix = ValueOffloadCoordinator.TablePrefix(tableName);
        var partitions = new List<string>();
        await foreach (var listed in blobs!.ListPrefixesAsync(tablePrefix, ct).WithCancellation(ct))
        {
            // {table}/{segment}/: the segment is unescaped exactly once, and only when this library wrote
            // it, so the partition sweep re-escapes it to the listed prefix (never to a double-escaped one).
            if (listed.Length > tablePrefix.Length + 1
                && listed.StartsWith(tablePrefix, StringComparison.Ordinal)
                && listed[^1] == '/'
                && OffloadBlobNames.TryUnescapeSegment(listed[tablePrefix.Length..^1], out var partition))
                partitions.Add(partition);
        }

        var columns = TypeMetadataCache.LargeValueColumns(entityType);
        var deleted = 0;
        List<Exception>? failures = null;
        foreach (var partition in partitions)
        {
            try
            {
                deleted += await coordinator.SweepAsync(partition, cutoff, columns, (select, c) => partitionRows(partition, select, c), ct);
            }
#pragma warning disable CA1031 // one partition's failure must not stop the others; every failure is rethrown below
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
#pragma warning restore CA1031
            {
                (failures ??= []).Add(ex);
            }
        }
        if (failures is not null)
            throw new AggregateException(
                $"The orphan sweep of table '{tableName}' deleted {deleted} blob(s), but {failures.Count} of {partitions.Count} partition(s) could not be swept.",
                failures);
        return deleted;
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. The argument checks of
    /// <see cref="IOrphanBlobSweeper.SweepOrphanBlobsAsync(string, TimeSpan, CancellationToken)"/>, for a store to run before it normalizes the partition.
    /// </summary>
    /// <param name="partitionKey">The partition argument.</param>
    /// <param name="olderThan">The age argument.</param>
    /// <exception cref="ArgumentNullException"><paramref name="partitionKey"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="olderThan"/> is negative.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void ValidateSweep(string partitionKey, TimeSpan olderThan)
    {
        if (string.IsNullOrWhiteSpace(partitionKey))
            throw new ArgumentNullException(nameof(partitionKey));
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero);
    }

    // "Now" on the clock that stamps blob Last-Modified times: the blob store's service time or, when it
    // cannot tell, the client clock moved back by ClientClockMargin (it may run ahead of the service).
    // Blobs modified after (now - grace) may be about to be referenced by a concurrent write: kept.
    private async Task<DateTimeOffset> ServiceNowAsync(CancellationToken ct)
    {
        try
        {
            if (await blobs!.GetServiceTimeAsync(ct) is { } serviceNow)
                return serviceNow;
        }
#pragma warning disable CA1031 // an unknown service time falls back to the (margined) client clock
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
        }
        var client = Clock.GetUtcNow();
        return client - DateTimeOffset.MinValue > ClientClockMargin ? client - ClientClockMargin : DateTimeOffset.MinValue;
    }

    // A cancelled cleanup is rethrown (the caller stops and reports the rest once); any other failure is reported.
    private async Task DeleteBlobAsync(string path, DateTimeOffset? unmodifiedSince, Action<string, Exception>? onFailure, CancellationToken ct)
    {
        try
        {
            await blobs!.DeleteIfUnmodifiedAsync(path, unmodifiedSince, ct);
        }
#pragma warning disable CA1031 // the row change already committed: a cleanup failure must not turn it into an error
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
#pragma warning restore CA1031
        {
            onFailure?.Invoke(path, ex);
        }
    }

    // The existing continuation rows the new plan no longer writes.
    private static List<string> StaleParts(IReadOnlyList<string> existing, SplitRowPlan plan)
    {
        var kept = new HashSet<string>(plan.Parts.Select(p => p.RowKey), StringComparer.Ordinal);
        return existing.Where(rk => !kept.Contains(rk)).ToList();
    }
}

/// <summary>
/// Storage-provider SPI, not for application code: the rows one large-value write puts in a single
/// atomic step (the primary, its continuation rows, and the deletes of stale continuation rows) and the
/// blobs the new row references. Built by <see cref="LargeValueTable.PlanAsync"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class LargeValueWritePlan
{
    internal LargeValueWritePlan(TableEntity logical, SplitRowPlan plan, IReadOnlyList<PendingBlob> pending, IReadOnlyList<string> stale, int storedPartCount)
    {
        StoredPartCount = Math.Max(0, storedPartCount);
        Primary = plan.Primary;
        Parts = plan.Parts;
        StalePartRowKeys = stale;
        BlobPaths = LargeValueTable.BlobPaths(logical);
        Pending = pending;
        Plan = plan;
    }

    /// <summary>Storage-provider SPI, not for application code. The primary row to insert or replace.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TableEntity Primary { get; }

    /// <summary>
    /// Storage-provider SPI, not for application code. The continuation rows to write, in index order:
    /// those the stored row declares are replaced, the others (<see cref="IsNewPart"/>) inserted.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IReadOnlyList<TableEntity> Parts { get; }

    /// <summary>
    /// Storage-provider SPI, not for application code. The number of continuation rows the stored row
    /// declared when the plan was made (0 when there was none): the entity's own rows.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public int StoredPartCount { get; }

    /// <summary>
    /// Storage-provider SPI, not for application code. True when the part at <paramref name="position"/>
    /// in <see cref="Parts"/> is beyond what the stored row declares: it must be inserted, never replace a
    /// row already at its key (that row is not the entity's: <see cref="LargeValueTable.ContinuationKeyTaken"/>).
    /// </summary>
    /// <param name="position">The zero-based position in <see cref="Parts"/> (the part's index less one).</param>
    /// <returns>Whether the part is new.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool IsNewPart(int position) => position + 1 > StoredPartCount;

    /// <summary>Storage-provider SPI, not for application code. Row keys of the existing continuation rows to delete.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IReadOnlyList<string> StalePartRowKeys { get; }

    /// <summary>Storage-provider SPI, not for application code. The blob names the new row references.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IReadOnlyList<string> BlobPaths { get; }

    internal IReadOnlyList<PendingBlob> Pending { get; }

    internal SplitRowPlan Plan { get; }
}
