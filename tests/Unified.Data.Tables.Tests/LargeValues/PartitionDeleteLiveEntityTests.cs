using System.Globalization;
using System.Linq.Expressions;
using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A partition delete never removes a continuation row of an entity that is live when the row is
/// removed, unless the same transaction removes that entity's main row (ROWS-1 of the fix-wave-2
/// review). The listing is read page by page and its transactions run one after another, so it can
/// list continuation rows without their main row: a leftover, or the rows of an entity created between
/// two listing pages whose main row sorts before the page boundary (<c>doc1</c> &lt; <c>doc10</c> &lt;
/// <c>doc1~0001</c>). Such rows are deleted only when, after they were listed, no main row exists for
/// them, and only while each is still the row that was listed. An entity written while the partition is
/// deleted is, afterwards, whole or gone; the rest of the partition is still deleted and the call
/// returns. Against the local Azurite through the real SDK, and in memory.
/// </summary>
public sealed class PartitionDeleteLiveEntityTests : AzuriteCleanupTestBase
{
    private static readonly string OnePart = new('a', 600_000);
    private static readonly string TwoParts = new('b', 1_000_000);

    private const string TableName = nameof(SplitTextEntity);

    private async Task<List<string>> RowKeysAsync() => (await RawRowsAsync(TableName)).ConvertAll(r => r.RowKey);

    /// <summary>The leftover an earlier partition delete that raced a growing upsert leaves: <c>r~0002</c>, no main row.</summary>
    private async Task SeedSecondPartLeftoverAsync() =>
        await TableService.GetTableClient(TableName).AddEntityAsync(RowRequestInterleaving.ContinuationRowsOf(Pk, "r", TwoParts)[1], Ct);

    private static string Outcome(Exception? read) => read is null ? "ok" : read.GetType().Name + ": " + read.Message;

    [Fact]
    public async Task Azurite_EntityCreatedOverALeftoverWhileThePartitionIsDeleted_SurvivesWhole()
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        await writer.EnsureCreatedAsync(Ct);
        await SeedSecondPartLeftoverAsync();
        Assert.Equal(["r~0002"], await RowKeysAsync());

