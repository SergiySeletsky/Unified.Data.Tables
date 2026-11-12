using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>A large-value aware entity of the ExistsAsync tests.</summary>
public class ExistsDamageDoc : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// <c>ExistsAsync</c> of a type with large values over a row whose value lies in the row's own cells
/// (CACHE-5 of the cache lens, the same defect as ROWS-1 of the rows lens, fix-wave-3 review). Existence
/// needs no value: a row whose value cannot be verified (a cell damaged outside the library) answers
/// <c>true</c> on both stores and nothing is cached for it; reads of it keep throwing
/// <see cref="TableValueIntegrityException"/> and <c>DeleteAsync</c> removes it. An intact in-row value is
/// still cached and read back whole.
/// </summary>
public class ExistsOfADamagedInRowValueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableStorage<ExistsDamageDoc> NewStore(RacingTableClient table, IMemoryCache cache)
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(nameof(ExistsDamageDoc)).Returns(table);
        return new TableStorage<ExistsDamageDoc>(service, cache, NullLogger<TableStorage<ExistsDamageDoc>>.Instance);
    }

    // The reproduction of the review (renamed), extended: nothing is cached for the damaged row (the next
    // read asks the table and throws), DeleteAsync removes it, and ExistsAsync then answers false.
    [Theory]
    [InlineData("Body__sha256")]
    [InlineData("Body__c3")]
    [InlineData("Body__chunks")]
    public async Task Exists_OfARowWhoseInRowValueIsDamaged_IsTrue_AndCachesNothing(string damagedCell)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore(table, cache);
        await store.UpsertAsync(new ExistsDamageDoc { Id = "p|a", Body = Ascii(150_000), Version = 1 }, Ct);
        var row = table.Stored("p", "a")!;
        Assert.False(row.ContainsKey("_SplitParts")); // the value is in the row itself
        row[damagedCell] = damagedCell == "Body__chunks" ? 4 : damagedCell == "Body__sha256" ? new string('0', 64) : "damaged";
        await table.UpsertEntityAsync(row, TableUpdateMode.Replace, Ct);
        cache.Compact(1.0);

        var thrown = await Record.ExceptionAsync(async () => Assert.True(await store.ExistsAsync("p|a", Ct)));
        Assert.Null(thrown);

        var reads = table.Count(r => r.IsWholeRowPointRead);
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => store.OneAsync("p|a", Ct));
        Assert.True(table.Count(r => r.IsWholeRowPointRead) > reads); // not served from the cache
        Assert.True(await store.ExistsAsync("p|a", Ct));

        await store.DeleteAsync("p|a", Ct);
        Assert.False(await store.ExistsAsync("p|a", Ct));
        Assert.Null(table.Stored("p", "a"));
    }

    // Control: an intact in-row value is cached by ExistsAsync, and the entity then served is whole.
    [Fact]
    public async Task Exists_OfAnIntactInRowValue_CachesTheWholeEntity()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore(table, cache);
        var body = Ascii(150_000);
        await store.UpsertAsync(new ExistsDamageDoc { Id = "p|a", Body = body, Version = 1 }, Ct);
        cache.Compact(1.0);

        Assert.True(await store.ExistsAsync("p|a", Ct));
        var reads = table.Count(r => r.IsWholeRowPointRead);
        var served = await store.OneAsync("p|a", Ct);
        Assert.Equal(reads, table.Count(r => r.IsWholeRowPointRead)); // from the cache
        Assert.Equal(body, served!.Body);
        Assert.Equal(table.Stored("p", "a")!.ETag.ToString(), served.ETag);
    }

    // The in-memory store answers the same for the same damaged row.
    [Theory]
    [InlineData("Body__sha256")]
    [InlineData("Body__c3")]
    [InlineData("Body__chunks")]
    public async Task InMemory_Exists_OfARowWhoseInRowValueIsDamaged_IsTrue(string damagedCell)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await NewStore(table, cache).UpsertAsync(new ExistsDamageDoc { Id = "p|a", Body = Ascii(150_000), Version = 1 }, Ct);
        var row = table.Stored("p", "a")!;
        row[damagedCell] = damagedCell == "Body__chunks" ? 4 : damagedCell == "Body__sha256" ? new string('0', 64) : "damaged";

        var memory = new InMemoryStorage<ExistsDamageDoc>();
        memory.SeedPhysicalRow(row);

        Assert.True(await memory.ExistsAsync("p|a", Ct));
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => memory.OneAsync("p|a", Ct));
        await memory.DeleteAsync("p|a", Ct);
        Assert.False(await memory.ExistsAsync("p|a", Ct));
    }
}

