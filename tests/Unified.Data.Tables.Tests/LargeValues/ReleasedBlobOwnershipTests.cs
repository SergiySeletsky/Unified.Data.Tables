using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The cleanup that follows a committed write or delete of a row deletes only that row's own blobs:
/// offload-shaped names under <c>{table}/{partitionKey}/{rowKey}/</c> of the store's table. A row copied
/// cell for cell from another table, or to another row key, still references the SOURCE row's blob;
/// saving or deleting through the copy must leave that blob alone, or the source row could never be
/// read again. The copy keeps working: a save through it uploads a blob of its own.
/// </summary>
public class ReleasedBlobOwnershipTests
{
    private const string Pk = "p";
    private const string CopyTable = "SplitTextEntityCopy";

    private static readonly string Text = Ascii(40_000, seed: 51);
    private static readonly string NewText = Ascii(40_000, seed: 52);
    private static readonly string Sha64 = new('a', 64);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // CleanupGrace zero: a released blob is deleted at once (the in-memory service clock is strictly
    // increasing, so every stored blob is already past the grace).
    private static UnifiedTableStorageOptions Options(string? table = null)
    {
        var options = new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000, CleanupGrace = TimeSpan.Zero },
            Cache = CachePolicy.Disabled,
        };
        if (table is not null) options.TableNameResolver = _ => table;
        return options;
    }

    private static InMemoryStorage<T> Memory<T>(string? table = null) where T : class, IEntity, new() => new(null, Options(table));

    private static BlobReference Reference(TableEntity row, string column) =>
        BlobReference.Parse(column, (string)row[column + "__blob"]);

    /// <summary>Stores the row <paramref name="rk"/> again under <paramref name="newRk"/>, cell for cell, as a re-keying script would.</summary>
    private static void RekeyRaw<T>(InMemoryStorage<T> store, string rk, string newRk) where T : class, IEntity, new()
    {
        var source = store.PhysicalRow(Pk, rk) ?? throw new InvalidOperationException($"no row '{rk}'");
        var copy = new TableEntity(Pk, newRk);
        foreach (var (key, value) in source)
        {
            if (key is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag"))
                copy[key] = value;
        }
        store.SeedPhysicalRow(copy);
    }

    /// <summary>
    /// Copies the row <paramref name="rk"/> into another store's table, cell for cell. Two tables of one
    /// account share the blob container, which an in-memory store does not: the source's blobs are put,
    /// under their own names, into the copy's container as well.
    /// </summary>
    private static async Task CopyRawAsync<T>(InMemoryStorage<T> source, InMemoryStorage<T> copy, string rk) where T : class, IEntity, new()
    {
        copy.SeedPhysicalRow(source.PhysicalRow(Pk, rk) ?? throw new InvalidOperationException($"no row '{rk}'"));
        foreach (var name in source.Blobs.Names)
        {
            await using var stream = await source.Blobs.OpenReadAsync("x", name, CancellationToken.None);
            var bytes = await ReadAllAsync(stream);
            copy.Blobs.Seed(name, bytes, Sha(bytes), DateTimeOffset.UtcNow.AddHours(-1));
        }
    }

    // ── InMemoryStorage: a row re-keyed inside one table ───────────────────

    [Fact]
    public async Task InMemory_UpsertThroughARekeyedCopy_KeepsTheSourceRowsBlob()
    {
        var store = Memory<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = Text, Title = "source" }, Ct);
        var sourceBlob = Assert.Single(store.OffloadedBlobNames());
        RekeyRaw(store, "r", "r2");

        var loaded = await store.OneAsync("p|r2", Ct);
        Assert.Equal(Text, loaded!.Body, StringComparer.Ordinal);
        loaded.Title = "copy";
        await store.UpsertAsync(loaded, Ct);

        Assert.Equal(Text, (await store.OneAsync("p|r", Ct))!.Body, StringComparer.Ordinal);
        var copyBlob = Reference(store.PhysicalRow(Pk, "r2")!, "Body").Path;
        Assert.StartsWith("SplitTextEntity/p/r2/Body/", copyBlob, StringComparison.Ordinal);
        Assert.Equal(new[] { sourceBlob, copyBlob }.Order(StringComparer.Ordinal), store.OffloadedBlobNames());
        var reread = await store.OneAsync("p|r2", Ct);
        Assert.Equal(Text, reread!.Body, StringComparer.Ordinal);
        Assert.Equal("copy", reread.Title);

        // From here on the copy owns its blob: replacing its value deletes that one, and never the source's.
        reread.Body = NewText;
        await store.UpsertAsync(reread, Ct);

        Assert.Contains(sourceBlob, store.OffloadedBlobNames());
        Assert.DoesNotContain(copyBlob, store.OffloadedBlobNames());
        Assert.Equal(2, store.OffloadedBlobNames().Count);
        Assert.Equal(Text, (await store.OneAsync("p|r", Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal(NewText, (await store.OneAsync("p|r2", Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task InMemory_DeleteOfARekeyedCopy_KeepsTheSourceRowsBlob()
    {
        var store = Memory<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = Text }, Ct);
        var sourceBlob = Assert.Single(store.OffloadedBlobNames());
        RekeyRaw(store, "r", "r2");

        await store.DeleteAsync("p|r2", Ct);

        Assert.Null(await store.OneAsync("p|r2", Ct));
        Assert.Equal([sourceBlob], store.OffloadedBlobNames());
        Assert.Equal(Text, (await store.OneAsync("p|r", Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task InMemory_UpsertBatchThroughARekeyedCopy_KeepsTheSourceRowsBlob()
    {
        var store = Memory<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = Text }, Ct);
        var sourceBlob = Assert.Single(store.OffloadedBlobNames());
        RekeyRaw(store, "r", "r2");

        await store.UpsertBatchAsync([new SplitTextEntity { Id = "p|r2", Body = "small now" }, new SplitTextEntity { Id = "p|r3", Body = "b" }], Ct);

        Assert.Equal([sourceBlob], store.OffloadedBlobNames());
        Assert.Equal(Text, (await store.OneAsync("p|r", Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal("small now", (await store.OneAsync("p|r2", Ct))!.Body);
    }

    [Fact]
    public async Task InMemory_BlobContent_UpsertThroughARekeyedCopy_KeepsTheSourceRowsBlob()
    {
        var store = Memory<FileEntity>();
        var bytes = RandomBytes(5_000, seed: 53);
        await store.CreateAsync(new FileEntity { Id = "p|r", File = TableBlobContent.FromBytes(bytes, "image/png"), Title = "source" }, Ct);
        var sourceBlob = Assert.Single(store.OffloadedBlobNames());
        RekeyRaw(store, "r", "r2");

        var loaded = await store.OneAsync("p|r2", Ct);
        loaded!.Title = "copy";
        await store.UpsertAsync(loaded, Ct);

        var copyBlob = Reference(store.PhysicalRow(Pk, "r2")!, "File").Path;
        Assert.StartsWith("FileEntity/p/r2/File/", copyBlob, StringComparison.Ordinal);
        Assert.Equal(new[] { sourceBlob, copyBlob }.Order(StringComparer.Ordinal), store.OffloadedBlobNames());
        foreach (var id in new[] { "p|r", "p|r2" })
        {
            var read = await store.OneAsync(id, Ct);
            await using var stream = await read!.File!.OpenReadAsync(Ct);
            Assert.Equal(bytes, await ReadAllAsync(stream));
        }
    }

    // ── InMemoryStorage: a row copied into another table ───────────────────

    [Fact]
    public async Task InMemory_UpsertThroughACopyInAnotherTable_KeepsTheSourceRowsBlob()
    {
        var source = Memory<SplitTextEntity>();
        await source.CreateAsync(new SplitTextEntity { Id = "p|r", Body = Text, Title = "source" }, Ct);
        var sourceBlob = Assert.Single(source.OffloadedBlobNames());
        var copy = Memory<SplitTextEntity>(CopyTable);
        await CopyRawAsync(source, copy, "r");

        var loaded = await copy.OneAsync("p|r", Ct);
        Assert.Equal(Text, loaded!.Body, StringComparer.Ordinal);
        loaded.Title = "copy";
        await copy.UpsertAsync(loaded, Ct);

        // The copy's container still holds the source's blob, beside the copy's own.
        var copyBlob = Reference(copy.PhysicalRow(Pk, "r")!, "Body").Path;
        Assert.StartsWith(CopyTable + "/p/r/Body/", copyBlob, StringComparison.Ordinal);
        Assert.Equal(new[] { sourceBlob, copyBlob }.Order(StringComparer.Ordinal), copy.OffloadedBlobNames());
        Assert.Equal(Text, (await copy.OneAsync("p|r", Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal(Text, (await source.OneAsync("p|r", Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task InMemory_DeleteOfACopyInAnotherTable_KeepsTheSourceRowsBlob()
    {
        var source = Memory<SplitTextEntity>();
        await source.CreateAsync(new SplitTextEntity { Id = "p|r", Body = Text }, Ct);
        var sourceBlob = Assert.Single(source.OffloadedBlobNames());
        var copy = Memory<SplitTextEntity>(CopyTable);
        await CopyRawAsync(source, copy, "r");

        await copy.DeleteAsync("p|r", Ct);

        Assert.Equal(0, copy.Count);
        Assert.Equal([sourceBlob], copy.OffloadedBlobNames());
    }

    // ── existing behaviour kept: a row's own blobs still go ────────────────

    [Fact]
    public async Task InMemory_OwnBlobs_AreStillDeleted_OnReplaceAndOnDelete()
    {
        var store = Memory<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = Text }, Ct);
        var first = Assert.Single(store.OffloadedBlobNames());

        await store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = NewText }, Ct);
        var second = Assert.Single(store.OffloadedBlobNames());
        Assert.NotEqual(first, second);

        await store.DeleteAsync("p|r", Ct);
        Assert.Empty(store.OffloadedBlobNames());
    }

    // ── the shared core ───────────────────────────────────────────────────

    [Fact]
    public async Task DeleteReleasedBlobs_DeletesOnlyOffloadShapedNamesUnderTheRowsOwnPrefix()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var table = new LargeValueTable("T", new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero }, blobs);
        var old = DateTimeOffset.UtcNow.AddHours(-1);
        string Seed(string name)
        {
            blobs.Seed(name, [1], Sha([1]), old);
            return name;
        }

        string[] own =
        [
            Seed(ValueOffloadCoordinator.BlobName("T", "p", "r", "Body", Sha64)),
            Seed(ValueOffloadCoordinator.BlobName("T", "p", "r", "Inner_Doc", Sha64)),
        ];
        string[] foreign =
        [
            Seed(ValueOffloadCoordinator.BlobName("T", "p", "r2", "Body", Sha64)),     // another row of the partition
            Seed(ValueOffloadCoordinator.BlobName("T", "p", "r~0001", "Body", Sha64)),
            Seed(ValueOffloadCoordinator.BlobName("T", "q", "r", "Body", Sha64)),      // another partition
            Seed(ValueOffloadCoordinator.BlobName("Other", "p", "r", "Body", Sha64)),  // another table
            Seed("T/p/r/Body/not-a-hash"),                                             // not an offloaded value's name
            Seed($"T/p/r/Body/{Sha64}/deeper"),
            Seed($"T/p/r/{Sha64}"),
            Seed("T/p/r/stray"),
        ];
        var skipped = new List<string>();

        await table.DeleteReleasedBlobsAsync("p", "r", [.. own, .. foreign], serviceNow: null, skipped.Add, onFailure: null, Ct);

        Assert.Equal(foreign.Order(StringComparer.Ordinal), blobs.Names.Order(StringComparer.Ordinal));
        Assert.Equal(foreign, skipped);
    }

    [Theory]
    [InlineData("a/b", "x y.")]
    [InlineData(".", "..")]
    [InlineData("p", "p")]
    [InlineData("%2E", "r%2Fs")]
    public async Task DeleteReleasedBlobs_KeysThatNeedEscaping_StillOwnTheirBlobs(string pk, string rk)
    {
        var blobs = new InMemoryOffloadBlobStore();
        var table = new LargeValueTable("T", new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero }, blobs);
        var old = DateTimeOffset.UtcNow.AddHours(-1);
        var own = ValueOffloadCoordinator.BlobName("T", pk, rk, "Body", Sha64);
        // The same characters spelled differently name another row's blob.
        var lookalike = ValueOffloadCoordinator.BlobName("T", pk, Uri.UnescapeDataString(rk) + "x", "Body", Sha64);
        blobs.Seed(own, [1], Sha([1]), old);
        blobs.Seed(lookalike, [1], Sha([1]), old);
        var skipped = new List<string>();

        await table.DeleteReleasedBlobsAsync(pk, rk, [own, lookalike], serviceNow: null, skipped.Add, onFailure: null, Ct);

        Assert.Equal([lookalike], blobs.Names);
        Assert.Equal([lookalike], skipped);
    }

    [Theory]
    [InlineData("p\uD800", "r")]
    [InlineData("p", "r\uDC00")]
    public async Task DeleteReleasedBlobs_AKeyThatCannotNameABlob_OwnsNone_AndDoesNotThrow(string pk, string rk)
    {
        var blobs = new InMemoryOffloadBlobStore();
        var table = new LargeValueTable("T", new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero }, blobs);
        var other = ValueOffloadCoordinator.BlobName("T", "p", "r", "Body", Sha64);
        blobs.Seed(other, [1], Sha([1]), DateTimeOffset.UtcNow.AddHours(-1));
        var skipped = new List<string>();

        await table.DeleteReleasedBlobsAsync(pk, rk, [other], serviceNow: null, skipped.Add, onFailure: null, Ct);

        Assert.Equal([other], blobs.Names);
        Assert.Equal([other], skipped);
    }

    // ── TableStorage (mocked table): nothing is deleted, and the reason is logged at debug ──

    [Fact]
    public async Task TableStorage_UpsertOverARowThatReferencesAnotherRowsBlob_LeavesItAlone_AndSaysSoAtDebug()
    {
        var inner = new InMemoryOffloadBlobStore();
        var sourceRow = await WrittenRowAsync(inner, new SplitTextEntity { Id = "p|r", Body = Text });
        var sourceBlob = Reference(sourceRow, "Body").Path;
        Age(inner, sourceBlob);
        var copyRow = new TableEntity(Pk, "r2") { ETag = new ETag("W/\"v1\"") };
        foreach (var (key, value) in sourceRow)
        {
            if (key is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag"))
                copyRow[key] = value;
        }
        var log = new List<string>();
        var blobs = new RecordingBlobStore(log, inner);
        var logger = new ListLogger<TableStorage<SplitTextEntity>>();
        using var h = new StorageHarness<SplitTextEntity>(options: Options(), logger: logger, blobs: blobs);
        h.SetupGet(Pk, "r2", copyRow);
        h.SetupUpdate("W/\"v2\"");
        h.SetupDelete();
        h.SetupTransaction();

        await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r2", Body = NewText }, Ct);
        await h.Store.UpsertBatchAsync([new SplitTextEntity { Id = "p|r2", Body = "small now" }], Ct);
        await h.Store.DeleteAsync("p|r2", Ct);

        Assert.Equal(0, blobs.Count("delete"));
        Assert.Contains(sourceBlob, inner.Names);
        var debug = logger.Entries.Where(e => e.Level == LogLevel.Debug && e.Message.Contains(sourceBlob, StringComparison.Ordinal)).ToList();
        Assert.Equal(3, debug.Count); // once for the upsert, once for the batch, once for the delete
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task TableStorage_ReplaceOfARowsOwnBlob_StillDeletesIt()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRowAsync(inner, new SplitTextEntity { Id = "p|r", Body = Text });
        var ownBlob = Reference(row, "Body").Path;
        Age(inner, ownBlob);
        var log = new List<string>();
        var blobs = new RecordingBlobStore(log, inner);
        using var h = new StorageHarness<SplitTextEntity>(options: Options(), blobs: blobs);
        h.SetupGet(Pk, "r", row);
        h.SetupUpdate("W/\"v2\"");

        await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = NewText }, Ct);

        Assert.DoesNotContain(ownBlob, inner.Names);
        Assert.Contains("delete " + ownBlob, blobs.Calls);
    }

    /// <summary>The row a create of <paramref name="entity"/> writes, with its blob left in <paramref name="inner"/>.</summary>
    private static async Task<TableEntity> WrittenRowAsync(InMemoryOffloadBlobStore inner, SplitTextEntity entity)
    {
        var written = new List<TableEntity>();
        using var h = new StorageHarness<SplitTextEntity>(options: Options(), blobs: inner);
        h.Table.When(t => t.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>()))
            .Do(c => written.Add(c.Arg<TableEntity>()));
        h.SetupAdd("W/\"v1\"");
        await h.Store.CreateAsync(entity, Ct);
        var row = Assert.Single(written);
        row.ETag = new ETag("W/\"v1\"");
        return row;
    }

    /// <summary>Makes a stored blob look written an hour ago.</summary>
    private static void Age(InMemoryOffloadBlobStore inner, string name)
    {
        using var stream = inner.OpenReadAsync("x", name, CancellationToken.None).GetAwaiter().GetResult();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        inner.Seed(name, buffer.ToArray(), Sha(buffer.ToArray()), DateTimeOffset.UtcNow.AddHours(-1));
    }

    /// <summary>Records what the store logs.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}

/// <summary>
/// <see cref="ReleasedBlobOwnershipTests"/> against a local Azurite, where two tables really share one
/// blob container: a row copied cell for cell into a second table, or to another row key of its table,
/// is saved or deleted through <see cref="TableStorage{T}"/>, and the source row still reads back whole.
/// </summary>
public sealed class AzuriteReleasedBlobOwnershipTests : AzuriteCleanupTestBase
{
    private const string SourceTable = nameof(SplitTextEntity);
    private const string CopyTable = "SplitTextEntityCopy";

    private static readonly string Text = Ascii(40_000, seed: 61);
    private static readonly string NewText = Ascii(40_000, seed: 62);

    private static string BlobOf(TableEntity row) => BlobReference.Parse("Body", (string)row["Body__blob"]).Path;

    /// <summary>The source row <c>r</c> with an offloaded value; returns its blob.</summary>
    private async Task<string> CreateSourceAsync()
    {
        await Store<SplitTextEntity>(TimeSpan.Zero).CreateAsync(new SplitTextEntity { Id = Id("r"), Body = Text, Title = "source" }, Ct);
        var blob = Assert.Single(await BlobNamesAsync());
        Assert.Equal($"{SourceTable}/{Pk}/r/Body/{Sha(Utf8(Text))}", blob);
        return blob;
    }

    private async Task AssertSourceReadsBackAsync() =>
        Assert.Equal(Text, (await Store<SplitTextEntity>(TimeSpan.Zero).OneAsync(Id("r"), Ct))!.Body, StringComparer.Ordinal);

    [Fact]
    public async Task UpsertThroughACopyInAnotherTable_KeepsTheSourceRowsBlob()
    {
        SkipUnlessAvailable();
        var sourceBlob = await CreateSourceAsync();
        await CopyRowRawAsync(SourceTable, "r", CopyTable, "r");
        await BlobsOlderThanAsync(TimeSpan.Zero); // past the (zero) cleanup grace on the service clock

        var copy = Store<SplitTextEntity>(TimeSpan.Zero, CopyTable);
        var loaded = await copy.OneAsync(Id("r"), Ct);
        Assert.Equal(Text, loaded!.Body, StringComparer.Ordinal);
        loaded.Title = "copy";
        await copy.UpsertAsync(loaded, Ct);

        await AssertSourceReadsBackAsync();
        var copyBlob = BlobOf(Assert.Single(await RawRowsAsync(CopyTable)));
        Assert.Equal($"{CopyTable}/{Pk}/r/Body/{Sha(Utf8(Text))}", copyBlob);
        Assert.Equal(new[] { sourceBlob, copyBlob }.Order(StringComparer.Ordinal), await BlobNamesAsync());
        var reread = await copy.OneAsync(Id("r"), Ct);
        Assert.Equal(Text, reread!.Body, StringComparer.Ordinal);
        Assert.Equal("copy", reread.Title);

        // From here on the copy owns its blob: replacing its value deletes that one, and never the source's.
        await BlobsOlderThanAsync(TimeSpan.Zero);
        reread.Body = NewText;
        await copy.UpsertAsync(reread, Ct);

        var names = await BlobNamesAsync();
        Assert.Contains(sourceBlob, names);
        Assert.DoesNotContain(copyBlob, names);
        Assert.Equal(2, names.Count);
        await AssertSourceReadsBackAsync();
        Assert.Equal(NewText, (await copy.OneAsync(Id("r"), Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task DeleteOfACopyInAnotherTable_KeepsTheSourceRowsBlob()
    {
        SkipUnlessAvailable();
        var sourceBlob = await CreateSourceAsync();
        await CopyRowRawAsync(SourceTable, "r", CopyTable, "r");
        await BlobsOlderThanAsync(TimeSpan.Zero);

        var copy = Store<SplitTextEntity>(TimeSpan.Zero, CopyTable);
        Assert.Equal(Text, (await copy.OneAsync(Id("r"), Ct))!.Body, StringComparer.Ordinal);
        await copy.DeleteAsync(Id("r"), Ct);

        Assert.Empty(await RawRowsAsync(CopyTable));
        Assert.Equal([sourceBlob], await BlobNamesAsync());
        await AssertSourceReadsBackAsync();
    }

    [Fact]
    public async Task UpsertThroughARekeyedCopy_KeepsTheSourceRowsBlob()
    {
        SkipUnlessAvailable();
        var sourceBlob = await CreateSourceAsync();
        await CopyRowRawAsync(SourceTable, "r", SourceTable, "r2");
        await BlobsOlderThanAsync(TimeSpan.Zero);

        var store = Store<SplitTextEntity>(TimeSpan.Zero);
        var loaded = await store.OneAsync(Id("r2"), Ct);
        Assert.Equal(Text, loaded!.Body, StringComparer.Ordinal);
        loaded.Title = "copy";
        await store.UpsertAsync(loaded, Ct);

        await AssertSourceReadsBackAsync();
        var copyBlob = BlobOf((await RawRowsAsync(SourceTable)).Single(r => r.RowKey == "r2"));
        Assert.Equal($"{SourceTable}/{Pk}/r2/Body/{Sha(Utf8(Text))}", copyBlob);
        Assert.Equal(new[] { sourceBlob, copyBlob }.Order(StringComparer.Ordinal), await BlobNamesAsync());
        var reread = await store.OneAsync(Id("r2"), Ct);
        Assert.Equal(Text, reread!.Body, StringComparer.Ordinal);
        Assert.Equal("copy", reread.Title);
    }

    [Fact]
    public async Task DeleteOfARekeyedCopy_KeepsTheSourceRowsBlob()
    {
        SkipUnlessAvailable();
        var sourceBlob = await CreateSourceAsync();
        await CopyRowRawAsync(SourceTable, "r", SourceTable, "r2");
        await BlobsOlderThanAsync(TimeSpan.Zero);

        await Store<SplitTextEntity>(TimeSpan.Zero).DeleteAsync(Id("r2"), Ct);

        Assert.Equal(["r"], (await RawRowsAsync(SourceTable)).Select(r => r.RowKey));
        Assert.Equal([sourceBlob], await BlobNamesAsync());
        await AssertSourceReadsBackAsync();
    }

    [Fact]
    public async Task OwnBlobs_AreStillDeleted_OnReplaceAndOnDelete()
    {
        SkipUnlessAvailable();
        var first = await CreateSourceAsync();
        await BlobsOlderThanAsync(TimeSpan.Zero);
        var store = Store<SplitTextEntity>(TimeSpan.Zero);

        await store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = NewText }, Ct);
        var second = Assert.Single(await BlobNamesAsync());
        Assert.NotEqual(first, second);

        await BlobsOlderThanAsync(TimeSpan.Zero);
        await store.DeleteAsync(Id("r"), Ct);
        Assert.Empty(await BlobNamesAsync());
    }
}
