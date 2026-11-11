using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A write that uploaded its blob and then failed leaves the content streaming that blob. A retry with
/// the same content reuses the blob, so it has to come before the blob is collected; once an orphan
/// sweep (or a partition delete's cleanup) has removed it, the write is refused with a message that says
/// the blob the content was uploaded to is gone and the content must be created again; a new content
/// over the same seekable stream then stores the file (BLOBS-5 and COMPLETENESS-7 of the fix-wave-2
/// review, closed by documentation and message: the content does not fall back to the stream).
/// </summary>
public class RetryAfterTheUploadedBlobIsGoneTests(ITestOutputHelper output)
{
    private const int Size = 200_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemoryStorage<FileEntity> Store() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });

    private static async Task<byte[]> ReadFileAsync(TableBlobContent content)
    {
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    // Fails with a duplicate key, which the store reports after it uploaded the file; then the orphan is collected.
    private static async Task FailAfterTheUploadAndCollectAsync(InMemoryStorage<FileEntity> store, FileEntity entity)
    {
        await store.CreateAsync(new FileEntity { Id = entity.Id, Title = "someone else's" }, Ct);
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(entity, Ct));
        Assert.Single(store.OffloadedBlobNames());
        Assert.Equal(1, await store.SweepOrphanBlobsAsync("p", TimeSpan.Zero, Ct));
        Assert.Empty(store.OffloadedBlobNames());
    }

    [Fact]
    public async Task ARetryWithTheSameStreamContent_AfterItsUploadedBlobWasCollected_IsRefusedWithAMessageAboutTheWrite()
    {
        var data = RandomBytes(Size, seed: 501);
        using var stream = new MemoryStream(data);
        var store = Store();
        var entity = new FileEntity { Id = "p|r", File = TableBlobContent.FromStream(stream, "application/octet-stream") };
        await FailAfterTheUploadAndCollectAsync(store, entity);

        var failure = await Assert.ThrowsAsync<TableValueIntegrityException>(() => store.UpsertAsync(entity, Ct));
        output.WriteLine(failure.Message);
        Assert.Contains("this content was uploaded to", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Create the content again", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("read the entity again", failure.Message, StringComparison.Ordinal);
        Assert.Equal("File", failure.ColumnName);

        // Nothing was stored: the row is the other writer's, and no blob was left.
        Assert.Null((await store.OneAsync("p|r", Ct))!.File);
        Assert.Empty(store.OffloadedBlobNames());

        // What the message says works: a new content over the same (unchanged) stream stores the file.
        entity.File = TableBlobContent.FromStream(stream, "application/octet-stream");
        await store.UpsertAsync(entity, Ct);
        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
    }

    [Fact]
    public async Task ARetryWithTheSameBytesContent_AfterItsUploadedBlobWasCollected_StoresTheFile()
    {
        var data = RandomBytes(Size, seed: 502);
        var store = Store();
        var entity = new FileEntity { Id = "p|r", File = TableBlobContent.FromBytes(data, "application/octet-stream") };
        await FailAfterTheUploadAndCollectAsync(store, entity);

        await store.UpsertAsync(entity, Ct);

        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
    }

    [Fact]
    public async Task ARetryWithTheSameStreamContent_BeforeItsUploadedBlobIsCollected_StoresTheFile()
    {
        var data = RandomBytes(Size, seed: 503);
        using var stream = new MemoryStream(data);
        var store = Store();
        await store.CreateAsync(new FileEntity { Id = "p|r", Title = "someone else's" }, Ct);
        var entity = new FileEntity { Id = "p|r", File = TableBlobContent.FromStream(stream, "application/octet-stream") };
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(entity, Ct));

        await store.UpsertAsync(entity, Ct);

        Assert.Equal(data, await ReadFileAsync((await store.OneAsync("p|r", Ct))!.File!));
        Assert.Single(store.OffloadedBlobNames());
    }
}
