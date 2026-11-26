using System.Collections.Concurrent;
using System.Runtime.Serialization;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Providers;

/// <summary>
/// The read cache of a <see cref="RowStoreStorage{T}"/>, with the policy <c>TableStorage&lt;T&gt;</c> has
/// (<see cref="CachePolicy"/>: a sliding hour by default): a point read and a whole-partition or
/// whole-table list are answered from memory until a write through the same store evicts them or their
/// time is up. A row another store, another process or the raw table changed is served stale until then,
/// which is the staleness an Azure deployment shows.
/// </summary>
/// <remarks>
/// Coherence with the store's own writes rests on one counter. A write raises it before it touches the
/// row store (<see cref="BeginWrite"/>) and again when it evicts; a read takes it before it reads
/// (<see cref="Mark"/>) and stores what it found only while the counter is still that value, checked and
/// stored under the lock an eviction takes. So a read that overlapped a write never leaves the older
/// version behind. Every entry and every hit is a deep copy (a serializer round trip), so nothing a
/// caller holds is shared with the cache.
/// </remarks>
internal sealed class RowStoreReadCache<T> where T : class, IEntity, new()
{
    private const string WholeTable = "*";

    private readonly CachePolicy policy;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, Entry<T>> entities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry<List<T>>> lists = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private long generation;

    public RowStoreReadCache(CachePolicy policy, TimeProvider clock)
    {
        this.policy = policy;
        this.clock = clock;
    }

    private sealed class Entry<TValue>(TValue value, DateTimeOffset expires)
    {
        public TValue Value { get; } = value;

        public DateTimeOffset Expires { get; set; } = expires;
    }

    /// <summary>Whether the policy caches anything; every member is a no-op otherwise.</summary>
    public bool Enabled => policy.Enabled;

    /// <summary>Taken by a read before it reads the row store: what it finds is stored only while no write has begun since.</summary>
    public long Mark() => policy.Enabled ? Interlocked.Read(ref generation) : 0;

    /// <summary>Noted by a write before it touches the row store, so a read that began earlier stores nothing.</summary>
    public void BeginWrite()
    {
        if (policy.Enabled)
            Interlocked.Increment(ref generation);
    }

    /// <summary>A copy of the cached entity at the canonical id; false when there is none or its time is up.</summary>
    public bool TryGetEntity(string canonicalId, out T? entity)
    {
        entity = null;
        if (!Live(entities, canonicalId, out var entry))
            return false;
        entity = Clone(entry.Value);
        return true;
    }

    /// <summary>Whether an entity is cached at the canonical id.</summary>
    public bool HoldsEntity(string canonicalId) => Live(entities, canonicalId, out _);

    /// <summary>Copies of the cached list of a partition (null: the whole table); false when there is none.</summary>
    public bool TryGetList(string? partition, out IEnumerable<T> list)
    {
        list = [];
        if (!Live(lists, partition ?? WholeTable, out var entry))
            return false;
        list = entry.Value.Select(Clone).ToList();
        return true;
    }

    private bool Live<TValue>(ConcurrentDictionary<string, Entry<TValue>> entries, string key, out Entry<TValue> entry)
    {
        if (!policy.Enabled || !entries.TryGetValue(key, out entry!))
        {
            entry = null!;
            return false;
        }
        var now = clock.GetUtcNow();
        if (entry.Expires <= now)
        {
            entries.TryRemove(new KeyValuePair<string, Entry<TValue>>(key, entry));
            return false;
        }
        if (policy.Mode == CacheExpirationMode.Sliding)
            entry.Expires = now + policy.Ttl;
        return true;
    }

    /// <summary>Stores a copy of an entity a read found, unless a write has begun since <paramref name="mark"/> or it cannot be copied.</summary>
    public void PutEntity(T entity, long mark)
    {
        if (!policy.Enabled || Interlocked.Read(ref generation) != mark || Snapshot(entity) is not { } snapshot)
            return;
        var entry = new Entry<T>(snapshot, clock.GetUtcNow() + policy.Ttl);
        lock (gate)
        {
            if (generation == mark)
                entities[EntityId.Canonicalize(entity.Id)] = entry;
        }
    }

