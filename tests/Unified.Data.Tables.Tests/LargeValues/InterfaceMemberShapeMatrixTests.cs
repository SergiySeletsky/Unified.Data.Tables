using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Every shape a member of an interface can have in C#, against every decision the library takes from the declared
/// types (SERIALIZER-1 and SERIALIZER-3 of the review of fix wave 6, and the neighbours found beside them). The rule:
/// a class or a struct keeps the writer's rule (a property with a getter and a setter, no [IgnoreDataMember]); a
/// member of an interface counts whatever its accessors and attributes, own or inherited at any depth, because the
/// implementing class decides what is written; an indexer never counts; and when a declared type has several members
/// of one name, every one of them is followed.
/// <para>
/// The member <c>Inner</c> (or <c>Item</c>) of the interface the property <c>Doc</c> is declared as: get/set, get-only,
/// set-only, init, [IgnoreDataMember], implemented by a get-only or an [IgnoreDataMember] class property (which the
/// writer does not store), implemented by a struct, a default body, an explicit implementation beside a public member, an explicit
/// implementation only, an explicit implementation in a derived interface, re-declared with <c>new</c> and the same
/// type, re-declared with <c>new</c> and another type, inherited one and two levels, get-only inherited two levels, a
/// diamond, from two bases with the same type, from two bases with different types (both orders, and with the leaf's
/// one get-only), generic, generic inherited, covariant, covariant inherited, an own indexer beside an inherited member
/// of the same name and type, and two interface levels that are each get-only. Each below a top-level and a nested
/// property, in four families: (A) a correctly marked value below the member (large-value aware, its column listed
/// once, the whole-entity write stores it and every read returns it, no builder update writes it), (T) a
/// <see cref="TableBlobContent"/> below the member or in it (the same decisions), (B) the misused attribute of ruling
/// R11 below it (opts nothing in, a whole-entity write reports it where the class stores the member, every builder
/// update is refused with the misuse), (P) an ordinary value below it (rule 2: the plain path, no user code, the same
/// cells). Then the plain-store refusal messages (a member the interface lacks; a nullable struct below six shapes;
/// an interface-typed property with no instance), each named declaration followed, and the class-owner controls.
/// </para>
/// </summary>
[Collection("OversizedCellPolicy")]
public class InterfaceMemberShapeMatrixTests
{
    private const string Table = "IfaceShapeMatrix";
    private const int Large = 200_000;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OversizedCellPolicy[] Policies =
        [OversizedCellPolicy.Throw, OversizedCellPolicy.TrimWithMarker, OversizedCellPolicy.TrimSilently];

    private static readonly string[] Kinds = [LargeValueStores.Memory, LargeValueStores.Azurite];

    // ── The holders: the interface-typed property at the top level, and below a nested object ───────────────

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

    // Shared by the families: a generic and a covariant interface.
    public interface IGen<T> { T Inner { get; set; } }
    public interface IBox<out T> { T Inner { get; } }

    // ── Family A: a correctly marked value below the member ──────────────────────────────────────────────

    public sealed class LeafA
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public sealed class OtherA
    {
        public string? Notes { get; set; }
    }

    public interface IGetSetA { LeafA Inner { get; set; } }
    public interface IGetOnlyA { LeafA Inner { get; } }
    public interface ISetOnlyA { LeafA Inner { set; } }
    public interface IInitA { LeafA Inner { get; init; } }
    public interface IIgnoredA { [IgnoreDataMember] LeafA Inner { get; set; } }
    public interface IDefaultBodyA { LeafA Inner => new(); }
    public interface IExplicitA { LeafA Inner { get; set; } }
    public interface IExplicitOnlyA { LeafA Inner { get; set; } }
    public interface IExplicitInInterfaceA : IGetSetA { LeafA IGetSetA.Inner { get => new(); set { } } }
    public interface INewSameA : IGetSetA { new LeafA Inner { get; } }
    public interface INewOtherA : IGetSetA { new OtherA Inner { get; set; } }
    public interface IInherited1A : IGetSetA { }
    public interface IInherited2A : IInherited1A { }
    public interface IGetOnly1A : IGetOnlyA { }
    public interface IGetOnly2A : IGetOnly1A { }
    public interface IDiamondLeftA : IGetOnlyA { }
    public interface IDiamondRightA : IGetOnlyA { }
    public interface IDiamondA : IDiamondLeftA, IDiamondRightA { }
    public interface IGetSetSecondA { LeafA Inner { get; set; } }
    public interface ITwoSameA : IGetSetA, IGetSetSecondA { }
    public interface IOtherBaseA { OtherA Inner { get; set; } }
    public interface ITwoMarkedFirstA : IGetSetA, IOtherBaseA { }
    public interface ITwoOtherFirstA : IOtherBaseA, IGetSetA { }
    public interface ITwoOtherFirstGetOnlyA : IOtherBaseA, IGetOnlyA { }
    public interface IGenInheritedA : IGen<LeafA> { }
    public interface IBoxInheritedA : IBox<LeafA> { }
    public interface IItemBaseA { LeafA Item { get; set; } }
    public interface IItemA : IItemBaseA { LeafA this[int index] { get; } }
    public interface IMidA { LeafA Inner { get; } }
    public interface IOuterA { IMidA Mid { get; } }

    public sealed class ImplA : IGetSetA, IGetOnly2A, ISetOnlyA, IIgnoredA, IDefaultBodyA, IExplicitInInterfaceA, INewSameA, IInherited2A,
        IDiamondA, ITwoSameA, IGenInheritedA, IBoxInheritedA
    {
        public LeafA Inner { get; set; } = new();
    }

    public sealed class InitImplA : IInitA { public LeafA Inner { get; init; } = new(); }
    public sealed class GetOnlyImplA : IGetOnlyA { public LeafA Inner { get; } = new(); }

    public struct StructImplA : IGetOnlyA
    {
        public StructImplA()
        {
        }

        public LeafA Inner { get; set; } = new();
    }
    public sealed class IgnoredImplA : IGetSetA { [IgnoreDataMember] public LeafA Inner { get; set; } = new(); }

    public sealed class ExplicitImplA : IExplicitA
    {
        public LeafA Inner { get; set; } = new();
        LeafA IExplicitA.Inner { get; set; } = new();
    }

    public sealed class ExplicitOnlyImplA : IExplicitOnlyA { LeafA IExplicitOnlyA.Inner { get; set; } = new(); }

    public sealed class NewOtherImplA : INewOtherA
    {
        public LeafA Inner { get; set; } = new();
        OtherA INewOtherA.Inner { get; set; } = new();
    }

    public sealed class TwoImplA : ITwoMarkedFirstA, ITwoOtherFirstA, ITwoOtherFirstGetOnlyA
    {
        public LeafA Inner { get; set; } = new();
        OtherA IOtherBaseA.Inner { get; set; } = new();
    }

