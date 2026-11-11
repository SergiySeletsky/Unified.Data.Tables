using Azure.Core;
using Azure.Core.Pipeline;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A stream over fixed bytes that misbehaves on demand: a read fails (or cancels a token) when it
/// arrives at <see cref="FailAt"/>, and the stream can refuse to be rewound. Every read stops short at
/// <see cref="FailAt"/> first, so the failure is hit at that exact offset whatever the reader's buffer.
/// </summary>
internal sealed class FaultyContentStream(byte[] data, bool seekable, long start = 0) : Stream
{
    private long position = start;
    private int arrivals;
    private bool disposed;

    /// <summary>The offset at which a read fails; -1 for never.</summary>
    public long FailAt { get; init; } = -1;

    /// <summary>How many times a read may arrive at <see cref="FailAt"/> before one fails (1 lets a first pass through).</summary>
    public int PassesBeforeFailure { get; init; }

    /// <summary>How many more reads fail at <see cref="FailAt"/> with an <see cref="IOException"/>.</summary>
    public int FailuresLeft { get; set; }

    /// <summary>Cancelled (and the read throws) when a read arrives at <see cref="FailAt"/>.</summary>
    public CancellationTokenSource? CancelAtFailAt { get; init; }

    /// <summary>Moving the stream (setting <see cref="Position"/> to another offset, or seeking there) throws from now on.</summary>
    public bool RefuseRewind { get; set; }

    /// <summary>Turns <see cref="RefuseRewind"/> on once a read has failed.</summary>
    public bool RefuseRewindAfterFailure { get; init; }

    /// <summary>Where the next read starts, whether or not the stream is seekable.</summary>
    public long ReadPosition => position;

    public override bool CanRead => !disposed;

    public override bool CanSeek => seekable && !disposed;

    public override bool CanWrite => false;

    public override long Length => seekable ? data.Length : throw new NotSupportedException();

    public override long Position
    {
        get => seekable ? position : throw new NotSupportedException();
        set
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!seekable) throw new NotSupportedException();
            if (RefuseRewind && value != position) throw new IOException("simulated seek failure");
            position = value;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var count = buffer.Length;
        if (FailAt >= 0 && position <= FailAt && position + count > FailAt)
        {
            if (position < FailAt)
            {
                count = (int)(FailAt - position);
            }
            else if (++arrivals > PassesBeforeFailure)
            {
                if (CancelAtFailAt is { IsCancellationRequested: false } cts)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }
                if (FailuresLeft > 0)
                {
                    FailuresLeft--;
                    if (RefuseRewindAfterFailure) RefuseRewind = true;
                    throw new IOException("simulated read failure");
                }
            }
        }

        var n = (int)Math.Min(count, data.Length - position);
        data.AsSpan((int)position, n).CopyTo(buffer);
        position += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<int>(ex);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            _ => data.Length + offset,
        };
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// A write of a <see cref="TableBlobContent"/> that fails part-way must not poison a retry of the same
/// entity. A seekable source is read from where its content starts on every attempt (the position is
/// put back when an attempt fails or is cancelled), so a retry stores every byte. A source that cannot
/// be read again (it cannot be rewound, or it is forward-only and was partly consumed) makes the content
/// unusable: every later write throws and nothing is stored, never a partial file and never an empty one.
/// </summary>
public class BlobContentRetryTests
{
    internal const int Size = 1_000_000;

