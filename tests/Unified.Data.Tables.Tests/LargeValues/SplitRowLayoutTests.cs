using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests.LargeValues;

public class SplitRowLayoutTests
{
    private static TableEntity Bag(string rk, params (string Column, object Payload)[] values)
    {
        var e = new TableEntity("pk", rk) { ["Name"] = "n" };
        foreach (var (column, payload) in values)
            foreach (var cell in SplitCodec.Encode(column, payload, SplitEncoding.Text))
                e[cell.Key] = cell.Value;
        return e;
    }

    private static int Custom(TableEntity e) =>
        e.Keys.Count(k => k is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag"));

    [Fact]
    public void NoSplitCells_IsSingleRow_AndUnchanged()
    {
        var bag = new TableEntity("pk", "rk") { ["A"] = 1 };
        var plan = SplitRowLayout.Plan(bag);
        Assert.Same(bag, plan.Primary);
        Assert.Empty(plan.Parts);
        Assert.False(plan.Primary.ContainsKey("_SplitParts"));
    }

    [Fact]
    public void EightTextCells_FitOneEntity()
    {
        var plan = SplitRowLayout.Plan(Bag("rk", ("Body", new string('a', 254_000))));
        Assert.Empty(plan.Parts);
        Assert.False(plan.Primary.ContainsKey("_SplitParts"));
    }

    [Fact]
    public void TwoEntities_OneTransaction()
    {
        var plan = SplitRowLayout.Plan(Bag("rk", ("Body", new string('a', 600_000))));
        var part = Assert.Single(plan.Parts);
        Assert.Equal("rk~0001", part.RowKey);
        Assert.Equal("rk", part["_SplitOf"]);
        Assert.Equal(1, part["_SplitIndex"]);
        Assert.Equal(1, plan.Primary["_SplitParts"]);
        Assert.True(SplitRowLayout.FitsOneTransaction(plan, 0));
        Assert.True(TableRowSize.Estimate(plan.Primary) <= SplitRowLayout.MaxEntityBytes);
        Assert.True(TableRowSize.Estimate(part) <= SplitRowLayout.MaxEntityBytes);
    }

    [Fact]
    public void PropertyCountLimit_ForcesAPart()
    {
        var bag = new TableEntity("pk", "rk");
        for (var i = 0; i < 300; i++) bag[$"V__c{i}"] = "x";
        var plan = SplitRowLayout.Plan(bag);
        Assert.NotEmpty(plan.Parts);
        Assert.True(Custom(plan.Primary) <= 252);
        foreach (var p in plan.Parts) Assert.True(Custom(p) <= 252);
        var total = Custom(plan.Primary) - 1 + plan.Parts.Sum(p => Custom(p) - 2);
        Assert.Equal(300, total);
    }

    [Fact]
    public void TwoMillionChars_DoesNotFitOneTransaction()
    {
        var plan = SplitRowLayout.Plan(Bag("rk", ("Body", new string('a', 2_000_000))));
        Assert.True(plan.Parts.Count > 1);
        Assert.False(SplitRowLayout.FitsOneTransaction(plan, 0));
    }

    [Fact]
    public void FitsOneTransaction_CountsExtraActions()
    {
        var parts = Enumerable.Range(1, 3).Select(i => new TableEntity("pk", $"rk~000{i}")).ToList();
        var plan = new SplitRowPlan(new TableEntity("pk", "rk"), parts);
        Assert.True(SplitRowLayout.FitsOneTransaction(plan, 96));
        Assert.False(SplitRowLayout.FitsOneTransaction(plan, 97));
    }

    [Fact]
    public void Assemble_IgnoresPartsBeyondCount_AndOtherRows()
    {
        var plan = SplitRowLayout.Plan(Bag("rk", ("Body", new string('a', 600_000))));
        var extra2 = new TableEntity("pk", "rk~0002") { ["_SplitOf"] = "rk", ["_SplitIndex"] = 2, ["Junk"] = 1 };
        var other = new TableEntity("pk", "rkx~0001") { ["_SplitOf"] = "rkx", ["_SplitIndex"] = 1, ["Junk2"] = 1 };
        var logical = SplitRowLayout.Assemble(plan.Primary, [plan.Parts[0], extra2, other]);
        Assert.False(logical.ContainsKey("Junk"));
        Assert.False(logical.ContainsKey("Junk2"));
        Assert.Equal(new string('a', 600_000), (string)SplitCodec.Decode("Body", logical.ToDictionary(k => k.Key, k => k.Value)));
        Assert.Equal("rk", logical.RowKey);
    }

    [Fact]
    public void Assemble_MissingPart_Throws()
    {
        var primary = new TableEntity("pk", "rk") { ["_SplitParts"] = 5 };
        var parts = new[] { 1, 2, 4, 5 }.Select(i =>
            new TableEntity("pk", $"rk~000{i}") { ["_SplitOf"] = "rk", ["_SplitIndex"] = i });
        var ex = Assert.Throws<TableValueIntegrityException>(() => SplitRowLayout.Assemble(primary, parts));
        Assert.Contains("part 3 of 5 missing", ex.Reason);
    }

    [Fact]
    public void ValidateRowKey_RejectsTilde_AndOverlongKeys()
    {
        SplitRowLayout.ValidateRowKey(new string('k', 480));
        Assert.Throws<ArgumentException>(() => SplitRowLayout.ValidateRowKey("a~b"));
        Assert.Throws<ArgumentException>(() => SplitRowLayout.ValidateRowKey(new string('k', 481)));
    }
}
