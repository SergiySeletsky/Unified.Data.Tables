using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Registration-time cache policies (<see cref="CachePolicy"/> via
/// <see cref="UnifiedTableStorageOptions"/>): disabling turns every read into a storage hit —
/// the correct posture for a process that shares its tables with other writers — and per-type
/// overrides beat the global default.
/// </summary>
public class CachePolicyTests
{
    private static UnifiedTableStorageOptions DisabledCache() =>
        new() { Cache = CachePolicy.Disabled };

    [Fact]
    public void CachePolicy_Factories_ProduceExpectedShapes()
    {
        Assert.False(CachePolicy.Disabled.Enabled);
        Assert.True(CachePolicy.Sliding(TimeSpan.FromMinutes(5)) is
            { Enabled: true, Mode: CacheExpirationMode.Sliding, Ttl.TotalMinutes: 5 });
        Assert.True(CachePolicy.Absolute(TimeSpan.FromSeconds(30)) is
            { Enabled: true, Mode: CacheExpirationMode.Absolute, Ttl.TotalSeconds: 30 });
    }

    [Fact]
    public async Task DisabledCache_OneAsync_AlwaysHitsStorage()
    {
        using var h = new StorageHarness<TestEntity>(options: DisabledCache());
        h.SetupGet("pk", "rk", Mocks.Row("pk", "rk"));

        await h.Store.OneAsync("pk|rk");
        await h.Store.OneAsync("pk|rk");

        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>(
            "pk", "rk", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledCache_QueryAsync_AlwaysHitsStorage()
    {
        using var h = new StorageHarness<TestEntity>(options: DisabledCache());
        h.SetupQueryAll(Mocks.Row("pk", "rk"));

        await h.Store.QueryAsync();
        await h.Store.QueryAsync();

        h.Table.Received(2).QueryAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledCache_CreateThenRead_DoesNotServeFromCache()
    {
        using var h = new StorageHarness<TestEntity>(options: DisabledCache());
        h.SetupAdd();
        h.SetupGet("cached1", "cached1", Mocks.Row("cached1", "cached1"));

        await h.Store.CreateAsync(new TestEntity { Id = "cached1", Name = "x" });
        await h.Store.OneAsync("cached1");

        // With caching disabled the read after the create still goes to storage.
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledCache_UpdateAsync_UsesWildcardETag()
    {
        using var h = new StorageHarness<TestEntity>(options: DisabledCache());
        h.SetupUpdate();

        await h.Store.UpdateAsync(new TestEntity { Id = "p|r", Name = "x" });

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), ETag.All, TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerTypeOverride_BeatsGlobalDefault()
    {
        var options = new UnifiedTableStorageOptions().CacheFor<TestEntity>(CachePolicy.Disabled);
        using var h = new StorageHarness<TestEntity>(options: options);
        h.SetupGet("pk", "rk", Mocks.Row("pk", "rk"));

        await h.Store.OneAsync("pk|rk");
        await h.Store.OneAsync("pk|rk");

        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>(
            "pk", "rk", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GlobalDefault_AppliesToTypesWithoutOverride()
    {
        // Same options instance, but OtherCachedEntity has no override → global (enabled) default.
        var options = new UnifiedTableStorageOptions().CacheFor<TestEntity>(CachePolicy.Disabled);
        using var h = new StorageHarness<SimpleEntity>(options: options);
        h.SetupGet("pk", "rk", Mocks.Row("pk", "rk"));

        await h.Store.OneAsync("pk|rk");
        await h.Store.OneAsync("pk|rk");

        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(
            "pk", "rk", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CacheFor_NullPolicy_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UnifiedTableStorageOptions().CacheFor<TestEntity>(null!));
    }
}
