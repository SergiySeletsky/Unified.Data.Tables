using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.NeutralFailures;

/// <summary>A type with a large value, so the split-aware read and delete paths are the ones taken.</summary>
public sealed class MissingTableSplitDoc : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }
}

/// <summary>
/// A table deleted behind a live Azure store, through the real SDK on Azurite. The SDK returns, rather
/// than throws, the 404 of a point read and of a delete, whatever its code, so a store that only catches
/// exceptions read a deleted table as a missing row (null, false, a no-op, <c>NotFound</c>) while every
/// local provider throws <see cref="TableNotFoundException"/>. Every member is
/// <see cref="TableNotFoundException"/> now, the point operations included.
/// </summary>
public sealed class AzuriteMissingTableTests : IAsyncLifetime
{
    private static readonly string ConnectionString = AzuriteConnection.ConnectionString;

    private static readonly Lazy<Task<bool>> Probe =
        new(() => AzuriteProbe.ProbeAsync(ConnectionString, blobs: false, AzuriteProbe.Required));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string table = "MissingTbl" + Guid.NewGuid().ToString("N")[..16];
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

    private TableStorage<T> Store<T>() where T : class, IEntity, new() =>
        new(service, new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<T>>.Instance,
            authorizer: null, options: new UnifiedTableStorageOptions { TableNameResolver = _ => table });

    private async Task<TableStorage<T>> StoreOverADeletedTableAsync<T>(T first) where T : class, IEntity, new()
    {
        var store = Store<T>();
        await store.CreateAsync(first, Ct);
        await service.DeleteTableAsync(table, Ct);
        return store;
    }

    private static void AssertTableMissing(Exception? thrown, string tableName, string member)
    {
        var missing = Assert.IsType<TableNotFoundException>(thrown);
        Assert.Equal(tableName, missing.TableName);
        Assert.Equal("TableNotFound", missing.ProviderErrorCode);
        var inner = Assert.IsType<Azure.RequestFailedException>(missing.InnerException);
        Assert.True(inner.Status == 404, member);
    }

    [Fact]
    public async Task ThePointMembersOfAPlainType_ReportADeletedTable_AsTableNotFound()
    {
        Assert.SkipUnless(available, "Azurite is not running");
        var store = await StoreOverADeletedTableAsync(new TestEntity { Id = "p|written", Name = "n" });

        var members = new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
        {
            ["one"] = () => store.OneAsync("p|absent", Ct),
            ["exists"] = () => store.ExistsAsync("p|absent", Ct),
            ["delete"] = () => store.DeleteAsync("p|absent", Ct),
            ["delete if exists"] = () => store.DeleteIfExistsAsync("p|absent", Ct),
            ["delete if match"] = () => store.DeleteIfMatchAsync("p|absent", "W/\"datetime'2024-01-01T00%3A00%3A00Z'\"", Ct),
        };
        foreach (var (member, call) in members)
            AssertTableMissing(await Record.ExceptionAsync(call), table, member);
    }

    [Fact]
    public async Task ThePointMembersOfASplitType_ReportADeletedTable_AsTableNotFound()
    {
        Assert.SkipUnless(available, "Azurite is not running");
        var store = await StoreOverADeletedTableAsync(new MissingTableSplitDoc { Id = "p|written", Body = "b" });

        var members = new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
        {
            ["one"] = () => store.OneAsync("p|absent", Ct),
            ["exists"] = () => store.ExistsAsync("p|absent", Ct),
            ["delete"] = () => store.DeleteAsync("p|absent", Ct),
            ["delete if exists"] = () => store.DeleteIfExistsAsync("p|absent", Ct),
        };
        foreach (var (member, call) in members)
            AssertTableMissing(await Record.ExceptionAsync(call), table, member);
    }

    [Fact]
    public async Task APolymorphicGetAndDelete_ReportADeletedTable_AsTableNotFound()
    {
        Assert.SkipUnless(available, "Azurite is not running");
        var store = new PolymorphicTableStorage<TestMessage>(service, table, NullLogger<PolymorphicTableStorage<TestMessage>>.Instance);
        await store.InsertAsync(new PolymorphicWrite<TestMessage>(new TableKey("p", "written"), new TestCreatedEvent { Id = "e" }), Ct);
        await service.DeleteTableAsync(table, Ct);

        AssertTableMissing(await Record.ExceptionAsync(() => store.GetAsync(new TableKey("p", "absent"), Ct)), table, "get");
        AssertTableMissing(await Record.ExceptionAsync(() => store.DeleteAsync(new TableKey("p", "absent"), Ct)), table, "delete");
    }

    [Fact]
    public async Task AMissingRowOfATableThatExists_IsStillAMissingRow()
    {
        Assert.SkipUnless(available, "Azurite is not running");
        var store = Store<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|written", Name = "n" }, Ct);
        var polymorphic = new PolymorphicTableStorage<TestMessage>(service, table, NullLogger<PolymorphicTableStorage<TestMessage>>.Instance);

        Assert.Null(await store.OneAsync("p|absent", Ct));
        Assert.False(await store.ExistsAsync("p|absent", Ct));
        await store.DeleteAsync("p|absent", Ct);
        // NotFound, or Indeterminate on a client without the library's retry observation (this one).
        Assert.NotEqual(DeleteStatus.Deleted, await store.DeleteIfExistsAsync("p|absent", Ct));
        Assert.Null(await polymorphic.GetAsync(new TableKey("p", "absent"), Ct));
        await polymorphic.DeleteAsync(new TableKey("p", "absent"), Ct);
    }
}
