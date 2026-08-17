using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the registration shape. The case that drives it: two stores over the SAME base type and
/// different tables, which an open-generic registration cannot express.
/// </summary>
public class PolymorphicServiceCollectionTests
{
    [Fact]
    public void AddUnifiedPolymorphicTable_TwoTablesOneBaseType_ResolveIndependently()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<TableServiceClient>());
        // No AddLogging() overload is referenced anywhere in this solution (only
        // Microsoft.Extensions.Logging.Abstractions is a package dependency, not the concrete
        // Microsoft.Extensions.Logging package that defines it) and this task may not add a package
        // reference, so register the same no-op ILogger<> mapping ServiceCollectionExtensionsTests
        // already uses to satisfy PolymorphicTableStorage<TBase>'s ILogger<> constructor parameter.
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddUnifiedTableStorage();
        services.AddUnifiedPolymorphicTable<TestMessage>("StateEventStore");
        services.AddUnifiedPolymorphicTable<TestMessage>("TransactionStore");

        using var provider = services.BuildServiceProvider();

        var stateEvents = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("StateEventStore");
        var transactions = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("TransactionStore");

        Assert.NotSame(stateEvents, transactions);
    }

    [Fact]
    public void AddUnifiedPolymorphicTable_ResolvesToTheAzureImplementation()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<TableServiceClient>());
        // No AddLogging() overload is referenced anywhere in this solution (only
        // Microsoft.Extensions.Logging.Abstractions is a package dependency, not the concrete
        // Microsoft.Extensions.Logging package that defines it) and this task may not add a package
        // reference, so register the same no-op ILogger<> mapping ServiceCollectionExtensionsTests
        // already uses to satisfy PolymorphicTableStorage<TBase>'s ILogger<> constructor parameter.
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddUnifiedTableStorage();
        services.AddUnifiedPolymorphicTable<TestMessage>("CommandStore");

        using var provider = services.BuildServiceProvider();

        Assert.IsType<PolymorphicTableStorage<TestMessage>>(
            provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("CommandStore"));
    }

    [Fact]
    public void AddUnifiedPolymorphicTable_IsASingletonPerKey()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<TableServiceClient>());
        // No AddLogging() overload is referenced anywhere in this solution (only
        // Microsoft.Extensions.Logging.Abstractions is a package dependency, not the concrete
        // Microsoft.Extensions.Logging package that defines it) and this task may not add a package
        // reference, so register the same no-op ILogger<> mapping ServiceCollectionExtensionsTests
        // already uses to satisfy PolymorphicTableStorage<TBase>'s ILogger<> constructor parameter.
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddUnifiedTableStorage();
        services.AddUnifiedPolymorphicTable<TestMessage>("CommandStore");

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("CommandStore"),
            provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("CommandStore"));
    }

    [Fact]
    public void AddUnifiedInMemoryPolymorphicTable_MirrorsTheAzureRegistration()
    {
        var services = new ServiceCollection();
        services.AddUnifiedInMemoryStorage();
        services.AddUnifiedInMemoryPolymorphicTable<TestMessage>("StateEventStore");
        services.AddUnifiedInMemoryPolymorphicTable<TestMessage>("TransactionStore");

        using var provider = services.BuildServiceProvider();

        var stateEvents = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("StateEventStore");
        var transactions = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("TransactionStore");

        Assert.IsType<InMemoryPolymorphicStorage<TestMessage>>(stateEvents);
        Assert.NotSame(stateEvents, transactions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddUnifiedPolymorphicTable_BlankTableName_Throws(string? tableName)
    {
        var services = new ServiceCollection();

        Assert.ThrowsAny<ArgumentException>(
            () => services.AddUnifiedPolymorphicTable<TestMessage>(tableName!));
    }
}
