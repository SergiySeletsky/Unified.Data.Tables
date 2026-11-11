using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// What the Blobs SDK's open-read stream does when the blob changes under it, scripted: it serves
/// <paramref name="data"/> from <paramref name="start"/> and, once <paramref name="failAfter"/> bytes
/// were handed out, every further read throws <paramref name="failure"/> (a ranged GET the service refused).
/// </summary>
internal sealed class ScriptedBlobReadStream(byte[] data, long start, int failAfter = int.MaxValue, Exception? failure = null) : Stream
{
    private long position = start;
    private int delivered;

    public bool Disposed { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (failure is not null && delivered >= failAfter)
            throw failure;
        var n = (int)Math.Min(Math.Min(buffer.Length, data.Length - position), failAfter - delivered);
        data.AsSpan((int)position, n).CopyTo(buffer);
        position += n;
        delivered += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<int>(ex);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Reading an offloaded value while the row is written again. A blob's name contains the SHA-256 of its
/// bytes and blobs are written create-only, so the bytes under a name never change: a write that reuses
/// the content only re-sets the blob's metadata (which changes its ETag), and a replace or delete removes
/// the blob. The read must survive the first (it reads on from where it was) and report the second as
/// <see cref="TableValueIntegrityException"/>, never as a raw <see cref="RequestFailedException"/>.
/// </summary>
public class OpenReadDuringWriteTests
{
    private const string Name = "T/p/r/File/abc";
    private const int Size = 100_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RequestFailedException Status(int status, string code) => new(status, code, code, null);

    /// <summary><see cref="AzureOffloadBlobStore"/> over a substitute blob whose open-read calls are scripted and recorded.</summary>
    private sealed class Rig
    {
        public Rig(byte[] data)
        {
            Data = data;
            Container.GetBlobClient(Name).Returns(Blob);
            Blob.Name.Returns(Name);
            Blob.OpenReadAsync(Arg.Any<BlobOpenReadOptions>(), Arg.Any<CancellationToken>())
                .Returns(call => Open(call.Arg<BlobOpenReadOptions>()));
            Blob.OpenRead(Arg.Any<BlobOpenReadOptions>(), Arg.Any<CancellationToken>())
                .Returns(call => Open(call.Arg<BlobOpenReadOptions>()).GetAwaiter().GetResult());
            Store = new AzureOffloadBlobStore(Container);
        }

        public byte[] Data { get; }

        public BlobContainerClient Container { get; } = Substitute.For<BlobContainerClient>();

        public BlobClient Blob { get; } = Substitute.For<BlobClient>();

        public AzureOffloadBlobStore Store { get; }

        /// <summary>What each open returns, in order: a stream from the requested position, or a failure. The last entry repeats.</summary>
        public List<Func<long, Stream>> Script { get; } = [];

        /// <summary>The position each open asked for, in order.</summary>
        public List<long> Opens { get; } = [];

        /// <summary>The streams handed out, in order.</summary>
        public List<Stream> Streams { get; } = [];

        private Task<Stream> Open(BlobOpenReadOptions options)
        {
            var step = Script[Math.Min(Opens.Count, Script.Count - 1)];
            Opens.Add(options.Position);
            try
            {
                var stream = step(options.Position);
                Streams.Add(stream);
                return Task.FromResult(stream);
            }
            catch (Exception ex)
            {
                return Task.FromException<Stream>(ex);
            }
        }

        public Func<long, Stream> Whole => position => new ScriptedBlobReadStream(Data, position);

        public Func<long, Stream> FailingAfter(int bytes, Exception failure) =>
            position => new ScriptedBlobReadStream(Data, position, bytes, failure);

        public static Func<long, Stream> Failing(Exception failure) => _ => throw failure;
    }

    public enum ReadKind
    {
        Sync,
        Async,
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream, ReadKind kind)
    {
        using var all = new MemoryStream();
        var buffer = new byte[8_192];
        while (true)
        {
            var n = kind == ReadKind.Sync ? stream.Read(buffer, 0, buffer.Length) : await stream.ReadAsync(buffer, Ct);
            if (n == 0) return all.ToArray();
            all.Write(buffer, 0, n);
        }
    }

    // ── the Azure store's read stream ──────────────────────────────────────

    [Theory]
    [InlineData(ReadKind.Sync)]
    [InlineData(ReadKind.Async)]
    public async Task ETagChangedMidRead_ReadsOnFromThePositionReached(ReadKind kind)
    {
        var rig = new Rig(RandomBytes(Size, seed: 121));
        // The metadata was re-set after 30,000 bytes: the pinned range is refused, a new open continues.
        rig.Script.Add(rig.FailingAfter(30_000, Status(412, "ConditionNotMet")));
        rig.Script.Add(rig.Whole);

        await using var stream = await rig.Store.OpenReadAsync("File", Name, Ct);
        var read = await ReadToEndAsync(stream, kind);

        Assert.Equal(rig.Data, read);
        Assert.Equal([0L, 30_000L], rig.Opens);
        Assert.True(((ScriptedBlobReadStream)rig.Streams[0]).Disposed, "the refused stream is released");
        Assert.False(stream.CanSeek);
        Assert.Equal(Size, stream.Position);
    }

    [Theory]
    [InlineData(ReadKind.Sync)]
    [InlineData(ReadKind.Async)]
    public async Task ETagChangedBeforeTheFirstRange_ReadsFromTheStartAgain(ReadKind kind)
    {
        var rig = new Rig(RandomBytes(Size, seed: 122));
        // A write landed between the SDK's HEAD and its first GET.
        rig.Script.Add(rig.FailingAfter(0, Status(412, "ConditionNotMet")));
        rig.Script.Add(rig.Whole);

        await using var stream = await rig.Store.OpenReadAsync("File", Name, Ct);

        Assert.Equal(rig.Data, await ReadToEndAsync(stream, kind));
        Assert.Equal([0L, 0L], rig.Opens);
    }

    [Theory]
    [InlineData(ReadKind.Sync)]
    [InlineData(ReadKind.Async)]
    public async Task ETagChangedSeveralTimes_EveryRangeIsReadOnce(ReadKind kind)
    {
        var rig = new Rig(RandomBytes(Size, seed: 123));
        rig.Script.Add(rig.FailingAfter(10_000, Status(412, "ConditionNotMet")));
        rig.Script.Add(rig.FailingAfter(25_000, Status(412, "ConditionNotMet")));
        rig.Script.Add(rig.FailingAfter(1, Status(412, "ConditionNotMet")));
        rig.Script.Add(rig.Whole);

        await using var stream = await rig.Store.OpenReadAsync("File", Name, Ct);

        Assert.Equal(rig.Data, await ReadToEndAsync(stream, kind));
        Assert.Equal([0L, 10_000L, 35_000L, 35_001L], rig.Opens);
    }

    [Theory]
    [InlineData(ReadKind.Sync)]
    [InlineData(ReadKind.Async)]
    public async Task BlobDeletedMidRead_ThrowsIntegrity_NotRequestFailed(ReadKind kind)
    {
        var rig = new Rig(RandomBytes(Size, seed: 124));
        rig.Script.Add(rig.FailingAfter(30_000, Status(404, "BlobNotFound")));

        await using var stream = await rig.Store.OpenReadAsync("File", Name, Ct);
        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadToEndAsync(stream, kind));

        Assert.Equal("File", ex.ColumnName);
        Assert.Contains(Name, ex.Reason, StringComparison.Ordinal);
        Assert.Contains("deleted while it was being read", ex.Reason, StringComparison.Ordinal);
        Assert.Equal([0L], rig.Opens); // a deleted blob is not opened again
    }

    [Theory]
    [InlineData(ReadKind.Sync)]
    [InlineData(ReadKind.Async)]
    public async Task ETagChanged_ThenTheBlobIsGoneWhenReopened_ThrowsIntegrity(ReadKind kind)
    {
        var rig = new Rig(RandomBytes(Size, seed: 125));
        rig.Script.Add(rig.FailingAfter(30_000, Status(412, "ConditionNotMet")));
        rig.Script.Add(Rig.Failing(Status(404, "BlobNotFound")));

        await using var stream = await rig.Store.OpenReadAsync("File", Name, Ct);
        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadToEndAsync(stream, kind));

        Assert.Equal("File", ex.ColumnName);
        Assert.Contains("deleted while it was being read", ex.Reason, StringComparison.Ordinal);
        Assert.Equal([0L, 30_000L], rig.Opens);
    }

