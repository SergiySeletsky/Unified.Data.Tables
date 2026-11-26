#pragma warning disable UDT0001 // the experimental raw-table API is part of what the engine implements

using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Sqlite;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.Sqlite;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.RowStore;

/// <summary>A large-value-aware entity whose body is stored as a primary row and continuation rows.</summary>
public class LeftoverRaceDoc : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }
}

/// <summary>An object stored flattened into its owner's row (Inner_Text, Inner_Number).</summary>
public class NestedPart
{
    public string? Text { get; set; }

    public int Number { get; set; }
}

/// <summary>An entity with a nested object that may be null.</summary>
public class NestedOwner : Entity
{
    public string? Name { get; set; }

    public NestedPart? Inner { get; set; }
}

/// <summary>
/// The row-store engine (InMemory and SQLite) against the findings of the 0.10.0 release review: each
/// test fails on the engine as it was before the fix.
/// </summary>
public sealed class RowStoreReviewFixTests : IDisposable
{
    private readonly TempDatabase database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => database.Dispose();

    public static TheoryData<string> Accounts => ["inmemory", "sqlite"];

    private IRowStoreAccount Account(string kind) =>
        kind == "inmemory" ? new InMemoryAccount() : database.Open().Database;

    private static RowStoreStorage<T> Engine<T>(IRowStoreAccount account, CachePolicy? cache = null) where T : class, IEntity, new() =>
        new(account, new StorageOptions(), authorizer: null, blobs: null, cache ?? CachePolicy.Disabled);

    private static async Task<List<RawRow>> PhysicalRowsAsync<T>(RowStoreStorage<T> store, string partition) where T : class, IEntity, new()
    {
        Assert.True(store.TryGetCapability<IRawTable>(out var raw));
        var rows = new List<RawRow>();
        await foreach (var row in raw!.QueryAsync(new RawQuery(partition), Ct))
            rows.Add(row);
        return rows;
    }

    // ── a delete that finds no primary, racing a create ───────────────────

    [Theory]
    [MemberData(nameof(Accounts))]
    public async Task ADeleteThatFoundNoPrimary_LeavesTheRowsOfACreateThatCommittedMeanwhile(string kind)
    {
        var account = Account(kind);
        var deleter = Engine<LeftoverRaceDoc>(account);
        var creator = Engine<LeftoverRaceDoc>(account);
        var body = new string('b', 950_000);
        var created = false;
        deleter.BeforeLeftoverListing = () =>
        {
            if (created)
                return;
            created = true;
            creator.CreateAsync(new LeftoverRaceDoc { Id = "p|doc", Body = body }, Ct).GetAwaiter().GetResult();
        };

        // A conditional delete on a version no row ever had: it read no primary, and the create that came in
        // between is another version, so it is a conflict and nothing of the new entity goes.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => deleter.DeleteIfMatchAsync("p|doc", "W/\"0\"", Ct));
        Assert.True(created);
        Assert.True((await PhysicalRowsAsync(deleter, "p")).Count >= 2, "the entity's primary and its continuation rows are there");
        Assert.Equal(body, (await deleter.OneAsync("p|doc", Ct))!.Body);

        // A plain delete that loses the same race reads the entity again and deletes the whole of it.
        await creator.DeleteAsync("p|doc", Ct);
        created = false;
        await deleter.DeleteAsync("p|doc", Ct);
        Assert.True(created);
        Assert.Empty(await PhysicalRowsAsync(deleter, "p"));
        Assert.Null(await deleter.OneAsync("p|doc", Ct));
    }

    // ── the read cache: a partition named "*" ─────────────────────────────

    [Theory]
    [MemberData(nameof(Accounts))]
    public async Task APartitionNamedStar_IsCachedApartFromTheWholeTable(string kind)
    {
        var store = Engine<TestEntity>(Account(kind), CachePolicy.Absolute(TimeSpan.FromMinutes(5)));
        await store.CreateAsync(new TestEntity { Id = "*|a" }, Ct);
        await store.CreateAsync(new TestEntity { Id = "b|c" }, Ct);

        Assert.Equal(["*|a"], (await store.QueryAsync("*", Ct)).Select(e => e.Id));
        Assert.Equal(["*|a", "b|c"], (await store.QueryAsync((string?)null, Ct)).Select(e => e.Id));
        await store.CreateAsync(new TestEntity { Id = "b|d" }, Ct);
        Assert.Equal(["*|a", "b|c", "b|d"], (await store.QueryAsync((string?)null, Ct)).Select(e => e.Id));
        Assert.Equal(["*|a"], (await store.QueryAsync("*", Ct)).Select(e => e.Id));
    }

