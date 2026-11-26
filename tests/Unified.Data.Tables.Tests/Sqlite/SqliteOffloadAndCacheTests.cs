#pragma warning disable UDT0001 // a row is changed behind a store through the experimental raw table
using System.Security.Cryptography;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Sqlite;

namespace Unified.Data.Tables.Tests.Sqlite;

/// <summary>
/// The offloaded values of the SQLite provider (the <c>__blobs</c> table behind
/// <see cref="IOffloadBlobStore"/>), held to the answers of the in-memory blob store, and the read cache
/// of a <see cref="SqliteStorage{T}"/>: on by default, as on the Azure provider, and as stale.
/// </summary>
public sealed class SqliteOffloadAndCacheTests : IDisposable
{
    private readonly TempDatabase database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => database.Dispose();

    public sealed class Doc : Entity
    {
        public string? Title { get; set; }

        public string? Note { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }
    }

    private static BlobReference Reference(string name, byte[] bytes) =>
        new(name, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, "application/octet-stream", "binary");

    private static async Task<string> Outcome(Func<Task<object?>> call)
    {
        try
        {
            return Convert.ToString(await call(), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        }
        catch (Exception ex) when (ex is TableValueIntegrityException or ArgumentException)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheBlobStore_AnswersAsTheInMemoryBlobStoreAnswers(bool file)
    {
        var account = database.Open(file ? database.Path : SqliteStorageOptions.InMemoryDataSource);
        IOffloadBlobStore sqlite = new SqliteOffloadBlobStore(account.Database, "values");
        IOffloadBlobStore memory = new InMemoryOffloadBlobStore();
        byte[] one = [1, 2, 3], other = [9, 9];

        foreach (var store in new[] { sqlite, memory })
        {
            Assert.Null(await store.GetStateAsync("T/p/r/Body/a", Ct));
            await store.UploadCreateOnlyAsync("T/p/r/Body/a", new MemoryStream(one), Reference("T/p/r/Body/a", one), "gzip", Ct);
            await store.UploadCreateOnlyAsync("T/p/r/Body/b", new MemoryStream(other), Reference("T/p/r/Body/b", other), null, Ct);
            await store.UploadCreateOnlyAsync("T/q/r/Body/c", new MemoryStream([]), Reference("T/q/r/Body/c", []), null, Ct);
            await store.UploadCreateOnlyAsync("U/p/r/Body/d", new MemoryStream(one), Reference("U/p/r/Body/d", one), null, Ct);

            var state = (await store.GetStateAsync("T/p/r/Body/a", Ct))!;
            Assert.Equal((Reference("x", one).Sha256, 3L), (state.Sha256, state.Length));
            await using (var content = await store.OpenReadAsync("Body", "T/p/r/Body/a", Ct))
            {
                using var held = new MemoryStream();
                await content.CopyToAsync(held, Ct);
                Assert.Equal(one, held.ToArray());
            }
            await using (var empty = await store.OpenReadAsync("Body", "T/q/r/Body/c", Ct))
                Assert.Equal(0, empty.Length);

            // Create-only: the same bytes again are success and a touch; other bytes under the name are refused.
            await store.UploadCreateOnlyAsync("T/p/r/Body/a", new MemoryStream(one), Reference("T/p/r/Body/a", one), null, Ct);
            var touched = (await store.GetStateAsync("T/p/r/Body/a", Ct))!;
            Assert.True(touched.LastModified > state.LastModified);
            await Assert.ThrowsAsync<TableValueIntegrityException>(() =>
                store.UploadCreateOnlyAsync("T/p/r/Body/a", new MemoryStream(other), Reference("T/p/r/Body/a", other), null, Ct));
            Assert.Equal(touched.Sha256, (await store.GetStateAsync("T/p/r/Body/a", Ct))!.Sha256);

            // A touch is always observably newer, and the service clock is never behind a stamp.
            await store.TouchAsync("T/p/r/Body/a", Ct);
            var again = (await store.GetStateAsync("T/p/r/Body/a", Ct))!;
            Assert.True(again.LastModified > touched.LastModified);
            Assert.True(await store.GetServiceTimeAsync(Ct) >= again.LastModified);

            // Listing: by prefix, in ordinal order; the directories under a prefix once each.
            Assert.Equal(["T/p/r/Body/a", "T/p/r/Body/b"], await Names(store.ListAsync("T/p/", Ct)));
            Assert.Equal(["T/p/r/Body/a", "T/p/r/Body/b", "T/q/r/Body/c"], await Names(store.ListAsync("T/", Ct)));
            Assert.Equal(4, (await Names(store.ListAsync("", Ct))).Count);
            Assert.Empty(await Names(store.ListAsync("V/", Ct)));
            Assert.Equal(["T/p/", "T/q/"], await store.ListPrefixesAsync("T/", Ct).ToListAsync(Ct));

            // A conditional delete loses to a newer touch, wins otherwise, and says so.
            Assert.False(await store.DeleteIfUnmodifiedAsync("T/p/r/Body/a", touched.LastModified, Ct));
            Assert.True(await store.DeleteIfUnmodifiedAsync("T/p/r/Body/a", again.LastModified, Ct));
            Assert.False(await store.DeleteIfUnmodifiedAsync("T/p/r/Body/a", null, Ct));
            Assert.True(await store.DeleteIfUnmodifiedAsync("T/p/r/Body/b", null, Ct));
            Assert.Null(await store.GetStateAsync("T/p/r/Body/a", Ct));
        }

        // Where the two could differ in words: a missing blob, a name the Blobs SDK would not address.
        foreach (var call in new Func<IOffloadBlobStore, Func<Task<object?>>>[]
        {
            s => async () => { await s.TouchAsync("T/missing", Ct); return null; },
            s => async () => await s.OpenReadAsync("Body", "T/missing", Ct),
            s => async () => await s.GetStateAsync("T/../x", Ct),
            s => async () => { await s.TouchAsync("./x", Ct); return null; },
            s => async () => await s.DeleteIfUnmodifiedAsync("T/./x", null, Ct),
            s => async () => await s.OpenReadAsync("Body", "a/../b", Ct),
            s => async () => { await s.UploadCreateOnlyAsync("a/..", new MemoryStream(one), Reference("a/..", one), null, Ct); return null; },
        })
        {
            Assert.Equal(await Outcome(call(memory)), await Outcome(call(sqlite)));
        }

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sqlite.GetStateAsync("T/x", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sqlite.TouchAsync("T/x", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sqlite.GetServiceTimeAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sqlite.DeleteIfUnmodifiedAsync("T/x", null, cancelled.Token));

        static async Task<List<string>> Names(IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> blobs)
        {
            var names = new List<string>();
            await foreach (var (name, _) in blobs)
                names.Add(name);
            return names;
        }
    }

    [Fact]
    public async Task Containers_KeepTheBlobsOfDifferentlyConfiguredStoresApart_InTheOneBlobsTable()
    {
        var account = database.Open();
        var first = new SqliteOffloadBlobStore(account.Database, "first");
        var second = new SqliteOffloadBlobStore(account.Database, "second");
        byte[] bytes = [1];

        await first.UploadCreateOnlyAsync("T/p/r/Body/a", new MemoryStream(bytes), Reference("T/p/r/Body/a", bytes), null, Ct);

        Assert.Null(await second.GetStateAsync("T/p/r/Body/a", Ct));
        Assert.False(await second.DeleteIfUnmodifiedAsync("T/p/r/Body/a", null, Ct));
        Assert.NotNull(await first.GetStateAsync("T/p/r/Body/a", Ct));
        Assert.Equal(1L, await account.Database.ReadAsync(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT count(*) FROM \"__blobs\" WHERE container = 'first'";
            return (long)command.ExecuteScalar()!;
        }, Ct));
    }

    [Fact]
    public async Task AnOffloadedValue_LivesInTheDatabase_SurvivesAReopen_AndGoesWithItsEntity()
    {
        void Configure(SqliteStorageOptions o)
        {
            o.Offload = new OffloadOptions { ThresholdBytes = 100_000, CleanupGrace = TimeSpan.Zero };
            o.Cache = CachePolicy.Disabled;
        }
        var body = string.Concat(Enumerable.Range(0, 30_000).Select(i => (i * 7919).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)));
        var account = database.Open(Configure);
        var store = new SqliteStorage<Doc>(account);
        await store.CreateAsync(new Doc { Id = "docs|big", Title = "big", Body = body }, Ct);
        await store.CreateAsync(new Doc { Id = "docs|small", Title = "small", Body = "fits a cell" }, Ct);
        account.Dispose();

        var reopened = database.Open(Configure);
        var again = new SqliteStorage<Doc>(reopened);
        var blobs = new SqliteOffloadBlobStore(reopened.Database, "unified-tables-values");

        Assert.Equal(body, (await again.OneAsync("docs|big", Ct))!.Body);
        var names = new List<string>();
        await foreach (var (name, _) in blobs.ListAsync("Doc/", Ct))
            names.Add(name);
        Assert.StartsWith("Doc/docs/big/Body/", Assert.Single(names), StringComparison.Ordinal);
        // One physical row for the entity: the value is a reference cell, not continuation rows.
        Assert.Equal(2, (await reopened.Database.GetTable("Doc").ReadAsync(RowRange.All, 10, Ct)).Count);

        await again.DeleteAsync("docs|big", Ct);

        Assert.Null(await blobs.GetStateAsync(names[0], Ct)); // grace zero: released with the row
        Assert.Equal(0, await again.SweepOrphanBlobsAsync(TimeSpan.Zero, Ct));
        Assert.Equal(1, await again.CountAsync(ct: Ct));
    }

    // ── The read cache: the Azure provider's default, and the staleness that comes with it ───────────

    private async Task ChangeBehindAsync(SqliteStorageAccount account, string text)
    {
        var raw = account.Tables.GetTable("Doc");
        await raw.SubmitAsync([RawOperation.Merge(new RawRow("docs", "a", new Dictionary<string, RawCell> { ["Title"] = RawCell.FromString(text) }))], Ct);
    }

    [Fact]
    public async Task ByDefault_ARowChangedBehindTheStore_IsServedStale_UntilTheStoreWritesIt_AsOnAzure()
    {
        var account = database.Open();
        var store = new SqliteStorage<Doc>(account);
        var other = new SqliteStorage<Doc>(account); // another instance over the same table, as another host is
        await store.CreateAsync(new Doc { Id = "docs|a", Title = "first" }, Ct);
        Assert.Equal("first", (await store.OneAsync("docs|a", Ct))!.Title); // cached from here on
        Assert.Single(await store.QueryAsync("docs", Ct));

        await other.UpdateAsync("docs|a", u => u.SetProperty(d => d.Title, "second"), Ct);
        await other.CreateAsync(new Doc { Id = "docs|b", Title = "new" }, Ct);

        // The point read and the partition list are answered from memory: stale.
        Assert.Equal("first", (await store.OneAsync("docs|a", Ct))!.Title);
        Assert.Single(await store.QueryAsync("docs", Ct));
        Assert.True(await store.ExistsAsync("docs|a", Ct));
        // What is never answered from the cache: a query with options, a page, a count, the raw table, the other store.
        Assert.Equal(2, (await store.QueryAsync(new QueryOptions { Partition = "docs" }, Ct)).Count);
        Assert.Equal(2, await store.CountAsync("docs", Ct));
        Assert.Equal("second", (await other.OneAsync("docs|a", Ct))!.Title);

        // A builder update reads the row it changes from the database, never from the cache, and a write
        // through the store evicts what it touched.
        await store.UpdateAsync("docs|a", u => u.SetProperty(d => d.Note, "note"), Ct);
        var fresh = (await store.OneAsync("docs|a", Ct))!;
        Assert.Equal(("second", "note"), (fresh.Title, fresh.Note));
        Assert.Equal(2, (await store.QueryAsync("docs", Ct)).Count());
    }

    [Fact]
    public async Task AStaleCachedRow_NeverOverwritesTheStoredOne_UnderAVersionCheck()
    {
        var account = database.Open();
        var store = new SqliteStorage<Doc>(account);
        await store.CreateAsync(new Doc { Id = "docs|a", Title = "first" }, Ct);
        var cached = (await store.OneAsync("docs|a", Ct))!;
        await ChangeBehindAsync(account, "changed behind the cache");

        var stale = (await store.OneAsync("docs|a", Ct))!;
        Assert.Equal((cached.ETag, "first"), (stale.ETag, stale.Title));
        stale.Title = "from the stale copy";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpdateAsync(stale, ConcurrencyMode.Strict, Ct));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.DeleteIfMatchAsync("docs|a", stale.ETag!, Ct));

