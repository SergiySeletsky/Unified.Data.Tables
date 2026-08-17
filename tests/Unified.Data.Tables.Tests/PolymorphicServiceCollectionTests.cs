using Azure;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
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

    [Fact]
    public async Task AddUnifiedInMemoryPolymorphicTable_Configure_AppliesTheDiscriminator_WithNoOtherRegistration()
    {
        // The exact host shape that used to silently default: ONLY the polymorphic fake is
        // registered, so sp.GetService<UnifiedTableStorageOptions>() returns null and the store fell
        // back to assembly-qualified tokens while production used a map. Nothing failed; the tokens
        // just differed, invisibly.
        var services = new ServiceCollection();
        services.AddUnifiedInMemoryPolymorphicTable<TestMessage>(
            "StateEventStore",
            o => o.TypeDiscriminator = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created"));

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("StateEventStore");

        var entry = await store.InsertAsync(
            new TableKey("p", "r"), new TestCreatedEvent { Id = "e1" }, TestContext.Current.CancellationToken);

        Assert.Equal("created", entry.Discriminator);
    }

    [Fact]
    public async Task AddUnifiedInMemoryPolymorphicTable_NamesTheTableInItsDuplicateKeyError()
    {
        var services = new ServiceCollection();
        services.AddUnifiedInMemoryPolymorphicTable<TestMessage>("StateEventStore");

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("StateEventStore");
        await store.InsertAsync(new TableKey("p", "r"), new TestCommand { Id = "c1" },
            TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<DuplicateKeyException>(() => store.InsertAsync(
            new TableKey("p", "r"), new TestCommand { Id = "c1" }, TestContext.Current.CancellationToken));

        Assert.Equal("StateEventStore", ex.EntityType);
    }

    [Fact]
    public async Task AddUnifiedPolymorphicTable_Configure_AppliesThatDiscriminatorToTheStore()
    {
        var table = Substitute.For<TableClient>();
        table.CreateIfNotExistsAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response<TableItem>>(null!));
        TableEntity? written = null;
        table.AddEntityAsync(Arg.Do<TableEntity>(e => written = e), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response>(new FakeResponse()));

        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient("StateEventStore").Returns(table);

        var services = new ServiceCollection();
        services.AddSingleton(service);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddUnifiedTableStorage();
        services.AddUnifiedPolymorphicTable<TestMessage>(
            "StateEventStore",
            o => o.TypeDiscriminator = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created"));

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("StateEventStore");

        await store.InsertAsync(new TableKey("p", "r"), new TestCreatedEvent { Id = "e1" },
            TestContext.Current.CancellationToken);

        Assert.Equal("created", written![SystemColumnNames.TypeName]);
    }

    [Fact]
    public void AddUnifiedPolymorphicTable_Configure_DoesNotBecomeTheProcessWideOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<TableServiceClient>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddUnifiedTableStorage();
        services.AddUnifiedPolymorphicTable<TestMessage>(
            "StateEventStore",
            o => o.TypeDiscriminator = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created"));

        using var provider = services.BuildServiceProvider();

        // One table's type map must not silently become the default every IStorage<T> resolves.
        Assert.Null(provider.GetRequiredService<UnifiedTableStorageOptions>().TypeDiscriminator);
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
