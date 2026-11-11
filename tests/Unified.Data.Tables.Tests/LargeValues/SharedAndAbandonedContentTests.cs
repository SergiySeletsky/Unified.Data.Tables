using Azure.Core;
using Azure.Core.Pipeline;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// One <see cref="TableBlobContent"/> assigned to more than one entity. A forward-only source is copied
/// to a temporary file once; the first upload binds the content to its blob and releases that copy, so
/// every later upload of the same content reads the blob it is bound to by then. Both entities end up
/// stored with the full content.
/// </summary>
public class SharedBlobContentTests
{
    private const int Size = 300_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemoryStorage<FileEntity> Store() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });

    private static TableBlobContent ForwardOnly(byte[] data) =>
        TableBlobContent.FromStream(new FaultyStream(data, seekable: false), "application/octet-stream");

    private static async Task AssertStoredAsync(InMemoryStorage<FileEntity> store, string id, byte[] expected)
    {
        var read = await store.OneAsync(id, Ct);
        Assert.NotNull(read?.File);
        Assert.Equal(expected.Length, read.File.Length);
        await using var stream = await read.File.OpenReadAsync(Ct);
        Assert.Equal(expected, await ReadAllAsync(stream));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneForwardOnlyContent_OnTwoEntitiesOfOneBatch_StoresBothInFull(bool upsert)
    {
        var data = RandomBytes(Size, seed: 81);
        var shared = ForwardOnly(data);
        var store = Store();
        FileEntity[] batch = [new() { Id = "p|a", File = shared }, new() { Id = "p|b", File = shared }];

        Assert.Equal(2, upsert ? await store.UpsertBatchAsync(batch, Ct) : await store.CreateBatchAsync(batch, Ct));

        await AssertStoredAsync(store, "p|a", data);
        await AssertStoredAsync(store, "p|b", data);
        Assert.Equal(2, store.Blobs.Names.Count);
        // The content itself still reads (it is bound to a blob that holds it).
        await using var back = await shared.OpenReadAsync(Ct);
        Assert.Equal(data, await ReadAllAsync(back));
    }

    [Fact]
    public async Task OneForwardOnlyContent_OnThreeEntitiesOfOneBatch_StoresAllInFull()
    {
        var data = RandomBytes(Size, seed: 82);
        var shared = ForwardOnly(data);
        var store = Store();

        await store.CreateBatchAsync(
            [new() { Id = "p|a", File = shared }, new() { Id = "p|b", File = shared }, new() { Id = "p|c", File = shared }], Ct);

        foreach (var id in new[] { "p|a", "p|b", "p|c" })
            await AssertStoredAsync(store, id, data);
        Assert.Equal(3, store.Blobs.Names.Count);
    }

    [Fact]
    public async Task OneForwardOnlyContent_OnTwoEntitiesWrittenOneAfterTheOther_StoresBothInFull()
    {
        var data = RandomBytes(Size, seed: 83);
        var shared = ForwardOnly(data);
        var store = Store();

        await store.CreateAsync(new FileEntity { Id = "p|a", File = shared }, Ct);
        await store.CreateAsync(new FileEntity { Id = "p|b", File = shared }, Ct);

        await AssertStoredAsync(store, "p|a", data);
        await AssertStoredAsync(store, "p|b", data);
        Assert.Equal(2, store.Blobs.Names.Count);
    }

    [Fact]
    public async Task OneForwardOnlyContent_OnEntitiesOfTwoPartitionsOfOneBatch_StoresBothInFull()
    {
        var data = RandomBytes(Size, seed: 84);
        var shared = ForwardOnly(data);
        var store = Store();

        await store.CreateBatchAsync([new() { Id = "p|a", File = shared }, new() { Id = "q|a", File = shared }], Ct);

        await AssertStoredAsync(store, "p|a", data);
        await AssertStoredAsync(store, "q|a", data);
    }

    [Fact]
    public async Task OneSeekableContent_OnTwoEntitiesOfOneBatch_StoresBothInFull()
    {
        var data = RandomBytes(Size, seed: 85);
        var shared = TableBlobContent.FromStream(new MemoryStream(data), "application/octet-stream");
        var store = Store();

        await store.CreateBatchAsync([new() { Id = "p|a", File = shared }, new() { Id = "p|b", File = shared }], Ct);

        await AssertStoredAsync(store, "p|a", data);
        await AssertStoredAsync(store, "p|b", data);
    }

    [Fact]
    public async Task Coordinator_ASecondPlanOfASharedContent_IsUploadedFromTheBlobTheContentIsBoundTo()
    {
        var blobs = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = new ValueOffloadCoordinator(blobs, new BlobOffloadOptions(), "T");
        var data = RandomBytes(Size, seed: 86);
        var shared = ForwardOnly(data);
        var first = new FileEntity { File = shared };
        var second = new FileEntity { File = shared };

        // As a batch does: prepare and plan every entity, then upload plan by plan.
        await coordinator.PrepareAsync(first, Ct);
        await coordinator.PrepareAsync(second, Ct);
        var (_, _, firstBlobs) = coordinator.Serialize(first, "p", "a", 0);
        var (_, _, secondBlobs) = coordinator.Serialize(second, "p", "b", 0);
        await coordinator.UploadAsync(firstBlobs, Ct);
        Assert.Null(shared.Source); // bound to its blob: the temporary copy is gone
        await coordinator.UploadAsync(secondBlobs, Ct);

        Assert.Equal(2, blobs.UploadCalls);
        Assert.Equal(1, blobs.OpenReadCalls); // the second upload read the first blob
        foreach (var blob in firstBlobs.Concat(secondBlobs))
        {
            await using var stream = await blobs.Inner.OpenReadAsync(blob.Column, blob.Reference.Path, Ct);
            Assert.Equal(data, await ReadAllAsync(stream));
        }
    }
}

