using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Two stores of one entity type over one <see cref="IMemoryCache"/> share its entries only when they read
/// and write the same table of the same account. A store of another table, or of the same table name in
/// another account, never answers a point read, an existence check or a partition query with a row the
/// other store cached (review finding dyn-cache, pre-existing since the cache keys named the type alone).
/// Two stores of the same table still share their entries, so a write through one is seen by the other.
/// </summary>
public sealed class SharedCacheStoreIsolationTests : AzuriteCleanupTestBase
{
    private const string Rk = "a";

    private readonly string tableA = "scia" + Guid.NewGuid().ToString("N");
    private readonly string tableB = "scib" + Guid.NewGuid().ToString("N");

    private TableStorage<T> CachingStore<T>(MemoryCache cache, string table) where T : class, IEntity, new()
    {
        var options = new UnifiedTableStorageOptions { TableNameResolver = _ => table };
        return new TableStorage<T>(TableService, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, options);
    }

    private async Task InOwnTablesAsync(Func<Task> test)
    {
        try
        {
            await test();
        }
        finally
        {
            await TableService.DeleteTableAsync(tableA, CancellationToken.None);
            await TableService.DeleteTableAsync(tableB, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoTables_OverOneCache_NeverServeEachOthersRows(bool largeValues)
    {
        SkipUnlessAvailable();
        await InOwnTablesAsync(() => largeValues ? TwoTablesAsync<RaceDoc>() : TwoTablesAsync<RacePlainDoc>());
    }

    private async Task TwoTablesAsync<T>() where T : Entity, IRaceDoc, new()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var a = CachingStore<T>(cache, tableA);
        var b = CachingStore<T>(cache, tableB);
        var id = Id(Rk);

        await a.CreateAsync(new T { Id = id, Body = "from A", Version = 1 }, Ct);
        // A has cached its entity and (after this query) its partition's list; B's table has no row yet.
        Assert.Single(await a.QueryAsync(Pk, Ct));
        Assert.Null(await b.OneAsync(id, Ct));
        Assert.False(await b.ExistsAsync(id, Ct));
        Assert.Empty(await b.QueryAsync(Pk, Ct));

        await b.CreateAsync(new T { Id = id, Body = "from B", Version = 2 }, Ct);
        Assert.Single(await b.QueryAsync(Pk, Ct));

        Assert.Equal("from A", (await a.OneAsync(id, Ct))!.Body);
        Assert.Equal("from B", (await b.OneAsync(id, Ct))!.Body);
        Assert.Equal("from A", Assert.Single(await a.QueryAsync(Pk, Ct)).Body);
        Assert.Equal("from B", Assert.Single(await b.QueryAsync(Pk, Ct)).Body);

        await a.DeleteAsync(id, Ct);
        Assert.Null(await a.OneAsync(id, Ct));
        Assert.Equal("from B", (await b.OneAsync(id, Ct))!.Body);
    }

    // Two stores of the SAME table over one cache share its entries, as before: a write through one evicts
    // what the other cached, so the other's next read answers the new version.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoStoresOfOneTable_OverOneCache_StillShareTheirEntries(bool largeValues)
    {
        SkipUnlessAvailable();
        await InOwnTablesAsync(() => largeValues ? OneTableAsync<RaceDoc>() : OneTableAsync<RacePlainDoc>());
    }

    private async Task OneTableAsync<T>() where T : Entity, IRaceDoc, new()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var a = CachingStore<T>(cache, tableA);
        // The same table, spelled in another case: Azure table names are case-insensitive.
        var b = CachingStore<T>(cache, tableA.ToUpperInvariant());
        var id = Id(Rk);

        await a.CreateAsync(new T { Id = id, Body = "v1", Version = 1 }, Ct);
        Assert.Equal("v1", (await b.OneAsync(id, Ct))!.Body);
        Assert.Equal("v1", Assert.Single(await b.QueryAsync(Pk, Ct)).Body);

        await a.UpsertAsync(new T { Id = id, Body = "v2", Version = 2 }, Ct);
        Assert.Equal("v2", (await b.OneAsync(id, Ct))!.Body);
        Assert.Equal("v2", Assert.Single(await b.QueryAsync(Pk, Ct)).Body);
    }

    // The same table name in two accounts (two service endpoints) is two tables.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneTableName_InTwoAccounts_OverOneCache_NeverServeEachOthersRows(bool largeValues)
    {
        if (largeValues)
            await TwoAccountsAsync<RaceDoc>();
        else
            await TwoAccountsAsync<RacePlainDoc>();
    }

    private static async Task TwoAccountsAsync<T>() where T : Entity, IRaceDoc, new()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var tableOfA = new RacingTableClient();
        var tableOfB = new RacingTableClient();
        var a = StoreOver<T>(tableOfA, new Uri("https://accounta.table.core.windows.net/"), cache);
        var b = StoreOver<T>(tableOfB, new Uri("https://accountb.table.core.windows.net/"), cache);
        const string id = "p|a";

        await a.CreateAsync(new T { Id = id, Body = "from A", Version = 1 }, Ct);
        Assert.Single(await a.QueryAsync("p", Ct));
        Assert.Null(await b.OneAsync(id, Ct));
        Assert.False(await b.ExistsAsync(id, Ct));
        Assert.Empty(await b.QueryAsync("p", Ct));
    }

    private static TableStorage<T> StoreOver<T>(RacingTableClient table, Uri account, IMemoryCache cache) where T : class, IEntity, new()
    {
        var service = Substitute.For<TableServiceClient>();
        service.Uri.Returns(account);
        service.GetTableClient(typeof(T).Name).Returns(table);
        return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, null,
            (Unified.Data.Tables.Providers.IOffloadBlobStore?)null);
    }
}
