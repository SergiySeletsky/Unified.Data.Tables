using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;
using Azure;
using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// One <see cref="TableBlobContent"/> held by several writes at the same time (BLOBS-1, BLOBS-L1 of the
/// fix-wave-2 review). Every write that reports success has stored the whole source; a write that
/// cannot is refused with <see cref="InvalidOperationException"/> before its row is written. The
/// content is hashed, copied and bound by one write at a time, and each written entity ends up with a
/// content that streams the blob of its own row.
/// </summary>
public class ConcurrentSharedContentTests(ITestOutputHelper output)
{
    private const int Size = 1_000_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemoryStorage<FileEntity> Store() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });

    private static async Task<Exception?> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<byte[]> ReadFileAsync(TableBlobContent content)
    {
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    // Counts, for two writes of one shared content, the ones that stored the whole file, the ones that
    // reported success with something else stored, and the refusals (which must be InvalidOperationException).
    private static async Task<(int Whole, int Partial, int Refused, List<string> Lines)> OutcomesAsync(
        InMemoryStorage<FileEntity> store, byte[] data, IReadOnlyList<(string Id, Exception? Failure)> writes)
    {
        var (whole, partial, refused) = (0, 0, 0);
        var lines = new List<string>();
        foreach (var (id, failure) in writes)
        {
            if (failure is not null)
            {
                refused++;
                lines.Add($"{id}: refused with {failure.GetType().Name}: {failure.Message}");
                Assert.IsType<InvalidOperationException>(failure);
                continue;
            }
            var read = await store.OneAsync(id, Ct);
            Assert.NotNull(read?.File);
            var stored = await ReadFileAsync(read.File);
            if (data.AsSpan().SequenceEqual(stored))
            {
                whole++;
            }
            else
            {
                partial++;
                lines.Add($"{id}: reported success, stored {stored.Length:N0} of {data.Length:N0} bytes");
            }
        }
        return (whole, partial, refused, lines);
    }

    /// <summary>
    /// BLOBS-1: one content over a forward-only stream on two entities created at the same time. The
    /// second write that sees the end of the stream sees it only after the first write finished (a slow
    /// last read), which at the time of the review left each row with a fragment of the file.
    /// </summary>
    [Fact]
    public async Task OneForwardOnlyContent_OnTwoEntitiesWrittenAtTheSameTime_StoresTheWholeFileInBoth()
    {
        var data = RandomBytes(Size, seed: 101);
        var firstWriteDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TurnTakingForwardOnlyStream(data)
        {
            BeforeEnd = n => n == 2 ? firstWriteDone.Task : Task.CompletedTask,
        };
        var content = TableBlobContent.FromStream(source, "application/octet-stream");
        var store = Store();
        var a = new FileEntity { Id = "p|a", File = content };
        var b = new FileEntity { Id = "p|b", File = content };

        var writeA = TryAsync(() => store.CreateAsync(a, Ct));
        var writeB = TryAsync(() => store.CreateAsync(b, Ct));
        await Task.WhenAny(writeA, writeB);
        firstWriteDone.TrySetResult();
        var (whole, partial, refused, lines) = await OutcomesAsync(store, data, [("p|a", await writeA), ("p|b", await writeB)]);

        foreach (var line in lines) output.WriteLine(line);
        Assert.Equal(0, partial);
        // One copy of the source serves both writes: both store the whole file.
        Assert.Equal((2, 0), (whole, refused));
        Assert.Equal(Size, source.Consumed);
    }

    /// <summary>
    /// BLOBS-1 with a seekable stream: two writes that hashed and uploaded the same stream at once
    /// disturbed each other and were both refused with "changed after it was prepared". Taking turns,
    /// both store the whole file.
    /// </summary>
    [Fact]
    public async Task OneSeekableContent_OnTwoEntitiesWrittenAtTheSameTime_StoresTheWholeFileInBoth()
    {
        var data = RandomBytes(Size, seed: 102);
        for (var round = 0; round < 8; round++)
        {
            var source = new TurnTakingSeekableStream(data);
            var content = TableBlobContent.FromStream(source, "application/octet-stream");
            var store = Store();
            var outcomes = await Task.WhenAll(
                TryAsync(() => store.CreateAsync(new FileEntity { Id = "p|a", File = content }, Ct)),
                TryAsync(() => store.CreateAsync(new FileEntity { Id = "p|b", File = content }, Ct)));

            var (whole, partial, refused, lines) = await OutcomesAsync(store, data, [("p|a", outcomes[0]), ("p|b", outcomes[1])]);
            foreach (var line in lines) output.WriteLine($"round {round} {line}");
            Assert.Equal((2, 0, 0), (whole, partial, refused));
            // The library is done reading the stream: it stands where the content started.
            Assert.Equal(0, source.Position);
        }
    }

    /// <summary>BLOBS-1 with natural timing (no delayed end of stream), many rounds.</summary>
    [Fact]
    public async Task OneForwardOnlyContent_OnTwoEntitiesWrittenAtTheSameTime_NaturalTiming_NeverStoresAPartialFile()
    {
        var data = RandomBytes(300_000, seed: 103);
        for (var round = 0; round < 20; round++)
        {
            var content = TableBlobContent.FromStream(new TurnTakingForwardOnlyStream(data, chunk: 16_384), "application/octet-stream");
            var store = Store();
            var failures = await Task.WhenAll(
                TryAsync(() => store.CreateAsync(new FileEntity { Id = "p|a", File = content }, Ct)),
                TryAsync(() => store.CreateAsync(new FileEntity { Id = "p|b", File = content }, Ct)));

            var (whole, partial, _, lines) = await OutcomesAsync(store, data, [("p|a", failures[0]), ("p|b", failures[1])]);
            foreach (var line in lines) output.WriteLine($"round {round} {line}");
            Assert.Equal(0, partial);
            Assert.Equal(2, whole);
        }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(data);
        return buffer.ToArray();
    }

    private static Stream PipeStream(byte[] data)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        pipe.Writer.Write(data);
        pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
        pipe.Writer.Complete();
        return pipe.Reader.AsStream();
    }

    /// <summary>
    /// BLOBS-1 (the verifier's check) with forward-only streams of the base class library and no hook:
    /// a decompression stream, and the stream of a pipe that holds the whole body (a buffered request).
    /// </summary>
    [Theory]
    [InlineData("gzip", false)]
    [InlineData("gzip", true)]
    [InlineData("pipe", false)]
    [InlineData("pipe", true)]
    public async Task OneContentOverAForwardOnlyFrameworkStream_OnTwoEntitiesWrittenAtTheSameTime_NeverStoresAPartialFile(string kind, bool onThreadPool)
    {
        var data = RandomBytes(Size, seed: 9101);
        var compressed = Gzip(data);
        for (var round = 0; round < 20; round++)
        {
            await using var stream = kind == "gzip"
                ? new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress)
                : PipeStream(data);
            var content = TableBlobContent.FromStream(stream, "application/octet-stream");
            var store = Store();
            var a = new FileEntity { Id = "p|a", File = content };
            var b = new FileEntity { Id = "p|b", File = content };

            var failures = onThreadPool
                ? await Task.WhenAll(Task.Run(() => TryAsync(() => store.CreateAsync(a, Ct))), Task.Run(() => TryAsync(() => store.CreateAsync(b, Ct))))
                : await Task.WhenAll(TryAsync(() => store.CreateAsync(a, Ct)), TryAsync(() => store.CreateAsync(b, Ct)));

            var (whole, partial, _, lines) = await OutcomesAsync(store, data, [("p|a", failures[0]), ("p|b", failures[1])]);
            foreach (var line in lines) output.WriteLine($"{kind} round {round} {line}");
            Assert.Equal(0, partial);
            Assert.Equal(2, whole);
        }
    }

    /// <summary>
    /// Contents that hold no stream of the caller (bytes, a content read from a row), written into
    /// several new rows at the same time: every row gets the whole file. (Control; passed at the review.)
    /// </summary>
    [Theory]
    [InlineData("bytes")]
    [InlineData("read from a row")]
    public async Task OneContentWithoutAStream_OnSeveralEntitiesWrittenAtTheSameTime_StoresEveryFileWhole(string kind)
    {
        var data = RandomBytes(Size, seed: 104);
        for (var round = 0; round < 10; round++)
        {
            var store = Store();
            TableBlobContent content;
            if (kind == "bytes")
            {
                content = TableBlobContent.FromBytes(data, "application/octet-stream");
            }
            else
            {
                await store.CreateAsync(new FileEntity { Id = "p|source", File = TableBlobContent.FromBytes(data, "application/octet-stream") }, Ct);
                content = (await store.OneAsync("p|source", Ct))!.File!;
            }

            var entities = Enumerable.Range(0, 6).Select(i => new FileEntity { Id = "p|copy" + i, File = content }).ToArray();
            var failures = await Task.WhenAll(entities.Select(e => Task.Run(() => TryAsync(() => store.CreateAsync(e, Ct)))));

            Assert.All(failures, Assert.Null);
            foreach (var entity in entities)
            {
                Assert.Equal(data, await ReadFileAsync((await store.OneAsync(entity.Id, Ct))!.File!));
                Assert.Equal(data, await ReadFileAsync(entity.File!));
            }
        }
    }

    /// <summary>
    /// One shared forward-only content, and one of the two writes fails after the source was copied
    /// (its blob upload fails). The other write still stores the whole file, or is refused with
    /// <see cref="InvalidOperationException"/>; it never fails on a temporary copy disposed under it.
    /// </summary>
    [Fact]
    public async Task OneForwardOnlyContent_OneOfTwoConcurrentWritesFails_TheOtherStoresTheWholeFileOrIsRefused()
    {
        var data = RandomBytes(300_000, seed: 105);
        for (var round = 0; round < 20; round++)
        {
            var content = TableBlobContent.FromStream(new TurnTakingForwardOnlyStream(data, chunk: 16_384), "application/octet-stream");
            var store = Store();
            store.Blobs.BeforeUpload = (name, _) =>
                name.Contains("/p/a/", StringComparison.Ordinal) ? throw new IOException("simulated blob store failure") : Task.CompletedTask;

            var failures = await Task.WhenAll(
                TryAsync(() => store.CreateAsync(new FileEntity { Id = "p|a", File = content }, Ct)),
                TryAsync(() => store.CreateAsync(new FileEntity { Id = "p|b", File = content }, Ct)));

            var failedA = Assert.IsType<IOException>(failures[0]);
            Assert.Equal("simulated blob store failure", failedA.Message);
            Assert.Null(await store.OneAsync("p|a", Ct));
            var (_, partial, _, lines) = await OutcomesAsync(store, data, [("p|b", failures[1])]);
            foreach (var line in lines) output.WriteLine($"round {round} {line}");
            Assert.Equal(0, partial);
        }
    }

    /// <summary>
    /// A write that holds a content (it is reading the source) and is cancelled releases it: another
    /// write of the same content is not left waiting, and is refused because the forward-only source
    /// was partly read. A write of an unrelated content is never held up by either.
    /// </summary>
    [Fact]
    public async Task AWriteCancelledWhileItHoldsAContent_ReleasesIt_AndNeverBlocksAnUnrelatedContent()
    {
        var data = RandomBytes(300_000, seed: 106);
        var source = new StallingForwardOnlyStream(data, stallAt: 100_000);
        var shared = TableBlobContent.FromStream(source, "application/octet-stream");
        var store = Store();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var first = TryAsync(() => store.CreateAsync(new FileEntity { Id = "p|a", File = shared }, cts.Token));
        await source.Stalled.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = TryAsync(() => store.CreateAsync(new FileEntity { Id = "p|b", File = shared }, Ct));

        // Meanwhile a write of another content goes through.
        var other = RandomBytes(50_000, seed: 107);
        await store.CreateAsync(new FileEntity { Id = "p|c", File = TableBlobContent.FromStream(new MemoryStream(other), "application/octet-stream") }, Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(other, await ReadFileAsync((await store.OneAsync("p|c", Ct))!.File!));
        Assert.False(second.IsCompleted);

        await cts.CancelAsync();
        var cancelled = await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var refused = await second.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
        Assert.Contains("partly read", Assert.IsType<InvalidOperationException>(refused).Message, StringComparison.Ordinal);
        Assert.Null(await store.OneAsync("p|a", Ct));
        Assert.Null(await store.OneAsync("p|b", Ct));
    }

    /// <summary>
    /// A write that holds a content while it waits for the blob service (its upload hangs) does not
    /// block a write of an unrelated content.
    /// </summary>
    [Fact]
    public async Task AWriteWaitingForTheBlobServiceWithAContent_DoesNotBlockAWriteOfAnotherContent()
    {
        var store = Store();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Blobs.BeforeUpload = (name, _) =>
        {
            if (!name.Contains("/p/slow/", StringComparison.Ordinal)) return Task.CompletedTask;
            waiting.TrySetResult();
            return release.Task;
        };
        var slowData = RandomBytes(100_000, seed: 108);
        var slow = TryAsync(() => store.CreateAsync(new FileEntity { Id = "p|slow", File = TableBlobContent.FromStream(new MemoryStream(slowData), "application/octet-stream") }, Ct));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var fastData = RandomBytes(100_000, seed: 109);
        await store.CreateAsync(new FileEntity { Id = "p|fast", File = TableBlobContent.FromStream(new MemoryStream(fastData), "application/octet-stream") }, Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(fastData, await ReadFileAsync((await store.OneAsync("p|fast", Ct))!.File!));

        release.SetResult();
        Assert.Null(await slow.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(slowData, await ReadFileAsync((await store.OneAsync("p|slow", Ct))!.File!));
    }

    // ── BLOBS-L1: two writes binding one content at the same instant ─────────────────────────

    private static readonly BlobOffloadOptions NoGrace = new() { CleanupGrace = TimeSpan.Zero };

    private static async Task WrittenRowAsync(InMemoryOffloadBlobStore blobs, FileEntity entity, string rk)
    {
        var coordinator = new ValueOffloadCoordinator(blobs, NoGrace, nameof(FileEntity));
        var (_, _, pending) = coordinator.Serialize(entity, "p", rk, 0);
        await coordinator.UploadAsync(pending, Ct);
    }

    /// <summary>
    /// BLOBS-L1: one content from a seekable stream, already hashed, on two entities whose blobs both
    /// exist. The two uploads only touch their blobs (released at the same instant) and then bind the
    /// content. Exactly one binds the shared instance; the other entity gets a content of its own, so each
    /// entity streams the blob of its own row.
    /// </summary>
    [Fact]
    public async Task TwoWritesBindingOneContentAtTheSameInstant_EachEntityEndsUpBoundToItsOwnBlob()
    {
        var data = RandomBytes(4_000, seed: 9401);
        const int rounds = 20_000;
        var mixed = 0;
        string? example = null;
        for (var round = 0; round < rounds; round++)
        {
            var inner = new InMemoryOffloadBlobStore();
            await WrittenRowAsync(inner, new FileEntity { Id = "p|a", File = TableBlobContent.FromBytes(data, "application/octet-stream") }, "a");
            await WrittenRowAsync(inner, new FileEntity { Id = "p|b", File = TableBlobContent.FromBytes(data, "application/octet-stream") }, "b");
            var coordinator = new ValueOffloadCoordinator(new TouchesReleasedTogether(inner, parties: 2), NoGrace, nameof(FileEntity));

            var content = TableBlobContent.FromStream(new MemoryStream(data), "application/octet-stream");
            var a = new FileEntity { Id = "p|a", File = content };
            var b = new FileEntity { Id = "p|b", File = content };
            await coordinator.PrepareAsync(a, Ct);
            var pendingA = coordinator.Serialize(a, "p", "a", 0).Blobs;
            var pendingB = coordinator.Serialize(b, "p", "b", 0).Blobs;
            var nameA = pendingA[0].Reference.Path;
            var nameB = pendingB[0].Reference.Path;

            var failures = await Task.WhenAll(
                Task.Run(() => TryAsync(() => coordinator.UploadAsync(pendingA, Ct))),
                Task.Run(() => TryAsync(() => coordinator.UploadAsync(pendingB, Ct))));

            Assert.All(failures, failure => Assert.Null(failure));
            if (a.File!.BoundPath != nameA || b.File!.BoundPath != nameB)
            {
                mixed++;
                example ??= $"round {round}: a.File streams '{a.File.BoundPath}' (its row: '{nameA}'), b.File streams '{b.File!.BoundPath}' (its row: '{nameB}')";
            }
            Assert.True(ReferenceEquals(a.File, content) ^ ReferenceEquals(b.File, content), "exactly one entity keeps the shared instance");
        }

        output.WriteLine($"{mixed} of {rounds} rounds left an entity with a content that streams the other row's blob. {example}");
        Assert.Equal(0, mixed);
    }

    /// <summary>Forwards to an in-memory blob store; every touch waits (spinning) until <c>parties</c> touches have arrived.</summary>
    private sealed class TouchesReleasedTogether(InMemoryOffloadBlobStore inner, int parties) : IOffloadBlobStore
    {
        private int arrived;

        public Task<BlobState?> GetStateAsync(string name, CancellationToken ct) => inner.GetStateAsync(name, ct);

        public Task UploadCreateOnlyAsync(string name, Stream content, BlobReference reference, string? contentEncoding, CancellationToken ct) =>
            inner.UploadCreateOnlyAsync(name, content, reference, contentEncoding, ct);

        public Task TouchAsync(string name, CancellationToken ct)
        {
            var touched = inner.TouchAsync(name, ct);
            Interlocked.Increment(ref arrived);
            var spin = new SpinWait();
            var deadline = Environment.TickCount64 + 5_000;
            while (Volatile.Read(ref arrived) < parties && Environment.TickCount64 < deadline)
                spin.SpinOnce(sleep1Threshold: -1);
            return touched;
        }

        public Task<Stream> OpenReadAsync(string column, string name, CancellationToken ct) => inner.OpenReadAsync(column, name, ct);

        public Task<bool> DeleteIfUnmodifiedAsync(string name, DateTimeOffset? unmodifiedSince, CancellationToken ct) =>
            inner.DeleteIfUnmodifiedAsync(name, unmodifiedSince, ct);

        public Task<DateTimeOffset?> GetServiceTimeAsync(CancellationToken ct) => inner.GetServiceTimeAsync(ct);

        public IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);
    }
}

/// <summary>
/// <see cref="ConcurrentSharedContentTests"/> through <see cref="TableStorage{T}"/> and the real SDK against
/// the local Azurite (own partition and container per test, both deleted at the end).
/// </summary>
public sealed class AzuriteConcurrentSharedContentTests(ITestOutputHelper output) : AzuriteContentTestBase
{
    private const int Size = 1_000_000;

    private static async Task<Exception?> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task<byte[]> StoredFileAsync(string id)
    {
        // A new store: the row and the blob are read from the service, not from a cache.
        var read = await Store<FileEntity>().OneAsync(id, Ct);
        Assert.NotNull(read?.File);
        await using var stream = await read.File.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    /// <summary>BLOBS-1 on Azurite: one forward-only content on two entities created at the same time.</summary>
    [Fact]
    public async Task OneForwardOnlyContent_OnTwoEntitiesWrittenAtTheSameTime_StoresTheWholeFileInBoth()
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 401);
        var firstWriteDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TurnTakingForwardOnlyStream(data)
        {
            BeforeEnd = n => n == 2 ? firstWriteDone.Task : Task.CompletedTask,
        };
        var content = TableBlobContent.FromStream(source, "application/octet-stream");
        var store = Store<FileEntity>();
        await store.EnsureCreatedAsync(Ct);
        var a = new FileEntity { Id = Id("a"), File = content };
        var b = new FileEntity { Id = Id("b"), File = content };

        var writeA = TryAsync(() => store.CreateAsync(a, Ct));
        var writeB = TryAsync(() => store.CreateAsync(b, Ct));
        await Task.WhenAny(writeA, writeB);
        firstWriteDone.TrySetResult();

        Assert.Null(await writeA);
        Assert.Null(await writeB);
        Assert.Equal(data, await StoredFileAsync(a.Id));
        Assert.Equal(data, await StoredFileAsync(b.Id));
    }

    /// <summary>BLOBS-1 on Azurite with a pipe stream (a buffered request body) and no hook.</summary>
    [Fact]
    public async Task OneContentOverAPipeStream_OnTwoEntitiesWrittenAtTheSameTime_NeverStoresAPartialFile()
    {
        Assert.SkipUnless(Available, SkipReason);
        const int rounds = 12;
        var data = RandomBytes(Size, seed: 9103);
        var store = Store<FileEntity>();
        await store.EnsureCreatedAsync(Ct);
        for (var round = 0; round < rounds; round++)
        {
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
            pipe.Writer.Write(data);
            await pipe.Writer.FlushAsync(Ct);
            await pipe.Writer.CompleteAsync();
            var content = TableBlobContent.FromStream(pipe.Reader.AsStream(), "application/octet-stream");
            var a = new FileEntity { Id = Id($"a{round}"), File = content };
            var b = new FileEntity { Id = Id($"b{round}"), File = content };

            var failures = await Task.WhenAll(TryAsync(() => store.CreateAsync(a, Ct)), TryAsync(() => store.CreateAsync(b, Ct)));

            foreach (var (id, failure) in new[] { (a.Id, failures[0]), (b.Id, failures[1]) })
            {
                if (failure is not null)
                {
                    output.WriteLine($"round {round} {id[^2..]}: refused with {failure.GetType().Name}: {failure.Message}");
                    Assert.IsType<InvalidOperationException>(failure);
                    continue;
                }
                Assert.Equal(data, await StoredFileAsync(id));
            }
        }
    }

    /// <summary>
    /// BLOBS-L1 through the public API: one content from a seekable stream on several entities whose rows
    /// already hold that file, all saved at the same time. Each written entity holds a content that
    /// streams the blob of its own row.
    /// </summary>
    [Fact]
    public async Task OneStreamContentOnSeveralEntitiesSavedAtTheSameTime_EachEntityEndsUpBoundToItsOwnBlob()
    {
        Assert.SkipUnless(Available, SkipReason);
        const int entities = 8;
        const int rounds = 150;
        var data = RandomBytes(4_000, seed: 9402);
        var store = Store<FileEntity>();
        var ids = Enumerable.Range(0, entities).Select(i => Id("e" + i)).ToArray();
        foreach (var id in ids)
            await store.UpsertAsync(new FileEntity { Id = id, File = TableBlobContent.FromBytes(data, "application/octet-stream") }, Ct);

        var mixed = 0;
        string? example = null;
        for (var round = 0; round < rounds; round++)
        {
            var content = TableBlobContent.FromStream(new MemoryStream(data), "application/octet-stream");
            var batch = ids.Select(id => new FileEntity { Id = id, File = content }).ToArray();
            var failures = await Task.WhenAll(batch.Select(e => TryAsync(() => store.UpsertAsync(e, Ct))));
            for (var i = 0; i < entities; i++)
            {
                Assert.Null(failures[i]);
                var own = $"{nameof(FileEntity)}/{Pk}/e{i}/File/{Sha(data)}";
                if (batch[i].File!.BoundPath != own)
                {
                    mixed++;
                    example ??= $"round {round}: the entity of row e{i} holds a content that streams '{batch[i].File!.BoundPath}'";
                }
            }
        }

        output.WriteLine($"{rounds} rounds of {entities} writes: {mixed} written entit(ies) hold a content that streams another row's blob. {example}");
        Assert.Equal(0, mixed);
    }
}

