using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Unified.Data.Tables;

/// <summary>
/// <see cref="IStorage{T}"/> implemented over a single Azure Table (one table per entity type
/// <typeparamref name="T"/>, named after <c>typeof(T).Name</c>). Reads are served through an
/// <see cref="IMemoryCache"/> per the configured <see cref="CachePolicy"/> (default: 1&#160;hour
/// sliding TTL); writes invalidate the relevant query caches and keep the per-entity cache
/// coherent. The underlying table is created lazily on first use (see
/// <see cref="EnsureCreatedAsync"/> for eager creation). Register as an open generic singleton,
/// e.g. <c>services.AddSingleton(typeof(IStorage&lt;&gt;), typeof(TableStorage&lt;&gt;))</c> — or use
/// <see cref="ServiceCollectionExtensions.AddUnifiedTableStorage(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{UnifiedTableStorageOptions})"/>.
/// </summary>
/// <typeparam name="T">The entity type; must derive from <see cref="Entity"/> and have a public parameterless constructor.</typeparam>
public class TableStorage<T> : IStorage<T> where T : Entity, new()
{
    private const char Separator = '|';

    // Pre-computed list of properties decorated with [ProtectedProperty] for whole-entity
    // UpdateAsync(T) enforcement. Empty for entity types that have no protected fields.
    private static readonly List<(PropertyInfo Prop, ProtectedPropertyAttribute Attr)> ProtectedProps =
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Prop: p, Attr: p.GetCustomAttribute<ProtectedPropertyAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Prop, x.Attr!))
            .ToList();

    private readonly TableClient client;
    private readonly IMemoryCache cache;
    private readonly ILogger<TableStorage<T>> logger;
    private readonly IProtectedPropertyAuthorizer? authorizer;
    private readonly CachePolicy cachePolicy;
    private readonly string typeName = typeof(T).Name;

    // Track known partition keys so we can invalidate query caches
    private readonly HashSet<string> trackedPartitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object partitionLock = new();

    // Coalesced lazy CreateIfNotExists: no network I/O at construction/DI-resolve time, one create
    // per store shared by all callers, and a FAILED attempt is forgotten so the next call retries
    // instead of poisoning the store for the process lifetime.
    private Task? tableInit;
    private readonly object initLock = new();

    /// <summary>Creates a store without protected-property enforcement.</summary>
    public TableStorage(TableServiceClient serviceClient, IMemoryCache cache, ILogger<TableStorage<T>> logger)
        : this(serviceClient, cache, logger, authorizer: null, options: null)
    {
    }

    /// <summary>
    /// Creates a store, optionally supplying an <see cref="IProtectedPropertyAuthorizer"/> used to
    /// gate changes to <see cref="ProtectedPropertyAttribute"/>-decorated properties.
    /// </summary>
    public TableStorage(TableServiceClient serviceClient, IMemoryCache cache, ILogger<TableStorage<T>> logger,
        IProtectedPropertyAuthorizer? authorizer)
        : this(serviceClient, cache, logger, authorizer, options: null)
    {
    }

    /// <summary>
    /// Creates a store with an <see cref="UnifiedTableStorageOptions"/> (cache policy etc.) and an
    /// optional <see cref="IProtectedPropertyAuthorizer"/>. Null options fall back to the defaults
    /// (sliding 1&#160;hour cache — the 0.2.x behaviour). The trailing defaults let the DI
    /// container select this constructor even when neither optional service is registered.
    /// </summary>
    public TableStorage(TableServiceClient serviceClient, IMemoryCache cache, ILogger<TableStorage<T>> logger,
        IProtectedPropertyAuthorizer? authorizer = null, UnifiedTableStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        this.cache = cache;
        this.logger = logger;
        this.authorizer = authorizer;
        cachePolicy = (options ?? new UnifiedTableStorageOptions()).ResolveCachePolicy(typeof(T));
        client = serviceClient.GetTableClient(typeName);
    }

    /// <summary>
    /// Eagerly ensure the underlying table exists (it is otherwise created lazily on first use).
    /// Call from host startup for fail-fast semantics.
    /// </summary>
    public Task EnsureCreatedAsync(CancellationToken ct = default) => EnsureTableAsync(ct);

    private Task EnsureTableAsync(CancellationToken ct)
    {
        var existing = Volatile.Read(ref tableInit);
        return existing is { IsCompletedSuccessfully: true } ? Task.CompletedTask : EnsureTableSlowAsync(ct);
    }

    private async Task EnsureTableSlowAsync(CancellationToken ct)
    {
        Task pending;
        lock (initLock)
        {
            // Reuse an in-flight or succeeded attempt; start fresh after a failed/canceled one.
            pending = tableInit is { IsFaulted: false, IsCanceled: false }
                ? tableInit
                // The shared operation deliberately ignores the first caller's token — a canceled
                // caller must not cancel (and thereby poison) everyone else's init.
                : tableInit = client.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);
        }

        try
        {
            await pending.WaitAsync(ct);
        }
        catch
        {
            if (pending.IsFaulted || pending.IsCanceled)
            {
                lock (initLock)
                {
                    if (ReferenceEquals(tableInit, pending))
                        tableInit = null;
                }
            }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<T> CreateAsync(T entity, CancellationToken ct = default)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }

        await EnsureTableAsync(ct);

        entity.CreatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = entity.CreatedAt; // an insert is a write — keep audit stamps consistent
        entity.Timestamp = null; // service last-write time is unknown until the next read
        entity.Id = NormalizeId(entity.Id);

        var (partitionKey, rowKey) = GetEntityKeys(entity.Id);
        var dataEntity = entity.ToTableEntity(partitionKey, rowKey);
        Response addResponse;
        try
        {
            addResponse = await client.AddEntityAsync(dataEntity, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // An existing key is an EXPECTED outcome of concurrent creates — surface it
            // provider-agnostically (GetOrCreateAsync/MutateOrCreateAsync absorb it entirely).
            throw new DuplicateKeyException(typeName, entity.Id, ex);
        }

        // Read ETag from the response headers. Relying on dataEntity.ETag being mutated by the SDK
        // is brittle — an empty ETag would then be handed to the next UpdateEntityAsync call and
        // rejected by Azure Tables with "400 InvalidHeaderValue" on the If-Match header.
        ETag? responseETag = null;
        try
        {
            responseETag = addResponse?.Headers.ETag;
        }
        catch
        {
            // Test doubles may return a Response whose Headers struct has no backing store; fall
            // through to the entity's own ETag.
        }
        var insertedETag = responseETag ?? dataEntity.ETag;
        entity.ETag = insertedETag.ToString();
        CacheEntity(entity.Id, entity, insertedETag);
        InvalidateQueryCache(partitionKey);

        return entity;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }
        id = NormalizeId(id);

        await EnsureTableAsync(ct);

        var (partitionKey, rowKey) = GetEntityKeys(id);
        await client.DeleteEntityAsync(partitionKey, rowKey, cancellationToken: ct);

        cache.Remove(EntityCacheKey(id));
        InvalidateQueryCache(partitionKey);
    }

    /// <inheritdoc />
    public async Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(partition))
            throw new ArgumentNullException(nameof(partition));

        await EnsureTableAsync(ct);

        var entities = new List<TableEntity>();
        await foreach (var entity in client.QueryAsync<TableEntity>(
            e => e.PartitionKey == partition,
            select: ["PartitionKey", "RowKey"],
            cancellationToken: ct))
        {
            entities.Add(entity);
        }

        if (entities.Count == 0) return 0;

        // Azure Table batch: max 100 entities per transaction, same partition
        foreach (var batch in entities.Chunk(100))
        {
            var actions = batch.Select(e =>
                new TableTransactionAction(TableTransactionActionType.Delete, e, ETag.All));
            await client.SubmitTransactionAsync(actions, ct);
        }

        // Invalidate caches
        foreach (var e in entities)
            cache.Remove(EntityCacheKey($"{e.PartitionKey}{Separator}{e.RowKey}"));
        InvalidateQueryCache(partition);

        logger.LogInformation("Batch-deleted {Count} {Type} entities from partition {Partition}",
            entities.Count, typeName, partition);

        return entities.Count;
    }

    /// <inheritdoc />
    public async Task<T?> OneAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }
        id = NormalizeId(id);

        var cacheKey = EntityCacheKey(id);
        if (cachePolicy.Enabled && cache.TryGetValue<CachedEntity>(cacheKey, out var cached) && cached is not null)
        {
            logger.LogDebug("[Cache HIT] {Type}.OneAsync id={Id}", typeName, id);
            return cached.Entity;
        }

        logger.LogDebug("[Cache MISS] {Type}.OneAsync id={Id} — fetching from Table Storage", typeName, id);
        await EnsureTableAsync(ct);
        var (partitionKey, rowKey) = GetEntityKeys(id);
        var response = await client.GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct);

        if (!response.HasValue)
            return null;

        var entity = response.Value!.FromTableEntity<T>();
        entity.ETag = response.Value!.ETag.ToString();
        entity.Timestamp = response.Value!.Timestamp;
        CacheEntity(id, entity, response.Value!.ETag);
        return entity;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }
        id = NormalizeId(id);

        if (cachePolicy.Enabled && cache.TryGetValue<CachedEntity>(EntityCacheKey(id), out var cached) && cached is not null)
        {
            logger.LogDebug("[Cache HIT] {Type}.ExistsAsync id={Id}", typeName, id);
            return true;
        }

        logger.LogDebug("[Cache MISS] {Type}.ExistsAsync id={Id} — fetching from Table Storage", typeName, id);
        await EnsureTableAsync(ct);
        var (partitionKey, rowKey) = GetEntityKeys(id);
        var response = await client.GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct);

        if (response.HasValue)
        {
            var entity = response.Value!.FromTableEntity<T>();
            entity.ETag = response.Value!.ETag.ToString();
            entity.Timestamp = response.Value!.Timestamp;
            CacheEntity(id, entity, response.Value!.ETag);
        }

        return response.HasValue;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default)
    {
        var cacheKey = QueryCacheKey(partition);
        if (cachePolicy.Enabled && cache.TryGetValue<IEnumerable<T>>(cacheKey, out var cached) && cached is not null)
        {
            logger.LogDebug("[Cache HIT] {Type}.QueryAsync partition={Partition}", typeName, partition ?? "*");
            return cached;
        }

        logger.LogDebug("[Cache MISS] {Type}.QueryAsync partition={Partition} — fetching from Table Storage", typeName, partition ?? "*");
        await EnsureTableAsync(ct);
        var results = new List<T>();
        var query = string.IsNullOrWhiteSpace(partition)
            ? client.QueryAsync<TableEntity>(cancellationToken: ct)
            : client.QueryAsync<TableEntity>(x => x.PartitionKey == partition, cancellationToken: ct);

        await foreach (var tableEntity in query.WithCancellation(ct))
        {
            var entity = tableEntity.FromTableEntity<T>();
            entity.ETag = tableEntity.ETag.ToString();
            entity.Timestamp = tableEntity.Timestamp;
            results.Add(entity);

            // Warm the individual entity cache with fresh ETags
            CacheEntity(entity.Id, entity, tableEntity.ETag);
        }

        if (cachePolicy.Enabled)
        {
            cache.Set(cacheKey, (IEnumerable<T>)results, CacheEntryOptions());
            TrackPartition(partition);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var results = new List<T>(options.Take ?? 4);
        await foreach (var entity in QueryStreamAsync(options, ct))
        {
            results.Add(entity);
        }
        return results;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var partition = string.IsNullOrWhiteSpace(options?.Partition) ? null : options!.Partition;
        var rowKeyPrefix = string.IsNullOrWhiteSpace(options?.RowKeyPrefix) ? null : options!.RowKeyPrefix;
        var take = options?.Take;

        if (rowKeyPrefix is not null && partition is null)
            throw new ArgumentException(
                "RowKeyPrefix requires Partition — a cross-partition RowKey range is a full table scan wearing a filter.",
                nameof(options));
        if (take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), take, "Take must be positive.");
        if (options?.ContinuationToken is not null)
            throw new ArgumentException(
                "ContinuationToken is only honored by QueryPageAsync — use QueryPageAsync to resume paging.",
                nameof(options));

        await EnsureTableAsync(ct);

        var filter = BuildFilter(partition, rowKeyPrefix);
        var maxPerPage = take is int t ? Math.Min(t, 1000) : (int?)null;
        var pageable = client.QueryAsync<TableEntity>(filter, maxPerPage, cancellationToken: ct);

        var yielded = 0;
        await foreach (var row in pageable.WithCancellation(ct))
        {
            var entity = row.FromTableEntity<T>();
            entity.ETag = row.ETag.ToString();
            entity.Timestamp = row.Timestamp;
            yield return entity;

            if (take is int max && ++yielded >= max)
                yield break;
        }
    }

    /// <inheritdoc />
    public async Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default)
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
        var innerToken = options.ContinuationToken is null ? null : PageCursor.Decode(options.ContinuationToken, fingerprint);

        await EnsureTableAsync(ct);

        var filter = BuildFilter(partition, rowKeyPrefix);
        var pageable = client.QueryAsync<TableEntity>(filter, pageSize, cancellationToken: ct);

        await foreach (var page in pageable.AsPages(innerToken, pageSize).WithCancellation(ct))
        {
            var items = page.Values.Select(Materialize).ToList();
            var next = page.ContinuationToken is null ? null : PageCursor.Encode(fingerprint, page.ContinuationToken);
            return new EntityPage<T>(items, next);
        }

        return new EntityPage<T>([], null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default)
    {
        var results = new List<T>(take ?? 4);
        await foreach (var entity in QueryStreamAsync(predicate, partition, take, ct))
            results.Add(entity);
        return results;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> QueryStreamAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be positive.");

        var predicateFilter = TableFilterTranslator.Translate(predicate);
        var filter = string.IsNullOrWhiteSpace(partition)
            ? predicateFilter
            : $"{TableClient.CreateQueryFilter($"PartitionKey eq {partition}")} and ({predicateFilter})";

        await EnsureTableAsync(ct);

        var maxPerPage = take is int t ? Math.Min(t, 1000) : (int?)null;
        var pageable = client.QueryAsync<TableEntity>(filter, maxPerPage, cancellationToken: ct);

        var yielded = 0;
        await foreach (var row in pageable.WithCancellation(ct))
        {
            yield return Materialize(row);
            if (take is int max && ++yielded >= max)
                yield break;
        }
    }

    /// <inheritdoc />
    public async Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, string? partition = null, CancellationToken ct = default)
    {
        await foreach (var _ in QueryStreamAsync(predicate, partition, 1, ct))
            return true;
        return false;
    }

    // Deserialize a queried row, stamp its ETag/Timestamp, and warm the per-entity cache.
    private T Materialize(TableEntity row)
    {
        var entity = row.FromTableEntity<T>();
        entity.ETag = row.ETag.ToString();
        entity.Timestamp = row.Timestamp;
        CacheEntity(entity.Id, entity, row.ETag);
        return entity;
    }

    /// <inheritdoc />
    /// <remarks>Batch writes bypass [ProtectedProperty] enforcement (per-row reads would defeat
    /// the point of a batch) — treat them as trusted server-side paths.</remarks>
    public Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
        => WriteBatchAsync(entities, TableTransactionActionType.Add, ct);

    /// <inheritdoc />
    /// <remarks>Batch writes bypass [ProtectedProperty] enforcement (per-row reads would defeat
    /// the point of a batch) — treat them as trusted server-side paths.</remarks>
    public Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
        => WriteBatchAsync(entities, TableTransactionActionType.UpsertReplace, ct);

    /// <inheritdoc />
    public async Task<int> CountAsync(string? partition = null, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);

        var filter = string.IsNullOrWhiteSpace(partition)
            ? null
            : TableClient.CreateQueryFilter($"PartitionKey eq {partition}");

        // Keys-only projection keeps each page tiny; Azure Tables has no server-side count.
        var count = 0;
        await foreach (var _ in client.QueryAsync<TableEntity>(filter, select: ["PartitionKey"], cancellationToken: ct)
                           .WithCancellation(ct))
        {
            count++;
        }
        return count;
    }

    private async Task<int> WriteBatchAsync(
        IReadOnlyCollection<T> entities, TableTransactionActionType actionType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return 0;

        await EnsureTableAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var rows = new List<(string Partition, string Id, TableEntity Row)>(entities.Count);
        foreach (var entity in entities)
        {
            if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
                throw new ArgumentException("Every batched entity must be non-null and have an Id.", nameof(entities));

            if (actionType == TableTransactionActionType.Add || entity.CreatedAt == default)
                entity.CreatedAt = now;
            entity.UpdatedAt = now;
            entity.Timestamp = null;
            // Row versions change but transaction sub-responses aren't correlated back — a kept
            // ETag would be a phantom-412 trap on the next optimistic update. Null = "re-read
            // before optimistic concurrency", the same doctrine as Timestamp.
            entity.ETag = null;
            entity.Id = NormalizeId(entity.Id);

            var (partitionKey, rowKey) = GetEntityKeys(entity.Id);
            rows.Add((partitionKey, entity.Id, entity.ToTableEntity(partitionKey, rowKey)));
        }

        try
        {
            foreach (var group in rows.GroupBy(r => r.Partition, StringComparer.Ordinal))
            {
                // Azure Table transactions: max 100 entities, single partition.
                foreach (var chunk in group.Chunk(100))
                {
                    var actions = chunk.Select(r => new TableTransactionAction(actionType, r.Row, ETag.All));
                    try
                    {
                        await client.SubmitTransactionAsync(actions, ct);
                    }
                    catch (TableTransactionFailedException ex)
                        when (actionType == TableTransactionActionType.Add
                            && (ex.Status == 409 || ex.ErrorCode == "InvalidDuplicateRow"))
                    {
                        // 409 = a row already exists in the table; 400 InvalidDuplicateRow = the
                        // same key twice WITHIN this transaction. Both are duplicate keys — and
                        // InMemoryStorage translates both, so production must match the fake.
                        var duplicateId = ex.FailedTransactionActionIndex is int i && i >= 0 && i < chunk.Length
                            ? chunk[i].Id
                            : chunk[0].Partition + Separator + "?";
                        throw new DuplicateKeyException(typeName, duplicateId, ex);
                    }
                }
            }
        }
        finally
        {
            // A partially-failed batch has still COMMITTED earlier chunks — invalidate the caches
            // for everything we may have touched, success or not, so no pre-image is ever served.
            foreach (var partition in rows.Select(r => r.Partition).Distinct(StringComparer.Ordinal))
                InvalidateQueryCache(partition);
            foreach (var (_, id, _) in rows)
                cache.Remove(EntityCacheKey(id));
        }

        logger.LogInformation("Batch-{Action} {Count} {Type} entities across {Partitions} partition(s)",
            actionType, rows.Count, typeName, rows.Select(r => r.Partition).Distinct(StringComparer.Ordinal).Count());

        return rows.Count;
    }

    private static string? BuildFilter(string? partition, string? rowKeyPrefix)
    {
        if (partition is null)
            return null;
        if (rowKeyPrefix is null)
            return TableClient.CreateQueryFilter($"PartitionKey eq {partition}");

        // Canonical prefix range: RowKey >= prefix AND RowKey < next(prefix).
        var upperBound = NextPrefix(rowKeyPrefix);
        return upperBound is null
            ? TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey ge {rowKeyPrefix}")
            : TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey ge {rowKeyPrefix} and RowKey lt {upperBound}");
    }

    // The smallest string strictly greater than every string starting with `prefix`: increment the
    // last incrementable char and truncate. An all-U+FFFF prefix has no finite upper bound → null.
    private static string? NextPrefix(string prefix)
    {
        for (var i = prefix.Length - 1; i >= 0; i--)
        {
            if (prefix[i] != char.MaxValue)
            {
                return prefix[..i] + (char)(prefix[i] + 1);
            }
        }
        return null;
    }

    /// <summary>
    /// Apply a builder-driven partial update to a single entity by id. Sends only the declared
    /// columns via <see cref="TableUpdateMode.Merge"/>, so unrelated columns are preserved
    /// server-side and no read is needed — concurrent merges to disjoint columns never conflict.
    /// Without <see cref="UpdateBuilder{T}.WithETag"/> the declared columns are last-writer-wins;
    /// with it the merge is conditional and a lost race throws
    /// <see cref="ConcurrencyConflictException"/>.
    /// </summary>
    /// <inheritdoc />
    public async Task<string> UpdateAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct = default)
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

        id = NormalizeId(id);
        await EnsureTableAsync(ct);
        var (partitionKey, rowKey) = GetEntityKeys(id);

        var partial = new TableEntity(partitionKey, rowKey);
        foreach (var (name, value) in builder.Updates)
        {
            foreach (var (col, cell) in TableEntitySerializer.FlattenProperty(name, value))
            {
                partial[col] = cell;
            }
        }
        // Always bump UpdatedAt — set last so it wins over any caller-supplied value.
        partial[nameof(Entity.UpdatedAt)] = DateTimeOffset.UtcNow;

        var condition = builder.ETag is null ? ETag.All : new ETag(builder.ETag);
        Response resp;
        try
        {
            resp = await client.UpdateEntityAsync(partial, condition, TableUpdateMode.Merge, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            // Conditional merge lost the race. The 412 proves a foreign writer touched the row,
            // so the cached entity AND any cached query results for its partition are stale.
            cache.Remove(EntityCacheKey(id));
            InvalidateQueryCache(partitionKey);
            throw new ConcurrencyConflictException(typeName, id, ex);
        }

        // Keep the cache coherent: if the entity is cached and every declared path maps onto a
        // top-level property, patch it in place with the new ETag so reads stay warm. Nested-path
        // updates (or unknown names) evict instead — the next OneAsync re-reads fresh.
        var newETag = ReadETagHeader(resp);
        if (cachePolicy.Enabled && cache.TryGetValue<CachedEntity>(EntityCacheKey(id), out var cached) && cached is not null
            && TryApplyUpdates(cached.Entity, builder.Updates))
        {
            cached.Entity.UpdatedAt = (DateTimeOffset)partial[nameof(Entity.UpdatedAt)];
            cached.Entity.Timestamp = null; // the row changed server-side; stale until re-read
            CacheEntity(id, cached.Entity, newETag ?? cached.ETag);
        }
        else
        {
            cache.Remove(EntityCacheKey(id));
        }

        InvalidateQueryCache(partitionKey);
        return newETag?.ToString() ?? string.Empty;
    }

    private static ETag? ReadETagHeader(Response response)
    {
        try
        {
            return response?.Headers.ETag;
        }
        catch (Exception ex) when (ex is NotSupportedException or NotImplementedException or NullReferenceException)
        {
            // Test doubles (strict mocks, partial fakes) may not implement the Headers plumbing —
            // tolerate exactly those. Anything else is a genuine SDK failure and must surface,
            // not be masked as "no ETag".
            return null;
        }
    }

    private static bool TryApplyUpdates(T entity, Dictionary<string, object> updates)
    {
        // Validate every path BEFORE mutating: the cached instance is shared with prior readers,
        // so a partial patch followed by eviction would leave them a half-updated object.
        var meta = TypeMetadataCache.GetMetadata(typeof(T));
        foreach (var name in updates.Keys)
        {
            if (!meta.PropertyMap.ContainsKey(name))
            {
                return false; // nested/unknown path — caller evicts the cache entry instead
            }
        }

        foreach (var (name, value) in updates)
        {
            meta.PropertyMap[name].SetValue(entity, value);
        }
        return true;
    }

    /// <inheritdoc />
    public Task<T> UpdateAsync(T entity, CancellationToken ct = default)
        => UpdateAsync(entity, ConcurrencyMode.Auto, ct);

    /// <inheritdoc />
    public async Task<T> UpdateAsync(T entity, ConcurrencyMode mode, CancellationToken ct = default)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }

        await EnforceProtectedPropertiesAsync(entity, ct);

        // Normalize first: the cached-ETag fallback below is keyed by the normalized id.
        entity.Id = NormalizeId(entity.Id);

        // Capture concurrency intent BEFORE mutating the entity.
        var callerSuppliedETag = !string.IsNullOrEmpty(entity.ETag);
        var etag = mode switch
        {
            // Strict: the round-tripped row version is mandatory; its absence is a caller bug,
            // not a case to silently degrade to last-writer-wins.
            ConcurrencyMode.Strict when !callerSuppliedETag => throw new InvalidOperationException(
                $"ConcurrencyMode.Strict requires {typeName}.ETag — read the entity first and round-trip its ETag."),
            ConcurrencyMode.Strict => new ETag(entity.ETag!),
            ConcurrencyMode.LastWriterWins => ETag.All,
            // Auto: caller-supplied ETag wins (cross-process optimistic concurrency); fall back to
            // the server-side cache, finally to ETag.All for legacy callers.
            _ => callerSuppliedETag ? new ETag(entity.ETag!) : GetCachedETag(entity.Id)
        };

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null; // service last-write time is unknown until the next read

        await EnsureTableAsync(ct);
        var (partitionKey, rowKey) = GetEntityKeys(entity.Id);
        var dataEntity = entity.ToTableEntity(partitionKey, rowKey);

        try
        {
            var updateResponse = await client.UpdateEntityAsync(dataEntity, etag, TableUpdateMode.Replace, ct);
            var newETag = updateResponse.Headers.ETag ?? etag;
            entity.ETag = newETag.ToString();

            CacheEntity(entity.Id, entity, newETag);
            InvalidateQueryCache(partitionKey);
        }
        catch (RequestFailedException ex) when (ex.Status == 412 && mode == ConcurrencyMode.Auto && !callerSuppliedETag)
        {
            // ETag mismatch on the cached (not caller-supplied) ETag — the cache was stale (e.g. a
            // concurrent write, or a QueryAsync overwrote the cache). Re-fetch the current ETag and
            // retry once. When the caller DID supply an ETag (or asked for Strict) we deliberately
            // do NOT retry: the genuine conflict surfaces to the caller (see the catch below).
            logger.LogWarning(ex, "ETag mismatch for {Type} {Id} — re-fetching and retrying once", typeName, entity.Id);

            var freshResponse = await client.GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct);
            if (!freshResponse.HasValue)
            {
                // Deleted externally between our read and this write — a conflict by definition.
                cache.Remove(EntityCacheKey(entity.Id));
                InvalidateQueryCache(partitionKey);
                throw new ConcurrencyConflictException(typeName, entity.Id, ex);
            }

            var freshETag = freshResponse.Value!.ETag;
            try
            {
                var retryResponse = await client.UpdateEntityAsync(dataEntity, freshETag, TableUpdateMode.Replace, ct);
                var newETag = retryResponse.Headers.ETag ?? freshETag;
                entity.ETag = newETag.ToString();

                CacheEntity(entity.Id, entity, newETag);
                InvalidateQueryCache(partitionKey);
            }
            catch (RequestFailedException retryEx) when (retryEx.Status == 412)
            {
                // Lost the race AGAIN between the re-fetch and the retry — a genuinely hot row.
                cache.Remove(EntityCacheKey(entity.Id));
                InvalidateQueryCache(partitionKey);
                throw new ConcurrencyConflictException(typeName, entity.Id, retryEx);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            // Strict mode, or Auto with a caller-round-tripped ETag: the row changed since it was
            // read. Surface the provider-agnostic conflict; the 412 also proves the cached entity
            // and the partition's cached query results are stale, so drop both — a follow-up read
            // (e.g. MutateAsync's retry) must see the fresh row.
            cache.Remove(EntityCacheKey(entity.Id));
            InvalidateQueryCache(partitionKey);
            throw new ConcurrencyConflictException(typeName, entity.Id, ex);
        }

        return entity;
    }

    /// <inheritdoc />
    public async Task<T> UpsertAsync(T entity, CancellationToken ct = default)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }

        await EnforceProtectedPropertiesAsync(entity, ct);

        // Preserve a caller-supplied creation stamp (read-modify-write flows); stamp fresh
        // otherwise. UpdatedAt is always bumped — an upsert is a write either way.
        if (entity.CreatedAt == default)
            entity.CreatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null; // service last-write time is unknown until the next read
        entity.Id = NormalizeId(entity.Id);

        await EnsureTableAsync(ct);
        var (partitionKey, rowKey) = GetEntityKeys(entity.Id);
        var dataEntity = entity.ToTableEntity(partitionKey, rowKey);
        var upsertResponse = await client.UpsertEntityAsync(dataEntity, TableUpdateMode.Replace, ct);

        // Same header-first ETag read as CreateAsync — the SDK does not mutate dataEntity.ETag.
        ETag? responseETag = null;
        try
        {
            responseETag = upsertResponse?.Headers.ETag;
        }
        catch
        {
            // Test doubles may return a Response whose Headers struct has no backing store.
        }
        var newETag = responseETag ?? dataEntity.ETag;
        entity.ETag = newETag.ToString();
        CacheEntity(entity.Id, entity, newETag);
        InvalidateQueryCache(partitionKey);

        return entity;
    }

    /// <summary>
    /// Enforce [ProtectedProperty] on whole-entity writes. If any protected field changed relative
    /// to the stored row and the caller isn't authorised (or no authorizer is registered), throw
    /// rather than silently overwriting the protected data. No-op (and no read) for entity types
    /// without protected properties.
    /// </summary>
    private async Task EnforceProtectedPropertiesAsync(T entity, CancellationToken ct)
    {
        if (ProtectedProps.Count == 0)
            return;

        var stored = await OneAsync(entity.Id, ct);
        if (stored is null)
            return;

        foreach (var (prop, attr) in ProtectedProps)
        {
            var oldVal = prop.GetValue(stored);
            var newVal = prop.GetValue(entity);
            if (!Equals(oldVal, newVal) && !(authorizer?.IsAllowed(attr.Roles) ?? false))
            {
                throw new UnauthorizedAccessException(
                    $"Property '{prop.Name}' on {typeName} is protected (requires roles: {attr.Roles}). " +
                    $"The current caller is not authorised to change it from '{oldVal}' to '{newVal}'.");
            }
        }
    }

    private static string NormalizeId(string id) => EntityId.Normalize(id);

    // Split on the FIRST separator only, so a row key may itself contain '|'
    // (e.g. "vision|execution|agent" → partition "vision", row "execution|agent").
    private static (string PartitionKey, string RowKey) GetEntityKeys(string id) => EntityId.Split(id);

    private string EntityCacheKey(string id) => $"{typeName}:entity:{id}";
    private string QueryCacheKey(string? partition) => $"{typeName}:query:{partition ?? "*"}";

    private void CacheEntity(string id, T entity, ETag etag)
    {
        if (!cachePolicy.Enabled)
            return;

        cache.Set(EntityCacheKey(id), new CachedEntity(entity, etag), CacheEntryOptions());
    }

    private MemoryCacheEntryOptions CacheEntryOptions() => cachePolicy.Mode == CacheExpirationMode.Absolute
        ? new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = cachePolicy.Ttl }
        : new MemoryCacheEntryOptions { SlidingExpiration = cachePolicy.Ttl };

    private ETag GetCachedETag(string id)
    {
        return cache.TryGetValue<CachedEntity>(EntityCacheKey(id), out var cached) && cached is not null
            ? cached.ETag
            : ETag.All;
    }

    private void InvalidateQueryCache(string partitionKey)
    {
        cache.Remove(QueryCacheKey(partitionKey));
        cache.Remove(QueryCacheKey(null)); // Also invalidate the "all" query

        lock (partitionLock)
        {
            foreach (var p in trackedPartitions)
            {
                cache.Remove(QueryCacheKey(p));
            }
        }
    }

    private void TrackPartition(string? partition)
    {
        if (partition == null) return;
        lock (partitionLock)
        {
            trackedPartitions.Add(partition);
        }
    }

    private sealed record CachedEntity(T Entity, ETag ETag);
}
