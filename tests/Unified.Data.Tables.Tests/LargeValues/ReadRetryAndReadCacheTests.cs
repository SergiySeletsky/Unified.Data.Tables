using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The read-cache guard (cache lane: a read caches what it found
/// only while no write or delete of the entity was noted since the read began) with the split-aware read
/// retry (rows lane: when the continuation rows or the blob a read fetches belong to another version of
/// the row, the row is read again and the read starts over from the new version).
/// <para>
/// The cache lane's own race tests hold the response of the continuation-row query in flight, so the
/// read there never retries. Here the writer commits BETWEEN the read of the primary row and the read of
/// its continuation rows (or its blob), which is the case that makes the read retry.
/// </para>
/// </summary>
public class ReadRetryAndReadCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<bool, bool> ShapesAndWriters() => new()
    {
        { false, false }, // continuation rows, the writer is another store with its own cache
        { false, true },  // continuation rows, the writer is the reading store itself
        { true, false },  // a blob
        { true, true },
    };

    private static string Body(bool blob, int version) => Ascii(blob ? 40_000 : 700_000, seed: version);

    private static ReadRetryRig<SplitTextEntity> Rig(bool blob) =>
        new(blob ? new BlobOffloadOptions { ThresholdBytes = 1_000, CleanupGrace = TimeSpan.Zero } : null);

    /// <summary>Runs <paramref name="write"/> once, right after the reader's point read of the whole row was answered.</summary>
    private static void AfterThePrimaryIsRead(ReadRetryRig<SplitTextEntity> rig, Func<Task> write)
    {
        var armed = 1;
        rig.Table.ResponseInFlight = async request =>
        {
            if (request.IsWholeRowPointRead && Interlocked.Exchange(ref armed, 0) == 1)
                await write();
        };
    }

    [Theory]
    [MemberData(nameof(ShapesAndWriters))]
    public async Task RowReplacedBetweenThePrimaryAndItsPartsOrBlob_TheReadReturnsTheNewVersionWhole_AndTheCacheNeverServesTheOld(bool blob, bool sameStore)
    {
        using var rig = Rig(blob);
        // Seeded through another store: a write that fits one row caches what it wrote, and the read under
        // test must go to the table.
        await rig.NewStore().CreateAsync(new SplitTextEntity { Id = "p|r", Title = "v1", Body = Body(blob, 1) }, Ct);
        var writer = sameStore ? rig.Store : rig.NewStore();
        AfterThePrimaryIsRead(rig, () => writer.UpsertAsync(new SplitTextEntity { Id = "p|r", Title = "v2", Body = Body(blob, 2) }, Ct));

        var read = await rig.Store.OneAsync("p|r", Ct);
        rig.Table.ResponseInFlight = null;

        // Never a mix of two versions: the new title with the new body, under the row's current version.
        var stored = rig.Table.Stored("p", "r")!;
        Assert.NotNull(read);
        Assert.Equal("v2", read.Title);
        Assert.Equal(Body(blob, 2), read.Body, StringComparer.Ordinal);
        Assert.Equal(stored.ETag.ToString(), read.ETag);

        // Whatever the cache holds now, the next read answers the new version.
        var next = await rig.Store.OneAsync("p|r", Ct);
        Assert.Equal("v2", next!.Title);
        Assert.Equal(Body(blob, 2), next.Body, StringComparer.Ordinal);
        Assert.Equal(stored.ETag.ToString(), next.ETag);
        var listed = Assert.Single(await rig.Store.QueryAsync("p", Ct));
        Assert.Equal("v2", listed.Title);
    }

    [Theory]
    [MemberData(nameof(ShapesAndWriters))]
    public async Task RowDeletedBetweenThePrimaryAndItsPartsOrBlob_TheReadAnswersNothing_AndTheEntityIsNotReadableAfterwards(bool blob, bool sameStore)
    {
        using var rig = Rig(blob);
        // Seeded through another store: a write that fits one row caches what it wrote, and the read under
        // test must go to the table.
        await rig.NewStore().CreateAsync(new SplitTextEntity { Id = "p|r", Title = "v1", Body = Body(blob, 1) }, Ct);
        var writer = sameStore ? rig.Store : rig.NewStore();
        AfterThePrimaryIsRead(rig, () => writer.DeleteAsync("p|r", Ct));

        var read = await rig.Store.OneAsync("p|r", Ct);
        rig.Table.ResponseInFlight = null;

        Assert.Null(read);
        Assert.Empty(rig.Table.StoredRows());
        Assert.Null(await rig.Store.OneAsync("p|r", Ct));
        Assert.False(await rig.Store.ExistsAsync("p|r", Ct));
        Assert.Empty(await rig.Store.QueryAsync("p", Ct));
    }

    // The same interleaving inside a cached partition query: the list that is cached must not hold the
    // version that was replaced while the query read it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartitionQuery_WhoseRowIsReplacedWhileItsPartsAreRead_DoesNotCacheTheOldVersion(bool sameStore)
    {
        using var rig = Rig(blob: false);
        await rig.NewStore().CreateAsync(new SplitTextEntity { Id = "p|r", Title = "v1", Body = Body(false, 1) }, Ct);
        var writer = sameStore ? rig.Store : rig.NewStore();
        var armed = 1;
        rig.Table.ResponseInFlight = async request =>
        {
            // The query that lists the partition's entities (not the one for an entity's continuation rows).
            if (request.IsEntityQuery && Interlocked.Exchange(ref armed, 0) == 1)
                await writer.UpsertAsync(new SplitTextEntity { Id = "p|r", Title = "v2", Body = Body(false, 2) }, Ct);
        };

        var listed = Assert.Single(await rig.Store.QueryAsync("p", Ct));
        rig.Table.ResponseInFlight = null;

        Assert.Equal("v2", listed.Title);
        Assert.Equal(Body(false, 2), listed.Body, StringComparer.Ordinal);
        var again = Assert.Single(await rig.Store.QueryAsync("p", Ct));
        Assert.Equal("v2", again.Title);
        Assert.Equal("v2", (await rig.Store.OneAsync("p|r", Ct))!.Title);
    }

    // COMPLETENESS-3 (the same defect as CACHE-5, ruling R5). The row is replaced three times while one
    // read is under way (a writer commits right after each of the reader's first three reads of the main
    // row). Every version that was ever stored is whole. The read is retried with the writers' pause, as a
    // write is, and its fourth attempt returns the fourth version; it is never an integrity failure.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RowReplacedDuringEachOfThreeAttempts_IsReadWholeOnTheFourth_NotAnIntegrityFailure(bool blob)
    {
        using var rig = Rig(blob);
        await rig.NewStore().CreateAsync(new SplitTextEntity { Id = "p|r", Title = "v1", Body = Body(blob, 1) }, Ct);
        var writer = rig.NewStore();
        var version = 1;
        rig.Table.ResponseInFlight = async request =>
        {
            // After each of the reader's first three point reads of the main row, another writer commits.
            if (request.IsWholeRowPointRead && version < 4)
            {
                var next = Interlocked.Increment(ref version);
                await writer.UpsertAsync(new SplitTextEntity { Id = "p|r", Title = "v" + next, Body = Body(blob, next) }, Ct);
            }
        };

        SplitTextEntity? read = null;
        var failure = await Record.ExceptionAsync(async () => read = await rig.Store.OneAsync("p|r", Ct));
        rig.Table.ResponseInFlight = null;

        // The table holds version 4, whole: a fresh store reads it.
        var stored = await rig.NewStore().OneAsync("p|r", Ct);
        Assert.Equal("v4", stored!.Title);
        Assert.Equal(Body(blob, 4), stored.Body, StringComparer.Ordinal);
        // The row changed under the read each time; nothing stored was ever damaged.
        Assert.False(failure is TableValueIntegrityException, "The read threw " + failure?.GetType().Name + ": " + failure?.Message);
        Assert.Null(failure);
        Assert.Equal("v4", read!.Title);
        Assert.Equal(Body(blob, 4), read.Body, StringComparer.Ordinal);
    }

    // The same on the in-memory store (an offloaded value: the reader's blob is released by each replace).
    [Fact]
    public async Task InMemory_RowReplacedDuringEachOfThreeAttempts_IsReadWholeOnTheFourth_NotAnIntegrityFailure()
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000, CleanupGrace = TimeSpan.Zero },
        });
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Title = "v1", Body = Body(true, 1) }, Ct);
        var version = 1;
        var reading = true;
        store.Blobs.BeforeOpenRead = async (_, _) =>
        {
            // Before each of the reader's first three downloads, another writer replaces the row.
            if (reading && version < 4)
            {
                reading = false;
                var next = Interlocked.Increment(ref version);
                await store.UpsertAsync(new SplitTextEntity { Id = "p|r", Title = "v" + next, Body = Body(true, next) }, Ct);
                reading = true;
            }
        };

        store.LargeValueRules.RetryDelay = (_, _) => Task.CompletedTask;
        SplitTextEntity? read = null;
        var failure = await Record.ExceptionAsync(async () => read = await store.OneAsync("p|r", Ct));
        store.Blobs.BeforeOpenRead = null;

        var stored = await store.OneAsync("p|r", Ct);
        Assert.Equal("v4", stored!.Title);
        Assert.Equal(Body(true, 4), stored.Body, StringComparer.Ordinal);
        Assert.False(failure is TableValueIntegrityException, "The read threw " + failure?.GetType().Name + ": " + failure?.Message);
        Assert.Null(failure);
        Assert.Equal("v4", read!.Title);
    }
}

