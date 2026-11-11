using Azure;
using Azure.Data.Tables;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.InMemory;

// Large values (TableValueSplit, TableBlobContent): the same tiers, ordering, integrity checks and
// exceptions as TableStorage<T>, through the same LargeValueTable SPI it uses. A write prepares,
// plans and uploads OUTSIDE the gate, then checks the version it planned against, swaps the primary,
// writes its continuation rows and removes the stale ones atomically UNDER the gate (the in-memory
// entity-group transaction); the blobs only the replaced row referenced are deleted after it. A failed
// write deletes no blob. Reads skip continuation rows, assemble the logical row under the gate and
// resolve its blobs outside it. Every member here is reached only for a large-value-aware T.
//
// As in TableStorage<T>, a continuation row is the entity's own when the stored primary declares it or
// when its _SplitOf cell names the entity (a leftover nothing declares). A write that finds a leftover
// of its own at the key of a new continuation row is planned again over the entity's own rows (it
// replaces the ones it needs and deletes the others); any other row at such a key is foreign and is
// never overwritten. A delete of an entity that has no primary row removes its leftovers.
public sealed partial class InMemoryStorage<T>
{
    private enum LargeWriteKind
    {
        Create,
        Upsert,
        Replace,
    }

    /// <summary>What a write or delete needs to know about the stored row.</summary>
    private sealed record PriorRow(long Version, int Parts, IReadOnlyList<string> Blobs);

    private static readonly IReadOnlyList<string> NoKeys = [];

    /// <summary>
    /// Runs before each attempt of a large-value write (single or batch transaction) takes the gate to
    /// commit, after it has read, planned and uploaded (for tests that let another writer in between a
    /// write's read and its commit).
    /// </summary>
    internal Action? BeforeLargeCommit { get; set; }

    /// <summary>The large-value rules this store shares with TableStorage (for tests that replace their retry wait).</summary>
    internal LargeValueTable LargeValueRules => large;

    // ── Writes ──────────────────────────────────────────────────────────────

    private async Task<T> UpsertLargeAsync(T entity, CancellationToken ct)
    {
        RefuseReservedRowKey(entity.Id);
        await EnforceProtectedPropertiesLargeAsync(entity, ct);

        if (entity.CreatedAt == default)
            entity.CreatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null;
        entity.Id = NormalizeId(entity.Id);
        return await WriteLargeAsync(entity, LargeWriteKind.Upsert, ifMatch: null, ct);
    }

    private async Task<T> UpdateLargeAsync(T entity, ConcurrencyMode mode, CancellationToken ct)
    {
        RefuseReservedRowKey(entity.Id);
        await EnforceProtectedPropertiesLargeAsync(entity, ct);

        var callerSuppliedETag = !string.IsNullOrEmpty(entity.ETag);
        if (mode == ConcurrencyMode.Strict && !callerSuppliedETag)
            throw new InvalidOperationException(
                $"ConcurrencyMode.Strict requires {typeof(T).Name}.ETag — read the entity first and round-trip its ETag.");
        if (mode == ConcurrencyMode.Auto && !callerSuppliedETag && !implicitLastWriterWins)
            throw new InvalidOperationException(ConcurrencyMessages.AutoRequiresETag(typeof(T).Name));

        // Strict, and Auto with a round-tripped ETag, replace only the version that was read;
        // LastWriterWins and the opted-in implicit fallback replace whatever is stored.
        var ifMatch = mode != ConcurrencyMode.LastWriterWins && callerSuppliedETag ? entity.ETag : null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null;
        entity.Id = NormalizeId(entity.Id);
        return await WriteLargeAsync(entity, LargeWriteKind.Replace, ifMatch, ct);
    }

    /// <summary>
    /// Writes a large-value-aware entity: prepares its blob contents, then plans, uploads and commits
    /// (see <see cref="WritePreparedAsync"/>), releasing any temporary copy the write did not bind.
    /// </summary>
    private async Task<T> WriteLargeAsync(T entity, LargeWriteKind kind, string? ifMatch, CancellationToken ct)
    {
        var keys = EntityId.Split(entity.Id);
        LargeValueTable.ValidateKeys(keys.PartitionKey, keys.RowKey);
        try
        {
            await large.PrepareAsync(entity, ct);
            return await WritePreparedAsync(entity, keys, kind, ifMatch, ct);
        }
        finally
        {
            LargeValueTable.ReleasePrepared(entity);
        }
    }

