using System.Reflection;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Unified.Data.Tables;

/// <summary>
/// <see cref="IStorage{T}"/> implemented over a single Azure Table (one table per entity type
/// <typeparamref name="T"/>, named after <c>typeof(T).Name</c>). Reads are served through an
/// <see cref="IMemoryCache"/> (1&#160;hour sliding TTL); writes invalidate the relevant query caches
/// and keep the per-entity cache coherent. Register as an open generic singleton, e.g.
/// <c>services.AddSingleton(typeof(IStorage&lt;&gt;), typeof(TableStorage&lt;&gt;))</c> — or use
/// <see cref="ServiceCollectionExtensions.AddUnifiedTableStorage(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>.
/// </summary>
/// <typeparam name="T">The entity type; must derive from <see cref="Entity"/> and have a public parameterless constructor.</typeparam>
public class TableStorage<T> : IStorage<T> where T : Entity, new()
{
    private const char Separator = '|';
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

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
    private readonly string typeName = typeof(T).Name;

    // Track known partition keys so we can invalidate query caches
    private readonly HashSet<string> trackedPartitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object partitionLock = new();

    /// <summary>Creates a store without protected-property enforcement.</summary>
    public TableStorage(TableServiceClient serviceClient, IMemoryCache cache, ILogger<TableStorage<T>> logger)
        : this(serviceClient, cache, logger, authorizer: null)
    {
    }