/// <summary>
/// A forward-only stream over fixed bytes whose reads complete asynchronously (as a network or a
/// decrypting stream does) and that serializes overlapping reads, so every read returns bytes exactly
/// once and in order, whoever asks.
/// </summary>
internal sealed class TurnTakingForwardOnlyStream(byte[] data, int chunk = 65_536) : Stream
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int position;
    private int ends;

    /// <summary>How many bytes were handed out in all.</summary>
    public int Consumed => position;

    /// <summary>Awaited before the n-th read that reports the end of the stream returns (n = 1, 2, ...).</summary>
    public Func<int, Task>? BeforeEnd { get; set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        gate.Wait();
        try
        {
            return Take(buffer.AsSpan(offset, count));
        }
        finally
        {
            gate.Release();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n;
        await gate.WaitAsync(cancellationToken);
        try
        {
            await Task.Delay(1, cancellationToken);
            n = Take(buffer.Span);
        }
        finally
        {
            gate.Release();
        }

        if (n == 0 && buffer.Length > 0 && BeforeEnd is { } hook)
            await hook(Interlocked.Increment(ref ends));
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Take(Span<byte> buffer)
    {
        var n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - position);
        data.AsSpan(position, n).CopyTo(buffer);
        position += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) gate.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A seekable stream over fixed bytes whose reads complete asynchronously and are serialized.</summary>
internal sealed class TurnTakingSeekableStream(byte[] data, int chunk = 65_536) : Stream
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => Interlocked.Read(ref position);
        set => Interlocked.Exchange(ref position, value);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        gate.Wait();
        try
        {
            return Take(buffer.AsSpan(offset, count));
        }
        finally
        {
            gate.Release();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await Task.Delay(1, cancellationToken);
            return Take(buffer.Span);
        }
        finally
        {
            gate.Release();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Take(Span<byte> buffer)
    {
        var at = Interlocked.Read(ref position);
        var n = (int)Math.Max(0, Math.Min(Math.Min(buffer.Length, chunk), data.Length - at));
        data.AsSpan((int)at, n).CopyTo(buffer);
        Interlocked.Exchange(ref position, at + n);
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => data.Length + offset,
        };
        return Position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) gate.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A forward-only stream whose read at <c>stallAt</c> waits (until cancelled) for data that never comes,
/// as a slow request body does; <see cref="Stalled"/> completes when a read is waiting there.
/// </summary>
internal sealed class StallingForwardOnlyStream(byte[] data, int stallAt) : Stream
{
    private readonly TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int position;

    public Task Stalled => stalled.Task;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("read asynchronously");

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (position >= stallAt)
        {
            stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        var n = Math.Min(Math.Min(buffer.Length, stallAt - position), data.Length - position);
        data.AsSpan(position, n).CopyTo(buffer.Span);
        position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
