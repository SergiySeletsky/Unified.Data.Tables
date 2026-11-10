using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// DI wiring for blob offload. The offload client is resolved only when
/// <see cref="UnifiedTableStorageOptions.BlobOffload"/> is set and never occupies the host's unkeyed
/// <see cref="BlobServiceClient"/> slot: it is <see cref="BlobOffloadOptions.ServiceClient"/>, else the host's
/// registered client when <see cref="BlobOffloadOptions.UseRegisteredServiceClient"/> is set, else the one the
/// connection-string or credential overload derives (registered under an internal key). Without one, only
/// large-value-aware types fail to resolve.
/// </summary>
[Collection("OversizedCellPolicy")]
public class OffloadRegistrationTests
{
    private static ServiceCollection Services(bool tableClient = true)
    {
        var services = new ServiceCollection();
        if (tableClient)
        {
            var service = Substitute.For<TableServiceClient>();
            service.GetTableClient(Arg.Any<string>()).Returns(Substitute.For<TableClient>());
            services.AddSingleton(service);
        }
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    private sealed class DummyCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private static BlobServiceClient? OffloadClient(IServiceProvider provider) =>
        provider.GetKeyedService<BlobServiceClient>(ServiceCollectionExtensions.OffloadBlobServiceClientKey);

    private static BlobServiceClient Recording()
    {
        var blobs = Substitute.For<BlobServiceClient>();
        blobs.GetBlobContainerClient(Arg.Any<string>()).Returns(Substitute.For<BlobContainerClient>());
        return blobs;
    }

    private static TableServiceClient TableService()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(Arg.Any<string>()).Returns(Substitute.For<TableClient>());
        return service;
    }

