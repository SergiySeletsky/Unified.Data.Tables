using System.Reflection;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Ruling R11 on a builder update whose path passes through a member that an interface-typed owner INHERITS
/// from another interface (SERIALIZER-1 of the review of fix wave 5): <c>Type.GetProperties</c> on an
/// interface lists only the members it declares, so the walk stopped there and the update wrote the member
/// marked on an interface as an ordinary cell, trimmed under a trimming policy. It is refused with the misuse
/// before any request, under every <see cref="OversizedCellPolicy"/> and at every size, on both stores. The
/// shapes the wave already refused stay refused; a plain type that meets an unmarked interface runs none of
/// its code; and what the walk finds is remembered (SERIALIZER-5).
/// </summary>
[Collection("OversizedCellPolicy")]
public class BuilderUpdateThroughAnInheritedInterfaceMemberTests
{
    private const string Table = "InheritedMemberUpdates";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, OversizedCellPolicy, int> StoresPoliciesSizes()
    {
        var d = new TheoryData<string, OversizedCellPolicy, int>();
        foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
            foreach (var p in new[] { OversizedCellPolicy.Throw, OversizedCellPolicy.TrimWithMarker, OversizedCellPolicy.TrimSilently })
                foreach (var n in new[] { 10, 40_000, 700_000 })
                    d.Add(k, p, n);
        return d;
    }

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public interface IMarked
    {
        [TableValueSplit(SplitEncoding.Text)]
        string? Notes { get; set; }
    }

    public sealed class MarkedImpl : IMarked
    {
        public string? Notes { get; set; }
    }

    // An unmarked interface whose member (declared as the implementing class) another interface inherits.
    public interface IOuterBase
    {
        MarkedImpl Inner { get; set; }
    }

    public interface IOuterDerived : IOuterBase
    {
        string? Label { get; set; }
    }

    public sealed class OuterImpl : IOuterDerived
    {
        public MarkedImpl Inner { get; set; } = new();
        public string? Label { get; set; }
    }

    // A plain entity: the path Doc.Inner.Notes runs through declared types only (IOuterDerived, then MarkedImpl).
    public sealed class InheritedHolder : Entity
    {
        public IOuterDerived Doc { get; set; } = new OuterImpl();
        public string? Title { get; set; }
    }

    // The same with the inner member declared as the marked interface itself.
    public interface IOuterBase2
    {
        IMarked Inner { get; set; }
    }

    public interface IOuterDerived2 : IOuterBase2
    {
    }

    public sealed class OuterImpl2 : IOuterDerived2
    {
        public IMarked Inner { get; set; } = new MarkedImpl();
    }

    public sealed class InheritedHolder2 : Entity
    {
        public IOuterDerived2 Doc { get; set; } = new OuterImpl2();
        public string? Title { get; set; }
    }

    // A generic interface.
    public interface IMarkedG<T>
    {
        [TableValueSplit(SplitEncoding.Text)]
        string? Notes { get; set; }
        T? Tag { get; set; }
    }

    public sealed class MarkedGImpl : IMarkedG<int>
    {
        public string? Notes { get; set; }
        public int Tag { get; set; }
    }

    public sealed class GenericHolder : Entity
    {
        public MarkedGImpl Doc { get; set; } = new();
        public IMarkedG<int>? Doc2 { get; set; }
        public string? Title { get; set; }
    }

