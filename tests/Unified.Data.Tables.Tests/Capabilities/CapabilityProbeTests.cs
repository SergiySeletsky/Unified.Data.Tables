using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.Capabilities;

/// <summary>
/// 0.10.0: new operations are side interfaces, found only through the capability probe. A store that
/// answers <see cref="IStorageCapabilities"/> is believed; only one that does not is tested with a cast.
/// The forwarding extensions throw <see cref="NotSupportedException"/> on a store without the capability,
/// and <c>GetInfo()</c> never throws.
/// </summary>
public class CapabilityProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── finding a capability ──────────────────────────────────────────────

    [Fact]
    public void PlainStore_ThatImplementsASideInterface_IsFoundByItsType()
    {
        var storage = Substitute.For<IStorage<TestEntity>, IConditionalDelete<TestEntity>>();

        Assert.True(storage.TryGetCapability<TestEntity, IConditionalDelete<TestEntity>>(out var capability));
        Assert.Same(storage, capability);
    }

    [Fact]
    public void PlainStore_WithoutTheSideInterface_HasNoCapability()
    {
        var storage = Substitute.For<IStorage<TestEntity>>();

        Assert.False(storage.TryGetCapability<TestEntity, IConditionalDelete<TestEntity>>(out var capability));
        Assert.Null(capability);
    }

    [Fact]
    public void StoreThatAnswersTheProbe_IsNeverCast_ItsAnswerIsFinal()
    {
        // The object's type implements the side interface (as a naive decorator's would), but its probe
        // says no: a cast here would claim a capability the store behind it lacks.
        var storage = Substitute.For<IStorage<TestEntity>, IStorageCapabilities, IConditionalDelete<TestEntity>>();
        ((IStorageCapabilities)storage).TryGetCapability(out Arg.Any<IConditionalDelete<TestEntity>?>()).Returns(false);

        Assert.False(storage.TryGetCapability<TestEntity, IConditionalDelete<TestEntity>>(out var capability));
        Assert.Null(capability);
        Assert.Throws<NotSupportedException>(() => { _ = storage.DeleteIfExistsAsync("p|r", Ct); });
    }

    [Fact]
    public void StoreThatAnswersTheProbe_HandsOutWhatTheProbeReturns()
    {
        var real = Substitute.For<IConditionalDelete<TestEntity>>();
        var storage = Substitute.For<IStorage<TestEntity>, IStorageCapabilities>();
        ((IStorageCapabilities)storage).TryGetCapability(out Arg.Any<IConditionalDelete<TestEntity>?>())
            .Returns(call => { call[0] = real; return true; });

        Assert.True(storage.TryGetCapability<TestEntity, IConditionalDelete<TestEntity>>(out var capability));
        Assert.Same(real, capability);
    }

    [Fact]
    public void ProbeThatSaysYesButHandsOutNull_CountsAsNo()
    {
        var storage = Substitute.For<IStorage<TestEntity>, IStorageCapabilities>();
        ((IStorageCapabilities)storage).TryGetCapability(out Arg.Any<IStorageInfo?>()).Returns(true);

        Assert.False(storage.TryGetCapability<TestEntity, IStorageInfo>(out var capability));
        Assert.Null(capability);
    }

    [Fact]
    public void PolymorphicStore_IsProbedTheSameWay()
    {
        var plain = Substitute.For<IPolymorphicStorage<TestMessage>>();
        var capable = Substitute.For<IPolymorphicStorage<TestMessage>, IAtomicInsert<TestMessage>>();

        Assert.False(plain.TryGetCapability<TestMessage, IAtomicInsert<TestMessage>>(out _));
        Assert.True(capable.TryGetCapability<TestMessage, IAtomicInsert<TestMessage>>(out var capability));
        Assert.Same(capable, capability);
    }

    [Fact]
    public void TryGetCapability_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ((IStorage<TestEntity>)null!).TryGetCapability<TestEntity, IStorageInfo>(out _));
        Assert.Throws<ArgumentNullException>(() => ((IPolymorphicStorage<TestMessage>)null!).TryGetCapability<TestMessage, IStorageInfo>(out _));
    }

    // ── the forwarding extensions ─────────────────────────────────────────

    [Fact]
    public async Task ConditionalDelete_ForwardsToTheCapability()
    {
        var storage = Substitute.For<IStorage<TestEntity>, IConditionalDelete<TestEntity>>();
        var capability = (IConditionalDelete<TestEntity>)storage;
        capability.DeleteIfMatchAsync("p|r", "W/\"1\"", Ct).Returns(DeleteStatus.Deleted);
        capability.DeleteIfExistsAsync("p|r", Ct).Returns(DeleteStatus.Indeterminate);

        Assert.Equal(DeleteStatus.Deleted, await storage.DeleteIfMatchAsync("p|r", "W/\"1\"", Ct));
        Assert.Equal(DeleteStatus.Indeterminate, await storage.DeleteIfExistsAsync("p|r", Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    public void DeleteIfMatch_WithoutAVersion_ThrowsArgumentException_AndCallsNothing(string? etag)
    {
        var storage = Substitute.For<IStorage<TestEntity>, IConditionalDelete<TestEntity>>();

        var ex = Assert.Throws<ArgumentException>(() => { _ = storage.DeleteIfMatchAsync("p|r", etag!, Ct); });

        Assert.Equal("etag", ex.ParamName);
        Assert.Contains("DeleteIfExistsAsync", ex.Message, StringComparison.Ordinal);
        Assert.Empty(((IConditionalDelete<TestEntity>)storage).ReceivedCalls());
    }

    [Fact]
    public void DeleteIfMatch_WithoutAVersion_IsAnArgumentError_EvenOnAStoreWithoutTheCapability()
    {
        var storage = Substitute.For<IStorage<TestEntity>>();

        Assert.Throws<ArgumentException>(() => { _ = storage.DeleteIfMatchAsync("p|r", "*", Ct); });
    }

    [Fact]
    public async Task AtomicBatch_ForwardsToTheCapability()
    {
        var storage = Substitute.For<IStorage<TestEntity>, IAtomicBatch<TestEntity>>();
        var capability = (IAtomicBatch<TestEntity>)storage;
        TestEntity[] batch = [new() { Id = "p|a" }, new() { Id = "p|b" }];

        await storage.CreateAtomicAsync(batch, Ct);
        await storage.UpsertAtomicAsync(batch, Ct);

        await capability.Received(1).CreateAtomicAsync(batch, Ct);
        await capability.Received(1).UpsertAtomicAsync(batch, Ct);
    }

    [Fact]
    public async Task AtomicInsert_ForwardsToTheCapability()
    {
        var storage = Substitute.For<IPolymorphicStorage<TestMessage>, IAtomicInsert<TestMessage>>();
        PolymorphicWrite<TestMessage>[] writes = [PolymorphicWrite<TestMessage>.Marker(new TableKey("p", "r"), new Dictionary<string, object>())];

        await storage.InsertAtomicAsync(writes, Ct);

        await ((IAtomicInsert<TestMessage>)storage).Received(1).InsertAtomicAsync(writes, Ct);
    }

    [Fact]
    public void EveryForwardingExtension_OnAStoreWithoutTheCapability_ThrowsNotSupported_NamingTheStoreAndTheInterface()
    {
        var storage = new InMemoryStorageWithoutCapabilities();
        var polymorphic = Substitute.For<IPolymorphicStorage<TestMessage>>();

        var delete = Assert.Throws<NotSupportedException>(() => { _ = storage.DeleteIfMatchAsync("p|r", "W/\"1\"", Ct); });
        Assert.Throws<NotSupportedException>(() => { _ = storage.DeleteIfExistsAsync("p|r", Ct); });
        var create = Assert.Throws<NotSupportedException>(() => { _ = storage.CreateAtomicAsync([], Ct); });
        Assert.Throws<NotSupportedException>(() => { _ = storage.UpsertAtomicAsync([], Ct); });
        var insert = Assert.Throws<NotSupportedException>(() => { _ = polymorphic.InsertAtomicAsync([], Ct); });

        Assert.Contains(nameof(InMemoryStorageWithoutCapabilities), delete.Message, StringComparison.Ordinal);
        Assert.Contains("IConditionalDelete", delete.Message, StringComparison.Ordinal);
        Assert.Contains("TryGetCapability", delete.Message, StringComparison.Ordinal);
        Assert.Contains("IAtomicBatch", create.Message, StringComparison.Ordinal);
        Assert.Contains("IAtomicInsert", insert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForwardingExtensions_NullStore_Throw()
    {
        IStorage<TestEntity> storage = null!;
        IPolymorphicStorage<TestMessage> polymorphic = null!;

        Assert.Throws<ArgumentNullException>(() => { _ = storage.DeleteIfMatchAsync("p|r", "e", Ct); });
        Assert.Throws<ArgumentNullException>(() => { _ = storage.DeleteIfExistsAsync("p|r", Ct); });
        Assert.Throws<ArgumentNullException>(() => { _ = storage.CreateAtomicAsync([], Ct); });
        Assert.Throws<ArgumentNullException>(() => { _ = storage.UpsertAtomicAsync([], Ct); });
        Assert.Throws<ArgumentNullException>(() => { _ = polymorphic.InsertAtomicAsync([], Ct); });
        Assert.Throws<ArgumentNullException>(() => storage.GetInfo());
        Assert.Throws<ArgumentNullException>(() => polymorphic.GetInfo());
    }

    // ── GetInfo never throws ──────────────────────────────────────────────

    [Fact]
    public void GetInfo_OfAStoreThatStatesNothing_IsUnknown()
    {
        Assert.Same(StorageInfo.Unknown, Substitute.For<IStorage<TestEntity>>().GetInfo());
        Assert.Same(StorageInfo.Unknown, Substitute.For<IPolymorphicStorage<TestMessage>>().GetInfo());
        Assert.Same(StorageInfo.Unknown, new InMemoryStorageWithoutCapabilities().GetInfo());
    }

    [Fact]
    public void GetInfo_ReturnsWhatTheStoreStates()
    {
        var stated = new StorageInfo { ProviderName = "Fake", TableName = "t", Durability = Durability.Volatile };
        var storage = Substitute.For<IStorage<TestEntity>, IStorageInfo>();
        ((IStorageInfo)storage).GetInfo().Returns(stated);
        var polymorphic = Substitute.For<IPolymorphicStorage<TestMessage>, IStorageInfo>();
        ((IStorageInfo)polymorphic).GetInfo().Returns(stated);

        Assert.Same(stated, storage.GetInfo());
        Assert.Same(stated, polymorphic.GetInfo());
    }

    [Fact]
    public void GetInfo_OfAMockThatReturnsNull_IsUnknown()
    {
        var storage = Substitute.For<IStorage<TestEntity>, IStorageInfo>();
        ((IStorageInfo)storage).GetInfo().Returns((StorageInfo)null!);

        Assert.Same(StorageInfo.Unknown, storage.GetInfo());
    }

    [Fact]
    public void GetInfo_OfAStoreThatFailsToDescribeItself_IsUnknown()
    {
        var storage = Substitute.For<IStorage<TestEntity>, IStorageInfo>();
        ((IStorageInfo)storage).GetInfo().Throws(new InvalidOperationException("boom"));

        Assert.Same(StorageInfo.Unknown, storage.GetInfo());
    }

    // ── StorageInfo ───────────────────────────────────────────────────────

    [Fact]
    public void StorageInfo_Unknown_StatesNothing()
    {
        var unknown = StorageInfo.Unknown;

        Assert.Null(unknown.ProviderName);
        Assert.Null(unknown.TableName);
        Assert.Equal(Durability.Unknown, unknown.Durability);
        Assert.Null(unknown.MaxBatchRows);
        Assert.Null(unknown.MaxBatchBytes);
        Assert.Null(unknown.MaxCellBytes);
        Assert.Null(unknown.MaxRowBytes);
        Assert.Null(unknown.ReadCacheMayServeStaleRows);
        Assert.Null(unknown.SinglePartitionTransactions);
        Assert.Equal(new StorageInfo(), unknown);
        Assert.Equal(default, Durability.Unknown);
    }

    [Fact]
    public void StorageInfo_CanGrow_WithoutBreakingAnImplementer()
    {
        // A sealed record whose only constructor takes nothing: a property added later cannot change a
        // signature an implementer compiled against, and nobody can derive from it.
        Assert.True(typeof(StorageInfo).IsSealed);
        var constructor = Assert.Single(typeof(StorageInfo).GetConstructors());
        Assert.Empty(constructor.GetParameters());
        Assert.All(
            typeof(StorageInfo).GetProperties().Where(p => p.GetMethod is { IsStatic: false }),
            p => Assert.True(p.SetMethod is { IsPublic: true }, p.Name + " must be init-settable"));

        var info = new StorageInfo
        {
            ProviderName = "AzureTables",
            TableName = "Orders",
            Durability = Durability.Replicated,
            MaxBatchRows = 100,
            MaxBatchBytes = 3_000_000,
            MaxCellBytes = 65_536,
            MaxRowBytes = 1_048_576,
            ReadCacheMayServeStaleRows = true,
            SinglePartitionTransactions = true,
        };
        Assert.Equal(info, info with { });
        Assert.NotEqual(info, info with { Durability = Durability.Fsynced });
    }

    [Fact]
    public void Durability_And_DeleteStatus_KeepTheirDeclaredOrder()
    {
        Assert.Equal(
            ["Unknown", "Volatile", "OsBuffered", "Fsynced", "Replicated"],
            Enum.GetValues<Durability>().Select(d => d.ToString()));
        Assert.Equal(["Deleted", "NotFound", "Indeterminate"], Enum.GetValues<DeleteStatus>().Select(d => d.ToString()));
    }

    // ── the library's own stores today ────────────────────────────────────

    [Fact]
    public void OrphanSweepExtension_StillFindsTheLibraryStores_AndStillRefusesAPlainOne()
    {
        IStorage<TestEntity> memory = new InMemoryStorage<TestEntity>();

        Assert.True(memory.TryGetCapability<TestEntity, IOrphanBlobSweeper>(out var sweeper));
        Assert.Same(memory, sweeper);
        Assert.False(new InMemoryStorageWithoutCapabilities().TryGetCapability<TestEntity, IOrphanBlobSweeper>(out _));
    }

    /// <summary>A hand-written decorator in the 0.9.2 style: IStorage&lt;T&gt; only, no probe, no side interface.</summary>
    private sealed class InMemoryStorageWithoutCapabilities : UneditedImplementer<TestEntity>;
}
