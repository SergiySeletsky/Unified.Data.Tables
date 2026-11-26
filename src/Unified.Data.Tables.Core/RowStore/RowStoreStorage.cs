using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Providers;

/// <summary>
/// <see cref="IStorage{T}"/> over a row store (<see cref="IRowStore"/>): the one engine every local
/// provider (in-memory, SQLite) runs, so they store and answer exactly alike. Rows round-trip through the
/// real <see cref="TableEntitySerializer"/> on every read and write; semantics mirror
/// <c>TableStorage&lt;T&gt;</c>: identical id normalization and first-<c>'|'</c> key splitting,
/// <see cref="DuplicateKeyException"/> on duplicate create, <see cref="EntityNotFoundException"/> on
/// updating a missing row, <see cref="ConcurrencyConflictException"/> on a lost version check,
/// idempotent delete, lexical (PartitionKey, RowKey) result ordering, the service's limits (100 rows and
/// the payload budget a transaction, one partition a transaction, 64 KiB a cell, 1 MB a row) and its key
/// rules. It throws only the provider-neutral <see cref="StorageException"/> types for what the service
/// would refuse, and never sends a request twice, so <see cref="DuplicateKeyException.MayBeOwnWrite"/> is
/// always false and a delete never answers <see cref="DeleteStatus.Indeterminate"/>.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
internal sealed partial class RowStoreStorage<T> : IStorage<T>, IOrphanBlobSweeper where T : class, IEntity, new()
{
    private static readonly List<(PropertyInfo Prop, ProtectedPropertyAttribute Attr)> ProtectedProps =
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Prop: p, Attr: p.GetCustomAttribute<ProtectedPropertyAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Prop, x.Attr!))
            .ToList();

    // True when T has a member that may be stored as split cells, continuation rows or a blob. Every
    // large-value branch below is reached only for such a T; any other T is one row an entity.
    private static readonly bool LargeValueAware = LargeValueTable.IsLargeValueAware(typeof(T));

    /// <summary>How many times in all a write that keeps losing its condition to concurrent writers is attempted (the number TableStorage uses).</summary>
    private const int MaxLargeWriteAttempts = LargeValueTable.MaxWriteAttempts;

    /// <summary>How many rows one pull of a scan asks the row store for (the service's page size).</summary>
    internal const int ScanPageSize = 1000;

    private readonly IRowStoreAccount account;
    private readonly IRowStore rows;
    private readonly IProtectedPropertyAuthorizer? authorizer;
    private readonly IdNormalization idNormalization;
    private readonly bool implicitLastWriterWins;
    private readonly LargeValueTable large;
    private readonly string typeName = typeof(T).Name;
    private readonly string tableName;

    // The clock that stamps CreatedAt and UpdatedAt (StorageOptions.TimeProvider; the system clock by default).
    private readonly TimeProvider clock;
    private readonly RowStoreReadCache<T> cache;
    private volatile bool tableEnsured;

    /// <summary>Creates the store of <typeparamref name="T"/> over one table of <paramref name="account"/>. Touches nothing: the table is created on first use.</summary>
    /// <param name="account">The account that holds the table.</param>
    /// <param name="options">Options; null selects the defaults.</param>
    /// <param name="authorizer">Gates changes to <see cref="ProtectedPropertyAttribute"/>-decorated properties; null allows none.</param>
    /// <param name="blobs">Where offloaded values go when <see cref="StorageOptions.Offload"/> is set; null stores every value in rows.</param>
    /// <param name="cachePolicy">The read cache; null resolves it from <paramref name="options"/> exactly as <c>TableStorage&lt;T&gt;</c> does (a sliding hour by default).</param>
    public RowStoreStorage(IRowStoreAccount account, StorageOptions? options, IProtectedPropertyAuthorizer? authorizer,
        IOffloadBlobStore? blobs, CachePolicy? cachePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        this.account = account;
        this.authorizer = authorizer;
        var opts = options ?? new StorageOptions();
        idNormalization = opts.IdNormalization;
        implicitLastWriterWins = opts.ImplicitLastWriterWins;
        clock = opts.TimeProvider;
        var resolved = opts.TableNameResolver?.Invoke(typeof(T));
        tableName = string.IsNullOrWhiteSpace(resolved) ? typeof(T).Name : resolved;
        rows = account.GetTable(tableName);
        large = new LargeValueTable(tableName, opts.Offload, opts.Offload is null ? null : blobs, typeof(T)) { Clock = clock };
        var policy = cachePolicy ?? opts.ResolveCachePolicy(typeof(T));
        cache = new RowStoreReadCache<T>(policy, clock);
        info = RowStoreInfo.Describe(account, tableName, policy.Enabled);
    }

    /// <summary>The table this store reads and writes.</summary>
    internal string TableName => tableName;

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

    // Mode-aware id normalization, identical to TableStorage's.
    private string NormalizeId(string id) =>
        idNormalization == IdNormalization.Normalized ? EntityId.Normalize(id) : id;

    // The table is created once, on first use, as TableStorage creates its table lazily. A table deleted
    // behind the store afterwards is not created again: its operations fail with TableNotFoundException.
    private ValueTask EnsureTableAsync(CancellationToken ct) => tableEnsured ? default : CreateTableOnceAsync(ct);

    private async ValueTask CreateTableOnceAsync(CancellationToken ct)
    {
        await account.CreateTableAsync(tableName, ct);
        tableEnsured = true;
    }

    /// <summary>Eagerly creates the table (it is otherwise created on first use).</summary>
    internal Task EnsureCreatedAsync(CancellationToken ct = default) => Run(EnsureTableAsync(ct).AsTask());

    // Every member runs through one of these: a table that is not there leaves as TableNotFoundException,
    // and a member that may change rows leaves nothing in the read cache it could have made stale.
    private async Task Run(Task work)
    {
        try
        {
            await work;
        }
        catch (RowStoreTableMissingException ex)
        {
            throw ServiceErrors.TableMissing(tableName, ex);
        }
    }

    private async Task<TResult> Run<TResult>(Task<TResult> work)
    {
        try
        {
            return await work;
        }
        catch (RowStoreTableMissingException ex)
        {
            throw ServiceErrors.TableMissing(tableName, ex);
        }
    }

    private async Task<TResult> Written<TResult>(Task<TResult> work, string? id)
    {
        try
        {
            return await work;
        }
        catch (RowStoreTableMissingException ex)
        {
            throw ServiceErrors.TableMissing(tableName, ex);
        }
        finally
        {
            // Committed or not (a failure may have committed part of a batch): nothing the write could
            // have changed is answered from the cache.
            if (cache.Enabled)
            {
                if (string.IsNullOrWhiteSpace(id))
                    cache.EvictAll();
                else
                    cache.EvictEntity(CacheId(id));
            }
        }
    }

    private string CacheId(string id) => EntityId.Canonicalize(NormalizeId(id));

    // ── IStorage<T> ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<T> CreateAsync(T entity, CancellationToken ct = default)
    {
        cache.BeginWrite();
        return Written(CreateCoreAsync(entity, ct), entity?.Id);
    }

    private async Task<T> CreateCoreAsync(T entity, CancellationToken ct)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }
        RefuseReservedRowKey(entity.Id);
        // An already-cancelled token fails the call before anything is stamped or written, as the SDK
        // call TableStorage<T> makes first does.
        ct.ThrowIfCancellationRequested();
        await EnsureTableAsync(ct);

        entity.CreatedAt = clock.GetUtcNow();
        // An insert is a write: keep audit stamps consistent, mirroring TableStorage. (Entity's
        // property initializer masked this for derived models; interface-only IEntity models have
        // no initializer, so the stamp must come from the storage layer.)
        entity.UpdatedAt = entity.CreatedAt;
        entity.Timestamp = null;
        entity.Id = NormalizeId(entity.Id);
        var keys = EntityId.Split(entity.Id);
        if (LargeValueAware)
            return await WriteLargeAsync(entity, LargeWriteKind.Create, ifMatch: null, ct);

        ServiceErrors.ThrowIfRefusedKey(keys, typeName);
        TableEntity row;
        try
        {
            row = Stored(Serialize(keys, entity));
        }
        catch (StorageRequestRejectedException)
        {
            // A row the service would refuse at a key that is stored: the duplicate is what is reported.
            if (await rows.GetAsync(keys.PartitionKey, keys.RowKey, ct) is not null)
                throw ServiceErrors.Coded(new DuplicateKeyException(typeName, entity.Id, ServiceErrors.AlreadyExists(entity.Id)));
            throw;
        }
        var result = await rows.CommitAsync(keys.PartitionKey, [RowWrite.Insert(row)], ct);
        if (!result.Committed)
            throw ServiceErrors.Coded(new DuplicateKeyException(typeName, entity.Id, ServiceErrors.AlreadyExists(entity.Id)));
        entity.ETag = result.ETags[0];
        return entity;
    }

    /// <inheritdoc />
    public Task<T> UpsertAsync(T entity, CancellationToken ct = default)
    {
        cache.BeginWrite();
        return Written(UpsertCoreAsync(entity, ct), entity?.Id);
    }

    private async Task<T> UpsertCoreAsync(T entity, CancellationToken ct)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }
        if (LargeValueAware)
            return await UpsertLargeAsync(entity, ct);

        await EnforceProtectedPropertiesAsync(entity, ct);

        if (entity.CreatedAt == default)
            entity.CreatedAt = clock.GetUtcNow();
        entity.UpdatedAt = clock.GetUtcNow();
        entity.Timestamp = null;
        entity.Id = NormalizeId(entity.Id);
        ct.ThrowIfCancellationRequested();
        var keys = EntityId.Split(entity.Id);
        ServiceErrors.ThrowIfRefusedKey(keys, typeName);

        await EnsureTableAsync(ct);
        var result = await rows.CommitAsync(keys.PartitionKey, [RowWrite.Upsert(Stored(Serialize(keys, entity)))], ct);
        entity.ETag = result.ETags[0];
        return entity;
    }

    /// <inheritdoc />
    public Task<T> UpdateAsync(T entity, CancellationToken ct = default)
        => UpdateAsync(entity, ConcurrencyMode.Auto, ct);

    /// <inheritdoc />
    public Task<T> UpdateAsync(T entity, ConcurrencyMode mode, CancellationToken ct = default)
    {
        cache.BeginWrite();
        return Written(UpdateCoreAsync(entity, mode, ct), entity?.Id);
    }

    private async Task<T> UpdateCoreAsync(T entity, ConcurrencyMode mode, CancellationToken ct)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }
        // "*" is not a version: refused, as TableStorage<T> refuses it, before anything is read or
        // written. An unconditional replace is spelled ConcurrencyMode.LastWriterWins, which never looks
        // at the ETag.
        if (mode != ConcurrencyMode.LastWriterWins && entity.ETag == "*")
            throw new ArgumentException(ConcurrencyMessages.WildcardETagOnUpdate(typeName), nameof(entity));
        if (LargeValueAware)
            return await UpdateLargeAsync(entity, mode, ct);

        await EnforceProtectedPropertiesAsync(entity, ct);

        var callerSuppliedETag = !string.IsNullOrEmpty(entity.ETag);
        if (mode == ConcurrencyMode.Strict && !callerSuppliedETag)
            throw new InvalidOperationException(
                $"ConcurrencyMode.Strict requires {typeName}.ETag — read the entity first and round-trip its ETag.");

        // Auto without an ETag throws since 0.6.0 (no version to check ≠ permission to clobber):
        // byte-identical message and semantics to TableStorage<T>. The pre-0.6.0 fallback survives
        // behind the same option.
        if (mode == ConcurrencyMode.Auto && !callerSuppliedETag && !implicitLastWriterWins)
            throw new InvalidOperationException(ConcurrencyMessages.AutoRequiresETag(typeName));

        var suppliedETag = entity.ETag;
        entity.UpdatedAt = clock.GetUtcNow();
        entity.Timestamp = null;
        entity.Id = NormalizeId(entity.Id);
        ct.ThrowIfCancellationRequested();
        var keys = EntityId.Split(entity.Id);
        ServiceErrors.ThrowIfRefusedKey(keys, typeName);

        await EnsureTableAsync(ct);
        // Strict, and Auto with a caller-round-tripped ETag, enforce the row version; a mismatch is a
        // conflict with no retry, exactly like TableStorage<T>. Auto without an ETag either threw above
        // or (via ImplicitLastWriterWins) converges last-writer-wins, and LastWriterWins skips the check.
        var ifMatch = mode != ConcurrencyMode.LastWriterWins && callerSuppliedETag ? suppliedETag : null;
        TableEntity row;
        try
        {
            row = Stored(Serialize(keys, entity));
        }
        catch (StorageRequestRejectedException)
        {
            // A row the service would refuse is refused after it has looked for the row: a missing row
            // and a lost version check are reported first, as the service reports them.
            await ThrowIfMissingOrChangedAsync(keys, entity.Id, ifMatch, ct);
            throw;
        }
        var result = await rows.CommitAsync(keys.PartitionKey, [RowWrite.Replace(row, ifMatch)], ct);
        if (result.Failure == RowCommitFailure.NotFound)
            throw ServiceErrors.Neutral(ServiceErrors.NotFound(entity.Id), typeName, entity.Id);
        if (!result.Committed)
            throw ServiceErrors.Coded(new ConcurrencyConflictException(typeName, entity.Id, ServiceErrors.ConditionNotSatisfied()));
        entity.ETag = result.ETags[0];
        return entity;
    }

    private async Task ThrowIfMissingOrChangedAsync((string PartitionKey, string RowKey) keys, string id, string? ifMatch, CancellationToken ct)
    {
        var stored = await rows.GetAsync(keys.PartitionKey, keys.RowKey, ct);
        if (stored is null)
            throw ServiceErrors.Neutral(ServiceErrors.NotFound(id), typeName, id);
        if (ifMatch is not null && !string.Equals(stored.ETag, ifMatch, StringComparison.Ordinal))
            throw ServiceErrors.Coded(new ConcurrencyConflictException(typeName, id, ServiceErrors.ConditionNotSatisfied()));
    }

    /// <inheritdoc />
    public Task<string> UpdateAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct = default)
    {
        cache.BeginWrite();
        return Written(MergeCoreAsync(id, builderAction, ct), id);
    }

    private async Task<string> MergeCoreAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct)
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
        ServiceErrors.ThrowIfRefusedKey(keys, typeName);

        await EnsureTableAsync(ct);
        while (true)
        {
            var existing = await rows.GetAsync(keys.PartitionKey, keys.RowKey, ct);
            if (existing is null)
            {
                // Named as the row is stored (normalized), as TableStorage names it.
                var normalizedId = NormalizeId(id);
                throw ServiceErrors.Neutral(ServiceErrors.NotFound(normalizedId), typeName, normalizedId);
            }

            // Conditional merge (WithETag): the merge only applies to the version that was read.
            if (builder.ETag is not null && !string.Equals(existing.ETag, builder.ETag, StringComparison.Ordinal))
                throw ServiceErrors.Coded(new ConcurrencyConflictException(typeName, id, ServiceErrors.ConditionNotSatisfied()));

            // Server-side Merge: only the declared (flattened) columns change; the rest survive.
            var merged = RowCells.Copy(existing.Data);
            foreach (var (name, value) in builder.Updates)
            {
                // The typed flatten refuses a [TableValueSplit] member exactly as TableStorage<T> does: a
                // merged plain cell beside the old split cells would read back the OLD value.
                foreach (var (col, cell) in TableEntitySerializer.FlattenProperty(typeof(T), name, value))
                {
                    merged[col] = RowCells.Detached(cell);
                }
            }
            merged[nameof(Entity.UpdatedAt)] = clock.GetUtcNow();
            ServiceErrors.ThrowIfRefusedRow(merged, typeName);

            // The merge is applied to the version it was computed from: a row another writer changed
            // meanwhile is merged again (an unconditional merge never fails for it), a row that is gone
            // is the missing row.
            var result = await rows.CommitAsync(keys.PartitionKey, [RowWrite.Replace(merged, existing.ETag)], ct);
            if (result.Committed)
                return result.ETags[0]!;
        }
    }

    /// <inheritdoc />
    public Task<T?> OneAsync(string id, CancellationToken ct = default) => Run(OneCoreAsync(id, ct));

    private async Task<T?> OneCoreAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }

        var normalized = NormalizeId(id);
        if (cache.Enabled && cache.TryGetEntity(EntityId.Canonicalize(normalized), out var cached))
            return cached;

        // Taken before the row is read: what this read finds is cached only if no write has begun since.
        var mark = cache.Mark();
        var keys = EntityId.Split(normalized);
        await EnsureTableAsync(ct);
        var row = await rows.GetAsync(keys.PartitionKey, keys.RowKey, ct);
        // A row with a _SplitOf cell (a continuation row) is not an entity: addressing one by id finds
        // nothing, for every T, as TableStorage<T> reads it.
        if (row is null || LargeValueTable.IsPartRow(row.Data))
            return null;
        var entity = LargeValueAware ? await MaterializeAsync(row, ct) : Materialize(row);
        if (entity is not null)
            cache.PutEntity(entity, mark);
        return entity;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string id, CancellationToken ct = default) => Run(ExistsCoreAsync(id, ct));

    private async Task<bool> ExistsCoreAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }

        var normalized = NormalizeId(id);
        if (cache.Enabled && cache.HoldsEntity(EntityId.Canonicalize(normalized)))
            return true;
        var keys = EntityId.Split(normalized);
        await EnsureTableAsync(ct);
        // A continuation row is not an entity: addressing one by id finds nothing.
        return await rows.GetAsync(keys.PartitionKey, keys.RowKey, ct) is { } row && !LargeValueTable.IsPartRow(row.Data);
    }

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default) => Run(QueryCoreAsync(partition, ct));

    private async Task<IEnumerable<T>> QueryCoreAsync(string? partition, CancellationToken ct)
    {
        var scope = string.IsNullOrWhiteSpace(partition) ? null : NormalizeId(partition); // match stored (normalized) PartitionKeys
        if (cache.Enabled && cache.TryGetList(scope, out var cached))
            return cached;

        var mark = cache.Mark();
        await EnsureTableAsync(ct);
        var results = new List<T>();
        await foreach (var row in ScanAsync(new RowRange(scope), ct))
        {
            if (LargeValueTable.IsPartRow(row.Data))
                continue;
            // Deleted while its blobs were read: no longer part of the result.
            if ((LargeValueAware ? await MaterializeAsync(row, ct) : Materialize(row)) is { } entity)
                results.Add(entity);
        }
        cache.PutList(scope, results, mark);
        return results;
    }

    /// <summary>
    /// Deserialized copies of every stored row in lexical key order, read past the cache (the Snapshot
    /// of the in-memory store): the entities of a large-value-aware type, every row of any other.
    /// </summary>
    internal async Task<IReadOnlyList<T>> SnapshotAsync(CancellationToken ct)
    {
        var results = new List<T>();
        try
        {
            await EnsureTableAsync(ct);
            await foreach (var row in ScanAsync(RowRange.All, ct))
            {
                if (!LargeValueAware)
                    results.Add(Materialize(row));
                else if (!LargeValueTable.IsPartRow(row.Data) && await MaterializeAsync(row, ct) is { } entity)
                    results.Add(entity);
            }
        }
        catch (RowStoreTableMissingException ex)
        {
            throw ServiceErrors.TableMissing(tableName, ex);
        }
        return results;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Run(BoundedAsync(Bounds(options), options.Take, ProjectedRows.SelectedColumns<T>(options), ct));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default)
    {
        var (matches, scope) = Filter(predicate, partition, take);
        return Run(FilteredAsync(matches, scope, take, ct));
    }

    // Rejects a predicate the real store cannot translate to OData, so the same predicate holds against
    // Azure Tables; then compiles the caller's real semantics. Id equality is canonicalized first: the
    // real store translates it to a PartitionKey/RowKey filter, which matches by the row a value
    // ADDRESSES ("a" and "a|a" are the same row), so the compiled CLR comparison must agree.
    private (Func<T, bool> Matches, string? Scope) Filter(Expression<Func<T, bool>> predicate, string? partition, int? take)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be positive.");

        _ = TableFilterTranslator.Translate(predicate);
        var canonical = (Expression<Func<T, bool>>)new IdEqualityCanonicalizer(predicate.Parameters[0]).Visit(predicate);
        var scope = string.IsNullOrWhiteSpace(partition) ? null : NormalizeId(partition); // match stored (normalized) PartitionKeys
        return (canonical.Compile(), scope);
    }

    private async Task<IReadOnlyList<T>> FilteredAsync(Func<T, bool> matches, string? scope, int? take, CancellationToken ct)
    {
        var results = new List<T>();
        await foreach (var entity in FilteredStreamAsync(matches, scope, take, ct))
            results.Add(entity);
        return results;
    }

    // The rows the predicate holds for, pulled a page at a time. For a large-value-aware T the predicate
    // is evaluated on the stored primary row, as the service filters for TableStorage (a filter cannot
    // name a large value), and only a row it matches is assembled and has its blobs read, so a row whose
    // large value cannot be read fails the query only when it matches.
    private async IAsyncEnumerable<T> FilteredStreamAsync(Func<T, bool> matches, string? scope, int? take,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var mark = cache.Mark();
        await EnsureTableAsync(ct);
        var yielded = 0;
        await foreach (var row in ScanAsync(new RowRange(scope), ct))
        {
            if (LargeValueTable.IsPartRow(row.Data))
                continue;
            T? entity;
            if (LargeValueAware)
            {
                if (!MatchesStoredRow(row, matches))
                    continue;
                // Deleted while its blobs were read: not part of the result, and it does not count toward take.
                entity = await MaterializeAsync(row, ct);
                if (entity is null)
                    continue;
            }
            else
            {
                entity = Materialize(row);
                if (!matches(entity))
                    continue;
            }
            cache.PutEntity(entity, mark);
            yield return entity;
            if (take is int max && ++yielded >= max)
                yield break;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null, CancellationToken ct = default)
    {
        options ??= new QueryOptions();
        return Guarded(BoundedStreamAsync(Bounds(options), options.Take, ProjectedRows.SelectedColumns<T>(options), ct), ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<T> QueryStreamAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null,
        CancellationToken ct = default)
    {
        var (matches, scope) = Filter(predicate, partition, take);
        return Guarded(FilteredStreamAsync(matches, scope, take, ct), ct);
    }

    // A stream that reports a table that is not there as the neutral exception, and looks at the token
    // between two rows, as every provider's stream does.
    private async IAsyncEnumerable<T> Guarded(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var rowsOf = source.GetAsyncEnumerator(ct);
        while (true)
        {
            T current;
            try
            {
                if (!await rowsOf.MoveNextAsync())
                    yield break;
                current = rowsOf.Current;
            }
            catch (RowStoreTableMissingException ex)
            {
                throw ServiceErrors.TableMissing(tableName, ex);
            }
            ct.ThrowIfCancellationRequested();
            yield return current;
        }
    }

    /// <inheritdoc />
    public Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default) => Run(QueryPageCoreAsync(options, ct));

    private async Task<EntityPage<T>> QueryPageCoreAsync(QueryOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateRowKeyRange();

        var partition = string.IsNullOrWhiteSpace(options.Partition) ? null : NormalizeId(options.Partition); // match stored (normalized) PartitionKeys
        var rowKeyPrefix = string.IsNullOrWhiteSpace(options.RowKeyPrefix) ? null : NormalizeId(options.RowKeyPrefix); // stored RowKeys are normalized too

        if (rowKeyPrefix is not null && partition is null)
            throw new ArgumentException(
                "RowKeyPrefix requires Partition — a cross-partition RowKey range is a full table scan wearing a filter.",
                nameof(options));
        if (options.Take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.Take, "Take (page size) must be positive.");

        var pageSize = options.Take is int t ? Math.Min(t, 1000) : 100;
        // The bounds as they are compared (normalized like ids). With no bound this is the three-part
        // fingerprint every earlier release issued, so a cursor from 0.9.2 still decodes.
        var from = options.FromRowKey is null ? null : NormalizeId(options.FromRowKey);
        var to = options.ToRowKey is null ? null : NormalizeId(options.ToRowKey);
        var fingerprint = PageCursor.Fingerprint(partition, rowKeyPrefix, pageSize, from, options.FromInclusive, to, options.ToInclusive);

        (string PartitionKey, string RowKey)? after = null;
        if (options.ContinuationToken is not null)
        {
            var inner = PageCursor.Decode(options.ContinuationToken, fingerprint);
            var sep = inner.IndexOf('\0', StringComparison.Ordinal);
            if (sep < 0)
                throw new ArgumentException(
                    "Malformed continuation token (was it issued by a different backend? " +
                    "cursors are backend-specific).", nameof(options));
            after = (inner.Substring(0, sep), inner.Substring(sep + 1));
        }

        var select = ProjectedRows.SelectedColumns<T>(options);
        var range = (partition is null
            ? RowRange.All
            : rowKeyPrefix is not null
                ? RowRange.Prefix(partition, rowKeyPrefix)
                : new RowRange(partition, from, options.FromInclusive, to, options.ToInclusive)) with { After = after };

        // Pages over the stored rows, as the service does for TableStorage: a continuation row (declared
        // or leftover) takes a place in the page and is then skipped, so a page can hold fewer entities
        // than its size, or none, and still carry a cursor; the cursor resumes after the last row of the page.
        var mark = cache.Mark();
        await EnsureTableAsync(ct);
        var candidate = await rows.ReadAsync(range, pageSize + 1, ct);
        var hasMore = candidate.Count > pageSize;
        var count = hasMore ? pageSize : candidate.Count;
        var items = new List<T>(count);
        for (var i = 0; i < count; i++)
        {
            var row = candidate[i];
            if (LargeValueTable.IsPartRow(row.Data))
                continue;
            // A projection that names no large value reads only the selected cells: a partial entity,
            // which never warms the cache.
            if (select is not null)
                items.Add(Materialize(row, select));
            else if ((LargeValueAware ? await MaterializeAsync(row, ct) : Materialize(row)) is { } entity)
            {
                cache.PutEntity(entity, mark);
                items.Add(entity);
            }
        }

        string? next = null;
        if (hasMore)
        {
            var last = candidate[pageSize - 1];
            next = PageCursor.Encode(fingerprint, last.PartitionKey + "\0" + last.RowKey);
        }
        return new EntityPage<T>(items, next);
    }

    // Rewrites top-level Id EQUALITY comparisons so both sides are canonicalized before comparing,
    // matching the real store's PartitionKey/RowKey translation, which compares by the row a value
    // addresses rather than by its spelling ("a" == "a|a"). Only `param.Id == <string>` (either
    // side) is rewritten; everything else passes through untouched.
    private sealed class IdEqualityCanonicalizer(ParameterExpression param) : ExpressionVisitor
    {
        private static readonly MethodInfo Canon = ((Func<string, string>)EntityId.Canonicalize).Method;

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

    /// <inheritdoc />
    public async Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, string? partition = null, CancellationToken ct = default)
    {
        var first = await QueryAsync(predicate, partition, 1, ct);
        return first.Count > 0;
    }

    /// <inheritdoc />
    public Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
    {
        cache.BeginWrite();
        return Written(WriteBatchAsync(entities, upsert: false, ct), id: null);
    }

    /// <inheritdoc />
    public Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
    {
        cache.BeginWrite();
        return Written(WriteBatchAsync(entities, upsert: true, ct), id: null);
    }

    /// <inheritdoc />
    public Task<int> CountAsync(string? partition = null, CancellationToken ct = default) => Run(CountCoreAsync(partition, ct));

    private async Task<int> CountCoreAsync(string? partition, CancellationToken ct)
    {
        var scope = string.IsNullOrWhiteSpace(partition) ? null : NormalizeId(partition); // match stored (normalized) PartitionKeys
        await EnsureTableAsync(ct);
        var count = 0;
        await foreach (var row in ScanAsync(new RowRange(scope), ct))
        {
            // Continuation rows of split values are not entities.
            if (!LargeValueAware || !LargeValueTable.IsPartRow(row.Data))
                count++;
        }
        return count;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        cache.BeginWrite();
        return Written(DeleteCoreAsync(id, ifMatch: null, ct), id);
    }

    // The one delete of a row by id, behind DeleteAsync and the conditional deletes (IConditionalDelete):
    // unconditional, or on condition of the version ifMatch names. Returns what it found.
    private async Task<DeleteStatus> DeleteCoreAsync(string id, string? ifMatch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }

        id = NormalizeId(id);
        var keys = EntityId.Split(id);
        ct.ThrowIfCancellationRequested();
        await EnsureTableAsync(ct);
        if (LargeValueAware)
            return await DeleteLargeAsync(keys, ifMatch, ct);

        // Deleting a missing row is a no-op, mirroring the SDK's idempotent DeleteEntity.
        var result = await rows.CommitAsync(keys.PartitionKey, [RowWrite.Delete(keys.RowKey, ifMatch)], ct);
        return result.Failure switch
        {
            RowCommitFailure.None => DeleteStatus.Deleted,
            RowCommitFailure.NotFound => DeleteStatus.NotFound,
            // The row is another version than the one the caller named.
            _ => throw ServiceErrors.Coded(new ConcurrencyConflictException(typeName, id, ServiceErrors.ConditionNotSatisfied())),
        };
    }

    /// <inheritdoc />
    public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default)
    {
        cache.BeginWrite();
        return Written(DeletePartitionCoreAsync(partition, ct), id: null);
    }

    private async Task<int> DeletePartitionCoreAsync(string partition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(partition))
            throw new ArgumentNullException(nameof(partition));

        partition = NormalizeId(partition); // match stored (normalized) PartitionKeys
        ct.ThrowIfCancellationRequested();
        await EnsureTableAsync(ct);
        if (LargeValueAware)
            return await DeletePartitionLargeAsync(partition, ct);

        // The rows are listed, then deleted a transaction (at most 100 rows) at a time. A row another
        // writer removed since the listing makes its transaction apply nothing: its rows are then
        // deleted one by one, and only the ones this call removed are counted.
        var listed = new List<string>();
        await foreach (var row in ScanAsync(new RowRange(partition), ct))
            listed.Add(row.RowKey);
        var deleted = 0;
        foreach (var chunk in listed.Chunk(BatchPlanner.DefaultMaxCount))
        {
            var result = await rows.CommitAsync(partition, [.. chunk.Select(rowKey => RowWrite.Delete(rowKey))], ct);
            if (result.Committed)
            {
                deleted += chunk.Length;
                continue;
            }
            foreach (var rowKey in chunk)
            {
                if ((await rows.CommitAsync(partition, [RowWrite.Delete(rowKey)], ct)).Committed)
                    deleted++;
            }
        }
        return deleted;
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private async Task<int> WriteBatchAsync(IReadOnlyCollection<T> entities, bool upsert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return 0;
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

        var now = clock.GetUtcNow();
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
            // unknown after a batch; re-read before optimistic updates.
            entity.ETag = null;
            entity.Id = NormalizeId(entity.Id);
            var keys = EntityId.Split(entity.Id);

            // A large-value aware create batch refuses a repeated key up front, as TableStorage<T> does; a
            // plain batch finds it in its transaction, as the service does (see WritePlainBatchAsync).
            if (LargeValueAware && !upsert && !withinBatch.Add(keys))
                throw new DuplicateKeyException(typeName, entity.Id,
                    new Azure.RequestFailedException(400, $"InvalidDuplicateRow: duplicate key within the batch. Id: {entity.Id}"));
            prepared.Add((keys, entity));
        }

        await EnsureTableAsync(ct);
        if (LargeValueAware)
            return await WriteBatchLargeAsync(prepared, upsert, ct);

        // Every entity is serialized before the first is stored, as TableStorage<T> serializes the whole
        // batch before it sends it: an entity the serializer refuses fails the batch with nothing written.
        var serialized = prepared.ConvertAll(p => Serialize(p.Keys, p.Entity));
        await WritePlainBatchAsync(prepared, serialized, upsert, ct);
        return prepared.Count;
    }

    /// <summary>
    /// The plain batch as TableStorage&lt;T&gt; sends it: per partition (in the order the partitions first
    /// appear), the rows packed into the same entity-group transactions (<see cref="BatchPlanner"/>, at most
    /// 100 rows and the payload budget each), each one commit of the row store. A transaction that fails
    /// applies nothing, and the ones before it stay committed. It fails as the service fails it, for the
    /// first row it refuses: a key the service refuses with 400 <c>InvalidInput</c>, a row named twice
    /// with 400 <c>InvalidDuplicateRow</c> (a <see cref="DuplicateKeyException"/> for a create, as
    /// TableStorage&lt;T&gt; translates it), a stored key of a create with a
    /// <see cref="DuplicateKeyException"/> over 409 <c>EntityAlreadyExists</c>, each wrapped in the
    /// transaction failure the SDK raises.
    /// </summary>
    private async Task WritePlainBatchAsync(List<((string, string) Keys, T Entity)> prepared, List<TableEntity> serialized, bool upsert, CancellationToken ct)
    {
        foreach (var group in Enumerable.Range(0, prepared.Count).GroupBy(i => prepared[i].Keys.Item1, StringComparer.Ordinal))
        {
            var members = group.ToArray();
            var plan = BatchPlanner.Plan([.. members.Select(i => TableRequestSize.EstimateEntityBytes(serialized[i]))]);
            foreach (var range in plan)
            {
                // A cancelled token fails a transaction before it is sent, as the SDK does.
                ct.ThrowIfCancellationRequested();
                var writes = new List<RowWrite>(range.Count);
                var inTransaction = new HashSet<(string, string)>();
                StorageException? refusal = null;
                for (var j = range.Start; j < range.Start + range.Count; j++)
                {
                    var (keys, entity) = prepared[members[j]];
                    if (ServiceErrors.RefusedKey(keys.Item1, keys.Item2) is { } refused)
                        refusal = ServiceErrors.Neutral(ServiceErrors.InTransaction(refused), typeName, entity.Id);
                    else if (ServiceErrors.RefusedRow(serialized[members[j]]) is { } oversized)
                        refusal = ServiceErrors.Neutral(ServiceErrors.InTransaction(oversized), typeName, entity.Id);
                    else if (!inTransaction.Add(keys))
                    {
                        var repeated = ServiceErrors.InTransaction(ServiceErrors.DuplicateRow(entity.Id));
                        // An upsert transaction that names a row twice is a request the service refuses.
                        refusal = upsert
                            ? ServiceErrors.Neutral(repeated, typeName, entity.Id)
                            : ServiceErrors.Coded(new DuplicateKeyException(typeName, entity.Id, repeated));
                    }
                    if (refusal is not null)
                        break;
                    var row = RowCells.Copy(serialized[members[j]]);
                    writes.Add(upsert ? RowWrite.Upsert(row) : RowWrite.Insert(row));
                }

                if (refusal is not null)
                {
                    // The rows before the refused one are looked at first, as the service looks at them:
                    // a stored key among them is the failure of the transaction.
                    if (!upsert)
                    {
                        for (var k = 0; k < writes.Count; k++)
                        {
                            if (await rows.GetAsync(group.Key, writes[k].RowKey, ct) is not null)
                                throw StoredKey(prepared[members[range.Start + k]].Entity.Id);
                        }
                    }
                    throw refusal;
                }

                var result = await rows.CommitAsync(group.Key, writes, ct);
                if (!result.Committed)
                    throw StoredKey(prepared[members[range.Start + result.FailedIndex]].Entity.Id);
            }
        }
    }

    private DuplicateKeyException StoredKey(string id) =>
        ServiceErrors.Coded(new DuplicateKeyException(typeName, id, ServiceErrors.InTransaction(ServiceErrors.AlreadyExists(id))));

    /// <summary>The rows a bounded query covers: its partition, and the RowKey prefix or bounds within it, normalized like ids.</summary>
    private RowRange Bounds(QueryOptions options)
    {
        options.ValidateRowKeyRange();
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

        if (partition is null)
            return RowRange.All;
        if (rowKeyPrefix is not null)
            return RowRange.Prefix(partition, rowKeyPrefix);
        return new RowRange(partition,
            options.FromRowKey is null ? null : NormalizeId(options.FromRowKey), options.FromInclusive,
            options.ToRowKey is null ? null : NormalizeId(options.ToRowKey), options.ToInclusive);
    }

    private async Task<IReadOnlyList<T>> BoundedAsync(RowRange range, int? take, IReadOnlyList<string>? select, CancellationToken ct)
    {
        var results = new List<T>(take ?? 4);
        await foreach (var entity in BoundedStreamAsync(range, take, select, ct))
            results.Add(entity);
        return results;
    }

    // A bounded query is the "never cached" read path, as in TableStorage. A row with a _SplitOf cell is
    // skipped for every T and never counts toward take. A projection that names no large value reads the
    // selected cells of each row and nothing else (nothing assembled, no blob read).
    private async IAsyncEnumerable<T> BoundedStreamAsync(RowRange range, int? take, IReadOnlyList<string>? select,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        var yielded = 0;
        // A page no larger than what is asked for: Take = 1 reads one row, not a thousand.
        var pageSize = take is int limit ? Math.Min(Math.Max(limit, 16), ScanPageSize) : ScanPageSize;
        await foreach (var row in ScanAsync(range, ct, pageSize))
        {
            if (LargeValueTable.IsPartRow(row.Data))
                continue;
            if (select is not null)
                yield return Materialize(row, select);
            else if (!LargeValueAware)
                yield return Materialize(row);
            else if (await MaterializeAsync(row, ct) is { } entity)
                yield return entity;
            else
                continue;
            if (take is int max && ++yielded >= max)
                yield break;
        }
    }

    /// <summary>The rows of <paramref name="range"/> in order, pulled from the row store a page at a time.</summary>
    private async IAsyncEnumerable<StoredRow> ScanAsync(RowRange range, [EnumeratorCancellation] CancellationToken ct, int pageSize = ScanPageSize)
    {
        var after = range.After;
        while (true)
        {
            var page = await rows.ReadAsync(range with { After = after }, pageSize, ct);
            foreach (var row in page)
                yield return row;
            if (page.Count < pageSize)
                yield break;
            after = (page[page.Count - 1].PartitionKey, page[page.Count - 1].RowKey);
        }
    }

    // The plain path, as TableStorage<T> serializes it: the row a read of a T comes from.
    private static TableEntity Serialize((string PartitionKey, string RowKey) keys, T entity) =>
        TableEntitySerializer.ToPlainTableEntity(entity, keys.PartitionKey, keys.RowKey, typeof(T));

    // The row as the store keeps it: a private copy (the serialized cells may be arrays the writer still
    // holds), refused when the service would refuse it (a cell above 64 KiB, a row above 1 MB).
    private TableEntity Stored(TableEntity serialized)
    {
        ServiceErrors.ThrowIfRefusedRow(serialized, typeName);
        return RowCells.Copy(serialized);
    }

    // A projected read (select not null) deserializes only the selected cells, as TableStorage<T> reads what
    // the service returns for a $select; nothing outside the row is read.
    private static T Materialize(StoredRow row, IReadOnlyList<string>? select)
    {
        if (select is null)
            return Materialize(row);
        var entity = ProjectedRows.Read<T>(RowCells.Copy(row.Data), select);
        entity.ETag = row.ETag;
        entity.Timestamp = row.Timestamp;
        return entity;
    }

    private static T Materialize(StoredRow row)
    {
        var entity = RowCells.Copy(row.Data).FromTableEntity<T>();
        entity.ETag = row.ETag;
        entity.Timestamp = row.Timestamp;
        return entity;
    }

    private async Task EnforceProtectedPropertiesAsync(T entity, CancellationToken ct)
    {
        if (ProtectedProps.Count == 0)
            return;

        var keys = EntityId.Split(NormalizeId(entity.Id));
        await EnsureTableAsync(ct);
        var row = await rows.GetAsync(keys.PartitionKey, keys.RowKey, ct);
        CheckProtectedProperties(row is null ? null : Materialize(row), entity);
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
            var largeValue = ProtectedLargeValues.IsLargeValue(typeof(T), prop);
            if (!(largeValue || oldVal is byte[] ? ProtectedLargeValues.IsUnchanged(oldVal, newVal) : Equals(oldVal, newVal))
                && !(authorizer?.IsAllowed(attr.Roles) ?? false))
            {
                throw new UnauthorizedAccessException(
                    $"Property '{prop.Name}' on {typeName} is protected (requires roles: {attr.Roles}). " +
                    (largeValue || ProtectedLargeValues.HoldsLargeValue(typeof(T), oldVal, newVal)
                        ? ProtectedLargeValues.DescribeRefusedChange(oldVal, newVal)
                        : $"The current caller is not authorised to change it from '{oldVal}' to '{newVal}'."));
            }
        }
    }
}

/// <summary>Copies of stored cells: what keeps a stored row and a caller's object from sharing anything mutable.</summary>
internal static class RowCells
{
    /// <summary>
    /// A copy of <paramref name="source"/> without its timestamp and ETag: every row goes into a store and
    /// comes out of it through one. The serializer passes a <c>byte[]</c> cell through by reference both
    /// ways (every other cell value is immutable), so each array is copied too.
    /// </summary>
    internal static TableEntity Copy(TableEntity source)
    {
        var copy = new TableEntity(source.PartitionKey, source.RowKey);
        foreach (var kv in source)
        {
            if (kv.Key is not ("PartitionKey" or "RowKey" or "odata.etag" or "Timestamp"))
                copy[kv.Key] = Detached(kv.Value);
        }
        return copy;
    }

    /// <summary>
    /// A cell value that shares nothing mutable with the given one. A <see cref="DateTimeOffset"/> is kept
    /// as the instant in UTC, as the service stores an Edm.DateTime and returns it (offset zero) whatever
    /// offset it was written with.
    /// </summary>
    internal static object Detached(object value) => value switch
    {
        byte[] bytes => bytes.Clone(),
        DateTimeOffset at => at.ToUniversalTime(),
        _ => value,
    };
}
