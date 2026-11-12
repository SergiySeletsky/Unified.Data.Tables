using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A cached entity can outlive its blob: another process replaced the file, or this process's own write
/// committed but threw and a sweep later removed the previous blob. Once a lazy open of a content finds
/// its blob missing, the store that materialized the content no longer answers that row from its caches
/// (the entity's entry and the cached lists of its partition), so "read the entity again", as the
/// exception says, returns the current row. The exception still reaches the caller of
/// <see cref="TableBlobContent.OpenReadAsync"/> unchanged (BLOBS-4, COMPLETENESS-9 and LEAD-3 of the
/// fix-wave-2 review).
/// </summary>
public class MissingBlobEvictsTheCachedEntityTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UnifiedTableStorageOptions Options() => new()
    {
        BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero },
    };

    private static async Task<byte[]> ReadFileAsync(TableBlobContent content)
    {
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    // The row a write of the entity stores through `blobs`, with its blob left there.
    private static async Task<TableEntity> WrittenRowAsync(InMemoryOffloadBlobStore blobs, FileEntity entity, string etag)
    {
        var coordinator = new ValueOffloadCoordinator(blobs, Options().BlobOffload, nameof(FileEntity));
        var (row, _, pending) = coordinator.Serialize(entity, "p", "a", 0);
        await coordinator.UploadAsync(pending, Ct);
        row.ETag = new ETag(etag);
        return row;
    }

    private static FileEntity Entity(byte[] data, string title = "t") =>
        new() { Id = "p|a", Title = title, File = TableBlobContent.FromBytes(data, "application/octet-stream") };

    [Fact]
    public async Task AfterAnotherProcessReplacedTheFile_ReadingTheEntityAgain_AsTheErrorSays_GetsAFileThatOpens()
    {
        var oldBytes = RandomBytes(20_000, seed: 1001);
        var newBytes = RandomBytes(20_000, seed: 1002);
        var blobs = new InMemoryOffloadBlobStore(); // the one container both processes use
        var v1 = await WrittenRowAsync(blobs, Entity(oldBytes), "W/\"1\"");
        var oldBlob = Assert.Single(blobs.Names);
        using var h = new StorageHarness<FileEntity>(options: Options(), blobs: blobs);
        h.SetupGet("p", "a", v1);

        // This process reads the entity (and caches it).
        var first = await h.Store.OneAsync("p|a", Ct);
        Assert.Equal(oldBytes, await ReadFileAsync(first!.File!));

        // Another process replaces the file: the row now references a new blob and the old blob is deleted.
        var v2 = await WrittenRowAsync(blobs, Entity(newBytes), "W/\"2\"");
        blobs.Remove(oldBlob);
        h.SetupGet("p", "a", v2);

        // A later request in this process: the entity comes from the cache, and its file cannot be opened.
        var stale = await h.Store.OneAsync("p|a", Ct);
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "a", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        var failure = await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadFileAsync(stale!.File!));
        output.WriteLine("first open: " + failure.Message);
        Assert.Contains("read the entity again", failure.Message, StringComparison.Ordinal);

        // The caller does what the message says: the next read goes to the table.
        var again = await h.Store.OneAsync("p|a", Ct);
        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>("p", "a", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        Assert.Equal(newBytes, await ReadFileAsync(again!.File!));
    }

    [Fact]
    public async Task AfterAnotherProcessReplacedTheFile_TheCachedListOfThePartition_IsReadAgainToo()
    {
        var oldBytes = RandomBytes(20_000, seed: 1011);
        var newBytes = RandomBytes(20_000, seed: 1012);
        var blobs = new InMemoryOffloadBlobStore();
        var v1 = await WrittenRowAsync(blobs, Entity(oldBytes), "W/\"1\"");
        var oldBlob = Assert.Single(blobs.Names);
        using var h = new StorageHarness<FileEntity>(options: Options(), blobs: blobs);
        h.SetupQueryByPartition(v1);

        var listed = Assert.Single(await h.Store.QueryAsync("p", Ct)); // the partition's list is cached
        Assert.Equal(oldBytes, await ReadFileAsync(listed.File!));

        var v2 = await WrittenRowAsync(blobs, Entity(newBytes), "W/\"2\"");
        blobs.Remove(oldBlob);
        h.SetupQueryByPartition(v2);

        var stale = Assert.Single(await h.Store.QueryAsync("p", Ct)); // still the cached list
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadFileAsync(stale.File!));

        var again = Assert.Single(await h.Store.QueryAsync("p", Ct));
        Assert.Equal(newBytes, await ReadFileAsync(again.File!));
    }

    [Fact]
    public async Task AWriteThatCommittedButThrew_ThenASweep_ReadingTheEntityAgain_GetsAFileThatOpens()
    {
        var oldBytes = RandomBytes(20_000, seed: 9301);
        var newBytes = RandomBytes(20_000, seed: 9302);
        var blobs = new InMemoryOffloadBlobStore();
        var v1 = await WrittenRowAsync(blobs, Entity(oldBytes), "W/\"1\"");
        using var h = new StorageHarness<FileEntity>(options: Options(), blobs: blobs);
        h.SetupGet("p", "a", v1);

        var first = await h.Store.OneAsync("p|a", Ct); // read and cached
        Assert.Equal(oldBytes, await ReadFileAsync(first!.File!));

        // The replace: the row write reaches the service and commits, the response does not come back.
        TableEntity? committed = null;
        h.Table.UpdateEntityAsync(Arg.Do<TableEntity>(row => committed = row), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Task<Response>>(_ => throw new TaskCanceledException("the caller's token was cancelled after the request was sent"));
        first.File = TableBlobContent.FromBytes(newBytes, "application/octet-stream");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Store.UpdateAsync(first, ConcurrencyMode.LastWriterWins, Ct));
        Assert.NotNull(committed);
        committed.ETag = new ETag("W/\"2\"");
        h.SetupGet("p", "a", committed); // what the table holds now
        Assert.Equal(2, blobs.Names.Count);

        // The orphan sweep: the row references the new blob only, so the old one goes.
        h.SetupQueryAll(committed);
        Assert.Equal(1, await h.Store.SweepOrphanBlobsAsync("p", TimeSpan.Zero, Ct));

        // A later request in the same process.
        var stale = await h.Store.OneAsync("p|a", Ct);
        var failure = await Record.ExceptionAsync(() => ReadFileAsync(stale!.File!));
        output.WriteLine("first open after the sweep: " + (failure is null ? "the file opened" : failure.Message));

        var again = await h.Store.OneAsync("p|a", Ct);
        Assert.Equal(newBytes, await ReadFileAsync(again!.File!));
    }

    [Fact]
    public async Task ACacheThatFailsToEvict_NeverReplacesOrHidesTheIntegrityFailure()
    {
        var oldBytes = RandomBytes(20_000, seed: 1021);
        var newBytes = RandomBytes(20_000, seed: 1022);
        var blobs = new InMemoryOffloadBlobStore();
        var v1 = await WrittenRowAsync(blobs, Entity(oldBytes), "W/\"1\"");
        var oldBlob = Assert.Single(blobs.Names);
        using var cache = new RemoveFailingCache();
        using var h = new StorageHarness<FileEntity>(options: Options(), cache: cache, blobs: blobs);
        h.SetupGet("p", "a", v1);
        var stale = await h.Store.OneAsync("p|a", Ct);

        await WrittenRowAsync(blobs, Entity(newBytes), "W/\"2\"");
        blobs.Remove(oldBlob);
        cache.FailRemoves = true;

        var failure = await Record.ExceptionAsync(() => ReadFileAsync(stale!.File!));
        Assert.IsType<TableValueIntegrityException>(failure);
        Assert.Contains("could not be opened", failure.Message, StringComparison.Ordinal);
        Assert.True(cache.RemoveAttempts > 0);
    }

    [Fact]
    public async Task InMemory_AContentWhoseBlobIsGone_ThrowsTheSameFailure_AndReadingTheEntityAgainGetsTheCurrentFile()
    {
        var oldBytes = RandomBytes(20_000, seed: 1031);
        var newBytes = RandomBytes(20_000, seed: 1032);
        var store = new InMemoryStorage<FileEntity>(null, Options());
        await store.CreateAsync(Entity(oldBytes), Ct);
        var held = (await store.OneAsync("p|a", Ct))!;

        await store.UpsertAsync(Entity(newBytes), Ct); // grace zero: the previous blob is deleted at once
        Assert.Single(store.OffloadedBlobNames());

        var failure = await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadFileAsync(held.File!));
        Assert.Contains("could not be opened", failure.Message, StringComparison.Ordinal);
        Assert.Contains("read the entity again", failure.Message, StringComparison.Ordinal);
        Assert.Equal(newBytes, await ReadFileAsync((await store.OneAsync("p|a", Ct))!.File!));
    }

    // A memory cache whose Remove can be made to throw (as a broken or disposed cache would).
    private sealed class RemoveFailingCache : IMemoryCache
    {
        private readonly MemoryCache inner = new(new MemoryCacheOptions());

        public bool FailRemoves { get; set; }

        public int RemoveAttempts { get; private set; }

        public ICacheEntry CreateEntry(object key) => inner.CreateEntry(key);

        public void Remove(object key)
        {
            if (FailRemoves)
            {
                RemoveAttempts++;
                throw new InvalidOperationException("the cache cannot remove entries");
            }
            inner.Remove(key);
        }

        public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);

        public void Dispose() => inner.Dispose();
    }
}

