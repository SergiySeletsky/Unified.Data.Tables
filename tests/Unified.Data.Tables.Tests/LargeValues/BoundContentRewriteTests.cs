using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>A content held by a value-type owner: the write cannot hand that property another instance.</summary>
public struct BlobSlot
{
    public BlobSlot()
    {
    }

    public TableBlobContent? Content { get; set; }
}

public class BlobSlotEntity : Entity
{
    public BlobSlot Slot { get; set; }
}

/// <summary>
/// A <see cref="TableBlobContent"/> that already streams a blob (it was read from a row, or written
/// before) and is written into another row, or another property. The instance is shared: the entity it
/// came from holds it, and so does every cached copy of that entity. Writing it elsewhere must leave it
/// bound where it was; the entity that was written gets a content of its own, bound to the blob its own
/// row references. Whatever then happens to one row leaves the other's content readable.
/// </summary>
public class BoundContentRewriteTests
{
    private const int Size = 50_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // CleanupGrace zero: a delete or replace removes the released blob at once.
    private static InMemoryStorage<T> Store<T>() where T : class, IEntity, new() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero } });

    private static string BlobName<T>(string rk, string column, byte[] data) =>
        ValueOffloadCoordinator.BlobName(typeof(T).Name, "p", rk, column, Sha(data));

    private static async Task<byte[]> BytesAsync(TableBlobContent? content)
    {
        Assert.NotNull(content);
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    private static TableBlobContent Stream(byte[] data) =>
        TableBlobContent.FromStream(new MemoryStream(data), "application/octet-stream");

    [Fact]
    public async Task ContentReadFromRowA_CreatedIntoRowB_ThenBDeleted_AStillReadsItsContent()
    {
        var data = RandomBytes(Size, seed: 101);
        var store = Store<FileEntity>();
        await store.CreateAsync(new FileEntity { Id = "p|a", File = Stream(data) }, Ct);
        var a = await store.OneAsync("p|a", Ct);
        var fromA = a!.File!;

        var b = new FileEntity { Id = "p|b", File = fromA };
        await store.CreateAsync(b, Ct);
        var writtenToB = b.File!;
        Assert.Equal(data, await BytesAsync(writtenToB)); // B, before its delete, reads the bytes

        await store.DeleteAsync("p|b", Ct);
        Assert.Equal([BlobName<FileEntity>("a", "File", data)], store.Blobs.Names);

        // Row A is untouched: the entity read from it, and a new read of it, still return the bytes.
        Assert.Equal(data, await BytesAsync(a.File));
        Assert.Equal(data, await BytesAsync((await store.OneAsync("p|a", Ct))!.File));

        // B held a content of its own, bound to the blob B's row referenced; A's stayed where it was.
        Assert.Same(fromA, a.File);
        Assert.NotSame(fromA, writtenToB);
        Assert.Equal(BlobName<FileEntity>("a", "File", data), fromA.BoundPath);
        Assert.Equal(BlobName<FileEntity>("b", "File", data), writtenToB.BoundPath);
        Assert.Equal(Size, writtenToB.Length);
        Assert.Equal(Sha(data), writtenToB.Sha256);
        Assert.Equal("application/octet-stream", writtenToB.ContentType);
    }

    [Fact]
    public async Task ContentReadFromRowA_CreatedIntoRowB_ThenADeleted_BStillReadsItsContent()
    {
        var data = RandomBytes(Size, seed: 102);
        var store = Store<FileEntity>();
        await store.CreateAsync(new FileEntity { Id = "p|a", File = Stream(data) }, Ct);
        var a = await store.OneAsync("p|a", Ct);
        var b = new FileEntity { Id = "p|b", File = a!.File };
        await store.CreateAsync(b, Ct);

        await store.DeleteAsync("p|a", Ct);
        Assert.Equal([BlobName<FileEntity>("b", "File", data)], store.Blobs.Names);

        // The entity that was written, and a fresh read of its row, read B's own blob.
        Assert.Equal(data, await BytesAsync(b.File));
        Assert.Equal(data, await BytesAsync((await store.OneAsync("p|b", Ct))!.File));
    }

    [Theory]
    [InlineData("upsert")]
    [InlineData("update")]
    [InlineData("createBatch")]
    [InlineData("upsertBatch")]
    public async Task ContentReadFromRowA_WrittenIntoRowB_ByAnyWrite_LeavesABoundToItsOwnBlob(string write)
    {
        var data = RandomBytes(Size, seed: 103);
        var store = Store<FileEntity>();
        await store.CreateAsync(new FileEntity { Id = "p|a", File = Stream(data) }, Ct);
        var a = await store.OneAsync("p|a", Ct);
        var fromA = a!.File!;
        var b = new FileEntity { Id = "p|b", File = fromA };
        if (write == "update")
        {
            // B exists with another file, which the update replaces.
            var existing = await store.CreateAsync(new FileEntity { Id = "p|b", File = Stream(RandomBytes(1_000, seed: 104)) }, Ct);
            b.ETag = existing.ETag;
        }

        switch (write)
        {
            case "upsert": await store.UpsertAsync(b, Ct); break;
            case "update": await store.UpdateAsync(b, Ct); break;
            case "createBatch": await store.CreateBatchAsync([b], Ct); break;
            default: await store.UpsertBatchAsync([b], Ct); break;
        }

        Assert.Equal(BlobName<FileEntity>("a", "File", data), fromA.BoundPath);
        Assert.NotSame(fromA, b.File);
        Assert.Equal(BlobName<FileEntity>("b", "File", data), b.File!.BoundPath);

        await store.DeleteAsync("p|b", Ct);
        Assert.Equal(data, await BytesAsync(a.File));
        Assert.Equal([BlobName<FileEntity>("a", "File", data)], store.Blobs.Names);
    }

    [Fact]
    public async Task ContentReadFromRowA_OnTwoEntitiesOfOneBatch_EachGetsItsOwnContent()
    {
        var data = RandomBytes(Size, seed: 105);
        var store = Store<FileEntity>();
        await store.CreateAsync(new FileEntity { Id = "p|a", File = Stream(data) }, Ct);
        var fromA = (await store.OneAsync("p|a", Ct))!.File!;
        var b = new FileEntity { Id = "p|b", File = fromA };
        var c = new FileEntity { Id = "p|c", File = fromA };

        await store.CreateBatchAsync([b, c], Ct);

        Assert.Equal(BlobName<FileEntity>("a", "File", data), fromA.BoundPath);
        Assert.Equal(BlobName<FileEntity>("b", "File", data), b.File!.BoundPath);
        Assert.Equal(BlobName<FileEntity>("c", "File", data), c.File!.BoundPath);
        await store.DeleteAsync("p|a", Ct);
        await store.DeleteAsync("p|b", Ct);
        Assert.Equal(data, await BytesAsync(c.File));
    }

    [Fact]
    public async Task RewritingARowWithItsOwnContent_KeepsTheInstance()
    {
        var data = RandomBytes(Size, seed: 106);
        var store = Store<FileEntity>();
        await store.CreateAsync(new FileEntity { Id = "p|a", File = Stream(data) }, Ct);
        var a = await store.OneAsync("p|a", Ct);
        var file = a!.File!;

        a.Title = "renamed";
        await store.UpdateAsync(a, Ct);
        await store.UpsertAsync(a, Ct);

        // Bound to this very blob already: nothing to replace.
        Assert.Same(file, a.File);
        Assert.Equal(data, await BytesAsync(a.File));
        Assert.Single(store.Blobs.Names);
    }

    [Fact]
    public async Task OneNewContentOnTwoProperties_EachPropertyEndsUpBoundToItsOwnBlob()
    {
        var data = RandomBytes(Size, seed: 107);
        var store = Store<TwoBlobEntity>();
        var shared = TableBlobContent.FromStream(new FaultyContentStream(data, seekable: false), "application/octet-stream");
        var entity = new TwoBlobEntity { Id = "p|r", First = shared, Second = shared };

        await store.CreateAsync(entity, Ct);

        Assert.NotSame(entity.First, entity.Second);
        Assert.Equal(BlobName<TwoBlobEntity>("r", "First", data), entity.First!.BoundPath);
        Assert.Equal(BlobName<TwoBlobEntity>("r", "Second", data), entity.Second!.BoundPath);

        // Dropping one property releases its blob; the other property's content still reads its own.
        var first = entity.First;
        entity.Second = null;
        await store.UpdateAsync(entity, Ct);
        Assert.Equal([BlobName<TwoBlobEntity>("r", "First", data)], store.Blobs.Names);
        Assert.Equal(data, await BytesAsync(first));
    }

    [Fact]
    public async Task InMemoryContent_OnTwoRows_IsNeverReplaced_AndNeverReadsABlob()
    {
        var data = RandomBytes(Size, seed: 108);
        var store = Store<FileEntity>();
        var content = TableBlobContent.FromBytes(data, "application/octet-stream");
        var a = new FileEntity { Id = "p|a", File = content };
        var b = new FileEntity { Id = "p|b", File = content };

        await store.CreateAsync(a, Ct);
        await store.CreateAsync(b, Ct);

        Assert.Same(content, a.File);
        Assert.Same(content, b.File);
        Assert.Equal(2, store.Blobs.Names.Count);
        await store.DeleteAsync("p|a", Ct);
        await store.DeleteAsync("p|b", Ct);
        Assert.Empty(store.Blobs.Names);
        Assert.Equal(data, await BytesAsync(content)); // its bytes are its own
    }

    [Fact]
    public async Task ContentOfANestedObject_WrittenIntoAnotherRow_GetsItsOwnInstanceToo()
    {
        var data = RandomBytes(Size, seed: 109);
        var store = Store<NestedBlobEntity>();
        await store.CreateAsync(new NestedBlobEntity { Id = "p|a", Inner = new NestedBlobHolder { Content = Stream(data) } }, Ct);
        var a = await store.OneAsync("p|a", Ct);
        var fromA = a!.Inner!.Content!;
        var b = new NestedBlobEntity { Id = "p|b", Inner = new NestedBlobHolder { Content = fromA } };

        await store.CreateAsync(b, Ct);

        Assert.NotSame(fromA, b.Inner.Content);
        Assert.Equal(BlobName<NestedBlobEntity>("a", "Inner_Content", data), fromA.BoundPath);
        Assert.Equal(BlobName<NestedBlobEntity>("b", "Inner_Content", data), b.Inner.Content!.BoundPath);
        await store.DeleteAsync("p|b", Ct);
        Assert.Equal(data, await BytesAsync(a.Inner.Content));
    }

    [Fact]
    public async Task ContentHeldByAValueTypeOwner_IsLeftAsItIs_AndTheRowStillStoresItsOwnBlob()
    {
        var data = RandomBytes(Size, seed: 110);
        var store = Store<BlobSlotEntity>();
        await store.CreateAsync(new BlobSlotEntity { Id = "p|a", Slot = new BlobSlot { Content = Stream(data) } }, Ct);
        var a = await store.OneAsync("p|a", Ct);
        var fromA = a!.Slot.Content!;
        var b = new BlobSlotEntity { Id = "p|b", Slot = new BlobSlot { Content = fromA } };

        await store.CreateAsync(b, Ct);

        // The property cannot be assigned through its value-type owner: the instance stays, bound where it was.
        Assert.Same(fromA, b.Slot.Content);
        Assert.Equal(BlobName<BlobSlotEntity>("a", "Slot_Content", data), fromA.BoundPath);
        // B's row references its own blob, which a fresh read streams.
        Assert.Equal(2, store.Blobs.Names.Count);
        var readB = await store.OneAsync("p|b", Ct);
        Assert.Equal(BlobName<BlobSlotEntity>("b", "Slot_Content", data), readB!.Slot.Content!.BoundPath);
        await store.DeleteAsync("p|b", Ct);
        Assert.Equal(data, await BytesAsync(a.Slot.Content));
    }

    // ── TableStorage: the cached copy of A shares A's content instance ─────

    private static readonly UnifiedTableStorageOptions Options = new()
    {
        BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero },
    };

    // The row a create of the entity writes, with its blob left in the store.
    private static async Task<TableEntity> WrittenRowAsync(InMemoryOffloadBlobStore blobs, FileEntity entity, string rk, string etag)
    {
        var coordinator = new ValueOffloadCoordinator(blobs, Options.BlobOffload, nameof(FileEntity));
        var (row, _, pending) = coordinator.Serialize(entity, "p", rk, 0);
        await coordinator.UploadAsync(pending, Ct);
        row.ETag = new ETag(etag);
        return row;
    }

    [Fact]
    public async Task TableStorage_CachedA_IsUnaffected_WhenItsContentIsWrittenIntoB_AndBIsDeleted()
    {
        var data = RandomBytes(Size, seed: 111);
        var blobs = new InMemoryOffloadBlobStore();
        var rowA = await WrittenRowAsync(blobs, new FileEntity { Id = "p|a", File = TableBlobContent.FromBytes(data, "application/octet-stream") }, "a", "W/\"a1\"");
        using var h = new StorageHarness<FileEntity>(options: Options, blobs: blobs);
        h.SetupGet("p", "a", rowA);
        h.SetupAdd("W/\"b1\"");
        h.SetupDelete();
        TableEntity? rowB = null;
        h.Table.When(t => t.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>())).Do(c => rowB = c.Arg<TableEntity>());

        var a = await h.Store.OneAsync("p|a", Ct); // read from the table, and cached
        var b = new FileEntity { Id = "p|b", File = a!.File };
        await h.Store.CreateAsync(b, Ct);

        Assert.NotNull(rowB);
        Assert.Equal(BlobName<FileEntity>("b", "File", data), b.File!.BoundPath);
        Assert.Equal(data, await BytesAsync(b.File));
        // The cached copy of B streams B's blob too.
        Assert.Equal(BlobName<FileEntity>("b", "File", data), (await h.Store.OneAsync("p|b", Ct))!.File!.BoundPath);

        rowB.ETag = new ETag("W/\"b1\"");
        h.SetupGet("p", "b", rowB);
        await h.Store.DeleteAsync("p|b", Ct);
        Assert.Equal([BlobName<FileEntity>("a", "File", data)], blobs.Names);

        // The entity the caller holds, and the cached one, still read A's blob.
        Assert.Equal(data, await BytesAsync(a.File));
        var cached = await h.Store.OneAsync("p|a", Ct);
        Assert.Equal(data, await BytesAsync(cached!.File));
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "a", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TableStorage_CachedB_ReadsItsOwnBlob_AfterAIsGone()
    {
        var data = RandomBytes(Size, seed: 112);
        var blobs = new InMemoryOffloadBlobStore();
        var rowA = await WrittenRowAsync(blobs, new FileEntity { Id = "p|a", File = TableBlobContent.FromBytes(data, "application/octet-stream") }, "a", "W/\"a1\"");
        using var h = new StorageHarness<FileEntity>(options: Options, blobs: blobs);
        h.SetupGet("p", "a", rowA);
        h.SetupAdd("W/\"b1\"");

        var a = await h.Store.OneAsync("p|a", Ct);
        var b = new FileEntity { Id = "p|b", File = a!.File };
        await h.Store.CreateAsync(b, Ct);
        blobs.Remove(BlobName<FileEntity>("a", "File", data)); // A was replaced or deleted since

        Assert.Equal(data, await BytesAsync(b.File));
        Assert.Equal(data, await BytesAsync((await h.Store.OneAsync("p|b", Ct))!.File)); // from the cache
        await h.Table.DidNotReceive().GetEntityIfExistsAsync<TableEntity>("p", "b", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }
}

