using System.Runtime.Serialization;
using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A large value that cannot be set on its property (the property's type changed since the row was
/// written) is reported with at most 200 characters of the value and its total length, never with the
/// whole value. A value that came from one ordinary cell is reported as on 0.8.3.
/// </summary>
public class LargeValueSetFailureMessageTests
{
    private const string Pk = "p";
    private const string Rk = "r";
    private const string Id = "p|r";

    /// <summary>The longest message a bounded failure may have: the column, 200 characters, the length and the wording.</summary>
    private const int MessageBound = 320;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The type that wrote the row.</summary>
    public class SetFailureTextDoc : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }
    }

    /// <summary>The same column read through a property a string cannot be set on.</summary>
    public class SetFailureNumberDoc : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public List<int>? Body { get; set; }
    }

    /// <summary>The same column read through a plain numeric property.</summary>
    public class SetFailurePlainNumberDoc : Entity
    {
        public int Body { get; set; }
    }

    /// <summary>A binary value, and the same column read through a property bytes cannot be set on.</summary>
    public class SetFailureBytesDoc : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public byte[]? Image { get; set; }
    }

    /// <summary>The binary column read as text.</summary>
    public class SetFailureBytesAsTextDoc : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Image { get; set; }
    }

    private static void AssertBounded(SerializationException ex, string value)
    {
        Assert.StartsWith("Failed to set 'Body=", ex.Message, StringComparison.Ordinal);
        Assert.Contains(value[..200], ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value[..201], ex.Message, StringComparison.Ordinal);
        Assert.Contains(value.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        Assert.InRange(ex.Message.Length, 1, MessageBound);
        Assert.NotNull(ex.InnerException);
    }

    [Theory]
    [InlineData(40_000)]
    [InlineData(700_000)]
    public void SplitValue_ThatCannotBeSet_IsReportedWith200CharactersAndItsLength(int length)
    {
        var body = Ascii(length);
        var row = new SetFailureTextDoc { Id = Id, Body = body }.ToTableEntity(Pk, Rk);
        Assert.True(row.ContainsKey("Body__chunks"));

        AssertBounded(Assert.Throws<SerializationException>(() => row.FromTableEntity<SetFailureNumberDoc>()), body);
        AssertBounded(Assert.Throws<SerializationException>(() => row.FromTableEntity<SetFailurePlainNumberDoc>()), body);
    }

    [Fact]
    public void SplitValue_CutBetweenASurrogatePair_KeepsThePairWhole()
    {
        // The 200th and 201st characters are one code point: the excerpt ends before it.
        var body = string.Concat(new string('a', 199), "😀", Ascii(40_000));
        var row = new SetFailureTextDoc { Id = Id, Body = body }.ToTableEntity(Pk, Rk);

        var ex = Assert.Throws<SerializationException>(() => row.FromTableEntity<SetFailureNumberDoc>());

        Assert.Contains(new string('a', 199), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\uD83D", ex.Message, StringComparison.Ordinal);
        Assert.InRange(ex.Message.Length, 1, MessageBound);
    }

    [Fact]
    public async Task OffloadedValue_ThatCannotBeSet_IsReportedBounded()
    {
        var body = Ascii(3_500_000);
        var options = new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions(), TableNameResolver = _ => "SetFailure" };
        var writer = new InMemoryStorage<SetFailureTextDoc>(null, options);
        await writer.CreateAsync(new SetFailureTextDoc { Id = Id, Body = body }, Ct);
        var blobName = Assert.Single(writer.OffloadedBlobNames());

        // The same row and blob, read through the type whose property cannot take a string.
        var reader = new InMemoryStorage<SetFailureNumberDoc>(null, options);
        reader.SeedPhysicalRow(writer.PhysicalRow(Pk, Rk)!);
        await using (var stored = await writer.Blobs.OpenReadAsync("Body", blobName, Ct))
            reader.Blobs.Seed(blobName, await ReadAllAsync(stored), Sha(Utf8(body)), DateTimeOffset.UtcNow);

        AssertBounded(await Assert.ThrowsAsync<SerializationException>(() => reader.OneAsync(Id, Ct)), body);
    }

    [Fact]
    public void SplitBytes_ThatCannotBeSet_AreReportedWithTheirLength()
    {
        var row = new SetFailureBytesDoc { Id = Id, Image = RandomBytes(100_000) }.ToTableEntity(Pk, Rk);
        Assert.True(row.ContainsKey("Image__chunks"));

        var ex = Assert.Throws<SerializationException>(() => row.FromTableEntity<SetFailureBytesAsTextDoc>());

        Assert.Equal("Failed to set 'Image=System.Byte[] (100,000 bytes)'.", ex.Message);
    }

    [Fact]
    public void ValueOfOneOrdinaryCell_ThatCannotBeSet_IsReportedAsOn083()
    {
        // 0.8.3 names the whole cell; a cell holds at most 32,768 characters, so this was never unbounded.
        var text = Ascii(300);
        var row = new TableEntity(Pk, Rk) { ["Body"] = text };

        var ex = Assert.Throws<SerializationException>(() => row.FromTableEntity<SetFailurePlainNumberDoc>());

        Assert.Equal($"Failed to set 'Body={text}'.", ex.Message);
    }

    [Fact]
    public void ShortSplitValue_ThatCannotBeSet_IsReportedWhole()
    {
        // A value written in split form although it fits one cell (not well-formed UTF-16): nothing to cut.
        var body = "ab\uD800cd";
        var row = new SetFailureTextDoc { Id = Id, Body = body }.ToTableEntity(Pk, Rk);
        Assert.Equal("utf16le", row["Body__enc"]);

        var ex = Assert.Throws<SerializationException>(() => row.FromTableEntity<SetFailureNumberDoc>());

        Assert.Equal($"Failed to set 'Body={body}'.", ex.Message);
    }
}
