using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// When the storage call of a write or delete throws, the write may or may not have committed (a timeout,
/// a cancellation, a connection dropped after the commit). The store cannot tell, so it evicts the
/// entity's entry and its partition's cached lists before the exception leaves, and the next read goes to
/// the table. The exception is the one the call threw. A failure before any storage call evicts nothing.
/// (LEAD-3 of the blobs verdicts, cache half; ruling R2: every entity type, plain types included.)
/// </summary>
public class FailedWriteCacheTests
{
    private const string Id = "p|a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableStorage<T> NewStore<T>(RacingTableClient table, IMemoryCache cache) where T : class, IEntity, new()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(T).Name).Returns(table);
        return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, null, (Unified.Data.Tables.Providers.IOffloadBlobStore?)null);
    }

    private static int? StoredVersion(RacingTableClient table) => table.Stored("p", "a") is { } row ? (int)row["Version"] : null;

    private static T Doc<T>(int version) where T : Entity, IRaceDoc, new() =>
        new() { Id = Id, Body = "v" + version, Version = version };

    /// <summary>The write whose storage call commits and then throws.</summary>
    public enum FailedWrite
    {
        Create,
        Update,
        Upsert,
        Builder,
        Delete,
    }

    public static TheoryData<FailedWrite, bool> Writes()
    {
        var data = new TheoryData<FailedWrite, bool>();
        foreach (var write in Enum.GetValues<FailedWrite>())
            foreach (var largeValues in new[] { false, true })
                data.Add(write, largeValues);
        return data;
    }

    // The transport commits the write and then throws, as a cancelled caller's token does once the request
    // has been sent. Before: the entity and its partition's list are cached at version 1. After: the
    // exception is the transport's own, and the next point read and the next partition query go to the
    // table and answer what it holds.
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AWrite_ThatCommittedButThrew_LeavesNoCacheEntry_SoTheNextReadGoesToTheTable(FailedWrite write, bool largeValues)
    {
        if (largeValues)
            await RunAsync<RaceDoc>(write);
        else
            await RunAsync<RacePlainDoc>(write);
    }

    private static async Task RunAsync<T>(FailedWrite write) where T : Entity, IRaceDoc, new()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<T>(table, cache);
        await store.UpsertAsync(Doc<T>(1), Ct);
        if (write == FailedWrite.Create)
        {
            // The row goes through a store over another cache: this store still holds version 1.
            using var otherCache = new MemoryCache(new MemoryCacheOptions());
            await NewStore<T>(table, otherCache).DeleteAsync(Id, Ct);
        }
        Assert.Equal(1, (await store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(write == FailedWrite.Create ? 0 : 1, (await store.QueryAsync("p", Ct)).Count()); // the list is cached
        var pointReads = table.Count(r => r.IsWholeRowPointRead);
        var queries = table.Count(r => r.IsEntityQuery);

        var thrown = new TaskCanceledException("the caller's token was cancelled after the request was sent");
        var failures = 0;
        table.Committed = _ =>
        {
            if (Interlocked.Increment(ref failures) == 1)
                throw thrown;
        };
        var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write switch
        {
            FailedWrite.Create => store.CreateAsync(Doc<T>(2), Ct),
            FailedWrite.Update => store.UpdateAsync(Doc<T>(2), ConcurrencyMode.LastWriterWins, Ct),
            FailedWrite.Upsert => store.UpsertAsync(Doc<T>(2), Ct),
            FailedWrite.Builder => store.UpdateAsync(Id, b => b.SetProperty(x => x.Version, 2), Ct),
            _ => store.DeleteAsync(Id, Ct),
        });
        table.Committed = null;
        Assert.Same(thrown, caught);

        var stored = StoredVersion(table);
        Assert.Equal(write == FailedWrite.Delete ? null : 2, stored); // it did commit
        Assert.Equal(stored, (await store.OneAsync(Id, Ct))?.Version);
        Assert.Equal(pointReads + 1, table.Count(r => r.IsWholeRowPointRead));
        Assert.Equal(stored is null ? 0 : 1, (await store.QueryAsync("p", Ct)).Count());
        Assert.Equal(queries + 1, table.Count(r => r.IsEntityQuery));
    }

    // A failure before any storage call (here: Strict without an ETag, refused before anything is sent)
    // changes nothing stored, and evicts nothing: the next read is still served from the cache.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWrite_RefusedBeforeAnyStorageCall_EvictsNothing(bool largeValues)
    {
        if (largeValues)
            await RefusedAsync<RaceDoc>();
        else
            await RefusedAsync<RacePlainDoc>();
    }

    private static async Task RefusedAsync<T>() where T : Entity, IRaceDoc, new()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<T>(table, cache);
        await store.UpsertAsync(Doc<T>(1), Ct);
        Assert.Equal(1, (await store.OneAsync(Id, Ct))!.Version);
        var requests = table.Requests.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(Doc<T>(2), ConcurrencyMode.Strict, Ct));

        Assert.Equal(1, (await store.OneAsync(Id, Ct))!.Version);
        Assert.Equal(requests, table.Requests.Count); // nothing sent, and the read was served from the cache
    }
}