    public sealed class ItemImplA : IItemA
    {
        public LeafA Item { get; set; } = new();
        LeafA IItemA.this[int index] => Item;
    }

    public sealed class MidImplA : IMidA { public LeafA Inner { get; set; } = new(); }
    public sealed class OuterImplA : IOuterA { public IMidA Mid { get; set; } = new MidImplA(); }

    // ── Family T: a TableBlobContent below the member (or the member typed TableBlobContent) ─────────────────

    public sealed class BlobLeafT
    {
        public TableBlobContent? File { get; set; }
    }

    public sealed class OtherT
    {
        public object? File { get; set; }
    }

    public interface IGetSetT { BlobLeafT Inner { get; set; } }
    public interface IGetOnlyT { BlobLeafT Inner { get; } }
    public interface ISetOnlyT { BlobLeafT Inner { set; } }
    public interface IIgnoredT { [IgnoreDataMember] BlobLeafT Inner { get; set; } }
    public interface IDefaultBodyT { BlobLeafT Inner => new(); }
    public interface INewSameT : IGetSetT { new BlobLeafT Inner { get; } }
    public interface IGetOnly1T : IGetOnlyT { }
    public interface IGetOnly2T : IGetOnly1T { }
    public interface IOtherBaseT { OtherT Inner { get; set; } }
    public interface ITwoOtherFirstGetOnlyT : IOtherBaseT, IGetOnlyT { }
    public interface IItemBaseT { BlobLeafT Item { get; set; } }
    public interface IItemT : IItemBaseT { BlobLeafT this[int index] { get; } }

    public interface IFileGetSetT { TableBlobContent? File { get; set; } }
    public interface IFileGetOnlyT { TableBlobContent? File { get; } }
    public interface IFileSetOnlyT { TableBlobContent? File { set; } }
    public interface IFileIgnoredT { [IgnoreDataMember] TableBlobContent? File { get; set; } }
    public interface IFileGetOnly1T : IFileGetOnlyT { }
    public interface IFileGetOnly2T : IFileGetOnly1T { }

    public sealed class ImplT : IGetSetT, IGetOnly2T, ISetOnlyT, IIgnoredT, IDefaultBodyT, INewSameT, IBox<BlobLeafT>
    {
        public BlobLeafT Inner { get; set; } = new();
    }

    public sealed class TwoImplT : ITwoOtherFirstGetOnlyT
    {
        public BlobLeafT Inner { get; set; } = new();
        OtherT IOtherBaseT.Inner { get; set; } = new();
    }

    public sealed class ItemImplT : IItemT
    {
        public BlobLeafT Item { get; set; } = new();
        BlobLeafT IItemT.this[int index] => Item;
    }

    public sealed class FileImplT : IFileGetSetT, IFileGetOnly2T, IFileSetOnlyT, IFileIgnoredT { public TableBlobContent? File { get; set; } }

    public sealed class BoxFileImplT : IBox<TableBlobContent?> { public TableBlobContent? Inner { get; set; } }

    // ── Family B: the misused attribute (R11) below the member ───────────────────────────────────────────

    public interface IMarked
    {
        [TableValueSplit(SplitEncoding.Text)]
        string? Notes { get; set; }
    }

    public sealed class MarkedB : IMarked
    {
        public string? Notes { get; set; }
    }

    public sealed class OtherB
    {
        public string? Notes { get; set; }
    }

    public interface IGetSetB { MarkedB Inner { get; set; } }
    public interface IGetOnlyB { MarkedB Inner { get; } }
    public interface ISetOnlyB { MarkedB Inner { set; } }
    public interface IInitB { MarkedB Inner { get; init; } }
    public interface IIgnoredB { [IgnoreDataMember] MarkedB Inner { get; set; } }
    public interface IDefaultBodyB { MarkedB Inner => new(); }
    public interface IExplicitB { MarkedB Inner { get; set; } }
    public interface IExplicitOnlyB { MarkedB Inner { get; set; } }
    public interface IExplicitInInterfaceB : IGetSetB { MarkedB IGetSetB.Inner { get => new(); set { } } }
    public interface INewSameB : IGetSetB { new MarkedB Inner { get; } }
    public interface INewOtherB : IGetSetB { new OtherB Inner { get; set; } }
    public interface IInherited1B : IGetSetB { }
    public interface IInherited2B : IInherited1B { }
    public interface IGetOnly1B : IGetOnlyB { }
    public interface IGetOnly2B : IGetOnly1B { }
    public interface IDiamondLeftB : IGetOnlyB { }
    public interface IDiamondRightB : IGetOnlyB { }
    public interface IDiamondB : IDiamondLeftB, IDiamondRightB { }
    public interface IGetSetSecondB { MarkedB Inner { get; set; } }
    public interface ITwoSameB : IGetSetB, IGetSetSecondB { }
    public interface IOtherBaseB { OtherB Inner { get; set; } }
    public interface ITwoMarkedFirstB : IGetSetB, IOtherBaseB { }
    public interface ITwoOtherFirstB : IOtherBaseB, IGetSetB { }
    public interface ITwoOtherFirstGetOnlyB : IOtherBaseB, IGetOnlyB { }
    public interface IGenInheritedB : IGen<MarkedB> { }
    public interface IBoxInheritedB : IBox<MarkedB> { }
    public interface IItemBaseB { MarkedB Item { get; set; } }
    public interface IItemB : IItemBaseB { MarkedB this[int index] { get; } }
    public interface IMidB { MarkedB Inner { get; } }
    public interface IOuterB { IMidB Mid { get; } }

    public sealed class ImplB : IGetSetB, IGetOnly2B, ISetOnlyB, IIgnoredB, IDefaultBodyB, IExplicitInInterfaceB, INewSameB, IInherited2B,
        IDiamondB, ITwoSameB, IGenInheritedB, IBoxInheritedB
    {
        public MarkedB Inner { get; set; } = new();
    }

    public sealed class InitImplB : IInitB { public MarkedB Inner { get; init; } = new(); }
    public sealed class GetOnlyImplB : IGetOnlyB { public MarkedB Inner { get; } = new(); }

    public struct StructImplB : IGetOnlyB
    {
        public StructImplB()
        {
        }

        public MarkedB Inner { get; set; } = new();
    }
    public sealed class IgnoredImplB : IGetSetB { [IgnoreDataMember] public MarkedB Inner { get; set; } = new(); }

    public sealed class ExplicitImplB : IExplicitB
    {
        public MarkedB Inner { get; set; } = new();
        MarkedB IExplicitB.Inner { get; set; } = new();
    }

    public sealed class ExplicitOnlyImplB : IExplicitOnlyB { MarkedB IExplicitOnlyB.Inner { get; set; } = new(); }

    public sealed class NewOtherImplB : INewOtherB
    {
        public MarkedB Inner { get; set; } = new();
        OtherB INewOtherB.Inner { get; set; } = new();
    }