    // An explicit implementation beside a public property of the same name.
    public sealed class ExplicitImpl : IMarked
    {
        string? IMarked.Notes { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class ExplicitHolder : Entity
    {
        public ExplicitImpl Doc { get; set; } = new();
        public string? Title { get; set; }
    }

    // A property hidden by `new` with the implementing class.
    public class HidingBase : Entity
    {
        public object? Doc { get; set; }
        public string? Title { get; set; }
    }

    public sealed class HidingHolder : HidingBase
    {
        public new MarkedImpl Doc { get; set; } = new();
    }

    // Two owners: an unmarked concrete class whose member is declared as the marked interface.
    public sealed class Middle
    {
        public IMarked Leaf { get; set; } = new MarkedImpl();
    }

    public sealed class TwoOwners : Entity
    {
        public Middle Doc { get; set; } = new();
        public string? Title { get; set; }
    }

    public sealed class RowWriter : Entity
    {
        public string? Title { get; set; }
    }

    // An aware entity (class-level opt-in) whose default instance reaches the marked interface through the
    // inherited member, and the same with the property empty on a new entity.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareInherited : Entity
    {
        public IOuterDerived Doc { get; set; } = new OuterImpl();
        public string? Title { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareInheritedNull : Entity
    {
        public IOuterDerived? Doc { get; set; }
        public string? Title { get; set; }
    }

    private static async Task<(Exception? Thrown, IReadOnlyList<string> Writes, Azure.Data.Tables.TableEntity Row)> UpdateAsync<T>(
        LargeValueStores stores, string id, OversizedCellPolicy policy, Action<UpdateBuilder<T>> update)
        where T : class, IEntity, new()
    {
        await stores.ShareTableAsync<RowWriter, T>(Table);
        var store = stores.Store<T>(Table);
        Exception? thrown = null;
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        IReadOnlyList<string> writes;
        try
        {
            writes = await stores.StorageWritesAsync(store, async () => thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, update, Ct)));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
        return (thrown, writes, (await stores.RawRowsAsync<T>(Table)).Single());
    }

    internal static string Describe(string shape, OversizedCellPolicy p, int n, Exception? thrown, IReadOnlyList<string> writes, Azure.Data.Tables.TableEntity row)
    {
        var cells = string.Join(", ", row.Keys.Where(k => k.Contains("Notes", StringComparison.Ordinal))
            .Select(k => $"{k}({(row[k] as string)?.Length ?? -1} chars)"));
        return $"{shape}, {p}, {n} characters: update {(thrown is null ? "accepted and reported as written" : $"threw {thrown.GetType().Name}: {thrown.Message[..Math.Min(160, thrown.Message.Length)]}")}; " +
               $"writes: [{string.Join(", ", writes)}]; stored cells: [{cells}]";
    }

    internal static string Ascii(int length, int seed)
    {
        var rng = new Random(seed);
        return string.Create(length, rng, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)r.Next(0x21, 0x7F);
        });
    }

    private static async Task RefusedAsync<T>(string shape, string kind, OversizedCellPolicy policy, int length, Action<UpdateBuilder<T>, string> set)
        where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        await stores.Store<RowWriter>(Table).CreateAsync(new RowWriter { Id = id, Title = "t" }, Ct);
        var notes = Ascii(length, length);
        var (thrown, writes, row) = await UpdateAsync<T>(stores, id, policy, b => set(b, notes));
        var notesCells = row.Keys.Where(k => k.Contains("Notes", StringComparison.Ordinal)).ToList();
        Assert.True(thrown is InvalidOperationException && writes.Count == 0 && notesCells.Count == 0,
            Describe(shape, policy, length, thrown, writes, row));
        Assert.Contains("[TableValueSplit] on IMarked", thrown!.Message, StringComparison.Ordinal);
    }

    // SERIALIZER-1: the member Inner, inherited by the declared interface, is declared as the implementing class.
    [Theory]
    [MemberData(nameof(StoresPoliciesSizes))]
    public Task InheritedInterfaceMemberOnThePath_DeclaredAsTheImplementingClass_IsRefused(string kind, OversizedCellPolicy policy, int length) =>
        RefusedAsync<InheritedHolder>("inherited-interface member -> implementing class", kind, policy, length,
            (b, v) => b.SetProperty(x => x.Doc.Inner.Notes, v));

    // SERIALIZER-1: the inherited member Inner is declared as the marked interface.
    [Theory]
    [MemberData(nameof(StoresPoliciesSizes))]
    public Task InheritedInterfaceMemberOnThePath_DeclaredAsTheMarkedInterface_IsRefused(string kind, OversizedCellPolicy policy, int length) =>
        RefusedAsync<InheritedHolder2>("inherited-interface member -> marked interface", kind, policy, length,
            (b, v) => b.SetProperty(x => x.Doc.Inner.Notes, v));

