using System.IO.Compression;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A retry that wraps the stream an earlier attempt consumed in a new framework wrapper (CONTENT-2 of the
/// fix-wave-3 review; ruling R9 point 4). <see cref="BufferedStream"/> and the compression streams expose
/// the stream they read, so what a write remembers about a stream holds for every stream on the content's
/// chain: the retry either stores exactly the bytes the first attempt was given, or is refused with
/// <see cref="InvalidOperationException"/> before anything is uploaded and the row keeps its previous file.
/// Never an empty or shorter file with success. (A user-written wrapper that does not expose its inner
/// stream cannot be seen through; that limitation is documented, not tested here.)
/// </summary>
public class NewWrapperOverAConsumedStreamTests(ITestOutputHelper output)
{
    private const int Size = 300_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemoryStorage<FileEntity> Store() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero } });

    private static async Task<byte[]> ReadFileAsync(TableBlobContent content)
    {
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    // Another writer changes the row once, between a write's upload and its row write.
    private static void RaceOnce(InMemoryStorage<FileEntity> store, string id)
    {
        var raced = false;
        store.BeforeLargeCommit = () =>
        {
            if (raced) return;
            raced = true;
            store.UpdateAsync(id, b => b.SetProperty(e => e.Title, "changed by another writer"), Ct).GetAwaiter().GetResult();
        };
    }

    // The bytes on the wire for a wrapper: compressed for a decompressing one, as they are otherwise.
    private static byte[] Wire(string wrapper, byte[] data)
    {
        var codec = wrapper.Replace("BufferedStream over ", string.Empty, StringComparison.Ordinal);
        if (codec == "BufferedStream")
            return data;
        using var buffer = new MemoryStream();
        using (var compressor = Compressor(codec, buffer))
            compressor.Write(data);
        return buffer.ToArray();
    }

    private static Stream Compressor(string codec, Stream target) => codec switch
    {
        "GZipStream" => new GZipStream(target, CompressionLevel.Fastest, leaveOpen: true),
        "DeflateStream" => new DeflateStream(target, CompressionLevel.Fastest, leaveOpen: true),
        "ZLibStream" => new ZLibStream(target, CompressionLevel.Fastest, leaveOpen: true),
        "BrotliStream" => new BrotliStream(target, CompressionLevel.Fastest, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null),
    };

    // A new wrapper over the caller's stream, as a delegate that "opens the source again" builds it.
    private static Stream Wrap(string wrapper, Stream inner) => wrapper switch
    {
        "BufferedStream" => new BufferedStream(inner),
        "GZipStream" => new GZipStream(inner, CompressionMode.Decompress, leaveOpen: true),
        "DeflateStream" => new DeflateStream(inner, CompressionMode.Decompress, leaveOpen: true),
        "ZLibStream" => new ZLibStream(inner, CompressionMode.Decompress, leaveOpen: true),
        "BrotliStream" => new BrotliStream(inner, CompressionMode.Decompress, leaveOpen: true),
        // Two wrappers deep: buffering over decompression.
        _ when wrapper.StartsWith("BufferedStream over ", StringComparison.Ordinal) =>
            new BufferedStream(Wrap(wrapper["BufferedStream over ".Length..], inner)),
        _ => throw new ArgumentOutOfRangeException(nameof(wrapper), wrapper, null),
    };

    private void AssertWholeOrRefusedKeepingThePreviousFile(string label, int attempts, Exception? outcome, byte[] data, byte[] stored)
    {
        output.WriteLine($"{label}: attempts {attempts}, outcome {(outcome is null ? "success" : outcome.GetType().Name + ": " + outcome.Message)}, stored {stored.Length:N0} of {data.Length:N0} bytes");
        Assert.Equal(2, attempts);
        if (outcome is null)
        {
            Assert.Equal(data, stored);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(outcome);
            Assert.Equal(new byte[] { 1, 2, 3 }, stored);
        }
    }

    /// <summary>CONTENT-2: a new wrapper on each attempt over the caller's forward-only stream (a request body).</summary>
    [Theory]
    [InlineData("BufferedStream")]
    [InlineData("GZipStream")]
    [InlineData("DeflateStream")]
    [InlineData("ZLibStream")]
    [InlineData("BrotliStream")]
    [InlineData("BufferedStream over GZipStream")]
    [InlineData("BufferedStream over BrotliStream")]
    public async Task MutateAsync_WrappingTheCallersForwardOnlyStreamInANewStreamOnEachAttempt_NeverReportsSuccessWithAShorterFile(string wrapper)
    {
        var data = RandomBytes(Size, seed: 41);
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|r", Title = "v1", File = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") }, Ct);
        var requestBody = new TurnTakingForwardOnlyStream(Wire(wrapper, data));
        RaceOnce(store, "p|r");
        var blobsBefore = 0;

        var attempts = 0;
        var outcome = await Record.ExceptionAsync(() => store.MutateAsync("p|r", e =>
        {
            if (++attempts == 2)
                blobsBefore = store.OffloadedBlobNames().Count;
            e.File = TableBlobContent.FromStream(Wrap(wrapper, requestBody), "application/octet-stream");
        }, ct: Ct));

        var stored = await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!);
        AssertWholeOrRefusedKeepingThePreviousFile(wrapper, attempts, outcome, data, stored);
        // Refused before anything was uploaded.
        if (outcome is not null)
            Assert.Equal(blobsBefore, store.OffloadedBlobNames().Count);
    }

    /// <summary>CONTENT-2, the verifier's variant: the stream under the new non-seekable wrapper is seekable, left at its end by the first attempt.</summary>
    [Theory]
    [InlineData("GZipStream")]
    [InlineData("DeflateStream")]
    [InlineData("ZLibStream")]
    [InlineData("BrotliStream")]
    [InlineData("BufferedStream over GZipStream")]
    public async Task MutateAsync_WrappingTheCallersSeekableStreamInANewStreamOnEachAttempt_NeverReportsSuccessWithAShorterFile(string wrapper)
    {
        var data = RandomBytes(Size, seed: 42);
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|s", Title = "v1", File = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") }, Ct);
        var inner = new MemoryStream(Wire(wrapper, data));
        RaceOnce(store, "p|s");

        var attempts = 0;
        var outcome = await Record.ExceptionAsync(() => store.MutateAsync("p|s", e =>
        {
            attempts++;
            e.File = TableBlobContent.FromStream(Wrap(wrapper, inner), "application/octet-stream");
        }, ct: Ct));

        var stored = await ReadFileAsync((await store.OneAsync("p|s", Ct))!.File!);
        AssertWholeOrRefusedKeepingThePreviousFile(wrapper + " over a seekable stream", attempts, outcome, data, stored);
    }

    /// <summary>Control: a delegate that puts the caller's seekable stream back before wrapping it again stores the whole file.</summary>
    [Theory]
    [InlineData("GZipStream")]
    [InlineData("DeflateStream")]
    [InlineData("ZLibStream")]
    [InlineData("BrotliStream")]
    [InlineData("BufferedStream over GZipStream")]
    public async Task MutateAsync_WrappingTheCallersSeekableStreamAgainAfterRewindingIt_StoresTheWholeFile(string wrapper)
    {
        var data = RandomBytes(Size, seed: 43);
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|w", Title = "v1", File = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") }, Ct);
        var inner = new MemoryStream(Wire(wrapper, data));
        RaceOnce(store, "p|w");

        var attempts = 0;
        await store.MutateAsync("p|w", e =>
        {
            attempts++;
            inner.Position = 0;
            e.File = TableBlobContent.FromStream(Wrap(wrapper, inner), "application/octet-stream");
        }, ct: Ct);

        Assert.Equal(2, attempts);
        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|w", Ct))!.File!));
    }

    /// <summary>
    /// After a failed write, a new wrapper over the inner stream that was given a new payload is stored
    /// whole; and after a write over a wrapper has committed, the inner stream stays remembered (ruling R9 as
    /// revised in fix wave 5; wave 4 forgot it): read directly, it is refused when it delivers nothing.
    /// </summary>
    [Fact]
    public async Task ANewWrapperOverARecycledInnerStream_IsStoredWhole_AfterAFailedWriteAndAfterACommittedOne()
    {
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|taken", Title = "someone else's" }, Ct);
        var body = new RecycledForwardOnlyBodyStream();
        body.Load(Wire("GZipStream", RandomBytes(Size, seed: 44)));
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(
            new FileEntity { Id = "p|taken", File = TableBlobContent.FromStream(Wrap("GZipStream", body), "application/octet-stream") }, Ct));

        var second = RandomBytes(Size, seed: 45);
        body.Load(Wire("GZipStream", second));
        await store.CreateAsync(new FileEntity { Id = "p|a", File = TableBlobContent.FromStream(Wrap("GZipStream", body), "application/octet-stream") }, Ct);
        Assert.Equal(second, await ReadFileAsync((await store.OneAsync("p|a", Ct))!.File!));

        // Committed: the inner stream is still remembered, also read directly; an empty payload is refused.
        body.Load([]);
        RecycledForwardOnlyStreamTests.AssertConsumedAndEmpty(await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateAsync(new FileEntity { Id = "p|b", File = TableBlobContent.FromStream(body, "application/octet-stream") }, Ct)));
        Assert.Null(await store.OneAsync("p|b", Ct));

        // A new payload under a new wrapper after that commit is stored whole.
        var third = RandomBytes(Size, seed: 47);
        body.Load(Wire("GZipStream", third));
        await store.CreateAsync(new FileEntity { Id = "p|c", File = TableBlobContent.FromStream(Wrap("GZipStream", body), "application/octet-stream") }, Ct);
        Assert.Equal(third, await ReadFileAsync((await store.OneAsync("p|c", Ct))!.File!));
    }

    /// <summary>The inner stream a failed write read only part of, through a wrapper, is refused under a new wrapper too.</summary>
    [Fact]
    public async Task ANewWrapperOverAnInnerStreamAFailedWriteReadOnlyPartOf_IsRefused()
    {
        var store = Store();
        var inner = new FaultyContentStream(RandomBytes(Size, seed: 46), seekable: false) { FailAt = 100_000, FailuresLeft = 1 };
        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(
            new FileEntity { Id = "p|r", File = TableBlobContent.FromStream(new BufferedStream(inner), "application/octet-stream") }, Ct));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(
            new FileEntity { Id = "p|r", File = TableBlobContent.FromStream(new BufferedStream(inner), "application/octet-stream") }, Ct));

        output.WriteLine(refused.Message);
        Assert.Contains("partly read", refused.Message, StringComparison.Ordinal);
        Assert.Null(await store.OneAsync("p|r", Ct));
        Assert.Empty(store.OffloadedBlobNames());
    }
}