    public sealed class TwoImplB : ITwoMarkedFirstB, ITwoOtherFirstB, ITwoOtherFirstGetOnlyB
    {
        public MarkedB Inner { get; set; } = new();
        OtherB IOtherBaseB.Inner { get; set; } = new();
    }

    public sealed class ItemImplB : IItemB
    {
        public MarkedB Item { get; set; } = new();
        MarkedB IItemB.this[int index] => Item;
    }

    public sealed class MidImplB : IMidB { public MarkedB Inner { get; set; } = new(); }
    public sealed class OuterImplB : IOuterB { public IMidB Mid { get; set; } = new MidImplB(); }

    // Class owners keep the writer's rule for what is a column; the R11 walk still follows every member a path names.
    public sealed class OwnerGetOnlyB { public MarkedB Inner { get; } = new(); }
    public sealed class OwnerIgnoredB { [IgnoreDataMember] public MarkedB Inner { get; set; } = new(); }

    // ── Family P: an ordinary value below the member (rule 2: no [TableValueSplit], no TableBlobContent) ─────

    internal static class PlainCode
    {
        internal static int Runs;
    }

    public sealed class LeafP
    {
        public string? Notes { get; set; }
    }

    public sealed class OtherP
    {
        public string? Notes { get; set; }
    }

    public interface IGetSetP { LeafP Inner { get; set; } }
    public interface IGetOnlyP { LeafP Inner { get; } }
    public interface ISetOnlyP { LeafP Inner { set; } }
    public interface IInitP { LeafP Inner { get; init; } }
    public interface IIgnoredP { [IgnoreDataMember] LeafP Inner { get; set; } }
    public interface IDefaultBodyP { LeafP Inner => new(); }
    public interface IExplicitP { LeafP Inner { get; set; } }
    public interface IExplicitOnlyP { LeafP Inner { get; set; } }
    public interface IExplicitInInterfaceP : IGetSetP { LeafP IGetSetP.Inner { get => new(); set { } } }
    public interface INewSameP : IGetSetP { new LeafP Inner { get; } }
    public interface INewOtherP : IGetSetP { new OtherP Inner { get; set; } }
    public interface IInherited1P : IGetSetP { }
    public interface IInherited2P : IInherited1P { }
    public interface IGetOnly1P : IGetOnlyP { }
    public interface IGetOnly2P : IGetOnly1P { }
    public interface IDiamondLeftP : IGetOnlyP { }
    public interface IDiamondRightP : IGetOnlyP { }
    public interface IDiamondP : IDiamondLeftP, IDiamondRightP { }
    public interface IGetSetSecondP { LeafP Inner { get; set; } }
    public interface ITwoSameP : IGetSetP, IGetSetSecondP { }
    public interface IOtherBaseP { OtherP Inner { get; set; } }
    public interface ITwoMarkedFirstP : IGetSetP, IOtherBaseP { }
    public interface ITwoOtherFirstP : IOtherBaseP, IGetSetP { }
    public interface ITwoOtherFirstGetOnlyP : IOtherBaseP, IGetOnlyP { }
    public interface IGenInheritedP : IGen<LeafP> { }
    public interface IBoxInheritedP : IBox<LeafP> { }
    public interface IItemBaseP { LeafP Item { get; set; } }
    public interface IItemP : IItemBaseP { LeafP this[int index] { get; } }
    public interface IMidP { LeafP Inner { get; } }
    public interface IOuterP { IMidP Mid { get; } }

    // Every constructor and getter of the implementing classes counts as user code run.
    public sealed class ImplP : IGetSetP, IGetOnly2P, ISetOnlyP, IIgnoredP, IDefaultBodyP, IExplicitInInterfaceP, INewSameP, IInherited2P,
        IDiamondP, ITwoSameP, IGenInheritedP, IBoxInheritedP
    {
        private LeafP _inner = new();
        public ImplP() => PlainCode.Runs++;
        public LeafP Inner { get { PlainCode.Runs++; return _inner; } set => _inner = value; }
    }

    public sealed class InitImplP : IInitP
    {
        private readonly LeafP _inner = new();
        public InitImplP() => PlainCode.Runs++;
        public LeafP Inner { get { PlainCode.Runs++; return _inner; } init => _inner = value; }
    }

    public struct StructImplP : IGetOnlyP
    {
        private LeafP _inner = new();
        public StructImplP() => PlainCode.Runs++;
        public LeafP Inner { readonly get { PlainCode.Runs++; return _inner; } set => _inner = value; }
    }

    public sealed class GetOnlyImplP : IGetOnlyP
    {
        private readonly LeafP _inner = new();
        public GetOnlyImplP() => PlainCode.Runs++;
        public LeafP Inner { get { PlainCode.Runs++; return _inner; } }
    }

    public sealed class IgnoredImplP : IGetSetP
    {
        private LeafP _inner = new();
        public IgnoredImplP() => PlainCode.Runs++;
        [IgnoreDataMember]
        public LeafP Inner { get { PlainCode.Runs++; return _inner; } set => _inner = value; }
    }

    public sealed class ExplicitImplP : IExplicitP
    {
        private LeafP _inner = new();
        private LeafP _explicit = new();
        public ExplicitImplP() => PlainCode.Runs++;
        public LeafP Inner { get { PlainCode.Runs++; return _inner; } set => _inner = value; }
        LeafP IExplicitP.Inner { get { PlainCode.Runs++; return _explicit; } set => _explicit = value; }
    }

    public sealed class ExplicitOnlyImplP : IExplicitOnlyP
    {
        private LeafP _explicit = new();
        public ExplicitOnlyImplP() => PlainCode.Runs++;
        LeafP IExplicitOnlyP.Inner { get { PlainCode.Runs++; return _explicit; } set => _explicit = value; }
    }

    public sealed class NewOtherImplP : INewOtherP
    {
        private LeafP _inner = new();
        private OtherP _other = new();
        public NewOtherImplP() => PlainCode.Runs++;
        public LeafP Inner { get { PlainCode.Runs++; return _inner; } set => _inner = value; }
        OtherP INewOtherP.Inner { get { PlainCode.Runs++; return _other; } set => _other = value; }
    }

    public sealed class TwoImplP : ITwoMarkedFirstP, ITwoOtherFirstP, ITwoOtherFirstGetOnlyP
    {
        private LeafP _inner = new();
        private OtherP _other = new();
        public TwoImplP() => PlainCode.Runs++;
        public LeafP Inner { get { PlainCode.Runs++; return _inner; } set => _inner = value; }
        OtherP IOtherBaseP.Inner { get { PlainCode.Runs++; return _other; } set => _other = value; }
    }

