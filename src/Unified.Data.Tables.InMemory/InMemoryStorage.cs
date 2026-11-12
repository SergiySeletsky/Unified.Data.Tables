using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Azure;
using Azure.Data.Tables;
using Unified.Data.Tables.Providers;

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
/// <typeparam name="T">The entity type; implements <see cref="IEntity"/> (derive from <see cref="Entity"/> for the convenience base) and has a public parameterless constructor.</typeparam>
public sealed partial class InMemoryStorage<T> : IStorage<T>, IOrphanBlobSweeper where T : class, IEntity, new()
{
    private static readonly List<(PropertyInfo Prop, ProtectedPropertyAttribute Attr)> ProtectedProps =
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Prop: p, Attr: p.GetCustomAttribute<ProtectedPropertyAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Prop, x.Attr!))
            .ToList();

    private sealed record StoredRow(TableEntity Data, long Version, DateTimeOffset Timestamp)
    {
        public string ETagString() => ETagOf(Version);
    }

    private static string ETagOf(long version) => $"W/\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    // True when T has a member that may be stored as split cells, continuation rows or a blob. Every
    // large-value branch below is reached only for such a T; any other T behaves exactly as before.
    private static readonly bool LargeValueAware = LargeValueTable.IsLargeValueAware(typeof(T));

    /// <summary>How many times in all a write that keeps losing its condition to concurrent writers is attempted (the number TableStorage uses).</summary>
    private const int MaxLargeWriteAttempts = LargeValueTable.MaxWriteAttempts;

    private readonly Dictionary<(string PartitionKey, string RowKey), StoredRow> rows = [];
    private readonly object gate = new();
    private readonly IProtectedPropertyAuthorizer? authorizer;
    private readonly IdNormalization idNormalization;
    private readonly bool implicitLastWriterWins;
    private readonly LargeValueTable large;

    // Whether the store has a blob container for offloaded values (the hosted sweep skips it otherwise).
    private readonly bool hasBlobOffload;
    private long versionCounter;

    /// <summary>Creates a store without protected-property enforcement.</summary>
    public InMemoryStorage()
        : this(null, null)
    {
    }

    /// <summary>
    /// Creates a store that gates <see cref="ProtectedPropertyAttribute"/>-decorated properties
    /// through <paramref name="authorizer"/>, mirroring <c>TableStorage&lt;T&gt;</c>.
    /// </summary>
    public InMemoryStorage(IProtectedPropertyAuthorizer? authorizer)
        : this(authorizer, null)
    {
    }

    /// <summary>
    /// Creates a store, optionally gating <see cref="ProtectedPropertyAttribute"/>-decorated
    /// properties through <paramref name="authorizer"/> and honouring
    /// <see cref="UnifiedTableStorageOptions.IdNormalization"/>, mirroring <c>TableStorage&lt;T&gt;</c>
    /// so a green fake test holds against Azure. The trailing defaults let the DI container select
    /// this constructor even when neither optional service is registered.
    /// </summary>
    public InMemoryStorage(IProtectedPropertyAuthorizer? authorizer = null, UnifiedTableStorageOptions? options = null)
    {
        this.authorizer = authorizer;
        var opts = options ?? new UnifiedTableStorageOptions();
        idNormalization = opts.IdNormalization;
        implicitLastWriterWins = opts.ImplicitLastWriterWins;
        // Blob offload gets this store's own in-memory container (no BlobServiceClient), named under the
        // table name TableStorage<T> would resolve, so blob names match the real store's.
        var tableName = opts.TableNameResolver?.Invoke(typeof(T));
        hasBlobOffload = opts.BlobOffload is not null;
        large = new LargeValueTable(string.IsNullOrWhiteSpace(tableName) ? typeof(T).Name : tableName,
            opts.BlobOffload, hasBlobOffload ? Blobs : null, typeof(T));
    }

    /// <summary>The offloaded-value blobs of this store (for tests); empty when blob offload is not configured.</summary>
    internal InMemoryOffloadBlobStore Blobs { get; } = new();

    /// <summary>Stored rows including continuation rows (for tests); <see cref="Count"/> counts entities.</summary>
    internal int PhysicalRowCount
    {
        get
        {
            lock (gate)
            {
                return rows.Count;
            }
        }
    }

    /// <summary>The row keys stored in <paramref name="partitionKey"/>, continuation rows included, in order (for tests).</summary>
    internal IReadOnlyList<string> PhysicalRowKeys(string partitionKey)
    {
        lock (gate)
        {
            return rows.Keys.Where(k => k.PartitionKey == partitionKey).Select(k => k.RowKey)
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// How many entity-group transactions the batch writes and the partition deletes of a
    /// large-value-aware type have attempted, committed or not (for tests; TableStorage sends one
    /// <c>$batch</c> request each).
    /// </summary>
    internal int BatchTransactions { get; set; }

    /// <summary>
    /// Runs after each large-value write or delete has committed, before its blob cleanup (for tests);
    /// a batch write and a partition delete run it after each of their transactions.
    /// </summary>
    internal Action? AfterLargeCommit { get; set; }

    /// <summary>A copy of the stored row at these keys, continuation rows included (for tests); null when absent.</summary>
    internal TableEntity? PhysicalRow(string partitionKey, string rowKey)
    {
        lock (gate)
        {
            return rows.TryGetValue((partitionKey, rowKey), out var row) ? CopyOf(row.Data) : null;
        }
    }

    /// <summary>Stores a raw row as is (for tests that stage damaged or foreign rows).</summary>
    internal void SeedPhysicalRow(TableEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (gate)
        {
            rows[(row.PartitionKey, row.RowKey)] = new StoredRow(CopyOf(row), ++versionCounter, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Removes a raw row as is (for tests that damage a stored entity); false when there was none.</summary>
    internal bool RemovePhysicalRow(string partitionKey, string rowKey)
    {
        lock (gate)
        {
            return rows.Remove((partitionKey, rowKey));
        }
    }

    // Mode-aware id normalization — identical to TableStorage's, preserving fake/Azure parity.
    private string NormalizeId(string id) =>
        idNormalization == IdNormalization.Normalized ? EntityId.Normalize(id) : id;

    // ── Test conveniences (not part of IStorage) ────────────────────────────

    /// <summary>Number of entities currently stored (continuation rows of split values are not counted).</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return LargeValueAware ? LogicalRows().Count() : rows.Count;
            }
        }
    }

    /// <summary>
    /// Names of the blobs that currently hold offloaded values (ordinal order), for assertions; empty
    /// when blob offload is not configured. A blob a replace or delete released stays listed until its
    /// cleanup grace (<see cref="BlobOffloadOptions.CleanupGrace"/>) has passed.
    /// </summary>
    public IReadOnlyList<string> OffloadedBlobNames() =>
        Blobs.Names.OrderBy(n => n, StringComparer.Ordinal).ToList();

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
        if (LargeValueAware)
        {
            // The in-memory blob store completes synchronously, so this never blocks on I/O.
            return MaterializeAllAsync(LogicalSnapshot(LogicalRows), CancellationToken.None).GetAwaiter().GetResult();
        }

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
        RefuseReservedRowKey(entity.Id);
        // An already-cancelled token fails the call before anything is stamped or written, as the SDK
        // call TableStorage<T> makes first does.
        ct.ThrowIfCancellationRequested();

        entity.CreatedAt = DateTimeOffset.UtcNow;
        // An insert is a write — keep audit stamps consistent, mirroring TableStorage. (Entity's
        // property initializer masked this for derived models; interface-only IEntity models have
        // no initializer, so the stamp must come from the storage layer.)
        entity.UpdatedAt = entity.CreatedAt;
        entity.Timestamp = null;
        entity.Id = NormalizeId(entity.Id);
        var keys = EntityId.Split(entity.Id);
        if (LargeValueAware)
            return WriteLargeAsync(entity, LargeWriteKind.Create, ifMatch: null, ct);

        ServiceErrors.ThrowIfRefusedKey(keys);
        lock (gate)
        {
            if (rows.ContainsKey(keys))
                throw new DuplicateKeyException(typeof(T).Name, entity.Id, ServiceErrors.AlreadyExists(entity.Id));
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
        if (LargeValueAware)
            return UpsertLargeAsync(entity, ct);

        EnforceProtectedProperties(entity);

        if (entity.CreatedAt == default)
            entity.CreatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null;
        entity.Id = NormalizeId(entity.Id);
        ct.ThrowIfCancellationRequested();
        var keys = EntityId.Split(entity.Id);
        ServiceErrors.ThrowIfRefusedKey(keys);

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
        if (LargeValueAware)
            return UpdateLargeAsync(entity, mode, ct);

        EnforceProtectedProperties(entity);

        var callerSuppliedETag = !string.IsNullOrEmpty(entity.ETag);
        if (mode == ConcurrencyMode.Strict && !callerSuppliedETag)
            throw new InvalidOperationException(
                $"ConcurrencyMode.Strict requires {typeof(T).Name}.ETag — read the entity first and round-trip its ETag.");

        // Auto without an ETag throws since 0.6.0 (no version to check ≠ permission to clobber) —
        // byte-identical message and semantics to TableStorage<T>, so a green fake test still
        // documents the real contract. The pre-0.6.0 fallback survives behind the same option.
        if (mode == ConcurrencyMode.Auto && !callerSuppliedETag && !implicitLastWriterWins)
            throw new InvalidOperationException(ConcurrencyMessages.AutoRequiresETag(typeof(T).Name));

        var suppliedETag = entity.ETag;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null;
        entity.Id = NormalizeId(entity.Id);
        ct.ThrowIfCancellationRequested();
        var keys = EntityId.Split(entity.Id);
        ServiceErrors.ThrowIfRefusedKey(keys);

        lock (gate)
        {
            if (!rows.TryGetValue(keys, out var existing))
                throw ServiceErrors.NotFound(entity.Id);

            // Strict — and Auto with a caller-round-tripped ETag — enforce the row version; a
            // mismatch surfaces as a conflict with no retry, exactly like TableStorage<T>. Auto
            // without an ETag either threw above or (via ImplicitLastWriterWins) converges
            // last-writer-wins, and LastWriterWins skips the check by definition.
            if (mode != ConcurrencyMode.LastWriterWins && callerSuppliedETag
                && !string.Equals(existing.ETagString(), suppliedETag, StringComparison.Ordinal))
            {
                throw new ConcurrencyConflictException(typeof(T).Name, entity.Id, ServiceErrors.ConditionNotSatisfied());
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

        var keys = EntityId.Split(NormalizeId(id));
        // A continuation row is not an entity: merging cells into one would surface them on the primary.
        RefuseReservedRowKey(id);
        ct.ThrowIfCancellationRequested();
        ServiceErrors.ThrowIfRefusedKey(keys);

        lock (gate)
        {
            if (!rows.TryGetValue(keys, out var existing))
                throw ServiceErrors.NotFound(id);

            // Conditional merge (WithETag): the merge only applies to the version that was read.
            if (builder.ETag is not null
                && !string.Equals(existing.ETagString(), builder.ETag, StringComparison.Ordinal))
            {
                throw new ConcurrencyConflictException(typeof(T).Name, id, ServiceErrors.ConditionNotSatisfied());
            }

            // Server-side Merge: only the declared (flattened) columns change; the rest survive.
            var merged = CopyOf(existing.Data);
            foreach (var (name, value) in builder.Updates)
            {
                // The typed flatten refuses a [TableValueSplit] member exactly as TableStorage<T> does: a
                // merged plain cell beside the old split cells would read back the OLD value.
                foreach (var (col, cell) in LargeValueRows.FlattenPropertyForUpdate(typeof(T), name, value))
                {
                    merged[col] = Detached(cell);
                }
            }
            merged[nameof(Entity.UpdatedAt)] = DateTimeOffset.UtcNow;
            ServiceErrors.ThrowIfOversizedProperty(merged);

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

        var keys = EntityId.Split(NormalizeId(id));
        if (LargeValueAware)
            return OneLargeAsync(keys, ct);

        lock (gate)
        {
            // As TableStorage<T> reads for every T: a row with a _SplitOf cell (a continuation row of a
            // large-value aware type) is not an entity.
            return Task.FromResult(rows.TryGetValue(keys, out var row) && !LargeValueTable.IsPartRow(row.Data) ? Materialize(row) : null);
        }
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }

        var keys = EntityId.Split(NormalizeId(id));

        lock (gate)
        {
            // A continuation row is not an entity: addressing one by id finds nothing.
            // For every T, as TableStorage<T> answers it.
            return Task.FromResult(rows.TryGetValue(keys, out var row) && !LargeValueTable.IsPartRow(row.Data));
        }
    }

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default)
    {
        var scope = string.IsNullOrWhiteSpace(partition) ? null : NormalizeId(partition); // match stored (normalized) PartitionKeys
        if (LargeValueAware)
            return QueryLargeAsync(scope, ct);
        lock (gate)
        {
            var matches = LogicalRows()
                .Where(kv => scope is null || kv.Key.PartitionKey == scope)
                .Select(kv => Materialize(kv.Value))
                .ToList();
            return Task.FromResult<IEnumerable<T>>(matches);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (LargeValueAware)
            return BoundedLargeAsync(options, ct);

        var select = ProjectedRows.SelectedColumns<T>(options);
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<T>>(Bounded(options).Select(kv => Materialize(kv.Value, select)).ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be positive.");

        // Reject predicates the real store cannot translate to OData, so a green fake test means the
        // same predicate holds against Azure Tables — then evaluate the caller's real semantics.
        // Id equality is canonicalized first: the real store translates it to a PartitionKey/RowKey
        // filter (0.6.0), which matches by the row a value ADDRESSES ("a" and "a|a" are the same
        // row) — the compiled CLR comparison must agree or the fake diverges on alias spellings.
        _ = TableFilterTranslator.Translate(predicate);
        var canonical = (Expression<Func<T, bool>>)new IdEqualityCanonicalizer(predicate.Parameters[0]).Visit(predicate);
        var matches = canonical.Compile();
        var scope = string.IsNullOrWhiteSpace(partition) ? null : NormalizeId(partition); // match stored (normalized) PartitionKeys
        if (LargeValueAware)
            return QueryLargeAsync(matches, scope, take, ct);

        lock (gate)
        {
            IEnumerable<T> query = LogicalRows()
                .Where(kv => scope is null || kv.Key.PartitionKey == scope)
                .Select(kv => Materialize(kv.Value))
                .Where(matches);
            if (take is int t)
                query = query.Take(t);
            return Task.FromResult<IReadOnlyList<T>>(query.ToList());
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        List<T> snapshot;
        if (LargeValueAware)
        {
            snapshot = [.. await BoundedLargeAsync(options ?? new QueryOptions(), ct)];
        }
        else
        {
            var select = ProjectedRows.SelectedColumns<T>(options);
            lock (gate)
            {
                snapshot = Bounded(options ?? new QueryOptions()).Select(kv => Materialize(kv.Value, select)).ToList();
            }
        }

        foreach (var entity in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return entity;
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
    public Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var partition = string.IsNullOrWhiteSpace(options.Partition) ? null : NormalizeId(options.Partition); // match stored (normalized) PartitionKeys
        var rowKeyPrefix = string.IsNullOrWhiteSpace(options.RowKeyPrefix) ? null : NormalizeId(options.RowKeyPrefix); // stored RowKeys are normalized too

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
            var sep = inner.IndexOf('\0');
            if (sep < 0)
                throw new ArgumentException(
                    "Malformed in-memory continuation token (was it issued by a different backend? " +
                    "cursors are backend-specific).", nameof(options));
            after = (inner.Substring(0, sep), inner.Substring(sep + 1));
        }

        var select = ProjectedRows.SelectedColumns<T>(options);
        if (LargeValueAware)
            return PageLargeAsync(partition, rowKeyPrefix, pageSize, fingerprint, after, select, ct);

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
            // A row with a _SplitOf cell takes its place in the page and is then skipped, as TableStorage<T>
            // pages for every T.
            var items = pageRows.Where(kv => !LargeValueTable.IsPartRow(kv.Value.Data)).Select(kv => Materialize(kv.Value, select)).ToList();

            string? next = null;
            if (hasMore)
            {
                var last = pageRows[pageRows.Count - 1].Key;
                next = PageCursor.Encode(fingerprint, last.PartitionKey + "\0" + last.RowKey);
            }

            return Task.FromResult(new EntityPage<T>(items, next));
        }
    }

    // Rewrites top-level Id EQUALITY comparisons so both sides are canonicalized before comparing —
    // matching the real store's PartitionKey/RowKey translation, which compares by the row a value
    // addresses rather than by its spelling ("a" == "a|a"). Only `param.Id == <string>` (either
    // side) is rewritten; everything else passes through untouched.
    private sealed class IdEqualityCanonicalizer(ParameterExpression param) : ExpressionVisitor
    {
        private static readonly System.Reflection.MethodInfo Canon =
            ((Func<string, string>)EntityId.Canonicalize).Method;

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.Equal
                && node.Left.Type == typeof(string) && node.Right.Type == typeof(string)
                && (IsEntityIdMember(node.Left) || IsEntityIdMember(node.Right)))
            {
                return Expression.Equal(
                    Expression.Call(Canon, node.Left),
                    Expression.Call(Canon, node.Right));
            }

            return base.VisitBinary(node);
        }

        private bool IsEntityIdMember(Expression e)
        {
            while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
                e = u.Operand;
            return e is MemberExpression { Member.Name: nameof(IEntity.Id) } m && m.Expression == param;
        }
    }

    // Cursor ordering matches OrderedRows: Ordinal PartitionKey, then Ordinal RowKey.
    private static int KeyCompare((string PartitionKey, string RowKey) key, (string PartitionKey, string RowKey) after)
    {
        var c = string.CompareOrdinal(key.PartitionKey, after.PartitionKey);
        return c != 0 ? c : string.CompareOrdinal(key.RowKey, after.RowKey);
    }

    /// <inheritdoc />
    public async Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, string? partition = null, CancellationToken ct = default)
    {
        var first = await QueryAsync(predicate, partition, 1, ct);
        return first.Count > 0;
    }

    /// <inheritdoc />
    public Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
        => WriteBatch(entities, upsert: false, ct);

    /// <inheritdoc />
    public Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
        => WriteBatch(entities, upsert: true, ct);

    /// <inheritdoc />
    public Task<int> CountAsync(string? partition = null, CancellationToken ct = default)
    {
        lock (gate)
        {
            var scope = string.IsNullOrWhiteSpace(partition) ? null : NormalizeId(partition); // match stored (normalized) PartitionKeys
            if (LargeValueAware)
                return Task.FromResult(LogicalRows().Count(kv => scope is null || kv.Key.PartitionKey == scope));
            var count = scope is null
                ? rows.Count
                : rows.Keys.Count(k => k.PartitionKey == scope);
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

        var keys = EntityId.Split(NormalizeId(id));
        ct.ThrowIfCancellationRequested();
        if (LargeValueAware)
            return DeleteLargeAsync(keys, ct);

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

        partition = NormalizeId(partition); // match stored (normalized) PartitionKeys
        ct.ThrowIfCancellationRequested();
        if (LargeValueAware)
            return DeletePartitionLargeAsync(partition, ct);
        lock (gate)
        {
            var doomed = rows.Keys.Where(k => k.PartitionKey == partition).ToList();
            foreach (var key in doomed)
                rows.Remove(key);
            return Task.FromResult(doomed.Count);
        }
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private Task<int> WriteBatch(IReadOnlyCollection<T> entities, bool upsert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return Task.FromResult(0);
        ct.ThrowIfCancellationRequested();

        // Refuse a reserved row key before the first entity is stamped or written, not halfway through.
        if (LargeValueAware)
        {
            foreach (var entity in entities)
            {
                if (entity is not null && !string.IsNullOrWhiteSpace(entity.Id))
                    RefuseReservedRowKey(entity.Id);
            }
        }

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
            entity.Id = NormalizeId(entity.Id);
            var keys = EntityId.Split(entity.Id);

            // A large-value aware create batch refuses a repeated key up front, as TableStorage<T> does; a
            // plain batch finds it in its transaction, as the service does (see WritePlainBatch).
            if (LargeValueAware && !upsert && !withinBatch.Add(keys))
                throw new DuplicateKeyException(typeof(T).Name, entity.Id,
                    new RequestFailedException(400, $"InvalidDuplicateRow: duplicate key within the batch. Id: {entity.Id}"));
            prepared.Add((keys, entity));
        }

        if (LargeValueAware)
            return WriteBatchLargeAsync(prepared, upsert, ct);

        // Every entity is serialized before the first is stored, as TableStorage<T> serializes the whole
        // batch before it sends it: an entity the serializer refuses fails the batch with nothing written.
        var serialized = prepared.ConvertAll(p => Serialize(p.Keys, p.Entity));
        WritePlainBatch(prepared, serialized, upsert, ct);
        return Task.FromResult(prepared.Count);
    }

    /// <summary>
    /// The plain batch as TableStorage&lt;T&gt; sends it: per partition (in the order the partitions first
    /// appear), the rows packed into the same entity-group transactions (<see cref="BatchPlanner"/>, at most
    /// 100 rows and the payload budget each), each applied atomically under the gate. A transaction that
    /// fails applies nothing, and the ones before it stay committed. It fails as the service fails it: a
    /// key the service refuses with a raw 400 <c>InvalidInput</c>, a row named twice with 400
    /// <c>InvalidDuplicateRow</c> (a <see cref="DuplicateKeyException"/> for a create, as TableStorage&lt;T&gt;
    /// translates it; raw for an upsert), a stored key of a create with a
    /// <see cref="DuplicateKeyException"/> over 409 <c>EntityAlreadyExists</c>, each wrapped in the
    /// <see cref="TableTransactionFailedException"/> the SDK raises.
    /// </summary>
    private void WritePlainBatch(List<((string, string) Keys, T Entity)> prepared, List<TableEntity> serialized, bool upsert, CancellationToken ct)
    {
        foreach (var group in Enumerable.Range(0, prepared.Count).GroupBy(i => prepared[i].Keys.Item1, StringComparer.Ordinal))
        {
            var members = group.ToArray();
            var plan = BatchPlanner.Plan([.. members.Select(i => TableRequestSize.EstimateEntityBytes(serialized[i]))]);
            foreach (var range in plan)
            {
                // A cancelled token fails a transaction before it is sent, as the SDK does.
                ct.ThrowIfCancellationRequested();
                lock (gate)
                {
                    var inTransaction = new HashSet<(string, string)>();
                    for (var j = range.Start; j < range.Start + range.Count; j++)
                    {
                        var (keys, entity) = prepared[members[j]];
                        if (ServiceErrors.RefusedKey(keys.Item1, keys.Item2) is { } refused)
                            throw ServiceErrors.InTransaction(refused);
                        if (ServiceErrors.OversizedProperty(serialized[members[j]]) is { } oversized)
                            throw ServiceErrors.InTransaction(oversized);
                        if (!inTransaction.Add(keys))
                        {
                            var repeated = ServiceErrors.InTransaction(ServiceErrors.DuplicateRow(entity.Id));
                            throw upsert ? repeated : new DuplicateKeyException(typeof(T).Name, entity.Id, repeated);
                        }
                        if (!upsert && rows.ContainsKey(keys))
                            throw new DuplicateKeyException(typeof(T).Name, entity.Id,
                                ServiceErrors.InTransaction(ServiceErrors.AlreadyExists(entity.Id)));
                    }
                    for (var j = range.Start; j < range.Start + range.Count; j++)
                        Store(prepared[members[j]].Keys, serialized[members[j]]);
                }
            }
        }
    }

    private IEnumerable<KeyValuePair<(string PartitionKey, string RowKey), StoredRow>> Bounded(QueryOptions options)
    {
        var partition = string.IsNullOrWhiteSpace(options.Partition) ? null : NormalizeId(options.Partition); // match stored (normalized) PartitionKeys
        var rowKeyPrefix = string.IsNullOrWhiteSpace(options.RowKeyPrefix) ? null : NormalizeId(options.RowKeyPrefix); // stored RowKeys are normalized too

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

        // A row with a _SplitOf cell is skipped for every T, as TableStorage<T> reads it.
        var matches = LogicalRows()
            .Where(kv => partition is null || kv.Key.PartitionKey == partition)
            .Where(kv => rowKeyPrefix is null || kv.Key.RowKey.StartsWith(rowKeyPrefix, StringComparison.Ordinal));

        return options.Take is int take ? matches.Take(take) : matches;
    }

    private IEnumerable<KeyValuePair<(string PartitionKey, string RowKey), StoredRow>> OrderedRows() =>
        rows.OrderBy(kv => kv.Key.PartitionKey, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.RowKey, StringComparer.Ordinal);

    private StoredRow Store((string PartitionKey, string RowKey) keys, T entity) =>
        Store(keys, Serialize(keys, entity));

    private StoredRow Store((string PartitionKey, string RowKey) keys, TableEntity serialized)
    {
        // The service refuses a cell above 64 KiB (a plain byte[] reaches it uncompressed).
        ServiceErrors.ThrowIfOversizedProperty(serialized);
        // A copy: the serialized cells may be arrays the writer still holds.
        var row = new StoredRow(CopyOf(serialized), ++versionCounter, DateTimeOffset.UtcNow);
        rows[keys] = row;
        return row;
    }

    // The plain path, as TableStorage<T> serializes it: the row a read of a T comes from.
    private static TableEntity Serialize((string PartitionKey, string RowKey) keys, T entity) =>
        LargeValueRows.ToPlainTableEntity(entity, keys.PartitionKey, keys.RowKey, typeof(T));

    // A projected read (select not null) deserializes only the selected cells, as TableStorage<T> reads what
    // the service returns for a $select; nothing outside the row is read.
    private static T Materialize(StoredRow row, IReadOnlyList<string>? select)
    {
        if (select is null)
            return Materialize(row);
        var entity = ProjectedRows.Read<T>(CopyOf(row.Data), select);
        entity.ETag = row.ETagString();
        entity.Timestamp = row.Timestamp;
        return entity;
    }

    private static T Materialize(StoredRow row)
    {
        var entity = CopyOf(row.Data).FromTableEntity<T>();
        entity.ETag = row.ETagString();
        entity.Timestamp = row.Timestamp;
        return entity;
    }

    // Isolate stored state from caller mutations: every row goes in and comes out through a copy. The
    // serializer passes a byte[] cell through by reference both ways (every other cell value is
    // immutable), so each array is copied too: a stored row never holds an array a caller passed in or
    // received.
    private static TableEntity CopyOf(TableEntity source)
    {
        var copy = new TableEntity(source.PartitionKey, source.RowKey);
        foreach (var kv in source)
        {
            if (kv.Key is not ("PartitionKey" or "RowKey" or "odata.etag" or "Timestamp"))
                copy[kv.Key] = Detached(kv.Value);
        }
        return copy;
    }

    // A cell value that shares nothing mutable with the given one. A DateTimeOffset is kept as the instant
    // in UTC, as the service stores an Edm.DateTime and returns it (offset zero) whatever offset it was
    // written with.
    private static object Detached(object value) => value switch
    {
        byte[] bytes => bytes.Clone(),
        DateTimeOffset at => at.ToUniversalTime(),
        _ => value,
    };

    private void EnforceProtectedProperties(T entity)
    {
        if (ProtectedProps.Count == 0)
            return;

        T? stored;
        var keys = EntityId.Split(NormalizeId(entity.Id));
        lock (gate)
        {
            stored = rows.TryGetValue(keys, out var row) ? Materialize(row) : null;
        }
        CheckProtectedProperties(stored, entity);
    }

    // Shared by the plain and the large-value paths: a changed protected property needs an authorised caller.
    private void CheckProtectedProperties(T? stored, T entity)
    {
        if (stored is null)
            return;

        foreach (var (prop, attr) in ProtectedProps)
        {
            var oldVal = prop.GetValue(stored);
            var newVal = prop.GetValue(entity);
            // As TableStorage: a large value is compared by content and never quoted, a byte[] is compared by
            // content (a read never hands back the stored array), and any other property keeps the comparison
            // of 0.8.3; every value that is not large keeps its message.
            var large = ProtectedLargeValues.IsLargeValue(typeof(T), prop);
            if (!(large || oldVal is byte[] ? ProtectedLargeValues.IsUnchanged(oldVal, newVal) : Equals(oldVal, newVal))
                && !(authorizer?.IsAllowed(attr.Roles) ?? false))
            {
                throw new UnauthorizedAccessException(
                    $"Property '{prop.Name}' on {typeof(T).Name} is protected (requires roles: {attr.Roles}). " +
                    (large || ProtectedLargeValues.HoldsLargeValue(typeof(T), oldVal, newVal)
                        ? ProtectedLargeValues.DescribeRefusedChange(oldVal, newVal)
                        : $"The current caller is not authorised to change it from '{oldVal}' to '{newVal}'."));
            }
        }
    }
}

