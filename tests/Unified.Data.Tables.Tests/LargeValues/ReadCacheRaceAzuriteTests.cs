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
/// The read-cache race of <see cref="ReadCacheRaceTests"/> against a LOCAL Azurite, through the real Azure
/// SDK clients: a pipeline policy holds one request of a read (the blob download before it is sent, or the
/// continuation-row query once its response has arrived) while a write or delete commits through the same
/// store. Each test works in its own partition <c>lv-{guid}</c> and its own blob container
/// <c>lv-{guid}</c> and deletes both when it ends; the tests skip only through <see cref="AzuriteProbe"/>.
/// </summary>
public sealed class ReadCacheRaceAzuriteTests : IAsyncLifetime
{
    private const string SkipReason = "Azurite (tables and blobs) is not reachable; set " + TestSupport.AzuriteConnection.Variable + " or start Azurite";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static readonly string ConnectionString = TestSupport.AzuriteConnection.ConnectionString;

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
        var tableOptions = new TableClientOptions();
        tableOptions.AddPolicy(held, HttpPipelinePosition.PerCall);
        var blobOptions = new BlobClientOptions();
        blobOptions.AddPolicy(held, HttpPipelinePosition.PerCall);
        tableService = new TableServiceClient(ConnectionString, tableOptions);
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

    private TableStorage<SplitTextEntity> Store(BlobOffloadOptions offload)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        return new TableStorage<SplitTextEntity>(tableService, cache, NullLogger<TableStorage<SplitTextEntity>>.Instance,
            authorizer: null, new UnifiedTableStorageOptions { BlobOffload = offload }, blobService);
    }

    // "blob": 40,000 characters over a 1,000-byte threshold, one blob. "rows": 600,000 characters under the
    // default threshold, a primary row and a continuation row.
    private static string Body(string stored, int version) =>
        stored == "blob" ? Ascii(40_000, seed: version) : new string((char)('a' + version), 600_000);

    [Theory]
    [InlineData("blob", false)]
    [InlineData("blob", true)]
    [InlineData("rows", false)]
    [InlineData("rows", true)]
    public async Task PointRead_ThatRacedAWriteOrDelete_LeavesNothingOlderInTheCache(string stored, bool delete)
    {
        Assert.SkipUnless(available, SkipReason);
        var offload = new BlobOffloadOptions { ContainerName = containerName };
        if (stored == "blob")
            offload.ThresholdBytes = 1_000;
        var id = pk + "|r1";
        var first = Body(stored, 1);
        // Written through another store, so the store under test has not cached it.
        await Store(offload).CreateAsync(new SplitTextEntity { Id = id, Title = "v1", Body = first }, Ct);
        var store = Store(offload);

        // The read has the primary row; what it still needs is held: the download of the first version's
        // blob (not sent yet), or the response of the continuation-row query (arrived, not delivered).
        if (stored == "blob")
            held.Arm(beforeSending: true, m => m.Request.Method == RequestMethod.Get
                && m.Request.Uri.Path.EndsWith(Sha(Utf8(first)), StringComparison.Ordinal));
        else
            held.Arm(beforeSending: false, m => m.Request.Method == RequestMethod.Get
                && Uri.UnescapeDataString(m.Request.Uri.Query) is var query
                && query.Contains("_SplitOf eq", StringComparison.Ordinal)
                && !query.Contains("$select", StringComparison.Ordinal));
        var reading = store.OneAsync(id, Ct);
        var reached = await Task.WhenAny(held.Reached, reading).WaitAsync(Patience, Ct);
        Assert.True(reached == held.Reached, "The read finished without sending the request the test meant to hold.");

        if (delete)
            await store.DeleteAsync(id, Ct);
        else
            await store.UpsertAsync(new SplitTextEntity { Id = id, Title = "v2", Body = Body(stored, 2) }, Ct);
        held.Release();
        Assert.Equal("v1", (await reading.WaitAsync(Patience, Ct))!.Title);

        var next = await store.OneAsync(id, Ct);
        if (delete)
        {
            Assert.Null(next);
            Assert.False(await store.ExistsAsync(id, Ct));
            Assert.Empty(await store.QueryAsync(pk, Ct));
            return;
        }

        Assert.Equal("v2", next!.Title);
        Assert.Equal(Body(stored, 2), next.Body, StringComparer.Ordinal);
        var row = await tableService.GetTableClient(nameof(SplitTextEntity)).GetEntityAsync<TableEntity>(pk, "r1", cancellationToken: Ct);
        Assert.Equal(row.Value.ETag.ToString(), next.ETag);
        Assert.True(await store.ExistsAsync(id, Ct));
        Assert.Equal("v2", Assert.Single(await store.QueryAsync(pk, Ct)).Title);
    }

    /// <summary>
    /// Holds the first request that matches, until released: before it is sent, or once its response has
    /// arrived and before the caller receives it.
    /// </summary>
    private sealed class HeldRequest : HttpPipelinePolicy
    {
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<HttpMessage, bool>? matches;
        private bool beforeSending;
        private int armed;

        /// <summary>Completes when a request is being held.</summary>
        public Task Reached => reached.Task;

        public void Arm(bool beforeSending, Func<HttpMessage, bool> matches)
        {
            this.beforeSending = beforeSending;
            this.matches = matches;
            Volatile.Write(ref armed, 1);
        }

        public void Release() => release.TrySetResult();

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            var hold = Volatile.Read(ref armed) == 1 && matches!(message) && Interlocked.Exchange(ref armed, 0) == 1;
            if (hold && beforeSending)
                await HoldAsync();
            await ProcessNextAsync(message, pipeline);
            if (hold && !beforeSending)
                await HoldAsync();
        }

        // The stores under test are asynchronous; a synchronous call is not held.
        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
            ProcessNext(message, pipeline);

        private Task HoldAsync()
        {
            reached.TrySetResult();
            return release.Task;
        }
    }
}
