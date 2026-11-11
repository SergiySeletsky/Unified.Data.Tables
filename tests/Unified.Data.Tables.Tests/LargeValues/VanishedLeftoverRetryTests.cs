using System.Globalization;
using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A write whose new continuation row failed on a key held by the entity's own leftover, and which then
/// finds that leftover gone (another caller deleted the id, shrank the entity or deleted the partition
/// in between), has lost a race: it is planned again and goes through, within the five attempts. It is
/// refused with the foreign-row error only when a row that is not the entity's continuation row holds
/// the key (ROWS-2 of the fix-wave-2 review). Against the local Azurite: a pipeline policy runs the
/// other caller just before the write looks at the entity's own continuation rows.
/// </summary>
public sealed class VanishedLeftoverRetryTests : AzuriteCleanupTestBase
{
    private static readonly string OnePart = new('a', 600_000);
    private static readonly string OtherOnePart = new('b', 600_000);
    private static readonly string TwoParts = new('c', 1_000_000);

    private const string TableName = nameof(SplitTextEntity);

    private async Task<List<string>> RowKeysAsync() => (await RawRowsAsync(TableName)).ConvertAll(r => r.RowKey);

    /// <summary>Leaves <c>r~0001</c> behind with no main row: a store that does not know split values deleted the main row.</summary>
    private async Task SeedLeftoverWithoutMainRowAsync()
    {
        await SplitOnlyStore<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("r"), Body = OnePart }, Ct);
        await Store<PlainEntity>(table: TableName).DeleteAsync(Id("r"), Ct);
        Assert.Equal(["r~0001"], await RowKeysAsync());
    }

    [Fact]
    public async Task Create_WhoseOwnLeftoverADeleteOfTheSameIdRemovesBeforeTheLook_IsPlannedAgain_AndStored()
    {
        SkipUnlessAvailable();
        await SeedLeftoverWithoutMainRowAsync();
        var deleter = SplitOnlyStore<SplitTextEntity>();
        var interleaved = new RowRequestInterleaving.BeforeRangeQuery(1, () => deleter.DeleteAsync(Id("r"), CancellationToken.None));
        var creator = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await creator.EnsureCreatedAsync(Ct);

        var failure = await Record.ExceptionAsync(
            () => creator.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = OtherOnePart }, Ct));

        Assert.Equal(1, interleaved.Fired);
        var keysAfter = await RowKeysAsync();
        Assert.True(failure is null,
            $"CreateAsync threw {failure?.GetType().Name}: {failure?.Message} Rows in the partition afterwards: [{string.Join(", ", keysAfter)}]");
        Assert.Equal(["r", "r~0001"], keysAfter);
        Assert.Equal(OtherOnePart, (await creator.OneAsync(Id("r"), Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Upsert_GrowingOntoItsOwnLeftover_WhichAShrinkingUpsertRemovesBeforeTheLook_IsPlannedAgain_AndIsTheLastWriter()
    {
        SkipUnlessAvailable();
        var seeder = SplitOnlyStore<SplitTextEntity>();
        // The stored row declares one continuation row; r~0002 is a leftover of an earlier, larger value.
        await seeder.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = OnePart }, Ct);
        await TableService.GetTableClient(TableName).AddEntityAsync(RowRequestInterleaving.ContinuationRowsOf(Pk, "r", TwoParts)[1], Ct);
        Assert.Equal(["r", "r~0001", "r~0002"], await RowKeysAsync());

        var other = SplitOnlyStore<SplitTextEntity>();
        var interleaved = new RowRequestInterleaving.BeforeRangeQuery(1,
            () => other.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = "small" }, CancellationToken.None));
        var writer = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await writer.EnsureCreatedAsync(Ct);

        var failure = await Record.ExceptionAsync(
            () => writer.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = TwoParts }, Ct));

        Assert.Equal(1, interleaved.Fired);
        var keysAfter = await RowKeysAsync();
        Assert.True(failure is null,
            $"UpsertAsync threw {failure?.GetType().Name}: {failure?.Message} Rows in the partition afterwards: [{string.Join(", ", keysAfter)}]");
        Assert.Equal(["r", "r~0001", "r~0002"], keysAfter);
        Assert.Equal(TwoParts, (await writer.OneAsync(Id("r"), Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task UpsertBatch_WhoseOwnLeftoverADeleteOfThatIdRemovesBeforeTheLook_IsPlannedAgain_AndStored()
    {
        SkipUnlessAvailable();
        await SeedLeftoverWithoutMainRowAsync();
        var deleter = SplitOnlyStore<SplitTextEntity>();
        var interleaved = new RowRequestInterleaving.BeforeRangeQuery(1, () => deleter.DeleteAsync(Id("r"), CancellationToken.None));
        var writer = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await writer.EnsureCreatedAsync(Ct);

        var failure = await Record.ExceptionAsync(() => writer.UpsertBatchAsync(
        [
            new SplitTextEntity { Id = Id("q"), Body = "a" },
            new SplitTextEntity { Id = Id("r"), Body = OtherOnePart },
        ], Ct));

        Assert.Equal(1, interleaved.Fired);
        var keysAfter = await RowKeysAsync();
        Assert.True(failure is null,
            $"UpsertBatchAsync threw {failure?.GetType().Name}: {failure?.Message} Rows in the partition afterwards: [{string.Join(", ", keysAfter)}]");
        Assert.Equal(["q", "r", "r~0001"], keysAfter);
        Assert.Equal(OtherOnePart, (await writer.OneAsync(Id("r"), Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Create_WhoseOwnLeftoverAPartitionDeleteRemovesBeforeTheLook_IsPlannedAgain_AndStored()
    {
        SkipUnlessAvailable();
        await SeedLeftoverWithoutMainRowAsync();
        var deleter = SplitOnlyStore<SplitTextEntity>();
        var interleaved = new RowRequestInterleaving.BeforeRangeQuery(1, () => deleter.DeletePartitionAsync(Pk, CancellationToken.None));
        var creator = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await creator.EnsureCreatedAsync(Ct);

        var failure = await Record.ExceptionAsync(
            () => creator.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = OtherOnePart }, Ct));

        Assert.Equal(1, interleaved.Fired);
        var keysAfter = await RowKeysAsync();
        Assert.True(failure is null,
            $"CreateAsync threw {failure?.GetType().Name}: {failure?.Message} Rows in the partition afterwards: [{string.Join(", ", keysAfter)}]");
        Assert.Equal(["r", "r~0001"], keysAfter);
    }

    /// <summary>A foreign row at the key is still refused, and left as it is (control, on the real SDK).</summary>
    [Fact]
    public async Task Create_ARowOfAnotherWriterAtTheKey_IsStillRefused_AndLeftAsItIs()
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        await writer.EnsureCreatedAsync(Ct);
        await TableService.GetTableClient(TableName).AddEntityAsync(new TableEntity(Pk, "r~0001") { ["Title"] = "theirs" }, Ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = OnePart }, Ct));

        Assert.Contains("'r~0001'", ex.Message, StringComparison.Ordinal);
        var rows = await RawRowsAsync(TableName);
        var row = Assert.Single(rows);
        Assert.Equal("theirs", row.GetString("Title"));
    }
}

/// <summary>
/// The SDK calls of the same refusal path (a mocked table): when the key of a new continuation row was
/// taken and is not among the entity's own rows, the row at that key is read once (that path only); no
/// row there is a lost race, which waits and plans again, a foreign row is refused.
/// </summary>
public class VanishedLeftoverRetryCallTests
{
    private static readonly string OnePartText = new('a', 600_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableTransactionFailedException TxFailed(int status, int index)
    {
        var inner = new RequestFailedException(status, "failed", "EntityAlreadyExists", null);
        inner.Data["FailedEntity"] = index.ToString(CultureInfo.InvariantCulture);
        return new TableTransactionFailedException(inner);
    }

    private static Response<IReadOnlyList<Response>> Committed() =>
        Response.FromValue<IReadOnlyList<Response>>([new FakeResponse("W/\"written\"")], new FakeResponse());

    /// <summary>The first transaction fails with 409 on its continuation row; later ones commit.</summary>
    private static List<List<TableTransactionAction>> FirstTransactionMeetsATakenKey(StorageHarness<SplitTextEntity> h)
    {
        var submitted = new List<List<TableTransactionAction>>();
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var actions = call.Arg<IEnumerable<TableTransactionAction>>().ToList();
                submitted.Add(actions);
                if (submitted.Count == 1)
                {
                    var index = actions.FindIndex(a => a.Entity.RowKey.Contains('~', StringComparison.Ordinal));
                    throw TxFailed(409, index);
                }
                return Committed();
            });
        // The entity's own continuation rows: none (the leftover is gone, or the row is foreign).
        h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable([]));
        return submitted;
    }

    [Fact]
    public async Task Create_TakenKeyNowEmpty_ReadsThatKeyOnce_AndIsPlannedAgain()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r~0001", null);
        var submitted = FirstTransactionMeetsATakenKey(h);

        var created = await h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = OnePartText }, Ct);

        Assert.Equal(2, submitted.Count);
        Assert.Equal(["Add r", "Add r~0001"], submitted[1].ConvertAll(a => $"{a.ActionType} {a.Entity.RowKey}"));
        Assert.Equal("W/\"written\"", created.ETag);
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "r~0001", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertBatch_TakenKeyNowEmpty_ReadsThatKeyOnce_AndIsPlannedAgain()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", null);
        h.SetupGet("p", "r~0001", null);
        var submitted = FirstTransactionMeetsATakenKey(h);

        Assert.Equal(1, await h.Store.UpsertBatchAsync([new SplitTextEntity { Id = "p|r", Body = OnePartText }], Ct));

        Assert.Equal(2, submitted.Count);
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "r~0001", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_TakenKeyStillEmptyEveryTime_EndsAsAConcurrencyConflict_AfterFiveAttempts()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r~0001", null);
        var submitted = new List<int>();
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ =>
            {
                submitted.Add(1);
                throw TxFailed(409, 1);
            });
        h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable([]));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = OnePartText }, Ct));

        Assert.Equal(LargeValueTable.MaxWriteAttempts, submitted.Count);
    }

    [Fact]
    public async Task Create_TakenKeyHeldByAForeignRow_IsRefused_WithNoRetry()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r~0001", new TableEntity("p", "r~0001") { ETag = new ETag("W/\"x\"") });
        var submitted = FirstTransactionMeetsATakenKey(h);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = OnePartText }, Ct));

        Assert.Contains("'r~0001'", ex.Message, StringComparison.Ordinal);
        Assert.Single(submitted);
    }
}
