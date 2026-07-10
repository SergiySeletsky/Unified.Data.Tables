using Azure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The <see cref="InMemoryStorage{T}"/> fake must be semantically faithful to
/// <see cref="TableStorage{T}"/>: same id conventions, 409 on duplicate create, 404 on updating a
/// missing row, ETag simulation with 412 conflicts, idempotent delete, real-serializer round-trips,
/// and lexical result ordering — so tests written against it hold in production.
/// </summary>
public class InMemoryStorageTests
{
    // ── Create ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_StoresAndReturnsEntity_WithETag()
    {
        var store = new InMemoryStorage<TestEntity>();

        var created = await store.CreateAsync(new TestEntity { Id = "p|r", Name = "x", Value = 7 });

        Assert.NotNull(created.ETag);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task CreateAsync_DuplicateId_Throws409()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r" });

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => store.CreateAsync(new TestEntity { Id = "p|r" }));

        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task CreateAsync_NormalizesId_LikeTableStorage()
    {
        var store = new InMemoryStorage<TestEntity>();

        var created = await store.CreateAsync(new TestEntity { Id = "  Part | Row1 " });

        Assert.Equal("part-|-row1", created.Id);
        Assert.NotNull(await store.OneAsync("part-|-row1"));
    }

    // ── Read ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneAsync_RoundTripsThroughRealSerializer()
    {
        var store = new InMemoryStorage<EntityWithDecimal>();
        await store.CreateAsync(new EntityWithDecimal { Id = "p|r", Amount = 10.123456789012345678m });

        var loaded = await store.OneAsync("p|r");

        // decimal is stored as double by the production serializer — the fake must exhibit the
        // same precision behaviour instead of hiding it.
        Assert.Equal((decimal)(double)10.123456789012345678m, loaded!.Amount);
    }

