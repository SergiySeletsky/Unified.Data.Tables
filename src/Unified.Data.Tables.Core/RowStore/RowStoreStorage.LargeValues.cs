using Azure;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Providers;

// Large values (TableValueSplit, TableBlobContent): the same tiers, ordering, integrity checks and
// exceptions as TableStorage<T>, through the same LargeValueTable rules it uses. A write prepares, reads
// the stored row, plans and uploads, then commits its rows as ONE commit of the row store: the primary on
// condition of the version it was planned against, a new continuation row on condition that its key is
// free, the stale ones deleted. The blobs only the replaced row referenced are deleted after it. A failed
// write deletes no blob. Reads skip continuation rows, assemble the logical row and resolve its blobs.
// Every member here is reached only for a large-value-aware T.
//
// As in TableStorage<T>, a continuation row is the entity's own when the stored primary declares it or
// when its _SplitOf cell names the entity (a leftover nothing declares). A write that finds a leftover
// of its own at the key of a new continuation row is planned again over the entity's own rows (it
// replaces the ones it needs and deletes the others, each on condition of the version read); any other
// row at such a key is foreign and is never overwritten. A delete of an entity that has no primary row
// removes its leftovers.
internal sealed partial class RowStoreStorage<T> : IBlobOffloadState
{
    private enum LargeWriteKind
    {
        Create,
        Upsert,
        Replace,
    }

    /// <summary>What a write or delete needs to know about the stored entity row.</summary>
    private sealed record PriorRow(string ETag, int Parts, IReadOnlyList<string> Blobs);

    /// <summary>What an operation of a large-value commit is, for reading a refused commit.</summary>
    private enum WriteRole
    {
        Primary,
        OwnPart,
        NewPart,
        Other,
    }

    private static readonly IReadOnlyList<string> NoKeys = [];

