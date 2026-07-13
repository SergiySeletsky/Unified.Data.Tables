using System.Text;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Azure Table Storage rejects any string cell over 64&#160;KB (UTF-16) with PropertyValueTooLarge,
/// and the WHOLE entity insert fails. These regression tests pin the serializer's guarantee that
/// every produced cell stays within the limit — losslessly GZip-compressed when possible, truncated
/// as a last resort — so a single oversized property can never silently lose the whole row.
/// These tests assume the default TrimWithMarker policy; they share a collection with everything
/// that mutates the static <see cref="TableEntitySerializer.OversizedCellPolicy"/> so a policy
/// change in a parallel test can never leak in.
/// </summary>
[Collection("OversizedCellPolicy")]
public class TableEntitySerializerSizeTests
{
    private const int MaxCellBytes = 65536;

    private static int CellBytes(object? cell) => cell is string s ? Encoding.Unicode.GetByteCount(s) : 0;

    [Fact]
    public void OversizedCompressibleString_IsStoredWithinCellLimit_AndRoundTripsLosslessly()
    {
        // 100K+ chars of repetitive text — far over the 32K-char cell limit, compresses well.
        var content = string.Concat(Enumerable.Repeat("The Product Manager Agent created a feature. ", 2300));
        var entity = new EntityWithContent { Content = content, Number = 7 }.ToTableEntity("pk", "rk");

        foreach (var kv in entity)
            Assert.True(CellBytes(kv.Value) <= MaxCellBytes,
                $"cell '{kv.Key}' must fit the Azure Tables 64KB property limit or the whole row is lost");

        var back = entity.FromTableEntity<EntityWithContent>();
        Assert.Equal(content, back.Content);
        Assert.Equal(7, back.Number);
    }

    [Fact]
    public void OversizedIncompressibleString_IsTruncatedButRowSurvives()
    {
        // Pseudo-random content (fixed seed) barely compresses — even gzip can't fit it in a cell.
        var rng = new Random(42);
        var chars = new char[600_000];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (char)rng.Next(0x21, 0x2FA0);
        var content = new string(chars);

        var entity = new EntityWithContent { Content = content }.ToTableEntity("pk", "rk");

        foreach (var kv in entity)
            Assert.True(CellBytes(kv.Value) <= MaxCellBytes,
                $"cell '{kv.Key}' must fit the 64KB property limit even for incompressible content");

        var back = entity.FromTableEntity<EntityWithContent>();
        Assert.NotNull(back.Content);
        Assert.Contains("[truncated", back.Content);
        Assert.True(back.Content!.Length < content.Length);
    }

    [Fact]
    public void TruncationBoundary_DoesNotSplitSurrogatePairs()
    {
        // Incompressible filler so the gzip fallback can't fit the cell and truncation kicks in,
        // with an emoji (surrogate pair) straddling the 30,000-char truncation boundary.
        var rng = new Random(7);
        var chars = new char[29_999];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (char)rng.Next(0x21, 0x2FA0);
        var content = new string(chars) + "😀" +
            string.Concat(Enumerable.Range(0, 300_000).Select(_ => (char)rng.Next(0x21, 0x2FA0)));

        var entity = new EntityWithContent { Content = content }.ToTableEntity("pk", "rk");

        // The stored cell must be valid UTF-16 end to end: a lone surrogate (split pair) makes the
        // Azure SDK's wire encoding corrupt or reject the value.
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        foreach (var kv in entity)
        {
            if (kv.Value is string cell)
            {
                var ex = Record.Exception(() => strictUtf8.GetBytes(cell));
                Assert.Null(ex);
            }
        }

        Assert.Contains("[truncated", entity.FromTableEntity<EntityWithContent>().Content);
    }

    [Fact]
    public void OversizedComplexListProperty_IsTrimmedButRowSurvives()
    {
        // A huge high-entropy list blows the 64KB cap even gzip-compressed — the JSON fallback must
        // trim the list to the largest fitting prefix instead of producing a cell the service
        // rejects (which would lose the whole row).
        var rng = new Random(11);
        string RandomBlob()
        {
            var c = new char[4000];
            for (var i = 0; i < c.Length; i++)
                c[i] = (char)rng.Next(0x21, 0x2FA0);
            return new string(c);
        }

        var entity = new EntityWithSteps
        {
            Steps = Enumerable.Range(0, 200).Select(_ => RandomBlob()).ToList(),
            Number = 3
        }.ToTableEntity("pk", "rk");

        foreach (var kv in entity)
            Assert.True(CellBytes(kv.Value) <= MaxCellBytes,
                $"cell '{kv.Key}' must fit the cell limit even for huge list properties");

        var back = entity.FromTableEntity<EntityWithSteps>();
        Assert.Equal(3, back.Number);
        Assert.NotNull(back.Steps);
    }

    [Fact]
    public void SmallString_StaysRawAndUnchanged()
    {
        var entity = new EntityWithContent { Content = "hello world" }.ToTableEntity("pk", "rk");

        Assert.True(entity.ContainsKey("Content"));
        Assert.Equal("hello world", entity["Content"]);
        Assert.Equal("hello world", entity.FromTableEntity<EntityWithContent>().Content);
    }
}
