using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Continuation-token paging: <see cref="IStorage{T}.QueryPageAsync"/> returns one page plus an opaque,
/// query-bound cursor. Paging through must cover every row exactly once in key order, report
/// <see cref="EntityPage{T}.HasMore"/> correctly, and reject a cursor replayed against different bounds.
/// </summary>
public class PagingTests
{
    public sealed class Row : Entity
    {
        public int N { get; set; }
    }

    private static async Task<InMemoryStorage<Row>> Seed(int count, string partition = "p")
    {
        var store = new InMemoryStorage<Row>();
        for (var i = 0; i < count; i++)
            // zero-padded row keys so lexical order == numeric order
            await store.CreateAsync(new Row { Id = $"{partition}|{i:D4}", N = i });
        return store;
    }

    [Fact]
    public async Task PagesThroughEveryRow_Once_InOrder()
    {
        var store = await Seed(25);

        var seen = new List<int>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10, ContinuationToken = cursor });
            seen.AddRange(page.Items.Select(r => r.N));
            cursor = page.ContinuationToken;
            pages++;
            Assert.True(pages <= 10, "paging did not terminate");
        }
        while (cursor is not null);

        Assert.Equal(3, pages);                            // 10 + 10 + 5
        Assert.Equal(Enumerable.Range(0, 25), seen);       // every row, once, in order
    }

    [Fact]
    public async Task FirstPage_ReportsHasMore_LastPageDoesNot()
    {
        var store = await Seed(15);

        var first = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10 });
        Assert.Equal(10, first.Items.Count);
        Assert.True(first.HasMore);

        var second = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10, ContinuationToken = first.ContinuationToken });
        Assert.Equal(5, second.Items.Count);
        Assert.False(second.HasMore);
        Assert.Null(second.ContinuationToken);
    }

    [Fact]
    public async Task ExactMultiple_LastFullPage_HasNoMore()
    {
        var store = await Seed(20);

        var first = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10 });
        var second = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10, ContinuationToken = first.ContinuationToken });

        Assert.Equal(10, second.Items.Count);
        Assert.False(second.HasMore); // no phantom empty page
    }

    [Fact]
    public async Task EmptyResult_HasNoItems_AndNoMore()
    {
        var store = await Seed(0);

        var page = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10 });

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Cursor_ReplayedAgainstDifferentPageSize_Throws()
    {
        var store = await Seed(25);

        var first = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10 });

        // Same partition, different page size -> the cursor's bounds fingerprint no longer matches.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 5, ContinuationToken = first.ContinuationToken }));
    }

    [Fact]
    public async Task Cursor_ReplayedAgainstDifferentPartition_Throws()
    {
        var store = await Seed(25, "p");
        for (var i = 0; i < 25; i++)
            await store.CreateAsync(new Row { Id = $"q|{i:D4}", N = i });

        var first = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10 });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.QueryPageAsync(new QueryOptions { Partition = "q", Take = 10, ContinuationToken = first.ContinuationToken }));
    }

    [Fact]
    public async Task Cursor_BoundsThatConcatenateToSameString_StillThrow()
    {
        // Regression: fingerprint("a","b",N) must NOT equal fingerprint("ab",null,N).
        var store = new InMemoryStorage<Row>();
        for (var i = 0; i < 15; i++)
        {
            await store.CreateAsync(new Row { Id = $"a|b{i:D3}", N = i });   // partition "a", prefix "b"
            await store.CreateAsync(new Row { Id = $"ab|{i:D3}", N = i });   // partition "ab"
        }

        var first = await store.QueryPageAsync(new QueryOptions { Partition = "a", RowKeyPrefix = "b", Take = 10 });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.QueryPageAsync(new QueryOptions { Partition = "ab", Take = 10, ContinuationToken = first.ContinuationToken }));
    }

    [Fact]
    public async Task ContinuationToken_OnNonPagingQuery_Throws()
    {
        var store = await Seed(25);
        var first = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10 });

        // Feeding a paging cursor to a non-paging query is a misuse — fail loudly, don't silently ignore it.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.QueryAsync(new QueryOptions { Partition = "p", Take = 10, ContinuationToken = first.ContinuationToken }));
    }

    [Fact]
    public async Task DefaultPageSize_IsHundred()
    {
        var store = await Seed(120);

        var page = await store.QueryPageAsync(new QueryOptions { Partition = "p" }); // no Take

        Assert.Equal(100, page.Items.Count);
        Assert.True(page.HasMore);
    }
}
