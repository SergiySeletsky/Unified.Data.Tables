using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the row-size estimate now that two batch planners share it. The numbers are Azure's own
/// accounting (88 B per entity, 8 B per property) and are load-bearing: they decide how many rows a
/// transaction carries.
/// </summary>
public class TableRowSizeTests
{
    [Fact]
    public void Estimate_EmptyRow_IsEntityOverheadOnly()
    {
        Assert.Equal(88L, TableRowSize.Estimate(new TableEntity()));
    }

    [Fact]
    public void Estimate_StringColumn_CountsTwoBytesPerChar_ForNameAndValue()
    {
        var row = new TableEntity { ["Ab"] = "cde" };

        // 88 entity + 8 per-property + (2 name chars * 2) + (3 value chars * 2) = 88 + 8 + 4 + 6
        Assert.Equal(106L, TableRowSize.Estimate(row));
    }

    [Fact]
    public void Estimate_BinaryColumn_CountsRawLength()
    {
        var row = new TableEntity { ["B"] = new byte[10] };

        // 88 + 8 + (1 name char * 2) + 10 bytes
        Assert.Equal(108L, TableRowSize.Estimate(row));
    }

    [Fact]
    public void Estimate_ScalarColumn_CountsEightBytes()
    {
        var row = new TableEntity { ["N"] = 42 };

        // 88 + 8 + 2 + 8
        Assert.Equal(106L, TableRowSize.Estimate(row));
    }
}