    [Theory]
    [InlineData(ReadKind.Sync)]
    [InlineData(ReadKind.Async)]
    public async Task ETagKeepsChangingWithoutProgress_GivesUpWithTheServicesError(ReadKind kind)
    {
        var rig = new Rig(RandomBytes(Size, seed: 126));
        rig.Script.Add(rig.FailingAfter(0, Status(412, "ConditionNotMet"))); // every open: refused at once

        await using var stream = await rig.Store.OpenReadAsync("File", Name, Ct);
        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => ReadToEndAsync(stream, kind));

        Assert.Equal(412, ex.Status);
        Assert.Equal(1 + AzureOffloadBlobStore.MaxReadReopens, rig.Opens.Count);
    }

    [Theory]
    [InlineData(ReadKind.Sync)]
    [InlineData(ReadKind.Async)]
    public async Task ReopenLimit_CountsOnlyReopensWithoutProgress(ReadKind kind)
    {
        var rig = new Rig(RandomBytes(Size, seed: 127));
        // More refusals than the limit, but each open delivers something first.
        for (var i = 0; i < AzureOffloadBlobStore.MaxReadReopens * 3; i++)
            rig.Script.Add(rig.FailingAfter(1_000, Status(412, "ConditionNotMet")));
        rig.Script.Add(rig.Whole);

        await using var stream = await rig.Store.OpenReadAsync("File", Name, Ct);

        Assert.Equal(rig.Data, await ReadToEndAsync(stream, kind));
        Assert.Equal(1 + (AzureOffloadBlobStore.MaxReadReopens * 3), rig.Opens.Count);
    }

    [Theory]
    [InlineData(ReadKind.Sync)]
    [InlineData(ReadKind.Async)]
    public async Task AnyOtherFailure_IsNotRetried_AndSurfacesAsIs(ReadKind kind)
    {
        var rig = new Rig(RandomBytes(Size, seed: 128));
        var failure = Status(500, "InternalError");
        rig.Script.Add(rig.FailingAfter(30_000, failure));

        await using var stream = await rig.Store.OpenReadAsync("File", Name, Ct);
        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => ReadToEndAsync(stream, kind));

        Assert.Same(failure, ex);
        Assert.Equal([0L], rig.Opens);
    }

    [Fact]
    public async Task MissingWhenOpened_ThrowsIntegrity_AsBefore()
    {
        var rig = new Rig(RandomBytes(Size, seed: 129));
        rig.Script.Add(Rig.Failing(Status(404, "BlobNotFound")));

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => rig.Store.OpenReadAsync("File", Name, Ct));

        Assert.Equal("File", ex.ColumnName);
        Assert.Contains("is missing", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposing_ReleasesTheSdkStream()
    {
        var rig = new Rig(RandomBytes(Size, seed: 130));
        rig.Script.Add(rig.Whole);

        var stream = await rig.Store.OpenReadAsync("File", Name, Ct);
        await stream.ReadExactlyAsync(new byte[16], Ct);
        await stream.DisposeAsync();

        Assert.True(((ScriptedBlobReadStream)Assert.Single(rig.Streams)).Disposed);
    }

    // ── the eager read path of TableStorage over that stream ───────────────

    private const string Pk = "p";
    private const string Rk = "r";
    private const string Id = "p|r";

    private static readonly UnifiedTableStorageOptions Offload = new()
    {
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 },
        Cache = CachePolicy.Disabled,
    };

    // The row a write of the entity stores (its blob's bytes are served by the scripted SDK streams).
    private static async Task<TableEntity> OffloadedRowAsync(SplitTextEntity entity, string etag)
    {
        var coordinator = new ValueOffloadCoordinator(new InMemoryOffloadBlobStore(), Offload.BlobOffload, nameof(SplitTextEntity));
        var (row, _, pending) = coordinator.Serialize(entity, Pk, Rk, 0);
        await coordinator.UploadAsync(pending, Ct);
        row.ETag = new ETag(etag);
        return row;
    }

    private static string PathOf(TableEntity row) => BlobReference.Parse("Body", (string)row["Body__blob"]).Path;

    private static BlobClient BlobOf(BlobContainerClient container, string name)
    {
        var blob = Substitute.For<BlobClient>();
        blob.Name.Returns(name);
        container.GetBlobClient(name).Returns(blob);
        return blob;
    }

    private static void PrimaryReads(StorageHarness<SplitTextEntity> h, params TableEntity[] versions)
    {
        var responses = versions.Select(Mocks.Found).ToArray();
        h.Table.GetEntityIfExistsAsync<TableEntity>(Pk, Rk, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(responses[0], responses[1..]);
    }

    [Fact]
    public async Task Eager_BlobDeletedMidDownloadByAConcurrentReplace_RetriesAndReturnsTheNewValue()
    {
        var oldBody = Ascii(40_000, seed: 1);
        var newBody = Ascii(40_000, seed: 2);
        var v1 = await OffloadedRowAsync(new SplitTextEntity { Id = Id, Body = oldBody }, "W/\"v1\"");
        var v2 = await OffloadedRowAsync(new SplitTextEntity { Id = Id, Body = newBody }, "W/\"v2\"");
        var container = Substitute.For<BlobContainerClient>();
        // The replace committed and deleted v1's blob while the reader was 10,000 bytes into it.
        BlobOf(container, PathOf(v1)).OpenReadAsync(Arg.Any<BlobOpenReadOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new ScriptedBlobReadStream(Utf8(oldBody), 0, 10_000, Status(404, "BlobNotFound"))));
        BlobOf(container, PathOf(v2)).OpenReadAsync(Arg.Any<BlobOpenReadOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new ScriptedBlobReadStream(Utf8(newBody), 0)));
        using var h = new StorageHarness<SplitTextEntity>(options: Offload, blobs: new AzureOffloadBlobStore(container));
        PrimaryReads(h, v1, v2);

        var read = await h.Store.OneAsync(Id, Ct);

        Assert.Equal(newBody, read!.Body, StringComparer.Ordinal);
        Assert.Equal("W/\"v2\"", read.ETag);
    }

    [Fact]
    public async Task Eager_BlobDeletedMidDownload_WithAnUnchangedRow_ThrowsIntegrity()
    {
        var body = Ascii(40_000, seed: 3);
        var v1 = await OffloadedRowAsync(new SplitTextEntity { Id = Id, Body = body }, "W/\"v1\"");
        var container = Substitute.For<BlobContainerClient>();
        BlobOf(container, PathOf(v1)).OpenReadAsync(Arg.Any<BlobOpenReadOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new ScriptedBlobReadStream(Utf8(body), 0, 10_000, Status(404, "BlobNotFound"))));
        using var h = new StorageHarness<SplitTextEntity>(options: Offload, blobs: new AzureOffloadBlobStore(container));
        PrimaryReads(h, v1, v1, v1);

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => h.Store.OneAsync(Id, Ct));

        Assert.Equal("Body", ex.ColumnName);
    }

    [Fact]
    public async Task Eager_WriteLandingBetweenTheSdksRequests_StillReturnsTheValue()
    {
        var body = Ascii(40_000, seed: 4);
        var v1 = await OffloadedRowAsync(new SplitTextEntity { Id = Id, Body = body }, "W/\"v1\"");
        var container = Substitute.For<BlobContainerClient>();
        var opens = new List<long>();
        // The blob's metadata was re-set after the SDK's HEAD: its first GET is refused, a new open reads all.
        BlobOf(container, PathOf(v1)).OpenReadAsync(Arg.Any<BlobOpenReadOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var position = call.Arg<BlobOpenReadOptions>().Position;
                opens.Add(position);
                return Task.FromResult<Stream>(opens.Count == 1
                    ? new ScriptedBlobReadStream(Utf8(body), position, 0, Status(412, "ConditionNotMet"))
                    : new ScriptedBlobReadStream(Utf8(body), position));
            });
        using var h = new StorageHarness<SplitTextEntity>(options: Offload, blobs: new AzureOffloadBlobStore(container));
        PrimaryReads(h, v1);

        var read = await h.Store.OneAsync(Id, Ct);

        Assert.Equal(body, read!.Body, StringComparer.Ordinal);
        Assert.Equal([0L, 0L], opens);
        // Not treated as a torn read: one point read of the row.
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(Pk, Rk, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── InMemoryStorage: an open stream is a snapshot of the stored bytes ───

    private static InMemoryStorage<FileEntity> InMemory() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero } });

    [Fact]
    public async Task InMemory_EntitySavedAgainWhileItsFileIsRead_TheReadGetsEveryByte()
    {
        var data = RandomBytes(Size, seed: 131);
        var store = InMemory();
        await store.CreateAsync(new FileEntity { Id = Id, File = TableBlobContent.FromBytes(data, "application/octet-stream") }, Ct);
        var name = Assert.Single(store.Blobs.Names);
        var before = (await store.Blobs.GetStateAsync(name, Ct))!.LastModified;

        var reader = await store.OneAsync(Id, Ct);
        await using var stream = await reader!.File!.OpenReadAsync(Ct);
        var head = new byte[16];
        await stream.ReadExactlyAsync(head, Ct);

        // Another caller loads the entity and saves it again with the same file: the write touches the blob.
        var loaded = await store.OneAsync(Id, Ct);
        loaded!.Title = "saved again";
        await store.UpdateAsync(loaded, Ct);
        Assert.True((await store.Blobs.GetStateAsync(name, Ct))!.LastModified > before, "the blob was touched");

        Assert.Equal(data, (byte[])[.. head, .. await ReadAllAsync(stream)]);
    }

    [Fact]
    public async Task InMemory_BlobRemovedWhileItIsRead_TheOpenStreamStillDeliversTheVerifiedContent()
    {
        var data = RandomBytes(Size, seed: 132);
        var store = InMemory();
        await store.CreateAsync(new FileEntity { Id = Id, File = TableBlobContent.FromBytes(data, "application/octet-stream") }, Ct);

        var reader = await store.OneAsync(Id, Ct);
        await using var stream = await reader!.File!.OpenReadAsync(Ct);
        var head = new byte[16];
        await stream.ReadExactlyAsync(head, Ct);
        await store.DeleteAsync(Id, Ct); // removes the row and (grace zero) its blob
        Assert.Empty(store.Blobs.Names);

        // No partial data either way: the stream holds what it opened, and verifies it.
        Assert.Equal(data, (byte[])[.. head, .. await ReadAllAsync(stream)]);
        // A new open finds the blob gone.
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => reader.File.OpenReadAsync(Ct));
    }
}

