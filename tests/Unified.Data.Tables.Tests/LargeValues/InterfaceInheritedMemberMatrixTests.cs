using System.Linq.Expressions;
using System.Reflection;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A member that an interface inherits from another interface counts, in every decision the library takes from
/// the declared types, exactly as a member the interface declares (SERIALIZER-1 and SERIALIZER-L1 of the review
/// of fix wave 5): whether a type is large-value aware, its large-value columns, the read target of a column
/// resolved through declared types, the refusal of a builder update that meets the interface-member misuse
/// (ruling R11), and the plain-store refusal. The shapes of the member looked up or walked into: declared on the
/// interface itself, inherited one level, inherited two levels, inherited through a generic interface, declared
/// on two inherited interfaces (the same name), and (for the misuse) an interface that inherits a marked
/// interface; each as the type of a top-level property, of a nested property, and of the entity itself; both
/// stores; every <see cref="OversizedCellPolicy"/>.
/// </summary>
[Collection("OversizedCellPolicy")]
public class InterfaceInheritedMemberMatrixTests
{
    private const string Table = "InheritedMemberMatrix";
    private const int Large = 200_000;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OversizedCellPolicy[] Policies =
        [OversizedCellPolicy.Throw, OversizedCellPolicy.TrimWithMarker, OversizedCellPolicy.TrimSilently];

    // ── The holders: a top-level property, a nested property ─────────────────────────────────────────

    public sealed class Top<TDoc, TImpl> : Entity where TImpl : TDoc, new()
    {
        public TDoc Doc { get; set; } = new TImpl();
        public string? Title { get; set; }
    }

    public sealed class Wrap<TDoc, TImpl> where TImpl : TDoc, new()
    {
        public TDoc Doc { get; set; } = new TImpl();
    }

    public sealed class Nested<TDoc, TImpl> : Entity where TImpl : TDoc, new()
    {
        public Wrap<TDoc, TImpl> W { get; set; } = new();
        public string? Title { get; set; }
    }

    public sealed class RowWriter : Entity
    {
        public string? Title { get; set; }
    }

    // ── Family A: a CORRECTLY marked member (on a class) below the interface member ─────────────────

    public sealed class LeafOk
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public interface IDecl
    {
        LeafOk Inner { get; set; }
    }

    public interface IInh1 : IDecl
    {
    }

    public interface IInh2 : IInh1
    {
    }

    public interface IGenBase<T>
    {
        T Inner { get; set; }
    }

    public interface IGen : IGenBase<LeafOk>
    {
    }

    public interface ITwoA
    {
        LeafOk Inner { get; set; }
    }

    public interface ITwoB
    {
        LeafOk Inner { get; set; }
    }

    public interface ITwo : ITwoA, ITwoB
    {
    }

    public sealed class AllImpl : IInh2, IGen, ITwo
    {
        public LeafOk Inner { get; set; } = new();
    }

    // The entity itself implements the interfaces.
    public sealed class SelfAll : Entity, IInh2, IGen, ITwo
    {
        public LeafOk Inner { get; set; } = new();
        public string? Title { get; set; }
    }

    public static readonly (string Name, Type Entity, string Column)[] AwareCases =
    [
        ("declared, top-level", typeof(Top<IDecl, AllImpl>), "Doc_Inner_Notes"),
        ("inherited one level, top-level", typeof(Top<IInh1, AllImpl>), "Doc_Inner_Notes"),
        ("inherited two levels, top-level", typeof(Top<IInh2, AllImpl>), "Doc_Inner_Notes"),
        ("inherited through a generic interface, top-level", typeof(Top<IGen, AllImpl>), "Doc_Inner_Notes"),
        ("declared on two inherited interfaces, top-level", typeof(Top<ITwo, AllImpl>), "Doc_Inner_Notes"),
        ("declared, nested", typeof(Nested<IDecl, AllImpl>), "W_Doc_Inner_Notes"),
        ("inherited one level, nested", typeof(Nested<IInh1, AllImpl>), "W_Doc_Inner_Notes"),
        ("inherited two levels, nested", typeof(Nested<IInh2, AllImpl>), "W_Doc_Inner_Notes"),
        ("inherited through a generic interface, nested", typeof(Nested<IGen, AllImpl>), "W_Doc_Inner_Notes"),
        ("declared on two inherited interfaces, nested", typeof(Nested<ITwo, AllImpl>), "W_Doc_Inner_Notes"),
        ("the same name with different types on two inherited interfaces, top-level", typeof(Top<IDiff, DiffImpl>), "Doc_Inner_Notes"),
        ("the same name with different types on two inherited interfaces, nested", typeof(Nested<IDiff, DiffImpl>), "W_Doc_Inner_Notes"),
        ("the entity itself", typeof(SelfAll), "Inner_Notes"),
    ];

