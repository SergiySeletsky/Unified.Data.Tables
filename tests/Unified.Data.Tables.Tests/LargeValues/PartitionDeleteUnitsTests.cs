using System.Globalization;
using System.Linq.Expressions;
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
/// A partition delete removes the listed rows in entity-group transactions of at most 100 deletes. A
/// primary row and its continuation rows (the rows whose <c>_SplitOf</c> is its row key) always go in
/// the SAME transaction, so a failure or a cancellation between two transactions never leaves
/// continuation rows without their primary; continuation rows whose primary is not listed are deleted
/// too, as a unit of their own. The returned count stays the number of logical entities.
/// </summary>
public class PartitionDeleteUnitsTests
{
    private const string Pk = "p";
    private const int Max = BatchPlanner.DefaultMaxCount;

    private static readonly string TwoRowText = new('s', 600_000); // a primary and one continuation row

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── rows as a partition delete lists them (keys and _SplitOf) ──────────

    /// <summary>A row that is not a continuation row, as the service returns it for the delete's projection.</summary>
    private static TableEntity Primary(string rk) => new(Pk, rk) { [SystemColumnNames.SplitOf] = null };

    private static TableEntity Part(string rk, int index) =>
        new(Pk, rk + "~" + index.ToString("D4", CultureInfo.InvariantCulture)) { [SystemColumnNames.SplitOf] = rk };

    /// <summary>A primary followed by <paramref name="parts"/> continuation rows.</summary>
    private static IEnumerable<TableEntity> Unit(string rk, int parts) =>
        Enumerable.Range(1, parts).Select(i => Part(rk, i)).Prepend(Primary(rk));

    private static IEnumerable<TableEntity> Singles(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => Primary(prefix + i.ToString("D3", CultureInfo.InvariantCulture)));

    private static List<int> Sizes(IReadOnlyList<IReadOnlyList<TableEntity>> transactions) => transactions.Select(t => t.Count).ToList();

    /// <summary>The key of the unit a row belongs to: the primary it continues, or itself.</summary>
    private static string UnitOf(TableEntity row) =>
        row.TryGetValue(SystemColumnNames.SplitOf, out var of) && of is string primary ? primary : row.RowKey;

