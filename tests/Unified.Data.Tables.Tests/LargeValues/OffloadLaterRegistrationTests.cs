using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A second <c>AddUnifiedTableStorage</c> call. The first call's options are the registered ones and a
/// later call changes none of them, with the one exception pinned here: when the registered options set
/// <see cref="UnifiedTableStorageOptions.BlobOffload"/> and no call has supplied the offload blob client
/// yet, a later call that can supply one does (its own <see cref="BlobOffloadOptions.ServiceClient"/>, the
/// host's registered client when it sets <see cref="BlobOffloadOptions.UseRegisteredServiceClient"/>, or
/// the client its connection string or endpoint derives). Before, such a client was dropped and every
/// store of a large-value type failed to resolve.
/// </summary>
[Collection("OversizedCellPolicy")]
public class OffloadLaterRegistrationTests
{
    private const string DevelopmentStorage = "UseDevelopmentStorage=true";
    private const string TableOnly = "TableEndpoint=https://acct.table.cosmos.azure.com:443/;SharedAccessSignature=sv=2020&sig=abc";
    private const string MalformedBlobEndpoint =
        "BlobEndpoint=not a uri;AccountName=acct;AccountKey=YWJj;TableEndpoint=https://acct.table.core.windows.net/";

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(Arg.Any<string>()).Returns(Substitute.For<TableClient>());
        services.AddSingleton(service);
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

    // ── a later call supplies the client the registered options lack ──────

    [Fact]
    public void LaterConnectionStringCall_SuppliesTheClient_TheFirstCallCouldNotDerive()
    {
        var services = Services();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions());
        services.AddUnifiedTableStorage(DevelopmentStorage);
        using var provider = services.BuildServiceProvider();

