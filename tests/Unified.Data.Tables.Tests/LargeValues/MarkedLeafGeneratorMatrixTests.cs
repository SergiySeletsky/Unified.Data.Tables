using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using Azure;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The generator of fix wave 8 (rules A, B and C; RV7 SERIALIZER-1, OBSERVED-1, OBSERVED-2): entity types built from
/// the cross product of
/// <list type="bullet">
/// <item>owner kinds: a class, a sealed class, an abstract class, a struct and an interface, each generic over the leaf
/// (<c>Owner&lt;TLeaf&gt;</c>) and not generic (the types whose names end in <c>N</c>, over the string leaf);</item>
/// <item>member shapes of the owner's member <c>Inner</c>, as far as C# lets each kind have them (the catalogue below);</item>
/// <item>holder kinds: the entity's property <c>Doc</c> declared as the owner type, as a base class (<see cref="EmptyBase"/>;
/// for a struct <see cref="ValueType"/>), as an interface (<see cref="IEmpty"/>) or as <see cref="object"/>, the last three
/// declaring nothing, each initialised with the owner's object on a new entity or empty;</item>
/// <item>leaf kinds below <c>Inner</c>: a <c>[TableValueSplit]</c> string, <c>byte[]</c> or JSON-stored object, a
/// <see cref="TableBlobContent"/>, a string a class-level <c>[TableValueSplit]</c> on the leaf's class covers, and (rule B)
/// a <c>[TableValueSplit]</c> member declared as a type stored as one JSON value holding a subclass stored as columns,
/// which is never stored.</item>
/// </list>
/// Every combination, on both stores, under every <see cref="OversizedCellPolicy"/>, at 10 and 200,000 characters (bytes):
/// a builder update of <c>Doc_Inner_V</c> by lambda (where the declared types have the member) and by a key of
/// <see cref="UpdateBuilder{T}.Updates"/>, and a whole-entity write. The outcomes allowed: refused before anything is
/// stored, or stored with every read (point read, after a cache hit, from a new store, query) returning the value
/// whole; a builder update whose path the declared types name is refused with <see cref="NotSupportedException"/>
/// (rule A). The documented exceptions, each asserted as documented:
/// <list type="number">
/// <item>The runtime-subtype shape (the holder declared as a type that does not show the member; the entity is then not
/// large-value aware): a key of <c>Updates</c> writes an ordinary cell, exactly as the plain serializer of 0.8.3 flattens
/// it; a whole-entity write is refused by the plain store.</item>
/// <item>A member the object's class does not store (a get-only or <c>[IgnoreDataMember]</c> property, an explicit
/// implementation only, the writer's rule since 0.8.3): nothing of the value is stored, every read returns no value.</item>
/// <item>A holder declared as an abstract class or an interface and empty on a new entity: a read cannot create the
/// object, so a value that needs split cells or a blob is refused, and one of one cell is an ordinary cell that every read
/// fails on with <see cref="SerializationException"/> (as on 0.8.3).</item>
/// <item>A struct holder declared as itself, holding the leaf's class: every read of a value below it fails with
/// <see cref="SerializationException"/> (OLDER-1 of the review of fix wave 7, identical on 0.8.3: the row is intact).</item>
/// </list>
/// </summary>
[Collection("OversizedCellPolicy")]
public class MarkedLeafGeneratorMatrixTests
{
    private const string Table = "MarkedLeafMatrix";
    private const string Column = "Doc_Inner_V";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OversizedCellPolicy[] Policies =
        [OversizedCellPolicy.Throw, OversizedCellPolicy.TrimSilently, OversizedCellPolicy.TrimWithMarker];

    private static readonly int[] Sizes = [10, 200_000];

    // ── Leaf kinds ────────────────────────────────────────────────────────────────────────────────────────

    public sealed class LeafStr { [TableValueSplit(SplitEncoding.Text)] public string? V { get; set; } }

    public sealed class LeafBytes { [TableValueSplit(SplitEncoding.Text)] public byte[]? V { get; set; } }

    public sealed record JsonBox(string Text);

    public sealed class LeafJson { [TableValueSplit(SplitEncoding.Text)] public JsonBox? V { get; set; } }

    public sealed class LeafBlob { public TableBlobContent? V { get; set; } }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class LeafClassLevel { public string? V { get; set; } }

    // Rule B: the member is declared as a type stored as one JSON value by its constructors, and holds a subclass the
    // serializer stores as columns: never stored, wherever it is.
    public class JsonFlatBase
    {
        public JsonFlatBase(int seed) => Seed = seed;

        public int Seed { get; set; }

        public string? Body { get; set; }
    }

    public sealed class JsonFlatSub : JsonFlatBase
    {
        public JsonFlatSub()
            : base(0)
        {
        }
    }

    public sealed class LeafJsonAsColumns { [TableValueSplit(SplitEncoding.Text)] public JsonFlatBase? V { get; set; } }

    // ── What a holder can be declared as without showing the member; the type a `new` re-declaration has ─────

    public class EmptyBase { }

    public interface IEmpty { }

    public sealed class Other { public string? V { get; set; } }

    // ── Holders ───────────────────────────────────────────────────────────────────────────────────────────

    public sealed class Initialised<TDecl, TRuntime> : Entity where TRuntime : TDecl, new()
    {
        public TDecl Doc { get; set; } = new TRuntime();
    }

    public sealed class Empty<TDecl> : Entity
    {
        public TDecl? Doc { get; set; }
    }

    public sealed class RowWriter : Entity
    {
        public string? Title { get; set; }
    }

    // ── Owners, generic over the leaf (the ...N types below mirror them over the string leaf) ─────────
    // <generic-owners>

