using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// CACHE-3 against a local Azurite, through the real Azure SDK clients
/// (the reviewer's reproduction uses an in-memory table and a blob store that holds a delete; this one
/// shows that the request it holds is a request the real path sends). A pipeline policy holds the first
/// blob DELETE: the request writer A sends after its row has committed, to remove the blob of the version
/// it replaced, and before it puts its own version in the cache. Writer B then writes or deletes the
/// entity and returns; when A's request is let through and A returns, the store answers what the table
/// holds (A's put gives way to B, instead of caching A's older version over B's).
/// Each test works in its own partition <c>lv-{guid}</c> and its own blob container
/// <c>lv-{guid}</c> and deletes both when it ends.
/// </summary>
public sealed class OverlappingWriteCacheAzuriteTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestSupport.AzuriteConnection.ConnectionString;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static readonly Lazy<Task<bool>> Probe =
        new(() => AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, AzuriteProbe.Required));

    private readonly string pk = "lv-" + Guid.NewGuid().ToString("N");
    private readonly string containerName = "lv-" + Guid.NewGuid().ToString("N");
    private readonly HeldRequest held = new();
    private readonly List<MemoryCache> caches = [];

    private TableServiceClient tableService = default!;
    private BlobServiceClient blobService = default!;
    private bool available;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        available = await Probe.Value;
        if (!available) return;
        var blobOptions = new BlobClientOptions();
        blobOptions.AddPolicy(held, HttpPipelinePosition.PerCall);
        tableService = new TableServiceClient(ConnectionString);
        blobService = new BlobServiceClient(ConnectionString, blobOptions);
    }

    public async ValueTask DisposeAsync()
    {
        held.Release();
        foreach (var cache in caches) cache.Dispose();
        if (!available) return;
        var client = tableService.GetTableClient(nameof(SplitTextEntity));
        try
        {
            var filter = TableClient.CreateQueryFilter($"PartitionKey eq {pk}");
            var keys = new List<string>();
            await foreach (var row in client.QueryAsync<TableEntity>(filter, select: ["RowKey"], cancellationToken: CancellationToken.None))
                keys.Add(row.RowKey);
            foreach (var rk in keys)
                await client.DeleteEntityAsync(pk, rk, ETag.All, CancellationToken.None);
        }
        catch (RequestFailedException)
        {
            // The table was never created, or a row is already gone.
        }
        await blobService.GetBlobContainerClient(containerName).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
    }

    private TableStorage<SplitTextEntity> Store()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        return new TableStorage<SplitTextEntity>(tableService, cache, NullLogger<TableStorage<SplitTextEntity>>.Instance,
            authorizer: null,
            new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { ContainerName = containerName, ThresholdBytes = 1_000 } },
            blobService);
    }

    private SplitTextEntity Doc(int version) => new() { Id = pk + "|r1", Title = "v" + version, Body = Ascii(40_000, seed: version) };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeValueWrite_WhoseBlobCleanupIsSlow_DoesNotOverwriteTheCacheEntryOfALaterWriteOrDelete(bool laterIsADelete)
    {
        Assert.SkipUnless(available, "Azurite (tables and blobs) is not reachable");
        var id = pk + "|r1";
        var store = Store();
        await store.CreateAsync(Doc(1), Ct);

        // A's cleanup: the DELETE of the blob of version 1, sent after A's row committed.
        var firstBlob = Sha(Utf8(Doc(1).Body!));
        held.Arm(m => m.Request.Method == RequestMethod.Delete && m.Request.Uri.Path.EndsWith(firstBlob, StringComparison.Ordinal));
        var writerA = store.UpsertAsync(Doc(2), Ct);
        var reached = await Task.WhenAny(held.Reached, writerA).WaitAsync(Patience, Ct);
        Assert.True(reached == held.Reached, "A returned without sending the blob delete the test meant to hold.");
        var rows = tableService.GetTableClient(nameof(SplitTextEntity));
        Assert.Equal("v2", (await rows.GetEntityAsync<TableEntity>(pk, "r1", cancellationToken: Ct)).Value.GetString("Title"));

        // B starts after A's row committed, and returns.
        if (laterIsADelete)
            await store.DeleteAsync(id, Ct);
        else
            await store.UpsertAsync(Doc(3), Ct);

        held.Release();
        await writerA.WaitAsync(Patience, Ct);

        // Both calls have returned. The store must answer what the table holds.
        var stored = await rows.GetEntityIfExistsAsync<TableEntity>(pk, "r1", cancellationToken: Ct);
        var read = await store.OneAsync(id, Ct);
        if (laterIsADelete)
        {
            Assert.False(stored.HasValue);
            Assert.Null(read);
            Assert.False(await store.ExistsAsync(id, Ct));
        }
        else
        {
            Assert.Equal("v3", stored.Value!.GetString("Title"));
            Assert.Equal("v3", read!.Title);
        }
    }

    /// <summary>Holds the first request that matches, before it is sent, until released.</summary>
    private sealed class HeldRequest : HttpPipelinePolicy
    {
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<HttpMessage, bool>? matches;
        private int armed;

        /// <summary>Completes when a request is being held.</summary>
        public Task Reached => reached.Task;

        public void Arm(Func<HttpMessage, bool> matches)
        {
            this.matches = matches;
            Volatile.Write(ref armed, 1);
        }

        public void Release() => release.TrySetResult();

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (Volatile.Read(ref armed) == 1 && matches!(message) && Interlocked.Exchange(ref armed, 0) == 1)
            {
                reached.TrySetResult();
                await release.Task;
            }
            await ProcessNextAsync(message, pipeline);
        }

        // The stores under test are asynchronous; a synchronous call is not held.
        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
            ProcessNext(message, pipeline);
    }
}
