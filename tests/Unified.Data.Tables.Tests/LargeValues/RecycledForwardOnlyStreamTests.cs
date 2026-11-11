using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A forward-only stream object that is given a new payload after a write read it (CONTENT-1 of the
/// fix-wave-3 review; ruling R9 points 1 to 3). A server hands every request of one connection the same
/// request-body object, so the library remembers a stream that a write read to its end only while that
/// write has not committed. After a committed write the next content over the object is a new payload,
/// whatever it delivers. After a failed write a new content over it is stored when the stream delivers
/// bytes again and refused, before anything is uploaded, when it delivers nothing (BLOBS-2: the retry
/// never stores an empty file). A stream a write read only part of stays refused.
/// </summary>
public class RecycledForwardOnlyStreamTests(ITestOutputHelper output)
{
    private const int Size = 300_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableBlobContent File(Stream stream) => TableBlobContent.FromStream(stream, "application/octet-stream");

    private static InMemoryStorage<FileEntity> Store() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero } });

    private static async Task<byte[]> ReadFileAsync(TableBlobContent content)
    {
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    /// <summary>The refusal of a new content over a stream a write that has not committed read to its end, and that delivers nothing now.</summary>
    internal static void AssertConsumedAndEmpty(InvalidOperationException ex)
    {
        Assert.Contains("already read", ex.Message, StringComparison.Ordinal);
        Assert.Contains("delivers nothing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FromBytes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("open the source again", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>CONTENT-1: the next request on a keep-alive connection, behind the same body object, is stored whole.</summary>
    [Fact]
    public async Task AForwardOnlyStreamObjectReusedForTheNextRequestBody_IsStoredWhole_NotRefusedAsAlreadyRead()
    {
        var store = Store();
        var body = new RecycledForwardOnlyBodyStream();

        body.Load(RandomBytes(Size, seed: 31));
        await store.UpsertAsync(new FileEntity { Id = "p|one", File = File(body) }, Ct);

        // The next request on the same connection: a new, unread body behind the same stream object.
        var second = RandomBytes(Size, seed: 32);
        body.Load(second);
        var outcome = await Record.ExceptionAsync(() => store.UpsertAsync(new FileEntity { Id = "p|two", File = File(body) }, Ct));
        output.WriteLine(outcome is null ? "second write stored" : $"second write: {outcome.GetType().Name}: {outcome.Message}");

        Assert.Null(outcome);
        Assert.Equal(second, await ReadFileAsync((await store.OneAsync("p|two", Ct))!.File!));
    }

    /// <summary>R9 point 1: a committed write forgets the stream, so an empty next payload is stored as an empty file.</summary>
    [Fact]
    public async Task AfterACommittedWrite_ANewContentOverTheSameStreamThatDeliversNothing_StoresAnEmptyFile()
    {
        var store = Store();
        var body = new RecycledForwardOnlyBodyStream();
        body.Load(RandomBytes(Size, seed: 33));
        await store.UpsertAsync(new FileEntity { Id = "p|one", File = File(body) }, Ct);

        body.Load([]);
        await store.UpsertAsync(new FileEntity { Id = "p|two", File = File(body) }, Ct);

        var read = await store.OneAsync("p|two", Ct);
        Assert.Equal(0, read!.File!.Length);
        Assert.Empty(await ReadFileAsync(read.File));
    }

    /// <summary>R9 point 2: after a failed write that read the stream to its end, an empty payload is the retry BLOBS-2 is about, and is refused.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterAFailedWrite_ANewContentOverTheConsumedStreamThatDeliversNothing_IsRefusedBeforeAnythingIsUploaded(bool loadEmptyPayload)
    {
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|r", File = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") }, Ct);
        var body = new RecycledForwardOnlyBodyStream();
        body.Load(RandomBytes(Size, seed: 34));
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new FileEntity { Id = "p|r", File = File(body) }, Ct));
        var blobs = store.OffloadedBlobNames();

        if (loadEmptyPayload)
            body.Load([]);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(new FileEntity { Id = "p|r", File = File(body) }, Ct));

        output.WriteLine(refused.Message);
        AssertConsumedAndEmpty(refused);
        Assert.Equal(blobs, store.OffloadedBlobNames());
        Assert.Equal(new byte[] { 1, 2, 3 }, await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
    }

    /// <summary>R9 point 2: after a failed write, a stream that was given a new payload is stored whole.</summary>
    [Fact]
    public async Task AfterAFailedWrite_ANewContentOverTheStreamThatDeliversANewPayload_IsStoredWhole()
    {
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|r", File = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") }, Ct);
        var body = new RecycledForwardOnlyBodyStream();
        body.Load(RandomBytes(Size, seed: 35));
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new FileEntity { Id = "p|r", File = File(body) }, Ct));

        var next = RandomBytes(Size, seed: 36);
        body.Load(next);
        await store.UpsertAsync(new FileEntity { Id = "p|r", File = File(body) }, Ct);

        Assert.Equal(next, await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
    }

    /// <summary>A failed write, then the same content instance written again: that commit forgets the stream too.</summary>
    [Fact]
    public async Task AFailedWriteRetriedWithTheSameContent_ForgetsTheStreamWhenTheRetryCommits()
    {
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|taken", Title = "someone else's" }, Ct);
        var body = new RecycledForwardOnlyBodyStream();
        var data = RandomBytes(Size, seed: 37);
        body.Load(data);
        var content = File(body);
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new FileEntity { Id = "p|taken", File = content }, Ct));
        await store.CreateAsync(new FileEntity { Id = "p|free", File = content }, Ct);
        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|free", Ct))!.File!));

        body.Load([]);
        await store.UpsertAsync(new FileEntity { Id = "p|next", File = File(body) }, Ct);

        Assert.Equal(0, (await store.OneAsync("p|next", Ct))!.File!.Length);
    }

    /// <summary>R9 point 3: a stream a failed write read only part of stays refused, whatever it would deliver now.</summary>
    [Fact]
    public async Task AForwardOnlyStreamAFailedWriteReadOnlyPartOf_IsRefusedForEveryLaterContent()
    {
        var store = Store();
        var stream = new FaultyContentStream(RandomBytes(Size, seed: 38), seekable: false) { FailAt = 100_000, FailuresLeft = 1 };
        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(new FileEntity { Id = "p|r", File = File(stream) }, Ct));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(new FileEntity { Id = "p|r", File = File(stream) }, Ct));
            output.WriteLine(refused.Message);
            Assert.Contains("partly read", refused.Message, StringComparison.Ordinal);
        }
        Assert.Null(await store.OneAsync("p|r", Ct));
        Assert.Empty(store.OffloadedBlobNames());
        Assert.Equal(100_000, stream.ReadPosition);
    }

    /// <summary>Two contents over one forward-only stream on two entities of one batch: refused before anything is written.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoContentsOverOneForwardOnlyStream_InOneBatch_AreRefusedAndNothingIsWritten(bool upsert)
    {
        var store = Store();
        var body = new RecycledForwardOnlyBodyStream();
        body.Load(RandomBytes(Size, seed: 39));
        FileEntity[] batch = [new FileEntity { Id = "p|a", File = File(body) }, new FileEntity { Id = "p|b", File = File(body) }];

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => upsert ? store.UpsertBatchAsync(batch, Ct) : store.CreateBatchAsync(batch, Ct));

        AssertConsumedAndEmpty(refused);
        Assert.Null(await store.OneAsync("p|a", Ct));
        Assert.Null(await store.OneAsync("p|b", Ct));
        Assert.Empty(store.OffloadedBlobNames());
    }

    /// <summary>
    /// Every kind of write tells the large-value layer when it committed: afterwards the stream it read is
    /// forgotten, and an empty next payload over the same object is stored (it would be refused otherwise).
    /// </summary>
    [Theory]
    [InlineData("Create")]
    [InlineData("Upsert")]
    [InlineData("Update")]
    [InlineData("UpdateStrict")]
    [InlineData("CreateBatch")]
    [InlineData("UpsertBatch")]
    [InlineData("MutateAsync")]
    public async Task EveryKindOfWrite_ForgetsTheStreamItReadWhenItCommits(string kind)
    {
        var store = Store();
        var existing = await store.CreateAsync(new FileEntity { Id = "p|existing", Title = "v1" }, Ct);
        var body = new RecycledForwardOnlyBodyStream();
        var data = RandomBytes(Size, seed: 40);
        body.Load(data);
        var id = kind is "Create" or "CreateBatch" ? "p|new" : "p|existing";

        await WriteAsync(store, kind, id, existing.ETag, File(body));
        Assert.Equal(data, await ReadFileAsync((await store.OneAsync(id, Ct))!.File!));

        body.Load([]);
        await store.UpsertAsync(new FileEntity { Id = "p|next", File = File(body) }, Ct);
        Assert.Equal(0, (await store.OneAsync("p|next", Ct))!.File!.Length);
    }

    internal static async Task WriteAsync(IStorage<FileEntity> store, string kind, string id, string? etag, TableBlobContent content)
    {
        switch (kind)
        {
            case "Create":
                await store.CreateAsync(new FileEntity { Id = id, File = content }, Ct);
                break;
            case "Upsert":
                await store.UpsertAsync(new FileEntity { Id = id, File = content }, Ct);
                break;
            case "Update":
                await store.UpdateAsync(new FileEntity { Id = id, File = content }, ConcurrencyMode.LastWriterWins, Ct);
                break;
            case "UpdateStrict":
                await store.UpdateAsync(new FileEntity { Id = id, ETag = etag, File = content }, ConcurrencyMode.Strict, Ct);
                break;
            case "CreateBatch":
                await store.CreateBatchAsync([new FileEntity { Id = id, File = content }], Ct);
                break;
            case "UpsertBatch":
                await store.UpsertBatchAsync([new FileEntity { Id = id, File = content }], Ct);
                break;
            default:
                await store.MutateAsync(id, e => e.File = content, ct: Ct);
                break;
        }
    }

    /// <summary>Control: a stream that reports <c>CanSeek</c> but throws on <c>Position</c> stores nothing; its own exception reaches the caller.</summary>
    [Fact]
    public async Task AStreamThatSaysItCanSeekButThrowsOnPosition_StoresNothing()
    {
        var store = Store();
        var outcome = await Record.ExceptionAsync(() => store.UpsertAsync(
            new FileEntity { Id = "p|x", File = File(new NoPositionStream(RandomBytes(1000))) }, Ct));
        output.WriteLine($"outcome: {outcome?.GetType().Name}: {outcome?.Message}");
        Assert.NotNull(outcome);
        Assert.Null(await store.OneAsync("p|x", Ct));
        Assert.Empty(store.OffloadedBlobNames());
    }
}

