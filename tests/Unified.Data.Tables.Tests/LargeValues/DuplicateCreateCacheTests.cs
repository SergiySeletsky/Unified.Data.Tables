using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Why a create that ends in 409 (<see cref="DuplicateKeyException"/>) evicts the entity's entry and its
/// partition's cached lists, at the cost of one more read: a 409 does not prove that this call wrote nothing.
/// The SDK retries an insert whose response was lost (a dropped connection, a timeout after the request was
/// sent); when the first attempt committed, the retry meets the row it wrote and answers 409. Without the
/// eviction the partition list cached before the create would keep answering without the row this call
/// inserted (release-readiness note: "a failed CreateAsync (409) evicts the cached entry and the
/// partition's cached queries").
/// </summary>
public sealed class DuplicateCreateCacheTests : AzuriteCleanupTestBase
{
    /// <summary>Lets the first insert reach the table, then fails its response, so the SDK sends it again.</summary>
    private sealed class LoseTheFirstInsertResponse : HttpPipelinePolicy
    {
        private int inserts;

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            ProcessNext(message, pipeline);
            ThrowOnTheFirstInsert(message);
        }

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            await ProcessNextAsync(message, pipeline);
            ThrowOnTheFirstInsert(message);
        }

        private void ThrowOnTheFirstInsert(HttpMessage message)
        {
            var isInsert = message.Request.Method == RequestMethod.Post
                && !message.Request.Uri.Path.EndsWith("/Tables", StringComparison.OrdinalIgnoreCase)
                && !message.Request.Uri.Path.Contains("$batch", StringComparison.Ordinal);
            if (isInsert && Interlocked.Increment(ref inserts) == 1)
                throw new IOException("the connection dropped after the insert was sent");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACreate_WhoseRetryMeetsItsOwnRow_LeavesNoStaleListCached(bool largeValues)
    {
        SkipUnlessAvailable();
        var tableName = "dcc" + Guid.NewGuid().ToString("N");
        var options = new TableClientOptions();
        options.Retry.Delay = TimeSpan.FromMilliseconds(10);
        options.AddPolicy(new LoseTheFirstInsertResponse(), HttpPipelinePosition.PerRetry);
        var service = new TableServiceClient(Environment.GetEnvironmentVariable(AzuriteLargeValueIntegrationTests.ConnectionStringVariable)
            is { Length: > 0 } configured ? configured : "UseDevelopmentStorage=true", options);
        try
        {
            await service.CreateTableIfNotExistsAsync(tableName, Ct);
            if (largeValues)
                await RunAsync<RaceDoc>(service, tableName);
            else
                await RunAsync<RacePlainDoc>(service, tableName);
        }
        finally
        {
            await TableService.DeleteTableAsync(tableName, CancellationToken.None);
        }
    }

    private async Task RunAsync<T>(TableServiceClient service, string tableName) where T : Entity, IRaceDoc, new()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null,
            new UnifiedTableStorageOptions { TableNameResolver = _ => tableName });
        var id = Id("a");
        Assert.Empty(await store.QueryAsync(Pk, Ct)); // the partition's (empty) list is cached
        Assert.Null(await store.OneAsync(id, Ct));

        // The first attempt commits, its response is lost, the retry meets the row: 409.
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new T { Id = id, Body = "mine", Version = 1 }, Ct));

        Assert.Equal("mine", Assert.Single(await store.QueryAsync(Pk, Ct)).Body);
        Assert.Equal("mine", (await store.OneAsync(id, Ct))!.Body);
    }
}