/// <summary>
/// A <see cref="TableStorage{T}"/> over the suite's in-memory <see cref="RacingTableClient"/> (real
/// versions, conditions and atomic transactions) with an in-memory blob store and a real
/// <see cref="MemoryCache"/>, so the store's own code runs unchanged and a test can act between two
/// requests.
/// </summary>
internal sealed class ReadRetryRig<T> : IDisposable where T : class, IEntity, new()
{
    private readonly List<MemoryCache> caches = [];
    private readonly BlobOffloadOptions? offload;

    public ReadRetryRig(BlobOffloadOptions? offload = null)
    {
        this.offload = offload;
        Store = NewStore();
    }

    public RacingTableClient Table { get; } = new();

    public InMemoryOffloadBlobStore Blobs { get; } = new();

    /// <summary>The store under test (it has a cache of its own).</summary>
    public TableStorage<T> Store { get; }

    /// <summary>Another store over the same table and blobs, with its own cache ("another process").</summary>
    public TableStorage<T> NewStore()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(T).Name).Returns(Table);
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null,
            new UnifiedTableStorageOptions { BlobOffload = offload }, Blobs)
        {
            // A lost race is retried at once: the tests are about what is read, not about the pause.
            LargeValueRetryDelay = (_, _) => Task.CompletedTask,
        };
    }

    public void Dispose()
    {
        foreach (var cache in caches)
            cache.Dispose();
    }
}