    /// <summary>
    /// Runs before each attempt of a large-value write (single or batch transaction) commits, after it
    /// has read, planned and uploaded (for tests that let another writer in between a write's read and
    /// its commit).
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
            entity.CreatedAt = clock.GetUtcNow();
        entity.UpdatedAt = clock.GetUtcNow();
        entity.Timestamp = null;
        entity.Id = NormalizeId(entity.Id);
        await EnsureTableAsync(ct);
        return await WriteLargeAsync(entity, LargeWriteKind.Upsert, ifMatch: null, ct);
    }

    private async Task<T> UpdateLargeAsync(T entity, ConcurrencyMode mode, CancellationToken ct)
    {
        RefuseReservedRowKey(entity.Id);
        await EnforceProtectedPropertiesLargeAsync(entity, ct);

        var callerSuppliedETag = !string.IsNullOrEmpty(entity.ETag);
        if (mode == ConcurrencyMode.Strict && !callerSuppliedETag)
            throw new InvalidOperationException(
                $"ConcurrencyMode.Strict requires {typeName}.ETag — read the entity first and round-trip its ETag.");
        if (mode == ConcurrencyMode.Auto && !callerSuppliedETag && !implicitLastWriterWins)
            throw new InvalidOperationException(ConcurrencyMessages.AutoRequiresETag(typeName));

        // Strict, and Auto with a round-tripped ETag, replace only the version that was read;
        // LastWriterWins and the opted-in implicit fallback replace whatever is stored.
        var ifMatch = mode != ConcurrencyMode.LastWriterWins && callerSuppliedETag ? entity.ETag : null;
        entity.UpdatedAt = clock.GetUtcNow();
        entity.Timestamp = null;
        entity.Id = NormalizeId(entity.Id);
        await EnsureTableAsync(ct);
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
            var (prior, atKey) = kind == LargeWriteKind.Create ? default : await ReadPriorAsync(keys, ct);
            var own = takeBack ? await OwnPartsAsync(keys, ct) : null;
            if (kind == LargeWriteKind.Replace)
            {
                if (prior is null)
                    throw new EntityNotFoundException(typeName, entity.Id, null);
                // As TableStorage: the version it read is not the caller's, so nothing is sent (no inner failure).
                if (strict && !string.Equals(prior.ETag, ifMatch, StringComparison.Ordinal))
                    throw new ConcurrencyConflictException(typeName, entity.Id, null);
            }

            var plan = own is not null
                ? large.PlanOverOwnParts(entity, keys.PartitionKey, keys.RowKey, prior?.Parts ?? 0, own.Keys)
                : await large.PlanAsync(entity, keys.PartitionKey, keys.RowKey, prior?.Parts ?? 0,
                    token => ExistingPartKeysAsync(keys, token), ct);
            // Every blob exists (uploaded, or touched when already there) before the row that references it.
            await large.UploadAsync(plan, ct);
            // A cancelled token fails the row write before it is sent, as the SDK does.
            ct.ThrowIfCancellationRequested();
            BeforeLargeCommit?.Invoke();

            // TableStorage writes a row with continuation rows to write or delete as one entity-group
            // transaction, and the SDK reports a failure of it as a TableTransactionFailedException.
            var transactional = plan.Parts.Count > 0 || plan.StalePartRowKeys.Count > 0;
            RequestFailedException Sent(RequestFailedException failure) =>
                transactional ? ServiceErrors.InTransaction(failure) : failure;

            // The service refuses the key when the row is written (the blobs are uploaded by then).
            if (ServiceErrors.RefusedKey(keys.PartitionKey, keys.RowKey) is { } refused)
                throw ServiceErrors.Neutral(Sent(refused), typeName, entity.Id);
            if (plan.Parts.Prepend(plan.Primary).Select(ServiceErrors.RefusedRow).FirstOrDefault(e => e is not null) is { } oversized)
                throw ServiceErrors.Neutral(Sent(oversized), typeName, entity.Id);

            var writes = new List<RowWrite>();
            var roles = new List<WriteRole>();
            AddUnitWrites(writes, roles, kind, atKey, plan, own);
            var result = await rows.CommitAsync(keys.PartitionKey, writes, ct);
            if (!result.Committed)
            {
                switch (roles[result.FailedIndex])
                {
                    case WriteRole.Primary when kind == LargeWriteKind.Create:
                        throw ServiceErrors.Coded(new DuplicateKeyException(typeName, entity.Id, Sent(ServiceErrors.AlreadyExists(entity.Id))));
                    case WriteRole.Primary:
                        // The stored row changed (or appeared, or vanished) since it was planned against.
                        var gone = result.Failure == RowCommitFailure.NotFound;
                        if (strict)
                        {
                            throw gone
                                ? ServiceErrors.Neutral(Sent(ServiceErrors.NotFound(entity.Id)), typeName, entity.Id)
                                : ServiceErrors.Coded(new ConcurrencyConflictException(typeName, entity.Id, Sent(ServiceErrors.ConditionNotSatisfied())));
                        }
                        lost = Sent(gone
                            ? ServiceErrors.NotFound(entity.Id)
                            : ServiceErrors.ConditionNotSatisfied("The entity was modified by another writer."));
                        lostRace = true;
                        continue;
                    case WriteRole.NewPart:
                        // A row holds the key of a new continuation row. A continuation row of this very
                        // entity is its own, left behind: the write is planned again over the entity's own
                        // rows. Any other row is foreign: the write is refused and that row is left as it is.
                        var taken = writes[result.FailedIndex].RowKey;
                        if (await rows.GetAsync(keys.PartitionKey, taken, ct) is { } holder
                            && !LargeValueTable.IsPartOf(holder.Data, keys.PartitionKey, keys.RowKey))
                            throw LargeValueTable.ContinuationKeyTaken(keys.PartitionKey, keys.RowKey, taken, Sent(Taken(taken)));
                        takeBack = true;
                        lost = Sent(Taken(taken));
                        continue;
                    default:
                        // The plan replaces and deletes the entity's own continuation rows as they were
                        // read: when they are not those rows now, the write is planned again.
                        lost = Sent(PartsChanged());
                        lostRace = true;
                        continue;
                }
            }

            // The swap committed: the blobs only the replaced row referenced can go (the same content
            // written again maps to the same name, which the new row still references). A committed
            // write is never reported as failed: a cancelled token only bounds how long cleanup may take.
            AfterLargeCommit?.Invoke();
            if (prior is { Blobs.Count: > 0 })
                await large.DeleteReleasedBlobsAsync(keys.PartitionKey, keys.RowKey,
                    prior.Blobs.Except(plan.BlobPaths, StringComparer.Ordinal), serviceNow: null, onForeign: null, onFailure: null, ct);
            entity.ETag = result.ETags[0];
            return entity;
        }

        throw ServiceErrors.Coded(new ConcurrencyConflictException(typeName, entity.Id, lost));
    }

    /// <summary>
    /// The operations of one entity's rows, in the order their conditions are looked at: the primary, on
    /// condition of the row it was planned against (none at its key for a create, or for an upsert that
    /// found none; the version read otherwise); the entity's own continuation rows the plan was made over,
    /// each on condition of the version read; the new continuation rows, each on condition that its key is
    /// free; then the rows the stored primary declares, replaced or deleted as they are.
    /// </summary>
    private static void AddUnitWrites(List<RowWrite> writes, List<WriteRole> roles, LargeWriteKind kind, string? atKey,
        LargeValueWritePlan plan, Dictionary<string, string>? own)
    {
        writes.Add(kind == LargeWriteKind.Create || atKey is null
            ? RowWrite.Insert(RowCells.Copy(plan.Primary))
            : RowWrite.Replace(RowCells.Copy(plan.Primary), atKey));
        roles.Add(WriteRole.Primary);

        if (own is not null)
        {
            for (var i = 0; i < plan.Parts.Count; i++)
            {
                if (!plan.IsNewPart(i) && own.TryGetValue(plan.Parts[i].RowKey, out var version))
                    Add(RowWrite.Replace(RowCells.Copy(plan.Parts[i]), version), WriteRole.OwnPart);
            }
            foreach (var stale in plan.StalePartRowKeys)
            {
                if (own.TryGetValue(stale, out var version))
                    Add(RowWrite.Delete(stale, version), WriteRole.OwnPart);
            }
        }
        for (var i = 0; i < plan.Parts.Count; i++)
        {
            if (plan.IsNewPart(i))
                Add(RowWrite.Insert(RowCells.Copy(plan.Parts[i])), WriteRole.NewPart);
        }
        for (var i = 0; i < plan.Parts.Count; i++)
        {
            if (!plan.IsNewPart(i) && own?.ContainsKey(plan.Parts[i].RowKey) != true)
                Add(RowWrite.Upsert(RowCells.Copy(plan.Parts[i])), WriteRole.Other);
        }
        foreach (var stale in plan.StalePartRowKeys)
        {
            if (own?.ContainsKey(stale) != true)
                Add(RowWrite.DeleteIfExists(stale), WriteRole.Other);
        }

        void Add(RowWrite write, WriteRole role)
        {
            writes.Add(write);
            roles.Add(role);
        }
    }

    private static RequestFailedException Taken(string rowKey) =>
        new(409, $"The specified entity already exists. RowKey: {rowKey}", "EntityAlreadyExists", null);

    private static RequestFailedException PartsChanged() =>
        new(412, "A continuation row of the entity was modified by another writer.", "UpdateConditionNotSatisfied", null);

    /// <summary>
    /// The entity's own continuation rows that exist now, whether its primary declares them or not, each
    /// with its version: the rows of its continuation-key range whose _SplitOf cell names it.
    /// </summary>
    private async Task<Dictionary<string, string>> OwnPartsAsync((string PartitionKey, string RowKey) keys, CancellationToken ct)
    {
        var own = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in await PartRowsAsync(keys, ct))
            own[row.RowKey] = row.ETag;
        return own;
    }

    private async Task<List<StoredRow>> PartRowsAsync((string PartitionKey, string RowKey) keys, CancellationToken ct)
    {
        var range = new RowRange(keys.PartitionKey,
            SplitRowLayout.PartRangeLower(keys.RowKey), true, SplitRowLayout.PartRangeUpper(keys.RowKey), false);
        var parts = new List<StoredRow>();
        await foreach (var row in ScanAsync(range, ct))
        {
            if (LargeValueTable.IsPartOf(row.Data, keys.PartitionKey, keys.RowKey))
                parts.Add(row);
        }
        return parts;
    }

    /// <summary>The row keys of the continuation rows of <paramref name="keys"/> that exist now.</summary>
    private async Task<IReadOnlyList<string>> ExistingPartKeysAsync((string PartitionKey, string RowKey) keys, CancellationToken ct)
    {
        var found = (await PartRowsAsync(keys, ct)).ConvertAll(r => r.RowKey);
        return found.Count == 0 ? NoKeys : found;
    }

    /// <summary>
    /// One entity of a large-value batch: the stored row it was planned against, its rows and, when it was
    /// planned over the entity's own continuation rows, those rows with their versions.
    /// </summary>
    private sealed record BatchUnit(
        T Entity, (string PartitionKey, string RowKey) Keys, PriorRow? Prior, string? AtKey, LargeValueWritePlan Plan, Dictionary<string, string>? OwnParts);

    /// <summary>The blobs one committed batch transaction released from one row, and the time of that commit.</summary>
    private sealed record ReleasedBlobs((string PartitionKey, string RowKey) Keys, List<string> Paths, DateTimeOffset? CommittedAt);

    /// <summary>
    /// The batch contract of TableStorage&lt;T&gt;: per partition, each entity's rows are one indivisible
    /// unit, packed in order into the same transactions (<see cref="LargeValueTable.PlanTransactions"/>),
    /// each one commit of the row store. A transaction's blobs are uploaded just before it; the
    /// released ones are deleted once, after the last transaction committed, each on condition of the grace
    /// counted from the commit that released it (a later entity may hold a content read from one of them).
    /// A batch that fails part way deletes none of them; the orphan sweep does. A repeated key in a create batch was
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

        // Exactly the entities whose PrepareAsync ran are released: one whose prepare never started (an
        // earlier one threw) holds no count on its contents, and releasing it could count out another
        // write of a content it shares.
        var enlisted = new List<T>(writes.Count);
        try
        {
            foreach (var (_, entity) in writes)
            {
                enlisted.Add(entity);
                await large.PrepareAsync(entity, ct);
            }
            var released = new List<ReleasedBlobs>();
            foreach (var group in writes.GroupBy(w => w.Keys.Item1, StringComparer.Ordinal))
            {
                var units = await PlanBatchUnitsAsync(group.Select(g => g.Entity).ToList(), kind, takeBack: null, ct);
                await CommitBatchUnitsAsync(units, kind, attempt: 1, takeBack: null, released, atomic: null, ct);
            }

            // Every transaction committed, so no content of the batch is still to be read from a released
            // blob (a cancelled token only bounds how long cleanup may take).
            foreach (var row in released)
                await large.DeleteReleasedBlobsAsync(row.Keys.PartitionKey, row.Keys.RowKey, row.Paths, row.CommittedAt,
                    onForeign: null, onFailure: null, ct);
        }
        finally
        {
            foreach (var entity in enlisted)
                LargeValueTable.ReleasePrepared(entity);
        }

        // Parity with TableStorage: a batch reports no version per entity; re-read before optimistic updates.
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
            var (prior, atKey) = kind == LargeWriteKind.Create ? default : await ReadPriorAsync(keys, ct);
            var own = takeBack is not null && takeBack.Contains(keys.RowKey) ? await OwnPartsAsync(keys, ct) : null;
            var plan = own is not null
                ? large.PlanOverOwnParts(entity, keys.PartitionKey, keys.RowKey, prior?.Parts ?? 0, own.Keys)
                : await large.PlanAsync(entity, keys.PartitionKey, keys.RowKey, prior?.Parts ?? 0,
                    token => ExistingPartKeysAsync(keys, token), ct);
            units.Add(new BatchUnit(entity, keys, prior, atKey, plan, own));
        }
        return units;
    }

    /// <summary>Whether the one transaction of an all-or-nothing batch has been handed to the row store (see WriteAtomicCoreAsync).</summary>
    private sealed class AtomicWrite
    {
        public bool Sent { get; set; }
    }

    private async Task CommitBatchUnitsAsync(
        List<BatchUnit> units, LargeWriteKind kind, int attempt, HashSet<string>? takeBack, List<ReleasedBlobs> released,
        AtomicWrite? atomic, CancellationToken ct)
    {
        var transactions = LargeValueTable.PlanTransactions(units.Select(u => u.Plan).ToList());
        // An all-or-nothing batch is one transaction or none, for every plan of it (the first one, and
        // each one made again after a lost race, none of which wrote anything).
        if (atomic is not null)
            RequireOneLargeTransaction(units, transactions);
        foreach (var range in transactions)
        {
            var chunk = units.GetRange(range.Start, range.Count);
            // Every blob exists before the rows that reference it, uploaded just before its own transaction.
            foreach (var unit in chunk)
                await large.UploadAsync(unit.Plan, ct);
            // A cancelled token fails a transaction before it is sent, as the SDK does.
            ct.ThrowIfCancellationRequested();
            BeforeLargeCommit?.Invoke();
            BatchTransactions++;

            // The first action the service would fail fails the whole transaction; nothing is applied.
            var writes = new List<RowWrite>();
            var roles = new List<WriteRole>();
            var unitOf = new List<int>();
            StorageException? refusal = null;
            for (var u = 0; u < chunk.Count && refusal is null; u++)
            {
                var unit = chunk[u];
                if (ServiceErrors.RefusedKey(unit.Keys.PartitionKey, unit.Keys.RowKey) is { } refused)
                    refusal = ServiceErrors.Neutral(ServiceErrors.InTransaction(refused), typeName, unit.Entity.Id);
                else if (unit.Plan.Parts.Prepend(unit.Plan.Primary).Select(ServiceErrors.RefusedRow).FirstOrDefault(e => e is not null) is { } oversized)
                    refusal = ServiceErrors.Neutral(ServiceErrors.InTransaction(oversized), typeName, unit.Entity.Id);
                else
                {
                    AddUnitWrites(writes, roles, kind, unit.AtKey, unit.Plan, unit.OwnParts);
                    while (unitOf.Count < writes.Count)
                        unitOf.Add(u);
                }
            }
            if (refusal is not null)
            {
                // The entities before the refused one are looked at first: a stored key of a create is
                // the failure of the transaction, as the service reports the first action it fails.
                if (kind == LargeWriteKind.Create)
                {
                    for (var i = 0; i < writes.Count; i++)
                    {
                        if (roles[i] == WriteRole.Primary && await rows.GetAsync(chunk[unitOf[i]].Keys.PartitionKey, writes[i].RowKey, ct) is not null)
                            throw StoredKey(chunk[unitOf[i]].Entity.Id);
                    }
                }
                throw refusal;
            }

            if (atomic is not null)
                atomic.Sent = true;
            var result = await rows.CommitAsync(chunk[0].Keys.PartitionKey, writes, ct);
            if (!result.Committed)
            {
                var lost = chunk[unitOf[result.FailedIndex]];
                var lostRace = true;
                var taking = takeBack;
                switch (roles[result.FailedIndex])
                {
                    case WriteRole.Primary when kind == LargeWriteKind.Create:
                        throw StoredKey(lost.Entity.Id);
                    case WriteRole.NewPart:
                        // A foreign row is never overwritten; the entity's own leftover is taken back
                        // by planning the transaction again with that entity over its own rows.
                        var taken = writes[result.FailedIndex].RowKey;
                        if (await rows.GetAsync(lost.Keys.PartitionKey, taken, ct) is { } holder
                            && !LargeValueTable.IsPartOf(holder.Data, lost.Keys.PartitionKey, lost.Keys.RowKey))
                            throw LargeValueTable.ContinuationKeyTaken(lost.Keys.PartitionKey, lost.Keys.RowKey, taken, ServiceErrors.InTransaction(Taken(taken)));
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

                if (attempt >= MaxLargeWriteAttempts)
                    throw ServiceErrors.Coded(new ConcurrencyConflictException(typeName, lost.Entity.Id,
                        ServiceErrors.InTransaction(ServiceErrors.ConditionNotSatisfied("The entity was modified by another writer."))));
                // After a lost race: the same pause as TableStorage (none when an own leftover is taken back).
                if (lostRace)
                    await large.WaitBeforeRetryAsync(attempt + 1, ct);
                var replanned = await PlanBatchUnitsAsync(chunk.ConvertAll(u => u.Entity), kind, taking, ct);
                await CommitBatchUnitsAsync(replanned, kind, attempt + 1, taking, released, atomic, ct);
                continue;
            }

            // Committed: the blobs only the replaced rows referenced go once the whole batch has committed,
            // their grace counted from now (the committed transaction is not failed by a cancelled token).
            AfterLargeCommit?.Invoke();
            DateTimeOffset? committedAt = null;
            foreach (var unit in chunk)
            {
                if (unit.Prior is { Blobs.Count: > 0 })
                {
                    committedAt ??= await large.CommitTimeAsync(CancellationToken.None);
                    released.Add(new ReleasedBlobs(unit.Keys, [.. unit.Prior.Blobs.Except(unit.Plan.BlobPaths, StringComparer.Ordinal)], committedAt));
                }
            }
        }
    }

    // ── Deletes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the entity and its continuation rows in one commit, on condition that the primary is still
    /// the version read (a row another writer changed meanwhile is read again); once it is gone, the blobs
    /// TableStorage's read before a delete finds it referencing (<see cref="LargeValueTable.PriorRowBlobPaths"/>)
    /// are deleted (best effort, after the grace); any others are left for the orphan sweep. When there is no row at the id, the
    /// entity's leftover continuation rows (if any) are removed, so nothing of it stays behind; a
    /// continuation row addressed by its own id is not an entity and is left alone. With
    /// <paramref name="ifMatch"/>, a stored version other than it is a conflict and nothing is deleted.
    /// </summary>
    private async Task<DeleteStatus> DeleteLargeAsync((string PartitionKey, string RowKey) keys, string? ifMatch, CancellationToken ct)
    {
        // No row-key check: a row written before the type opted in may carry any key and stays deletable.
        var id = keys.PartitionKey + EntityId.Separator + keys.RowKey;
        for (var attempt = 1; ; attempt++)
        {
            var stored = await rows.GetAsync(keys.PartitionKey, keys.RowKey, ct);
            if (stored is null)
            {
                // No primary row: all that can be left of the entity are continuation rows nothing declares.
                var leftovers = await ExistingPartKeysAsync(keys, ct);
                if (leftovers.Count == 0)
                    return DeleteStatus.NotFound;
                await rows.CommitAsync(keys.PartitionKey, [.. leftovers.Select(RowWrite.DeleteIfExists)], ct);
                AfterLargeCommit?.Invoke();
                return DeleteStatus.NotFound;
            }
            // A continuation row addressed by its own id is not an entity: nothing is deleted.
            if (LargeValueTable.IsPartRow(stored.Data))
                return DeleteStatus.NotFound;
            // The version read is not the caller's: a conflict with nothing sent (no inner failure), as TableStorage.
            if (ifMatch is not null && !string.Equals(stored.ETag, ifMatch, StringComparison.Ordinal))
                throw new ConcurrencyConflictException(typeName, id, null);

            // A part count that cannot be read (a damaged row) is not "none": the continuation rows
            // that are there go with the row, so a row no read can return is still deletable.
            var writes = new List<RowWrite> { RowWrite.Delete(keys.RowKey, stored.ETag) };
            if (!DeclaresNoParts(stored.Data))
                writes.AddRange((await ExistingPartKeysAsync(keys, ct)).Select(RowWrite.DeleteIfExists));
            var result = await rows.CommitAsync(keys.PartitionKey, writes, ct);
            if (!result.Committed)
            {
                // Written or deleted by another writer since it was read: it is read again, as it is now.
                if (attempt >= MaxLargeWriteAttempts)
                    throw ServiceErrors.Coded(new ConcurrencyConflictException(typeName, id,
                        ServiceErrors.InTransaction(ServiceErrors.ConditionNotSatisfied("The entity was modified by another writer."))));
                continue;
            }

            // The entity is gone: its blobs follow (a cancelled token only bounds how long that may take).
            AfterLargeCommit?.Invoke();
            await large.DeleteReleasedBlobsAsync(keys.PartitionKey, keys.RowKey, LargeValueTable.PriorRowBlobPaths(typeof(T), stored.Data),
                serviceNow: null, onForeign: null, onFailure: null, ct);
            return DeleteStatus.Deleted;
        }
    }

    /// <summary>
    /// Deletes the partition as TableStorage does: the rows are listed, then removed in the same
    /// transactions (<see cref="LargeValueTable.PlanPartitionDelete"/>: a primary and its continuation
    /// rows always together, at most 100 rows each), each one commit of the row store. A token
    /// cancelled between two transactions stops the delete there, with whole entities left; a row
    /// written after the listing survives; a transaction one of whose entities was written after the
    /// listing deletes nothing and its entities are deleted one by one, as they are then, so no
    /// continuation row is left without its primary. Continuation rows listed without their primary go last, and
    /// only when no primary exists for them by then, each only while it is still the version listed, so
    /// an entity written meanwhile is left whole. Then the blobs no remaining row references are deleted.
    /// </summary>
    private async Task<int> DeletePartitionLargeAsync(string partition, CancellationToken ct)
    {
        // The partition's rows in row-key order, as the service lists them, with their versions.
        var listed = new List<TableEntity>();
        var listedVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var stored in ScanAsync(new RowRange(partition), ct))
        {
            listed.Add(stored.Data);
            listedVersions[stored.RowKey] = stored.ETag;
        }
        // Every row goes, continuation rows included; only logical entities are counted.
        var logical = listed.Count(row => !LargeValueTable.IsPartRow(row));
        // Continuation rows listed without their primary go last, as TableStorage deletes them.
        var (whole, leftovers) = LargeValueTable.SeparateListedLeftovers(listed);

        foreach (var transaction in LargeValueTable.PlanPartitionDelete(whole))
        {
            // A cancelled token fails a transaction before it is sent, as the SDK does.
            ct.ThrowIfCancellationRequested();
            BatchTransactions++;
            // As on the service: each primary goes on condition of the version listed (412) and every
            // listed row must still be there (404); otherwise the transaction deletes nothing.
            var result = await rows.CommitAsync(partition,
                [.. transaction.Select(row => RowWrite.Delete(row.RowKey, LargeValueTable.IsPartRow(row) ? null : listedVersions[row.RowKey]))], ct);
            if (!result.Committed)
            {
                // An entity was written after the listing: each entity of the transaction is deleted on
                // its own, as it is now (its primary and the continuation rows that name it), as TableStorage does.
                foreach (var key in transaction.Select(UnitKeyOf).Distinct(StringComparer.Ordinal))
                    await DeleteLargeAsync((partition, key), ifMatch: null, ct);
                continue;
            }
            AfterLargeCommit?.Invoke();
        }
        foreach (var (primaryRowKey, group) in leftovers)
        {
            ct.ThrowIfCancellationRequested();
            // A primary there now owns these rows: they are left alone. Otherwise each row goes only
            // while it is still the row listed (a row a writer took back since is another version).
            if (await rows.GetAsync(partition, primaryRowKey, ct) is { } primary && !LargeValueTable.IsPartRow(primary.Data))
                continue;
            BatchTransactions++;
            foreach (var row in group)
                await rows.CommitAsync(partition, [RowWrite.Delete(row.RowKey, listedVersions[row.RowKey])], ct);
            AfterLargeCommit?.Invoke();
        }

        // The rows are gone: so are the blobs stored under the partition that no row references now (best
        // effort, after the grace; a row another writer created meanwhile keeps its blobs; a cancelled
        // token only bounds how long that may take, the rest is left to the orphan sweep).
        await large.DeletePartitionBlobsAsync(partition, typeof(T), (_, token) => PartitionRowsAsync(partition, token), onListFailure: null, onFailure: null, ct);
        return logical;
    }

    // The row key of the entity a listed row belongs to: its own, or the primary's its _SplitOf cell names.
    private static string UnitKeyOf(TableEntity row) =>
        row.TryGetValue(SystemColumnNames.SplitOf, out var of) && of is string primary ? primary : row.RowKey;

    // ── Orphan sweep (any T: blobs under the partition may come from any type sharing the table) ──

    /// <inheritdoc />
    public Task<int> SweepOrphanBlobsAsync(string partitionKey, TimeSpan olderThan, CancellationToken ct = default)
    {
        LargeValueTable.ValidateSweep(partitionKey, olderThan);
        var partition = NormalizeId(partitionKey); // match stored (normalized) PartitionKeys, as DeletePartitionAsync
        return Run(large.SweepOrphanBlobsAsync(partition, olderThan, typeof(T), (_, token) => PartitionRowsAsync(partition, token), ct));
    }

    bool IBlobOffloadState.HasBlobOffload => large.HasBlobOffload;

    /// <inheritdoc />
    public Task<int> SweepOrphanBlobsAsync(TimeSpan olderThan, CancellationToken ct = default) =>
        // The listed partitions are stored keys already: they are not normalized again.
        Run(large.SweepTableOrphanBlobsAsync(olderThan, typeof(T), (partition, _, token) => PartitionRowsAsync(partition, token), ct));

    // Every stored row of the partition (continuation rows included), each a copy. The projection is not
    // applied: a whole row holds every cell the sweep would have selected.
    private async IAsyncEnumerable<TableEntity> PartitionRowsAsync(string partition,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        await foreach (var row in ScanAsync(new RowRange(partition), ct))
            yield return RowCells.Copy(row.Data);
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    // Whether the predicate holds for the primary row as a server-side filter sees it.
    private static bool MatchesStoredRow(StoredRow stored, Func<T, bool> matches)
    {
        T view;
        try
        {
            view = ProjectedRows.FilterView<T>(RowCells.Copy(stored.Data));
        }
        catch (TableValueIntegrityException)
        {
            // The row's own cells cannot be read even without its large values: it is assembled, and its
            // read reports the failure.
            return true;
        }
        view.ETag = stored.ETag;
        view.Timestamp = stored.Timestamp;
        return matches(view);
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

    /// <summary>
    /// The stored entity row's version, part count and blobs (null when there is none), and the version of
    /// whatever row holds the key (a continuation row is no entity, but it is in the way of an insert). The
    /// blobs are those TableStorage's point read before a write learns: the references in the cells it
    /// selects (<see cref="LargeValueTable.PriorRowBlobPaths"/>); one in any other cell is left for the sweep.
    /// </summary>
    private async Task<(PriorRow? Prior, string? AtKey)> ReadPriorAsync((string PartitionKey, string RowKey) keys, CancellationToken ct)
    {
        var row = await rows.GetAsync(keys.PartitionKey, keys.RowKey, ct);
        if (row is null)
            return default;
        return LargeValueTable.IsPartRow(row.Data)
            ? (null, row.ETag)
            : (new PriorRow(row.ETag, LargeValueTable.PartCount(row.Data), LargeValueTable.PriorRowBlobPaths(typeof(T), row.Data)), row.ETag);
    }

    /// <summary>
    /// The entity a stored primary row holds: its continuation rows read and merged in, its offloaded
    /// values downloaded. A writer that replaces or deletes the row meanwhile can remove a continuation
    /// row or a blob this read still needs: as in TableStorage, the row is then read again after every
    /// failed attempt, the last included: an unchanged version is a genuine integrity failure; a row
    /// deleted meanwhile is null; a changed version retries the read from the new version after the
    /// writers' pause, and when the version changed during each of the <see cref="MaxLargeReadAttempts"/>
    /// attempts the read throws <see cref="ConcurrencyConflictException"/> with the last integrity failure
    /// as its inner exception.
    /// </summary>
    private async Task<T?> MaterializeAsync(StoredRow row, CancellationToken ct)
    {
        var keys = (row.PartitionKey, row.RowKey);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var logical = LargeValueTable.PartCount(row.Data) == 0
                    ? RowCells.Copy(row.Data)
                    : RowCells.Copy(LargeValueTable.Assemble(row.Data, (await PartRowsAsync(keys, ct)).Select(p => p.Data)));
                var entity = await large.ReadAsync<T>(logical, ct);
                entity.ETag = row.ETag;
                entity.Timestamp = row.Timestamp;
                return entity;
            }
            catch (TableValueIntegrityException failure)
            {
                var current = await rows.GetAsync(keys.PartitionKey, keys.RowKey, ct);
                if (current is null || LargeValueTable.IsPartRow(current.Data))
                    return null;
                if (string.Equals(current.ETag, row.ETag, StringComparison.Ordinal))
                    throw;
                if (attempt >= MaxLargeReadAttempts)
                    throw new ConcurrencyConflictException(
                        ConcurrencyMessages.ReadKeptChanging(typeName, keys.PartitionKey, keys.RowKey, MaxLargeReadAttempts), failure);
                await large.WaitBeforeRetryAsync(attempt + 1, ct);
                row = current;
            }
        }
    }

    /// <summary>How many times a read whose blobs belonged to another version of the row is attempted in all (as TableStorage).</summary>
    private const int MaxLargeReadAttempts = LargeValueTable.MaxWriteAttempts;

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

    private async Task EnforceProtectedPropertiesLargeAsync(T entity, CancellationToken ct)
    {
        if (ProtectedProps.Count == 0)
            return;
        var keys = EntityId.Split(NormalizeId(entity.Id));
        await EnsureTableAsync(ct);
        var row = await rows.GetAsync(keys.PartitionKey, keys.RowKey, ct);
        var stored = row is null || LargeValueTable.IsPartRow(row.Data) ? null : await MaterializeAsync(row, ct);
        CheckProtectedProperties(stored, entity);
    }
}