    private async Task<T> WritePreparedAsync(T entity, (string PartitionKey, string RowKey) keys, LargeWriteKind kind, string? ifMatch, CancellationToken ct)
    {
        // A Replace with a caller ETag is strict: a different stored version is a conflict, never a retry.
        // A Replace without one (last-writer-wins) and an Upsert retry on a lost race instead.
        var strict = kind == LargeWriteKind.Replace && ifMatch is not null;
        RequestFailedException? lost = null;
        // Set once the key of a new continuation row turned out to be held by a leftover continuation row
        // of this very entity: from then on every attempt is planned over the entity's own rows.
        var takeBack = false;
        var lostRace = false;

        for (var attempt = 1; attempt <= MaxLargeWriteAttempts; attempt++)
        {
            // After a lost race: the same short, growing, jittered pause as TableStorage. (Taking an own
            // leftover back is no race, and does not wait.)
            if (lostRace)
                await large.WaitBeforeRetryAsync(attempt, ct);
            lostRace = false;
            PriorRow? prior;
            Dictionary<string, long>? own;
            lock (gate)
            {
                prior = kind == LargeWriteKind.Create ? null : ReadPrior(keys);
                own = takeBack ? OwnParts(keys) : null;
            }
            if (kind == LargeWriteKind.Replace)
            {
                if (prior is null)
                    throw new RequestFailedException(404, $"The specified resource does not exist. Id: {entity.Id}");
                if (strict && !string.Equals(ETagOf(prior.Version), ifMatch, StringComparison.Ordinal))
                    throw Conflict(entity.Id);
            }

            var plan = own is not null
                ? large.PlanOverOwnParts(entity, keys.PartitionKey, keys.RowKey, prior?.Parts ?? 0, own.Keys)
                : await large.PlanAsync(entity, keys.PartitionKey, keys.RowKey, prior?.Parts ?? 0,
                    _ =>
                    {
                        // PlanAsync runs outside the gate; the part lookup takes it (the commit re-checks the version).
                        lock (gate)
                        {
                            return Task.FromResult(ExistingPartKeys(keys));
                        }
                    }, ct);
            // Every blob exists (uploaded, or touched when already there) before the row that references it.
            await large.UploadAsync(plan, ct);
            BeforeLargeCommit?.Invoke();

            long version;
            lock (gate)
            {
                if (kind == LargeWriteKind.Create)
                {
                    if (rows.ContainsKey(keys))
                        throw new DuplicateKeyException(typeof(T).Name, entity.Id,
                            new RequestFailedException(409, $"The specified entity already exists. Id: {entity.Id}"));
                }
                else if (ReadPrior(keys) is var current && current?.Version != prior?.Version)
                {
                    // The stored row changed (or appeared, or vanished) since it was planned against.
                    if (strict)
                    {
                        throw current is null
                            ? new RequestFailedException(404, $"The specified resource does not exist. Id: {entity.Id}")
                            : Conflict(entity.Id);
                    }
                    lost = new RequestFailedException(current is null ? 404 : 412, "The entity was modified by another writer.");
                    lostRace = true;
                    continue;
                }

                // The plan replaces and deletes the entity's own continuation rows as they were read
                // (TableStorage does so on condition of their versions): when they are not those rows
                // now, the write is planned again.
                if (own is not null && !SameParts(own, OwnParts(keys)))
                {
                    lost = PartsChanged();
                    lostRace = true;
                    continue;
                }

                if (TakenPartKey(keys, plan) is { } taken)
                {
                    // A row holds the key of a new continuation row. A continuation row of this very
                    // entity is its own, left behind: the write is planned again over the entity's own
                    // rows. Any other row is foreign: the write is refused and that row is left as it is.
                    if (!IsOwnPart(keys, taken))
                        throw LargeValueTable.ContinuationKeyTaken(keys.PartitionKey, keys.RowKey, taken, Taken(taken));
                    takeBack = true;
                    lost = Taken(taken);
                    continue;
                }
                version = Commit(keys, plan);
            }

            // The swap committed: the blobs only the replaced row referenced can go (the same content
            // written again maps to the same name, which the new row still references). A committed
            // write is never reported as failed: a cancelled token only bounds how long cleanup may take.
            AfterLargeCommit?.Invoke();
            if (prior is { Blobs.Count: > 0 })
                await large.DeleteReleasedBlobsAsync(keys.PartitionKey, keys.RowKey,
                    prior.Blobs.Except(plan.BlobPaths, StringComparer.Ordinal), serviceNow: null, onForeign: null, onFailure: null, ct);
            entity.ETag = ETagOf(version);
            return entity;
        }

        throw new ConcurrencyConflictException(typeof(T).Name, entity.Id, lost);
    }