    /// <summary>Four reads of the hashing pass (81,920 bytes each) succeed; the fifth starts here.</summary>
    internal const int FailAt = 327_680;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemoryStorage<FileEntity> Store() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });

    private static FileEntity Entity(Stream source, string id = "p|r") =>
        new() { Id = id, Title = "t", File = TableBlobContent.FromStream(source, "application/octet-stream") };

    internal static void AssertSourceLost(InvalidOperationException ex)
    {
        Assert.Contains("'File'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("partly read by a failed write", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Create the content again", ex.Message, StringComparison.Ordinal);
    }

    private static async Task AssertStoredAsync(InMemoryStorage<FileEntity> store, byte[] expected, string id = "p|r")
    {
        var read = await store.OneAsync(id, Ct);
        Assert.NotNull(read?.File);
        Assert.Equal(expected.Length, read.File.Length);
        Assert.Equal(Sha(expected), read.File.Sha256);
        await using var stream = await read.File.OpenReadAsync(Ct);
        Assert.Equal(expected, await ReadAllAsync(stream));
        Assert.Single(store.Blobs.Names);
    }

    private static void AssertNothingStored(InMemoryStorage<FileEntity> store)
    {
        Assert.Equal(0, store.PhysicalRowCount);
        Assert.Empty(store.Blobs.Names);
    }

    [Fact]
    public async Task SeekableSourceFailingOnceMidHash_RetryStoresEveryByte()
    {
        var data = RandomBytes(Size, seed: 41);
        var source = new FaultyContentStream(data, seekable: true) { FailAt = FailAt, FailuresLeft = 1 };
        var entity = Entity(source);
        var store = Store();

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));
        var afterFailure = source.Position;
        AssertNothingStored(store);

        await store.CreateAsync(entity, Ct);

        await AssertStoredAsync(store, data);
        Assert.Equal(Size, entity.File!.Length);
        Assert.Equal(0, afterFailure); // the failed attempt put the source back where its content starts
    }

    [Fact]
    public async Task CancellationMidHash_RetryStoresEveryByte()
    {
        var data = RandomBytes(Size, seed: 42);
        using var cts = new CancellationTokenSource();
        var source = new FaultyContentStream(data, seekable: true) { FailAt = FailAt, CancelAtFailAt = cts };
        var entity = Entity(source);
        var store = Store();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(entity, cts.Token));
        var afterCancel = source.Position;
        AssertNothingStored(store);

        await store.CreateAsync(entity, Ct);

        await AssertStoredAsync(store, data);
        Assert.Equal(0, afterCancel);
    }

    [Fact]
    public async Task SeekableSourceThatCannotBeRewound_RetryThrowsTheClearError_AndStoresNothing()
    {
        var data = RandomBytes(Size, seed: 43);
        var source = new FaultyContentStream(data, seekable: true) { FailAt = FailAt, FailuresLeft = 1, RefuseRewindAfterFailure = true };
        var entity = Entity(source);
        var store = Store();

        // The write fails for its own reason (the read), not for the rewind that failed after it.
        var first = await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));
        Assert.Equal("simulated read failure", first.Message);

        var retry = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));
        AssertNothingStored(store);
        AssertSourceLost(Assert.IsType<InvalidOperationException>(retry));
        // Every later write, whatever the operation.
        AssertSourceLost(await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(entity, Ct)));
        AssertSourceLost(await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateBatchAsync([entity], Ct)));
        AssertNothingStored(store);
    }

    [Fact]
    public async Task SourceThatCannotBeRewoundAfterItWasHashed_RetryThrows_NeverStoresAnEmptyFile()
    {
        var data = RandomBytes(Size, seed: 44);
        var source = new FaultyContentStream(data, seekable: true) { RefuseRewind = true };
        var entity = Entity(source);
        var store = Store();

        // The hashing pass read everything; putting the source back failed.
        var first = await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));
        Assert.Equal("simulated seek failure", first.Message);
        Assert.Equal(Size, source.Position);

        // A retry that read "the rest" of this source would seal and store zero bytes.
        var retry = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));
        AssertNothingStored(store);
        AssertSourceLost(Assert.IsType<InvalidOperationException>(retry));
    }

    [Fact]
    public async Task NonSeekableSourceFailingMidCopy_RetryThrowsTheClearError_AndStoresNothing()
    {
        var data = RandomBytes(Size, seed: 45);
        var source = new FaultyContentStream(data, seekable: false) { FailAt = FailAt, FailuresLeft = 1 };
        var entity = Entity(source);
        var store = Store();

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));
        Assert.Equal(FailAt, source.ReadPosition); // consumed, and a forward-only stream cannot give it back

        // A retry that read on would store only the 672,320 bytes that are left.
        var retry = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));
        AssertNothingStored(store);
        AssertSourceLost(Assert.IsType<InvalidOperationException>(retry));
        AssertSourceLost(await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(entity, Ct)));
        AssertNothingStored(store);
        Assert.Equal(FailAt, source.ReadPosition); // the refused writes read nothing more
    }

    [Fact]
    public async Task NonSeekableSource_CancelledMidCopy_RetryThrowsTheClearError()
    {
        var data = RandomBytes(Size, seed: 46);
        using var cts = new CancellationTokenSource();
        var source = new FaultyContentStream(data, seekable: false) { FailAt = FailAt, CancelAtFailAt = cts };
        var entity = Entity(source);
        var store = Store();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(entity, cts.Token));

        var retry = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));
        AssertNothingStored(store);
        AssertSourceLost(Assert.IsType<InvalidOperationException>(retry));
    }

    [Fact]
    public async Task UploadFailingOnce_RetryStoresEveryByte_ForASeekableSource()
    {
        var data = RandomBytes(Size, seed: 47);
        var source = new FaultyContentStream(data, seekable: true);
        var entity = Entity(source);
        var store = Store();
        var failures = 1;
        store.Blobs.BeforeUpload = (_, _) => failures-- > 0 ? throw new IOException("simulated blob store failure") : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));
        AssertNothingStored(store);

        await store.CreateAsync(entity, Ct);

        await AssertStoredAsync(store, data);
    }

    [Fact]
    public async Task SourceFailingOnceDuringTheUpload_RetryStoresEveryByte_ForASeekableSource()
    {
        var data = RandomBytes(Size, seed: 48);
        // The hashing pass gets through; the upload's pass fails at the same offset, leaving the source mid-way.
        var source = new FaultyContentStream(data, seekable: true) { FailAt = FailAt, PassesBeforeFailure = 1, FailuresLeft = 1 };
        var entity = Entity(source);
        var store = Store();

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));
        Assert.Equal(FailAt, source.Position);
        AssertNothingStored(store);

        await store.CreateAsync(entity, Ct);

        await AssertStoredAsync(store, data);
    }

    [Fact]
    public async Task SourceNotAtItsStart_EveryAttemptReadsFromWhereTheContentStarted()
    {
        const int start = 1_000;
        var data = RandomBytes(Size, seed: 49);
        var source = new FaultyContentStream(data, seekable: true, start) { FailAt = FailAt, FailuresLeft = 1 };
        var entity = Entity(source);
        var store = Store();

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));
        Assert.Equal(start, source.Position);

        await store.CreateAsync(entity, Ct);

        await AssertStoredAsync(store, data[start..]);
    }

    [Fact]
    public async Task SourceDisposedBetweenAttempts_RetryThrowsInvalidOperation_NotObjectDisposed()
    {
        var data = RandomBytes(Size, seed: 50);
        var source = new FaultyContentStream(data, seekable: true) { FailAt = FailAt, FailuresLeft = 1 };
        var entity = Entity(source);
        var store = Store();
        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));

        await source.DisposeAsync(); // the caller gave up on the stream, then retried the write anyway

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(entity, Ct));
        Assert.Contains("'File'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Create the content again", ex.Message, StringComparison.Ordinal);
        AssertNothingStored(store);
    }

    [Fact]
    public async Task TokenCancelledBeforeTheWrite_LeavesAForwardOnlySourceUntouched()
    {
        var data = RandomBytes(Size, seed: 51);
        var source = new FaultyContentStream(data, seekable: false);
        var entity = Entity(source);
        var store = Store();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(entity, cancelled.Token));
        Assert.Equal(0, source.ReadPosition); // nothing was read: the content is as good as new

        await store.CreateAsync(entity, Ct);

        await AssertStoredAsync(store, data);
    }

    [Fact]
    public async Task ContentWhoseSourceWasLost_CannotBeOpenedForReading_AndSaysWhy()
    {
        var source = new FaultyContentStream(RandomBytes(Size, seed: 52), seekable: false) { FailAt = FailAt, FailuresLeft = 1 };
        var entity = Entity(source);
        await Assert.ThrowsAsync<IOException>(() => Store().CreateAsync(entity, Ct));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => entity.File!.OpenReadAsync(Ct));

        Assert.Contains("partly read by a failed write", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceDisposedAfterItWasPrepared_TheUploadRefusesTheContent_AndStoresNothing()
    {
        var data = RandomBytes(Size, seed: 54);
        var source = new FaultyContentStream(data, seekable: true);
        var blobs = new InMemoryOffloadBlobStore();
        var coordinator = new ValueOffloadCoordinator(blobs, new BlobOffloadOptions(), "T");
        var entity = Entity(source);
        await coordinator.PrepareAsync(entity, Ct);
        var (_, _, pending) = coordinator.Serialize(entity, "p", "r", 0);

        await source.DisposeAsync(); // the caller let go of the stream before the write was over

        // Every upload starts where the content starts, or not at all.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.UploadAsync(pending, Ct));
        Assert.Contains("'File'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Create the content again", ex.Message, StringComparison.Ordinal);
        Assert.Empty(blobs.Names);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareAsync(entity, Ct));
    }

    [Fact]
    public async Task SuccessPath_SeekableSource_IsLeftWhereItsContentStarts_AfterTheHashingPass()
    {
        // Unchanged behaviour: the hashing pass rewinds, the upload then reads the source to its end.
        var data = RandomBytes(Size, seed: 53);
        var source = new FaultyContentStream(data, seekable: true);
        var coordinator = new ValueOffloadCoordinator(new InMemoryOffloadBlobStore(), new BlobOffloadOptions(), "T");
        var entity = Entity(source);

        await coordinator.PrepareAsync(entity, Ct);

        Assert.Equal(0, source.Position);
        Assert.Equal(Size, entity.File!.Length);
        Assert.Equal(Sha(data), entity.File.Sha256);
        Assert.Same(source, entity.File.Source);
    }
}

