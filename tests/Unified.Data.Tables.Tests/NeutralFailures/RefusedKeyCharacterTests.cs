using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.NeutralFailures;

/// <summary>
/// A key with a <c>/</c>, which the service refuses, is answered the same on every provider: a point read
/// finds nothing, a delete deletes nothing, a write is <see cref="StorageRequestRejectedException"/> (400 <c>InvalidInput</c>). Sent in
/// the URL of a point request, a <c>/</c> changed its address, and Azurite answered with a 500 that read as
/// a transient failure that may have committed.
/// </summary>
public sealed class RefusedKeyCharacterTests : IAsyncLifetime
{
    private static readonly string ConnectionString = AzuriteConnection.ConnectionString;

    private static readonly Lazy<Task<bool>> Probe =
        new(() => AzuriteProbe.ProbeAsync(ConnectionString, blobs: false, AzuriteProbe.Required));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string table = "RefusedKeys" + Guid.NewGuid().ToString("N")[..16];
    private TableServiceClient service = default!;
    private bool available;

    public async ValueTask InitializeAsync()
    {
        available = await Probe.Value;
        if (available)
            service = new TableServiceClient(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (available)
            await service.DeleteTableAsync(table);
    }

    // A '/' only: the other refused characters are escaped in the URL and compared by InMemoryFidelityTests.
    public static TheoryData<string> Ids => new() { "p|a/b", "p/q|r", "p/q|a/b" };

    private static async Task<string[]> OutcomesAsync(IStorage<TestEntity> store, string id)
    {
        async Task<string> Outcome(Func<Task<string>> call)
        {
            try
            {
                return await call();
            }
            catch (StorageException ex)
            {
                return $"{ex.GetType().Name}({ex.ProviderErrorCode})";
            }
        }

        await store.CreateAsync(new TestEntity { Id = "p|warm" }, Ct); // the table exists
        return
        [
            await Outcome(async () => (await store.OneAsync(id, Ct)) is null ? "null" : "row"),
            await Outcome(async () => (await store.ExistsAsync(id, Ct)).ToString()),
            await Outcome(async () => { await store.DeleteAsync(id, Ct); return "ok"; }),
            await Outcome(async () => (await store.DeleteIfExistsAsync(id, Ct)).ToString()),
            await Outcome(async () => { await store.UpsertAsync(new TestEntity { Id = id }, Ct); return "ok"; }),
            await Outcome(async () => { await store.CreateAsync(new TestEntity { Id = id }, Ct); return "ok"; }),
            await Outcome(async () => { await store.UpdateAsync(new TestEntity { Id = id }, ConcurrencyMode.LastWriterWins, Ct); return "ok"; }),
            await Outcome(async () => await store.UpdateAsync(id, b => b.SetProperty(e => e.Name, "n"), Ct)),
        ];
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task EveryPointMember_AnswersARefusedKey_AsTheLocalProvidersDo(string id)
    {
        Assert.SkipUnless(available, "Azurite is not running");
        var azure = new TableStorage<TestEntity>(service, new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<TestEntity>>.Instance,
            authorizer: null, options: new UnifiedTableStorageOptions { TableNameResolver = _ => table });

        var local = await OutcomesAsync(new InMemoryStorage<TestEntity>(), id);
        var remote = await OutcomesAsync(azure, id);

        Assert.Equal(local, remote);
        Assert.Equal("null", remote[0]);
        Assert.Equal("False", remote[1]);
        Assert.Equal("ok", remote[2]);
        Assert.Equal("NotFound", remote[3]);
        Assert.All(remote[4..], outcome => Assert.Equal("StorageRequestRejectedException(InvalidInput)", outcome));
    }

    [Fact]
    public async Task APolymorphicStore_AnswersARefusedKey_AsTheLocalProvidersDo()
    {
        Assert.SkipUnless(available, "Azurite is not running");
        var azure = new PolymorphicTableStorage<TestMessage>(service, table, NullLogger<PolymorphicTableStorage<TestMessage>>.Instance);
        var key = new TableKey("p", "a/b");

        foreach (IPolymorphicStorage<TestMessage> store in new IPolymorphicStorage<TestMessage>[] { new InMemoryPolymorphicStorage<TestMessage>(table), azure })
        {
            Assert.Null(await store.GetAsync(key, Ct));
            await store.DeleteAsync(key, Ct);
            var upsert = await Assert.ThrowsAsync<StorageRequestRejectedException>(
                () => store.UpsertAsync(new PolymorphicWrite<TestMessage>(key, new TestCreatedEvent { Id = "e" }), Ct));
            Assert.Equal("InvalidInput", upsert.ProviderErrorCode);
            var merge = await Assert.ThrowsAsync<StorageRequestRejectedException>(
                () => store.MergeColumnsAsync(key, new Dictionary<string, object> { ["_IsPublished"] = true }, Ct));
            Assert.Equal("InvalidInput", merge.ProviderErrorCode);
        }
    }
}
