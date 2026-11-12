using System.Security.Cryptography;
using System.Text;

namespace Unified.Data.Tables.Tests.LargeValues;

public class SplitCodecTests
{
    private static Dictionary<string, object> Cells(IReadOnlyList<KeyValuePair<string, object>> list) =>
        list.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Encode_Text_ProducesReadableStringCells_NoneLongerThan32000()
    {
        var s = new string('a', 70_001);
        var cells = Cells(SplitCodec.Encode("Body", s, SplitEncoding.Text));

        Assert.Equal(3, (int)cells["Body__chunks"]);
        Assert.Equal("text", cells["Body__enc"]);
        Assert.Equal(70_001L, cells["Body__len"]);
        for (var i = 0; i < 3; i++)
        {
            var chunk = Assert.IsType<string>(cells[$"Body__c{i}"]);
            Assert.True(chunk.Length <= 32_000);
        }
    }

    [Fact]
    public void Encode_Text_NeverSplitsASurrogatePair()
    {
        var s = new string('a', 31_999) + "😀" + new string('b', 10);
        var cells = Cells(SplitCodec.Encode("Body", s, SplitEncoding.Text));

        var first = (string)cells["Body__c0"];
        Assert.False(char.IsHighSurrogate(first[^1]));
        Assert.Equal(s, (string)SplitCodec.Decode("Body", cells), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(SplitEncoding.Text, true)]
    [InlineData(SplitEncoding.Gzip, true)]
    [InlineData(SplitEncoding.Text, false)]
    [InlineData(SplitEncoding.Gzip, false)]
    public void IllFormedString_IsStoredAsUtf16le_AndRoundTripsExactly(SplitEncoding encoding, bool atEnd)
    {
        var s = atEnd
            ? new string('a', 40_000) + '\uD800'
            : new string('a', 31_999) + '\uD800' + new string('b', 20_000);
        Assert.False(SplitCodec.IsWellFormedUtf16(s));

        var cells = Cells(SplitCodec.Encode("Body", s, encoding));

        Assert.Equal("utf16le", cells["Body__enc"]);
        Assert.IsType<byte[]>(cells["Body__c0"]);
        Assert.Equal(s, (string)SplitCodec.Decode("Body", cells), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(SplitEncoding.Text)]
    [InlineData(SplitEncoding.Gzip)]
    public void Encode_Bytes_AlwaysBinaryCells(SplitEncoding encoding)
    {
        var bytes = new byte[200_000];
        new Random(7).NextBytes(bytes);
        var cells = Cells(SplitCodec.Encode("Body", bytes, encoding));

        Assert.Equal("binary", cells["Body__enc"]);
        var n = (int)cells["Body__chunks"];
        Assert.Equal(4, n);
        for (var i = 0; i < n; i++)
            Assert.True(Assert.IsType<byte[]>(cells[$"Body__c{i}"]).Length <= 65_536);
        Assert.Equal(bytes, (byte[])SplitCodec.Decode("Body", cells));
    }

    [Fact]
    public void Encode_Gzip_StoresBinaryCells_NotBase64()
    {
        var rng = new Random(3);
        var sb = new StringBuilder();
        for (var i = 0; i < 300_000; i++) sb.Append((char)('a' + rng.Next(26)));
        var s = sb.ToString();
        var cells = Cells(SplitCodec.Encode("Body", s, SplitEncoding.Gzip));

        Assert.Equal("gzip", cells["Body__enc"]);
        Assert.IsType<byte[]>(cells["Body__c0"]);
        Assert.Equal(s, (string)SplitCodec.Decode("Body", cells), StringComparer.Ordinal);
    }

    [Fact]
    public void Sha256_IsOverStoredBytes_SameForSplitAndStored()
    {
        var s = new string('x', 70_000);
        var cells = Cells(SplitCodec.Encode("Body", s, SplitEncoding.Text));
        var stored = SplitCodec.ToStored(s, SplitEncoding.Text);

        Assert.Equal(stored.Sha256, cells["Body__sha256"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant(), stored.Sha256);
        Assert.Equal("text", stored.Enc);
    }

    [Fact]
    public void Decode_CorruptChunk_Throws()
    {
        var cells = Cells(SplitCodec.Encode("Body", new string('a', 70_000), SplitEncoding.Text));
        cells["Body__c1"] = new string('b', ((string)cells["Body__c1"]).Length);

        var ex = Assert.Throws<TableValueIntegrityException>(() => SplitCodec.Decode("Body", cells));
        Assert.Equal("Body", ex.ColumnName);
    }

    [Fact]
    public void Decode_MissingChunk_Throws()
    {
        var cells = Cells(SplitCodec.Encode("Body", new string('a', 70_000), SplitEncoding.Text));
        cells.Remove("Body__c1");

        var ex = Assert.Throws<TableValueIntegrityException>(() => SplitCodec.Decode("Body", cells));
        Assert.Equal("Body", ex.ColumnName);
    }

    [Fact]
    public void Decode_WrongChunkCount_Throws()
    {
        var cells = Cells(SplitCodec.Encode("Body", new string('a', 70_000), SplitEncoding.Text));
        cells["Body__chunks"] = 2;

        var ex = Assert.Throws<TableValueIntegrityException>(() => SplitCodec.Decode("Body", cells));
        Assert.Equal("Body", ex.ColumnName);
    }

    [Fact]
    public void FromStored_HashMismatch_Throws()
    {
        var stored = SplitCodec.ToStored("hello", SplitEncoding.Text);
        var bad = new string('0', 64);

        var ex = Assert.Throws<TableValueIntegrityException>(() => SplitCodec.FromStored("Body", stored.Bytes, "text", bad));
        Assert.Equal("Body", ex.ColumnName);
        Assert.Equal("hello", SplitCodec.FromStored("Body", stored.Bytes, "text", stored.Sha256));
    }

    [Fact]
    public void FromStored_UnknownEncoding_Throws()
    {
        var stored = SplitCodec.ToStored("hello", SplitEncoding.Text);
        Assert.Throws<TableValueIntegrityException>(() => SplitCodec.FromStored("Body", stored.Bytes, "rot13", stored.Sha256));
    }

    [Fact]
    public void FitsOneCell_FollowsCellLimits()
    {
        Assert.True(SplitCodec.FitsOneCell(new string('a', 32_000), SplitEncoding.Text));
        Assert.False(SplitCodec.FitsOneCell(new string('a', 32_001), SplitEncoding.Text));
        Assert.False(SplitCodec.FitsOneCell("a" + '\uD800', SplitEncoding.Text));
        Assert.True(SplitCodec.FitsOneCell(new string('a', 32_000), SplitEncoding.Gzip));
        Assert.True(SplitCodec.FitsOneCell("hello", SplitEncoding.Gzip));
        Assert.False(SplitCodec.FitsOneCell(new string('a', 32_001), SplitEncoding.Gzip));
        Assert.False(SplitCodec.FitsOneCell("a" + '\uD800', SplitEncoding.Gzip));
        Assert.True(SplitCodec.FitsOneCell(new byte[65_536], SplitEncoding.Gzip));
        Assert.False(SplitCodec.FitsOneCell(new byte[65_537], SplitEncoding.Gzip));
    }

    [Fact]
    public void EmptyValues_RoundTrip()
    {
        Assert.Equal("", (string)SplitCodec.Decode("Body", Cells(SplitCodec.Encode("Body", "", SplitEncoding.Text))));
        Assert.Empty((byte[])SplitCodec.Decode("Body", Cells(SplitCodec.Encode("Body", Array.Empty<byte>(), SplitEncoding.Gzip))));
    }

    [Theory]
    [InlineData("Body__c12", "Body", "Chunk", 12)]
    [InlineData("Parent_Child__sha256", "Parent_Child", "Sha256", 0)]
    [InlineData("Body__chunks", "Body", "Count", 0)]
    [InlineData("Body__enc", "Body", "Encoding", 0)]
    [InlineData("Body__len", "Body", "Length", 0)]
    [InlineData("Body__blob", "Body", "BlobReference", 0)]
    public void TryParseLargeValueColumn_RecognisesItsSuffixes(string column, string baseColumn, string kind, int index)
    {
        Assert.True(SplitCodec.TryParseLargeValueColumn(column, out var b, out var k, out var i));
        Assert.Equal(baseColumn, b);
        Assert.Equal(kind, k.ToString());
        Assert.Equal(index, i);
    }

    [Theory]
    [InlineData("Body__Json")]
    [InlineData("Body__GZip")]
    [InlineData("Body__Truncated")]
    [InlineData("Body")]
    [InlineData("Body__c")]
    [InlineData("__c1")]
    public void TryParseLargeValueColumn_RejectsOthers(string column) =>
        Assert.False(SplitCodec.TryParseLargeValueColumn(column, out _, out _, out _));

    [Theory]
    [InlineData("Tags__Json")]
    [InlineData("Tags__GZip")]
    [InlineData("Tags__Truncated")]
    [InlineData("Plain")]
    [InlineData("Body__c")]
    public void TryParseLargeValueColumn_OnAColumnThatIsNotOurs_AllocatesNothing(string column)
    {
        // Asked of every column of every row read: a plain type's __Json/__GZip fallbacks must cost nothing.
        Assert.False(SplitCodec.TryParseLargeValueColumn(column, out _, out _, out _));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            SplitCodec.TryParseLargeValueColumn(column, out _, out _, out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ToTableEntity_WithOffloadButNoPendingList_IsRefused()
    {
        var ctx = new ValueWriteContext(new BlobOffloadOptions(), "T", new HashSet<string>(), IsClone: false);
        Assert.Throws<ArgumentNullException>(() =>
            TableEntitySerializer.ToTableEntity(new SplitTextEntity { Body = "x" }, "p", "r", ctx, pending: null));
        // A context without offload never produces a pending blob, so it needs no list.
        Assert.Equal("x", TableEntitySerializer.ToTableEntity(new SplitTextEntity { Body = "x" }, "p", "r", ValueWriteContext.ForClone, pending: null)["Body"]);
    }
}
