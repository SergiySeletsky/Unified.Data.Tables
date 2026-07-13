using System.Linq.Expressions;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Regression tests for the 0.5.2 correctness patch slate. Each test names the issue it guards
/// (B1/B2/G1/G3/G5/G6) so a future breakage points straight at the fix it undid.
/// </summary>
public class Fixes052Tests
{
    // ── B1: non-UTC DateTime write path ─────────────────────────────────────

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task B1_NonUtcDateTime_WritesWithoutCrash_AndPreservesInstant(DateTimeKind kind)
    {
        var store = new InMemoryStorage<EntityWithDateTime>();
        var input = DateTime.SpecifyKind(new DateTime(2026, 7, 11, 15, 0, 0), kind);

        // Pre-0.5.2 a Local-kind DateTime threw ArgumentException on the write path on any non-UTC
        // host: new DateTimeOffset(dt, TimeSpan.Zero) requires the offset to match the value's Kind.
        // 0.5.2 normalizes the Kind to UTC first, so the write always succeeds.
        await store.CreateAsync(new EntityWithDateTime { Id = "p|r", EventDate = input, OptionalDate = input });
        var read = await store.OneAsync("p|r");

        Assert.NotNull(read);
        // Local is converted to its UTC instant; Unspecified is treated as already-UTC. Either way
        // the round-tripped instant matches (DateTime equality compares ticks, not Kind).
        var expected = kind == DateTimeKind.Local ? input.ToUniversalTime() : input;
        Assert.Equal(expected, read!.EventDate);
        Assert.Equal(expected, read.OptionalDate);
    }

    // ── B2: nested filter into a JSON-serialized owner ──────────────────────

    [Fact]
    public void B2_FilterIntoJsonSerializedNested_Throws()
    {
        // Location is a positional record → stored as one JSON cell, so x.Location.Lat has no
        // column to target. Rejecting beats silently translating to a filter that matches nothing.
        Expression<Func<EntityWithJsonNested, bool>> predicate = x => x.Location.Lat > 5;

        var ex = Assert.Throws<NotSupportedException>(() => TableFilterTranslator.Translate(predicate));
        Assert.Contains("JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void B2_FilterIntoFlattenedNested_StillTranslates()
    {
        // Control: AddressInfo flattens to Address_City columns, so nested access DOES translate —
        // the B2 guard must not over-reach and reject legitimately flattened owners.
        Expression<Func<NestedEntity, bool>> predicate = x => x.Address.City == "NYC";

        var filter = TableFilterTranslator.Translate(predicate);
        Assert.Contains("Address_City", filter);
    }

    [Fact]
    public void B2_FilterIntoAbstractDeclaredNested_StillTranslates()
    {
        // The guard sees only the STATIC type (abstract ShapeBase, whose public-ctor list is empty),
        // but the serializer flattens by RUNTIME type (Circle → Shape_Radius). Rejecting here would
        // be a false positive breaking a previously-valid query.
        Expression<Func<EntityWithAbstractNested, bool>> predicate = x => x.Shape.Radius > 5;

        var filter = TableFilterTranslator.Translate(predicate);
        Assert.Contains("Shape_Radius", filter);
    }

    [Fact]
    public void B2_FilterIntoEnumerableInterfaceNested_Throws()
    {
        // An interface that is itself IEnumerable is always stored as JSON — the guard must reject the
        // filter. Regression for the check ordering (IEnumerable must be tested before interface/abstract).
        Expression<Func<EntityWithEnumerableInterfaceNested, bool>> predicate = x => x.Stream.Score > 5;

        var ex = Assert.Throws<NotSupportedException>(() => TableFilterTranslator.Translate(predicate));
        Assert.Contains("JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── G1: cache hands out isolated copies ─────────────────────────────────

    [Fact]
    public async Task G1_OneAsync_ReturnsIsolatedCopy_MutationDoesNotCorruptCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet(Mocks.Row("p", "r", name: "original"));

        var first = await h.Store.OneAsync("p|r");
        first!.Name = "MUTATED";
        var second = await h.Store.OneAsync("p|r");   // served from cache

        Assert.Equal("original", second!.Name);       // the mutation to `first` must not leak in
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G1_QueryAsync_ReturnsIsolatedCopies_MutationDoesNotCorruptCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByPartition(Mocks.Row("p", "r", name: "original"));

        var first = (await h.Store.QueryAsync("p")).Single();
        first.Name = "MUTATED";
        var second = (await h.Store.QueryAsync("p")).Single();   // served from cache

        Assert.Equal("original", second.Name);
        h.Table.Received(1).QueryAsync<TableEntity>(
            Arg.Any<Expression<Func<TableEntity, bool>>>(), Arg.Any<int?>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G1_PredicateQuery_WarmsPerEntityCacheWithIsolatedCopy()
    {
        // The predicate/stream/page read paths go through Materialize, which warms the per-entity
        // cache. That cached instance must be isolated from the one handed to the caller, or a later
        // OneAsync hit returns the caller's mutation. (Was unfixed by the first G1 pass.)
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "r", name: "original"));

        var list = await h.Store.QueryAsync(x => x.Value == 42, "p");
        list[0].Name = "MUTATED";
        var one = await h.Store.OneAsync("p|r");   // cache HIT warmed by the predicate query

        Assert.Equal("original", one!.Name);
        await h.Table.DidNotReceive().GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G1_QueryPage_WarmsPerEntityCacheWithIsolatedCopy()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "r", name: "original"));

        var page = await h.Store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10 });
        page.Items[0].Name = "MUTATED";
        var one = await h.Store.OneAsync("p|r");   // cache HIT warmed by the page fetch