/// <summary>The same retries against Azurite, where the truncated file was first seen.</summary>
public sealed class AzuriteBlobContentRetryTests : AzuriteContentTestBase
{
    private const int Size = BlobContentRetryTests.Size;
    private const int FailAt = BlobContentRetryTests.FailAt;

    private FileEntity Entity(Stream source, string rk) =>
        new() { Id = Id(rk), Title = "t", File = TableBlobContent.FromStream(source, "application/octet-stream") };

    // Reads the row through a new store (nothing cached) and compares the blob's bytes.
    private async Task AssertStoredAsync(string rk, byte[] expected)
    {
        var blob = Assert.Single(await BlobsAsync<FileEntity>());
        Assert.Equal(expected.Length, blob.Properties.ContentLength);
        var read = await Store<FileEntity>().OneAsync(Id(rk), Ct);
        Assert.NotNull(read?.File);
        Assert.Equal(expected.Length, read.File.Length);
        Assert.Equal(Sha(expected), read.File.Sha256);
        await using var stream = await read.File.OpenReadAsync(Ct);
        Assert.Equal(expected, await ReadAllAsync(stream));
    }

    private async Task AssertNothingStoredAsync()
    {
        Assert.Empty(await RawRowsAsync<FileEntity>());
        Assert.Empty(await BlobsAsync<FileEntity>());
    }

