using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Behavioural tests for the server-side LINQ query surface against the faithful in-memory fake. The
/// fake validates every predicate through the same <see cref="TableFilterTranslator"/> the real store
/// uses (so an untranslatable predicate fails identically), then evaluates the caller's real semantics.
/// </summary>
public class LinqQueryTests
{
    public enum State { Open, Closed }

    public sealed class Doc : Entity
    {
        public string Owner { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal Price { get; set; }
        public bool Active { get; set; }
        public State Status { get; set; }

        // Computed / get-only — no stored column.
        public bool IsOpenComputed => Status == State.Open;
    }

    private static async Task<InMemoryStorage<Doc>> Seed()
    {
        var store = new InMemoryStorage<Doc>();
        await store.CreateAsync(new Doc { Id = "a|1", Owner = "alice", Count = 1, Price = 10m, Active = true, Status = State.Open });
        await store.CreateAsync(new Doc { Id = "a|2", Owner = "bob", Count = 5, Price = 30m, Active = false, Status = State.Closed });
        await store.CreateAsync(new Doc { Id = "b|3", Owner = "alice", Count = 9, Price = 50m, Active = true, Status = State.Open });
        return store;
    }

    [Fact]
    public async Task QueryAsync_Predicate_FiltersByEnum()
    {
        var store = await Seed();

        var open = await store.QueryAsync(d => d.Status == State.Open);

        Assert.Equal(2, open.Count);
        Assert.All(open, d => Assert.Equal(State.Open, d.Status));
    }

    [Fact]
    public async Task QueryAsync_Predicate_CombinesWithAndOr()
    {
        var store = await Seed();

        var result = await store.QueryAsync(d => d.Active && d.Count > 3);

        Assert.Single(result);
        Assert.Equal("b|3", result[0].Id);
    }

    [Fact]
    public async Task QueryAsync_Predicate_DecimalComparison_RoundTripsThroughDouble()
    {
        var store = await Seed();

        var pricey = await store.QueryAsync(d => d.Price >= 30m);

        Assert.Equal(2, pricey.Count);
        Assert.DoesNotContain(pricey, d => d.Id == "a|1");
    }

    [Fact]
    public async Task QueryAsync_Predicate_ScopesToPartition()
    {
        var store = await Seed();

        var inA = await store.QueryAsync(d => d.Owner == "alice", partition: "a");

        Assert.Single(inA);
        Assert.Equal("a|1", inA[0].Id);
    }

    [Fact]
    public async Task QueryAsync_Predicate_HonoursTake()
    {
        var store = await Seed();

        var one = await store.QueryAsync(d => d.Owner == "alice", take: 1);

        Assert.Single(one);
    }

    [Fact]
    public async Task AnyAsync_ReturnsTrue_WhenAMatchExists()
    {
        var store = await Seed();
        Assert.True(await store.AnyAsync(d => d.Count == 9));
        Assert.False(await store.AnyAsync(d => d.Count == 999));
    }

    [Fact]
    public async Task QueryStreamAsync_Predicate_StreamsMatches()
    {
        var store = await Seed();

        var owners = new List<string>();
        await foreach (var d in store.QueryStreamAsync(d => d.Status == State.Open))
            owners.Add(d.Owner);

        Assert.Equal(2, owners.Count);
    }

    [Fact]
    public async Task QueryAsync_UntranslatablePredicate_ThrowsSameAsRealStore()
    {
        var store = await Seed();

        // A green fake must not accept a predicate the real store cannot push server-side.
        await Assert.ThrowsAsync<NotSupportedException>(() => store.QueryAsync(d => d.Owner.Contains("li")));
    }

    [Fact]
    public async Task QueryAsync_NonPositiveTake_Throws()
    {
        var store = await Seed();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.QueryAsync(d => d.Active, take: 0));
    }

    [Fact]
    public async Task QueryAsync_ComputedProperty_RejectedByFake_MatchingRealStore()
    {
        var store = await Seed();

        // The fake must reject a computed (unpersisted) column, because the real store would silently
        // match nothing — the whole point of routing the fake through the same translator.
        await Assert.ThrowsAsync<NotSupportedException>(() => store.QueryAsync(d => d.IsOpenComputed));
    }
}
