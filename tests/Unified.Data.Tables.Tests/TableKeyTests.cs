namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins <see cref="TableKey"/>'s bridge to the composite-id convention. The round-trip matters
/// because a polymorphic store addresses rows by key while the rest of the library addresses them
/// by id; the two spellings must agree or the same row gets two identities.
/// </summary>
public class TableKeyTests
{
    [Fact]
    public void FromId_CompositeId_SplitsOnFirstSeparator()
    {
        var key = TableKey.FromId("vision|execution|agent");

        Assert.Equal("vision", key.PartitionKey);
        Assert.Equal("execution|agent", key.RowKey);
    }

    [Fact]
    public void FromId_SingleSegmentId_UsesIdForBothKeys()
    {
        var key = TableKey.FromId("solo");

        Assert.Equal("solo", key.PartitionKey);
        Assert.Equal("solo", key.RowKey);
    }

    [Fact]
    public void ToId_EqualKeys_CollapsesToSingleSegment()
    {
        Assert.Equal("solo", new TableKey("solo", "solo").ToId());
    }

    [Fact]
    public void ToId_DistinctKeys_ProducesCompositeId()
    {
        Assert.Equal("agg-1|cmd-9", new TableKey("agg-1", "cmd-9").ToId());
    }

    [Theory]
    [InlineData("agg-1|cmd-9")]
    [InlineData("solo")]
    [InlineData("p|r|extra")]
    public void FromId_ThenToId_RoundTrips(string id)
    {
        Assert.Equal(id, TableKey.FromId(id).ToId());
    }

    [Fact]
    public void ToString_ReturnsTheComposeId()
    {
        Assert.Equal("agg-1|cmd-9", new TableKey("agg-1", "cmd-9").ToString());
    }

    [Fact]
    public void ToString_DefaultValue_IsNotNull()
    {
        // default(TableKey) has null keys, so ToId() handed back null — a null ToString() breaks the
        // object contract and NREs every message that interpolates a key.
        Assert.Equal("(default)", default(TableKey).ToString());
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(new TableKey("a", "b"), new TableKey("a", "b"));
        Assert.NotEqual(new TableKey("a", "b"), new TableKey("a", "c"));
    }

    [Fact]
    public void Equality_IsCaseSensitive_BecauseKeysAreUsedVerbatim()
    {
        Assert.NotEqual(new TableKey("A", "b"), new TableKey("a", "b"));
    }
}
