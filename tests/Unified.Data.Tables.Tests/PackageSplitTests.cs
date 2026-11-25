using System.Reflection;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The 0.10.0 package split: the serializer, the large-value layout, the provider SPI and the neutral
/// options moved from the Azure provider (<c>Unified.Data.Tables</c>) to <c>Unified.Data.Tables.Core</c>,
/// and the in-memory provider references the core instead of the Azure provider. Nothing a caller compiled
/// against 0.9.2 binds to may be lost by the move, and the in-memory provider must not bring
/// <c>Azure.Storage.Blobs</c> or the Azure provider with it.
/// </summary>
// The registrations write the process-wide TableEntitySerializer.OversizedCellPolicy.
[Collection("OversizedCellPolicy")]
public class PackageSplitTests
{
    private static readonly Assembly Azure = typeof(TableStorage<>).Assembly;
    private static readonly Assembly Core = typeof(TableEntitySerializer).Assembly;
    private static readonly Assembly InMemory = typeof(InMemoryStorage<>).Assembly;

    // The public types 0.10.0 adds to the core: 0.9.2 had none of them, so nothing can be bound to them
    // under the Azure assembly's name.
    private static readonly Type[] NewInCore = [typeof(StorageOptions), typeof(OffloadOptions)];

    // ── the assemblies ────────────────────────────────────────────────────

    [Fact]
    public void TheThreeAssemblies_AreDistinct_AndNamedAsTheirPackages()
    {
        Assert.Equal("Unified.Data.Tables", Azure.GetName().Name);
        Assert.Equal("Unified.Data.Tables.Core", Core.GetName().Name);
        Assert.Equal("Unified.Data.Tables.InMemory", InMemory.GetName().Name);
    }