        Assert.Equal("changed behind the cache", (await store.OneAsync("docs|a", Ct))!.Title);
    }

    [Fact]
    public async Task WithTheCacheDisabled_EveryReadIsTheStoredRow()
    {
        var account = database.Open(o => o.Cache = CachePolicy.Disabled);
        var store = new SqliteStorage<Doc>(account);
        await store.CreateAsync(new Doc { Id = "docs|a", Title = "first" }, Ct);
        Assert.Equal("first", (await store.OneAsync("docs|a", Ct))!.Title);

        await ChangeBehindAsync(account, "changed behind the store");

        Assert.Equal("changed behind the store", (await store.OneAsync("docs|a", Ct))!.Title);
        Assert.False(store.GetInfo().ReadCacheMayServeStaleRows);
    }

    [Fact]
    public async Task TheCachePolicyOfOneType_OverridesTheDefault_AsOnAzure()
    {
        var account = database.Open(o => o.CacheFor<Doc>(CachePolicy.Disabled));
        var store = new SqliteStorage<Doc>(account);
        await store.CreateAsync(new Doc { Id = "docs|a", Title = "first" }, Ct);
        await store.OneAsync("docs|a", Ct);

        await ChangeBehindAsync(account, "changed");

        Assert.Equal("changed", (await store.OneAsync("docs|a", Ct))!.Title);
    }
}