    [Fact]
    public async Task TheDefaultSqliteStore_AnswersAPartitionNamedStar_AndTheWholeTable_Each_AsTheyAre()
    {
        var store = new SqliteStorage<TestEntity>(database.Open());
        await store.CreateAsync(new TestEntity { Id = "*|a" }, Ct);
        await store.CreateAsync(new TestEntity { Id = "b|c" }, Ct);

        Assert.Equal(["*|a", "b|c"], (await store.QueryAsync((string?)null, Ct)).Select(e => e.Id));
        Assert.Equal(["*|a"], (await store.QueryAsync("*", Ct)).Select(e => e.Id));
    }

    // ── table names ───────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Accounts))]
    public async Task TheTableStore_RefusesANameWithATrailingLineFeed_AndTheReservedNameTables(string kind)
    {
        var tables = new RowStoreTableStore(Account(kind));

        foreach (var name in new[] { "abc\n", "abcdef\n", "tables", "Tables", "TABLES" })
        {
            var refused = await Assert.ThrowsAsync<StorageRequestRejectedException>(() => tables.CreateTableAsync(name, Ct));
            Assert.Equal("InvalidResourceName", refused.ProviderErrorCode);
            Assert.False(await tables.TableExistsAsync(name, Ct));
        }
        Assert.True(await tables.CreateTableAsync("abc", Ct));
        Assert.False(SqliteDatabase.IsTableName("abc\n"));
        Assert.True(SqliteDatabase.IsTableName("abc"));
    }

    // ── raw rows ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("PartitionKey")]
    [InlineData("RowKey")]
    [InlineData("Timestamp")]
    [InlineData("odata.etag")]
    public void ARawRow_RefusesACellNamedAsAColumnTheStoreSets(string name)
    {
        var refused = Assert.Throws<ArgumentException>(
            () => new RawRow("p", "r", new Dictionary<string, RawCell> { [name] = RawCell.FromString("zz") }));
        Assert.Equal("cells", refused.ParamName);
        _ = new RawRow("p", "r", new Dictionary<string, RawCell> { ["partitionkey"] = RawCell.FromString("a cell like any other") });
    }

    [Fact]
    public async Task ARawRow_WhoseCellsGainAReservedNameAfterConstruction_IsRefusedByEachWrite_BeforeAnythingIsSent()
    {
        var cells = new Dictionary<string, RawCell> { ["A"] = RawCell.FromString("a") };
        var row = new RawRow("p", "r", cells);
        cells["PartitionKey"] = RawCell.FromString("q");

        var table = new InMemoryAccount().Table("Rows");
        table.Create();
        var local = new RowStoreRawTable(table, "Rows", ensureTable: null);
        await Assert.ThrowsAsync<ArgumentException>(() => local.SubmitAsync([RawOperation.Insert(row)], Ct));
        Assert.Null(await table.GetAsync("p", "r", Ct));
        Assert.Null(await table.GetAsync("q", "r", Ct));

        var transport = new ScriptedTableTransport(_ => throw new InvalidOperationException("No request is expected."));
        var azure = new AzureTableStore(transport.Service()).GetTable("Raw");
        await Assert.ThrowsAsync<ArgumentException>(() => azure.SubmitAsync([RawOperation.Insert(row)], Ct));
        Assert.Empty(transport.Requests);
    }

    // A row store whose every commit loses to another writer.
    private sealed class AlwaysChangedRows(IRowStore inner) : IRowStore
    {
        public int Commits { get; private set; }

        public string TableName => inner.TableName;

        public ValueTask<StoredRow?> GetAsync(string partitionKey, string rowKey, CancellationToken ct) => inner.GetAsync(partitionKey, rowKey, ct);

        public ValueTask<IReadOnlyList<StoredRow>> ReadAsync(RowRange range, int limit, CancellationToken ct) => inner.ReadAsync(range, limit, ct);

        public ValueTask<RowCommitResult> CommitAsync(string partitionKey, IReadOnlyList<RowWrite> writes, CancellationToken ct)
        {
            Commits++;
            return new(RowCommitResult.Failed(RowCommitFailure.VersionMismatch, 0));
        }
    }

