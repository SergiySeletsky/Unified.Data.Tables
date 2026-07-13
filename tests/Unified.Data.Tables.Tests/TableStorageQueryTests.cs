using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Covers the server-side query surface of the REAL <see cref="TableStorage{T}"/> against the
/// NSubstitute-mocked Azure SDK (so it runs in CI without Azurite): the predicate/paging/streaming
/// methods and the OData filter they hand the SDK.
/// </summary>
public class TableStorageQueryTests
{
    [Fact]
    public async Task QueryAsync_Options_ReturnsRows()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "a"), Mocks.Row("p", "b"));

        var results = await h.Store.QueryAsync(new QueryOptions { Partition = "p" });

        Assert.Equal(2, results.Count);
        Assert.Contains("PartitionKey eq 'p'", h.LastQueryFilter);
    }

    [Fact]
    public async Task QueryStreamAsync_Options_StreamsRows()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "a"));

        var streamed = new List<TestEntity>();
        await foreach (var e in h.Store.QueryStreamAsync(new QueryOptions { Partition = "p", Take = 5 }))
            streamed.Add(e);

        Assert.Single(streamed);
    }

    [Fact]
    public async Task QueryStreamAsync_Options_InvalidBounds_ThrowEagerly()
    {
        using var h = new StorageHarness<TestEntity>();

        // Validation lives in the (non-iterator) public method, so it throws on the call, not on enumeration.
        Assert.Throws<ArgumentException>(() => h.Store.QueryStreamAsync(new QueryOptions { RowKeyPrefix = "x" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Store.QueryStreamAsync(new QueryOptions { Partition = "p", Take = 0 }));
        Assert.Throws<ArgumentException>(() => h.Store.QueryStreamAsync(new QueryOptions { Partition = "p", ContinuationToken = "tok" }));
    }

    [Fact]
    public async Task QueryAsync_Predicate_TranslatesToServerFilter()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "a", value: 7));

        var results = await h.Store.QueryAsync(x => x.Value >= 5 && x.Name == "test", partition: "p");

        Assert.Single(results);
        // Both the partition scope and the predicate are pushed to the server as one OData filter.
        Assert.Contains("PartitionKey eq 'p'", h.LastQueryFilter);
        Assert.Contains("Value ge 5", h.LastQueryFilter);
        Assert.Contains("Name eq 'test'", h.LastQueryFilter);
    }

    [Fact]
    public async Task QueryStreamAsync_Predicate_UntranslatableThrowsEagerly()
    {
        using var h = new StorageHarness<TestEntity>();

        Assert.Throws<NotSupportedException>(() => h.Store.QueryStreamAsync(x => x.Name.StartsWith("prefix")));
    }

    [Fact]
    public async Task AnyAsync_ReflectsWhetherRowsMatch()
    {
        using var withRows = new StorageHarness<TestEntity>();
        withRows.SetupQueryByFilter(Mocks.Row("p", "a"));
        Assert.True(await withRows.Store.AnyAsync(x => x.Value == 42));

        using var empty = new StorageHarness<TestEntity>();
        empty.SetupQueryByFilter();
        Assert.False(await empty.Store.AnyAsync(x => x.Value == 42));
    }

    [Fact]
    public async Task QueryPageAsync_ReturnsFirstPage()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "a"), Mocks.Row("p", "b"));

        var page = await h.Store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10 });

        Assert.Equal(2, page.Items.Count);
        Assert.False(page.HasMore);          // the mock page carries no continuation token
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task QueryPageAsync_NoPages_ReturnsEmpty()
    {
        using var h = new StorageHarness<TestEntity>();
        // A pageable that yields NO pages at all — exercises the "no first page" branch.
        h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
             .Returns(AsyncPageable<TableEntity>.FromPages(Array.Empty<Page<TableEntity>>()));

        var page = await h.Store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10 });

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }
}
