using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.Golden;

/// <summary>
/// Golden corpus, part (b): paging cursors. A cursor outlives the process that issued it (a client holds
/// it between requests, across a rolling deploy), so the token strings 0.9.2 issues are held to checked-in
/// files, and the checked-in tokens are decoded and resumed from.
/// </summary>
/// <remarks>
/// Every field of a line that is a string is JSON-encoded (so a NUL or a non-ASCII character is visible and
/// the file stays ASCII); <c>null</c> is a missing bound. The token itself is written as issued.
/// </remarks>
public class GoldenCursorCorpusTests
{
    private const string PageCursorFile = "Cursors/page-cursor.txt";
    private const string InMemoryFile = "Cursors/in-memory-paging.txt";
    private const string AzureFile = "Cursors/azure-paging.txt";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── PageCursor itself ────────────────────────────────────────────────────────────────────────────

    // Partition, RowKey prefix, page size, and the provider's own token the cursor wraps: the in-memory
    // stores wrap "PartitionKey NUL RowKey", the Azure store the SDK's "NextPartitionKey NextRowKey".
    private static readonly (string? Partition, string? Prefix, int PageSize, string Inner)[] Cursors =
    [
        (null, null, 100, "tenant-a\u0000ord-0100"),
        ("p", null, 10, "p\u0000r"),
        ("tenant-a", null, 100, "tenant-a\u0000ord-0100"),
        ("tenant-a", "ord-", 25, "tenant-a\u0000ord-0025"),
        ("tenant-a", "ord-", 1000, "1!12!dGVuYW50LWE- 1!12!b3JkLTEwMDA-"),
        (null, null, 1000, "1!12!dGVuYW50LWI- "),
        // Length prefixes keep these two apart although their bounds concatenate to the same text.
        ("a", "b", 10, "a\u0000b1"),
        ("ab", null, 10, "ab\u0000b1"),
        // Bounds made of the characters the fingerprint itself uses.
        ("~", "~", 1, "~\u0000~"),
        ("1:x|2:y", "3:z|", 7, "1:x|2:y\u00003:z|9"),
        ("", "", 5, "\u0000"),
        ("юнікод-中文", "é😀", 50, "юнікод-中文\u0000é😀-1"),
    ];

    [Fact]
    public void PageCursor_Fingerprints_AndTokens_AreTheCheckedInOnes()
    {
        var text = new StringBuilder(
            "# PageCursor: Fingerprint(partition, rowKeyPrefix, pageSize) and Encode(fingerprint, inner).\n" +
            "# partition\trowKeyPrefix\tpageSize\tinner\tfingerprint\ttoken\n");
        foreach (var (partition, prefix, pageSize, inner) in Cursors)
        {
            var fingerprint = PageCursor.Fingerprint(partition, prefix, pageSize);
            text.Append(CultureInfo.InvariantCulture,
                $"{J(partition)}\t{J(prefix)}\t{pageSize}\t{J(inner)}\t{J(fingerprint)}\t{PageCursor.Encode(fingerprint, inner)}\n");
        }

        GoldenFile.AssertMatches(PageCursorFile, text.ToString());
    }

    [Fact]
    public void PageCursor_CheckedInTokens_Decode_AndOnlyForTheirBounds()
    {
        var lines = GoldenFile.Lines(PageCursorFile);
        Assert.Equal(Cursors.Length, lines.Count);
        foreach (var line in lines)
        {
            var fields = line.Split('\t');
            var (partition, prefix, pageSize, inner) = (U(fields[0]), U(fields[1]), int.Parse(fields[2], CultureInfo.InvariantCulture), U(fields[3])!);
            var token = fields[5];
            var fingerprint = PageCursor.Fingerprint(partition, prefix, pageSize);

            Assert.Equal(U(fields[4]), fingerprint);
            Assert.Equal(inner, PageCursor.Decode(token, fingerprint));

            // A token replayed against any other bound is refused, never silently resumed.
            Assert.Throws<ArgumentException>(() => PageCursor.Decode(token, PageCursor.Fingerprint(partition, prefix, pageSize + 1)));
            Assert.Throws<ArgumentException>(() => PageCursor.Decode(token, PageCursor.Fingerprint(partition + "x", prefix, pageSize)));
            Assert.Throws<ArgumentException>(() => PageCursor.Decode(token, PageCursor.Fingerprint(partition, prefix + "x", pageSize)));
        }
    }

