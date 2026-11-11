using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unified.Data.Tables.Providers;

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
/// <typeparam name="T">The entity type; implements <see cref="IEntity"/> (derive from <see cref="Entity"/> for the convenience base) and has a public parameterless constructor.</typeparam>
public partial class TableStorage<T> : IStorage<T>, IOrphanBlobSweeper where T : class, IEntity, new()
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
    private readonly IdNormalization idNormalization;
    private readonly bool implicitLastWriterWins;
    private readonly string typeName = typeof(T).Name;

    // Cache keys use the fully-qualified type name so two entity types with the same simple name in
    // different namespaces never collide in a shared IMemoryCache. (The Azure table name still uses the
    // simple name for back-compat — changing that would rename the table and need a data migration.)
    private readonly string cacheKeyPrefix = typeof(T).FullName ?? typeof(T).Name;

    private readonly TableInitializer tableInitializer;

    // The resolved table name (also the first segment of every offloaded blob name).
    private readonly string tableName;

    // The large-value rules of this table (split rows, blob offload, cleanup), shared with other
    // providers through the storage-provider SPI so they run the same code.
    private readonly LargeValueTable large;

    /// <summary>The client clock blob cleanup falls back to when no service time is known (for tests).</summary>
    internal TimeProvider LargeValueClock
    {
        get => large.Clock;
        set => large.Clock = value;
    }

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
    /// (sliding 1&#160;hour cache — the 0.2.x behaviour). Blob offload needs a
    /// <see cref="BlobServiceClient"/> for a type with large-value members: set <see cref="BlobOffloadOptions.ServiceClient"/> or use the constructor that takes one.
    /// </summary>
    public TableStorage(TableServiceClient serviceClient, IMemoryCache cache, ILogger<TableStorage<T>> logger,
        IProtectedPropertyAuthorizer? authorizer, UnifiedTableStorageOptions? options)
        : this(serviceClient, cache, logger, authorizer, options, blobServiceClient: null)
    {
    }

    /// <summary>
    /// Creates a store with an <see cref="UnifiedTableStorageOptions"/>, an optional
    /// <see cref="IProtectedPropertyAuthorizer"/> and an optional <see cref="BlobServiceClient"/> for
    /// blob offload (<see cref="UnifiedTableStorageOptions.BlobOffload"/>). The trailing defaults let the
    /// DI container select this constructor whichever of the optional services are registered; in DI the
    /// blob client is the library's own keyed registration (see <c>AddUnifiedTableStorage</c>), never the
    /// host's unkeyed <see cref="BlobServiceClient"/>. <see cref="BlobOffloadOptions.ServiceClient"/> is used
    /// when <paramref name="blobServiceClient"/> is null. No call is made to either service here; the table
    /// and the blob container are created on first use.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="UnifiedTableStorageOptions.BlobOffload"/> is set, <typeparamref name="T"/> has a
    /// <see cref="TableValueSplitAttribute"/> or <see cref="TableBlobContent"/> member, and no blob client is
    /// supplied (neither <paramref name="blobServiceClient"/> nor <see cref="BlobOffloadOptions.ServiceClient"/>).
    /// Any other <typeparamref name="T"/> needs none.
    /// </exception>
    public TableStorage(TableServiceClient serviceClient, IMemoryCache cache, ILogger<TableStorage<T>> logger,
        IProtectedPropertyAuthorizer? authorizer = null, UnifiedTableStorageOptions? options = null,
        [FromKeyedServices(ServiceCollectionExtensions.OffloadBlobServiceClientKey)] BlobServiceClient? blobServiceClient = null)
        : this(serviceClient, cache, logger, authorizer, options, OffloadStore(options, blobServiceClient))
    {
    }

    /// <summary>Creates a store over an explicit offload blob store (tests and other providers).</summary>
    internal TableStorage(TableServiceClient serviceClient, IMemoryCache cache, ILogger<TableStorage<T>> logger,
        IProtectedPropertyAuthorizer? authorizer, UnifiedTableStorageOptions? options, IOffloadBlobStore? blobs)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        this.cache = cache;
        this.logger = logger;
        this.authorizer = authorizer;
        var opts = options ?? new UnifiedTableStorageOptions();
        cachePolicy = opts.ResolveCachePolicy(typeof(T));
        idNormalization = opts.IdNormalization;
        implicitLastWriterWins = opts.ImplicitLastWriterWins;
        tableName = ResolveTableName(opts);
        client = serviceClient.GetTableClient(tableName);
        tableInitializer = new TableInitializer(client);
        large = new LargeValueTable(tableName, opts.BlobOffload, blobs);
    }

    // The Azure blob container for this store's offloaded values. Built only for a type that can carry
    // them (no client call either way); a plain type never touches the blob service and needs no client,
    // so a host with offload configured but no blob client still resolves every plain store.
    private static AzureOffloadBlobStore? OffloadStore(UnifiedTableStorageOptions? options, BlobServiceClient? blobServiceClient)
    {
        if (!LargeValueAware || options?.BlobOffload is not { } offload)
            return null;
        var client = blobServiceClient ?? offload.ServiceClient
            ?? throw new InvalidOperationException(
                $"UnifiedTableStorageOptions.BlobOffload is set and {typeof(T).Name} has large-value members, but no {nameof(BlobServiceClient)} was supplied. " +
                $"Set {nameof(BlobOffloadOptions)}.{nameof(BlobOffloadOptions.ServiceClient)}, set {nameof(BlobOffloadOptions)}.{nameof(BlobOffloadOptions.UseRegisteredServiceClient)} " +
                $"to use the {nameof(BlobServiceClient)} registered in DI, use a connection-string or credential overload of AddUnifiedTableStorage that derives one, or pass one to the constructor.");
        return new AzureOffloadBlobStore(client.GetBlobContainerClient(offload.ContainerName));
    }

    /// <summary>
    /// The table this store reads and writes. Defaults to <c>typeof(T).Name</c>; see
    /// <see cref="UnifiedTableStorageOptions.TableNameResolver"/> for why an override exists.
    /// A resolver returning null or whitespace falls back to the default rather than producing an
    /// unusable client — a silently wrong table name is far worse than ignoring a broken resolver.
    /// </summary>
    internal static string ResolveTableName(UnifiedTableStorageOptions options)
    {
        var resolved = options.TableNameResolver?.Invoke(typeof(T));
        return string.IsNullOrWhiteSpace(resolved) ? typeof(T).Name : resolved!;
    }

    /// <summary>
    /// Eagerly ensure the underlying table exists (it is otherwise created lazily on first use).
    /// Call from host startup for fail-fast semantics.
    /// </summary>
    public Task EnsureCreatedAsync(CancellationToken ct = default) => EnsureTableAsync(ct);

    private Task EnsureTableAsync(CancellationToken ct) => tableInitializer.EnsureAsync(ct);

    /// <inheritdoc />
    public async Task<T> CreateAsync(T entity, CancellationToken ct = default)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Id))
        {
            throw new ArgumentNullException(nameof(entity));
        }
        RefuseReservedRowKey(entity.Id);

        await EnsureTableAsync(ct);

        entity.CreatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = entity.CreatedAt; // an insert is a write — keep audit stamps consistent
        entity.Timestamp = null; // service last-write time is unknown until the next read
        entity.Id = NormalizeId(entity.Id);

        var (partitionKey, rowKey) = GetEntityKeys(entity.Id);
        if (LargeValueAware)
        {
            CommitLargeWrite(entity, partitionKey, await WriteLargeAsync(entity, LargeWriteKind.Create, ETag.All, ct));
            return entity;
        }

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
        // Any key is deletable (a row written before the type opted in may have any shape); a
        // continuation row addressed by id is not an entity, so the split-aware delete finds nothing.
        await EnsureTableAsync(ct);

        var (partitionKey, rowKey) = GetEntityKeys(id);
        if (LargeValueAware)
            await DeleteLargeAsync(partitionKey, rowKey, ct);
        else
            await client.DeleteEntityAsync(partitionKey, rowKey, cancellationToken: ct);

        cache.Remove(EntityCacheKey(id));
        InvalidateQueryCache(partitionKey);
    }

    /// <inheritdoc />
    public async Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(partition))
            throw new ArgumentNullException(nameof(partition));
        partition = NormalizeId(partition); // match stored (normalized) PartitionKeys

        await EnsureTableAsync(ct);

        var entities = new List<TableEntity>();
        await foreach (var entity in client.QueryAsync<TableEntity>(
            e => e.PartitionKey == partition,
            select: LargeValueAware ? ["PartitionKey", "RowKey", SystemColumnNames.SplitOf] : ["PartitionKey", "RowKey"],
            cancellationToken: ct))
        {
            entities.Add(entity);
        }

        if (entities.Count == 0)
        {
            await DeletePartitionBlobsAsync(partition, ct);
            return 0;
        }
        // Every row goes, continuation rows included; only logical entities are counted.
        var logicalCount = LargeValueAware ? entities.Count(e => !SplitRowLayout.IsPartRow(e)) : entities.Count;

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
            logicalCount, typeName, partition);

        // The rows are gone: so are the blobs stored under the partition (best effort; a cancelled token
        // only bounds how long that may take, the rest is left to the orphan sweep).
        await DeletePartitionBlobsAsync(partition, ct);
        return logicalCount;
    }

    /// <inheritdoc />
    public async Task<T?> OneAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }
        id = NormalizeId(id);

        if (cachePolicy.Enabled && cache.TryGetValue<CachedEntity>(EntityKey(id), out var cached) && cached is not null)
        {
            logger.LogDebug("[Cache HIT] {Type}.OneAsync id={Id}", typeName, id);
            return Clone(cached.Entity);
        }

        logger.LogDebug("[Cache MISS] {Type}.OneAsync id={Id} — fetching from Table Storage", typeName, id);
        // Taken before the first storage call: what this read finds is cached only if no write or delete
        // of the entity has completed in between (see WriteGenerations).
        var mark = BeginRead(id);
        await EnsureTableAsync(ct);
        var (partitionKey, rowKey) = GetEntityKeys(id);
        var response = await client.GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct);

        if (!response.HasValue || SplitRowLayout.IsPartRow(response.Value!))
            return null;

        TableEntity row;
        T entity;
        if (LargeValueAware)
        {
            // Deleted while its parts or blobs were read: absent.
            if (await ReadLargeAsync(response.Value!, ct) is not { } read)
                return null;
            (row, entity) = read;
        }
        else
        {
            row = response.Value!;
            entity = row.FromTableEntity<T>();
        }
        entity.ETag = row.ETag.ToString();
        entity.Timestamp = row.Timestamp;
        CacheRead(id, entity, row.ETag, mark);
        // `entity` is now caller-private (CacheRead snapshotted its own copy), so hand it back as-is.
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

        if (cachePolicy.Enabled && cache.TryGetValue<CachedEntity>(EntityKey(id), out var cached) && cached is not null)
        {
            logger.LogDebug("[Cache HIT] {Type}.ExistsAsync id={Id}", typeName, id);
            return true;
        }

        logger.LogDebug("[Cache MISS] {Type}.ExistsAsync id={Id} — fetching from Table Storage", typeName, id);
        var mark = BeginRead(id); // before the first storage call, as in OneAsync
        await EnsureTableAsync(ct);
        var (partitionKey, rowKey) = GetEntityKeys(id);
        var response = await client.GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct);

        if (response.HasValue && SplitRowLayout.IsPartRow(response.Value!))
            return false;

        // A split or offloaded value is not assembled or downloaded just to answer "does it exist" - it
        // is simply not cached.
        // A plain type over a row another type wrote with large-value cells is not cached either: its
        // read would throw for an offloaded value, and existence needs no value.
        if (response.HasValue && (LargeValueAware
            ? SplitRowLayout.PartCount(response.Value!) == 0 && !HasBlobReference(response.Value!)
            : !TableEntitySerializer.HasLargeValueCells(response.Value!)))
        {
            var entity = response.Value!.FromTableEntity<T>();
            entity.ETag = response.Value!.ETag.ToString();
            entity.Timestamp = response.Value!.Timestamp;
            CacheRead(id, entity, response.Value!.ETag, mark);
        }

        return response.HasValue;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default)
    {
        // Match stored (normalized) PartitionKeys — writes normalize the id, so a natural-form partition
        // ("My Vision") would otherwise silently match nothing. Collapse whitespace to null so a
        // whole-table query caches under the "*" key that writes actually invalidate (not a raw
        // whitespace key that InvalidateQueryCache would never evict).
        partition = string.IsNullOrWhiteSpace(partition) ? null : NormalizeId(partition);

        var cacheKey = QueryCacheKey(partition);
        if (cachePolicy.Enabled && cache.TryGetValue<IEnumerable<T>>(cacheKey, out var cached) && cached is not null)
        {
            logger.LogDebug("[Cache HIT] {Type}.QueryAsync partition={Partition}", typeName, partition ?? "*");
            return cached.Select(Clone).ToList();
        }

        logger.LogDebug("[Cache MISS] {Type}.QueryAsync partition={Partition} — fetching from Table Storage", typeName, partition ?? "*");
        // Taken before the first storage call: the list, and each entity it warms, is cached only if no
        // write or delete it could have missed has completed in between (see WriteGenerations).
        var mark = BeginScan();
        await EnsureTableAsync(ct);
        var results = new List<T>();
        var query = string.IsNullOrWhiteSpace(partition)
            ? client.QueryAsync<TableEntity>(cancellationToken: ct)
            : client.QueryAsync<TableEntity>(x => x.PartitionKey == partition, cancellationToken: ct);

        await foreach (var queried in query.WithCancellation(ct))
        {
            if (SplitRowLayout.IsPartRow(queried))
                continue;
            TableEntity tableEntity;
            T entity;
            if (LargeValueAware)
            {
                // Deleted while its parts or blobs were read: no longer part of the result.
                if (await ReadLargeAsync(queried, ct) is not { } read)
                    continue;
                (tableEntity, entity) = read;
            }
            else
            {
                tableEntity = queried;
                entity = queried.FromTableEntity<T>();
            }
            entity.ETag = tableEntity.ETag.ToString();
            entity.Timestamp = tableEntity.Timestamp;
            results.Add(entity);

            // Warm the individual entity cache with fresh ETags
            CacheRead(entity.Id, entity, tableEntity.ETag, mark);
        }

        if (cachePolicy.Enabled)
            // Sized by row count so a SizeLimit-bounded cache actually accounts for a large (or
            // whole-table) result list instead of counting it as one unit. Not cached when a write to
            // the partition (to any partition, for the whole table) completed while it was read.
            Generations.PutListIfUnchanged(mark, partition, cache, cacheKey, (IEnumerable<T>)results, CacheEntryOptions(Math.Max(1, results.Count)));

        // Hand back isolated copies so a caller mutating a result never corrupts the cached instances.
        return results.Select(Clone).ToList();
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
    public async Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default)
    {
        var results = new List<T>(take ?? 4);
        await foreach (var entity in QueryStreamAsync(predicate, partition, take, ct))
            results.Add(entity);
        return results;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null, CancellationToken ct = default)
    {
        var partition = string.IsNullOrWhiteSpace(options?.Partition) ? null : NormalizeId(options!.Partition);
        // Normalize the RowKey prefix too: writes normalize the whole id, so stored RowKeys are
        // normalized — a natural-form prefix would otherwise silently match nothing, exactly like an
        // un-normalized partition. (Idempotent for the append-log path, which pre-normalizes.)
        var rowKeyPrefix = string.IsNullOrWhiteSpace(options?.RowKeyPrefix) ? null : NormalizeId(options!.RowKeyPrefix);
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

        // A bounded query is the "never cached" read path — it does not warm the per-entity cache.
        // A PROJECTED row must never warm it either: a partial entity in the cache would be served
        // to a later caller that asked for every column.
        // A projection that omits every large value is not assembled (its cells were not fetched).
        var select = BuildSelect(options);
        return StreamAsync(BuildFilter(partition, rowKeyPrefix), take, warmCache: false, ct, select, assemble: LargeValueAware && select is null);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<T> QueryStreamAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be positive.");
        if (!string.IsNullOrWhiteSpace(partition))
            partition = NormalizeId(partition); // match stored (normalized) PartitionKeys

        var predicateFilter = TableFilterTranslator.Translate(predicate);
        var filter = string.IsNullOrWhiteSpace(partition)
            ? predicateFilter
            : $"{TableClient.CreateQueryFilter($"PartitionKey eq {partition}")} and ({predicateFilter})";

        return StreamAsync(filter, take, warmCache: true, ct, assemble: LargeValueAware);
    }


    /// <summary>
    /// Expands a caller's <see cref="QueryOptions.Select"/> with the columns deserialization cannot
    /// work without, and returns <c>null</c> (meaning every column) when no projection was asked for.
    ///
    /// The trap this closes: a projection of <c>["Content"]</c> is a perfectly valid Tables query
    /// that comes back with no PartitionKey, no RowKey, no ETag and no type discriminator, and
    /// deserializes into a silently wrong entity. Projection is a transfer optimisation, never a
    /// change to what an entity IS, so the system columns are not the caller's to omit.
    /// </summary>
    internal static IEnumerable<string>? BuildSelect(QueryOptions? options)
    {
        if (options?.Select is not { Count: > 0 } requested) return null;
        // A large value spans cells and continuation rows: naming it fetches (and assembles) the whole row.
        if (LargeValueAware && SelectsLargeValue(requested)) return null;

        var columns = new List<string>(requested.Count + SystemColumns.Length + SplitSystemColumns.Length);
        columns.AddRange(SystemColumns);
        if (LargeValueAware) columns.AddRange(SplitSystemColumns);
        foreach (var column in requested)
            if (!columns.Contains(column, StringComparer.Ordinal)) columns.Add(column);

        return columns;
    }

    /// <summary>Columns every projection carries whether the caller asked for them or not.</summary>
    private static readonly string[] SystemColumns = ["PartitionKey", "RowKey", "Timestamp", "odata.etag", "_TypeName"];

    // Shared server-side streaming iterator. Validation lives in the public methods so it throws
    // eagerly rather than being deferred to the first MoveNext of the returned sequence.
    private async IAsyncEnumerable<T> StreamAsync(string? filter, int? take, bool warmCache,
        [EnumeratorCancellation] CancellationToken ct, IEnumerable<string>? select = null, bool assemble = false)
    {
        // Taken before the first storage call, once for the whole stream: an entity it yields is cached
        // only if no write or delete of that entity has completed since the stream began (see
        // WriteGenerations), however long the caller takes to consume it.
        var mark = warmCache ? BeginScan() : default;
        await EnsureTableAsync(ct);

        var maxPerPage = take is int t ? Math.Min(t, 1000) : (int?)null;
        var pageable = client.QueryAsync<TableEntity>(filter, maxPerPage, select, ct);

        var projection = ProjectionContext(select);
        var yielded = 0;
        await foreach (var queried in pageable.WithCancellation(ct))
        {
            // Continuation rows are not entities: they never count toward take, nor reach the cache.
            if (SplitRowLayout.IsPartRow(queried))
                continue;
            if (assemble)
            {
                // The whole row: its split values reassembled and its offloaded values resolved (a row
                // deleted while that was read is skipped).
                if (await ReadLargeAsync(queried, ct) is not { } read)
                    continue;
                var entity = Stamp(read.Entity, read.Row);
                if (warmCache) CacheRead(entity.Id, entity, read.Row.ETag, mark);
                yield return entity;
            }
            else
            {
                // A projection that omits every large value: nothing outside the row is read.
                yield return warmCache ? Materialize(queried, mark) : Deserialize(queried, projection);
            }

            if (take is int max && ++yielded >= max)
                yield break;
        }
    }

    /// <inheritdoc />
    public async Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var partition = string.IsNullOrWhiteSpace(options.Partition) ? null : NormalizeId(options.Partition);
        // Normalize the prefix to the stored form (see QueryStreamAsync); done BEFORE the fingerprint so
        // paging with the same natural-form prefix across pages yields a matching cursor.
        var rowKeyPrefix = string.IsNullOrWhiteSpace(options.RowKeyPrefix) ? null : NormalizeId(options.RowKeyPrefix);

        if (rowKeyPrefix is not null && partition is null)
            throw new ArgumentException(
                "RowKeyPrefix requires Partition — a cross-partition RowKey range is a full table scan wearing a filter.",
                nameof(options));
        if (options.Take is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.Take, "Take (page size) must be positive.");

        var pageSize = options.Take is int t ? Math.Min(t, 1000) : 100;
        var fingerprint = PageCursor.Fingerprint(partition, rowKeyPrefix, pageSize);
        var innerToken = options.ContinuationToken is null ? null : PageCursor.Decode(options.ContinuationToken, fingerprint);

        var select = BuildSelect(options);
        // Taken before the first storage call (a projected page caches nothing): an entity of the page
        // is cached only if no write or delete of it has completed in between (see WriteGenerations).
        var mark = select is null ? BeginScan() : default;
        await EnsureTableAsync(ct);

        var filter = BuildFilter(partition, rowKeyPrefix);
        var assemble = LargeValueAware && select is null;
        var pageable = client.QueryAsync<TableEntity>(filter, pageSize, select, ct);

        // Take the first server page from the cursor position (one page per call).
        await using var pages = pageable.AsPages(innerToken, pageSize).GetAsyncEnumerator(ct);
        if (!await pages.MoveNextAsync())
            return new EntityPage<T>([], null);

        var page = pages.Current;
        var items = new List<T>(page.Values.Count);
        var projection = ProjectionContext(select);
        foreach (var queried in page.Values)
        {
            if (SplitRowLayout.IsPartRow(queried))
                continue;
            // A projected row is a partial entity: it must never warm the per-entity cache.
            if (select is not null)
                items.Add(Deserialize(queried, projection));
            else if (assemble)
            {
                if (await ReadLargeAsync(queried, ct) is not { } read)
                    continue;
                var entity = Stamp(read.Entity, read.Row);
                CacheRead(entity.Id, entity, read.Row.ETag, mark);
                items.Add(entity);
            }
            else
                items.Add(Materialize(queried, mark));
        }
        var next = page.ContinuationToken is null ? null : PageCursor.Encode(fingerprint, page.ContinuationToken);
        return new EntityPage<T>(items, next);
    }

    /// <inheritdoc />
    public async Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, string? partition = null, CancellationToken ct = default)
    {
        await using var enumerator = QueryStreamAsync(predicate, partition, 1, ct).GetAsyncEnumerator(ct);
        return await enumerator.MoveNextAsync();
    }

    // Deserialize a queried row and stamp its ETag/Timestamp (no caching). A projected row
    // (ProjectionContext): an offloaded value the projection omits keeps its default instead of throwing.
    private static T Deserialize(TableEntity row, ValueReadContext? projection = null) =>
        Stamp(TableEntitySerializer.FromTableEntity<T>(row, projection ?? ValueReadContext.None), row);

    // The read context of a query, built once per query rather than per row: none for a whole-entity
    // read, the projected columns otherwise.
    private static ValueReadContext ProjectionContext(IEnumerable<string>? select) =>
        select is null ? ValueReadContext.None : new ValueReadContext(NothingResolved, null, [.. select]);

    private static readonly Dictionary<string, object> NothingResolved = new(StringComparer.Ordinal);

    // Stamp the row's version and service write time on the entity it holds.
    private static T Stamp(T entity, TableEntity row)
    {
        entity.ETag = row.ETag.ToString();
        entity.Timestamp = row.Timestamp;
        return entity;
    }

    // Deserialize and warm the per-entity cache with the fresh ETag (a read's put: see CacheRead).
    private T Materialize(TableEntity row, ReadMark mark)
    {
        var entity = Deserialize(row);
        CacheRead(entity.Id, entity, row.ETag, mark);
        return entity;
    }

    // A cache-isolated deep copy of an entity (serializer round-trip, like the in-memory fake), so a
    // caller mutating a returned entity never corrupts the shared cached instance. ETag/Timestamp are
    // not serialized as columns, so they are restored explicitly.
    private static T Clone(T entity)
    {
        var (partitionKey, rowKey) = GetEntityKeys(entity.Id);
        // The clone context keeps a cached split value a plain in-process bag, never re-encoded for storage.
        var copy = TableEntitySerializer.ToTableEntity(entity, partitionKey, rowKey, ValueWriteContext.ForClone, pending: null)
            .FromTableEntity<T>();
        copy.ETag = entity.ETag;
        copy.Timestamp = entity.Timestamp;
        return copy;
    }

    /// <inheritdoc />
    /// <remarks>Batch writes bypass [ProtectedProperty] enforcement (per-row reads would defeat
    /// the point of a batch) — treat them as trusted server-side paths. For a type with a
    /// <see cref="TableValueSplitAttribute"/> or <see cref="TableBlobContent"/> member each entity's rows
    /// (primary, continuation rows, stale-part deletes) are one indivisible unit packed into the
    /// partition's transactions, so the batch stays atomic per transaction and an entity's rows never
    /// straddle two.</remarks>
    public Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
        => WriteBatchAsync(entities, TableTransactionActionType.Add, ct);

    /// <inheritdoc />
    /// <remarks>Batch writes bypass [ProtectedProperty] enforcement (per-row reads would defeat
    /// the point of a batch) — treat them as trusted server-side paths. For a type with a
    /// <see cref="TableValueSplitAttribute"/> or <see cref="TableBlobContent"/> member each entity's rows
    /// (primary, continuation rows, stale-part deletes) are one indivisible unit packed into the
    /// partition's transactions, so the batch stays atomic per transaction and an entity's rows never
    /// straddle two.</remarks>
    public Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default)
        => WriteBatchAsync(entities, TableTransactionActionType.UpsertReplace, ct);

    /// <inheritdoc />
    public async Task<int> CountAsync(string? partition = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(partition))
            partition = NormalizeId(partition); // match stored (normalized) PartitionKeys

        await EnsureTableAsync(ct);

        var filter = string.IsNullOrWhiteSpace(partition)
            ? null
            : TableClient.CreateQueryFilter($"PartitionKey eq {partition}");

        // Keys-only projection keeps each page tiny; Azure Tables has no server-side count.
        // A split-aware table also selects _SplitOf so continuation rows are not counted as entities.
        var count = 0;
        await foreach (var row in client.QueryAsync<TableEntity>(
                               filter, select: LargeValueAware ? ["PartitionKey", SystemColumnNames.SplitOf] : ["PartitionKey"], cancellationToken: ct)
                           .WithCancellation(ct))
        {
            if (!SplitRowLayout.IsPartRow(row))
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
            // A split-aware entity is serialized by its own per-entity write below.
            rows.Add((partitionKey, entity.Id, LargeValueAware ? null! : entity.ToTableEntity(partitionKey, rowKey)));
        }

        try
        {
            if (LargeValueAware)
                await WriteBatchLargeAsync(entities, actionType, ct);
            else
                await SubmitBatchRowsAsync(rows, actionType, ct);
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

    private async Task SubmitBatchRowsAsync(
        List<(string Partition, string Id, TableEntity Row)> rows, TableTransactionActionType actionType, CancellationToken ct)
    {
        foreach (var group in rows.GroupBy(r => r.Partition, StringComparer.Ordinal))
        {
            // Azure Table transactions cap on BOTH entity count and payload size, and a
            // count-only chunker sends a legal-looking 100-entity batch that the service
            // rejects with 413 once the rows carry binary columns — after earlier chunks
            // have already committed. BatchPlanner caps on both axes and refuses an
            // unsendable entity up front. See BatchPlanner for the measured budget.
            var groupRows = group.ToArray();
            var plan = BatchPlanner.Plan([.. groupRows.Select(r => TableRowSize.Estimate(r.Row))]);
            foreach (var range in plan)
            {
                var chunk = new (string Partition, string Id, TableEntity Row)[range.Count];
                Array.Copy(groupRows, range.Start, chunk, 0, range.Count);

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
        // A continuation row is not an entity: merging cells into one would surface them on the primary.
        RefuseReservedRowKey(id);
        await EnsureTableAsync(ct);
        var (partitionKey, rowKey) = GetEntityKeys(id);

        var partial = new TableEntity(partitionKey, rowKey);
        foreach (var (name, value) in builder.Updates)
        {
            // The typed overload refuses a [TableValueSplit] member: its cells must be replaced together.
            foreach (var (col, cell) in TableEntitySerializer.FlattenProperty(typeof(T), name, value))
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
        RefuseReservedRowKey(entity.Id);

        await EnforceProtectedPropertiesAsync(entity, ct);

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
            // Auto without an ETag has NO version to check — since 0.6.0 that is a caller bug, not
            // a case to silently degrade to last-writer-wins (the pre-0.6.0 fallback survives
            // behind options.ImplicitLastWriterWins as a migration cushion). Deterministic by
            // design: the cached ETag is deliberately NOT consulted for writes — a write conditional
            // on cache state was itself a lost-update vector, and the in-memory fake (which has no
            // ETag cache) stays behaviourally identical.
            ConcurrencyMode.Auto when !callerSuppliedETag && !implicitLastWriterWins =>
                throw new InvalidOperationException(ConcurrencyMessages.AutoRequiresETag(typeName)),
            _ => callerSuppliedETag ? new ETag(entity.ETag!) : ETag.All
        };

        // The opted-in fallback still says so out loud: intentional LWW should be spelled
        // ConcurrencyMode.LastWriterWins.
        if (mode == ConcurrencyMode.Auto && !callerSuppliedETag)
            logger.LogWarning(
                "{Type}.UpdateAsync({Id}): Auto mode with no ETag is writing unconditionally " +
                "(last-writer-wins) because ImplicitLastWriterWins is enabled. Round-trip the ETag, " +
                "use MutateAsync, or spell it ConcurrencyMode.LastWriterWins.", typeName, entity.Id);

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.Timestamp = null; // service last-write time is unknown until the next read

        await EnsureTableAsync(ct);
        var (partitionKey, rowKey) = GetEntityKeys(entity.Id);
        if (LargeValueAware)
        {
            try
            {
                CommitLargeWrite(entity, partitionKey, await WriteLargeAsync(entity, LargeWriteKind.Replace, etag, ct));
            }
            catch (ConcurrencyConflictException)
            {
                cache.Remove(EntityCacheKey(entity.Id));
                InvalidateQueryCache(partitionKey);
                throw;
            }
            return entity;
        }

        var dataEntity = entity.ToTableEntity(partitionKey, rowKey);

        try
        {
            var updateResponse = await client.UpdateEntityAsync(dataEntity, etag, TableUpdateMode.Replace, ct);
            var newETag = updateResponse.Headers.ETag ?? etag;
            entity.ETag = newETag.ToString();

            CacheEntity(entity.Id, entity, newETag);
            InvalidateQueryCache(partitionKey);
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
        RefuseReservedRowKey(entity.Id);

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
        if (LargeValueAware)
        {
            try
            {
                CommitLargeWrite(entity, partitionKey, await WriteLargeAsync(entity, LargeWriteKind.Upsert, ETag.All, ct));
            }
            catch (ConcurrencyConflictException)
            {
                cache.Remove(EntityCacheKey(entity.Id));
                InvalidateQueryCache(partitionKey);
                throw;
            }
            return entity;
        }

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

    // Mode-aware id normalization — every id and partition/prefix argument funnels through here,
    // so IdNormalization.AsWritten uniformly disables the rewrite for case-sensitive legacy keys.
    private string NormalizeId(string id) =>
        idNormalization == IdNormalization.Normalized ? EntityId.Normalize(id) : id;

    // Split on the FIRST separator only, so a row key may itself contain '|'
    // (e.g. "vision|execution|agent" → partition "vision", row "execution|agent").
    private static (string PartitionKey, string RowKey) GetEntityKeys(string id) => EntityId.Split(id);

    // The cache key of an entity, for a read: a lookup, or the read's own put (CacheRead).
    private string EntityKey(string id) => $"{cacheKeyPrefix}:entity:{id}";

    // The cache key of an entity, for a MUTATION: every caller has just committed (or lost) a write or
    // delete of the entity and is about to evict or patch its entry. The write is noted here, before the
    // entry is touched: that order is what keeps a read that raced the write from leaving the older
    // version behind (see WriteGenerations). Reads use EntityKey.
    private string EntityCacheKey(string id)
    {
        Generations.NoteWrite(id);
        return EntityKey(id);
    }

    private string QueryCacheKey(string? partition) => $"{cacheKeyPrefix}:query:{partition ?? "*"}";

    // A WRITE's put: the entity a create, update or upsert has just committed, under the version the
    // service returned for it. The write is noted before the entry is overwritten (see WriteGenerations).
    private void CacheEntity(string id, T entity, ETag etag)
    {
        Generations.NoteWrite(id);
        if (cachePolicy.Enabled && Snapshot(id, entity) is { } snapshot)
            cache.Set(EntityKey(id), new CachedEntity(snapshot, etag), CacheEntryOptions());
    }

    // A READ's put: the entity a point read, a query, a page or a stream found. It is cached only while
    // no write or delete of it has been noted since the read took its mark, which it did before its
    // first storage call; otherwise what it found may already be the older version.
    private void CacheRead(string id, T entity, ETag etag, ReadMark mark)
    {
        // Asked before the snapshot too: an entity that will not be cached is not copied.
        if (!cachePolicy.Enabled || !Generations.Unchanged(mark, id))
            return;
        if (Snapshot(id, entity) is { } snapshot)
            Generations.PutEntityIfUnchanged(mark, id, cache, EntityKey(id), new CachedEntity(snapshot, etag), CacheEntryOptions());
    }

    // A private snapshot for the cache, never the caller-visible instance. Every read path (OneAsync,
    // QueryAsync, the Materialize-based predicate/stream/page paths) and every write path
    // (Create/Update/Upsert) hands its entity to the caller too; caching a clone here is the one
    // choke point that keeps a caller mutating a returned/written entity from corrupting the cache.
    // Null when the entity cannot be cloned.
    private T? Snapshot(string id, T entity)
    {
        try
        {
            return Clone(entity);
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException or NotSupportedException)
        {
            // Caching is best-effort and runs AFTER the write is committed. If this entity cannot
            // round-trip through the serializer (e.g. an interface-typed member that falls to a JSON
            // cell), skip the cache rather than failing an already-successful write — such an entity is
            // not cacheable anyway, and a later read goes to the backend (its own concern).
            logger.LogDebug(ex, "[Cache SKIP] {Type} id={Id} — entity is not round-trippable for caching", typeName, id);
            return null;
        }
    }

    // Size makes every entry countable so the store works with a SizeLimit-bounded IMemoryCache
    // (otherwise cache.Set throws AFTER the write is already committed to Azure). Single entities
    // count 1; query result lists count their row count.
    private MemoryCacheEntryOptions CacheEntryOptions(long size = 1) => cachePolicy.Mode == CacheExpirationMode.Absolute
        ? new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = cachePolicy.Ttl, Size = size }
        : new MemoryCacheEntryOptions { SlidingExpiration = cachePolicy.Ttl, Size = size };

    private void InvalidateQueryCache(string partitionKey)
    {
        // The write is noted before its lists are evicted (see WriteGenerations), so a query that read
        // the partition before the write committed cannot put its list back afterwards.
        Generations.NotePartitionWrite(partitionKey);
        // A write to one partition can only affect that partition's cached query and the whole-table
        // ("all") query — never another partition's filtered results — so evict exactly those two.
        // (This replaces the old "evict every tracked partition" sweep and its unbounded tracked set.)
        cache.Remove(QueryCacheKey(partitionKey));
        cache.Remove(QueryCacheKey(null));
    }

    private sealed record CachedEntity(T Entity, ETag ETag);

    // ── Read-cache coherence ────────────────────────────────────────────────
    //
    // A read puts what it read into the cache some time after it read it: the response is in flight, and
    // a split-aware read still queries the continuation rows and downloads the blobs. A write or delete
    // that commits in between must not be followed by the read caching the older version: later reads
    // would be served it from memory, and a deleted entity would stay readable. So:
    //
    //   - a mutation, once it has committed, NOTES itself (advances a write generation) before it evicts
    //     or overwrites a cache entry: the entity's generation in EntityCacheKey and CacheEntity, before
    //     the entity's entry goes or is replaced; the partition's in InvalidateQueryCache, before the
    //     cached lists go. Every mutating path goes through those helpers;
    //   - a read takes a ReadMark (the generations it depends on) before its first storage call and puts
    //     its result only while they are unchanged. Comparing and putting are one critical section with
    //     the advance.
    //
    // Why that is enough: take a read that fetched an older version than a write committed. It took its
    // mark before that commit, hence before the write's advance. Either the advance came before the
    // read's compare-and-put, and the read puts nothing; or it came after, and then the write evicts or
    // overwrites what the read put, because it touches the entry only after advancing. So once the write
    // has returned, nothing older than it is in the cache, or was ever visible there after that moment.
    // Writes by other processes are not seen, as before (see CachePolicy).

    private static readonly ConditionalWeakTable<IMemoryCache, WriteGenerations> GenerationsByCache = new();

    private WriteGenerations? generations;

    // One per cache and entity type: two stores of T over one IMemoryCache share its entries, so they
    // share the generations that guard them. (A store built without a cache has nothing to guard.)
    private WriteGenerations Generations => generations ??= cache is null
        ? new WriteGenerations()
        : GenerationsByCache.GetValue(cache, static _ => new WriteGenerations());

    /// <summary>The mark of a read of one entity; taken before the read's first storage call.</summary>
    private ReadMark BeginRead(string id) => cachePolicy.Enabled ? Generations.MarkEntity(id) : default;

    /// <summary>The mark of a read of many entities; taken before the read's first storage call.</summary>
    private ReadMark BeginScan() => cachePolicy.Enabled ? Generations.MarkAll() : default;

    /// <summary>
    /// What a read noted before its first storage call: the write generation of the one entity it reads
    /// (<see cref="Stripe"/>, <see cref="Generation"/>), or every generation (<see cref="All"/>) for a
    /// read of many entities. The default value marks nothing: a read that carries it caches nothing.
    /// </summary>
    private readonly struct ReadMark(int stripe, long generation, long[]? all)
    {
        public bool Taken { get; } = true;

        public int Stripe { get; } = stripe;

        public long Generation { get; } = generation;

        public long[]? All { get; } = all;
    }

    /// <summary>
    /// The write generations of one cache: counters that a mutation advances and a read compares. An
    /// entity and a partition each map onto one of a fixed number of counters, so memory stays bounded
    /// however many keys are written; two keys that share a counter only make a read that raced a write
    /// of the OTHER key skip its put.
    /// </summary>
    private sealed class WriteGenerations
    {
        private const int Stripes = 256;
        private const uint HashSeed = 2166136261;

        private readonly Lock gate = new();
        private readonly long[] counters = new long[Stripes];

        /// <summary>Notes a committed write or delete of the entity <paramref name="id"/>, before its entry is touched.</summary>
        public void NoteWrite(ReadOnlySpan<char> id)
        {
            var stripe = EntityStripe(id);
            lock (gate)
            {
                counters[stripe]++;
            }
        }

        /// <summary>Notes a committed write or delete somewhere in the partition, before its lists are evicted.</summary>
        public void NotePartitionWrite(ReadOnlySpan<char> partitionKey)
        {
            var stripe = PartitionStripe(partitionKey);
            lock (gate)
            {
                counters[stripe]++;
            }
        }

        /// <summary>The mark of a read of the entity <paramref name="id"/> alone.</summary>
        public ReadMark MarkEntity(ReadOnlySpan<char> id)
        {
            var stripe = EntityStripe(id);
            lock (gate)
            {
                return new ReadMark(stripe, counters[stripe], null);
            }
        }

        /// <summary>The mark of a read of many entities (a partition, a page, a predicate, the table).</summary>
        public ReadMark MarkAll()
        {
            lock (gate)
            {
                return new ReadMark(-1, 0, (long[])counters.Clone());
            }
        }

        /// <summary>True while no write or delete of the entity <paramref name="id"/> was noted since <paramref name="mark"/>.</summary>
        public bool Unchanged(ReadMark mark, ReadOnlySpan<char> id)
        {
            var stripe = EntityStripe(id);
            lock (gate)
            {
                return EntityUnchanged(mark, stripe);
            }
        }

        /// <summary>
        /// Caches what a read found for the entity <paramref name="id"/>, unless a write or delete of it
        /// was noted since <paramref name="mark"/>. One critical section with <see cref="NoteWrite"/>.
        /// </summary>
        public bool PutEntityIfUnchanged<TItem>(
            ReadMark mark, ReadOnlySpan<char> id, IMemoryCache cache, string key, TItem value, MemoryCacheEntryOptions options)
        {
            var stripe = EntityStripe(id);
            lock (gate)
            {
                if (!EntityUnchanged(mark, stripe))
                    return false;
                cache.Set(key, value, options);
                return true;
            }
        }

        /// <summary>
        /// Caches the list a query read for <paramref name="partitionKey"/> (null: the whole table),
        /// unless a write or delete in that partition (anywhere, for the whole table) was noted since
        /// <paramref name="mark"/>. One critical section with the two Note methods.
        /// </summary>
        public bool PutListIfUnchanged<TItem>(
            ReadMark mark, string? partitionKey, IMemoryCache cache, string key, TItem value, MemoryCacheEntryOptions options)
        {
            // Only a read of many entities (MarkAll) caches a list.
            if (mark.All is not { } all)
                return false;
            var stripe = partitionKey is null ? -1 : PartitionStripe(partitionKey);
            lock (gate)
            {
                // Every mutation advances at least one counter, so "no counter moved" is "no write at all".
                var unchanged = stripe < 0 ? counters.AsSpan().SequenceEqual(all) : counters[stripe] == all[stripe];
                if (!unchanged)
                    return false;
                cache.Set(key, value, options);
                return true;
            }
        }

        // Called under the gate.
        private bool EntityUnchanged(ReadMark mark, int stripe) => mark.All is { } all
            ? counters[stripe] == all[stripe]
            : mark.Taken && mark.Stripe == stripe && counters[stripe] == mark.Generation;

        // The counter of an entity, by its keys however the id spells them ("a" and "a|a" are one row).
        private static int EntityStripe(ReadOnlySpan<char> id) => Stripe(id.Contains(Separator)
            ? Hash(HashSeed, id)
            : Hash(Hash(Hash(HashSeed, id), [Separator]), id));

        // The counter of a partition's cached list.
        private static int PartitionStripe(ReadOnlySpan<char> partitionKey) => Stripe(Hash(HashSeed, partitionKey));

        // FNV-1a over the UTF-16 code units: the same on every run, and nothing is allocated.
        private static uint Hash(uint hash, ReadOnlySpan<char> text)
        {
            foreach (var c in text)
                hash = (hash ^ c) * 16777619;
            return hash;
        }

        private static int Stripe(uint hash) => (int)((hash ^ (hash >> 16)) % Stripes);
    }
}
