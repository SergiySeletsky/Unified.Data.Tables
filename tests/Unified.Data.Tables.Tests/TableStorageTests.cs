using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Behavioural tests for <see cref="TableStorage{T}"/> against NSubstitute mocks of the Azure Tables
/// SDK: CRUD, id normalization and composite-key splitting, in-memory cache hits/invalidation,
/// batch partition delete, and the ETag optimistic-concurrency / retry semantics.
/// </summary>
public class TableStorageTests
{
    // ── Constructor ─────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_CreatesTableClient_WithEntityTypeName()
    {
        using var h = new StorageHarness<TestEntity>();
        h.Service.Received(1).GetTableClient("TestEntity");
    }

    [Fact]
    public void Constructor_CallsCreateIfNotExists()
    {
        using var h = new StorageHarness<TestEntity>();
        h.Table.Received(1).CreateIfNotExists(Arg.Any<CancellationToken>());
    }

    // ── CreateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_WithValidEntity_ReturnsEntity()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupAdd();

        var result = await h.Store.CreateAsync(new TestEntity { Id = "part|row1", Name = "Test", Value = 10 });

        Assert.NotNull(result);
        Assert.Equal("Test", result.Name);
        Assert.Equal("part|row1", result.Id);
    }

    [Fact]
    public async Task CreateAsync_PopulatesETagFromResponse()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupAdd("W/\"created-etag\"");

        var result = await h.Store.CreateAsync(new TestEntity { Id = "e1", Name = "x" });

        Assert.Equal(new ETag("W/\"created-etag\"").ToString(), result.ETag);
    }

    [Fact]
    public async Task CreateAsync_NormalizesId_ToLowerTrimmedAndHyphenated()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupAdd();

        var result = await h.Store.CreateAsync(new TestEntity { Id = "  Part | Row1 ", Name = "Test" });

        Assert.Equal("part-|-row1", result.Id);
    }

    [Fact]
    public async Task CreateAsync_SetsCreatedAtToUtcNow()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupAdd();

        var before = DateTimeOffset.UtcNow;
        var result = await h.Store.CreateAsync(new TestEntity { Id = "id1", Name = "Test" });
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(result.CreatedAt, before, after);
    }

    [Fact]
    public async Task CreateAsync_ResetsTimestamp_UntilNextRead()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupAdd();

        var entity = new TestEntity { Id = "ts1", Name = "Test", Timestamp = DateTimeOffset.UtcNow };
        var result = await h.Store.CreateAsync(entity);

        Assert.Null(result.Timestamp);
    }

    [Fact]
    public async Task CreateAsync_WithNullEntity_ThrowsArgumentNull()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.CreateAsync(null!));
    }

    [Fact]
    public async Task CreateAsync_WithEmptyId_ThrowsArgumentNull()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.CreateAsync(new TestEntity { Id = "  " }));
    }

    [Fact]
    public async Task CreateAsync_CachesEntity_SoNextReadSkipsStorage()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupAdd();

        await h.Store.CreateAsync(new TestEntity { Id = "cached1", Name = "CacheTest" });
        var found = await h.Store.OneAsync("cached1");

        Assert.NotNull(found);
        await h.Table.DidNotReceive().GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_InvalidatesQueryCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryAll();
        h.SetupAdd();

        await h.Store.QueryAsync();                                   // warm cache
        await h.Store.CreateAsync(new TestEntity { Id = "new1" });    // invalidate
        await h.Store.QueryAsync();                                   // miss → 2nd SDK call

        h.Table.Received(2).QueryAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── DeleteAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_WithValidId_CallsDeleteWithSplitKeys()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupDelete();

        await h.Store.DeleteAsync("part|row");

        await h.Table.Received(1).DeleteEntityAsync("part", "row", Arg.Any<ETag>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_NormalizesId()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupDelete();

        await h.Store.DeleteAsync("  MyId ");

        await h.Table.Received(1).DeleteEntityAsync("myid", "myid", Arg.Any<ETag>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_WithNullId_ThrowsArgumentNull()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.DeleteAsync(null!));
    }

    [Fact]
    public async Task DeleteAsync_WithEmptyId_ThrowsArgumentNull()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.DeleteAsync("   "));
    }

    [Fact]
    public async Task DeleteAsync_AfterCreate_EvictsFromCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupAdd();
        h.SetupDelete();
        await h.Store.CreateAsync(new TestEntity { Id = "del1", Name = "DeleteMe" });

        await h.Store.DeleteAsync("del1");

        h.SetupGet(entity: null);
        Assert.Null(await h.Store.OneAsync("del1"));
    }

    [Fact]
    public async Task QueryAsync_AfterDelete_InvalidatesCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryAll(Mocks.Row("students", "alice"));
        h.SetupDelete();

        await h.Store.QueryAsync();
        await h.Store.DeleteAsync("students|alice");
        await h.Store.QueryAsync();

        h.Table.Received(2).QueryAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── DeletePartitionAsync ────────────────────────────────────────────────

    [Fact]
    public async Task DeletePartitionAsync_DeletesAllRowsAndReturnsCount()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByPartition(Mocks.Row("p", "r1"), Mocks.Row("p", "r2"), Mocks.Row("p", "r3"));
        h.SetupTransaction();

        var count = await h.Store.DeletePartitionAsync("p");

        Assert.Equal(3, count);
        await h.Table.Received(1).SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePartitionAsync_WithNoRows_ReturnsZero_AndSubmitsNothing()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByPartition();

        var count = await h.Store.DeletePartitionAsync("empty");

        Assert.Equal(0, count);
        await h.Table.DidNotReceive().SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePartitionAsync_WithNullPartition_ThrowsArgumentNull()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.DeletePartitionAsync(null!));
    }

    // ── OneAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneAsync_WithExistingEntity_ReturnsEntity()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet("part", "row", Mocks.Row("part", "row", name: "hello"));

        var result = await h.Store.OneAsync("part|row");

        Assert.NotNull(result);
        Assert.Equal("hello", result!.Name);
    }

    [Fact]
    public async Task OneAsync_PopulatesETagFromRow()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet("part", "row", Mocks.Row("part", "row"));

        var result = await h.Store.OneAsync("part|row");

        Assert.Equal(new ETag("W/\"etag1\"").ToString(), result!.ETag);
    }

    [Fact]
    public async Task OneAsync_PopulatesTimestampFromRow()
    {
        using var h = new StorageHarness<TestEntity>();
        var serviceTime = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        h.SetupGet("part", "row", Mocks.Row("part", "row", timestamp: serviceTime));

        var result = await h.Store.OneAsync("part|row");

        Assert.Equal(serviceTime, result!.Timestamp);
    }

    [Fact]
    public async Task OneAsync_WithNonExistentEntity_ReturnsNull()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet(entity: null);

        Assert.Null(await h.Store.OneAsync("missing"));
    }

    [Fact]
    public async Task OneAsync_WithNullId_ThrowsArgumentNull()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.OneAsync(null!));
    }

    [Fact]
    public async Task OneAsync_ReturnsCachedEntityOnSecondCall()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet("pk", "rk", Mocks.Row("pk", "rk"));

        await h.Store.OneAsync("pk|rk");
        await h.Store.OneAsync("pk|rk");

        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(
            "pk", "rk", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneAsync_NormalizesId()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet(entity: null);

        await h.Store.OneAsync("  MY ID ");

        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(
            "my-id", "my-id", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneAsync_WithoutSeparator_UsesIdAsBothPartitionAndRow()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet(entity: null);

        await h.Store.OneAsync("simplekey");

        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(
            "simplekey", "simplekey", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneAsync_WithMultipleSeparators_SplitsOnlyOnFirst()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet(entity: null);

        await h.Store.OneAsync("a|b|c");

        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(
            "a", "b|c", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── ExistsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistsAsync_WithExistingEntity_ReturnsTrue()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet("pk", "rk", Mocks.Row("pk", "rk"));

        Assert.True(await h.Store.ExistsAsync("pk|rk"));
    }

    [Fact]
    public async Task ExistsAsync_WithNonExistent_ReturnsFalse()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet(entity: null);

        Assert.False(await h.Store.ExistsAsync("nope"));
    }

    [Fact]
    public async Task ExistsAsync_WithNullId_ThrowsArgumentNull()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.ExistsAsync(null!));
    }

    [Fact]
    public async Task ExistsAsync_CachesEntityOnFirstHit()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet("pk", "rk", Mocks.Row("pk", "rk"));

        await h.Store.ExistsAsync("pk|rk");
        var second = await h.Store.ExistsAsync("pk|rk");

        Assert.True(second);
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(
            "pk", "rk", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueFromCache_WhenPreviouslyCreated()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupAdd();
        await h.Store.CreateAsync(new TestEntity { Id = "cached-exist", Name = "X" });

        Assert.True(await h.Store.ExistsAsync("cached-exist"));
        await h.Table.DidNotReceive().GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── QueryAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_WithNoPartition_ReturnsAllEntities()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryAll(Mocks.Row("pk", "rk"));

        var results = await h.Store.QueryAsync();

        Assert.Single(results);
    }

    [Fact]
    public async Task QueryAsync_WithPartition_QueriesByPartition()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByPartition(Mocks.Row("mypartition", "rk"));

        var results = await h.Store.QueryAsync("mypartition");

        Assert.Single(results);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmptyForNoResults()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryAll();

        Assert.Empty(await h.Store.QueryAsync());
    }

    [Fact]
    public async Task QueryAsync_SecondCall_ReturnsCachedData()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryAll(Mocks.Row("pk", "rk"));

        var first = await h.Store.QueryAsync();
        var second = await h.Store.QueryAsync();

        Assert.Single(first);
        Assert.Single(second);
        h.Table.Received(1).QueryAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_WarmsIndividualEntityCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryAll(Mocks.Row("pk2", "rk2"));

        await h.Store.QueryAsync();
        var entity = await h.Store.OneAsync("pk2|rk2");

        Assert.NotNull(entity);
        await h.Table.DidNotReceive().GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_MultipleEntities_ReturnsAll()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryAll(Mocks.Row("pk", "rk1", "first", 1), Mocks.Row("pk", "rk2", "second", 2));

        var results = (await h.Store.QueryAsync()).ToList();

        Assert.Equal(2, results.Count);
    }

    // ── UpdateAsync (whole entity) ──────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_WithValidEntity_ReturnsUpdatedEntity()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate("W/\"etag2\"");

        var result = await h.Store.UpdateAsync(new TestEntity { Id = "upd1", Name = "Updated", Value = 99 });

        Assert.NotNull(result);
        Assert.Equal("Updated", result.Name);
        Assert.Equal(new ETag("W/\"etag2\"").ToString(), result.ETag);
    }

    [Fact]
    public async Task UpdateAsync_SetsUpdatedAtToUtcNow()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate();

        var before = DateTimeOffset.UtcNow;
        var result = await h.Store.UpdateAsync(new TestEntity { Id = "upd2", Name = "Updated" });
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(result.UpdatedAt, before, after);
    }

    [Fact]
    public async Task UpdateAsync_ResetsTimestamp_UntilNextRead()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate();

        var entity = new TestEntity { Id = "upd-ts", Name = "x", Timestamp = DateTimeOffset.UtcNow };
        var result = await h.Store.UpdateAsync(entity);

        Assert.Null(result.Timestamp);
    }

    [Fact]
    public async Task UpdateAsync_WithNullEntity_ThrowsArgumentNull()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.UpdateAsync((TestEntity)null!));
    }

    [Fact]
    public async Task UpdateAsync_WithEmptyId_ThrowsArgumentNull()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.UpdateAsync(new TestEntity { Id = "" }));
    }

    [Fact]
    public async Task UpdateAsync_NormalizesId()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate();

        var result = await h.Store.UpdateAsync(new TestEntity { Id = "  UpD ", Name = "Normalized" });

        Assert.Equal("upd", result.Id);
    }

    [Fact]
    public async Task UpdateAsync_WithCallerSuppliedETag_UsesThatETag()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate("W/\"new\"");

        await h.Store.UpdateAsync(new TestEntity { Id = "e1", Name = "x", ETag = "W/\"caller-etag\"" });

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), new ETag("W/\"caller-etag\""), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_UsesCachedETag_WhenCallerSuppliesNone()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet("pk", "rk", Mocks.Row("pk", "rk"));   // caches with ETag W/"etag1"
        await h.Store.OneAsync("pk|rk");
        h.SetupUpdate("W/\"new\"");

        await h.Store.UpdateAsync(new TestEntity { Id = "pk|rk", Name = "updated" });   // no ETag

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), new ETag("W/\"etag1\""), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_WithNoCachedETag_UsesETagAll()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate();

        await h.Store.UpdateAsync(new TestEntity { Id = "no-cache-etag", Name = "Test" });

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), ETag.All, TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_OnETagMismatch_WithoutCallerETag_RetriesWithFreshETag()
    {
        using var h = new StorageHarness<TestEntity>();
        var calls = 0;
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns(_ => calls++ == 0
                ? throw new RequestFailedException(412, "Precondition Failed")
                : Mocks.EtagResponse("W/\"fresh-etag\""));
        h.SetupGet("retry", "key", Mocks.Row("retry", "key"));

        var result = await h.Store.UpdateAsync(new TestEntity { Id = "retry|key", Name = "Retry" });

        Assert.NotNull(result);
        await h.Table.Received(2).UpdateEntityAsync(
            Arg.Any<TableEntity>(), Arg.Any<ETag>(), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_OnETagMismatch_WhenEntityDeleted_Throws()
    {
        using var h = new StorageHarness<TestEntity>();
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(412, "Precondition Failed"));
        h.SetupGet("deleted", "key", entity: null);

        await Assert.ThrowsAsync<RequestFailedException>(
            () => h.Store.UpdateAsync(new TestEntity { Id = "deleted|key", Name = "Gone" }));
    }

    [Fact]
    public async Task UpdateAsync_WithCallerETag_DoesNotRetryOnConflict()
    {
        using var h = new StorageHarness<TestEntity>();
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(412, "Precondition Failed"));

        await Assert.ThrowsAsync<RequestFailedException>(
            () => h.Store.UpdateAsync(new TestEntity { Id = "strict|key", Name = "x", ETag = "W/\"caller\"" }));

        // Strict optimistic concurrency: the 412 surfaces, no re-fetch, no retry.
        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), Arg.Any<ETag>(), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
        await h.Table.DidNotReceive().GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── Cross-partition cache invalidation ──────────────────────────────────

    [Fact]
    public async Task InvalidateQueryCache_ClearsTrackedPartitions()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByPartition(Mocks.Row("p1", "r1"));
        h.SetupAdd();

        await h.Store.QueryAsync("p1");
        await h.Store.QueryAsync("p2");
        await h.Store.CreateAsync(new TestEntity { Id = "p1|new", Name = "New" });   // invalidates p1, p2, *
        await h.Store.QueryAsync("p2");

        h.Table.Received(3).QueryAsync<TableEntity>(
            Arg.Any<System.Linq.Expressions.Expression<Func<TableEntity, bool>>>(),
            Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }
}