    [Fact]
    public async Task SeekableSourceFailingOnceMidHash_RetryStoresEveryByte()
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 61);
        var source = new FaultyContentStream(data, seekable: true) { FailAt = FailAt, FailuresLeft = 1 };
        var entity = Entity(source, "s1");
        var store = Store<FileEntity>();

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));
        var afterFailure = source.Position;
        await AssertNothingStoredAsync();

        await store.CreateAsync(entity, Ct);

        await AssertStoredAsync("s1", data);
        Assert.Equal(0, afterFailure);
    }

    [Fact]
    public async Task CancellationMidHash_RetryStoresEveryByte()
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 62);
        using var cts = new CancellationTokenSource();
        var source = new FaultyContentStream(data, seekable: true) { FailAt = FailAt, CancelAtFailAt = cts };
        var entity = Entity(source, "s2");
        var store = Store<FileEntity>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(entity, cts.Token));
        var afterCancel = source.Position;
        await AssertNothingStoredAsync();

        await store.CreateAsync(entity, Ct);

        await AssertStoredAsync("s2", data);
        Assert.Equal(0, afterCancel);
    }

    [Fact]
    public async Task SeekableSourceThatCannotBeRewound_RetryThrowsTheClearError_AndStoresNothing()
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 63);
        var source = new FaultyContentStream(data, seekable: true) { FailAt = FailAt, FailuresLeft = 1, RefuseRewindAfterFailure = true };
        var entity = Entity(source, "s3");
        var store = Store<FileEntity>();

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));

        var retry = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));
        await AssertNothingStoredAsync();
        BlobContentRetryTests.AssertSourceLost(Assert.IsType<InvalidOperationException>(retry));
        BlobContentRetryTests.AssertSourceLost(await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(entity, Ct)));
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task NonSeekableSourceFailingMidCopy_RetryThrowsTheClearError_AndStoresNothing()
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 64);
        var source = new FaultyContentStream(data, seekable: false) { FailAt = FailAt, FailuresLeft = 1 };
        var entity = Entity(source, "s4");
        var store = Store<FileEntity>();

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));

        // A retry that read on would store only the 672,320 bytes that are left.
        var retry = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));
        await AssertNothingStoredAsync();
        BlobContentRetryTests.AssertSourceLost(Assert.IsType<InvalidOperationException>(retry));
    }

    [Fact]
    public async Task UploadFailingOnce_RetryStoresEveryByte_ForASeekableSource()
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 65);
        var source = new FaultyContentStream(data, seekable: true);
        var entity = Entity(source, "s5");
        var failing = new FailFirstUpload();
        var store = Store<FileEntity>(blobs: BlobServiceWith(failing));

        // The SDK had read the whole source before the request failed.
        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));
        Assert.Equal(1, failing.Failures);
        Assert.Equal(Size, source.Position);
        await AssertNothingStoredAsync();

        await store.CreateAsync(entity, Ct);

        await AssertStoredAsync("s5", data);
    }

    [Fact]
    public async Task SourceFailingOnceDuringTheUpload_RetryStoresEveryByte_ForASeekableSource()
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 66);
        var source = new FaultyContentStream(data, seekable: true) { FailAt = FailAt, PassesBeforeFailure = 1, FailuresLeft = 1 };
        var entity = Entity(source, "s6");
        var store = Store<FileEntity>();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAsync(entity, Ct));
        Assert.True(ex is IOException || ex.GetBaseException() is IOException, ex.ToString());
        Assert.Equal(FailAt, source.Position);
        await AssertNothingStoredAsync();

        await store.CreateAsync(entity, Ct);

        await AssertStoredAsync("s6", data);
    }

    /// <summary>Fails the first upload of a blob (Put Blob or Put Block) below the table's prefix.</summary>
    private sealed class FailFirstUpload : HttpPipelineSynchronousPolicy
    {
        private int failuresLeft = 1;

        public int Failures { get; private set; }

        public override void OnSendingRequest(HttpMessage message)
        {
            var uri = message.Request.Uri;
            if (message.Request.Method != RequestMethod.Put
                || !uri.Path.Contains("/" + nameof(FileEntity) + "/", StringComparison.Ordinal)
                || uri.Query.Contains("comp=metadata", StringComparison.Ordinal))
                return;
            if (Interlocked.Decrement(ref failuresLeft) < 0)
                return;
            Failures++;
            throw new IOException("simulated blob upload failure");
        }
    }
}
