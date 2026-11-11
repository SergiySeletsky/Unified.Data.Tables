using Azure.Data.Tables;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A plain type (its declared types opt into nothing) reads a row whose extra columns merely have the NAME of a
/// large-value cell (<c>{Column}__c{n}</c>, <c>__chunks</c>, <c>__sha256</c>, <c>__enc</c>, <c>__len</c>,
/// <c>__blob</c>), written by another program, exactly as the published 0.8.3 reads it: no exception, the
/// columns ignored or mapped as 0.8.3 maps them (<c>Stats__len</c> to <c>Stats.len</c>). A row that really holds
/// the library's large value keeps its documented behaviour: a plain reader reassembles complete split cells,
/// throws <see cref="TableValueIntegrityException"/> for a split value whose chunks are missing, and
/// <see cref="InvalidOperationException"/> for a genuine blob reference. For a large-value aware type an
/// incomplete group stays damage and throws. (Review finding SERIALIZER-2 of fix wave 2.)
/// </summary>
public class PlainTypeLookalikeColumnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Stats
    {
#pragma warning disable IDE1006, SA1300 // the member names of the foreign schema
        public int len { get; set; }

        public string? enc { get; set; }
#pragma warning restore IDE1006, SA1300
    }

    /// <summary>A plain type: nothing is opted in.</summary>
    public sealed class Note : Entity
    {
        public string? Body { get; set; }

        public Stats? Stats { get; set; }
    }

    /// <summary>The same columns, opted in.</summary>
    public sealed class SplitNote : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }
    }

    private static TableEntity Row(string column, object value) => new("p", "r")
    {
        ["Id"] = "p|r",
        ["Body"] = "text",
        [column] = value,
    };

    [Theory]
    [InlineData("Body__len", 4L)]
    [InlineData("Body__sha256", "982d9e3eb996f559e633f4d194def3761d909f5a3b647d1a851fead67c32c9d1")]
    [InlineData("Body__enc", "utf-8")]
    [InlineData("Body__chunks", 1)]
    [InlineData("Body__blob", "container/notes/r.txt")]
    [InlineData("Body__c0", "a cached copy")]
    public void PlainType_ReadsARowThatHasAnExtraColumnNamedLikeALargeValueCell_AsOn083(string column, object value)
    {
        var note = Row(column, value).FromTableEntity<Note>();

        Assert.Equal("text", note.Body);
    }

    [Fact]
    public void PlainType_MapsADoubleUnderscoreColumnToTheNestedMember_AsOn083()
    {
        Assert.Equal(5, new TableEntity("p", "r") { ["Id"] = "p|r", ["Stats__len"] = 5 }.FromTableEntity<Note>().Stats?.len);
        Assert.Equal("x", new TableEntity("p", "r") { ["Id"] = "p|r", ["Stats__enc"] = "x" }.FromTableEntity<Note>().Stats?.enc);
    }

    [Fact]
    public void PlainType_ReadsAManifestThatIsNotWellFormed_AsOn083()
    {
        // Every manifest name is there, but no cell holds what the library writes: not a large value.
        var row = new TableEntity("p", "r")
        {
            ["Id"] = "p|r",
            ["Body"] = "text",
            ["Body__chunks"] = "one",
            ["Body__sha256"] = "not a hash",
            ["Body__enc"] = "utf-8",
            ["Body__len"] = "4",
        };

        Assert.Equal("text", row.FromTableEntity<Note>().Body);
    }

    [Fact]
    public async Task PlainStore_ReadsSuchARow_AndItsPartition_AsOn083()
    {
        using var h = new StorageHarness<Note>();
        var stored = Row("Body__sha256", "982d9e3eb996f559e633f4d194def3761d909f5a3b647d1a851fead67c32c9d1");
        stored.ETag = new Azure.ETag("W/\"1\"");
        h.SetupGet(stored);
        h.SetupQueryByPartition(stored);

        Assert.Equal("text", (await h.Store.OneAsync("p|r", Ct))!.Body);
        Assert.Equal("text", Assert.Single(await h.Store.QueryAsync("p", Ct)).Body);
    }

    // ── what really is a large value keeps its documented behaviour ────────

    [Fact]
    public void PlainType_ReassemblesGenuineSplitCells()
    {
        var text = Ascii(40_000);
        var row = new TableEntity("p", "r") { ["Id"] = "p|r" };
        foreach (var (key, value) in SplitCodec.Encode("Body", text, SplitEncoding.Text))
            row[key] = value;

        Assert.Equal(text, row.FromTableEntity<Note>().Body);
    }

    [Fact]
    public void PlainType_GenuineSplitRowWhoseChunksAreAllOnContinuationRows_Throws()
    {
        // The main row of a value split across continuation rows: the manifest is there, no chunk is.
        var row = new TableEntity("p", "r") { ["Id"] = "p|r", ["Body"] = "a stale plain cell" };
        foreach (var (key, value) in SplitCodec.Encode("Body", Ascii(100_000), SplitEncoding.Text))
        {
            if (!key.StartsWith("Body__c", StringComparison.Ordinal) || key == "Body__chunks")
                row[key] = value;
        }
        Assert.False(row.ContainsKey("Body__c0"));

        Assert.Throws<TableValueIntegrityException>(() => row.FromTableEntity<Note>());
    }

    [Fact]
    public void PlainType_GenuineSplitRowMissingSomeChunks_Throws()
    {
        var row = new TableEntity("p", "r") { ["Id"] = "p|r" };
        foreach (var (key, value) in SplitCodec.Encode("Body", Ascii(100_000), SplitEncoding.Text))
        {
            if (key != "Body__c2")
                row[key] = value;
        }

        Assert.Throws<TableValueIntegrityException>(() => row.FromTableEntity<Note>());
    }

    [Fact]
    public void PlainType_GenuineBlobReference_Throws()
    {
        var reference = new Providers.BlobReference("T/p/r/Body/" + new string('a', 64), new string('a', 64), 10, "text/plain; charset=utf-8", "text");
        var row = new TableEntity("p", "r") { ["Id"] = "p|r", ["Body"] = "text", ["Body__blob"] = reference.ToJson() };

        var ex = Assert.Throws<InvalidOperationException>(() => row.FromTableEntity<Note>());
        Assert.Contains("'Body'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Body__len", 4L)]
    [InlineData("Body__sha256", "982d9e3eb996f559e633f4d194def3761d909f5a3b647d1a851fead67c32c9d1")]
    [InlineData("Body__chunks", 1)]
    [InlineData("Body__c0", "a cached copy")]
    public void AwareType_IncompleteGroup_IsDamage_AndThrows(string column, object value)
    {
        Assert.Throws<TableValueIntegrityException>(() => Row(column, value).FromTableEntity<SplitNote>());
    }

    [Fact]
    public void AwareType_BlobCellThatIsNotAReference_IsDamage_AndThrows()
    {
        Assert.Throws<InvalidOperationException>(() => Row("Body__blob", "container/notes/r.txt").FromTableEntity<SplitNote>());
    }
}