/// <summary>The same against Azurite: two stores with a cache each stand for two instances of the application.</summary>
public sealed class AzuriteMissingBlobEvictsTheCachedEntityTests(ITestOutputHelper output) : AzuriteContentTestBase
{
    private static async Task<byte[]> ReadFileAsync(TableBlobContent content)
    {
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    [Fact]
    public async Task AfterAnotherInstanceReplacedTheFile_ReadingTheEntityAgain_AsTheErrorSays_GetsAFileThatOpens()
    {
        Assert.SkipUnless(Available, SkipReason);
        var oldBytes = RandomBytes(20_000, seed: 1003);
        var newBytes = RandomBytes(20_000, seed: 1004);
        var id = Id("stale");
        var instanceA = Store<FileEntity>(Offload(TimeSpan.Zero)); // its own cache: the default, one hour sliding
        var instanceB = Store<FileEntity>(Offload(TimeSpan.Zero));
        await instanceB.CreateAsync(new FileEntity { Id = id, File = TableBlobContent.FromBytes(oldBytes, "application/octet-stream") }, Ct);

        // Instance A reads the entity (and caches it).
        var first = await instanceA.OneAsync(id, Ct);
        Assert.Equal(oldBytes, await ReadFileAsync(first!.File!));

        // Instance B replaces the file once the old blob is older than its (zero) grace: the old blob is deleted.
        var newest = (await BlobsAsync<FileEntity>()).Max(b => b.Properties.LastModified!.Value);
        while ((await Container.GetPropertiesAsync(cancellationToken: Ct)).GetRawResponse().Headers.Date <= newest)
            await Task.Delay(200, Ct);
        await instanceB.UpsertAsync(new FileEntity { Id = id, File = TableBlobContent.FromBytes(newBytes, "application/octet-stream") }, Ct);
        Assert.Single(await BlobsAsync<FileEntity>());

        // A later request on instance A.
        var stale = await instanceA.OneAsync(id, Ct);
        var failure = await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadFileAsync(stale!.File!));
        output.WriteLine("first open on instance A: " + failure.Message);
        Assert.Contains("read the entity again", failure.Message, StringComparison.Ordinal);

        var again = await instanceA.OneAsync(id, Ct);
        Assert.Equal(newBytes, await ReadFileAsync(again!.File!));
    }
}
