using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// A type read through its single public parameterized constructor (a positional record, or a class whose
/// only public constructor takes arguments) keeps every member the JSON carries: the constructor takes what
/// its parameters name, and every other settable or init-only member is then assigned from the JSON as
/// System.Text.Json and Newtonsoft assign it. Up to 0.9.0's review the converter invoked the constructor and
/// dropped every other member (M1 of the PR #18 deep review), so <c>Lines</c> and <c>Note</c> below read back
/// empty and the next write stored the loss.
/// </summary>
public class ConstructorRecordMemberTests
{
    private const string Pk = "p";
    private const string Rk = "r";

    // ── models ───────────────────────────────────────────────────────────────

    public sealed record OrderRec(string Number)
    {
        public List<string> Lines { get; init; } = [];

        public string? Note { get; set; }
    }

    public sealed class OrderHolder
    {
        public OrderRec Order { get; set; } = null!;
    }

    public sealed record Inner(string Name)
    {
        public int Extra { get; set; }
    }

    public sealed record Outer(string Id)
    {
        public Inner? Child { get; init; }

        public List<Inner> Children { get; set; } = [];

        public Dictionary<string, Inner> ByKey { get; init; } = [];
    }

    public sealed class OuterHolder
    {
        public Outer Value { get; set; } = null!;
    }

    public sealed record WithDefaults(string Id)
    {
        public int Priority { get; init; } = 5;

        public string Label { get; set; } = "default";

        public List<string> Tags { get; init; } = ["seed"];
    }

    public sealed class WithDefaultsHolder
    {
        public WithDefaults Value { get; set; } = null!;
    }

    public sealed record Annotated(string Id)
    {
        [JsonIgnore]
        public string? Ignored { get; set; }

        [JsonPropertyName("t")]
        public string? Renamed { get; set; }

        public string? PrivateSet { get; private set; }

        [JsonInclude]
        public string? IncludedPrivateSet { get; private set; }

        public string Computed => Id + "!";
    }

    public sealed class AnnotatedHolder
    {
        public Annotated Value { get; set; } = null!;
    }

    /// <summary>A constructor parameter whose name matches its property only case-insensitively.</summary>
    public sealed class CaseMismatch
    {
        public CaseMismatch(string NAME, IEnumerable<string> items)
        {
            Name = NAME;
            Items = [.. items];
        }

        public string Name { get; }

        // Settable, and ALSO fed by the constructor: the constructor's value stands.
        public ImmutableList<string> Items { get; set; }

        public string? Extra { get; set; }
    }

    public sealed class CaseMismatchHolder
    {
        public CaseMismatch Value { get; set; } = null!;
    }

    /// <summary>A class whose only public constructor takes arguments, with settable members besides.</summary>
    public sealed class CtorClass
    {
        public CtorClass(string id) => Id = id;

        public string Id { get; }

        public double Amount { get; set; }

        public DateTimeOffset When { get; init; }

        public Inner? Nested { get; set; }
    }

    public sealed class CtorClassHolder
    {
        public CtorClass Value { get; set; } = null!;

        public List<CtorClass> Many { get; set; } = [];
    }

    /// <summary>A positional record struct: the members are set on the constructed value, not on a copy.</summary>
    public readonly record struct Point(int X)
    {
        public int Y { get; init; }
    }

    public sealed class PointHolder
    {
        public Point Value { get; set; }

        public List<Point> Many { get; set; } = [];
    }

    /// <summary>A private constructor annotated with a foreign JsonConstructor attribute, with a settable member beside it.</summary>
    public sealed class ForeignCtor
    {
        [Foreign.JsonConstructor]
        private ForeignCtor(string id) => Id = id;

        public string Id { get; }

        public string? Extra { get; set; }

        public static ForeignCtor Create(string id) => new(id);
    }

    public sealed class ForeignCtorHolder
    {
        public ForeignCtor Value { get; set; } = null!;
    }

    /// <summary>Two members under one camelCase JSON name: System.Text.Json has no contract for it.</summary>
    public sealed class CollidingNames
    {
        public CollidingNames(string url) => Url = url;

        public string Url { get; }

        [JsonPropertyName("url")]
        public string Link => Url;
    }

    public sealed class CollidingNamesHolder
    {
        public CollidingNames Value { get; set; } = null!;
    }

    // ── round trips ─────────────────────────────────────────────────────────