/// <summary>The same guarantee end to end against Azurite, where the store's cache and the real blob deletes meet.</summary>
public sealed class AzuriteBoundContentRewriteTests : AzuriteContentTestBase
{
    private const int Size = 200_000;

    private static async Task<byte[]> BytesAsync(TableBlobContent? content)
    {
        Assert.NotNull(content);
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    [Fact]
    public async Task ContentReadFromRowA_CreatedIntoRowB_ThenBDeleted_AAndItsCachedCopyStillRead()
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 113);
        // CleanupGrace zero: deleting B removes B's blob at once.
        var store = Store<FileEntity>(Offload(TimeSpan.Zero));
        await store.CreateAsync(new FileEntity { Id = Id("a"), File = TableBlobContent.FromStream(new MemoryStream(data), "application/octet-stream") }, Ct);
        var a = await store.OneAsync(Id("a"), Ct);

        var b = new FileEntity { Id = Id("b"), File = a!.File };
        await store.CreateAsync(b, Ct);
        var writtenToB = b.File;
        Assert.Equal(data, await BytesAsync(writtenToB)); // B, before its delete, reads the bytes
        Assert.Equal(2, (await BlobsAsync<FileEntity>()).Count);

        await store.DeleteAsync(Id("b"), Ct);
        var left = Assert.Single(await BlobsAsync<FileEntity>());
        Assert.Contains("/a/File/", left.Name, StringComparison.Ordinal);

