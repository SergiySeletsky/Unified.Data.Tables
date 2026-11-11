using System.Reflection;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>A plain entity of the Azurite cache-rule tests whose constructor counts its runs and can throw.</summary>
public sealed class CacheRuleAzuriteCountingDoc : Entity
{
    private static int constructed;
    private static volatile bool failing;

    public CacheRuleAzuriteCountingDoc()
    {
        Interlocked.Increment(ref constructed);
        if (failing)
            throw new InvalidOperationException("the host is not ready yet");
    }

    public static int Constructed => Volatile.Read(ref constructed);

    public static bool Failing { get => failing; set => failing = value; }

    public string? Title { get; set; }
}

/// <summary>
/// The builder update over a cached entity (ruling R10 as revised) against Azurite, with the service's own
/// ETags: CACHE-R4-1, CACHE-R4-2, SERIALIZER-L1 and D1, and the kept-warm and evicted cases. Each test uses
/// its own table, partition and container, deleted at the end.
/// </summary>
[Collection("OversizedCellPolicy")] // the trimmed-string case sets TableEntitySerializer.OversizedCellPolicy
public sealed class BuilderUpdateCacheEntryRuleAzuriteTests : AzuriteCleanupTestBase
{
    private readonly string tableName = "bucr" + Guid.NewGuid().ToString("N");

    private TableStorage<T> CachingStore<T>(MemoryCache cache, TableServiceClient? service = null) where T : class, IEntity, new()
    {
        Table(tableName);
        var options = new UnifiedTableStorageOptions
        {
            TableNameResolver = _ => tableName,
            BlobOffload = new BlobOffloadOptions { ContainerName = ContainerName, ThresholdBytes = SmallThreshold },
        };
        return new TableStorage<T>(service ?? TableService, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, options, BlobService);
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

    // Runs an action once, after the response of the first merge (PATCH/MERGE of an entity) has arrived and
    // before the store that sent it receives it.
    private sealed class AfterFirstMergeResponse(Func<Task> action) : HttpPipelinePolicy
    {
        private int done;

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
            ProcessAsync(message, pipeline).AsTask().GetAwaiter().GetResult();

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            await ProcessNextAsync(message, pipeline);
            var method = message.Request.Method.Method;
            if ((method is "PATCH" or "MERGE" || message.Request.Headers.TryGetValue("X-HTTP-Method", out _))
                && message.Response.Status == 204 && Interlocked.Exchange(ref done, 1) == 0)
                await action();
        }
    }

    // CACHE-R4-1 on Azurite: a read through A during A's merge flight caches B's later change; A's builder
    // must not patch that entry, so B's change survives A's optimistic write of another column.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABuilderUpdate_WhoseEntryARaceReadPutDuringItsFlight_NeverLetsAnOptimisticWriteUndoALaterChange(bool conditional)
    {
        SkipUnlessAvailable();
        await InOwnTableAsync(async () =>
        {
            using var cacheA = new MemoryCache(new MemoryCacheOptions());
            using var cacheB = new MemoryCache(new MemoryCacheOptions());
            var id = Id("a");
            TableStorage<CacheRuleDoc>? a = null;
            var b = CachingStore<CacheRuleDoc>(cacheB);
            var interleaved = false;
            var policy = new AfterFirstMergeResponse(async () =>
            {
                interleaved = true;
                await b.MutateAsync(id, e => e.Name = "changed by B", ct: Ct);
                _ = await a!.OneAsync(id, Ct); // a read through A caches B's version
            });
            a = CachingStore<CacheRuleDoc>(cacheA, TableServiceWith(policy));

            var e0 = (await b.UpsertAsync(new CacheRuleDoc { Id = id, Name = "original", Version = 1 }, Ct)).ETag!;
            await a.UpdateAsync(id, u =>
            {
                u.SetProperty(x => x.Name, "set by A");
                if (conditional)
                    u.WithETag(e0);
            }, Ct);
            Assert.True(interleaved);
            Assert.Equal("changed by B", (await FreshReadAsync<CacheRuleDoc>(id)).Name); // B's write is the later one

            await a.MutateAsync(id, e => e.Tag = "set by A", ct: Ct);
            var stored = await FreshReadAsync<CacheRuleDoc>(id);
            Assert.Equal("set by A", stored.Tag);
            Assert.Equal("changed by B", stored.Name); // B's later, committed change survives
        });
    }

