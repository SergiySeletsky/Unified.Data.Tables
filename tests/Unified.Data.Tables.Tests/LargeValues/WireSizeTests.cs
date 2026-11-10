using System.Text.Json;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The wire size a large-value transaction is planned on: the Azure SDK JSON-escapes every non-ASCII
/// character and the HTML-sensitive ASCII ones as <c>\uXXXX</c>, so a cell of Cyrillic or of <c>&lt;</c>
/// is six times its character count on the wire, not the two bytes per character the storage estimate counts.
/// </summary>
public class WireSizeTests
{
    private static TableEntity Bag(string rk, string column, string text)
    {
        var e = new TableEntity("pk", rk) { ["Name"] = "n" };
        foreach (var cell in SplitCodec.Encode(column, text, SplitEncoding.Text))
            e[cell.Key] = cell.Value;
        return e;
    }

    [Theory]
    [InlineData("abc", 3)]
    [InlineData("ж", 6)]
    [InlineData("<", 6)]
    [InlineData(">&'+`\"\\", 42)]
    [InlineData("\u0001", 6)]
    [InlineData("😀", 12)]
    public void EscapedLength_CountsSixBytesPerEscapedChar(string text, long expected) =>
        Assert.Equal(expected, TableRowSize.EscapedJsonLength(text));

    [Theory]
    [InlineData('a')]
    [InlineData('ж')]
    [InlineData('<')]
    [InlineData('&')]
    [InlineData('"')]
    [InlineData('é')]
    public void WireSize_IsNeverBelowWhatTheDefaultJsonEncoderWrites(char c)
    {
        var text = new string(c, 32_000);
        var row = new TableEntity("pk", "rk") { ["Body__c0"] = text, ["N"] = 1, ["L"] = 5L, ["B"] = new byte[1000] };
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["PartitionKey"] = "pk", ["RowKey"] = "rk", ["Body__c0"] = text, ["N"] = 1,
            ["L"] = "5", ["L@odata.type"] = "Edm.Int64", ["B"] = new byte[1000], ["B@odata.type"] = "Edm.Binary",
        });
        Assert.True(TableRowSize.WireSize(row) >= json.Length, $"{TableRowSize.WireSize(row)} < {json.Length}");
    }

    [Fact]
    public void CyrillicCell_IsSixTimesItsLengthOnTheWire()
    {
        var row = new TableEntity("pk", "rk") { ["Body__c0"] = new string('ж', 32_000) };
        Assert.True(TableRowSize.WireSize(row) >= 192_000);
        Assert.True(TableRowSize.Estimate(row) < 70_000); // the storage estimate stays two bytes per char
    }

    [Theory]
    [InlineData('ж', 700_000)]
    [InlineData('ж', 1_200_000)]
    [InlineData('<', 700_000)]
    [InlineData('<', 1_200_000)]
    public void EscapedText_ThatOverflowsTheWireLimit_DoesNotFitOneTransaction(char c, int length)
    {
        var plan = SplitRowLayout.Plan(Bag("rk", "Body", new string(c, length)));
        Assert.False(SplitRowLayout.FitsOneTransaction(plan, 0));
    }

    [Fact]
    public void AsciiText_ThatFitsTheEstimate_StillFitsOneTransaction()
    {
        // The wire measure only ever adds a constraint: an ASCII value is planned exactly as before.
        var plan = SplitRowLayout.Plan(Bag("rk", "Body", new string('a', 1_200_000)));
        Assert.True(SplitRowLayout.FitsOneTransaction(plan, 0));
    }

    [Fact]
    public void EscapedText_WithinTheWireLimit_StillSplits()
    {
        var plan = SplitRowLayout.Plan(Bag("rk", "Body", new string('ж', 300_000)));
        Assert.True(SplitRowLayout.FitsOneTransaction(plan, 0));
    }

    [Theory]
    [InlineData('ж')]
    [InlineData('<')]
    public void EscapedText_IsPlacedWithinTheEntityWireBudget(char c)
    {
        // The emulator limits an entity by its request body: split cells keep every row within it.
        var plan = SplitRowLayout.Plan(Bag("rk", "Body", new string(c, 600_000)));
        Assert.True(plan.Parts.Count >= 3);
        Assert.True(TableRowSize.WireSize(plan.Primary) <= SplitRowLayout.MaxEntityWireBytes);
        foreach (var part in plan.Parts) Assert.True(TableRowSize.WireSize(part) <= SplitRowLayout.MaxEntityWireBytes);
    }

    [Fact]
    public void AsciiText_KeepsItsRowLayout()
    {
        var plan = SplitRowLayout.Plan(Bag("rk", "Body", new string('a', 600_000)));
        Assert.Single(plan.Parts);
    }

    [Fact]
    public void ExtraActions_CountTowardTheByteBudget()
    {
        // A plan just under the wire budget stops fitting once stale-row deletes are added to it.
        var lo = 1;
        var hi = 700_000;
        while (hi - lo > 1)
        {
            var mid = lo + ((hi - lo) / 2);
            if (SplitRowLayout.FitsOneTransaction(SplitRowLayout.Plan(Bag("rk", "Body", new string('ж', mid))), 0)) lo = mid; else hi = mid;
        }
        var plan = SplitRowLayout.Plan(Bag("rk", "Body", new string('ж', lo)));
        Assert.True(SplitRowLayout.FitsOneTransaction(plan, 0));
        Assert.False(SplitRowLayout.FitsOneTransaction(plan, 10));
    }

    [Theory]
    [InlineData('ж', 700_000)]
    [InlineData('<', 1_200_000)]
    public void Coordinator_OffloadsEscapedText_ThatWouldOverflowTheWire(char c, int length)
    {
        var coordinator = new ValueOffloadCoordinator(new InMemory.InMemoryOffloadBlobStore(), new BlobOffloadOptions(), "T");
        var (row, plan, blobs) = coordinator.Serialize(new SplitTextEntity { Body = new string(c, length) }, "pk", "rk", 0);
        Assert.Single(blobs);
        Assert.Empty(plan.Parts);
        Assert.True(row.ContainsKey("Body__blob"));
    }

    [Fact]
    public void Coordinator_WithoutOffload_RefusesEscapedTextThatOverflowsTheWire()
    {
        var coordinator = new ValueOffloadCoordinator(null, null, "T");
        Assert.Throws<OversizedValueException>(() =>
            coordinator.Serialize(new SplitTextEntity { Body = new string('ж', 700_000) }, "pk", "rk", 0));
    }
}