    public sealed class ItemImplP : IItemP
    {
        private LeafP _item = new();
        public ItemImplP() => PlainCode.Runs++;
        public LeafP Item { get { PlainCode.Runs++; return _item; } set => _item = value; }
        LeafP IItemP.this[int index] => Item;
    }

    public sealed class MidImplP : IMidP
    {
        private LeafP _inner = new();
        public MidImplP() => PlainCode.Runs++;
        public LeafP Inner { get { PlainCode.Runs++; return _inner; } set => _inner = value; }
    }

    public sealed class OuterImplP : IOuterP
    {
        private IMidP _mid = new MidImplP();
        public OuterImplP() => PlainCode.Runs++;
        public IMidP Mid { get { PlainCode.Runs++; return _mid; } set => _mid = value; }
    }

    // ── The catalogue ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One shape: the declared type of <c>Doc</c>, the class a new holder puts there, the path from <c>Doc</c> to the
    /// value, whether that class stores the member (the implementing class decides: the writer writes the public
    /// properties of the runtime class with a getter and a setter and no [IgnoreDataMember], so an explicit
    /// implementation alone, a get-only class property or an ignored one stores nothing, as for every type), and whether
    /// a path expression can be built through the member (it cannot read through a set-only member: no C# lambda
    /// compiles, CS0154, and <see cref="Expression.Property(Expression, PropertyInfo)"/> refuses to read through it; only
    /// a key of <see cref="UpdateBuilder{T}.Updates"/> names it).
    /// </summary>
    public sealed record Shape(string Name, Type Declared, Type Impl, string Path = "Inner_Notes", bool Stored = true, bool Expression = true);

    // The shapes of a family, each type named by its shape and the family's suffix.
    private static Shape[] Catalogue(string family, Type leaf)
    {
        Type Of(string name) => typeof(InterfaceMemberShapeMatrixTests).GetNestedType(name + family)
            ?? throw new InvalidOperationException($"No type {name}{family}.");
        return
        [
            new("get/set", Of("IGetSet"), Of("Impl")),
            new("get-only", Of("IGetOnly"), Of("Impl")),
            new("set-only", Of("ISetOnly"), Of("Impl"), Expression: false),
            new("init", Of("IInit"), Of("InitImpl")),
            new("[IgnoreDataMember]", Of("IIgnored"), Of("Impl")),
            new("get-only, implemented by a get-only property", Of("IGetOnly"), Of("GetOnlyImpl"), Stored: false),
            new("get-only, implemented by a struct", Of("IGetOnly"), Of("StructImpl")),
            new("get/set, implemented by an [IgnoreDataMember] property", Of("IGetSet"), Of("IgnoredImpl"), Stored: false),
            new("a default body (get-only)", Of("IDefaultBody"), Of("Impl")),
            new("an explicit implementation beside a public member", Of("IExplicit"), Of("ExplicitImpl")),
            new("an explicit implementation only", Of("IExplicitOnly"), Of("ExplicitOnlyImpl"), Stored: false),
            new("an explicit implementation in a derived interface", Of("IExplicitInInterface"), Of("Impl")),
            new("re-declared with new and the same type (get-only)", Of("INewSame"), Of("Impl")),
            new("re-declared with new and another type", Of("INewOther"), Of("NewOtherImpl")),
            new("inherited one level", Of("IInherited1"), Of("Impl")),
            new("inherited two levels", Of("IInherited2"), Of("Impl")),
            new("get-only, inherited two levels", Of("IGetOnly2"), Of("Impl")),
            new("get-only, a diamond", Of("IDiamond"), Of("Impl")),
            new("from two bases with the same type", Of("ITwoSame"), Of("Impl")),
            new("from two bases with different types, the leaf's first", Of("ITwoMarkedFirst"), Of("TwoImpl")),
            new("from two bases with different types, the other first", Of("ITwoOtherFirst"), Of("TwoImpl")),
            new("from two bases with different types, the other first and the leaf's get-only", Of("ITwoOtherFirstGetOnly"), Of("TwoImpl")),
            new("generic", typeof(IGen<>).MakeGenericType(leaf), Of("Impl")),
            new("generic, inherited", Of("IGenInherited"), Of("Impl")),
            new("covariant", typeof(IBox<>).MakeGenericType(leaf), Of("Impl")),
            new("covariant, inherited", Of("IBoxInherited"), Of("Impl")),
            new("an own indexer beside an inherited member of the same name and type", Of("IItem"), Of("ItemImpl"), Path: "Item_Notes"),
            new("two interface levels, each get-only", Of("IOuter"), Of("OuterImpl"), Path: "Mid_Inner_Notes"),
        ];
    }

    public static readonly Shape[] FamilyA = Catalogue("A", typeof(LeafA));

    public static readonly Shape[] FamilyT =
    [
        new("get/set, a TableBlobContent below", typeof(IGetSetT), typeof(ImplT), "Inner_File"),
        new("get-only, a TableBlobContent below", typeof(IGetOnlyT), typeof(ImplT), "Inner_File"),
        new("set-only, a TableBlobContent below", typeof(ISetOnlyT), typeof(ImplT), "Inner_File", Expression: false),
        new("[IgnoreDataMember], a TableBlobContent below", typeof(IIgnoredT), typeof(ImplT), "Inner_File"),
        new("a default body (get-only), a TableBlobContent below", typeof(IDefaultBodyT), typeof(ImplT), "Inner_File"),
        new("re-declared with new and the same type (get-only), a TableBlobContent below", typeof(INewSameT), typeof(ImplT), "Inner_File"),
        new("get-only, inherited two levels, a TableBlobContent below", typeof(IGetOnly2T), typeof(ImplT), "Inner_File"),
        new("covariant, a TableBlobContent below", typeof(IBox<BlobLeafT>), typeof(ImplT), "Inner_File"),
        new("from two bases with different types, the other first and the leaf's get-only, a TableBlobContent below", typeof(ITwoOtherFirstGetOnlyT), typeof(TwoImplT), "Inner_File"),
        new("an own indexer beside an inherited member of the same name and type, a TableBlobContent below", typeof(IItemT), typeof(ItemImplT), "Item_File"),
        new("the member typed TableBlobContent, get/set", typeof(IFileGetSetT), typeof(FileImplT), "File"),
        new("the member typed TableBlobContent, get-only", typeof(IFileGetOnlyT), typeof(FileImplT), "File"),
        new("the member typed TableBlobContent, set-only", typeof(IFileSetOnlyT), typeof(FileImplT), "File", Expression: false),
        new("the member typed TableBlobContent, [IgnoreDataMember]", typeof(IFileIgnoredT), typeof(FileImplT), "File"),
        new("the member typed TableBlobContent, get-only, inherited two levels", typeof(IFileGetOnly2T), typeof(FileImplT), "File"),
        new("the member typed TableBlobContent, covariant", typeof(IBox<TableBlobContent>), typeof(BoxFileImplT), "Inner"),
    ];

