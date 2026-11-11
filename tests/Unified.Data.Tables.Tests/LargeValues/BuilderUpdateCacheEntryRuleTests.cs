using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>An enum of the builder-update cache-rule tests.</summary>
public enum CacheRuleColor
{
    /// <summary>The initial value.</summary>
    Red,

    /// <summary>The value an update sets.</summary>
    Blue,
}

/// <summary>A nested object of the builder-update cache-rule tests.</summary>
public class CacheRuleAddress
{
    public string? City { get; set; }

    public string? Street { get; set; }
}

/// <summary>What the builder-update cache-rule tests need from an entity.</summary>
public interface ICacheRuleDoc
{
    string? Body { get; set; }

    string? Name { get; set; }

    int Version { get; set; }

    string? Tag { get; set; }

    CacheRuleColor Color { get; set; }

    int Count { get; set; }

    bool Flag { get; set; }

    DateTimeOffset When { get; set; }

    Guid Key { get; set; }

    CacheRuleAddress? Address { get; set; }

    List<string>? Tags { get; set; }

    byte[]? Data { get; set; }

    string? Note { get; set; }
}

/// <summary>A plain entity (opts into nothing) of the builder-update cache-rule tests.</summary>
public class CacheRuleDoc : Entity, ICacheRuleDoc
{
    public string? Body { get; set; }

    public string? Name { get; set; }

    public int Version { get; set; }

    public string? Tag { get; set; }

    public CacheRuleColor Color { get; set; }

    public int Count { get; set; }

    public bool Flag { get; set; }

    public DateTimeOffset When { get; set; }

    public Guid Key { get; set; }

    public CacheRuleAddress? Address { get; set; }

    public List<string>? Tags { get; set; }

    public byte[]? Data { get; set; }

    public string? Note { get; set; }
}

/// <summary>A large-value aware entity of the builder-update cache-rule tests.</summary>
public class CacheRuleSplitDoc : Entity, ICacheRuleDoc
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    public string? Name { get; set; }

    public int Version { get; set; }

    public string? Tag { get; set; }

    public CacheRuleColor Color { get; set; }

    public int Count { get; set; }

    public bool Flag { get; set; }

    public DateTimeOffset When { get; set; }

    public Guid Key { get; set; }

    public CacheRuleAddress? Address { get; set; }

    public List<string>? Tags { get; set; }

    public byte[]? Data { get; set; }

    public string? Note { get; set; }
}

/// <summary>A plain entity whose constructor counts its runs and can be made to throw.</summary>
public sealed class CacheRuleCountingDoc : Entity
{
    private static int constructed;
    private static volatile bool failing;

    public CacheRuleCountingDoc()
    {
        Interlocked.Increment(ref constructed);
        if (failing)
            throw new InvalidOperationException("the host is not ready yet");
    }

    public static int Constructed => Volatile.Read(ref constructed);

    public static bool Failing { get => failing; set => failing = value; }

    public string? Title { get; set; }

    public CacheRuleAddress? Address { get; set; }
}

