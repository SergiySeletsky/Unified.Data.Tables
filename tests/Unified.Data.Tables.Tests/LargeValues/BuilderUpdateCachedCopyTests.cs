using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>A plain entity of the builder-update cache tests (opts into nothing).</summary>
public class BuilderCopyDoc : Entity, IRaceDoc
{
    public string? Body { get; set; }

    public int Version { get; set; }

    public string? Name { get; set; }

    public int Count { get; set; }

    public string? Tag { get; set; }
}

/// <summary>A large-value aware entity of the builder-update cache tests.</summary>
public class BuilderCopySplitDoc : Entity, IRaceDoc
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    public int Version { get; set; }

    public string? Name { get; set; }

    public int Count { get; set; }

    public string? Tag { get; set; }
}

/// <summary>A shape behind an interface (no default instance): a value of it does not round-trip.</summary>
public interface IBuilderCopyShape
{
    int Sides { get; set; }
}

/// <summary>An implementation of <see cref="IBuilderCopyShape"/>.</summary>
public class BuilderCopySquare : IBuilderCopyShape
{
    public int Sides { get; set; } = 4;
}

/// <summary>A plain entity whose version with a shape cannot be copied for the cache.</summary>
public class BuilderCopyShapeDoc : Entity
{
    public string? Name { get; set; }

    public int Version { get; set; }

    public string? Tag { get; set; }

    public IBuilderCopyShape? Shape { get; set; }
}