    public static readonly Shape[] FamilyB =
    [
        .. Catalogue("B", typeof(MarkedB)),
        // A class owner: the member is not a column the writer stores, but it is a property along the updated path.
        new("a get-only member of a class owner", typeof(OwnerGetOnlyB), typeof(OwnerGetOnlyB), Stored: false),
        new("an [IgnoreDataMember] member of a class owner", typeof(OwnerIgnoredB), typeof(OwnerIgnoredB), Stored: false),
    ];

    public static readonly Shape[] FamilyP = Catalogue("P", typeof(LeafP));

    private static readonly string[] Positions = ["top-level", "nested"];

    // The entity type and the column of a shape at a position.
    private static (Type Entity, string Column) Case(Shape shape, string position) => position == "top-level"
        ? (typeof(Top<,>).MakeGenericType(shape.Declared, shape.Impl), $"Doc_{shape.Path}")
        : (typeof(Nested<,>).MakeGenericType(shape.Declared, shape.Impl), $"W_Doc_{shape.Path}");

    private static TheoryData<string, string> ShapesPositions(Shape[] family)
    {
        var d = new TheoryData<string, string>();
        foreach (var s in family)
            foreach (var p in Positions)
                d.Add(s.Name, p);
        return d;
    }

    private static TheoryData<string, string, string, OversizedCellPolicy> ShapesPositionsStoresPolicies(Shape[] family)
    {
        var d = new TheoryData<string, string, string, OversizedCellPolicy>();
        foreach (var s in family)
            foreach (var p in Positions)
                foreach (var k in Kinds)
                    foreach (var policy in Policies)
                        d.Add(s.Name, p, k, policy);
        return d;
    }

    public static TheoryData<string, string> FamilyAShapes() => ShapesPositions(FamilyA);
    public static TheoryData<string, string, string, OversizedCellPolicy> FamilyAShapesStoresPolicies() => ShapesPositionsStoresPolicies(FamilyA);
    public static TheoryData<string, string> FamilyTShapes() => ShapesPositions(FamilyT);
    public static TheoryData<string, string, string, OversizedCellPolicy> FamilyTShapesStoresPolicies() => ShapesPositionsStoresPolicies(FamilyT);
    public static TheoryData<string, string> FamilyBShapes() => ShapesPositions(FamilyB);
    public static TheoryData<string, string, string, OversizedCellPolicy> FamilyBShapesStoresPolicies() => ShapesPositionsStoresPolicies(FamilyB);
    public static TheoryData<string, string> FamilyPShapes() => ShapesPositions(FamilyP);

    // ── Families A and T: awareness and the listed column ──────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(FamilyAShapes))]
    public void CorrectlyMarkedValue_BelowEveryInterfaceMemberShape_IsLargeValueAware_AndListsItsColumnOnce(string shape, string position) =>
        AssertAwareWithColumn(FamilyA, shape, position);

    [Theory]
    [MemberData(nameof(FamilyTShapes))]
    public void TableBlobContent_BelowOrInEveryInterfaceMemberShape_IsLargeValueAware_AndListsItsColumnOnce(string shape, string position) =>
        AssertAwareWithColumn(FamilyT, shape, position);

    private static void AssertAwareWithColumn(Shape[] family, string shape, string position)
    {
        var (entity, column) = Case(family.Single(s => s.Name == shape), position);
        var columns = TypeMetadataCache.LargeValueColumns(entity);
        Assert.True(TypeMetadataCache.IsLargeValueAware(entity) && columns.Count(c => c == column) == 1,
            $"{shape}, {position}: aware={TypeMetadataCache.IsLargeValueAware(entity)}, columns=[{string.Join(", ", columns)}]");
    }

    // ── Families A and T: the whole-entity write stores it, every read returns it, no builder update writes it ─

    [Theory]
    [MemberData(nameof(FamilyAShapesStoresPolicies))]
    public Task CorrectlyMarkedValue_BelowEveryInterfaceMemberShape_IsStoredWhole_AndNoBuilderUpdateWritesIt(string shape, string position, string kind, OversizedCellPolicy policy)
    {
        var s = FamilyA.Single(x => x.Name == shape);
        var (entity, column) = Case(s, position);
        return Invoke(nameof(StoredWholeAsync), entity, $"{shape}, {position}, {kind}, {policy}", column, s.Stored, s.Expression, false, kind, policy);
    }

    [Theory]
    [MemberData(nameof(FamilyTShapesStoresPolicies))]
    public Task TableBlobContent_BelowOrInEveryInterfaceMemberShape_IsStoredWhole_AndNoBuilderUpdateWritesIt(string shape, string position, string kind, OversizedCellPolicy policy)
    {
        var s = FamilyT.Single(x => x.Name == shape);
        var (entity, column) = Case(s, position);
        return Invoke(nameof(StoredWholeAsync), entity, $"{shape}, {position}, {kind}, {policy}", column, s.Stored, s.Expression, true, kind, policy);
    }

    // content: the value is a TableBlobContent (stored as a blob, read back as content whose bytes are compared);
    // otherwise a string of Large characters (split cells).
    private static async Task StoredWholeAsync<T>(string name, string column, bool stored, bool expression, bool content, string kind, OversizedCellPolicy policy)
        where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("a");
        var bytes = Bytes(70_000, name.Length);
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(Large, name.Length);
        var store = stores.Store<T>(Table);
        await WithPolicyAsync(policy, async () =>
        {
            var written = new T { Id = id };
            Set(written, column, content ? TableBlobContent.FromBytes(bytes, "application/octet-stream") : notes);
            var thrown = await Record.ExceptionAsync(() => store.CreateAsync(written, Ct));
            Assert.True(thrown is null, $"{name}: whole-entity write {thrown?.GetType().Name}: {thrown?.Message}");
            foreach (var read in new[] { await store.OneAsync(id, Ct), await store.OneAsync(id, Ct), await stores.Store<T>(Table).OneAsync(id, Ct) })
                await AssertReadBackAsync(name, read!, column, stored, content, bytes, notes);

            foreach (var (source, length) in Sources(expression))
            {
                object update = content
                    ? TableBlobContent.FromBytes(Bytes(length, 7), "application/octet-stream")
                    : BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(length, 7);
                Exception? refused = null;
                var writes = await stores.StorageWritesAsync(store, async () =>
                    refused = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => SetPath(b, source, column, update), Ct)));
                Assert.True(refused is NotSupportedException && writes.Count == 0,
                    $"{name}, builder update ({source}) of {length}: {refused?.GetType().Name ?? "accepted"}: {refused?.Message}; writes: [{string.Join(", ", writes)}]");
            }

