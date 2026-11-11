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

        for (var attempt = 1; attempt <= MaxLargeWriteAttempts; attempt++)
        {
            PriorRow? prior;
            lock (gate)
            {
                prior = kind == LargeWriteKind.Create ? null : ReadPrior(keys);
            }
            if (kind == LargeWriteKind.Replace)
            {
                if (prior is null)
                    throw new RequestFailedException(404, $"The specified resource does not exist. Id: {entity.Id}");
                if (strict && !string.Equals(ETagOf(prior.Version), ifMatch, StringComparison.Ordinal))
                    throw Conflict(entity.Id);
            }

            var plan = await large.PlanAsync(entity, keys.PartitionKey, keys.RowKey, prior?.Parts ?? 0,
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
                    continue;
                }

                if (TakenPartKey(keys, plan) is { } taken)
                    throw LargeValueTable.ContinuationKeyTaken(keys.PartitionKey, keys.RowKey, taken, Taken(taken));
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

    // Under the gate: the key of the first new continuation row (one the stored row does not declare) that
    // another row already holds; TableStorage inserts a new part, so the service fails it with 409.
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

    /// <summary>One entity of a large-value batch: the stored row it was planned against and its rows.</summary>
    private sealed record BatchUnit(T Entity, (string PartitionKey, string RowKey) Keys, PriorRow? Prior, LargeValueWritePlan Plan);

    /// <summary>
    /// The batch contract of TableStorage&lt;T&gt;: per partition, each entity's rows are one indivisible
    /// unit, packed in order into the same transactions (<see cref="LargeValueTable.PlanTransactions"/>),
    /// each applied atomically under the gate. A transaction's blobs are uploaded just before it, and
    /// released ones deleted after each commit. A repeated key in a create batch was
    /// refused before this point; in an upsert batch its last occurrence is written. An existing key fails
    /// its create transaction; an upsert transaction that lost a race is planned again and retried.
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
                var units = await PlanBatchUnitsAsync(group.Select(g => g.Entity).ToList(), kind, ct);
                await CommitBatchUnitsAsync(units, kind, attempt: 1, ct);
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

    private async Task<List<BatchUnit>> PlanBatchUnitsAsync(List<T> entities, LargeWriteKind kind, CancellationToken ct)
    {
        var units = new List<BatchUnit>(entities.Count);
        foreach (var entity in entities)
        {
            var keys = EntityId.Split(entity.Id);
            PriorRow? prior;
            lock (gate)
            {
                prior = kind == LargeWriteKind.Create ? null : ReadPrior(keys);
            }
            var plan = await large.PlanAsync(entity, keys.PartitionKey, keys.RowKey, prior?.Parts ?? 0,
                _ =>
                {
                    lock (gate)
                    {
                        return Task.FromResult(ExistingPartKeys(keys));
                    }
                }, ct);
            units.Add(new BatchUnit(entity, keys, prior, plan));
        }
        return units;
    }

    private async Task CommitBatchUnitsAsync(List<BatchUnit> units, LargeWriteKind kind, int attempt, CancellationToken ct)
    {
        foreach (var range in LargeValueTable.PlanTransactions(units.Select(u => u.Plan).ToList()))
        {
            var chunk = units.GetRange(range.Start, range.Count);
            // Every blob exists before the rows that reference it, uploaded just before its own transaction.
            foreach (var unit in chunk)
                await large.UploadAsync(unit.Plan, ct);
            // A cancelled token fails a transaction before it is sent, as the SDK does.
            ct.ThrowIfCancellationRequested();
            BatchUnit? lost = null;
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
                    if (TakenPartKey(unit.Keys, unit.Plan) is { } taken)
                        throw LargeValueTable.ContinuationKeyTaken(unit.Keys.PartitionKey, unit.Keys.RowKey, taken, Taken(taken));
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
                var replanned = await PlanBatchUnitsAsync(chunk.ConvertAll(u => u.Entity), kind, ct);
                await CommitBatchUnitsAsync(replanned, kind, attempt + 1, ct);
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
    /// Deletes the entity and its continuation rows in one step under the gate (a missing row is a
    /// no-op); once it is gone, the blobs it referenced are deleted (best effort, after the grace).
    /// </summary>
    private async Task DeleteLargeAsync((string PartitionKey, string RowKey) keys, CancellationToken ct)
    {
        // No row-key check: a row written before the type opted in may carry any key and stays
        // deletable; a continuation row addressed by id reads as missing below, so it is never deleted.
        IReadOnlyList<string> released;
        lock (gate)
        {
            var prior = ReadPrior(keys);
            if (prior is null)
                return;
            if (prior.Parts > 0)
            {
                foreach (var part in ExistingPartKeys(keys))
                    rows.Remove((keys.PartitionKey, part));
            }
            rows.Remove(keys);
            released = prior.Blobs;
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

    private async Task<EntityPage<T>> PageLargeAsync(
        string? partition, string? rowKeyPrefix, int pageSize, string fingerprint,
        (string PartitionKey, string RowKey)? after, CancellationToken ct)
    {
        List<StoredRow> pageRows;
        string? next = null;
        lock (gate)
        {
            IEnumerable<KeyValuePair<(string PartitionKey, string RowKey), StoredRow>> rowsQuery = LogicalRows()
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
            pageRows = page.Select(kv => Assembled(kv.Value)).ToList();
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
    /// TableStorage, the row is then read again and, when its version changed, the read retries from the
    /// new version (at most <see cref="MaxLargeReadAttempts"/> attempts); an unchanged version is a genuine
    /// integrity failure; a row deleted meanwhile is null.
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
            catch (TableValueIntegrityException) when (attempt < MaxLargeReadAttempts)
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
                row = current;
            }
        }
    }

    /// <summary>How many times a read whose blobs belonged to another version of the row is attempted in all (as TableStorage).</summary>
    private const int MaxLargeReadAttempts = 3;

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
