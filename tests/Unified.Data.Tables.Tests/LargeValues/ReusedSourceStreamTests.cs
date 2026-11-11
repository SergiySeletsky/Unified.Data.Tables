using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>An entity with two files, for the tests of one stream behind two contents.</summary>
public class TwoFilesFromOneStreamEntity : Entity
{
    public TableBlobContent? First { get; set; }

    public TableBlobContent? Second { get; set; }
}

/// <summary>
/// A new <see cref="TableBlobContent"/> created over a stream that an earlier write attempt read
/// (BLOBS-2 of the fix-wave-2 review): what the read-modify-write helpers, and a caller's own retry
/// loop, do when they build the entity again. A seekable stream is left where the content started
/// whenever the library is done reading it, so the new content stores exactly the same bytes. A
/// forward-only stream cannot be read again: a new content over it is refused before anything is
/// uploaded, and the row keeps its previous file.
/// </summary>
public class ReusedSourceStreamTests(ITestOutputHelper output)
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

    // Another writer changes the row once, between a write's upload and its row write.
    private static void RaceOnce(InMemoryStorage<FileEntity> store, string id = "p|r")
    {
        var raced = false;
        store.BeforeLargeCommit = () =>
        {
            if (raced) return;
            raced = true;
            store.UpdateAsync(id, b => b.SetProperty(e => e.Title, "changed by another writer"), Ct).GetAwaiter().GetResult();
        };
    }

    /// <summary>The refusal of a new content over a forward-only stream a write has already read: it says so and names the remedies.</summary>
    internal static void AssertAlreadyReadForwardOnly(InvalidOperationException ex)
    {
        Assert.Contains("not seekable", ex.Message, StringComparison.Ordinal);
        Assert.Contains("already read", ex.Message, StringComparison.Ordinal);
        Assert.Contains("seekable stream", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FromBytes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("open the source again", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MutateAsync_SettingAFileFromASeekableStream_RetriedAfterAConflictAfterTheUpload_StoresTheWholeFile()
    {
        var data = RandomBytes(Size, seed: 201);
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|r", Title = "v1" }, Ct);
        using var stream = new MemoryStream(data);
        RaceOnce(store);

        var attempts = 0;
        var saved = await store.MutateAsync("p|r", e =>
        {
            attempts++;
            e.File = File(stream);
        }, ct: Ct);

        var read = await store.OneAsync("p|r", Ct);
        output.WriteLine($"attempts {attempts}; stream at {stream.Position:N0}; stored {read!.File!.Length:N0}; returned {saved.File!.Length:N0}");
        Assert.Equal(2, attempts);
        Assert.Equal(data, await ReadFileAsync(read.File));
        Assert.Equal(Size, saved.File.Length);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task MutateAsync_SettingAFileFromAForwardOnlyStream_RetriedAfterAConflict_IsRefusedAndKeepsThePreviousFile()
    {
        var data = RandomBytes(Size, seed: 205);
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|r", Title = "v1", File = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") }, Ct);
        var stream = new TurnTakingForwardOnlyStream(data);
        RaceOnce(store);

        var attempts = 0;
        var outcome = await Record.ExceptionAsync(() => store.MutateAsync("p|r", e =>
        {
            attempts++;
            e.File = File(stream);
        }, ct: Ct));

        AssertAlreadyReadForwardOnly(Assert.IsType<InvalidOperationException>(outcome));
        Assert.Equal(2, attempts);
        var read = await store.OneAsync("p|r", Ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, await ReadFileAsync(read!.File!));
    }

    [Theory]
    [InlineData("TryMutateAsync")]
    [InlineData("TryTransitionAsync")]
    [InlineData("MutateOrCreateAsync")]
    public async Task TheOtherRetryHelpers_SettingAFileFromASeekableStream_RetriedAfterAConflict_StoreTheWholeFile(string helper)
    {
        var data = RandomBytes(Size, seed: 9203);
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|r", Title = "v1", File = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") }, Ct);
        using var stream = new MemoryStream(data);
        RaceOnce(store);
        var attempts = 0;
        void Set(FileEntity e)
        {
            attempts++;
            e.File = File(stream);
        }

        switch (helper)
        {
            case "TryMutateAsync":
                Assert.Equal(MutationStatus.Updated, (await store.TryMutateAsync("p|r", Set, ct: Ct)).Status);
                break;
            case "TryTransitionAsync":
                Assert.Equal(MutationStatus.Updated, (await store.TryTransitionAsync("p|r", _ => true, Set, ct: Ct)).Status);
                break;
            default:
                await store.MutateOrCreateAsync("p|r", () => new FileEntity(), Set, ct: Ct);
                break;
        }

        var read = await store.OneAsync("p|r", Ct);
        Assert.Equal(2, attempts);
        Assert.Equal(data, await ReadFileAsync(read!.File!));
    }

    [Theory]
    [InlineData("TryMutateAsync")]
    [InlineData("TryTransitionAsync")]
    [InlineData("MutateOrCreateAsync")]
    public async Task TheOtherRetryHelpers_SettingAFileFromAForwardOnlyStream_RetriedAfterAConflict_AreRefusedAndKeepThePreviousFile(string helper)
    {
        var data = RandomBytes(Size, seed: 9204);
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|r", Title = "v1", File = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") }, Ct);
        var stream = new TurnTakingForwardOnlyStream(data);
        RaceOnce(store);
        void Set(FileEntity e) => e.File = File(stream);

        var outcome = await Record.ExceptionAsync(() => helper switch
        {
            "TryMutateAsync" => store.TryMutateAsync("p|r", Set, ct: Ct),
            "TryTransitionAsync" => store.TryTransitionAsync("p|r", _ => true, Set, ct: Ct),
            _ => (Task)store.MutateOrCreateAsync("p|r", () => new FileEntity(), Set, ct: Ct),
        });

        AssertAlreadyReadForwardOnly(Assert.IsType<InvalidOperationException>(outcome));
        Assert.Equal(new byte[] { 1, 2, 3 }, await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
    }

    [Fact]
    public async Task ASecondContentOverTheSameStream_AfterAWriteThatFailedAtItsRowWrite_StoresTheWholeFile()
    {
        var data = RandomBytes(Size, seed: 202);
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|taken", Title = "someone else's" }, Ct);
        using var stream = new MemoryStream(data);

        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new FileEntity { Id = "p|taken", File = File(stream) }, Ct));
        Assert.Equal(0, stream.Position);

        await store.CreateAsync(new FileEntity { Id = "p|free", File = File(stream) }, Ct);

        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|free", Ct))!.File!));
    }

    [Fact]
    public async Task ARetryLoopAroundAnUpsertThatBuildsTheEntityFromTheStream_StoresTheWholeFile()
    {
        var data = RandomBytes(Size, seed: 206);
        var store = Store();
        using var stream = new MemoryStream(data);
        var transient = 1;
        store.BeforeLargeCommit = () =>
        {
            if (transient-- > 0) throw new IOException("simulated transient failure of the row write");
        };

        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                await store.UpsertAsync(new FileEntity { Id = "p|r", File = File(stream) }, Ct);
                break;
            }
            catch (IOException) when (attempts < 3)
            {
            }
        }

        Assert.Equal(2, attempts);
        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
    }

    [Fact]
    public async Task ARetryThatBuildsTheEntityAgain_AfterTheSourceFailedDuringTheUpload_StoresTheWholeFileNotTheTail()
    {
        const int size = 1_000_000;
        var data = RandomBytes(size, seed: 207);
        var store = Store();
        // The hashing pass gets through; the upload's pass fails once at the same offset.
        var stream = new FaultyContentStream(data, seekable: true) { FailAt = 327_680, PassesBeforeFailure = 1, FailuresLeft = 1 };

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(new FileEntity { Id = "p|r", File = File(stream) }, Ct));
        Assert.Equal(0, stream.Position);

        await store.CreateAsync(new FileEntity { Id = "p|r", File = File(stream) }, Ct);

        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
    }

    /// <summary>A write cancelled during its upload leaves the stream where the content started (here not at 0).</summary>
    [Fact]
    public async Task AWriteCancelledDuringItsUpload_LeavesTheStreamWhereTheContentStarted_SoANewContentStoresTheSameBytes()
    {
        const int start = 1_000;
        var data = RandomBytes(Size, seed: 210);
        var store = Store();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var stream = new FaultyContentStream(data, seekable: true, start) { FailAt = 100_000, PassesBeforeFailure = 1, CancelAtFailAt = cts };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(new FileEntity { Id = "p|r", File = File(stream) }, cts.Token));
        Assert.Equal(start, stream.Position);
        Assert.Null(await store.OneAsync("p|r", Ct));

        await store.CreateAsync(new FileEntity { Id = "p|r", File = File(stream) }, Ct);

        Assert.Equal(data[start..], await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
    }

    /// <summary>A successful write leaves a seekable stream where the content started, too.</summary>
    [Fact]
    public async Task ASuccessfulWrite_LeavesASeekableStreamWhereTheContentStarted()
    {
        const int start = 1_000;
        var data = RandomBytes(Size, seed: 211);
        var store = Store();
        var stream = new FaultyContentStream(data, seekable: true, start);

        await store.CreateAsync(new FileEntity { Id = "p|r", File = File(stream) }, Ct);

        Assert.Equal(start, stream.Position);
    }

    /// <summary>
    /// The upload failed part-way and the stream could not be put back (seeking now throws): a new
    /// content over that stream is refused before anything is uploaded, never stores the tail.
    /// </summary>
    [Fact]
    public async Task ANewContentOverASeekableStreamThatCouldNotBePutBack_IsRefusedWithInvalidOperation()
    {
        var data = RandomBytes(Size, seed: 212);
        var store = Store();
        var stream = new FaultyContentStream(data, seekable: true) { FailAt = 100_000, PassesBeforeFailure = 1, FailuresLeft = 1, RefuseRewindAfterFailure = true };

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(new FileEntity { Id = "p|r", File = File(stream) }, Ct));
        Assert.Equal(100_000, stream.Position);
        var blobs = store.OffloadedBlobNames();

        var retry = await Record.ExceptionAsync(() => store.CreateAsync(new FileEntity { Id = "p|r", File = File(stream) }, Ct));

        var refused = Assert.IsType<InvalidOperationException>(retry);
        Assert.Contains("could not put it back", refused.Message, StringComparison.Ordinal);
        Assert.Null(await store.OneAsync("p|r", Ct));
        Assert.Equal(blobs, store.OffloadedBlobNames());
    }

    /// <summary>
    /// A forward-only stream that a successful write has read: that write committed, so the stream is
    /// forgotten and a new content over the same object is a new payload, whatever it delivers, an empty
    /// one included (ruling R9 point 1 of fix wave 4, CONTENT-1: a server reuses one request-body object
    /// for every request of a connection). Before R9 this content was refused for the life of the stream.
    /// The refusal after a write that did NOT commit is <see cref="MutateAsync_SettingAFileFromAForwardOnlyStream_RetriedAfterAConflict_IsRefusedAndKeepsThePreviousFile"/>.
    /// </summary>
    [Fact]
    public async Task ANewContentOverAForwardOnlyStreamThatACommittedWriteHasRead_IsANewPayload_AnEmptyOneIncluded()
    {
        var data = RandomBytes(Size, seed: 213);
        var store = Store();
        var stream = new FaultyContentStream(data, seekable: false);
        await store.CreateAsync(new FileEntity { Id = "p|a", File = File(stream) }, Ct);

        await store.CreateAsync(new FileEntity { Id = "p|b", File = File(stream) }, Ct);

        Assert.Equal(0, (await store.OneAsync("p|b", Ct))!.File!.Length);
        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|a", Ct))!.File!));
    }

    /// <summary>
    /// Two contents over one forward-only stream on two properties of one entity: the second would
    /// find the stream at its end and store an empty file; it is refused, and nothing is written.
    /// </summary>
    [Fact]
    public async Task TwoContentsOverOneForwardOnlyStream_OnOneEntity_AreRefusedAndNothingIsWritten()
    {
        var data = RandomBytes(Size, seed: 214);
        var store = new InMemoryStorage<TwoFilesFromOneStreamEntity>(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });
        var stream = new FaultyContentStream(data, seekable: false);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateAsync(new TwoFilesFromOneStreamEntity { Id = "p|r", First = File(stream), Second = File(stream) }, Ct));

        AssertAlreadyReadForwardOnly(refused);
        Assert.Null(await store.OneAsync("p|r", Ct));
        Assert.Empty(store.OffloadedBlobNames());
    }

    // ── Controls: what already worked and must keep working ─────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MutateAsync_AssigningOneContentCreatedOutsideTheDelegate_StoresTheWholeFile(bool seekable)
    {
        var data = RandomBytes(Size, seed: 9201);
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|r", Title = "v1" }, Ct);
        Stream stream = seekable ? new MemoryStream(data) : new TurnTakingForwardOnlyStream(data);
        var content = File(stream);
        RaceOnce(store);

        var attempts = 0;
        await store.MutateAsync("p|r", e =>
        {
            attempts++;
            e.File = content;
        }, ct: Ct);

        Assert.Equal(2, attempts);
        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
    }

    [Fact]
    public async Task MutateAsync_OpeningTheSourceAgainInsideTheDelegate_StoresTheWholeFile()
    {
        var data = RandomBytes(Size, seed: 9202);
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|r", Title = "v1" }, Ct);
        RaceOnce(store);

        var attempts = 0;
        await store.MutateAsync("p|r", e =>
        {
            attempts++;
            e.File = File(new TurnTakingForwardOnlyStream(data));
        }, ct: Ct);

        Assert.Equal(2, attempts);
        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
    }

    [Fact]
    public async Task ASecondContentOverTheSameStream_AfterAWriteThatFailedBeforeItsUpload_StoresTheWholeFile()
    {
        var data = RandomBytes(Size, seed: 203);
        var store = Store();
        var created = await store.CreateAsync(new FileEntity { Id = "p|r", Title = "v1" }, Ct);
        await store.UpdateAsync("p|r", b => b.SetProperty(e => e.Title, "v2"), Ct);
        using var stream = new MemoryStream(data);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpdateAsync(new FileEntity { Id = "p|r", ETag = created.ETag, File = File(stream) }, ConcurrencyMode.Strict, Ct));
        Assert.Equal(0, stream.Position);

        await store.UpsertAsync(new FileEntity { Id = "p|r", File = File(stream) }, Ct);

        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
    }

    [Fact]
    public async Task TwoContentsCreatedOverOneSeekableStream_InOneBatchOrOnOneEntity_BothStoreTheWholeFile()
    {
        var data = RandomBytes(Size, seed: 209);
        var store = Store();
        using var stream = new MemoryStream(data);
        await store.CreateBatchAsync(
        [
            new FileEntity { Id = "p|a", File = File(stream) },
            new FileEntity { Id = "p|b", File = File(stream) },
        ], Ct);
        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|a", Ct))!.File!));
        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|b", Ct))!.File!));

        var two = new InMemoryStorage<TwoFilesFromOneStreamEntity>(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });
        using var other = new MemoryStream(data);
        await two.CreateAsync(new TwoFilesFromOneStreamEntity { Id = "p|c", First = File(other), Second = File(other) }, Ct);
        var read = await two.OneAsync("p|c", Ct);
        Assert.Equal(data, await ReadFileAsync(read!.First!));
        Assert.Equal(data, await ReadFileAsync(read.Second!));
    }
}

