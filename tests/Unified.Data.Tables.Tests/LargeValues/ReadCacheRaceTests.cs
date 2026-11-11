using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A read caches what it read some time after it read it: the response is in flight, and for a split-aware
/// type the continuation-row query and the blob download still have to run. A write or delete that commits
/// in between must not be followed by the read caching the older version: later reads would be served
/// that version from memory, a read-then-upsert caller would write it back, and a deleted entity would
/// stay readable. These tests hold a read at that point, commit a write or delete through the same store,
/// let the read finish, and then ask the store again.
/// </summary>
public class ReadCacheRaceTests
{
    private const string Pk = "p";
    private const string Id = "p|a";
    private const string Sibling = "p|b";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>How the body is stored, which decides what a read still fetches after the primary row.</summary>
    public enum Shape
    {
        /// <summary>One cell: the read is one request.</summary>
        Inline,

        /// <summary>Continuation rows: the read queries them after the primary row.</summary>
        ContinuationRows,

        /// <summary>A blob: the read downloads it after the primary row.</summary>
        Blob,
    }

    /// <summary>A write or delete of the entity <see cref="Id"/>, through the store under test.</summary>
    public enum Mutation
    {
        Upsert,
        Replace,
        Merge,
        UpsertBatch,
        Delete,
        DeletePartition,
    }

    private static readonly Shape[] Shapes = [Shape.Inline, Shape.ContinuationRows, Shape.Blob];

    private static readonly Mutation[] Mutations =
        [Mutation.Upsert, Mutation.Replace, Mutation.Merge, Mutation.UpsertBatch, Mutation.Delete, Mutation.DeletePartition];

    public static TheoryData<Shape, Mutation> ShapesAndMutations()
    {
        var data = new TheoryData<Shape, Mutation>();
        foreach (var shape in Shapes)
            foreach (var mutation in Mutations)
                data.Add(shape, mutation);
        return data;
    }

    public static TheoryData<Shape, Mutation, int> ShapesMutationsAndSteps()
    {
        var data = new TheoryData<Shape, Mutation, int>();
        foreach (var shape in Shapes)
            foreach (var mutation in Mutations)
                for (var step = 1; step <= 4; step++)
                    data.Add(shape, mutation, step);
        return data;
    }

    public static TheoryData<Mutation> AllMutations() => [.. Mutations];

    private static string BodyOf(Shape shape, int version) => shape switch
    {
        Shape.Inline => "body-v" + version,
        Shape.ContinuationRows => new string((char)('a' + version), 600_000),
        _ => Ascii(40_000, seed: version),
    };

    private static bool Deletes(Mutation mutation) => mutation is Mutation.Delete or Mutation.DeletePartition;

    // ── rig ────────────────────────────────────────────────────────────────

    /// <summary>A read held where its storage response is in flight.</summary>
    private sealed class Paused<TResult>(Task<TResult> reading, TaskCompletionSource release)
    {
        /// <summary>Lets the response arrive and waits for the read to finish.</summary>
        public Task<TResult> ResumeAsync()
        {
            release.TrySetResult();
            return reading.WaitAsync(Patience, Ct);
        }
    }

    /// <summary>
    /// A store over an in-memory table and blob container that a test can observe and pause, plus a second
    /// store over the same data with its own cache, to seed rows the store under test has not cached.
    /// </summary>
    private sealed class Rig<T> : IDisposable where T : class, IEntity, IRaceDoc, new()
    {
        private readonly MemoryCache seederCache = new(new MemoryCacheOptions());
        private readonly Action<UpdateBuilder<T>> setVersion2;

        public Rig(Shape shape, Action<UpdateBuilder<T>> setVersion2)
        {
            Shape = shape;
            this.setVersion2 = setVersion2;
            Store = NewStore(Cache);
            Seeder = NewStore(seederCache);
        }

        public Shape Shape { get; }

        public RacingTableClient Table { get; } = new();

        public InMemoryOffloadBlobStore Blobs { get; } = new();

        public ObservedMemoryCache Cache { get; } = new();

        public TableStorage<T> Store { get; }

        public TableStorage<T> Seeder { get; }

        public TableStorage<T> NewStore(IMemoryCache cache)
        {
            var service = Substitute.For<TableServiceClient>();
            service.GetTableClient(typeof(T).Name).Returns(Table);
            var options = new UnifiedTableStorageOptions
            {
                BlobOffload = Shape == Shape.Blob ? new BlobOffloadOptions { ThresholdBytes = 1_000 } : null,
            };
            return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, options, Blobs);
        }

        public T Doc(string id, int version) => new() { Id = id, Body = BodyOf(Shape, version), Version = version };

        public Task SeedAsync(string id, int version = 1) => Seeder.UpsertAsync(Doc(id, version), Ct);

        /// <summary>
        /// Starts <paramref name="read"/> and returns once what it asked storage for is in flight: the rows
        /// (or the blob) are the ones stored now, the read has not received them and has cached nothing.
        /// </summary>
        public async Task<Paused<TResult>> PauseAsync<TResult>(Func<Task<TResult>> read)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var armed = 1;
            async Task HoldOnce()
            {
                if (Interlocked.Exchange(ref armed, 0) != 1)
                    return;
                reached.TrySetResult();
                await release.Task;
            }

