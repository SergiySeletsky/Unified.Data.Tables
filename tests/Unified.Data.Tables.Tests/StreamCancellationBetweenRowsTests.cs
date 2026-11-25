using Azure.Data.Tables;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// A stream of the Azure stores ends at the next row once its token is cancelled. The SDK looks at the
/// token only when it requests a page, so a token cancelled between two rows of one page used to be
/// noticed at the next page, or never: the rest of the page was handed out, which the in-memory stores
/// never did. Found by the conformance kit (<c>Stream.TokenCancelledWhileReading_EndsWithOperationCanceled</c>).
/// </summary>
public class StreamCancellationBetweenRowsTests
{
    [Fact]
    public async Task TableStorage_OptionsStream_StopsAtTheNextRow()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "a"), Mocks.Row("p", "b"), Mocks.Row("p", "c"));
        using var cts = new CancellationTokenSource();

        var seen = new List<string>();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var entity in h.Store.QueryStreamAsync(new QueryOptions { Partition = "p" }, cts.Token))
            {
                seen.Add(entity.Id);
                await cts.CancelAsync();
            }
        });

        // One page of three rows: the first was handed out, the second was not.
        Assert.Equal(["p|a"], seen);
    }

    [Fact]
    public async Task TableStorage_PredicateStream_StopsAtTheNextRow()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "a"), Mocks.Row("p", "b"));
        using var cts = new CancellationTokenSource();

        var seen = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in h.Store.QueryStreamAsync(x => x.Value == 42, "p", null, cts.Token))
            {
                seen++;
                await cts.CancelAsync();
            }
        });

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task PolymorphicTableStorage_Stream_StopsAtTheNextRow()
    {
        using var h = new PolymorphicHarness<object>();
        h.SetupQuery(new TableEntity("p", "a"), new TableEntity("p", "b"), new TableEntity("p", "c"));
        using var cts = new CancellationTokenSource();

        var seen = new List<string>();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var entry in h.Store.QueryStreamAsync("p", null, cts.Token))
            {
                seen.Add(entry.Key.RowKey);
                await cts.CancelAsync();
            }
        });

        Assert.Equal(["a"], seen);
    }

    [Fact]
    public async Task ATokenThatIsNeverCancelled_StillYieldsEveryRow()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "a"), Mocks.Row("p", "b"), Mocks.Row("p", "c"));
        using var cts = new CancellationTokenSource();

        var seen = 0;
        await foreach (var _ in h.Store.QueryStreamAsync(new QueryOptions { Partition = "p" }, cts.Token))
            seen++;

        Assert.Equal(3, seen);
    }
}
