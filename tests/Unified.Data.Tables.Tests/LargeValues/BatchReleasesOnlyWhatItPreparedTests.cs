using Azure.Core;
using Azure.Core.Pipeline;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A batch releases exactly the contents it prepared (fix wave 3, stage x1, item 3; the concern of stage
/// b1). When the prepare of one entity of a batch fails, the entities after it were never prepared:
/// releasing them as well counted out another write of a content they share, whose temporary copy of a
/// forward-only source was then deleted under it, so that write was refused although nothing of it had
/// failed. Here a write of an entity with two files is paused while it uploads its first file; meanwhile a
/// batch fails in the prepare of its second entity, and its third entity holds the other write's second,
/// forward-only file. The other write must store that file whole.
/// </summary>
public class BatchReleasesOnlyWhatItPreparedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static Stream ForwardOnly(byte[] data) => new TurnTakingForwardOnlyStream(data);

    internal static TableBlobContent Failing() =>
        TableBlobContent.FromStream(new FailingStream(), "application/octet-stream");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABatchThatFailsInPrepare_DoesNotCountOutAnotherWriteOfAContentOfAnEntityItNeverPrepared(bool upsert)
    {
        var store = new InMemoryStorage<TwoBlobEntity>(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });
        var data = RandomBytes(300_000, seed: 31);
        var shared = TableBlobContent.FromStream(ForwardOnly(data), "application/octet-stream");
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Blobs.BeforeUpload = async (name, _) =>
        {
            if (name.Contains("/w/First/", StringComparison.Ordinal))
            {
                paused.TrySetResult();
                await resume.Task;
            }
        };

        var write = Task.Run(() => store.CreateAsync(
            new TwoBlobEntity { Id = "p|w", First = TableBlobContent.FromBytes(RandomBytes(1_000), "application/octet-stream"), Second = shared }, Ct), Ct);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        List<TwoBlobEntity> batch =
        [
            new() { Id = "p|b1" },
            new() { Id = "p|b2", First = Failing() },
            new() { Id = "p|b3", Second = shared },
        ];
        var batchFailure = await Record.ExceptionAsync(() => upsert ? store.UpsertBatchAsync(batch, Ct) : store.CreateBatchAsync(batch, Ct));
        resume.SetResult();

        Assert.IsType<IOException>(batchFailure);
        var written = await Record.ExceptionAsync(() => write);
        Assert.True(written is null, $"The other write was refused: {written?.GetType().Name}: {written?.Message}");
        var read = await store.OneAsync("p|w", Ct);
        await using var stream = await read!.Second!.OpenReadAsync(Ct);
        Assert.Equal(data, await ReadAllAsync(stream));
        Assert.Null(await store.OneAsync("p|b1", Ct));
    }

    [Fact]
    public async Task ABatchCancelledInPrepare_DoesNotCountOutAnotherWriteOfAContentOfAnEntityItNeverPrepared()
    {
        var store = new InMemoryStorage<TwoBlobEntity>(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });
        var data = RandomBytes(300_000, seed: 33);
        var shared = TableBlobContent.FromStream(ForwardOnly(data), "application/octet-stream");
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Blobs.BeforeUpload = async (name, _) =>
        {
            if (name.Contains("/w/First/", StringComparison.Ordinal))
            {
                paused.TrySetResult();
                await resume.Task;
            }
        };
        var write = Task.Run(() => store.CreateAsync(
            new TwoBlobEntity { Id = "p|w", First = TableBlobContent.FromBytes(RandomBytes(1_000), "application/octet-stream"), Second = shared }, Ct), Ct);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        using var cancel = new CancellationTokenSource();
        var hanging = new HangingStream();
        List<TwoBlobEntity> batch =
        [
            new() { Id = "p|b1" },
            new() { Id = "p|b2", First = TableBlobContent.FromStream(hanging, "application/octet-stream") },
            new() { Id = "p|b3", Second = shared },
        ];
        var running = store.CreateBatchAsync(batch, cancel.Token);
        await hanging.Reading.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await cancel.CancelAsync();
        var batchFailure = await Record.ExceptionAsync(() => running);
        resume.SetResult();

        Assert.IsAssignableFrom<OperationCanceledException>(batchFailure);
        var written = await Record.ExceptionAsync(() => write);
        Assert.True(written is null, $"The other write was refused: {written?.GetType().Name}: {written?.Message}");
        var read = await store.OneAsync("p|w", Ct);
        await using var stream = await read!.Second!.OpenReadAsync(Ct);
        Assert.Equal(data, await ReadAllAsync(stream));
    }

    /// <summary>A seekable source whose read waits until it is cancelled.</summary>
    internal sealed class HangingStream : MemoryStream
    {
        public HangingStream()
            : base(new byte[64])
        {
        }

        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    /// <summary>A seekable source whose every read fails: its prepare fails.</summary>
    internal sealed class FailingStream : MemoryStream
    {
        public FailingStream()
            : base(new byte[64])
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("simulated source failure");

        public override int Read(Span<byte> buffer) => throw new IOException("simulated source failure");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("simulated source failure");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("simulated source failure");
    }
}

