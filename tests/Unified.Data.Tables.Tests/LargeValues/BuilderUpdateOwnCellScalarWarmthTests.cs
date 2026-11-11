using System.Globalization;
using System.Reflection;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>An enum of the own-cell scalar warmth tests.</summary>
public enum OwnCellScalarColor
{
    /// <summary>The initial value.</summary>
    Red,

    /// <summary>The value an update sets.</summary>
    Blue,
}

/// <summary>
/// The scalar properties of the own-cell warmth tests: every type the serializer stores in one cell under the
/// property's own column (and their nullable forms), and the scalars it stores as JSON.
/// </summary>
public class OwnCellScalarDocBase : Entity
{
    public string? Text { get; set; } = "t0";

    public bool Flag { get; set; }

    public bool? MaybeFlag { get; set; } = false;

    public int Count { get; set; } = 1;

    public int? MaybeCount { get; set; } = 1;

    public uint Unsigned { get; set; } = 1;

    public uint? MaybeUnsigned { get; set; } = 1;

    public long Long { get; set; } = 1;

    public long? MaybeLong { get; set; } = 1;

    public ulong Big { get; set; } = 1;

    public ulong? MaybeBig { get; set; } = 1;

    public byte Small { get; set; } = 1;

    public byte? MaybeSmall { get; set; } = 1;

    public double Real { get; set; } = 1.5;

    public double? MaybeReal { get; set; } = 1.5;

    public decimal Money { get; set; } = 1.5m;

    public decimal? MaybeMoney { get; set; } = 1.5m;

    public Guid Key { get; set; } = new("11111111-1111-1111-1111-111111111111");

    public Guid? MaybeKey { get; set; } = new Guid("11111111-1111-1111-1111-111111111111");

    public TimeSpan Span { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan? MaybeSpan { get; set; } = TimeSpan.FromMinutes(1);

    public DateTimeOffset When { get; set; } = new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public DateTimeOffset? MaybeWhen { get; set; } = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public DateTime At { get; set; } = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    public DateTime? MaybeAt { get; set; } = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    public OwnCellScalarColor Color { get; set; }

    public OwnCellScalarColor? MaybeColor { get; set; } = OwnCellScalarColor.Red;

    // Scalars the serializer stores as JSON (a __Json column), not in a cell of their own.
    public float Single { get; set; } = 1.5f;

    public short Short { get; set; } = 1;

    public char Letter { get; set; } = 'a';

    public sbyte Tiny { get; set; } = 1;

    public ushort UShort { get; set; } = 1;
}

/// <summary>A plain entity (opts into nothing) of the own-cell warmth tests.</summary>
public class OwnCellScalarDoc : OwnCellScalarDocBase
{
}

/// <summary>A large-value aware entity of the own-cell warmth tests.</summary>
public class OwnCellScalarSplitDoc : OwnCellScalarDocBase
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; } = "body";
}

/// <summary>An IMemoryCache whose lookups (and, when asked, removals) throw once armed, with an exception other than ObjectDisposedException.</summary>
public sealed class LookupFailingCache(IMemoryCache inner) : IMemoryCache
{
    public bool Armed { get; set; }

    public bool RemoveFails { get; set; }

    public IMemoryCache Inner => inner;

    public ICacheEntry CreateEntry(object key) => inner.CreateEntry(key);

    public void Dispose() => inner.Dispose();

    public void Remove(object key)
    {
        if (Armed && RemoveFails)
            throw new InvalidOperationException("the cache removal failed");
        inner.Remove(key);
    }

    public bool TryGetValue(object key, out object? value)
    {
        if (Armed)
            throw new InvalidOperationException("the cache lookup failed");
        return inner.TryGetValue(key, out value);
    }
}