/// <summary>
/// <c>UpdateAsync(id, builder)</c> over a cached entity, ruling R10 as revised (fix wave 5): the entity
/// stays warm only when the entry the merge was sent over is still the entry in the cache, every path is
/// top-level and every value is a non-null scalar the merge wrote whole in its own cell. Otherwise the
/// entry is evicted and the next read returns what the row holds. CACHE-R4-1 (a read put an entry during
/// the merge's flight), CACHE-L1 (the same through a query), CACHE-R4-2 (a whole object whose null member
/// the merge did not write), SERIALIZER-L1 (an exception while the copy is made) and D1 (constructions).
/// </summary>
[Collection("OversizedCellPolicy")] // the trimmed-string case sets TableEntitySerializer.OversizedCellPolicy
public class BuilderUpdateCacheEntryRuleTests
{
    private const string Id = "p|a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableStorage<T> NewStore<T>(RacingTableClient table, IMemoryCache cache)
        where T : class, IEntity, new()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(T).Name).Returns(table);
        return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, null,
            (Unified.Data.Tables.Providers.IOffloadBlobStore?)null);
    }

    // CACHE-R4-1. Store A (cache A, no entry yet) merges Name="set by A". While its response is in flight,
    // store B (cache B) changes Name to "changed by B" (a later write, reported), and a read through A
    // caches that row. A's builder must not patch that entry and keep its (current) ETag: B's committed
    // change has to survive A's later optimistic write of another column.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ABuilderUpdate_WhoseEntryARaceReadPutDuringItsFlight_NeverLetsAnOptimisticWriteUndoALaterChange(bool largeValues, bool conditional)
    {
        if (largeValues)
            await RunRaceAsync<CacheRuleSplitDoc>(conditional);
        else
            await RunRaceAsync<CacheRuleDoc>(conditional);
    }

    private static async Task RunRaceAsync<T>(bool conditional) where T : Entity, ICacheRuleDoc, new()
    {
        var table = new RacingTableClient();
        using var cacheA = new MemoryCache(new MemoryCacheOptions());
        using var cacheB = new MemoryCache(new MemoryCacheOptions());
        var a = NewStore<T>(table, cacheA);
        var b = NewStore<T>(table, cacheB);

        var created = await b.UpsertAsync(new T { Id = Id, Body = "body", Name = "original", Version = 1 }, Ct);
        var e0 = created.ETag!;

        var armed = true;
        string? readETag = null;
        table.Committed = request =>
        {
            if (!armed || request.Operation != "update")
                return;
            armed = false;
            // A's merge has committed; its response has not reached A yet.
            b.MutateAsync(Id, e => e.Name = "changed by B", ct: Ct).GetAwaiter().GetResult();
            readETag = a.OneAsync(Id, Ct).GetAwaiter().GetResult()!.ETag; // a read through A caches B's version
        };

        await a.UpdateAsync(Id, u =>
        {
            u.SetProperty(x => x.Name, "set by A");
            if (conditional)
                u.WithETag(e0);
        }, Ct);
        table.Committed = null;
        Assert.False(armed);
        Assert.Equal("changed by B", (string)table.Stored("p", "a")!["Name"]); // B's change is the later one

        var served = (await a.OneAsync(Id, Ct))!;
        var rowETag = table.Stored("p", "a")!.ETag.ToString();
        // An entry that carries the row's current ETag holds the row's content.
        var invariantHolds = served.ETag != rowETag || served.Name == "changed by B";

        await a.MutateAsync(Id, e => e.Tag = "set by A", ct: Ct);
        var stored = table.Stored("p", "a")!;
        Assert.Equal("set by A", (string)stored["Tag"]);
        Assert.True(invariantHolds && (string)stored["Name"] == "changed by B",
            $"served Name='{served.Name}' ETag={served.ETag}, row ETag={rowETag}, race read ETag={readETag}; after MutateAsync the row's Name is '{stored["Name"]}' (B's committed change must survive)");
    }

    // CACHE-L1: CACHE-R4-1 with the race read being a partition query, a predicate query, ExistsAsync or a
    // page. A stream (QueryStreamAsync) does not warm the entity cache: the control.
    [Theory]
    [InlineData(false, "query")]
    [InlineData(true, "query")]
    [InlineData(false, "predicate")]
    [InlineData(true, "predicate")]
    [InlineData(false, "stream")]
    [InlineData(false, "exists")]
    [InlineData(true, "exists")]
    [InlineData(false, "page")]
    [InlineData(true, "page")]
    public async Task ABuilderUpdate_WhoseEntryAQueryPutDuringItsFlight_NeverLetsAnOptimisticWriteUndoALaterChange(bool largeValues, string read)
    {
        if (largeValues)
            await RunQueryRaceAsync<CacheRuleSplitDoc>(read);
        else
            await RunQueryRaceAsync<CacheRuleDoc>(read);
    }

    private static async Task RunQueryRaceAsync<T>(string read) where T : Entity, ICacheRuleDoc, new()
    {
        var table = new RacingTableClient();
        using var cacheA = new MemoryCache(new MemoryCacheOptions());
        using var cacheB = new MemoryCache(new MemoryCacheOptions());
        var a = NewStore<T>(table, cacheA);
        var b = NewStore<T>(table, cacheB);
        await b.UpsertAsync(new T { Id = Id, Body = "body", Name = "original", Version = 1 }, Ct);

        var armed = true;
        table.Committed = request =>
        {
            if (!armed || request.Operation != "update")
                return;
            armed = false;
            b.MutateAsync(Id, e => e.Name = "changed by B", ct: Ct).GetAwaiter().GetResult();
            switch (read)
            {
                case "query":
                    _ = a.QueryAsync("p", Ct).GetAwaiter().GetResult().ToList();
                    break;
                case "predicate":
                    _ = a.QueryAsync(x => x.Version == 1, "p", ct: Ct).GetAwaiter().GetResult();
                    break;
                case "exists":
                    Assert.True(a.ExistsAsync(Id, Ct).GetAwaiter().GetResult());
                    break;
                case "page":
                    _ = a.QueryPageAsync(new QueryOptions { Partition = "p" }, Ct).GetAwaiter().GetResult();
                    break;
                default:
                    var e = a.QueryStreamAsync(new QueryOptions { Partition = "p" }, Ct).GetAsyncEnumerator(Ct);
                    try
                    {
                        while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult()) { }
                    }
                    finally
                    {
                        e.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    break;
            }
        };

        await a.UpdateAsync(Id, u => u.SetProperty(x => x.Name, "set by A"), Ct);
        table.Committed = null;
        Assert.False(armed);
        Assert.Equal("changed by B", (string)table.Stored("p", "a")!["Name"]);

        var served = (await a.OneAsync(Id, Ct))!;
        var rowETag = table.Stored("p", "a")!.ETag.ToString();
        await a.MutateAsync(Id, e => e.Tag = "t", ct: Ct);
        var stored = table.Stored("p", "a")!;
        Assert.True((string)stored["Name"] == "changed by B",
            $"{read}: served Name='{served.Name}' ETag={served.ETag}, row ETag={rowETag}; after MutateAsync the row's Name is '{stored["Name"]}'");
    }

    // CACHE-R4-2 (as the verifier narrowed it): a whole-object update conditional on the cached version.
    // The merge sends only the non-null members' cells, so the row keeps Address_Street. An entry carrying
    // the row's ETag must hold the row's content, and a MutateAsync of another column keeps the column.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConditionalWholeObjectBuilderUpdate_NeverLetsTheCacheAnswerOrAnOptimisticWriteStoreWhatTheRowDoesNotHold(bool largeValues)
    {
        if (largeValues)
            await RunWholeObjectAsync<CacheRuleSplitDoc>();
        else
            await RunWholeObjectAsync<CacheRuleDoc>();
    }

    private static async Task RunWholeObjectAsync<T>() where T : Entity, ICacheRuleDoc, new()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<T>(table, cache);
        await store.UpsertAsync(new T { Id = Id, Address = new CacheRuleAddress { City = "c0", Street = "s0" } }, Ct);
        var read = (await store.OneAsync(Id, Ct))!;

        await store.UpdateAsync(Id, u => u.SetProperty(x => x.Address, new CacheRuleAddress { City = "c1" }).WithETag(read.ETag!), Ct);
        Assert.Equal("s0", (string)table.Stored("p", "a")!["Address_Street"]); // the merge kept the column

        var served = (await store.OneAsync(Id, Ct))!;
        var rowETagAfterMerge = table.Stored("p", "a")!.ETag.ToString();
        var lines = new List<string> { $"served: City={served.Address?.City} Street={served.Address?.Street ?? "null"} ETag={served.ETag} (row ETag {rowETagAfterMerge})" };

        var memory = new InMemoryStorage<T>();
        await memory.UpsertAsync(new T { Id = Id, Address = new CacheRuleAddress { City = "c0", Street = "s0" } }, Ct);
        var mread = (await memory.OneAsync(Id, Ct))!;
        await memory.UpdateAsync(Id, u => u.SetProperty(x => x.Address, new CacheRuleAddress { City = "c1" }).WithETag(mread.ETag!), Ct);
        lines.Add($"InMemoryStorage serves: Street={(await memory.OneAsync(Id, Ct))!.Address?.Street ?? "null"}");

        await store.MutateAsync(Id, e => e.Tag = "t", ct: Ct);
        var stored = table.Stored("p", "a")!;
        lines.Add($"after MutateAsync(Tag): row Address_Street={(stored.TryGetValue("Address_Street", out var s) ? s : "<gone>")}");

        Assert.True((served.ETag != rowETagAfterMerge || served.Address?.Street == "s0") && stored.TryGetValue("Address_Street", out var kept) && (string)kept == "s0",
            string.Join(" | ", lines));
    }

    /// <summary>The builder updates that do not keep the entity warm (R10 as revised, points 2 and 3).</summary>
    public static TheoryData<string, bool, bool> NotKeptWarm()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var kind in new[] { "null", "nested object", "object with a null member", "list", "byte[]", "trimmed string", "nested path" })
        {
            foreach (var largeValues in new[] { false, true })
            {
                data.Add(kind, largeValues, false);
                data.Add(kind, largeValues, true);
            }
        }
        return data;
    }

    // A value that is not a non-null scalar written whole in its own cell, or a nested path: the entry is
    // evicted, and the next read fetches the row and returns what it holds (as a fresh store reads it, and
    // as InMemoryStorage answers).
    [Theory]
    [MemberData(nameof(NotKeptWarm))]
    public async Task ABuilderUpdate_ThatIsNotAWholeScalarPerColumn_EvictsTheEntity_AndTheNextReadReturnsTheRow(string kind, bool largeValues, bool conditional)
    {
        if (largeValues)
            await RunNotKeptWarmAsync<CacheRuleSplitDoc>(kind, conditional);
        else
            await RunNotKeptWarmAsync<CacheRuleDoc>(kind, conditional);
    }

    private static async Task RunNotKeptWarmAsync<T>(string kind, bool conditional) where T : Entity, ICacheRuleDoc, new()
    {
        var previous = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimWithMarker;
        try
        {
            var table = new RacingTableClient();
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = NewStore<T>(table, cache);
            await store.UpsertAsync(Initial<T>(), Ct);
            var read = (await store.OneAsync(Id, Ct))!;

            await store.UpdateAsync(Id, u =>
            {
                Apply(u, kind);
                if (conditional)
                    u.WithETag(read.ETag!);
            }, Ct);

            var gets = table.Count(r => r.Operation == "get");
            var served = (await store.OneAsync(Id, Ct))!;
            Assert.Equal(gets + 1, table.Count(r => r.Operation == "get")); // evicted: this read fetched the row

            using var freshCache = new MemoryCache(new MemoryCacheOptions());
            var fresh = (await NewStore<T>(table, freshCache).OneAsync(Id, Ct))!;
            Assert.Equal(Describe(fresh), Describe(served));
            Assert.Equal(fresh.ETag, served.ETag);

            var memory = new InMemoryStorage<T>();
            await memory.UpsertAsync(Initial<T>(), Ct);
            var mread = (await memory.OneAsync(Id, Ct))!;
            await memory.UpdateAsync(Id, u =>
            {
                Apply(u, kind);
                if (conditional)
                    u.WithETag(mread.ETag!);
            }, Ct);
            Assert.Equal(Describe(fresh), Describe((await memory.OneAsync(Id, Ct))!));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = previous;
        }
    }

    /// <summary>The scalar builder updates that keep the entity warm.</summary>
    public static TheoryData<string, bool, bool> KeptWarm()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var kind in new[] { "string", "enum", "int", "bool", "date", "guid", "several" })
        {
            foreach (var largeValues in new[] { false, true })
            {
                data.Add(kind, largeValues, false);
                data.Add(kind, largeValues, true);
            }
        }
        return data;
    }

    // Non-null scalars, each written whole in its own cell: the entity stays warm (no read), with the
    // values applied, and equals a fresh read of the row. Conditional on the cached version it carries the
    // row's ETag; unconditional it keeps the cached entity's own (older) ETag, as on 0.8.3.
    [Theory]
    [MemberData(nameof(KeptWarm))]
    public async Task ABuilderUpdate_OfWholeScalars_KeepsTheEntityWarm_AndItEqualsAFreshReadOfTheRow(string kind, bool largeValues, bool conditional)
    {
        if (largeValues)
            await RunKeptWarmAsync<CacheRuleSplitDoc>(kind, conditional);
        else
            await RunKeptWarmAsync<CacheRuleDoc>(kind, conditional);
    }

    private static async Task RunKeptWarmAsync<T>(string kind, bool conditional) where T : Entity, ICacheRuleDoc, new()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<T>(table, cache);
        await store.UpsertAsync(Initial<T>(), Ct);
        var read = (await store.OneAsync(Id, Ct))!;

        var returned = await store.UpdateAsync(Id, u =>
        {
            Apply(u, kind);
            if (conditional)
                u.WithETag(read.ETag!);
        }, Ct);

        var gets = table.Count(r => r.Operation == "get");
        var served = (await store.OneAsync(Id, Ct))!;
        Assert.Equal(gets, table.Count(r => r.Operation == "get")); // warm: served from the cache

        using var freshCache = new MemoryCache(new MemoryCacheOptions());
        var fresh = (await NewStore<T>(table, freshCache).OneAsync(Id, Ct))!;
        Assert.Equal(Describe(fresh), Describe(served));
        Assert.Equal(conditional ? returned : read.ETag, served.ETag);
        Assert.Equal(returned, fresh.ETag);
    }

    // D1 and R10 as revised: the entity is constructed once when it is kept warm (one copy, as on 0.8.3),
    // and never when the entry is evicted.
    [Theory]
    [InlineData("scalar", 1)]
    [InlineData("null", 0)]
    [InlineData("object", 0)]
    [InlineData("nested path", 0)]
    [InlineData("not cached", 0)]
    public async Task ABuilderUpdate_ConstructsTheEntityOnce_OnlyWhenItKeepsItWarm(string kind, int expected)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<CacheRuleCountingDoc>(table, cache);
        await store.CreateAsync(new CacheRuleCountingDoc { Id = Id, Title = "one", Address = new CacheRuleAddress { City = "c0" } }, Ct);
        _ = await store.OneAsync(Id, Ct);
        if (kind == "not cached")
            cache.Compact(1.0);

        var before = CacheRuleCountingDoc.Constructed;
        await store.UpdateAsync(Id, u =>
        {
            switch (kind)
            {
                case "null":
                    u.Updates[nameof(CacheRuleCountingDoc.Title)] = null!; // SetProperty refuses null; Updates does not
                    break;
                case "object":
                    u.SetProperty(x => x.Address, new CacheRuleAddress { City = "c1", Street = "s1" });
                    break;
                case "nested path":
                    u.SetProperty(x => x.Address!.City, "c1");
                    break;
                default:
                    u.SetProperty(x => x.Title, "two");
                    break;
            }
        }, Ct);
        Assert.Equal(expected, CacheRuleCountingDoc.Constructed - before);
    }

    // SERIALIZER-L1 (fake client; the Azurite twin is in the Azurite class): while the constructor fails,
    // a builder update that keeps the entity warm commits and lets the constructor's exception out, as on
    // 0.8.3 (TargetInvocationException); the pre-update entity is not left cached: the next read of the
    // same store returns the row's content.
    [Fact]
    public async Task ABuilderUpdate_WhileTheConstructorFails_EvictsTheEntry_AndLetsTheExceptionOut()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<CacheRuleCountingDoc>(table, cache);
        await store.CreateAsync(new CacheRuleCountingDoc { Id = Id, Title = "before" }, Ct);
        _ = await store.OneAsync(Id, Ct); // cached

        CacheRuleCountingDoc.Failing = true;
        Exception? thrown;
        try
        {
            thrown = await Record.ExceptionAsync(() => store.UpdateAsync(Id, b => b.SetProperty(x => x.Title, "after"), Ct));
        }
        finally
        {
            CacheRuleCountingDoc.Failing = false;
        }

        Assert.IsType<System.Reflection.TargetInvocationException>(thrown);
        Assert.Equal("after", (string)table.Stored("p", "a")!["Title"]);
        Assert.Equal("after", (await store.OneAsync(Id, Ct))!.Title);
    }

    // Anything that throws while the entry is put (here the cache itself) evicts the entry before the
    // exception leaves; the exception is the cache's, as on 0.8.3. The next read returns the row.
    [Fact]
    public async Task ABuilderUpdate_WhoseCachePutThrows_EvictsTheEntry_AndLetsTheExceptionOut()
    {
        var table = new RacingTableClient();
        using var cache = new BeforeSetMemoryCache();
        var store = NewStore<CacheRuleDoc>(table, cache);
        await store.UpsertAsync(new CacheRuleDoc { Id = Id, Name = "before", Version = 1 }, Ct);
        _ = await store.OneAsync(Id, Ct); // cached

        var refuse = true;
        cache.BeforeSet = key =>
        {
            if (refuse && key.Contains(":entity:", StringComparison.Ordinal))
                throw new InvalidOperationException("the cache refuses the entry");
        };
        var thrown = await Record.ExceptionAsync(() => store.UpdateAsync(Id, b => b.SetProperty(x => x.Name, "after"), Ct));
        refuse = false;

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal("after", (string)table.Stored("p", "a")!["Name"]);
        var gets = table.Count(r => r.Operation == "get");
        Assert.Equal("after", (await store.OneAsync(Id, Ct))!.Name);
        Assert.Equal(gets + 1, table.Count(r => r.Operation == "get")); // the entry was evicted
    }

    internal static T Initial<T>() where T : Entity, ICacheRuleDoc, new() => new()
    {
        Id = Id,
        Body = "body",
        Name = "n0",
        Version = 1,
        Color = CacheRuleColor.Red,
        Count = 1,
        Flag = false,
        When = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
        Key = new Guid("11111111-1111-1111-1111-111111111111"),
        Address = new CacheRuleAddress { City = "c0", Street = "s0" },
        Tags = ["t0"],
        Data = [1, 2, 3],
        Note = "note0",
    };

    // Random printable ASCII: too large for one cell even compressed, so a trimming policy trims it.
    private static readonly string Oversized = string.Create(150_000, new Random(7), static (span, r) =>
    {
        for (var i = 0; i < span.Length; i++) span[i] = (char)r.Next(0x21, 0x7F);
    });

    internal static void Apply<T>(UpdateBuilder<T> u, string kind) where T : class, ICacheRuleDoc
    {
        switch (kind)
        {
            // SetProperty refuses null; the public Updates dictionary does not.
            case "null": u.Updates[nameof(ICacheRuleDoc.Name)] = null!; break;
            case "nested object": u.SetProperty(x => x.Address, new CacheRuleAddress { City = "c1", Street = "s1" }); break;
            case "object with a null member": u.SetProperty(x => x.Address, new CacheRuleAddress { City = "c1" }); break;
            case "list": u.SetProperty(x => x.Tags, ["t1"]); break;
            case "byte[]": u.SetProperty(x => x.Data, [7, 7, 7]); break;
            case "trimmed string": u.SetProperty(x => x.Note, Oversized); break;
            case "nested path": u.SetProperty(x => x.Address!.City, "c1"); break;
            case "string": u.SetProperty(x => x.Name, "n1"); break;
            case "enum": u.SetProperty(x => x.Color, CacheRuleColor.Blue); break;
            case "int": u.SetProperty(x => x.Count, 5); break;
            case "bool": u.SetProperty(x => x.Flag, true); break;
            case "date": u.SetProperty(x => x.When, new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero)); break;
            case "guid": u.SetProperty(x => x.Key, new Guid("22222222-2222-2222-2222-222222222222")); break;
            case "several":
                u.SetProperty(x => x.Name, "n1").SetProperty(x => x.Count, 5).SetProperty(x => x.Color, CacheRuleColor.Blue)
                    .SetProperty(x => x.Flag, true).SetProperty(x => x.Key, new Guid("22222222-2222-2222-2222-222222222222"));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    // Everything a reader sees except the versions (ETag, Timestamp, UpdatedAt).
    internal static string Describe(ICacheRuleDoc e) =>
        $"Body={e.Body} Name={e.Name ?? "null"} Version={e.Version} Tag={e.Tag ?? "null"} Color={e.Color} Count={e.Count} Flag={e.Flag} "
        + $"When={e.When.UtcDateTime:O} Key={e.Key} Address={(e.Address is null ? "null" : $"{e.Address.City ?? "null"}/{e.Address.Street ?? "null"}")} "
        + $"Tags=[{(e.Tags is null ? "null" : string.Join(",", e.Tags))}] Data=[{(e.Data is null ? "null" : string.Join(",", e.Data))}] "
        + $"Note={(e.Note is null ? "null" : e.Note.Length + " chars")}";
}