        Assert.Equal("original", one!.Name);
        await h.Table.DidNotReceive().GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G1_CreateThenMutate_DoesNotCorruptCache()
    {
        // Write paths cache the entity too; the returned instance must be isolated from the cached one,
        // or a create-then-mutate (or read-modify-write) leaks a never-persisted value into later reads.
        using var h = new StorageHarness<TestEntity>();
        h.SetupAdd();

        var created = await h.Store.CreateAsync(new TestEntity { Id = "p|r", Name = "original" });
        created.Name = "MUTATED";
        var one = await h.Store.OneAsync("p|r");   // cache HIT warmed by CreateAsync

        Assert.Equal("original", one!.Name);
        await h.Table.DidNotReceive().GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G1_UpsertThenMutate_DoesNotCorruptCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpsert();

        var upserted = await h.Store.UpsertAsync(new TestEntity { Id = "p|r", Name = "original" });
        upserted.Name = "MUTATED";
        var one = await h.Store.OneAsync("p|r");   // cache HIT warmed by UpsertAsync

        Assert.Equal("original", one!.Name);
        await h.Table.DidNotReceive().GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G1_CommittedWrite_DoesNotThrow_WhenEntityIsNotRoundTrippable()
    {
        // The cache snapshot (Clone) round-trips through the serializer AFTER the write commits. For an
        // entity that serializes but cannot deserialize (interface-typed member → JSON cell), the clone
        // throws — but caching is best-effort and must not fail a write that already succeeded.
        using var h = new StorageHarness<EntityWithEnumerableInterfaceNested>();
        h.SetupAdd();

        var created = await h.Store.CreateAsync(
            new EntityWithEnumerableInterfaceNested { Id = "p|r", Stream = new ScalarStreamImpl { Score = 1 } });

        Assert.NotNull(created);   // did not throw
        await h.Table.Received(1).AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>());
    }

    // ── G3: partition-scoped methods normalize the partition arg ────────────

    [Fact]
    public async Task G3_CachedQuery_NaturalAndNormalizedPartition_ShareOneFetch()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByPartition(Mocks.Row("my-vision", "r1"));

        await h.Store.QueryAsync("My Vision");   // fetch #1 — normalizes to "my-vision"
        await h.Store.QueryAsync("my-vision");   // cache HIT — same normalized key

        h.Table.Received(1).QueryAsync<TableEntity>(
            Arg.Any<Expression<Func<TableEntity, bool>>>(), Arg.Any<int?>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G3_BoundedQuery_NormalizesPartitionInODataFilter()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("my-vision", "r1"));

        await h.Store.QueryAsync(new QueryOptions { Partition = "My Vision" });