    [Fact]
    public void ConnectionStringOverload_WithoutOffload_RegistersNoBlobClient()
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage("UseDevelopmentStorage=true");
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<BlobServiceClient>());
        Assert.Null(OffloadClient(provider));
    }

    [Fact]
    public void ConnectionStringOverload_WithOffload_RegistersOffloadClientOnlyUnderItsKey()
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage("UseDevelopmentStorage=true", o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<BlobServiceClient>());
        Assert.Equal("devstoreaccount1", OffloadClient(provider)!.AccountName);
    }

    [Theory]
    [InlineData("TableEndpoint=https://acct.table.cosmos.azure.com:443/;SharedAccessSignature=sv=2020&sig=abc")]
    [InlineData("TableEndpoint=https://acct.table.core.windows.net/;SharedAccessSignature=sv=2020&sig=abc")]
    public void ConnectionStringOverload_NoBlobEndpointDerivable_RegistersNoBlobClient_AndPlainTypesResolve(string connectionString)
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage(connectionString, o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<BlobServiceClient>());
        Assert.Null(OffloadClient(provider));
        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
    }

    [Theory]
    [InlineData("BlobEndpoint=not a uri;AccountName=acct;AccountKey=YWJj")]
    [InlineData("BlobEndpoint=ftp//broken;SharedAccessSignature=sv=2020&sig=abc")]
    public void ConnectionStringOverload_MalformedBlobEndpoint_WithOffload_FailsRegistration(string connectionString)
    {
        var services = Services(tableClient: false);

        var ex = Assert.Throws<ArgumentException>(
            () => services.AddUnifiedTableStorage(connectionString, o => o.BlobOffload = new BlobOffloadOptions()));

        Assert.Contains("BlobOffloadOptions.ServiceClient", ex.Message, StringComparison.Ordinal);
        Assert.Equal("connectionString", ex.ParamName);
    }

    [Theory]
    [InlineData("BlobEndpoint=not a uri;AccountName=acct;AccountKey=YWJj;TableEndpoint=https://acct.table.core.windows.net/")]
    [InlineData("BlobEndpoint=ftp//broken;TableEndpoint=https://acct.table.core.windows.net/;SharedAccessSignature=sv=2020&sig=abc")]
    public void ConnectionStringOverload_MalformedBlobEndpoint_WithoutOffload_RegistersAsBefore(string connectionString)
    {
        var services = Services();

        services.AddUnifiedTableStorage(connectionString);
        using var provider = services.BuildServiceProvider();

        Assert.Null(OffloadClient(provider));
        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
    }

    [Theory]
    [InlineData("UseDevelopmentStorage=true", true)]
    [InlineData("usedevelopmentstorage=TRUE", true)]
    [InlineData("UseDevelopmentStorage=false;TableEndpoint=https://t.example.com/", false)]
    [InlineData("AccountName=acct;AccountKey=YWJj", true)]
    [InlineData("AccountName=acct;AccountKey=YWJj;TableEndpoint=https://acct.table.core.windows.net/", true)]
    [InlineData("AccountName=acct;AccountKey=YWJj;TableEndpoint=https://acct.table.cosmos.azure.com:443/", false)]
    [InlineData("AccountName=;TableEndpoint=https://acct.table.core.windows.net/;SharedAccessSignature=sv=2020", false)]
    [InlineData("BlobEndpoint=;TableEndpoint=https://acct.table.core.windows.net/;SharedAccessSignature=sv=2020", false)]
    [InlineData("BlobEndpoint=https://acct.blob.core.windows.net/;SharedAccessSignature=sv=2020", true)]
    [InlineData("TableEndpoint=https://acct.table.core.windows.net/;SharedAccessSignature=sv=2020", false)]
    public void HasBlobEndpoint_ParsesTheConnectionString(string connectionString, bool expected)
    {
        Assert.Equal(expected, ServiceCollectionExtensions.HasBlobEndpoint(connectionString));
    }

    [Fact]
    public void Constructors_BindAsBefore()
    {
        var service = TableService();
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var logger = NullLogger<TableStorage<FileEntity>>.Instance;
        var options = new UnifiedTableStorageOptions();

        // The source forms existing callers use still compile and bind.
        Assert.NotNull(new TableStorage<FileEntity>(service, cache, logger));
        Assert.NotNull(new TableStorage<FileEntity>(service, cache, logger, authorizer: null));
        Assert.NotNull(new TableStorage<FileEntity>(service, cache, logger, options: options));
        Assert.NotNull(new TableStorage<FileEntity>(service, cache, logger, null, options));
        Assert.Throws<InvalidOperationException>(() =>
            new TableStorage<FileEntity>(service, cache, logger, null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() }));
    }

    [Fact]
    public void Constructor_OffloadWithoutClient_LeavesPlainTypesUnaffected()
    {
        // "Types with neither are unaffected": only a large-value-aware type needs the blob client.
        var service = TableService();
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var options = new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() };

        Assert.NotNull(new TableStorage<PlainEntity>(service, cache, NullLogger<TableStorage<PlainEntity>>.Instance, null, options));
        Assert.NotNull(new TableStorage<PlainEntity>(service, cache, NullLogger<TableStorage<PlainEntity>>.Instance, null, options, blobServiceClient: null));
        Assert.Throws<InvalidOperationException>(() =>
            new TableStorage<FileEntity>(service, cache, NullLogger<TableStorage<FileEntity>>.Instance, null, options));
    }

    [Fact]
    public void Constructor_UsesTheOptionsServiceClient()
    {
        var service = TableService();
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var blobs = Recording();
        var options = new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { ContainerName = "values", ServiceClient = blobs } };

        Assert.NotNull(new TableStorage<FileEntity>(service, cache, NullLogger<TableStorage<FileEntity>>.Instance, null, options));
        blobs.Received(1).GetBlobContainerClient("values");
    }

    [Fact]
    public void Constructor_AnExplicitClientArgument_WinsOverTheOptionsServiceClient()
    {
        var service = TableService();
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var inOptions = Recording();
        var argument = Recording();
        var options = new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { ContainerName = "values", ServiceClient = inOptions } };

        Assert.NotNull(new TableStorage<FileEntity>(service, cache, NullLogger<TableStorage<FileEntity>>.Instance, null, options, argument));
        argument.Received(1).GetBlobContainerClient("values");
        Assert.Empty(inOptions.ReceivedCalls());
    }

    [Fact]
    public void CredentialOverload_WithOffload_DerivesBlobEndpoint_UnderItsKey()
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage(new Uri("https://acct.table.core.windows.net"), new DummyCredential(),
            o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<BlobServiceClient>());
        Assert.Equal(new Uri("https://acct.blob.core.windows.net"), OffloadClient(provider)!.Uri);
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
    }

    [Fact]
    public void CredentialOverload_WithoutOffload_RegistersNoBlobClient()
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage(new Uri("https://acct.table.core.windows.net"), new DummyCredential());
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<BlobServiceClient>());
        Assert.Null(OffloadClient(provider));
    }

    [Theory]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=YWJj;TableEndpoint=https://acct.table.cosmos.azure.com:443/;")]
    [InlineData("AccountName=acct;AccountKey=YWJj;TableEndpoint=https://tables.example.com/;")]
    public void ConnectionStringOverload_CosmosOrCustomTableHost_RegistersNoBlobClient_AndOffloadFailsClearly(string connectionString)
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage(connectionString, o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        Assert.Null(OffloadClient(provider));
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Contains(nameof(BlobServiceClient), ex.Message, StringComparison.Ordinal);
        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
    }

    [Fact]
    public void ConnectionStringOverload_AzureStorageTableEndpoint_RegistersBlobClient()
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage(
            "AccountName=acct;AccountKey=YWJj;TableEndpoint=https://acct.table.core.windows.net/;BlobEndpoint=https://acct.blob.core.windows.net/",
            o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        Assert.Equal(new Uri("https://acct.blob.core.windows.net/"), OffloadClient(provider)!.Uri);
    }

    [Theory]
    [InlineData("https://acct.table.cosmos.azure.com")]
    [InlineData("https://acct.table.example.com")]
    [InlineData("https://localhost:10002/devstoreaccount1")]
    public void CredentialOverload_CosmosOrNonStorageHost_RegistersNoBlobClient(string endpoint)
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage(new Uri(endpoint), new DummyCredential(), o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<BlobServiceClient>());
        Assert.Null(OffloadClient(provider));
    }

    [Fact]
    public void CredentialOverload_SovereignCloud_DerivesBlobEndpoint()
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage(new Uri("https://acct.table.core.chinacloudapi.cn"), new DummyCredential(),
            o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        Assert.Equal(new Uri("https://acct.blob.core.chinacloudapi.cn"), OffloadClient(provider)!.Uri);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HostBlobClient_RegisteredBeforeOrAfter_StaysTheHosts_AndIsNotUsedForOffload(bool after)
    {
        var services = Services(tableClient: false);
        var mine = Recording();
        if (!after) services.AddSingleton(mine);
        services.AddUnifiedTableStorage("UseDevelopmentStorage=true", o => o.BlobOffload = new BlobOffloadOptions());
        if (after) services.AddSingleton(mine);
        using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<BlobServiceClient>());
        Assert.Single(provider.GetServices<BlobServiceClient>());
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Empty(mine.ReceivedCalls());
        Assert.NotSame(mine, OffloadClient(provider));
    }

    [Fact]
    public void HostBlobClient_IsUsed_WhenTheOptionsSaySo()
    {
        var services = Services();
        var mine = Recording();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { ContainerName = "values", UseRegisteredServiceClient = true });
        services.AddSingleton(mine); // resolved lazily, so registration order does not matter
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        mine.Received(1).GetBlobContainerClient("values");
    }

    [Fact]
    public void HostBlobClient_WithoutTheOption_IsNotUsed_AndOffloadFailsClearly()
    {
        var services = Services();
        var mine = Recording();
        services.AddSingleton(mine);
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Contains(nameof(BlobOffloadOptions.UseRegisteredServiceClient), ex.Message, StringComparison.Ordinal);
        Assert.Empty(mine.ReceivedCalls());
        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
    }

    [Fact]
    public void UseRegisteredServiceClient_WithNoneRegistered_FailsOnlyForLargeValueTypes()
    {
        var services = Services();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { UseRegisteredServiceClient = true });
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
    }

    [Fact]
    public void ExplicitServiceClient_IsUsed_AndWinsOverTheHostsAndTheDerivedOne()
    {
        var services = Services(tableClient: false);
        var explicitClient = Recording();
        var hostClient = Recording();
        services.AddSingleton(hostClient);
        services.AddUnifiedTableStorage("UseDevelopmentStorage=true", o => o.BlobOffload = new BlobOffloadOptions
        {
            ContainerName = "values",
            ServiceClient = explicitClient,
            UseRegisteredServiceClient = true,
        });
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        explicitClient.Received(1).GetBlobContainerClient("values");
        Assert.Empty(hostClient.ReceivedCalls());
    }

    [Fact]
    public void BlobOffloadWithoutClient_ThrowsAtResolve_OnlyForLargeValueTypes()
    {
        var services = Services();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Contains(nameof(BlobServiceClient), ex.Message, StringComparison.Ordinal);
        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
    }

    [Fact]
    public void PlainHost_NoBlobOffload_ResolvesAsBefore()
    {
        var services = Services();
        services.AddUnifiedTableStorage();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
        Assert.Null(provider.GetService<BlobServiceClient>());
    }

    [Fact]
    public void BlobClientWithoutBlobOffload_IsNotUsed()
    {
        var services = Services();
        var blobs = Recording();
        services.AddSingleton(blobs);
        services.AddUnifiedTableStorage();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Empty(blobs.ReceivedCalls());
    }
}