    /// <summary>
    /// A legacy row of a type System.Text.Json cannot describe still reads through its constructor, as it did
    /// before the members beside the constructor were assigned.
    /// </summary>
    [Fact]
    public void LegacyRow_OfATypeWithNoSystemTextJsonContract_StillReadsThroughItsConstructor()
    {
        var entity = new TableEntity(Pk, Rk) { ["Value__Json"] = """{"Url":"x"}""" };

        var restored = entity.FromTableEntity<CollidingNamesHolder>();

        Assert.Equal("x", restored.Value.Url);
    }

    [Fact]
    public void PositionalRecordStruct_KeepsItsInitMembers()
    {
        var source = new PointHolder { Value = new Point(1) { Y = 2 }, Many = [new Point(3) { Y = 4 }] };

        var entity = source.ToTableEntity(Pk, Rk);
        var restored = entity.FromTableEntity<PointHolder>();

        Assert.Equal(source.Value, restored.Value);
        Assert.Equal(source.Many, restored.Many);
    }

    [Fact]
    public void ForeignAnnotatedPrivateConstructor_KeepsSettableMembers()
    {
        var entity = new TableEntity(Pk, Rk) { ["Value__Json"] = """{"id":"x","extra":"e"}""" };

        var restored = entity.FromTableEntity<ForeignCtorHolder>();

        Assert.Equal("x", restored.Value.Id);
        Assert.Equal("e", restored.Value.Extra);
    }

    [Fact]
    public void PositionalRecord_KeepsInitAndSetMembers()
    {
        var source = new OrderHolder { Order = new OrderRec("A-1") { Lines = ["x", "y"], Note = "n" } };

        var entity = source.ToTableEntity(Pk, Rk);
        Assert.True(entity.ContainsKey("Order__Json"));

        var restored = entity.FromTableEntity<OrderHolder>();

        Assert.Equal(source.Order.Number, restored.Order.Number);
        Assert.Equal(source.Order.Lines, restored.Order.Lines);
        Assert.Equal("n", restored.Order.Note);
    }

    [Fact]
    public void NestedRecords_InMembersCollectionsAndDictionaries_KeepTheirMembers()
    {
        var source = new OuterHolder
        {
            Value = new Outer("o")
            {
                Child = new Inner("c") { Extra = 1 },
                Children = [new Inner("a") { Extra = 2 }, new Inner("b") { Extra = 3 }],
                ByKey = new() { ["k"] = new Inner("d") { Extra = 4 } },
            },
        };

        var restored = source.ToTableEntity(Pk, Rk).FromTableEntity<OuterHolder>();

        Assert.Equal("o", restored.Value.Id);
        Assert.Equal(new Inner("c") { Extra = 1 }, restored.Value.Child);
        Assert.Equal(source.Value.Children, restored.Value.Children);
        Assert.Equal(4, restored.Value.ByKey["k"].Extra);
        Assert.Equal("d", restored.Value.ByKey["k"].Name);
    }

    [Fact]
    public void MembersAbsentFromTheJson_KeepTheirInitialisers()
    {
        var entity = new TableEntity(Pk, Rk) { ["Value__Json"] = """{"id":"x"}""" };

        var restored = entity.FromTableEntity<WithDefaultsHolder>();

        Assert.Equal("x", restored.Value.Id);
        Assert.Equal(5, restored.Value.Priority);
        Assert.Equal("default", restored.Value.Label);
        Assert.Equal(["seed"], restored.Value.Tags);
    }

    [Fact]
    public void MembersWithValues_ReplaceTheirInitialisers()
    {
        var source = new WithDefaultsHolder { Value = new WithDefaults("x") { Priority = 9, Label = "L", Tags = ["a"] } };

        var restored = source.ToTableEntity(Pk, Rk).FromTableEntity<WithDefaultsHolder>();

        Assert.Equal(9, restored.Value.Priority);
        Assert.Equal("L", restored.Value.Label);
        Assert.Equal(["a"], restored.Value.Tags);
    }

    [Fact]
    public void Attributes_AreHonouredAsSystemTextJsonHonoursThem()
    {
        var entity = new TableEntity(Pk, Rk)
        {
            ["Value__Json"] = """{"id":"x","ignored":"no","t":"renamed","privateSet":"no","includedPrivateSet":"yes","computed":"no"}""",
        };

        var restored = entity.FromTableEntity<AnnotatedHolder>();

        Assert.Equal("x", restored.Value.Id);
        Assert.Null(restored.Value.Ignored);
        Assert.Equal("renamed", restored.Value.Renamed);
        Assert.Null(restored.Value.PrivateSet);
        Assert.Equal("yes", restored.Value.IncludedPrivateSet);
        Assert.Equal("x!", restored.Value.Computed);
    }