    public static TheoryData<string> AwareCaseNames() => [.. AwareCases.Select(c => c.Name)];

    public static TheoryData<string, string, OversizedCellPolicy> AwareCasesStoresPolicies()
    {
        var d = new TheoryData<string, string, OversizedCellPolicy>();
        foreach (var c in AwareCases)
            foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
                foreach (var p in Policies)
                    d.Add(c.Name, k, p);
        return d;
    }

    // Awareness and the listed columns: as the shape with a directly declared member.
    [Theory]
    [MemberData(nameof(AwareCaseNames))]
    public void CorrectlyMarkedMemberBelowAnInterfaceMember_IsLargeValueAware_AndListsItsColumnOnce(string name)
    {
        var (_, entity, column) = AwareCases.Single(c => c.Name == name);
        Assert.True(TypeMetadataCache.IsLargeValueAware(entity), $"{name}: not large-value aware");
        Assert.Equal(1, TypeMetadataCache.LargeValueColumns(entity).Count(c => c == column));
    }

    // The whole-entity write stores the value and every read returns it; a builder update of it is refused as
    // for every large value; nothing is stored trimmed or unsplit.
    [Theory]
    [MemberData(nameof(AwareCasesStoresPolicies))]
    public Task CorrectlyMarkedMemberBelowAnInterfaceMember_IsStoredWhole_AndNeverWrittenByABuilderUpdate(string name, string kind, OversizedCellPolicy policy)
    {
        var (_, entity, column) = AwareCases.Single(c => c.Name == name);
        return Invoke(nameof(StoredWholeAsync), entity, name, column, kind, policy);
    }

