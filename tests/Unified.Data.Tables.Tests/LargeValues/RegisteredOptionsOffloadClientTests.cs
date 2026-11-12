using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The blob client a store uses is the one the documented order gives when it is applied to the options the
/// store receives (the registered, unkeyed <see cref="UnifiedTableStorageOptions"/>): their
/// <see cref="BlobOffloadOptions.ServiceClient"/>; else the host's registered client when they set
/// <see cref="BlobOffloadOptions.UseRegisteredServiceClient"/>, and then no other one; only when they choose
/// neither does a call supply one, and the first one supplied is kept. Before (CACHE-1), options the host
/// registered as an instance lost their client to the one a later connection-string or credential call
/// derived, and the values went to another account, silently.
/// </summary>
[Collection("OversizedCellPolicy")]
public class RegisteredOptionsOffloadClientTests
{
    private const string DevelopmentStorage = "UseDevelopmentStorage=true";
    private const string Container = "values";

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(Arg.Any<string>()).Returns(Substitute.For<TableClient>());
        services.AddSingleton(service);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    private static BlobServiceClient? OffloadClient(IServiceProvider provider) =>
        provider.GetKeyedService<BlobServiceClient>(ServiceCollectionExtensions.OffloadBlobServiceClientKey);

    private static BlobServiceClient Recording()
    {
        var blobs = Substitute.For<BlobServiceClient>();
        blobs.GetBlobContainerClient(Arg.Any<string>()).Returns(Substitute.For<BlobContainerClient>());
        return blobs;
    }

    private sealed class DummyCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    // No client other than the configured one may be registered for offload (one registered under the
    // library's key is fine when it is the configured one).
    private static void AssertNoOtherOffloadClient(IServiceProvider provider, BlobServiceClient configured)
    {
        var keyed = OffloadClient(provider);
        Assert.True(keyed is null || ReferenceEquals(keyed, configured),
            $"a client other than the configured one is registered for blob offload: {keyed?.Uri}");
    }