/// <summary>
/// A write that fails releases the temporary copy of a forward-only source (the standing design). Such a
/// content cannot be written again: the next write refuses it with a clear error before anything is
/// read or uploaded. Content from a seekable stream or from bytes survives a failed write.
/// </summary>
public class AbandonedBlobContentTests
{
    private const int Size = 100_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemoryStorage<T> Store<T>() where T : class, IEntity, new() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });

    private static TableBlobContent ForwardOnly(byte[] data) =>
        TableBlobContent.FromStream(new FaultyStream(data, seekable: false), "application/octet-stream");

    internal static void AssertAbandoned(Exception? ex, string column)
    {
        var invalid = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains($"'{column}'", invalid.Message, StringComparison.Ordinal);
        Assert.Contains("temporary copy was released by a failed write", invalid.Message, StringComparison.Ordinal);
        Assert.Contains("Create the content again from the source", invalid.Message, StringComparison.Ordinal);
    }

    // A forward-only content whose first write failed in the upload: its temporary copy is released.
    private static async Task<TableBlobContent> AbandonedAsync<T>(InMemoryStorage<T> store, Func<TableBlobContent, T> entity)
        where T : class, IEntity, new()
    {
        var content = ForwardOnly(RandomBytes(Size, seed: 91));
        store.Blobs.BeforeUpload = (_, _) => throw new IOException("simulated blob store failure");
        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity(content), Ct));
        store.Blobs.BeforeUpload = null;
        Assert.Null(content.Source);
        Assert.True(content.Abandoned);
        return content;
    }

    [Fact]
    public async Task WrittenAgain_ThrowsTheClearError_BeforeAnythingIsUploaded()
    {
        var store = Store<TwoBlobEntity>();
        var abandoned = await AbandonedAsync(store, c => new TwoBlobEntity { Id = "p|r", Second = c });
        var uploads = 0;
        store.Blobs.BeforeUpload = (_, _) =>
        {
            uploads++;
            return Task.CompletedTask;
        };
        // A content that could be uploaded stands before the abandoned one.
        var entity = new TwoBlobEntity { Id = "p|r", First = TableBlobContent.FromBytes(RandomBytes(1_000), "image/png"), Second = abandoned };

        var ex = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));

        Assert.Equal(0, uploads);
        Assert.Empty(store.Blobs.Names);
        Assert.Equal(0, store.PhysicalRowCount);
        AssertAbandoned(ex, "Second");
        // Every later write, whatever the operation.
        AssertAbandoned(await Record.ExceptionAsync(() => store.UpsertAsync(entity, Ct)), "Second");
        AssertAbandoned(await Record.ExceptionAsync(() => store.CreateBatchAsync([entity], Ct)), "Second");
        Assert.Equal(0, uploads);
    }

    [Fact]
    public async Task MutateRetryAfterAConflict_WithAForwardOnlyContent_ThrowsTheClearError()
    {
        var store = Store<FileEntity>();
        await store.CreateAsync(new FileEntity { Id = "p|r", Title = "v1" }, Ct);
        var content = ForwardOnly(RandomBytes(Size, seed: 92));
        var attempts = 0;

        var ex = await Record.ExceptionAsync(() => store.MutateAsync("p|r", e =>
        {
            // Another writer gets in between this attempt's read and its write, once.
            if (attempts++ == 0)
                store.UpsertAsync(new FileEntity { Id = "p|r", Title = "v2" }, Ct).GetAwaiter().GetResult();
            e.File = content;
        }, ct: Ct));

        Assert.Equal(2, attempts);
        AssertAbandoned(ex, "File");
        // Nothing of the content was stored, and the other writer's row is intact.
        Assert.Empty(store.Blobs.Names);
        var row = await store.OneAsync("p|r", Ct);
        Assert.Equal("v2", row!.Title);
        Assert.Null(row.File);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MutateRetryAfterAConflict_WithASeekableStreamOrBytes_StoresTheContent(bool seekableStream)
    {
        var store = Store<FileEntity>();
        await store.CreateAsync(new FileEntity { Id = "p|r", Title = "v1" }, Ct);
        var data = RandomBytes(Size, seed: 93);
        var content = seekableStream
            ? TableBlobContent.FromStream(new MemoryStream(data), "application/octet-stream")
            : TableBlobContent.FromBytes(data, "application/octet-stream");
        var attempts = 0;

        await store.MutateAsync("p|r", e =>
        {
            if (attempts++ == 0)
                store.UpsertAsync(new FileEntity { Id = "p|r", Title = "v2" }, Ct).GetAwaiter().GetResult();
            e.File = content;
        }, ct: Ct);

        Assert.Equal(2, attempts);
        var row = await store.OneAsync("p|r", Ct);
        Assert.Equal("v2", row!.Title);
        await using var stream = await row.File!.OpenReadAsync(Ct);
        Assert.Equal(data, await ReadAllAsync(stream));
    }

    [Fact]
    public async Task InABatch_RefusesTheWholeBatchBeforeAnyUpload()
    {
        var store = Store<FileEntity>();
        var abandoned = await AbandonedAsync(store, c => new FileEntity { Id = "p|x", File = c });
        var uploads = 0;
        store.Blobs.BeforeUpload = (_, _) =>
        {
            uploads++;
            return Task.CompletedTask;
        };

        var ex = await Record.ExceptionAsync(() => store.UpsertBatchAsync(
        [
            new() { Id = "p|a", File = TableBlobContent.FromBytes(RandomBytes(1_000), "image/png") },
            new() { Id = "p|b", File = abandoned },
        ], Ct));

        Assert.Equal(0, uploads);
        Assert.Equal(0, store.PhysicalRowCount);
        AssertAbandoned(ex, "File");
    }

    [Fact]
    public async Task OpenedForReading_SaysTheTemporaryCopyWasReleased()
    {
        var store = Store<FileEntity>();
        var abandoned = await AbandonedAsync(store, c => new FileEntity { Id = "p|x", File = c });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => abandoned.OpenReadAsync(Ct));

        Assert.Contains("temporary copy was released by a failed write", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASeekableStreamOrBytes_SurvivesTheSameFailedWrite()
    {
        var store = Store<TwoBlobEntity>();
        var data = RandomBytes(Size, seed: 94);
        var entity = new TwoBlobEntity
        {
            Id = "p|r",
            First = TableBlobContent.FromStream(new MemoryStream(data), "application/octet-stream"),
            Second = TableBlobContent.FromBytes(data, "application/octet-stream"),
        };
        store.Blobs.BeforeUpload = (_, _) => throw new IOException("simulated blob store failure");
        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(entity, Ct));
        store.Blobs.BeforeUpload = null;

        await store.CreateAsync(entity, Ct);

        var read = await store.OneAsync("p|r", Ct);
        await using var first = await read!.First!.OpenReadAsync(Ct);
        await using var second = await read.Second!.OpenReadAsync(Ct);
        Assert.Equal(data, await ReadAllAsync(first));
        Assert.Equal(data, await ReadAllAsync(second));
    }
}