/// <summary>A list property set through the builder (the cached copy and the caller's value).</summary>
public class BuilderCopyListDoc : Entity
{
    public List<string>? Tags { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// A real <see cref="MemoryCache"/> that lets a test act on the calling thread right before a stored entry
/// takes effect.
/// </summary>
internal sealed class BeforeSetMemoryCache : IMemoryCache
{
    private readonly MemoryCache inner = new(new MemoryCacheOptions());

    public Action<string>? BeforeSet { get; set; }

    public ICacheEntry CreateEntry(object key) => new Entry(inner.CreateEntry(key), this);

    public void Remove(object key) => inner.Remove(key);

    public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);

    public void Dispose() => inner.Dispose();

    private sealed class Entry(ICacheEntry entry, BeforeSetMemoryCache owner) : ICacheEntry
    {
        public object Key => entry.Key;

        public object? Value
        {
            get => entry.Value;
            set => entry.Value = value;
        }

        public DateTimeOffset? AbsoluteExpiration
        {
            get => entry.AbsoluteExpiration;
            set => entry.AbsoluteExpiration = value;
        }

        public TimeSpan? AbsoluteExpirationRelativeToNow
        {
            get => entry.AbsoluteExpirationRelativeToNow;
            set => entry.AbsoluteExpirationRelativeToNow = value;
        }

        public TimeSpan? SlidingExpiration
        {
            get => entry.SlidingExpiration;
            set => entry.SlidingExpiration = value;
        }

        public IList<IChangeToken> ExpirationTokens => entry.ExpirationTokens;

        public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => entry.PostEvictionCallbacks;

        public CacheItemPriority Priority
        {
            get => entry.Priority;
            set => entry.Priority = value;
        }

        public long? Size
        {
            get => entry.Size;
            set => entry.Size = value;
        }

        public void Dispose()
        {
            owner.BeforeSet?.Invoke((string)entry.Key);
            entry.Dispose();
        }
    }
}

/// <summary>
/// <c>UpdateAsync(id, builder)</c> over a cached entity (CACHE-2, CACHE-L1 and CACHE-6 of the fix-wave-3
/// review; ruling R10). The store keeps the cached entity warm with the update applied to a private copy,
/// taken after the patch, so no object the caller passed in is reachable from the cache. The copy carries
/// the update's new ETag only when the update was conditional on exactly the cached entity's version (only
/// then is its content known to be the row's); otherwise it keeps the ETag the cached entity had, as
/// 0.8.3 did, and an optimistic write with it meets 412, evicts and reads the row. Every type, every
/// topology: two stores with two caches, a store with caching disabled over a shared cache, one store.
/// </summary>
public class BuilderUpdateCachedCopyTests
{
    private const string Id = "p|a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableStorage<T> NewStore<T>(RacingTableClient table, IMemoryCache cache, UnifiedTableStorageOptions? options = null)
        where T : class, IEntity, new()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(T).Name).Returns(table);
        return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, options,
            (Unified.Data.Tables.Providers.IOffloadBlobStore?)null);
    }

    private static T Doc<T>(int version, string name) where T : Entity, IRaceDoc, new()
    {
        var doc = new T { Id = Id, Body = "v" + version, Version = version };
        SetName(doc, name);
        return doc;
    }

    private static void SetName(object e, string value) => e.GetType().GetProperty("Name")!.SetValue(e, value);

    private static void SetTag(object e, string value) => e.GetType().GetProperty("Tag")!.SetValue(e, value);

    private static string? Name(object? e) => (string?)e?.GetType().GetProperty("Name")!.GetValue(e);

    // CACHE-2. A caches the entity; another writer B changes Name (optimistically); A merges another
    // column without a condition, then makes an optimistic read-modify-write of a third column. B's
    // committed change must survive (on 0.8.3 the patched copy kept its old ETag, the Strict write met
    // 412, the conflict evicted, and the retry read the table).
    [Theory]
    [InlineData("two caches", false)]
    [InlineData("disabled store over the same cache", false)]
    [InlineData("two caches", true)]
    [InlineData("disabled store over the same cache", true)]
    public async Task ABuilderUpdate_OverACachedEntityAnotherWriterChanged_NeverLetsAnOptimisticWriteUndoThatChange(string topology, bool largeValues)
    {
        if (largeValues)
            await RunAnotherWriterAsync<BuilderCopySplitDoc>(topology);
        else
            await RunAnotherWriterAsync<BuilderCopyDoc>(topology);
    }

    private static async Task RunAnotherWriterAsync<T>(string topology) where T : Entity, IRaceDoc, new()
    {
        var table = new RacingTableClient();
        using var cacheA = new MemoryCache(new MemoryCacheOptions());
        using var cacheB = new MemoryCache(new MemoryCacheOptions());
        var a = NewStore<T>(table, cacheA);
        var b = topology == "two caches"
            ? NewStore<T>(table, cacheB)
            : NewStore<T>(table, cacheA, new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled });

        await a.UpsertAsync(Doc<T>(1, "original"), Ct);
        Assert.Equal("original", Name(await a.OneAsync(Id, Ct))); // cached in A

        await b.MutateAsync(Id, e => SetName(e, "changed by B"), ct: Ct);
        Assert.Equal("changed by B", (string)table.Stored("p", "a")!["Name"]);

        await a.UpdateAsync(Id, u => u.SetProperty(x => x.Version, 2), Ct);
        await a.MutateAsync(Id, e => SetTag(e, "set by A"), ct: Ct);

        var stored = table.Stored("p", "a")!;
        Assert.Equal("set by A", (string)stored["Tag"]);
        Assert.Equal(2, (int)stored["Version"]);
        Assert.Equal("changed by B", (string)stored["Name"]); // B's committed change survives
    }

    // CACHE-2, a condition that is not the cached version: the caller holds the row's current ETag (B's
    // write returned it) while A's cache still holds the version before B's. The merge succeeds, but it
    // was applied to B's content, not to the cached content: the copy keeps the cached (older) ETag.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABuilderUpdate_ConditionalOnAnotherVersionThanTheCachedOne_NeverLetsAnOptimisticWriteUndoTheOtherWritersChange(bool largeValues)
    {
        if (largeValues)
            await RunConditionalOnAnotherVersionAsync<BuilderCopySplitDoc>();
        else
            await RunConditionalOnAnotherVersionAsync<BuilderCopyDoc>();
    }

    private static async Task RunConditionalOnAnotherVersionAsync<T>() where T : Entity, IRaceDoc, new()
    {
        var table = new RacingTableClient();
        using var cacheA = new MemoryCache(new MemoryCacheOptions());
        using var cacheB = new MemoryCache(new MemoryCacheOptions());
        var a = NewStore<T>(table, cacheA);
        var b = NewStore<T>(table, cacheB);

        await a.UpsertAsync(Doc<T>(1, "original"), Ct);
        var cachedETag = (await a.OneAsync(Id, Ct))!.ETag;
        var byB = await b.MutateAsync(Id, e => SetName(e, "changed by B"), ct: Ct);
        Assert.NotEqual(cachedETag, byB.ETag);

        var returned = await a.UpdateAsync(Id, u => u.SetProperty(x => x.Version, 2).WithETag(byB.ETag!), Ct);
        Assert.Equal(table.Stored("p", "a")!.ETag.ToString(), returned);
        var served = (await a.OneAsync(Id, Ct))!;
        Assert.Equal(cachedETag, served.ETag); // the cached entity's own version, never the merge's

        await a.MutateAsync(Id, e => SetTag(e, "set by A"), ct: Ct);
        var stored = table.Stored("p", "a")!;
        Assert.Equal("set by A", (string)stored["Tag"]);
        Assert.Equal("changed by B", (string)stored["Name"]);
    }

    // CACHE-2, scenario 2: inside one store and one cache. Builder update A (Name) has committed and is
    // putting its copy; builder update B (Count) starts right then, finds the entry A has not replaced yet,
    // and puts its own copy; a MutateAsync in that moment must not write the version before A back.
    [Fact]
    public async Task TwoBuilderUpdates_AndAnOptimisticWrite_NeverUndoTheFirstBuilderUpdate()
    {
        var table = new RacingTableClient();
        using var cache = new BeforeSetMemoryCache();
        var store = NewStore<BuilderCopyDoc>(table, cache);
        await store.UpsertAsync(new BuilderCopyDoc { Id = Id, Version = 1, Name = "original" }, Ct);
        Assert.Equal("original", (await store.OneAsync(Id, Ct))!.Name);

        var stage = 0;
        cache.BeforeSet = key =>
        {
            if (stage != 1 || !key.Contains(":entity:", StringComparison.Ordinal))
                return;
            stage = 2;
            store.UpdateAsync(Id, u => u.SetProperty(x => x.Count, 5), Ct).GetAwaiter().GetResult();
            store.MutateAsync(Id, e => e.Tag = "mutated", ct: Ct).GetAwaiter().GetResult();
            stage = 3;
        };
        stage = 1;
        await store.UpdateAsync(Id, u => u.SetProperty(x => x.Name, "set by A"), Ct);
        cache.BeforeSet = null;
        Assert.Equal(3, stage);

        var stored = table.Stored("p", "a")!;
        Assert.Equal(5, (int)stored["Count"]);
        Assert.Equal("mutated", (string)stored["Tag"]);
        Assert.Equal("set by A", (string)stored["Name"]); // A's committed merge survives
    }

    // The in-memory store (no cache) for the same sequence as the two-store case: B's change survives.
    [Fact]
    public async Task InMemory_ABuilderUpdate_ThenMutateAsync_KeepsTheOtherWritersChange()
    {
        var store = new InMemoryStorage<BuilderCopyDoc>();
        await store.UpsertAsync(new BuilderCopyDoc { Id = Id, Version = 1, Name = "original" }, Ct);
        _ = await store.OneAsync(Id, Ct);
        await store.MutateAsync(Id, e => e.Name = "changed by B", ct: Ct);
        await store.UpdateAsync(Id, u => u.SetProperty(x => x.Version, 2), Ct);
        await store.MutateAsync(Id, e => e.Tag = "set by A", ct: Ct);

        var stored = await store.OneAsync(Id, Ct);
        Assert.Equal("changed by B", stored!.Name);
        Assert.Equal("set by A", stored.Tag);
    }

    // What R10 keeps: a builder update conditional on exactly the cached version keeps the cache warm,
    // with the update applied and the new ETag; the next read is served from the cache and an optimistic
    // write with it succeeds on its first attempt.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABuilderUpdateConditionalOnTheCachedVersion_KeepsTheCacheWarmWithTheNewETag(bool largeValues)
    {
        if (largeValues)
            await RunConditionalOnTheCachedVersionAsync<BuilderCopySplitDoc>();
        else
            await RunConditionalOnTheCachedVersionAsync<BuilderCopyDoc>();
    }

    private static async Task RunConditionalOnTheCachedVersionAsync<T>() where T : Entity, IRaceDoc, new()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<T>(table, cache);
        await store.UpsertAsync(Doc<T>(1, "original"), Ct);
        var read = (await store.OneAsync(Id, Ct))!;

        var returned = await store.UpdateAsync(Id, u => u.SetProperty(x => x.Version, 2).WithETag(read.ETag!), Ct);

        var reads = table.Count(r => r.Operation == "get");
        var served = (await store.OneAsync(Id, Ct))!;
        Assert.Equal(reads, table.Count(r => r.Operation == "get")); // served from the cache
        Assert.Equal(2, served.Version);
        Assert.Equal("original", Name(served));
        Assert.Equal(returned, served.ETag);
        Assert.Equal(table.Stored("p", "a")!.ETag.ToString(), served.ETag);

        int Writes() => table.Count(r => r.Operation is "add" or "upsert" or "update" or "transaction");
        var writes = Writes();
        await store.MutateAsync(Id, e => SetTag(e, "t"), ct: Ct);
        Assert.Equal(writes + 1, Writes()); // the first optimistic write succeeded: no 412, no retry

        var stored = table.Stored("p", "a")!;
        Assert.Equal("original", (string)stored["Name"]);
        Assert.Equal(2, (int)stored["Version"]);
        Assert.Equal("t", (string)stored["Tag"]);
    }

    // CACHE-L1: one store, one cache, no other writer, no race. Version 1 is cached; version 2 (with a
    // shape) cannot be copied, so its write leaves version 1's entry (documented). A builder update then
    // patches that entry; the following MutateAsync must not write version 1's Name back.
    [Fact]
    public async Task AWriteThatCannotBeCached_ThenABuilderUpdate_NeverLetsMutateAsyncUndoThatWrite()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<BuilderCopyShapeDoc>(table, cache);
        await store.UpsertAsync(new BuilderCopyShapeDoc { Id = Id, Name = "v1", Version = 1 }, Ct);
        Assert.Equal("v1", (await store.OneAsync(Id, Ct))!.Name);

        await store.UpsertAsync(new BuilderCopyShapeDoc { Id = Id, Name = "v2", Version = 2, Shape = new BuilderCopySquare() }, Ct);
        Assert.Equal("v2", (string)table.Stored("p", "a")!["Name"]);

        await store.UpdateAsync(Id, u => u.SetProperty(x => x.Version, 3), Ct);
        _ = await Record.ExceptionAsync(() => store.MutateAsync(Id, e => e.Tag = "t", ct: Ct));

        // Whatever MutateAsync does (succeed or throw), it has not replaced v2's Name with v1's.
        Assert.Equal("v2", (string)table.Stored("p", "a")!["Name"]);
        Assert.Equal(3, (int)table.Stored("p", "a")!["Version"]);
    }

    // CACHE-6: the caller changes the list it passed to the builder after the update returned (nothing is
    // written); the store must keep answering what it stored. Unconditional and conditional updates alike.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABuilderUpdate_DoesNotLetTheCallersLaterChangesReachTheCachedEntity(bool conditional)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<BuilderCopyListDoc>(table, cache);
        await store.UpsertAsync(new BuilderCopyListDoc { Id = Id, Tags = ["a"], Version = 1 }, Ct);
        var read = (await store.OneAsync(Id, Ct))!;

        var mine = new List<string> { "b" };
        await store.UpdateAsync(Id, u =>
        {
            u.SetProperty(x => x.Tags, mine);
            if (conditional)
                u.WithETag(read.ETag!);
        }, Ct);
        mine.Add("never written");

        var served = (await store.OneAsync(Id, Ct))!;
        Assert.Equal(["b"], served.Tags!);
        served.Tags!.Add("changed by a reader");
        Assert.Equal(["b"], (await store.OneAsync(Id, Ct))!.Tags!);
    }
}
