using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A stream a write read to its end stays remembered for as long as the stream object lives, whatever
/// happened to that write or to any other write (ruling R9 as revised in fix wave 5; STREAMS-1 and
/// STREAMS-L1 of the fix-wave-4 review). No commit forgets it: not the commit of one transaction of a batch
/// whose later transaction fails, not the commit of another write that shares the content. So the caller's
/// retry, a new content over the consumed stream that delivers nothing, is refused before anything is
/// uploaded and every row keeps the file it holds; a new payload of at least one byte is stored whole.
/// </summary>
public class ConsumedStreamStaysRememberedTests(ITestOutputHelper output)
{
    private const int Size = 300_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableBlobContent File(Stream stream) => TableBlobContent.FromStream(stream, "application/octet-stream");

    private static InMemoryStorage<FileEntity> Store() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero } });

    private static async Task<byte[]> ReadFileAsync(InMemoryStorage<FileEntity> store, string id)
    {
        var read = await store.OneAsync(id, Ct);
        await using var stream = await read!.File!.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    private static Task WriteBatchAsync(InMemoryStorage<FileEntity> store, bool upsert, FileEntity[] batch) =>
        upsert ? store.UpsertBatchAsync(batch, Ct) : store.CreateBatchAsync(batch, Ct);

    // STREAMS-1, shape A. One content on two entities of one batch; two partitions give two transactions, the
    // first commits and the second fails. The caller retries the whole call with a new content over the same,
    // consumed stream: it delivers nothing, so it is refused before anything is uploaded, and a|1 keeps the
    // file the first call committed. The one-partition control (one transaction that fails) behaves the same.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ABatch_ASharedStreamContent_WhoseLaterTransactionFailed_RetriedWithANewContent_IsRefusedAndKeepsTheCommittedFile(bool twoPartitions, bool upsert)
    {
        var store = Store();
        var body = new ReloadableForwardOnlyBody();
        var data = RandomBytes(Size, seed: 71);
        body.Load(data);
        var second = twoPartitions ? "b|1" : "a|2";
        var transactions = 0;
        store.BeforeLargeCommit = () =>
        {
            if (++transactions == (twoPartitions ? 2 : 1))
                throw new InvalidOperationException("injected: the transaction fails (a transient service error)");
        };
        var shared = File(body);
        var first = await Record.ExceptionAsync(() => WriteBatchAsync(store, upsert,
            [new FileEntity { Id = "a|1", File = shared }, new FileEntity { Id = second, File = shared }]));
        output.WriteLine($"first call: {first?.GetType().Name}: {first?.Message}");
        Assert.NotNull(first);
        store.BeforeLargeCommit = null;
        if (twoPartitions)
            Assert.Equal(data, await ReadFileAsync(store, "a|1"));
        var blobs = store.OffloadedBlobNames();

        var again = File(body);
        var retry = await Record.ExceptionAsync(() => WriteBatchAsync(store, upsert,
            [new FileEntity { Id = "a|1", File = again }, new FileEntity { Id = second, File = again }]));
        output.WriteLine(retry is null ? "retry: success" : $"retry: {retry.GetType().Name}: {retry.Message}");

        RecycledForwardOnlyStreamTests.AssertConsumedAndEmpty(Assert.IsType<InvalidOperationException>(retry));
        Assert.Equal(blobs, store.OffloadedBlobNames());
        if (twoPartitions)
            Assert.Equal(data, await ReadFileAsync(store, "a|1"));
        else
            Assert.Null(await store.OneAsync("a|1", Ct));
        Assert.Null(await store.OneAsync(second, Ct));
    }

    // STREAMS-1, shape B. The stream is on the entity of the committed transaction only; another transaction
    // of the call fails. The retry of the call with a new content over the consumed stream is refused, and
    // a|1 keeps its committed file.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABatch_AStreamOnTheCommittedTransaction_AnotherTransactionFailed_RetriedWithANewContent_IsRefusedAndKeepsTheCommittedFile(bool upsert)
    {
        var store = Store();
        var body = new ReloadableForwardOnlyBody();
        var data = RandomBytes(Size, seed: 75);
        body.Load(data);
        var transactions = 0;
        store.BeforeLargeCommit = () =>
        {
            if (++transactions == 2)
                throw new InvalidOperationException("injected: the second transaction fails");
        };
        var first = await Record.ExceptionAsync(() => WriteBatchAsync(store, upsert,
            [new FileEntity { Id = "a|1", File = File(body) }, new FileEntity { Id = "b|1", File = TableBlobContent.FromBytes([9], "application/octet-stream") }]));
        Assert.NotNull(first);
        store.BeforeLargeCommit = null;
        Assert.Equal(data, await ReadFileAsync(store, "a|1"));
        var blobs = store.OffloadedBlobNames();

        var retry = await Record.ExceptionAsync(() => WriteBatchAsync(store, upsert,
            [new FileEntity { Id = "a|1", File = File(body) }, new FileEntity { Id = "b|1", File = TableBlobContent.FromBytes([9], "application/octet-stream") }]));
        output.WriteLine(retry is null ? "retry: success" : $"retry: {retry.GetType().Name}: {retry.Message}");

        RecycledForwardOnlyStreamTests.AssertConsumedAndEmpty(Assert.IsType<InvalidOperationException>(retry));
        Assert.Equal(blobs, store.OffloadedBlobNames());
        Assert.Equal(data, await ReadFileAsync(store, "a|1"));
    }

    // STREAMS-L1. One content over a forward-only stream on two rows, written by two concurrent single writes.
    // W1 prepares the content (reads the stream to its end), uploads and is held before its commit; W2 (the same
    // content, another row) commits; then W1 fails. The retry of W1 with a new content over the consumed stream
    // is refused before anything is uploaded, and a|1 keeps its previous file.
    [Fact]
    public async Task ASharedStreamContent_WhoseWriteFailedAfterAnotherWriteOfItCommitted_RetriedWithANewContent_IsRefusedAndKeepsThePreviousFile()
    {
        var store = Store();
        var old = RandomBytes(5_000, seed: 90);
        await store.UpsertAsync(new FileEntity { Id = "a|1", File = TableBlobContent.FromBytes(old, "application/octet-stream") }, Ct);
        var body = new ReloadableForwardOnlyBody();
        var data = RandomBytes(Size, seed: 91);
        body.Load(data);
        var shared = File(body);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        store.BeforeLargeCommit = () =>
        {
            if (Interlocked.Increment(ref calls) != 1)
                return;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            throw new InvalidOperationException("injected: W1's commit fails");
        };

        var w1 = Task.Run(() => store.UpsertAsync(new FileEntity { Id = "a|1", File = shared }, Ct), Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "W1 did not reach its commit");
        await store.UpsertAsync(new FileEntity { Id = "b|1", File = shared }, Ct);
        release.Set();
        var failed = await Record.ExceptionAsync(() => w1);
        output.WriteLine($"W1: {failed?.GetType().Name}: {failed?.Message}");
        Assert.NotNull(failed);
        store.BeforeLargeCommit = null;
        Assert.Equal(old, await ReadFileAsync(store, "a|1"));
        Assert.Equal(data, await ReadFileAsync(store, "b|1"));
        var blobs = store.OffloadedBlobNames();

        var retry = await Record.ExceptionAsync(() => store.UpsertAsync(new FileEntity { Id = "a|1", File = File(body) }, Ct));
        output.WriteLine(retry is null ? "retry of W1: success" : $"retry of W1: {retry.GetType().Name}: {retry.Message}");

        RecycledForwardOnlyStreamTests.AssertConsumedAndEmpty(Assert.IsType<InvalidOperationException>(retry));
        Assert.Equal(blobs, store.OffloadedBlobNames());
        Assert.Equal(old, await ReadFileAsync(store, "a|1"));
    }

    // STREAMS-L1, the mirror. W1 reads the stream to its end and is held before its commit until W2 (the same
    // content, another row) has reached its own commit; W1 commits, then W2 fails. The retry of W2 with a new
    // content over the consumed stream is refused, and b|1 keeps its previous file.
    [Fact]
    public async Task ASharedStreamContent_WhoseOtherWriteFailedAfterTheReaderCommitted_RetriedWithANewContent_IsRefusedAndKeepsThePreviousFile()
    {
        var store = Store();
        var old = RandomBytes(5_000, seed: 92);
        await store.UpsertAsync(new FileEntity { Id = "b|1", File = TableBlobContent.FromBytes(old, "application/octet-stream") }, Ct);
        var body = new ReloadableForwardOnlyBody();
        var data = RandomBytes(Size, seed: 93);
        body.Load(data);
        var shared = File(body);

        using var w1AtCommit = new ManualResetEventSlim();
        using var w2AtCommit = new ManualResetEventSlim();
        using var w1Done = new ManualResetEventSlim();
        var calls = 0;
        store.BeforeLargeCommit = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                w1AtCommit.Set();
                Assert.True(w2AtCommit.Wait(TimeSpan.FromSeconds(30)), "W2 did not reach its commit");
                return;
            }
            w2AtCommit.Set();
            w1Done.Wait(TimeSpan.FromSeconds(30));
            throw new InvalidOperationException("injected: W2's commit fails");
        };

        var w1 = Task.Run(() => store.UpsertAsync(new FileEntity { Id = "a|1", File = shared }, Ct), Ct);
        Assert.True(w1AtCommit.Wait(TimeSpan.FromSeconds(30)), "W1 did not reach its commit");
        var w2 = Task.Run(() => store.UpsertAsync(new FileEntity { Id = "b|1", File = shared }, Ct), Ct);
        await w1;
        w1Done.Set();
        var failed = await Record.ExceptionAsync(() => w2);
        output.WriteLine($"W2: {failed?.GetType().Name}: {failed?.Message}");
        Assert.NotNull(failed);
        store.BeforeLargeCommit = null;
        Assert.Equal(data, await ReadFileAsync(store, "a|1"));
        Assert.Equal(old, await ReadFileAsync(store, "b|1"));
        var blobs = store.OffloadedBlobNames();

        var retry = await Record.ExceptionAsync(() => store.UpsertAsync(new FileEntity { Id = "b|1", File = File(body) }, Ct));
        output.WriteLine(retry is null ? "retry of W2: success" : $"retry of W2: {retry.GetType().Name}: {retry.Message}");

        RecycledForwardOnlyStreamTests.AssertConsumedAndEmpty(Assert.IsType<InvalidOperationException>(retry));
        Assert.Equal(blobs, store.OffloadedBlobNames());
        Assert.Equal(old, await ReadFileAsync(store, "b|1"));
    }

    // R9 as revised: a new payload of at least one byte on a stream object that a committed write read to its
    // end, and after the batch that failed part-way, is stored whole (the next request on the connection).
    [Fact]
    public async Task AStreamABatchThatFailedPartWayRead_IsStoredWholeWhenItDeliversANewPayload()
    {
        var store = Store();
        var body = new ReloadableForwardOnlyBody();
        body.Load(RandomBytes(Size, seed: 76));
        var transactions = 0;
        store.BeforeLargeCommit = () =>
        {
            if (++transactions == 2)
                throw new InvalidOperationException("injected: the second transaction fails");
        };
        var shared = File(body);
        Assert.NotNull(await Record.ExceptionAsync(() => store.UpsertBatchAsync(
            [new FileEntity { Id = "a|1", File = shared }, new FileEntity { Id = "b|1", File = shared }], Ct)));
        store.BeforeLargeCommit = null;

        var next = RandomBytes(Size, seed: 77);
        body.Load(next);
        var again = File(body);
        await store.UpsertBatchAsync([new FileEntity { Id = "a|1", File = again }, new FileEntity { Id = "b|1", File = again }], Ct);

        Assert.Equal(next, await ReadFileAsync(store, "a|1"));
        Assert.Equal(next, await ReadFileAsync(store, "b|1"));
    }

    // STREAMS-2. A write that read its forward-only stream to its end and was then cancelled (the token fires as
    // the read ends, before the temporary copy is flushed): the stream was read to its end, so a new payload on
    // that stream object is stored whole, and an empty one is refused as after any write that read it to its end.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AForwardOnlyStreamReadToItsEnd_ByAWriteCancelledAfterTheRead_IsStoredWhenItDeliversANewPayload(bool newPayload)
    {
        var store = Store();
        var body = new ReloadableForwardOnlyBody();
        body.Load(RandomBytes(Size, seed: 72));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        body.OnEnd = cts.Cancel;
        var content = File(body);
        var first = await Record.ExceptionAsync(() => store.UpsertAsync(new FileEntity { Id = "p|1", File = content }, cts.Token));
        output.WriteLine($"first write: {first?.GetType().Name}: {first?.Message}");
        Assert.IsAssignableFrom<OperationCanceledException>(first);
        body.OnEnd = null;
        Assert.Null(await store.OneAsync("p|1", Ct));
        Assert.Empty(store.OffloadedBlobNames());

        // The content itself has nothing left to upload from: its temporary copy was not completed.
        var same = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(new FileEntity { Id = "p|1", File = content }, Ct));
        output.WriteLine($"same content: {same.Message}");
        Assert.DoesNotContain("partly read", same.Message, StringComparison.Ordinal);

        var next = RandomBytes(Size, seed: 73);
        body.Load(newPayload ? next : []);
        var outcome = await Record.ExceptionAsync(() => store.UpsertAsync(new FileEntity { Id = "p|2", File = File(body) }, Ct));
        output.WriteLine(outcome is null ? "new content: stored" : $"new content: {outcome.GetType().Name}: {outcome.Message}");
        if (newPayload)
        {
            Assert.Null(outcome);
            Assert.Equal(next, await ReadFileAsync(store, "p|2"));
        }
        else
        {
            RecycledForwardOnlyStreamTests.AssertConsumedAndEmpty(Assert.IsType<InvalidOperationException>(outcome));
            Assert.Null(await store.OneAsync("p|2", Ct));
            Assert.Empty(store.OffloadedBlobNames());
        }
    }
}