/// <summary>
/// The values of the own-cell warmth matrix. <c>Warm</c>: a read converts the merge's single cell under the
/// property's own column back to a value equal to the one set, so the builder update keeps the cached entity
/// warm, as 0.8.3 did (CACHE-1 of the review of fix wave 5). Otherwise the entity is evicted and the next read
/// fetches the row (ruling R10 as revised, point 3).
/// </summary>
internal static class OwnCellScalarCases
{
    private static readonly DateTimeOffset Sentinel = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // name -> (property, value, warm, onAzurite: whether the service stores the value)
    public static readonly Dictionary<string, (string Property, object Value, bool Warm, bool OnAzurite)> All = new()
    {
        ["string"] = ("Text", "t1", true, true),
        ["string empty"] = ("Text", "", true, true),
        ["bool"] = ("Flag", true, true, true),
        ["bool?"] = ("MaybeFlag", true, true, true),
        ["int"] = ("Count", 7, true, true),
        ["int min"] = ("Count", int.MinValue, true, true),
        ["int?"] = ("MaybeCount", 7, true, true),
        ["uint"] = ("Unsigned", 7u, true, true),
        ["uint max"] = ("Unsigned", uint.MaxValue, true, true),
        ["uint?"] = ("MaybeUnsigned", 7u, true, true),
        ["long"] = ("Long", 7L, true, true),
        ["long min"] = ("Long", long.MinValue, true, true),
        ["long?"] = ("MaybeLong", long.MaxValue, true, true),
        ["ulong"] = ("Big", 7ul, true, true),
        ["ulong above long"] = ("Big", (ulong)long.MaxValue + 1, true, true),
        ["ulong max"] = ("Big", ulong.MaxValue, true, true),
        ["ulong?"] = ("MaybeBig", ulong.MaxValue, true, true),
        ["byte"] = ("Small", (byte)7, true, true),
        ["byte zero"] = ("Small", (byte)0, true, true),
        ["byte max"] = ("Small", byte.MaxValue, true, true),
        ["byte?"] = ("MaybeSmall", (byte)7, true, true),
        ["double"] = ("Real", 0.1, true, true),
        ["double max"] = ("Real", double.MaxValue, true, true),
        ["double?"] = ("MaybeReal", -2.5e-300, true, true),
        ["decimal"] = ("Money", 7.5m, true, true),
        ["decimal tenth"] = ("Money", 0.1m, true, true),
        ["decimal 15 digits"] = ("Money", 123456789.012345m, true, true),
        ["decimal?"] = ("MaybeMoney", -42.25m, true, true),
        ["decimal precise"] = ("Money", 0.1234567890123456789m, false, true),
        ["decimal? precise"] = ("MaybeMoney", 1.0000000000000001m, false, true),
        ["guid"] = ("Key", new Guid("22222222-2222-2222-2222-222222222222"), true, true),
        ["guid?"] = ("MaybeKey", new Guid("33333333-3333-3333-3333-333333333333"), true, true),
        ["TimeSpan"] = ("Span", TimeSpan.FromMinutes(7), true, true),
        ["TimeSpan zero"] = ("Span", TimeSpan.Zero, true, true),
        ["TimeSpan negative"] = ("Span", TimeSpan.FromTicks(-1234567890123), true, true),
        ["TimeSpan max"] = ("Span", TimeSpan.MaxValue, true, true),
        ["TimeSpan min"] = ("Span", TimeSpan.MinValue, true, true),
        ["TimeSpan?"] = ("MaybeSpan", TimeSpan.FromTicks(1), true, true),
        ["DateTimeOffset"] = ("When", new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero), true, true),
        ["DateTimeOffset 1601"] = ("When", new DateTimeOffset(1601, 6, 1, 0, 0, 0, TimeSpan.Zero), true, true),
        ["DateTimeOffset after sentinel"] = ("When", Sentinel.AddTicks(1), true, true),
        ["DateTimeOffset default"] = ("When", default(DateTimeOffset), true, true),
        ["DateTimeOffset sentinel"] = ("When", Sentinel, false, true),
        ["DateTimeOffset?"] = ("MaybeWhen", new DateTimeOffset(1601, 1, 2, 0, 0, 0, TimeSpan.Zero), true, true),
        ["DateTimeOffset? sentinel"] = ("MaybeWhen", Sentinel, false, true),
        ["DateTime utc"] = ("At", new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Utc), true, true),
        ["DateTime unspecified"] = ("At", new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Unspecified), true, true),
        ["DateTime 1601"] = ("At", new DateTime(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc), true, true),
        ["DateTime default"] = ("At", default(DateTime), true, true),
        ["DateTime sentinel"] = ("At", new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc), false, true),
        ["DateTime local"] = ("At", new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Local), false, true),
        ["DateTime?"] = ("MaybeAt", new DateTime(1601, 3, 4, 0, 0, 0, DateTimeKind.Utc), true, true),
        ["DateTime? sentinel"] = ("MaybeAt", new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), false, true),
        ["enum"] = ("Color", OwnCellScalarColor.Blue, true, true),
        ["enum undefined"] = ("Color", (OwnCellScalarColor)42, true, true),
        ["enum?"] = ("MaybeColor", OwnCellScalarColor.Blue, true, true),
        ["float (JSON)"] = ("Single", 2.5f, false, true),
        ["short (JSON)"] = ("Short", (short)7, false, true),
        ["char (JSON)"] = ("Letter", 'z', false, true),
        ["sbyte (JSON)"] = ("Tiny", (sbyte)-7, false, true),
        ["ushort (JSON)"] = ("UShort", (ushort)7, false, true),
        ["another runtime type"] = ("Count", 7L, false, false),
    };

    public static TheoryData<string, bool, bool> Matrix(bool azurite)
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var (name, c) in All)
        {
            if (azurite && !c.OnAzurite)
                continue;
            foreach (var largeValues in new[] { false, true })
            {
                data.Add(name, largeValues, false);
                data.Add(name, largeValues, true);
            }
        }
        return data;
    }

    public static PropertyInfo Property(Type type, string name) => type.GetProperty(name)!;

    // Every property a reader sees, except the versions (ETag, Timestamp, UpdatedAt).
    public static string Describe(OwnCellScalarDocBase e, bool withCreatedAt = true)
    {
        var parts = new List<string>();
        foreach (var p in e.GetType().GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (p.Name is nameof(Entity.ETag) or nameof(Entity.Timestamp) or nameof(Entity.UpdatedAt)
                || (!withCreatedAt && p.Name == nameof(Entity.CreatedAt)))
                continue;
            parts.Add(p.Name + "=" + Show(p.GetValue(e)));
        }
        return string.Join(" ", parts);
    }

    public static string Show(object? value) => value switch
    {
        null => "null",
        DateTimeOffset d => d.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTime d => d.Ticks.ToString(CultureInfo.InvariantCulture) + "t",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!,
    };
}

