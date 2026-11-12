using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A keyed <see cref="UnifiedTableStorageOptions"/> registration of the host (for a purpose of its own) is
/// not "the options are registered": a store never receives it. The first registration call beside it
/// behaves as a first call: its options become the registered ones, it applies their oversized-cell policy,
/// and it supplies their offload blob client by the documented order (CACHE-2, COMPLETENESS-6). Before, the
/// check counted keyed descriptors, so the call registered its options but applied neither.
/// </summary>
// The registrations write the process-wide TableEntitySerializer.OversizedCellPolicy.
[Collection("OversizedCellPolicy")]
public class KeyedHostOptionsRegistrationTests
{
    private const string DevelopmentStorage = "UseDevelopmentStorage=true";

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

    // CACHE-2, the blob-client half (new in 0.9.0).
    [Fact]
    public void FirstConnectionStringCall_BesideKeyedOptionsOfTheHost_RegistersTheOffloadClientItDerives()
    {
        var services = Services();
        services.AddKeyedSingleton("tenant", new UnifiedTableStorageOptions());

        services.AddUnifiedTableStorage(DevelopmentStorage, o => o.BlobOffload = new BlobOffloadOptions());
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<UnifiedTableStorageOptions>().BlobOffload);
        Assert.Equal("devstoreaccount1", OffloadClient(provider)?.AccountName);
        Assert.IsType<TableStorage<FileEntity>>(provider.GetRequiredService<IStorage<FileEntity>>());
        // The host's keyed registration is left as it was.
        Assert.Null(provider.GetRequiredKeyedService<UnifiedTableStorageOptions>("tenant").BlobOffload);
    }

    // CACHE-2, the policy half. On 0.8.3 the policy was not applied either: correcting it is deliberate, a
    // keyed registration never was what a store receives.
    [Fact]
    public void FirstCall_BesideKeyedOptionsOfTheHost_AppliesItsOversizedCellPolicy()
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        try
        {
            TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.Throw;
            var services = Services();
            services.AddKeyedSingleton("tenant", new UnifiedTableStorageOptions());

            services.AddUnifiedTableStorage(o => o.OversizedCells = OversizedCellPolicy.TrimWithMarker);
            using var provider = services.BuildServiceProvider();

            Assert.Equal(OversizedCellPolicy.TrimWithMarker, provider.GetRequiredService<UnifiedTableStorageOptions>().OversizedCells);
            Assert.Equal(OversizedCellPolicy.TrimWithMarker, TableEntitySerializer.OversizedCellPolicy);
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    // COMPLETENESS-6: both halves through one connection-string call.
    [Fact]
    public void ConnectionStringCall_AfterTheHostRegisteredKeyedOptions_RegistersTheOffloadClient_AndAppliesThePolicy()
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        try
        {
            TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.Throw;
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddKeyedSingleton("host", new UnifiedTableStorageOptions());

            services.AddUnifiedTableStorage(DevelopmentStorage, o =>
            {
                o.BlobOffload = new BlobOffloadOptions();
                o.OversizedCells = OversizedCellPolicy.TrimWithMarker;
            });

            using var provider = services.BuildServiceProvider();
            Assert.NotNull(provider.GetRequiredService<UnifiedTableStorageOptions>().BlobOffload);
            var client = OffloadClient(provider);
            var resolution = Record.Exception(() => provider.GetRequiredService<IStorage<FileEntity>>());
            Assert.True(client is not null && resolution is null && TableEntitySerializer.OversizedCellPolicy == OversizedCellPolicy.TrimWithMarker,
                $"offload blob client registered: {client is not null}; policy applied: {TableEntitySerializer.OversizedCellPolicy}; "
                + $"IStorage<FileEntity> resolution: {(resolution is null ? "ok" : resolution.GetType().Name + ": " + resolution.Message)}");
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    // The same through the in-memory registration (its only use of the check is the policy).
    [Fact]
    public void FirstInMemoryCall_BesideKeyedOptionsOfTheHost_AppliesItsOversizedCellPolicy()
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        try
        {
            TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.Throw;
            var services = new ServiceCollection();
            services.AddKeyedSingleton("tenant", new UnifiedTableStorageOptions());

            services.AddUnifiedInMemoryStorage(o => o.OversizedCells = OversizedCellPolicy.TrimWithMarker);
            using var provider = services.BuildServiceProvider();

            Assert.Equal(OversizedCellPolicy.TrimWithMarker, provider.GetRequiredService<UnifiedTableStorageOptions>().OversizedCells);
            Assert.Equal(OversizedCellPolicy.TrimWithMarker, TableEntitySerializer.OversizedCellPolicy);
            Assert.IsType<InMemoryStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    // Control: a second call beside the keyed options is still a later call. It changes neither the policy
    // nor the registered options.
    [Fact]
    public void SecondCall_BesideKeyedOptionsOfTheHost_LeavesTheFirstCallsPolicy()
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        try
        {
            var services = Services();
            services.AddKeyedSingleton("tenant", new UnifiedTableStorageOptions());

            services.AddUnifiedTableStorage(o => o.OversizedCells = OversizedCellPolicy.TrimWithMarker);
            services.AddUnifiedInMemoryStorage(o => o.OversizedCells = OversizedCellPolicy.Throw);
            services.AddUnifiedTableStorage(o => o.OversizedCells = OversizedCellPolicy.Throw);
            using var provider = services.BuildServiceProvider();

            Assert.Equal(OversizedCellPolicy.TrimWithMarker, provider.GetRequiredService<UnifiedTableStorageOptions>().OversizedCells);
            Assert.Equal(OversizedCellPolicy.TrimWithMarker, TableEntitySerializer.OversizedCellPolicy);
            Assert.Single(services, d => !d.IsKeyedService && d.ServiceType == typeof(UnifiedTableStorageOptions));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }
}