    // Under the gate: the key of the first new continuation row (one the stored row does not declare, and
    // not one of the entity's own rows the plan was made over) that a row already holds; TableStorage
    // inserts a new part, so the service fails it with 409.
    private string? TakenPartKey((string PartitionKey, string RowKey) keys, LargeValueWritePlan plan)
    {
        for (var i = 0; i < plan.Parts.Count; i++)
        {
            if (plan.IsNewPart(i) && rows.ContainsKey((keys.PartitionKey, plan.Parts[i].RowKey)))
                return plan.Parts[i].RowKey;
        }
        return null;
    }

    private static RequestFailedException Taken(string rowKey) =>
        new(409, $"The specified entity already exists. RowKey: {rowKey}", "EntityAlreadyExists", null);

    private static RequestFailedException PartsChanged() =>
        new(412, "A continuation row of the entity was modified by another writer.", "UpdateConditionNotSatisfied", null);

    /// <summary>Under the gate: true when the row at <paramref name="partRowKey"/> is a continuation row of the entity at <paramref name="keys"/>.</summary>
    private bool IsOwnPart((string PartitionKey, string RowKey) keys, string partRowKey) =>
        rows.TryGetValue((keys.PartitionKey, partRowKey), out var row)
        && LargeValueTable.IsPartOf(row.Data, keys.PartitionKey, keys.RowKey);