    private static async Task StoredWholeAsync<T>(string name, string column, string kind, OversizedCellPolicy policy) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<T>(Table);
        var id = stores.Id("a");
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(Large, column.Length);
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        try
        {
            var written = new T { Id = id };
            SetLeaf(written, column, notes);
            await store.CreateAsync(written, Ct);
            Assert.Equal(notes, GetLeaf((await store.OneAsync(id, Ct))!, column));
            Assert.Equal(notes, GetLeaf((await store.OneAsync(id, Ct))!, column));
            Assert.Equal(notes, GetLeaf((await stores.Store<T>(Table).OneAsync(id, Ct))!, column));

            foreach (var length in new[] { 10, Large })
            {
                var update = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(length, 7);
                Exception? thrown = null;
                var writes = await stores.StorageWritesAsync(store, async () =>
                    thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.SetProperty(PathOf<T>(column), update), Ct)));
                Assert.True(thrown is NotSupportedException && writes.Count == 0,
                    $"{name}, {kind}, {policy}, update of {length} characters: {thrown?.GetType().Name}: {thrown?.Message}; writes: [{string.Join(", ", writes)}]");
            }
            Assert.Equal(notes, GetLeaf((await stores.Store<T>(Table).OneAsync(id, Ct))!, column));
            var row = (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "a");
            Assert.False(row.ContainsKey(column) || row.ContainsKey(column + "__GZip") || row.ContainsKey(column + "__Truncated"),
                $"{name}: an ordinary cell for {column}: [{string.Join(", ", row.Keys)}]");
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    // Two inherited interfaces declare Inner with different types (the class implements one explicitly): the
    // declared walk sees both, the type is aware, and the column is listed once.
    public sealed class LeafOk2
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public interface IDiffA
    {
        LeafOk Inner { get; set; }
    }

    public interface IDiffB
    {
        LeafOk2 Inner { get; set; }
    }

    public interface IDiff : IDiffA, IDiffB
    {
    }

    public sealed class DiffImpl : IDiff
    {
        public LeafOk Inner { get; set; } = new();
        LeafOk2 IDiffB.Inner { get; set; } = new();
    }

    [Fact]
    public void SameMemberNameWithDifferentTypesOnTwoInheritedInterfaces_IsAware_AndListsTheColumnOnce()
    {
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(Top<IDiff, DiffImpl>)));
        Assert.Equal(["Doc_Inner_Notes"], TypeMetadataCache.LargeValueColumns(typeof(Top<IDiff, DiffImpl>)).Where(c => c.StartsWith("Doc", StringComparison.Ordinal)));
    }

    // The read target resolved through declared types (the path a type that is not aware takes).
    public sealed class PlainLeaf
    {
        public string? Label { get; set; }
    }

    public interface IPlainBase
    {
        PlainLeaf Inner { get; set; }
    }

    public interface IPlainDerived : IPlainBase
    {
    }

    public interface IPlainGen<T>
    {
        T Inner { get; set; }
    }

    public interface IPlainGenDerived : IPlainGen<PlainLeaf>
    {
    }

    public sealed class PlainImpl : IPlainDerived, IPlainGenDerived
    {
        public PlainLeaf Inner { get; set; } = new();
    }

    public sealed class PlainHolder : Entity
    {
        public IPlainDerived? Doc { get; set; }
        public IPlainGenDerived? Gen { get; set; }
        public IPlainBase? Direct { get; set; }
    }

    [Fact]
    public void ReadTargetThroughDeclaredTypes_SeesAnInheritedInterfaceMember_AsADeclaredOne()
    {
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(PlainHolder)));
        var direct = TypeMetadataCache.ReadTarget(typeof(PlainHolder), "Direct_Inner_Label");
        Assert.NotNull(direct);
        Assert.Equal(direct, TypeMetadataCache.ReadTarget(typeof(PlainHolder), "Doc_Inner_Label"));
        Assert.Equal(direct, TypeMetadataCache.ReadTarget(typeof(PlainHolder), "Gen_Inner_Label"));
        Assert.Null(TypeMetadataCache.ReadTarget(typeof(PlainHolder), "Doc_Inner_Missing"));
        Assert.True(TypeMetadataCache.TryGetMember(typeof(IPlainDerived), "Inner", out var memberType, out var large) && memberType == typeof(PlainLeaf) && !large);
    }

    // The plain-store refusal names declarations that work: a nullable struct reached through a member the
    // declared interface inherits is named, and following the declaration stores and reads the value back.
    public struct SMarked
    {
        public SMarked()
        {
        }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public interface INsBase
    {
        SMarked? Extra { get; set; }
    }

    public interface INsDer : INsBase
    {
    }

    public sealed class NsImpl : INsDer
    {
        public SMarked? Extra { get; set; }
    }

    public sealed class NsHolder : Entity
    {
        public INsDer Doc { get; set; } = new NsImpl();
    }

    public interface INsBaseF
    {
        SMarked Extra { get; set; }
    }

    public interface INsDerF : INsBaseF
    {
    }

    public sealed class NsImplF : INsDerF
    {
        public SMarked Extra { get; set; }
    }

    public sealed class NsHolderF : Entity
    {
        public INsDerF Doc { get; set; } = new NsImplF();
    }

    [Theory]
    [MemberData(nameof(StoresAndSizes))]
    public async Task PlainStoreRefusal_ThroughAnInheritedInterfaceMember_NamesTheNullableStruct_AndTheNamedDeclarationWorks(string kind, int length)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refused = await Assert.ThrowsAsync<NotSupportedException>(() => stores.Store<NsHolder>(Table).CreateAsync(
            new NsHolder { Id = stores.Id("r"), Doc = new NsImpl { Extra = new SMarked { Notes = "x" } } }, Ct));
        Assert.Contains("Declare INsDer.Extra as SMarked, not nullable", refused.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<NsHolder>(Table));

        var notes = new string('n', length);
        await stores.Store<NsHolderF>(Table).CreateAsync(new NsHolderF { Id = stores.Id("f"), Doc = new NsImplF { Extra = new SMarked { Notes = notes } } }, Ct);
        Assert.Equal(notes, (await stores.Store<NsHolderF>(Table).OneAsync(stores.Id("f"), Ct))!.Doc.Extra.Notes);
    }

    public static TheoryData<string, int> StoresAndSizes() => new()
    {
        { LargeValueStores.Memory, 10 }, { LargeValueStores.Memory, 40_000 },
        { LargeValueStores.Azurite, 10 }, { LargeValueStores.Azurite, 40_000 },
    };

    // ── Family B: the misuse (R11): [TableValueSplit] on an interface member, reached through the member ──

    public interface IMarked
    {
        [TableValueSplit(SplitEncoding.Text)]
        string? Notes { get; set; }
    }

    public interface IMarkedDer : IMarked
    {
    }

    public sealed class MarkedImpl : IMarked
    {
        public string? Notes { get; set; }
    }

    public sealed class MarkedDerImpl : IMarkedDer
    {
        public string? Notes { get; set; }
    }

    public interface IOD<TI>
    {
        TI Inner { get; set; }
    }

    public interface IO1<TI> : IOD<TI>
    {
    }

    public interface IO2<TI> : IO1<TI>
    {
    }

    public interface IOTA<TI>
    {
        TI Inner { get; set; }
    }

    public interface IOTB<TI>
    {
        TI Inner { get; set; }
    }

    public interface IOT<TI> : IOTA<TI>, IOTB<TI>
    {
    }

    public sealed class OutImpl<TI, TV> : IO2<TI>, IOT<TI> where TV : TI, new()
    {
        public TI Inner { get; set; } = new TV();
    }

    public interface INg0
    {
        MarkedImpl Inner { get; set; }
    }

    public interface INg1 : INg0
    {
    }

    public interface INg2 : INg1
    {
    }

    public sealed class NgImpl : INg2
    {
        public MarkedImpl Inner { get; set; } = new();
    }

    public sealed class SelfMisused : Entity, IO2<MarkedImpl>
    {
        public MarkedImpl Inner { get; set; } = new();
        public string? Title { get; set; }
    }

    public sealed class SelfMarkedDer : Entity, IMarkedDer
    {
        public string? Notes { get; set; }
        public string? Title { get; set; }
    }

    private static IEnumerable<(string Name, Type Entity, string Column)> MisuseCatalogue()
    {
        var inners = new (string Name, Type Declared, Type Impl)[]
        {
            ("the implementing class", typeof(MarkedImpl), typeof(MarkedImpl)),
            ("the marked interface", typeof(IMarked), typeof(MarkedImpl)),
            ("an interface that inherits the marked interface", typeof(IMarkedDer), typeof(MarkedDerImpl)),
        };
        var outers = new (string Name, Type Generic)[]
        {
            ("declared", typeof(IOD<>)), ("inherited one level", typeof(IO1<>)), ("inherited two levels", typeof(IO2<>)),
            ("declared on two inherited interfaces", typeof(IOT<>)),
        };
        foreach (var inner in inners)
        {
            foreach (var outer in outers)
            {
                var doc = outer.Generic.MakeGenericType(inner.Declared);
                var impl = typeof(OutImpl<,>).MakeGenericType(inner.Declared, inner.Impl);
                yield return ($"{outer.Name} (generic), Inner as {inner.Name}, top-level", typeof(Top<,>).MakeGenericType(doc, impl), "Doc_Inner_Notes");
                yield return ($"{outer.Name} (generic), Inner as {inner.Name}, nested", typeof(Nested<,>).MakeGenericType(doc, impl), "W_Doc_Inner_Notes");
            }
        }
        yield return ("inherited two levels, not generic, top-level", typeof(Top<INg2, NgImpl>), "Doc_Inner_Notes");
        yield return ("inherited two levels, not generic, nested", typeof(Nested<INg2, NgImpl>), "W_Doc_Inner_Notes");
        yield return ("the entity itself, inherited interface member", typeof(SelfMisused), "Inner_Notes");
        yield return ("the entity itself implements an interface that inherits the marked one", typeof(SelfMarkedDer), "Notes");
    }

    public static readonly (string Name, Type Entity, string Column)[] MisuseCases = [.. MisuseCatalogue()];

    public static TheoryData<string> MisuseCaseNames() => [.. MisuseCases.Select(c => c.Name)];

    public static TheoryData<string, string, OversizedCellPolicy> MisuseCasesStoresPolicies()
    {
        var d = new TheoryData<string, string, OversizedCellPolicy>();
        foreach (var c in MisuseCases)
            foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
                foreach (var p in Policies)
                    d.Add(c.Name, k, p);
        return d;
    }

    // The misuse opts nothing in; a whole-entity write reports it.
    [Theory]
    [MemberData(nameof(MisuseCaseNames))]
    public void MemberMarkedOnAnInterface_ReachedThroughAnyInterfaceMember_OptsNothingIn_AndAWholeEntityWriteReportsIt(string name)
    {
        var (_, entity, column) = MisuseCases.Single(c => c.Name == name);
        Assert.False(TypeMetadataCache.IsLargeValueAware(entity), $"{name}: large-value aware");
        Assert.DoesNotContain(column, TypeMetadataCache.LargeValueColumns(entity));
        var written = (IEntity)Activator.CreateInstance(entity)!;
        written.Id = "a|b";
        SetLeaf(written, column, "x");
        var whole = Record.Exception(() => TableEntitySerializer.ToTableEntity(written, "a", "b"));
        Assert.True(whole is InvalidOperationException && whole.Message.Contains("[TableValueSplit] on IMarked.Notes", StringComparison.Ordinal),
            $"{name}: {whole?.GetType().Name}: {whole?.Message}");
    }

    // A builder update never writes a cell for the member: refused with the misuse before any request.
    [Theory]
    [MemberData(nameof(MisuseCasesStoresPolicies))]
    public Task MemberMarkedOnAnInterface_ReachedThroughAnyInterfaceMember_IsRefusedOnABuilderUpdate(string name, string kind, OversizedCellPolicy policy)
    {
        var (_, entity, column) = MisuseCases.Single(c => c.Name == name);
        return Invoke(nameof(MisuseRefusedAsync), entity, name, column, kind, policy);
    }

    private static async Task MisuseRefusedAsync<T>(string name, string column, string kind, OversizedCellPolicy policy) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        await stores.Store<RowWriter>(Table).CreateAsync(new RowWriter { Id = id, Title = "t" }, Ct);
        await stores.ShareTableAsync<RowWriter, T>(Table);
        var store = stores.Store<T>(Table);
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        try
        {
            foreach (var length in new[] { 10, Large })
            {
                var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(length, 5);
                Exception? thrown = null;
                var writes = await stores.StorageWritesAsync(store, async () =>
                    thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.SetProperty(PathOf<T>(column), notes), Ct)));
                var row = (await stores.RawRowsAsync<T>(Table)).Single();
                Assert.True(thrown is InvalidOperationException && writes.Count == 0 && !row.Keys.Any(k => k.Contains("Notes", StringComparison.Ordinal)),
                    BuilderUpdateThroughAnInheritedInterfaceMemberTests.Describe(name, policy, length, thrown, writes, row));
                Assert.Contains("[TableValueSplit] on IMarked.Notes", thrown!.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    // ── L1 of the review (the verifier's tests), on both stores ───────────────────────────────────────

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public sealed class HolderDirect : Entity
    {
        public IDecl Doc { get; set; } = new AllImpl();
        public string? Title { get; set; }
    }

    public sealed class HolderInherited : Entity
    {
        public IInh1 Doc { get; set; } = new AllImpl();
        public string? Title { get; set; }
    }

    private static async Task<string> TrimmingBuilderUpdateAsync<T>(string kind, Action<UpdateBuilder<T>, string> set) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        await stores.Store<RowWriter>(Table).CreateAsync(new RowWriter { Id = id, Title = "t" }, Ct);
        await stores.ShareTableAsync<RowWriter, T>(Table);
        var store = stores.Store<T>(Table);
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(Large, 11);
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimSilently;
        Exception? thrown;
        try
        {
            thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => set(b, notes), Ct));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
        var row = (await stores.RawRowsAsync<T>(Table)).Single();
        var cells = string.Join(", ", row.Keys.Where(k => k.Contains("Notes", StringComparison.Ordinal)).Select(k => $"{k}({(row[k] as string)?.Length} chars)"));
        return $"{(thrown is null ? "accepted and reported as written" : thrown.GetType().Name + ": " + thrown.Message[..Math.Min(200, thrown.Message.Length)])}; stored cells: [{cells}]";
    }

    // SERIALIZER-L1, control: the interface declares the member itself.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task CorrectlyMarkedMember_BelowAnInterfaceThatDeclaresIt_IsNotTrimmedByABuilderUpdate(string kind)
    {
        var outcome = await TrimmingBuilderUpdateAsync<HolderDirect>(kind, (b, v) => b.SetProperty(x => x.Doc.Inner.Notes, v));
        Assert.True(outcome.StartsWith(nameof(NotSupportedException), StringComparison.Ordinal) && outcome.EndsWith("stored cells: []", StringComparison.Ordinal), outcome);
    }

    // SERIALIZER-L1: the declared interface inherits the member.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task CorrectlyMarkedMember_BelowAnInheritedInterfaceMember_IsNotTrimmedByABuilderUpdate(string kind)
    {
        var outcome = await TrimmingBuilderUpdateAsync<HolderInherited>(kind, (b, v) => b.SetProperty(x => x.Doc.Inner.Notes, v));
        Assert.True(outcome.StartsWith(nameof(NotSupportedException), StringComparison.Ordinal) && outcome.EndsWith("stored cells: []", StringComparison.Ordinal), outcome);
    }

    // SERIALIZER-L1: the whole-entity write of the inherited shape stores and reads back as the direct shape does
    // (no refusal naming a declaration that is already in place).
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task WholeEntityWrite_BelowAnInheritedInterfaceMember_StoresAndReadsBack_AsTheDirectShapeDoes(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(Large, 12);
        var direct = await Record.ExceptionAsync(() => stores.Store<HolderDirect>().CreateAsync(
            new HolderDirect { Id = stores.Id("d"), Doc = new AllImpl { Inner = new LeafOk { Notes = notes } } }, Ct));
        var inherited = await Record.ExceptionAsync(() => stores.Store<HolderInherited>().CreateAsync(
            new HolderInherited { Id = stores.Id("i"), Doc = new AllImpl { Inner = new LeafOk { Notes = notes } } }, Ct));
        Assert.True(direct is null && inherited is null,
            $"direct: {direct?.GetType().Name}: {direct?.Message} | inherited: {inherited?.GetType().Name}: {inherited?.Message}");
        Assert.Equal(notes, (await stores.Store<HolderDirect>().OneAsync(stores.Id("d"), Ct))!.Doc.Inner.Notes);
        Assert.Equal(notes, (await stores.Store<HolderInherited>().OneAsync(stores.Id("i"), Ct))!.Doc.Inner.Notes);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static Task Invoke(string method, Type entity, params object[] args) =>
        (Task)typeof(InterfaceInheritedMemberMatrixTests).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(entity).Invoke(null, args)!;

    // The property named on a declared type, also one an interface inherits (as the C# compiler resolves it).
    private static PropertyInfo Member(Type type, string name) =>
        type.GetProperty(name)
        ?? type.GetInterfaces().Select(i => i.GetProperty(name)).First(p => p is not null)!;

    // x => x.A.B.C for the column "A_B_C", through the declared types.
    private static Expression<Func<T, string?>> PathOf<T>(string column)
    {
        var x = Expression.Parameter(typeof(T), "x");
        Expression body = x;
        foreach (var segment in column.Split('_'))
            body = Expression.Property(body, Member(body.Type, segment));
        return Expression.Lambda<Func<T, string?>>(body, x);
    }

    private static void SetLeaf(object entity, string column, string value)
    {
        var owner = entity;
        var segments = column.Split('_');
        foreach (var segment in segments[..^1])
            owner = owner.GetType().GetProperty(segment)!.GetValue(owner)!;
        owner.GetType().GetProperty(segments[^1])!.SetValue(owner, value);
    }

    private static string? GetLeaf(object entity, string column)
    {
        object? owner = entity;
        foreach (var segment in column.Split('_'))
            owner = owner?.GetType().GetProperty(segment)!.GetValue(owner);
        return (string?)owner;
    }
}
