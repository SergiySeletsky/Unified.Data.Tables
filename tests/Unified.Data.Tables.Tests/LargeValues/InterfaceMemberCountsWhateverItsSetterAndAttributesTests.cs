using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A member of an interface counts as a possible column whatever its setter and its attributes (SERIALIZER-1 of
/// the review of fix wave 6): the declared interface tells only what a path reaches through it, and the
/// implementing class decides what is written. An interface member declared <c>{ get; }</c> (directly, inherited,
/// covariant, re-declared get-only with <c>new</c>) or marked <c>[IgnoreDataMember]</c>, implemented by a class
/// property with a getter and a setter, is a column the writer stores; the declared-type walks applied the
/// class rule "get and set, no [IgnoreDataMember]" to the interface member and skipped it. A correctly marked
/// value below it then made no type aware: its builder update stored the value trimmed and reported success, and
/// the plain-store refusal named a declaration that is already in place. Now the type is large-value aware and
/// lists the column; the builder update is refused before any request on both stores, under every policy and at
/// every size; the whole-entity write stores the value and every read returns it. Control: the same shape with
/// <c>{ get; set; }</c> on the interface.
/// </summary>
[Collection("OversizedCellPolicy")]
public class InterfaceMemberCountsWhateverItsSetterAndAttributesTests
{
    private const string Table = "IfaceMemberAnySetter";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Leaf
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public interface IDocRw
    {
        Leaf Inner { get; set; }
    }

    // A read-only contract implemented by a class with a setter (ordinary C#).
    public interface IDocRo
    {
        Leaf Inner { get; }
    }

    public interface IDocRoDerived : IDocRo
    {
    }

    // A covariant interface: its members can only have getters.
    public interface IBox<out T>
    {
        T Inner { get; }
    }

    public interface IDocIgnored
    {
        [IgnoreDataMember]
        Leaf Inner { get; set; }
    }

    // The derived interface re-declares the inherited get/set member with `new` as get-only (same type).
    public interface IDocNewRo : IDocRw
    {
        new Leaf Inner { get; }
    }

    public sealed class DocImpl : IDocRw, IDocRoDerived, IBox<Leaf>, IDocIgnored, IDocNewRo
    {
        public Leaf Inner { get; set; } = new();
    }

    public sealed class HolderRw : Entity
    {
        public IDocRw Doc { get; set; } = new DocImpl();
        public string? Title { get; set; }
    }

    public sealed class HolderRo : Entity
    {
        public IDocRo Doc { get; set; } = new DocImpl();
        public string? Title { get; set; }
    }

    public sealed class HolderRoDerived : Entity
    {
        public IDocRoDerived Doc { get; set; } = new DocImpl();
        public string? Title { get; set; }
    }

    public sealed class HolderBox : Entity
    {
        public IBox<Leaf> Doc { get; set; } = new DocImpl();
        public string? Title { get; set; }
    }

    public sealed class HolderIgnored : Entity
    {
        public IDocIgnored Doc { get; set; } = new DocImpl();
        public string? Title { get; set; }
    }

    public sealed class HolderNewRo : Entity
    {
        public IDocNewRo Doc { get; set; } = new DocImpl();
        public string? Title { get; set; }
    }

    public sealed class RowWriter : Entity
    {
        public string? Title { get; set; }
    }

    private static readonly Dictionary<string, Type> Shapes = new()
    {
        ["get;set; on the interface (control)"] = typeof(HolderRw),
        ["get-only on the interface"] = typeof(HolderRo),
        ["get-only, inherited from another interface"] = typeof(HolderRoDerived),
        ["covariant generic interface (get-only)"] = typeof(HolderBox),
        ["[IgnoreDataMember] on the interface member"] = typeof(HolderIgnored),
        ["get/set inherited, re-declared get-only with new"] = typeof(HolderNewRo),
    };

    public static TheoryData<string> ShapeNames() => [.. Shapes.Keys];

    public static TheoryData<string, string, OversizedCellPolicy, int> ShapesStoresPoliciesSizes()
    {
        var d = new TheoryData<string, string, OversizedCellPolicy, int>();
        foreach (var s in Shapes.Keys)
            foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
                foreach (var p in new[] { OversizedCellPolicy.Throw, OversizedCellPolicy.TrimWithMarker, OversizedCellPolicy.TrimSilently })
                    foreach (var n in new[] { 10, 40_000, 700_000 })
                        d.Add(s, k, p, n);
        return d;
    }

    public static TheoryData<string, string> ShapesStores()
    {
        var d = new TheoryData<string, string>();
        foreach (var s in Shapes.Keys)
            foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
                d.Add(s, k);
        return d;
    }

    public static TheoryData<string, OversizedCellPolicy> StoresTrimPolicies()
    {
        var d = new TheoryData<string, OversizedCellPolicy>();
        foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
            foreach (var p in new[] { OversizedCellPolicy.TrimSilently, OversizedCellPolicy.TrimWithMarker })
                d.Add(k, p);
        return d;
    }

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    // Awareness and the listed column, as for the control shape (R3: decided from the declared types, which
    // reach Leaf.Notes: the C# compiler resolves x.Doc.Inner.Notes through them).
    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void CorrectlyMarkedMember_BelowAnInterfaceMemberOfAnySetter_IsLargeValueAware(string shape)
    {
        var entity = Shapes[shape];
        var aware = TypeMetadataCache.IsLargeValueAware(entity);
        var columns = TypeMetadataCache.LargeValueColumns(entity);
        Assert.True(aware && columns.Contains("Doc_Inner_Notes"),
            $"{shape}: aware={aware}, columns=[{string.Join(", ", columns)}]");
    }

    // A builder update of the correctly marked value is refused as for every large value, on both stores, under
    // every policy and at every size; it never stores an ordinary (trimmed) cell.
    [Theory]
    [MemberData(nameof(ShapesStoresPoliciesSizes))]
    public Task BuilderUpdate_OfACorrectlyMarkedValue_BelowAnInterfaceMemberOfAnySetter_IsRefused(string shape, string kind, OversizedCellPolicy policy, int length) =>
        (Task)typeof(InterfaceMemberCountsWhateverItsSetterAndAttributesTests).GetMethod(nameof(RefusedAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(Shapes[shape]).Invoke(null, [shape, kind, policy, length])!;

    private static async Task RefusedAsync<T>(string shape, string kind, OversizedCellPolicy policy, int length) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        await stores.Store<RowWriter>(Table).CreateAsync(new RowWriter { Id = id, Title = "t" }, Ct);
        await stores.ShareTableAsync<RowWriter, T>(Table);
        var store = stores.Store<T>(Table);
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(length, length);
        var path = PathOf<T>();
        Exception? thrown = null;
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        IReadOnlyList<string> writes;
        try
        {
            writes = await stores.StorageWritesAsync(store, async () =>
                thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.SetProperty(path, notes), Ct)));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
        var row = (await stores.RawRowsAsync<T>(Table)).Single();
        var notesCells = row.Keys.Where(k => k.Contains("Notes", StringComparison.Ordinal)).ToList();
        Assert.True(thrown is NotSupportedException && writes.Count == 0 && notesCells.Count == 0,
            BuilderUpdateThroughAnInheritedInterfaceMemberTests.Describe(shape, policy, length, thrown, writes, row));
    }

    // x => x.Doc.Inner.Notes through the declared types of each holder.
    private static Expression<Func<T, string?>> PathOf<T>()
    {
        var x = Expression.Parameter(typeof(T), "x");
        var doc = Expression.Property(x, typeof(T).GetProperty("Doc")!);
        var docType = typeof(T).GetProperty("Doc")!.PropertyType;
        var innerProp = docType.GetProperty("Inner") ?? docType.GetInterfaces().Select(i => i.GetProperty("Inner")).First(p => p is not null)!;
        var inner = Expression.Property(doc, innerProp);
        var notes = Expression.Property(inner, typeof(Leaf).GetProperty(nameof(Leaf.Notes))!);
        return Expression.Lambda<Func<T, string?>>(notes, x);
    }

    // The same update written as C# source, for the plainest shape: the expression a user writes.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task BuilderUpdate_WrittenAsCSharp_GetOnlyInterfaceMember_TrimSilently_200000_IsRefused(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("c");
        await stores.Store<RowWriter>(Table).CreateAsync(new RowWriter { Id = id, Title = "t" }, Ct);
        await stores.ShareTableAsync<RowWriter, HolderRo>(Table);
        var store = stores.Store<HolderRo>(Table);
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 3);
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimSilently;
        Exception? thrown;
        try
        {
            thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.SetProperty(x => x.Doc.Inner.Notes, notes), Ct));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
        var read = await stores.Store<HolderRo>(Table).OneAsync(id, Ct);
        Assert.True(thrown is NotSupportedException,
            $"update {(thrown is null ? "accepted and reported as written" : thrown.GetType().Name)}; read back {read?.Doc.Inner.Notes?.Length} of {notes.Length} characters");
    }

    // The whole-entity write stores the value and reads it back as the control shape does: no refusal that names
    // a declaration already in place (Doc.Inner IS declared as Leaf, the type that carries the member).
    [Theory]
    [MemberData(nameof(ShapesStores))]
    public Task WholeEntityWrite_BelowAnInterfaceMemberOfAnySetter_StoresAndReadsBack_AsTheControlShapeDoes(string shape, string kind) =>
        (Task)typeof(InterfaceMemberCountsWhateverItsSetterAndAttributesTests).GetMethod(nameof(WholeAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(Shapes[shape]).Invoke(null, [shape, kind])!;

    private static async Task WholeAsync<T>(string shape, string kind) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 12);
        var entity = new T { Id = stores.Id("w") };
        var doc = typeof(T).GetProperty("Doc")!.GetValue(entity)!;
        ((DocImpl)doc).Inner.Notes = notes;
        var thrown = await Record.ExceptionAsync(() => stores.Store<T>(Table).CreateAsync(entity, Ct));
        Assert.True(thrown is null, $"{shape}: {thrown?.GetType().Name}: {thrown?.Message}");
        var read = await stores.Store<T>(Table).OneAsync(stores.Id("w"), Ct);
        var readDoc = (DocImpl)typeof(T).GetProperty("Doc")!.GetValue(read)!;
        Assert.Equal(notes, readDoc.Inner.Notes);
    }

    // ── The verifier's cases: the type creates its own row (no other writer) ─────────────────────────────

    public sealed class Leaf2
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    // A read-only contract implemented by a mutable class: the writer stores Doc2Impl.Inner (get; set;).
    public interface IDoc2Ro
    {
        Leaf2 Inner { get; }
    }

    public sealed class Doc2Impl : IDoc2Ro
    {
        public Leaf2 Inner { get; set; } = new();
    }

    public sealed class Holder2Ro : Entity
    {
        public IDoc2Ro Doc { get; set; } = new Doc2Impl();
        public string? Title { get; set; }
    }

    // The first remedy the plain-store refusal named: the class-level attribute.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class Holder2RoMarked : Entity
    {
        public IDoc2Ro Doc { get; set; } = new Doc2Impl();
    }

    // The type creates its own row (Notes empty: "a member that holds null is not refused"), then sets Notes with
    // a builder update, as an application does. No other writer is involved. Never stored trimmed.
    [Theory]
    [MemberData(nameof(StoresTrimPolicies))]
    public async Task SameType_BuilderUpdate_OfAMarkedValueBelowAGetOnlyInterfaceMember_IsRefused_NeverStoredTrimmed(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("s1");
        var store = stores.Store<Holder2Ro>(Table);
        await store.CreateAsync(new Holder2Ro { Id = id, Title = "t" }, Ct);
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 61);
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        Exception? thrown;
        try
        {
            thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.SetProperty(x => x.Doc.Inner.Notes, notes), Ct));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
        var read = (await stores.Store<Holder2Ro>(Table).OneAsync(id, Ct))!.Doc.Inner.Notes;
        Assert.True(thrown is NotSupportedException && read is null,
            $"{kind}, {policy}: update {(thrown is null ? "accepted and reported as written" : thrown.GetType().Name + ": " + thrown.Message)}; " +
            $"read back {read?.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"} of {notes.Length} characters");
    }

    // Control: the class-level remedy works, because the read side resolves the column through the runtime class
    // (the default instance) and sees Doc2Impl.Inner.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheClassLevelAttribute_StoresTheValueWhole_AndRefusesItsBuilderUpdate(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("s1c");
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 62);
        var store = stores.Store<Holder2RoMarked>(Table);
        var entity = new Holder2RoMarked { Id = id };
        ((Doc2Impl)entity.Doc).Inner.Notes = notes;
        await store.CreateAsync(entity, Ct);
        var first = (await store.OneAsync(id, Ct))!.Doc.Inner.Notes;
        var second = (await store.OneAsync(id, Ct))!.Doc.Inner.Notes;
        var fresh = (await stores.Store<Holder2RoMarked>(Table).OneAsync(id, Ct))!.Doc.Inner.Notes;
        var update = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.SetProperty(x => x.Doc.Inner.Notes, "short"), Ct));
        var after = (await stores.Store<Holder2RoMarked>(Table).OneAsync(id, Ct))!.Doc.Inner.Notes;
        Assert.True(first == notes && second == notes && fresh == notes && update is NotSupportedException && after == notes,
            $"{kind}: reads {first?.Length}/{second?.Length}/{fresh?.Length} of {notes.Length}; update {update?.GetType().Name ?? "accepted"}; after {after?.Length}");
    }

    // No refusal names a declaration already in place: IDoc2Ro.Inner IS declared as Leaf2, the type that carries
    // the member; the plain-store refusal told the user to declare it so.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ThePlainStoreRefusal_NamesNoDeclarationThatIsAlreadyInPlace(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var entity = new Holder2Ro { Id = stores.Id("s1r") };
        ((Doc2Impl)entity.Doc).Inner.Notes = "ten chars!";
        var thrown = await Record.ExceptionAsync(() => stores.Store<Holder2Ro>(Table).CreateAsync(entity, Ct));
        Assert.True(thrown is null || !thrown.Message.Contains("declare Holder2Ro.Doc.Inner with the type that carries the member", StringComparison.Ordinal),
            $"{kind}: {thrown?.GetType().Name}: {thrown?.Message}");
    }
}
