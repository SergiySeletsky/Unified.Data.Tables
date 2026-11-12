namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Ruling R15 (OBSERVED-1 of the review of fix wave 5): a value that leaves its cell (split cells, continuation
/// rows, a blob) is stored only where the property a read of the store's entity type sets its column on is a
/// large-value member of the object the read creates there. An aware entity whose property is declared as a
/// base class and empty on a new entity, written holding a subclass whose member is marked, had its value
/// accepted and stored split; a read then created the base class, whose member is not marked, and the store's
/// own cache copy (and the next save of the entity that was read) trimmed it or failed. Such a write is refused
/// with <see cref="NotSupportedException"/> before anything is stored or uploaded, on both stores, for every
/// write path, under every <see cref="OversizedCellPolicy"/>. A value that fits one cell is an ordinary cell as
/// before; the three remedies the message names store the value and every read returns it whole.
/// </summary>
[Collection("OversizedCellPolicy")]
public class LargeValueOnlyWhereAReadCarriesItTests
{
    private const int Large = 200_000;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OversizedCellPolicy[] Policies =
        [OversizedCellPolicy.Throw, OversizedCellPolicy.TrimWithMarker, OversizedCellPolicy.TrimSilently];

    public class MemoBase
    {
        public virtual string? Notes { get; set; }
        public virtual byte[]? Data { get; set; }
        public List<string>? Items { get; set; }
    }

    public sealed class MemoMarked : MemoBase
    {
        [TableValueSplit(SplitEncoding.Text)]
        public override string? Notes { get; set; }

        [TableValueSplit(SplitEncoding.Gzip)]
        public override byte[]? Data { get; set; }
    }

    // The class-level opt-in of the runtime type covers the JSON-stored member it inherits.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class MemoMarkedByClass : MemoBase
    {
    }

    // Aware by its class-level opt-in; Doc is empty on a new entity, so a read creates a MemoBase there.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareNullDefault : Entity
    {
        public MemoBase? Doc { get; set; }
        public string? Title { get; set; }
    }

    public sealed class Holder
    {
        public MemoBase? Doc { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareNestedNullDefault : Entity
    {
        public Holder H { get; set; } = new();
        public string? Title { get; set; }
    }

    // Remedy 1: the property initialised with an instance of the runtime type.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareWithDefault : Entity
    {
        public MemoBase Doc { get; set; } = new MemoMarked();
        public string? Title { get; set; }
    }

    // Remedy 2: the property declared with the runtime type (aware by its declared types).
    public sealed class AwareDeclaredAsRuntime : Entity
    {
        public MemoMarked? Doc { get; set; }
        public string? Title { get; set; }
    }

    // Remedy 3: the member marked on the declared type.
    public class MemoBaseMarked
    {
        [TableValueSplit(SplitEncoding.Text)]
        public virtual string? Notes { get; set; }
    }

    public sealed class MemoBaseMarkedSub : MemoBaseMarked
    {
        public override string? Notes { get; set; }
    }

    public sealed class AwareMarkedOnDeclared : Entity
    {
        public MemoBaseMarked? Doc { get; set; }
        public string? Title { get; set; }
    }

    // A subclass written through a store of its base type: a read creates the base, whose Notes is not marked.
    public class BaseEntity : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Other { get; set; }

        public virtual string? Notes { get; set; }
    }

    public sealed class SubEntity : BaseEntity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public override string? Notes { get; set; }
    }

    public static TheoryData<string, OversizedCellPolicy> StoresAndPolicies()
    {
        var d = new TheoryData<string, OversizedCellPolicy>();
        foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
            foreach (var p in Policies)
                d.Add(k, p);
        return d;
    }

    public static TheoryData<string, OversizedCellPolicy, string, string> RefusedCases()
    {
        var d = new TheoryData<string, OversizedCellPolicy, string, string>();
        foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
            foreach (var p in Policies)
                foreach (var value in new[] { "text", "binary", "json" })
                    foreach (var write in new[] { "create", "upsert", "update", "create-batch", "upsert-batch" })
                        d.Add(k, p, value, write);
        return d;
    }

    private static MemoBase LargeMemo(string value) => value switch
    {
        "text" => new MemoMarked { Notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(Large, 1) },
        "binary" => new MemoMarked { Data = RandomBytes(Large) },
        "json" => new MemoMarkedByClass { Items = [.. Enumerable.Range(0, 20).Select(i => BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(10_000, i))] },
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ColumnOf(string value) => value switch
    {
        "text" => "Doc_Notes",
        "binary" => "Doc_Data",
        _ => "Doc_Items",
    };

    private static byte[] RandomBytes(int n)
    {
        var bytes = new byte[n];
        new Random(n).NextBytes(bytes);
        return bytes;
    }

    private static async Task<T> WithPolicy<T>(OversizedCellPolicy policy, Func<Task<T>> action)
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        try
        {
            return await action();
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    private static Task Write<T>(IStorage<T> store, string write, T entity) where T : class, IEntity, new() => write switch
    {
        "create" => store.CreateAsync(entity, Ct),
        "upsert" => store.UpsertAsync(entity, Ct),
        "update" => store.UpdateAsync(entity, Ct),
        "create-batch" => store.CreateBatchAsync([entity], Ct),
        "upsert-batch" => store.UpsertBatchAsync([entity], Ct),
        _ => throw new ArgumentOutOfRangeException(nameof(write)),
    };

    // The reproduction turned into the requirement: the write is refused and nothing is stored or uploaded,
    // so no read can return a shorter value.
    [Theory]
    [MemberData(nameof(RefusedCases))]
    public async Task ValueBelowAnEmptyBaseTypedProperty_WhoseRuntimeSubtypeMarksIt_IsRefused_AndNothingIsStored(
        string kind, OversizedCellPolicy policy, string value, string write)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<AwareNullDefault>();
        var id = stores.Id("w");
        string? etag = null;
        if (write == "update")
            etag = (await store.CreateAsync(new AwareNullDefault { Id = id, Doc = new MemoMarked { Notes = "small" }, Title = "t" }, Ct)).ETag;
        var before = await stores.RawRowsAsync<AwareNullDefault>();

        var thrown = await WithPolicy(policy, async () => await Record.ExceptionAsync(() =>
            Write(store, write, new AwareNullDefault { Id = id, ETag = etag, Doc = LargeMemo(value), Title = "t2" })));

        var column = ColumnOf(value);
        var ex = Assert.IsType<NotSupportedException>(thrown);
        Assert.Contains($"'{column}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(MemoBase), ex.Message, StringComparison.Ordinal);
        Assert.Contains(value == "json" ? nameof(MemoMarkedByClass) : nameof(MemoMarked), ex.Message, StringComparison.Ordinal);
        Assert.Contains("constructor", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[TableValueSplit]", ex.Message, StringComparison.Ordinal);

        var after = await stores.RawRowsAsync<AwareNullDefault>();
        Assert.Equal(before.Count, after.Count);
        Assert.DoesNotContain(after, r => r.Keys.Any(k => k.StartsWith(column, StringComparison.Ordinal) && k != "Doc_Notes"));
        Assert.Empty(await stores.BlobNamesAsync<AwareNullDefault>());
        if (write == "update")
        {
            var read = await WithPolicy(policy, () => store.OneAsync(id, Ct));
            Assert.Equal("small", read!.Doc!.Notes);
            Assert.Equal("t", read.Title);
        }
        else
        {
            Assert.Null(await store.OneAsync(id, Ct));
        }
    }

    // The same with a blob offload threshold the value exceeds: nothing is uploaded.
    [Theory]
    [MemberData(nameof(StoresAndPolicies))]
    public async Task ValueBelowAnEmptyBaseTypedProperty_AboveTheOffloadThreshold_IsRefusedBeforeAnyUpload(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind, thresholdBytes: 1_000);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<AwareNullDefault>();
        var thrown = await WithPolicy(policy, async () => await Record.ExceptionAsync(() =>
            store.CreateAsync(new AwareNullDefault { Id = stores.Id("w"), Doc = LargeMemo("text") }, Ct)));
        Assert.IsType<NotSupportedException>(thrown);
        Assert.Empty(await stores.RawRowsAsync<AwareNullDefault>());
        Assert.Empty(await stores.BlobNamesAsync<AwareNullDefault>());
    }

    // Deeper: the empty base-typed property is below a default instance.
    [Theory]
    [MemberData(nameof(StoresAndPolicies))]
    public async Task ValueBelowANestedEmptyBaseTypedProperty_IsRefused(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<AwareNestedNullDefault>();
        var thrown = await WithPolicy(policy, async () => await Record.ExceptionAsync(() =>
            store.CreateAsync(new AwareNestedNullDefault { Id = stores.Id("w"), H = new Holder { Doc = LargeMemo("text") } }, Ct)));
        var ex = Assert.IsType<NotSupportedException>(thrown);
        Assert.Contains("'H_Doc_Notes'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<AwareNestedNullDefault>());
    }

    // The property holds a default instance of the base type itself: a read keeps that MemoBase.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareBaseDefault : Entity
    {
        public MemoBase Doc { get; set; } = new();
        public string? Title { get; set; }
    }

    [Theory]
    [MemberData(nameof(StoresAndPolicies))]
    public async Task ValueBelowAPropertyWhoseDefaultInstanceIsTheBaseType_IsRefused(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<AwareBaseDefault>();
        var thrown = await WithPolicy(policy, async () => await Record.ExceptionAsync(() =>
            store.CreateAsync(new AwareBaseDefault { Id = stores.Id("w"), Doc = LargeMemo("text") }, Ct)));
        var ex = Assert.IsType<NotSupportedException>(thrown);
        Assert.Contains("'Doc_Notes'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<AwareBaseDefault>());
    }

    // A subclass written through a store of its base type, whose own member is marked only by the subclass.
    [Theory]
    [MemberData(nameof(StoresAndPolicies))]
    public async Task ValueOfASubclassMember_ThroughAStoreOfTheBaseTypeThatDoesNotMarkIt_IsRefused(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<BaseEntity>();
        var thrown = await WithPolicy(policy, async () => await Record.ExceptionAsync(() =>
            store.CreateAsync(new SubEntity { Id = stores.Id("w"), Notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(Large, 2) }, Ct)));
        var ex = Assert.IsType<NotSupportedException>(thrown);
        Assert.Contains("'Notes'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<BaseEntity>());

        // The base type's own large member is stored as before.
        var other = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(Large, 3);
        await store.CreateAsync(new SubEntity { Id = stores.Id("o"), Other = other, Notes = "n" }, Ct);
        var read = await WithPolicy(policy, () => store.OneAsync(stores.Id("o"), Ct));
        Assert.Equal(other, read!.Other);
        Assert.Equal("n", read.Notes);
    }

    // A value that fits one cell stays an ordinary cell, as before.
    [Theory]
    [MemberData(nameof(StoresAndPolicies))]
    public async Task ValueThatFitsOneCell_BelowAnEmptyBaseTypedProperty_IsStoredAsAnOrdinaryCell(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<AwareNullDefault>();
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(10_000, 4);
        await WithPolicy(policy, () => store.CreateAsync(new AwareNullDefault { Id = stores.Id("w"), Doc = new MemoMarked { Notes = notes } }, Ct));
        var row = Assert.Single(await stores.RawRowsAsync<AwareNullDefault>());
        Assert.Equal(notes, row["Doc_Notes"]);
        var read = await WithPolicy(policy, () => store.OneAsync(stores.Id("w"), Ct));
        Assert.Equal(notes, read!.Doc!.Notes);
        Assert.Equal(notes, (await WithPolicy(policy, () => store.OneAsync(stores.Id("w"), Ct)))!.Doc!.Notes);
    }

    // The control of the review and the three remedies: the value is stored and every read returns it whole.
    public static TheoryData<string, OversizedCellPolicy, string> RemedyCases()
    {
        var d = new TheoryData<string, OversizedCellPolicy, string>();
        foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
            foreach (var p in Policies)
                foreach (var remedy in new[] { "default-instance", "declared-as-the-runtime-type", "marked-on-the-declared-type" })
                    d.Add(k, p, remedy);
        return d;
    }

    [Theory]
    [MemberData(nameof(RemedyCases))]
    public Task EachRemedyTheRefusalNames_StoresTheValue_AndEveryReadReturnsItWhole(string kind, OversizedCellPolicy policy, string remedy) =>
        remedy switch
        {
            "default-instance" => RemedyAsync<AwareWithDefault>(kind, policy,
                (e, v) => e.Doc = new MemoMarked { Notes = v }, e => e.Doc.Notes),
            "declared-as-the-runtime-type" => RemedyAsync<AwareDeclaredAsRuntime>(kind, policy,
                (e, v) => e.Doc = new MemoMarked { Notes = v }, e => e.Doc?.Notes),
            _ => RemedyAsync<AwareMarkedOnDeclared>(kind, policy,
                (e, v) => e.Doc = new MemoBaseMarkedSub { Notes = v }, e => e.Doc?.Notes),
        };

    private static async Task RemedyAsync<T>(string kind, OversizedCellPolicy policy, Action<T, string> set, Func<T, string?> get)
        where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<T>();
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(Large, 5);
        var entity = new T { Id = stores.Id("w") };
        set(entity, notes);
        await WithPolicy(policy, async () =>
        {
            await store.CreateAsync(entity, Ct);
            Assert.Equal(notes, get((await store.OneAsync(stores.Id("w"), Ct))!));
            var second = (await store.OneAsync(stores.Id("w"), Ct))!;
            Assert.Equal(notes, get(second));
            // The entity that was read is saved again: still whole.
            await store.UpsertAsync(second, Ct);
            Assert.Equal(notes, get((await stores.Store<T>().OneAsync(stores.Id("w"), Ct))!));
            return 0;
        });
        Assert.DoesNotContain(await stores.RawRowsAsync<T>(), r => r.ContainsKey("Doc_Notes") || r.ContainsKey("Doc_Notes__GZip"));
    }

    // Values that fit one cell each but make the row too large: the planner moves some off the row. A value a
    // read would not carry back into a large-value member is never one of them: it stays an ordinary cell, the
    // others move, and every value reads back whole.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareCrowded : Entity
    {
        public MemoBase? Doc { get; set; }
        public string? A { get; set; }
        public string? B { get; set; }
        public string? C { get; set; }
        public string? D { get; set; }
        public string? E { get; set; }
        public string? F { get; set; }
        public string? G { get; set; }
        public string? H { get; set; }
        public string? I { get; set; }
        public string? J { get; set; }
        public string? K { get; set; }
        public string? L { get; set; }
        public string? M { get; set; }
        public string? N { get; set; }
        public string? O { get; set; }
        public string? P { get; set; }
        public string? Q { get; set; }
        public string? R { get; set; }
    }

    private static string V(int seed, int n = 30_000) => BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(n, seed);

    [Theory]
    [MemberData(nameof(StoresAndPolicies))]
    public async Task ValueThatFitsOneCell_OnACrowdedRow_StaysInItsCell_WhileTheOthersMove(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<AwareCrowded>();
        var entity = new AwareCrowded
        {
            Id = stores.Id("w"), Doc = new MemoMarked { Notes = V(0, 32_000) },
            A = V(1), B = V(2), C = V(3), D = V(4), E = V(5), F = V(6), G = V(7), H = V(8), I = V(9), J = V(10), K = V(11), L = V(12), M = V(13), N = V(14), O = V(15), P = V(16), Q = V(17), R = V(18),
        };
        await WithPolicy(policy, () => store.CreateAsync(entity, Ct));
        var primary = (await stores.RawRowsAsync<AwareCrowded>()).Single(r => r.RowKey == "w");
        Assert.Equal(entity.Doc.Notes, primary["Doc_Notes"]);
        for (var i = 0; i < 2; i++)
        {
            var read = await WithPolicy(policy, () => store.OneAsync(stores.Id("w"), Ct));
            Assert.Equal(entity.Doc.Notes, read!.Doc!.Notes);
            Assert.Equal(entity.A, read.A);
            Assert.Equal(entity.R, read.R);
        }
    }

    // The same row where nothing else can move (the other values are not marked): refused before anything is
    // stored, never stored with that value split.
    public sealed class Crowded : Entity
    {
        public MemoBase? Doc { get; set; }
        public string? A { get; set; }
        public string? B { get; set; }
        public string? C { get; set; }
        public string? D { get; set; }
        public string? E { get; set; }
        public string? F { get; set; }
        public string? G { get; set; }
        public string? H { get; set; }
        public string? I { get; set; }
        public string? J { get; set; }
        public string? K { get; set; }
        public string? L { get; set; }
        public string? M { get; set; }
        public string? N { get; set; }
        public string? O { get; set; }
        public string? P { get; set; }
        public string? Q { get; set; }
        public string? R { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareCrowdedUnmarked : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }

        public Crowded Inner { get; set; } = new();
    }

    [Theory]
    [MemberData(nameof(StoresAndPolicies))]
    public async Task ValueThatFitsOneCell_OnARowNothingElseCanLeave_IsRefusedBeforeAnythingIsStored(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<AwareCrowdedUnmarked>();
        var inner = new Crowded { Doc = new MemoMarked { Notes = V(0, 32_000) } };
        foreach (var p in typeof(Crowded).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name.Length == 1))
            p.SetValue(inner, V(p.Name[0]));
        var thrown = await WithPolicy(policy, async () => await Record.ExceptionAsync(() =>
            store.CreateAsync(new AwareCrowdedUnmarked { Id = stores.Id("w"), Inner = inner }, Ct)));
        Assert.True(thrown is NotSupportedException or OversizedValueException, $"{thrown?.GetType().Name}: {thrown?.Message}");
        Assert.Empty(await stores.RawRowsAsync<AwareCrowdedUnmarked>());
    }
}