/// <summary>
/// The same against Azurite, where the failure was first seen: a 9 MiB file (more than one of the SDK's
/// 4 MiB ranges) read lazily while the row is saved again or deleted, and an eagerly read value whose
/// blob changes between the SDK's HEAD and its GET.
/// </summary>
public sealed class AzuriteOpenReadDuringWriteTests : AzuriteContentTestBase
{
    private const int FileSize = 9 * 1024 * 1024;

    private async Task<(byte[] Data, string BlobName)> CreateFileAsync(string rk, int seed)
    {
        var data = RandomBytes(FileSize, seed);
        await Store<FileEntity>().CreateAsync(
            new FileEntity { Id = Id(rk), Title = "t", File = TableBlobContent.FromStream(new MemoryStream(data), "application/octet-stream") }, Ct);
        return (data, Assert.Single(await BlobsAsync<FileEntity>()).Name);
    }

    [Fact]
    public async Task EntitySavedAgainWhileItsFileIsRead_TheReadGetsEveryByte()
    {
        Assert.SkipUnless(Available, SkipReason);
        var (data, name) = await CreateFileAsync("f1", seed: 141);
        var blob = Container.GetBlobClient(name);
        var before = (await blob.GetPropertiesAsync(cancellationToken: Ct)).Value.ETag;

        var reader = await Store<FileEntity>().OneAsync(Id("f1"), Ct);
        await using var stream = await reader!.File!.OpenReadAsync(Ct);
        var head = new byte[16];
        await stream.ReadExactlyAsync(head, Ct);

        // Another caller loads the entity and saves it again with the same file: the write touches the
        // blob (Set Blob Metadata), which changes its ETag.
        var second = Store<FileEntity>();
        var loaded = await second.OneAsync(Id("f1"), Ct);
        loaded!.Title = "saved again";
        await second.UpdateAsync(loaded, Ct);
        Assert.NotEqual(before, (await blob.GetPropertiesAsync(cancellationToken: Ct)).Value.ETag);

        var rest = await ReadAllAsync(stream);

        Assert.Equal(FileSize, head.Length + rest.Length);
        Assert.True(data.AsSpan(0, 16).SequenceEqual(head));
        Assert.True(data.AsSpan(16).SequenceEqual(rest), "the bytes read after the touch are the file's");
    }

