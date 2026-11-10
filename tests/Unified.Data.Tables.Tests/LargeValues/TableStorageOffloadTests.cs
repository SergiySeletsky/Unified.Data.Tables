using Azure;
using Azure.Data.Tables;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// <see cref="TableStorage{T}"/> with blob offload: blobs are uploaded (create-only) before the entity
/// write, a replace swaps the row with If-Match and only then deletes the blobs the new row no longer
/// references, a delete removes the entity first and its blobs after, and reads resolve references
/// (eagerly for values, lazily for <see cref="TableBlobContent"/>) and verify them.
/// </summary>
public class TableStorageOffloadTests
{
    private const string Pk = "p";
    private const string Rk = "r";
    private const string Id = "p|r";
    private const int SmallThreshold = 1_000;

    private static readonly string BigText = Ascii(40_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UnifiedTableStorageOptions Offload(CachePolicy? cache = null) => new()
    {
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = SmallThreshold },
        Cache = cache ?? CachePolicy.Sliding(TimeSpan.FromHours(1)),
    };

    private sealed class Rig<T> : IDisposable where T : class, IEntity, new()
    {
        public Rig(InMemoryOffloadBlobStore? inner = null, CachePolicy? cache = null, bool discardUploads = false)
        {
            Blobs = new RecordingBlobStore(Log, inner) { DiscardUploads = discardUploads };
            H = new StorageHarness<T>(options: Offload(cache), blobs: Blobs);
            var table = H.Table;
            table.When(t => t.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>()))
                .Do(c => { Rows.Add(c.Arg<TableEntity>()); Add("table:add"); });
            table.When(t => t.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>()))
                .Do(c => { Rows.Add(c.Arg<TableEntity>()); Add("table:update"); });
            table.When(t => t.DeleteEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>()))
                .Do(_ => Add("table:delete"));
            table.When(t => t.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>()))
                .Do(_ => Add("table:tx"));
            table.When(t => t.GetEntityIfExistsAsync<TableEntity>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()))
                .Do(_ => Add("table:get"));
            H.SetupAdd("W/\"added\"");
            H.SetupUpdate("W/\"updated\"");
            H.SetupDelete();
            H.SetupTransaction();
        }

        public List<string> Log { get; } = [];

        public List<TableEntity> Rows { get; } = [];

        public RecordingBlobStore Blobs { get; }

        public StorageHarness<T> H { get; }

        public TableStorage<T> Store => H.Store;

        public TableClient Table => H.Table;

        private void Add(string entry)
        {
            lock (Log) Log.Add(entry);
        }

        public void ClearLog()
        {
            lock (Log) Log.Clear();
            Blobs.Calls.Clear();
        }

        public void Dispose() => H.Dispose();
    }

    /// <summary>The row a create of <paramref name="entity"/> writes, with its blobs left in <paramref name="inner"/>.</summary>
    private static async Task<TableEntity> WrittenRow<T>(InMemoryOffloadBlobStore inner, T entity, string etag = "W/\"v1\"")
        where T : class, IEntity, new()
    {
        using var rig = new Rig<T>(inner);
        await rig.Store.CreateAsync(entity, Ct);
        var row = Assert.Single(rig.Rows);
        row.ETag = new ETag(etag);
        return row;
    }

    private static BlobReference Reference(TableEntity row, string column) =>
        BlobReference.Parse(column, (string)row[column + SplitCodec.BlobSuffix]);

    /// <summary>Makes the blob a row references look written an hour ago (older than the cleanup grace).</summary>
    private static async Task Age(InMemoryOffloadBlobStore inner, TableEntity row, string column)
    {
        var reference = Reference(row, column);
        await using var stream = await inner.OpenReadAsync(column, reference.Path, CancellationToken.None);
        inner.Seed(reference.Path, await ReadAllAsync(stream), reference.Sha256, DateTimeOffset.UtcNow.AddHours(-1));
    }

    // ── writes ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Offloaded_UploadsBlobBeforeEntityWrite()
    {
        using var rig = new Rig<SplitTextEntity>();

        await rig.Store.CreateAsync(new SplitTextEntity { Id = Id, Body = BigText }, Ct);

        Assert.Equal(["blob:get", "blob:upload", "table:add"], rig.Log);
        var row = Assert.Single(rig.Rows);
        Assert.True(row.ContainsKey("Body__blob"));
        Assert.Equal(Sha(Utf8(BigText)), Reference(row, "Body").Sha256);
    }

    [Fact]
    public async Task Create_TableBlobContent_100MB_StreamsAndRowCarriesOnlyReference()
    {
        const long size = 100_000_000;
        using var rig = new Rig<FileEntity>(discardUploads: true);
        var entity = new FileEntity { Id = Id, File = TableBlobContent.FromStream(new GeneratedStream(size, seekable: true), "application/octet-stream") };

        var before = GC.GetAllocatedBytesForCurrentThread();
        await rig.Store.CreateAsync(entity, Ct);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var row = Assert.Single(rig.Rows);
        Assert.True(row.ContainsKey("File__blob"));
        Assert.DoesNotContain(row.Keys, k => k.Contains("__c", StringComparison.Ordinal));
        Assert.Equal(size, Reference(row, "File").Length);
        Assert.Equal(size, entity.File!.Length);
        // Every byte reached the store, and none of them was held in memory on the way.
        Assert.Equal(size, rig.Blobs.DiscardedBytes);
        Assert.True(allocated < 16 * 1024 * 1024, $"allocated {allocated:N0} bytes");
    }

    [Fact]
    public async Task Replace_NewContent_UploadThenIfMatchSwapThenDeleteOld()
    {
        var inner = new InMemoryOffloadBlobStore();
        var oldRow = await WrittenRow(inner, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1, 2, 3], "image/png") });
        var oldPath = Reference(oldRow, "File").Path;
        await Age(inner, oldRow, "File");
        using var rig = new Rig<FileEntity>(inner);
        rig.H.SetupGet(Pk, Rk, oldRow);

        await rig.Store.UpdateAsync(new FileEntity { Id = Id, ETag = "W/\"v1\"", File = TableBlobContent.FromBytes([4, 5, 6], "image/png") }, Ct);

        Assert.Equal(["table:get", "blob:get", "blob:upload", "table:update", "blob:delete"], rig.Log);
        await rig.Table.Received(1).UpdateEntityAsync(Arg.Any<TableEntity>(), new ETag("W/\"v1\""), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
        Assert.Equal("delete " + oldPath, rig.Blobs.Calls[^1]);
        var newPath = Reference(rig.Rows[^1], "File").Path;
        Assert.Equal([newPath], inner.Names);
    }

    [Fact]
    public async Task Replace_SameContent_DoesNotDeleteItsOwnBlob()
    {
        var inner = new InMemoryOffloadBlobStore();
        var oldRow = await WrittenRow(inner, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1, 2, 3], "image/png") });
        using var rig = new Rig<FileEntity>(inner);
        rig.H.SetupGet(Pk, Rk, oldRow);

        await rig.Store.UpdateAsync(new FileEntity { Id = Id, ETag = "W/\"v1\"", File = TableBlobContent.FromBytes([1, 2, 3], "image/png") }, Ct);

        Assert.Equal(["table:get", "blob:get", "blob:touch", "table:update"], rig.Log);
        Assert.Equal([Reference(oldRow, "File").Path], inner.Names);
    }

    [Fact]
    public async Task Replace_ConcurrentWriterReusesOldBlob_BlobSurvives()
    {
        var inner = new InMemoryOffloadBlobStore();
        var oldRow = await WrittenRow(inner, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1, 2, 3], "image/png") });
        var oldPath = Reference(oldRow, "File").Path;
        await Age(inner, oldRow, "File");
        using var rig = new Rig<FileEntity>(inner);
        rig.H.SetupGet(Pk, Rk, oldRow);
        // Right after our swap commits, another writer reuses the old content for its own row: it touches
        // the blob before committing that row, so the blob is recent when our cleanup runs.
        rig.Table.When(t => t.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>()))
            .Do(_ => inner.TouchAsync(oldPath, CancellationToken.None).GetAwaiter().GetResult());

        await rig.Store.UpdateAsync(new FileEntity { Id = Id, ETag = "W/\"v1\"", File = TableBlobContent.FromBytes([4, 5, 6], "image/png") }, Ct);

        Assert.Contains(oldPath, inner.Names);
        Assert.Equal([false], rig.Blobs.DeleteResults);
    }

    [Fact]
    public async Task Replace_OldBlobOlderThanGrace_IsDeleted()
    {
        var inner = new InMemoryOffloadBlobStore();
        var oldRow = await WrittenRow(inner, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1, 2, 3], "image/png") });
        var oldPath = Reference(oldRow, "File").Path;
        await Age(inner, oldRow, "File");
        using var rig = new Rig<FileEntity>(inner);
        rig.H.SetupGet(Pk, Rk, oldRow);

        var before = DateTimeOffset.UtcNow;
        await rig.Store.UpdateAsync(new FileEntity { Id = Id, ETag = "W/\"v1\"", File = TableBlobContent.FromBytes([4, 5, 6], "image/png") }, Ct);
        var after = DateTimeOffset.UtcNow;

        Assert.DoesNotContain(oldPath, inner.Names);
        var since = Assert.Single(rig.Blobs.DeleteConditions);
        var grace = new BlobOffloadOptions().CleanupGrace;
        Assert.InRange(since!.Value, before - grace, after - grace);
    }

    [Fact]
    public async Task Delete_RecentlyTouchedBlob_IsKept()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1], "image/png") });
        using var rig = new Rig<FileEntity>(inner);
        rig.H.SetupGet(Pk, Rk, row);

        await rig.Store.DeleteAsync(Id, Ct);

        // Written moments ago (within the grace): another writer may be about to reference it.
        await rig.Table.Received(1).DeleteEntityAsync(Pk, Rk, Arg.Any<ETag>(), Arg.Any<CancellationToken>());
        Assert.Equal([Reference(row, "File").Path], inner.Names);
        Assert.Equal([false], rig.Blobs.DeleteResults);
    }

    [Fact]
    public async Task DeletePartition_RecentlyTouchedBlob_IsKept()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1], "image/png") });
        using var rig = new Rig<FileEntity>(inner);
        rig.H.SetupQueryByPartition(row);

        await rig.Store.DeletePartitionAsync(Pk, Ct);

        Assert.Single(inner.Names);
        Assert.Equal([false], rig.Blobs.DeleteResults);
    }

    [Fact]
    public void CleanupGrace_DefaultsToFiveMinutes_RejectsNegative()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new BlobOffloadOptions().CleanupGrace);
        var options = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero };
        Assert.Equal(TimeSpan.Zero, options.CleanupGrace);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.CleanupGrace = TimeSpan.FromSeconds(-1));
    }

    [Fact]
    public async Task Upsert_SameSplitValue_DoesNotDeleteItsOwnBlob()
    {
        var inner = new InMemoryOffloadBlobStore();
        var oldRow = await WrittenRow(inner, new SplitTextEntity { Id = Id, Body = BigText });
        using var rig = new Rig<SplitTextEntity>(inner);
        rig.H.SetupGet(Pk, Rk, oldRow);

        await rig.Store.UpsertAsync(new SplitTextEntity { Id = Id, Body = BigText }, Ct);

        Assert.Equal(0, rig.Blobs.Count("delete"));
        Assert.Equal(0, rig.Blobs.Count("upload"));
        Assert.Equal([Reference(oldRow, "Body").Path], inner.Names);
    }

    [Fact]
    public async Task Replace_LosesRace_DeletesNoBlob()
    {
        var inner = new InMemoryOffloadBlobStore();
        var oldRow = await WrittenRow(inner, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1, 2, 3], "image/png") });
        using var rig = new Rig<FileEntity>(inner);
        rig.H.SetupGet(Pk, Rk, oldRow);
        rig.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RequestFailedException(412, "precondition", "UpdateConditionNotSatisfied", null));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            rig.Store.UpdateAsync(new FileEntity { Id = Id, ETag = "W/\"v1\"", File = TableBlobContent.FromBytes([4, 5, 6], "image/png") }, Ct));

        Assert.Equal(0, rig.Blobs.Count("delete"));
        // The new blob stays (an orphan sweep collects it); the old one is still referenced.
        Assert.Equal(2, inner.Names.Count);
    }

    [Fact]
    public async Task Create_WriteFails_LeavesUploadedBlob()
    {
        using var rig = new Rig<SplitTextEntity>();
        rig.Table.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RequestFailedException(500, "boom"));

        await Assert.ThrowsAsync<RequestFailedException>(() => rig.Store.CreateAsync(new SplitTextEntity { Id = Id, Body = BigText }, Ct));

        Assert.Equal(0, rig.Blobs.Count("delete"));
        Assert.Single(rig.Blobs.Inner.Names);
    }

    [Fact]
    public async Task Delete_EntityFirst_ThenBlobs()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new TextAndBlobEntity { Id = Id, Body = BigText, Content = TableBlobContent.FromBytes([9], "image/png") });
        await Age(inner, row, "Body");
        await Age(inner, row, "Content");
        using var rig = new Rig<TextAndBlobEntity>(inner);
        rig.H.SetupGet(Pk, Rk, row);

        await rig.Store.DeleteAsync(Id, Ct);

        Assert.Equal(["table:get", "table:delete", "blob:delete", "blob:delete"], rig.Log);
        Assert.Empty(inner.Names);
    }

    [Fact]
    public async Task Delete_BlobDeleteFails_EntityStillDeleted_NoThrow()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1], "image/png") });
        using var rig = new Rig<FileEntity>(inner);
        rig.H.SetupGet(Pk, Rk, row);
        rig.Blobs.DeleteFailure = new RequestFailedException(500, "blob service down");

        await rig.Store.DeleteAsync(Id, Ct);

        await rig.Table.Received(1).DeleteEntityAsync(Pk, Rk, Arg.Any<ETag>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, rig.Blobs.Count("delete"));
    }

    [Fact]
    public async Task DeletePartition_DeletesRowsThenEveryBlobOfThePartition()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1], "image/png") });
        await WrittenRow(inner, new FileEntity { Id = "other|r", File = TableBlobContent.FromBytes([2], "image/png") });
        await Age(inner, row, "File");
        using var rig = new Rig<FileEntity>(inner);
        rig.H.SetupQueryByPartition(row);

        Assert.Equal(1, await rig.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal("table:tx", rig.Log[0]);
        Assert.Contains("blob:delete", rig.Log);
        Assert.Contains("list FileEntity/p/", rig.Blobs.Calls);
        // Only the partition's blobs go.
        Assert.Single(inner.Names);
        Assert.StartsWith("FileEntity/other/", inner.Names.Single(), StringComparison.Ordinal);
    }

    // ── reads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Read_TableBlobContent_NeverDownloadsUntilOpened()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1, 2, 3, 4], "image/png"), Title = "t" });
        using var rig = new Rig<FileEntity>(inner);
        rig.H.SetupGet(Pk, Rk, row);

        var read = await rig.Store.OneAsync(Id, Ct);

        Assert.NotNull(read?.File);
        Assert.Equal(0, rig.Blobs.Count("read"));
        Assert.Equal(4, read!.File!.Length);
        await using var stream = await read.File.OpenReadAsync(Ct);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await ReadAllAsync(stream));
        Assert.Equal(1, rig.Blobs.Count("read"));
    }

    [Fact]
    public async Task Read_OffloadedText_ResolvesAndVerifies()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new SplitTextEntity { Id = Id, Body = BigText, Title = "t" });
        using var rig = new Rig<SplitTextEntity>(inner);
        rig.H.SetupGet(Pk, Rk, row);

        var read = await rig.Store.OneAsync(Id, Ct);

        Assert.Equal(BigText, read!.Body);
        Assert.Equal("t", read.Title);
    }

    [Fact]
    public async Task Read_MissingBlob_ThrowsIntegrity()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new SplitTextEntity { Id = Id, Body = BigText });
        inner.Remove(Reference(row, "Body").Path);
        using var rig = new Rig<SplitTextEntity>(inner);
        rig.H.SetupGet(Pk, Rk, row);

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => rig.Store.OneAsync(Id, Ct));
    }

    [Fact]
    public async Task Read_TamperedTextBlob_ThrowsIntegrity()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new SplitTextEntity { Id = Id, Body = BigText });
        var tampered = Utf8(BigText);
        tampered[0] ^= 1;
        inner.Tamper(Reference(row, "Body").Path, tampered);
        using var rig = new Rig<SplitTextEntity>(inner);
        rig.H.SetupGet(Pk, Rk, row);

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => rig.Store.OneAsync(Id, Ct));
    }

    [Fact]
    public async Task Query_ResolvesOffloadedValues()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new SplitTextEntity { Id = Id, Body = BigText });
        using var rig = new Rig<SplitTextEntity>(inner);
        rig.H.SetupQueryByPartition(row);
        rig.H.SetupQueryByFilter(row);

        Assert.Equal(BigText, Assert.Single(await rig.Store.QueryAsync(Pk, Ct)).Body);
        Assert.Equal(BigText, Assert.Single(await rig.Store.QueryAsync(new QueryOptions { Partition = Pk }, Ct)).Body);
        Assert.Equal(BigText, Assert.Single(await rig.Store.QueryAsync(e => e.Id == Id, Pk, ct: Ct)).Body);
        Assert.Equal(BigText, Assert.Single((await rig.Store.QueryPageAsync(new QueryOptions { Partition = Pk }, Ct)).Items).Body);
    }

    [Fact]
    public async Task Projection_OmittingOffloadedProperty_TouchesNoBlob()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new SplitTextEntity { Id = Id, Body = BigText, Title = "t" });
        using var rig = new Rig<SplitTextEntity>(inner);
        // The mock hands back the whole row even for a projection: the store must still not resolve it.
        rig.H.SetupQueryByFilter(row);
        rig.ClearLog();

        var items = await rig.Store.QueryAsync(new QueryOptions { Partition = Pk, Select = ["Title"] }, Ct);
        var page = await rig.Store.QueryPageAsync(new QueryOptions { Partition = Pk, Select = ["Title"] }, Ct);

        Assert.Equal("t", Assert.Single(items).Title);
        Assert.Null(items[0].Body);
        Assert.Single(page.Items);
        Assert.DoesNotContain(rig.Log, l => l.StartsWith("blob:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlainStore_ReadingAnOffloadedRow_Throws_InsteadOfReturningItEmpty()
    {
        // A store whose type does not opt in (another type wrote the row) cannot resolve the reference.
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new SplitTextEntity { Id = Id, Body = BigText, Title = "t" });
        using var plain = new StorageHarness<PlainEntity>();
        plain.SetupGet(Pk, Rk, row);
        plain.SetupQueryByFilter(row);

        var one = await Assert.ThrowsAsync<InvalidOperationException>(() => plain.Store.OneAsync(Id, Ct));
        Assert.Contains("'Body'", one.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => plain.Store.QueryAsync(new QueryOptions { Partition = Pk }, Ct));

        // Existence needs no value: it answers without materializing (or caching) the entity.
        Assert.True(await plain.Store.ExistsAsync(Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => plain.Store.OneAsync(Id, Ct));
    }

    [Fact]
    public async Task OptedInStore_WithoutBlobOffload_ReadingAnOffloadedRow_ThrowsIntegrity()
    {
        // The type opts the property in, but this store has no BlobOffload: the reference is recognized
        // and cannot be fetched, which is an integrity failure (never an empty value).
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new SplitTextEntity { Id = Id, Body = BigText, Title = "t" });
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet(Pk, Rk, row);

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => h.Store.OneAsync(Id, Ct));
        Assert.Contains("blob offload is not configured", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Body", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TableBlobContentStore_WithoutBlobOffload_ReadsLazily_ThrowsIntegrityWhenOpened()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1, 2, 3], "image/png"), Title = "t" });
        using var h = new StorageHarness<FileEntity>();
        h.SetupGet(Pk, Rk, row);

        var read = await h.Store.OneAsync(Id, Ct);

        Assert.NotNull(read?.File);
        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => read!.File!.OpenReadAsync(Ct));
        Assert.Contains("blob offload is not configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_NamingOffloadedProperty_ReturnsWholeValue()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new SplitTextEntity { Id = Id, Body = BigText, Title = "t" });
        using var rig = new Rig<SplitTextEntity>(inner);
        rig.H.SetupQueryByFilter(row);

        var items = await rig.Store.QueryAsync(new QueryOptions { Partition = Pk, Select = ["Body"] }, Ct);

        Assert.Equal(BigText, Assert.Single(items).Body);
    }

    [Fact]
    public async Task Cache_ClonesOffloadedEntity_WithoutReencoding()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new TextAndBlobEntity { Id = Id, Body = BigText, Content = TableBlobContent.FromBytes([7, 7], "image/png") });
        using var rig = new Rig<TextAndBlobEntity>(inner);
        rig.H.SetupGet(Pk, Rk, row);

        var first = await rig.Store.OneAsync(Id, Ct);
        var second = await rig.Store.OneAsync(Id, Ct);

        Assert.Equal(1, rig.Blobs.Count("read"));
        Assert.Equal(BigText, first!.Body);
        Assert.Equal(BigText, second!.Body);
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Content!.Length);
        // The clone neither uploads nor re-encodes anything.
        Assert.Equal(0, rig.Blobs.Count("upload"));
        await rig.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(Pk, Rk, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exists_OffloadedRow_DownloadsNothing()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, new SplitTextEntity { Id = Id, Body = BigText });
        using var rig = new Rig<SplitTextEntity>(inner);
        rig.H.SetupGet(Pk, Rk, row);

        Assert.True(await rig.Store.ExistsAsync(Id, Ct));
        Assert.Equal(0, rig.Blobs.Count("read"));
        // Not cached as an entity without its value: the next read resolves it.
        Assert.Equal(BigText, (await rig.Store.OneAsync(Id, Ct))!.Body);
    }

    // ── carried fixes: shared and failed prepared content ──────────────────

    [Fact]
    public async Task Create_OneNonSeekableContentOnTwoProperties_WritesBoth()
    {
        using var rig = new Rig<TwoBlobEntity>();
        var data = RandomBytes(5_000);
        var shared = TableBlobContent.FromStream(new NonSeekable(data), "application/octet-stream");

        await rig.Store.CreateAsync(new TwoBlobEntity { Id = Id, First = shared, Second = shared }, Ct);

        var row = Assert.Single(rig.Rows);
        foreach (var column in new[] { "First", "Second" })
        {
            await using var stream = await rig.Blobs.Inner.OpenReadAsync(column, Reference(row, column).Path, Ct);
            Assert.Equal(data, await ReadAllAsync(stream));
        }
        await using var back = await shared.OpenReadAsync(Ct);
        Assert.Equal(data, await ReadAllAsync(back));
    }

    [Fact]
    public async Task Create_SerializeFails_ReleasesPreparedTempFile()
    {
        using var rig = new Rig<NeverOffloadWithFileEntity>();
        var content = TableBlobContent.FromStream(new NonSeekable(RandomBytes(1_000)), "application/octet-stream");
        var entity = new NeverOffloadWithFileEntity { Id = Id, Body = Ascii(5_000_000), File = content };

        await Assert.ThrowsAsync<OversizedValueException>(() => rig.Store.CreateAsync(entity, Ct));

        Assert.Null(content.Source);
        Assert.Empty(rig.Log);
    }

    [Fact]
    public async Task Create_UploadFails_ReleasesPreparedTempFile()
    {
        using var rig = new Rig<FileEntity>();
        var content = TableBlobContent.FromStream(new NonSeekable(RandomBytes(1_000)), "application/octet-stream");
        string? spoolPath = null;
        rig.Blobs.OnGetState = _ => spoolPath = (content.Source as FileStream)?.Name;
        rig.Blobs.GetStateFailure = new RequestFailedException(500, "blob service down");

        await Assert.ThrowsAsync<RequestFailedException>(() => rig.Store.CreateAsync(new FileEntity { Id = Id, File = content }, Ct));

        Assert.NotNull(spoolPath);
        Assert.False(File.Exists(spoolPath), "the temporary copy must be deleted");
        Assert.Null(content.Source);
        Assert.DoesNotContain("table:add", rig.Log);
    }

    [Fact]
    public async Task PlainType_WithBlobOffloadConfigured_NeverTouchesTheBlobStore()
    {
        using var rig = new Rig<PlainEntity>();
        var row = new TableEntity(Pk, Rk) { ETag = new ETag("W/\"v1\""), ["Body"] = "b", ["Id"] = Id };
        rig.H.SetupGet(row);
        rig.H.SetupUpsert();
        rig.H.SetupQueryByPartition(row);

        await rig.Store.CreateAsync(new PlainEntity { Id = Id, Body = new string('x', 10_000) }, Ct);
        await rig.Store.UpsertAsync(new PlainEntity { Id = Id, Body = "b" }, Ct);
        await rig.Store.UpdateAsync(new PlainEntity { Id = Id, Body = "c", ETag = "W/\"v1\"" }, Ct);
        rig.H.Cache.Remove($"{typeof(PlainEntity).FullName}:entity:{Id}");
        Assert.Equal("b", (await rig.Store.OneAsync(Id, Ct))!.Body);
        await rig.Store.DeleteAsync(Id, Ct);
        await rig.Store.DeletePartitionAsync(Pk, Ct);

        Assert.DoesNotContain(rig.Log, l => l.StartsWith("blob:", StringComparison.Ordinal));
        // The same single-row SDK calls as without offload: no prior read, no transaction.
        await rig.Table.Received(1).AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>());
        await rig.Table.Received(1).UpsertEntityAsync(Arg.Any<TableEntity>(), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
        await rig.Table.Received(1).DeleteEntityAsync(Pk, Rk, Arg.Any<ETag>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_TableBlobContentWithoutOffload_Throws()
    {
        using var h = new StorageHarness<FileEntity>();

        await Assert.ThrowsAsync<OversizedValueException>(() =>
            h.Store.CreateAsync(new FileEntity { Id = Id, File = TableBlobContent.FromBytes([1], "image/png") }, Ct));
        await h.Table.DidNotReceiveWithAnyArgs().AddEntityAsync<TableEntity>(default!, default);
    }

    /// <summary>A forward-only stream over fixed bytes.</summary>
    private sealed class NonSeekable(byte[] data) : Stream
    {
        private readonly MemoryStream inner = new(data, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
