using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The 0.4.0 concurrency toolkit: ETag-conditional partial updates (<see cref="UpdateBuilder{T}.WithETag"/>),
/// the provider-agnostic <see cref="ConcurrencyConflictException"/>, conflict-driven cache eviction,
/// and the <see cref="StorageExtensions.MutateAsync{T}"/> compare-and-swap helper.
/// </summary>
public class ConcurrencyTests
{
    // ── Conditional merge (WithETag) — TableStorage ─────────────────────────

    [Fact]
    public async Task BuilderUpdate_WithETag_SendsConditionalMerge()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate("W/\"new\"");

        await h.Store.UpdateAsync("p|r", b => b.WithETag("W/\"read\"").SetProperty(x => x.Name, "x"));

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), new ETag("W/\"read\""), TableUpdateMode.Merge, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuilderUpdate_WithoutETag_MergesUnconditionally()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate();

        await h.Store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "x"));

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), ETag.All, TableUpdateMode.Merge, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuilderUpdate_WithStaleETag_ThrowsConflict_AndEvictsCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet("p", "r", Mocks.Row("p", "r"));
        await h.Store.OneAsync("p|r");                                    // warm the entity cache
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(412, "Precondition Failed"));

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Store.UpdateAsync("p|r", b => b.WithETag("W/\"stale\"").SetProperty(x => x.Name, "x")));
        await h.Store.OneAsync("p|r");                                    // must re-read — cache evicted

        Assert.Equal("p|r", ex.Id);
        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>(
            "p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuilderUpdate_ReturnsTheNewETag()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate("W/\"after-merge\"");

        var etag = await h.Store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "x"));

        Assert.Equal(new ETag("W/\"after-merge\"").ToString(), etag);
    }

    [Fact]
    public void WithETag_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => new UpdateBuilder<TestEntity>().WithETag(""));
    }

    [Fact]
    public void WithETag_Wildcard_Throws()
    {
        // "*" is ETag.All — it matches any row version, silently turning the "conditional"
        // merge unconditional. Misuse must fail loudly, not degrade.
        Assert.Throws<ArgumentException>(() => new UpdateBuilder<TestEntity>().WithETag("*"));
    }

    [Fact]
    public async Task BuilderUpdate_Conflict_InvalidatesPartitionQueryCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByPartition(Mocks.Row("p", "r"));
        await h.Store.QueryAsync("p");                                    // warm the partition query cache
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(412, "Precondition Failed"));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Store.UpdateAsync("p|r", b => b.WithETag("W/\"stale\"").SetProperty(x => x.Name, "x")));
        await h.Store.QueryAsync("p");                                    // must MISS the query cache

        // The 412 proves a foreign writer touched the partition — cached query results for it
        // are stale too, so the second query must go back to storage.
        h.Table.Received(2).QueryAsync<TableEntity>(
            Arg.Any<System.Linq.Expressions.Expression<Func<TableEntity, bool>>>(),
            Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── Conflict-driven cache eviction on whole-entity updates ──────────────

    [Fact]
    public async Task StrictConflict_EvictsCachedEntity_SoNextReadIsFresh()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet("p", "r", Mocks.Row("p", "r"));
        var entity = await h.Store.OneAsync("p|r");                       // warm cache + carry ETag
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(412, "Precondition Failed"));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Store.UpdateAsync(entity!, ConcurrencyMode.Strict));
        await h.Store.OneAsync("p|r");                                    // must MISS the cache

        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>(
            "p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StrictConflict_InvalidatesPartitionQueryCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet("p", "r", Mocks.Row("p", "r"));
        h.SetupQueryByPartition(Mocks.Row("p", "r"));
        var entity = await h.Store.OneAsync("p|r");                       // carry the ETag
        await h.Store.QueryAsync("p");                                    // warm the partition query cache
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(412, "Precondition Failed"));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Store.UpdateAsync(entity!, ConcurrencyMode.Strict));
        await h.Store.QueryAsync("p");                                    // must MISS the query cache

        h.Table.Received(2).QueryAsync<TableEntity>(
            Arg.Any<System.Linq.Expressions.Expression<Func<TableEntity, bool>>>(),
            Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── WithETag — InMemory parity ──────────────────────────────────────────

    [Fact]
    public async Task InMemory_BuilderUpdate_WithFreshETag_Applies_AndReturnsNewETag()
    {
        var store = new InMemoryStorage<TestEntity>();
        var created = await store.CreateAsync(new TestEntity { Id = "p|r", Name = "v1", Value = 5 });

        var newETag = await store.UpdateAsync("p|r", b => b.WithETag(created.ETag!).SetProperty(x => x.Name, "v2"));
        var loaded = await store.OneAsync("p|r");

        Assert.Equal("v2", loaded!.Name);
        Assert.Equal(5, loaded.Value);                 // merge preserved the untouched column
        Assert.NotEqual(created.ETag, newETag);
    }

    [Fact]
    public async Task InMemory_BuilderUpdate_WithStaleETag_ThrowsConflict()
    {
        var store = new InMemoryStorage<TestEntity>();
        var created = await store.CreateAsync(new TestEntity { Id = "p|r", Name = "v1" });
        var staleETag = created.ETag!;
        await store.UpdateAsync(new TestEntity { Id = "p|r", Name = "v2" }, ConcurrencyMode.LastWriterWins); // bump the version

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpdateAsync("p|r", b => b.WithETag(staleETag).SetProperty(x => x.Name, "v3")));

        Assert.Equal("v2", (await store.OneAsync("p|r"))!.Name);          // nothing applied
    }

    // ── MutateAsync (compare-and-swap) ──────────────────────────────────────

    [Fact]
    public async Task MutateAsync_AppliesMutation_AndReturnsPersistedEntity()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Value = 41 });

        var result = await store.MutateAsync("p|r", e => e.Value++);

        Assert.Equal(42, result.Value);
        Assert.Equal(42, (await store.OneAsync("p|r"))!.Value);
    }

    [Fact]
    public async Task MutateAsync_LosingARace_ReReadsAndReapplies_SoNoIncrementIsLost()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Value = 10 });

        // A competitor commits between our read and our write on the FIRST attempt only.
        var competed = false;
        var result = await store.MutateAsync("p|r", e =>
        {
            if (!competed)
            {
                competed = true;
                store.UpdateAsync(new TestEntity { Id = "p|r", Value = 100 }, ConcurrencyMode.LastWriterWins)
                     .GetAwaiter().GetResult();
            }
            e.Value += 1;
        });

        // The retry re-read the competitor's 100 and applied +1 to THAT — not to our stale 10.
        Assert.Equal(101, result.Value);
    }

    [Fact]
    public async Task MutateAsync_ExhaustedAttempts_PropagatesConflict()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Value = 0 });

        // A competitor wins EVERY race.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.MutateAsync("p|r", e =>
        {
            store.UpdateAsync(new TestEntity { Id = "p|r", Value = 999 }, ConcurrencyMode.LastWriterWins)
                 .GetAwaiter().GetResult();
            e.Value += 1;
        }, maxAttempts: 2));
    }

    [Fact]
    public async Task MutateAsync_BacksOffBetweenAttempts()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Value = 0 });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.MutateAsync("p|r", e =>
        {
            store.UpdateAsync(new TestEntity { Id = "p|r", Value = 999 }, ConcurrencyMode.LastWriterWins)
                 .GetAwaiter().GetResult();
            e.Value += 1;
        }, maxAttempts: 3));
        stopwatch.Stop();

        // Immediate retries re-race the same hot writer. Two lost non-final attempts must back
        // off ≥ 20ms and ≥ 40ms (linear base + jitter); assert only the deterministic lower
        // bound — no upper bound, so the test cannot flake under load.
        Assert.True(stopwatch.ElapsedMilliseconds >= 55,
            $"expected ≥55ms of accumulated backoff, got {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task MutateAsync_MissingEntity_ThrowsInvalidOperation()
    {
        var store = new InMemoryStorage<TestEntity>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MutateAsync("nope", _ => { }));
    }

    [Fact]
    public async Task MutateAsync_AgainstTableStorage_RetriesThroughTheCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet("p", "r", Mocks.Row("p", "r", value: 10));
        var updateCalls = 0;
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns(_ => updateCalls++ == 0
                ? throw new RequestFailedException(412, "Precondition Failed")
                : Mocks.EtagResponse("W/\"final\""));

        var result = await h.Store.MutateAsync("p|r", e => e.Value += 1);

        // Attempt 1: cached-or-fresh read + strict update -> conflict evicts the cache entry.
        // Attempt 2: the read MUST go back to storage (2 SDK gets), then the update lands.
        Assert.Equal(11, result.Value);
        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>(
            "p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await h.Table.Received(2).UpdateEntityAsync(
            Arg.Any<TableEntity>(), Arg.Any<ETag>(), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }
}