    /// <summary>Stores copies of a list a query read (and of each of its entities), unless a write has begun since <paramref name="mark"/>.</summary>
    public void PutList(string? partition, List<T> results, long mark)
    {
        if (!policy.Enabled || Interlocked.Read(ref generation) != mark)
            return;
        var copies = new List<T>(results.Count);
        foreach (var entity in results)
        {
            // A list one of whose entities cannot be copied is not cached at all.
            if (Snapshot(entity) is not { } snapshot)
                return;
            copies.Add(snapshot);
        }
        var expires = clock.GetUtcNow() + policy.Ttl;
        lock (gate)
        {
            if (generation != mark)
                return;
            lists[partition ?? WholeTable] = new Entry<List<T>>(copies, expires);
            foreach (var copy in copies)
                entities[EntityId.Canonicalize(copy.Id)] = new Entry<T>(copy, expires);
        }
    }

    /// <summary>After a write of one entity: its entry and the lists it may be part of go.</summary>
    public void EvictEntity(string canonicalId)
    {
        if (!policy.Enabled)
            return;
        lock (gate)
        {
            generation++;
            entities.TryRemove(canonicalId, out _);
            lists.TryRemove(EntityId.Split(canonicalId).PartitionKey, out _);
            lists.TryRemove(WholeTable, out _);
        }
    }

    /// <summary>After a write of many rows (a batch, a partition delete): everything goes.</summary>
    public void EvictAll()
    {
        if (!policy.Enabled)
            return;
        lock (gate)
        {
            generation++;
            entities.Clear();
            lists.Clear();
        }
    }

    // Null when the entity cannot round-trip through the serializer (an interface-typed member that falls
    // to a JSON cell, say): caching is best effort, and such an entity is read from the row store each time.
    private static T? Snapshot(T entity)
    {
        try
        {
            return Clone(entity);
        }
        catch (Exception ex) when (ex is SerializationException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    // A deep copy of an entity (a serializer round trip), as TableStorage<T> copies into and out of its
    // cache: each byte[] cell copied, each DateTimeOffset cell in UTC as a read of the table returns it.
    private static T Clone(T entity)
    {
        var (partitionKey, rowKey) = EntityId.Split(entity.Id);
        // The clone context keeps a cached split value a plain in-process bag, never re-encoded for storage.
        var row = TableEntitySerializer.ToTableEntity(entity, partitionKey, rowKey, ValueWriteContext.ForClone, pending: null);
        foreach (var column in row.Keys.ToList())
        {
            if (row[column] is byte[] or DateTimeOffset)
                row[column] = RowCells.Detached(row[column]);
        }
        var copy = row.FromTableEntity<T>();
        copy.ETag = entity.ETag;
        copy.Timestamp = entity.Timestamp;
        return copy;
    }
}

/// <summary>What a store over a row store states about itself (<see cref="IStorageInfo"/>).</summary>
internal static class RowStoreInfo
{
    /// <summary>The largest cell the engine accepts: 64 KiB, as the service.</summary>
    internal const long MaxCellBytes = ServiceErrors.MaxPropertyBytes;

    /// <summary>
    /// The info of a store over one table of <paramref name="account"/>: the provider and the durability
    /// the account states, and the limits the engine plans and refuses by, which are the service's.
    /// </summary>
    internal static StorageInfo Describe(IRowStoreAccount account, string tableName, bool readCacheEnabled) => new()
    {
        ProviderName = account.ProviderName,
        TableName = tableName,
        Durability = account.Durability,
        MaxBatchRows = BatchPlanner.DefaultMaxCount,
        MaxBatchBytes = BatchPlanner.DefaultMaxBytes,
        MaxCellBytes = MaxCellBytes,
        MaxRowBytes = TableRequestSize.MaxEntityBytes,
        ReadCacheMayServeStaleRows = readCacheEnabled,
        SinglePartitionTransactions = true,
    };
}
