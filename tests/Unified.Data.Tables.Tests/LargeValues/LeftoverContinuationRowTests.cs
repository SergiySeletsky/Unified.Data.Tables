using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A continuation row whose <c>_SplitOf</c> names an entity is that entity's own, even when no stored
/// row declares it (a writer that does not know split values deleted or replaced the primary, a
/// partition delete stopped halfway, a delete raced a write). Such a leftover no longer blocks the key:
/// a write that finds one at the key of a new continuation row reads the entity's own continuation rows
/// (once, and only then), replaces the ones it needs on condition of the versions it read, and deletes
/// the others in the same transaction; a delete of an entity that has no primary row removes them. A row
/// at such a key that is not the entity's continuation row is still foreign and never overwritten.
/// The provider contract (both stores, end to end) is in <see cref="LargeBatchContract"/>; here are the
/// exact SDK calls <see cref="TableStorage{T}"/> makes, and the plan they come from.
/// </summary>
public class LeftoverContinuationRowTests
{
    private static readonly string OnePartText = new('a', 600_000);
    private static readonly string TwoPartText = new('b', 1_000_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── helpers ────────────────────────────────────────────────────────────

    private static TableEntity PriorPrimary(string rk, int parts, string etag)
    {
        var row = new TableEntity("p", rk) { ETag = new ETag(etag) };
        if (parts > 0) row[SystemColumnNames.SplitParts] = parts;
        return row;
    }

    /// <summary>A continuation row of <paramref name="rk"/> as the range query returns it.</summary>
    private static TableEntity OwnPart(string rk, int index, string etag) =>
        new("p", $"{rk}~{index:D4}") { ETag = new ETag(etag), [SystemColumnNames.SplitOf] = rk };

    private static TableTransactionFailedException TxFailed(int status, int index)
    {
        var code = status switch { 409 => "EntityAlreadyExists", 404 => "ResourceNotFound", _ => "UpdateConditionNotSatisfied" };
        var inner = new RequestFailedException(status, "failed", code, null);
        inner.Data["FailedEntity"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ex = new TableTransactionFailedException(inner);
        Assert.Equal(index, ex.FailedTransactionActionIndex);
        return ex;
    }

    private static Response<IReadOnlyList<Response>> Committed(string primaryETag = "W/\"written\"") =>
        Response.FromValue<IReadOnlyList<Response>>([new FakeResponse(primaryETag)], new FakeResponse());

    /// <summary>What the mocked table was asked to do, and the script it answers from.</summary>
    private sealed class Script
    {
        private readonly Queue<Func<Response<IReadOnlyList<Response>>>> transactionOutcomes = new();
        private readonly Queue<TableEntity[]> rangeQueryResults = new();

        public Script(StorageHarness<SplitTextEntity> h)
        {
            h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Transactions.Add(call.Arg<IEnumerable<TableTransactionAction>>().ToList());
                    return transactionOutcomes.Count > 0 ? transactionOutcomes.Dequeue()() : Committed();
                });
            h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    RangeQueries.Add(call.ArgAt<string?>(0) ?? string.Empty);
                    return Mocks.Pageable(rangeQueryResults.Count > 0 ? rangeQueryResults.Dequeue() : []);
                });
        }

        /// <summary>The actions of each transaction submitted, in order.</summary>
        public List<List<TableTransactionAction>> Transactions { get; } = [];

        /// <summary>The filter of each query, in order.</summary>
        public List<string> RangeQueries { get; } = [];

        public Script ThenFail(int status, int index)
        {
            var failure = TxFailed(status, index);
            transactionOutcomes.Enqueue(() => throw failure);
            return this;
        }

        public Script ThenCommit(string primaryETag = "W/\"written\"")
        {
            transactionOutcomes.Enqueue(() => Committed(primaryETag));
            return this;
        }

        public Script ThenList(params TableEntity[] rows)
        {
            rangeQueryResults.Enqueue(rows);
            return this;
        }
    }

    private static string Describe(TableTransactionAction action) =>
        $"{action.ActionType} {action.Entity.RowKey}" + (action.ETag == default ? string.Empty : $" If-Match {action.ETag}");

    private static List<string> Describe(List<TableTransactionAction> actions) => actions.ConvertAll(Describe);

    // ── writes ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_NewRowsKeyHeldByAnOwnLeftover_IsReplacedOnConditionOfItsVersion_AndTheOthersAreDeleted()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var script = new Script(h)
            .ThenFail(409, index: 1)
            .ThenList(OwnPart("r", 1, "W/\"e1\""), OwnPart("r", 2, "W/\"e2\""), OwnPart("r", 3, "W/\"e3\""))
            .ThenCommit("W/\"mine\"");

        var created = await h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = OnePartText }, Ct);

        Assert.Equal(2, script.Transactions.Count);
        // First the normal plan: a new continuation row is an insert, so a row at its key fails the write.
        Assert.Equal(["Add r", "Add r~0001"], Describe(script.Transactions[0]));
        // Then the entity's own rows as they were read: the one the value needs is replaced, the others go.
        Assert.Equal(
            ["Add r", "UpdateReplace r~0001 If-Match W/\"e1\"", "Delete r~0002 If-Match W/\"e2\"", "Delete r~0003 If-Match W/\"e3\""],
            Describe(script.Transactions[1]));
        var query = Assert.Single(script.RangeQueries);
        Assert.Contains("RowKey ge 'r~'", query, StringComparison.Ordinal);
        Assert.Contains("_SplitOf eq 'r'", query, StringComparison.Ordinal);
        Assert.Equal("W/\"mine\"", created.ETag);
        await h.Table.DidNotReceiveWithAnyArgs().GetEntityIfExistsAsync<TableEntity>(default!, default!, default, default);
    }

    [Fact]
    public async Task Upsert_GrowingOntoOwnLeftovers_ReplacesThemOnConditionOfTheirVersions()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        // The stored row declares one continuation row; r~0002 and r~0003 are leftovers.
        h.SetupGet("p", "r", PriorPrimary("r", 1, "W/\"v1\""));
        var script = new Script(h)
            .ThenFail(409, index: 2)
            .ThenList(OwnPart("r", 1, "W/\"e1\""), OwnPart("r", 2, "W/\"e2\""), OwnPart("r", 3, "W/\"e3\""));

        await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = TwoPartText }, Ct);

        Assert.Equal(2, script.Transactions.Count);
        Assert.Equal(["UpdateReplace r If-Match W/\"v1\"", "UpsertReplace r~0001", "Add r~0002"], Describe(script.Transactions[0]));
        Assert.Equal(
            [
                "UpdateReplace r If-Match W/\"v1\"", "UpdateReplace r~0001 If-Match W/\"e1\"",
                "UpdateReplace r~0002 If-Match W/\"e2\"", "Delete r~0003 If-Match W/\"e3\"",
            ],
            Describe(script.Transactions[1]));
        Assert.Single(script.RangeQueries);
        // The primary is read again for the second attempt: both are conditional on what was read.
        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForeignRowAtTheKey_IsRefused_AfterOneLookAtTheEntitysOwnRows_AndNothingIsRetried()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        // The range query's own filter demands _SplitOf eq 'r'; a row that slips through with another
        // value (or none) is still not the entity's.
        var script = new Script(h)
            .ThenFail(409, index: 1)
            .ThenList(new TableEntity("p", "r~0001") { ETag = new ETag("W/\"x\""), [SystemColumnNames.SplitOf] = "another" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = OnePartText }, Ct));

        Assert.Contains("'r~0001'", ex.Message, StringComparison.Ordinal);
        Assert.IsType<TableTransactionFailedException>(ex.InnerException);
        Assert.Single(script.Transactions);
        Assert.Single(script.RangeQueries);
    }

    [Fact]
    public async Task OwnLeftoverThatChangedAfterItWasRead_IsNotOverwritten_TheWriteLooksAgain()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var script = new Script(h)
            .ThenFail(409, index: 1)
            .ThenList(OwnPart("r", 1, "W/\"e1\""))
            // Between the look and the write another writer replaced r~0001: the condition fails.
            .ThenFail(412, index: 1)
            .ThenList(OwnPart("r", 1, "W/\"e1b\""))
            .ThenCommit();

        await h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = OnePartText }, Ct);

        Assert.Equal(3, script.Transactions.Count);
        Assert.Equal(["Add r", "UpdateReplace r~0001 If-Match W/\"e1\""], Describe(script.Transactions[1]));
        Assert.Equal(["Add r", "UpdateReplace r~0001 If-Match W/\"e1b\""], Describe(script.Transactions[2]));
        Assert.Equal(2, script.RangeQueries.Count);
    }

    [Fact]
    public async Task OwnLeftoverThatTurnedForeignAfterItWasRead_IsNotOverwritten_TheWriteIsRefused()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var script = new Script(h)
            .ThenFail(409, index: 1)
            .ThenList(OwnPart("r", 1, "W/\"e1\""))
            // Another writer deleted the leftover and put a row of its own at the key: the replace is
            // conditional on the leftover's version, so that row is not touched.
            .ThenFail(412, index: 1)
            .ThenList()
            .ThenFail(409, index: 1)
            .ThenList();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = OnePartText }, Ct));

        Assert.Contains("'r~0001'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, script.Transactions.Count);
        // Once the row is not the entity's, the plan inserts again: nothing ever replaces a foreign row.
        Assert.Equal(["Add r", "Add r~0001"], Describe(script.Transactions[2]));
    }

    [Fact]
    public async Task OwnLeftoverRemovedAfterItWasRead_BecomesAPlainInsert()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var script = new Script(h)
            .ThenFail(409, index: 1)
            .ThenList(OwnPart("r", 1, "W/\"e1\""))
            .ThenFail(404, index: 1)
            .ThenList()
            .ThenCommit();

        await h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = OnePartText }, Ct);

        Assert.Equal(3, script.Transactions.Count);
        Assert.Equal(["Add r", "Add r~0001"], Describe(script.Transactions[2]));
    }

    [Fact]
    public async Task NoLeftover_TheNormalPathMakesNoRangeQuery_AndNoReadAfterTheCommit()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "s", PriorPrimary("s", 0, "W/\"v1\""));
        var script = new Script(h);

        await h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = OnePartText }, Ct);
        await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|s", Body = TwoPartText }, Ct);

        Assert.Equal(["Add r", "Add r~0001"], Describe(script.Transactions[0]));
        Assert.Equal(["UpdateReplace s If-Match W/\"v1\"", "Add s~0001", "Add s~0002"], Describe(script.Transactions[1]));
        Assert.Equal(2, script.Transactions.Count);
        Assert.Empty(script.RangeQueries);
        // A create reads nothing; an upsert reads the stored row once.
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── batches ────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertBatch_OwnLeftoverOfOneEntity_IsTakenBackInTheRetriedTransaction()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet(null);
        var script = new Script(h)
            .ThenFail(409, index: 2)
            .ThenList(OwnPart("r2", 1, "W/\"e1\""), OwnPart("r2", 2, "W/\"e2\""));

        await h.Store.UpsertBatchAsync(
        [
            new SplitTextEntity { Id = "p|r1", Body = "a" },
            new SplitTextEntity { Id = "p|r2", Body = OnePartText },
            new SplitTextEntity { Id = "p|r3", Body = "c" },
        ], Ct);

        Assert.Equal(2, script.Transactions.Count);
        Assert.Equal(["Add r1", "Add r2", "Add r2~0001", "Add r3"], Describe(script.Transactions[0]));
        Assert.Equal(
            ["Add r1", "Add r2", "UpdateReplace r2~0001 If-Match W/\"e1\"", "Delete r2~0002 If-Match W/\"e2\"", "Add r3"],
            Describe(script.Transactions[1]));
        // Only the entity whose key was held is looked up.
        var query = Assert.Single(script.RangeQueries);
        Assert.Contains("_SplitOf eq 'r2'", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateBatch_OwnLeftover_IsTakenBack_AndAConditionThatFailsOnItIsRetried()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var script = new Script(h)
            .ThenFail(409, index: 1)
            .ThenList(OwnPart("r1", 1, "W/\"e1\""))
            .ThenFail(412, index: 1)
            .ThenList(OwnPart("r1", 1, "W/\"e1b\""));

        await h.Store.CreateBatchAsync([new SplitTextEntity { Id = "p|r1", Body = OnePartText }], Ct);

        Assert.Equal(3, script.Transactions.Count);
        Assert.Equal(["Add r1", "UpdateReplace r1~0001 If-Match W/\"e1b\""], Describe(script.Transactions[2]));
        await h.Table.DidNotReceiveWithAnyArgs().GetEntityIfExistsAsync<TableEntity>(default!, default!, default, default);
    }

    // ── deletes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_NoPrimaryRow_DeletesTheLeftoverContinuationRows_EachOnConditionOfItsVersion()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", null);
        h.SetupDelete();
        var script = new Script(h).ThenList(OwnPart("r", 1, "W/\"e1\""), OwnPart("r", 2, "W/\"e2\""));

        await h.Store.DeleteAsync("p|r", Ct);

        Assert.Equal(["Delete r~0001 If-Match W/\"e1\"", "Delete r~0002 If-Match W/\"e2\""], Describe(Assert.Single(script.Transactions)));
        var query = Assert.Single(script.RangeQueries);
        Assert.Contains("_SplitOf eq 'r'", query, StringComparison.Ordinal);
        await h.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Delete_NoPrimaryRow_NoLeftovers_IsOnePointReadAndOneRangeQuery_AndNoWrite()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", null);
        h.SetupDelete();
        var script = new Script(h);

        await h.Store.DeleteAsync("p|r", Ct);

        Assert.Empty(script.Transactions);
        Assert.Single(script.RangeQueries);
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await h.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Delete_StoredPrimary_MakesNoExtraRangeQuery()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"v1\""));
        h.SetupDelete();
        var script = new Script(h);

        await h.Store.DeleteAsync("p|r", Ct);

        // The normal delete of a one-row entity: the point read and the conditional delete.
        Assert.Empty(script.RangeQueries);
        Assert.Empty(script.Transactions);
        await h.Table.Received(1).DeleteEntityAsync("p", "r", new ETag("W/\"v1\""), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_OfAContinuationRowsOwnId_MakesNoRangeQuery_AndDeletesNothing()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r~0001", OwnPart("r", 1, "W/\"e1\""));
        h.SetupDelete();
        var script = new Script(h);

        await h.Store.DeleteAsync("p|r~0001", Ct);

        Assert.Empty(script.RangeQueries);
        Assert.Empty(script.Transactions);
        await h.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Delete_NoPrimaryRow_ALeftoverWasTakenBackMeanwhile_ReadsAgain_AndDeletesTheEntityThatIsThereNow()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        // First no primary; by the retry a writer has created the entity over its leftover.
        var missing = Mocks.NotFound();
        var createdMeanwhile = Mocks.Found(PriorPrimary("r", 1, "W/\"v1\""));
        h.Table.GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(missing, createdMeanwhile);
        var script = new Script(h)
            .ThenList(OwnPart("r", 1, "W/\"e1\""))
            .ThenFail(412, index: 0)
            .ThenList(OwnPart("r", 1, "W/\"e1b\""));

        await h.Store.DeleteAsync("p|r", Ct);

        Assert.Equal(2, script.Transactions.Count);
        Assert.Equal(["Delete r~0001 If-Match W/\"e1\""], Describe(script.Transactions[0]));
        Assert.Equal(["Delete r If-Match W/\"v1\"", "Delete r~0001 If-Match *"], Describe(script.Transactions[1]));
    }

    [Fact]
    public async Task Delete_NoPrimaryRow_MoreLeftoversThanOneTransactionHolds_GoInSeveral()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", null);
        var script = new Script(h).ThenList([.. Enumerable.Range(1, 150).Select(i => OwnPart("r", i, "W/\"e\""))]);

        await h.Store.DeleteAsync("p|r", Ct);

        Assert.Equal([100, 50], script.Transactions.Select(t => t.Count));
        Assert.All(script.Transactions.SelectMany(t => t), a => Assert.Equal(TableTransactionActionType.Delete, a.ActionType));
    }

    [Fact]
    public async Task Delete_NoPrimaryRow_TheTableIsGone_IsANoOp_AsForAnyOtherType()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        // A point read of a table that does not exist reads as "no row"; the range query says so.
        h.SetupGet("p", "r", null);
        h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new RequestFailedException(404, "The table specified does not exist.", "TableNotFound", null));

        await h.Store.DeleteAsync("p|r", Ct);

        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
    }
}

