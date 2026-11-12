using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the documented limit of <see cref="StorageExtensions.MutateAsync{T}"/> under transport faults,
/// against the local Azurite through the real SDK: when the service applies the strict update but its
/// response is lost, the SDK's own retry of that request gets 412 (the row now carries the version the
/// first attempt produced). Nothing in the response tells that conflict from another writer's, so
/// <c>MutateAsync</c> re-reads the row, which already holds the mutation, and applies it again. A
/// mutation that is not idempotent must guard itself (see the README), or the client must not retry.
/// </summary>
public sealed class MutateAfterSdkRetryTests : AzuriteCleanupTestBase
{
    /// <summary>Lets the first single-entity update reach the service, then loses its response.</summary>
    private sealed class LoseFirstUpdateResponse : HttpPipelinePolicy
    {
        private int fired;

        public int Fired => Volatile.Read(ref fired);

        private bool IsUpdate(HttpMessage message) =>
            (message.Request.Method == RequestMethod.Put || message.Request.Method == RequestMethod.Patch || message.Request.Method.Method == "MERGE")
            && message.Request.Uri.Path.Contains("(PartitionKey=", StringComparison.Ordinal)
            && Interlocked.Exchange(ref fired, 1) == 0;

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            await ProcessNextAsync(message, pipeline);
            if (IsUpdate(message))
                throw new IOException("Simulated: the connection dropped after the service applied the update.");
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            ProcessNext(message, pipeline);
            if (IsUpdate(message))
                throw new IOException("Simulated: the connection dropped after the service applied the update.");
        }
    }

    private TableStorage<SimpleEntity> StoreWith(HttpPipelinePolicy policy, int maxRetries)
    {
        var options = new TableClientOptions();
        options.Retry.Delay = TimeSpan.FromMilliseconds(10);
        options.Retry.MaxRetries = maxRetries;
        options.AddPolicy(policy, HttpPipelinePosition.PerRetry);
        Table(nameof(SimpleEntity));
        return new TableStorage<SimpleEntity>(new TableServiceClient("UseDevelopmentStorage=true", options),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<SimpleEntity>>.Instance,
            authorizer: null, new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled });
    }

    [Fact]
    public async Task Azurite_AnAppliedUpdateWhoseResponseIsLost_IsRetriedByTheSdk_AndTheMutationRunsAgain()
    {
        SkipUnlessAvailable();
        var lose = new LoseFirstUpdateResponse();
        var store = StoreWith(lose, maxRetries: 3);
        await store.EnsureCreatedAsync(Ct);
        await store.CreateAsync(new SimpleEntity { Id = Id("c"), Count = 0 }, Ct);

        var runs = 0;
        await store.MutateAsync(Id("c"), e => { runs++; e.Count++; }, ct: Ct);

        Assert.Equal(1, lose.Fired);
        Assert.Equal(2, runs);
        Assert.Equal(2, (await store.OneAsync(Id("c"), Ct))!.Count);
    }

    /// <summary>The mitigation the README names: with SDK retries off, the lost response surfaces and the mutation ran once.</summary>
    [Fact]
    public async Task Azurite_WithSdkRetriesOff_TheLostResponseSurfaces_AndTheMutationRanOnce()
    {
        SkipUnlessAvailable();
        var lose = new LoseFirstUpdateResponse();
        var store = StoreWith(lose, maxRetries: 0);
        await store.EnsureCreatedAsync(Ct);
        await store.CreateAsync(new SimpleEntity { Id = Id("c"), Count = 0 }, Ct);

        var runs = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => store.MutateAsync(Id("c"), e => { runs++; e.Count++; }, ct: Ct));

        Assert.Equal(1, runs);
        Assert.Equal(1, (await store.OneAsync(Id("c"), Ct))!.Count);
    }
}
