using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The outcome verbs: expected concurrent situations (already exists, gone, someone got there
/// first) come back as RETURN VALUES — exceptions stay reserved for programmer errors and
/// infrastructure failures. <c>GetOrCreateAsync</c>/<c>MutateOrCreateAsync</c> converge and return
/// the entity; <c>TryMutateAsync</c>/<c>TryTransitionAsync</c> report a <see cref="MutationStatus"/>.
/// </summary>
public class OutcomeVerbsTests
{
    // ── DuplicateKeyException (the primitive the verbs absorb) ──────────────

    [Fact]
    public async Task CreateAsync_OnAzure409_ThrowsDuplicateKey_WithProviderInner()
    {
        using var h = new StorageHarness<TestEntity>();
        h.Table.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(409, "EntityAlreadyExists"));

        var ex = await Assert.ThrowsAsync<DuplicateKeyException>(
            () => h.Store.CreateAsync(new TestEntity { Id = "p|r" }));

        Assert.Equal("p|r", ex.Id);
        Assert.Equal(409, Assert.IsType<RequestFailedException>(ex.InnerException).Status);
    }

    [Fact]
    public async Task CreateBatchAsync_OnAzure409_ThrowsDuplicateKey_WithPartitionFallbackId()
    {
        using var h = new StorageHarness<TestEntity>();
        // Without a parsable FailedTransactionActionIndex the id falls back to "{partition}|?" —
        // still enough for the caller to know WHERE the duplicate landed.
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw new TableTransactionFailedException(
                new RequestFailedException(409, "The specified entity already exists.")));

        var ex = await Assert.ThrowsAsync<DuplicateKeyException>(() => h.Store.CreateBatchAsync(
        [
            new TestEntity { Id = "p|r1" },
            new TestEntity { Id = "p|r2" },
        ]));

        Assert.Equal("p|?", ex.Id);
        Assert.IsType<TableTransactionFailedException>(ex.InnerException);
    }

    [Fact]
    public async Task CreateBatchAsync_OnWithinBatchDuplicate400_ThrowsDuplicateKey_LikeInMemory()
    {
        using var h = new StorageHarness<TestEntity>();
        // A duplicate INSIDE one transaction surfaces as 400 InvalidDuplicateRow, not 409 —
        // it must translate identically, or tests written against InMemory lie about production.
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw new TableTransactionFailedException(
                new RequestFailedException(400, "InvalidDuplicateRow", "InvalidDuplicateRow", null)));

        var ex = await Assert.ThrowsAsync<DuplicateKeyException>(() => h.Store.CreateBatchAsync(
        [
            new TestEntity { Id = "p|same" },
            new TestEntity { Id = "p|same" },
        ]));

        Assert.IsType<TableTransactionFailedException>(ex.InnerException);
    }

    // ── GetOrCreateAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrCreate_ReturnsExisting_WithoutInvokingFactory()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "existing" });
        var factoryCalls = 0;

        var result = await store.GetOrCreateAsync("p|r", () => { factoryCalls++; return new TestEntity(); });

        Assert.Equal("existing", result.Name);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task GetOrCreate_CreatesWhenMissing_AndAssignsTheId()
    {
        var store = new InMemoryStorage<TestEntity>();

        var result = await store.GetOrCreateAsync("p|r", () => new TestEntity { Name = "fresh" });

        Assert.Equal("p|r", result.Id);
        Assert.NotNull(result.ETag);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task GetOrCreate_LosingTheCreateRace_ReturnsTheWinnersRow()
    {
        var store = new InMemoryStorage<TestEntity>();

        // The competitor creates the row between our read (miss) and our create.
        var result = await store.GetOrCreateAsync("p|r", () =>
        {
            store.CreateAsync(new TestEntity { Id = "p|r", Name = "winner" }).GetAwaiter().GetResult();
            return new TestEntity { Name = "loser" };
        });

        Assert.Equal("winner", result.Name);
        Assert.Equal(1, store.Count);
    }

    // ── MutateOrCreateAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task MutateOrCreate_AppliesTheDeltaUniformly_OnInsertAndOnUpdate()
    {
        var store = new InMemoryStorage<TestEntity>();

        // First call: create supplies the base state, mutate applies the delta → 1.
        var first = await store.MutateOrCreateAsync("p|r",
            create: () => new TestEntity { Value = 0 },
            mutate: e => e.Value++);
        // Second call: same delta against the existing row → 2.
        var second = await store.MutateOrCreateAsync("p|r",
            create: () => new TestEntity { Value = 0 },
            mutate: e => e.Value++);

        Assert.Equal(1, first.Value);
        Assert.Equal(2, second.Value);
    }

    [Fact]
    public async Task MutateOrCreate_LosingTheCreateRace_MutatesTheWinnersRow()
    {
        var store = new InMemoryStorage<TestEntity>();
        var competed = false;

        var result = await store.MutateOrCreateAsync("p|r",
            create: () =>
            {
                if (!competed)
                {
                    competed = true;
                    store.CreateAsync(new TestEntity { Id = "p|r", Value = 100 }).GetAwaiter().GetResult();
                }
                return new TestEntity { Value = 0 };
            },
            mutate: e => e.Value++);

        // The retry read the winner's 100 and applied the delta to THAT.
        Assert.Equal(101, result.Value);
    }

    [Fact]
    public async Task MutateOrCreate_CreateRaceLostOnTheFinalAttempt_SurfacesAsConcurrencyConflict()
    {
        // A pathological interleaving: every read misses, yet every create hits an existing key
        // (create-then-delete competitors). Exhausted attempts must surface as the documented
        // ConcurrencyConflictException — never as a leaked DuplicateKeyException.
        var storage = Substitute.For<IStorage<TestEntity>>();
        storage.OneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((TestEntity?)null);
        storage.CreateAsync(Arg.Any<TestEntity>(), Arg.Any<CancellationToken>())
            .Returns<TestEntity>(_ => throw new DuplicateKeyException("TestEntity", "p|r", null));

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => storage.MutateOrCreateAsync("p|r",
            create: () => new TestEntity { Value = 0 },
            mutate: e => e.Value++,
            maxAttempts: 2));

        Assert.IsType<DuplicateKeyException>(ex.InnerException);
    }

    [Fact]
    public async Task MutateAsync_CanceledDuringBackoff_PropagatesCancellation()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Value = 0 });
        using var cts = new CancellationTokenSource();

        // The competitor wins the first race AND cancels — the backoff delay must observe the
        // token and surface cancellation instead of retrying into a canceled operation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MutateAsync("p|r", e =>
        {
            store.UpdateAsync(new TestEntity { Id = "p|r", Value = 999 }, ConcurrencyMode.LastWriterWins)
                 .GetAwaiter().GetResult();
            cts.Cancel();
            e.Value++;
        }, maxAttempts: 3, ct: cts.Token));
    }

    // ── TryMutateAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task TryMutate_Updated_CarriesThePersistedEntity()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Value = 1 });

        var result = await store.TryMutateAsync("p|r", e => e.Value = 2);

        Assert.True(result.Succeeded);
        Assert.Equal(MutationStatus.Updated, result.Status);
        Assert.Equal(2, result.Entity!.Value);
        Assert.NotNull(result.Entity.ETag);
    }

    [Fact]
    public async Task TryMutate_MissingRow_ReturnsNotFound_InsteadOfThrowing()
    {
        var store = new InMemoryStorage<TestEntity>();

        var result = await store.TryMutateAsync("nope", _ => { });

        Assert.False(result.Succeeded);
        Assert.Equal(MutationStatus.NotFound, result.Status);
        Assert.Null(result.Entity);
    }

    [Fact]
    public async Task TryMutate_ExhaustedRaces_ReturnsConflicted_InsteadOfThrowing()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Value = 0 });

        var result = await store.TryMutateAsync("p|r", e =>
        {
            // A competitor wins EVERY race.
            store.UpdateAsync(new TestEntity { Id = "p|r", Value = 999 }, ConcurrencyMode.LastWriterWins)
                 .GetAwaiter().GetResult();
            e.Value++;
        }, maxAttempts: 2);

        Assert.Equal(MutationStatus.Conflicted, result.Status);
        Assert.Null(result.Entity);
    }

    // ── TryTransitionAsync (exactly-once transitions as results) ────────────

    [Fact]
    public async Task TryTransition_WhenPreconditionHolds_AppliesExactlyOnce()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "open" });

        var result = await store.TryTransitionAsync("p|r",
            when: e => e.Name == "open",
            apply: e => e.Name = "resolved");

        Assert.Equal(MutationStatus.Updated, result.Status);
        Assert.Equal("resolved", (await store.OneAsync("p|r"))!.Name);
    }

    [Fact]
    public async Task TryTransition_AlreadyTransitioned_ReturnsPreconditionFailed_WithTheFreshRow()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "resolved", Value = 7 });

        var result = await store.TryTransitionAsync("p|r",
            when: e => e.Name == "open",
            apply: e => e.Name = "resolved");

        Assert.Equal(MutationStatus.PreconditionFailed, result.Status);
        Assert.NotNull(result.Entity);            // the caller can inspect who won / current state
        Assert.Equal(7, result.Entity!.Value);
    }

    [Fact]
    public async Task TryTransition_LosingTheRaceToTheSameTransition_ReportsPreconditionFailed_NotConflict()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "open" });
        var competed = false;

        // The competitor performs the SAME transition between our read and our write. Our strict
        // write conflicts; the retry re-reads, sees the precondition no longer holds, and reports
        // the EXPECTED outcome — no exception anywhere.
        var result = await store.TryTransitionAsync("p|r",
            when: e => e.Name == "open",
            apply: e =>
            {
                if (!competed)
                {
                    competed = true;
                    store.UpdateAsync(new TestEntity { Id = "p|r", Name = "resolved" }, ConcurrencyMode.LastWriterWins)
                         .GetAwaiter().GetResult();
                }
                e.Name = "resolved";
            });

        Assert.Equal(MutationStatus.PreconditionFailed, result.Status);
        Assert.Equal("resolved", result.Entity!.Name);
    }

    [Fact]
    public async Task TryTransition_MissingRow_ReturnsNotFound()
    {
        var store = new InMemoryStorage<TestEntity>();

        var result = await store.TryTransitionAsync("nope", _ => true, _ => { });

        Assert.Equal(MutationStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task TryTransition_HotRow_WithPreconditionStillTrue_ReturnsConflicted()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "open", Value = 0 });

        // A competitor bumps an UNRELATED field every attempt (precondition stays true), so every
        // strict write loses without the transition ever becoming moot.
        var result = await store.TryTransitionAsync("p|r",
            when: e => e.Name == "open",
            apply: e =>
            {
                store.MutateAsync("p|r", x => x.Value++).GetAwaiter().GetResult();
                e.Name = "resolved";
            },
            maxAttempts: 2);

        Assert.Equal(MutationStatus.Conflicted, result.Status);
        Assert.Equal("open", (await store.OneAsync("p|r"))!.Name);   // transition never half-applied
    }

    [Fact]
    public async Task TryTransition_AgainstTableStorage_LostRace_ReReadsThroughEvictedCache_AndReportsPreconditionFailed()
    {
        using var h = new StorageHarness<TestEntity>();
        var reads = 0;
        h.Table.GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => reads++ == 0
                ? Mocks.Found(Mocks.Row("p", "r", name: "open"))
                : Mocks.Found(Mocks.Row("p", "r", name: "resolved")));
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(412, "Precondition Failed"));

        var result = await h.Store.TryTransitionAsync("p|r",
            when: e => e.Name == "open",
            apply: e => e.Name = "resolved");

        // Attempt 1 read "open" (cached), lost the strict write — the conflict evicted the cache,
        // so attempt 2's read went back to storage, saw the winner's "resolved", and reported the
        // EXPECTED outcome instead of a conflict.
        Assert.Equal(MutationStatus.PreconditionFailed, result.Status);
        Assert.Equal("resolved", result.Entity!.Name);
        Assert.Equal(2, reads);
    }

    // ── The verbs work against TableStorage too (cache interplay) ───────────

    [Fact]
    public async Task GetOrCreate_AgainstTableStorage_AbsorbsTheProvider409()
    {
        using var h = new StorageHarness<TestEntity>();
        var reads = 0;
        h.Table.GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => reads++ == 0 ? Mocks.NotFound() : Mocks.Found(Mocks.Row("p", "r", name: "winner")));
        h.Table.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(409, "EntityAlreadyExists"));

        var result = await h.Store.GetOrCreateAsync("p|r", () => new TestEntity { Name = "loser" });

        Assert.Equal("winner", result.Name);
    }
}