        Assert.NotNull(h.LastQueryFilter);
        Assert.Contains("my-vision", h.LastQueryFilter);
        Assert.DoesNotContain("My Vision", h.LastQueryFilter);
    }

    [Fact]
    public async Task G3_Fake_PartitionScopedMethods_NormalizeArg()
    {
        // Parity: the fake normalizes the partition arg the same way, so a green fake test holds
        // against Azure. Stored id "My Vision|r1" → PartitionKey "my-vision".
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "My Vision|r1", Name = "x" });

        Assert.Single(await store.QueryAsync("My Vision"));
        Assert.Equal(1, await store.CountAsync("My Vision"));
        Assert.Equal(1, await store.DeletePartitionAsync("My Vision"));
        Assert.Empty(await store.QueryAsync("My Vision"));
    }

    [Fact]
    public async Task G3_RowKeyPrefix_IsNormalizedToStoredForm()
    {
        // Stored id "proj|Task A" → RowKey "task-a" (whole id normalized on write). A natural-form
        // RowKeyPrefix must be normalized to match, consistent with Partition.
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "proj|Task A", Name = "x" });

        Assert.Single(await store.QueryAsync(new QueryOptions { Partition = "proj", RowKeyPrefix = "Task A" }));
        var page = await store.QueryPageAsync(new QueryOptions { Partition = "proj", RowKeyPrefix = "Task A" });
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task G3_RowKeyPrefix_NormalizedInODataFilter()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("proj", "task-a"));

        await h.Store.QueryAsync(new QueryOptions { Partition = "proj", RowKeyPrefix = "Task A" });

        Assert.NotNull(h.LastQueryFilter);
        Assert.Contains("task-a", h.LastQueryFilter);
        Assert.DoesNotContain("Task A", h.LastQueryFilter);
    }

    // ── G4: whole-table query cache is invalidated by writes ────────────────

    [Fact]
    public async Task G4_WhitespacePartitionQuery_IsInvalidatedByWrite()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "r"));
        h.SetupAdd();

        await h.Store.QueryAsync("   ");   // whitespace collapses to a whole-table scan cached under "*"
        await h.Store.CreateAsync(new TestEntity { Id = "p|new", Name = "x" }); // invalidates "*"
        await h.Store.QueryAsync("   ");   // must re-fetch — pre-fix this key was never evicted

        h.Table.Received(2).QueryAsync<TableEntity>(
            Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── G5: cache keys are namespace-qualified (FullName) ───────────────────

    [Fact]
    public async Task G5_SharedCache_SameSimpleNameDifferentNamespaces_DoNotCollide()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var a = new StorageHarness<CollisionA.Widget>(cache: cache);
        using var b = new StorageHarness<CollisionB.Widget>(cache: cache);
        a.SetupGet(Mocks.Row("p", "r", name: "A-value"));
        b.SetupGet(Mocks.Row("p", "r", name: "B-value"));

        var fromA = await a.Store.OneAsync("p|r");   // caches under CollisionA.Widget|...
        var fromB = await b.Store.OneAsync("p|r");   // pre-0.5.2 (simple-name key) would read A's entry

        Assert.Equal("A-value", fromA!.Name);
        Assert.Equal("B-value", fromB!.Name);
        // Proof there was no collision: B actually fetched instead of being served A's cached row.
        await b.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── G6: cache entries declare Size (survive a size-limited cache) ───────

    [Fact]
    public async Task G6_SizeLimitedCache_EntityPath_CachesWithoutThrowing()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        using var h = new StorageHarness<TestEntity>(cache: cache);
        h.SetupGet(Mocks.Row("p", "r", name: "x"));

        // Pre-0.5.2 the entry omitted Size; MemoryCache with a SizeLimit throws on Set without it,
        // so the first read below would blow up. With Size set, it caches and the second read hits.
        Assert.NotNull(await h.Store.OneAsync("p|r"));
        Assert.NotNull(await h.Store.OneAsync("p|r"));

        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G6_SizeLimitedCache_QueryPath_CachesWithoutThrowing()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        using var h = new StorageHarness<TestEntity>(cache: cache);
        h.SetupQueryByPartition(Mocks.Row("p", "r"));

        // The query path Sets two kinds of entries (the result list and each warmed entity); both
        // must declare Size or this throws.
        await h.Store.QueryAsync("p");
        await h.Store.QueryAsync("p");   // cache hit

        h.Table.Received(1).QueryAsync<TableEntity>(
            Arg.Any<Expression<Func<TableEntity, bool>>>(), Arg.Any<int?>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }
}