    [Fact]
    public async Task ARawMerge_ThatKeepsLosingItsCondition_FailsAsAConflict_InsteadOfRetryingForever()
    {
        var table = new InMemoryAccount().Table("Rows");
        table.Create();
        Assert.True((await table.CommitAsync("p", [RowWrite.Insert(new TableEntity("p", "r") { ["A"] = "a" })], Ct)).Committed);
        var rows = new AlwaysChangedRows(table);
        var raw = new RowStoreRawTable(rows, "Rows", ensureTable: null);

        // On a thread of its own: a merge that never stops would never return to the caller's thread.
        var merge = Task.Run(() => raw.SubmitAsync([RawOperation.Merge(new RawRow("p", "r", new Dictionary<string, RawCell> { ["B"] = RawCell.FromInt32(1) }))], Ct), Ct);
        var finished = await Task.WhenAny(merge, Task.Delay(TimeSpan.FromSeconds(30), Ct));

        Assert.Same(merge, finished);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => merge);
        Assert.True(rows.Commits > 1);
    }

    // ── keys longer than the service holds ────────────────────────────────

    [Theory]
    [MemberData(nameof(Accounts))]
    public async Task AKeyLongerThan512Characters_IsRefused_OnEveryWritePath(string kind)
    {
        var account = Account(kind);
        var store = Engine<TestEntity>(account);
        var tooLong = new string('k', TableKeyRules.MaxKeyLength + 1);

        await Assert.ThrowsAsync<StorageRequestRejectedException>(() => store.CreateAsync(new TestEntity { Id = "p|" + tooLong }, Ct));
        await Assert.ThrowsAsync<StorageRequestRejectedException>(() => store.UpsertAsync(new TestEntity { Id = tooLong + "|r" }, Ct));
        await Assert.ThrowsAsync<StorageRequestRejectedException>(() => store.CreateBatchAsync([new TestEntity { Id = "p|" + tooLong }], Ct));
        await Assert.ThrowsAsync<StorageRequestRejectedException>(() => store.UpdateAsync("p|" + tooLong, b => b.SetProperty(e => e.Name, "n"), Ct));
        Assert.True(store.TryGetCapability<IRawTable>(out var raw));
        await Assert.ThrowsAsync<StorageRequestRejectedException>(() => raw!.SubmitAsync([RawOperation.Insert(new RawRow("p", tooLong))], Ct));
        var polymorphic = new RowStorePolymorphicStorage<object>(account, "Events", null);
        await Assert.ThrowsAsync<StorageRequestRejectedException>(
            () => polymorphic.InsertAsync(PolymorphicWrite<object>.Marker(new TableKey("p", tooLong), new Dictionary<string, object> { ["_A"] = 1 }), Ct));
        Assert.Equal(0, await store.CountAsync(ct: Ct));

        var longest = new string('k', TableKeyRules.MaxKeyLength);
        await store.CreateAsync(new TestEntity { Id = "p|" + longest }, Ct);
        Assert.True(await store.ExistsAsync("p|" + longest, Ct));
    }

    // ── system columns of the polymorphic store ───────────────────────────

    [Theory]
    [MemberData(nameof(Accounts))]
    public async Task ASystemColumn_IsStoredAsTheServiceStoresIt(string kind)
    {
        var store = new RowStorePolymorphicStorage<object>(Account(kind), "Events", null);
        var key = new TableKey("p", "r");
        var instant = new DateTimeOffset(2024, 1, 2, 1, 4, 5, TimeSpan.Zero);

        await store.InsertAsync(PolymorphicWrite<object>.Marker(key, new Dictionary<string, object>
        {
            ["_A"] = 1, ["_N"] = null!, ["_Utc"] = instant.UtcDateTime, ["_At"] = instant.ToOffset(TimeSpan.FromHours(2)), ["_Bin"] = BinaryData.FromBytes(new byte[] { 7 }),
        }), Ct);
        var read = (await store.GetAsync(key, Ct))!;
        Assert.False(read.Columns.ContainsKey("_N"));
        Assert.Equal(instant, read.Column<DateTimeOffset>("_Utc"));
        Assert.Equal(TimeSpan.Zero, read.Column<DateTimeOffset>("_At").Offset);
        Assert.Equal([7], read.Column<byte[]>("_Bin"));

        // A merge of null leaves the cell; a merged DateTimeOffset is its instant in UTC.
        await store.MergeColumnsAsync(key, new Dictionary<string, object> { ["_A"] = null!, ["_M"] = instant.ToOffset(TimeSpan.FromHours(-3)) }, Ct);
        read = (await store.GetAsync(key, Ct))!;
        Assert.Equal(1, read.Column<int>("_A"));
        Assert.Equal(TimeSpan.Zero, read.Column<DateTimeOffset>("_M").Offset);
        Assert.DoesNotContain(read.Columns.Values, v => v is null);

        // A value that is not a table cell is refused, the same on each local provider, and nothing changes.
        foreach (var value in new object[] { 1.5m, 2.5f, 3UL, TimeSpan.FromSeconds(5), (short)1, DateTime.Now, new DateTime(2024, 1, 1), new[] { 1 } })
        {
            await Assert.ThrowsAsync<NotSupportedException>(
                () => store.InsertAsync(PolymorphicWrite<object>.Marker(new TableKey("p", "other"), new Dictionary<string, object> { ["_X"] = value }), Ct));
            await Assert.ThrowsAsync<NotSupportedException>(() => store.MergeColumnsAsync(key, new Dictionary<string, object> { ["_X"] = value }, Ct));
        }
        Assert.Equal(1, await store.CountAsync(ct: Ct));
        Assert.False((await store.GetAsync(key, Ct))!.Columns.ContainsKey("_X"));
    }

    // ── predicates on a nested member ─────────────────────────────────────

    [Theory]
    [MemberData(nameof(Accounts))]
    public async Task APredicateOnANestedMember_DoesNotMatchARowWhoseNestedObjectIsNull(string kind)
    {
        var store = Engine<NestedOwner>(Account(kind));
        await store.CreateAsync(new NestedOwner { Id = "p|set", Inner = new NestedPart { Text = "a", Number = 1 } }, Ct);
        await store.CreateAsync(new NestedOwner { Id = "p|none" }, Ct);
        await store.CreateAsync(new NestedOwner { Id = "p|other", Name = "n" }, Ct);

        Assert.Equal(["p|set"], (await store.QueryAsync(x => x.Inner!.Text == "a", ct: Ct)).Select(e => e.Id));
        Assert.Empty(await store.QueryAsync(x => x.Inner!.Number == 0, ct: Ct));
        Assert.True(await store.AnyAsync(x => x.Inner!.Number > 0, ct: Ct));
        Assert.Equal(["p|other", "p|set"], (await store.QueryAsync(x => x.Name == "n" || x.Inner!.Number == 1, ct: Ct)).Select(e => e.Id));
        var streamed = new List<string>();
        await foreach (var entity in store.QueryStreamAsync(x => x.Inner!.Text == "a", ct: Ct))
            streamed.Add(entity.Id);
        Assert.Equal(["p|set"], streamed);
    }

    // ── values the service cannot hold ────────────────────────────────────

    [Theory]
    [MemberData(nameof(Accounts))]
    public async Task AnInfiniteDouble_IsRefusedBeforeAnythingIsWritten(string kind)
    {
        var account = Account(kind);
        var store = Engine<CompareEntity>(account);
        foreach (var value in new[] { double.PositiveInfinity, double.NegativeInfinity })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(new CompareEntity { Id = "p|r", D = value }, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(new CompareEntity { Id = "p|r", D = value }, Ct));
            Assert.True(store.TryGetCapability<IRawTable>(out var raw));
            await Assert.ThrowsAsync<ArgumentException>(
                () => raw!.SubmitAsync([RawOperation.Insert(new RawRow("p", "raw", new Dictionary<string, RawCell> { ["D"] = RawCell.FromDouble(value) }))], Ct));
            var polymorphic = new RowStorePolymorphicStorage<object>(account, "Events", null);
            await Assert.ThrowsAsync<ArgumentException>(
                () => polymorphic.InsertAsync(PolymorphicWrite<object>.Marker(new TableKey("p", "r"), new Dictionary<string, object> { ["_D"] = value }), Ct));
        }
        Assert.Equal(0, await store.CountAsync(ct: Ct));
        // NaN is a number the service stores.
        await store.CreateAsync(new CompareEntity { Id = "p|nan", D = double.NaN }, Ct);
        Assert.True(double.IsNaN((await store.OneAsync("p|nan", Ct))!.D));
    }

    // ── projection ────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Accounts))]
    public async Task ASelectEntry_NamesTheColumnsTheServiceProjects_TrimmedAndSplitAtCommas(string kind)
    {
        var store = Engine<CompareEntity>(Account(kind));
        await store.CreateAsync(new CompareEntity { Id = "p|r", Name = "n1", N = 5 }, Ct);

        var spaced = Assert.Single(await store.QueryAsync(new QueryOptions { Partition = "p", Select = ["Name "] }, Ct));
        Assert.Equal(("n1", 0), (spaced.Name, spaced.N));
        var joined = Assert.Single(await store.QueryAsync(new QueryOptions { Partition = "p", Select = ["Name,N"] }, Ct));
        Assert.Equal(("n1", 5), (joined.Name, joined.N));
    }

    // ── a partial update of a large value ─────────────────────────────────

    [Theory]
    [MemberData(nameof(Accounts))]
    public async Task APartialUpdateOfALargeValue_IsRefusedWhetherOrNotTheRowExists(string kind)
    {
        var store = Engine<LeftoverRaceDoc>(Account(kind));

        await Assert.ThrowsAsync<NotSupportedException>(() => store.UpdateAsync("p|missing", b => b.SetProperty(x => x.Body, "x"), Ct));
        await store.CreateAsync(new LeftoverRaceDoc { Id = "p|stored", Body = "small" }, Ct);
        await Assert.ThrowsAsync<NotSupportedException>(() => store.UpdateAsync("p|stored", b => b.SetProperty(x => x.Body, "x"), Ct));
        Assert.Equal("small", (await store.OneAsync("p|stored", Ct))!.Body);
    }
}

