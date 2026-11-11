using System.Reflection;
using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Many opted-in values that each fit one cell: together they exceed one entity, so the planner moves the
/// largest of them to split form and their chunks go to continuation rows, until the first row fits; the
/// existing rules then decide about blob offload. A row that fits is stored as before. When nothing more
/// can be moved, the error names a real column and an accurate reason. On
/// <see cref="InMemoryStorage{T}"/> and on <see cref="TableStorage{T}"/> over Azurite.
/// </summary>
public class WideRowPromotionTests
{
    private const string Pk = "p";
    private const string Rk = "r";

    /// <summary>The estimated size one entity may have.</summary>
    private const long EntityBytes = 1_000_000;

    /// <summary>The request body one row of a split entity may have (the emulator limits a replaced entity by it).</summary>
    private const long EntityWireBytes = 1_000_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── models ─────────────────────────────────────────────────────────────

    /// <summary>Class-level opt-in: 24 text values.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class WideTextDoc : Entity
    {
        public string? P00 { get; set; }
        public string? P01 { get; set; }
        public string? P02 { get; set; }
        public string? P03 { get; set; }
        public string? P04 { get; set; }
        public string? P05 { get; set; }
        public string? P06 { get; set; }
        public string? P07 { get; set; }
        public string? P08 { get; set; }
        public string? P09 { get; set; }
        public string? P10 { get; set; }
        public string? P11 { get; set; }
        public string? P12 { get; set; }
        public string? P13 { get; set; }
        public string? P14 { get; set; }
        public string? P15 { get; set; }
        public string? P16 { get; set; }
        public string? P17 { get; set; }
        public string? P18 { get; set; }
        public string? P19 { get; set; }
        public string? P20 { get; set; }
        public string? P21 { get; set; }
        public string? P22 { get; set; }
        public string? P23 { get; set; }
        public int Version { get; set; }
    }

    /// <summary>The same values, none of which may ever be offloaded.</summary>
    [TableValueSplit(SplitEncoding.Text, Offload = BlobOffload.Never)]
    public sealed class WideNeverDoc : Entity
    {
        public string? P00 { get; set; }
        public string? P01 { get; set; }
        public string? P02 { get; set; }
        public string? P03 { get; set; }
        public string? P04 { get; set; }
        public string? P05 { get; set; }
        public string? P06 { get; set; }
        public string? P07 { get; set; }
        public string? P08 { get; set; }
        public string? P09 { get; set; }
        public string? P10 { get; set; }
        public string? P11 { get; set; }
        public string? P12 { get; set; }
        public string? P13 { get; set; }
        public string? P14 { get; set; }
        public string? P15 { get; set; }
        public string? P16 { get; set; }
        public string? P17 { get; set; }
        public string? P18 { get; set; }
        public string? P19 { get; set; }
        public string? P20 { get; set; }
        public string? P21 { get; set; }
        public string? P22 { get; set; }
        public string? P23 { get; set; }
    }

