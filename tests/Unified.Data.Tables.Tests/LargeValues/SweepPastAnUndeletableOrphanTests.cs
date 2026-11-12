using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// One orphan whose delete fails (an error other than "gone" or "modified since it was listed", such as
/// an immutability policy, a permission or a service error) must not stop the sweep of the partition's
/// other orphans: every orphan is tried, and the failures are thrown together afterwards as an
/// <see cref="AggregateException"/> whose message says how many blobs were deleted.
/// </summary>
public class SweepPastAnUndeletableOrphanTests
{
    private const string Pk = "p";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UnifiedTableStorageOptions Offload() => new()
    {
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 },
    };

    private static string Seed(InMemoryOffloadBlobStore blobs, string pk, string rk)
    {
        var bytes = Utf8(pk + "/" + rk);
        var name = ValueOffloadCoordinator.BlobName(nameof(SplitTextEntity), pk, rk, "Body", Sha(bytes));
        blobs.Seed(name, bytes, Sha(bytes), DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
        return name;
    }

    private static InMemoryStorage<SplitTextEntity> FailingFirst(out string stuck, out string[] others)
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Offload());
        // Listed in ordinal order: "a" is the first orphan the sweep tries.
        var a = Seed(store.Blobs, Pk, "a");
        others = [Seed(store.Blobs, Pk, "b"), Seed(store.Blobs, Pk, "c")];
        stuck = a;
        store.Blobs.BeforeDelete = (name, _) => name == a
            ? throw new InvalidOperationException("this blob is under an immutability policy")
            : Task.CompletedTask;
        return store;
    }

    [Fact]
    public async Task InMemory_PartitionSweep_DeletesTheOtherOrphans_ThenThrowsTheFailure()
    {
        var store = FailingFirst(out var stuck, out var others);

        var ex = await Assert.ThrowsAsync<AggregateException>(() => store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.IsType<InvalidOperationException>(Assert.Single(ex.InnerExceptions));
        Assert.Contains("deleted 2 blob(s)", ex.Message, StringComparison.Ordinal);
        Assert.Equal([stuck], store.Blobs.Names);
        Assert.All(others, o => Assert.DoesNotContain(o, store.Blobs.Names));
    }

    [Fact]
    public async Task InMemory_TableSweep_SweepsEveryPartition_AndCountsTheFailingPartitionsDeletes()
    {
        var store = FailingFirst(out var stuck, out _);
        Seed(store.Blobs, "q", "a");

        var ex = await Assert.ThrowsAsync<AggregateException>(() => store.SweepOrphanBlobsAsync(TimeSpan.FromMinutes(5), Ct));

        // One inner exception per failed partition; the count includes what the failed partition deleted.
        var partition = Assert.IsType<AggregateException>(Assert.Single(ex.InnerExceptions));
        Assert.IsType<InvalidOperationException>(Assert.Single(partition.InnerExceptions));
        Assert.Contains("deleted 3 blob(s)", ex.Message, StringComparison.Ordinal);
        Assert.Equal([stuck], store.Blobs.Names);
    }

    [Fact]
    public async Task InMemory_CallerCancellation_DuringADelete_EndsTheSweep()
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Offload());
        var a = Seed(store.Blobs, Pk, "a");
        var b = Seed(store.Blobs, Pk, "b");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        store.Blobs.BeforeDelete = async (name, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), cts.Token));

        Assert.Contains(a, store.Blobs.Names);
        Assert.Contains(b, store.Blobs.Names);
    }

    // ── TableStorage over a stubbed table ─────────────────────────────────

    [Fact]
    public async Task TableStorage_PartitionSweep_DeletesTheOtherOrphans_ThenThrowsTheFailure()
    {
        var inner = new InMemoryOffloadBlobStore();
        var stuck = Seed(inner, Pk, "a");
        var others = new[] { Seed(inner, Pk, "b"), Seed(inner, Pk, "c") };
        var blobs = new RecordingBlobStore([], inner)
        {
            BeforeDelete = name => name == stuck
                ? throw new RequestFailedException(409, "This operation is not permitted as the blob is immutable due to a policy.", "BlobImmutableDueToPolicy", null)
                : Task.CompletedTask,
        };
        using var h = new StorageHarness<SplitTextEntity>(options: Offload(), blobs: blobs);
        h.SetupQueryByFilter();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => h.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.Equal(409, Assert.IsType<RequestFailedException>(Assert.Single(ex.InnerExceptions)).Status);
        Assert.Contains("deleted 2 blob(s)", ex.Message, StringComparison.Ordinal);
        Assert.Equal([stuck], inner.Names);
        Assert.All(others, o => Assert.DoesNotContain(o, inner.Names));
    }
}

