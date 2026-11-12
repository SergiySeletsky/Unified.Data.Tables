using Azure;
using Azure.Data.Tables;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// For a type with large values an upsert, a last-writer-wins update, a delete and a batch upsert read
/// the stored row and then write on condition of the version they read. A write that loses that
/// condition to a concurrent writer reads again and retries: five attempts in all, with a short,
/// growing, jittered pause between them, then <see cref="ConcurrencyConflictException"/>. Each lost
/// attempt is caused by one other writer's commit, so up to four callers upserting the same row at
/// once all succeed whatever the timing; the pauses spread a larger crowd out.
/// </summary>
public class WriteRetryBackoffTests
{
    private static readonly string TwoRowText = new('a', 600_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── helpers ────────────────────────────────────────────────────────────

    private static TableEntity PriorPrimary(string rk, int parts, string etag)
    {
        var row = new TableEntity("p", rk) { ETag = new ETag(etag) };
        if (parts > 0) row[SystemColumnNames.SplitParts] = parts;
        return row;
    }

    private static TableTransactionFailedException TxFailed(int status, int index)
    {
        var code = status switch { 409 => "EntityAlreadyExists", 404 => "ResourceNotFound", _ => "UpdateConditionNotSatisfied" };
        var inner = new RequestFailedException(status, "failed", code, null);
        inner.Data["FailedEntity"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new TableTransactionFailedException(inner);
    }

    private static RequestFailedException ConditionFailed() => new(412, "failed", "UpdateConditionNotSatisfied", null);

    /// <summary>Replaces the store's wait between attempts with one that only records what it was asked to wait.</summary>
    private static List<TimeSpan> RecordWaits(StorageHarness<SplitTextEntity> h)
    {
        var waits = new List<TimeSpan>();
        h.Store.LargeValueRetryDelay = (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        };
        return waits;
    }

    /// <summary>The four pauses between five attempts: about 10 ms doubling, each between half of it and all of it.</summary>
    private static void AssertBackoff(List<TimeSpan> waits)
    {
        Assert.Equal(4, waits.Count);
        for (var i = 0; i < waits.Count; i++)
        {
            var ceiling = 10 * Math.Pow(2, i);
            Assert.InRange(waits[i].TotalMilliseconds, ceiling / 2, ceiling);
        }
    }

    // ── a writer that always loses ─────────────────────────────────────────

    [Fact]
    public async Task Upsert_LosingEveryRace_ThrowsConcurrencyConflict_AfterFiveAttempts_WithAPauseBeforeEachRetry()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"prior\""));
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(ConditionFailed());
        var waits = RecordWaits(h);

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct));

        Assert.Equal("p|r", ex.Id);
        Assert.IsType<RequestFailedException>(ex.InnerException);
        await h.Table.Received(5).UpdateEntityAsync(Arg.Any<TableEntity>(), new ETag("W/\"prior\""), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
        await h.Table.Received(5).GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        AssertBackoff(waits);
    }

    [Fact]
    public async Task Upsert_NeedingATransaction_LosingEveryRace_ThrowsAfterFiveTransactions()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"prior\""));
        var failure = TxFailed(412, index: 0);
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw failure);
        var waits = RecordWaits(h);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = TwoRowText }, Ct));

        await h.Table.ReceivedWithAnyArgs(5).SubmitTransactionAsync(default!, default);
        AssertBackoff(waits);
    }

    [Fact]
    public async Task LastWriterWinsUpdate_LosingEveryRace_ThrowsAfterFiveAttempts()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"prior\""));
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(ConditionFailed());
        var waits = RecordWaits(h);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Store.UpdateAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, ConcurrencyMode.LastWriterWins, Ct));

        await h.Table.ReceivedWithAnyArgs(5).UpdateEntityAsync<TableEntity>(default!, default, default, default);
        AssertBackoff(waits);
    }

    [Fact]
    public async Task StrictUpdate_LosingItsCondition_IsNeverRetried()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"prior\""));
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(ConditionFailed());
        var waits = RecordWaits(h);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Store.UpdateAsync(new SplitTextEntity { Id = "p|r", Body = "small", ETag = "W/\"prior\"" }, ConcurrencyMode.Strict, Ct));

        // The caller named the version: a different one is the caller's conflict, at once.
        await h.Table.ReceivedWithAnyArgs(1).UpdateEntityAsync<TableEntity>(default!, default, default, default);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Delete_LosingEveryRace_ThrowsConcurrencyConflict_AfterFiveAttempts_WithAPauseBeforeEachRetry()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"prior\""));
        h.Table.DeleteEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(ConditionFailed());
        var waits = RecordWaits(h);

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => h.Store.DeleteAsync("p|r", Ct));

        Assert.Equal("p|r", ex.Id);
        await h.Table.Received(5).DeleteEntityAsync("p", "r", new ETag("W/\"prior\""), Arg.Any<CancellationToken>());
        AssertBackoff(waits);
    }

    [Fact]
    public async Task UpsertBatch_LosingEveryRace_ThrowsConcurrencyConflict_AfterFiveTransactions_WithAPauseBeforeEachRetry()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet(PriorPrimary("r1", 0, "W/\"v1\""));
        var failure = TxFailed(412, index: 1);
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw failure);
        var waits = RecordWaits(h);

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => h.Store.UpsertBatchAsync(
        [
            new SplitTextEntity { Id = "p|r1", Body = "a" },
            new SplitTextEntity { Id = "p|r2", Body = "b" },
        ], Ct));

        Assert.Equal("p|r2", ex.Id);
        await h.Table.ReceivedWithAnyArgs(5).SubmitTransactionAsync(default!, default);
        AssertBackoff(waits);
    }

    // ── a writer that loses as often as four concurrent callers can make it ──

    [Fact]
    public async Task Upsert_TheUnluckiestOfFourCallers_LosesThreeRaces_AndSucceedsOnItsFourthAttempt()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"prior\""));
        // Each lost race is another caller's commit, and each of the other three commits once.
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw ConditionFailed(),
                _ => throw ConditionFailed(),
                _ => throw ConditionFailed(),
                _ => Mocks.EtagResponse("W/\"fourth\""));
        var waits = RecordWaits(h);

        var written = await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct);

        Assert.Equal("W/\"fourth\"", written.ETag);
        Assert.Equal(3, waits.Count);
    }

    [Fact]
    public async Task UpsertBatch_ATransactionThatLosesFourRaces_SucceedsOnItsFifthAttempt()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet(PriorPrimary("r1", 0, "W/\"v1\""));
        var failure = TxFailed(412, index: 0);
        var ok = Substitute.For<Response<IReadOnlyList<Response>>>();
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw failure, _ => throw failure, _ => throw failure, _ => throw failure, _ => ok);
        var waits = RecordWaits(h);

        var n = await h.Store.UpsertBatchAsync([new SplitTextEntity { Id = "p|r1", Body = "a" }], Ct);

        Assert.Equal(1, n);
        await h.Table.ReceivedWithAnyArgs(5).SubmitTransactionAsync(default!, default);
        AssertBackoff(waits);
    }

    // ── the pause ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AWriteThatLosesNoRace_NeverWaits()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"prior\""));
        h.SetupUpdate();
        h.SetupDelete();
        h.SetupTransaction();
        var waits = RecordWaits(h);

        await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct);
        await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = TwoRowText }, Ct);
        await h.Store.UpsertBatchAsync([new SplitTextEntity { Id = "p|r", Body = "small" }], Ct);
        await h.Store.DeleteAsync("p|r", Ct);

        Assert.Empty(waits);
    }

    [Fact]
    public async Task TakingAnOwnLeftoverBack_IsNotALostRace_AndDoesNotWait()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var failure = TxFailed(409, index: 1);
        var ok = Substitute.For<Response<IReadOnlyList<Response>>>();
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw failure, _ => ok);
        h.SetupQueryByFilter(new TableEntity("p", "r~0001") { ETag = new ETag("W/\"e1\""), [SystemColumnNames.SplitOf] = "r" });
        var waits = RecordWaits(h);

        await h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = TwoRowText }, Ct);

        await h.Table.ReceivedWithAnyArgs(2).SubmitTransactionAsync(default!, default);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task ThePause_HonoursTheCallersToken()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"prior\""));
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(ConditionFailed());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        // The real wait, cancelled while it is pending.
        h.Store.LargeValueRetryDelay = async (delay, token) =>
        {
            Assert.Equal(cts.Token, token);
            await cts.CancelAsync();
            await Task.Delay(TimeSpan.FromMinutes(1), token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, cts.Token));

        // Cancelled between the first attempt and the second: nothing more was sent.
        await h.Table.ReceivedWithAnyArgs(1).UpdateEntityAsync<TableEntity>(default!, default, default, default);
    }

    [Fact]
    public async Task TheDefaultPause_IsARealDelay_ThatACancelledTokenEnds()
    {
        var rules = new LargeValueTable("T", options: null, blobs: null);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rules.WaitBeforeRetryAsync(2, cts.Token));
        await rules.WaitBeforeRetryAsync(2, Ct); // about 10 ms
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rules.WaitBeforeRetryAsync(1, Ct));
    }

    [Fact]
    public void Backoff_IsAbout10msDoubling_CappedAt250ms_EachBetweenHalfOfItAndAllOfIt()
    {
        Assert.Equal(5, LargeValueTable.MaxWriteAttempts);
        var longest = new Dictionary<int, double>();
        for (var attempt = 2; attempt <= 40; attempt++)
        {
            var ceiling = Math.Min(250, 10 * Math.Pow(2, attempt - 2));
            var samples = Enumerable.Range(0, 200).Select(_ => LargeValueTable.RetryBackoff(attempt).TotalMilliseconds).ToList();
            Assert.All(samples, ms => Assert.InRange(ms, Math.Floor(ceiling / 2), ceiling));
            // Jittered: not one fixed length.
            Assert.True(samples.Distinct().Count() > 1, $"attempt {attempt}: every pause was {samples[0]} ms");
            longest[attempt] = samples.Max();
        }
        // A write that uses every attempt has waited 150 ms at most (10 + 20 + 40 + 80).
        Assert.InRange(Enumerable.Range(2, LargeValueTable.MaxWriteAttempts - 1).Sum(a => longest[a]), 75, 150);
        Assert.Throws<ArgumentOutOfRangeException>(() => LargeValueTable.RetryBackoff(1));
    }

    // ── InMemoryStorage: the same number, the same pauses ──────────────────

    private static readonly string[] Bodies = ["one", "two", "three", "four", "five", "six"];

    /// <summary>
    /// Lets another writer commit an upsert of the same row between each attempt's read and its commit, the
    /// first <paramref name="times"/> times. Returns the counters the test asserts on.
    /// </summary>
    private static (Func<int> Attempts, List<TimeSpan> Waits) Contend(InMemoryStorage<SplitTextEntity> store, int times, string id = "p|r")
    {
        var attempts = 0;
        var competing = false;
        store.BeforeLargeCommit = () =>
        {
            if (competing)
                return;
            if (++attempts > times)
                return;
            competing = true;
            try
            {
                // The in-memory store completes synchronously.
                store.UpsertAsync(new SplitTextEntity { Id = id, Body = "theirs " + attempts }, CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                competing = false;
            }
        };
        var waits = new List<TimeSpan>();
        store.LargeValueRules.RetryDelay = (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        };
        return (() => attempts, waits);
    }

    [Fact]
    public async Task InMemory_Upsert_LosingEveryRace_ThrowsConcurrencyConflict_AfterFiveAttempts_WithAPauseBeforeEachRetry()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        var (attempts, waits) = Contend(store, times: int.MaxValue);

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "mine" }, Ct));

        Assert.Equal("p|r", ex.Id);
        Assert.Equal(5, attempts());
        AssertBackoff(waits);
        Assert.Equal("theirs 5", (await store.OneAsync("p|r", Ct))!.Body);
    }

    [Fact]
    public async Task InMemory_Upsert_TheUnluckiestOfFourCallers_LosesThreeRaces_AndSucceeds()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        var (attempts, waits) = Contend(store, times: 3);

        await store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "mine" }, Ct);

        Assert.Equal(4, attempts());
        Assert.Equal(3, waits.Count);
        Assert.Equal("mine", (await store.OneAsync("p|r", Ct))!.Body);
    }

    [Fact]
    public async Task InMemory_LastWriterWinsUpdate_LosingEveryRace_ThrowsAfterFiveAttempts()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = "first" }, Ct);
        var (attempts, waits) = Contend(store, times: int.MaxValue);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpdateAsync(new SplitTextEntity { Id = "p|r", Body = "mine" }, ConcurrencyMode.LastWriterWins, Ct));

        Assert.Equal(5, attempts());
        AssertBackoff(waits);
    }

    [Fact]
    public async Task InMemory_UpsertBatch_LosingEveryRace_ThrowsConcurrencyConflict_AfterFiveTransactions()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = "p|r2", Body = "first" }, Ct);
        var (attempts, waits) = Contend(store, times: int.MaxValue, id: "p|r2");
        store.BatchTransactions = 0;

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpsertBatchAsync(
        [
            new SplitTextEntity { Id = "p|r1", Body = "a" },
            new SplitTextEntity { Id = "p|r2", Body = "b" },
        ], Ct));

        Assert.Equal("p|r2", ex.Id);
        Assert.Equal(5, attempts());
        Assert.Equal(5, store.BatchTransactions);
        AssertBackoff(waits);
        // Atomic: nothing of the losing transaction was stored.
        Assert.Null(await store.OneAsync("p|r1", Ct));
    }

    [Fact]
    public async Task InMemory_AWriteThatLosesNoRace_NeverWaits()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        var (_, waits) = Contend(store, times: 0);

        foreach (var body in Bodies)
            await store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = body }, Ct);
        await store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = TwoRowText }, Ct);
        await store.UpsertBatchAsync([new SplitTextEntity { Id = "p|r", Body = "small" }], Ct);
        await store.DeleteAsync("p|r", Ct);

        Assert.Empty(waits);
    }
}