/// <summary><see cref="BatchReleasesOnlyWhatItPreparedTests"/> through <see cref="TableStorage{T}"/> on Azurite.</summary>
public sealed class AzuriteBatchReleasesOnlyWhatItPreparedTests : AzuriteContentTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABatchThatFailsInPrepare_DoesNotCountOutAnotherWriteOfAContentOfAnEntityItNeverPrepared(bool upsert)
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(300_000, seed: 32);
        var shared = TableBlobContent.FromStream(BatchReleasesOnlyWhatItPreparedTests.ForwardOnly(data), "application/octet-stream");
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = Store<TwoBlobEntity>(blobs: BlobServiceWith(new PauseUpload("/w/First/", paused, resume.Task)));
        await store.EnsureCreatedAsync(Ct);

        var write = Task.Run(() => store.CreateAsync(
            new TwoBlobEntity { Id = Id("w"), First = TableBlobContent.FromBytes(RandomBytes(1_000), "application/octet-stream"), Second = shared }, Ct), Ct);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        List<TwoBlobEntity> batch =
        [
            new() { Id = Id("b1") },
            new() { Id = Id("b2"), First = BatchReleasesOnlyWhatItPreparedTests.Failing() },
            new() { Id = Id("b3"), Second = shared },
        ];
        var batchFailure = await Record.ExceptionAsync(() => upsert ? store.UpsertBatchAsync(batch, Ct) : store.CreateBatchAsync(batch, Ct));
        resume.SetResult();

        Assert.IsType<IOException>(batchFailure);
        var written = await Record.ExceptionAsync(() => write);
        Assert.True(written is null, $"The other write was refused: {written?.GetType().Name}: {written?.Message}");
        var read = await Store<TwoBlobEntity>().OneAsync(Id("w"), Ct);
        await using var stream = await read!.Second!.OpenReadAsync(Ct);
        Assert.Equal(data, await ReadAllAsync(stream));
        Assert.Null(await Store<TwoBlobEntity>().OneAsync(Id("b1"), Ct));
    }

    /// <summary>Holds the first blob upload whose path contains <c>marker</c> until <c>resume</c> completes.</summary>
    private sealed class PauseUpload(string marker, TaskCompletionSource paused, Task resume) : HttpPipelinePolicy
    {
        private bool Matches(HttpMessage message) =>
            message.Request.Method == RequestMethod.Put
            && Uri.UnescapeDataString(message.Request.Uri.Path).Contains(marker, StringComparison.Ordinal);

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (Matches(message))
            {
                paused.TrySetResult();
                await resume;
            }
            await ProcessNextAsync(message, pipeline);
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (Matches(message))
            {
                paused.TrySetResult();
                resume.GetAwaiter().GetResult();
            }
            ProcessNext(message, pipeline);
        }
    }
}