            await AssertReadBackAsync(name + ", after the refused builder updates", (await stores.Store<T>(Table).OneAsync(id, Ct))!, column, stored, content, bytes, notes);
            var row = (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "a");
            Assert.False(row.ContainsKey(column) || row.ContainsKey(column + "__GZip") || row.ContainsKey(column + "__Json") || row.ContainsKey(column + "__Truncated"),
                $"{name}: an ordinary cell for {column}: [{string.Join(", ", row.Keys)}]");
            Assert.True(stored || !row.Keys.Any(k => k.StartsWith(column, StringComparison.Ordinal)),
                $"{name}: a cell for a member the class does not store: [{string.Join(", ", row.Keys)}]");
        });
    }

    private static async Task AssertReadBackAsync(string name, object read, string column, bool stored, bool content, byte[] bytes, string notes)
    {
        var value = Get(read, column);
        if (!stored)
        {
            // The class does not store the member (as for every type): a read returns what a new instance holds.
            Assert.True(value is null, $"{name}: read back a value for a member the class does not store");
            return;
        }
        if (!content)
        {
            Assert.True((string?)value == notes, $"{name}: read back {((string?)value)?.Length ?? -1} of {notes.Length} characters");
            return;
        }
        Assert.True(value is TableBlobContent, $"{name}: read back {value?.GetType().Name ?? "null"}, not a TableBlobContent");
        await using var stream = await ((TableBlobContent)value!).OpenReadAsync(Ct);
        Assert.Equal(bytes, await OffloadTestSupport.ReadAllAsync(stream));
    }

    // ── Family B: the misuse opts nothing in, and a whole-entity write reports it where the class stores it ───

    [Theory]
    [MemberData(nameof(FamilyBShapes))]
    public void MisusedAttribute_BelowEveryInterfaceMemberShape_OptsNothingIn_AndAWholeEntityWriteThatMeetsItReportsIt(string shape, string position)
    {
        var s = FamilyB.Single(x => x.Name == shape);
        var (entity, column) = Case(s, position);
        Assert.False(TypeMetadataCache.IsLargeValueAware(entity), $"{shape}, {position}: large-value aware");
        Assert.DoesNotContain(column, TypeMetadataCache.LargeValueColumns(entity));
        var written = (IEntity)Activator.CreateInstance(entity)!;
        written.Id = "a|b";
        Set(written, column, "x");
        var whole = Record.Exception(() => TableEntitySerializer.ToTableEntity(written, "a", "b"));
        // Where the class does not store the member (an explicit implementation only, a get-only or ignored class
        // property), the writer never meets the misused type, as for every type.
        Assert.True(s.Stored
                ? whole is InvalidOperationException && whole.Message.Contains("[TableValueSplit] on IMarked.Notes", StringComparison.Ordinal)
                : whole is null,
            $"{shape}, {position}: whole-entity write {whole?.GetType().Name ?? "accepted"}: {whole?.Message}");
    }

    // ── Family B: every builder update is refused with the misuse, before any request ──────────────────────

    [Theory]
    [MemberData(nameof(FamilyBShapesStoresPolicies))]
    public Task MisusedAttribute_BelowEveryInterfaceMemberShape_IsRefusedOnEveryBuilderUpdate(string shape, string position, string kind, OversizedCellPolicy policy)
    {
        var s = FamilyB.Single(x => x.Name == shape);
        var (entity, column) = Case(s, position);
        return Invoke(nameof(MisuseRefusedAsync), entity, $"{shape}, {position}", column, s.Expression, kind, policy);
    }

    private static async Task MisuseRefusedAsync<T>(string name, string column, bool expression, string kind, OversizedCellPolicy policy) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        await stores.Store<RowWriter>(Table).CreateAsync(new RowWriter { Id = id, Title = "t" }, Ct);
        await stores.ShareTableAsync<RowWriter, T>(Table);
        var store = stores.Store<T>(Table);
        await WithPolicyAsync(policy, async () =>
        {
            foreach (var (source, length) in Sources(expression))
            {
                var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(length, 5);
                Exception? thrown = null;
                var writes = await stores.StorageWritesAsync(store, async () =>
                    thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => SetPath(b, source, column, notes), Ct)));
                var row = (await stores.RawRowsAsync<T>(Table)).Single();
                Assert.True(thrown is InvalidOperationException && writes.Count == 0 && !row.Keys.Any(k => k.Contains("Notes", StringComparison.Ordinal))
                            && thrown.Message.Contains("[TableValueSplit] on IMarked.Notes", StringComparison.Ordinal),
                    BuilderUpdateThroughAnInheritedInterfaceMemberTests.Describe($"{name}, {source}", policy, length, thrown, writes, row));
            }
        });
    }

    // ── Family P (rule 2): an ordinary value keeps the plain path, runs no user code, gives the same cells ────

    [Theory]
    [MemberData(nameof(FamilyPShapes))]
    public void OrdinaryValue_BelowEveryInterfaceMemberShape_KeepsThePlainPath_AndRunsNoUserCode(string shape, string position)
    {
        var s = FamilyP.Single(x => x.Name == shape);
        var (entity, column) = Case(s, position);
        PlainCode.Runs = 0;
        var aware = TypeMetadataCache.IsLargeValueAware(entity);
        var columns = TypeMetadataCache.LargeValueColumns(entity);
        var misuse = Record.Exception(() => TypeMetadataCache.RefuseInterfaceMemberSplitOnPath(entity, column));
        var cells = LargeValueRows.FlattenPropertyForUpdate(entity, column, "n");
        var runs = PlainCode.Runs;
        Assert.True(!aware && columns.Count == 0 && misuse is null && runs == 0 && cells.Keys.SequenceEqual([column]) && Equals(cells[column], "n"),
            $"{shape}, {position}: aware={aware}, columns=[{string.Join(", ", columns)}], misuse={misuse?.GetType().Name}, user code runs={runs}, " +
            $"cells=[{string.Join(", ", cells.Keys)}]");

        // The whole-entity write of the plain serializer: the member's value as one ordinary cell (none where the class
        // does not store the member).
        var written = (IEntity)Activator.CreateInstance(entity)!;
        written.Id = "a|b";
        Set(written, column, "n");
        var row = TableEntitySerializer.ToTableEntity(written, "a", "b");
        Assert.Equal(s.Stored ? "n" : null, row.TryGetValue(column, out var cell) ? cell : null);
        Assert.DoesNotContain(row.Keys, k => k.StartsWith(column + "__", StringComparison.Ordinal));
    }

    // ── The plain-store refusal messages ───────────────────────────────────────────────────────────────────

    // The member is not on the interface at all (only the implementing class has it): the declared types reach no
    // large-value member, the type is not aware, and the refusal names the declaration that is missing (Doc.Inner with
    // the type that carries the member); either way of declaring it, followed, stores the value and every read returns
    // it: the property declared as the class, or the member added to the interface (here get-only, a read-only
    // contract, which counts as every member of an interface does).
    public interface IBare { string? Title { get; set; } }

    public interface IBareWithInner { string? Title { get; set; } LeafA Inner { get; } }

    public sealed class BareImpl : IBare, IBareWithInner
    {
        public string? Title { get; set; }
        public LeafA Inner { get; set; } = new();
    }

    public sealed class BareHolder : Entity
    {
        public IBare Doc { get; set; } = new BareImpl();
    }

    public sealed class BareHolderDeclared : Entity
    {
        public BareImpl Doc { get; set; } = new();
    }

    public sealed class BareHolderInterfaceDeclared : Entity
    {
        public IBareWithInner Doc { get; set; } = new BareImpl();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task APlainStoreRefusal_NamesADeclarationThatIsNotInPlace_AndFollowingItStoresTheValue(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(BareHolder)));
        var refused = await Record.ExceptionAsync(() => stores.Store<BareHolder>(Table).CreateAsync(
            new BareHolder { Id = stores.Id("p"), Doc = new BareImpl { Inner = new LeafA { Notes = "x" } } }, Ct));
        Assert.True(refused is NotSupportedException && refused.Message.Contains("declare BareHolder.Doc.Inner with the type that carries the member (LeafA)", StringComparison.Ordinal),
            $"{refused?.GetType().Name}: {refused?.Message}");
        Assert.Empty(await stores.RawRowsAsync<BareHolder>(Table));

        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(Large, 3);
        await stores.Store<BareHolderDeclared>(Table).CreateAsync(new BareHolderDeclared { Id = stores.Id("d"), Doc = new BareImpl { Inner = new LeafA { Notes = notes } } }, Ct);
        Assert.Equal(notes, (await stores.Store<BareHolderDeclared>(Table).OneAsync(stores.Id("d"), Ct))!.Doc.Inner.Notes);
        await stores.Store<BareHolderInterfaceDeclared>(Table).CreateAsync(
            new BareHolderInterfaceDeclared { Id = stores.Id("i"), Doc = new BareImpl { Inner = new LeafA { Notes = notes } } }, Ct);
        Assert.Equal(notes, (await stores.Store<BareHolderInterfaceDeclared>(Table).OneAsync(stores.Id("i"), Ct))!.Doc.Inner.Notes);
    }

    // A nullable struct below the member, for the member shapes whose walk the message takes: the refusal names the
    // nullable property on the path (the interface the path passes through), and the declaration it names, followed,
    // makes the type aware: the value is stored and read back.
    public struct SMarked
    {
        public SMarked()
        {
        }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public interface IBoxExtra<out T> { T Extra { get; } }

    public interface INsGetOnly { SMarked? Extra { get; } }
    public interface INsSetOnly { SMarked? Extra { set; } }
    public interface INsIgnored { [IgnoreDataMember] SMarked? Extra { get; set; } }
    public interface INsGetOnly1 : INsGetOnly { }
    public interface INsGetOnly2 : INsGetOnly1 { }
    public interface INsOther { string? Extra { get; set; } }
    public interface INsTwo : INsOther, INsGetOnly { }

    public sealed class NsImpl : INsGetOnly2, INsSetOnly, INsIgnored, IBoxExtra<SMarked?> { public SMarked? Extra { get; set; } }

    public sealed class NsTwoImpl : INsTwo
    {
        public SMarked? Extra { get; set; }
        string? INsOther.Extra { get; set; }
    }

    public interface ISGetOnly { SMarked Extra { get; } }
    public interface ISSetOnly { SMarked Extra { set; } }
    public interface ISIgnored { [IgnoreDataMember] SMarked Extra { get; set; } }
    public interface ISGetOnly1 : ISGetOnly { }
    public interface ISGetOnly2 : ISGetOnly1 { }
    public interface ISTwo : INsOther, ISGetOnly { }

    public sealed class SImpl : ISGetOnly2, ISSetOnly, ISIgnored, IBoxExtra<SMarked> { public SMarked Extra { get; set; } }

    public sealed class STwoImpl : ISTwo
    {
        public SMarked Extra { get; set; }
        string? INsOther.Extra { get; set; }
    }

    public static readonly (string Name, Type Nullable, Type NullableImpl, string Owner, Type Declared, Type DeclaredImpl)[] NullableShapes =
    [
        ("get-only", typeof(INsGetOnly), typeof(NsImpl), "INsGetOnly", typeof(ISGetOnly), typeof(SImpl)),
        ("set-only", typeof(INsSetOnly), typeof(NsImpl), "INsSetOnly", typeof(ISSetOnly), typeof(SImpl)),
        ("[IgnoreDataMember]", typeof(INsIgnored), typeof(NsImpl), "INsIgnored", typeof(ISIgnored), typeof(SImpl)),
        ("get-only, inherited two levels", typeof(INsGetOnly2), typeof(NsImpl), "INsGetOnly2", typeof(ISGetOnly2), typeof(SImpl)),
        ("covariant", typeof(IBoxExtra<SMarked?>), typeof(NsImpl), "IBoxExtra`1", typeof(IBoxExtra<SMarked>), typeof(SImpl)),
        ("from two bases with different types, the other first", typeof(INsTwo), typeof(NsTwoImpl), "INsTwo", typeof(ISTwo), typeof(STwoImpl)),
    ];

    public static TheoryData<string, string> NullableShapesStores()
    {
        var d = new TheoryData<string, string>();
        foreach (var s in NullableShapes)
            foreach (var k in Kinds)
                d.Add(s.Name, k);
        return d;
    }

    [Theory]
    [MemberData(nameof(NullableShapesStores))]
    public Task APlainStoreRefusal_BelowANullableStruct_NamesTheNullableProperty_AndTheNamedDeclarationWorks(string shape, string kind)
    {
        var s = NullableShapes.Single(x => x.Name == shape);
        return (Task)typeof(InterfaceMemberShapeMatrixTests).GetMethod(nameof(NullableRefusalAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(Top<,>).MakeGenericType(s.Nullable, s.NullableImpl), typeof(Top<,>).MakeGenericType(s.Declared, s.DeclaredImpl))
            .Invoke(null, [shape, s.Owner, kind])!;
    }

    private static async Task NullableRefusalAsync<TNullable, TDeclared>(string shape, string owner, string kind)
        where TNullable : class, IEntity, new()
        where TDeclared : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(TNullable)), $"{shape}: the nullable form is large-value aware");
        var refusedEntity = new TNullable { Id = stores.Id("n") };
        Set(refusedEntity, "Doc_Extra", new SMarked { Notes = "x" });
        var refused = await Record.ExceptionAsync(() => stores.Store<TNullable>(Table).CreateAsync(refusedEntity, Ct));
        Assert.True(refused is NotSupportedException && refused.Message.Contains($"Declare {owner}.Extra as SMarked, not nullable", StringComparison.Ordinal),
            $"{shape}: {refused?.GetType().Name}: {refused?.Message}");
        Assert.Empty(await stores.RawRowsAsync<TNullable>(Table));

        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(TDeclared)), $"{shape}: the declaration the message names does not make the type aware");
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(40_000, shape.Length);
        var stored = new TDeclared { Id = stores.Id("d") };
        Set(stored, "Doc_Extra", new SMarked { Notes = notes });
        await stores.Store<TDeclared>(Table).CreateAsync(stored, Ct);
        var read = (await stores.Store<TDeclared>(Table).OneAsync(stores.Id("d"), Ct))!;
        Assert.Equal(notes, ((SMarked)Get(read, "Doc_Extra")!).Notes);
    }

    // The interface-typed property holds no instance on a new entity: the entity is aware by its declared types (the
    // member below counts, whatever its accessors), and a value that needs split cells is refused because a read
    // cannot create the interface (ruling R15); the refusal names remedies that are not in place, and the first,
    // followed (an instance in the constructor: the holders of family A), stores it and reads it back (the matrix).
    public sealed class NoInstance<TDoc> : Entity
    {
        public TDoc? Doc { get; set; }
    }

    public static TheoryData<string, string> NoInstanceShapesStores()
    {
        var d = new TheoryData<string, string>();
        foreach (var s in new[] { "get-only", "set-only", "[IgnoreDataMember]", "get-only, inherited two levels", "covariant" })
            foreach (var k in Kinds)
                d.Add(s, k);
        return d;
    }

    [Theory]
    [MemberData(nameof(NoInstanceShapesStores))]
    public Task AValueBelowAnInterfaceTypedPropertyWithNoInstance_IsRefused_NamingRemediesThatAreNotInPlace(string shape, string kind)
    {
        var s = FamilyA.Single(x => x.Name == shape);
        return (Task)typeof(InterfaceMemberShapeMatrixTests).GetMethod(nameof(NoInstanceRefusedAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(NoInstance<>).MakeGenericType(s.Declared), s.Impl)
            .Invoke(null, [shape, kind])!;
    }

    private static async Task NoInstanceRefusedAsync<T, TImpl>(string shape, string kind) where T : class, IEntity, new() where TImpl : new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(T)), $"{shape}: not large-value aware");
        var entity = new T { Id = stores.Id("x") };
        typeof(T).GetProperty("Doc")!.SetValue(entity, new TImpl());
        Set(entity, "Doc_Inner_Notes", BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(Large, 4));
        var refused = await Record.ExceptionAsync(() => stores.Store<T>(Table).CreateAsync(entity, Ct));
        Assert.True(refused is NotSupportedException && refused.Message.Contains("initialise the property with an instance in the constructor", StringComparison.Ordinal),
            $"{shape}: {refused?.GetType().Name}: {refused?.Message}");
        Assert.Empty(await stores.RawRowsAsync<T>(Table));
        Assert.Empty(await stores.BlobNamesAsync<T>(Table));
    }

    // ── Class owners keep the writer's rule for what is a column ────────────────────────────────────────────

    public sealed class OwnerGetOnlyA { public LeafA Inner { get; } = new(); }
    public sealed class OwnerIgnoredA { [IgnoreDataMember] public LeafA Inner { get; set; } = new(); }
    public sealed class OwnerSetOnlyA { public LeafA Inner { set { } } }

    // A class owner's get-only, [IgnoreDataMember] or set-only property is no column the writer stores, so a correctly
    // marked value below it makes no type aware (the writer never writes it); the interface rule applies to interface
    // owners only.
    [Fact]
    public void ClassOwners_KeepTheWritersRule()
    {
        foreach (var owner in new[] { typeof(OwnerGetOnlyA), typeof(OwnerIgnoredA), typeof(OwnerSetOnlyA) })
        {
            var entity = typeof(Top<,>).MakeGenericType(owner, owner);
            Assert.False(TypeMetadataCache.IsLargeValueAware(entity), $"{owner.Name}: large-value aware");
            Assert.Empty(TypeMetadataCache.LargeValueColumns(entity));
        }
    }

    public static TheoryData<string> Stores() => [.. Kinds];

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────────

    private static Task Invoke(string method, Type entity, params object[] args) =>
        (Task)typeof(InterfaceMemberShapeMatrixTests).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(entity).Invoke(null, args)!;

    private static async Task WithPolicyAsync(OversizedCellPolicy policy, Func<Task> action)
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        try
        {
            await action();
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // The ways a builder update names the column, at a size that fits one cell and one that does not.
    private static (string Source, int Length)[] Sources(bool expression) => expression
        ? [("expression", 10), ("expression", Large), ("Updates", 10), ("Updates", Large)]
        : [("Updates", 10), ("Updates", Large)];

    // The builder update of the column: an expression built through the declared members (as a lambda names them; a
    // set-only member, which no lambda can name, only through the Updates dictionary), or the key of the public
    // Updates dictionary.
    private static void SetPath<T>(UpdateBuilder<T> builder, string source, string column, object value)
    {
        if (source == "Updates")
            builder.Updates[column] = value;
        else
            builder.SetProperty(PathOf<T>(column), value);
    }

    // x => (object)x.A.B.C for the column "A_B_C" (the builder takes the member path below the conversion).
    private static Expression<Func<T, object>> PathOf<T>(string column)
    {
        var x = Expression.Parameter(typeof(T), "x");
        Expression body = x;
        foreach (var segment in column.Split('_'))
            body = Expression.Property(body, Member(body.Type, segment));
        return Expression.Lambda<Func<T, object>>(Expression.Convert(body, typeof(object)), x);
    }

    // The member a path through the declared type names, as the C# compiler resolves it: the type's own member first,
    // then the first interface it inherits that has one. Never an indexer.
    private static PropertyInfo Member(Type type, string name) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0)
        ?? type.GetInterfaces().SelectMany(i => i.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .First(p => p.Name == name && p.GetIndexParameters().Length == 0);

    // The object that owns the last segment of the column, and the property there: each segment through the public
    // property of the runtime object (the one the writer stores), or through the declared member where the runtime
    // class has no public one (an explicit implementation).
    private static (object Owner, PropertyInfo Leaf) Locate(object entity, string column)
    {
        var owner = entity;
        var declared = entity.GetType();
        var segments = column.Split('_');
        for (var i = 0; ; i++)
        {
            var declaredMember = Member(declared, segments[i]);
            var member = owner.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name == segments[i] && p.GetIndexParameters().Length == 0 && p.CanRead) ?? declaredMember;
            if (i == segments.Length - 1)
                return (owner, member);
            owner = member.GetValue(owner)!;
            declared = declaredMember.PropertyType;
        }
    }

    private static void Set(object entity, string column, object value)
    {
        var (owner, leaf) = Locate(entity, column);
        leaf.SetValue(owner, value);
    }

    private static object? Get(object entity, string column)
    {
        var (owner, leaf) = Locate(entity, column);
        return leaf.GetValue(owner);
    }
}
