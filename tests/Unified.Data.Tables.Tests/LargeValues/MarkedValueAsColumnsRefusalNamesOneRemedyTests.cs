using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Rule B's refusal (a member marked <see cref="TableValueSplitAttribute"/> holding a value the serializer stores as
/// columns) never names "store an instance of the declared type": the library cannot predict System.Text.Json's round
/// trip of an arbitrary graph, and three reviews in a row found a graph for which an instance of the declared type, so
/// stored, did not read back whole (public fields and value tuples, RV10 SERIALIZER-1; framework collections that do not
/// round-trip, SERIALIZER-2; a constructor parameter with a non-null default, SERIALIZER-3; ruling R17). The refusal
/// names the column, the declared and the runtime type, what the attribute applies to, and only the remedy that does not
/// depend on the JSON graph: declare the property with the runtime type and put the attribute on the members of that
/// type that hold large values (for a <c>Nullable&lt;S&gt;</c> property, named <c>S?</c>: declare it as <c>S</c>, the
/// attribute on the members of <c>S</c>). That remedy, followed, stores the value whole on both stores at every size.
/// </summary>
[Collection("OversizedCellPolicy")]
public class MarkedValueAsColumnsRefusalNamesOneRemedyTests
{
    private const string Table = "RefusalNamesOneRemedy";
    private const string FirstRemedy = "Store an instance of";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly int[] Sizes = [10, 200_000];

