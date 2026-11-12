using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The read cache copies an entity at every put and every hit (so a caller mutating what it got never
/// changes the cache). The copy of a large value is its in-process value: it is never run through the split
/// codec (no gzip, no hash, no chunking and no reassembly), so a cache hit of a multi-megabyte Gzip value
/// costs a copy, not a compression and a decompression (review finding dyn-cache: an 8M-char Gzip hit took
/// 223-356 ms against 86-140 ms uncached). The copy is still isolated: what a hit returns is equal to what
/// was written, and mutating it (a list, a byte[]) never reaches the next hit.
/// </summary>
public sealed class CacheHitCopiesWithoutTheCodecTests : AzuriteCleanupTestBase
{
    public sealed class Bulky : Entity
    {
        [TableValueSplit(SplitEncoding.Gzip)]
        public string? Zipped { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Text { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Malformed { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public byte[]? Bytes { get; set; }

        [TableValueSplit(SplitEncoding.Gzip)]
        public List<string>? Lines { get; set; }

        public string? Title { get; set; }
    }

    private static Bulky Sample(string id)
    {
        var bytes = new byte[150_000];
        new Random(7).NextBytes(bytes);
        return new Bulky
        {
            Id = id,
            Zipped = Ascii(120_000, 1),
            Text = Ascii(70_000, 2),
            Malformed = "\uD800" + Ascii(40_000, 3) + "\uDC00",
            Bytes = bytes,
            Lines = [.. Enumerable.Range(0, 4_000).Select(i => "line " + i + " " + Ascii(20, i))],
            Title = "t",
        };
    }

    private static string Ascii(int length, int seed)
    {
        var rng = new Random(seed);
        return string.Create(length, rng, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)r.Next(0x21, 0x7F);
        });
    }

    private static readonly string[] CodecSuffixes = ["__chunks", "__sha256", "__enc", "__len", "__c0", "__blob"];

    // The copy the cache makes: the serializer's clone context keeps each large value as one in-process cell,
    // never as encoded chunks.
    [Fact]
    public void TheCloneContext_KeepsEveryLargeValueOneCell_WithoutEncodingIt()
    {
        var row = TableEntitySerializer.ToTableEntity(Sample("p|a"), "p", "a", ValueWriteContext.ForClone, pending: null);

        var encoded = row.Keys.Where(k => CodecSuffixes.Any(s => k.EndsWith(s, StringComparison.Ordinal))).ToList();
        Assert.True(encoded.Count == 0, "encoded cells in the clone: " + string.Join(", ", encoded));
        var copy = row.FromTableEntity<Bulky>();
        AssertSame(Sample("p|a"), copy);
    }

    // On a store over a scripted table: a hit returns the written values, and a caller mutating them never
    // changes the next hit.
    [Fact]
    public async Task ACacheHit_ReturnsTheWrittenValues_AndIsIsolated_OverAScriptedTable()
    {
        var table = new RacingTableClient();
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(Bulky).Name).Returns(table);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TableStorage<Bulky>(service, cache, NullLogger<TableStorage<Bulky>>.Instance, authorizer: null, null,
            (Unified.Data.Tables.Providers.IOffloadBlobStore?)null);
        await HitsAreIsolatedAsync(store, "p|a", () => table.Count(r => r.IsWholeRowPointRead));
    }

    [Fact]
    public async Task ACacheHit_ReturnsTheWrittenValues_AndIsIsolated_OnAzurite()
    {
        SkipUnlessAvailable();
        var tableName = "chwc" + Guid.NewGuid().ToString("N");
        try
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = new TableStorage<Bulky>(TableService, cache, NullLogger<TableStorage<Bulky>>.Instance, authorizer: null,
                new UnifiedTableStorageOptions { TableNameResolver = _ => tableName });
            await HitsAreIsolatedAsync(store, Id("a"), reads: null);
        }
        finally
        {
            await TableService.DeleteTableAsync(tableName, CancellationToken.None);
        }
    }

    private static async Task HitsAreIsolatedAsync(TableStorage<Bulky> store, string id, Func<int>? reads)
    {
        await store.CreateAsync(Sample(id), Ct);
        var before = reads?.Invoke();

        var first = (await store.OneAsync(id, Ct))!;
        AssertSame(Sample(id), first);
        first.Lines!.Add("mine");
        first.Lines[0] = "changed";
        first.Bytes![0] ^= 0xFF;
        first.Zipped = "x";

        var second = (await store.OneAsync(id, Ct))!;
        AssertSame(Sample(id), second);
        Assert.NotSame(first.Lines, second.Lines);
        if (reads is not null)
            Assert.Equal(before, reads()); // both reads were cache hits
    }

    private static void AssertSame(Bulky expected, Bulky actual)
    {
        Assert.Equal(expected.Zipped, actual.Zipped);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Malformed, actual.Malformed);
        Assert.Equal(expected.Bytes, actual.Bytes);
        Assert.Equal(expected.Lines, actual.Lines);
        Assert.Equal(expected.Title, actual.Title);
    }
}