/// <summary>
/// CACHE-1 and CACHE-3 of the review of fix wave 5 on the fake table client. A builder update whose every value
/// is a non-null top-level scalar that the merge writes as one cell under the property's own column, and that a
/// read converts back to an equal value, keeps the cached entity warm with the requests 0.8.3 sends (no point
/// read follows), and the entity it keeps equals a fresh read of the row; any other value evicts and the next
/// read fetches the row. A cache whose lookup throws never prevents the merge 0.8.3 sends.
/// </summary>
public class BuilderUpdateOwnCellScalarWarmthTests
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

    // The reviewer's reproduction of CACHE-1 (int is the control).
    [Theory]
    [InlineData("int")]
    [InlineData("uint")]
    [InlineData("uint?")]
    [InlineData("byte")]
    [InlineData("TimeSpan")]
    [InlineData("ulong")]
    public async Task APlainBuilderUpdate_OfAScalarStoredInItsOwnCell_KeepsTheEntityWarm_As083(string kind)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<OwnCellScalarDoc>(table, cache);
        await store.UpsertAsync(new OwnCellScalarDoc { Id = Id, Text = "n", Count = 1, Unsigned = 1, Small = 1, Span = TimeSpan.FromMinutes(1), Big = 1, MaybeUnsigned = 1 }, Ct);
        _ = await store.OneAsync(Id, Ct); // cached

        await store.UpdateAsync(Id, u =>
        {
            switch (kind)
            {
                case "int": u.SetProperty(x => x.Count, 7); break;
                case "uint": u.SetProperty(x => x.Unsigned, 7u); break;
                case "uint?": u.SetProperty(x => x.MaybeUnsigned, 7u); break;
                case "byte": u.SetProperty(x => x.Small, (byte)7); break;
                case "TimeSpan": u.SetProperty(x => x.Span, TimeSpan.FromMinutes(7)); break;
                default: u.SetProperty(x => x.Big, 7ul); break;
            }
        }, Ct);

        var before = table.Count(r => r.IsWholeRowPointRead);
        var served = (await store.OneAsync(Id, Ct))!;
        var pointReads = table.Count(r => r.IsWholeRowPointRead) - before;

        using var freshCache = new MemoryCache(new MemoryCacheOptions());
        var fresh = (await NewStore<OwnCellScalarDoc>(table, freshCache).OneAsync(Id, Ct))!;
        Assert.Equal(OwnCellScalarCases.Describe(fresh), OwnCellScalarCases.Describe(served));
        Assert.True(pointReads == 0,
            $"{kind}: the read after the builder update sent {pointReads} point read(s); 0.8.3 answers it from the cache (the value round-trips: a fresh read returns it)");
    }

    public static TheoryData<string, bool, bool> Matrix() => OwnCellScalarCases.Matrix(azurite: false);

    // Every scalar type the serializer supports, plain and large-value aware, conditional and not: kept warm
    // exactly when a read converts the cell back to an equal value, and then equal to a fresh read of the row
    // in that property (and in every other); otherwise evicted, and the next read fetches the row.
    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task ABuilderUpdate_KeepsTheEntityWarm_ExactlyWhenTheCellReadsBackEqual_AndItEqualsAFreshRead(string kind, bool largeValues, bool conditional)
    {
        if (largeValues)
            await RunAsync<OwnCellScalarSplitDoc>(kind, conditional);
        else
            await RunAsync<OwnCellScalarDoc>(kind, conditional);
    }

    private static async Task RunAsync<T>(string kind, bool conditional) where T : OwnCellScalarDocBase, new()
    {
        var (property, value, warm, _) = OwnCellScalarCases.All[kind];
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<T>(table, cache);
        await store.UpsertAsync(new T { Id = Id }, Ct);
        var read = (await store.OneAsync(Id, Ct))!;

        var returned = await store.UpdateAsync(Id, u =>
        {
            u.Updates[property] = value;
            if (conditional)
                u.WithETag(read.ETag!);
        }, Ct);

        var gets = table.Count(r => r.IsWholeRowPointRead);
        var served = (await store.OneAsync(Id, Ct))!;
        var pointReads = table.Count(r => r.IsWholeRowPointRead) - gets;

        using var freshCache = new MemoryCache(new MemoryCacheOptions());
        var fresh = (await NewStore<T>(table, freshCache).OneAsync(Id, Ct))!;
        var p = OwnCellScalarCases.Property(typeof(T), property);
        Assert.Equal(OwnCellScalarCases.Show(p.GetValue(fresh)), OwnCellScalarCases.Show(p.GetValue(served)));
        Assert.True(Equals(p.GetValue(fresh), p.GetValue(served)), $"{kind}: served {OwnCellScalarCases.Show(p.GetValue(served))}, fresh {OwnCellScalarCases.Show(p.GetValue(fresh))}");
        Assert.Equal(OwnCellScalarCases.Describe(fresh), OwnCellScalarCases.Describe(served));
        Assert.Equal(warm ? 0 : 1, pointReads);
        if (warm)
        {
            Assert.Equal(OwnCellScalarCases.Show(value), OwnCellScalarCases.Show(p.GetValue(served)));
            Assert.Equal(conditional ? returned : read.ETag, served.ETag);
        }
        else
        {
            Assert.Equal(fresh.ETag, served.ETag);
        }
        Assert.Equal(returned, fresh.ETag);

        // InMemoryStorage answers what the row holds after the same update (its own creation time aside).
        var memory = new InMemoryStorage<T>();
        await memory.UpsertAsync(new T { Id = Id }, Ct);
        var mread = (await memory.OneAsync(Id, Ct))!;
        await memory.UpdateAsync(Id, u =>
        {
            u.Updates[property] = value;
            if (conditional)
                u.WithETag(mread.ETag!);
        }, Ct);
        Assert.Equal(OwnCellScalarCases.Describe(fresh, withCreatedAt: false), OwnCellScalarCases.Describe((await memory.OneAsync(Id, Ct))!, withCreatedAt: false));
    }

    // Several values in one update: warm only when every one of them reads back equal.
    [Theory]
    [InlineData(true, "uint", "TimeSpan", "byte", "ulong max", "decimal", "DateTime 1601")]
    [InlineData(true, "string", "bool", "int", "long", "double", "guid", "enum", "DateTimeOffset")]
    [InlineData(false, "uint", "decimal precise")]
    [InlineData(false, "TimeSpan", "DateTimeOffset sentinel")]
    [InlineData(false, "byte", "float (JSON)")]
    public async Task ABuilderUpdate_OfSeveralScalars_IsKeptWarmOnlyWhenEveryValueReadsBackEqual(bool warm, params string[] kinds)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<OwnCellScalarDoc>(table, cache);
        await store.UpsertAsync(new OwnCellScalarDoc { Id = Id }, Ct);
        _ = await store.OneAsync(Id, Ct);

        await store.UpdateAsync(Id, u =>
        {
            foreach (var kind in kinds)
            {
                var (property, value, _, _) = OwnCellScalarCases.All[kind];
                u.Updates[property] = value;
            }
        }, Ct);

        var gets = table.Count(r => r.IsWholeRowPointRead);
        var served = (await store.OneAsync(Id, Ct))!;
        Assert.Equal(warm ? 0 : 1, table.Count(r => r.IsWholeRowPointRead) - gets);
        using var freshCache = new MemoryCache(new MemoryCacheOptions());
        var fresh = (await NewStore<OwnCellScalarDoc>(table, freshCache).OneAsync(Id, Ct))!;
        Assert.Equal(OwnCellScalarCases.Describe(fresh), OwnCellScalarCases.Describe(served));
    }

    // A decimal whose double cell does not convert back to a decimal at all (it rounds above the decimal
    // range): the row cannot be read; the update must not keep the entity warm (0.8.3 did, answering a value
    // the row cannot return), so the next read fetches the row and fails as a fresh read does.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABuilderUpdate_OfADecimalWhoseCellDoesNotReadBack_EvictsTheEntity_AndTheNextReadFailsAsAFreshRead(bool largeValues)
    {
        if (largeValues)
            await RunUnreadableDecimalAsync<OwnCellScalarSplitDoc>();
        else
            await RunUnreadableDecimalAsync<OwnCellScalarDoc>();
    }

    private static async Task RunUnreadableDecimalAsync<T>() where T : OwnCellScalarDocBase, new()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<T>(table, cache);
        await store.UpsertAsync(new T { Id = Id }, Ct);
        _ = await store.OneAsync(Id, Ct);

        await store.UpdateAsync(Id, u => u.SetProperty(x => x.Money, decimal.MaxValue), Ct);

        using var freshCache = new MemoryCache(new MemoryCacheOptions());
        var fresh = await Record.ExceptionAsync(() => NewStore<T>(table, freshCache).OneAsync(Id, Ct));
        var served = await Record.ExceptionAsync(() => store.OneAsync(Id, Ct));
        Assert.NotNull(fresh);
        Assert.Equal(fresh.GetType(), served?.GetType());
    }

    // CACHE-3 (the reviewer's reproduction, corrected to the requirement): a cache whose lookup throws (not
    // ObjectDisposedException) never prevents the merge 0.8.3 sends. The merge is sent, and afterwards the
    // entry is evicted (here the removal works, so the update returns); the entity from before the merge is
    // never left cached. The reproduction also asked for the cache's exception to leave the call, which is
    // 0.8.3's outcome only because its first cache call came after the merge; the requirement accepts the
    // eviction instead, and the next test covers a cache whose removal throws too.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APlainBuilderUpdate_OverACacheWhoseLookupThrows_SendsTheMergeAs083(bool largeValues)
    {
        if (largeValues)
            await RunLookupThrowsAsync<OwnCellScalarSplitDoc>();
        else
            await RunLookupThrowsAsync<OwnCellScalarDoc>();
    }

    private static async Task RunLookupThrowsAsync<T>() where T : OwnCellScalarDocBase, new()
    {
        var table = new RacingTableClient();
        using var inner = new MemoryCache(new MemoryCacheOptions());
        var cache = new LookupFailingCache(inner);
        var store = NewStore<T>(table, cache);
        await store.UpsertAsync(new T { Id = Id, Text = "n" }, Ct);
        _ = await store.OneAsync(Id, Ct); // cached
        cache.Armed = true;

        await store.UpdateAsync(Id, u => u.SetProperty(x => x.Text, "m"), Ct);

        Assert.Equal(1, table.Count(r => r.Operation == "update"));
        Assert.Equal("m", (string)table.Stored("p", "a")!["Text"]);
        cache.Armed = false;
        var gets = table.Count(r => r.IsWholeRowPointRead);
        Assert.Equal("m", (await store.OneAsync(Id, Ct))!.Text); // the entity from before the merge is not cached
        Assert.Equal(gets + 1, table.Count(r => r.IsWholeRowPointRead));
    }

    // CACHE-3: when the removal after the merge throws too, the merge is still sent and the cache's exception
    // leaves the call after it, as on 0.8.3 (whose first cache call came after the merge).
    [Fact]
    public async Task ABuilderUpdate_OverACacheWhoseLookupAndRemovalThrow_SendsTheMerge_AndTheCachesExceptionFollows()
    {
        var table = new RacingTableClient();
        using var inner = new MemoryCache(new MemoryCacheOptions());
        var cache = new LookupFailingCache(inner) { RemoveFails = true };
        var store = NewStore<OwnCellScalarDoc>(table, cache);
        await store.UpsertAsync(new OwnCellScalarDoc { Id = Id, Text = "n" }, Ct);
        cache.Armed = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(Id, u => u.SetProperty(x => x.Text, "m"), Ct));

        Assert.Equal(1, table.Count(r => r.Operation == "update"));
        Assert.Equal("m", (string)table.Stored("p", "a")!["Text"]);
    }

    // CACHE-3 with every kind of value: a value that would keep the entity warm, and one that evicts, both
    // send the merge over a failing lookup and leave no entity from before the merge in the cache.
    [Theory]
    [InlineData("uint")]
    [InlineData("decimal precise")]
    [InlineData("float (JSON)")]
    public async Task ABuilderUpdate_OverACacheWhoseLookupThrows_SendsTheMerge_ForEveryKindOfValue(string kind)
    {
        var (property, value, _, _) = OwnCellScalarCases.All[kind];
        var table = new RacingTableClient();
        using var inner = new MemoryCache(new MemoryCacheOptions());
        var cache = new LookupFailingCache(inner);
        var store = NewStore<OwnCellScalarDoc>(table, cache);
        await store.UpsertAsync(new OwnCellScalarDoc { Id = Id }, Ct);
        _ = await store.OneAsync(Id, Ct);
        cache.Armed = true;

        await store.UpdateAsync(Id, u => u.Updates[property] = value, Ct);

        Assert.Equal(1, table.Count(r => r.Operation == "update"));
        cache.Armed = false;
        using var freshCache = new MemoryCache(new MemoryCacheOptions());
        var fresh = (await NewStore<OwnCellScalarDoc>(table, freshCache).OneAsync(Id, Ct))!;
        Assert.Equal(OwnCellScalarCases.Describe(fresh), OwnCellScalarCases.Describe((await store.OneAsync(Id, Ct))!));
    }
}