    public class CGetSet<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; set; } = new(); }
    public class CGetOnly<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; } = new(); }
    public class CInit<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; init; } = new(); }
    public class CPrivateSet<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; private set; } = new(); }
    public class CIgnored<TL> : EmptyBase, IEmpty where TL : new() { [IgnoreDataMember] public TL Inner { get; set; } = new(); }
    public class CVirtual<TL> : EmptyBase, IEmpty where TL : new() { public virtual TL Inner { get; set; } = new(); }
    public class CVirtualIgnored<TL> : EmptyBase, IEmpty where TL : new() { [IgnoreDataMember] public virtual TL Inner { get; set; } = new(); }
    public sealed class CVirtualIgnoredOverride<TL> : CVirtualIgnored<TL> where TL : new() { public override TL Inner { get; set; } = new(); }
    public class COverride<TL> : CVirtual<TL> where TL : new() { public override TL Inner { get; set; } = new(); }
    public class CNewSame<TL> : CGetOnly<TL> where TL : new() { public new TL Inner { get; set; } = new(); }
    public class CNewOther<TL> : CGetSet<TL> where TL : new() { public new Other Inner { get; set; } = new(); }
    public class CInherited1<TL> : CGetSet<TL> where TL : new() { }
    public class CInherited2<TL> : CInherited1<TL> where TL : new() { }
    public class CJson<TL> : EmptyBase, IEmpty where TL : new() { public CJson(int seed) => Seed = seed; public int Seed { get; set; } public TL Inner { get; set; } = new(); }
    public sealed class CJsonSub<TL> : CJson<TL> where TL : new() { public CJsonSub() : base(0) { } }

    public sealed class SGetSet<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; set; } = new(); }
    public sealed class SGetOnly<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; } = new(); }
    public sealed class SInit<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; init; } = new(); }
    public sealed class SPrivateSet<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; private set; } = new(); }
    public sealed class SIgnored<TL> : EmptyBase, IEmpty where TL : new() { [IgnoreDataMember] public TL Inner { get; set; } = new(); }
    public sealed class SOverride<TL> : CVirtual<TL> where TL : new() { public override TL Inner { get; set; } = new(); }
    public sealed class SNewSame<TL> : CGetOnly<TL> where TL : new() { public new TL Inner { get; set; } = new(); }
    public sealed class SNewOther<TL> : CGetSet<TL> where TL : new() { public new Other Inner { get; set; } = new(); }
    public sealed class SInherited1<TL> : CGetSet<TL> where TL : new() { }
    public sealed class SInherited2<TL> : CInherited1<TL> where TL : new() { }

    public abstract class AGetSet<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; set; } = new(); }
    public sealed class AGetSetSub<TL> : AGetSet<TL> where TL : new() { }
    public abstract class AGetOnly<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; } = new(); }
    public sealed class AGetOnlySub<TL> : AGetOnly<TL> where TL : new() { }
    public abstract class AInit<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; init; } = new(); }
    public sealed class AInitSub<TL> : AInit<TL> where TL : new() { }
    public abstract class APrivateSet<TL> : EmptyBase, IEmpty where TL : new() { public TL Inner { get; private set; } = new(); }
    public sealed class APrivateSetSub<TL> : APrivateSet<TL> where TL : new() { }
    public abstract class AIgnored<TL> : EmptyBase, IEmpty where TL : new() { [IgnoreDataMember] public TL Inner { get; set; } = new(); }
    public sealed class AIgnoredSub<TL> : AIgnored<TL> where TL : new() { }
    public abstract class AAbstract<TL> : EmptyBase, IEmpty where TL : new() { public abstract TL Inner { get; set; } }
    public sealed class AAbstractImpl<TL> : AAbstract<TL> where TL : new() { public override TL Inner { get; set; } = new(); }
    public abstract class AAbstractIgnored<TL> : EmptyBase, IEmpty where TL : new() { [IgnoreDataMember] public abstract TL Inner { get; set; } }
    public sealed class AAbstractIgnoredImpl<TL> : AAbstractIgnored<TL> where TL : new() { public override TL Inner { get; set; } = new(); }
    public abstract class AVirtual<TL> : EmptyBase, IEmpty where TL : new() { public virtual TL Inner { get; set; } = new(); }
    public sealed class AVirtualSub<TL> : AVirtual<TL> where TL : new() { }
    public abstract class AVirtualIgnored<TL> : EmptyBase, IEmpty where TL : new() { [IgnoreDataMember] public virtual TL Inner { get; set; } = new(); }
    public sealed class AVirtualIgnoredOverride<TL> : AVirtualIgnored<TL> where TL : new() { public override TL Inner { get; set; } = new(); }
    public abstract class AOverride<TL> : CVirtual<TL> where TL : new() { public override TL Inner { get; set; } = new(); }
    public sealed class AOverrideSub<TL> : AOverride<TL> where TL : new() { }
    public abstract class ANewSame<TL> : CGetOnly<TL> where TL : new() { public new TL Inner { get; set; } = new(); }
    public sealed class ANewSameSub<TL> : ANewSame<TL> where TL : new() { }
    public sealed class ANewSetterSub<TL> : AGetOnly<TL> where TL : new() { public new TL Inner { get; set; } = new(); }
    public abstract class ANewOther<TL> : CGetSet<TL> where TL : new() { public new Other Inner { get; set; } = new(); }
    public sealed class ANewOtherSub<TL> : ANewOther<TL> where TL : new() { }
    public abstract class AInherited1<TL> : AGetSet<TL> where TL : new() { }
    public sealed class AInherited1Sub<TL> : AInherited1<TL> where TL : new() { }
    public abstract class AInherited2<TL> : AInherited1<TL> where TL : new() { }
    public sealed class AInherited2Sub<TL> : AInherited2<TL> where TL : new() { }

    public struct VGetSet<TL> : IEmpty where TL : new() { public VGetSet() { } public TL Inner { get; set; } = new(); }
    public struct VGetOnly<TL> : IEmpty where TL : new() { public VGetOnly() { } public TL Inner { get; } = new(); }
    public struct VInit<TL> : IEmpty where TL : new() { public VInit() { } public TL Inner { get; init; } = new(); }
    public struct VPrivateSet<TL> : IEmpty where TL : new() { public VPrivateSet() { } public TL Inner { get; private set; } = new(); }
    public struct VIgnored<TL> : IEmpty where TL : new() { public VIgnored() { } [IgnoreDataMember] public TL Inner { get; set; } = new(); }

    public interface IGetSet<TL> { TL Inner { get; set; } }
    public interface IGetOnly<TL> { TL Inner { get; } }
    public interface IInit<TL> { TL Inner { get; init; } }
    public interface IIgnored<TL> { [IgnoreDataMember] TL Inner { get; set; } }
    public interface IDefaultBody<TL> where TL : new() { TL Inner => new(); }
    public interface IExplicitInDerived<TL> : IGetSet<TL> where TL : new() { TL IGetSet<TL>.Inner { get => new(); set { } } }
    public interface INewSame<TL> : IGetSet<TL> { new TL Inner { get; } }
    public interface INewOther<TL> : IGetSet<TL> { new Other Inner { get; set; } }
    public interface IExplicit<TL> { TL Inner { get; set; } }
    public interface IExplicitBeside<TL> { TL Inner { get; set; } }
    public interface IInherited1<TL> : IGetSet<TL> { }
    public interface IInherited2<TL> : IInherited1<TL> { }
    public interface IGetSetSecond<TL> { TL Inner { get; set; } }
    public interface ITwoBases<TL> : IGetSet<TL>, IGetSetSecond<TL> { }
    public sealed class IImpl<TL> : EmptyBase, IEmpty, IGetSet<TL>, IGetOnly<TL>, IIgnored<TL>, IDefaultBody<TL>, IExplicitInDerived<TL>, INewSame<TL>, IInherited2<TL>, ITwoBases<TL> where TL : new() { public TL Inner { get; set; } = new(); }
    public sealed class IInitImpl<TL> : EmptyBase, IEmpty, IInit<TL> where TL : new() { public TL Inner { get; init; } = new(); }
    public sealed class INewOtherImpl<TL> : EmptyBase, IEmpty, INewOther<TL> where TL : new() { public TL Inner { get; set; } = new(); Other INewOther<TL>.Inner { get; set; } = new(); }
    public sealed class IExplicitImpl<TL> : EmptyBase, IEmpty, IExplicit<TL> where TL : new() { TL IExplicit<TL>.Inner { get; set; } = new(); }
    public sealed class IExplicitBesideImpl<TL> : EmptyBase, IEmpty, IExplicitBeside<TL> where TL : new() { public TL Inner { get; set; } = new(); TL IExplicitBeside<TL>.Inner { get; set; } = new(); }
    public struct IStructImpl<TL> : IEmpty, IGetSet<TL> where TL : new() { public IStructImpl() { } public TL Inner { get; set; } = new(); }

    // </generic-owners>

    // ── The same owners, not generic, over the string leaf (generated from the block above: TLeaf -> LeafStr) ─
    // <non-generic-owners>

    public class CGetSetN : EmptyBase, IEmpty { public LeafStr Inner { get; set; } = new(); }
    public class CGetOnlyN : EmptyBase, IEmpty { public LeafStr Inner { get; } = new(); }
    public class CInitN : EmptyBase, IEmpty { public LeafStr Inner { get; init; } = new(); }
    public class CPrivateSetN : EmptyBase, IEmpty { public LeafStr Inner { get; private set; } = new(); }
    public class CIgnoredN : EmptyBase, IEmpty { [IgnoreDataMember] public LeafStr Inner { get; set; } = new(); }
    public class CVirtualN : EmptyBase, IEmpty { public virtual LeafStr Inner { get; set; } = new(); }
    public class CVirtualIgnoredN : EmptyBase, IEmpty { [IgnoreDataMember] public virtual LeafStr Inner { get; set; } = new(); }
    public sealed class CVirtualIgnoredOverrideN : CVirtualIgnoredN { public override LeafStr Inner { get; set; } = new(); }
    public class COverrideN : CVirtualN { public override LeafStr Inner { get; set; } = new(); }
    public class CNewSameN : CGetOnlyN { public new LeafStr Inner { get; set; } = new(); }
    public class CNewOtherN : CGetSetN { public new Other Inner { get; set; } = new(); }
    public class CInherited1N : CGetSetN { }
    public class CInherited2N : CInherited1N { }
    public class CJsonN : EmptyBase, IEmpty { public CJsonN(int seed) => Seed = seed; public int Seed { get; set; } public LeafStr Inner { get; set; } = new(); }
    public sealed class CJsonSubN : CJsonN { public CJsonSubN() : base(0) { } }

    public sealed class SGetSetN : EmptyBase, IEmpty { public LeafStr Inner { get; set; } = new(); }
    public sealed class SGetOnlyN : EmptyBase, IEmpty { public LeafStr Inner { get; } = new(); }
    public sealed class SInitN : EmptyBase, IEmpty { public LeafStr Inner { get; init; } = new(); }
    public sealed class SPrivateSetN : EmptyBase, IEmpty { public LeafStr Inner { get; private set; } = new(); }
    public sealed class SIgnoredN : EmptyBase, IEmpty { [IgnoreDataMember] public LeafStr Inner { get; set; } = new(); }
    public sealed class SOverrideN : CVirtualN { public override LeafStr Inner { get; set; } = new(); }
    public sealed class SNewSameN : CGetOnlyN { public new LeafStr Inner { get; set; } = new(); }
    public sealed class SNewOtherN : CGetSetN { public new Other Inner { get; set; } = new(); }
    public sealed class SInherited1N : CGetSetN { }
    public sealed class SInherited2N : CInherited1N { }

    public abstract class AGetSetN : EmptyBase, IEmpty { public LeafStr Inner { get; set; } = new(); }
    public sealed class AGetSetSubN : AGetSetN { }
    public abstract class AGetOnlyN : EmptyBase, IEmpty { public LeafStr Inner { get; } = new(); }
    public sealed class AGetOnlySubN : AGetOnlyN { }
    public abstract class AInitN : EmptyBase, IEmpty { public LeafStr Inner { get; init; } = new(); }
    public sealed class AInitSubN : AInitN { }
    public abstract class APrivateSetN : EmptyBase, IEmpty { public LeafStr Inner { get; private set; } = new(); }
    public sealed class APrivateSetSubN : APrivateSetN { }
    public abstract class AIgnoredN : EmptyBase, IEmpty { [IgnoreDataMember] public LeafStr Inner { get; set; } = new(); }
    public sealed class AIgnoredSubN : AIgnoredN { }
    public abstract class AAbstractN : EmptyBase, IEmpty { public abstract LeafStr Inner { get; set; } }
    public sealed class AAbstractImplN : AAbstractN { public override LeafStr Inner { get; set; } = new(); }
    public abstract class AAbstractIgnoredN : EmptyBase, IEmpty { [IgnoreDataMember] public abstract LeafStr Inner { get; set; } }
    public sealed class AAbstractIgnoredImplN : AAbstractIgnoredN { public override LeafStr Inner { get; set; } = new(); }
    public abstract class AVirtualN : EmptyBase, IEmpty { public virtual LeafStr Inner { get; set; } = new(); }
    public sealed class AVirtualSubN : AVirtualN { }
    public abstract class AVirtualIgnoredN : EmptyBase, IEmpty { [IgnoreDataMember] public virtual LeafStr Inner { get; set; } = new(); }
    public sealed class AVirtualIgnoredOverrideN : AVirtualIgnoredN { public override LeafStr Inner { get; set; } = new(); }
    public abstract class AOverrideN : CVirtualN { public override LeafStr Inner { get; set; } = new(); }
    public sealed class AOverrideSubN : AOverrideN { }
    public abstract class ANewSameN : CGetOnlyN { public new LeafStr Inner { get; set; } = new(); }
    public sealed class ANewSameSubN : ANewSameN { }
    public sealed class ANewSetterSubN : AGetOnlyN { public new LeafStr Inner { get; set; } = new(); }
    public abstract class ANewOtherN : CGetSetN { public new Other Inner { get; set; } = new(); }
    public sealed class ANewOtherSubN : ANewOtherN { }
    public abstract class AInherited1N : AGetSetN { }
    public sealed class AInherited1SubN : AInherited1N { }
    public abstract class AInherited2N : AInherited1N { }
    public sealed class AInherited2SubN : AInherited2N { }

    public struct VGetSetN : IEmpty { public VGetSetN() { } public LeafStr Inner { get; set; } = new(); }
    public struct VGetOnlyN : IEmpty { public VGetOnlyN() { } public LeafStr Inner { get; } = new(); }
    public struct VInitN : IEmpty { public VInitN() { } public LeafStr Inner { get; init; } = new(); }
    public struct VPrivateSetN : IEmpty { public VPrivateSetN() { } public LeafStr Inner { get; private set; } = new(); }
    public struct VIgnoredN : IEmpty { public VIgnoredN() { } [IgnoreDataMember] public LeafStr Inner { get; set; } = new(); }

    public interface IGetSetN { LeafStr Inner { get; set; } }
    public interface IGetOnlyN { LeafStr Inner { get; } }
    public interface IInitN { LeafStr Inner { get; init; } }
    public interface IIgnoredN { [IgnoreDataMember] LeafStr Inner { get; set; } }
    public interface IDefaultBodyN { LeafStr Inner => new(); }
    public interface IExplicitInDerivedN : IGetSetN { LeafStr IGetSetN.Inner { get => new(); set { } } }
    public interface INewSameN : IGetSetN { new LeafStr Inner { get; } }
    public interface INewOtherN : IGetSetN { new Other Inner { get; set; } }
    public interface IExplicitN { LeafStr Inner { get; set; } }
    public interface IExplicitBesideN { LeafStr Inner { get; set; } }
    public interface IInherited1N : IGetSetN { }
    public interface IInherited2N : IInherited1N { }
    public interface IGetSetSecondN { LeafStr Inner { get; set; } }
    public interface ITwoBasesN : IGetSetN, IGetSetSecondN { }
    public sealed class IImplN : EmptyBase, IEmpty, IGetSetN, IGetOnlyN, IIgnoredN, IDefaultBodyN, IExplicitInDerivedN, INewSameN, IInherited2N, ITwoBasesN { public LeafStr Inner { get; set; } = new(); }
    public sealed class IInitImplN : EmptyBase, IEmpty, IInitN { public LeafStr Inner { get; init; } = new(); }
    public sealed class INewOtherImplN : EmptyBase, IEmpty, INewOtherN { public LeafStr Inner { get; set; } = new(); Other INewOtherN.Inner { get; set; } = new(); }
    public sealed class IExplicitImplN : EmptyBase, IEmpty, IExplicitN { LeafStr IExplicitN.Inner { get; set; } = new(); }
    public sealed class IExplicitBesideImplN : EmptyBase, IEmpty, IExplicitBesideN { public LeafStr Inner { get; set; } = new(); LeafStr IExplicitBesideN.Inner { get; set; } = new(); }
    public struct IStructImplN : IEmpty, IGetSetN { public IStructImplN() { } public LeafStr Inner { get; set; } = new(); }
    // </non-generic-owners>

    /// <summary>One shape of the owner's member: the kind of the owner, the shape, the owner type a holder is declared as, and the type of the object a holder holds.</summary>
    public sealed record Shape(string Kind, string Name, Type Declared, Type Runtime);

    private static readonly Shape[] GenericShapes =
    [
        new("class", "get/set", typeof(CGetSet<>), typeof(CGetSet<>)),
        new("class", "get-only", typeof(CGetOnly<>), typeof(CGetOnly<>)),
        new("class", "init", typeof(CInit<>), typeof(CInit<>)),
        new("class", "private set", typeof(CPrivateSet<>), typeof(CPrivateSet<>)),
        new("class", "[IgnoreDataMember]", typeof(CIgnored<>), typeof(CIgnored<>)),
        new("class", "virtual", typeof(CVirtual<>), typeof(CVirtual<>)),
        new("class", "virtual [IgnoreDataMember], the object an override", typeof(CVirtualIgnored<>), typeof(CVirtualIgnoredOverride<>)),
        new("class", "override", typeof(COverride<>), typeof(COverride<>)),
        new("class", "virtual, the object an override", typeof(CVirtual<>), typeof(COverride<>)),
        new("class", "new with the same type", typeof(CNewSame<>), typeof(CNewSame<>)),
        new("class", "get-only, the object a new re-declaration with a setter", typeof(CGetOnly<>), typeof(CNewSame<>)),
        new("class", "new with another type", typeof(CNewOther<>), typeof(CNewOther<>)),
        new("class", "get/set, the object a new re-declaration with another type", typeof(CGetSet<>), typeof(CNewOther<>)),
        new("class", "inherited one level", typeof(CInherited1<>), typeof(CInherited1<>)),
        new("class", "inherited two levels", typeof(CInherited2<>), typeof(CInherited2<>)),
        new("class", "get/set, the class stored as JSON by its constructors, the object a flattened subclass", typeof(CJson<>), typeof(CJsonSub<>)),
        new("sealed class", "get/set", typeof(SGetSet<>), typeof(SGetSet<>)),
        new("sealed class", "get-only", typeof(SGetOnly<>), typeof(SGetOnly<>)),
        new("sealed class", "init", typeof(SInit<>), typeof(SInit<>)),
        new("sealed class", "private set", typeof(SPrivateSet<>), typeof(SPrivateSet<>)),
        new("sealed class", "[IgnoreDataMember]", typeof(SIgnored<>), typeof(SIgnored<>)),
        new("sealed class", "override", typeof(SOverride<>), typeof(SOverride<>)),
        new("sealed class", "override of a virtual [IgnoreDataMember] member", typeof(CVirtualIgnoredOverride<>), typeof(CVirtualIgnoredOverride<>)),
        new("sealed class", "new with the same type", typeof(SNewSame<>), typeof(SNewSame<>)),
        new("sealed class", "new with another type", typeof(SNewOther<>), typeof(SNewOther<>)),
        new("sealed class", "inherited one level", typeof(SInherited1<>), typeof(SInherited1<>)),
        new("sealed class", "inherited two levels", typeof(SInherited2<>), typeof(SInherited2<>)),
        new("abstract class", "get/set", typeof(AGetSet<>), typeof(AGetSetSub<>)),
        new("abstract class", "get-only", typeof(AGetOnly<>), typeof(AGetOnlySub<>)),
        new("abstract class", "init", typeof(AInit<>), typeof(AInitSub<>)),
        new("abstract class", "private set", typeof(APrivateSet<>), typeof(APrivateSetSub<>)),
        new("abstract class", "[IgnoreDataMember]", typeof(AIgnored<>), typeof(AIgnoredSub<>)),
        new("abstract class", "abstract", typeof(AAbstract<>), typeof(AAbstractImpl<>)),
        new("abstract class", "abstract [IgnoreDataMember], the object an override", typeof(AAbstractIgnored<>), typeof(AAbstractIgnoredImpl<>)),
        new("abstract class", "virtual", typeof(AVirtual<>), typeof(AVirtualSub<>)),
        new("abstract class", "virtual [IgnoreDataMember], the object an override", typeof(AVirtualIgnored<>), typeof(AVirtualIgnoredOverride<>)),
        new("abstract class", "override", typeof(AOverride<>), typeof(AOverrideSub<>)),
        new("abstract class", "new with the same type", typeof(ANewSame<>), typeof(ANewSameSub<>)),
        new("abstract class", "get-only, the object a new re-declaration with a setter", typeof(AGetOnly<>), typeof(ANewSetterSub<>)),
        new("abstract class", "new with another type", typeof(ANewOther<>), typeof(ANewOtherSub<>)),
        new("abstract class", "inherited one level", typeof(AInherited1<>), typeof(AInherited1Sub<>)),
        new("abstract class", "inherited two levels", typeof(AInherited2<>), typeof(AInherited2Sub<>)),
        new("struct", "get/set", typeof(VGetSet<>), typeof(VGetSet<>)),
        new("struct", "get-only", typeof(VGetOnly<>), typeof(VGetOnly<>)),
        new("struct", "init", typeof(VInit<>), typeof(VInit<>)),
        new("struct", "private set", typeof(VPrivateSet<>), typeof(VPrivateSet<>)),
        new("struct", "[IgnoreDataMember]", typeof(VIgnored<>), typeof(VIgnored<>)),
        new("interface", "get/set", typeof(IGetSet<>), typeof(IImpl<>)),
        new("interface", "get-only", typeof(IGetOnly<>), typeof(IImpl<>)),
        new("interface", "init", typeof(IInit<>), typeof(IInitImpl<>)),
        new("interface", "[IgnoreDataMember]", typeof(IIgnored<>), typeof(IImpl<>)),
        new("interface", "virtual (a default body)", typeof(IDefaultBody<>), typeof(IImpl<>)),
        new("interface", "override (an explicit implementation in a derived interface)", typeof(IExplicitInDerived<>), typeof(IImpl<>)),
        new("interface", "new with the same type", typeof(INewSame<>), typeof(IImpl<>)),
        new("interface", "new with another type", typeof(INewOther<>), typeof(INewOtherImpl<>)),
        new("interface", "explicit implementation only", typeof(IExplicit<>), typeof(IExplicitImpl<>)),
        new("interface", "explicit implementation beside a public member", typeof(IExplicitBeside<>), typeof(IExplicitBesideImpl<>)),
        new("interface", "inherited one level", typeof(IInherited1<>), typeof(IImpl<>)),
        new("interface", "inherited two levels", typeof(IInherited2<>), typeof(IImpl<>)),
        new("interface", "from two bases", typeof(ITwoBases<>), typeof(IImpl<>)),
        new("interface", "get/set, implemented by a struct", typeof(IGetSet<>), typeof(IStructImpl<>)),
    ];

    /// <summary>
    /// The combinations C# cannot express, and why (reported; nothing to build). Every other combination of an owner
    /// kind, a member shape, a holder kind and a leaf kind is in <see cref="Cases"/>.
    /// </summary>
    internal static readonly string[] Excluded =
    [
        "sealed class: virtual and abstract members (CS0549, CS0513), so no virtual [IgnoreDataMember] declared on it either",
        "class (not abstract): abstract members (CS0513)",
        "struct: abstract, virtual and override members, new re-declarations, inheritance at one or two levels, two bases (a struct derives from ValueType only)",
        "interface: a private setter (CS0442: an abstract property cannot have a private accessor; with a body, no class implements it, CS9044)",
        "interface: abstract (every interface member without a body is abstract: the get/set shape)",
        "class, sealed class, abstract class, struct: from two bases (a class has one base class; two interfaces are the interface kind)",
        "class, sealed class, abstract class, struct: an explicit interface implementation as the owner's own member (it is not a member of the class: the interface kind holds the explicit shapes)",
    ];

    /// <summary>What stands in for a combination, and what is written for one leaf only (not a limit of C#).</summary>
    internal static readonly string[] Substituted =
    [
        "struct, holder declared as a base class: ValueType (the only base class a struct has besides object)",
        "the non-generic owners are written for the [TableValueSplit] string leaf only; the generic owners take every leaf kind",
    ];

    public static readonly string[] LeafKinds =
    [
        "[TableValueSplit] string", "[TableValueSplit] byte[]", "[TableValueSplit] JSON-stored object", "TableBlobContent",
        "string covered by a class-level attribute", "[TableValueSplit] JSON-stored type holding an object stored as columns",
    ];

    private static readonly Type[] LeafTypes = [typeof(LeafStr), typeof(LeafBytes), typeof(LeafJson), typeof(LeafBlob), typeof(LeafClassLevel), typeof(LeafJsonAsColumns)];

    public static readonly string[] HolderKinds = ["the owner type", "a base class", "an interface", "object"];

    /// <summary>
    /// One combination: its name, the owner type (closed), the declared type of the holder, the type of the object it
    /// holds, and the entity type the generator built.
    /// </summary>
    public sealed record Case(string Name, Shape Shape, bool Generic, int Leaf, string Holder, bool Initialised, Type Owner, Type Declared, Type Runtime, Type Entity)
    {
        public Type LeafType => LeafTypes[Leaf];

        /// <summary>The holder is declared as a type that does not show the member: the documented runtime-subtype shape.</summary>
        public bool RuntimeSubtype => Holder != HolderKinds[0];

        /// <summary>The writer's rule (0.8.3) on the object's class: a public member of the leaf's type with a getter and a setter, not ignored.</summary>
        public bool RuntimeStores => Runtime.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.Name == "Inner" && p.PropertyType == LeafType && p.CanRead && p.CanWrite && p.GetCustomAttribute<IgnoreDataMemberAttribute>() is null);

        /// <summary>
        /// The object's class has two members of one name the writer would store (a <c>new</c> re-declaration with
        /// another type): the serializer cannot describe it (on 0.8.3 too), so a write of it throws before anything is stored.
        /// </summary>
        public bool RuntimeUnwritable => Runtime.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<IgnoreDataMemberAttribute>() is null)
            .GroupBy(p => p.Name).Any(g => g.Count() > 1);

        /// <summary>An empty holder a read cannot create (an abstract class or an interface).</summary>
        public bool ReadCannotCreate => !Initialised && (Declared.IsAbstract || Declared.IsInterface);

        /// <summary>A struct holder declared as itself, holding the leaf's class (OLDER-1).</summary>
        public bool StructDeclaredAsItself => !RuntimeSubtype && Declared.IsValueType;
    }

    private static readonly Lazy<Dictionary<string, Case>> All = new(() => Build().ToDictionary(c => c.Name));

    private static IEnumerable<Case> Build()
    {
        foreach (var shape in GenericShapes)
        {
            for (var leaf = 0; leaf < LeafTypes.Length; leaf++)
            {
                foreach (var c in Holders(shape, generic: true, leaf, shape.Declared.MakeGenericType(LeafTypes[leaf]), shape.Runtime.MakeGenericType(LeafTypes[leaf])))
                    yield return c;
            }
            // The same shape, not generic (the mirror over the string leaf).
            Type Mirror(Type t) => typeof(MarkedLeafGeneratorMatrixTests).GetNestedType(t.Name[..t.Name.IndexOf('`', StringComparison.Ordinal)] + "N")
                ?? throw new InvalidOperationException($"No non-generic mirror of {t.Name}.");
            foreach (var c in Holders(shape, generic: false, 0, Mirror(shape.Declared), Mirror(shape.Runtime)))
                yield return c;
        }
    }

    private static IEnumerable<Case> Holders(Shape shape, bool generic, int leaf, Type owner, Type runtime)
    {
        foreach (var holder in HolderKinds)
        {
            var declared = holder switch
            {
                "the owner type" => owner,
                "a base class" => runtime.IsValueType ? typeof(ValueType) : typeof(EmptyBase),
                "an interface" => typeof(IEmpty),
                _ => typeof(object),
            };
            foreach (var initialised in new[] { true, false })
            {
                var entity = initialised
                    ? typeof(Initialised<,>).MakeGenericType(declared, runtime)
                    : typeof(Empty<>).MakeGenericType(declared);
                var name = $"{shape.Kind}{(generic ? ", generic" : "")}, {shape.Name}, leaf {LeafKinds[leaf]}, holder declared as {holder}, {(initialised ? "initialised" : "empty")}";
                yield return new Case(name, shape, generic, leaf, holder, initialised, owner, declared, runtime, entity);
            }
        }
    }

    public static TheoryData<string, string> Cases()
    {
        var d = new TheoryData<string, string>();
        foreach (var name in All.Value.Keys)
            foreach (var kind in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
                d.Add(name, kind);
        return d;
    }

    // The size of the matrix, and its dimensions, as the report gives them.
    [Fact]
    public void TheMatrix_HasEveryCombinationCSharpCanExpress()
    {
        var cases = All.Value.Values.ToList();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{GenericShapes.Length} member shapes over 5 owner kinds; {cases.Count(c => c.Generic)} generic and {cases.Count(c => !c.Generic)} non-generic " +
            $"combinations, {cases.Count} in all ({cases.Count(c => c.RuntimeSubtype)} of the runtime-subtype shape); {cases.Count * 2} theory rows; " +
            $"{cases.Count * 2 * Policies.Length * Sizes.Length} (combination, store, policy, size) runs." +
            "\nExcluded:\n  " + string.Join("\n  ", Excluded) + "\nSubstituted:\n  " + string.Join("\n  ", Substituted));
        Assert.Equal(GenericShapes.Length * (LeafTypes.Length + 1) * HolderKinds.Length * 2, cases.Count);
    }

    // ── The test ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Cases))]
    public Task EveryCombination_IsRefusedBeforeAnythingIsStored_OrStoredWhole_OrBehavesAsDocumented(string name, string kind) =>
        (Task)typeof(MarkedLeafGeneratorMatrixTests).GetMethod(nameof(RunAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(All.Value[name].Entity).Invoke(null, [All.Value[name], kind])!;

    private static async Task RunAsync<T>(Case c, string kind) where T : class, IEntity, new()
    {
        await using var updates = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(updates is null, LargeValueStores.SkipReason);
        await using var writes = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(writes is null, LargeValueStores.SkipReason);
        // The builder updates go to a row of their own, which another type writes: the entity type may be one the
        // serializer cannot write at all (two members of one name).
        var seed = updates.Id("u");
        await updates.Store<RowWriter>(Table).CreateAsync(new RowWriter { Id = seed, Title = "t" }, Ct);
        await updates.ShareTableAsync<RowWriter, T>(Table);
        var updateStore = updates.Store<T>(Table);
        var writeStore = writes.Store<T>(Table);
        var seedNumber = 0;
        foreach (var policy in Policies)
        {
            foreach (var size in Sizes)
            {
                var label = $"{c.Name}, {kind}, {policy}, {size}";
                var before = TableEntitySerializer.OversizedCellPolicy;
                TableEntitySerializer.OversizedCellPolicy = policy;
                try
                {
                    foreach (var source in c.RuntimeSubtype ? new[] { "Updates" } : ["lambda", "Updates"])
                        await BuilderUpdateAsync(c, updates, updateStore, seed, source, Value(c.LeafType, size, ++seedNumber), $"{label}, builder update by {source}");
                    await WholeEntityWriteAsync(c, writes, writeStore, $"w{(int)policy}-{size}", size, ++seedNumber, $"{label}, whole-entity write");
                }
                finally
                {
                    TableEntitySerializer.OversizedCellPolicy = before;
                }
            }
        }
    }

    private static async Task BuilderUpdateAsync<T>(Case c, LargeValueStores stores, IStorage<T> store, string id, string source, object value, string label)
        where T : class, IEntity, new()
    {
        var rowBefore = c.RuntimeSubtype ? (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "u") : null;
        Exception? thrown = null;
        var requests = await stores.StorageWritesAsync(store, async () => thrown = await Record.ExceptionAsync(() =>
            store.UpdateAsync(id, b =>
            {
                if (source == "Updates")
                    b.Updates[Column] = value;
                else
                    b.SetProperty(PathOf<T>(), value);
            }, Ct)));
        // On Azurite every request the store sends is listed; the in-memory store's merge is not, so its row is read.
        var row = requests.Count > 0 || stores.Kind == LargeValueStores.Memory || c.RuntimeSubtype
            ? (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "u")
            : null;
        if (!c.RuntimeSubtype)
        {
            // Rule A: the declared types name the path to a marked member: refused before any request, no cell.
            Assert.True(thrown is NotSupportedException && requests.Count == 0 && row?.Keys.Any(k => k.StartsWith(Column, StringComparison.Ordinal)) != true,
                $"{label}: {thrown?.GetType().Name ?? "accepted"}: {thrown?.Message}; requests [{string.Join(", ", requests)}]; cells [{string.Join(", ", row?.Keys ?? [])}]");
            Assert.Equal(TableEntitySerializer.PartialUpdateRefusal(Column), thrown!.Message);
            Outcome($"builder update by {source} refused (rule A)");
            return;
        }

        // Documented (the runtime-subtype shape, RV7 OBSERVED-1): the entity is not large-value aware, and the update is
        // the one of 0.8.3: the cells the plain serializer flattens the value into, or what it throws.
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(T)), $"{label}: the entity is large-value aware");
        Dictionary<string, object>? plain = null;
        var plainThrown = Record.Exception(() => plain = TableEntitySerializer.FlattenProperty(Column, value));
        if (plainThrown is not null)
        {
            Assert.True(thrown?.GetType() == plainThrown.GetType() && Same(rowBefore!, row!),
                $"{label}: the plain serializer throws {plainThrown.GetType().Name}, the update {thrown?.GetType().Name ?? "was accepted"}");
            Outcome($"builder update by Updates as on 0.8.3: refused ({thrown!.GetType().Name})");
            return;
        }
        // The service refuses a cell it cannot store (a binary cell over 64 KiB); the in-memory store stores it.
        if (thrown is RequestFailedException)
        {
            Assert.True(Same(rowBefore!, row!), $"{label}: the service refused the update, and the row changed");
            Outcome("builder update by Updates as on 0.8.3: the service refused the cell");
            return;
        }
        Assert.True(thrown is null, $"{label}: {thrown?.GetType().Name}: {thrown?.Message}");
        foreach (var (column, cell) in plain!)
            Assert.True(row!.TryGetValue(column, out var stored) && SameCell(stored, cell), $"{label}: cell {column} is not the plain serializer's");
        Outcome("builder update by Updates as on 0.8.3: written as the plain serializer's cells");
    }

    private static async Task WholeEntityWriteAsync<T>(Case c, LargeValueStores stores, IStorage<T> store, string rowKey, int size, int seed, string label)
        where T : class, IEntity, new()
    {
        var id = stores.Id(rowKey);
        var value = Value(c.LeafType, size, seed);
        var entity = new T { Id = id };
        var doc = typeof(T).GetProperty("Doc")!;
        var holder = doc.GetValue(entity);
        if (holder is null || (c.StructDeclaredAsItself && !c.Initialised))
            holder = Activator.CreateInstance(c.Runtime)!;
        SetLeaf(Inner(holder, c), value);
        doc.SetValue(entity, holder);

        var rowsBefore = (await stores.RawRowsAsync<T>(Table)).Select(r => r.RowKey).ToHashSet(StringComparer.Ordinal);
        // Only a TableBlobContent goes to a blob here: no other value reaches the offload threshold (3 MiB).
        var blobLeaf = c.LeafType == typeof(LeafBlob);
        var blobsBefore = blobLeaf ? (await stores.BlobNamesAsync<T>(Table)).ToHashSet(StringComparer.Ordinal) : [];
        var thrown = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));
        var rows = (await stores.RawRowsAsync<T>(Table)).Where(r => !rowsBefore.Contains(r.RowKey)).ToList();
        var blobs = blobLeaf ? (await stores.BlobNamesAsync<T>(Table)).Where(b => !blobsBefore.Contains(b)).ToList() : [];
        var stored = $"rows [{string.Join("; ", rows.Select(r => r.RowKey + ": " + string.Join(",", r.Keys)))}], blobs {blobs.Count}";
        if (thrown is not null)
        {
            Assert.True(rows.Count == 0 && blobs.Count == 0, $"{label}: refused ({thrown.GetType().Name}: {thrown.Message}), and something was stored: {stored}");
            // The plain store refuses a value in a large-value member below a runtime subtype, whatever its size; a class
            // the serializer cannot describe fails as it fails on 0.8.3.
            Assert.True(!c.RuntimeSubtype || !c.RuntimeStores || thrown is NotSupportedException || (c.RuntimeUnwritable && thrown is ArgumentException),
                $"{label}: {thrown.GetType().Name}: {thrown.Message}");
            Outcome($"whole-entity write refused ({thrown.GetType().Name})");
            return;
        }

        Assert.True(!c.RuntimeSubtype || !c.RuntimeStores, $"{label}: the plain store stored a value below a runtime subtype: {stored}");
        var cells = rows.SelectMany(r => r.Keys).Where(k => k.StartsWith(Column, StringComparison.Ordinal)).ToList();
        if (!c.RuntimeStores)
        {
            // Documented: the class the holder holds does not store the member (the writer's rule): nothing of the
            // value is stored, and a read returns no value.
            Assert.True(cells.Count == 0 && blobs.Count == 0, $"{label}: a member the class does not store was written: {stored}");
        }
        else
        {
            Assert.True(cells.Count > 0 || blobs.Count > 0, $"{label}: the write was accepted and stored nothing of the value: {stored}");
        }

        var reads = new (string Name, Func<Task<T?>> Read)[]
        {
            ("point read", () => store.OneAsync(id, Ct)),
            ("after a cache hit", () => store.OneAsync(id, Ct)),
            ("from a new store", () => stores.Store<T>(Table).OneAsync(id, Ct)),
            ("query", async () => (await stores.Store<T>(Table).QueryAsync(x => x.Id == id, stores.Pk, ct: Ct)).SingleOrDefault()),
        };
        var outcome = c.RuntimeStores ? "whole-entity write stored, every read returns it whole" : "whole-entity write stored nothing of the value (the class does not store the member)";
        foreach (var (readName, read) in reads)
        {
            T? back = null;
            var failed = await Record.ExceptionAsync(async () => back = await read());
            if (failed is not null)
            {
                // Documented: a read that cannot create the holder, and a struct holder declared as itself (OLDER-1).
                Assert.True(failed is SerializationException && (c.ReadCannotCreate || c.StructDeclaredAsItself),
                    $"{label}, {readName}: {failed.GetType().Name}: {failed.Message}");
                outcome = c.StructDeclaredAsItself
                    ? "whole-entity write stored, every read fails (a struct holder declared as itself, OLDER-1)"
                    : "whole-entity write stored one cell, every read fails (an empty holder a read cannot create)";
                continue;
            }
            Assert.False(c.ReadCannotCreate && c.RuntimeStores, $"{label}, {readName}: read back below a holder a read cannot create");
            Assert.False(c.StructDeclaredAsItself && c.RuntimeStores, $"{label}, {readName}: read back below a struct holder declared as itself");
            var got = LeafValue(back!, c);
            if (!c.RuntimeStores)
                Assert.True(got is null, $"{label}, {readName}: read back a value of a member the class does not store");
            else
            {
                // Rule B: a marked member holding an object stored as columns is never stored.
                Assert.False(value is JsonFlatSub, $"{label}, {readName}: a marked value was stored as columns");
                Assert.True(await SameValueAsync(got, value), $"{label}, {readName}: read back {Describe(got)}, not the value written ({Describe(value)})");
            }
        }
        Outcome(outcome);
        await store.DeleteAsync(id, Ct);
    }

    // One line per outcome in the test's output: the report counts them.
    private static void Outcome(string outcome) => TestContext.Current.TestOutputHelper?.WriteLine("outcome: " + outcome);

    // ── Values ────────────────────────────────────────────────────────────────────────────────────────────

    private static object Value(Type leaf, int size, int seed)
    {
        var text = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, seed);
        if (leaf == typeof(LeafBytes) || leaf == typeof(LeafBlob))
        {
            var bytes = new byte[size];
            new Random(seed).NextBytes(bytes);
            return leaf == typeof(LeafBlob) ? TableBlobContent.FromBytes(bytes, "application/octet-stream") : bytes;
        }
        return leaf == typeof(LeafJson) ? new JsonBox(text) : leaf == typeof(LeafJsonAsColumns) ? new JsonFlatSub { Body = text } : text;
    }

    private static async Task<bool> SameValueAsync(object? got, object written)
    {
        switch (written)
        {
            case byte[] bytes:
                return got is byte[] back && back.AsSpan().SequenceEqual(bytes);
            case TableBlobContent content:
            {
                if (got is not TableBlobContent read)
                    return false;
                await using var expected = await content.OpenReadAsync(Ct);
                await using var actual = await read.OpenReadAsync(Ct);
                var want = await OffloadTestSupport.ReadAllAsync(expected);
                var have = await OffloadTestSupport.ReadAllAsync(actual);
                return have.AsSpan().SequenceEqual(want);
            }
            default:
                return Equals(got, written);
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => $"a string of {s.Length} characters",
        byte[] b => $"{b.Length} bytes",
        JsonBox j => $"a JsonBox of {j.Text.Length} characters",
        TableBlobContent t => $"a TableBlobContent of {t.Length} bytes",
        _ => value.GetType().Name,
    };

    private static bool SameCell(object? stored, object cell) => cell is byte[] bytes
        ? stored is byte[] b && b.AsSpan().SequenceEqual(bytes)
        : Equals(stored, cell);

    private static bool Same(TableEntity before, TableEntity after) =>
        before.Keys.Count == after.Keys.Count && before.Keys.All(k => k is "odata.etag" or "Timestamp" || (after.TryGetValue(k, out var v) && SameCell(v, before[k])));

    // ── Paths through the objects ─────────────────────────────────────────────────────────────────────────

    // The owner's member Inner of the leaf's type on the object a holder holds: the public one the writer stores, or the
    // owner's declared member where the class implements it explicitly.
    private static object? Inner(object holder, Case c) =>
        (holder.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(p => p.Name == "Inner" && p.PropertyType == c.LeafType)
         ?? CompilerMember(c.Owner, "Inner")).GetValue(holder);

    private static void SetLeaf(object? leaf, object value) => leaf!.GetType().GetProperty("V")!.SetValue(leaf, value);

    private static object? LeafValue(object entity, Case c) =>
        entity.GetType().GetProperty("Doc")!.GetValue(entity) is { } holder && Inner(holder, c) is { } leaf
            ? leaf.GetType().GetProperty("V")!.GetValue(leaf)
            : null;

    // x => (object)x.Doc.Inner.V, each member as the C# compiler resolves it through the declared types: the most
    // derived declaration of the name, then the interfaces.
    private static Expression<Func<T, object>> PathOf<T>()
    {
        var x = Expression.Parameter(typeof(T), "x");
        Expression body = x;
        foreach (var segment in Column.Split('_'))
            body = Expression.Property(body, CompilerMember(body.Type, segment));
        return Expression.Lambda<Func<T, object>>(Expression.Convert(body, typeof(object)), x);
    }

    private static PropertyInfo CompilerMember(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0) is { } own)
                return own;
        }
        return type.GetInterfaces().SelectMany(i => i.GetProperties(BindingFlags.Public | BindingFlags.Instance)).First(p => p.Name == name && p.GetIndexParameters().Length == 0);
    }
}