    [Theory]
    [MemberData(nameof(Stores))]
    public Task GenericInterface_ThroughTheImplementingClass_IsRefused(string kind) =>
        RefusedAsync<GenericHolder>("generic interface, implementing class", kind, OversizedCellPolicy.TrimSilently, 200_000,
            (b, v) => b.SetProperty(x => x.Doc.Notes, v));

    [Theory]
    [MemberData(nameof(Stores))]
    public Task GenericInterface_ThroughTheInterface_IsRefused(string kind) =>
        RefusedAsync<GenericHolder>("generic interface, declared interface", kind, OversizedCellPolicy.TrimSilently, 200_000,
            (b, v) => b.SetProperty(x => x.Doc2!.Notes, v));

    [Theory]
    [MemberData(nameof(Stores))]
    public Task ExplicitImplementation_IsRefused(string kind) =>
        RefusedAsync<ExplicitHolder>("explicit implementation", kind, OversizedCellPolicy.TrimSilently, 200_000,
            (b, v) => b.SetProperty(x => x.Doc.Notes, v));

    [Theory]
    [MemberData(nameof(Stores))]
    public Task HiddenProperty_DeclaredAsTheImplementingClass_IsRefused(string kind) =>
        RefusedAsync<HidingHolder>("new-hidden property", kind, OversizedCellPolicy.TrimSilently, 200_000,
            (b, v) => b.SetProperty(x => x.Doc.Notes, v));

    [Theory]
    [MemberData(nameof(Stores))]
    public Task TwoOwners_LeafDeclaredAsTheInterface_IsRefused(string kind) =>
        RefusedAsync<TwoOwners>("two owners", kind, OversizedCellPolicy.TrimSilently, 200_000,
            (b, v) => b.SetProperty(x => x.Doc.Leaf.Notes, v));

    // SERIALIZER-4 of the review of wave 4, kept: an aware entity whose default instance reaches the marked
    // interface through the inherited member reports exactly what a whole-entity write reports.
    [Fact]
    public void AwareEntity_InheritedInterfaceShape_DefaultInstance_ReportsTheMisuse()
    {
        var whole = Record.Exception(() => TableEntitySerializer.ToTableEntity(new AwareInherited { Id = "a|b" }, "a", "b"));
        var partial = Record.Exception(() => LargeValueRows.FlattenPropertyForUpdate(typeof(AwareInherited), "Doc_Inner_Notes", "x"));
        Assert.True(whole is InvalidOperationException && partial is InvalidOperationException && whole.Message == partial.Message,
            $"whole: {whole?.GetType().Name}: {whole?.Message}\npartial: {partial?.GetType().Name}: {partial?.Message}");
    }

    // SERIALIZER-1 (the verifier's assertion): with Doc empty on a new entity the builder update reports the
    // misuse, not the "constructor or a getter failed" refusal. The message may name no implementing class.
    [Fact]
    public void AwareEntity_InheritedInterfaceShape_NullDefault_ReportsTheMisuse()
    {
        var whole = Record.Exception(() => TableEntitySerializer.ToTableEntity(
            new AwareInheritedNull { Id = "a|b", Doc = new OuterImpl() }, "a", "b"));
        var partial = Record.Exception(() => LargeValueRows.FlattenPropertyForUpdate(typeof(AwareInheritedNull), "Doc_Inner_Notes", "x"));
        Assert.True(whole is InvalidOperationException && partial is InvalidOperationException
                    && partial.Message.Contains("[TableValueSplit] on IMarked.Notes", StringComparison.Ordinal),
            $"whole: {whole?.GetType().Name}: {whole?.Message}\npartial: {partial?.GetType().Name}: {partial?.Message}");
    }

    // ── Rule 2: a plain type that meets an unmarked interface: no constructor, getter or static constructor ─

    internal static class Counters
    {
        internal static int Ctor;
        internal static int Getter;
        internal static int Cctor;
    }

    public interface IUnmarked
    {
        string? Notes { get; set; }
    }