        // The delete has listed r~0002. Before its transaction, another caller creates r with two
        // continuation rows: the create takes the leftover back and reports success.
        SplitTextEntity? created = null;
        var interleaved = new RowRequestInterleaving.BeforeTransaction(1, async () =>
            created = await writer.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = TwoParts }, CancellationToken.None));
        var deleter = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await deleter.EnsureCreatedAsync(Ct);

        var deleted = await deleter.DeletePartitionAsync(Pk, Ct);

        Assert.Equal(1, interleaved.Fired);
        Assert.NotNull(created);
        var keys = await RowKeysAsync();
        var read = await Record.ExceptionAsync(() => SplitOnlyStore<SplitTextEntity>().OneAsync(Id("r"), Ct));
        Assert.True(read is null && (keys.Count == 0 || keys.SequenceEqual(["r", "r~0001", "r~0002"])),
            $"DeletePartitionAsync returned {deleted}. Rows left: [{string.Join(", ", keys)}]. Reading the entity that CreateAsync reported as stored: {Outcome(read)}");
        Assert.Equal(0, deleted);
    }

    /// <summary>After such a delete every listing read of the partition still works, also for its other entities.</summary>
    [Fact]
    public async Task Azurite_AfterTheDelete_ThePartitionCanStillBeQueried()
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        await writer.EnsureCreatedAsync(Ct);
        await SeedSecondPartLeftoverAsync();
        var interleaved = new RowRequestInterleaving.BeforeTransaction(1,
            () => writer.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = TwoParts }, CancellationToken.None));
        var deleter = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await deleter.EnsureCreatedAsync(Ct);
        await deleter.DeletePartitionAsync(Pk, Ct);
        await writer.CreateAsync(new SplitTextEntity { Id = Id("neighbour"), Body = "small" }, Ct);

        var store = SplitOnlyStore<SplitTextEntity>();
        Assert.Equal(2, (await store.QueryAsync(Pk, Ct)).Count());
        Assert.Equal(2, (await store.QueryAsync(new QueryOptions { Partition = Pk }, Ct)).Count);
        Assert.Equal(2, (await store.QueryPageAsync(new QueryOptions { Partition = Pk }, Ct)).Items.Count);
        Assert.True(await store.ExistsAsync(Id("r"), Ct));
        Assert.Equal(2, await store.CountAsync(Pk, Ct));
        Assert.Equal(TwoParts, (await store.OneAsync(Id("r"), Ct))!.Body, StringComparer.Ordinal);
    }

    /// <summary>The whole chain with calls of the split-aware store only: two deletes, each overlapping one write of one entity.</summary>
    [Fact]
    public async Task Azurite_TwoPartitionDeletesThatEachOverlapAWriteOfOneEntity_LeaveThatEntityWholeOrGone()
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        await writer.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = OnePart }, Ct);
        Assert.Equal(["r", "r~0001"], await RowKeysAsync());

        // First delete: it listed r and r~0001; an upsert grows r to two continuation rows before its
        // transaction. What it listed goes; r~0002 stays behind as a leftover.
        var first = new RowRequestInterleaving.BeforeTransaction(1,
            () => writer.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = TwoParts }, CancellationToken.None));
        var firstDeleter = SplitOnlyStore<SplitTextEntity>(TableServiceWith(first));
        await firstDeleter.EnsureCreatedAsync(Ct);
        Assert.Equal(1, await firstDeleter.DeletePartitionAsync(Pk, Ct));
        Assert.Equal(["r~0002"], await RowKeysAsync());

        // Second delete: it listed the leftover; the entity is created again before the leftover's delete.
        var second = new RowRequestInterleaving.BeforeTransaction(1,
            () => writer.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = TwoParts }, CancellationToken.None));
        var secondDeleter = SplitOnlyStore<SplitTextEntity>(TableServiceWith(second));
        await secondDeleter.EnsureCreatedAsync(Ct);
        var deleted = await secondDeleter.DeletePartitionAsync(Pk, Ct);

        Assert.Equal(1, second.Fired);
        var keys = await RowKeysAsync();
        var read = await Record.ExceptionAsync(() => SplitOnlyStore<SplitTextEntity>().OneAsync(Id("r"), Ct));
        Assert.True(read is null && (keys.Count == 0 || keys.SequenceEqual(["r", "r~0001", "r~0002"])),
            $"The second DeletePartitionAsync returned {deleted}. Rows left: [{string.Join(", ", keys)}]. Reading the entity that CreateAsync reported as stored: {Outcome(read)}");
    }

    /// <summary>
    /// The entity is created between two listing pages, the second of which begins with the leftover:
    /// the listing then holds the continuation rows as they are after the write (current versions) and
    /// not the main row. Only a look for the main row after the listing tells them apart from leftovers.
    /// </summary>
    [Fact]
    public async Task Azurite_EntityCreatedBetweenTwoPagesOfTheListing_OverALeftover_SurvivesWhole()
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        for (var chunk = 0; chunk < 10; chunk++)
            await writer.CreateBatchAsync(
                Enumerable.Range(chunk * 100, 100).Select(i => new SplitTextEntity { Id = Id($"a{i:D4}"), Body = "v" }).ToList(), Ct);
        await SeedSecondPartLeftoverAsync();

        var interleaved = new RowRequestInterleaving.BeforeRangeQuery(2,
            () => writer.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = TwoParts }, CancellationToken.None));
        var deleter = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await deleter.EnsureCreatedAsync(Ct);

        var deleted = await deleter.DeletePartitionAsync(Pk, Ct);

        Assert.Equal(1, interleaved.Fired);
        var keys = await RowKeysAsync();
        var read = await Record.ExceptionAsync(() => SplitOnlyStore<SplitTextEntity>().OneAsync(Id("r"), Ct));
        Assert.True(read is null && (keys.Count == 0 || keys.SequenceEqual(["r", "r~0001", "r~0002"])),
            $"DeletePartitionAsync returned {deleted} after {interleaved.Queries} listing request(s). Rows left: [{string.Join(", ", keys)}]. Reading the entity that CreateAsync reported as stored: {Outcome(read)}");
        Assert.Equal(1000, deleted);
    }

    /// <summary>The same with no leftover anywhere: one create between two listing pages is enough.</summary>
    [Fact]
    public async Task Azurite_EntityCreatedBetweenTwoPagesOfTheListing_WithNoLeftoverInTheTable_SurvivesWhole()
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        // A thousand one-row entities fill the first page; the ordinary entity doc10 begins the second.
        for (var chunk = 0; chunk < 10; chunk++)
            await writer.CreateBatchAsync(
                Enumerable.Range(chunk * 100, 100).Select(i => new SplitTextEntity { Id = Id($"a{i:D4}"), Body = "v" }).ToList(), Ct);
        await writer.CreateAsync(new SplitTextEntity { Id = Id("doc10"), Body = "v" }, Ct);
        Assert.Equal(1001, (await RowKeysAsync()).Count);

        // Between the two pages, doc1 is created: doc1 sorts behind the listing, doc1~0001 ahead of it.
        SplitTextEntity? created = null;
        var interleaved = new RowRequestInterleaving.BeforeRangeQuery(2, async () =>
            created = await writer.CreateAsync(new SplitTextEntity { Id = Id("doc1"), Body = OnePart }, CancellationToken.None));
        var deleter = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await deleter.EnsureCreatedAsync(Ct);

        var deleted = await deleter.DeletePartitionAsync(Pk, Ct);

        Assert.Equal(1, interleaved.Fired);
        Assert.NotNull(created);
        var keys = await RowKeysAsync();
        var read = await Record.ExceptionAsync(() => SplitOnlyStore<SplitTextEntity>().OneAsync(Id("doc1"), Ct));
        Assert.True(read is null && (keys.Count == 0 || keys.SequenceEqual(["doc1", "doc1~0001"])),
            $"DeletePartitionAsync returned {deleted} after {interleaved.Queries} listing request(s). Rows left: [{string.Join(", ", keys)}]. Reading the entity that CreateAsync reported as stored: {Outcome(read)}");
        Assert.Equal(1001, deleted);
    }

    /// <summary>A control: an entity whose main row was listed and that grows between the two pages is deleted whole.</summary>
    [Fact]
    public async Task Azurite_EntityThatGrowsBetweenTwoPagesOfTheListing_IsDeletedWhole()
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        for (var chunk = 0; chunk < 10; chunk++)
            await writer.CreateBatchAsync(
                Enumerable.Range(chunk * 100, chunk == 9 ? 99 : 100).Select(i => new SplitTextEntity { Id = Id($"a{i:D4}"), Body = "v" }).ToList(), Ct);
        await writer.CreateAsync(new SplitTextEntity { Id = Id("doc1"), Body = "v" }, Ct);
        await writer.CreateAsync(new SplitTextEntity { Id = Id("doc10"), Body = "v" }, Ct);
        Assert.Equal(1001, (await RowKeysAsync()).Count);

        var interleaved = new RowRequestInterleaving.BeforeRangeQuery(2,
            () => writer.UpsertAsync(new SplitTextEntity { Id = Id("doc1"), Body = OnePart }, CancellationToken.None));
        var deleter = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await deleter.EnsureCreatedAsync(Ct);

        var deleted = await deleter.DeletePartitionAsync(Pk, Ct);

        Assert.Equal(1, interleaved.Fired);
        Assert.Equal(1001, deleted);
        Assert.Empty(await RowKeysAsync());
    }

    /// <summary>The upsert lands between the delete's first transaction and the leftover's: the other rows still go.</summary>
    [Fact]
    public async Task Azurite_EntityUpsertedOverALeftoverWhileALargePartitionIsDeleted_SurvivesWhole()
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        await writer.CreateBatchAsync(Enumerable.Range(0, 100).Select(i => new SplitTextEntity { Id = Id($"a{i:D3}"), Body = "v" }).ToList(), Ct);
        await SeedSecondPartLeftoverAsync();

        var interleaved = new RowRequestInterleaving.BeforeTransaction(2,
            () => writer.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = TwoParts }, CancellationToken.None));
        var deleter = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await deleter.EnsureCreatedAsync(Ct);

        var deleted = await deleter.DeletePartitionAsync(Pk, Ct);

        Assert.Equal(1, interleaved.Fired);
        var keys = await RowKeysAsync();
        var read = await Record.ExceptionAsync(() => SplitOnlyStore<SplitTextEntity>().OneAsync(Id("r"), Ct));
        Assert.True(read is null && (keys.Count == 0 || keys.SequenceEqual(["r", "r~0001", "r~0002"])),
            $"DeletePartitionAsync returned {deleted}. Rows left: [{string.Join(", ", keys)}]. Reading the entity that UpsertAsync reported as stored: {Outcome(read)}");
        Assert.Equal(100, deleted);
    }

    /// <summary>With no concurrent writer, leftovers (with no main row, several to one entity) still go, and only entities are counted.</summary>
    [Fact]
    public async Task Azurite_NoConcurrentWriter_LeftoversAreStillDeleted_AndOnlyEntitiesAreCounted()
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        await writer.CreateAsync(new SplitTextEntity { Id = Id("a"), Body = OnePart }, Ct);
        await writer.CreateAsync(new SplitTextEntity { Id = Id("z"), Body = "small" }, Ct);
        foreach (var part in RowRequestInterleaving.ContinuationRowsOf(Pk, "r", TwoParts))
            await TableService.GetTableClient(TableName).AddEntityAsync(part, Ct);
        await SeedLeftoverOfAsync("q", OnePart);
        Assert.Equal(["a", "a~0001", "q~0001", "r~0001", "r~0002", "z"], await RowKeysAsync());

        Assert.Equal(2, await SplitOnlyStore<SplitTextEntity>().DeletePartitionAsync(Pk, Ct));

        Assert.Empty(await RowKeysAsync());
    }

    private async Task SeedLeftoverOfAsync(string rk, string body)
    {
        foreach (var part in RowRequestInterleaving.ContinuationRowsOf(Pk, rk, body))
            await TableService.GetTableClient(TableName).AddEntityAsync(part, Ct);
    }

    // ── InMemoryStorage: the same guarantees ────────────────────────────────

    [Fact]
    public async Task InMemory_EntityCreatedOverALeftoverWhileThePartitionIsDeleted_SurvivesWhole()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        // A hundred one-row entities fill the delete's first transaction; the leftover comes after it.
        await store.CreateBatchAsync(Enumerable.Range(0, 100).Select(i => new SplitTextEntity { Id = $"p|a{i:D3}", Body = "v" }).ToList(), Ct);
        store.SeedPhysicalRow(RowRequestInterleaving.ContinuationRowsOf("p", "r", TwoParts)[1]);
        var fired = 0;
        store.AfterLargeCommit = () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
                store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = TwoParts }, CancellationToken.None).GetAwaiter().GetResult();
        };

        var deleted = await store.DeletePartitionAsync("p", Ct);

        Assert.Equal(1, fired);
        var keys = store.PhysicalRowKeys("p");
        var read = await Record.ExceptionAsync(() => store.OneAsync("p|r", Ct));
        Assert.True(read is null && (keys.Count == 0 || keys.SequenceEqual(["r", "r~0001", "r~0002"])),
            $"DeletePartitionAsync returned {deleted}. Rows left: [{string.Join(", ", keys)}]. Reading the entity that CreateAsync reported as stored: {Outcome(read)}");
        Assert.Equal(100, deleted);
    }

    /// <summary>A listed leftover that changed before its delete (no main row appeared) is left alone; the rest goes.</summary>
    [Fact]
    public async Task InMemory_LeftoverThatChangedAfterItWasListed_IsLeftAlone_TheRestIsDeleted()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        await store.CreateBatchAsync(Enumerable.Range(0, 100).Select(i => new SplitTextEntity { Id = $"p|a{i:D3}", Body = "v" }).ToList(), Ct);
        var leftover = RowRequestInterleaving.ContinuationRowsOf("p", "r", TwoParts)[1];
        store.SeedPhysicalRow(leftover);
        var fired = 0;
        store.AfterLargeCommit = () =>
        {
            // Another writer rewrites the row after it was listed (a new version, still no main row).
            if (Interlocked.Exchange(ref fired, 1) == 0)
                store.SeedPhysicalRow(leftover);
        };

        Assert.Equal(100, await store.DeletePartitionAsync("p", Ct));

        Assert.Equal(["r~0002"], store.PhysicalRowKeys("p"));
    }

    [Fact]
    public async Task InMemory_NoConcurrentWriter_LeftoversAreStillDeleted_AndOnlyEntitiesAreCounted()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = "p|a", Body = OnePart }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = "p|z", Body = "small" }, Ct);
        foreach (var part in RowRequestInterleaving.ContinuationRowsOf("p", "r", TwoParts))
            store.SeedPhysicalRow(part);
        store.SeedPhysicalRow(RowRequestInterleaving.ContinuationRowsOf("p", "q", OnePart)[0]);
        Assert.Equal(["a", "a~0001", "q~0001", "r~0001", "r~0002", "z"], store.PhysicalRowKeys("p"));

        Assert.Equal(2, await store.DeletePartitionAsync("p", Ct));

        Assert.Empty(store.PhysicalRowKeys("p"));
    }
}

