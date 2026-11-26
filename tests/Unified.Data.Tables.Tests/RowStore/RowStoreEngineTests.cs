#pragma warning disable UDT0001 // the experimental raw-table API is part of what the engine implements

using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.RowStore;

/// <summary>
/// The row store a local provider implements (<see cref="IRowStore"/>, here the in-memory one): key order,
/// bounded range reads from a resume key, and the one atomic commit with its conditions.
/// </summary>
public class InMemoryRowStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableEntity Row(string pk, string rk, string? value = null)
    {
        var row = new TableEntity(pk, rk);
        if (value is not null)
            row["V"] = value;
        return row;
    }

    private static async Task<InMemoryTable> TableAsync(params (string Pk, string Rk)[] keys)
    {
        var table = new InMemoryAccount().Table("T");
        table.Create();
        foreach (var group in keys.GroupBy(k => k.Pk))
            Assert.True((await table.CommitAsync(group.Key, [.. group.Select(k => RowWrite.Insert(Row(k.Pk, k.Rk)))], Ct)).Committed);
        return table;
    }

    private static async Task<List<string>> KeysAsync(IRowStore table, RowRange range, int limit = 100) =>
        (await table.ReadAsync(range, limit, Ct)).Select(r => r.PartitionKey + "|" + r.RowKey).ToList();

    [Fact]
    public async Task Reads_AreInOrdinalKeyOrder_PartitionThenRow()
    {
        // Ordinal, not culture order: upper case before lower case, and "a~" after "a|".
        var table = await TableAsync(("b", "2"), ("b", "10"), ("a", "z"), ("a", "Z"), ("B", "1"), ("a", "a~1"), ("a", "a"));

        Assert.Equal(["B|1", "a|Z", "a|a", "a|a~1", "a|z", "b|10", "b|2"], await KeysAsync(table, RowRange.All));
        Assert.Equal(["a|Z", "a|a", "a|a~1", "a|z"], await KeysAsync(table, new RowRange("a")));
        Assert.Empty(await KeysAsync(table, new RowRange("none")));
    }

    [Fact]
    public async Task ARange_SelectsTheRowKeysBetweenItsBounds_EachInclusiveOrNot()
    {
        var table = await TableAsync(("p", "a"), ("p", "b"), ("p", "c"), ("p", "d"), ("q", "b"));

        Assert.Equal(["p|b", "p|c"], await KeysAsync(table, new RowRange("p", "b", true, "d", false)));
        Assert.Equal(["p|c", "p|d"], await KeysAsync(table, new RowRange("p", "b", false, "d", true)));
        Assert.Equal(["p|c", "p|d"], await KeysAsync(table, new RowRange("p", "bb")));
        Assert.Equal(["p|a", "p|b"], await KeysAsync(table, new RowRange("p", ToRowKey: "bb")));
        Assert.Empty(await KeysAsync(table, new RowRange("p", "d", true, "a", true)));
        // A prefix is the range from the prefix to the next string that does not start with it.
        var prefixed = await TableAsync(("p", "ab"), ("p", "ab￿"), ("p", "ab￿x"), ("p", "ac"), ("p", "a"));
        Assert.Equal(["p|ab", "p|ab￿", "p|ab￿x"], await KeysAsync(prefixed, RowRange.Prefix("p", "ab")));
        Assert.Equal(["p|ab￿", "p|ab￿x"], await KeysAsync(prefixed, RowRange.Prefix("p", "ab￿")));
        Assert.Equal(5, (await KeysAsync(prefixed, RowRange.Prefix("p", null))).Count);
    }

    [Fact]
    public async Task ARead_ReturnsABoundedPage_AndResumesAfterAKey_AcrossPartitions()
    {
        var table = await TableAsync(("a", "1"), ("a", "2"), ("b", "1"), ("b", "2"), ("c", "1"));

        // Pulled two rows at a time, from the last key of the page before.
        var seen = new List<string>();
        (string, string)? after = null;
        while (true)
        {
            var page = await table.ReadAsync(RowRange.All with { After = after }, 2, Ct);
            seen.AddRange(page.Select(r => r.PartitionKey + "|" + r.RowKey));
            if (page.Count < 2)
                break;
            after = (page[^1].PartitionKey, page[^1].RowKey);
        }
        Assert.Equal(["a|1", "a|2", "b|1", "b|2", "c|1"], seen);

        // A resume key need not be a stored row (it was deleted since), nor a stored partition.
        Assert.Equal(["b|1", "b|2", "c|1"], await KeysAsync(table, RowRange.All with { After = ("a", "9") }));
        Assert.Equal(["b|1", "b|2", "c|1"], await KeysAsync(table, RowRange.All with { After = ("aa", "1") }));
        Assert.Equal(["b|2"], await KeysAsync(table, new RowRange("b") with { After = ("b", "1") }));
        Assert.Equal(["b|1", "b|2"], await KeysAsync(table, new RowRange("b") with { After = ("a", "2") }));
        Assert.Empty(await KeysAsync(table, new RowRange("b") with { After = ("c", "0") }));
        Assert.Equal(["a|1"], await KeysAsync(table, RowRange.All, limit: 1));
    }

    [Fact]
    public async Task ACommit_AppliesEveryOperation_OrNone_AndNamesTheFirstThatFails()
    {
        var table = await TableAsync(("p", "stored"));
        var stored = (await table.GetAsync("p", "stored", Ct))!;

        var exists = await table.CommitAsync("p", [RowWrite.Insert(Row("p", "new")), RowWrite.Insert(Row("p", "stored"))], Ct);
        Assert.Equal((RowCommitFailure.AlreadyExists, 1, false), (exists.Failure, exists.FailedIndex, exists.Committed));
        var missing = await table.CommitAsync("p", [RowWrite.Replace(Row("p", "none")), RowWrite.Insert(Row("p", "new"))], Ct);
        Assert.Equal((RowCommitFailure.NotFound, 0), (missing.Failure, missing.FailedIndex));
        Assert.Equal(RowCommitFailure.NotFound, (await table.CommitAsync("p", [RowWrite.Delete("none")], Ct)).Failure);
        var stale = await table.CommitAsync("p", [RowWrite.Insert(Row("p", "new")), RowWrite.Delete("stored", "W/\"0\"")], Ct);
        Assert.Equal((RowCommitFailure.VersionMismatch, 1), (stale.Failure, stale.FailedIndex));
        Assert.Equal(RowCommitFailure.VersionMismatch, (await table.CommitAsync("p", [RowWrite.Replace(Row("p", "stored"), "W/\"0\"")], Ct)).Failure);
        // Nothing of the refused commits was applied.
        Assert.Equal(["p|stored"], await KeysAsync(table, RowRange.All));
        Assert.Equal(stored.ETag, (await table.GetAsync("p", "stored", Ct))!.ETag);

        var applied = await table.CommitAsync("p",
        [
            RowWrite.Replace(Row("p", "stored", "v2"), stored.ETag), RowWrite.Insert(Row("p", "new")), RowWrite.Upsert(Row("p", "other")),
            RowWrite.DeleteIfExists("none"),
        ], Ct);
        Assert.True(applied.Committed);
        Assert.Equal(-1, applied.FailedIndex);
        Assert.Equal(4, applied.ETags.Count);
        Assert.Null(applied.ETags[3]);
        Assert.Equal(3, applied.ETags.Take(3).Distinct().Count());
        var replaced = (await table.GetAsync("p", "stored", Ct))!;
        Assert.Equal(applied.ETags[0], replaced.ETag);
        Assert.NotEqual(stored.ETag, replaced.ETag);
        Assert.Equal("v2", replaced.Data["V"]);
        Assert.Equal(applied.Timestamp, replaced.Timestamp);

        Assert.True((await table.CommitAsync("p", [RowWrite.Delete("stored", replaced.ETag), RowWrite.Delete("new")], Ct)).Committed);
        Assert.Equal(["p|other"], await KeysAsync(table, RowRange.All));
    }

    [Fact]
    public async Task ATableThatDoesNotExist_ThrowsFromEveryOperation_UntilItIsCreated()
    {
        var account = new InMemoryAccount();
        var table = account.GetTable("Missing");

        await Assert.ThrowsAsync<RowStoreTableMissingException>(async () => await table.GetAsync("p", "r", Ct));
        await Assert.ThrowsAsync<RowStoreTableMissingException>(async () => await table.ReadAsync(RowRange.All, 10, Ct));
        var thrown = await Assert.ThrowsAsync<RowStoreTableMissingException>(async () => await table.CommitAsync("p", [RowWrite.Insert(Row("p", "r"))], Ct));
        Assert.Equal("Missing", thrown.TableName);
        Assert.False(await account.TableExistsAsync("missing", Ct));

        // Names are compared ignoring case, as the service compares them; the handle stays valid.
        Assert.True(await account.CreateTableAsync("MISSING", Ct));
        Assert.False(await account.CreateTableAsync("missing", Ct));
        Assert.True((await table.CommitAsync("p", [RowWrite.Insert(Row("p", "r"))], Ct)).Committed);
        Assert.True(await account.DeleteTableAsync("Missing", Ct));
        Assert.False(await account.DeleteTableAsync("Missing", Ct));
        await Assert.ThrowsAsync<RowStoreTableMissingException>(async () => await table.GetAsync("p", "r", Ct));
        // A table created again is empty.
        Assert.True(await account.CreateTableAsync("Missing", Ct));
        Assert.Null(await table.GetAsync("p", "r", Ct));
    }

    [Fact]
    public async Task ConcurrentInsertsOfOneKey_CommitExactlyOnce()
    {
        var table = await TableAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(
            async () => await table.CommitAsync("p", [RowWrite.Insert(Row("p", "one", "writer " + i))], Ct), Ct)));

        Assert.Single(results, r => r.Committed);
        Assert.All(results.Where(r => !r.Committed), r => Assert.Equal(RowCommitFailure.AlreadyExists, r.Failure));
    }
}

