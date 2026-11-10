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
    public void Assemble_IgnoresNonChunkCellsOnParts()
    {
        var plan = SplitRowLayout.Plan(Bag("rk", ("Body", new string('a', 600_000))));
        var part = plan.Parts[0];
        // Cells a stray merge could leave on a continuation row, including one the primary also has.
        part["Name"] = "injected";
        part["UpdatedAt"] = DateTimeOffset.UnixEpoch;
        part["Body__sha256"] = "0000";

        var logical = SplitRowLayout.Assemble(plan.Primary, [part]);

        Assert.Equal("n", logical["Name"]);
        Assert.False(logical.ContainsKey("UpdatedAt"));
        Assert.Equal(plan.Primary["Body__sha256"], logical["Body__sha256"]);
        Assert.False(logical.ContainsKey("_SplitOf"));
        Assert.False(logical.ContainsKey("_SplitIndex"));
        Assert.Equal(new string('a', 600_000), (string)SplitCodec.Decode("Body", logical.ToDictionary(k => k.Key, k => k.Value)));
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
    public void ValidateRowKey_RejectsOverlongKeys()
    {
        SplitRowLayout.ValidateRowKey(new string('k', 480));
        Assert.Throws<ArgumentException>(() => SplitRowLayout.ValidateRowKey(new string('k', 481)));
    }

    [Theory]
    [InlineData("a~b")]
    [InlineData("~h0000000000000000001")]
    [InlineData("x~12")]
    [InlineData("x~123")]
    [InlineData("x~12345")]
    [InlineData("a~0001x")]
    [InlineData("a~000a")]
    [InlineData("a~")]
    [InlineData("~")]
    [InlineData("a~~b")]
    [InlineData("a٠١٢٣")]
    [InlineData("a~٠١٢٣")] // non-ASCII digits are not a part index
    [InlineData("0001")]
    public void ValidateRowKey_AllowsTildeOutsideTheContinuationSuffix(string rowKey)
    {
        Assert.False(SplitRowLayout.IsReservedRowKey(rowKey));
        SplitRowLayout.ValidateRowKey(rowKey);
    }

    [Fact]
    public void ValidateRowKey_AllowsAppendLogKeys()
    {
        var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        SplitRowLayout.ValidateRowKey(RowKeys.AppendKey(at, subStream: "chat"));
        SplitRowLayout.ValidateRowKey(RowKeys.AppendKey(at, subStream: "chat~7", uniquifier: "0001"));
    }

    [Theory]
    [InlineData("r~0001")]
    [InlineData("r~0000")]
    [InlineData("r~9999")]
    [InlineData("~0001")]
    [InlineData("a~b~0042")]
    public void ValidateRowKey_RejectsTheContinuationSuffix_WithAClearMessage(string rowKey)
    {
        Assert.True(SplitRowLayout.IsReservedRowKey(rowKey));
        var ex = Assert.Throws<ArgumentException>(() => SplitRowLayout.ValidateRowKey(rowKey));
        Assert.Contains("'~' followed by exactly 4 digits", ex.Message, StringComparison.Ordinal);
        Assert.Contains(rowKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPartKeyThePlannerWrites_HasTheReservedShape()
    {
        var plan = SplitRowLayout.Plan(Bag("a~b", ("Body", new string('a', 1_300_000))));

        Assert.NotEmpty(plan.Parts);
        Assert.False(SplitRowLayout.IsReservedRowKey(plan.Primary.RowKey));
        Assert.All(plan.Parts, p => Assert.True(SplitRowLayout.IsReservedRowKey(p.RowKey)));
    }

    [Fact]
    public void LargePrimary_FirstChunkMovesToPart_EveryRowWithinByteBudget()
    {
        var bag = new TableEntity("pk", "rk");
        for (var i = 0; i < 15; i++) bag[$"O{i}"] = new string('o', 32_000); // ~960,000 B ordinary
        bag["Extra"] = new string('e', 14_000);                                // ~990,000 B total
        for (var i = 0; i < 4; i++) bag[$"V__c{i}"] = new byte[65_536];
        bag["V__chunks"] = 4;
        var plan = SplitRowLayout.Plan(bag);
        Assert.NotEmpty(plan.Parts);
        Assert.True(TableRowSize.Estimate(plan.Primary) <= SplitRowLayout.MaxEntityBytes);
        foreach (var p in plan.Parts) Assert.True(TableRowSize.Estimate(p) <= SplitRowLayout.MaxEntityBytes);
    }

    [Theory]
    [InlineData("x")]
    [InlineData(-1)]
    public void Assemble_GarbledSplitParts_Throws(object garbled)
    {
        var primary = new TableEntity("pk", "rk") { ["_SplitParts"] = garbled };
        var ex = Assert.Throws<TableValueIntegrityException>(() => SplitRowLayout.Assemble(primary, []));
        Assert.Equal("_SplitParts", ex.ColumnName);
    }

    [Fact]
    public void Plan_NonSplittableCellsOverLimit_ThrowsOversized()
    {
        var bag = new TableEntity("pk", "rk");
        for (var i = 0; i < 40; i++) bag[$"O{i}"] = new string('o', 32_000); // 2.5 MB ordinary
        var ex = Assert.Throws<OversizedValueException>(() => SplitRowLayout.Plan(bag));
        Assert.Equal("rk", ex.ColumnName);
    }

    // ── packing a batch into transactions ──────────────────────────────────

    private static (SplitRowPlan Plan, int ExtraActions) Unit(string rk, string pk = "pk", int body = 10, int stale = 0)
    {
        var bag = new TableEntity(pk, rk) { ["Name"] = "n" };
        foreach (var cell in SplitCodec.Encode("Body", new string('a', body), SplitEncoding.Text))
            bag[cell.Key] = cell.Value;
        return (SplitRowLayout.Plan(bag), stale);
    }

    [Fact]
    public void Pack_SmallRows_OneHundredActionsPerTransaction()
    {
        var units = Enumerable.Range(0, 150).Select(i => Unit($"r{i:D3}")).ToList();

        var ranges = SplitRowLayout.PackTransactions(units);

        Assert.Equal([(0, 100), (100, 50)], ranges.Select(r => (r.Start, r.Count)));
    }

    [Fact]
    public void Pack_CountsContinuationRowsAndStaleDeletes_AndNeverSplitsAUnit()
    {
        // Two-row units of 1.2 MB each: two fit the 3,000,000 B budget, a third does not.
        var units = Enumerable.Range(0, 5).Select(i => Unit($"r{i}", body: 600_000)).ToList();
        Assert.All(units, u => Assert.Single(u.Plan.Parts));

        Assert.Equal([(0, 2), (2, 2), (4, 1)], SplitRowLayout.PackTransactions(units).Select(r => (r.Start, r.Count)));

        // A primary and 99 stale-part deletes are 100 actions: the next unit opens a transaction.
        var withStale = new List<(SplitRowPlan, int)> { Unit("a", stale: 99), Unit("b") };
        Assert.Equal([(0, 1), (1, 1)], SplitRowLayout.PackTransactions(withStale).Select(r => (r.Start, r.Count)));
    }

    [Fact]
    public void Pack_EscapedTextIsBudgetedOnItsWireSize()
    {
        // 300,000 Cyrillic chars: 600 KB estimated, 1.8 MB on the wire. Two fit the estimate budget
        // of one transaction but not the 4 MiB request limit together with a third.
        var units = Enumerable.Range(0, 3).Select(i =>
        {
            var bag = new TableEntity("pk", $"r{i}") { ["Name"] = "n" };
            foreach (var cell in SplitCodec.Encode("Body", new string('Ж', 300_000), SplitEncoding.Text))
                bag[cell.Key] = cell.Value;
            return (SplitRowLayout.Plan(bag), 0);
        }).ToList();

        var ranges = SplitRowLayout.PackTransactions(units);

        Assert.Equal([(0, 2), (2, 1)], ranges.Select(r => (r.Start, r.Count)));
    }

    [Fact]
    public void Pack_APartitionChangeStartsATransaction()
    {
        var units = new List<(SplitRowPlan, int)> { Unit("r1", "p1"), Unit("r2", "p1"), Unit("r1", "p2") };

        Assert.Equal([(0, 2), (2, 1)], SplitRowLayout.PackTransactions(units).Select(r => (r.Start, r.Count)));
    }
}