    [Fact]
    public async Task BlobDeletedWhileItIsRead_ThrowsIntegrity_NotRequestFailed()
    {
        Assert.SkipUnless(Available, SkipReason);
        var (_, name) = await CreateFileAsync("f2", seed: 142);

        var reader = await Store<FileEntity>().OneAsync(Id("f2"), Ct);
        await using var stream = await reader!.File!.OpenReadAsync(Ct);
        await stream.ReadExactlyAsync(new byte[16], Ct);
        await Container.DeleteBlobAsync(name, cancellationToken: Ct);

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadAllAsync(stream));

        Assert.Equal("File", ex.ColumnName);
        Assert.Contains("deleted while it was being read", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowDeletedWhileItsFileIsRead_ThrowsIntegrity_NotRequestFailed()
    {
        Assert.SkipUnless(Available, SkipReason);
        await CreateFileAsync("f3", seed: 143);

        var reader = await Store<FileEntity>().OneAsync(Id("f3"), Ct);
        await using var stream = await reader!.File!.OpenReadAsync(Ct);
        await stream.ReadExactlyAsync(new byte[16], Ct);
        // CleanupGrace zero: the delete removes the blob at once.
        await Store<FileEntity>(Offload(TimeSpan.Zero)).DeleteAsync(Id("f3"), Ct);
        Assert.Empty(await BlobsAsync<FileEntity>());

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadAllAsync(stream));
    }

