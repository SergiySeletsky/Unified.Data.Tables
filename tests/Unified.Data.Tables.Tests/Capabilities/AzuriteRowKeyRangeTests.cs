using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.Capabilities;

/// <summary>
/// 0.10.0 against the local Azurite: a RowKey range over a table that holds continuation rows. A bound
/// selects entities by the key of their main row; the continuation rows of a large value are fetched by
/// the read that assembles it, whatever the bounds, and never come back as entities, from a bounded scan
/// or from a projected one of another type on the same table (break B6).
/// </summary>
public sealed class AzuriteRowKeyRangeTests : AzuriteCleanupTestBase
{
    private const int TwoRows = 600_000; // characters: more than one row holds, so the value has continuation rows

    private async Task<TableStorage<SplitTextEntity>> SeededAsync()
    {
        var store = SplitOnlyStore<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = Id("r1"), Title = "one", Body = new string('a', TwoRows) }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = Id("r2"), Title = "two", Body = "small" }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = Id("r3"), Title = "three", Body = new string('c', TwoRows) }, Ct);
        // r1 and r3 each have a main row and continuation rows ("r1~0001", ...), which sort after "r1" and before "r2".
        Assert.True((await RawRowsAsync(nameof(SplitTextEntity))).Count > 3);
        return store;
    }

    [Fact]
    public async Task Azurite_AnUpperBound_BetweenAMainRowAndItsContinuationRows_ReturnsTheEntityWhole()
    {
        SkipUnlessAvailable();
        var store = await SeededAsync();

        // "r1~" lies after the main row "r1" and before its first continuation row "r1~0001".
        var upTo = await store.QueryAsync(new QueryOptions { Partition = Pk, ToRowKey = "r1~" }, Ct);

        var one = Assert.Single(upTo);
        Assert.Equal(Id("r1"), one.Id);
        Assert.Equal(TwoRows, one.Body!.Length);
    }

    [Fact]
    public async Task Azurite_ARangeThatHoldsOnlyContinuationRows_IsEmpty_AndTheyNeverCountTowardTake()
    {
        SkipUnlessAvailable();
        var store = await SeededAsync();

        var onlyParts = await store.QueryAsync(new QueryOptions { Partition = Pk, FromRowKey = "r1~", ToRowKey = "r2" }, Ct);
        var firstAfter = await store.QueryAsync(new QueryOptions { Partition = Pk, FromRowKey = "r1~", Take = 1 }, Ct);
        var paged = await store.QueryPageAsync(new QueryOptions { Partition = Pk, FromRowKey = "r1", FromInclusive = false, ToRowKey = "r3", ToInclusive = true }, Ct);
        var streamed = new List<string>();
        await foreach (var entity in store.QueryStreamAsync(new QueryOptions { Partition = Pk, FromRowKey = "r2", Select = ["Title"] }, Ct))
            streamed.Add(entity.Id);

        Assert.Empty(onlyParts);
        Assert.Equal([Id("r2")], firstAfter.Select(e => e.Id));
        Assert.Equal([Id("r2"), Id("r3")], paged.Items.Select(e => e.Id));
        Assert.Equal(TwoRows, paged.Items[1].Body!.Length);
        Assert.Equal([Id("r2"), Id("r3")], streamed);
    }

    [Fact]
    public async Task Azurite_AProjectedScan_OfATypeWithoutLargeValues_OnTheSameTable_ReturnsNoContinuationRow()
    {
        SkipUnlessAvailable();
        await SeededAsync();
        // Another type, without large values, reading the table the split-aware type writes.
        var plain = Store<SimpleEntity>(table: nameof(SplitTextEntity));

        var projected = await plain.QueryAsync(new QueryOptions { Partition = Pk, Select = ["Name"] }, Ct);
        var bounded = await plain.QueryAsync(new QueryOptions { Partition = Pk, Select = ["Name"], FromRowKey = "r1", ToRowKey = "r2" }, Ct);
        var whole = await plain.QueryAsync(new QueryOptions { Partition = Pk }, Ct);

        Assert.Equal([Id("r1"), Id("r2"), Id("r3")], projected.Select(e => e.Id));
        Assert.Equal([Id("r1")], bounded.Select(e => e.Id));
        // As a scan of whole rows has always answered.
        Assert.Equal(projected.Select(e => e.Id), whole.Select(e => e.Id));
    }
}
