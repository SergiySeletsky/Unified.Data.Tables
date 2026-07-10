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

    // ── UpsertAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_CallsUpsertEntity_InReplaceMode()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpsert();

        await h.Store.UpsertAsync(new TestEntity { Id = "p|r", Name = "x" });

        await h.Table.Received(1).UpsertEntityAsync(
            Arg.Any<TableEntity>(), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertAsync_PopulatesETagFromResponse()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpsert("W/\"upserted\"");

        var result = await h.Store.UpsertAsync(new TestEntity { Id = "p|r", Name = "x" });

        Assert.Equal(new ETag("W/\"upserted\"").ToString(), result.ETag);
    }

    [Fact]
    public async Task UpsertAsync_PreservesCallerSuppliedCreatedAt()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpsert();
        var created = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await h.Store.UpsertAsync(new TestEntity { Id = "p|r", CreatedAt = created });

        Assert.Equal(created, result.CreatedAt);
    }

    [Fact]
    public async Task UpsertAsync_StampsUpdatedAt_AndResetsTimestamp()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpsert();

        var before = DateTimeOffset.UtcNow;
        var result = await h.Store.UpsertAsync(new TestEntity { Id = "p|r", Timestamp = DateTimeOffset.UtcNow });
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(result.UpdatedAt, before, after);
        Assert.Null(result.Timestamp);
    }

    [Fact]
    public async Task UpsertAsync_WithNullEntity_ThrowsArgumentNull()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.UpsertAsync(null!));
    }

    [Fact]
    public async Task UpsertAsync_CachesEntity_AndInvalidatesQueryCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryAll();
        h.SetupUpsert();

        await h.Store.QueryAsync();                                       // warm query cache
        await h.Store.UpsertAsync(new TestEntity { Id = "up|1", Name = "x" });
        var cached = await h.Store.OneAsync("up|1");                      // served from entity cache
        await h.Store.QueryAsync();                                       // query cache invalidated → 2nd SDK call

        Assert.NotNull(cached);
        await h.Table.DidNotReceive().GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        h.Table.Received(2).QueryAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertAsync_ChangingProtectedProperty_WithoutAuthorizer_Throws()
    {
        using var h = new StorageHarness<ProtectedEntity>();   // no authorizer registered
        var stored = new ProtectedEntity { Id = "all|e1", Name = "old", Salary = 100m }.ToTableEntity("all", "e1");
        stored.ETag = new ETag("W/\"etag1\"");
        h.SetupGet("all", "e1", stored);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.Store.UpsertAsync(new ProtectedEntity { Id = "all|e1", Name = "old", Salary = 200m }));
    }

    // ── UpdateAsync (explicit ConcurrencyMode) ──────────────────────────────

    [Fact]
    public async Task UpdateAsync_Strict_WithoutETag_ThrowsInvalidOperation()
    {
        using var h = new StorageHarness<TestEntity>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Store.UpdateAsync(new TestEntity { Id = "p|r", Name = "x" }, ConcurrencyMode.Strict));
    }

    [Fact]
    public async Task UpdateAsync_Strict_UsesCallerETag()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate("W/\"new\"");

        await h.Store.UpdateAsync(
            new TestEntity { Id = "p|r", Name = "x", ETag = "W/\"mine\"" }, ConcurrencyMode.Strict);

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), new ETag("W/\"mine\""), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_Strict_412Propagates_WithoutRetry()
    {
        using var h = new StorageHarness<TestEntity>();
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(412, "Precondition Failed"));

        await Assert.ThrowsAsync<RequestFailedException>(() => h.Store.UpdateAsync(
            new TestEntity { Id = "p|r", Name = "x", ETag = "W/\"mine\"" }, ConcurrencyMode.Strict));

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), Arg.Any<ETag>(), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
        await h.Table.DidNotReceive().GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_LastWriterWins_UsesWildcardETag_EvenWhenEntityCarriesOne()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate();

        await h.Store.UpdateAsync(
            new TestEntity { Id = "p|r", Name = "x", ETag = "W/\"stale\"" }, ConcurrencyMode.LastWriterWins);

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), ETag.All, TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    // ── QueryAsync(QueryOptions) / QueryStreamAsync ─────────────────────────

    [Fact]
    public async Task QueryOptions_RowKeyPrefix_WithoutPartition_Throws()
    {
        using var h = new StorageHarness<TestEntity>();

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Store.QueryAsync(new QueryOptions { RowKeyPrefix = "msg_" }));
    }

    [Fact]
    public async Task QueryOptions_NonPositiveTake_Throws()
    {
        using var h = new StorageHarness<TestEntity>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => h.Store.QueryAsync(new QueryOptions { Take = 0 }));
    }

    [Fact]
    public async Task QueryOptions_Partition_BuildsEqualityFilter()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("vis-1", "r1"));

        var results = await h.Store.QueryAsync(new QueryOptions { Partition = "vis-1" });

        Assert.Single(results);
        Assert.Equal("PartitionKey eq 'vis-1'", h.LastQueryFilter);
    }

    [Fact]
    public async Task QueryOptions_RowKeyPrefix_BuildsRangeFilter()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter();

        await h.Store.QueryAsync(new QueryOptions { Partition = "vis-1", RowKeyPrefix = "msg_" });

        // '_' + 1 = '`' — the canonical [prefix, next(prefix)) range.
        Assert.Equal("PartitionKey eq 'vis-1' and RowKey ge 'msg_' and RowKey lt 'msg`'", h.LastQueryFilter);
    }

    [Fact]
    public async Task QueryOptions_PrefixEndingInMaxChar_DropsUpperBound()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter();

        await h.Store.QueryAsync(new QueryOptions { Partition = "p", RowKeyPrefix = "a￿" });

        Assert.Equal("PartitionKey eq 'p' and RowKey ge 'a￿' and RowKey lt 'b'", h.LastQueryFilter);
    }

    [Fact]
    public async Task QueryOptions_Take_LimitsResults()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "r1"), Mocks.Row("p", "r2"), Mocks.Row("p", "r3"));

        var results = await h.Store.QueryAsync(new QueryOptions { Partition = "p", Take = 2 });

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task QueryOptions_IsNeverCached()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "r1"));

        await h.Store.QueryAsync(new QueryOptions { Partition = "p" });
        await h.Store.QueryAsync(new QueryOptions { Partition = "p" });

        h.Table.Received(2).QueryAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryStreamAsync_NullOptions_StreamsWholeTable_WithETagAndTimestamp()
    {
        using var h = new StorageHarness<TestEntity>();
        var serviceTime = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
        h.SetupQueryByFilter(Mocks.Row("p", "r1", timestamp: serviceTime), Mocks.Row("p", "r2"));

        var seen = new List<TestEntity>();
        await foreach (var entity in h.Store.QueryStreamAsync())
            seen.Add(entity);

        Assert.Equal(2, seen.Count);
        Assert.Null(h.LastQueryFilter);
        Assert.Equal(new ETag("W/\"etag1\"").ToString(), seen[0].ETag);
        Assert.Equal(serviceTime, seen[0].Timestamp);
    }

    // ── Batch writes ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateBatchAsync_EmptyCollection_ReturnsZero_WithoutCalls()
    {
        using var h = new StorageHarness<TestEntity>();

        var written = await h.Store.CreateBatchAsync([]);

        Assert.Equal(0, written);
        await h.Table.DidNotReceive().SubmitTransactionAsync(
            Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertBatchAsync_ChunksAt100PerTransaction()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupTransaction();
        var entities = Enumerable.Range(0, 150)
            .Select(i => new TestEntity { Id = $"p|r{i:D3}", Value = i })
            .ToList();

        var written = await h.Store.UpsertBatchAsync(entities);

        Assert.Equal(150, written);
        await h.Table.Received(2).SubmitTransactionAsync(
            Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertBatchAsync_GroupsByPartition_OneTransactionEach()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupTransaction();

        await h.Store.UpsertBatchAsync(
        [
            new TestEntity { Id = "p1|a" },
            new TestEntity { Id = "p2|a" },
            new TestEntity { Id = "p1|b" },
        ]);

        await h.Table.Received(2).SubmitTransactionAsync(
            Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateBatchAsync_StampsTimestamps_AndNormalizesIds()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupTransaction();
        var entity = new TestEntity { Id = "  P1 | A ", Timestamp = DateTimeOffset.UtcNow };

        var before = DateTimeOffset.UtcNow;
        await h.Store.CreateBatchAsync([entity]);
        var after = DateTimeOffset.UtcNow;

        Assert.Equal("p1-|-a", entity.Id);
        Assert.InRange(entity.CreatedAt, before, after);
        Assert.InRange(entity.UpdatedAt, before, after);
        Assert.Null(entity.Timestamp);
    }

    [Fact]
    public async Task UpsertBatchAsync_EntityWithoutId_Throws()
    {
        using var h = new StorageHarness<TestEntity>();

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Store.UpsertBatchAsync([new TestEntity { Id = " " }]));
    }

    [Fact]
    public async Task UpsertBatchAsync_InvalidatesQueryCache_ForAffectedPartitions()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByPartition(Mocks.Row("p1", "r1"));
        h.SetupTransaction();

        await h.Store.QueryAsync("p1");                                    // warm
        await h.Store.UpsertBatchAsync([new TestEntity { Id = "p1|new" }]);
        await h.Store.QueryAsync("p1");                                    // must miss

        h.Table.Received(2).QueryAsync<TableEntity>(
            Arg.Any<System.Linq.Expressions.Expression<Func<TableEntity, bool>>>(),
            Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── CountAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CountAsync_CountsAllRows()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "r1"), Mocks.Row("p", "r2"), Mocks.Row("q", "r3"));

        var count = await h.Store.CountAsync();

        Assert.Equal(3, count);
        Assert.Null(h.LastQueryFilter);
    }

    [Fact]
    public async Task CountAsync_WithPartition_FiltersByPartition()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "r1"));

        var count = await h.Store.CountAsync("p");

        Assert.Equal(1, count);
        Assert.Equal("PartitionKey eq 'p'", h.LastQueryFilter);
    }
}