    /// <summary>Values stored gzip-compressed once they are split.</summary>
    [TableValueSplit(SplitEncoding.Gzip)]
    public sealed class WideGzipDoc : Entity
    {
        public string? P00 { get; set; }
        public string? P01 { get; set; }
        public string? P02 { get; set; }
        public string? P03 { get; set; }
        public string? P04 { get; set; }
        public string? P05 { get; set; }
        public string? P06 { get; set; }
        public string? P07 { get; set; }
        public string? P08 { get; set; }
        public string? P09 { get; set; }
        public string? P10 { get; set; }
        public string? P11 { get; set; }
        public string? P12 { get; set; }
        public string? P13 { get; set; }
        public string? P14 { get; set; }
        public string? P15 { get; set; }
        public string? P16 { get; set; }
        public string? P17 { get; set; }
        public string? P18 { get; set; }
        public string? P19 { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class WideBytesDoc : Entity
    {
        public byte[]? P00 { get; set; }
        public byte[]? P01 { get; set; }
        public byte[]? P02 { get; set; }
        public byte[]? P03 { get; set; }
        public byte[]? P04 { get; set; }
        public byte[]? P05 { get; set; }
        public byte[]? P06 { get; set; }
        public byte[]? P07 { get; set; }
        public byte[]? P08 { get; set; }
        public byte[]? P09 { get; set; }
        public byte[]? P10 { get; set; }
        public byte[]? P11 { get; set; }
        public byte[]? P12 { get; set; }
        public byte[]? P13 { get; set; }
        public byte[]? P14 { get; set; }
        public byte[]? P15 { get; set; }
        public byte[]? P16 { get; set; }
        public byte[]? P17 { get; set; }
        public byte[]? P18 { get; set; }
        public byte[]? P19 { get; set; }
    }

    /// <summary>JSON-stored collections.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class WideJsonDoc : Entity
    {
        public List<string>? P00 { get; set; }
        public List<string>? P01 { get; set; }
        public List<string>? P02 { get; set; }
        public List<string>? P03 { get; set; }
        public List<string>? P04 { get; set; }
        public List<string>? P05 { get; set; }
        public List<string>? P06 { get; set; }
        public List<string>? P07 { get; set; }
        public List<string>? P08 { get; set; }
        public List<string>? P09 { get; set; }
        public List<string>? P10 { get; set; }
        public List<string>? P11 { get; set; }
        public List<string>? P12 { get; set; }
        public List<string>? P13 { get; set; }
        public List<string>? P14 { get; set; }
        public List<string>? P15 { get; set; }
        public List<string>? P16 { get; set; }
        public List<string>? P17 { get; set; }
        public List<string>? P18 { get; set; }
        public List<string>? P19 { get; set; }
    }

    /// <summary>One opted-in value beside twenty ordinary ones, which nothing can move off the row.</summary>
    public sealed class WideMixedDoc : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }

        public string? N00 { get; set; }
        public string? N01 { get; set; }
        public string? N02 { get; set; }
        public string? N03 { get; set; }
        public string? N04 { get; set; }
        public string? N05 { get; set; }
        public string? N06 { get; set; }
        public string? N07 { get; set; }
        public string? N08 { get; set; }
        public string? N09 { get; set; }
        public string? N10 { get; set; }
        public string? N11 { get; set; }
        public string? N12 { get; set; }
        public string? N13 { get; set; }
        public string? N14 { get; set; }
        public string? N15 { get; set; }
        public string? N16 { get; set; }
        public string? N17 { get; set; }
        public string? N18 { get; set; }
        public string? N19 { get; set; }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>The numbered properties (<c>P00</c>, <c>N07</c>...) of <typeparamref name="T"/>, in name order.</summary>
    private static PropertyInfo[] Numbered<T>() =>
        typeof(T).GetProperties()
            .Where(p => p.Name.Length == 3 && char.IsAsciiLetterUpper(p.Name[0]) && char.IsAsciiDigit(p.Name[1]) && char.IsAsciiDigit(p.Name[2]))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>An entity whose first <paramref name="count"/> numbered properties hold <paramref name="value"/> of their index.</summary>
    private static T Fill<T>(string id, int count, Func<int, object> value) where T : Entity, new()
    {
        var entity = new T { Id = id };
        var properties = Numbered<T>();
        for (var i = 0; i < count; i++)
            properties[i].SetValue(entity, value(i));
        return entity;
    }

    private static void AssertSameValues<T>(T expected, T? actual)
    {
        Assert.NotNull(actual);
        foreach (var property in Numbered<T>())
        {
            switch (property.GetValue(expected))
            {
                case string text:
                    Assert.Equal(text, Assert.IsType<string>(property.GetValue(actual)), StringComparer.Ordinal);
                    break;
                case byte[] bytes:
                    Assert.Equal(bytes, Assert.IsType<byte[]>(property.GetValue(actual)));
                    break;
                case List<string> list:
                    Assert.Equal(list, Assert.IsType<List<string>>(property.GetValue(actual)));
                    break;
                default:
                    Assert.Null(property.GetValue(actual));
                    break;
            }
        }
    }

    private static string Cyrillic(int length, int seed)
    {
        const string Letters = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
        var random = new Random(seed);
        return string.Create(length, random, (span, rng) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = Letters[rng.Next(Letters.Length)];
        });
    }

    /// <summary>A list whose JSON is about 30,000 characters.</summary>
    private static List<string> Items(int seed) =>
        Enumerable.Range(0, 290).Select(i => $"{seed:D2}-{i:D4}-" + new string((char)('a' + (i % 26)), 92)).ToList();