    [Fact]
    public void InMemory_ReferencesTheCore_NotTheAzureProvider_NorTheBlobSdk()
    {
        var references = InMemory.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.Contains("Unified.Data.Tables.Core", references);
        Assert.Contains("Unified.Data.Tables.Abstractions", references);
        Assert.DoesNotContain("Unified.Data.Tables", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Azure.Storage", StringComparison.Ordinal));
    }

    [Fact]
    public void Core_ReferencesNeitherTheBlobSdk_NorAProvider()
    {
        var references = Core.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.Contains("Unified.Data.Tables.Abstractions", references);
        Assert.DoesNotContain("Unified.Data.Tables", references);
        Assert.DoesNotContain("Unified.Data.Tables.InMemory", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Azure.Storage", StringComparison.Ordinal));
    }

    // ── type forwards ─────────────────────────────────────────────────────

    public static TheoryData<Type> MovedPublicTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in Core.GetExportedTypes().Where(t => !t.IsNested && !NewInCore.Contains(t)).OrderBy(t => t.FullName, StringComparer.Ordinal))
            data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(MovedPublicTypes))]
    public void EveryPublicTypeThatMoved_IsForwardedByTheAzureAssembly(Type moved)
    {
        // What code compiled against 0.9.2 holds: the type's name qualified by the Azure assembly.
        var bound = Type.GetType($"{moved.FullName}, {Azure.GetName().Name}", throwOnError: false);

        Assert.Same(moved, bound);
        Assert.Contains(moved, Azure.GetForwardedTypes());
    }

    [Fact]
    public void TheMovedTypes_AreTheOnesThe092AzureAssemblyDeclared()
    {
        // The complete list, written out: a type that is added to the core later is new there and needs
        // no forward (add it to NewInCore), but none of these may ever lose its own.
        string[] expected =
        [
            "Unified.Data.Tables.AssemblyQualifiedTypeDiscriminator",
            "Unified.Data.Tables.CacheExpirationMode",
            "Unified.Data.Tables.CachePolicy",
            "Unified.Data.Tables.ITypeDiscriminator",
            "Unified.Data.Tables.OversizedCellException",
            "Unified.Data.Tables.OversizedCellPolicy",
            "Unified.Data.Tables.Providers.BlobReference",
            "Unified.Data.Tables.Providers.BlobState",
            "Unified.Data.Tables.Providers.IOffloadBlobStore",
            "Unified.Data.Tables.Providers.LargeValueRows",
            "Unified.Data.Tables.Providers.LargeValueTable",
            "Unified.Data.Tables.Providers.LargeValueWritePlan",
            "Unified.Data.Tables.Providers.OffloadBlobNames",
            "Unified.Data.Tables.Providers.ProjectedRows",
            "Unified.Data.Tables.Providers.ProtectedLargeValues",
            "Unified.Data.Tables.TableEntitySerializer",
            "Unified.Data.Tables.TableFilterTranslator",
            "Unified.Data.Tables.TableRequestSize",
            "Unified.Data.Tables.TypeDiscriminatorMap",
        ];

        var forwarded = Azure.GetForwardedTypes().Select(t => t.FullName!).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, forwarded);
    }

    [Fact]
    public void TheTypesThatStayed_AreStillDeclaredByTheAzureAssembly()
    {
        Assert.Same(Azure, typeof(UnifiedTableStorageOptions).Assembly);
        Assert.Same(Azure, typeof(BlobOffloadOptions).Assembly);
        Assert.Same(Azure, typeof(PolymorphicTableStorage<>).Assembly);
        Assert.Same(Azure, typeof(ServiceCollectionExtensions).Assembly);
        Assert.Same(Azure, typeof(OrphanBlobSweepOptions).Assembly);
    }

    [Fact]
    public void TheSerializersOversizedCellPolicy_IsOneState_UnderEitherAssemblyName()
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        try
        {
            // The property as a 0.9.2 caller reaches it: through the Azure assembly's name.
            var property = Type.GetType("Unified.Data.Tables.TableEntitySerializer, Unified.Data.Tables", throwOnError: true)!
                .GetProperty(nameof(TableEntitySerializer.OversizedCellPolicy), BindingFlags.Public | BindingFlags.Static)!;

            property.SetValue(null, OversizedCellPolicy.TrimSilently);
            Assert.Equal(OversizedCellPolicy.TrimSilently, TableEntitySerializer.OversizedCellPolicy);

            TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimWithMarker;
            Assert.Equal(OversizedCellPolicy.TrimWithMarker, property.GetValue(null));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    // ── options: a neutral base, the Azure type on top ────────────────────

    [Fact]
    public void AzureOptions_DeriveFromTheNeutralOnes_AndKeepEveryMemberACallerBoundTo()
    {
        Assert.True(typeof(UnifiedTableStorageOptions).IsSubclassOf(typeof(StorageOptions)));
        Assert.True(typeof(UnifiedTableStorageOptions).IsSealed);
        Assert.True(typeof(BlobOffloadOptions).IsSubclassOf(typeof(OffloadOptions)));
        Assert.True(typeof(BlobOffloadOptions).IsSealed);

        // A member reference compiled against 0.9.2 names the Azure type; the runtime finds a member that
        // moved to a base class from there. Reflection resolves them the same way.
        foreach (var name in new[] { "Cache", "OversizedCells", "IdNormalization", "ImplicitLastWriterWins", "BlobOffload", "TableNameResolver", "TypeDiscriminator" })
            Assert.NotNull(typeof(UnifiedTableStorageOptions).GetProperty(name, BindingFlags.Public | BindingFlags.Instance));
        foreach (var name in new[] { "ContainerName", "ServiceClient", "UseRegisteredServiceClient", "ThresholdBytes", "CleanupGrace" })
            Assert.NotNull(typeof(BlobOffloadOptions).GetProperty(name, BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(OffloadOptions.MaxThresholdBytes, BlobOffloadOptions.MaxThresholdBytes);
    }

    [Fact]
    public void CacheFor_OnTheAzureOptions_StillReturnsTheAzureType()
    {
        // The 0.9.2 signature, exactly: a compiled call site names this return type, and a fluent chain
        // goes on to members only the Azure type has.
        var declared = typeof(UnifiedTableStorageOptions)
            .GetMethod(nameof(UnifiedTableStorageOptions.CacheFor), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        Assert.Equal(typeof(UnifiedTableStorageOptions), declared.ReturnType);

        var options = new UnifiedTableStorageOptions();
        var returned = options.CacheFor<TestEntity>(CachePolicy.Disabled);
        returned.BlobOffload = new BlobOffloadOptions();

        Assert.Same(options, returned);
        Assert.Same(CachePolicy.Disabled, options.ResolveCachePolicy(typeof(TestEntity)));
        Assert.Same(options.Cache, options.ResolveCachePolicy(typeof(PlainEntity)));
    }

    [Fact]
    public void CacheFor_OnTheNeutralOptions_SetsTheSamePolicy_WhicheverTypeItIsCalledThrough()
    {
        // The lambda an in-memory registration takes since 0.10.0.
        Action<StorageOptions> configure = o => o.CacheFor<TestEntity>(CachePolicy.Disabled);

        var neutral = new StorageOptions();
        configure(neutral);
        var azure = new UnifiedTableStorageOptions();
        configure(azure);

        Assert.Same(neutral, neutral.CacheFor<PlainEntity>(CachePolicy.Disabled));
        Assert.Same(CachePolicy.Disabled, neutral.ResolveCachePolicy(typeof(TestEntity)));
        Assert.Same(CachePolicy.Disabled, azure.ResolveCachePolicy(typeof(TestEntity)));
    }

    [Fact]
    public void BlobOffload_AndTheNeutralOffload_AreTheSameInstance()
    {
        var offload = new BlobOffloadOptions { ContainerName = "values", ThresholdBytes = 1_000 };
        var options = new UnifiedTableStorageOptions { BlobOffload = offload };

        StorageOptions neutral = options;
        Assert.Same(offload, neutral.Offload);
        Assert.Same(offload, options.BlobOffload);

        // Set through the base, read through the Azure property, and back to none.
        var other = new BlobOffloadOptions();
        neutral.Offload = other;
        Assert.Same(other, options.BlobOffload);
        neutral.Offload = null;
        Assert.Null(options.BlobOffload);
    }

    [Fact]
    public void AzureOptions_RefuseOffloadOptionsWithoutTheAzurePart_InsteadOfIgnoringThem()
    {
        var options = new UnifiedTableStorageOptions();

        var refused = Assert.Throws<ArgumentException>(() => options.Offload = new OffloadOptions());

        Assert.Contains(nameof(BlobOffloadOptions), refused.Message, StringComparison.Ordinal);
        Assert.Null(options.Offload);
        Assert.Null(options.BlobOffload);
    }

    [Fact]
    public void NeutralOffloadOptions_KeepTheDefaultsAndTheRanges()
    {
        var offload = new OffloadOptions();

        Assert.Equal("unified-tables-values", offload.ContainerName);
        Assert.Equal(3_145_728, offload.ThresholdBytes);
        Assert.Equal(TimeSpan.FromMinutes(5), offload.CleanupGrace);
        Assert.Equal(4_194_304, OffloadOptions.MaxThresholdBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => offload.ThresholdBytes = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => offload.ThresholdBytes = OffloadOptions.MaxThresholdBytes + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => offload.CleanupGrace = TimeSpan.FromTicks(-1));
    }

    // ── the in-memory provider on the neutral options ─────────────────────

    public static TheoryData<string, StorageOptions> OffloadConfigurations() => new()
    {
        { "neutral options, neutral offload", new StorageOptions { Offload = new OffloadOptions { ThresholdBytes = 1_000 } } },
        { "neutral options, Azure offload", new StorageOptions { Offload = new BlobOffloadOptions { ThresholdBytes = 1_000 } } },
        { "Azure options, as before 0.10.0", new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 } } },
    };

    [Theory]
    [MemberData(nameof(OffloadConfigurations))]
    public async Task InMemory_Offloads_HoweverItsOptionsNameTheOffload(string how, StorageOptions options)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryStorage<SplitTextEntity>(null, options);
        var body = new string('x', 40_000);

        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = body }, ct);

        Assert.True(store.Blobs.Names.Count == 1, $"{how}: {store.Blobs.Names.Count} blobs");
        Assert.Equal(body, (await store.OneAsync("p|r", ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task InMemory_WithoutOffload_KeepsTheValueInRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryStorage<SplitTextEntity>(null, new StorageOptions());
        var body = new string('x', 40_000);

        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = body }, ct);

        Assert.Empty(store.Blobs.Names);
        Assert.Equal(body, (await store.OneAsync("p|r", ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public void InMemoryRegistration_RegistersNeutralOptions_AndItsLambdaMayStillCallCacheFor()
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        try
        {
            var services = new ServiceCollection();
            services.AddUnifiedInMemoryStorage(o => o.CacheFor<TestEntity>(CachePolicy.Disabled));
            using var provider = services.BuildServiceProvider();

            var options = provider.GetRequiredService<StorageOptions>();
            Assert.Equal(typeof(StorageOptions), options.GetType());
            Assert.Same(CachePolicy.Disabled, options.ResolveCachePolicy(typeof(TestEntity)));
            Assert.Null(provider.GetService<UnifiedTableStorageOptions>());
            Assert.IsType<InMemoryStorage<TestEntity>>(provider.GetRequiredService<IStorage<TestEntity>>());
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    // ── both providers in one container: the first registration's options still win ──

    private static ServiceCollection TableServices()
    {
        var services = new ServiceCollection();
        var tables = Substitute.For<TableServiceClient>();
        tables.GetTableClient(Arg.Any<string>()).Returns(Substitute.For<TableClient>());
        services.AddSingleton(tables);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    [Fact]
    public void TableRegistration_AlsoRegistersItsOptionsUnderTheNeutralType_SoALaterInMemoryCallKeepsThem()
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        try
        {
            var services = TableServices();
            services.AddUnifiedTableStorage(o => o.OversizedCells = OversizedCellPolicy.TrimWithMarker);
            services.AddUnifiedInMemoryStorage(o => o.OversizedCells = OversizedCellPolicy.Throw);
            using var provider = services.BuildServiceProvider();

            var azure = provider.GetRequiredService<UnifiedTableStorageOptions>();
            Assert.Same(azure, provider.GetRequiredService<StorageOptions>());
            Assert.Equal(OversizedCellPolicy.TrimWithMarker, azure.OversizedCells);
            Assert.Equal(OversizedCellPolicy.TrimWithMarker, TableEntitySerializer.OversizedCellPolicy);
            Assert.Equal(1, services.Count(d => !d.IsKeyedService && d.ServiceType == typeof(StorageOptions)));
            Assert.IsType<TableStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    [Fact]
    public void InMemoryRegistrationFirst_ALaterTableCall_DoesNotResetItsOversizedCellPolicy()
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        try
        {
            var services = TableServices();
            services.AddUnifiedInMemoryStorage(o => o.OversizedCells = OversizedCellPolicy.TrimWithMarker);
            services.AddUnifiedTableStorage(o => o.OversizedCells = OversizedCellPolicy.Throw);
            using var provider = services.BuildServiceProvider();

            // The in-memory options were the first registration and stay the ones the stores are given.
            Assert.Equal(OversizedCellPolicy.TrimWithMarker, TableEntitySerializer.OversizedCellPolicy);
            Assert.Equal(OversizedCellPolicy.TrimWithMarker, provider.GetRequiredService<StorageOptions>().OversizedCells);
            Assert.Equal(1, services.Count(d => !d.IsKeyedService && d.ServiceType == typeof(StorageOptions)));
            Assert.IsType<InMemoryStorage<PlainEntity>>(provider.GetRequiredService<IStorage<PlainEntity>>());
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    [Fact]
    public async Task InMemoryRegistration_KeepsTheAzureOptionsTheHostRegisteredItself()
    {
        // A test host that swaps only the registration line and keeps registering its production options:
        // the in-memory stores received them while both providers shared one options type, and still do.
        var ct = TestContext.Current.CancellationToken;
        var before = TableEntitySerializer.OversizedCellPolicy;
        try
        {
            TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimWithMarker;
            var hosts = new UnifiedTableStorageOptions { IdNormalization = IdNormalization.AsWritten };
            var services = new ServiceCollection();
            services.AddSingleton(hosts);

            services.AddUnifiedInMemoryStorage(o => o.OversizedCells = OversizedCellPolicy.Throw);
            services.AddUnifiedInMemoryStorage();
            using var provider = services.BuildServiceProvider();

            Assert.Same(hosts, provider.GetRequiredService<StorageOptions>());
            Assert.Equal(1, services.Count(d => !d.IsKeyedService && d.ServiceType == typeof(StorageOptions)));
            // Not the first registration: its options are dropped and its policy is not applied.
            Assert.Equal(OversizedCellPolicy.TrimWithMarker, TableEntitySerializer.OversizedCellPolicy);

            var store = provider.GetRequiredService<IStorage<TestEntity>>();
            await store.CreateAsync(new TestEntity { Id = "Part|Row Key" }, ct);
            Assert.NotNull(await store.OneAsync("Part|Row Key", ct));
            Assert.Null(await store.OneAsync("part|row-key", ct));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    [Fact]
    public async Task InMemoryPolymorphicTable_ResolvesTheDiscriminatorOfTheAzureOptionsTheHostRegistered()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddSingleton(new UnifiedTableStorageOptions { TypeDiscriminator = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created") });
        services.AddUnifiedInMemoryPolymorphicTable<TestMessage>("StateEventStore");
        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("StateEventStore");
        var entry = await store.InsertAsync(new TableKey("p", "r"), new TestCreatedEvent { Id = "e1" }, ct);

        Assert.Equal("created", entry.Discriminator);
    }

    // ── the provider SPI on the neutral offload type ──────────────────────

    [Fact]
    public void LargeValueTable_TakesNeutralOffloadOptions_AndAnAzureInstanceAsBefore()
    {
        var parameter = typeof(LargeValueTable)
            .GetConstructor([typeof(string), typeof(OffloadOptions), typeof(IOffloadBlobStore)])!
            .GetParameters()[1];
        Assert.Equal(typeof(OffloadOptions), parameter.ParameterType);

        // Source written against 0.9.2 passes the Azure type and still compiles.
        var blobs = new InMemoryOffloadBlobStore();
        Assert.True(new LargeValueTable("Docs", new BlobOffloadOptions(), blobs).HasBlobOffload);
        Assert.True(new LargeValueTable("Docs", new OffloadOptions(), blobs).HasBlobOffload);
        Assert.False(new LargeValueTable("Docs", null, blobs).HasBlobOffload);
    }
}