/// <summary>Shared and abandoned contents through <see cref="TableStorage{T}"/> against Azurite.</summary>
public sealed class AzuriteSharedAndAbandonedContentTests : AzuriteContentTestBase
{
    private const int Size = 300_000;

    private static TableBlobContent ForwardOnly(byte[] data) =>
        TableBlobContent.FromStream(new FaultyStream(data, seekable: false), "application/octet-stream");

    private async Task AssertStoredAsync<T>(string rk, byte[] expected, Func<T, TableBlobContent?> file) where T : class, IEntity, new()
    {
        var read = await Store<T>().OneAsync(Id(rk), Ct);
        Assert.NotNull(read);
        var content = file(read);
        Assert.NotNull(content);
        Assert.Equal(expected.Length, content.Length);
        await using var stream = await content.OpenReadAsync(Ct);
        Assert.Equal(expected, await ReadAllAsync(stream));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneForwardOnlyContent_OnTwoEntitiesOfOneBatch_StoresBothInFull(bool upsert)
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 95);
        var shared = ForwardOnly(data);
        var store = Store<FileEntity>();
        FileEntity[] batch = [new() { Id = Id("a"), File = shared }, new() { Id = Id("b"), File = shared }];

        Assert.Equal(2, upsert ? await store.UpsertBatchAsync(batch, Ct) : await store.CreateBatchAsync(batch, Ct));