    /// <summary>
    /// Creates a store, optionally supplying an <see cref="IProtectedPropertyAuthorizer"/> used to
    /// gate changes to <see cref="ProtectedPropertyAttribute"/>-decorated properties.
    /// </summary>
    public TableStorage(TableServiceClient serviceClient, IMemoryCache cache, ILogger<TableStorage<T>> logger,
        IProtectedPropertyAuthorizer? authorizer)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        this.cache = cache;
        this.logger = logger;
        this.authorizer = authorizer;
        client = serviceClient.GetTableClient(typeName);
        client.CreateIfNotExists();
    }

    /// <inheritdoc />
    public async Task<T> CreateAsync(T entity, CancellationToken ct = default)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }

        entity.CreatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null; // service last-write time is unknown until the next read
        entity.Id = NormalizeId(entity.Id);

        var (partitionKey, rowKey) = GetEntityKeys(entity.Id);
        var dataEntity = entity.ToTableEntity(partitionKey, rowKey);
        var addResponse = await client.AddEntityAsync(dataEntity, ct);

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
        if (cache.TryGetValue<CachedEntity>(cacheKey, out var cached) && cached is not null)
        {
            logger.LogDebug("[Cache HIT] {Type}.OneAsync id={Id}", typeName, id);
            return cached.Entity;
        }

        logger.LogDebug("[Cache MISS] {Type}.OneAsync id={Id} — fetching from Table Storage", typeName, id);
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

        if (cache.TryGetValue<CachedEntity>(EntityCacheKey(id), out var cached) && cached is not null)
        {
            logger.LogDebug("[Cache HIT] {Type}.ExistsAsync id={Id}", typeName, id);
            return true;
        }

        logger.LogDebug("[Cache MISS] {Type}.ExistsAsync id={Id} — fetching from Table Storage", typeName, id);
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
        if (cache.TryGetValue<IEnumerable<T>>(cacheKey, out var cached) && cached is not null)
        {
            logger.LogDebug("[Cache HIT] {Type}.QueryAsync partition={Partition}", typeName, partition ?? "*");
            return cached;
        }

        logger.LogDebug("[Cache MISS] {Type}.QueryAsync partition={Partition} — fetching from Table Storage", typeName, partition ?? "*");
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

        cache.Set(cacheKey, (IEnumerable<T>)results, new MemoryCacheEntryOptions
        {
            SlidingExpiration = CacheDuration
        });

        TrackPartition(partition);

        return results;
    }

    /// <summary>
    /// Apply a builder-driven partial update to a single entity by id. Sends only the declared
    /// columns via <see cref="TableUpdateMode.Merge"/>, so unrelated columns are preserved
    /// server-side and no read is needed. Last-writer-wins on the declared columns (no ETag check);
    /// other columns are race-safe by Merge semantics.
    /// </summary>
    /// <inheritdoc />
    public async Task UpdateAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct = default)
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

        var resp = await client.UpdateEntityAsync(partial, ETag.All, TableUpdateMode.Merge, ct);

        // Keep the cache coherent: if the entity is cached, patch it in-memory with the same updates
        // and the new ETag so subsequent reads stay warm. Otherwise drop the entry — the next
        // OneAsync will re-read fresh.
        if (cache.TryGetValue<CachedEntity>(EntityCacheKey(id), out var cached) && cached is not null)
        {
            ApplyUpdates(cached.Entity, builder.Updates);
            cached.Entity.UpdatedAt = (DateTimeOffset)partial[nameof(Entity.UpdatedAt)];
            cached.Entity.Timestamp = null; // the row changed server-side; stale until re-read
            CacheEntity(id, cached.Entity, resp.Headers.ETag ?? cached.ETag);
        }
        else
        {
            cache.Remove(EntityCacheKey(id));
        }

        InvalidateQueryCache(partitionKey);
    }

    private static void ApplyUpdates(T entity, Dictionary<string, object> updates)
    {
        var meta = TypeMetadataCache.GetMetadata(typeof(T));
        foreach (var (name, value) in updates)
        {
            if (!meta.PropertyMap.TryGetValue(name, out var prop))
            {
                throw new InvalidOperationException($"Property '{name}' not found on {typeof(T).Name}.");
            }

            prop.SetValue(entity, value);
        }
    }

    /// <inheritdoc />
    public async Task<T> UpdateAsync(T entity, CancellationToken ct = default)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }

        // Enforce [ProtectedProperty] on whole-entity replacement. If any protected field changed
        // and the caller isn't authorised (or no authorizer is registered), throw rather than
        // silently overwriting the protected data.
        if (ProtectedProps.Count > 0)
        {
            var stored = await OneAsync(entity.Id, ct);
            if (stored is not null)
            {
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
        }

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null; // service last-write time is unknown until the next read
        entity.Id = NormalizeId(entity.Id);

        var (partitionKey, rowKey) = GetEntityKeys(entity.Id);

        // Caller-supplied ETag wins (round-tripped through the API for cross-process optimistic
        // concurrency). Fall back to the server-side cache if the caller didn't carry one, finally
        // to ETag.All for legacy callers.
        var callerSuppliedETag = !string.IsNullOrEmpty(entity.ETag);
        var etag = callerSuppliedETag ? new ETag(entity.ETag!) : GetCachedETag(entity.Id);
        var dataEntity = entity.ToTableEntity(partitionKey, rowKey);

        try
        {
            var updateResponse = await client.UpdateEntityAsync(dataEntity, etag, TableUpdateMode.Replace, ct);
            var newETag = updateResponse.Headers.ETag ?? etag;
            entity.ETag = newETag.ToString();

            CacheEntity(entity.Id, entity, newETag);
            InvalidateQueryCache(partitionKey);
        }
        catch (RequestFailedException ex) when (ex.Status == 412 && !callerSuppliedETag)
        {
            // ETag mismatch on the cached (not caller-supplied) ETag — the cache was stale (e.g. a
            // concurrent write, or a QueryAsync overwrote the cache). Re-fetch the current ETag and
            // retry once. When the caller DID supply an ETag we deliberately do NOT retry: the 412
            // propagates so the genuine concurrency conflict surfaces to the caller.
            logger.LogWarning(ex, "ETag mismatch for {Type} {Id} — re-fetching and retrying once", typeName, entity.Id);

            var freshResponse = await client.GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct);
            if (!freshResponse.HasValue)
                throw; // Entity was deleted externally

            var freshETag = freshResponse.Value!.ETag;
            var retryResponse = await client.UpdateEntityAsync(dataEntity, freshETag, TableUpdateMode.Replace, ct);
            var newETag = retryResponse.Headers.ETag ?? freshETag;
            entity.ETag = newETag.ToString();

            CacheEntity(entity.Id, entity, newETag);
            InvalidateQueryCache(partitionKey);
        }

        return entity;
    }

    private static string NormalizeId(string id) => id.Trim().Replace(' ', '-').ToLowerInvariant();

    // Split on the FIRST separator only, so a row key may itself contain '|'
    // (e.g. "vision|execution|agent" → partition "vision", row "execution|agent").
    private static (string PartitionKey, string RowKey) GetEntityKeys(string id)
    {
        var keys = id.Split(Separator, 2);
        return keys.Length > 1 ? (keys[0], keys[1]) : (keys[0], keys[0]);
    }

    private string EntityCacheKey(string id) => $"{typeName}:entity:{id}";
    private string QueryCacheKey(string? partition) => $"{typeName}:query:{partition ?? "*"}";

    private void CacheEntity(string id, T entity, ETag etag)
    {
        cache.Set(EntityCacheKey(id), new CachedEntity(entity, etag), new MemoryCacheEntryOptions
        {
            SlidingExpiration = CacheDuration
        });
    }

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