/// <summary>
/// The SDK calls of <see cref="TableStorage{T}"/>'s partition delete around continuation rows listed
/// without their main row (a mocked table): one point read of the main row's key per such entity after
/// the listing; the rows deleted in a transaction of their own, each on condition of the version it was
/// listed at; a version that no longer matches, or a row that is gone, leaves the rows alone without
/// failing the delete. A listing in which every continuation row comes with its main row costs what it
/// cost before: no point read, no condition.
/// </summary>
public class PartitionDeleteListedLeftoverCallTests
{
    private const string Pk = "p";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableEntity Primary(string rk) => new(Pk, rk) { ETag = new ETag("W/\"m-" + rk + "\""), [SystemColumnNames.SplitOf] = null };

    private static TableEntity Part(string rk, int index) =>
        new(Pk, rk + "~" + index.ToString("D4", CultureInfo.InvariantCulture))
        {
            ETag = new ETag($"W/\"p-{rk}-{index}\""),
            [SystemColumnNames.SplitOf] = rk,
        };

    private static void Lists(StorageHarness<SplitTextEntity> h, params TableEntity[] rows) =>
        h.Table.QueryAsync<TableEntity>(Arg.Any<Expression<Func<TableEntity, bool>>>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable(rows));

    private static string Describe(TableTransactionAction action) =>
        $"{action.ActionType} {action.Entity.RowKey}" + (action.ETag == ETag.All ? string.Empty : $" If-Match {action.ETag}");