/// <summary>An entity with a double and a few plain columns.</summary>
public class CompareEntity : Entity
{
    public string? Name { get; set; }

    public int N { get; set; }

    public double D { get; set; }
}

/// <summary>The Azure provider against the same read-cache finding, on Azurite.</summary>
public sealed class AzureStarPartitionCacheTests : IAsyncLifetime
{
    private static readonly Lazy<Task<bool>> Probe =
        new(() => AzuriteProbe.ProbeAsync(AzuriteConnection.ConnectionString, blobs: false, AzuriteProbe.Required));

    private readonly string table = "Star" + Guid.NewGuid().ToString("N");
    private TableServiceClient service = default!;
    private bool available;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        available = await Probe.Value;
        if (available)
            service = new TableServiceClient(AzuriteConnection.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (available)
            await service.DeleteTableAsync(table);
    }

    [Fact]
    public async Task APartitionNamedStar_IsCachedApartFromTheWholeTable()
    {
        Assert.SkipUnless(available, "Azurite is not running (" + AzuriteConnection.Variable + " overrides UseDevelopmentStorage=true)");
        var store = new TableStorage<TestEntity>(service, new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<TestEntity>>.Instance,
            authorizer: null, new UnifiedTableStorageOptions { TableNameResolver = _ => table });
        await store.CreateAsync(new TestEntity { Id = "*|a" }, Ct);
        await store.CreateAsync(new TestEntity { Id = "b|c" }, Ct);

        Assert.Equal(["*|a"], (await store.QueryAsync("*", Ct)).Select(e => e.Id));
        Assert.Equal(["*|a", "b|c"], (await store.QueryAsync((string?)null, Ct)).Select(e => e.Id));
        await store.CreateAsync(new TestEntity { Id = "b|d" }, Ct);
        Assert.Equal(["*|a", "b|c", "b|d"], (await store.QueryAsync((string?)null, Ct)).Select(e => e.Id));
        Assert.Equal(["*|a"], (await store.QueryAsync("*", Ct)).Select(e => e.Id));
    }

    [Fact]
    public async Task APolymorphicInsert_WithANullSystemColumn_ReturnsAnEntryWithoutIt_AsARead()
    {
        Assert.SkipUnless(available, "Azurite is not running (" + AzuriteConnection.Variable + " overrides UseDevelopmentStorage=true)");
        await service.CreateTableIfNotExistsAsync(table, Ct);
        var store = new PolymorphicTableStorage<object>(service, table, NullLogger<PolymorphicTableStorage<object>>.Instance);
        var key = new TableKey("p", "r");

        var inserted = await store.InsertAsync(PolymorphicWrite<object>.Marker(key, new Dictionary<string, object> { ["_A"] = 1, ["_N"] = null! }), Ct);

        Assert.False(inserted.Columns.ContainsKey("_N"));
        Assert.Equal(1, inserted.Column<int>("_A"));
        Assert.False((await store.GetAsync(key, Ct))!.Columns.ContainsKey("_N"));
    }
}
