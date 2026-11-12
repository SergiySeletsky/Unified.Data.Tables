using System.Collections.Immutable;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Rule B's refusal (a member marked <see cref="TableValueSplitAttribute"/> holding an object the serializer stores as
/// columns) names "store an instance of the declared type" only when an instance of the declared type is read back whole
/// through its whole JSON graph: every type the library's reader constructs through a constructor, at every depth of the
/// declared member types (collection and dictionary element types included), binds every member its JSON stores, every
/// type System.Text.Json constructs itself can set every member it stores, and no member on the way is declared as
/// <see cref="object"/>, an interface or an abstract type. Otherwise only the second remedy is named. The check looked
/// at the declared type's own members only, so the first remedy was named for a declared type whose member's type does not
/// bind a member, and followed, that nested member read back null at every size (SERIALIZER-2 of the review of fix wave 9).
/// </summary>
[Collection("OversizedCellPolicy")]
public class MarkedValueAsColumnsRemedyOverTheWholeGraphTests
{
    private const string Table = "RemedyWholeGraph";
    private const string FirstRemedy = "Store an instance of";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly int[] Sizes = [10, 200_000];

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    private static string Ascii(int size, int seed) => BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, seed);

    // ── The reviewer's shape (P1): the declared type binds its members, the type of one of them does not ──────────

    public class NestedUnbound
    {
        public NestedUnbound(int seed) => Seed = seed;

        public int Seed { get; set; }

        public string? Body { get; set; }
    }

    public class OuterOverUnbound
    {
        public OuterOverUnbound(int seed, NestedUnbound? inner)
        {
            Seed = seed;
            Inner = inner;
        }

        public int Seed { get; set; }

        public NestedUnbound? Inner { get; set; }
    }

    public sealed class OuterOverUnboundFlatSub : OuterOverUnbound
    {
        public OuterOverUnboundFlatSub()
            : base(0, null)
        {
        }

        public string? Text { get; set; }
    }

    public sealed class OverUnboundHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public OuterOverUnbound? Doc { get; set; }
    }

    // The remedy named, followed: Doc declared as the runtime type, the attribute moved to the members of that type that
    // can hold a large value (a class-level attribute on the runtime type covers them).
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class OuterOverUnboundFlatSubMarked : OuterOverUnbound
    {
        public OuterOverUnboundFlatSubMarked()
            : base(0, null)
        {
        }

        public string? Text { get; set; }
    }

    public sealed class OverUnboundMovedHolder : Entity
    {
        public OuterOverUnboundFlatSubMarked? Doc { get; set; }
    }

    // RV9 SERIALIZER-2, P1 corrected to the requirement: the first remedy is not named (an instance of the declared type
    // does not read back whole), and the remedy that is named, followed, stores and reads the value back whole.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ADeclaredTypeWhoseMembersTypeDoesNotBindAMember_TheRefusalNamesOnlyTheRemedyThatWorks(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refused = await Record.ExceptionAsync(() => stores.Store<OverUnboundHolder>(Table).CreateAsync(
            new OverUnboundHolder { Id = stores.Id("r"), Doc = new OuterOverUnboundFlatSub { Seed = 1, Inner = new NestedUnbound(2) { Body = "x" } } }, Ct));
        Assert.IsType<NotSupportedException>(refused);
        Assert.Equal(
            "'Doc' holds a OuterOverUnboundFlatSub, and OverUnboundHolder.Doc (declared as OuterOverUnbound) is marked [TableValueSplit]. The " +
            "serializer stores a OuterOverUnboundFlatSub as columns, one for each of its members, not as one value, and [TableValueSplit] on a " +
            "property applies to one value stored in the property's own cells (a string, a byte[] or an object stored as one JSON value), not " +
            "to an object stored as columns: its members would be stored as OuterOverUnboundFlatSub declares them, and a trimming " +
            "OversizedCellPolicy could trim them. Declare OverUnboundHolder.Doc as OuterOverUnboundFlatSub and move [TableValueSplit] from it " +
            "to the members of OuterOverUnboundFlatSub that can hold a large value. Nothing was stored.",
            refused.Message);
        Assert.Empty(await stores.RawRowsAsync<OverUnboundHolder>(Table));

        foreach (var size in Sizes)
        {
            var id = stores.Id("m" + size);
            var text = Ascii(size, size + 3);
            await stores.Store<OverUnboundMovedHolder>(Table).CreateAsync(
                new OverUnboundMovedHolder { Id = id, Doc = new OuterOverUnboundFlatSubMarked { Seed = 4, Text = text, Inner = new NestedUnbound(5) } }, Ct);
            var back = (await stores.Store<OverUnboundMovedHolder>(Table).OneAsync(id, Ct))!.Doc;
            Assert.True(back is { Seed: 4, Inner.Seed: 5 } && back.Text == text,
                $"{kind}, {size}: read back Seed={back?.Seed}, Inner.Seed={back?.Inner?.Seed}, Text {back?.Text?.Length} of {size}");
        }
    }

    // ── A declared type whose whole graph is bound: the first remedy is named, and followed it works ──────────────

    public class NestedBound
    {
        public NestedBound(int seed, string? body)
        {
            Seed = seed;
            Body = body;
        }

        public int Seed { get; set; }

        public string? Body { get; set; }
    }

    // A type System.Text.Json constructs itself and whose every member it stores is settable.
    public class NestedSettable
    {
        public string? Body { get; set; }

        public int Seed { get; init; }
    }

    public class OuterOverBound
    {
        public OuterOverBound(int seed, NestedBound? inner, IReadOnlyList<NestedBound>? items, Dictionary<string, NestedSettable>? map, NestedSettable[]? array)
        {
            Seed = seed;
            Inner = inner;
            Items = items;
            Map = map;
            Array = array;
        }

        public int Seed { get; set; }

        public NestedBound? Inner { get; set; }

        public IReadOnlyList<NestedBound>? Items { get; set; }

        public Dictionary<string, NestedSettable>? Map { get; set; }

        public NestedSettable[]? Array { get; set; }
    }

    public sealed class OuterOverBoundFlatSub : OuterOverBound
    {
        public OuterOverBoundFlatSub()
            : base(0, null, null, null, null)
        {
        }
    }

    public sealed class OverBoundHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public OuterOverBound? Doc { get; set; }
    }

    // A type that refers to itself (cycle-guarded), every member bound.
    public class Chain
    {
        public Chain(string? body, Chain? next)
        {
            Body = body;
            Next = next;
        }

        public string? Body { get; set; }

        public Chain? Next { get; set; }
    }

    public sealed class ChainFlatSub : Chain
    {
        public ChainFlatSub()
            : base(null, null)
        {
        }
    }

    public sealed class ChainHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public Chain? Doc { get; set; }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ADeclaredTypeWhoseWholeGraphIsBound_TheRefusalNamesTheFirstRemedy_AndFollowedItStoresTheValueWhole(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refused = await Record.ExceptionAsync(() => stores.Store<OverBoundHolder>(Table).CreateAsync(
            new OverBoundHolder { Id = stores.Id("r"), Doc = new OuterOverBoundFlatSub { Seed = 1 } }, Ct));
        Assert.IsType<NotSupportedException>(refused);
        Assert.Contains("Store an instance of OuterOverBound in OverBoundHolder.Doc (it is stored as one JSON value), or declare OverBoundHolder.Doc " +
                        "as OuterOverBoundFlatSub and move [TableValueSplit] from it to the members of OuterOverBoundFlatSub that can hold a large value.",
            refused.Message, StringComparison.Ordinal);
        var chain = await Record.ExceptionAsync(() => stores.Store<ChainHolder>(Table).CreateAsync(new ChainHolder { Id = stores.Id("c"), Doc = new ChainFlatSub() }, Ct));
        Assert.IsType<NotSupportedException>(chain);
        Assert.Contains("Store an instance of Chain in ChainHolder.Doc (it is stored as one JSON value)", chain.Message, StringComparison.Ordinal);

        foreach (var size in Sizes)
        {
            var (a, b, c, d) = (Ascii(size, 1), Ascii(10, 2), Ascii(10, 3), Ascii(10, 4));
            var id = stores.Id("b" + size);
            await stores.Store<OverBoundHolder>(Table).CreateAsync(new OverBoundHolder
            {
                Id = id,
                Doc = new OuterOverBound(4, new NestedBound(5, a), [new NestedBound(6, b)], new() { ["k"] = new NestedSettable { Seed = 7, Body = c } }, [new NestedSettable { Seed = 8, Body = d }]),
            }, Ct);
            var back = (await stores.Store<OverBoundHolder>(Table).OneAsync(id, Ct))!.Doc;
            Assert.True(back is { Seed: 4, Inner: { Seed: 5 } inner, Items: [{ Seed: 6 } item], Map: { } map, Array: [{ Seed: 8 } element] }
                        && back.GetType() == typeof(OuterOverBound) && inner.Body == a && item.Body == b && map["k"] is { Seed: 7 } m && m.Body == c && element.Body == d,
                $"{kind}, {size}: read back Seed={back?.Seed}, Inner.Body {back?.Inner?.Body?.Length}, Items[0].Body {back?.Items?[0].Body?.Length}, " +
                $"Map[k].Body {back?.Map?["k"].Body?.Length}, Array[0].Body {back?.Array?[0].Body?.Length} of {size}");

            var cid = stores.Id("c" + size);
            await stores.Store<ChainHolder>(Table).CreateAsync(new ChainHolder { Id = cid, Doc = new Chain(a, new Chain(b, null)) }, Ct);
            var cback = (await stores.Store<ChainHolder>(Table).OneAsync(cid, Ct))!.Doc;
            Assert.True(cback is { Next: { Next: null } next } && cback.Body == a && next.Body == b,
                $"{kind}, {size}: chain read back Body {cback?.Body?.Length}, Next.Body {cback?.Next?.Body?.Length} of {size}");
        }
    }

    // ── Every shape of the graph that does not read back whole: the first remedy is not named ────────────────────

    public interface INote
    {
        string? Body { get; }
    }

    public abstract class AbstractNote
    {
        public string? Body { get; set; }
    }

    public class NestedGetOnly
    {
        public NestedGetOnly()
        {
        }

        public NestedGetOnly(string? body) => Body = body;

        public string? Body { get; }
    }

    public class NestedPrivateSet
    {
        public string? Body { get; private set; }
    }

    public class Decl<TM>
    {
        public Decl(int seed, TM? member)
        {
            Seed = seed;
            Member = member;
        }

        public int Seed { get; set; }

        public TM? Member { get; set; }
    }

    public sealed class DeclFlatSub<TM> : Decl<TM>
    {
        public DeclFlatSub()
            : base(0, default)
        {
        }
    }

    public sealed class DeclHolder<TM> : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public Decl<TM>? Doc { get; set; }
    }

    private static string Message(Type member) =>
        TableEntitySerializer.MarkedValueStoredAsColumns("Doc", typeof(DeclHolder<>).MakeGenericType(member).GetProperty("Doc")!,
            typeof(DeclFlatSub<>).MakeGenericType(member));

    public static TheoryData<string> NotWhole() =>
    [
        "object", "an interface", "an abstract class", "a type its constructor does not bind", "a list of a type its constructor does not bind",
        "a dictionary of a type its constructor does not bind", "an immutable array of a type its constructor does not bind",
        "an array of a type its constructor does not bind", "a nullable struct of a type that cannot set a member", "a get-only member",
        "a member with a private setter", "a list of object", "a TableBlobContent",
    ];

    private static Type MemberType(string shape) => shape switch
    {
        "object" => typeof(object),
        "an interface" => typeof(INote),
        "an abstract class" => typeof(AbstractNote),
        "a type its constructor does not bind" => typeof(NestedUnbound),
        "a list of a type its constructor does not bind" => typeof(List<NestedUnbound>),
        "a dictionary of a type its constructor does not bind" => typeof(Dictionary<string, NestedUnbound>),
        "an immutable array of a type its constructor does not bind" => typeof(ImmutableArray<NestedUnbound>),
        "an array of a type its constructor does not bind" => typeof(NestedUnbound[]),
        "a nullable struct of a type that cannot set a member" => typeof(GetOnlyStruct?),
        "a get-only member" => typeof(NestedGetOnly),
        "a member with a private setter" => typeof(NestedPrivateSet),
        "a list of object" => typeof(List<object>),
        "a TableBlobContent" => typeof(TableBlobContent),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    // System.Text.Json constructs it itself (no constructor of its own) and cannot set its get-only member.
    public struct GetOnlyStruct
    {
        public string? Body { get; }
    }

    [Theory]
    [MemberData(nameof(NotWhole))]
    public void AGraphThatDoesNotReadBackWhole_TheRefusalDoesNotNameTheFirstRemedy(string shape)
    {
        var message = Message(MemberType(shape));
        Assert.DoesNotContain(FirstRemedy, message, StringComparison.Ordinal);
        Assert.Contains("Declare DeclHolder`1.Doc as DeclFlatSub`1 and move [TableValueSplit]", message, StringComparison.Ordinal);
    }

    public static TheoryData<string> Whole() => ["a string", "a bound type", "a list of a bound type", "a settable type", "a dictionary of a settable type", "a nullable int"];

    [Theory]
    [MemberData(nameof(Whole))]
    public void AGraphThatReadsBackWhole_TheRefusalNamesTheFirstRemedy(string shape)
    {
        var member = shape switch
        {
            "a string" => typeof(string),
            "a bound type" => typeof(NestedBound),
            "a list of a bound type" => typeof(List<NestedBound>),
            "a settable type" => typeof(NestedSettable),
            "a dictionary of a settable type" => typeof(Dictionary<string, NestedSettable>),
            "a nullable int" => typeof(int?),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        Assert.Contains("Store an instance of Decl`1 in DeclHolder`1.Doc", Message(member), StringComparison.Ordinal);
    }

    // The second remedy followed for a shape the first one is withheld for: a bound declared type whose member is declared
    // as a type its constructor does not bind (a list of it), the value in a member of the runtime type.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class DeclFlatSubMarked : Decl<List<NestedUnbound>>
    {
        public DeclFlatSubMarked()
            : base(0, null)
        {
        }

        public string? Text { get; set; }
    }

    public sealed class DeclMovedHolder : Entity
    {
        public DeclFlatSubMarked? Doc { get; set; }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheSecondRemedy_FollowedForAGraphThatDoesNotReadBackWhole_StoresTheValueWhole(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refused = await Record.ExceptionAsync(() => stores.Store<DeclHolder<List<NestedUnbound>>>(Table).CreateAsync(
            new DeclHolder<List<NestedUnbound>> { Id = stores.Id("r"), Doc = new DeclFlatSub<List<NestedUnbound>>() }, Ct));
        Assert.IsType<NotSupportedException>(refused);
        Assert.DoesNotContain(FirstRemedy, refused.Message, StringComparison.Ordinal);
        foreach (var size in Sizes)
        {
            var id = stores.Id("s" + size);
            var text = Ascii(size, size + 9);
            await stores.Store<DeclMovedHolder>(Table).CreateAsync(new DeclMovedHolder { Id = id, Doc = new DeclFlatSubMarked { Seed = 3, Text = text } }, Ct);
            var back = (await stores.Store<DeclMovedHolder>(Table).OneAsync(id, Ct))!.Doc;
            Assert.True(back is { Seed: 3 } && back.Text == text, $"{kind}, {size}: read back Seed={back?.Seed}, Text {back?.Text?.Length} of {size}");
        }
    }
}