            if (Shape == Shape.Blob)
                Blobs.BeforeOpenRead = (_, _) => HoldOnce();
            else
                Table.ResponseInFlight = request => IsTheReadsLastFetch(request) ? HoldOnce() : Task.CompletedTask;

            var reading = read();
            var first = await Task.WhenAny(reached.Task, reading).WaitAsync(Patience, Ct);
            Assert.True(first == reached.Task, "The read finished without fetching what the test meant to hold in flight.");
            return new Paused<TResult>(reading, release);
        }

        // Inline: the entity itself (a point read or a query). Continuation rows: the range query a READ
        // sends for them (it projects nothing; a write's planning query projects the keys).
        private bool IsTheReadsLastFetch(TableRequest request) => Shape == Shape.Inline
            ? request.IsWholeRowPointRead || request.IsEntityQuery
            : request.IsContinuationRowQuery && request.Select is null;

        public Task MutateAsync(Mutation mutation, string id = Id) => MutateAsync(Store, mutation, id);

        public Task MutateAsync(TableStorage<T> store, Mutation mutation, string id = Id) => mutation switch
        {
            Mutation.Upsert => store.UpsertAsync(Doc(id, 2), Ct),
            Mutation.Replace => store.UpdateAsync(Doc(id, 2), ConcurrencyMode.LastWriterWins, Ct),
            Mutation.Merge => store.UpdateAsync(id, setVersion2, Ct),
            Mutation.UpsertBatch => store.UpsertBatchAsync([Doc(id, 2)], Ct),
            Mutation.Delete => store.DeleteAsync(id, Ct),
            _ => store.DeletePartitionAsync(EntityId.Split(id).PartitionKey, Ct),
        };

        /// <summary>The entities the table holds now (continuation rows are not entities), in key order.</summary>
        public List<(string Id, int Version)> StoredDocs(string? partition = null) =>
            Table.StoredRows()
                .Where(r => !r.ContainsKey(SystemColumnNames.SplitOf) && (partition is null || r.PartitionKey == partition))
                .Select(r => (EntityId.Combine(r.PartitionKey, r.RowKey), (int)r[nameof(IRaceDoc.Version)]))
                .ToList();

        public string? StoredETag(string id)
        {
            var (pk, rk) = EntityId.Split(id);
            return Table.Stored(pk, rk)?.ETag.ToString();
        }

        /// <summary>
        /// What the store answers now must be what the table holds after <paramref name="applied"/>: the new
        /// version with the stored row's own ETag, or nothing.
        /// </summary>
        public async Task AssertStoreAnswersWhatTheTableHoldsAsync(Mutation applied, string id = Id)
        {
            var next = await Store.OneAsync(id, Ct);
            if (Deletes(applied))
            {
                Assert.Null(next);
                Assert.False(await Store.ExistsAsync(id, Ct));
                Assert.DoesNotContain(await Store.QueryAsync(EntityId.Split(id).PartitionKey, Ct), d => d.Id == id);
                return;
            }

            Assert.NotNull(next);
            Assert.Equal(2, next.Version);
            // A merge sets the version only: the body stays the first one.
            Assert.Equal(BodyOf(Shape, applied == Mutation.Merge ? 1 : 2), next.Body, StringComparer.Ordinal);
            Assert.Equal(StoredETag(id), next.ETag);
            Assert.True(await Store.ExistsAsync(id, Ct));
            Assert.Equal(2, Assert.Single(await Store.QueryAsync(EntityId.Split(id).PartitionKey, Ct), d => d.Id == id).Version);
        }

        public void Dispose()
        {
            Cache.Dispose();
            seederCache.Dispose();
        }
    }

    private static Rig<RaceDoc> Split(Shape shape) => new(shape, b => b.SetProperty(x => x.Version, 2));

    private static Rig<RacePlainDoc> Plain() => new(Shape.Inline, b => b.SetProperty(x => x.Version, 2));

    private static IEnumerable<(string Id, int Version)> Versions<T>(IEnumerable<T> docs) where T : IEntity, IRaceDoc =>
        docs.Select(d => (d.Id, d.Version));

    // ── a point read that raced a write or delete ──────────────────────────

    private static async Task PointReadRacesAsync<T>(Rig<T> rig, Mutation mutation) where T : class, IEntity, IRaceDoc, new()
    {
        await rig.SeedAsync(Id);

        var paused = await rig.PauseAsync(() => rig.Store.OneAsync(Id, Ct));
        await rig.MutateAsync(mutation);
        var raced = await paused.ResumeAsync();

        // The racing read itself answers with what it started on; what matters is what it left behind.
        Assert.Equal(1, raced!.Version);
        await rig.AssertStoreAnswersWhatTheTableHoldsAsync(mutation);
    }

    [Theory]
    [MemberData(nameof(ShapesAndMutations))]
    public async Task PointRead_ThatRacedAWriteOrDelete_LeavesNothingOlderInTheCache(Shape shape, Mutation mutation)
    {
        using var rig = Split(shape);
        await PointReadRacesAsync(rig, mutation);
    }

    [Theory]
    [MemberData(nameof(AllMutations))]
    public async Task PointRead_OfAPlainType_ThatRacedAWriteOrDelete_LeavesNothingOlderInTheCache(Mutation mutation)
    {
        using var rig = Plain();
        await PointReadRacesAsync(rig, mutation);
    }

    private static async Task ExistsRacesADeleteAsync<T>(Rig<T> rig) where T : class, IEntity, IRaceDoc, new()
    {
        await rig.SeedAsync(Id);

        // ExistsAsync caches the row it found (one that needs nothing more fetched).
        var paused = await rig.PauseAsync(() => rig.Store.ExistsAsync(Id, Ct));
        await rig.Store.DeleteAsync(Id, Ct);
        Assert.True(await paused.ResumeAsync());

        Assert.False(await rig.Store.ExistsAsync(Id, Ct));
        Assert.Null(await rig.Store.OneAsync(Id, Ct));
    }

    [Fact]
    public async Task Exists_ThatRacedADelete_DoesNotKeepTheEntityReadable()
    {
        using var rig = Split(Shape.Inline);
        await ExistsRacesADeleteAsync(rig);
    }

    [Fact]
    public async Task Exists_OfAPlainType_ThatRacedADelete_DoesNotKeepTheEntityReadable()
    {
        using var rig = Plain();
        await ExistsRacesADeleteAsync(rig);
    }

    // ── the reader finishes in the middle of the writer's work ─────────────

    // The writer commits, then maintains the cache in several steps (evict or overwrite the entity entry,
    // evict the partition's list, evict the whole-table list). Whichever step the racing read finishes
    // after, it must not leave the older version behind: the write is noted before the first of them.
    [Theory]
    [MemberData(nameof(ShapesMutationsAndSteps))]
    public async Task PointRead_FinishingBetweenTwoStepsOfTheWritersCacheMaintenance_LeavesNothingOlder(
        Shape shape, Mutation mutation, int step)
    {
        using var rig = Split(shape);
        await rig.SeedAsync(Id);
        var paused = await rig.PauseAsync(() => rig.Store.OneAsync(Id, Ct));

        var steps = 0;
        rig.Cache.After = (_, _) =>
        {
            if (Interlocked.Increment(ref steps) == step)
                paused.ResumeAsync().GetAwaiter().GetResult();
        };
        await rig.MutateAsync(mutation);
        rig.Cache.After = null;
        await paused.ResumeAsync(); // a write with fewer steps than this: the read finishes after it

        await rig.AssertStoreAnswersWhatTheTableHoldsAsync(mutation);
    }

    [Theory]
    [MemberData(nameof(AllMutations))]
    public async Task PointRead_OfAPlainType_FinishingRightAfterTheWritersFirstCacheStep_LeavesNothingOlder(Mutation mutation)
    {
        using var rig = Plain();
        await rig.SeedAsync(Id);
        var paused = await rig.PauseAsync(() => rig.Store.OneAsync(Id, Ct));

        var steps = 0;
        rig.Cache.After = (_, _) =>
        {
            if (Interlocked.Increment(ref steps) == 1)
                paused.ResumeAsync().GetAwaiter().GetResult();
        };
        await rig.MutateAsync(mutation);
        rig.Cache.After = null;

        await rig.AssertStoreAnswersWhatTheTableHoldsAsync(mutation);
    }

    // The read finishes after the write committed and before the writer touched the cache: the read may
    // cache the old version for that instant (the write has not returned yet), and the writer's own cache
    // maintenance then removes or replaces it.
    [Theory]
    [MemberData(nameof(ShapesAndMutations))]
    public async Task PointRead_FinishingBetweenTheCommitAndTheWritersCacheMaintenance_LeavesNothingOlder(Shape shape, Mutation mutation)
    {
        using var rig = Split(shape);
        await rig.SeedAsync(Id);
        var paused = await rig.PauseAsync(() => rig.Store.OneAsync(Id, Ct));

        var commits = 0;
        rig.Table.Committed = _ =>
        {
            if (Interlocked.Increment(ref commits) == 1)
                paused.ResumeAsync().GetAwaiter().GetResult();
        };
        await rig.MutateAsync(mutation);

        var next = await rig.Store.OneAsync(Id, Ct);
        if (Deletes(mutation))
        {
            Assert.Null(next);
            Assert.False(await rig.Store.ExistsAsync(Id, Ct));
        }
        else
        {
            Assert.Equal(2, next!.Version);
            Assert.Equal(BodyOf(shape, mutation == Mutation.Merge ? 1 : 2), next.Body, StringComparer.Ordinal);
        }
    }

    // ── a cached query that raced a write or delete ────────────────────────

    public static TheoryData<Shape, Mutation, bool> ScanRaces()
    {
        var data = new TheoryData<Shape, Mutation, bool>();
        foreach (var shape in Shapes)
            foreach (var mutation in new[] { Mutation.Upsert, Mutation.Merge, Mutation.Delete })
                foreach (var wholeTable in new[] { false, true })
                    data.Add(shape, mutation, wholeTable);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScanRaces))]
    public async Task CachedQuery_ThatRacedAWriteOrDelete_IsNotServedFromTheCacheAfterwards(Shape shape, Mutation mutation, bool wholeTable)
    {
        using var rig = Split(shape);
        await rig.SeedAsync(Id);
        await rig.SeedAsync(Sibling);
        var scope = wholeTable ? null : Pk;

        // Held with the first entity's data in flight (the page itself, its continuation rows or its blob).
        var paused = await rig.PauseAsync(() => rig.Store.QueryAsync(scope, Ct));
        await rig.MutateAsync(mutation);
        await paused.ResumeAsync();

        // The entity first: a fresh query would warm the entity cache again and hide an older entry.
        await rig.AssertStoreAnswersWhatTheTableHoldsAsync(mutation);
        Assert.Equal(rig.StoredDocs(), Versions(await rig.Store.QueryAsync(scope, Ct)));
    }

    [Theory]
    [InlineData(Shape.Inline, false)]
    [InlineData(Shape.Inline, true)]
    [InlineData(Shape.ContinuationRows, false)]
    [InlineData(Shape.ContinuationRows, true)]
    [InlineData(Shape.Blob, false)]
    [InlineData(Shape.Blob, true)]
    public async Task CachedQuery_ThatRacedACreate_IncludesTheNewEntityAfterwards(Shape shape, bool wholeTable)
    {
        using var rig = Split(shape);
        await rig.SeedAsync(Id);
        var scope = wholeTable ? null : Pk;

        var paused = await rig.PauseAsync(() => rig.Store.QueryAsync(scope, Ct));
        await rig.Store.CreateAsync(rig.Doc(Sibling, 2), Ct);
        await paused.ResumeAsync();

        Assert.Equal([(Id, 1), (Sibling, 2)], Versions(await rig.Store.QueryAsync(scope, Ct)));
        Assert.Equal(2, (await rig.Store.OneAsync(Sibling, Ct))!.Version);
    }

    [Fact]
    public async Task CachedQuery_OfAPlainType_ThatRacedAWriteOrDelete_IsNotServedFromTheCacheAfterwards()
    {
        foreach (var mutation in new[] { Mutation.Upsert, Mutation.Replace, Mutation.Merge, Mutation.UpsertBatch, Mutation.Delete })
        {
            using var rig = Plain();
            await rig.SeedAsync(Id);
            await rig.SeedAsync(Sibling);

            var paused = await rig.PauseAsync(() => rig.Store.QueryAsync(Pk, Ct));
            await rig.MutateAsync(mutation);
            await paused.ResumeAsync();

            await rig.AssertStoreAnswersWhatTheTableHoldsAsync(mutation);
            Assert.Equal(rig.StoredDocs(), Versions(await rig.Store.QueryAsync(Pk, Ct)));
        }
    }

    public static TheoryData<Shape, Mutation, bool, int> ScanShapesMutationsAndSteps()
    {
        var data = new TheoryData<Shape, Mutation, bool, int>();
        foreach (var shape in Shapes)
            foreach (var mutation in Mutations)
                foreach (var wholeTable in new[] { false, true })
                    for (var step = 1; step <= 6; step++)
                        data.Add(shape, mutation, wholeTable, step);
        return data;
    }

    // The query finishes between two steps of the writer's cache maintenance: after the entity's entry
    // went and before the lists did, between the two lists, or (a batch evicts the lists first) after the
    // lists went and before the entity's entry did.
    [Theory]
    [MemberData(nameof(ScanShapesMutationsAndSteps))]
    public async Task CachedQuery_FinishingBetweenTwoStepsOfTheWritersCacheMaintenance_IsNotLeftStale(
        Shape shape, Mutation mutation, bool wholeTable, int step)
    {
        using var rig = Split(shape);
        await rig.SeedAsync(Id);
        await rig.SeedAsync(Sibling);
        var scope = wholeTable ? null : Pk;
        var paused = await rig.PauseAsync(() => rig.Store.QueryAsync(scope, Ct));

        var steps = 0;
        rig.Cache.After = (_, _) =>
        {
            if (Interlocked.Increment(ref steps) == step)
                paused.ResumeAsync().GetAwaiter().GetResult();
        };
        await rig.MutateAsync(mutation);
        rig.Cache.After = null;
        await paused.ResumeAsync(); // a write with fewer steps than this: the query finishes after it

        await rig.AssertStoreAnswersWhatTheTableHoldsAsync(mutation);
        Assert.Equal(rig.StoredDocs(), Versions(await rig.Store.QueryAsync(scope, Ct)));
    }

    // ── queries that warm the entity cache ─────────────────────────────────

    public static TheoryData<Shape, string, bool, Mutation> WarmingRaces()
    {
        var data = new TheoryData<Shape, string, bool, Mutation>();
        foreach (var shape in Shapes)
            foreach (var read in new[] { "page", "predicate" })
                foreach (var wholeTable in new[] { false, true })
                    foreach (var mutation in new[] { Mutation.Upsert, Mutation.Delete })
                        data.Add(shape, read, wholeTable, mutation);
        return data;
    }

    private static Task WarmAsync<T>(Rig<T> rig, string read, bool wholeTable) where T : class, IEntity, IRaceDoc, new()
    {
        var scope = wholeTable ? null : Pk;
        return read == "page"
            ? rig.Store.QueryPageAsync(new QueryOptions { Partition = scope }, Ct)
            : rig.Store.QueryAsync(d => d.Version >= 1, scope, take: null, Ct);
    }

    [Theory]
    [MemberData(nameof(WarmingRaces))]
    public async Task PageOrPredicateQuery_ThatRacedAWriteOrDelete_DoesNotWarmTheCacheWithTheOlderVersion(
        Shape shape, string read, bool wholeTable, Mutation mutation)
    {
        using var rig = Split(shape);
        await rig.SeedAsync(Id);
        await rig.SeedAsync(Sibling);

        var paused = await rig.PauseAsync(async () =>
        {
            await WarmAsync(rig, read, wholeTable);
            return true;
        });
        await rig.MutateAsync(mutation);
        await paused.ResumeAsync();

        await rig.AssertStoreAnswersWhatTheTableHoldsAsync(mutation);
    }

    [Theory]
    [InlineData("page")]
    [InlineData("predicate")]
    public async Task PageOrPredicateQuery_OfAPlainType_ThatRacedAWrite_DoesNotWarmTheCacheWithTheOlderVersion(string read)
    {
        using var rig = Plain();
        await rig.SeedAsync(Id);

        var paused = await rig.PauseAsync(async () =>
        {
            await WarmAsync(rig, read, wholeTable: false);
            return true;
        });
        await rig.MutateAsync(Mutation.Upsert);
        await paused.ResumeAsync();

        await rig.AssertStoreAnswersWhatTheTableHoldsAsync(Mutation.Upsert);
    }

    // ── a write completes while the read is copying its entity for the cache ─

    // The read looked at the generation, found it unchanged and started copying the entity for the cache
    // (a serializer round trip, which reads the body). A write that completes right then must still win:
    // looking only before the put would leave this window open.
    [Theory]
    [InlineData("one")]
    [InlineData("exists")]
    [InlineData("query")]
    [InlineData("table")]
    [InlineData("page")]
    [InlineData("predicate")]
    public async Task Read_WhenAWriteCompletesWhileTheReadCopiesItsEntityForTheCache_LeavesNothingOlder(string read)
    {
        using var rig = new Rig<RaceProbeDoc>(Shape.Inline, b => b.SetProperty(x => x.Version, 2));
        var probe = new RaceProbe();
        RaceProbeDoc.Probe.Value = probe;
        await rig.SeedAsync(Id);

        var paused = await rig.PauseAsync(async () =>
        {
            switch (read)
            {
                case "one":
                    await rig.Store.OneAsync(Id, Ct);
                    break;
                case "exists":
                    await rig.Store.ExistsAsync(Id, Ct);
                    break;
                case "query":
                    await rig.Store.QueryAsync(Pk, Ct);
                    break;
                case "table":
                    await rig.Store.QueryAsync(ct: Ct);
                    break;
                default:
                    await WarmAsync(rig, read, wholeTable: false);
                    break;
            }
            return true;
        });
        var written = false;
        probe.Arm(() =>
        {
            // On the reader's thread, inside its copy of the entity: the write runs to completion here.
            rig.Store.UpsertAsync(rig.Doc(Id, 2), Ct).GetAwaiter().GetResult();
            written = true;
        });
        await paused.ResumeAsync();

        Assert.True(written, "The read did not copy the entity for the cache.");
        await rig.AssertStoreAnswersWhatTheTableHoldsAsync(Mutation.Upsert);
        var scope = read == "table" ? null : Pk;
        Assert.Equal([(Id, 2)], Versions(await rig.Store.QueryAsync(scope, Ct)));
    }

    // ── a stream consumed slowly ─────────────────────────────────────────────

    // The page that holds both entities was fetched when the stream began; the caller takes the first
    // entity, a write to the second one completes, and only then does the caller take the second.
    private static async Task StreamConsumedSlowlyAsync<T>(Rig<T> rig, Mutation mutation) where T : class, IEntity, IRaceDoc, new()
    {
        await rig.SeedAsync(Sibling);
        await rig.SeedAsync(Id);

        await using var stream = rig.Store.QueryStreamAsync(d => d.Version >= 1, Pk, take: null, Ct).GetAsyncEnumerator(Ct);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(Id, stream.Current.Id);
        await rig.MutateAsync(mutation, Sibling);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(1, stream.Current.Version); // what the page held
        Assert.False(await stream.MoveNextAsync());

        await rig.AssertStoreAnswersWhatTheTableHoldsAsync(mutation, Sibling);
        // The entity that was not written is cached as before.
        var pointReads = rig.Table.Count(r => r.IsWholeRowPointRead);
        Assert.Equal(1, (await rig.Store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(pointReads, rig.Table.Count(r => r.IsWholeRowPointRead));
    }

    [Theory]
    [InlineData(Mutation.Upsert)]
    [InlineData(Mutation.Merge)]
    [InlineData(Mutation.Delete)]
    public async Task PredicateStream_ConsumedSlowly_DoesNotCacheAnEntityWrittenMeanwhile(Mutation mutation)
    {
        using var rig = Split(Shape.Inline);
        await StreamConsumedSlowlyAsync(rig, mutation);
    }

    [Theory]
    [InlineData(Mutation.Upsert)]
    [InlineData(Mutation.Merge)]
    [InlineData(Mutation.Delete)]
    public async Task PredicateStream_OfAPlainType_ConsumedSlowly_DoesNotCacheAnEntityWrittenMeanwhile(Mutation mutation)
    {
        using var rig = Plain();
        await StreamConsumedSlowlyAsync(rig, mutation);
    }

    // ── two stores over one cache ──────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ShapesAndMutations))]
    public async Task PointRead_ThatRacedAWriteThroughAnotherStoreOverTheSameCache_LeavesNothingOlder(Shape shape, Mutation mutation)
    {
        using var rig = Split(shape);
        var writer = rig.NewStore(rig.Cache);
        await rig.SeedAsync(Id);

        var paused = await rig.PauseAsync(() => rig.Store.OneAsync(Id, Ct));
        await rig.MutateAsync(writer, mutation);
        await paused.ResumeAsync();

        await rig.AssertStoreAnswersWhatTheTableHoldsAsync(mutation);
    }

    // ── the cache still works ──────────────────────────────────────────────

    [Theory]
    [InlineData(Shape.Inline)]
    [InlineData(Shape.ContinuationRows)]
    [InlineData(Shape.Blob)]
    public async Task NoRace_ASecondPointReadIsServedFromTheCache(Shape shape)
    {
        using var rig = Split(shape);
        await rig.SeedAsync(Id);

        var first = await rig.Store.OneAsync(Id, Ct);
        var second = await rig.Store.OneAsync(Id, Ct);

        Assert.Equal(1, rig.Table.Count(r => r.IsWholeRowPointRead));
        Assert.Equal(first!.Body, second!.Body, StringComparer.Ordinal);
        Assert.Equal(rig.StoredETag(Id), second.ETag);
        Assert.True(await rig.Store.ExistsAsync(Id, Ct));
        Assert.Equal(1, rig.Table.Count(r => r.IsWholeRowPointRead));
    }

    [Fact]
    public async Task NoRace_PlainType_PointReadExistsAndQueriesAreServedFromTheCache()
    {
        using var rig = Plain();
        await rig.SeedAsync(Id);
        await rig.SeedAsync(Sibling);

        Assert.True(await rig.Store.ExistsAsync(Id, Ct));          // fetches and caches the row
        Assert.Equal(1, (await rig.Store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(1, rig.Table.Count(r => r.IsWholeRowPointRead));

        Assert.Equal(2, (await rig.Store.QueryAsync(Pk, Ct)).Count());
        Assert.Equal(2, (await rig.Store.QueryAsync(Pk, Ct)).Count());
        Assert.Equal(2, (await rig.Store.QueryAsync(ct: Ct)).Count());
        Assert.Equal(2, (await rig.Store.QueryAsync(ct: Ct)).Count());
        Assert.Equal(2, rig.Table.Count(r => r.IsEntityQuery));    // one per scope

        // The partition query warmed the sibling too.
        Assert.Equal(1, (await rig.Store.OneAsync(Sibling, Ct))!.Version);
        Assert.Equal(1, rig.Table.Count(r => r.IsWholeRowPointRead));
    }

    [Theory]
    [InlineData(Shape.Inline, false)]
    [InlineData(Shape.Inline, true)]
    [InlineData(Shape.ContinuationRows, false)]
    [InlineData(Shape.Blob, true)]
    public async Task NoRace_ASecondQueryIsServedFromTheCache_AndItWarmedTheEntities(Shape shape, bool wholeTable)
    {
        using var rig = Split(shape);
        await rig.SeedAsync(Id);
        await rig.SeedAsync(Sibling);
        var scope = wholeTable ? null : Pk;

        Assert.Equal([(Id, 1), (Sibling, 1)], Versions(await rig.Store.QueryAsync(scope, Ct)));
        Assert.Equal([(Id, 1), (Sibling, 1)], Versions(await rig.Store.QueryAsync(scope, Ct)));
        Assert.Equal(1, rig.Table.Count(r => r.IsEntityQuery));

        Assert.Equal(1, (await rig.Store.OneAsync(Sibling, Ct))!.Version);
        Assert.Equal(0, rig.Table.Count(r => r.IsWholeRowPointRead));
    }

    [Theory]
    [InlineData(Shape.Inline, "page", false)]
    [InlineData(Shape.Inline, "predicate", true)]
    [InlineData(Shape.ContinuationRows, "page", true)]
    [InlineData(Shape.ContinuationRows, "predicate", false)]
    [InlineData(Shape.Blob, "page", false)]
    [InlineData(Shape.Blob, "predicate", true)]
    public async Task NoRace_APageOrPredicateQueryWarmsTheEntityCache(Shape shape, string read, bool wholeTable)
    {
        using var rig = Split(shape);
        await rig.SeedAsync(Id);
        await rig.SeedAsync(Sibling);

        await WarmAsync(rig, read, wholeTable);

        Assert.Equal(1, (await rig.Store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(1, (await rig.Store.OneAsync(Sibling, Ct))!.Version);
        Assert.Equal(0, rig.Table.Count(r => r.IsWholeRowPointRead));
    }

    [Theory]
    [InlineData(Shape.Inline)]
    [InlineData(Shape.ContinuationRows)]
    [InlineData(Shape.Blob)]
    public async Task ReadThatStartsAfterAWriteReturned_IsCachedAsBefore(Shape shape)
    {
        using var rig = Split(shape);
        await rig.SeedAsync(Id);
        await rig.Store.OneAsync(Id, Ct);
        await rig.MutateAsync(Mutation.UpsertBatch); // a batch evicts the entry

        Assert.Equal(2, (await rig.Store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(2, (await rig.Store.OneAsync(Id, Ct))!.Version);

        // One read before the write, one after it; the last one came from the cache.
        Assert.Equal(2, rig.Table.Count(r => r.IsWholeRowPointRead));
    }

    // ── only the entity that was written is affected ───────────────────────

    // "p|a" is read while "p|b" (same partition) or "q|a" (another one) is written: what the read
    // found is still the stored version, and it is cached as before. (Entities share a fixed number of
    // write counters; these ids do not share one.)
    [Theory]
    [InlineData(Shape.Inline, "p|b")]
    [InlineData(Shape.Inline, "q|a")]
    [InlineData(Shape.ContinuationRows, "p|b")]
    [InlineData(Shape.Blob, "q|a")]
    public async Task PointRead_ThatRacedAWriteToAnotherEntity_IsStillCached(Shape shape, string written)
    {
        using var rig = Split(shape);
        await rig.SeedAsync(Id);
        await rig.SeedAsync(written);

        var paused = await rig.PauseAsync(() => rig.Store.OneAsync(Id, Ct));
        await rig.MutateAsync(Mutation.Upsert, written);
        Assert.Equal(1, (await paused.ResumeAsync())!.Version);

        Assert.Equal(1, (await rig.Store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(1, rig.Table.Count(r => r.IsWholeRowPointRead));
    }

    [Fact]
    public async Task PointRead_OfAPlainType_ThatRacedAWriteToAnotherEntity_IsStillCached()
    {
        using var rig = Plain();
        await rig.SeedAsync(Id);
        await rig.SeedAsync(Sibling);

        var paused = await rig.PauseAsync(() => rig.Store.OneAsync(Id, Ct));
        await rig.MutateAsync(Mutation.Upsert, Sibling);
        await rig.MutateAsync(Mutation.Delete, Sibling);
        await paused.ResumeAsync();

        Assert.Equal(1, (await rig.Store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(1, rig.Table.Count(r => r.IsWholeRowPointRead));
    }

    [Fact]
    public async Task PartitionQuery_ThatRacedAWriteToAnotherPartition_IsStillCached()
    {
        using var rig = Split(Shape.Inline);
        await rig.SeedAsync(Id);
        await rig.SeedAsync("q|a");

        var paused = await rig.PauseAsync(() => rig.Store.QueryAsync(Pk, Ct));
        await rig.MutateAsync(Mutation.Upsert, "q|a");
        await paused.ResumeAsync();

        Assert.Equal([(Id, 1)], Versions(await rig.Store.QueryAsync(Pk, Ct)));
        Assert.Equal(1, rig.Table.Count(r => r.IsEntityQuery));
        Assert.Equal(1, (await rig.Store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(0, rig.Table.Count(r => r.IsWholeRowPointRead));
    }

    [Fact]
    public async Task WholeTableQuery_ThatRacedAWrite_KeepsTheEntitiesItReadThatWereNotWritten()
    {
        using var rig = Split(Shape.Inline);
        await rig.SeedAsync(Id);
        await rig.SeedAsync("q|a");

        var paused = await rig.PauseAsync(() => rig.Store.QueryAsync(ct: Ct));
        await rig.MutateAsync(Mutation.Upsert, "q|a");
        await paused.ResumeAsync();

        // The list is read again (it held the old "q|a"); "p|a" was warmed; "q|a" is the written version.
        Assert.Equal([(Id, 1), ("q|a", 2)], Versions(await rig.Store.QueryAsync(ct: Ct)));
        Assert.Equal(2, rig.Table.Count(r => r.IsEntityQuery));
        Assert.Equal(1, (await rig.Store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(2, (await rig.Store.OneAsync("q|a", Ct))!.Version);
        Assert.Equal(0, rig.Table.Count(r => r.IsWholeRowPointRead));
    }

    // ── random reads around one write per entity ───────────────────────────

    // Whatever the interleaving, once everything has returned the store must answer what the table holds.
    // Each round starts on a cold cache (so its reads go to the table) and runs random reads beside one
    // write or delete per entity. One writer per entity: two writers of one entity race each other's
    // cache maintenance, which is not what this fix is about.
    [Theory]
    [InlineData(Shape.Inline)]
    [InlineData(Shape.Blob)]
    public async Task RandomReadsAroundOneWritePerEntity_OnceTheyReturn_TheStoreAnswersWhatTheTableHolds(Shape shape)
    {
        using var rig = Split(shape);
        // Every response takes a moment to arrive, a different one each time.
        rig.Table.ResponseInFlight = async _ =>
        {
            for (var hops = Random.Shared.Next(4); hops > 0; hops--)
                await Task.Yield();
        };
        string[] ids = ["p|a", "p|b", "q|a", "q|b"];
        var versions = new int[ids.Length];
        var exists = new bool[ids.Length];
        var random = new Random(20261111);

        for (var round = 0; round < 60; round++)
        {
            rig.Cache.Clear();
            var merged = new bool[ids.Length];
            var work = new List<Task>();
            for (var k = 0; k < ids.Length; k++)
            {
                var id = ids[k];
                var choice = exists[k] ? random.Next(5) : random.Next(2);
                var version = choice == 4 ? versions[k] : ++versions[k];
                exists[k] = choice != 4;
                merged[k] = choice == 3;
                work.Add(Task.Run(() => choice switch
                {
                    0 => rig.Store.UpsertAsync(rig.Doc(id, version), Ct),
                    1 => rig.Store.UpsertBatchAsync([rig.Doc(id, version)], Ct),
                    2 => rig.Store.UpdateAsync(rig.Doc(id, version), ConcurrencyMode.LastWriterWins, Ct),
                    3 => rig.Store.UpdateAsync(id, b => b.SetProperty(x => x.Version, version), Ct),
                    _ => rig.Store.DeleteAsync(id, Ct),
                }, Ct));
            }
            for (var reader = 0; reader < 8; reader++)
            {
                var id = ids[random.Next(ids.Length)];
                var partition = EntityId.Split(id).PartitionKey;
                var scope = random.Next(2) == 0 ? partition : null;
                var choice = random.Next(7);
                work.Add(Task.Run(() => choice switch
                {
                    0 or 1 => rig.Store.OneAsync(id, Ct),
                    2 => rig.Store.ExistsAsync(id, Ct),
                    3 => rig.Store.QueryAsync(partition, Ct),
                    4 => rig.Store.QueryAsync(ct: Ct),
                    5 => rig.Store.QueryPageAsync(new QueryOptions { Partition = scope }, Ct),
                    _ => (Task)rig.Store.QueryAsync(d => d.Version >= 0, scope, take: null, Ct),
                }, Ct));
            }
            await Task.WhenAll(work).WaitAsync(Patience, Ct);

            var stored = rig.StoredDocs();
            for (var k = 0; k < ids.Length; k++)
            {
                int? expected = exists[k] ? versions[k] : null;
                Assert.Equal(expected, stored.Where(d => d.Id == ids[k]).Select(d => (int?)d.Version).SingleOrDefault());
                var read = await rig.Store.OneAsync(ids[k], Ct);
                Assert.Equal(expected, read?.Version);
                Assert.Equal(exists[k], await rig.Store.ExistsAsync(ids[k], Ct));
                // (A builder merge leaves a cached entity's ETag behind: a known defect outside this fix.)
                if (read is not null && !merged[k])
                    Assert.Equal(rig.StoredETag(ids[k]), read.ETag);
            }
            Assert.Equal(rig.StoredDocs("p"), Versions(await rig.Store.QueryAsync("p", Ct)));
            Assert.Equal(rig.StoredDocs("q"), Versions(await rig.Store.QueryAsync("q", Ct)));
            Assert.Equal(stored, Versions(await rig.Store.QueryAsync(ct: Ct)));
        }
    }

    // ── parity: the in-memory store has no read cache to go stale ──────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InMemory_ReadThatRacedAWriteOrDelete_TheNextReadSeesTheWriteOrDelete(bool delete)
    {
        var store = new InMemoryStorage<RaceDoc>(options: new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 },
        });
        await store.CreateAsync(new RaceDoc { Id = Id, Body = BodyOf(Shape.Blob, 1), Version = 1 }, Ct);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 1;
        store.Blobs.BeforeOpenRead = async (_, _) =>
        {
            if (Interlocked.Exchange(ref armed, 0) != 1)
                return;
            reached.TrySetResult();
            await release.Task;
        };

        var reading = store.OneAsync(Id, Ct);
        await reached.Task.WaitAsync(Patience, Ct);
        if (delete)
            await store.DeleteAsync(Id, Ct);
        else
            await store.UpsertAsync(new RaceDoc { Id = Id, Body = BodyOf(Shape.Blob, 2), Version = 2 }, Ct);
        release.TrySetResult();
        Assert.Equal(1, (await reading.WaitAsync(Patience, Ct))!.Version);

        int? expected = delete ? null : 2;
        (string, int)[] expectedList = delete ? [] : [(Id, 2)];
        Assert.Equal(expected, (await store.OneAsync(Id, Ct))?.Version);
        Assert.Equal(!delete, await store.ExistsAsync(Id, Ct));
        Assert.Equal(expectedList, Versions(await store.QueryAsync(Pk, Ct)));
    }
}
