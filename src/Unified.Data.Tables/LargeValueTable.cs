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
    /// <exception cref="ArgumentException">The key contains <c>'~'</c> or is too long.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void ValidateRowKey(string rowKey)
    {
        ArgumentNullException.ThrowIfNull(rowKey);
        SplitRowLayout.ValidateRowKey(rowKey);
    }

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
    /// malformed reference cannot name a blob and must not block a write that replaces it: it is
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
            return new LargeValueWritePlan(row, plan, pending, []);

        var existing = await existingPartRowKeys(ct);
        var stale = StaleParts(existing, plan);
        if (!SplitRowLayout.FitsOneTransaction(plan, stale.Count))
        {
            // Budget for deleting every existing part: the stale ones are a subset whatever the new plan.
            (row, plan, pending) = coordinator.Serialize(entity, partitionKey, rowKey, extraActions: existing.Count);
            stale = StaleParts(existing, plan);
        }
        return new LargeValueWritePlan(row, plan, pending, stale);
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
    /// Storage-provider SPI, not for application code. Deletes blobs no row references any more, after
    /// the write or delete that released them has committed. Each delete is conditional on the blob not
    /// having been modified within <see cref="BlobOffloadOptions.CleanupGrace"/> before
    /// <paramref name="serviceNow"/>, so a blob a concurrent writer has just touched survives. Best
    /// effort: a failure goes to <paramref name="onFailure"/>.
    /// </summary>
    /// <param name="paths">The released blob names.</param>
    /// <param name="serviceNow">
    /// The storage service's time of the commit that released them (its response <c>Date</c>), or null
    /// to ask the blob store (<see cref="IOffloadBlobStore.GetServiceTimeAsync"/>); only when neither is
    /// known is the client clock used, less <see cref="ClientClockMargin"/>.
    /// </param>
    /// <param name="onFailure">Called with the blob name and the error of each failed delete.</param>
    /// <param name="ct">Cancels the deletes.</param>
    /// <returns>A task that completes when every delete was attempted.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async Task DeleteReleasedBlobsAsync(
        IEnumerable<string> paths, DateTimeOffset? serviceNow, Action<string, Exception>? onFailure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (blobs is null)
            return;
        var released = paths.ToList();
        if (released.Count == 0)
            return;
        var unmodifiedSince = (serviceNow ?? await ServiceNowAsync(ct)) - cleanupGrace;
        foreach (var path in released)
            await DeleteBlobAsync(path, unmodifiedSince, onFailure, ct);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. After a partition's rows are deleted, deletes
    /// every blob stored under it, each on condition that it was modified neither within the cleanup
    /// grace nor since it was listed. Best effort: a failure goes to the callbacks.
    /// </summary>
    /// <param name="partitionKey">The deleted partition.</param>
    /// <param name="onListFailure">Called when the blobs cannot be listed.</param>
    /// <param name="onFailure">Called with the blob name and the error of each failed delete.</param>
    /// <param name="ct">Cancels the listing and the deletes.</param>
    /// <returns>A task that completes when every delete was attempted.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async Task DeletePartitionBlobsAsync(
        string partitionKey, Action<Exception>? onListFailure, Action<string, Exception>? onFailure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        if (blobs is null)
            return;
        var prefix = ValueOffloadCoordinator.PartitionPrefix(tableName, partitionKey);
        // The service time before the listing: no later than any time the listing could report.
        var cutoff = await ServiceNowAsync(ct) - cleanupGrace;
        var listed = new List<(string Name, DateTimeOffset LastModified)>();
        try
        {
            await foreach (var blob in blobs.ListAsync(prefix, ct))
                listed.Add(blob);
        }
#pragma warning disable CA1031 // the rows are already deleted: a cleanup failure must not turn that into an error
        catch (Exception ex)
#pragma warning restore CA1031
        {
            onListFailure?.Invoke(ex);
            return;
        }
        foreach (var (name, lastModified) in listed)
            await DeleteBlobAsync(name, lastModified < cutoff ? lastModified : cutoff, onFailure, ct);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. The core of
    /// <see cref="IStorage{T}.SweepOrphanBlobsAsync"/>: deletes the blobs offloaded under
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
        if (blobs is null)
            return Task.FromResult(0);
        return SweepCoreAsync(partitionKey, olderThan, entityType, partitionRows, ct);
    }

    private async Task<int> SweepCoreAsync(
        string partitionKey, TimeSpan olderThan, Type entityType,
        Func<IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TableEntity>> partitionRows, CancellationToken ct)
    {
        // Ages are measured on the service clock, read before the listing (so never later than it).
        var now = await ServiceNowAsync(ct);
        // An age beyond the start of the calendar makes no blob old enough (instead of overflowing).
        var cutoff = olderThan >= now - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : now - olderThan;
        return await coordinator.SweepAsync(partitionKey, cutoff, TypeMetadataCache.LargeValueColumns(entityType), partitionRows, ct);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. The argument checks of
    /// <see cref="IStorage{T}.SweepOrphanBlobsAsync"/>, for a store to run before it normalizes the partition.
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

    private async Task DeleteBlobAsync(string path, DateTimeOffset? unmodifiedSince, Action<string, Exception>? onFailure, CancellationToken ct)
    {
        try
        {
            await blobs!.DeleteIfUnmodifiedAsync(path, unmodifiedSince, ct);
        }
#pragma warning disable CA1031 // the row change already committed: a cleanup failure must not turn it into an error
        catch (Exception ex)
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
    internal LargeValueWritePlan(TableEntity logical, SplitRowPlan plan, IReadOnlyList<PendingBlob> pending, IReadOnlyList<string> stale)
    {
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

    /// <summary>Storage-provider SPI, not for application code. The continuation rows to upsert, in index order.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IReadOnlyList<TableEntity> Parts { get; }

    /// <summary>Storage-provider SPI, not for application code. Row keys of the existing continuation rows to delete.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IReadOnlyList<string> StalePartRowKeys { get; }

    /// <summary>Storage-provider SPI, not for application code. The blob names the new row references.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IReadOnlyList<string> BlobPaths { get; }

    internal IReadOnlyList<PendingBlob> Pending { get; }

    internal SplitRowPlan Plan { get; }
}