    /// <summary>
    /// Under the gate: the entity's own continuation rows that exist now, whether its primary declares
    /// them or not, each with its version.
    /// </summary>
    private Dictionary<string, long> OwnParts((string PartitionKey, string RowKey) keys)
    {
        var own = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows.Values)
        {
            if (LargeValueTable.IsPartOf(row.Data, keys.PartitionKey, keys.RowKey))
                own[row.Data.RowKey] = row.Version;
        }
        return own;
    }

    private static bool SameParts(Dictionary<string, long> read, Dictionary<string, long> now) =>
        read.Count == now.Count && read.All(part => now.TryGetValue(part.Key, out var version) && version == part.Value);

    // Under the gate: the primary, its continuation rows and the deletes of its stale ones, as one step.
    private long Commit((string PartitionKey, string RowKey) keys, LargeValueWritePlan plan)
    {
        var now = DateTimeOffset.UtcNow;
        var primary = new StoredRow(CopyOf(plan.Primary), ++versionCounter, now);
        rows[keys] = primary;
        foreach (var part in plan.Parts)
            rows[(keys.PartitionKey, part.RowKey)] = new StoredRow(CopyOf(part), ++versionCounter, now);
        foreach (var stale in plan.StalePartRowKeys)
            rows.Remove((keys.PartitionKey, stale));
        return primary.Version;
    }

    /// <summary>
    /// One entity of a large-value batch: the stored row it was planned against, its rows and, when it was
    /// planned over the entity's own continuation rows, those rows with their versions.
    /// </summary>
    private sealed record BatchUnit(
        T Entity, (string PartitionKey, string RowKey) Keys, PriorRow? Prior, LargeValueWritePlan Plan, Dictionary<string, long>? OwnParts);

    /// <summary>
    /// The batch contract of TableStorage&lt;T&gt;: per partition, each entity's rows are one indivisible
    /// unit, packed in order into the same transactions (<see cref="LargeValueTable.PlanTransactions"/>),
    /// each applied atomically under the gate. A transaction's blobs are uploaded just before it, and
    /// released ones deleted after each commit. A repeated key in a create batch was
    /// refused before this point; in an upsert batch its last occurrence is written. An existing key fails
    /// its create transaction; an upsert transaction that lost a race is planned again and retried (five
    /// attempts in all, with a short pause before each retry, as TableStorage). A
    /// transaction that finds the key of a new continuation row held by a leftover continuation row of the
    /// same entity is planned again over that entity's own rows; a foreign row there fails it.
    /// </summary>
    private async Task<int> WriteBatchLargeAsync(List<((string, string) Keys, T Entity)> prepared, bool upsert, CancellationToken ct)
    {
        var kind = upsert ? LargeWriteKind.Upsert : LargeWriteKind.Create;
        var writes = prepared;
        if (upsert)
        {
            // Each key once, with its last occurrence (last writer wins), as TableStorage writes it.
            var last = new Dictionary<(string, string), int>();
            for (var i = 0; i < prepared.Count; i++)
                last[prepared[i].Keys] = i;
            writes = prepared.Where((p, i) => last[p.Keys] == i).ToList();
        }

        try
        {
            foreach (var (_, entity) in writes)
                await large.PrepareAsync(entity, ct);
            foreach (var group in writes.GroupBy(w => w.Keys.Item1, StringComparer.Ordinal))
            {
                var units = await PlanBatchUnitsAsync(group.Select(g => g.Entity).ToList(), kind, takeBack: null, ct);
                await CommitBatchUnitsAsync(units, kind, attempt: 1, takeBack: null, ct);
            }
        }
        finally
        {
            foreach (var (_, entity) in writes)
                LargeValueTable.ReleasePrepared(entity);
        }

        // Parity with TableStorage: a batch reports no version per entity — re-read before optimistic updates.
        foreach (var (_, entity) in prepared)
            entity.ETag = null;
        return prepared.Count;
    }

    /// <summary>
    /// Plans the entities of one partition. <paramref name="takeBack"/> names (by row key) the entities
    /// that are planned over their own continuation rows because a leftover held a key of theirs.
    /// </summary>
    private async Task<List<BatchUnit>> PlanBatchUnitsAsync(List<T> entities, LargeWriteKind kind, HashSet<string>? takeBack, CancellationToken ct)
    {
        var units = new List<BatchUnit>(entities.Count);
        foreach (var entity in entities)
        {
            var keys = EntityId.Split(entity.Id);
            PriorRow? prior;
            Dictionary<string, long>? own;
            lock (gate)
            {
                prior = kind == LargeWriteKind.Create ? null : ReadPrior(keys);
                own = takeBack is not null && takeBack.Contains(keys.RowKey) ? OwnParts(keys) : null;
            }
            var plan = own is not null
                ? large.PlanOverOwnParts(entity, keys.PartitionKey, keys.RowKey, prior?.Parts ?? 0, own.Keys)
                : await large.PlanAsync(entity, keys.PartitionKey, keys.RowKey, prior?.Parts ?? 0,
                    _ =>
                    {
                        lock (gate)
                        {
                            return Task.FromResult(ExistingPartKeys(keys));
                        }
                    }, ct);
            units.Add(new BatchUnit(entity, keys, prior, plan, own));
        }
        return units;
    }

    private async Task CommitBatchUnitsAsync(List<BatchUnit> units, LargeWriteKind kind, int attempt, HashSet<string>? takeBack, CancellationToken ct)
    {
        foreach (var range in LargeValueTable.PlanTransactions(units.Select(u => u.Plan).ToList()))
        {
            var chunk = units.GetRange(range.Start, range.Count);
            // Every blob exists before the rows that reference it, uploaded just before its own transaction.
            foreach (var unit in chunk)
                await large.UploadAsync(unit.Plan, ct);
            // A cancelled token fails a transaction before it is sent, as the SDK does.
            ct.ThrowIfCancellationRequested();
            BeforeLargeCommit?.Invoke();
            BatchUnit? lost = null;
            var lostRace = true;
            var taking = takeBack;
            lock (gate)
            {
                BatchTransactions++;
                // The first action the service would fail fails the whole transaction; nothing is applied.
                foreach (var unit in chunk)
                {
                    if (kind == LargeWriteKind.Create)
                    {
                        if (rows.ContainsKey(unit.Keys))
                            throw new DuplicateKeyException(typeof(T).Name, unit.Entity.Id,
                                new RequestFailedException(409, $"The specified entity already exists. Id: {unit.Entity.Id}"));
                    }
                    else if (ReadPrior(unit.Keys)?.Version != unit.Prior?.Version)
                    {
                        lost = unit;
                        break;
                    }
                    // The entity's own continuation rows the plan was made over are not those rows now.
                    if (unit.OwnParts is not null && !SameParts(unit.OwnParts, OwnParts(unit.Keys)))
                    {
                        lost = unit;
                        break;
                    }
                    if (TakenPartKey(unit.Keys, unit.Plan) is { } taken)
                    {
                        // A foreign row is never overwritten; the entity's own leftover is taken back
                        // by planning the transaction again with that entity over its own rows.
                        if (!IsOwnPart(unit.Keys, taken))
                            throw LargeValueTable.ContinuationKeyTaken(unit.Keys.PartitionKey, unit.Keys.RowKey, taken, Taken(taken));
                        lost = unit;
                        lostRace = false;
                        // As TableStorage: every entity of the transaction that inserts a continuation
                        // row is planned over its own rows, so one retry takes all their leftovers back.
                        taking = new HashSet<string>(takeBack ?? [], StringComparer.Ordinal);
                        foreach (var other in chunk)
                        {
                            if (Enumerable.Range(0, other.Plan.Parts.Count).Any(other.Plan.IsNewPart))
                                taking.Add(other.Keys.RowKey);
                        }
                        break;
                    }
                }
                if (lost is null)
                {
                    foreach (var unit in chunk)
                        Commit(unit.Keys, unit.Plan);
                }
            }

            if (lost is not null)
            {
                if (attempt >= MaxLargeWriteAttempts)
                    throw new ConcurrencyConflictException(typeof(T).Name, lost.Entity.Id,
                        new RequestFailedException(412, "The entity was modified by another writer."));
                // After a lost race: the same pause as TableStorage (none when an own leftover is taken back).
                if (lostRace)
                    await large.WaitBeforeRetryAsync(attempt + 1, ct);
                var replanned = await PlanBatchUnitsAsync(chunk.ConvertAll(u => u.Entity), kind, taking, ct);
                await CommitBatchUnitsAsync(replanned, kind, attempt + 1, taking, ct);
                continue;
            }

            // Committed: the blobs only the replaced rows referenced go (a cancelled token only bounds it).
            AfterLargeCommit?.Invoke();
            foreach (var unit in chunk)
            {
                if (unit.Prior is { Blobs.Count: > 0 })
                    await large.DeleteReleasedBlobsAsync(unit.Keys.PartitionKey, unit.Keys.RowKey,
                        unit.Prior.Blobs.Except(unit.Plan.BlobPaths, StringComparer.Ordinal), serviceNow: null, onForeign: null, onFailure: null, ct);
            }
        }
    }

    // ── Deletes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the entity and its continuation rows in one step under the gate; once it is gone, the blobs
    /// it referenced are deleted (best effort, after the grace). When there is no row at the id, the
    /// entity's leftover continuation rows (if any) are removed, so nothing of it stays behind; a
    /// continuation row addressed by its own id is not an entity and is left alone.
    /// </summary>
    private async Task DeleteLargeAsync((string PartitionKey, string RowKey) keys, CancellationToken ct)
    {
        // No row-key check: a row written before the type opted in may carry any key and stays deletable.
        IReadOnlyList<string> released = NoKeys;
        lock (gate)
        {
            if (!rows.TryGetValue(keys, out var stored))
            {
                // No primary row: all that can be left of the entity are continuation rows nothing declares.
                var leftovers = ExistingPartKeys(keys);
                if (leftovers.Count == 0)
                    return;
                foreach (var part in leftovers)
                    rows.Remove((keys.PartitionKey, part));
            }
            else if (LargeValueTable.IsPartRow(stored.Data))
            {
                // A continuation row addressed by its own id is not an entity: nothing is deleted.
                return;
            }
            else
            {
                // A part count that cannot be read (a damaged row) is not "none": the continuation rows
                // that are there go with the row, so a row no read can return is still deletable.
                if (!DeclaresNoParts(stored.Data))
                {
                    foreach (var part in ExistingPartKeys(keys))
                        rows.Remove((keys.PartitionKey, part));
                }
                rows.Remove(keys);
                released = LargeValueTable.BlobPaths(stored.Data);
            }
        }

        // The entity is gone: its blobs follow (a cancelled token only bounds how long that may take).
        AfterLargeCommit?.Invoke();
        await large.DeleteReleasedBlobsAsync(keys.PartitionKey, keys.RowKey, released, serviceNow: null, onForeign: null, onFailure: null, ct);
    }

    /// <summary>
    /// Deletes the partition as TableStorage does: the rows are listed, then removed in the same
    /// transactions (<see cref="LargeValueTable.PlanPartitionDelete"/>: a primary and its continuation
    /// rows always together, at most 100 rows each), each applied atomically under the gate. A token
    /// cancelled between two transactions stops the delete there, with whole entities left; a row
    /// written after the listing survives. Then the blobs no remaining row references are deleted.
    /// </summary>
    private async Task<int> DeletePartitionLargeAsync(string partition, CancellationToken ct)
    {
        // The partition's rows in row-key order, as the service lists them.
        List<TableEntity> listed;
        lock (gate)
        {
            listed = rows.Where(kv => kv.Key.PartitionKey == partition)
                .OrderBy(kv => kv.Key.RowKey, StringComparer.Ordinal)
                .Select(kv => kv.Value.Data)
                .ToList();
        }
        // Every row goes, continuation rows included; only logical entities are counted.
        var logical = listed.Count(row => !LargeValueTable.IsPartRow(row));

        foreach (var transaction in LargeValueTable.PlanPartitionDelete(listed))
        {
            // A cancelled token fails a transaction before it is sent, as the SDK does.
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                BatchTransactions++;
                foreach (var row in transaction)
                    rows.Remove((row.PartitionKey, row.RowKey));
            }
            AfterLargeCommit?.Invoke();
        }

        // The rows are gone: so are the blobs stored under the partition that no row references now (best
        // effort, after the grace; a row another writer created meanwhile keeps its blobs; a cancelled
        // token only bounds how long that may take, the rest is left to the orphan sweep).
        await large.DeletePartitionBlobsAsync(partition, typeof(T), (_, _) => PartitionRowsAsync(partition), onListFailure: null, onFailure: null, ct);
        return logical;
    }

    // ── Orphan sweep (any T: blobs under the partition may come from any type sharing the table) ──

    /// <inheritdoc />
    public Task<int> SweepOrphanBlobsAsync(string partitionKey, TimeSpan olderThan, CancellationToken ct = default)
    {
        LargeValueTable.ValidateSweep(partitionKey, olderThan);
        var partition = NormalizeId(partitionKey); // match stored (normalized) PartitionKeys, as DeletePartitionAsync
        return large.SweepOrphanBlobsAsync(partition, olderThan, typeof(T), (_, _) => PartitionRowsAsync(partition), ct);
    }

    /// <inheritdoc />
    public Task<int> SweepOrphanBlobsAsync(TimeSpan olderThan, CancellationToken ct = default) =>
        // The listed partitions are stored keys already: they are not normalized again.
        large.SweepTableOrphanBlobsAsync(olderThan, typeof(T), (partition, _, _) => PartitionRowsAsync(partition), ct);

    // A snapshot of every stored row of the partition (continuation rows included), taken under the gate.
    // The projection is not applied: a whole row holds every cell the sweep would have selected.
    private async IAsyncEnumerable<TableEntity> PartitionRowsAsync(string partition)
    {
        List<TableEntity> snapshot;
        lock (gate)
        {
            snapshot = rows.Where(kv => kv.Key.PartitionKey == partition).Select(kv => CopyOf(kv.Value.Data)).ToList();
        }
        foreach (var row in snapshot)
            yield return row;
        await Task.CompletedTask;
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    private async Task<T?> OneLargeAsync((string PartitionKey, string RowKey) keys, CancellationToken ct)
    {
        StoredRow? row;
        lock (gate)
        {
            // A continuation row is not an entity: addressing one by id finds nothing.
            row = rows.TryGetValue(keys, out var stored) && !LargeValueTable.IsPartRow(stored.Data)
                ? Assembled(stored)
                : null;
        }
        return row is null ? null : await MaterializeAsync(row, ct);
    }

    private async Task<IEnumerable<T>> QueryLargeAsync(string? scope, CancellationToken ct) =>
        await MaterializeAllAsync(LogicalSnapshot(() => LogicalRows().Where(kv => scope is null || kv.Key.PartitionKey == scope)), ct);

    private async Task<IReadOnlyList<T>> QueryLargeAsync(Func<T, bool> matches, string? scope, int? take, CancellationToken ct)
    {
        var candidates = LogicalSnapshot(() => LogicalRows().Where(kv => scope is null || kv.Key.PartitionKey == scope));
        var results = new List<T>();
        foreach (var row in candidates)
        {
            if (take is int t && results.Count >= t)
                break;
            if (await MaterializeAsync(row, ct) is { } entity && matches(entity))
                results.Add(entity);
        }
        return results;
    }

    private async Task<IReadOnlyList<T>> BoundedLargeAsync(QueryOptions options, CancellationToken ct) =>
        await MaterializeAllAsync(LogicalSnapshot(() => Bounded(options)), ct);

    // Pages over the stored rows, as the service does for TableStorage: a continuation row (declared or
    // leftover) takes a place in the page and is then skipped, so a page can hold fewer entities than its
    // size, or none, and still carry a cursor; the cursor resumes after the last row of the page.
    private async Task<EntityPage<T>> PageLargeAsync(
        string? partition, string? rowKeyPrefix, int pageSize, string fingerprint,
        (string PartitionKey, string RowKey)? after, CancellationToken ct)
    {
        List<StoredRow> pageRows;
        string? next = null;
        lock (gate)
        {
            IEnumerable<KeyValuePair<(string PartitionKey, string RowKey), StoredRow>> rowsQuery = OrderedRows()
                .Where(kv => partition is null || kv.Key.PartitionKey == partition)
                .Where(kv => rowKeyPrefix is null || kv.Key.RowKey.StartsWith(rowKeyPrefix, StringComparison.Ordinal));
            if (after is { } a)
                rowsQuery = rowsQuery.Where(kv => KeyCompare(kv.Key, a) > 0);

            var candidate = rowsQuery.Take(pageSize + 1).ToList();
            var hasMore = candidate.Count > pageSize;
            var page = hasMore ? candidate.Take(pageSize).ToList() : candidate;
            if (hasMore)
            {
                var last = page[page.Count - 1].Key;
                next = PageCursor.Encode(fingerprint, last.PartitionKey + "\0" + last.RowKey);
            }
            pageRows = page.Where(kv => !LargeValueTable.IsPartRow(kv.Value.Data)).Select(kv => Assembled(kv.Value)).ToList();
        }

        return new EntityPage<T>(await MaterializeAllAsync(pageRows, ct), next);
    }

    // ── Helpers (callers marked "under the gate" must hold it) ───────────────

    /// <summary>Under the gate: the entity rows (continuation rows skipped) in lexical key order.</summary>
    private IEnumerable<KeyValuePair<(string PartitionKey, string RowKey), StoredRow>> LogicalRows() =>
        OrderedRows().Where(kv => !LargeValueTable.IsPartRow(kv.Value.Data));

    /// <summary>Takes the gate and returns the selected entity rows, each assembled with its continuation rows.</summary>
    private List<StoredRow> LogicalSnapshot(Func<IEnumerable<KeyValuePair<(string PartitionKey, string RowKey), StoredRow>>> select)
    {
        lock (gate)
        {
            return select().Select(kv => Assembled(kv.Value)).ToList();
        }
    }

    /// <summary>Under the gate: the stored primary merged with its continuation rows (missing ones throw).</summary>
    private StoredRow Assembled(StoredRow primary)
    {
        if (LargeValueTable.PartCount(primary.Data) == 0)
            return primary;
        var pk = primary.Data.PartitionKey;
        var rk = primary.Data.RowKey;
        var parts = rows.Values.Select(r => r.Data).Where(d => LargeValueTable.IsPartOf(d, pk, rk));
        return primary with { Data = LargeValueTable.Assemble(primary.Data, parts) };
    }

    private static bool DeclaresNoParts(TableEntity row)
    {
        try
        {
            return LargeValueTable.PartCount(row) == 0;
        }
        catch (TableValueIntegrityException)
        {
            return false;
        }
    }

    /// <summary>Under the gate: the stored entity row's version, part count and blobs; null when there is none.</summary>
    private PriorRow? ReadPrior((string PartitionKey, string RowKey) keys) =>
        rows.TryGetValue(keys, out var row) && !LargeValueTable.IsPartRow(row.Data)
            ? new PriorRow(row.Version, LargeValueTable.PartCount(row.Data), LargeValueTable.BlobPaths(row.Data))
            : null;

    /// <summary>Under the gate: the row keys of the continuation rows of <paramref name="keys"/> that exist now.</summary>
    private IReadOnlyList<string> ExistingPartKeys((string PartitionKey, string RowKey) keys)
    {
        var found = rows.Values
            .Where(r => LargeValueTable.IsPartOf(r.Data, keys.PartitionKey, keys.RowKey))
            .Select(r => r.Data.RowKey)
            .ToList();
        return found.Count == 0 ? NoKeys : found;
    }

    /// <summary>
    /// The entity an assembled row holds, its offloaded values downloaded outside the gate. A writer that
    /// replaces or deletes the row meanwhile can delete a blob the snapshot references: as in
    /// TableStorage, the row is then read again after every failed attempt, the last included: an
    /// unchanged version is a genuine integrity failure; a row deleted meanwhile is null; a changed
    /// version retries the read from the new version after the writers' pause, and when the version
    /// changed during each of the <see cref="MaxLargeReadAttempts"/> attempts the read throws
    /// <see cref="ConcurrencyConflictException"/> with the last integrity failure as its inner exception.
    /// </summary>
    private async Task<T?> MaterializeAsync(StoredRow row, CancellationToken ct)
    {
        var keys = (row.Data.PartitionKey, row.Data.RowKey);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var entity = await large.ReadAsync<T>(CopyOf(row.Data), ct);
                entity.ETag = row.ETagString();
                entity.Timestamp = row.Timestamp;
                return entity;
            }
            catch (TableValueIntegrityException failure)
            {
                StoredRow? current;
                lock (gate)
                {
                    current = rows.TryGetValue(keys, out var stored) && !LargeValueTable.IsPartRow(stored.Data)
                        ? Assembled(stored)
                        : null;
                }
                if (current is null)
                    return null;
                if (current.Version == row.Version)
                    throw;
                if (attempt >= MaxLargeReadAttempts)
                    throw new ConcurrencyConflictException(
                        ConcurrencyMessages.ReadKeptChanging(typeof(T).Name, keys.PartitionKey, keys.RowKey, MaxLargeReadAttempts), failure);
                await large.WaitBeforeRetryAsync(attempt + 1, ct);
                row = current;
            }
        }
    }

    /// <summary>How many times a read whose blobs belonged to another version of the row is attempted in all (as TableStorage).</summary>
    private const int MaxLargeReadAttempts = LargeValueTable.MaxWriteAttempts;

    private async Task<List<T>> MaterializeAllAsync(List<StoredRow> source, CancellationToken ct)
    {
        var entities = new List<T>(source.Count);
        foreach (var row in source)
        {
            // Deleted while its blobs were read: no longer part of the result.
            if (await MaterializeAsync(row, ct) is { } entity)
                entities.Add(entity);
        }
        return entities;
    }

    /// <summary>
    /// For a large-value-aware <typeparamref name="T"/>, refuses a row key reserved for continuation rows,
    /// and a key that is not well-formed UTF-16 (it cannot name a blob).
    /// </summary>
    private void RefuseReservedRowKey(string id)
    {
        if (!LargeValueAware)
            return;
        var keys = EntityId.Split(NormalizeId(id));
        LargeValueTable.ValidateKeys(keys.PartitionKey, keys.RowKey);
    }

    private static ConcurrencyConflictException Conflict(string id) =>
        new(typeof(T).Name, id, new RequestFailedException(412, "Precondition Failed: the entity was modified by another writer."));

    private async Task EnforceProtectedPropertiesLargeAsync(T entity, CancellationToken ct)
    {
        if (ProtectedProps.Count == 0)
            return;
        var stored = await OneLargeAsync(EntityId.Split(NormalizeId(entity.Id)), ct);
        CheckProtectedProperties(stored, entity);
    }
}