    /// <summary>The columns stored in split form: those with a <c>__chunks</c> cell on the first row.</summary>
    private static List<string> SplitColumns(TableEntity primary) =>
        primary.Keys.Where(k => k.EndsWith("__chunks", StringComparison.Ordinal))
            .Select(k => k[..^"__chunks".Length])
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

    private static int CustomProperties(TableEntity row) =>
        row.Keys.Count(k => k is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag"));

    /// <summary>Every stored row is within the limits of one entity, and the first row declares the others.</summary>
    private static void AssertRowsWithinEntityLimits(IReadOnlyList<TableEntity> rows)
    {
        Assert.All(rows, row =>
        {
            Assert.InRange(TableRowSize.Estimate(row), 1, EntityBytes);
            Assert.InRange(CustomProperties(row), 1, 252);
        });
        if (rows.Count > 1)
            Assert.Equal(rows.Count - 1, rows[0]["_SplitParts"]);
        for (var i = 1; i < rows.Count; i++)
            Assert.Equal(rows[0].RowKey, rows[i]["_SplitOf"]);
    }

    // ── stored and read back ───────────────────────────────────────────────

    public static TheoryData<string, string, int, int> WideRows()
    {
        var data = new TheoryData<string, string, int, int>();
        foreach (var store in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        {
            // (kind, values, how many of them must leave the first row)
            data.Add(store, "text", 20, 4);    // 20 x 30,000 chars: the review finding
            data.Add(store, "text", 17, 1);    // just over one entity
            data.Add(store, "text", 24, 8);
            // 20 x 60,000 bytes: four must leave for the stored size, four more for the request size
            // (base64: twelve cells of 80,000 characters are what one request body of 1 MB holds).
            data.Add(store, "bytes", 20, 8);
            data.Add(store, "json", 20, 4);
            data.Add(store, "gzip", 20, 4);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(WideRows))]
    public async Task ManyValuesThatEachFitOneCell_AreStored_AndReadBackEqual(string store, string kind, int count, int moved)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);

        var stored = kind switch
        {
            "text" => StoredAndReadBackAsync(stores!, Fill<WideTextDoc>(stores!.Id("r1"), count, i => Ascii(30_000, seed: i + 1)), moved, "text"),
            "bytes" => StoredAndReadBackAsync(stores!, Fill<WideBytesDoc>(stores!.Id("r1"), count, i => RandomBytes(60_000, seed: i + 1)), moved, "binary"),
            "json" => StoredAndReadBackAsync(stores!, Fill<WideJsonDoc>(stores!.Id("r1"), count, i => Items(i)), moved, "text"),
            _ => StoredAndReadBackAsync(stores!, Fill<WideGzipDoc>(stores!.Id("r1"), count, i => Ascii(30_000, seed: i + 1)), moved, "gzip"),
        };
        await stored;
    }

