#pragma warning disable UDT0001 // the experimental raw-table API is what is tested
using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.RowStore;

/// <summary>
/// The service stores at most 252 properties besides PartitionKey, RowKey and Timestamp, and refuses a
/// write with more (400 <c>TooManyProperties</c>). The local providers refuse it too, so a raw or
/// polymorphic row that works locally is not refused by Azure.
/// </summary>
public class PropertyCountLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, RawCell> Cells(int count) =>
        Enumerable.Range(0, count).ToDictionary(i => $"C{i:D3}", i => RawCell.FromInt32(i), StringComparer.Ordinal);

    [Fact]
    public void ARowOf252Properties_IsAccepted_And253AreRefused()
    {
        var fits = new TableEntity("p", "r") { ETag = new Azure.ETag("W/\"1\"") };
        for (var i = 0; i < TableRequestSize.MaxEntityProperties; i++)
            fits[$"C{i:D3}"] = i;
        fits["Nothing"] = null; // a null cell is not sent, so not counted
        Assert.Null(ServiceErrors.RefusedRow(fits));

        fits["OneMore"] = 1;
        var refused = ServiceErrors.RefusedRow(fits);
        Assert.NotNull(refused);
        Assert.Equal(400, refused.Status);
        Assert.Equal("TooManyProperties", refused.ErrorCode);
    }

    [Theory]
    [InlineData(253)]
    [InlineData(300)]
    public async Task ARawInsertOfTooManyCells_IsRefusedLocally_AsTheServiceRefusesIt(int cells)
    {
        var store = new InMemoryPolymorphicStorage<TestMessage>("Wide");
        Assert.True(store.TryGetCapability<IRawTable>(out var raw));

        var thrown = await Assert.ThrowsAsync<StorageRequestRejectedException>(
            () => raw.SubmitAsync([RawOperation.Insert(new RawRow("p", "r", Cells(cells)))], Ct));
        Assert.Equal("TooManyProperties", thrown.ProviderErrorCode);

        var stored = await raw.SubmitAsync([RawOperation.Insert(new RawRow("p", "ok", Cells(252)))], Ct);
        Assert.Single(stored);
    }
}
