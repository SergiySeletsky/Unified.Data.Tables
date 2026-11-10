namespace Unified.Data.Tables.Tests.LargeValues;

public class BlobOffloadOptionsTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(4_194_305L)]
    [InlineData(long.MaxValue)]
    public void ThresholdBytes_OutsideOneToTheTransactionLimit_IsRefused(long value)
    {
        var options = new BlobOffloadOptions();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.ThresholdBytes = value);
        Assert.Equal(value, ex.ActualValue);
        Assert.Equal(3_145_728, options.ThresholdBytes);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(3_145_728L)]
    [InlineData(4_194_304L)]
    public void ThresholdBytes_WithinTheRange_IsKept(long value)
    {
        var options = new BlobOffloadOptions { ThresholdBytes = value };
        Assert.Equal(value, options.ThresholdBytes);
    }
}