/// <summary>The plan a write is made from when it takes the entity's own continuation rows back.</summary>
public class OwnPartsPlanTests
{
    private static readonly string OnePartText = new('a', 600_000);
    private static readonly string TwoPartText = new('b', 1_000_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LargeValueTable Table() => new("SplitTextEntity", options: null, blobs: null);

    private static SplitTextEntity Entity(string body) => new() { Id = "p|r", Body = body };

    private static Task<IReadOnlyList<string>> NotAsked(CancellationToken ct) =>
        throw new InvalidOperationException("the existing continuation rows must not be asked for");

    [Fact]
    public void PlanOverOwnParts_ReplacesTheRowsTheValueNeeds_AndDeletesTheOthers()
    {
        var plan = Table().PlanOverOwnParts(Entity(OnePartText), "p", "r", storedPartCount: 0, ["r~0001", "r~0002", "r~0003"]);

        Assert.Equal(["r~0001"], plan.Parts.Select(p => p.RowKey));
        Assert.False(plan.IsNewPart(0));
        Assert.Equal(["r~0002", "r~0003"], plan.StalePartRowKeys);
    }

    [Fact]
    public void PlanOverOwnParts_ARowThatIsNotThere_IsStillNew()
    {
        var plan = Table().PlanOverOwnParts(Entity(TwoPartText), "p", "r", storedPartCount: 0, ["r~0002"]);

        Assert.Equal(["r~0001", "r~0002"], plan.Parts.Select(p => p.RowKey));
        Assert.True(plan.IsNewPart(0));
        Assert.False(plan.IsNewPart(1));
        Assert.Empty(plan.StalePartRowKeys);
    }

    [Fact]
    public void PlanOverOwnParts_RowsTheStoredRowDeclares_AreNeverNew_WhetherTheyExistOrNot()
    {
        var plan = Table().PlanOverOwnParts(Entity(TwoPartText), "p", "r", storedPartCount: 1, []);

        Assert.False(plan.IsNewPart(0));
        Assert.True(plan.IsNewPart(1));
        Assert.Equal(1, plan.StoredPartCount);
    }

    [Fact]
    public void PlanOverOwnParts_SmallValue_DeletesEveryOwnRow()
    {
        var plan = Table().PlanOverOwnParts(Entity("small"), "p", "r", storedPartCount: 0, ["r~0001", "r~0002"]);

        Assert.Empty(plan.Parts);
        Assert.Equal(["r~0001", "r~0002"], plan.StalePartRowKeys);
    }

    [Fact]
    public void PlanOverOwnParts_NinetyNineLeftovers_FitOneTransactionWithTheWrite()
    {
        var own = Enumerable.Range(1, 99).Select(i => $"r~{i:D4}").ToList();

        var plan = Table().PlanOverOwnParts(Entity(OnePartText), "p", "r", storedPartCount: 0, own);

        // The primary, one continuation row and 98 deletes: exactly the 100 actions of one transaction.
        Assert.Equal(98, plan.StalePartRowKeys.Count);
        Assert.Equal(BatchPlanner.DefaultMaxCount, 1 + plan.Parts.Count + plan.StalePartRowKeys.Count);
        Assert.True(SplitRowLayout.FitsOneTransaction(plan.Plan, plan.StalePartRowKeys.Count));
    }

    [Fact]
    public async Task PlanAsync_JudgesByTheDeclaredCountOnly_AndDoesNotAskForTheExistingRows()
    {
        var plan = await Table().PlanAsync(Entity(TwoPartText), "p", "r", storedPartCount: 1, NotAsked, Ct);

        Assert.False(plan.IsNewPart(0));
        Assert.True(plan.IsNewPart(1));
        Assert.Empty(plan.StalePartRowKeys);
    }

    [Fact]
    public async Task PlanAsync_AShrink_AsksOnce_AndDeletesWhatExistsBeyondTheNewValue()
    {
        var asked = 0;

        var plan = await Table().PlanAsync(Entity(OnePartText), "p", "r", storedPartCount: 2, _ =>
        {
            asked++;
            // What is really there: the two declared rows and a leftover beyond them.
            return Task.FromResult<IReadOnlyList<string>>(["r~0001", "r~0002", "r~0003"]);
        }, Ct);

        Assert.Equal(1, asked);
        Assert.False(plan.IsNewPart(0));
        Assert.Equal(["r~0002", "r~0003"], plan.StalePartRowKeys);
    }

    [Fact]
    public void IsNewPart_OutsideTheParts_Throws()
    {
        var plan = Table().PlanOverOwnParts(Entity(OnePartText), "p", "r", storedPartCount: 0, []);

        Assert.True(plan.IsNewPart(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.IsNewPart(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.IsNewPart(-1));
    }
}

/// <summary>
/// <see cref="InMemoryStorage{T}"/> has no versions to put on a transaction's actions; it holds the same
/// line under its gate: a plan made over the entity's own continuation rows is applied only while they
/// are still those rows.
/// </summary>
public class InMemoryLeftoverRaceTests
{
    private static readonly string OnePartText = new('a', 600_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableEntity Leftover(string rk, int index) =>
        new("p", $"{rk}~{index:D4}") { [SystemColumnNames.SplitOf] = rk, [SystemColumnNames.SplitIndex] = index, ["Title"] = "leftover" };

    [Fact]
    public async Task OwnLeftoverThatChangedAfterItWasRead_TheWriteIsPlannedAgain_AndSucceeds()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        store.SeedPhysicalRow(Leftover("r", 1));
        var attempts = 0;
        store.BeforeLargeCommit = () =>
        {
            // Attempt 1 meets the leftover; attempt 2 has read the entity's own rows: change one now.
            if (++attempts == 2)
                store.SeedPhysicalRow(Leftover("r", 1));
        };

        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = OnePartText }, Ct);

        Assert.Equal(3, attempts);
        Assert.Equal(["r", "r~0001"], store.PhysicalRowKeys("p"));
        Assert.False(store.PhysicalRow("p", "r~0001")!.ContainsKey("Title"));
        Assert.Equal(OnePartText, (await store.OneAsync("p|r", Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task OwnLeftoverThatTurnedForeignAfterItWasRead_IsNotOverwritten_TheWriteIsRefused()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        store.SeedPhysicalRow(Leftover("r", 1));
        var attempts = 0;
        store.BeforeLargeCommit = () =>
        {
            // After attempt 2 read the leftover, another writer puts a row of its own at the key.
            if (++attempts == 2)
                store.SeedPhysicalRow(new TableEntity("p", "r~0001") { ["Title"] = "foreign" });
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = OnePartText }, Ct));

        Assert.Contains("'r~0001'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, attempts);
        Assert.Equal(["r~0001"], store.PhysicalRowKeys("p"));
        Assert.Equal("foreign", store.PhysicalRow("p", "r~0001")!["Title"]);
    }

    [Fact]
    public async Task BatchTransaction_OwnLeftoverThatChangedAfterItWasRead_IsPlannedAgain_AndNothingIsHalfWritten()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        store.SeedPhysicalRow(Leftover("r2", 1));
        var attempts = 0;
        store.BeforeLargeCommit = () =>
        {
            if (++attempts == 2)
            {
                // Nothing of the first two attempts is stored.
                Assert.Equal(["r2~0001"], store.PhysicalRowKeys("p"));
                store.SeedPhysicalRow(Leftover("r2", 1));
            }
        };

        await store.UpsertBatchAsync(
        [
            new SplitTextEntity { Id = "p|r1", Body = "a" },
            new SplitTextEntity { Id = "p|r2", Body = OnePartText },
        ], Ct);

        Assert.Equal(3, attempts);
        Assert.Equal(["r1", "r2", "r2~0001"], store.PhysicalRowKeys("p"));
        Assert.Equal(OnePartText, (await store.OneAsync("p|r2", Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Delete_NoPrimaryRow_RemovesOnlyTheEntitysOwnContinuationRows()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        store.SeedPhysicalRow(Leftover("r", 1));
        store.SeedPhysicalRow(Leftover("r", 2));
        store.SeedPhysicalRow(new TableEntity("p", "r~0003") { ["Title"] = "foreign" });
        store.SeedPhysicalRow(new TableEntity("p", "r~0004") { [SystemColumnNames.SplitOf] = "another", ["Title"] = "theirs" });
        store.SeedPhysicalRow(new TableEntity("q", "r~0001") { [SystemColumnNames.SplitOf] = "r" }); // another partition
        var commits = 0;
        store.AfterLargeCommit = () => commits++;

        await store.DeleteAsync("p|r", Ct);

        Assert.Equal(["r~0003", "r~0004"], store.PhysicalRowKeys("p"));
        Assert.Equal(["r~0001"], store.PhysicalRowKeys("q"));
        Assert.Equal(1, commits);

        // Nothing left of it: a second delete commits nothing.
        await store.DeleteAsync("p|r", Ct);
        Assert.Equal(1, commits);
    }
}

/// <summary>
/// The finding as it was reproduced, on a local Azurite: a store of a type that does not know split
/// values (sharing the table, as an older version of the application or another service does) deletes
/// the primary row and leaves the continuation row behind. The key is writable again, and the requests
/// show that only the write that meets a leftover pays for the look at the entity's own rows.
/// </summary>
public sealed class AzuriteLeftoverContinuationRowTests : AzuriteCleanupTestBase
{
    private static readonly string TwoRows = new('a', 600_000);

    private const string TableName = nameof(SplitTextEntity);

    /// <summary>The same table through a type with no split member: its delete removes one row.</summary>
    private TableStorage<PlainEntity> PlainStore() => Store<PlainEntity>(table: TableName);

    private async Task<List<string>> RowKeysAsync() => (await RawRowsAsync(TableName)).ConvertAll(r => r.RowKey);

    [Fact]
    public async Task PrimaryDeletedByAStoreThatDoesNotKnowSplitValues_TheKeyIsWritableAgain()
    {
        SkipUnlessAvailable();
        var store = SplitOnlyStore<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = TwoRows }, Ct);
        await PlainStore().DeleteAsync(Id("r"), Ct);
        Assert.Equal(["r~0001"], await RowKeysAsync());
        var other = new string('b', 600_000);

        await store.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = other }, Ct);

        Assert.Equal(["r", "r~0001"], await RowKeysAsync());
        Assert.Equal(other, (await store.OneAsync(Id("r"), Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task PrimaryReplacedByAStoreThatDoesNotKnowSplitValues_TheNextLargeUpsertTakesTheRowsBack()
    {
        SkipUnlessAvailable();
        var store = SplitOnlyStore<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = TwoRows }, Ct);
        // A plain upsert rewrites the primary without the part count: r~0001 is declared by nothing now.
        await PlainStore().UpsertAsync(new PlainEntity { Id = Id("r"), Body = "plain" }, Ct);
        Assert.Equal(["r", "r~0001"], await RowKeysAsync());
        Assert.Equal("plain", (await store.OneAsync(Id("r"), Ct))!.Body);

        await store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = TwoRows }, Ct);

        Assert.Equal(["r", "r~0001"], await RowKeysAsync());
        Assert.Equal(TwoRows, (await store.OneAsync(Id("r"), Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task PrimaryDeletedByAStoreThatDoesNotKnowSplitValues_DeletingTheEntityRemovesWhatIsLeft()
    {
        SkipUnlessAvailable();
        var store = SplitOnlyStore<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = new string('c', 1_000_000) }, Ct);
        await PlainStore().DeleteAsync(Id("r"), Ct);
        Assert.Equal(["r~0001", "r~0002"], await RowKeysAsync());

        await store.DeleteAsync(Id("r"), Ct);

        Assert.Empty(await RowKeysAsync());
    }

    [Fact]
    public async Task OnlyTheWriteThatMeetsALeftover_LooksForTheEntitysOwnRows()
    {
        SkipUnlessAvailable();
        var requests = new List<string>();
        var store = SplitOnlyStore<SplitTextEntity>(TableServiceWith(new RequestRecorder(requests)));
        await store.EnsureCreatedAsync(Ct);

        // No leftover: an insert with a continuation row is one transaction and nothing else.
        lock (requests) requests.Clear();
        await store.CreateAsync(new SplitTextEntity { Id = Id("clean"), Body = TwoRows }, Ct);
        Assert.Equal(["POST $batch"], Snapshot(requests));

        // An upsert over it: the point read and one transaction.
        lock (requests) requests.Clear();
        await store.UpsertAsync(new SplitTextEntity { Id = Id("clean"), Body = new string('b', 600_000) }, Ct);
        Assert.Equal(["GET entity", "POST $batch"], Snapshot(requests));

        // A leftover at the key: the failed transaction, one range query, the transaction again.
        await store.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = TwoRows }, Ct);
        await PlainStore().DeleteAsync(Id("r"), Ct);
        lock (requests) requests.Clear();
        await store.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = TwoRows }, Ct);
        Assert.Equal(["POST $batch", "GET query", "POST $batch"], Snapshot(requests));
    }

    private static List<string> Snapshot(List<string> requests)
    {
        lock (requests) return [.. requests];
    }

    /// <summary>Records each table request as its method and kind (<c>$batch</c>, <c>entity</c>, <c>query</c>).</summary>
    private sealed class RequestRecorder(List<string> log) : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            var path = message.Request.Uri.Path;
            var kind = path.EndsWith("$batch", StringComparison.Ordinal) ? "$batch"
                : path.Contains("(PartitionKey=", StringComparison.Ordinal) ? "entity"
                : path.EndsWith("/Tables", StringComparison.Ordinal) ? "Tables"
                : "query";
            lock (log) log.Add(message.Request.Method.Method + " " + kind);
        }
    }
}