    // ── The in-memory stores: the token of a first page, and the page a resume from it returns ───────

    private static readonly string[] SeedIds =
    [
        "tenant-a|inv-0001", "tenant-a|inv-0002",
        "tenant-a|ord-0001", "tenant-a|ord-0002", "tenant-a|ord-0003", "tenant-a|ord-0004", "tenant-a|ord-0005",
        "tenant-b|ord-0001", "tenant-b|ord-0002",
    ];

    private static readonly (string Name, QueryOptions Query)[] Pagings =
    [
        ("partition", new QueryOptions { Partition = "tenant-a", Take = 2 }),
        ("partition in natural form", new QueryOptions { Partition = " Tenant A ", Take = 2 }),
        ("partition and prefix", new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "ord-", Take = 2 }),
        ("partition and prefix in natural form", new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "ORD-", Take = 2 }),
        ("whole table", new QueryOptions { Take = 3 }),
        ("whole table across a partition end", new QueryOptions { Take = 7 }),
    ];

    private static async Task<Dictionary<string, Func<QueryOptions, Task<(IReadOnlyList<string> Ids, string? Token)>>>> InMemoryStoresAsync()
    {
        // One type whose rows are plain, one that is large-value aware: the two paging paths of the store.
        var plain = new InMemoryStorage<GoldenNullableScalars>();
        var aware = new InMemoryStorage<GoldenSplit>();
        foreach (var id in SeedIds)
        {
            await plain.CreateAsync(new GoldenNullableScalars { Id = id }, Ct);
            await aware.CreateAsync(new GoldenSplit { Id = id }, Ct);
        }
        return new(StringComparer.Ordinal)
        {
            ["plain"] = async q => { var p = await plain.QueryPageAsync(q, Ct); return ([.. p.Items.Select(e => e.Id)], p.ContinuationToken); },
            ["large-value aware"] = async q => { var p = await aware.QueryPageAsync(q, Ct); return ([.. p.Items.Select(e => e.Id)], p.ContinuationToken); },
        };
    }

    [Fact]
    public async Task InMemoryStores_Tokens_AreTheCheckedInOnes()
    {
        var text = new StringBuilder(
            "# InMemoryStorage<T>.QueryPageAsync over nine rows: the first page, its token, and the page the token resumes at.\n" +
            "# store\tquery\tfirst page\ttoken\tnext page\n");
        foreach (var (kind, page) in await InMemoryStoresAsync())
        {
            foreach (var (name, query) in Pagings)
            {
                var first = await page(query);
                Assert.NotNull(first.Token);
                var next = await page(query with { ContinuationToken = first.Token });
                text.Append(CultureInfo.InvariantCulture, $"{kind}\t{name}\t{string.Join(",", first.Ids)}\t{first.Token}\t{string.Join(",", next.Ids)}\n");
            }
        }

        GoldenFile.AssertMatches(InMemoryFile, text.ToString());
    }

    [Fact]
    public async Task InMemoryStores_ResumeFromTheCheckedInTokens()
    {
        var stores = await InMemoryStoresAsync();
        var lines = GoldenFile.Lines(InMemoryFile);
        Assert.Equal(stores.Count * Pagings.Length, lines.Count);
        foreach (var line in lines)
        {
            var fields = line.Split('\t');
            var query = Pagings.Single(p => p.Name == fields[1]).Query;

            var next = await stores[fields[0]](query with { ContinuationToken = fields[3] });

            Assert.Equal(fields[4], string.Join(",", next.Ids));
        }
    }

    // ── The Azure store: the token it issues for the continuation the service returned ───────────────

    private static readonly (string Name, QueryOptions Query, string NextPartitionKey, string? NextRowKey)[] AzurePagings =
    [
        ("partition", new QueryOptions { Partition = "tenant-a", Take = 2 }, "1!12!dGVuYW50LWE-", "1!12!b3JkLTAwMDE-"),
        ("partition in natural form", new QueryOptions { Partition = " Tenant A ", Take = 2 }, "1!12!dGVuYW50LWE-", "1!12!b3JkLTAwMDE-"),
        ("partition and prefix", new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "ord-", Take = 2 }, "1!12!dGVuYW50LWE-", "1!12!b3JkLTAwMDM-"),
        ("whole table", new QueryOptions { Take = 3 }, "1!12!dGVuYW50LWE-", "1!12!b3JkLTAwMDI-"),
        ("whole table, the service names the next partition only", new QueryOptions { Take = 3 }, "1!12!dGVuYW50LWI-", null),
        ("default page size", new QueryOptions { Partition = "tenant-a" }, "1!12!dGVuYW50LWE-", "1!12!b3JkLTAxMDA-"),
        ("page size above the service maximum", new QueryOptions { Partition = "tenant-a", Take = 5000 }, "1!12!dGVuYW50LWE-", "1!12!b3JkLTEwMDA-"),
    ];

    [Fact]
    public async Task AzureStore_Tokens_AreTheCheckedInOnes_AndResumeWhereTheServiceSaid()
    {
        var text = new StringBuilder(
            "# TableStorage<T>.QueryPageAsync through the Azure SDK: the request it sends ($filter, $top), the continuation\n" +
            "# the service answers with (x-ms-continuation-NextPartitionKey / -NextRowKey), and the token the store issues.\n" +
            "# query\t$filter\t$top\tNextPartitionKey\tNextRowKey\ttoken\n");
        foreach (var (name, query, nextPartitionKey, nextRowKey) in AzurePagings)
        {
            var (store, transport) = AzureStore(nextPartitionKey, nextRowKey);

            var first = await store.QueryPageAsync(query, Ct);

            var sent = Assert.Single(transport.Requests, r => r.Method == "GET");
            Assert.NotNull(first.ContinuationToken);
            text.Append(CultureInfo.InvariantCulture,
                $"{name}\t{J(sent.Query.GetValueOrDefault("$filter"))}\t{sent.Query.GetValueOrDefault("$top") ?? "null"}\t{nextPartitionKey}\t{nextRowKey ?? "null"}\t{first.ContinuationToken}\n");

            // The resume asks the service for exactly the position it handed out.
            await store.QueryPageAsync(query with { ContinuationToken = first.ContinuationToken }, Ct);
            var resumed = transport.Requests.Last(r => r.Method == "GET");
            Assert.Equal(nextPartitionKey, resumed.Query.GetValueOrDefault("NextPartitionKey"));
            Assert.Equal(nextRowKey, resumed.Query.GetValueOrDefault("NextRowKey"));
            Assert.Equal(sent.Query.GetValueOrDefault("$filter"), resumed.Query.GetValueOrDefault("$filter"));
        }

        GoldenFile.AssertMatches(AzureFile, text.ToString());
    }

    [Fact]
    public async Task AzureStore_ResumesFromTheCheckedInTokens()
    {
        var lines = GoldenFile.Lines(AzureFile);
        Assert.Equal(AzurePagings.Length, lines.Count);
        foreach (var line in lines)
        {
            var fields = line.Split('\t');
            var (_, query, nextPartitionKey, nextRowKey) = AzurePagings.Single(p => p.Name == fields[0]);
            var (store, transport) = AzureStore(nextPartitionKey, nextRowKey);

            await store.QueryPageAsync(query with { ContinuationToken = fields[5] }, Ct);

            var resumed = Assert.Single(transport.Requests, r => r.Method == "GET");
            Assert.Equal(fields[3], resumed.Query.GetValueOrDefault("NextPartitionKey"));
            Assert.Equal(fields[4], resumed.Query.GetValueOrDefault("NextRowKey") ?? "null");
            Assert.Equal(U(fields[1]), resumed.Query.GetValueOrDefault("$filter"));
        }
    }

    // A store over the real SDK whose every query is answered with an empty page and that continuation.
    private static (TableStorage<GoldenNullableScalars> Store, GoldenTransport Transport) AzureStore(string nextPartitionKey, string? nextRowKey)
    {
        var headers = new Dictionary<string, string> { ["x-ms-continuation-NextPartitionKey"] = nextPartitionKey };
        if (nextRowKey is not null)
            headers["x-ms-continuation-NextRowKey"] = nextRowKey;
        var transport = new GoldenTransport(_ => new GoldenTransport.Answer(200, "{\"value\":[]}", headers));
        var store = new TableStorage<GoldenNullableScalars>(
            transport.Service(), new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<GoldenNullableScalars>>.Instance);
        return (store, transport);
    }

    // JSON, with the apostrophe every filter is full of left as it is (JSON does not need it escaped).
    private static string J(string? value) => JsonSerializer.Serialize(value).Replace(EscapedApostrophe, "'", StringComparison.Ordinal);

    private static readonly string EscapedApostrophe = (char)0x5C + "u0027";

    private static string? U(string json) => JsonSerializer.Deserialize<string?>(json);
}