/// <summary>
/// <see cref="ConsumedStreamStaysRememberedTests"/> through <see cref="TableStorage{T}"/> and the real SDK against
/// the local Azurite (STREAMS-1 and STREAMS-L1; the mechanism is the shared coordinator and content code).
/// </summary>
public sealed class AzuriteConsumedStreamStaysRememberedTests(ITestOutputHelper output) : AzuriteContentTestBase
{
    private const int Size = 300_000;

    private static TableBlobContent File(Stream stream) => TableBlobContent.FromStream(stream, "application/octet-stream");

    private static async Task<byte[]> ReadAsync(TableStorage<FileEntity> store, string id)
    {
        var read = await store.OneAsync(id, Ct);
        await using var stream = await read!.File!.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    // STREAMS-1, shape A, on both batch kinds: the second partition's blob upload fails (503 from a per-call
    // policy), so its transaction fails after the first partition's committed. The retry of the call with a new
    // content over the consumed stream is refused before anything is uploaded; the committed row keeps its file.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABatch_ASharedStreamContent_WhoseLaterTransactionFailed_RetriedWithANewContent_IsRefusedAndKeepsTheCommittedFile(bool upsert)
    {
        Assert.SkipUnless(Available, SkipReason);
        // A second partition of this test's own; its rows are deleted at the end.
        var pkB = Pk + "b";
        var policy = new FailBlobRequestsPolicy();
        var store = Store<FileEntity>(blobs: BlobServiceWith(policy));
        try
        {
            var body = new ReloadableForwardOnlyBody();
            var data = RandomBytes(Size, seed: 81);
            body.Load(data);
            var shared = File(body);
            policy.Marker = "/" + pkB + "/";
            var first = await Record.ExceptionAsync(() => Batch(store, upsert,
                [new FileEntity { Id = Pk + "|1", File = shared }, new FileEntity { Id = pkB + "|1", File = shared }]));
            output.WriteLine($"first call: {first?.GetType().Name}: {first?.Message}");
            Assert.NotNull(first);
            policy.Marker = null;
            Assert.Equal(data, await ReadAsync(store, Pk + "|1"));
            var blobs = (await BlobsAsync<FileEntity>()).ConvertAll(b => b.Name);

            var again = File(body);
            var retry = await Record.ExceptionAsync(() => Batch(store, upsert,
                [new FileEntity { Id = Pk + "|1", File = again }, new FileEntity { Id = pkB + "|1", File = again }]));
            output.WriteLine(retry is null ? "retry: success" : $"retry: {retry.GetType().Name}: {retry.Message}");

            RecycledForwardOnlyStreamTests.AssertConsumedAndEmpty(Assert.IsType<InvalidOperationException>(retry));
            Assert.Equal(blobs, (await BlobsAsync<FileEntity>()).ConvertAll(b => b.Name));
            Assert.Equal(data, await ReadAsync(Store<FileEntity>(), Pk + "|1"));
            Assert.Null(await Store<FileEntity>().OneAsync(pkB + "|1", Ct));
        }
        finally
        {
            await DeletePartitionAsync(pkB);
        }
    }

    // STREAMS-L1 on TableStorage: one content on two rows. The write that read the stream fails after its upload
    // (a create over an existing row), the other write of the content commits; and the mirror, where the reader
    // commits and the other write fails. The retry of the failed write with a new content over the consumed
    // stream is refused before anything is uploaded, and its row keeps its previous file.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASharedStreamContent_OneOfWhoseWritesFailedAndTheOtherCommitted_RetriedWithANewContent_IsRefusedAndKeepsThePreviousFile(bool readerFails)
    {
        Assert.SkipUnless(Available, SkipReason);
        var store = Store<FileEntity>(Offload(TimeSpan.Zero));
        var old = RandomBytes(5_000, seed: 94);
        var failing = readerFails ? Id("a") : Id("b");
        var committing = readerFails ? Id("b") : Id("a");
        await store.CreateAsync(new FileEntity { Id = failing, File = TableBlobContent.FromBytes(old, "application/octet-stream") }, Ct);
        var body = new ReloadableForwardOnlyBody();
        var data = RandomBytes(Size, seed: 95);
        body.Load(data);
        var shared = File(body);

        // The first write reads the stream; a create over the existing row fails after its upload.
        if (readerFails)
        {
            await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new FileEntity { Id = failing, File = shared }, Ct));
            await store.UpsertAsync(new FileEntity { Id = committing, File = shared }, Ct);
        }
        else
        {
            await store.UpsertAsync(new FileEntity { Id = committing, File = shared }, Ct);
            await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new FileEntity { Id = failing, File = shared }, Ct));
        }
        Assert.Equal(data, await ReadAsync(Store<FileEntity>(), committing));
        var blobs = (await BlobsAsync<FileEntity>()).ConvertAll(b => b.Name);

        var retry = await Record.ExceptionAsync(() => store.UpsertAsync(new FileEntity { Id = failing, File = File(body) }, Ct));
        output.WriteLine(retry is null ? "retry: success" : $"retry: {retry.GetType().Name}: {retry.Message}");

        RecycledForwardOnlyStreamTests.AssertConsumedAndEmpty(Assert.IsType<InvalidOperationException>(retry));
        Assert.Equal(blobs, (await BlobsAsync<FileEntity>()).ConvertAll(b => b.Name));
        Assert.Equal(old, await ReadAsync(Store<FileEntity>(), failing));
    }

    private static Task Batch(TableStorage<FileEntity> store, bool upsert, FileEntity[] batch) =>
        upsert ? store.UpsertBatchAsync(batch, Ct) : store.CreateBatchAsync(batch, Ct);

    private async Task DeletePartitionAsync(string pk)
    {
        var client = TableService.GetTableClient(nameof(FileEntity));
        try
        {
            var keys = new List<string>();
            var filter = Azure.Data.Tables.TableClient.CreateQueryFilter($"PartitionKey eq {pk}");
            await foreach (var row in client.QueryAsync<Azure.Data.Tables.TableEntity>(filter, select: ["RowKey"], cancellationToken: CancellationToken.None))
                keys.Add(row.RowKey);
            foreach (var rk in keys)
                await client.DeleteEntityAsync(pk, rk, Azure.ETag.All, CancellationToken.None);
        }
        catch (Azure.RequestFailedException)
        {
            // The table was never created, or a row is already gone.
        }
    }
}

/// <summary>Fails (once armed) every blob request whose URI contains <see cref="Marker"/>.</summary>
internal sealed class FailBlobRequestsPolicy : Azure.Core.Pipeline.HttpPipelineSynchronousPolicy
{
    public string? Marker { get; set; }

    public override void OnSendingRequest(Azure.Core.HttpMessage message)
    {
        if (Marker is { } marker && message.Request.Uri.ToString().Contains(marker, StringComparison.Ordinal))
            throw new Azure.RequestFailedException(503, "injected: the blob service is unavailable");
    }
}

/// <summary>
/// A forward-only request body whose stream object is given the next body by <see cref="Load"/> (a web server
/// reuses one request-body object for every request of a connection). <see cref="OnEnd"/> runs when a read
/// reports the end of the body.
/// </summary>
internal sealed class ReloadableForwardOnlyBody : Stream
{
    private byte[] data = [];
    private int position;

    public Action? OnEnd { get; set; }

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
        if (n == 0)
            OnEnd?.Invoke();
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
