using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A write puts its entity into the cache when its call returns, some time after its row committed (its
/// response travels back; a large-value write first deletes the blobs of the version it replaced). A write
/// or delete of the same entity that commits and returns in between must not be overwritten in the cache
/// by the earlier write: once both calls have returned, the store answers what the table holds. The rule
/// that delivers it: a write's put gives way (it evicts) when another write or delete of the entity was
/// noted since the write began. (CACHE-3, CACHE-9; ruling R2: every entity type, plain types included.)
/// </summary>
public class OverlappingWriteCacheTests
{
    private const string Id = "p|a";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableStorage<T> NewStore<T>(
        RacingTableClient table, IMemoryCache cache, IOffloadBlobStore? blobs = null, UnifiedTableStorageOptions? options = null)
        where T : class, IEntity, new()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(T).Name).Returns(table);
        return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, options, blobs);
    }

    private static RaceDoc BlobDoc(int version) => new() { Id = Id, Body = Ascii(40_000, seed: version), Version = version };

    private static int? StoredVersion(RacingTableClient table) => table.Stored("p", "a") is { } row ? (int)row["Version"] : null;

    private static UnifiedTableStorageOptions OffloadOptions() =>
        new() { BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 } };

    /// <summary>A blob store whose next delete (the post-commit cleanup of a write) is held until the test releases it.</summary>
    private sealed class HoldingBlobStore(InMemoryOffloadBlobStore inner) : IOffloadBlobStore
    {
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;

        /// <summary>Completes when a delete is being held.</summary>
        public Task Reached => reached.Task;

        public void HoldTheNextDelete() => Volatile.Write(ref armed, 1);

        public void Release() => release.TrySetResult();

        public Task<BlobState?> GetStateAsync(string name, CancellationToken ct) => inner.GetStateAsync(name, ct);

        public Task UploadCreateOnlyAsync(string name, Stream content, BlobReference reference, string? contentEncoding, CancellationToken ct) =>
            inner.UploadCreateOnlyAsync(name, content, reference, contentEncoding, ct);

        public Task TouchAsync(string name, CancellationToken ct) => inner.TouchAsync(name, ct);

        public Task<Stream> OpenReadAsync(string column, string name, CancellationToken ct) => inner.OpenReadAsync(column, name, ct);

        public async Task<bool> DeleteIfUnmodifiedAsync(string name, DateTimeOffset? unmodifiedSince, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                reached.TrySetResult();
                await release.Task;
            }
            return await inner.DeleteIfUnmodifiedAsync(name, unmodifiedSince, ct);
        }

        public Task<DateTimeOffset?> GetServiceTimeAsync(CancellationToken ct) => inner.GetServiceTimeAsync(ct);

        public IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListAsync(string prefix, CancellationToken ct) =>
            inner.ListAsync(prefix, ct);
    }

    // ── a large-value write whose blob cleanup ends after a later write or delete returned (CACHE-3) ─────

    // Writer A replaces the entity (the row commits) and then deletes the blob of the version it replaced.
    // Writer B starts after A's commit, writes a newer version (or deletes the entity) and returns. When
    // A's cleanup ends and A returns, the store answers what the table holds: B's version, or nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeValueWrite_ThatFinishesItsBlobCleanupLate_DoesNotOverwriteTheCacheEntryOfALaterWriteOrDelete(bool laterIsADelete)
    {
        var table = new RacingTableClient();
        var blobs = new HoldingBlobStore(new InMemoryOffloadBlobStore());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<RaceDoc>(table, cache, blobs, OffloadOptions());
        await store.UpsertAsync(BlobDoc(1), Ct);

        blobs.HoldTheNextDelete();
        var writerA = store.UpsertAsync(BlobDoc(2), Ct);
        await blobs.Reached.WaitAsync(Patience, Ct);
        Assert.Equal(2, StoredVersion(table)); // A's row is committed; A is cleaning up

        if (laterIsADelete)
            await store.DeleteAsync(Id, Ct);
        else
            await store.UpsertAsync(BlobDoc(3), Ct);
        // B has returned: the store answers what B wrote.
        Assert.Equal(laterIsADelete ? null : 3, (await store.OneAsync(Id, Ct))?.Version);

        blobs.Release();
        await writerA.WaitAsync(Patience, Ct);

        // Both calls have returned. The store answers what the table holds.
        int? expected = laterIsADelete ? null : 3;
        Assert.Equal(expected, StoredVersion(table));
        Assert.Equal(expected, (await store.OneAsync(Id, Ct))?.Version);
        Assert.Equal(!laterIsADelete, await store.ExistsAsync(Id, Ct));
    }

    // The same through the library's own optimistic read-modify-write (MutateAsync writes with
    // ConcurrencyMode.Strict): round-tripping ETags does not protect a caller from the earlier write's put.
    [Fact]
    public async Task LargeValueWrite_ThatFinishesItsBlobCleanupLate_DoesNotOverwriteWhatALaterMutateAsyncWrote()
    {
        var table = new RacingTableClient();
        var blobs = new HoldingBlobStore(new InMemoryOffloadBlobStore());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<RaceDoc>(table, cache, blobs, OffloadOptions());
        await store.UpsertAsync(BlobDoc(1), Ct);

        blobs.HoldTheNextDelete();
        var writerA = store.UpsertAsync(BlobDoc(2), Ct);
        await blobs.Reached.WaitAsync(Patience, Ct);
        Assert.Equal(2, StoredVersion(table)); // A's row is committed; A is cleaning up

        var mutated = await store.MutateAsync(Id, doc =>
        {
            doc.Version = 3;
            doc.Body = Ascii(40_000, seed: 3);
        }, ct: Ct);
        Assert.Equal(3, mutated.Version);
        Assert.Equal(3, (await store.OneAsync(Id, Ct))!.Version);

        blobs.Release();
        await writerA.WaitAsync(Patience, Ct);

        Assert.Equal(3, StoredVersion(table));
        Assert.Equal(3, (await store.OneAsync(Id, Ct))!.Version);
    }

    // Writes that do not overlap behave as before: the later one is cached, and served without a read.
    [Fact]
    public async Task TwoLargeValueWritesInSequence_LeaveTheLaterOneCached()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<RaceDoc>(table, cache, new InMemoryOffloadBlobStore(), OffloadOptions());
        await store.UpsertAsync(BlobDoc(1), Ct);

        await store.UpsertAsync(BlobDoc(2), Ct);
        await store.UpsertAsync(BlobDoc(3), Ct);

        Assert.Equal(3, StoredVersion(table));
        Assert.Equal(3, (await store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(0, table.Count(r => r.IsWholeRowPointRead)); // served from the cache
    }

    [Fact]
    public async Task TwoPlainWritesInSequence_LeaveTheLaterOneCached()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<RacePlainDoc>(table, cache);
        await store.CreateAsync(new RacePlainDoc { Id = Id, Version = 1 }, Ct);
        await store.UpdateAsync(new RacePlainDoc { Id = Id, Version = 2 }, ConcurrencyMode.LastWriterWins, Ct);
        await store.UpsertAsync(new RacePlainDoc { Id = Id, Version = 3 }, Ct);
        await store.UpdateAsync(Id, b => b.SetProperty(x => x.Version, 4), Ct);

        Assert.Equal(4, StoredVersion(table));
        Assert.Equal(4, (await store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(0, table.Count(r => r.IsWholeRowPointRead)); // served from the cache
    }

    // ── a plain write whose response arrives after a later write or delete returned (CACHE-9) ───────────

    // Writer A's upsert commits; while its response is still in flight (so before A touches the cache),
    // writer B's upsert or delete commits and returns. Once A has returned, the store answers what the
    // table holds. (The same scenario leaves A's older version cached on the published 0.8.3.)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainWrite_WhoseResponseArrivesAfterALaterWriteOrDeleteReturned_DoesNotLeaveItsOlderVersionCached(bool laterIsADelete)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<RacePlainDoc>(table, cache);
        await store.UpsertAsync(new RacePlainDoc { Id = Id, Version = 1 }, Ct);

        var commits = 0;
        table.Committed = _ =>
        {
            if (Interlocked.Increment(ref commits) != 1)
                return;
            // A has committed version 2 and has not received its response. B runs to completion.
            if (laterIsADelete)
                store.DeleteAsync(Id, Ct).GetAwaiter().GetResult();
            else
                store.UpsertAsync(new RacePlainDoc { Id = Id, Version = 3 }, Ct).GetAwaiter().GetResult();
        };
        await store.UpsertAsync(new RacePlainDoc { Id = Id, Version = 2 }, Ct);
        table.Committed = null;

        int? expected = laterIsADelete ? null : 3;
        Assert.Equal(expected, StoredVersion(table));
        Assert.Equal(expected, (await store.OneAsync(Id, Ct))?.Version);
        Assert.Equal(!laterIsADelete, await store.ExistsAsync(Id, Ct));
    }

    // ── every write kind against a later upsert or delete, in both orders of their returns ──────────────

    /// <summary>The earlier write, A.</summary>
    public enum FirstWrite
    {
        Create,
        Update,
        Upsert,
        Builder,
    }

    public static TheoryData<FirstWrite, bool, bool, bool> Overlaps()
    {
        var data = new TheoryData<FirstWrite, bool, bool, bool>();
        foreach (var first in Enum.GetValues<FirstWrite>())
            foreach (var laterIsADelete in new[] { false, true })
                foreach (var laterReturnsFirst in new[] { true, false })
                    foreach (var largeValues in new[] { false, true })
                        data.Add(first, laterIsADelete, laterReturnsFirst, largeValues);
        return data;
    }

    // A commits first. B (an upsert or a delete of the same entity) starts after A's commit and before A
    // has returned; either B returns first (A's response, or the rest of A's call, is still on its way),
    // or A returns first and B returns after it. Once both have returned, the store answers what the
    // table holds, and ExistsAsync agrees. Through a type that opts into nothing and through a
    // large-value type whose value fits one cell (one SDK call, cached like a plain write).
    [Theory]
    [MemberData(nameof(Overlaps))]
    public async Task OverlappingWrites_OfOneEntity_LeaveTheStoreAnsweringWhatTheTableHolds(
        FirstWrite first, bool laterIsADelete, bool laterReturnsFirst, bool largeValues)
    {
        if (largeValues)
            await RunOverlapAsync<RaceDoc>(first, laterIsADelete, laterReturnsFirst);
        else
            await RunOverlapAsync<RacePlainDoc>(first, laterIsADelete, laterReturnsFirst);
    }

    private static T Doc<T>(int version) where T : Entity, IRaceDoc, new() =>
        new() { Id = Id, Body = "v" + version, Version = version };

    private static async Task RunOverlapAsync<T>(FirstWrite first, bool laterIsADelete, bool laterReturnsFirst)
        where T : Entity, IRaceDoc, new()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<T>(table, cache);
        if (first != FirstWrite.Create)
        {
            await store.UpsertAsync(Doc<T>(1), Ct);
            Assert.Equal(1, (await store.OneAsync(Id, Ct))!.Version); // cached: the builder update patches it
        }

        var laterCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? later = null;
        Task Later() => laterIsADelete ? store.DeleteAsync(Id, Ct) : store.UpsertAsync(Doc<T>(3), Ct);

        var commits = 0;
        table.Committed = _ =>
        {
            var commit = Interlocked.Increment(ref commits);
            if (commit == 1)
            {
                // A has committed and has not returned.
                if (laterReturnsFirst)
                {
                    Later().GetAwaiter().GetResult(); // B runs to completion
                }
                else
                {
                    later = Task.Run(Later);
                    Assert.True(laterCommitted.Task.Wait(Patience), "B did not commit.");
                }
            }
            else if (commit == 2 && !laterReturnsFirst)
            {
                // B has committed; it returns only once A has returned.
                laterCommitted.TrySetResult();
                Assert.True(firstReturned.Task.Wait(Patience), "A did not return.");
            }
        };

        switch (first)
        {
            case FirstWrite.Create:
                await store.CreateAsync(Doc<T>(2), Ct);
                break;
            case FirstWrite.Update:
                await store.UpdateAsync(Doc<T>(2), ConcurrencyMode.LastWriterWins, Ct);
                break;
            case FirstWrite.Upsert:
                await store.UpsertAsync(Doc<T>(2), Ct);
                break;
            case FirstWrite.Builder:
                await store.UpdateAsync(Id, b => b.SetProperty(x => x.Version, 2), Ct);
                break;
        }
        firstReturned.TrySetResult();
        if (later is not null)
            await later.WaitAsync(Patience, Ct);
        table.Committed = null;
        Assert.Equal(2, commits);

        // Both calls have returned.
        var stored = StoredVersion(table);
        Assert.Equal(laterIsADelete ? null : 3, stored);
        Assert.Equal(stored, (await store.OneAsync(Id, Ct))?.Version);
        Assert.Equal(stored is not null, await store.ExistsAsync(Id, Ct));
    }

    // Two stores over one IMemoryCache share its entries, and so the rule: B through the second store.
    [Fact]
    public async Task OverlappingWrites_ThroughTwoStoresOverOneCache_LeaveTheStoreAnsweringWhatTheTableHolds()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var a = NewStore<RacePlainDoc>(table, cache);
        var b = NewStore<RacePlainDoc>(table, cache);
        await a.UpsertAsync(new RacePlainDoc { Id = Id, Version = 1 }, Ct);

        var commits = 0;
        table.Committed = _ =>
        {
            if (Interlocked.Increment(ref commits) == 1)
                b.UpsertAsync(new RacePlainDoc { Id = Id, Version = 3 }, Ct).GetAwaiter().GetResult();
        };
        await a.UpsertAsync(new RacePlainDoc { Id = Id, Version = 2 }, Ct);
        table.Committed = null;

        Assert.Equal(3, StoredVersion(table));
        Assert.Equal(3, (await a.OneAsync(Id, Ct))!.Version);
        Assert.Equal(3, (await b.OneAsync(Id, Ct))!.Version);
    }

    // A write to ANOTHER entity in between does not make a write give up its put.
    [Fact]
    public async Task AWriteOfAnotherEntityInBetween_LeavesTheWriteCached()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<RacePlainDoc>(table, cache);
        await store.UpsertAsync(new RacePlainDoc { Id = Id, Version = 1 }, Ct);

        var commits = 0;
        table.Committed = _ =>
        {
            if (Interlocked.Increment(ref commits) == 1)
                store.UpsertAsync(new RacePlainDoc { Id = "q|z", Version = 9 }, Ct).GetAwaiter().GetResult();
        };
        await store.UpsertAsync(new RacePlainDoc { Id = Id, Version = 2 }, Ct);
        table.Committed = null;

        var reads = table.Count(r => r.IsWholeRowPointRead);
        Assert.Equal(2, (await store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(reads, table.Count(r => r.IsWholeRowPointRead)); // served from the cache
    }
}