    // ── eager values: a write between the SDK's HEAD and its GET ───────────

    private BlobOffloadOptions SmallThreshold(TimeSpan? grace = null)
    {
        var offload = Offload(grace);
        offload.ThresholdBytes = 1_000; // a value just too large for one cell is offloaded
        return offload;
    }

    private TableStorage<SplitTextEntity> ReaderRunning(Func<Task> betweenHeadAndGet, out BeforeFirstRangedGet policy)
    {
        policy = new BeforeFirstRangedGet("/" + nameof(SplitTextEntity) + "/", betweenHeadAndGet);
        return Store<SplitTextEntity>(SmallThreshold(), BlobServiceWith(policy));
    }

    [Fact]
    public async Task Eager_BlobTouchedBetweenHeadAndGet_StillReturnsTheValue()
    {
        Assert.SkipUnless(Available, SkipReason);
        var body = Ascii(40_000, seed: 144);
        await Store<SplitTextEntity>(SmallThreshold()).CreateAsync(new SplitTextEntity { Id = Id("e1"), Body = body }, Ct);
        var blob = Container.GetBlobClient(Assert.Single(await BlobsAsync<SplitTextEntity>()).Name);

        // Another caller saves the same value again: its write touches the blob.
        var reader = ReaderRunning(
            () => Store<SplitTextEntity>(SmallThreshold()).UpsertAsync(new SplitTextEntity { Id = Id("e1"), Body = body, Title = "again" }, Ct),
            out var policy);
        var before = (await blob.GetPropertiesAsync(cancellationToken: Ct)).Value.ETag;

        var read = await reader.OneAsync(Id("e1"), Ct);

        Assert.Equal(1, policy.Runs);
        Assert.NotEqual(before, (await blob.GetPropertiesAsync(cancellationToken: Ct)).Value.ETag);
        Assert.Equal(body, read!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Eager_ValueReplacedBetweenHeadAndGet_ReturnsTheNewValue()
    {
        Assert.SkipUnless(Available, SkipReason);
        var oldBody = Ascii(40_000, seed: 145);
        var newBody = Ascii(40_000, seed: 146);
        await Store<SplitTextEntity>(SmallThreshold()).CreateAsync(new SplitTextEntity { Id = Id("e2"), Body = oldBody }, Ct);

        // CleanupGrace zero: the replace deletes the old blob right after its commit.
        var reader = ReaderRunning(
            () => Store<SplitTextEntity>(SmallThreshold(TimeSpan.Zero)).UpsertAsync(new SplitTextEntity { Id = Id("e2"), Body = newBody }, Ct),
            out var policy);

        var read = await reader.OneAsync(Id("e2"), Ct);

        Assert.Equal(1, policy.Runs);
        Assert.Equal(newBody, read!.Body, StringComparer.Ordinal);
        Assert.Single(await BlobsAsync<SplitTextEntity>());
    }

    [Fact]
    public async Task Eager_BlobDeletedBetweenHeadAndGet_WithAnUnchangedRow_ThrowsIntegrity()
    {
        Assert.SkipUnless(Available, SkipReason);
        await Store<SplitTextEntity>(SmallThreshold()).CreateAsync(new SplitTextEntity { Id = Id("e3"), Body = Ascii(40_000, seed: 147) }, Ct);
        var name = Assert.Single(await BlobsAsync<SplitTextEntity>()).Name;

        var reader = ReaderRunning(() => Container.DeleteBlobAsync(name, cancellationToken: Ct), out var policy);

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => reader.OneAsync(Id("e3"), Ct));

        Assert.Equal(1, policy.Runs);
        Assert.Equal("Body", ex.ColumnName);
    }

    /// <summary>
    /// Runs an action once, just before the first ranged GET of a blob below a path marker is sent: after
    /// the SDK's HEAD (Get Blob Properties), before its first download request.
    /// </summary>
    private sealed class BeforeFirstRangedGet(string pathMarker, Func<Task> action) : HttpPipelinePolicy
    {
        private int runs;

        public int Runs => Volatile.Read(ref runs);

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (Matches(message) && Interlocked.Exchange(ref runs, 1) == 0)
                await action();
            await ProcessNextAsync(message, pipeline);
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (Matches(message) && Interlocked.Exchange(ref runs, 1) == 0)
                action().GetAwaiter().GetResult();
            ProcessNext(message, pipeline);
        }

        private bool Matches(HttpMessage message) =>
            message.Request.Method == RequestMethod.Get
            && message.Request.Uri.Path.Contains(pathMarker, StringComparison.Ordinal)
            && message.Request.Headers.TryGetValue("x-ms-range", out _);
    }
}
