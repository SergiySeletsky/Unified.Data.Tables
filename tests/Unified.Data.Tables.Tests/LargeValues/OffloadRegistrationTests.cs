using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// DI wiring for blob offload: which overloads register a <see cref="BlobServiceClient"/>, that the
/// container picks the constructor that takes it, and that offload without a client fails at resolve.
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

    [Fact]
    public void ConnectionStringOverload_RegistersBlobServiceClient()
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage("UseDevelopmentStorage=true");
        using var provider = services.BuildServiceProvider();

        var blobs = provider.GetService<BlobServiceClient>();

        Assert.NotNull(blobs);
        Assert.Equal("devstoreaccount1", blobs!.AccountName);
    }

    [Fact]
    public void ConnectionStringOverload_TableOnlySas_RegistersNoBlobClient_AndStillResolves()
    {
        // A blob client cannot be built from this string; an existing host using it must resolve as before.
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage("TableEndpoint=https://acct.table.cosmos.azure.com:443/;SharedAccessSignature=sv=2020&sig=abc");
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<BlobServiceClient>());
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
    }

    [Fact]
    public void Constructors_BindAsBefore()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(Arg.Any<string>()).Returns(Substitute.For<TableClient>());
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
    public void CredentialOverload_DerivesBlobEndpoint()
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage(new Uri("https://acct.table.core.windows.net"), new DummyCredential());
        using var provider = services.BuildServiceProvider();

        var blobs = provider.GetService<BlobServiceClient>();

        Assert.NotNull(blobs);
        Assert.Equal(new Uri("https://acct.blob.core.windows.net"), blobs!.Uri);
    }

    [Fact]
    public void CustomHost_RegistersNoBlobClient()
    {
        var services = Services(tableClient: false);
        services.AddUnifiedTableStorage(new Uri("https://localhost:10002/devstoreaccount1"), new DummyCredential());
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<BlobServiceClient>());
    }

    [Fact]
    public void ExistingBlobClient_IsNotReplaced()
    {
        var services = Services(tableClient: false);
        var mine = Substitute.For<BlobServiceClient>();
        services.AddSingleton(mine);
        services.AddUnifiedTableStorage("UseDevelopmentStorage=true");
        using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetService<BlobServiceClient>());
    }

    [Fact]
    public void DiResolvesSixParameterCtor_WithBlobClient()
    {
        var services = Services();
        var blobs = Substitute.For<BlobServiceClient>();
        blobs.GetBlobContainerClient(Arg.Any<string>()).Returns(Substitute.For<BlobContainerClient>());
        services.AddSingleton(blobs);
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { ContainerName = "values" });
        using var provider = services.BuildServiceProvider();

        var storage = provider.GetRequiredService<IStorage<FileEntity>>();

        Assert.IsType<TableStorage<FileEntity>>(storage);
        blobs.Received(1).GetBlobContainerClient("values");
    }

    [Fact]
    public void BlobOffloadWithoutClient_ThrowsAtResolve()
    {
        var services = Services();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Contains(nameof(BlobServiceClient), ex.Message, StringComparison.Ordinal);
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
        var blobs = Substitute.For<BlobServiceClient>();
        services.AddSingleton(blobs);
        services.AddUnifiedTableStorage();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Empty(blobs.ReceivedCalls());
    }
}
