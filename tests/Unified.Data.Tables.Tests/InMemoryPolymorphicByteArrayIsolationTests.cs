using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The in-memory polymorphic store shares no byte array with its callers, like
/// <see cref="InMemoryStorage{T}"/>: the real polymorphic store has no cache and sends and receives every
/// array over the wire, so a caller that reuses a buffer it wrote, or changes an array it read, never
/// changes a stored row. A test double that kept the caller's array would show a change the table never shows.
/// </summary>
public class InMemoryPolymorphicByteArrayIsolationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(InMemoryPolymorphicStorage<TestMessage> Store, TableKey Key)> StoreWithARowAsync()
    {
        var store = new InMemoryPolymorphicStorage<TestMessage>();
        var key = new TableKey("p", "r");
        await store.InsertAsync(key, new TestCommand { Id = "c1" }, Ct);
        return (store, key);
    }

    [Fact]
    public async Task AWriter_ReusingTheByteArrayItMerged_DoesNotChangeTheStoredRow()
    {
        var (store, key) = await StoreWithARowAsync();
        var buffer = new byte[] { 1, 2, 3 };

        await store.MergeColumnsAsync(key, new Dictionary<string, object> { ["_Data"] = buffer }, Ct);
        buffer[0] = 99;

        var entry = await store.GetAsync(key, Ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(entry!.Columns["_Data"]));
    }

    [Fact]
    public async Task AReader_ChangingTheByteArrayItReceived_DoesNotChangeTheStoredRow()
    {
        var (store, key) = await StoreWithARowAsync();
        await store.MergeColumnsAsync(key, new Dictionary<string, object> { ["_Data"] = new byte[] { 1, 2, 3 } }, Ct);

        var first = await store.GetAsync(key, Ct);
        Assert.IsType<byte[]>(first!.Columns["_Data"])[0] = 99;

        var second = await store.GetAsync(key, Ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(second!.Columns["_Data"]));
    }
}
