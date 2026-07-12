using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Azure;
using Azure.Data.Tables;

namespace Unified.Data.Tables.InMemory;

/// <summary>
/// Faithful in-memory <see cref="IStorage{T}"/> for tests, dev mode, and offline runtime. Rows are
/// stored as serialized <see cref="TableEntity"/>s and round-trip through the REAL
/// <see cref="TableEntitySerializer"/> on every read and write, so code under test exercises
/// production serialization behaviour (decimal-as-double, enum-as-string, <c>Parent_Child</c>
/// flattening, <c>__Json</c>/<c>__GZip</c> cells, 64&#160;KB handling) instead of hiding it.
/// Semantics mirror <c>TableStorage&lt;T&gt;</c>: identical id normalization and first-<c>'|'</c>
/// key splitting, 409 <see cref="RequestFailedException"/> on duplicate create, 404 on updating a
/// missing row, ETag simulation with 412 conflicts, idempotent delete, and lexical
/// (PartitionKey, RowKey) result ordering.
/// </summary>
/// <typeparam name="T">The entity type; must derive from <see cref="Entity"/> and have a public parameterless constructor.</typeparam>
public sealed class InMemoryStorage<T> : IStorage<T> where T : Entity, new()
{
    private static readonly List<(PropertyInfo Prop, ProtectedPropertyAttribute Attr)> ProtectedProps =
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Prop: p, Attr: p.GetCustomAttribute<ProtectedPropertyAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Prop, x.Attr!))
            .ToList();

    private sealed record StoredRow(TableEntity Data, long Version, DateTimeOffset Timestamp)
    {
        public string ETagString() => $"W/\"{Version.ToString(CultureInfo.InvariantCulture)}\"";
    }

    private readonly Dictionary<(string PartitionKey, string RowKey), StoredRow> rows = [];
    private readonly object gate = new();
    private readonly IProtectedPropertyAuthorizer? authorizer;
    private long versionCounter;

    /// <summary>Creates a store without protected-property enforcement.</summary>
    public InMemoryStorage()
    {
    }

    /// <summary>
    /// Creates a store that gates <see cref="ProtectedPropertyAttribute"/>-decorated properties
    /// through <paramref name="authorizer"/>, mirroring <c>TableStorage&lt;T&gt;</c>.
    /// </summary>
    public InMemoryStorage(IProtectedPropertyAuthorizer? authorizer) => this.authorizer = authorizer;

    // ── Test conveniences (not part of IStorage) ────────────────────────────

    /// <summary>Number of rows currently stored.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return rows.Count;
            }
        }
    }

    /// <summary>Removes all rows.</summary>
    public void Clear()
    {
        lock (gate)
        {
            rows.Clear();
        }
    }

    /// <summary>Deserialized copies of every stored row (lexical key order), for assertions.</summary>
    public IReadOnlyList<T> Snapshot()
    {
        lock (gate)
        {
            return OrderedRows().Select(kv => Materialize(kv.Value)).ToList();
        }
    }

    // ── IStorage<T> ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<T> CreateAsync(T entity, CancellationToken ct = default)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }

        entity.CreatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null;
        entity.Id = EntityId.Normalize(entity.Id);
        var keys = EntityId.Split(entity.Id);

        lock (gate)
        {
            if (rows.ContainsKey(keys))
                throw new DuplicateKeyException(typeof(T).Name, entity.Id,
                    new RequestFailedException(409, $"The specified entity already exists. Id: {entity.Id}"));
            entity.ETag = Store(keys, entity).ETagString();
        }

        return Task.FromResult(entity);
    }

    /// <inheritdoc />
    public Task<T> UpsertAsync(T entity, CancellationToken ct = default)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }

        EnforceProtectedProperties(entity);

        if (entity.CreatedAt == default)
            entity.CreatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null;
        entity.Id = EntityId.Normalize(entity.Id);
        var keys = EntityId.Split(entity.Id);

        lock (gate)
        {
            entity.ETag = Store(keys, entity).ETagString();
        }

        return Task.FromResult(entity);
    }

    /// <inheritdoc />
    public Task<T> UpdateAsync(T entity, CancellationToken ct = default)
        => UpdateAsync(entity, ConcurrencyMode.Auto, ct);

    /// <inheritdoc />
    public Task<T> UpdateAsync(T entity, ConcurrencyMode mode, CancellationToken ct = default)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }

        EnforceProtectedProperties(entity);

        var callerSuppliedETag = !string.IsNullOrEmpty(entity.ETag);
        if (mode == ConcurrencyMode.Strict && !callerSuppliedETag)
            throw new InvalidOperationException(
                $"ConcurrencyMode.Strict requires {typeof(T).Name}.ETag — read the entity first and round-trip its ETag.");

        var suppliedETag = entity.ETag;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null;
        entity.Id = EntityId.Normalize(entity.Id);
        var keys = EntityId.Split(entity.Id);

        lock (gate)
        {
            if (!rows.TryGetValue(keys, out var existing))
                throw new RequestFailedException(404, $"The specified resource does not exist. Id: {entity.Id}");

            // Strict — and Auto with a caller-round-tripped ETag — enforce the row version; a
            // mismatch surfaces as a conflict with no retry, exactly like TableStorage<T>. Auto
            // without an ETag converges last-writer-wins (the real cached-ETag path retries to the
            // same outcome), and LastWriterWins skips the check by definition.
            if (mode != ConcurrencyMode.LastWriterWins && callerSuppliedETag
                && !string.Equals(existing.ETagString(), suppliedETag, StringComparison.Ordinal))
            {
                throw new ConcurrencyConflictException(typeof(T).Name, entity.Id,
                    new RequestFailedException(412, "Precondition Failed: the entity was modified by another writer."));
            }

            entity.ETag = Store(keys, entity).ETagString();
        }

        return Task.FromResult(entity);
    }

    /// <inheritdoc />
    public Task<string> UpdateAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }
        ArgumentNullException.ThrowIfNull(builderAction);

        var builder = new UpdateBuilder<T>();
        builderAction(builder);
        if (builder.Updates.Count == 0)
        {
            throw new InvalidOperationException("No updates specified.");
        }

        var keys = EntityId.Split(EntityId.Normalize(id));

        lock (gate)
        {
            if (!rows.TryGetValue(keys, out var existing))
                throw new RequestFailedException(404, $"The specified resource does not exist. Id: {id}");

            // Conditional merge (WithETag): the merge only applies to the version that was read.
            if (builder.ETag is not null
                && !string.Equals(existing.ETagString(), builder.ETag, StringComparison.Ordinal))
            {
                throw new ConcurrencyConflictException(typeof(T).Name, id,
                    new RequestFailedException(412, "Precondition Failed: the entity was modified by another writer."));
            }

            // Server-side Merge: only the declared (flattened) columns change; the rest survive.
            var merged = CopyOf(existing.Data);
            foreach (var (name, value) in builder.Updates)
            {
                foreach (var (col, cell) in TableEntitySerializer.FlattenProperty(name, value))
                {
                    merged[col] = cell;
                }
            }
            merged[nameof(Entity.UpdatedAt)] = DateTimeOffset.UtcNow;

            var stored = new StoredRow(merged, ++versionCounter, DateTimeOffset.UtcNow);
            rows[keys] = stored;
            return Task.FromResult(stored.ETagString());
        }
    }

    /// <inheritdoc />
    public Task<T?> OneAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }

        var keys = EntityId.Split(EntityId.Normalize(id));

        lock (gate)
        {
            return Task.FromResult(rows.TryGetValue(keys, out var row) ? Materialize(row) : null);
        }
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }

        var keys = EntityId.Split(EntityId.Normalize(id));

        lock (gate)
        {
            return Task.FromResult(rows.ContainsKey(keys));
        }
    }

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default)
    {
        lock (gate)
        {
            var matches = OrderedRows()
                .Where(kv => string.IsNullOrWhiteSpace(partition) || kv.Key.PartitionKey == partition)
                .Select(kv => Materialize(kv.Value))
                .ToList();
            return Task.FromResult<IEnumerable<T>>(matches);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<T>>(Bounded(options).ToList());
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        List<T> snapshot;
        lock (gate)
        {
            snapshot = Bounded(options ?? new QueryOptions()).ToList();
        }

        foreach (var entity in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return entity;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var partition = string.IsNullOrWhiteSpace(options.Partition) ? null : options.Partition;
        var rowKeyPrefix = string.IsNullOrWhiteSpace(options.RowKeyPrefix) ? null : options.RowKeyPrefix;

        if (rowKeyPrefix is not null && partition is null)
            throw new ArgumentException(
                "RowKeyPrefix requires Partition — a cross-partition RowKey range is a full table scan wearing a filter.",
                nameof(options));
        if (options.Take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.Take, "Take (page size) must be positive.");

        var pageSize = options.Take is int t ? Math.Min(t, 1000) : 100;
        var fingerprint = PageCursor.Fingerprint(partition, rowKeyPrefix, pageSize);

        (string PartitionKey, string RowKey)? after = null;
        if (options.ContinuationToken is not null)
        {
            var inner = PageCursor.Decode(options.ContinuationToken, fingerprint);
            var sep = inner.IndexOf(' ');
            if (sep < 0)
                throw new ArgumentException(
                    "Malformed in-memory continuation token (was it issued by a different backend? " +
                    "cursors are backend-specific).", nameof(options));
            after = (inner.Substring(0, sep), inner.Substring(sep + 1));
        }

        lock (gate)
        {
            IEnumerable<KeyValuePair<(string PartitionKey, string RowKey), StoredRow>> rowsQuery = OrderedRows()
                .Where(kv => partition is null || kv.Key.PartitionKey == partition)
                .Where(kv => rowKeyPrefix is null || kv.Key.RowKey.StartsWith(rowKeyPrefix, StringComparison.Ordinal));
            if (after is { } a)
                rowsQuery = rowsQuery.Where(kv => KeyCompare(kv.Key, a) > 0);

            var candidate = rowsQuery.Take(pageSize + 1).ToList();
            var hasMore = candidate.Count > pageSize;
            var pageRows = hasMore ? candidate.Take(pageSize).ToList() : candidate;
            var items = pageRows.Select(kv => Materialize(kv.Value)).ToList();

            string? next = null;
            if (hasMore)
            {
                var last = pageRows[pageRows.Count - 1].Key;
                next = PageCursor.Encode(fingerprint, last.PartitionKey + " " + last.RowKey);
            }

            return Task.FromResult(new EntityPage<T>(items, next));
        }
    }

    // Cursor ordering matches OrderedRows: Ordinal PartitionKey, then Ordinal RowKey.
    private static int KeyCompare((string PartitionKey, string RowKey) key, (string PartitionKey, string RowKey) after)
    {
        var c = string.CompareOrdinal(key.PartitionKey, after.PartitionKey);
        return c != 0 ? c : string.CompareOrdinal(key.RowKey, after.RowKey);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be positive.");

        // Reject predicates the real store cannot translate to OData, so a green fake test means the
        // same predicate holds against Azure Tables — then evaluate the caller's real semantics.
        _ = TableFilterTranslator.Translate(predicate);
        var matches = predicate.Compile();
        var scope = string.IsNullOrWhiteSpace(partition) ? null : partition;

        lock (gate)
        {
            IEnumerable<T> query = OrderedRows()
                .Where(kv => scope is null || kv.Key.PartitionKey == scope)
                .Select(kv => Materialize(kv.Value))
                .Where(matches);
            if (take is int t)
                query = query.Take(t);
            return Task.FromResult<IReadOnlyList<T>>(query.ToList());
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> QueryStreamAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var snapshot = await QueryAsync(predicate, partition, take, ct);
        foreach (var entity in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return entity;
        }
    }

    /// <inheritdoc />
    public async Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, string? partition = null, CancellationToken ct = default)
    {
        var first = await QueryAsync(predicate, partition, 1, ct);
        return first.Count > 0;
    }

    /// <inheritdoc />
    public Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
        => WriteBatch(entities, upsert: false);

    /// <inheritdoc />
    public Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
        => WriteBatch(entities, upsert: true);

    /// <inheritdoc />
    public Task<int> CountAsync(string? partition = null, CancellationToken ct = default)
    {
        lock (gate)
        {
            var count = string.IsNullOrWhiteSpace(partition)
                ? rows.Count
                : rows.Keys.Count(k => k.PartitionKey == partition);
            return Task.FromResult(count);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }

        var keys = EntityId.Split(EntityId.Normalize(id));

        lock (gate)
        {
            // Deleting a missing row is a no-op, mirroring the SDK's idempotent DeleteEntity.
            rows.Remove(keys);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(partition))
            throw new ArgumentNullException(nameof(partition));

        lock (gate)
        {
            var doomed = rows.Keys.Where(k => k.PartitionKey == partition).ToList();
            foreach (var key in doomed)
                rows.Remove(key);
            return Task.FromResult(doomed.Count);
        }
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private Task<int> WriteBatch(IReadOnlyCollection<T> entities, bool upsert)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return Task.FromResult(0);

        var now = DateTimeOffset.UtcNow;
        var prepared = new List<((string, string) Keys, T Entity)>(entities.Count);
        var withinBatch = new HashSet<(string, string)>();
        foreach (var entity in entities)
        {
            if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
                throw new ArgumentException("Every batched entity must be non-null and have an Id.", nameof(entities));

            if (!upsert || entity.CreatedAt == default)
                entity.CreatedAt = now;
            entity.UpdatedAt = now;
            entity.Timestamp = null;
            // Parity with TableStorage: batch sub-responses aren't correlated back, so the ETag is
            // unknown after a batch — re-read before optimistic updates.
            entity.ETag = null;
            entity.Id = EntityId.Normalize(entity.Id);
            var keys = EntityId.Split(entity.Id);

            if (!upsert && !withinBatch.Add(keys))
                throw new DuplicateKeyException(typeof(T).Name, entity.Id,
                    new RequestFailedException(400, $"InvalidDuplicateRow: duplicate key within the batch. Id: {entity.Id}"));
            prepared.Add((keys, entity));
        }

        lock (gate)
        {
            if (!upsert)
            {
                // Validate before applying — a duplicate fails the batch without partial writes
                // (the fake treats the whole call as one transaction; close enough to the real
                // per-partition 100-row transactions for test purposes).
                var duplicate = prepared.FirstOrDefault(p => rows.ContainsKey(p.Keys));
                if (duplicate.Entity is not null)
                    throw new DuplicateKeyException(typeof(T).Name, duplicate.Entity.Id,
                        new RequestFailedException(409, $"The specified entity already exists. Id: {duplicate.Entity.Id}"));
            }

            foreach (var (keys, entity) in prepared)
                Store(keys, entity);
        }

        return Task.FromResult(prepared.Count);
    }

    private IEnumerable<T> Bounded(QueryOptions options)
    {
        var partition = string.IsNullOrWhiteSpace(options.Partition) ? null : options.Partition;
        var rowKeyPrefix = string.IsNullOrWhiteSpace(options.RowKeyPrefix) ? null : options.RowKeyPrefix;

        if (rowKeyPrefix is not null && partition is null)
            throw new ArgumentException(
                "RowKeyPrefix requires Partition — a cross-partition RowKey range is a full table scan wearing a filter.",
                nameof(options));
        if (options.Take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.Take, "Take must be positive.");
        if (options.ContinuationToken is not null)
            throw new ArgumentException(
                "ContinuationToken is only honored by QueryPageAsync — use QueryPageAsync to resume paging.",
                nameof(options));

        var matches = OrderedRows()
            .Where(kv => partition is null || kv.Key.PartitionKey == partition)
            .Where(kv => rowKeyPrefix is null || kv.Key.RowKey.StartsWith(rowKeyPrefix, StringComparison.Ordinal))
            .Select(kv => Materialize(kv.Value));

        return options.Take is int take ? matches.Take(take) : matches;
    }

    private IEnumerable<KeyValuePair<(string PartitionKey, string RowKey), StoredRow>> OrderedRows() =>
        rows.OrderBy(kv => kv.Key.PartitionKey, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.RowKey, StringComparer.Ordinal);

    private StoredRow Store((string PartitionKey, string RowKey) keys, T entity)
    {
        var row = new StoredRow(
            entity.ToTableEntity(keys.PartitionKey, keys.RowKey),
            ++versionCounter,
            DateTimeOffset.UtcNow);
        rows[keys] = row;
        return row;
    }

    private static T Materialize(StoredRow row)
    {
        var entity = CopyOf(row.Data).FromTableEntity<T>();
        entity.ETag = row.ETagString();
        entity.Timestamp = row.Timestamp;
        return entity;
    }

    // Isolate stored state from caller mutations (the serializer round-trip already deep-copies
    // values on materialize; this guards the stored TableEntity itself).
    private static TableEntity CopyOf(TableEntity source)
    {
        var copy = new TableEntity(source.PartitionKey, source.RowKey);
        foreach (var kv in source)
        {
            if (kv.Key is not ("PartitionKey" or "RowKey" or "odata.etag" or "Timestamp"))
                copy[kv.Key] = kv.Value;
        }
        return copy;
    }

    private void EnforceProtectedProperties(T entity)
    {
        if (ProtectedProps.Count == 0)
            return;

        T? stored;
        var keys = EntityId.Split(EntityId.Normalize(entity.Id));
        lock (gate)
        {
            stored = rows.TryGetValue(keys, out var row) ? Materialize(row) : null;
        }
        if (stored is null)
            return;

        foreach (var (prop, attr) in ProtectedProps)
        {
            var oldVal = prop.GetValue(stored);
            var newVal = prop.GetValue(entity);
            if (!Equals(oldVal, newVal) && !(authorizer?.IsAllowed(attr.Roles) ?? false))
            {
                throw new UnauthorizedAccessException(
                    $"Property '{prop.Name}' on {typeof(T).Name} is protected (requires roles: {attr.Roles}). " +
                    $"The current caller is not authorised to change it from '{oldVal}' to '{newVal}'.");
            }
        }
    }
}