/// <summary><see cref="RecycledForwardOnlyStreamTests"/> through <see cref="TableStorage{T}"/> and the real SDK against the local Azurite.</summary>
public sealed class AzuriteRecycledForwardOnlyStreamTests(ITestOutputHelper output) : AzuriteContentTestBase
{
    private const int Size = 300_000;

    private static TableBlobContent File(Stream stream) => TableBlobContent.FromStream(stream, "application/octet-stream");

    private static async Task<byte[]> ReadFileAsync(TableBlobContent content)
    {
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    /// <summary>CONTENT-1 on Azurite.</summary>
    [Fact]
    public async Task AForwardOnlyStreamObjectReusedForTheNextRequestBody_IsStoredWhole_NotRefusedAsAlreadyRead()
    {
        Assert.SkipUnless(Available, SkipReason);
        var store = Store<FileEntity>(Offload(TimeSpan.Zero));
        var body = new RecycledForwardOnlyBodyStream();
        body.Load(RandomBytes(Size, seed: 41));
        await store.UpsertAsync(new FileEntity { Id = Id("one"), File = File(body) }, Ct);

        var second = RandomBytes(Size, seed: 42);
        body.Load(second);
        var outcome = await Record.ExceptionAsync(() => store.UpsertAsync(new FileEntity { Id = Id("two"), File = File(body) }, Ct));
        output.WriteLine(outcome is null ? "second write stored" : $"second write: {outcome.GetType().Name}: {outcome.Message}");

        Assert.Null(outcome);
        Assert.Equal(second, await ReadFileAsync((await Store<FileEntity>().OneAsync(Id("two"), Ct))!.File!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterAFailedWrite_ANewContentOverTheStream_IsStoredWhenItDeliversBytes_AndRefusedWhenItDeliversNothing(bool newPayload)
    {
        Assert.SkipUnless(Available, SkipReason);
        var store = Store<FileEntity>(Offload(TimeSpan.Zero));
        await store.CreateAsync(new FileEntity { Id = Id("r"), File = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") }, Ct);
        var body = new RecycledForwardOnlyBodyStream();
        body.Load(RandomBytes(Size, seed: 43));
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new FileEntity { Id = Id("r"), File = File(body) }, Ct));
        var blobs = (await BlobsAsync<FileEntity>()).ConvertAll(b => b.Name);

        var next = RandomBytes(Size, seed: 44);
        body.Load(newPayload ? next : []);
        var outcome = await Record.ExceptionAsync(() => store.UpsertAsync(new FileEntity { Id = Id("r"), File = File(body) }, Ct));

        var stored = await ReadFileAsync((await Store<FileEntity>().OneAsync(Id("r"), Ct))!.File!);
        if (newPayload)
        {
            Assert.Null(outcome);
            Assert.Equal(next, stored);
        }
        else
        {
            RecycledForwardOnlyStreamTests.AssertConsumedAndEmpty(Assert.IsType<InvalidOperationException>(outcome));
            Assert.Equal(new byte[] { 1, 2, 3 }, stored);
            Assert.Equal(blobs, (await BlobsAsync<FileEntity>()).ConvertAll(b => b.Name));
        }
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Upsert")]
    [InlineData("Update")]
    [InlineData("UpdateStrict")]
    [InlineData("CreateBatch")]
    [InlineData("UpsertBatch")]
    [InlineData("MutateAsync")]
    public async Task EveryKindOfWrite_ForgetsTheStreamItReadWhenItCommits(string kind)
    {
        Assert.SkipUnless(Available, SkipReason);
        var store = Store<FileEntity>(Offload(TimeSpan.Zero));
        var existing = await store.CreateAsync(new FileEntity { Id = Id("existing"), Title = "v1" }, Ct);
        var body = new RecycledForwardOnlyBodyStream();
        var data = RandomBytes(Size, seed: 45);
        body.Load(data);
        var id = kind is "Create" or "CreateBatch" ? Id("new") : Id("existing");

        await RecycledForwardOnlyStreamTests.WriteAsync(store, kind, id, existing.ETag, File(body));
        Assert.Equal(data, await ReadFileAsync((await Store<FileEntity>().OneAsync(id, Ct))!.File!));

        body.Load([]);
        await store.UpsertAsync(new FileEntity { Id = Id("next"), File = File(body) }, Ct);
        Assert.Equal(0, (await Store<FileEntity>().OneAsync(Id("next"), Ct))!.File!.Length);
    }
}

/// <summary>
/// A forward-only request body whose stream object is reused for the next request (as a web server reuses
/// its request-body stream on a keep-alive connection): <see cref="Load"/> hands it the next body.
/// </summary>
internal sealed class RecycledForwardOnlyBodyStream : Stream
{
    private byte[] data = [];
    private int position;

    public void Load(byte[] body)
    {
        data = body;
        position = 0;
    }

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
        var n = Math.Min(count, data.Length - position);
        Array.Copy(data, position, buffer, offset, n);
        position += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A stream that reports <c>CanSeek</c> but cannot tell or set its position.</summary>
internal sealed class NoPositionStream(byte[] data) : Stream
{
    private int position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => throw new NotSupportedException("no position");
        set => throw new NotSupportedException("no position");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(count, data.Length - position);
        Array.Copy(data, position, buffer, offset, n);
        position += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("no seek");

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