/// <summary>
/// The engine over a row store (<see cref="RowStoreStorage{T}"/>), through the in-memory provider and
/// directly: what the rebase of 0.10.0 added to the in-memory stores, and the read cache a local provider
/// with other writers (SQLite) runs with.
/// </summary>
public class RowStoreEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ── what the store states, and the clock ──────────────────────────────

    [Fact]
    public void TheInMemoryStores_StateWhatTheyAre_VolatileWithTheServicesLimits_AndNoReadCache()
    {
        var expected = new StorageInfo
        {
            ProviderName = "InMemory",
            TableName = "TestEntity",
            Durability = Durability.Volatile,
            MaxBatchRows = 100,
            MaxBatchBytes = 3_000_000,
            MaxCellBytes = 64 * 1024,
            MaxRowBytes = 1_000_000,
            ReadCacheMayServeStaleRows = false,
            SinglePartitionTransactions = true,
        };

        IStorage<TestEntity> store = new InMemoryStorage<TestEntity>();
        Assert.Equal(expected, store.GetInfo());
        // The default options name a sliding hour of cache; this store has none whatever they say.
        Assert.Equal(expected with { TableName = "Renamed" },
            new InMemoryStorage<TestEntity>(null, new StorageOptions { TableNameResolver = _ => "Renamed" }).GetInfo());
        IPolymorphicStorage<object> polymorphic = new InMemoryPolymorphicStorage<object>("Events");
        Assert.Equal(expected with { TableName = "Events" }, polymorphic.GetInfo());
    }

    [Fact]
    public async Task TheInMemoryStore_StampsCreatedAtAndUpdatedAt_FromTheOptionsClock()
    {
        var clock = new Clock(new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var store = new InMemoryStorage<TestEntity>(null, new StorageOptions { TimeProvider = clock });

        var created = await store.CreateAsync(new TestEntity { Id = "p|a" }, Ct);
        Assert.Equal(clock.Now, created.CreatedAt);
        Assert.Equal(clock.Now, created.UpdatedAt);

        var createdAt = clock.Now;
        clock.Now = clock.Now.AddHours(1);
        var updated = await store.UpdateAsync(created, Ct);
        Assert.Equal(createdAt, updated.CreatedAt);
        Assert.Equal(clock.Now, updated.UpdatedAt);

        clock.Now = clock.Now.AddHours(1);
        await store.UpdateAsync("p|a", b => b.SetProperty(e => e.Name, "n"), Ct);
        Assert.Equal(clock.Now, (await store.OneAsync("p|a", Ct))!.UpdatedAt);
        // (The Entity base class stamps CreatedAt itself when it is constructed; an upsert keeps a stamp it finds.)
        await store.UpsertBatchAsync([new TestEntity { Id = "p|b", CreatedAt = default }], Ct);
        var batched = (await store.OneAsync("p|b", Ct))!;
        Assert.Equal((clock.Now, clock.Now), (batched.CreatedAt, batched.UpdatedAt));
        // The row's own timestamp is the store's time of the write, not the caller's clock.
        Assert.NotEqual(clock.Now, batched.Timestamp);
    }

    // ── the service's limits ──────────────────────────────────────────────

    [Fact]
    public async Task ARowLargerThanOneMegabyte_IsRefused_AsTheServiceRefusesIt()
    {
        IStorage<TestEntity> store = new InMemoryStorage<TestEntity>();
        Assert.True(store.TryGetCapability<TestEntity, IRawTable>(out var raw));
        // Each cell fits (60 KB), the row does not (18 of them).
        var cells = Enumerable.Range(0, 18).ToDictionary(i => "B" + i, _ => RawCell.FromBinary(new byte[60_000]));

        var refused = await Assert.ThrowsAsync<StorageRequestRejectedException>(
            () => raw!.SubmitAsync([RawOperation.Insert(new RawRow("p", "big", cells))], Ct));
        Assert.Equal("EntityTooLarge", refused.ProviderErrorCode);
        var cell = await Assert.ThrowsAsync<StorageRequestRejectedException>(() => raw!.SubmitAsync(
            [RawOperation.Insert(new RawRow("p", "cell", new Dictionary<string, RawCell> { ["B"] = RawCell.FromBinary(new byte[(64 * 1024) + 1]) }))], Ct));
        Assert.Equal("PropertyValueTooLarge", cell.ProviderErrorCode);
        Assert.Equal(0, await store.CountAsync(ct: Ct));

        // A merge that would grow a stored row past the limit is refused too, and changes nothing.
        var half = Enumerable.Range(0, 9).ToDictionary(i => "B" + i, _ => RawCell.FromBinary(new byte[60_000]));
        var other = Enumerable.Range(9, 9).ToDictionary(i => "B" + i, _ => RawCell.FromBinary(new byte[60_000]));
        await raw!.SubmitAsync([RawOperation.Insert(new RawRow("p", "grown", half))], Ct);
        await Assert.ThrowsAsync<StorageRequestRejectedException>(() => raw.SubmitAsync([RawOperation.Merge(new RawRow("p", "grown", other))], Ct));
        Assert.Equal(9, (await raw.GetAsync("p", "grown", Ct))!.Cells.Count);

        // The polymorphic store holds rows to the same limits.
        var polymorphic = new InMemoryPolymorphicStorage<object>("Events");
        var columns = Enumerable.Range(0, 18).ToDictionary(i => "_B" + i, _ => (object)new byte[60_000]);
        var tooLarge = await Assert.ThrowsAsync<StorageRequestRejectedException>(
            () => polymorphic.InsertAsync(PolymorphicWrite<object>.Marker(new TableKey("p", "r"), columns), Ct));
        Assert.Equal("EntityTooLarge", tooLarge.ProviderErrorCode);
        Assert.Equal(0, polymorphic.Count);
    }

    // ── the table behind the store ────────────────────────────────────────

    [Fact]
    public async Task ATableDeletedBehindTheStore_FailsWithTableNotFound_NeverAMissingRow()
    {
        IStorage<TestEntity> store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|a" }, Ct);
        Assert.True(store.TryGetCapability<TestEntity, ITableStore>(out var tables));
        var listed = new List<string>();
        await foreach (var name in tables!.ListTablesAsync(Ct))
            listed.Add(name);
        Assert.Equal(["TestEntity"], listed);

        Assert.True(await tables.DeleteTableAsync("testentity", Ct));

        // As TableStorage<T>: the table is created once, on first use, and not again behind a delete.
        var read = await Assert.ThrowsAsync<TableNotFoundException>(() => store.OneAsync("p|a", Ct));
        Assert.Equal("TestEntity", read.TableName);
        Assert.Equal("TableNotFound", read.ProviderErrorCode);
        Assert.NotNull(read.InnerException);
        await Assert.ThrowsAsync<TableNotFoundException>(() => store.CreateAsync(new TestEntity { Id = "p|b" }, Ct));
        await Assert.ThrowsAsync<TableNotFoundException>(() => store.DeleteAsync("p|a", Ct));
        await Assert.ThrowsAsync<TableNotFoundException>(() => store.DeletePartitionAsync("p", Ct));
        await Assert.ThrowsAsync<TableNotFoundException>(() => store.QueryAsync("p", Ct));
        await Assert.ThrowsAsync<TableNotFoundException>(async () => { await foreach (var _ in store.QueryStreamAsync(ct: Ct)) { } });
        await Assert.ThrowsAsync<TableNotFoundException>(() => store.UpdateAsync("p|a", b => b.SetProperty(e => e.Name, "n"), Ct));

        var polymorphic = new InMemoryPolymorphicStorage<object>("Events");
        await polymorphic.EnsureCreatedAsync(Ct);
        Assert.True(((IPolymorphicStorage<object>)polymorphic).TryGetCapability<object, ITableStore>(out var polymorphicTables));
        Assert.True(await polymorphicTables!.DeleteTableAsync("Events", Ct));
        await Assert.ThrowsAsync<TableNotFoundException>(() => polymorphic.GetAsync(new TableKey("p", "r"), Ct));
        await Assert.ThrowsAsync<TableNotFoundException>(() => polymorphic.DeleteAsync(new TableKey("p", "r"), Ct));
    }

    [Fact]
    public async Task TheTableStore_RefusesANameTheServiceRefuses()
    {
        IStorage<TestEntity> store = new InMemoryStorage<TestEntity>();
        Assert.True(store.TryGetCapability<TestEntity, ITableStore>(out var tables));

        foreach (var name in new[] { "ab", "1abc", "with-dash", "with_underscore", new string('a', 64) })
        {
            var refused = await Assert.ThrowsAsync<StorageRequestRejectedException>(() => tables!.CreateTableAsync(name, Ct));
            Assert.Equal("InvalidResourceName", refused.ProviderErrorCode);
            Assert.False(await tables!.TableExistsAsync(name, Ct));
        }
        Assert.True(await tables!.CreateTableAsync(new string('a', 63), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => tables.CreateTableAsync(" ", Ct));
    }

    // ── all-or-nothing batches ────────────────────────────────────────────

    [Fact]
    public async Task AnAtomicBatchRefusedBeforeItIsWritten_LeavesTheCallersEntitiesAsTheyWere_WhateverRefusedIt()
    {
        IStorage<TestEntity> store = new InMemoryStorage<TestEntity>();
        var stamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TestEntity[] batch =
        [
            new() { Id = "P|Good", ETag = "kept", CreatedAt = stamp, UpdatedAt = stamp },
            new() { Id = "P|Bad#Key", ETag = "kept too", CreatedAt = stamp, UpdatedAt = stamp },
        ];

        // Not a BatchNotAtomicException: a key the service refuses, found when the rows are planned.
        await Assert.ThrowsAsync<StorageRequestRejectedException>(() => store.CreateAtomicAsync(batch, Ct));

        Assert.Equal(0, await store.CountAsync(ct: Ct));
        Assert.Equal(["P|Good", "P|Bad#Key"], batch.Select(e => e.Id));
        Assert.Equal(["kept", "kept too"], batch.Select(e => e.ETag));
        Assert.All(batch, e => Assert.Equal((stamp, stamp), (e.CreatedAt, e.UpdatedAt)));

        // A batch that reached the store and was refused there (a stored key) wrote nothing either, but
        // its entities are stamped as any batch that was sent stamps them.
        await store.CreateAsync(new TestEntity { Id = "p|stored" }, Ct);
        TestEntity[] duplicate = [new() { Id = "P|New", ETag = "x" }, new() { Id = "P|Stored", ETag = "y" }];
        var failure = await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAtomicAsync(duplicate, Ct));
        Assert.False(failure.MayBeOwnWrite);
        Assert.Equal("p|stored", failure.Id);
        Assert.Equal(1, await store.CountAsync(ct: Ct));
        Assert.Equal(["p|new", "p|stored"], duplicate.Select(e => e.Id));
        Assert.All(duplicate, e => Assert.Null(e.ETag));
    }

    [Fact]
    public async Task AConditionalDelete_IsNeverIndeterminate_AndAConflictIsNeverItsOwnWrite()
    {
        IStorage<TestEntity> store = new InMemoryStorage<TestEntity>();
        var created = await store.CreateAsync(new TestEntity { Id = "p|a" }, Ct);
        var stale = created.ETag!;
        var updated = await store.UpdateAsync(created, Ct);
        Assert.NotEqual(stale, updated.ETag);

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.DeleteIfMatchAsync("p|a", stale, Ct));
        Assert.False(conflict.MayBeOwnWrite);
        Assert.True(await store.ExistsAsync("p|a", Ct));
        Assert.Equal(DeleteStatus.Deleted, await store.DeleteIfMatchAsync("P|A", updated.ETag!, Ct));
        Assert.Equal(DeleteStatus.NotFound, await store.DeleteIfMatchAsync("p|a", updated.ETag!, Ct));
        Assert.Equal(DeleteStatus.NotFound, await store.DeleteIfExistsAsync("p|a", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteIfMatchAsync("p|a", "*", Ct));
    }

    // ── range reads are pulled a page at a time ───────────────────────────

    [Fact]
    public async Task AScanLongerThanOnePull_ReturnsEveryRowOnce_InOrder()
    {
        var store = new InMemoryStorage<TestEntity>();
        var ids = Enumerable.Range(0, 2_350).Select(i => $"p{i % 3}|r{i:D5}").ToList();
        foreach (var partition in ids.GroupBy(id => id[..2]))
            await store.CreateBatchAsync([.. partition.Select(id => new TestEntity { Id = id, Value = 1 })], Ct);
        var expected = ids.Order(StringComparer.Ordinal).ToList();

        Assert.Equal(expected, (await store.QueryAsync((string?)null, Ct)).Select(e => e.Id));
        var streamed = new List<string>();
        await foreach (var entity in store.QueryStreamAsync(ct: Ct))
            streamed.Add(entity.Id);
        Assert.Equal(expected, streamed);
        Assert.Equal(expected, (await store.QueryAsync(e => e.Value == 1, ct: Ct)).Select(e => e.Id));
        Assert.Equal(expected.Take(1_500), (await store.QueryAsync(new QueryOptions { Take = 1_500 }, Ct)).Select(e => e.Id));
        Assert.Equal(2_350, await store.CountAsync(ct: Ct));

        // Pages follow each other over the partitions without a gap or a repeat.
        var paged = new List<string>();
        string? token = null;
        do
        {
            var page = await store.QueryPageAsync(new QueryOptions { Take = 700, ContinuationToken = token }, Ct);
            paged.AddRange(page.Items.Select(e => e.Id));
            token = page.ContinuationToken;
        }
        while (token is not null);
        Assert.Equal(expected, paged);

        Assert.Equal(expected.Count(id => id.StartsWith("p1", StringComparison.Ordinal)), await store.DeletePartitionAsync("p1", Ct));
        Assert.Equal(expected.Where(id => !id.StartsWith("p1", StringComparison.Ordinal)), store.Snapshot().Select(e => e.Id));
    }

    // ── the read cache (a provider with other writers; the in-memory store has none) ──

    private static (RowStoreStorage<TestEntity> Store, IRawTable Raw, Clock Clock) Cached(CachePolicy? policy = null)
    {
        var clock = new Clock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new RowStoreStorage<TestEntity>(new InMemoryAccount(), new StorageOptions { TimeProvider = clock }, authorizer: null, blobs: null, policy);
        Assert.True(store.TryGetCapability<IRawTable>(out var raw));
        return (store, raw!, clock);
    }

    private static Task RenameBehindAsync(IRawTable raw, string rowKey, string name) =>
        raw.SubmitAsync([RawOperation.Merge(new RawRow("p", rowKey, new Dictionary<string, RawCell> { ["Name"] = RawCell.FromString(name) }))], Ct);

    [Fact]
    public async Task TheEngine_CachesReadsByDefault_AsTheAzureProviderDoes_AndSaysSo()
    {
        var (store, raw, clock) = Cached();
        Assert.True(store.GetInfo().ReadCacheMayServeStaleRows);
        await store.CreateAsync(new TestEntity { Id = "p|a", Name = "first" }, Ct);
        Assert.Equal("first", (await store.OneAsync("p|a", Ct))!.Name);

        // Another writer (here: the raw table, which goes past the cache) changes the row.
        await RenameBehindAsync(raw, "a", "behind");

        // The stale row is served, by any spelling of its id, until the sliding hour ends.
        Assert.Equal("first", (await store.OneAsync("P|A", Ct))!.Name);
        clock.Now = clock.Now.AddMinutes(59);
        Assert.Equal("first", (await store.OneAsync("p|a", Ct))!.Name);
        clock.Now = clock.Now.AddMinutes(59);
        Assert.Equal("first", (await store.OneAsync("p|a", Ct))!.Name);
        clock.Now = clock.Now.AddMinutes(61);
        Assert.Equal("behind", (await store.OneAsync("p|a", Ct))!.Name);
    }

    [Fact]
    public async Task ACachedEntity_IsACopy_AndEveryWriteThroughTheStoreEvictsIt()
    {
        var (store, raw, _) = Cached();
        await store.CreateAsync(new TestEntity { Id = "p|a", Name = "first" }, Ct);
        var read = (await store.OneAsync("p|a", Ct))!;
        read.Name = "changed by the caller";
        Assert.Equal("first", (await store.OneAsync("p|a", Ct))!.Name);
        Assert.True(await store.ExistsAsync("p|a", Ct));

        // Each of these goes to the rows again afterwards: what was changed behind the store shows.
        var step = 0;
        async Task AssertEvictedByAsync(Func<Task> write)
        {
            var before = (await store.OneAsync("p|a", Ct))!.Name;
            await RenameBehindAsync(raw, "a", "behind " + step++);
            Assert.Equal(before, (await store.OneAsync("p|a", Ct))!.Name);
            await write();
            var after = await store.OneAsync("p|a", Ct);
            Assert.True(after is null || after.Name != before, "the cached entity was served after a write");
        }

        await AssertEvictedByAsync(() => store.UpdateAsync("p|a", b => b.SetProperty(e => e.Value, 1), Ct));
        Assert.Equal("behind 0", (await store.OneAsync("p|a", Ct))!.Name);
        await AssertEvictedByAsync(async () => await store.UpsertAsync(new TestEntity { Id = "p|a", Name = "upserted" }, Ct));
        Assert.Equal("upserted", (await store.OneAsync("p|a", Ct))!.Name);
        await AssertEvictedByAsync(async () => await store.UpsertBatchAsync([new TestEntity { Id = "p|a", Name = "batched" }], Ct));
        Assert.Equal("batched", (await store.OneAsync("p|a", Ct))!.Name);
        await AssertEvictedByAsync(async () => await store.UpsertAtomicAsync([new TestEntity { Id = "p|a", Name = "atomic" }], Ct));
        // A failed write evicts too: its row may be another version than the cached one.
        await AssertEvictedByAsync(async () => await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new TestEntity { Id = "p|a" }, Ct)));
        await AssertEvictedByAsync(() => store.DeleteAsync("p|a", Ct));
        Assert.Null(await store.OneAsync("p|a", Ct));
        Assert.False(await store.ExistsAsync("p|a", Ct));
    }

    [Fact]
    public async Task APartitionList_IsCached_UntilAWriteOfThePartitionThroughTheStore()
    {
        var (store, raw, _) = Cached(CachePolicy.Absolute(TimeSpan.FromMinutes(5)));
        await store.CreateBatchAsync([new TestEntity { Id = "p|a", Name = "a" }, new TestEntity { Id = "p|b", Name = "b" }, new TestEntity { Id = "q|a", Name = "q" }], Ct);

        Assert.Equal(["a", "b"], (await store.QueryAsync("p", Ct)).Select(e => e.Name));
        Assert.Equal(3, (await store.QueryAsync((string?)null, Ct)).Count());
        await RenameBehindAsync(raw, "a", "behind");
        // The list, and the entities it warmed, are served from memory; a predicate query reads the rows.
        Assert.Equal(["a", "b"], (await store.QueryAsync("P", Ct)).Select(e => e.Name));
        Assert.Equal("a", (await store.OneAsync("p|a", Ct))!.Name);
        Assert.Equal(["behind"], (await store.QueryAsync(new QueryOptions { Partition = "p", Take = 1 }, Ct)).Select(e => e.Name));

        await store.DeleteAsync("p|b", Ct);
        Assert.Equal(["behind"], (await store.QueryAsync("p", Ct)).Select(e => e.Name));
        Assert.Equal(2, (await store.QueryAsync((string?)null, Ct)).Count());
    }

    [Fact]
    public async Task ADisabledCache_ReadsTheRowsEveryTime()
    {
        var (store, raw, _) = Cached(CachePolicy.Disabled);
        Assert.False(store.GetInfo().ReadCacheMayServeStaleRows);
        await store.CreateAsync(new TestEntity { Id = "p|a", Name = "first" }, Ct);
        Assert.Equal("first", (await store.OneAsync("p|a", Ct))!.Name);
        Assert.Single(await store.QueryAsync("p", Ct));

        await RenameBehindAsync(raw, "a", "behind");

        Assert.Equal("behind", (await store.OneAsync("p|a", Ct))!.Name);
        Assert.Equal(["behind"], (await store.QueryAsync("p", Ct)).Select(e => e.Name));
    }

    [Fact]
    public async Task AReadThatOverlapsAWrite_DoesNotLeaveTheOlderVersionInTheCache()
    {
        // Readers and one writer on the same row: after the last write, a read returns that write.
        var (store, _, _) = Cached();
        await store.CreateAsync(new TestEntity { Id = "p|a", Value = 0 }, Ct);
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                await store.OneAsync("p|a", Ct);
        }, Ct)).ToArray();

        for (var value = 1; value <= 300; value++)
        {
            await store.UpsertAsync(new TestEntity { Id = "p|a", Value = value }, Ct);
            Assert.Equal(value, (await store.OneAsync("p|a", Ct))!.Value);
        }
        await stop.CancelAsync();
        await Task.WhenAll(readers);
        Assert.Equal(300, (await store.OneAsync("p|a", Ct))!.Value);
    }
}
