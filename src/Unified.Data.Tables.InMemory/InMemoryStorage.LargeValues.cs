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
        LargeValueTable.ValidateRowKey(keys.RowKey);
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
                var current = ReadPrior(keys);
                if (kind == LargeWriteKind.Create)
                {
                    if (rows.ContainsKey(keys))
                        throw new DuplicateKeyException(typeof(T).Name, entity.Id,
                            new RequestFailedException(409, $"The specified entity already exists. Id: {entity.Id}"));
                }
                else if (current?.Version != prior?.Version)
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

                version = Commit(keys, plan);
            }

            // The swap committed: the blobs only the replaced row referenced can go (the same content
            // written again maps to the same name, which the new row still references).
            if (prior is { Blobs.Count: > 0 })
                await large.DeleteReleasedBlobsAsync(prior.Blobs.Except(plan.BlobPaths, StringComparer.Ordinal), onFailure: null, ct);
            entity.ETag = ETagOf(version);
            return entity;
        }

        throw new ConcurrencyConflictException(typeof(T).Name, entity.Id, lost);
    }

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

    private async Task<int> WriteBatchLargeAsync(List<((string, string) Keys, T Entity)> prepared, bool upsert, CancellationToken ct)
    {
        if (!upsert)
        {
            // Same up-front duplicate check as the plain batch: a known duplicate fails the call before
            // anything is written. Past it, each entity is written atomically with its own continuation
            // rows (no cross-entity atomicity), exactly as TableStorage<T> writes a split-aware batch.
            lock (gate)
            {
                var duplicate = prepared.FirstOrDefault(p => rows.ContainsKey(p.Keys));
                if (duplicate.Entity is not null)
                    throw new DuplicateKeyException(typeof(T).Name, duplicate.Entity.Id,
                        new RequestFailedException(409, $"The specified entity already exists. Id: {duplicate.Entity.Id}"));
            }
        }

        foreach (var (_, entity) in prepared)
        {
            await WriteLargeAsync(entity, upsert ? LargeWriteKind.Upsert : LargeWriteKind.Create, ifMatch: null, ct);
            // Parity with TableStorage: a batch reports no version per entity — re-read before optimistic updates.
            entity.ETag = null;
        }
        return prepared.Count;
    }

    // ── Deletes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the entity and its continuation rows in one step under the gate (a missing row is a
    /// no-op); once it is gone, the blobs it referenced are deleted (best effort, after the grace).
    /// </summary>
    private async Task DeleteLargeAsync((string PartitionKey, string RowKey) keys, CancellationToken ct)
    {
        LargeValueTable.ValidateRowKey(keys.RowKey);
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

        await large.DeleteReleasedBlobsAsync(released, onFailure: null, ct);
    }

    private async Task<int> DeletePartitionLargeAsync(string partition, CancellationToken ct)
    {
        int logical;
        lock (gate)
        {
            var doomed = rows.Where(kv => kv.Key.PartitionKey == partition).ToList();
            logical = doomed.Count(kv => !LargeValueTable.IsPartRow(kv.Value.Data));
            foreach (var (key, _) in doomed)
                rows.Remove(key);
        }

        // The rows are gone: so are the blobs stored under the partition (best effort, after the grace).
        await large.DeletePartitionBlobsAsync(partition, onListFailure: null, onFailure: null, ct);
        return logical;
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
            var entity = await MaterializeAsync(row, ct);
            if (matches(entity))
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

    private async Task<T> MaterializeAsync(StoredRow row, CancellationToken ct)
    {
        var entity = await large.ReadAsync<T>(CopyOf(row.Data), ct);
        entity.ETag = row.ETagString();
        entity.Timestamp = row.Timestamp;
        return entity;
    }

    private async Task<List<T>> MaterializeAllAsync(List<StoredRow> source, CancellationToken ct)
    {
        var entities = new List<T>(source.Count);
        foreach (var row in source)
            entities.Add(await MaterializeAsync(row, ct));
        return entities;
    }

    /// <summary>For a large-value-aware <typeparamref name="T"/>, refuses a row key reserved for continuation rows.</summary>
    private void RefuseReservedRowKey(string id)
    {
        if (LargeValueAware)
            LargeValueTable.ValidateRowKey(EntityId.Split(NormalizeId(id)).RowKey);
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