    private static string Ascii(int size, int seed) => BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, seed);

    // The refusal, as a user meets it, for a property declared as declared (named so) holding a runtime.
    private static string Expected(string column, string property, string declared, string runtime) =>
        $"'{column}' holds a {runtime}, and {property} (declared as {declared}) is marked [TableValueSplit]. The serializer stores a " +
        $"{runtime} as columns, one for each of its members, not as one value, and [TableValueSplit] on a property applies to one value " +
        "stored in the property's own cells (a string, a byte[] or an object stored as one JSON value), not to an object stored as " +
        $"columns: its members would be stored as {runtime} declares them, and a trimming OversizedCellPolicy could trim them. Declare " +
        $"{property} as {runtime} and move [TableValueSplit] from it to the members of {runtime} that can hold a large value. Nothing " +
        "was stored.";

    // ── Declared types read through a constructor that binds every property (RV10 repro R1, R2 and S1) ──────────

    public class GraphDecl<TM>
    {
        public GraphDecl(int seed, TM member)
        {
            Seed = seed;
            Member = member;
        }

        public int Seed { get; set; }

        public TM Member { get; set; }
    }

    public sealed class GraphDeclFlatSub<TM> : GraphDecl<TM>
    {
        public GraphDeclFlatSub()
            : base(0, default!)
        {
        }
    }

    public sealed class GraphDeclHolder<TM> : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public GraphDecl<TM>? Doc { get; set; }
    }

    // A type System.Text.Json constructs itself whose data member is a public field (not [JsonInclude]).
    public sealed class FieldNote
    {
        public int Seed { get; set; }

#pragma warning disable CA1051, SA1401, S1104 // a public field is the shape of RV10 SERIALIZER-1
        public string? Body;
#pragma warning restore CA1051, SA1401, S1104
    }

    public sealed class ObjectKey
    {
        public int K { get; set; }
    }

    // A type whose constructor binds every member (wave 9's first-remedy shape).
    public sealed class BoundNote
    {
        public BoundNote(int seed, string? body)
        {
            Seed = seed;
            Body = body;
        }

        public int Seed { get; set; }

        public string? Body { get; set; }
    }

    // A constructor parameter with a non-null default (RV10 repro L1): a null member is not written and reads back as it.
    public class Defaulted
    {
        public Defaulted(string? body, string? title = "untitled")
        {
            Body = body;
            Title = title;
        }

        public string? Body { get; set; }

        public string? Title { get; set; }
    }

    public sealed class DefaultedFlatSub : Defaulted
    {
        public DefaultedFlatSub()
            : base(null)
        {
        }
    }

    public sealed class DefaultedHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public Defaulted? Doc { get; set; }
    }

    public static TheoryData<string, string> ShapesStores()
    {
        var d = new TheoryData<string, string>();
        foreach (var kind in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
            foreach (var shape in Shapes)
                d.Add(shape, kind);
        return d;
    }

    private static readonly string[] Shapes =
    [
        // RV10 SERIALIZER-1 (R1): a value tuple, a type with a public field.
        "a value tuple", "a type with a public field",
        // RV10 SERIALIZER-2 (R2, S1): framework collections that do not round-trip, and the ones that do.
        "Stack", "ImmutableStack", "ConcurrentStack", "ReadOnlyDictionary", "IReadOnlySet", "KeyCollection", "a dictionary with an object key",
        "Queue", "LinkedList", "SortedSet", "ObservableCollection", "Tuple", "KeyValuePair", "List", "Dictionary", "HashSet",
        "ImmutableArray", "ImmutableList", "ImmutableDictionary", "ReadOnlyCollection",
        // RV10 SERIALIZER-3 (L1): a constructor parameter with a non-null default.
        "a constructor parameter with a non-null default",
        // Graphs wave 10 judged to read back whole.
        "a string", "a nullable int", "a bound type", "a list of a bound type",
    ];

    // RV10 SERIALIZER-1, -2 and -3 (repro R1, R2, S1 and L1), restated as ruling R17 requires: whatever the declared type's
    // graph, the refusal does not name "Store an instance of"; it names only the declaration remedy; nothing is stored.
    [Theory]
    [MemberData(nameof(ShapesStores))]
    public Task TheRefusal_NeverNamesStoringAnInstanceOfTheDeclaredType_WhateverItsGraph(string shape, string kind) => shape switch
    {
        "a value tuple" => GraphRefusedAsync<(string? Body, int N)>(kind),
        "a type with a public field" => GraphRefusedAsync<FieldNote>(kind),
        "Stack" => GraphRefusedAsync<Stack<string>>(kind),
        "ImmutableStack" => GraphRefusedAsync<ImmutableStack<string>>(kind),
        "ConcurrentStack" => GraphRefusedAsync<ConcurrentStack<string>>(kind),
        "ReadOnlyDictionary" => GraphRefusedAsync<ReadOnlyDictionary<string, string>>(kind),
        "IReadOnlySet" => GraphRefusedAsync<IReadOnlySet<string>>(kind),
        "KeyCollection" => GraphRefusedAsync<Dictionary<string, string>.KeyCollection>(kind),
        "a dictionary with an object key" => GraphRefusedAsync<Dictionary<ObjectKey, string>>(kind),
        "Queue" => GraphRefusedAsync<Queue<string>>(kind),
        "LinkedList" => GraphRefusedAsync<LinkedList<string>>(kind),
        "SortedSet" => GraphRefusedAsync<SortedSet<string>>(kind),
        "ObservableCollection" => GraphRefusedAsync<ObservableCollection<string>>(kind),
        "Tuple" => GraphRefusedAsync<Tuple<string, int>>(kind),
        "KeyValuePair" => GraphRefusedAsync<KeyValuePair<string, string>>(kind),
        "List" => GraphRefusedAsync<List<string>>(kind),
        "Dictionary" => GraphRefusedAsync<Dictionary<string, string>>(kind),
        "HashSet" => GraphRefusedAsync<HashSet<string>>(kind),
        "ImmutableArray" => GraphRefusedAsync<ImmutableArray<string>>(kind),
        "ImmutableList" => GraphRefusedAsync<ImmutableList<string>>(kind),
        "ImmutableDictionary" => GraphRefusedAsync<ImmutableDictionary<string, string>>(kind),
        "ReadOnlyCollection" => GraphRefusedAsync<ReadOnlyCollection<string>>(kind),
        "a constructor parameter with a non-null default" => RefusedAsync(kind, new DefaultedHolder { Doc = new DefaultedFlatSub() },
            Expected("Doc", "DefaultedHolder.Doc", nameof(Defaulted), nameof(DefaultedFlatSub))),
        "a string" => GraphRefusedAsync<string>(kind),
        "a nullable int" => GraphRefusedAsync<int?>(kind),
        "a bound type" => GraphRefusedAsync<BoundNote>(kind),
        "a list of a bound type" => GraphRefusedAsync<List<BoundNote>>(kind),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static Task GraphRefusedAsync<TM>(string kind) =>
        RefusedAsync(kind, new GraphDeclHolder<TM> { Doc = new GraphDeclFlatSub<TM>() },
            Expected("Doc", "GraphDeclHolder`1.Doc", "GraphDecl`1", "GraphDeclFlatSub`1"));

    private static async Task RefusedAsync<T>(string kind, T entity, string expected) where T : Entity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        entity.Id = stores.Id("r");
        var refused = await Record.ExceptionAsync(() => stores.Store<T>(Table).CreateAsync(entity, Ct));
        Assert.IsType<NotSupportedException>(refused);
        Assert.DoesNotContain(FirstRemedy, refused.Message, StringComparison.Ordinal);
        Assert.Equal(expected, refused.Message);
        Assert.Empty(await stores.RawRowsAsync<T>(Table));
        Assert.Empty(await stores.BlobNamesAsync<T>(Table));
    }

    // ── The named remedy, followed, for a class holder and a Nullable<S> holder ─────────────────────────────────

    // A class holder whose declared type wave 9 named the first remedy for (its constructor binds every member).
    public class BoundDoc
    {
        public BoundDoc(int seed, string? body = null)
        {
            Seed = seed;
            Body = body;
        }

        public int Seed { get; set; }

        public string? Body { get; set; }
    }

    public sealed class BoundDocFlatSub : BoundDoc
    {
        public BoundDocFlatSub()
            : base(0)
        {
        }

        public string? Text { get; set; }
    }

    public sealed class ClassHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public BoundDoc? Doc { get; set; }
    }

    // The remedy followed: Doc declared as the runtime type, [TableValueSplit] moved from Doc to the member of that type that
    // holds the large value.
    public sealed class BoundDocMarkedSub : BoundDoc
    {
        public BoundDocMarkedSub()
            : base(0)
        {
        }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Text { get; set; }
    }

    public sealed class ClassMovedHolder : Entity
    {
        public BoundDocMarkedSub? Doc { get; set; }
    }

    // A struct with a parameterless constructor is stored as columns; the property is declared as its nullable form.
    public struct NoteStruct
    {
        public NoteStruct()
        {
        }

        public int Seed { get; set; }

        public string? Body { get; set; }
    }

    public sealed class NullableHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public NoteStruct? Doc { get; set; }
    }

    // The remedy followed: Doc declared as S (not nullable), [TableValueSplit] moved to the member of S that holds the value.
    public struct MarkedNoteStruct
    {
        public MarkedNoteStruct()
        {
        }

        public int Seed { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }
    }

    public sealed class NullableMovedHolder : Entity
    {
        public MarkedNoteStruct Doc { get; set; } = new();
    }

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AClassHolder_TheNamedRemedyFollowed_StoresTheValueWhole(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refused = await Record.ExceptionAsync(() => stores.Store<ClassHolder>(Table).CreateAsync(
            new ClassHolder { Id = stores.Id("r"), Doc = new BoundDocFlatSub { Seed = 1, Text = "hello" } }, Ct));
        Assert.IsType<NotSupportedException>(refused);
        Assert.Equal(Expected("Doc", "ClassHolder.Doc", nameof(BoundDoc), nameof(BoundDocFlatSub)), refused.Message);

        foreach (var size in Sizes)
        {
            var id = stores.Id("c" + size);
            var text = Ascii(size, size + 21);
            await stores.Store<ClassMovedHolder>(Table).CreateAsync(new ClassMovedHolder { Id = id, Doc = new BoundDocMarkedSub { Seed = 4, Body = "b", Text = text } }, Ct);
            var point = (await stores.Store<ClassMovedHolder>(Table).OneAsync(id, Ct))!.Doc;
            var queried = (await stores.Store<ClassMovedHolder>(Table).QueryAsync(stores.Pk, Ct)).Single(e => e.Id == id).Doc;
            foreach (var (read, back) in new[] { ("point read", point), ("query", queried) })
            {
                Assert.True(back is { Seed: 4, Body: "b" } && back.Text == text,
                    $"{kind}, {size}, {read}: read back Seed={back?.Seed}, Body={back?.Body}, Text {back?.Text?.Length} of {size}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ANullableStructHolder_TheNamedRemedyFollowed_StoresTheValueWhole(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refused = await Record.ExceptionAsync(() => stores.Store<NullableHolder>(Table).CreateAsync(
            new NullableHolder { Id = stores.Id("r"), Doc = new NoteStruct { Seed = 1, Body = "hello" } }, Ct));
        Assert.IsType<NotSupportedException>(refused);
        Assert.Equal(Expected("Doc", "NullableHolder.Doc", nameof(NoteStruct) + "?", nameof(NoteStruct)), refused.Message);

        foreach (var size in Sizes)
        {
            var id = stores.Id("s" + size);
            var body = Ascii(size, size + 22);
            await stores.Store<NullableMovedHolder>(Table).CreateAsync(new NullableMovedHolder { Id = id, Doc = new MarkedNoteStruct { Seed = 4, Body = body } }, Ct);
            var point = (await stores.Store<NullableMovedHolder>(Table).OneAsync(id, Ct))!.Doc;
            var queried = (await stores.Store<NullableMovedHolder>(Table).QueryAsync(stores.Pk, Ct)).Single(e => e.Id == id).Doc;
            foreach (var (read, back) in new[] { ("point read", point), ("query", queried) })
                Assert.True(back is { Seed: 4 } && back.Body == body, $"{kind}, {size}, {read}: read back Seed={back.Seed}, Body {back.Body?.Length} of {size}");
        }
    }
}