/// <summary>
/// ROWS-1 of the fix-wave-3 review (the same defect as CACHE-5): <c>ExistsAsync</c>, <c>OneAsync</c> and
/// <c>DeleteAsync</c> of a row whose in-row split value is damaged answer the same on
/// <see cref="InMemoryStorage{T}"/> and on <see cref="TableStorage{T}"/> over Azurite.
/// </summary>
public sealed class ExistsOfADamagedInRowValueParityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> RunAsync(string kind, Func<TableEntity, TableEntity> damage)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind, offload: false);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores!.Store<SplitTextEntity>();
        // A value split over cells of the main row only (no continuation row).
        await store.CreateAsync(new SplitTextEntity { Id = stores.Id("r"), Body = new string('q', 100_000), Title = "t" }, Ct);
        var row = (await stores.RawRowsAsync<SplitTextEntity>()).Single(r => r.RowKey == "r");
        var damaged = damage(row);
        if (store is InMemoryStorage<SplitTextEntity> memory)
            memory.SeedPhysicalRow(damaged);
        else
            await new TableServiceClient(TestSupport.AzuriteConnection.ConnectionString).GetTableClient(nameof(SplitTextEntity)).UpsertEntityAsync(damaged, TableUpdateMode.Replace, Ct);

        var lines = new List<string>();
        async Task Step(string name, Func<Task<object?>> f)
        {
            try { lines.Add($"{name}: {await f() ?? "ok"}"); }
            catch (Exception ex) { lines.Add($"{name}: threw {ex.GetType().Name}"); }
        }
        var s = stores.Store<SplitTextEntity>();
        await Step("cells", () => Task.FromResult<object?>(string.Join(",", row.Keys.Where(k => k.StartsWith("Body", StringComparison.Ordinal)).Take(4))));
        await Step("exists", async () => await s.ExistsAsync(stores.Id("r"), Ct));
        await Step("one", async () => (await s.OneAsync(stores.Id("r"), Ct))?.Title);
        await Step("delete", async () => { await s.DeleteAsync(stores.Id("r"), Ct); return null; });
        await Step("exists after", async () => await s.ExistsAsync(stores.Id("r"), Ct));
        return string.Join(" | ", lines);
    }

    private static TableEntity CorruptHash(TableEntity row)
    {
        var copy = new TableEntity(row.PartitionKey, row.RowKey);
        foreach (var (k, v) in row)
        {
            if (k is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag") continue;
            copy[k] = v;
        }
        foreach (var k in copy.Keys.Where(k => k.EndsWith("__sha256", StringComparison.Ordinal)).ToList())
            copy[k] = new string('0', 64);
        foreach (var k in copy.Keys.Where(k => k.EndsWith("__c0", StringComparison.Ordinal)).ToList())
            copy.Remove(k);
        return copy;
    }

    [Fact]
    public async Task Exists_OfARowWhoseInRowSplitValueIsDamaged_AnswersTheSameOnBothStores()
    {
        var table = await RunAsync(LargeValueStores.Azurite, CorruptHash);
        var memory = await RunAsync(LargeValueStores.Memory, CorruptHash);
        Assert.True(table == memory, $"\nTableStorage:    {table}\nInMemoryStorage: {memory}");
        Assert.Contains("exists: True", table, StringComparison.Ordinal);
        Assert.Contains("one: threw TableValueIntegrityException", table, StringComparison.Ordinal);
        Assert.Contains("exists after: False", table, StringComparison.Ordinal);
    }
}

/// <summary>Control of ROWS-1 on Azurite: an intact in-row split value cached by ExistsAsync is read back whole.</summary>
public sealed class ExistsCachesAnIntactInRowValueAzuriteTests : AzuriteCleanupTestBase
{
    [Fact]
    public async Task ExistsAsync_CachesAnInRowSplitValue_ThatOneAsyncThenReturnsWhole()
    {
        SkipUnlessAvailable();
        Table(nameof(SplitTextEntity));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TableStorage<SplitTextEntity>(TableService, cache, NullLogger<TableStorage<SplitTextEntity>>.Instance,
            authorizer: null, new UnifiedTableStorageOptions());
        var body = string.Concat(Enumerable.Range(0, 100_000).Select(i => (char)('a' + (i % 26))));
        await SplitOnlyStore<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = Id("r"), Body = body, Title = "t" }, Ct);
        Assert.True(await store.ExistsAsync(Id("r"), Ct));
        var cells = string.Join(",", (await RawRowsAsync(nameof(SplitTextEntity)))[0].Keys.Where(k => k.StartsWith("Body", StringComparison.Ordinal)));
        var read = await store.OneAsync(Id("r"), Ct);
        Assert.True(read?.Body == body, $"cells [{cells}]; body length after ExistsAsync then OneAsync: {read?.Body?.Length}");
    }
}
