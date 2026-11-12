using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A <see cref="TableBlobContent"/> that streams a blob through one store keeps streaming that blob
/// through that store, also when the entity is saved through another store that gives its blob the same
/// name (another container or account, the same table name and keys). The entity the first store holds,
/// and returns from its cache, is not affected by what happens to the other store's blob; the property
/// written through the second store ends up with a content that streams the second store's blob
/// (BLOBS-3 of the fix-wave-2 review).
/// </summary>
public class ContentStaysWithItsStoreTests(ITestOutputHelper output)
{
    private const int Size = 50_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UnifiedTableStorageOptions Options() => new()
    {
        BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero },
    };

    private static InMemoryStorage<FileEntity> Memory() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero } });

    private static async Task<byte[]> ReadFileAsync(TableBlobContent content)
    {
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    // The row a create of the entity writes through the store over `blobs`, with its blob left there.
    private static async Task<TableEntity> WrittenRowAsync(InMemoryOffloadBlobStore blobs, FileEntity entity, string rk, string etag)
    {
        var coordinator = new ValueOffloadCoordinator(blobs, Options().BlobOffload, nameof(FileEntity));
        var (row, _, pending) = coordinator.Serialize(entity, "p", rk, 0);
        await coordinator.UploadAsync(pending, Ct);
        row.ETag = new ETag(etag);
        return row;
    }

    [Fact]
    public async Task AnEntityReadFromOneStore_SavedThroughAnotherWithTheSameBlobName_TheFirstStoresCachedEntityStillReadsItsOwnBlob()
    {
        var data = RandomBytes(Size, seed: 301);
        var blobs1 = new InMemoryOffloadBlobStore();
        var blobs2 = new InMemoryOffloadBlobStore();
        var row1 = await WrittenRowAsync(blobs1, new FileEntity { Id = "p|a", File = TableBlobContent.FromBytes(data, "application/octet-stream") }, "a", "W/\"a1\"");
        using var account1 = new StorageHarness<FileEntity>(options: Options(), blobs: blobs1);
        using var account2 = new StorageHarness<FileEntity>(options: Options(), blobs: blobs2);
        account1.SetupGet("p", "a", row1);
        account2.SetupGet("p", "a", null);
        account2.SetupAdd("W/\"b1\"");

        var a = await account1.Store.OneAsync("p|a", Ct); // read from account 1, and cached there
        var held = a!.File!;
        await account2.Store.UpsertAsync(a, Ct);           // the same entity saved into account 2
        var name = Assert.Single(blobs2.Names);
        Assert.Equal(name, Assert.Single(blobs1.Names));   // the same blob name in both containers

        // The property written through account 2 streams account 2's blob: it still reads once account 1's is gone...
        var written = a.File!;
        Assert.NotSame(held, written);
        blobs1.Remove(name);
        Assert.Equal(data, await ReadFileAsync(written));
        await WrittenRowAsync(blobs1, new FileEntity { Id = "p|a", File = TableBlobContent.FromBytes(data, "application/octet-stream") }, "a", "W/\"a1\"");

        // ...and account 2's copy going away does not affect what account 1 handed out.
        blobs2.Remove(name);
        Assert.Equal(data, await ReadAllAsync(await blobs1.OpenReadAsync("File", name, Ct)));
        var cached = await account1.Store.OneAsync("p|a", Ct);
        await account1.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "a", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        var failure = await Record.ExceptionAsync(() => ReadFileAsync(cached!.File!));
        output.WriteLine(failure is null ? "account 1's cached entity read its file" : $"{failure.GetType().Name}: {failure.Message}");
        Assert.Null(failure);
        Assert.Equal(data, await ReadFileAsync(held));
    }

    [Fact]
    public async Task InMemory_AnEntityReadFromOneStore_SavedIntoAnother_ThenTheOthersRowDeleted_TheFirstStoresContentStillReads()
    {
        var data = RandomBytes(Size, seed: 302);
        var store1 = Memory();
        var store2 = Memory();
        await store1.CreateAsync(new FileEntity { Id = "p|a", File = TableBlobContent.FromBytes(data, "application/octet-stream") }, Ct);
        var a = await store1.OneAsync("p|a", Ct);
        var held = a!.File!;                                  // what a cache, or any other holder of the entity, keeps

        var copy = new FileEntity { Id = "p|a", File = held };
        await store2.UpsertAsync(copy, Ct);
        Assert.NotSame(held, copy.File);                      // the property written through store 2 got a content of its own
        Assert.Equal(data, await ReadFileAsync(copy.File!));
        await store2.DeleteAsync("p|a", Ct);                  // store 2's row and (grace zero) its blob are gone

        Assert.Single(store1.OffloadedBlobNames());           // store 1 still has its row and blob
        var failure = await Record.ExceptionAsync(() => ReadFileAsync(held));
        output.WriteLine(failure is null ? "the content read from store 1 still reads" : $"{failure.GetType().Name}: {failure.Message}");
        Assert.Null(failure);
        Assert.Equal(data, await ReadFileAsync((await store1.OneAsync("p|a", Ct))!.File!));
    }

    [Fact]
    public async Task OneEntitySavedToAPrimaryAndThenASecondaryStore_ThePrimarysCachedEntityStillReadsThePrimarysBlob()
    {
        var data = RandomBytes(Size, seed: 9303);
        var primaryBlobs = new InMemoryOffloadBlobStore();
        var secondaryBlobs = new InMemoryOffloadBlobStore();
        using var primary = new StorageHarness<FileEntity>(options: Options(), blobs: primaryBlobs);
        using var secondary = new StorageHarness<FileEntity>(options: Options(), blobs: secondaryBlobs);
        primary.SetupGet("p", "a", null);
        primary.SetupAdd("W/\"p1\"");
        secondary.SetupGet("p", "a", null);
        secondary.SetupAdd("W/\"s1\"");

        var doc = new FileEntity { Id = "p|a", File = TableBlobContent.FromStream(new MemoryStream(data), "application/octet-stream") };
        await primary.Store.UpsertAsync(doc, Ct);   // written and cached by the primary's store
        var primaryContent = doc.File!;
        await secondary.Store.UpsertAsync(doc, Ct); // the same entity, saved to the secondary
        var name = Assert.Single(primaryBlobs.Names);
        Assert.Equal(name, Assert.Single(secondaryBlobs.Names));
        Assert.NotSame(primaryContent, doc.File);   // the secondary's write gave the property a content of its own

        secondaryBlobs.Remove(name); // the secondary's copy is replaced, deleted or unreachable later

        var cached = await primary.Store.OneAsync("p|a", Ct);
        await primary.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "a", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()); // the write's own prior-row read only
        var failure = await Record.ExceptionAsync(() => ReadFileAsync(cached!.File!));
        output.WriteLine(failure is null ? "the primary's cached entity read its file" : $"{failure.GetType().Name}: {failure.Message}");
        Assert.Null(failure);
        Assert.Equal(data, await ReadFileAsync(primaryContent));
        // The entity written last streams the secondary's blob, which is gone.
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadFileAsync(doc.File!));
    }

    [Fact]
    public async Task AContentSavedAgainThroughTheStoreThatBoundIt_KeepsItsInstance()
    {
        var data = RandomBytes(Size, seed: 304);
        var store = Memory();
        var entity = new FileEntity { Id = "p|a", File = TableBlobContent.FromBytes(data, "application/octet-stream") };
        await store.CreateAsync(entity, Ct);
        var loaded = await store.OneAsync("p|a", Ct);
        var file = loaded!.File!;

        loaded.Title = "changed";
        await store.UpsertAsync(loaded, Ct);

        Assert.Same(file, loaded.File);
        Assert.Equal(data, await ReadFileAsync(file));
        Assert.Single(store.OffloadedBlobNames());
    }
}
