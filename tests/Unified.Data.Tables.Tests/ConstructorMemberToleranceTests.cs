using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The members beside a constructor are assigned from the JSON one by one, and a member whose stored JSON
/// cannot be read into its current type keeps the value the constructor and its initialiser gave it, as
/// 0.8.3 (which assigned none of them) read it. A member typed as an interface or an abstract class, a
/// member whose type changed since the row was written, and a null stored for a non-nullable value type
/// therefore leave the row readable, and the other members are still restored. Before this, the first such
/// member failed every read of the row (the PR #18 final-review regression of the M1 fix).
/// </summary>
public class ConstructorMemberToleranceTests
{
    private const string Pk = "p";
    private const string Rk = "r";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── models ───────────────────────────────────────────────────────────────

    public interface ITolShape
    {
        int Radius { get; }
    }

    public sealed class TolCircle : ITolShape
    {
        public int Radius { get; set; }
    }

    public abstract class TolBase
    {
        public string? Label { get; set; }
    }

    public sealed class TolDerived : TolBase
    {
    }

    public sealed record TolDoc(string Name)
    {
        public ITolShape? Shape { get; set; }

        public TolBase? Base { get; set; }

        public string? Note { get; set; }
    }

    /// <summary>A plain entity: it opts into nothing.</summary>
    public sealed class TolDocEntity : Entity
    {
        public TolDoc? Doc { get; set; }
    }

    public sealed record TolOrder(string Number)
    {
        public int Count { get; set; } = 3;

        public List<string> Lines { get; init; } = [];
    }

    public sealed class TolOrderHolder
    {
        public TolOrder Order { get; set; } = null!;
    }

    /// <summary>A plain entity holding the drift-prone record.</summary>
    public sealed class TolOrderEntity : Entity
    {
        public TolOrder? Order { get; set; }
    }

    // ── serializer ───────────────────────────────────────────────────────────

    [Fact]
    public void InterfaceAndAbstractMembers_KeepTheirInitialisers_AndTheOtherMembersAreRestored()
    {
        var source = new TableEntity(Pk, Rk)
        {
            ["Doc__Json"] = """{"name":"x","shape":{"radius":2},"base":{"label":"b"},"note":"n"}""",
        };

        var restored = source.FromTableEntity<TolDocEntityHolder>();

        Assert.Equal("x", restored.Doc!.Name);
        Assert.Null(restored.Doc.Shape);
        Assert.Null(restored.Doc.Base);
        Assert.Equal("n", restored.Doc.Note);
    }

    public sealed class TolDocEntityHolder
    {
        public TolDoc? Doc { get; set; }
    }

    [Theory]
    [InlineData("""{"number":"A","count":"seven","lines":["l"]}""")]
    [InlineData("""{"number":"A","count":null,"lines":["l"]}""")]
    [InlineData("""{"number":"A","count":{"nested":1},"lines":["l"]}""")]
    public void MemberWhoseStoredJsonNoLongerMatchesItsType_KeepsItsInitialiser(string json)
    {
        var entity = new TableEntity(Pk, Rk) { ["Order__Json"] = json };

        var restored = entity.FromTableEntity<TolOrderHolder>();

        Assert.Equal("A", restored.Order.Number);
        Assert.Equal(3, restored.Order.Count);
        Assert.Equal(["l"], restored.Order.Lines);
    }

    [Fact]
    public void MemberWhoseStoredJsonMatches_IsStillAssigned()
    {
        var entity = new TableEntity(Pk, Rk) { ["Order__Json"] = """{"number":"A","count":7,"lines":["l"]}""" };

        var restored = entity.FromTableEntity<TolOrderHolder>();

        Assert.Equal(7, restored.Order.Count);
        Assert.Equal(["l"], restored.Order.Lines);
    }

    // ── both stores ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PlainEntity_WithAnInterfaceMemberInARecord_ReadsBackOnBothStores()
    {
        var transcript = await FidelityScript.BothAsync(async s =>
        {
            var doc = new TolDocEntity
            {
                Id = s.Id("d"),
                Doc = new TolDoc("x") { Shape = new TolCircle { Radius = 2 }, Note = "n" },
            };
            await s.StepAsync("create", () => s.Store<TolDocEntity>().CreateAsync(doc, Ct));
            await s.StepAsync("one", async () =>
            {
                var read = await s.Store<TolDocEntity>().OneAsync(s.Id("d"), Ct);
                return (object?)$"{read!.Doc!.Name}/{(read.Doc.Shape is null ? "no shape" : "shape")}/{read.Doc.Note}";
            });
            await s.ListAsync<TolDocEntity>(e => e.Doc?.Note);
        });

        Assert.Contains("one: x/no shape/n", transcript);
        Assert.Contains($"list {nameof(TolDocEntity)} pa: [pa|d=n]", transcript);
    }

    [Fact]
    public async Task PlainEntity_LegacyRowWithADriftedMember_ReadsBackOnBothStores()
    {
        var transcript = await FidelityScript.BothAsync(async s =>
        {
            await s.SeedAsync<TolOrderEntity>(new TableEntity(s.Pk, "o")
            {
                ["Order__Json"] = """{"number":"A","count":"seven","lines":["l"]}""",
            });
            await s.StepAsync("one", async () =>
            {
                var read = await s.Store<TolOrderEntity>().OneAsync(s.Id("o"), Ct);
                return (object?)$"{read!.Order!.Number}/{read.Order.Count}/{string.Join(",", read.Order.Lines)}";
            });
        });

        Assert.Contains("one: A/3/l", transcript);
    }
}