        Assert.Equal("devstoreaccount1", OffloadClient(provider)!.AccountName);
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Null(provider.GetService<BlobServiceClient>()); // still private to the library
    }

    [Fact]
    public void LaterCredentialCall_SuppliesTheClient_TheFirstCallCouldNotDerive()
    {
        var services = Services();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions());
        services.AddUnifiedTableStorage(new Uri("https://acct.table.core.windows.net"), new DummyCredential());
        using var provider = services.BuildServiceProvider();

        Assert.Equal(new Uri("https://acct.blob.core.windows.net"), OffloadClient(provider)!.Uri);
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Null(provider.GetService<BlobServiceClient>());
    }

    [Fact]
    public void LaterCall_SuppliesItsOwnServiceClient_AndTheRegisteredOptionsStillNameTheContainer()
    {
        var services = Services();
        var later = Recording();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { ContainerName = "first" });
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { ContainerName = "second", ServiceClient = later });
        using var provider = services.BuildServiceProvider();

        Assert.Same(later, OffloadClient(provider));
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        later.Received(1).GetBlobContainerClient("first");
        later.DidNotReceive().GetBlobContainerClient("second");
    }

    [Fact]
    public void LaterCall_ThatSetsUseRegisteredServiceClient_UsesTheHostsClient()
    {
        var services = Services();
        var host = Recording();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { ContainerName = "values" });
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { UseRegisteredServiceClient = true });
        services.AddSingleton(host); // resolved lazily, so the host may register it after both calls
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        host.Received(1).GetBlobContainerClient("values");
    }

    [Fact]
    public void LaterCall_PrefersItsServiceClient_OverTheHostsAndTheDerivedOne()
    {
        var services = Services();
        var explicitClient = Recording();
        var host = Recording();
        services.AddSingleton(host);
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { ContainerName = "values" });
        services.AddUnifiedTableStorage(DevelopmentStorage, o => o.BlobOffload = new BlobOffloadOptions
        {
            ServiceClient = explicitClient,
            UseRegisteredServiceClient = true,
        });
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        explicitClient.Received(1).GetBlobContainerClient("values");
        Assert.Empty(host.ReceivedCalls());
    }

    [Fact]
    public void LaterCall_PrefersTheHostsClient_OverTheDerivedOne_WhenItSaysSo()
    {
        var services = Services();
        var host = Recording();
        services.AddSingleton(host);
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { ContainerName = "values" });
        services.AddUnifiedTableStorage(DevelopmentStorage, o => o.BlobOffload = new BlobOffloadOptions { UseRegisteredServiceClient = true });
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        host.Received(1).GetBlobContainerClient("values");
    }

    [Fact]
    public void LaterCall_ThatCannotSupplyOne_LeavesTheClientToTheNextCall()
    {
        var services = Services();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions());
        services.AddUnifiedTableStorage(TableOnly);
        services.AddUnifiedTableStorage();
        Assert.DoesNotContain(services, d => d.IsKeyedService && d.ServiceType == typeof(BlobServiceClient));

        services.AddUnifiedTableStorage(DevelopmentStorage);
        using var provider = services.BuildServiceProvider();

        Assert.Equal("devstoreaccount1", OffloadClient(provider)!.AccountName);
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
    }

    [Fact]
    public void LaterConnectionStringCall_MalformedBlobEndpoint_FailsRegistration_WhileTheClientIsMissing()
    {
        var services = Services();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions());

        var ex = Assert.Throws<ArgumentException>(() => services.AddUnifiedTableStorage(MalformedBlobEndpoint));

        Assert.Contains("BlobOffloadOptions.ServiceClient", ex.Message, StringComparison.Ordinal);
        Assert.Equal("connectionString", ex.ParamName);
    }

    // ── everything else stays with the first registration ─────────────────

    [Fact]
    public void LaterCall_DoesNotReplaceAnExplicitClient()
    {
        var services = Services();
        var first = Recording();
        var later = Recording();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { ContainerName = "values", ServiceClient = first });
        services.AddUnifiedTableStorage(DevelopmentStorage, o => o.BlobOffload = new BlobOffloadOptions { ServiceClient = later });
        using var provider = services.BuildServiceProvider();

        Assert.Same(first, OffloadClient(provider));
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        first.Received(1).GetBlobContainerClient("values");
        Assert.Empty(later.ReceivedCalls());
    }

    [Fact]
    public void LaterCall_DoesNotReplaceADerivedClient()
    {
        var services = Services();
        var later = Recording();
        services.AddUnifiedTableStorage(DevelopmentStorage, o => o.BlobOffload = new BlobOffloadOptions());
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { ServiceClient = later, UseRegisteredServiceClient = true });
        using var provider = services.BuildServiceProvider();

        Assert.Equal("devstoreaccount1", OffloadClient(provider)!.AccountName);
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Empty(later.ReceivedCalls());
    }

    [Fact]
    public void LaterCall_DoesNotOverrideTheHostsClient_TheFirstCallChose()
    {
        var services = Services();
        var host = Recording();
        var later = Recording();
        services.AddSingleton(host);
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { ContainerName = "values", UseRegisteredServiceClient = true });
        services.AddUnifiedTableStorage(DevelopmentStorage, o => o.BlobOffload = new BlobOffloadOptions { ServiceClient = later });
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        host.Received(1).GetBlobContainerClient("values");
        Assert.Empty(later.ReceivedCalls());
    }

    [Fact]
    public void LaterCall_DoesNotFillIn_WhenTheFirstCallChoseTheHostsClient_AndTheHostHasNone()
    {
        // The first call's choice (the host's client) is a registration under the library's key, so it
        // stands: a later call does not swap in another client behind it.
        var services = Services();
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { UseRegisteredServiceClient = true });
        services.AddUnifiedTableStorage(DevelopmentStorage, o => o.BlobOffload = new BlobOffloadOptions { ServiceClient = Recording() });
        using var provider = services.BuildServiceProvider();

        Assert.Null(OffloadClient(provider));
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
    }

    [Fact]
    public void LaterCall_WhenTheRegisteredOptionsHaveNoBlobOffload_RegistersNoClient_AndItsOwnOptionsAreIgnored()
    {
        var services = Services();
        var later = Recording();
        services.AddUnifiedTableStorage();
        services.AddUnifiedTableStorage(DevelopmentStorage, o => o.BlobOffload = new BlobOffloadOptions { ServiceClient = later });
        services.AddUnifiedTableStorage(new Uri("https://acct.table.core.windows.net"), new DummyCredential(),
            o => o.BlobOffload = new BlobOffloadOptions { UseRegisteredServiceClient = true });
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetRequiredService<UnifiedTableStorageOptions>().BlobOffload);
        Assert.Null(OffloadClient(provider));
        Assert.Null(provider.GetService<BlobServiceClient>());
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Empty(later.ReceivedCalls());
    }

    [Fact]
    public void LaterCall_LeavesTheRegisteredOptionsObjectAsItWas()
    {
        var services = Services();
        UnifiedTableStorageOptions? first = null;
        services.AddUnifiedTableStorage(o =>
        {
            first = o;
            o.BlobOffload = new BlobOffloadOptions { ContainerName = "first", ThresholdBytes = 1_234 };
        });
        services.AddUnifiedTableStorage(DevelopmentStorage, o =>
        {
            o.IdNormalization = IdNormalization.AsWritten;
            o.BlobOffload = new BlobOffloadOptions { ContainerName = "second", ServiceClient = Recording(), UseRegisteredServiceClient = true };
        });
        using var provider = services.BuildServiceProvider();

        var registered = provider.GetRequiredService<UnifiedTableStorageOptions>();
        Assert.Same(first, registered);
        Assert.Equal(IdNormalization.Normalized, registered.IdNormalization);
        Assert.Equal("first", registered.BlobOffload!.ContainerName);
        Assert.Equal(1_234, registered.BlobOffload.ThresholdBytes);
        Assert.Null(registered.BlobOffload.ServiceClient);
        Assert.False(registered.BlobOffload.UseRegisteredServiceClient);
        Assert.Single(services, d => d.ServiceType == typeof(UnifiedTableStorageOptions));
        Assert.Single(services, d => d.IsKeyedService && d.ServiceType == typeof(BlobServiceClient));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaterCall_NeverTakesTheHostsClient_WithoutUseRegisteredServiceClient(bool laterCallDerivesOne)
    {
        var services = Services();
        var host = Recording();
        services.AddSingleton(host);
        services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions());
        if (laterCallDerivesOne)
            services.AddUnifiedTableStorage(DevelopmentStorage, o => o.BlobOffload = new BlobOffloadOptions());
        else
            services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        Assert.Same(host, provider.GetRequiredService<BlobServiceClient>());
        Assert.Single(provider.GetServices<BlobServiceClient>());
        Assert.NotSame(host, OffloadClient(provider));
        if (laterCallDerivesOne)
            Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        else
            Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Empty(host.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterConnectionStringCall_MalformedBlobEndpoint_RegistersAsBefore_WhenNoClientIsNeeded(bool offloadWithAClient)
    {
        // Nothing asks this call for a blob client (offload is off, or an earlier call supplied one), so
        // its string's blob endpoint is not even built.
        var services = Services();
        var first = Recording();
        if (offloadWithAClient)
            services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions { ServiceClient = first });
        else
            services.AddUnifiedTableStorage();

        services.AddUnifiedTableStorage(MalformedBlobEndpoint, o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        Assert.Same(offloadWithAClient ? first : null, OffloadClient(provider));
        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
    }

    [Fact]
    public void KeyedOptionsOfTheHost_AreNotReadAsTheRegisteredOptions()
    {
        // Only the unkeyed options are the ones TableStorage<T> receives; a keyed descriptor's instance
        // cannot even be read through the unkeyed accessor.
        var services = Services();
        services.AddKeyedSingleton("tenant", new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });

        services.AddUnifiedTableStorage(DevelopmentStorage);
        services.AddUnifiedTableStorage(DevelopmentStorage);
        using var provider = services.BuildServiceProvider();

        Assert.Null(OffloadClient(provider));
        Assert.Null(provider.GetRequiredService<UnifiedTableStorageOptions>().BlobOffload);
        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
    }

    // ── in-memory parity ──────────────────────────────────────────────────

    [Fact]
    public async Task InMemory_ASecondRegistration_KeepsTheFirstOptions_AndALargeValueStoreStillResolves()
    {
        // The in-memory store needs no blob client, so a second call has nothing to contribute; like the
        // Azure store, a large-value type resolves and offloads under the first call's options.
        var services = new ServiceCollection();
        services.AddUnifiedInMemoryStorage(o => o.BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 });
        services.AddUnifiedInMemoryStorage(o => o.BlobOffload = new BlobOffloadOptions { ServiceClient = Recording() });
        using var provider = services.BuildServiceProvider();
        var body = Ascii(40_000);

        var store = Assert.IsType<InMemoryStorage<SplitTextEntity>>(provider.GetRequiredService<IStorage<SplitTextEntity>>());
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = body }, TestContext.Current.CancellationToken);

        Assert.Equal(1_000, provider.GetRequiredService<UnifiedTableStorageOptions>().BlobOffload!.ThresholdBytes);
        Assert.Single(store.Blobs.Names);
        Assert.Equal(body, (await store.OneAsync("p|r", TestContext.Current.CancellationToken))!.Body, StringComparer.Ordinal);
    }
}