    [Fact]
    public void ParameterNamedLikeItsPropertyInAnotherCase_TheConstructorValueStands()
    {
        var entity = new TableEntity(Pk, Rk) { ["Value__Json"] = """{"name":"a","items":["i","j"],"extra":"e"}""" };

        var restored = entity.FromTableEntity<CaseMismatchHolder>();

        Assert.Equal("a", restored.Value.Name);
        Assert.Equal(["i", "j"], restored.Value.Items);
        Assert.Equal("e", restored.Value.Extra);
    }

    [Fact]
    public void ConstructorClass_KeepsSettableMembers_AlsoAsCollectionElements()
    {
        var when = new DateTimeOffset(2026, 11, 12, 1, 2, 3, TimeSpan.Zero);
        var source = new CtorClassHolder
        {
            Value = new CtorClass("v") { Amount = 1.5, When = when, Nested = new Inner("n") { Extra = 7 } },
            Many = [new CtorClass("m") { Amount = 2.5 }],
        };

        var restored = source.ToTableEntity(Pk, Rk).FromTableEntity<CtorClassHolder>();

        Assert.Equal(1.5, restored.Value.Amount);
        Assert.Equal(when, restored.Value.When);
        Assert.Equal(new Inner("n") { Extra = 7 }, restored.Value.Nested);
        Assert.Equal(2.5, Assert.Single(restored.Many).Amount);
    }

    // ── rows written before the fix ─────────────────────────────────────────

    /// <summary>
    /// Writing is unchanged: the cell 0.8.3 wrote for the value is exactly System.Text.Json's own output
    /// (camelCase, nulls left out), so every row 0.8.3 wrote carries the members and now reads them.
    /// </summary>
    [Fact]
    public void TheWrittenCell_IsUnchanged()
    {
        var source = new OrderHolder { Order = new OrderRec("A-1") { Lines = ["x"], Note = null } };

        var cell = source.ToTableEntity(Pk, Rk).GetString("Order__Json");

        Assert.Equal("""{"number":"A-1","lines":["x"]}""", cell);
    }

    [Fact]
    public void LegacyRow_WrittenInCamelCase_ReadsEveryMember()
    {
        var entity = new TableEntity(Pk, Rk) { ["Order__Json"] = """{"number":"A-1","lines":["x","y"],"note":"n"}""" };

        var restored = entity.FromTableEntity<OrderHolder>();

        Assert.Equal("A-1", restored.Order.Number);
        Assert.Equal(["x", "y"], restored.Order.Lines);
        Assert.Equal("n", restored.Order.Note);
    }

    [Fact]
    public void LegacyRow_WrittenInPascalCaseByNewtonsoft_ReadsEveryMember()
    {
        var entity = new TableEntity(Pk, Rk) { ["Order__Json"] = """{"Number":"A-1","Lines":["x"],"Note":"n"}""" };

        var restored = entity.FromTableEntity<OrderHolder>();

        Assert.Equal("A-1", restored.Order.Number);
        Assert.Equal(["x"], restored.Order.Lines);
        Assert.Equal("n", restored.Order.Note);
    }

    [Fact]
    public void LegacyRow_WithoutTheExtraMembers_ReadsAsBefore()
    {
        var entity = new TableEntity(Pk, Rk) { ["Order__Json"] = """{"number":"A-1"}""" };

        var restored = entity.FromTableEntity<OrderHolder>();

        Assert.Equal("A-1", restored.Order.Number);
        Assert.Empty(restored.Order.Lines);
        Assert.Null(restored.Order.Note);
    }

    [Fact]
    public void LegacyRow_WithAnUnknownMember_IgnoresIt()
    {
        var entity = new TableEntity(Pk, Rk) { ["Order__Json"] = """{"number":"A-1","gone":42,"note":"n"}""" };

        var restored = entity.FromTableEntity<OrderHolder>();

        Assert.Equal("n", restored.Order.Note);
    }

    /// <summary>Parity with System.Text.Json on the same value, for the record it can bind itself.</summary>
    [Fact]
    public void MatchesSystemTextJson_ForARecordItCanBind()
    {
        var value = new OrderRec("A-1") { Lines = ["x", "y"], Note = "n" };
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
        var expected = JsonSerializer.Deserialize<OrderRec>(JsonSerializer.Serialize(value, options), options)!;

        var restored = new OrderHolder { Order = value }.ToTableEntity(Pk, Rk).FromTableEntity<OrderHolder>().Order;

        Assert.Equal(expected.Number, restored.Number);
        Assert.Equal(expected.Lines, restored.Lines);
        Assert.Equal(expected.Note, restored.Note);
    }
}