    // CACHE-R4-2 on Azurite (as the verifier narrowed it).
    [Fact]
    public async Task AConditionalWholeObjectBuilderUpdate_NeverLetsAnOptimisticWriteEraseAColumnTheMergeKept()
    {
        SkipUnlessAvailable();
        await InOwnTableAsync(async () =>
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = CachingStore<CacheRuleDoc>(cache);
            var id = Id("w");
            await store.UpsertAsync(new CacheRuleDoc { Id = id, Address = new CacheRuleAddress { City = "c0", Street = "s0" } }, Ct);
            var read = (await store.OneAsync(id, Ct))!;
            await store.UpdateAsync(id, u => u.SetProperty(x => x.Address, new CacheRuleAddress { City = "c1" }).WithETag(read.ETag!), Ct);

            var afterMerge = await FreshReadAsync<CacheRuleDoc>(id);
            var served = (await store.OneAsync(id, Ct))!;

            await store.MutateAsync(id, e => e.Tag = "t", ct: Ct);
            var stored = await FreshReadAsync<CacheRuleDoc>(id);
            Assert.True((served.ETag != afterMerge.ETag || served.Address?.Street == afterMerge.Address?.Street) && stored.Address?.Street == "s0",
                $"table after the merge: Street={afterMerge.Address?.Street ?? "null"}; cache served Street={served.Address?.Street ?? "null"} with ETag {served.ETag} (table {afterMerge.ETag}); after MutateAsync(Tag) the table holds Street={stored.Address?.Street ?? "null"}");
        });
    }

    // Not kept warm on Azurite: the next read fetches the row and returns what a fresh store reads.
    [Theory]
    [InlineData("null", false)]
    [InlineData("nested object", false)]
    [InlineData("object with a null member", true)]
    [InlineData("list", false)]
    [InlineData("byte[]", true)]
    [InlineData("trimmed string", false)]
    [InlineData("trimmed string", true)]
    [InlineData("nested path", false)]
    public async Task ABuilderUpdate_ThatIsNotAWholeScalarPerColumn_EvictsTheEntity_AndTheNextReadReturnsTheRow(string kind, bool conditional)
    {
        SkipUnlessAvailable();
        var previous = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimWithMarker;
        try
        {
            await InOwnTableAsync(async () =>
            {
                using var cache = new MemoryCache(new MemoryCacheOptions());
                var store = CachingStore<CacheRuleDoc>(cache);
                var id = Id("n");
                var initial = BuilderUpdateCacheEntryRuleTests.Initial<CacheRuleDoc>();
                initial.Id = id;
                await store.UpsertAsync(initial, Ct);
                var read = (await store.OneAsync(id, Ct))!;
                await store.UpdateAsync(id, u =>
                {
                    BuilderUpdateCacheEntryRuleTests.Apply(u, kind);
                    if (conditional)
                        u.WithETag(read.ETag!);
                }, Ct);

                var served = (await store.OneAsync(id, Ct))!;
                var fresh = await FreshReadAsync<CacheRuleDoc>(id);
                Assert.Equal(BuilderUpdateCacheEntryRuleTests.Describe(fresh), BuilderUpdateCacheEntryRuleTests.Describe(served));
                Assert.Equal(fresh.ETag, served.ETag);
            });
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = previous;
        }
    }

    // Kept warm on Azurite: the served entity equals a fresh read of the row; its ETag is the row's only
    // when the update was conditional on the cached version.
    [Theory]
    [InlineData("string", false)]
    [InlineData("string", true)]
    [InlineData("enum", true)]
    [InlineData("int", false)]
    [InlineData("bool", true)]
    [InlineData("date", false)]
    [InlineData("date", true)]
    [InlineData("guid", true)]
    [InlineData("several", true)]
    public async Task ABuilderUpdate_OfWholeScalars_KeepsTheEntityWarm_AndItEqualsAFreshReadOfTheRow(string kind, bool conditional)
    {
        SkipUnlessAvailable();
        await InOwnTableAsync(async () =>
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = CachingStore<CacheRuleDoc>(cache);
            var id = Id("k");
            var initial = BuilderUpdateCacheEntryRuleTests.Initial<CacheRuleDoc>();
            initial.Id = id;
            await store.UpsertAsync(initial, Ct);
            var read = (await store.OneAsync(id, Ct))!;
            var returned = await store.UpdateAsync(id, u =>
            {
                BuilderUpdateCacheEntryRuleTests.Apply(u, kind);
                if (conditional)
                    u.WithETag(read.ETag!);
            }, Ct);

            var served = (await store.OneAsync(id, Ct))!;
            var fresh = await FreshReadAsync<CacheRuleDoc>(id);
            Assert.Equal(BuilderUpdateCacheEntryRuleTests.Describe(fresh), BuilderUpdateCacheEntryRuleTests.Describe(served));
            Assert.Equal(conditional ? returned : read.ETag, served.ETag);
            Assert.Equal(returned, fresh.ETag);
        });
    }

    // SERIALIZER-L1: a plain type, a builder update of a cached entity while its constructor fails. As on
    // the published 0.8.3: the merge commits, TargetInvocationException leaves, and the same store then
    // reads the row's content (the pre-update entity is not left cached).
    [Fact]
    public async Task APlainBuilderUpdateOfACachedEntity_WhileTheConstructorFails_LeavesTheSameStoreReadingTheRow()
    {
        SkipUnlessAvailable();
        await InOwnTableAsync(async () =>
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = CachingStore<CacheRuleAzuriteCountingDoc>(cache);
            var id = Id("r");
            await store.CreateAsync(new CacheRuleAzuriteCountingDoc { Id = id, Title = "before" }, Ct);
            _ = await store.OneAsync(id, Ct); // cached
            CacheRuleAzuriteCountingDoc.Failing = true;
            Exception? thrown;
            try
            {
                thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.SetProperty(x => x.Title, "after"), Ct));
            }
            finally
            {
                CacheRuleAzuriteCountingDoc.Failing = false;
            }
            var row = (await Table(tableName).GetEntityAsync<TableEntity>(Pk, "r", cancellationToken: Ct)).Value;
            Assert.Equal("after", row.GetString("Title"));
            Assert.IsType<TargetInvocationException>(thrown); // 0.8.3's exception
            Assert.Equal("after", (await store.OneAsync(id, Ct))!.Title);
        });
    }

    // D1 (as R10 as revised requires): a builder update over a cached plain entity constructs it once when
    // it keeps it warm, and not at all when it evicts.
    [Fact]
    public async Task APlainBuilderUpdate_OverACachedEntity_ConstructsItOnceWhenKeptWarm_AndNeverWhenEvicted()
    {
        SkipUnlessAvailable();
        await InOwnTableAsync(async () =>
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = CachingStore<CacheRuleAzuriteCountingDoc>(cache);
            var id = Id("c");
            await store.CreateAsync(new CacheRuleAzuriteCountingDoc { Id = id, Title = "one" }, Ct);
            _ = await store.OneAsync(id, Ct); // cached

            var before = CacheRuleAzuriteCountingDoc.Constructed;
            await store.UpdateAsync(id, b => b.SetProperty(x => x.Title, "two"), Ct);
            Assert.Equal(1, CacheRuleAzuriteCountingDoc.Constructed - before);
            Assert.Equal("two", (await store.OneAsync(id, Ct))!.Title);

            before = CacheRuleAzuriteCountingDoc.Constructed;
            await store.UpdateAsync(id, b => b.Updates[nameof(CacheRuleAzuriteCountingDoc.Title)] = null!, Ct); // SetProperty refuses null
            Assert.Equal(0, CacheRuleAzuriteCountingDoc.Constructed - before);
            Assert.Equal("two", (await store.OneAsync(id, Ct))!.Title); // the merge wrote no cell for null
        });
    }
}