    private static async Task StoredAndReadBackAsync<T>(LargeValueStores stores, T entity, int moved, string encoding)
        where T : Entity, new()
    {
        var id = entity.Id;
        await stores.Store<T>().CreateAsync(entity, Ct);

        // The first row keeps as many values in their cells as one entity holds; the others are split
        // values, each one chunk, placed on the rows as any chunk is (continuation rows, and the first
        // row where it has room). Every value uses its property's encoding. No blob is involved.
        var rows = await stores.RawRowsAsync<T>();
        Assert.InRange(rows.Count, 2, 99);
        AssertRowsWithinEntityLimits(rows);
        Assert.All(rows, row => Assert.InRange(TableRowSize.WireSize(row), 1, EntityWireBytes));
        var split = SplitColumns(rows[0]);
        Assert.Equal(moved, split.Count);
        Assert.All(split, column =>
        {
            Assert.Equal(1, rows[0][column + "__chunks"]);
            Assert.Equal(encoding, rows[0][column + "__enc"]);
            Assert.False(rows[0].ContainsKey(column));
            Assert.False(rows[0].ContainsKey(column + "__Json"));
            Assert.Single(rows, row => row.ContainsKey(column + "__c0"));
        });
        Assert.Contains(rows.Skip(1), part => part.Keys.Any(k => k.EndsWith("__c0", StringComparison.Ordinal)));
        Assert.Empty(await stores.BlobNamesAsync<T>());

        AssertSameValues(entity, await stores.Store<T>().OneAsync(id, Ct));
        AssertSameValues(entity, Assert.Single(await stores.Store<T>().QueryAsync(stores.Pk, Ct)));
        Assert.Equal(1, await stores.Store<T>().CountAsync(stores.Pk, Ct));

        // Save it again as read: the same rows.
        var read = await stores.Store<T>().OneAsync(id, Ct);
        await stores.Store<T>().UpdateAsync(read!, Ct);
        AssertSameValues(entity, await stores.Store<T>().OneAsync(id, Ct));
        var again = await stores.RawRowsAsync<T>();
        Assert.Equal(rows.Count, again.Count);
        Assert.Equal(split, SplitColumns(again[0]));

        await stores.Store<T>().DeleteAsync(id, Ct);
        Assert.Empty(await stores.RawRowsAsync<T>());
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task WithoutBlobOffload_TheValuesStillMoveToContinuationRows(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, offload: false);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var entity = Fill<WideTextDoc>(stores!.Id("r1"), 20, i => Ascii(30_000, seed: i + 1));

        await stores.Store<WideTextDoc>().CreateAsync(entity, Ct);

        Assert.Equal(2, (await stores.RawRowsAsync<WideTextDoc>()).Count);
        AssertSameValues(entity, await stores.Store<WideTextDoc>().OneAsync(entity.Id, Ct));
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task RowThatFits_IsStoredExactlyAsBefore_EveryValueInItsOwnCell(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var entity = Fill<WideTextDoc>(stores!.Id("r1"), 16, i => Ascii(30_000, seed: i + 1));

        await stores.Store<WideTextDoc>().CreateAsync(entity, Ct);

        var row = Assert.Single(await stores.RawRowsAsync<WideTextDoc>());
        Assert.Empty(SplitColumns(row));
        Assert.False(row.ContainsKey("_SplitParts"));
        Assert.DoesNotContain(row.Keys, k => SplitCodec.TryParseLargeValueColumn(k, out _, out _, out _));
        foreach (var property in Numbered<WideTextDoc>().Take(16))
            Assert.Equal((string)property.GetValue(entity)!, row[property.Name]);
        AssertSameValues(entity, await stores.Store<WideTextDoc>().OneAsync(entity.Id, Ct));
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task RowThatFitsOneEntity_IsNotTouched_HoweverLargeItsRequestIs(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        // 15 x 60,000 bytes: 900 KB stored, 1.2 MB as base64. The row fits one entity, so every value
        // stays in its cell, as before (the service measures the stored size).
        var entity = Fill<WideBytesDoc>(stores!.Id("r1"), 15, i => RandomBytes(60_000, seed: i + 1));

        await stores.Store<WideBytesDoc>().CreateAsync(entity, Ct);

        var row = Assert.Single(await stores.RawRowsAsync<WideBytesDoc>());
        Assert.Empty(SplitColumns(row));
        Assert.InRange(TableRowSize.Estimate(row), 1, EntityBytes);
        Assert.InRange(TableRowSize.WireSize(row), EntityWireBytes + 1, long.MaxValue);
        AssertSameValues(entity, await stores.Store<WideBytesDoc>().OneAsync(entity.Id, Ct));
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task GrowingAndShrinking_MovesValuesToContinuationRowsAndBack(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        await stores.Store<WideTextDoc>().CreateAsync(Fill<WideTextDoc>(id, 16, i => Ascii(30_000, seed: i + 1)), Ct);
        Assert.Single(await stores.RawRowsAsync<WideTextDoc>());

        // Four more values: the row no longer fits one entity.
        var grown = await stores.Store<WideTextDoc>().OneAsync(id, Ct);
        var properties = Numbered<WideTextDoc>();
        for (var i = 16; i < 20; i++)
            properties[i].SetValue(grown, Ascii(30_000, seed: i + 1));
        grown!.Version = 2;
        await stores.Store<WideTextDoc>().UpdateAsync(grown, Ct);
        Assert.Equal(2, (await stores.RawRowsAsync<WideTextDoc>()).Count);
        var readGrown = await stores.Store<WideTextDoc>().OneAsync(id, Ct);
        AssertSameValues(grown, readGrown);
        Assert.Equal(2, readGrown!.Version);

        // Back to sixteen: one row again, every value in its own cell, no continuation row left.
        for (var i = 16; i < 20; i++)
            properties[i].SetValue(readGrown, null);
        await stores.Store<WideTextDoc>().UpdateAsync(readGrown, Ct);
        var row = Assert.Single(await stores.RawRowsAsync<WideTextDoc>());
        Assert.Empty(SplitColumns(row));
        Assert.False(row.ContainsKey("_SplitParts"));
        AssertSameValues(readGrown, await stores.Store<WideTextDoc>().OneAsync(id, Ct));
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task BatchOfWideRows_IsStored_AndReadBackEqual(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var entities = Enumerable.Range(0, 3)
            .Select(n => Fill<WideTextDoc>(stores!.Id("r" + n), 18 + n, i => Ascii(30_000, seed: (n * 100) + i + 1)))
            .ToList();

        Assert.Equal(3, await stores!.Store<WideTextDoc>().UpsertBatchAsync(entities, Ct));

        foreach (var entity in entities)
            AssertSameValues(entity, await stores.Store<WideTextDoc>().OneAsync(entity.Id, Ct));
        Assert.Equal(3, await stores.Store<WideTextDoc>().CountAsync(stores.Pk, Ct));
    }

    // ── the existing rules then decide about blob offload ──────────────────

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task MovedValues_AboveTheThreshold_GoToBlobs(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, thresholdBytes: 1_000);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var entity = Fill<WideTextDoc>(stores!.Id("r1"), 20, i => Ascii(30_000, seed: i + 1));

        await stores.Store<WideTextDoc>().CreateAsync(entity, Ct);

        // A value that fits one cell stays in its cell whatever the threshold; the four that had to leave
        // the row are split values, and a split value above the threshold is offloaded.
        var row = Assert.Single(await stores.RawRowsAsync<WideTextDoc>());
        Assert.Equal(4, row.Keys.Count(k => k.EndsWith("__blob", StringComparison.Ordinal)));
        Assert.Equal(16, Numbered<WideTextDoc>().Count(p => row.ContainsKey(p.Name)));
        Assert.Equal(4, (await stores.BlobNamesAsync<WideTextDoc>()).Count);
        AssertSameValues(entity, await stores.Store<WideTextDoc>().OneAsync(entity.Id, Ct));
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task RowTooLargeForOneTransaction_OffloadsMovedValues_UntilItFits(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        // 24 x 32,000 Cyrillic characters: 1.5 MB stored, 4.6 MB as the request sends them (six bytes each).
        var entity = Fill<WideTextDoc>(stores!.Id("r1"), 24, i => Cyrillic(32_000, seed: i + 1));

        await stores.Store<WideTextDoc>().CreateAsync(entity, Ct);

        // Nineteen values leave the first row (nine for the stored size, ten more for the request size);
        // their chunks do not all fit one transaction beside it, so the largest go to blobs.
        var rows = await stores.RawRowsAsync<WideTextDoc>();
        AssertRowsWithinEntityLimits(rows);
        Assert.All(rows, row => Assert.InRange(TableRowSize.WireSize(row), 1, EntityWireBytes));
        Assert.Equal(5, Numbered<WideTextDoc>().Count(p => rows[0].ContainsKey(p.Name)));
        var blobs = await stores.BlobNamesAsync<WideTextDoc>();
        Assert.InRange(blobs.Count, 1, 19);
        Assert.Equal(blobs.Count, rows[0].Keys.Count(k => k.EndsWith("__blob", StringComparison.Ordinal)));
        AssertSameValues(entity, await stores.Store<WideTextDoc>().OneAsync(entity.Id, Ct));
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task NeverOffload_OnEveryValue_AndNoRoomInOneTransaction_ThrowsOffloadForbiddenByProperty(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var entity = Fill<WideNeverDoc>(stores!.Id("r1"), 24, i => Cyrillic(32_000, seed: i + 1));

        var ex = await Assert.ThrowsAsync<OversizedValueException>(() => stores.Store<WideNeverDoc>().CreateAsync(entity, Ct));

        Assert.True(ex.OffloadForbiddenByProperty);
        Assert.Contains(ex.ColumnName, Numbered<WideNeverDoc>().Select(p => p.Name));
        Assert.Contains("BlobOffload.Never", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<WideNeverDoc>());
        Assert.Empty(await stores.BlobNamesAsync<WideNeverDoc>());
    }

    // ── nothing more can be moved ──────────────────────────────────────────

    [Theory]
    [InlineData(LargeValueStores.Memory, true)]
    [InlineData(LargeValueStores.Memory, false)]
    [InlineData(LargeValueStores.Azurite, true)]
    [InlineData(LargeValueStores.Azurite, false)]
    public async Task OrdinaryCellsThatAloneExceedOneEntity_Throw_NamingTheLargestOfThem(string store, bool offload)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, offload: offload);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var entity = Fill<WideMixedDoc>(stores!.Id("r1"), 20, i => Ascii(i == 7 ? 32_000 : 30_000, seed: i + 1));
        entity.Body = Ascii(30_000, seed: 99);

        var ex = await Assert.ThrowsAsync<OversizedValueException>(() => stores.Store<WideMixedDoc>().CreateAsync(entity, Ct));

        // The opted-in value was moved; what is left cannot be. The error names the largest cell that is
        // left, not the row key, and does not send the reader to blob offload, which cannot help here.
        Assert.Equal("N07", ex.ColumnName);
        Assert.Equal(64_000, ex.EncodedBytes);
        Assert.False(ex.OffloadForbiddenByProperty);
        Assert.StartsWith("Property 'N07' encodes to 64,000 B and cannot be stored: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1,000,000", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[TableValueSplit]", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BlobOffload", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'r1'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<WideMixedDoc>());
        Assert.Empty(await stores.BlobNamesAsync<WideMixedDoc>());
    }

    // ── the planner on its own ─────────────────────────────────────────────

    private static ValueOffloadCoordinator Coordinator(bool offload = true) => offload
        ? new ValueOffloadCoordinator(new InMemoryOffloadBlobStore(), new BlobOffloadOptions(), "T")
        : new ValueOffloadCoordinator(null, null, "T");

    [Fact]
    public void TheLargestValuesMoveFirst_AndOnlyAsManyAsItTakes()
    {
        // Seventeen values of 30,000 characters and three larger ones: 1.21 MB.
        var entity = Fill<WideTextDoc>("p|r", 20, i => Ascii(i switch { 5 => 32_000, 2 => 31_500, 11 => 31_000, _ => 30_000 }, seed: i + 1));

        var (row, plan, blobs) = Coordinator().Serialize(entity, Pk, Rk, 0);

        // The three larger ones free 188 KB, 21 KB short: one more goes, the first of the equal ones.
        Assert.Equal(["P00", "P02", "P05", "P11"], SplitColumns(row));
        Assert.Empty(blobs);
        Assert.Single(plan.Parts);
        Assert.InRange(TableRowSize.Estimate(plan.Primary), EntityBytes - 60_000, EntityBytes);
        Assert.Equal(16, Numbered<WideTextDoc>().Count(p => row.ContainsKey(p.Name)));
        AssertSameValues(entity, SplitRowLayout.Assemble(plan.Primary, plan.Parts).FromTableEntity<WideTextDoc>());
    }

    [Fact]
    public void ValueAlreadyTooLargeForOneCell_AndInlineOnes_ShareTheContinuationRows()
    {
        var entity = Fill<WideTextDoc>("p|r", 18, i => Ascii(30_000, seed: i + 1));
        entity.P20 = Ascii(400_000, seed: 77);

        var (row, plan, blobs) = Coordinator().Serialize(entity, Pk, Rk, 0);

        Assert.Equal(13, row["P20__chunks"]);
        // 18 x 60 KB do not fit beside the manifests: two inline values leave the first row as well.
        Assert.Equal(["P00", "P01", "P20"], SplitColumns(row));
        Assert.Empty(blobs);
        Assert.True(SplitRowLayout.FitsOneTransaction(plan, 0));
        AssertSameValues(entity, SplitRowLayout.Assemble(plan.Primary, plan.Parts).FromTableEntity<WideTextDoc>());
    }

    [Fact]
    public void ThePublicSerializer_AndTheCacheCopy_KeepEveryValueInItsCell()
    {
        var entity = Fill<WideTextDoc>("p|r", 20, i => Ascii(30_000, seed: i + 1));

        // No rows are planned here: the bag is unbounded, as before.
        Assert.Empty(SplitColumns(entity.ToTableEntity(Pk, Rk)));
        Assert.Empty(SplitColumns(TableEntitySerializer.ToTableEntity(entity, Pk, Rk, ValueWriteContext.ForClone, pending: null)));
    }

    [Fact]
    public void Plan_CellsThatCannotMove_Throw_NamingTheLargestOfThem_AndBothLimits()
    {
        var bag = new TableEntity("pk", "rk");
        for (var i = 0; i < 40; i++) bag[$"O{i:D2}"] = new string('o', i == 13 ? 32_000 : 30_000);
        bag["Tags__Json"] = new string('j', 31_000);

        var ex = Assert.Throws<OversizedValueException>(() => SplitRowLayout.Plan(bag));

        Assert.Equal("O13", ex.ColumnName);
        Assert.Equal(64_000, ex.EncodedBytes);
        Assert.Contains("42 properties", ex.Message, StringComparison.Ordinal);
        Assert.Contains("252 properties", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1,000,000", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BlobOffload", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'rk'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_TooManyCells_Throw_NamingARealColumn()
    {
        var bag = new TableEntity("pk", "rk");
        for (var i = 0; i < 300; i++) bag[$"C{i:D3}"] = i == 150 ? "the largest" : "x";

        var ex = Assert.Throws<OversizedValueException>(() => SplitRowLayout.Plan(bag));

        Assert.Equal("C150", ex.ColumnName);
        Assert.Contains("301 properties", ex.Message, StringComparison.Ordinal);
        Assert.Contains("252 properties", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BlobOffload", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_NamesTheColumnOfAJsonOrGZipCell_NotTheCell()
    {
        var bag = new TableEntity("pk", "rk");
        for (var i = 0; i < 40; i++) bag[$"O{i:D2}"] = new string('o', 30_000);
        bag["Tags__Json"] = new string('j', 32_000);

        Assert.Equal("Tags", Assert.Throws<OversizedValueException>(() => SplitRowLayout.Plan(bag)).ColumnName);
    }

    [Fact]
    public void ChoosePromotions_TakesTheLargestGainsFirst_UntilTheRowFits()
    {
        var overflow = new PrimaryOverflow(Properties: 30, Bytes: 1_150_000, LargestColumn: "A", LargestBytes: 1);
        (string, long)[] candidates = [("small", 10_000), ("big", 90_000), ("tie1", 50_000), ("tie2", 50_000), ("loss", -20), ("mid", 60_000)];

        Assert.Equal(["big", "mid"], SplitRowLayout.ChoosePromotions(overflow, candidates));
        Assert.Equal(
            ["big", "mid", "tie1"],
            SplitRowLayout.ChoosePromotions(overflow with { Bytes = 1_150_001 }, candidates));
        // Everything that frees bytes goes when even that is not enough; the caller then reports what is left.
        Assert.Equal(
            ["big", "mid", "tie1", "tie2", "small"],
            SplitRowLayout.ChoosePromotions(overflow with { Bytes = 5_000_000 }, candidates));
    }

    [Fact]
    public void RequestBudget_IsAppliedOnlyToARowThatHadToBeMoved_LargestRequestSizeFirst()
    {
        // Twelve Cyrillic values of 30,000 characters and eight ASCII ones: 1.2 MB stored. Four values
        // leave for the stored size (the first four: all are equal there). The first row then still sends
        // eight Cyrillic cells of 180,000 bytes each and eight ASCII ones of 30,000: four more Cyrillic
        // ones leave, the largest on the wire, and the row is within 1 MB.
        var entity = Fill<WideTextDoc>("p|r", 20, i => i < 12 ? Cyrillic(30_000, seed: i + 1) : new string('a', 30_000));

        var (row, plan, _) = Coordinator().Serialize(entity, Pk, Rk, 0);

        Assert.Equal(["P00", "P01", "P02", "P03", "P04", "P05", "P06", "P07"], SplitColumns(row));
        Assert.InRange(TableRowSize.WireSize(plan.Primary), 1, EntityWireBytes);
        Assert.All(plan.Parts, part => Assert.InRange(TableRowSize.WireSize(part), 1, EntityWireBytes));
        AssertSameValues(entity, SplitRowLayout.Assemble(plan.Primary, plan.Parts).FromTableEntity<WideTextDoc>());

        // The same Cyrillic values in a row that fits one entity (eleven of them: 660 KB stored, 2 MB
        // as the request sends them) stay in their cells.
        var fits = Fill<WideTextDoc>("p|r", 11, i => Cyrillic(30_000, seed: i + 1));
        var (inlineRow, inlinePlan, _) = Coordinator().Serialize(fits, Pk, Rk, 0);
        Assert.Empty(SplitColumns(inlineRow));
        Assert.Empty(inlinePlan.Parts);
    }

    [Fact]
    public void ChoosePromotions_ForTheRequestBudget_TakesTheLargestGainsFirst_WithinTheProperties()
    {
        (string, long)[] candidates = [("a", 40_000), ("b", 180_000), ("c", 180_000), ("d", 0)];

        Assert.Equal(["b"], SplitRowLayout.ChoosePromotions(properties: 30, excess: 100_000, candidates));
        Assert.Equal(["b", "c", "a"], SplitRowLayout.ChoosePromotions(properties: 30, excess: 400_000, candidates));
        Assert.Equal(["b"], SplitRowLayout.ChoosePromotions(properties: 247, excess: 400_000, candidates));
        Assert.Empty(SplitRowLayout.ChoosePromotions(properties: 30, excess: 0, candidates));
    }

    [Fact]
    public void ChoosePromotions_StopsWhereTheRowHasNoPropertiesLeft_AndMovesNothingWhenTheCountIsTheCause()
    {
        (string, long)[] candidates = [("a", 50_000), ("b", 50_000), ("c", 50_000)];

        // A moved value leaves four manifest cells where its one cell was: three more properties each.
        var tight = new PrimaryOverflow(Properties: 247, Bytes: 1_140_000, LargestColumn: "A", LargestBytes: 1);
        Assert.Equal(["a"], SplitRowLayout.ChoosePromotions(tight, candidates));
        Assert.Empty(SplitRowLayout.ChoosePromotions(tight with { Properties = 250 }, candidates));

        // Too many properties: moving a value only adds some.
        var tooMany = new PrimaryOverflow(Properties: 260, Bytes: 500_000, LargestColumn: "A", LargestBytes: 1);
        Assert.Empty(SplitRowLayout.ChoosePromotions(tooMany, candidates));
    }

    [Fact]
    public void PrimaryOverflowError_SaysWhenValuesCouldNotLeaveForLackOfProperties()
    {
        var overflow = new PrimaryOverflow(Properties: 251, Bytes: 1_100_000, LargestColumn: "Big", LargestBytes: 64_000);

        var plain = SplitRowLayout.PrimaryOverflowError(overflow, movable: 0);
        var blocked = SplitRowLayout.PrimaryOverflowError(overflow, movable: 3);

        Assert.Equal("Big", plain.ColumnName);
        Assert.Equal(64_000, plain.EncodedBytes);
        Assert.False(plain.OffloadForbiddenByProperty);
        Assert.Contains("251 properties, 1,100,000 estimated bytes", plain.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("more [TableValueSplit] value", plain.Message, StringComparison.Ordinal);
        Assert.Contains("3 more [TableValueSplit] value(s)", blocked.Message, StringComparison.Ordinal);
        Assert.Contains("no property left", blocked.Message, StringComparison.Ordinal);
        Assert.EndsWith("or store less in one entity.", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RowThatDoesNotFitOneTransactionBecauseOfStaleRows_Throws_NamingARealColumn()
    {
        var coordinator = Coordinator();
        var entity = new SplitTextEntity { Id = "p|r", Title = "t", Body = Ascii(500) };

        // One small row and the deletes of 100 continuation rows: 101 actions.
        var ex = Assert.Throws<OversizedValueException>(() => coordinator.Serialize(entity, Pk, Rk, extraActions: 100));

        Assert.Equal("Body", ex.ColumnName);
        Assert.Contains("100", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BlobOffload", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'r'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RowTooLargeForOneTransaction_WithoutBlobOffload_StillAdvisesToConfigureIt()
    {
        // The advice is right here: with blob offload the moved values could go to blobs.
        var entity = Fill<WideTextDoc>("p|r", 24, i => Cyrillic(32_000, seed: i + 1));

        var ex = Assert.Throws<OversizedValueException>(() => Coordinator(offload: false).Serialize(entity, Pk, Rk, 0));

        Assert.Contains(ex.ColumnName, Numbered<WideTextDoc>().Select(p => p.Name));
        Assert.False(ex.OffloadForbiddenByProperty);
        Assert.Contains("blob offload is not configured", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Configure UnifiedTableStorageOptions.BlobOffload", ex.Message, StringComparison.Ordinal);
    }
}
