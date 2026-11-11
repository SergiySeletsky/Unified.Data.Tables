using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
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
    /// Storage-provider SPI, not for application code. How many times in all a write, a delete or a batch
    /// transaction of a large-value type is attempted when it keeps losing its condition to concurrent
    /// writers, before it throws <see cref="ConcurrencyConflictException"/>. Every provider uses this
    /// number.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public const int MaxWriteAttempts = 5;

    /// <summary>The pause before the first retry; it doubles with every further one.</summary>
    internal static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>The longest pause before a retry.</summary>
    internal static readonly TimeSpan RetryMaxDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Waits between two attempts (replaced by tests).</summary>
    internal Func<TimeSpan, CancellationToken, Task> RetryDelay { get; set; } = Task.Delay;

    /// <summary>
    /// Storage-provider SPI, not for application code. Waits before attempt <paramref name="attempt"/> of
    /// a write, delete or batch transaction that lost a race to a concurrent writer: about
    /// 10&#160;ms before the first retry, doubling with each further one and never more than
    /// 250&#160;ms, each pause a random length between half of that and all of it, so that writers that
    /// collided do not collide again in step.
    /// </summary>
    /// <param name="attempt">The attempt about to be made: 2 for the first retry.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task that completes when the attempt may be made.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempt"/> is less than 2.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled during the wait.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Task WaitBeforeRetryAsync(int attempt, CancellationToken ct) => RetryDelay(RetryBackoff(attempt), ct);

    /// <summary>
    /// The pause before attempt <paramref name="attempt"/> (2 = the first retry): a random length between
    /// half of and the whole of <see cref="RetryBaseDelay"/> doubled once per earlier retry, the whole
    /// capped at <see cref="RetryMaxDelay"/>.
    /// </summary>
    internal static TimeSpan RetryBackoff(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 2);
        var ceiling = (int)Math.Min(RetryMaxDelay.TotalMilliseconds, RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 2, 30)));
        // Not a secret: the generator is only the one at hand that needs no instance and no lock.
        return TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(ceiling / 2, ceiling + 1));
    }

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
    /// row at a key a foreign row already holds (one written before the type opted in, or by a type sharing
    /// the table): the write is refused rather than overwrite that row. A row there that is a continuation
    /// row of the same entity (its <c>_SplitOf</c> cell names the entity's row key) is not foreign: it is
    /// the entity's own, left behind, and the write takes it back (<see cref="PlanOverOwnParts"/>).
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

    // The projection of the point read before a write or delete, per entity type, built on first use (by
    // an operation, never by a store's type initializer: listing the columns may construct the entity).
    private static readonly ConcurrentDictionary<Type, PriorRowProjection> PriorRowProjections = new();

    private sealed record PriorRowProjection(string[]? Select, HashSet<string>? ReferenceColumns);

    /// <summary>
    /// Storage-provider SPI, not for application code. The <c>$select</c> of the point read that precedes
    /// an upsert, a replace, a delete and a batch upsert of an existing row of
    /// <paramref name="entityType"/>: the part count and continuation marker cells and one
    /// <c>{column}__blob</c> cell per large-value column of the type (legacy alias columns included). When
    /// that list would name more columns, or be longer as a URL-encoded <c>$select</c> value, than the
    /// orphan sweep allows itself, the row is read whole instead (null), so the read always stays within
    /// the service's projection and URL limits. Computed on first use and kept.
    /// </summary>
    /// <param name="entityType">The store's entity type.</param>
    /// <returns>The columns to select, or null to read the row whole.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IReadOnlyList<string>? PriorRowSelect(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return PriorRowProjectionOf(entityType).Select;
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. The blob names the point read before a write or
    /// delete learns from the stored <paramref name="row"/>: the references in the cells
    /// <see cref="PriorRowSelect"/> selects, or in every cell when it reads the row whole. A provider that
    /// holds the whole row passes it as it is and releases the same blobs as one that reads the
    /// projection; a reference in any other cell is left for the orphan sweep. Null and malformed cells
    /// are handled as <see cref="BlobPaths"/> handles them.
    /// </summary>
    /// <param name="entityType">The store's entity type.</param>
    /// <param name="row">The stored primary row, whole or projected onto <see cref="PriorRowSelect"/>.</param>
    /// <param name="onMalformed">Called with the column and the error of each malformed reference.</param>
    /// <returns>The referenced blob names.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IReadOnlyList<string> PriorRowBlobPaths(
        Type entityType, TableEntity row, Action<string, TableValueIntegrityException>? onMalformed = null)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(row);
        var selected = PriorRowProjectionOf(entityType).ReferenceColumns;
        var paths = new List<string>();
        foreach (var (column, cell) in ValueOffloadCoordinator.ReferenceCells(row))
        {
            if (selected is not null && !selected.Contains(column))
                continue;
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

    private static PriorRowProjection PriorRowProjectionOf(Type entityType) =>
        PriorRowProjections.GetOrAdd(entityType, static type =>
        {
            var columns = TypeMetadataCache.LargeValueColumns(type);
            if (columns.Count > ValueOffloadCoordinator.MaxSweepSelectColumns)
                return new PriorRowProjection(null, null);
            // The version is not a column: the service returns it with every entity (and may refuse a
            // $select that names odata.etag).
            string[] select =
            [
                SystemColumnNames.SplitParts,
                SystemColumnNames.SplitOf,
                .. columns.Select(c => c + SplitCodec.BlobSuffix),
            ];
            return ValueOffloadCoordinator.SelectLength(select) > ValueOffloadCoordinator.MaxSweepSelectLength
                ? new PriorRowProjection(null, null)
                : new PriorRowProjection(select, new HashSet<string>(columns, StringComparer.Ordinal));
        });

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
    /// plan writes. A continuation row beyond what the stored row declares is planned as new
    /// (<see cref="LargeValueWritePlan.IsNewPart"/>): when the provider finds its key held by a
    /// continuation row of the same entity, it plans again with <see cref="PlanOverOwnParts"/>.
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
            return new LargeValueWritePlan(row, plan, pending, [], storedPartCount, []);

        var existing = await existingPartRowKeys(ct);
        var stale = StaleParts(existing, plan);
        if (!SplitRowLayout.FitsOneTransaction(plan, stale.Count))
        {
            // Budget for deleting every existing part: the stale ones are a subset whatever the new plan.
            (row, plan, pending) = coordinator.Serialize(entity, partitionKey, rowKey, extraActions: existing.Count);
            stale = StaleParts(existing, plan);
        }
        return new LargeValueWritePlan(row, plan, pending, stale, storedPartCount, []);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Plans a write as <see cref="PlanAsync"/> does, over
    /// the continuation rows of the entity that exist now, whether its stored row declares them or not. A
    /// continuation row whose <c>_SplitOf</c> cell names the entity is the entity's own even when no stored
    /// row declares it: it was left behind by a writer that does not know split values, by a partition
    /// delete that stopped halfway, or by a delete that raced a write. The plan writes over the ones the
    /// new value needs (<see cref="LargeValueWritePlan.IsNewPart"/> is false for them) and deletes all
    /// the others (<see cref="LargeValueWritePlan.StalePartRowKeys"/>) in the same atomic step, so no
    /// stray row remains. A provider uses it once a plan from <see cref="PlanAsync"/> found the key of a
    /// new continuation row held by such a row, and applies the plan only while those rows are still
    /// the rows it was given (a row at such a key that is not the entity's is foreign:
    /// <see cref="ContinuationKeyTaken"/>).
    /// </summary>
    /// <param name="entity">The entity to write (prepared with <see cref="PrepareAsync"/>).</param>
    /// <param name="partitionKey">Its partition key.</param>
    /// <param name="rowKey">Its row key.</param>
    /// <param name="storedPartCount">The part count of the stored row (0 when there is none).</param>
    /// <param name="ownPartRowKeys">
    /// The row keys of every continuation row of the entity that exists now: the rows for which
    /// <see cref="IsPartOf"/> is true.
    /// </param>
    /// <returns>The plan.</returns>
    /// <exception cref="OversizedValueException">The entity cannot be stored within the limits.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public LargeValueWritePlan PlanOverOwnParts(
        object entity, string partitionKey, string rowKey, int storedPartCount, IReadOnlyCollection<string> ownPartRowKeys)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(ownPartRowKeys);
        var (row, plan, pending) = coordinator.Serialize(entity, partitionKey, rowKey, extraActions: 0);
        var stale = StaleParts(ownPartRowKeys, plan);
        if (!SplitRowLayout.FitsOneTransaction(plan, stale.Count))
        {
            // Budget for deleting every existing part: the stale ones are a subset whatever the new plan.
            (row, plan, pending) = coordinator.Serialize(entity, partitionKey, rowKey, extraActions: ownPartRowKeys.Count);
            stale = StaleParts(ownPartRowKeys, plan);
        }
        return new LargeValueWritePlan(row, plan, pending, stale, storedPartCount, ownPartRowKeys);
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
    /// concurrent writer has just touched survives; a grace that reaches back beyond the start of the
    /// calendar (<see cref="TimeSpan.MaxValue"/>) makes no blob old enough, so nothing is deleted. Best
    /// effort: a failure goes to <paramref name="onFailure"/>.
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
        if (blobs is null || NeverCleansUp)
            return;
        var released = OwnBlobs(partitionKey, rowKey, paths, onForeign);
        if (released.Count == 0)
            return;
        using var budget = new CleanupBudget(ct, CancelledCleanupBudget);
        var next = 0;
        try
        {
            var unmodifiedSince = Cutoff(serviceNow ?? await ServiceNowAsync(budget.Token), cleanupGrace);
            // No blob is old enough: none is asked for (rather than sent a condition no blob can meet).
            if (unmodifiedSince == DateTimeOffset.MinValue)
                return;
            for (; next < released.Count; next++)
                await DeleteBlobAsync(released[next], unmodifiedSince, onFailure, budget.Token);
        }
        catch (OperationCanceledException ex) when (budget.Token.IsCancellationRequested)
        {
            onFailure?.Invoke(released[next], CleanupStopped(released.Count - next, ex));
        }
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. The time of a commit that released blobs, on the
    /// clock cleanup measures their age with, for a cleanup that runs later
    /// (<see cref="DeleteReleasedBlobsAsync"/>, <c>serviceNow</c>): a batch deletes the blobs its
    /// transactions released only after its last transaction has committed, and the grace of each is
    /// counted from the commit that released it. Null when this table never deletes a released blob.
    /// </summary>
    /// <param name="ct">Cancels the read of the blob store's clock.</param>
    /// <returns>The blob store's time now (the client clock less <see cref="ClientClockMargin"/> when it cannot tell), or null.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async Task<DateTimeOffset?> CommitTimeAsync(CancellationToken ct) =>
        blobs is null || NeverCleansUp ? null : await ServiceNowAsync(ct);

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
    /// Storage-provider SPI, not for application code. Packs the rows a partition delete listed, in the
    /// order listed, into entity-group transactions of at most <see cref="BatchPlanner.DefaultMaxCount"/>
    /// deletes. A primary row and its continuation rows (the rows whose <c>_SplitOf</c> cell is its row
    /// key) are one unit, never divided between two transactions, so a failure or a cancellation between
    /// two transactions leaves no continuation row without its primary. Continuation rows whose primary
    /// is not among <paramref name="rows"/> are a unit of their own. An entity has at most 99
    /// continuation rows, so its unit always fits one transaction; a larger group, which only rows this
    /// library did not write can form, is divided, its continuation rows first and its primary last.
    /// </summary>
    /// <param name="rows">
    /// The rows of one partition, each with its keys and, when it is a continuation row, its
    /// <c>_SplitOf</c> cell (a null cell, as a projection returns it for a row without one, is not one).
    /// </param>
    /// <returns>The transactions, in order; each row is in exactly one of them.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IReadOnlyList<IReadOnlyList<TableEntity>> PlanPartitionDelete(IReadOnlyList<TableEntity> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        const int max = BatchPlanner.DefaultMaxCount;

        // The units, in the order their first row was listed. A unit's rows need not be adjacent in the
        // listing: other rows can sort between a primary "a" and its continuation row "a~0001".
        var units = new List<List<TableEntity>>();
        var unitOf = new Dictionary<string, List<TableEntity>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            var key = PrimaryKeyOf(row) ?? row.RowKey ?? string.Empty;
            if (!unitOf.TryGetValue(key, out var unit))
            {
                unitOf[key] = unit = [];
                units.Add(unit);
            }
            unit.Add(row);
        }

        var transactions = new List<IReadOnlyList<TableEntity>>();
        var current = new List<TableEntity>();
        foreach (var unit in units)
        {
            if (unit.Count > max)
            {
                // More rows name this primary than a transaction holds: they cannot go together. The
                // continuation rows go first and the primary last, so an interrupted delete leaves a
                // primary that still deletes cleanly rather than continuation rows nothing owns.
                Flush();
                foreach (var chunk in unit.Where(r => PrimaryKeyOf(r) is not null).Concat(unit.Where(r => PrimaryKeyOf(r) is null)).Chunk(max))
                    transactions.Add(chunk);
                continue;
            }
            if (current.Count + unit.Count > max)
                Flush();
            current.AddRange(unit);
        }
        Flush();
        return transactions;

        void Flush()
        {
            if (current.Count == 0)
                return;
            transactions.Add(current);
            current = [];
        }
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Separates, from the rows a partition delete
    /// listed, the continuation rows listed without their primary: a leftover, or the rows of an entity
    /// written while the listing was read page by page, whose primary sorts before a page boundary and
    /// its continuation rows after it. Those may belong to an entity that is live, so they are not
    /// deleted with the others: each group (the rows that name one primary) is deleted only when no
    /// primary exists for it after the listing, and each row only while it is still the row listed.
    /// </summary>
    /// <param name="rows">The rows of one partition, as for <see cref="PlanPartitionDelete"/>.</param>
    /// <returns>
    /// <c>Whole</c>: the other rows, in the order listed (<paramref name="rows"/> itself when there is no
    /// such group), to be planned with <see cref="PlanPartitionDelete"/>. <c>Leftovers</c>: the groups,
    /// in the order their first row was listed, each with the row key of the primary its rows name.
    /// </returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static (IReadOnlyList<TableEntity> Whole, IReadOnlyList<(string PrimaryRowKey, IReadOnlyList<TableEntity> Rows)> Leftovers)
        SeparateListedLeftovers(IReadOnlyList<TableEntity> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        // The row keys of the listed rows that are not continuation rows: the primaries the listing holds.
        var primaries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (PrimaryKeyOf(row) is null)
                primaries.Add(row.RowKey ?? string.Empty);
        }

        var groups = new List<(string PrimaryRowKey, IReadOnlyList<TableEntity> Rows)>();
        var groupOf = new Dictionary<string, List<TableEntity>>(StringComparer.Ordinal);
        List<TableEntity>? whole = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (PrimaryKeyOf(row) is not { } primary || primaries.Contains(primary))
            {
                whole?.Add(row);
                continue;
            }
            whole ??= [.. rows.Take(i)];
            if (!groupOf.TryGetValue(primary, out var group))
            {
                groupOf[primary] = group = [];
                groups.Add((primary, group));
            }
            group.Add(row);
        }
        return (whole ?? rows, groups);
    }

    // The row key of the primary a continuation row belongs to (its _SplitOf cell); null for any other row.
    private static string? PrimaryKeyOf(TableEntity row) =>
        row.TryGetValue(SystemColumnNames.SplitOf, out var of) ? of as string : null;

    /// <summary>
    /// Storage-provider SPI, not for application code. After a partition's rows are deleted, deletes the
    /// blobs stored under it that no row of the partition references NOW: a row another writer created
    /// while the partition was being deleted survives the delete, and keeps its blobs however long the
    /// delete took. A blob is a candidate when it was not modified within
    /// <see cref="BlobOffloadOptions.CleanupGrace"/> (younger ones are left for the orphan sweep; a grace
    /// that reaches back beyond the start of the calendar leaves them all) and its name has the exact
    /// offload shape (<see cref="OffloadBlobNames.IsOffloadBlobName"/>). The blobs are listed first; the
    /// rows are read (once, through <paramref name="partitionRows"/>) only when some blob is old enough;
    /// each delete is conditional on the blob not having been modified since it was listed. Rows are
    /// never changed. Best effort: a failure goes to the callbacks.
    /// </summary>
    /// <param name="partitionKey">The deleted partition, already normalized as the store stores it.</param>
    /// <param name="entityType">The store's entity type (its large-value columns are always read).</param>
    /// <param name="partitionRows">
    /// Returns every stored row that is in the partition now, continuation rows included, projected onto
    /// the given columns, or whole when the projection is null. Called at most once, with the cleanup's
    /// own token (not the caller's, which may be cancelled already).
    /// </param>
    /// <param name="onListFailure">
    /// Called when the blobs cannot be listed or the rows cannot be read: nothing is deleted then.
    /// </param>
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
        string partitionKey, Type entityType,
        Func<IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TableEntity>> partitionRows,
        Action<Exception>? onListFailure, Action<string, Exception>? onFailure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(partitionRows);
        // No blob is ever named after a key that is not well-formed UTF-16 (writes refuse one).
        if (blobs is null || NeverCleansUp || !SplitCodec.IsWellFormedUtf16(partitionKey))
            return;
        using var budget = new CleanupBudget(ct, CancelledCleanupBudget);
        var token = budget.Token;
        IReadOnlyList<(string Name, DateTimeOffset LastModified)> orphans = [];
        var next = 0;
        try
        {
            // The service time before the listing: no later than any time the listing could report.
            var cutoff = Cutoff(await ServiceNowAsync(token), cleanupGrace);
            // No blob is old enough: nothing is listed or asked for.
            if (cutoff == DateTimeOffset.MinValue)
                return;
            try
            {
                // Never "every blob under the prefix": a row that is in the partition now (one a
                // concurrent writer created after the delete listed its rows) keeps what it references.
                orphans = await coordinator.OrphansAsync(
                    partitionKey, cutoff, includeCutoff: true, TypeMetadataCache.LargeValueColumns(entityType), partitionRows, token);
            }
#pragma warning disable CA1031 // the rows are already deleted: a cleanup failure must not turn that into an error
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
#pragma warning restore CA1031
            {
                onListFailure?.Invoke(ex);
                return;
            }
            for (; next < orphans.Count; next++)
                await DeleteBlobAsync(orphans[next].Name, orphans[next].LastModified, onFailure, token);
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            if (next < orphans.Count)
                onFailure?.Invoke(orphans[next].Name, CleanupStopped(orphans.Count - next, ex));
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
    private async Task<DateTimeOffset> SweepCutoffAsync(TimeSpan olderThan, CancellationToken ct) =>
        Cutoff(await ServiceNowAsync(ct), olderThan);

    // The instant before which a blob is old enough for a cleanup or a sweep: now less the age. An age
    // that reaches back beyond the start of the calendar makes no blob old enough
    // (DateTimeOffset.MinValue) instead of overflowing the subtraction. Every cutoff is computed here.
    private static DateTimeOffset Cutoff(DateTimeOffset now, TimeSpan age) =>
        age >= now - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : now - age;

    // A cleanup grace longer than the calendar ("never clean up after a write", TimeSpan.MaxValue): no
    // blob can be that old whatever the time is, so the service is not even asked for it.
    private bool NeverCleansUp => cleanupGrace >= DateTimeOffset.MaxValue - DateTimeOffset.MinValue;

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
    private static List<string> StaleParts(IEnumerable<string> existing, SplitRowPlan plan)
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
    // The continuation rows of the entity that existed when the plan was made over them (PlanOverOwnParts).
    private readonly HashSet<string> ownParts;

    internal LargeValueWritePlan(
        TableEntity logical, SplitRowPlan plan, IReadOnlyList<PendingBlob> pending, IReadOnlyList<string> stale,
        int storedPartCount, IEnumerable<string> ownPartRowKeys)
    {
        StoredPartCount = Math.Max(0, storedPartCount);
        Primary = plan.Primary;
        Parts = plan.Parts;
        StalePartRowKeys = stale;
        BlobPaths = LargeValueTable.BlobPaths(logical);
        Pending = pending;
        Plan = plan;
        ownParts = new HashSet<string>(ownPartRowKeys, StringComparer.Ordinal);
    }

    /// <summary>Storage-provider SPI, not for application code. The primary row to insert or replace.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TableEntity Primary { get; }

    /// <summary>
    /// Storage-provider SPI, not for application code. The continuation rows to write, in index order:
    /// those the stored row declares, and the entity's own rows the plan was made over, are replaced; the
    /// others (<see cref="IsNewPart"/>) are inserted.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IReadOnlyList<TableEntity> Parts { get; }

    /// <summary>
    /// Storage-provider SPI, not for application code. The number of continuation rows the stored row
    /// declared when the plan was made (0 when there was none). They are the entity's own rows; so are
    /// the continuation rows a plan from <see cref="LargeValueTable.PlanOverOwnParts"/> was made over.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public int StoredPartCount { get; }

    /// <summary>
    /// Storage-provider SPI, not for application code. True when the part at <paramref name="position"/>
    /// in <see cref="Parts"/> is beyond what the stored row declares and is not one of the entity's own
    /// continuation rows the plan was made over: it must be inserted, never replace a row already at its
    /// key. When a row is there, the provider reads the entity's own continuation rows: if that row is one
    /// of them it plans again over them (<see cref="LargeValueTable.PlanOverOwnParts"/>), otherwise the
    /// row is foreign (<see cref="LargeValueTable.ContinuationKeyTaken"/>).
    /// </summary>
    /// <param name="position">The zero-based position in <see cref="Parts"/> (the part's index less one).</param>
    /// <returns>Whether the part is new.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is not a position in <see cref="Parts"/>.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool IsNewPart(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(position, Parts.Count);
        return position + 1 > StoredPartCount && !ownParts.Contains(Parts[position].RowKey);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Row keys of the existing continuation rows to
    /// delete: the stored row's stale ones, and the entity's own leftover rows the new value does not need.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IReadOnlyList<string> StalePartRowKeys { get; }

    /// <summary>Storage-provider SPI, not for application code. The blob names the new row references.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IReadOnlyList<string> BlobPaths { get; }

    internal IReadOnlyList<PendingBlob> Pending { get; }

    internal SplitRowPlan Plan { get; }
}
