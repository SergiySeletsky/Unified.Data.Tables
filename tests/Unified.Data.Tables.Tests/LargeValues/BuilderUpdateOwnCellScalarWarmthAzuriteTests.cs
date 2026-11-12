using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// CACHE-1 and CACHE-3 of the review of fix wave 5 against Azurite, with the service's own cells and ETags: a
/// builder update of every scalar type the serializer supports keeps the cached entity warm exactly when the
/// service returns the merge's cell as a value equal to the one set (no point read follows, and the entity kept
/// equals a fresh read of the row); any other value evicts and the next read fetches the row. A cache whose
/// lookup throws never prevents the merge. Each test uses its own partition and container, deleted at the end.
/// </summary>
public sealed class BuilderUpdateOwnCellScalarWarmthAzuriteTests : AzuriteCleanupTestBase
{
    private const string TableName = "OwnCellScalarWarmth";

    // Counts the point reads (GET of one entity) the store sends.
    private sealed class PointReadCounter : HttpPipelinePolicy
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            Note(message);
            ProcessNext(message, pipeline);
        }

        public override ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            Note(message);
            return ProcessNextAsync(message, pipeline);
        }

        private void Note(HttpMessage message)
        {
            if (message.Request.Method == RequestMethod.Get
                && message.Request.Uri.Path.Contains("(PartitionKey=", StringComparison.Ordinal))
                Interlocked.Increment(ref count);
        }
    }

    private TableStorage<T> CachingStore<T>(IMemoryCache cache, TableServiceClient? service = null) where T : class, IEntity, new()
    {
        Table(TableName);
        var options = new UnifiedTableStorageOptions
        {
            TableNameResolver = _ => TableName,
            BlobOffload = new BlobOffloadOptions { ContainerName = ContainerName, ThresholdBytes = SmallThreshold },
        };
        return new TableStorage<T>(service ?? TableService, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, options, BlobService);
    }

    public static TheoryData<string, bool, bool> Matrix() => OwnCellScalarCases.Matrix(azurite: true);

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task ABuilderUpdate_KeepsTheEntityWarm_ExactlyWhenTheCellReadsBackEqual_AndItEqualsAFreshRead(string kind, bool largeValues, bool conditional)
    {
        SkipUnlessAvailable();
        if (largeValues)
            await RunAsync<OwnCellScalarSplitDoc>(kind, conditional);
        else
            await RunAsync<OwnCellScalarDoc>(kind, conditional);
    }

    private async Task RunAsync<T>(string kind, bool conditional) where T : OwnCellScalarDocBase, new()
    {
        var (property, value, warm, _) = OwnCellScalarCases.All[kind];
        var counter = new PointReadCounter();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = CachingStore<T>(cache, TableServiceWith(counter));
        var id = Id(largeValues(typeof(T)) + "-" + Guid.NewGuid().ToString("N"));
        await store.UpsertAsync(new T { Id = id }, Ct);
        var read = (await store.OneAsync(id, Ct))!;

        var returned = await store.UpdateAsync(id, u =>
        {
            u.Updates[property] = value;
            if (conditional)
                u.WithETag(read.ETag!);
        }, Ct);

        var before = counter.Count;
        var served = (await store.OneAsync(id, Ct))!;
        var pointReads = counter.Count - before;

        using var freshCache = new MemoryCache(new MemoryCacheOptions());
        var fresh = (await CachingStore<T>(freshCache).OneAsync(id, Ct))!;
        var p = OwnCellScalarCases.Property(typeof(T), property);
        Assert.True(Equals(p.GetValue(fresh), p.GetValue(served)),
            $"{kind}: served {OwnCellScalarCases.Show(p.GetValue(served))}, fresh {OwnCellScalarCases.Show(p.GetValue(fresh))}");
        Assert.Equal(OwnCellScalarCases.Describe(fresh), OwnCellScalarCases.Describe(served));
        Assert.Equal(warm ? 0 : 1, pointReads);
        if (warm)
        {
            Assert.True(Equals(value, p.GetValue(served)), $"{kind}: served {OwnCellScalarCases.Show(p.GetValue(served))}, set {OwnCellScalarCases.Show(value)}");
            Assert.Equal(conditional ? returned : read.ETag, served.ETag);
        }
        else
        {
            Assert.Equal(fresh.ETag, served.ETag);
        }
        Assert.Equal(returned, fresh.ETag);

        static string largeValues(Type t) => t == typeof(OwnCellScalarSplitDoc) ? "s" : "p";
    }

    // CACHE-3 on Azurite: a cache whose lookup throws never prevents the merge; the entity from before the
    // merge is not left cached.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABuilderUpdate_OverACacheWhoseLookupThrows_SendsTheMerge_AndLeavesNoEntityFromBeforeIt(bool largeValues)
    {
        SkipUnlessAvailable();
        if (largeValues)
            await RunLookupThrowsAsync<OwnCellScalarSplitDoc>();
        else
            await RunLookupThrowsAsync<OwnCellScalarDoc>();
    }

    private async Task RunLookupThrowsAsync<T>() where T : OwnCellScalarDocBase, new()
    {
        using var inner = new MemoryCache(new MemoryCacheOptions());
        var cache = new LookupFailingCache(inner);
        var store = CachingStore<T>(cache);
        var id = Id("lookup-" + Guid.NewGuid().ToString("N"));
        await store.UpsertAsync(new T { Id = id, Text = "n" }, Ct);
        _ = await store.OneAsync(id, Ct);
        cache.Armed = true;

        await store.UpdateAsync(id, u => u.SetProperty(x => x.Text, "m"), Ct);

        cache.Armed = false;
        using var freshCache = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal("m", (await CachingStore<T>(freshCache).OneAsync(id, Ct))!.Text);
        Assert.Equal("m", (await store.OneAsync(id, Ct))!.Text);
    }
}
