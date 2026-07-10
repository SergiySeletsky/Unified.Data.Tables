namespace Unified.Data.Tables.Tests;

/// <summary>
/// RowKey ordering encoders: Azure Tables returns lexical RowKey order, so "most recent first"
/// must be encoded — <see cref="RowKeys.InvertedTicks"/> makes later timestamps sort first.
/// </summary>
public class RowKeysTests
{
    [Fact]
    public void InvertedTicks_LaterTimestamps_SortLexicallyFirst()
    {
        var earlier = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.True(string.CompareOrdinal(RowKeys.InvertedTicks(later), RowKeys.InvertedTicks(earlier)) < 0);
    }

    [Fact]
    public void Ticks_EarlierTimestamps_SortLexicallyFirst()
    {
        var earlier = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.True(string.CompareOrdinal(RowKeys.Ticks(earlier), RowKeys.Ticks(later)) < 0);
    }

    [Fact]
    public void InvertedTicks_IsFixedWidth_SoLexicalOrderIsNumericOrder()
    {
        Assert.Equal(19, RowKeys.InvertedTicks(DateTimeOffset.UnixEpoch).Length);
        Assert.Equal(19, RowKeys.InvertedTicks(DateTimeOffset.MaxValue).Length);
    }

    [Fact]
    public void InvertedTicks_RoundTrips()
    {
        var timestamp = new DateTimeOffset(2026, 7, 10, 15, 30, 45, TimeSpan.Zero);

        Assert.Equal(timestamp, RowKeys.FromInvertedTicks(RowKeys.InvertedTicks(timestamp)));
    }
}
