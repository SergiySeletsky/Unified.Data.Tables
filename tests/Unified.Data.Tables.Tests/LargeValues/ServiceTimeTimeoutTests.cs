using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The cleanup after a committed delete asks the blob store for its service time. When that request is
/// cancelled although the caller did not cancel (an SDK or network timeout surfaces as a
/// <see cref="TaskCanceledException"/>), the time is unknown, not the call cancelled: the cleanup falls
/// back to the client clock less <see cref="LargeValueTable.ClientClockMargin"/>, and the committed
/// delete is never reported as failed.
/// </summary>
public class ServiceTimeTimeoutTests
{
    private const string Pk = "p";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UnifiedTableStorageOptions Offload() => new()
    {
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000, CleanupGrace = TimeSpan.FromMinutes(5) },
        Cache = CachePolicy.Disabled,
    };

    private static DateTimeOffset? TimesOut() => throw new TaskCanceledException("The operation was cancelled because it exceeded the configured timeout.");

    [Fact]
    public async Task PartitionDelete_ServiceTimeTimesOut_CommitsAndCleansUpOnTheClientClock()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var now = DateTimeOffset.UtcNow;
        var fresh = $"{nameof(SplitTextEntity)}/{Pk}/a/Body/{Sha(Utf8("a"))}";
        var old = $"{nameof(SplitTextEntity)}/{Pk}/b/Body/{Sha(Utf8("b"))}";
        // Fresh: inside grace + margin on the client clock. Old: beyond both.
        blobs.Seed(fresh, Utf8("a"), Sha(Utf8("a")), now - TimeSpan.FromMinutes(8));
        blobs.Seed(old, Utf8("b"), Sha(Utf8("b")), now - TimeSpan.FromMinutes(30));
        var recording = new RecordingBlobStore([], blobs) { ServiceTime = TimesOut };
        using var h = new StorageHarness<SplitTextEntity>(options: Offload(), blobs: recording);
        h.SetupQueryByFilter();
        h.SetupTransaction();

        Assert.Equal(0, await h.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal([fresh], blobs.Names);
        Assert.Equal(1, recording.ServiceTimeCalls);
    }

    [Fact]
    public async Task Sweep_ServiceTimeTimesOut_MeasuresAgeOnTheClientClock()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var now = DateTimeOffset.UtcNow;
        var fresh = $"{nameof(SplitTextEntity)}/{Pk}/a/Body/{Sha(Utf8("a"))}";
        var old = $"{nameof(SplitTextEntity)}/{Pk}/b/Body/{Sha(Utf8("b"))}";
        blobs.Seed(fresh, Utf8("a"), Sha(Utf8("a")), now - TimeSpan.FromMinutes(12));
        blobs.Seed(old, Utf8("b"), Sha(Utf8("b")), now - TimeSpan.FromMinutes(30));
        var recording = new RecordingBlobStore([], blobs) { ServiceTime = TimesOut };
        using var h = new StorageHarness<SplitTextEntity>(options: Offload(), blobs: recording);
        h.SetupQueryByFilter();

        Assert.Equal(1, await h.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(10), Ct));

        Assert.Equal([fresh], blobs.Names);
    }

    [Fact]
    public async Task CallerCancellation_OfTheServiceTime_StillEndsTheSweep()
    {
        var blobs = new InMemoryOffloadBlobStore();
        using var cts = new CancellationTokenSource();
        var recording = new RecordingBlobStore([], blobs)
        {
            ServiceTime = () =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        };
        using var h = new StorageHarness<SplitTextEntity>(options: Offload(), blobs: recording);
        h.SetupQueryByFilter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(10), cts.Token));
    }
}

/// <summary>
/// <see cref="ServiceTimeTimeoutTests"/> against a LOCAL Azurite: a pipeline policy times out the
/// container-properties request the service time is read from.
/// </summary>
public sealed class ServiceTimeTimeoutAzuriteTests : AzuriteCleanupTestBase
{
    /// <summary>Throws a timeout's <see cref="TaskCanceledException"/> for every container-properties request.</summary>
    private sealed class TimeOutContainerProperties : HttpPipelinePolicy
    {
        public int TimedOut { get; private set; }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            Check(message);
            ProcessNext(message, pipeline);
        }

        public override ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            Check(message);
            return ProcessNextAsync(message, pipeline);
        }

        private void Check(HttpMessage message)
        {
            var query = message.Request.Uri.Query;
            if (message.Request.Method == RequestMethod.Get
                && query.Contains("restype=container", StringComparison.Ordinal)
                && !query.Contains("comp=", StringComparison.Ordinal))
            {
                TimedOut++;
                throw new TaskCanceledException("The operation was cancelled because it exceeded the configured timeout.");
            }
        }
    }

    [Fact]
    public async Task PartitionDelete_ServiceTimeTimesOut_IsNotReportedAsFailed()
    {
        SkipUnlessAvailable();
        await Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("r"), Body = Ascii(40_000, seed: 9) }, Ct);
        var policy = new TimeOutContainerProperties();
        var options = new BlobClientOptions();
        options.AddPolicy(policy, HttpPipelinePosition.PerCall);
        var blobService = new BlobServiceClient(
            Environment.GetEnvironmentVariable(AzuriteLargeValueIntegrationTests.ConnectionStringVariable) is { Length: > 0 } configured
                ? configured
                : "UseDevelopmentStorage=true",
            options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TableStorage<SplitTextEntity>(TableService, cache, NullLogger<TableStorage<SplitTextEntity>>.Instance, authorizer: null,
            new UnifiedTableStorageOptions
            {
                BlobOffload = new BlobOffloadOptions { ContainerName = ContainerName, ThresholdBytes = SmallThreshold },
                Cache = CachePolicy.Disabled,
            },
            blobService);

        Assert.Equal(1, await store.DeletePartitionAsync(Pk, Ct));

        Assert.True(policy.TimedOut > 0);
        Assert.Empty(await RawRowsAsync(nameof(SplitTextEntity)));
        // A fresh blob is within the client clock's margin: kept for the orphan sweep.
        Assert.Single(await BlobNamesAsync());
    }
}