    /// <summary>Every row exactly once, at most 100 per transaction, and no unit in two transactions.</summary>
    private static void AssertWholeUnits(IReadOnlyList<TableEntity> rows, IReadOnlyList<IReadOnlyList<TableEntity>> transactions)
    {
        Assert.Equal(rows.Select(r => r.RowKey).Order(StringComparer.Ordinal), transactions.SelectMany(t => t).Select(r => r.RowKey).Order(StringComparer.Ordinal));
        Assert.All(transactions, t => Assert.InRange(t.Count, 1, Max));
        var transactionOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < transactions.Count; i++)
        {
            foreach (var row in transactions[i])
            {
                if (transactionOf.TryGetValue(UnitOf(row), out var other))
                    Assert.True(other == i, $"the unit '{UnitOf(row)}' is in transactions {other} and {i}");
                transactionOf[UnitOf(row)] = i;
            }
        }
    }

    // ── the chunking helper ───────────────────────────────────────────────

    [Fact]
    public void Plan_NoRows_NoTransactions()
    {
        Assert.Empty(LargeValueTable.PlanPartitionDelete([]));
    }

    [Fact]
    public void Plan_RowsWithoutContinuationRows_AreChunksOfAHundred_InTheOrderListed()
    {
        List<TableEntity> rows = [.. Singles("a", 250)];

        var transactions = LargeValueTable.PlanPartitionDelete(rows);

        Assert.Equal([100, 100, 50], Sizes(transactions));
        Assert.Equal(rows.Select(r => r.RowKey), transactions.SelectMany(t => t).Select(r => r.RowKey));
    }

    [Fact]
    public void Plan_PrimaryAtThe100thRow_GoesWithItsContinuationRows_IntoTheNextTransaction()
    {
        List<TableEntity> rows = [.. Singles("a", 99), .. Unit("b", 3)];

        var transactions = LargeValueTable.PlanPartitionDelete(rows);

        Assert.Equal([99, 4], Sizes(transactions));
        Assert.Equal(["b", "b~0001", "b~0002", "b~0003"], transactions[1].Select(r => r.RowKey));
        AssertWholeUnits(rows, transactions);
    }

    [Fact]
    public void Plan_UnitOfAHundredRows_FillsATransactionOfItsOwn()
    {
        // The largest entity there is: a primary and 99 continuation rows.
        List<TableEntity> rows = [Primary("a"), .. Unit("b", 99), Primary("c")];

        var transactions = LargeValueTable.PlanPartitionDelete(rows);

        Assert.Equal([1, 100, 1], Sizes(transactions));
        AssertWholeUnits(rows, transactions);
    }

    [Theory]
    [InlineData(new[] { 50, 50 }, new[] { 100 })]            // two units fill one transaction exactly
    [InlineData(new[] { 60, 40, 1 }, new[] { 100, 1 })]      // ... and the next row starts another
    [InlineData(new[] { 100, 100 }, new[] { 100, 100 })]
    [InlineData(new[] { 99, 2 }, new[] { 99, 2 })]           // one row short: the next unit does not fit
    [InlineData(new[] { 99, 1, 100 }, new[] { 100, 100 })]
    [InlineData(new[] { 1, 100, 99, 1 }, new[] { 1, 100, 100 })]
    [InlineData(new[] { 34, 33, 33, 34 }, new[] { 100, 34 })]
    public void Plan_UnitsAreNeverDivided_AndFillTransactionsExactlyWhenTheyCan(int[] unitRows, int[] expected)
    {
        var rows = new List<TableEntity>();
        for (var i = 0; i < unitRows.Length; i++)
            rows.AddRange(Unit("u" + i.ToString("D2", CultureInfo.InvariantCulture), unitRows[i] - 1));

        var transactions = LargeValueTable.PlanPartitionDelete(rows);

        Assert.Equal(expected, Sizes(transactions));
        AssertWholeUnits(rows, transactions);
    }

    [Fact]
    public void Plan_ContinuationRowsWithoutAPrimary_AreAUnitOfTheirOwn()
    {
        List<TableEntity> rows = [.. Singles("a", 99), Part("gone", 1), Part("gone", 2)];

        var transactions = LargeValueTable.PlanPartitionDelete(rows);

        Assert.Equal([99, 2], Sizes(transactions));
        Assert.Equal(["gone~0001", "gone~0002"], transactions[1].Select(r => r.RowKey));
    }

    [Fact]
    public void Plan_ContinuationRowsListedAwayFromTheirPrimary_StillGoWithIt()
    {
        // "a-000".."a-098" sort between "a" and "a~0001": the unit's rows are not adjacent in the listing.
        List<TableEntity> rows = [Primary("a"), .. Singles("a-", 99), Part("a", 1), Part("a", 2)];

        var transactions = LargeValueTable.PlanPartitionDelete(rows);

        Assert.Equal([100, 2], Sizes(transactions));
        Assert.Equal(["a", "a~0001", "a~0002"], transactions[0].Take(3).Select(r => r.RowKey));
        AssertWholeUnits(rows, transactions);
    }

    [Fact]
    public void Plan_ANullOrForeignSplitOfCell_IsNotAContinuationRow()
    {
        var plain = new TableEntity(Pk, "x");                              // no cell at all (the emulator)
        var projected = Primary("y");                                      // null cell (the service)
        var odd = new TableEntity(Pk, "z") { [SystemColumnNames.SplitOf] = 5 }; // not a row key
        List<TableEntity> rows = [plain, projected, odd];

        var transactions = LargeValueTable.PlanPartitionDelete(rows);

        Assert.Equal(["x", "y", "z"], Assert.Single(transactions).Select(r => r.RowKey));
    }

    [Fact]
    public void Plan_MoreRowsNamingOnePrimaryThanATransactionHolds_AreDivided_ThePrimaryLast()
    {
        // Not something this library writes (an entity has at most 99 continuation rows): the group
        // cannot go in one transaction, so its continuation rows go first and its primary last.
        List<TableEntity> rows = [Primary("a"), .. Unit("big", 150), Primary("z")];

        var transactions = LargeValueTable.PlanPartitionDelete(rows);

        Assert.Equal(rows.Count, transactions.Sum(t => t.Count));
        Assert.All(transactions, t => Assert.InRange(t.Count, 1, Max));
        var withPrimary = transactions.Select((t, i) => (t, i)).Single(x => x.t.Any(r => r.RowKey == "big")).i;
        var lastWithPart = transactions.Select((t, i) => (t, i)).Last(x => x.t.Any(r => UnitOf(r) == "big" && r.RowKey != "big")).i;
        Assert.True(withPrimary >= lastWithPart, "the primary is deleted no earlier than its last continuation row");
        Assert.Equal("big", transactions[withPrimary].Where(r => UnitOf(r) == "big").Last().RowKey);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Plan_AnyMixOfUnits_KeepsEveryUnitWhole(int seed)
    {
        var random = new Random(seed);
        var rows = new List<TableEntity>();
        for (var i = 0; i < 60; i++)
        {
            var rk = "u" + i.ToString("D3", CultureInfo.InvariantCulture);
            rows.AddRange(Unit(rk, random.Next(4) == 0 ? random.Next(0, 100) : random.Next(0, 3)));
        }

        var transactions = LargeValueTable.PlanPartitionDelete(rows);

        AssertWholeUnits(rows, transactions);
    }

    // ── TableStorage (mocked table) ────────────────────────────────────────

    private static void Lists(StorageHarness<SplitTextEntity> h, List<TableEntity> rows, List<IEnumerable<string>?>? selects = null) =>
        h.Table.QueryAsync<TableEntity>(
                Arg.Any<Expression<Func<TableEntity, bool>>>(), Arg.Any<int?>(),
                Arg.Do<IEnumerable<string>?>(s => selects?.Add(s)), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable([.. rows]));

    /// <summary>Records each submitted transaction; the transaction at <paramref name="failAt"/> (zero-based) fails.</summary>
    private static List<List<string>> CaptureTransactions<T>(StorageHarness<T> h, int? failAt = null) where T : class, IEntity, new()
    {
        var submitted = new List<List<string>>();
        var response = Substitute.For<Response<IReadOnlyList<Response>>>();
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var actions = call.Arg<IEnumerable<TableTransactionAction>>().ToList();
                Assert.All(actions, a => Assert.Equal(TableTransactionActionType.Delete, a.ActionType));
                submitted.Add(actions.Select(a => a.Entity.RowKey).ToList());
                if (submitted.Count - 1 == failAt)
                    throw new RequestFailedException(503, "the second transaction failed");
                return response;
            });
        return submitted;
    }

    /// <summary>The continuation rows among <paramref name="rows"/> whose primary is not among them.</summary>
    private static List<string> PartsWithoutPrimary(IEnumerable<TableEntity> rows)
    {
        var left = rows.ToList();
        var keys = left.Select(r => r.RowKey).ToHashSet(StringComparer.Ordinal);
        return left.Where(r => UnitOf(r) != r.RowKey && !keys.Contains(UnitOf(r))).Select(r => r.RowKey).ToList();
    }

    [Fact]
    public async Task TableStorage_SecondTransactionFails_LeavesNoContinuationRowWithoutItsPrimary()
    {
        // The 100th listed row is a primary; its continuation rows follow.
        List<TableEntity> rows = [.. Singles("a", 99), .. Unit("b", 2)];
        using var h = new StorageHarness<SplitTextEntity>();
        Lists(h, rows);
        var submitted = CaptureTransactions(h, failAt: 1);

        await Assert.ThrowsAsync<RequestFailedException>(() => h.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(2, submitted.Count);
        var deleted = submitted[0].ToHashSet(StringComparer.Ordinal);
        var left = rows.Where(r => !deleted.Contains(r.RowKey)).ToList();
        Assert.Empty(PartsWithoutPrimary(left));
        Assert.Equal(["b", "b~0001", "b~0002"], left.Select(r => r.RowKey));
        Assert.Equal(["b", "b~0001", "b~0002"], submitted[1]);
    }

    [Fact]
    public async Task TableStorage_DeletesEveryRow_InWholeUnits_AndCountsLogicalEntities()
    {
        // The listing returns every row's version; "gone~0001" is a continuation row without its primary.
        var leftover = Part("gone", 1);
        leftover.ETag = new ETag("W/\"listed\"");
        List<TableEntity> rows = [.. Singles("a", 99), .. Unit("b", 2), leftover, .. Singles("c", 120)];
        var selects = new List<IEnumerable<string>?>();
        using var h = new StorageHarness<SplitTextEntity>();
        Lists(h, rows, selects);
        h.SetupGet(Pk, "gone", null);
        var submitted = CaptureTransactions(h);

        // 99 + 1 + 120 entities; the continuation rows are deleted, not counted.
        Assert.Equal(220, await h.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(rows.Select(r => r.RowKey).Order(StringComparer.Ordinal), submitted.SelectMany(t => t).Order(StringComparer.Ordinal));
        Assert.All(submitted, t => Assert.InRange(t.Count, 1, Max));
        // The leftover goes last, in a transaction of its own, once no primary is found for it
        // (PartitionDeleteLiveEntityTests: it may belong to an entity written while the delete ran).
        Assert.Equal([99, 100, 23, 1], submitted.Select(t => t.Count));
        Assert.Equal(["b", "b~0001", "b~0002", "c000"], submitted[1].Take(4));
        Assert.Equal(["gone~0001"], submitted[3]);
        Assert.Equal(["PartitionKey", "RowKey", SystemColumnNames.SplitOf], selects[0]!);
    }

    [Fact]
    public async Task TableStorage_PlainType_StillDeletesInChunksOfAHundred_InTheOrderListed()
    {
        // A type that opts into nothing: the same listing projection and the same transactions as before.
        List<TableEntity> rows = [.. Enumerable.Range(0, 250).Select(i => new TableEntity(Pk, "r" + i.ToString("D3", CultureInfo.InvariantCulture)))];
        var selects = new List<IEnumerable<string>?>();
        using var h = new StorageHarness<PlainEntity>();
        h.Table.QueryAsync<TableEntity>(
                Arg.Any<Expression<Func<TableEntity, bool>>>(), Arg.Any<int?>(),
                Arg.Do<IEnumerable<string>?>(selects.Add), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable([.. rows]));
        var submitted = CaptureTransactions(h);

        Assert.Equal(250, await h.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal([100, 100, 50], submitted.Select(t => t.Count));
        Assert.Equal(rows.Select(r => r.RowKey), submitted.SelectMany(t => t));
        Assert.Equal(["PartitionKey", "RowKey"], selects[0]!);
    }

    [Fact]
    public async Task TableStorage_SecondTransactionFails_TheCacheForgetsWhatTheFirstDeleted()
    {
        List<TableEntity> rows = [.. Singles("a", 99), .. Unit("b", 2)];
        using var h = new StorageHarness<SplitTextEntity>(); // the default policy caches entities for an hour
        var stored = new TableEntity(Pk, "a000") { ETag = new ETag("W/\"v1\""), ["Title"] = "cached", ["Id"] = "p|a000" };
        h.SetupGet(Pk, "a000", stored);
        Assert.Equal("cached", (await h.Store.OneAsync("p|a000", Ct))!.Title);
        Lists(h, rows);
        var submitted = CaptureTransactions(h, failAt: 1);

        await Assert.ThrowsAsync<RequestFailedException>(() => h.Store.DeletePartitionAsync(Pk, Ct));

        // The first transaction committed: "a000" is gone, and a read must not answer from the cache.
        Assert.Contains("a000", submitted[0]);
        h.SetupGet(Pk, "a000", null);
        Assert.Null(await h.Store.OneAsync("p|a000", Ct));
    }

    // ── InMemoryStorage: the same transactions ─────────────────────────────

    private static async Task<InMemoryStorage<SplitTextEntity>> SeededAsync()
    {
        // No blob offload: a 600,000-character value is a primary and one continuation row.
        var store = new InMemoryStorage<SplitTextEntity>();
        await store.CreateBatchAsync(
            Enumerable.Range(0, 99).Select(i => new SplitTextEntity { Id = "p|a" + i.ToString("D3", CultureInfo.InvariantCulture), Body = "v" + i }).ToList(), Ct);
        await store.CreateAsync(new SplitTextEntity { Id = "p|b", Body = TwoRowText }, Ct);
        Assert.Equal("b", store.PhysicalRowKeys(Pk)[99]); // the 100th row is the primary; its part follows
        Assert.Equal("b~0001", store.PhysicalRowKeys(Pk)[100]);
        store.BatchTransactions = 0;
        return store;
    }

    [Fact]
    public async Task InMemory_DeletesInTheSameTransactionsAsTableStorage()
    {
        var store = await SeededAsync();
        store.SeedPhysicalRow(Part("gone", 1)); // a leftover continuation row with no primary

        Assert.Equal(100, await store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(0, store.PhysicalRowCount);
        // 99 rows, then the primary with its part, then the leftover (a continuation row without its
        // primary) on its own, as TableStorage deletes it.
        Assert.Equal(3, store.BatchTransactions);
    }

    [Fact]
    public async Task InMemory_CancelledBetweenTransactions_LeavesNoContinuationRowWithoutItsPrimary()
    {
        var store = await SeededAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        // The caller gives up once the first transaction has committed.
        store.AfterLargeCommit = () =>
        {
            store.AfterLargeCommit = null;
            cts.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeletePartitionAsync(Pk, cts.Token));

        Assert.Equal(["b", "b~0001"], store.PhysicalRowKeys(Pk));
        Assert.Equal(1, store.BatchTransactions);
        Assert.Equal(TwoRowText, (await store.OneAsync("p|b", Ct))!.Body, StringComparer.Ordinal);
        // The delete can simply be run again.
        Assert.Equal(1, await store.DeletePartitionAsync(Pk, Ct));
        Assert.Equal(0, store.PhysicalRowCount);
    }
}

/// <summary>
/// <see cref="PartitionDeleteUnitsTests"/> against a local Azurite: the 100th row of the partition is a
/// primary whose continuation row follows, and a pipeline policy fails the delete's second transaction.
/// </summary>
public sealed class AzuritePartitionDeleteUnitsTests : AzuriteCleanupTestBase
{
    private const string TableName = nameof(SplitTextEntity);

    private static readonly string TwoRowText = new('s', 600_000);

    [Fact]
    public async Task SecondTransactionFails_LeavesNoContinuationRowWithoutItsPrimary()
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        await writer.CreateBatchAsync(
            Enumerable.Range(0, 99).Select(i => new SplitTextEntity { Id = Id("a" + i.ToString("D3", CultureInfo.InvariantCulture)), Body = "v" + i }).ToList(), Ct);
        await writer.CreateAsync(new SplitTextEntity { Id = Id("b"), Body = TwoRowText }, Ct);
        var before = (await RawRowsAsync(TableName)).Select(r => r.RowKey).ToList();
        Assert.Equal(101, before.Count);
        Assert.Equal(["b", "b~0001"], before.Skip(99)); // the 100th row is the primary; its part follows

        var failing = new FailSecondBatch();
        var deleting = SplitOnlyStore<SplitTextEntity>(TableServiceWith(failing));
        await Assert.ThrowsAnyAsync<RequestFailedException>(() => deleting.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(2, failing.Batches);
        var left = await RawRowsAsync(TableName);
        Assert.Equal(["b", "b~0001"], left.Select(r => r.RowKey));
        Assert.Equal(TwoRowText, (await writer.OneAsync(Id("b"), Ct))!.Body, StringComparer.Ordinal);

        // The delete can simply be run again; only the logical entity is counted.
        Assert.Equal(1, await writer.DeletePartitionAsync(Pk, Ct));
        Assert.Empty(await RawRowsAsync(TableName));
    }

    /// <summary>Fails the second entity-group transaction (<c>POST .../$batch</c>) before it is sent.</summary>
    private sealed class FailSecondBatch : HttpPipelineSynchronousPolicy
    {
        private int batches;

        public int Batches => Volatile.Read(ref batches);

        public override void OnSendingRequest(HttpMessage message)
        {
            if (message.Request.Method == RequestMethod.Post
                && message.Request.Uri.Path.EndsWith("$batch", StringComparison.Ordinal)
                && Interlocked.Increment(ref batches) == 2)
                throw new RequestFailedException(503, "the second transaction was not sent (injected)");
        }
    }
}