    /// <summary>Records each transaction; <paramref name="fail"/> may throw for one of them (by its actions).</summary>
    private static List<List<string>> Transactions(StorageHarness<SplitTextEntity> h, Func<List<TableTransactionAction>, Exception?>? fail = null)
    {
        var submitted = new List<List<string>>();
        var response = Substitute.For<Response<IReadOnlyList<Response>>>();
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var actions = call.Arg<IEnumerable<TableTransactionAction>>().ToList();
                submitted.Add(actions.ConvertAll(Describe));
                if (fail?.Invoke(actions) is { } ex)
                    throw ex;
                return response;
            });
        return submitted;
    }

    private static TableTransactionFailedException TxFailed(int status, int index)
    {
        var inner = new RequestFailedException(status, "failed", status == 404 ? "ResourceNotFound" : "UpdateConditionNotSatisfied", null);
        inner.Data["FailedEntity"] = index.ToString(CultureInfo.InvariantCulture);
        return new TableTransactionFailedException(inner);
    }

    [Fact]
    public async Task EveryContinuationRowListedWithItsMainRow_NoPointRead_AndNoCondition()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        Lists(h, Primary("a"), Part("a", 1), Primary("b"));
        var submitted = Transactions(h);

        Assert.Equal(2, await h.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal([["Delete a", "Delete a~0001", "Delete b"]], submitted);
        await h.Table.DidNotReceiveWithAnyArgs().GetEntityIfExistsAsync<TableEntity>(default!, default!, default, default);
        await h.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task LeftoversWithNoMainRow_AreDeletedOnConditionOfTheirListedVersions_AfterOnePointReadOfTheMainRowsKey()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        Lists(h, Primary("a"), Part("r", 1), Part("r", 2));
        h.SetupGet(Pk, "r", null);
        var submitted = Transactions(h);

        Assert.Equal(1, await h.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal([["Delete a"], ["Delete r~0001 If-Match W/\"p-r-1\"", "Delete r~0002 If-Match W/\"p-r-2\""]], submitted);
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(Pk, "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await h.Table.ReceivedWithAnyArgs(1).GetEntityIfExistsAsync<TableEntity>(default!, default!, default, default);
    }

    [Fact]
    public async Task ContinuationRowsWhoseMainRowExistsNow_AreLeftAlone_AndTheRestIsDeleted()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        Lists(h, Primary("a"), Part("r", 1));
        h.SetupGet(Pk, "r", new TableEntity(Pk, "r") { ETag = new ETag("W/\"live\""), [SystemColumnNames.SplitParts] = 1 });
        var submitted = Transactions(h);

        Assert.Equal(1, await h.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal([["Delete a"]], submitted);
    }

    [Fact]
    public async Task ARowAtTheMainKeyThatIsItselfAContinuationRow_IsNoMainRow_TheLeftoversGo()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        Lists(h, Part("r", 1));
        h.SetupGet(Pk, "r", new TableEntity(Pk, "r") { ETag = new ETag("W/\"x\""), [SystemColumnNames.SplitOf] = "other" });
        var submitted = Transactions(h);

        Assert.Equal(0, await h.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal([["Delete r~0001 If-Match W/\"p-r-1\""]], submitted);
    }

    [Theory]
    [InlineData(412)]
    [InlineData(404)]
    public async Task ALeftoverThatChangedOrIsGone_IsLeftAlone_TheOthersOfItsEntityAndThePartitionStillGo_AndTheCallReturns(int status)
    {
        using var h = new StorageHarness<SplitTextEntity>();
        Lists(h, Primary("a"), Part("r", 1), Part("r", 2), Part("s", 1));
        h.SetupGet(Pk, "r", null);
        h.SetupGet(Pk, "s", null);
        var singles = new List<string>();
        h.Table.DeleteEntityAsync(Pk, Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var rk = call.ArgAt<string>(1);
                singles.Add($"{rk} If-Match {call.ArgAt<ETag>(2)}");
                return rk == "r~0001" ? throw new RequestFailedException(status, "changed") : (Response)new FakeResponse();
            });
        // The transaction of r's rows fails on r~0001 (another writer took it back, or deleted it).
        var submitted = Transactions(h, actions => actions.Any(a => a.Entity.RowKey == "r~0001") ? TxFailed(status, 0) : null);

        Assert.Equal(1, await h.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(
            [["Delete a"], ["Delete r~0001 If-Match W/\"p-r-1\"", "Delete r~0002 If-Match W/\"p-r-2\""], ["Delete s~0001 If-Match W/\"p-s-1\""]],
            submitted);
        // Each row of the failed transaction is then deleted on its own condition: r~0002 goes, r~0001 stays.
        Assert.Equal(["r~0001 If-Match W/\"p-r-1\"", "r~0002 If-Match W/\"p-r-2\""], singles);
    }

    [Fact]
    public async Task AFailureThatIsNoChange_FailsThePartitionDelete_AsAnyFailedTransactionDoes()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        Lists(h, Primary("a"), Part("r", 1));
        h.SetupGet(Pk, "r", null);
        Transactions(h, actions => actions.Any(a => a.Entity.RowKey == "r~0001") ? new RequestFailedException(503, "unavailable") : null);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => h.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(503, ex.Status);
    }
}
