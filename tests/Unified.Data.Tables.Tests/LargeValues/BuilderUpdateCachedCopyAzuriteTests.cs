using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The builder update over a cached entity (CACHE-2 of the fix-wave-3 review; ruling R10) against a local
/// Azurite, with the service's own ETags: two stores with two caches, the other writer changes a column,
/// the first store merges another one and then makes an optimistic read-modify-write. The other writer's
/// change survives; a merge conditional on the cached version keeps the cache warm with the stored ETag.
/// Each test works in its own table, partition <c>lv-{guid}</c> and container <c>lv-{guid}</c>, deleted at
/// the end.
/// </summary>
public sealed class BuilderUpdateCachedCopyAzuriteTests : AzuriteCleanupTestBase
{
    private readonly string tableName = "bucc" + Guid.NewGuid().ToString("N");

    private TableStorage<T> CachingStore<T>(MemoryCache cache) where T : class, IEntity, new()
    {
        Table(tableName);
        var options = new UnifiedTableStorageOptions
        {
            TableNameResolver = _ => tableName,
            BlobOffload = new BlobOffloadOptions { ContainerName = ContainerName, ThresholdBytes = SmallThreshold },
        };
        return new TableStorage<T>(TableService, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, options, BlobService);
    }

    private async Task InOwnTableAsync(Func<Task> test)
    {
        try
        {
            await test();
        }
        finally
        {
            await TableService.DeleteTableAsync(tableName, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABuilderUpdate_OverACachedEntityAnotherWriterChanged_NeverLetsAnOptimisticWriteUndoThatChange(bool largeValues)
    {
        SkipUnlessAvailable();
        await InOwnTableAsync(() => largeValues ? RunAnotherWriterAsync<BuilderCopySplitDoc>() : RunAnotherWriterAsync<BuilderCopyDoc>());
    }

    private async Task RunAnotherWriterAsync<T>() where T : Entity, IRaceDoc, new()
    {
        using var cacheA = new MemoryCache(new MemoryCacheOptions());
        using var cacheB = new MemoryCache(new MemoryCacheOptions());
        var a = CachingStore<T>(cacheA);
        var b = CachingStore<T>(cacheB);
        var id = Id("a");

        var initial = new T { Id = id, Body = "v1", Version = 1 };
        Set(initial, "Name", "original");
        await a.UpsertAsync(initial, Ct);
        Assert.Equal("original", Get(await a.OneAsync(id, Ct), "Name"));

        await b.MutateAsync(id, e => Set(e, "Name", "changed by B"), ct: Ct);
        await a.UpdateAsync(id, u => u.SetProperty(x => x.Version, 2), Ct);
        await a.MutateAsync(id, e => Set(e, "Tag", "set by A"), ct: Ct);

        using var fresh = new MemoryCache(new MemoryCacheOptions());
        var stored = await CachingStore<T>(fresh).OneAsync(id, Ct);
        Assert.Equal("set by A", Get(stored, "Tag"));
        Assert.Equal(2, stored!.Version);
        Assert.Equal("changed by B", Get(stored, "Name")); // B's committed change survives
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABuilderUpdateConditionalOnTheCachedVersion_KeepsTheCacheWarmWithTheStoredETag(bool largeValues)
    {
        SkipUnlessAvailable();
        await InOwnTableAsync(() => largeValues ? RunConditionalAsync<BuilderCopySplitDoc>() : RunConditionalAsync<BuilderCopyDoc>());
    }

    private async Task RunConditionalAsync<T>() where T : Entity, IRaceDoc, new()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = CachingStore<T>(cache);
        var id = Id("c");
        var initial = new T { Id = id, Body = "v1", Version = 1 };
        Set(initial, "Name", "original");
        await store.UpsertAsync(initial, Ct);
        var read = (await store.OneAsync(id, Ct))!;

        var returned = await store.UpdateAsync(id, u => u.SetProperty(x => x.Version, 2).WithETag(read.ETag!), Ct);

        var served = (await store.OneAsync(id, Ct))!;
        Assert.Equal(2, served.Version);
        Assert.Equal(returned, served.ETag);
        using var fresh = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal(returned, (await CachingStore<T>(fresh).OneAsync(id, Ct))!.ETag); // the stored row's ETag

        served.Version = 3;
        await store.UpdateAsync(served, ConcurrencyMode.Strict, Ct); // current: no conflict
        using var other = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal(3, (await CachingStore<T>(other).OneAsync(id, Ct))!.Version);
    }

    private static void Set(object e, string property, string value) => e.GetType().GetProperty(property)!.SetValue(e, value);

    private static string? Get(object? e, string property) => (string?)e?.GetType().GetProperty(property)!.GetValue(e);
}