    [Fact]
    public async Task OneAsync_ReturnsIsolatedCopy()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "original" });

        var first = await store.OneAsync("p|r");
        first!.Name = "mutated";
        var second = await store.OneAsync("p|r");

        Assert.Equal("original", second!.Name);
    }

    [Fact]
    public async Task OneAsync_Missing_ReturnsNull()
    {
        var store = new InMemoryStorage<TestEntity>();
        Assert.Null(await store.OneAsync("nope"));
    }

    [Fact]
    public async Task OneAsync_PopulatesTimestamp()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r" });

        var loaded = await store.OneAsync("p|r");

        Assert.NotNull(loaded!.Timestamp);
    }

    // ── Update (modes + ETag simulation) ────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_MissingRow_Throws404()
    {
        var store = new InMemoryStorage<TestEntity>();

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => store.UpdateAsync(new TestEntity { Id = "p|r" }));

        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public async Task UpdateAsync_WithStaleETag_Throws412()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "v1" });
        var readCopy = await store.OneAsync("p|r");
        await store.UpdateAsync(new TestEntity { Id = "p|r", Name = "v2" });   // no ETag → LWW, bumps version

        readCopy!.Name = "conflicting";
        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => store.UpdateAsync(readCopy));

        Assert.Equal(412, ex.Status);
    }

    [Fact]
    public async Task UpdateAsync_WithFreshETag_Succeeds_AndRefreshesETagInPlace()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "v1" });
        var entity = await store.OneAsync("p|r");

        entity!.Name = "v2";
        await store.UpdateAsync(entity);
        var etagAfterFirst = entity.ETag;
        entity.Name = "v3";
        await store.UpdateAsync(entity);   // sequential updates on same instance must not 412

        Assert.Equal("v3", (await store.OneAsync("p|r"))!.Name);
        Assert.NotEqual(etagAfterFirst, entity.ETag);
    }

    [Fact]
    public async Task UpdateAsync_Strict_WithoutETag_ThrowsInvalidOperation()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateAsync(new TestEntity { Id = "p|r" }, ConcurrencyMode.Strict));
    }

    [Fact]
    public async Task UpdateAsync_LastWriterWins_IgnoresStaleETag()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "v1" });
        var stale = await store.OneAsync("p|r");
        await store.UpdateAsync(new TestEntity { Id = "p|r", Name = "v2" });

        stale!.Name = "forced";
        await store.UpdateAsync(stale, ConcurrencyMode.LastWriterWins);

        Assert.Equal("forced", (await store.OneAsync("p|r"))!.Name);
    }

    // ── Builder merge ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_Builder_MergesOnlyDeclaredColumns()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "old", Value = 5 });

        await store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "new"));
        var loaded = await store.OneAsync("p|r");

        Assert.Equal("new", loaded!.Name);
        Assert.Equal(5, loaded.Value);   // untouched column preserved
    }

    [Fact]
    public async Task UpdateAsync_Builder_MissingRow_Throws404()
    {
        var store = new InMemoryStorage<TestEntity>();

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "x")));

        Assert.Equal(404, ex.Status);
    }

    // ── Upsert ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_InsertsThenReplaces()
    {
        var store = new InMemoryStorage<TestEntity>();

        await store.UpsertAsync(new TestEntity { Id = "p|r", Name = "first", Value = 1 });
        await store.UpsertAsync(new TestEntity { Id = "p|r", Name = "second" });

        var loaded = await store.OneAsync("p|r");
        Assert.Equal("second", loaded!.Name);
        Assert.Equal(0, loaded.Value);   // Replace semantics: row looks like the new object
        Assert.Equal(1, store.Count);
    }

    // ── Query surface ───────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_Partition_ReturnsLexicalOrder()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|b" });
        await store.CreateAsync(new TestEntity { Id = "p|a" });
        await store.CreateAsync(new TestEntity { Id = "q|z" });

        var results = (await store.QueryAsync("p")).Select(e => e.Id).ToList();

        Assert.Equal(new List<string> { "p|a", "p|b" }, results);
    }

    [Fact]
    public async Task QueryOptions_RowKeyPrefix_And_Take()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "vis|msg_001" });
        await store.CreateAsync(new TestEntity { Id = "vis|msg_002" });
        await store.CreateAsync(new TestEntity { Id = "vis|msg_003" });
        await store.CreateAsync(new TestEntity { Id = "vis|chat_001" });

        var results = await store.QueryAsync(new QueryOptions { Partition = "vis", RowKeyPrefix = "msg_", Take = 2 });

        Assert.Equal(new List<string> { "vis|msg_001", "vis|msg_002" }, results.Select(e => e.Id).ToList());
    }

    [Fact]
    public async Task QueryOptions_RowKeyPrefix_WithoutPartition_Throws()
    {
        var store = new InMemoryStorage<TestEntity>();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.QueryAsync(new QueryOptions { RowKeyPrefix = "msg_" }));
    }

    [Fact]
    public async Task QueryStreamAsync_StreamsAll()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|a" });
        await store.CreateAsync(new TestEntity { Id = "p|b" });

        var seen = new List<string>();
        await foreach (var entity in store.QueryStreamAsync())
            seen.Add(entity.Id);

        Assert.Equal(2, seen.Count);
    }

    // ── Batch + count + delete ──────────────────────────────────────────────

    [Fact]
    public async Task CreateBatchAsync_DuplicateInBatch_Throws409_WithoutPartialWrites()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|existing" });

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => store.CreateBatchAsync(
        [
            new TestEntity { Id = "p|new1" },
            new TestEntity { Id = "p|existing" },
        ]));

        Assert.Equal(409, ex.Status);
        Assert.Equal(1, store.Count);   // nothing from the failed batch landed
    }

    [Fact]
    public async Task UpsertBatchAsync_WritesAll_AndReturnsCount()
    {
        var store = new InMemoryStorage<TestEntity>();

        var written = await store.UpsertBatchAsync(
        [
            new TestEntity { Id = "p|a" },
            new TestEntity { Id = "q|b" },
        ]);

        Assert.Equal(2, written);
        Assert.Equal(2, await store.CountAsync());
        Assert.Equal(1, await store.CountAsync("p"));
    }

    [Fact]
    public async Task UpsertBatchAsync_ResetsETags_LikeTableStorage()
    {
        var store = new InMemoryStorage<TestEntity>();
        var entity = new TestEntity { Id = "p|a", ETag = "W/\"stale\"" };

        await store.UpsertBatchAsync([entity]);

        Assert.Null(entity.ETag);
    }

    [Fact]
    public async Task CreateBatchAsync_DuplicateWithinBatch_Throws400()
    {
        var store = new InMemoryStorage<TestEntity>();

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => store.CreateBatchAsync(
        [
            new TestEntity { Id = "p|same" },
            new TestEntity { Id = "p|same" },
        ]));

        Assert.Equal(400, ex.Status);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task DeleteAsync_MissingRow_IsIdempotent()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.DeleteAsync("nope");   // must not throw (SDK DeleteEntity is idempotent)
    }

    [Fact]
    public async Task DeletePartitionAsync_RemovesOnlyThatPartition()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|a" });
        await store.CreateAsync(new TestEntity { Id = "p|b" });
        await store.CreateAsync(new TestEntity { Id = "q|c" });

        var deleted = await store.DeletePartitionAsync("p");

        Assert.Equal(2, deleted);
        Assert.Equal(1, store.Count);
    }

    // ── Protected properties ────────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_ChangingProtectedProperty_WithoutAuthorizer_Throws()
    {
        var store = new InMemoryStorage<ProtectedEntity>();
        await store.UpsertAsync(new ProtectedEntity { Id = "p|r", Name = "x", Salary = 100m });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => store.UpsertAsync(new ProtectedEntity { Id = "p|r", Name = "x", Salary = 200m }));
    }

    [Fact]
    public async Task UpdateAsync_ChangingProtectedProperty_WhenAuthorizerAllows_Succeeds()
    {
        var authorizer = Substitute.For<IProtectedPropertyAuthorizer>();
        authorizer.IsAllowed("admin,accountant").Returns(true);
        var store = new InMemoryStorage<ProtectedEntity>(authorizer);
        await store.UpsertAsync(new ProtectedEntity { Id = "p|r", Salary = 100m });

        var result = await store.UpdateAsync(
            new ProtectedEntity { Id = "p|r", Salary = 200m }, ConcurrencyMode.LastWriterWins);

        Assert.Equal(200m, result.Salary);
    }

    // ── Conveniences + DI ───────────────────────────────────────────────────

    [Fact]
    public async Task Snapshot_And_Clear_Work()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|a", Name = "x" });

        var snapshot = store.Snapshot();
        store.Clear();

        Assert.Single(snapshot);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void AddUnifiedInMemoryStorage_RegistersOpenGenericSingleton()
    {
        var services = new ServiceCollection();
        services.AddUnifiedInMemoryStorage();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IStorage<TestEntity>>();
        var second = provider.GetRequiredService<IStorage<TestEntity>>();

        Assert.IsType<InMemoryStorage<TestEntity>>(first);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task OversizedString_RoundTrips_ThroughGZipCell()
    {
        var store = new InMemoryStorage<TestEntity>();
        var big = new string('x', 40_000) + "END";   // > 64KB UTF-16 → __GZip cell in production
        await store.CreateAsync(new TestEntity { Id = "p|big", Name = big });

        var loaded = await store.OneAsync("p|big");

        Assert.Equal(big, loaded!.Name);   // lossless GZip round-trip, same as TableStorage
    }
}
