using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.Capabilities;

/// <summary>
/// 0.10.0 declares a RowKey range on <see cref="QueryOptions"/> (<c>FromRowKey</c>, <c>ToRowKey</c> and
/// their inclusivity) and the rule every provider checks it by. Every provider honours the bounds in
/// each of its query members, and the paging cursor binds them: a cursor issued for one range does not
/// resume a query over another.
/// </summary>
public class QueryRangeDeclarationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── the options ───────────────────────────────────────────────────────

    [Fact]
    public void Defaults_NoBound_LowerInclusive_UpperExclusive()
    {
        var options = new QueryOptions();

        Assert.Null(options.FromRowKey);
        Assert.Null(options.ToRowKey);
        Assert.True(options.FromInclusive);
        Assert.False(options.ToInclusive);
        Assert.False(options.HasRowKeyRange);
        options.ValidateRowKeyRange();
    }

    [Theory]
    [InlineData("a", null)]
    [InlineData(null, "z")]
    [InlineData("a", "z")]
    [InlineData("", null)] // the empty string is a bound, not "unset"
    public void AnyBound_IsARange(string? from, string? to)
    {
        var options = new QueryOptions { Partition = "p", FromRowKey = from, ToRowKey = to };

        Assert.True(options.HasRowKeyRange);
        options.ValidateRowKeyRange();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ARange_WithoutAPartition_Throws(string? partition)
    {
        var from = Assert.Throws<ArgumentException>(() => new QueryOptions { Partition = partition, FromRowKey = "a" }.ValidateRowKeyRange());
        var to = Assert.Throws<ArgumentException>(() => new QueryOptions { Partition = partition, ToRowKey = "z" }.ValidateRowKeyRange());

        Assert.Equal("options", from.ParamName);
        Assert.Contains("require Partition", from.Message, StringComparison.Ordinal);
        Assert.Contains("require Partition", to.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARange_CombinedWithARowKeyPrefix_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new QueryOptions { Partition = "p", RowKeyPrefix = "ord-", ToRowKey = "ord-5" }.ValidateRowKeyRange());

        Assert.Equal("options", ex.ParamName);
        Assert.Contains("RowKeyPrefix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InclusivityFlags_WithoutABound_AreNotARange()
    {
        var options = new QueryOptions { FromInclusive = false, ToInclusive = true };

        Assert.False(options.HasRowKeyRange);
        options.ValidateRowKeyRange(); // no partition needed: nothing is bounded
    }

    // ── the cursor fingerprint ────────────────────────────────────────────

    [Theory]
    [InlineData(null, null, 100, "~|~|100")]
    [InlineData("tenant-a", null, 100, "8:tenant-a|~|100")]
    [InlineData("tenant-a", "ord-", 25, "8:tenant-a|4:ord-|25")]
    [InlineData("", "", 5, "0:|0:|5")]
    public void Fingerprint_WithoutABound_IsThe092Fingerprint_WhateverTheFlags(string? partition, string? prefix, int pageSize, string issuedBy092)
    {
        // The literal is what 0.9.2 computes; a cursor in a client's hands carries exactly these bytes.
        Assert.Equal(issuedBy092, PageCursor.Fingerprint(partition, prefix, pageSize));
        foreach (var fromInclusive in new[] { true, false })
        {
            foreach (var toInclusive in new[] { true, false })
                Assert.Equal(issuedBy092, PageCursor.Fingerprint(partition, prefix, pageSize, null, fromInclusive, null, toInclusive));
        }
    }

    [Fact]
    public void ACursorIssuedBy092_StillDecodes_ForAQueryWithoutBounds()
    {
        // Encode("8:tenant-a|~|100", "tenant-a NUL ord-0100") as 0.9.2 wrote it (Golden/Data/Cursors/page-cursor.txt).
        var issued = PageCursor.Encode(PageCursor.Fingerprint("tenant-a", null, 100), "tenant-a\u0000ord-0100");

        Assert.Equal(
            "tenant-a\u0000ord-0100",
            PageCursor.Decode(issued, PageCursor.Fingerprint("tenant-a", null, 100, null, true, null, false)));
    }

    [Theory]
    [InlineData("a", true, "m", false, "1:p|~|10|1:a|1|1:m|0")]
    [InlineData("a", false, "m", true, "1:p|~|10|1:a|0|1:m|1")]
    [InlineData("a", true, null, false, "1:p|~|10|1:a|1|~|-")]
    [InlineData(null, true, "m", false, "1:p|~|10|~|-|1:m|0")]
    [InlineData("", true, null, false, "1:p|~|10|0:|1|~|-")]
    public void Fingerprint_WithABound_AppendsTheFourRangeParts(string? from, bool fromInclusive, string? to, bool toInclusive, string expected) =>
        Assert.Equal(expected, PageCursor.Fingerprint("p", null, 10, from, fromInclusive, to, toInclusive));

    [Fact]
    public void Fingerprint_OfABoundedQuery_ExtendsTheUnboundedOne()
    {
        var unbounded = PageCursor.Fingerprint("p", null, 10);

        Assert.StartsWith(unbounded + "|", PageCursor.Fingerprint("p", null, 10, "a", true, "m", false), StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_TheFlagOfABoundThatIsNotSet_DoesNotCount()
    {
        Assert.Equal(
            PageCursor.Fingerprint("p", null, 10, "a", true, null, false),
            PageCursor.Fingerprint("p", null, 10, "a", true, null, true));
        Assert.Equal(
            PageCursor.Fingerprint("p", null, 10, null, true, "m", false),
            PageCursor.Fingerprint("p", null, 10, null, false, "m", false));
    }

    [Fact]
    public void Fingerprints_OfDifferentRanges_NeverCollide()
    {
        string[] fingerprints =
        [
            PageCursor.Fingerprint("p", null, 10),
            PageCursor.Fingerprint("p", null, 10, "a", true, "m", false),
            PageCursor.Fingerprint("p", null, 10, "a", false, "m", false),
            PageCursor.Fingerprint("p", null, 10, "a", true, "m", true),
            PageCursor.Fingerprint("p", null, 10, "a", true, null, false),
            PageCursor.Fingerprint("p", null, 10, null, true, "a", false),
            PageCursor.Fingerprint("p", null, 10, "", true, null, false),
            // Bounds made of the characters the fingerprint itself uses.
            PageCursor.Fingerprint("p", null, 10, "a|1|1:m", true, null, false),
            PageCursor.Fingerprint("p", null, 10, "a", true, "m|0", false),
            PageCursor.Fingerprint("p", null, 10, "~", true, "-", false),
        ];

        Assert.Equal(fingerprints.Length, fingerprints.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ACursor_IsRefused_WhenABoundOrItsInclusivityChanges()
    {
        var issuedFor = PageCursor.Fingerprint("p", null, 10, "a", true, "m", false);
        var cursor = PageCursor.Encode(issuedFor, "p\u0000c");

        Assert.Equal("p\u0000c", PageCursor.Decode(cursor, issuedFor));
        Assert.Throws<ArgumentException>(() => PageCursor.Decode(cursor, PageCursor.Fingerprint("p", null, 10, "a", false, "m", false)));
        Assert.Throws<ArgumentException>(() => PageCursor.Decode(cursor, PageCursor.Fingerprint("p", null, 10, "a", true, "m", true)));
        Assert.Throws<ArgumentException>(() => PageCursor.Decode(cursor, PageCursor.Fingerprint("p", null, 10, "b", true, "m", false)));
        Assert.Throws<ArgumentException>(() => PageCursor.Decode(cursor, PageCursor.Fingerprint("p", null, 10, "a", true, "n", false)));
        Assert.Throws<ArgumentException>(() => PageCursor.Decode(cursor, PageCursor.Fingerprint("p", null, 10)));
        // And a cursor of the unbounded query is refused by a bounded one.
        var unbounded = PageCursor.Encode(PageCursor.Fingerprint("p", null, 10), "p\u0000c");
        Assert.Throws<ArgumentException>(() => PageCursor.Decode(unbounded, issuedFor));
    }

    // ── the providers ─────────────────────────────────────────────────────

    // A lower bound, an upper bound, both (capped by Take): each alone selects its rows.
    private static readonly QueryOptions[] BoundedQueries =
    [
        new() { Partition = "p", FromRowKey = "b" },
        new() { Partition = "p", ToRowKey = "b" },
        new() { Partition = "p", FromRowKey = "a", ToRowKey = "c", Take = 1 },
    ];

    // What each of them selects from the row keys a, b, c: [b, end), [start, b), the first of [a, c).
    private static readonly string[][] BoundedIds = [["p|b", "p|c"], ["p|a"], ["p|a"]];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InMemory_AnswersABoundedQuery_WithTheRowKeysBetweenTheBounds(int query)
    {
        // Each of the three query members honours the bounds; none returns the whole partition.
        var options = BoundedQueries[query];
        var storage = new InMemoryStorage<TestEntity>();
        await storage.CreateBatchAsync([new() { Id = "p|a" }, new() { Id = "p|b" }, new() { Id = "p|c" }, new() { Id = "q|b" }], Ct);

        Assert.Equal(BoundedIds[query], (await storage.QueryAsync(options, Ct)).Select(e => e.Id));
        var streamed = new List<string>();
        await foreach (var entity in storage.QueryStreamAsync(options, Ct))
            streamed.Add(entity.Id);
        Assert.Equal(BoundedIds[query], streamed);
        var page = await storage.QueryPageAsync(options, Ct);
        Assert.Equal(BoundedIds[query], page.Items.Select(e => e.Id));
        // Take is the page size: the third query has a second page, the unbounded ones do not.
        Assert.Equal(query == 2, page.ContinuationToken is not null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InMemory_LargeValueType_AnswersABoundedQueryToo(int query)
    {
        var options = BoundedQueries[query];
        var storage = new InMemoryStorage<SplitTextEntity>();
        // The middle entity is stored over continuation rows ("b~..."), which a range must not return.
        await storage.CreateAsync(new SplitTextEntity { Id = "p|a", Body = "x", Title = "A" }, Ct);
        await storage.CreateAsync(new SplitTextEntity { Id = "p|b", Body = new string((char)0x263A, 600_000), Title = "B" }, Ct);
        await storage.CreateAsync(new SplitTextEntity { Id = "p|c", Body = "z", Title = "C" }, Ct);
        Assert.True(storage.PhysicalRowCount > 3);

        var all = await storage.QueryAsync(options, Ct);
        Assert.Equal(BoundedIds[query], all.Select(e => e.Id));
        Assert.All(all, e => Assert.NotNull(e.Body));
        var streamed = new List<string>();
        await foreach (var entity in storage.QueryStreamAsync(options, Ct))
            streamed.Add(entity.Id);
        Assert.Equal(BoundedIds[query], streamed);
        // A page is cut over the stored rows: the continuation rows of "b" take places in it and are skipped.
        var page = await storage.QueryPageAsync(options, Ct);
        Assert.Equal(BoundedIds[query], page.Items.Select(e => e.Id));
        Assert.Equal(query == 2, page.ContinuationToken is not null);
        // A projection takes another path through the store; it honours the bounds as well.
        var projected = await storage.QueryAsync(options with { Select = ["Title"] }, Ct);
        Assert.Equal(BoundedIds[query], projected.Select(e => e.Id));
        Assert.All(projected, e => Assert.Null(e.Body));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Azure_AnswersABoundedQuery_WithTheBoundsInTheFilter(int query)
    {
        // Until the Azure provider implemented the range it refused these with NotSupportedException
        // before any request; now each of the three query members sends the bounds to the service.
        var options = BoundedQueries[query];
        var transport = new ScriptedTableTransport(_ => new ScriptedTableTransport.Answer(200, "{\"value\":[]}"));
        var storage = new TableStorage<TestEntity>(transport.Service(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TableStorage<TestEntity>>.Instance, authorizer: null, options: new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled });

        Assert.Empty(await storage.QueryAsync(options, Ct));
        await foreach (var _ in storage.QueryStreamAsync(options, Ct)) { }
        Assert.Empty((await storage.QueryPageAsync(options, Ct)).Items);

        var queries = transport.Requests.Where(r => r.Method == "GET").ToList();
        Assert.Equal(3, queries.Count);
        Assert.All(queries, q => Assert.Contains("RowKey ", q.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BothProviders_ReportAnInvalidRange_AsAnArgumentError_NotAsUnsupported()
    {
        var memory = new InMemoryStorage<TestEntity>();
        var transport = new ScriptedTableTransport(_ => new ScriptedTableTransport.Answer(200, "{\"value\":[]}"));
        var azure = new TableStorage<TestEntity>(transport.Service(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TableStorage<TestEntity>>.Instance, authorizer: null, options: new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled });
        var noPartition = new QueryOptions { FromRowKey = "a" };
        var withPrefix = new QueryOptions { Partition = "p", RowKeyPrefix = "a", ToRowKey = "b" };

        foreach (var options in new[] { noPartition, withPrefix })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => memory.QueryAsync(options, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => memory.QueryPageAsync(options, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => azure.QueryAsync(options, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => azure.QueryPageAsync(options, Ct));
        }
    }

    [Fact]
    public async Task AQueryWithoutBounds_IsUntouched()
    {
        var storage = new InMemoryStorage<TestEntity>();
        await storage.CreateBatchAsync([new() { Id = "p|a" }, new() { Id = "p|b" }, new() { Id = "q|a" }], Ct);

        Assert.Equal(2, (await storage.QueryAsync(new QueryOptions { Partition = "p" }, Ct)).Count);
        Assert.Single(await storage.QueryAsync(new QueryOptions { Partition = "p", RowKeyPrefix = "b" }, Ct));
        Assert.Equal(2, (await storage.QueryPageAsync(new QueryOptions { Partition = "p", FromInclusive = false, ToInclusive = true }, Ct)).Items.Count);
    }
}