/// <summary><see cref="ReusedSourceStreamTests"/> through <see cref="TableStorage{T}"/> and the real SDK against the local Azurite.</summary>
public sealed class AzuriteReusedSourceStreamTests(ITestOutputHelper output) : AzuriteContentTestBase
{
    private const int Size = 1_000_000;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("UNIFIED_TABLES_TEST_STORAGE") is { Length: > 0 } configured ? configured : "UseDevelopmentStorage=true";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MutateAsync_SettingAFileFromTheCallersStream_RetriedAfterAConflictAfterTheUpload_StoresTheWholeFileOrIsRefused(bool seekable)
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 402);
        var id = Id("m");
        await Store<FileEntity>().CreateAsync(new FileEntity { Id = id, Title = "v1", File = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") }, Ct);
        Stream stream = seekable ? new MemoryStream(data) : new TurnTakingForwardOnlyStream(data);

        // Another writer merges a column into the row once, just before this store's first row write
        // (its conditional PUT) is sent: after the blob upload.
        var raw = TableService.GetTableClient(nameof(FileEntity));
        var race = new BeforeFirstEntityPut(async () =>
            await raw.UpdateEntityAsync(new TableEntity(Pk, "m") { ["Title"] = "changed by another writer" }, ETag.All, TableUpdateMode.Merge, Ct));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var options = new TableClientOptions();
        options.AddPolicy(race, HttpPipelinePosition.PerCall);
        var store = new TableStorage<FileEntity>(
            new TableServiceClient(ConnectionString, options), cache, NullLogger<TableStorage<FileEntity>>.Instance,
            authorizer: null, new UnifiedTableStorageOptions { BlobOffload = Offload(TimeSpan.Zero) }, BlobService);

        var attempts = 0;
        var outcome = await Record.ExceptionAsync(() => store.MutateAsync(id, e =>
        {
            attempts++;
            e.File = TableBlobContent.FromStream(stream, "application/octet-stream");
        }, ct: Ct));

        var read = await Store<FileEntity>().OneAsync(id, Ct);
        output.WriteLine($"seekable {seekable}: conflicts {race.Fired}; attempts {attempts}; outcome {outcome?.GetType().Name ?? "success"}; stored {read!.File!.Length:N0}");
        Assert.Equal(1, race.Fired);
        Assert.Equal(2, attempts);
        if (seekable)
        {
            Assert.Null(outcome);
            Assert.Equal(Size, read.File.Length);
            Assert.Equal(0, stream.Position);
        }
        else
        {
            ReusedSourceStreamTests.AssertAlreadyReadForwardOnly(Assert.IsType<InvalidOperationException>(outcome));
            Assert.Equal(3, read.File.Length);
        }
    }

    /// <summary>Runs an action once, before the first PUT of an entity (a conditional replace) is sent.</summary>
    private sealed class BeforeFirstEntityPut(Func<Task> action) : HttpPipelinePolicy
    {
        private int fired;

        public int Fired => Volatile.Read(ref fired);

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (Matches(message) && Interlocked.Exchange(ref fired, 1) == 0)
                await action();
            await ProcessNextAsync(message, pipeline);
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (Matches(message) && Interlocked.Exchange(ref fired, 1) == 0)
                action().GetAwaiter().GetResult();
            ProcessNext(message, pipeline);
        }

        private static bool Matches(HttpMessage message) =>
            message.Request.Method == RequestMethod.Put
            && message.Request.Uri.Path.Contains("(PartitionKey=", StringComparison.Ordinal);
    }
}
