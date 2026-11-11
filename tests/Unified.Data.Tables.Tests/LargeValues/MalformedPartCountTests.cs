using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A primary row whose <c>_SplitParts</c> cell is not a count (a damaged row) cannot be read, and that
/// is right: its value is not whole. But it must still be deletable, or nothing in the library can
/// remove it. A delete does not trust such a count: it looks for the continuation rows that are there
/// (the range query) and deletes them with the primary. The provider contract (both stores, end to end)
/// is in <see cref="LargeBatchContract"/>; here are the SDK calls and the blob cleanup.
/// </summary>
public class MalformedPartCountTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<object> NotACount => [(object)"many", -1, 2.5, long.MaxValue];

    private static TableEntity Damaged(object partCount) =>
        new("p", "r") { ETag = new ETag("W/\"v1\""), [SystemColumnNames.SplitParts] = partCount };

    private static TableEntity OwnPart(int index) =>
        new("p", $"r~{index:D4}") { ETag = new ETag("W/\"part\""), [SystemColumnNames.SplitOf] = "r" };

    [Theory]
    [MemberData(nameof(NotACount))]
    public async Task Delete_PartCountThatCannotBeRead_DeletesThePrimaryAndTheContinuationRowsThatExist(object partCount)
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", Damaged(partCount));
        h.SetupQueryByFilter(OwnPart(1), OwnPart(2));
        var tx = new List<List<TableTransactionAction>>();
        var resp = Substitute.For<Response<IReadOnlyList<Response>>>();
        h.Table.SubmitTransactionAsync(Arg.Do<IEnumerable<TableTransactionAction>>(a => tx.Add([.. a])), Arg.Any<CancellationToken>())
            .Returns(resp);

        await h.Store.DeleteAsync("p|r", Ct);

        var actions = Assert.Single(tx);
        Assert.Equal(["r", "r~0001", "r~0002"], actions.Select(a => a.Entity.RowKey));
        Assert.All(actions, a => Assert.Equal(TableTransactionActionType.Delete, a.ActionType));
        // Still on condition of the version read: a damaged row is not deleted blindly either.
        Assert.Equal(new ETag("W/\"v1\""), actions[0].ETag);
        Assert.Contains("_SplitOf eq 'r'", h.LastQueryFilter!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_PartCountThatCannotBeRead_NoContinuationRowThere_IsOneConditionalDelete()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", Damaged("many"));
        h.SetupQueryByFilter();
        h.SetupDelete();
        h.SetupTransaction();

        await h.Store.DeleteAsync("p|r", Ct);

        await h.Table.Received(1).DeleteEntityAsync("p", "r", new ETag("W/\"v1\""), Arg.Any<CancellationToken>());
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
    }

    [Fact]
    public async Task Delete_PartCountThatCannotBeRead_StillReleasesTheRowsBlobs()
    {
        var log = new List<string>();
        var blobs = new RecordingBlobStore(log);
        using var h = new StorageHarness<SplitTextEntity>(
            options: new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000, CleanupGrace = TimeSpan.Zero } },
            blobs: blobs);
        // A real offloaded row, then its part count is damaged.
        TableEntity? stored = null;
        h.SetupGet("p", "r", null);
        h.Table.AddEntityAsync(Arg.Do<TableEntity>(row => stored = row), Arg.Any<CancellationToken>()).Returns(Mocks.EtagResponse("W/\"v1\""));
        await h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = new string('x', 40_000) }, Ct);
        Assert.NotNull(stored);
        Assert.Single(blobs.Inner.Names);
        stored!.ETag = new ETag("W/\"v1\"");
        stored[SystemColumnNames.SplitParts] = "many";
        h.SetupGet("p", "r", stored);
        h.SetupQueryByFilter();
        h.SetupDelete();
        // A blob is deleted once the service clock has passed its Last-Modified.
        blobs.ServiceTime = () => DateTimeOffset.UtcNow.AddMinutes(1);

        await h.Store.DeleteAsync("p|r", Ct);

        Assert.Empty(blobs.Inner.Names);
    }

    [Theory]
    [MemberData(nameof(NotACount))]
    public async Task ReadsAndWrites_OfSuchARow_StillRefuseIt(object partCount)
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", Damaged(partCount));
        h.SetupUpdate();
        h.SetupTransaction();

        // Not whole: it is not returned, and it is not silently written over.
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => h.Store.OneAsync("p|r", Ct));
        await Assert.ThrowsAsync<TableValueIntegrityException>(
            () => h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "new" }, Ct));

        await h.Table.DidNotReceiveWithAnyArgs().UpdateEntityAsync<TableEntity>(default!, default, default, default);
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
    }

    [Theory]
    [MemberData(nameof(NotACount))]
    public async Task InMemory_Delete_PartCountThatCannotBeRead_RemovesThePrimaryAndItsContinuationRows(object partCount)
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        store.SeedPhysicalRow(new TableEntity("p", "r") { [SystemColumnNames.SplitParts] = partCount });
        store.SeedPhysicalRow(new TableEntity("p", "r~0001") { [SystemColumnNames.SplitOf] = "r", [SystemColumnNames.SplitIndex] = 1 });
        store.SeedPhysicalRow(new TableEntity("p", "r~0002") { [SystemColumnNames.SplitOf] = "r", [SystemColumnNames.SplitIndex] = 2 });
        store.SeedPhysicalRow(new TableEntity("p", "s") { ["Title"] = "neighbour" });
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => store.OneAsync("p|r", Ct));

        await store.DeleteAsync("p|r", Ct);

        Assert.Equal(["s"], store.PhysicalRowKeys("p"));
    }
}
