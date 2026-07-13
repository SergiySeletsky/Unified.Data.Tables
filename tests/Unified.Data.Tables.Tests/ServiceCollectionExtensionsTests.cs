using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// DI wiring via <see cref="ServiceCollectionExtensions.AddUnifiedTableStorage(IServiceCollection)"/>:
/// the open-generic <see cref="IStorage{T}"/> mapping, the memory cache, the connection-string
/// overload, and argument validation.
/// </summary>
// AddUnifiedTableStorage applies options.OversizedCells to the STATIC serializer policy, so this
// class shares the collection with every other policy-sensitive test to avoid parallel races.
[Collection("OversizedCellPolicy")]
public class ServiceCollectionExtensionsTests
{
    private static ServiceCollection ServicesWithMockedClient()
    {
        var services = new ServiceCollection();
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(Arg.Any<string>()).Returns(Substitute.For<TableClient>());
        services.AddSingleton(service);
        // AddUnifiedTableStorage leaves logging to the host; register a no-op logger so the
        // open-generic TableStorage<T> can be constructed without pulling in the logging runtime.
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    [Fact]
    public void AddUnifiedTableStorage_RegistersOpenGenericStorage()
    {
        var services = ServicesWithMockedClient();
        services.AddUnifiedTableStorage();
        using var provider = services.BuildServiceProvider();

        var storage = provider.GetService<IStorage<TestEntity>>();

        Assert.NotNull(storage);
        Assert.IsType<TableStorage<TestEntity>>(storage);
    }

    [Fact]
    public void AddUnifiedTableStorage_RegistersMemoryCache()
    {
        var services = ServicesWithMockedClient();
        services.AddUnifiedTableStorage();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IMemoryCache>());
    }

    [Fact]
    public void AddUnifiedTableStorage_StorageIsSingleton()
    {
        var services = ServicesWithMockedClient();
        services.AddUnifiedTableStorage();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetService<IStorage<TestEntity>>();
        var second = provider.GetService<IStorage<TestEntity>>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddUnifiedTableStorage_WithConnectionString_RegistersTableServiceClientAndCache()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddUnifiedTableStorage("UseDevelopmentStorage=true");
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<TableServiceClient>());
        Assert.NotNull(provider.GetService<IMemoryCache>());
    }

    [Fact]
    public void AddUnifiedTableStorage_RegistersConfiguredOptions()
    {
        var services = ServicesWithMockedClient();

        services.AddUnifiedTableStorage(o => o.Cache = CachePolicy.Absolute(TimeSpan.FromSeconds(30)));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<UnifiedTableStorageOptions>();
        Assert.Equal(CacheExpirationMode.Absolute, options.Cache.Mode);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Cache.Ttl);
    }

    [Fact]
    public void AddUnifiedTableStorage_WithTokenCredential_RegistersTableServiceClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var credential = Substitute.For<Azure.Core.TokenCredential>();

        services.AddUnifiedTableStorage(new Uri("https://account.table.core.windows.net"), credential);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<TableServiceClient>());
        Assert.NotNull(provider.GetService<IStorage<TestEntity>>());
    }

    [Fact]
    public void AddUnifiedTableStorage_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddUnifiedTableStorage(null!));
    }

    [Fact]
    public void AddUnifiedTableStorage_NullConnectionString_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddUnifiedTableStorage((string)null!));
    }

    [Fact]
    public void AddUnifiedTableStorage_WhitespaceConnectionString_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddUnifiedTableStorage("   "));
    }
}
