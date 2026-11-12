namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A positional record stored as JSON, marked <see cref="TableValueSplitAttribute"/> or not, reads back every
/// member on both stores, as one cell and as split cells (M1 of the PR #18 deep review: the members that are
/// not constructor parameters read back empty at every size).
/// </summary>
[Collection("OversizedCellPolicy")]
public class ConstructorRecordSplitValueTests
{
    private const string Table = "CtorRecordSplit";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record OrderRec(string Number)
    {
        public List<string> Lines { get; init; } = [];

        public string? Note { get; set; }
    }

    public sealed class MarkedOrder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public OrderRec? Order { get; set; }
    }

    public sealed class GzipOrder : Entity
    {
        [TableValueSplit(SplitEncoding.Gzip)]
        public OrderRec? Order { get; set; }
    }

    public sealed class PlainOrder : Entity
    {
        public OrderRec? Order { get; set; }
    }

    public static TheoryData<string, int> Cases()
    {
        var d = new TheoryData<string, int>();
        foreach (var kind in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        {
            d.Add(kind, 2);
            d.Add(kind, 4_000);
        }
        return d;
    }

    private static OrderRec Make(int lines) =>
        new("A-1") { Lines = [.. Enumerable.Range(0, lines).Select(i => "line-" + i.ToString("D6"))], Note = "n" };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task MarkedText_ReadsEveryMember(string kind, int lines)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<MarkedOrder>(Table);
        var id = stores.Id("r");
        var order = Make(lines);

        await store.CreateAsync(new MarkedOrder { Id = id, Order = order }, Ct);
        var read = (await store.OneAsync(id, Ct))!;

        Assert.Equal(order.Number, read.Order!.Number);
        Assert.Equal(order.Lines, read.Order.Lines);
        Assert.Equal("n", read.Order.Note);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task MarkedGzip_ReadsEveryMember(string kind, int lines)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<GzipOrder>(Table);
        var id = stores.Id("g");
        var order = Make(lines);

        await store.CreateAsync(new GzipOrder { Id = id, Order = order }, Ct);
        var read = (await store.OneAsync(id, Ct))!;

        Assert.Equal(order.Lines, read.Order!.Lines);
        Assert.Equal("n", read.Order.Note);
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task Unmarked_ReadsEveryMember(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<PlainOrder>(Table);
        var id = stores.Id("u");
        var order = Make(2);

        await store.CreateAsync(new PlainOrder { Id = id, Order = order }, Ct);
        var read = (await store.OneAsync(id, Ct))!;

        Assert.Equal(order.Lines, read.Order!.Lines);
        Assert.Equal("n", read.Order.Note);

        // The next write stores what was read: nothing is lost on the way round.
        await store.UpsertAsync(read, Ct);
        var again = (await store.OneAsync(id, Ct))!;
        Assert.Equal(order.Lines, again.Order!.Lines);
        Assert.Equal("n", again.Order.Note);
    }
}