        Assert.Equal(data, await BytesAsync(a.File));
        Assert.Equal(data, await BytesAsync((await store.OneAsync(Id("a"), Ct))!.File)); // the cached entity
        Assert.NotSame(a.File, writtenToB); // B had a content of its own
    }

    [Fact]
    public async Task ContentReadFromRowA_CreatedIntoRowB_ThenADeleted_BAndItsCachedCopyStillRead()
    {
        Assert.SkipUnless(Available, SkipReason);
        var data = RandomBytes(Size, seed: 114);
        var store = Store<FileEntity>(Offload(TimeSpan.Zero));
        await store.CreateAsync(new FileEntity { Id = Id("a"), File = TableBlobContent.FromStream(new MemoryStream(data), "application/octet-stream") }, Ct);
        var a = await store.OneAsync(Id("a"), Ct);
        var b = new FileEntity { Id = Id("b"), File = a!.File };
        await store.CreateAsync(b, Ct);

        await store.DeleteAsync(Id("a"), Ct);
        var left = Assert.Single(await BlobsAsync<FileEntity>());
        Assert.Contains("/b/File/", left.Name, StringComparison.Ordinal);

        Assert.Equal(data, await BytesAsync(b.File));
        Assert.Equal(data, await BytesAsync((await store.OneAsync(Id("b"), Ct))!.File)); // the cached entity
    }
}