    public interface IUnmarkedDerived : IUnmarked
    {
    }

    public sealed class CountedImpl : IUnmarkedDerived
    {
        static CountedImpl() => Counters.Cctor++;
        public CountedImpl() => Counters.Ctor++;
        private string? _notes;
        public string? Notes { get { Counters.Getter++; return _notes; } set => _notes = value; }
    }

    public sealed class CountedEntity : Entity, IUnmarkedDerived
    {
        static CountedEntity() => Counters.Cctor++;
        private string? _notes;
        public string? Notes { get { Counters.Getter++; return _notes; } set => _notes = value; }
        public IUnmarked? ByInterface { get; set; }
        public IUnmarkedDerived? ByDerivedInterface { get; set; }
        public CountedImpl? ByClass { get; set; }
    }

    [Fact]
    public void PlainType_UnmarkedInterface_FlattenForUpdate_RunsNoUserCode_AndGivesTheSameCells()
    {
        var a = LargeValueRows.FlattenPropertyForUpdate(typeof(CountedEntity), "Notes", "n");
        var b = LargeValueRows.FlattenPropertyForUpdate(typeof(CountedEntity), "ByInterface_Notes", "n");
        var c = LargeValueRows.FlattenPropertyForUpdate(typeof(CountedEntity), "ByClass_Notes", "n");
        var d = LargeValueRows.FlattenPropertyForUpdate(typeof(CountedEntity), "ByDerivedInterface_Notes", "n");
        var keys = string.Join(",", a.Keys.Concat(b.Keys).Concat(c.Keys).Concat(d.Keys));
        Assert.True(Counters.Ctor == 0 && Counters.Getter == 0 && Counters.Cctor == 0
                    && keys == "Notes,ByInterface_Notes,ByClass_Notes,ByDerivedInterface_Notes"
                    && !TypeMetadataCache.IsLargeValueAware(typeof(CountedEntity)),
            $"ctor={Counters.Ctor} getter={Counters.Getter} cctor={Counters.Cctor} cells={keys}");
    }

    // ── SERIALIZER-5: what the walk finds is remembered: no property is listed again on a repeated update ─

    private sealed class CountingType(Type inner) : TypeDelegator(inner)
    {
        public int GetPropertiesCalls;

        public override PropertyInfo[] GetProperties(BindingFlags bindingAttr)
        {
            GetPropertiesCalls++;
            return base.GetProperties(bindingAttr);
        }
    }

    // A type no other test uses: the library's caches are keyed by the underlying type, so a type another
    // test has met would answer from what that test left (and this test could not fail).
    public sealed class OnlyCountedHere : Entity
    {
        public string? Title { get; set; }
    }

    public interface ICountedOuter
    {
        OnlyCountedLeaf Inner { get; set; }
    }

    public sealed class OnlyCountedLeaf
    {
        public string? Label { get; set; }
    }

    [Fact]
    public void TheInterfaceCheck_IsRememberedPerType_NotRepeatedOnEveryCall()
    {
        var t = new CountingType(typeof(OnlyCountedHere));
        for (var i = 0; i < 3; i++)
            Assert.Equal(["Title"], LargeValueRows.FlattenPropertyForUpdate(t, "Title", "v").Keys);
        Assert.True(t.GetPropertiesCalls <= 1, $"after 3 builder-update flattens of one column: GetProperties x{t.GetPropertiesCalls}");

        // An interface owner on the path: a repeated walk lists none of its members again.
        var outer = new CountingType(typeof(ICountedOuter));
        TypeMetadataCache.RefuseInterfaceMemberSplitOnPath(outer, "Inner_Label");
        var first = outer.GetPropertiesCalls;
        for (var i = 0; i < 3; i++)
            TypeMetadataCache.RefuseInterfaceMemberSplitOnPath(outer, "Inner_Label");
        Assert.True(first >= 1 && outer.GetPropertiesCalls == first,
            $"first walk through an interface owner: GetProperties x{first}; after 3 more: x{outer.GetPropertiesCalls}");
    }
}
