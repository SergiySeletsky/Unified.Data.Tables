using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Ruling R13 (CACHE-R4-3) against Azurite: a byte[] a caller passed to the store or received from it is never
/// the array a cache entry holds, so neither what the cache answers nor what an optimistic write sends from it
/// changes when the caller reuses or edits that array. Own table, partition and container, deleted at the end.
/// </summary>
public sealed class NoByteArraySharedWithTheStoreAzuriteTests : AzuriteCleanupTestBase
{
    private readonly string tableName = "nbas" + Guid.NewGuid().ToString("N");

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

    private async Task<T> FreshReadAsync<T>(string id) where T : class, IEntity, new()
    {
        using var fresh = new MemoryCache(new MemoryCacheOptions());
        return (await CachingStore<T>(fresh).OneAsync(id, Ct))!;
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

    // CACHE-R4-3, the conditional builder: the caller's buffer must not reach the cached copy, so a
    // MutateAsync of another column never stores bytes that were never written.
    [Fact]
    public async Task AConditionalBuilderUpdate_WithAByteArray_NeverLetsMutateAsyncStoreBytesThatWereNeverWritten()
    {
        SkipUnlessAvailable();
        await InOwnTableAsync(async () =>
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = CachingStore<SharedBytesDoc>(cache);
            var id = Id("b");
            await store.UpsertAsync(new SharedBytesDoc { Id = id, Data = [1, 2, 3], Version = 1 }, Ct);
            var read = (await store.OneAsync(id, Ct))!;
            var mine = new byte[] { 7, 7, 7 };
            await store.UpdateAsync(id, u => u.SetProperty(x => x.Data, mine).WithETag(read.ETag!), Ct);
            mine[0] = 99; // the caller reuses its buffer; nothing is written

            var served = (await store.OneAsync(id, Ct))!;
            await store.MutateAsync(id, e => e.Version = 2, ct: Ct);
            var stored = await FreshReadAsync<SharedBytesDoc>(id);
            Assert.True(served.Data!.SequenceEqual(new byte[] { 7, 7, 7 }) && stored.Data!.SequenceEqual(new byte[] { 7, 7, 7 }),
                $"served [{string.Join(",", served.Data!)}]; after MutateAsync(Version) the table holds [{string.Join(",", stored.Data!)}]");
            Assert.Equal(2, stored.Version);
        });
    }

    // CACHE-R4-3, the write path: a writer reuses the buffer it wrote; a later MutateAsync of another column
    // (served from the cache, with the row's own ETag) must store the bytes that were written.
    [Fact]
    public async Task AWriter_ReusingTheByteArrayItWrote_NeverLetsMutateAsyncStoreBytesThatWereNeverWritten()
    {
        SkipUnlessAvailable();
        await InOwnTableAsync(async () =>
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = CachingStore<SharedBytesDoc>(cache);
            var id = Id("u");
            var buffer = new byte[] { 1, 2, 3 };
            await store.UpsertAsync(new SharedBytesDoc { Id = id, Data = buffer, Version = 1 }, Ct);
            buffer[0] = 99; // the caller reuses its buffer; nothing is written

            await store.MutateAsync(id, e => e.Version = 2, ct: Ct);
            var stored = await FreshReadAsync<SharedBytesDoc>(id);
            Assert.Equal(2, stored.Version);
            Assert.Equal(new byte[] { 1, 2, 3 }, stored.Data);
        });
    }

    // CACHE-R4-3, the read path: a reader that changes the byte[] it received does not change what the next
    // reader gets.
    [Fact]
    public async Task AReader_ChangingTheByteArrayItReceived_DoesNotChangeTheCachedEntity()
    {
        SkipUnlessAvailable();
        await InOwnTableAsync(async () =>
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = CachingStore<SharedBytesDoc>(cache);
            var id = Id("r");
            await store.UpsertAsync(new SharedBytesDoc { Id = id, Data = [1, 2, 3], Version = 1 }, Ct);
            cache.Compact(1.0);
            var first = (await store.OneAsync(id, Ct))!; // a read's put
            first.Data![1] = 98;
            var second = (await store.OneAsync(id, Ct))!;
            Assert.Equal(new byte[] { 1, 2, 3 }, second.Data);
        });
    }

    // R13 for a large-value byte[] on Azurite (one cell, split): the writer's buffer and the reader's array
    // never reach the cache, so a MutateAsync from the cache stores what was written.
    [Theory]
    [InlineData(16)]
    [InlineData(200_000)]
    public async Task ALargeValueByteArray_IsNeverSharedWithTheCachedEntity(int length)
    {
        SkipUnlessAvailable();
        await InOwnTableAsync(async () =>
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = CachingStore<SharedBytesLargeDoc>(cache);
            var id = Id("l");
            var original = new byte[length];
            new Random(length).NextBytes(original);
            var mine = (byte[])original.Clone();
            await store.UpsertAsync(new SharedBytesLargeDoc { Id = id, Image = mine, Version = 1 }, Ct);
            mine[0] ^= 0xFF;
            (await store.OneAsync(id, Ct))!.Image![1] ^= 0xFF;

            await store.MutateAsync(id, e => e.Version = 2, ct: Ct);
            var stored = await FreshReadAsync<SharedBytesLargeDoc>(id);
            Assert.Equal(2, stored.Version);
            Assert.Equal(original, stored.Image);
        });
    }
}
