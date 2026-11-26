using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.Capabilities;

/// <summary>
/// <see cref="DelegatingStorage{T}"/>, the base class of a decorator: every <see cref="IStorage{T}"/>
/// member forwards to the wrapped store, and the capability probe answers for that store, so a decorator
/// exposes exactly the side interfaces the store it wraps supports.
/// </summary>
public class DelegatingStorageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Passthrough(IStorage<TestEntity> inner) : DelegatingStorage<TestEntity>(inner);

    // ── capabilities ──────────────────────────────────────────────────────

    [Fact]
    public void TheBaseClass_ImplementsTheStoreAndTheProbe_AndNoSideInterface()
    {
        var interfaces = typeof(DelegatingStorage<TestEntity>).GetInterfaces();

        Assert.Equal(
            new[] { typeof(IStorage<TestEntity>), typeof(IStorageCapabilities) }.OrderBy(t => t.Name),
            interfaces.OrderBy(t => t.Name));
    }

    [Fact]
    public async Task OverAStoreWithACapability_TheDecoratorExposesIt()
    {
        var inner = Substitute.For<IStorage<TestEntity>, IConditionalDelete<TestEntity>>();
        ((IConditionalDelete<TestEntity>)inner).DeleteIfExistsAsync("p|r", Ct).Returns(DeleteStatus.Deleted);
        IStorage<TestEntity> decorator = new Passthrough(inner);

        Assert.True(decorator.TryGetCapability<TestEntity, IConditionalDelete<TestEntity>>(out var capability));
        Assert.Same(inner, capability);
        Assert.Equal(DeleteStatus.Deleted, await decorator.DeleteIfExistsAsync("p|r", Ct));
    }

    [Fact]
    public void OverAStoreWithoutTheCapability_TheDecoratorDoesNotClaimIt()
    {
        IStorage<TestEntity> decorator = new Passthrough(Substitute.For<IStorage<TestEntity>>());

        Assert.False(decorator.TryGetCapability<TestEntity, IConditionalDelete<TestEntity>>(out var capability));
        Assert.Null(capability);
        Assert.False(decorator is IConditionalDelete<TestEntity>);
        var ex = Assert.Throws<NotSupportedException>(() => { _ = decorator.DeleteIfExistsAsync("p|r", Ct); });
        Assert.Contains(nameof(Passthrough), ex.Message, StringComparison.Ordinal);
        Assert.Same(StorageInfo.Unknown, decorator.GetInfo());
    }

    [Fact]
    public void DecoratorsStack_TheProbeReachesTheInnermostStore()
    {
        var inner = Substitute.For<IStorage<TestEntity>, IStorageInfo>();
        var stated = new StorageInfo { ProviderName = "Fake" };
        ((IStorageInfo)inner).GetInfo().Returns(stated);
        IStorage<TestEntity> decorator = new Passthrough(new Passthrough(new Passthrough(inner)));

        Assert.Same(stated, decorator.GetInfo());
    }

    [Fact]
    public void AStackedDecoratorThatHidesACapability_HidesItFromTheOuterOnesToo()
    {
        var inner = Substitute.For<IStorage<TestEntity>, IConditionalDelete<TestEntity>, IStorageInfo>();
        IStorage<TestEntity> decorator = new Passthrough(new WithoutConditionalDelete(new Passthrough(inner)));

        Assert.False(decorator.TryGetCapability<TestEntity, IConditionalDelete<TestEntity>>(out _));
        // Only the one it names: the others still pass.
        Assert.True(decorator.TryGetCapability<TestEntity, IStorageInfo>(out var info));
        Assert.Same(inner, info);
    }

    /// <summary>A subclass that deliberately hides one capability of the store it wraps.</summary>
    private sealed class WithoutConditionalDelete(IStorage<TestEntity> inner) : DelegatingStorage<TestEntity>(inner)
    {
        public override bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
        {
            if (typeof(TCap) == typeof(IConditionalDelete<TestEntity>))
            {
                capability = null;
                return false;
            }
            return base.TryGetCapability(out capability);
        }
    }

    [Fact]
    public void ASubclass_CanAddACapability_TheInnerStoreLacks()
    {
        IStorage<TestEntity> decorator = new Describing(Substitute.For<IStorage<TestEntity>>());

        Assert.True(decorator.TryGetCapability<TestEntity, IStorageInfo>(out var capability));
        Assert.Same(decorator, capability);
        Assert.Equal("Described", decorator.GetInfo().ProviderName);
        // Adding one adds nothing else.
        Assert.False(decorator.TryGetCapability<TestEntity, IConditionalDelete<TestEntity>>(out _));
    }

    /// <summary>A subclass that deliberately adds a capability by implementing the side interface.</summary>
    private sealed class Describing(IStorage<TestEntity> inner) : DelegatingStorage<TestEntity>(inner), IStorageInfo
    {
        public StorageInfo GetInfo() => new() { ProviderName = "Described" };
    }

    [Fact]
    public async Task ASubclass_ThatImplementsASideInterface_SeesTheCallsMadeThroughIt()
    {
        var inner = Substitute.For<IStorage<TestEntity>, IConditionalDelete<TestEntity>>();
        ((IConditionalDelete<TestEntity>)inner).DeleteIfExistsAsync("p|r", Ct).Returns(DeleteStatus.NotFound);
        var counting = new CountingDeletes(inner);
        // Wrapped once more: the outer decorator finds the counting one, not the store behind it.
        IStorage<TestEntity> decorator = new Passthrough(counting);

        Assert.Equal(DeleteStatus.NotFound, await decorator.DeleteIfExistsAsync("p|r", Ct));

        Assert.Equal(1, counting.Deletes);
    }

    private sealed class CountingDeletes(IStorage<TestEntity> inner) : DelegatingStorage<TestEntity>(inner), IConditionalDelete<TestEntity>
    {
        public int Deletes { get; private set; }

        public Task<DeleteStatus> DeleteIfMatchAsync(string id, string etag, CancellationToken ct = default)
        {
            Deletes++;
            return Inner.DeleteIfMatchAsync(id, etag, ct);
        }

        public Task<DeleteStatus> DeleteIfExistsAsync(string id, CancellationToken ct = default)
        {
            Deletes++;
            return Inner.DeleteIfExistsAsync(id, ct);
        }
    }

    [Fact]
    public void Constructor_NullInner_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new Passthrough(null!));

    // ── the orphan sweep goes through the probe too ───────────────────────

    [Fact]
    public async Task OrphanSweep_OfADecorator_SweepsTheStoreItWraps()
    {
        IStorage<TestEntity> decorator = new Passthrough(new InMemoryStorage<TestEntity>());

        Assert.False(decorator is IOrphanBlobSweeper);
        Assert.Equal(0, await decorator.SweepOrphanBlobsAsync(TimeSpan.Zero, Ct));
        Assert.Equal(0, await decorator.SweepOrphanBlobsAsync("p", TimeSpan.Zero, Ct));
    }

    [Fact]
    public void OrphanSweep_OfADecoratorOverAStoreWithoutIt_IsNotSupported()
    {
        IStorage<TestEntity> decorator = new Passthrough(Substitute.For<IStorage<TestEntity>>());

        Assert.Throws<NotSupportedException>(() => { _ = decorator.SweepOrphanBlobsAsync(TimeSpan.Zero, Ct); });
    }

    [Fact]
    public void HostedSweep_ResolvesTheSweeperBehindADecorator_AndNoneBehindOneWithoutIt()
    {
        var store = new InMemoryStorage<TestEntity>();
        var target = new OrphanBlobSweepTarget<TestEntity>();

        using var capable = new ServiceCollection().AddSingleton<IStorage<TestEntity>>(new Passthrough(store)).BuildServiceProvider();
        using var incapable = new ServiceCollection()
            .AddSingleton<IStorage<TestEntity>>(new Passthrough(Substitute.For<IStorage<TestEntity>>())).BuildServiceProvider();

        Assert.Same(store, target.Resolve(capable));
        Assert.Null(target.Resolve(incapable));
        // The internal "has it blob offload at all" state is read off the object the probe handed out.
        Assert.True(target.Resolve(capable) is IBlobOffloadState { HasBlobOffload: false });
    }

    // ── every IStorage<T> member forwards ─────────────────────────────────

    [Fact]
    public async Task EveryMember_ForwardsToTheInnerStore_WithItsArguments()
    {
        var inner = Substitute.For<IStorage<TestEntity>>();
        IStorage<TestEntity> decorator = new Passthrough(inner);
        var entity = new TestEntity { Id = "p|r" };
        TestEntity[] batch = [entity];
        var options = new QueryOptions { Partition = "p" };
        Expression<Func<TestEntity, bool>> predicate = x => x.Value > 1;
        Action<UpdateBuilder<TestEntity>> build = _ => { };
        var stream = AsyncEnumerable.Empty<TestEntity>();
        var page = new EntityPage<TestEntity>([entity], "next");
        inner.CreateAsync(entity, Ct).Returns(entity);
        inner.UpsertAsync(entity, Ct).Returns(entity);
        inner.UpdateAsync(entity, Ct).Returns(entity);
        inner.UpdateAsync(entity, ConcurrencyMode.Strict, Ct).Returns(entity);
        inner.UpdateAsync("p|r", build, Ct).Returns("etag");
        inner.OneAsync("p|r", Ct).Returns(entity);
        inner.ExistsAsync("p|r", Ct).Returns(true);
        inner.QueryAsync("p", Ct).Returns(batch);
        inner.QueryAsync(options, Ct).Returns(batch);
        inner.QueryAsync(predicate, "p", 3, Ct).Returns(batch);
        inner.QueryStreamAsync(options, Ct).Returns(stream);
        inner.QueryStreamAsync(predicate, "p", 3, Ct).Returns(stream);
        inner.QueryPageAsync(options, Ct).Returns(page);
        inner.AnyAsync(predicate, "p", Ct).Returns(true);
        inner.CreateBatchAsync(batch, Ct).Returns(1);
        inner.UpsertBatchAsync(batch, Ct).Returns(2);
        inner.CountAsync("p", Ct).Returns(7);
        inner.DeletePartitionAsync("p", Ct).Returns(5);

        await decorator.DeleteAsync("p|r", Ct);
        Assert.Equal(5, await decorator.DeletePartitionAsync("p", Ct));
        Assert.Same(entity, await decorator.CreateAsync(entity, Ct));
        Assert.Same(entity, await decorator.UpsertAsync(entity, Ct));
        Assert.Same(entity, await decorator.UpdateAsync(entity, Ct));
        Assert.Same(entity, await decorator.UpdateAsync(entity, ConcurrencyMode.Strict, Ct));
        Assert.Equal("etag", await decorator.UpdateAsync("p|r", build, Ct));
        Assert.Same(entity, await decorator.OneAsync("p|r", Ct));
        Assert.True(await decorator.ExistsAsync("p|r", Ct));
        Assert.Same(batch, await decorator.QueryAsync("p", Ct));
        Assert.Same(batch, await decorator.QueryAsync(options, Ct));
        Assert.Same(batch, await decorator.QueryAsync(predicate, "p", 3, Ct));
        Assert.Same(stream, decorator.QueryStreamAsync(options, Ct));
        Assert.Same(stream, decorator.QueryStreamAsync(predicate, "p", 3, Ct));
        Assert.Same(page, await decorator.QueryPageAsync(options, Ct));
        Assert.True(await decorator.AnyAsync(predicate, "p", Ct));
        Assert.Equal(1, await decorator.CreateBatchAsync(batch, Ct));
        Assert.Equal(2, await decorator.UpsertBatchAsync(batch, Ct));
        Assert.Equal(7, await decorator.CountAsync("p", Ct));

        await inner.Received(1).DeleteAsync("p|r", Ct);
        // One call of the inner store per member of the interface: nothing was answered by the base class.
        Assert.Equal(typeof(IStorage<TestEntity>).GetMethods().Length, inner.ReceivedCalls().Count());
    }

    [Fact]
    public void EveryMember_IsVirtual_SoASubclassOverridesOnlyWhatItChanges()
    {
        var map = typeof(DelegatingStorage<TestEntity>).GetInterfaceMap(typeof(IStorage<TestEntity>));

        Assert.All(map.TargetMethods, m => Assert.True(m is { IsVirtual: true, IsFinal: false }, m.Name + " must be overridable"));
    }

    [Fact]
    public async Task AnOverride_ReplacesOneMember_AndLeavesTheRestForwarding()
    {
        var inner = new InMemoryStorage<TestEntity>();
        var decorator = new CountingCreates(inner);

        await decorator.CreateAsync(new TestEntity { Id = "p|r", Name = "n" }, Ct);

        Assert.Equal(1, decorator.Creates);
        Assert.Equal("n", (await decorator.OneAsync("p|r", Ct))!.Name);
        Assert.True(await inner.ExistsAsync("p|r", Ct));
    }

    private sealed class CountingCreates(IStorage<TestEntity> inner) : DelegatingStorage<TestEntity>(inner)
    {
        public int Creates { get; private set; }

        public override Task<TestEntity> CreateAsync(TestEntity entity, CancellationToken ct = default)
        {
            Creates++;
            return base.CreateAsync(entity, ct);
        }
    }
}