    private static void AssertFailsWithTheLibrarysMessage(IServiceProvider provider)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Contains("no BlobServiceClient was supplied", failure.Message, StringComparison.Ordinal);
    }

    // CACHE-1: RegistrationReviewTests.LaterConnectionStringCall_MustNotOverrideTheServiceClient_TheRegisteredOptionsName.
    [Fact]
    public void LaterConnectionStringCall_KeepsTheServiceClient_TheRegisteredOptionsName()
    {
        var services = Services();
        var configured = Recording();
        services.AddSingleton(new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ContainerName = Container, ServiceClient = configured },
        });

        services.AddUnifiedTableStorage(DevelopmentStorage);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        configured.Received(1).GetBlobContainerClient(Container);
        AssertNoOtherOffloadClient(provider, configured);
    }

    // CACHE-1, the verifier's credential-overload case.
    [Fact]
    public void LaterCredentialCall_KeepsTheServiceClient_TheRegisteredOptionsName()
    {
        var services = Services();
        var configured = Recording();
        services.AddSingleton(new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ContainerName = Container, ServiceClient = configured },
        });

        services.AddUnifiedTableStorage(new Uri("https://acct.table.core.windows.net"), new DummyCredential());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        configured.Received(1).GetBlobContainerClient(Container);
        AssertNoOtherOffloadClient(provider, configured);
    }

    // CACHE-1, second form: RegistrationReviewTests.LaterConnectionStringCall_MustNotReplaceTheHostsClient_TheRegisteredOptionsAskFor.
    // The reproduction accepted "the host's client, or the library's message"; the requirement is the
    // host's client when the host has one, so this asserts that.
    [Fact]
    public void LaterConnectionStringCall_UsesTheHostsClient_TheRegisteredOptionsAskFor()
    {
        var services = Services();
        var host = Recording();
        services.AddSingleton(host);
        services.AddSingleton(new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ContainerName = Container, UseRegisteredServiceClient = true },
        });

        services.AddUnifiedTableStorage(DevelopmentStorage);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        host.Received(1).GetBlobContainerClient(Container);
        Assert.Same(host, OffloadClient(provider));
        Assert.Same(host, provider.GetRequiredService<BlobServiceClient>());
    }

    // ... and when the host has no client, no other one: the store fails with the library's message.
    [Fact]
    public void LaterConnectionStringCall_DoesNotSupplyAClient_WhenTheRegisteredOptionsAskForTheHostsAndTheHostHasNone()
    {
        var services = Services();
        services.AddSingleton(new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ContainerName = Container, UseRegisteredServiceClient = true },
        });

        services.AddUnifiedTableStorage(DevelopmentStorage);
        services.AddUnifiedTableStorage(new Uri("https://acct.table.core.windows.net"), new DummyCredential());
        using var provider = services.BuildServiceProvider();

        Assert.Null(OffloadClient(provider));
        AssertFailsWithTheLibrarysMessage(provider);
        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
    }

    // The reviewer's control: nothing to derive, the configured client is used.
    [Fact]
    public void LaterCallWithoutADerivedClient_KeepsTheServiceClient_TheRegisteredOptionsName()
    {
        var services = Services();
        var configured = Recording();
        services.AddSingleton(new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ContainerName = Container, ServiceClient = configured },
        });

        services.AddUnifiedTableStorage();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        AssertNoOtherOffloadClient(provider, configured);
        configured.Received(1).GetBlobContainerClient(Container);
    }

    // The verifier's control: options registered through a factory keep their client.
    [Fact]
    public void OptionsRegisteredThroughAFactory_KeepTheirServiceClient_BesideALaterConnectionStringCall()
    {
        var services = Services();
        var configured = Recording();
        services.AddSingleton(_ => new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ContainerName = Container, ServiceClient = configured },
        });

        services.AddUnifiedTableStorage(DevelopmentStorage);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        configured.Received(1).GetBlobContainerClient(Container);
        AssertNoOtherOffloadClient(provider, configured);
    }

    // Options registered through a factory are decided when the container builds them: one that asks for
    // the host's client gets it, also beside a later call that derives one.
    [Fact]
    public void OptionsRegisteredThroughAFactory_ThatAskForTheHostsClient_GetIt_BesideALaterConnectionStringCall()
    {
        var services = Services();
        var host = Recording();
        services.AddSingleton(_ => new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ContainerName = Container, UseRegisteredServiceClient = true },
        });
        services.AddUnifiedTableStorage(DevelopmentStorage);
        services.AddSingleton(host); // resolved lazily: the host may register it after the call

        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        host.Received(1).GetBlobContainerClient(Container);
        Assert.Same(host, provider.GetRequiredService<BlobServiceClient>());
    }

    // Options registered through a factory that choose no client: the later call supplies the one it derives.
    [Fact]
    public void OptionsRegisteredThroughAFactory_ThatChooseNoClient_TakeTheOneALaterCallDerives()
    {
        var services = Services();
        services.AddSingleton(_ => new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { ContainerName = Container } });

        services.AddUnifiedTableStorage(DevelopmentStorage);
        services.AddUnifiedTableStorage(new Uri("https://acct.table.core.windows.net"), new DummyCredential());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Equal("devstoreaccount1", OffloadClient(provider)?.AccountName);
    }

    // Options registered through a factory without blob offload: every store resolves as before, and the
    // host's own client is untouched.
    [Fact]
    public void OptionsRegisteredThroughAFactory_WithoutBlobOffload_ResolveEveryStore_AndLeaveTheHostsClient()
    {
        var services = Services();
        var host = Recording();
        services.AddSingleton(host);
        services.AddSingleton(_ => new UnifiedTableStorageOptions());

        services.AddUnifiedTableStorage(DevelopmentStorage);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        Assert.Same(host, provider.GetRequiredService<BlobServiceClient>());
        Assert.Single(provider.GetServices<BlobServiceClient>());
        Assert.Empty(host.ReceivedCalls());
    }

    // Who registered the options does not matter: options the host registers after a call of the library
    // are the ones every store receives, and their client is the one the stores use.
    [Fact]
    public void OptionsTheHostRegistersAfterALibraryCall_DecideTheClient()
    {
        var services = Services();
        var configured = Recording();
        services.AddUnifiedTableStorage(DevelopmentStorage, o => o.BlobOffload = new BlobOffloadOptions());
        services.AddSingleton(new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ContainerName = Container, ServiceClient = configured },
        });
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        configured.Received(1).GetBlobContainerClient(Container);
        AssertNoOtherOffloadClient(provider, configured);
    }

    private const string MalformedBlobEndpoint =
        "BlobEndpoint=not a uri;AccountName=acct;AccountKey=YWJj;TableEndpoint=https://acct.table.core.windows.net/";

    // Beside options the container has not built yet, a call whose string has a malformed blob endpoint
    // fails nothing: those options may never need its client. When they do, the store says so.
    [Fact]
    public void LaterConnectionStringCall_WithAMalformedBlobEndpoint_BesideOptionsRegisteredThroughAFactory_RegistersAndOffersNoClient()
    {
        var services = Services();
        services.AddSingleton(_ => new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { ContainerName = Container } });

        var failure = Record.Exception(() => services.AddUnifiedTableStorage(MalformedBlobEndpoint));
        using var provider = services.BuildServiceProvider();

        Assert.Null(failure);
        Assert.Null(OffloadClient(provider));
        Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
        AssertFailsWithTheLibrarysMessage(provider);
    }

    // CACHE-4 (documented, unchanged by the fix of CACHE-1): the in-memory registration's options ask for
    // blob offload and choose no client, so a later table call is the one to supply it and validates the
    // blob endpoint of its string, although the in-memory stores (the first IStorage<> registration) use
    // none.
    [Fact]
    public void LaterTableCall_AfterAnInMemoryRegistrationWithBlobOffload_ValidatesTheBlobEndpointOfItsString()
    {
        var services = new ServiceCollection();
        services.AddUnifiedInMemoryStorage(o => o.BlobOffload = new BlobOffloadOptions());

        var failure = Assert.Throws<ArgumentException>(() => services.AddUnifiedTableStorage(MalformedBlobEndpoint));

        Assert.Contains("no BlobServiceClient can be built", failure.Message, StringComparison.Ordinal);
    }

    // ── the whole matrix: options of the host (instance or factory), then one library call ───────────

    public enum HostOffload
    {
        None,
        Set,
        Explicit,
        UseHost,
    }

    public enum Overload
    {
        Plain,
        ConnectionString,
        Credential,
    }

    public enum CallOffload
    {
        None,
        Set,
        Explicit,
        UseHost,
    }

    [Fact]
    public void OptionsOfTheHost_ThenAnyLibraryCall_TheStoreUsesTheClientTheDocumentedOrderGives()
    {
        var wrong = new List<string>();
        var cases = 0;
        foreach (var asFactory in new[] { false, true })
        foreach (var hostOffload in Enum.GetValues<HostOffload>())
        foreach (var overload in Enum.GetValues<Overload>())
        foreach (var callOffload in Enum.GetValues<CallOffload>())
        foreach (var hostHasAClient in new[] { false, true })
        {
            cases++;
            var what = $"{(asFactory ? "factory" : "instance")} options {hostOffload}, then {overload}/{callOffload}, host {(hostHasAClient ? "has" : "has no")} client";
            var expected = Expected(asFactory, hostOffload, overload, callOffload, hostHasAClient);
            var actual = Actual(asFactory, hostOffload, overload, callOffload, hostHasAClient);
            if (actual != expected)
                wrong.Add($"{what}: the store uses '{actual}', the documented order gives '{expected}'");
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {cases} cases differ:\n" + string.Join("\n", wrong));
        Assert.Equal(192, cases);
    }

    private static string Expected(bool asFactory, HostOffload hostOffload, Overload overload, CallOffload callOffload, bool hostHasAClient)
    {
        switch (hostOffload)
        {
            case HostOffload.None:
                return "no offload";
            case HostOffload.Explicit:
                return "options";
            case HostOffload.UseHost:
                // The one documented limit: options the container builds through a factory cannot be read at
                // registration, so the library registers its resolution-time choice only beside a call that
                // brings a client of its own (its ServiceClient, its UseRegisteredServiceClient, or one its
                // string or endpoint derives). Registering it beside every call would register a keyed
                // BlobServiceClient for hosts that use no offload at all, which other containers resolve
                // differently (CACHE-L1). Without one the store fails loudly with the library's message;
                // no other client is ever used.
                if (asFactory && !CallBringsAClient(overload, callOffload))
                    return "fails";
                return hostHasAClient ? "host" : "fails";
        }
        // The registered options choose neither: the call supplies one by its own rule.
        switch (callOffload)
        {
            case CallOffload.Explicit:
                return "call";
            case CallOffload.UseHost:
                return hostHasAClient ? "host" : "fails";
        }
        return overload switch
        {
            Overload.ConnectionString => "devstoreaccount1",
            Overload.Credential => "acct",
            _ => "fails",
        };
    }

    private static bool CallBringsAClient(Overload overload, CallOffload callOffload) =>
        callOffload is CallOffload.Explicit or CallOffload.UseHost || overload != Overload.Plain;

    private static string Actual(bool asFactory, HostOffload hostOffload, Overload overload, CallOffload callOffload, bool hostHasAClient)
    {
        var services = Services();
        var host = Recording();
        if (hostHasAClient)
            services.AddSingleton(host);
        var optionsClient = Recording();
        var callClient = Recording();
        var options = new UnifiedTableStorageOptions
        {
            BlobOffload = hostOffload switch
            {
                HostOffload.Set => new BlobOffloadOptions { ContainerName = Container },
                HostOffload.Explicit => new BlobOffloadOptions { ContainerName = Container, ServiceClient = optionsClient },
                HostOffload.UseHost => new BlobOffloadOptions { ContainerName = Container, UseRegisteredServiceClient = true },
                _ => null,
            },
        };
        if (asFactory)
            services.AddSingleton(_ => options);
        else
            services.AddSingleton(options);

        Action<UnifiedTableStorageOptions> configure = o => o.BlobOffload = callOffload switch
        {
            CallOffload.Set => new BlobOffloadOptions(),
            CallOffload.Explicit => new BlobOffloadOptions { ServiceClient = callClient },
            CallOffload.UseHost => new BlobOffloadOptions { UseRegisteredServiceClient = true },
            _ => null,
        };
        switch (overload)
        {
            case Overload.Plain:
                services.AddUnifiedTableStorage(configure);
                break;
            case Overload.ConnectionString:
                services.AddUnifiedTableStorage(DevelopmentStorage, configure);
                break;
            default:
                services.AddUnifiedTableStorage(new Uri("https://acct.table.core.windows.net"), new DummyCredential(), configure);
                break;
        }

        using var provider = services.BuildServiceProvider();
        if (provider.GetRequiredService<IStorage<PlainEntity>>() is not TableStorage<PlainEntity>)
            return "a plain store did not resolve";
        if (provider.GetService<BlobServiceClient>() is var unkeyed && !ReferenceEquals(unkeyed, hostHasAClient ? host : null))
            return "the unkeyed BlobServiceClient changed";
        try
        {
            _ = provider.GetRequiredService<IStorage<FileEntity>>();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("no BlobServiceClient was supplied", StringComparison.Ordinal))
        {
            return "fails";
        }

        var asked = new List<string>();
        if (optionsClient.ReceivedCalls().Any()) asked.Add("options");
        if (callClient.ReceivedCalls().Any()) asked.Add("call");
        if (host.ReceivedCalls().Any()) asked.Add("host");
        if (asked.Count > 1)
            return "several clients: " + string.Join(", ", asked);
        if (asked.Count == 1)
            return asked[0];
        if (hostOffload == HostOffload.None)
            return "no offload";
        return OffloadClient(provider)?.AccountName ?? "none";
    }
}