/// <summary>
/// <see cref="SweepPastAnUndeletableOrphanTests"/> against a LOCAL Azurite through the real Blobs SDK:
/// the delete of one orphan is failed by a pipeline policy with a 409, as an immutability policy would.
/// </summary>
public sealed class SweepPastAnUndeletableOrphanAzuriteTests : AzuriteCleanupTestBase
{
    /// <summary>Fails every DELETE of a blob whose path contains <paramref name="marker"/> with a 409.</summary>
    private sealed class FailDeletesOf(string marker) : HttpPipelinePolicy
    {
        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            Fail(message);
            ProcessNext(message, pipeline);
        }

        public override ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            Fail(message);
            return ProcessNextAsync(message, pipeline);
        }

        private void Fail(HttpMessage message)
        {
            if (message.Request.Method == RequestMethod.Delete
                && message.Request.Uri.ToUri().AbsolutePath.Contains(marker, StringComparison.Ordinal))
                throw new RequestFailedException(409, "This operation is not permitted as the blob is immutable due to a policy.", "BlobImmutableDueToPolicy", null);
        }
    }

    private async Task<string> UploadOrphanAsync(string rk)
    {
        var bytes = Utf8(Pk + "/" + rk);
        var sha = Sha(bytes);
        var name = ValueOffloadCoordinator.BlobName(nameof(SplitTextEntity), Pk, rk, "Body", sha);
        await Container.CreateIfNotExistsAsync(cancellationToken: Ct);
        await using var content = new MemoryStream(bytes);
        await new AzureOffloadBlobStore(Container).UploadCreateOnlyAsync(
            name, content, new BlobReference(name, sha, bytes.LongLength, "application/octet-stream", "binary"), null, Ct);
        return name;
    }

    private TableStorage<SplitTextEntity> FailingStore()
    {
        var options = new BlobClientOptions();
        options.AddPolicy(new FailDeletesOf("/" + Pk + "/a/Body/"), HttpPipelinePosition.PerCall);
        var blobService = new BlobServiceClient(
            Environment.GetEnvironmentVariable(AzuriteLargeValueIntegrationTests.ConnectionStringVariable) is { Length: > 0 } configured
                ? configured
                : "UseDevelopmentStorage=true",
            options);
        Table(nameof(SplitTextEntity)); // this test's rows in it are deleted at the end
        return new TableStorage<SplitTextEntity>(TableService, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TableStorage<SplitTextEntity>>.Instance, authorizer: null,
            new UnifiedTableStorageOptions
            {
                BlobOffload = new BlobOffloadOptions { ContainerName = ContainerName, ThresholdBytes = SmallThreshold },
                Cache = CachePolicy.Disabled,
            },
            blobService);
    }

    [Fact]
    public async Task PartitionSweep_DeletesTheOtherOrphans_ThenThrowsTheFailure()
    {
        SkipUnlessAvailable();
        var stuck = await UploadOrphanAsync("a");
        await UploadOrphanAsync("b");
        await UploadOrphanAsync("c");
        await BlobsOlderThanAsync(TimeSpan.Zero);
        var store = FailingStore();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => store.SweepOrphanBlobsAsync(Pk, TimeSpan.Zero, Ct));

        Assert.Equal(409, Assert.IsType<RequestFailedException>(Assert.Single(ex.InnerExceptions)).Status);
        Assert.Contains("deleted 2 blob(s)", ex.Message, StringComparison.Ordinal);
        Assert.Equal([stuck], await BlobNamesAsync());
    }

    [Fact]
    public async Task TableSweep_DeletesTheOtherOrphans_ThenThrowsTheFailure()
    {
        SkipUnlessAvailable();
        var stuck = await UploadOrphanAsync("a");
        await UploadOrphanAsync("b");
        await BlobsOlderThanAsync(TimeSpan.Zero);
        var store = FailingStore();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => store.SweepOrphanBlobsAsync(TimeSpan.Zero, Ct));

        var partition = Assert.IsType<AggregateException>(Assert.Single(ex.InnerExceptions));
        Assert.Equal(409, Assert.IsType<RequestFailedException>(Assert.Single(partition.InnerExceptions)).Status);
        Assert.Contains("deleted 1 blob(s)", ex.Message, StringComparison.Ordinal);
        Assert.Equal([stuck], await BlobNamesAsync());
    }
}