        await AssertStoredAsync<FileEntity>("a", data, e => e.File);
        await AssertStoredAsync<FileEntity>("b", data, e => e.File);
        Assert.Equal(2, (await BlobsAsync<FileEntity>()).Count);
    }

    [Fact]
    public async Task OneForwardOnlyContent_OnTwoEntitiesWrittenOneAfterTheOther_StoresBothInFull()
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 96);
        var shared = ForwardOnly(data);
        var store = Store<FileEntity>();

        await store.CreateAsync(new FileEntity { Id = Id("a"), File = shared }, Ct);
        await store.CreateAsync(new FileEntity { Id = Id("b"), File = shared }, Ct);

        await AssertStoredAsync<FileEntity>("a", data, e => e.File);
        await AssertStoredAsync<FileEntity>("b", data, e => e.File);
        Assert.Equal(2, (await BlobsAsync<FileEntity>()).Count);
    }

    [Fact]
    public async Task AbandonedContent_WrittenAgain_ThrowsTheClearError_BeforeAnythingIsUploaded()
    {
        Assert.SkipUnless(Available, SkipReason);
        var puts = new BlobPuts { FailuresLeft = 1 };
        // A low threshold, so that a text value just too large for one cell is offloaded too (a blob that could be uploaded).
        var offload = Offload();
        offload.ThresholdBytes = 1_000;
        var store = Store<TextAndBlobEntity>(offload, BlobServiceWith(puts));
        var content = ForwardOnly(RandomBytes(Size, seed: 97));

        // The first write fails in its upload: the temporary copy of the forward-only source is released.
        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(new TextAndBlobEntity { Id = Id("r"), Content = content }, Ct));
        Assert.True(content.Abandoned);
        Assert.Equal(1, puts.Count);

        // The text value's blob stands before the abandoned content.
        var entity = new TextAndBlobEntity { Id = Id("r"), Body = Ascii(40_000, seed: 98), Content = content };
        var ex = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));

        Assert.Equal(1, puts.Count); // no blob was sent
        Assert.Empty(await BlobsAsync<TextAndBlobEntity>());
        Assert.Empty(await RawRowsAsync<TextAndBlobEntity>());
        AbandonedBlobContentTests.AssertAbandoned(ex, "Content");
    }

    /// <summary>Counts the blob uploads (Put Blob or Put Block) below the table's prefix, and can fail the first ones.</summary>
    private sealed class BlobPuts : HttpPipelineSynchronousPolicy
    {
        private int count;

        public int FailuresLeft { get; set; }

        public int Count => Volatile.Read(ref count);

        public override void OnSendingRequest(HttpMessage message)
        {
            var uri = message.Request.Uri;
            if (message.Request.Method != RequestMethod.Put
                || !uri.Path.Contains("/" + nameof(TextAndBlobEntity) + "/", StringComparison.Ordinal)
                || uri.Query.Contains("comp=metadata", StringComparison.Ordinal))
                return;
            Interlocked.Increment(ref count);
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new IOException("simulated blob upload failure");
            }
        }
    }
}
