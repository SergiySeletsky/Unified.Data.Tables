#pragma warning disable UDT0001 // a read-only decorator must hide the experimental raw-table API too

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Unified.Data.Tables.Conformance;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.ConformanceKit;

/// <summary>
/// The two decorators the conformance package ships for tests, <see cref="CountingStorage{T}"/> and
/// <see cref="FaultInjectingStorage{T}"/>, their base <see cref="InterceptingStorage{T}"/>, and what a
/// decorator that restricts writes must do about the capability probe.
/// </summary>
public class ConformanceDecoratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConformanceItem Item(string id, string name = "n") => new() { Id = id, Name = name };

    private static ReferenceStorage<ConformanceItem> Reference() =>
        new(new InMemoryStorage<ConformanceItem>(), IdNormalization.Normalized);

    private static async Task<List<T>> ListAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
            items.Add(item);
        return items;
    }

    // ── CountingStorage ───────────────────────────────────────────────────

    [Fact]
    public async Task Counting_CountsEachMemberOnce_AndForwardsIt()
    {
        var counted = new CountingStorage<ConformanceItem>(new InMemoryStorage<ConformanceItem>());
        Expression<Func<ConformanceItem, bool>> any = x => x.Name == "n";

        var created = await counted.CreateAsync(Item("p|a"), Ct);
        await counted.UpsertAsync(Item("p|b"), Ct);
        await counted.UpdateAsync(created, Ct);
        await counted.UpdateAsync(created, ConcurrencyMode.LastWriterWins, Ct);
        await counted.UpdateAsync("p|a", b => b.SetProperty(x => x.Name, "m"), Ct);
        await counted.OneAsync("p|a", Ct);
        await counted.ExistsAsync("p|a", Ct);
        await counted.QueryAsync("p", Ct);
        await counted.QueryAsync(new QueryOptions { Partition = "p" }, Ct);
        await counted.QueryAsync(any, "p", null, Ct);
        await ListAsync(counted.QueryStreamAsync(new QueryOptions { Partition = "p" }, Ct));
        await ListAsync(counted.QueryStreamAsync(any, "p", null, Ct));
        await counted.QueryPageAsync(new QueryOptions { Partition = "p" }, Ct);
        await counted.AnyAsync(any, "p", Ct);
        await counted.CreateBatchAsync([Item("p|c")], Ct);
        await counted.UpsertBatchAsync([Item("p|c")], Ct);
        Assert.Equal(3, await counted.CountAsync("p", Ct));
        await counted.DeleteAsync("p|a", Ct);
        Assert.Equal(2, await counted.DeletePartitionAsync("p", Ct));

        var expected = Enum.GetValues<StorageMember>()
            .Where(m => m is not (StorageMember.DeleteIfMatch or StorageMember.DeleteIfExists or StorageMember.CreateAtomic or StorageMember.UpsertAtomic))
            .ToDictionary(m => m, m => m == StorageMember.Update ? 2L : 1L);
        Assert.Equal(expected, counted.Snapshot());
        Assert.Equal(19, counted.Total);
        Assert.Equal(2, counted.Count(StorageMember.Update));
        Assert.Equal(0, counted.Count(StorageMember.DeleteIfExists));

        counted.Reset();
        Assert.Equal(0, counted.Total);
        Assert.Empty(counted.Snapshot());
    }

    [Fact]
    public async Task Counting_CountsACallThatFails_AndAStreamOnlyWhenItIsRead()
    {
        var counted = new CountingStorage<ConformanceItem>(new InMemoryStorage<ConformanceItem>());
        await counted.CreateAsync(Item("p|a"), Ct);

        await Assert.ThrowsAsync<DuplicateKeyException>(() => counted.CreateAsync(Item("p|a"), Ct));
        Assert.Equal(2, counted.Count(StorageMember.Create));

        var stream = counted.QueryStreamAsync(new QueryOptions { Partition = "p" }, Ct);
        Assert.Equal(0, counted.Count(StorageMember.QueryStream));
        Assert.Single(await ListAsync(stream));
        Assert.Equal(1, counted.Count(StorageMember.QueryStream));
    }

    [Fact]
    public async Task Counting_SeesConditionalDeletesAndAtomicBatches_MadeThroughTheProbe()
    {
        var counted = new CountingStorage<ConformanceItem>(Reference());
        IStorage<ConformanceItem> store = counted;

        await store.CreateAtomicAsync([Item("p|a"), Item("p|b")], Ct);
        await store.UpsertAtomicAsync([Item("p|a")], Ct);
        Assert.Equal(DeleteStatus.Deleted, await store.DeleteIfExistsAsync("p|a", Ct));
        var b = (await store.OneAsync("p|b", Ct))!;
        Assert.Equal(DeleteStatus.Deleted, await store.DeleteIfMatchAsync("p|b", b.ETag!, Ct));

        Assert.Equal(1, counted.Count(StorageMember.CreateAtomic));
        Assert.Equal(1, counted.Count(StorageMember.UpsertAtomic));
        Assert.Equal(1, counted.Count(StorageMember.DeleteIfExists));
        Assert.Equal(1, counted.Count(StorageMember.DeleteIfMatch));
        // The plain members were not called behind the decorator's back, and are not counted for them.
        Assert.Equal(0, counted.Count(StorageMember.Delete));
        Assert.Equal(0, counted.Count(StorageMember.CreateBatch));
        // A capability the decorator does not stand in is the wrapped store's own.
        Assert.Equal("Reference", store.GetInfo().ProviderName);
    }

    [Fact]
    public void AnInterceptingDecorator_NeverClaimsACapabilityTheWrappedStoreLacks()
    {
        // A store as one was written against 0.9.2 implements no side interface. (InMemoryStorage did not
        // either, until it became the row-store engine of 0.10.0; it stood here before.)
        IStorage<ConformanceItem> counted = new CountingStorage<ConformanceItem>(new Capabilities.UneditedImplementer<ConformanceItem>());

        Assert.False(counted.TryGetCapability<ConformanceItem, IConditionalDelete<ConformanceItem>>(out var delete));
        Assert.Null(delete);
        Assert.False(counted.TryGetCapability<ConformanceItem, IAtomicBatch<ConformanceItem>>(out _));
        Assert.False(counted.TryGetCapability<ConformanceItem, IOrphanBlobSweeper>(out _));
        Assert.Throws<NotSupportedException>(() => { _ = counted.DeleteIfExistsAsync("p|a", Ct); });

        // The ones a wrapped store has are passed on: every one of them, for the in-memory store.
        IStorage<ConformanceItem> overMemory = new CountingStorage<ConformanceItem>(new InMemoryStorage<ConformanceItem>());
        Assert.True(overMemory.TryGetCapability<ConformanceItem, IOrphanBlobSweeper>(out _));
        Assert.True(overMemory.TryGetCapability<ConformanceItem, IConditionalDelete<ConformanceItem>>(out _));
        Assert.True(overMemory.TryGetCapability<ConformanceItem, IAtomicBatch<ConformanceItem>>(out _));
        Assert.True(overMemory.TryGetCapability<ConformanceItem, IStorageInfo>(out _));
    }

    // ── FaultInjectingStorage ─────────────────────────────────────────────

    [Fact]
    public async Task FaultBefore_FailsTheNextCalls_WithoutReachingTheStore()
    {
        var memory = new InMemoryStorage<ConformanceItem>();
        var faulty = new FaultInjectingStorage<ConformanceItem>(memory).FailNext(StorageMember.Create, FaultTiming.Before, calls: 2);
        Assert.Equal(2, faulty.Pending(StorageMember.Create));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Assert.ThrowsAsync<TransientStorageException>(() => faulty.CreateAsync(Item("p|a"), Ct));
            Assert.False(failure.MayHaveCommitted);
            Assert.Contains("Create", failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, memory.Count);
        Assert.Equal(0, faulty.Pending(StorageMember.Create));
        // Used up: the third call goes through. Other members were never affected.
        await faulty.CreateAsync(Item("p|a"), Ct);
        Assert.Equal(1, memory.Count);
    }

    [Fact]
    public async Task FaultAfter_IsALostResponse_TheWriteLanded_AndTheCallerSeesATransientFailure()
    {
        var memory = new InMemoryStorage<ConformanceItem>();
        var faulty = new FaultInjectingStorage<ConformanceItem>(memory).FailNext(StorageMember.Create, FaultTiming.After);

        var failure = await Assert.ThrowsAsync<TransientStorageException>(() => faulty.CreateAsync(Item("p|a", "mine"), Ct));

        Assert.True(failure.MayHaveCommitted);
        Assert.Equal("mine", (await memory.OneAsync("p|a", Ct))!.Name);
        // The retry meets its own row.
        await Assert.ThrowsAsync<DuplicateKeyException>(() => faulty.CreateAsync(Item("p|a", "mine"), Ct));
    }

    [Fact]
    public async Task Faults_AreUsedInTheOrderScripted_PerMember_AndCanCarryAnyException()
    {
        var memory = new InMemoryStorage<ConformanceItem>();
        await memory.CreateAsync(Item("p|a"), Ct);
        var faulty = new FaultInjectingStorage<ConformanceItem>(memory)
            .FailNext(StorageMember.One, FaultTiming.Before, failure: () => new TransientStorageException("throttled") { RetryAfter = TimeSpan.FromSeconds(3) })
            .FailNext(StorageMember.One, FaultTiming.After, failure: () => new StorageRequestRejectedException("second"))
            .FailNext(StorageMember.Exists, FaultTiming.Before);

        Assert.Equal(TimeSpan.FromSeconds(3), (await Assert.ThrowsAsync<TransientStorageException>(() => faulty.OneAsync("p|a", Ct))).RetryAfter);
        Assert.Equal("second", (await Assert.ThrowsAsync<StorageRequestRejectedException>(() => faulty.OneAsync("p|a", Ct))).Message);
        Assert.NotNull(await faulty.OneAsync("p|a", Ct));
        Assert.Equal(1, faulty.Pending(StorageMember.Exists));

        faulty.Clear();
        Assert.True(await faulty.ExistsAsync("p|a", Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => faulty.FailNext(StorageMember.One, FaultTiming.Before, calls: 0));
    }

    [Fact]
    public async Task AFaultAfter_StaysInPlace_WhenTheStoreItselfFailsTheCall()
    {
        var memory = new InMemoryStorage<ConformanceItem>();
        await memory.CreateAsync(Item("p|a"), Ct);
        var faulty = new FaultInjectingStorage<ConformanceItem>(memory).FailNext(StorageMember.Create, FaultTiming.After);

        // The store's own failure is the result: no response was "lost".
        await Assert.ThrowsAsync<DuplicateKeyException>(() => faulty.CreateAsync(Item("p|a"), Ct));
        Assert.Equal(1, faulty.Pending(StorageMember.Create));
        await Assert.ThrowsAsync<TransientStorageException>(() => faulty.CreateAsync(Item("p|b"), Ct));
        Assert.True(await memory.ExistsAsync("p|b", Ct));
    }

    [Fact]
    public async Task AStream_FailsWhenItsEnumerationStarts_OrAfterItsLastRow()
    {
        var memory = new InMemoryStorage<ConformanceItem>();
        await memory.CreateBatchAsync([Item("p|a"), Item("p|b")], Ct);
        var faulty = new FaultInjectingStorage<ConformanceItem>(memory).FailNext(StorageMember.QueryStream, FaultTiming.Before);

        var stream = faulty.QueryStreamAsync(new QueryOptions { Partition = "p" }, Ct);
        await Assert.ThrowsAsync<TransientStorageException>(() => ListAsync(stream));

        faulty.FailNext(StorageMember.QueryStream, FaultTiming.After);
        var seen = 0;
        await Assert.ThrowsAsync<TransientStorageException>(async () =>
        {
            await foreach (var _ in faulty.QueryStreamAsync(new QueryOptions { Partition = "p" }, Ct))
                seen++;
        });
        Assert.Equal(2, seen);
    }

    [Fact]
    public async Task ALostResponse_OfAConditionalDelete_CanBeScripted_ThroughTheProbe()
    {
        var reference = Reference();
        await reference.CreateAsync(Item("p|a"), Ct);
        IStorage<ConformanceItem> faulty = new FaultInjectingStorage<ConformanceItem>(reference).FailNext(StorageMember.DeleteIfExists, FaultTiming.After);

        var lost = await Assert.ThrowsAsync<TransientStorageException>(() => faulty.DeleteIfExistsAsync("p|a", Ct));

        Assert.True(lost.MayHaveCommitted);
        // The delete landed; the retry cannot tell that it was its own: on a provider that retries inside
        // the call this is the case DeleteStatus.Indeterminate exists for.
        Assert.Equal(DeleteStatus.NotFound, await faulty.DeleteIfExistsAsync("p|a", Ct));
    }

    // ── a decorator that restricts writes ─────────────────────────────────

    /// <summary>Refuses every write of <see cref="IStorage{T}"/>, and nothing else: what a first attempt at a read-only decorator looks like.</summary>
    private class NaiveReadOnly(IStorage<ConformanceItem> inner) : DelegatingStorage<ConformanceItem>(inner)
    {
        private static InvalidOperationException ReadOnly() => new("This store is read-only.");

        public override Task DeleteAsync(string id, CancellationToken ct = default) => throw ReadOnly();

        public override Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default) => throw ReadOnly();

        public override Task<ConformanceItem> CreateAsync(ConformanceItem entity, CancellationToken ct = default) => throw ReadOnly();

        public override Task<ConformanceItem> UpsertAsync(ConformanceItem entity, CancellationToken ct = default) => throw ReadOnly();

        public override Task<ConformanceItem> UpdateAsync(ConformanceItem entity, CancellationToken ct = default) => throw ReadOnly();

        public override Task<ConformanceItem> UpdateAsync(ConformanceItem entity, ConcurrencyMode mode, CancellationToken ct = default) => throw ReadOnly();

        public override Task<string> UpdateAsync(string id, Action<UpdateBuilder<ConformanceItem>> builderAction, CancellationToken ct = default) => throw ReadOnly();

        public override Task<int> CreateBatchAsync(IReadOnlyCollection<ConformanceItem> entities, CancellationToken ct = default) => throw ReadOnly();

        public override Task<int> UpsertBatchAsync(IReadOnlyCollection<ConformanceItem> entities, CancellationToken ct = default) => throw ReadOnly();
    }

    /// <summary>The same, and it hides the capabilities that write: the probe is part of the store's surface.</summary>
    private sealed class ReadOnly(IStorage<ConformanceItem> inner) : NaiveReadOnly(inner)
    {
        public override bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
        {
            if (typeof(TCap) == typeof(IConditionalDelete<ConformanceItem>) || typeof(TCap) == typeof(IAtomicBatch<ConformanceItem>)
                || typeof(TCap) == typeof(IOrphanBlobSweeper) || typeof(TCap) == typeof(IRawTable) || typeof(TCap) == typeof(ITableStore))
            {
                capability = null;
                return false;
            }
            return base.TryGetCapability(out capability);
        }
    }

    [Fact]
    public async Task ADecoratorThatOnlyOverridesTheWrites_IsBypassedByTheCapabilities()
    {
        // Pinned on purpose: DelegatingStorage hands out the wrapped store's own capability objects, so
        // overriding the IStorage<T> members alone does not make a store read-only.
        var reference = Reference();
        await reference.CreateAsync(Item("p|a"), Ct);
        IStorage<ConformanceItem> naive = new NaiveReadOnly(reference);

        await Assert.ThrowsAsync<InvalidOperationException>(() => naive.DeleteAsync("p|a", Ct));
        Assert.Equal(DeleteStatus.Deleted, await naive.DeleteIfExistsAsync("p|a", Ct));
        await naive.CreateAtomicAsync([Item("p|b")], Ct);

        Assert.False(await reference.ExistsAsync("p|a", Ct));
        Assert.True(await reference.ExistsAsync("p|b", Ct));
    }

    [Fact]
    public async Task ADecoratorThatRestrictsWrites_MustAlsoAnswerTheProbe_AndThenNothingGetsPast()
    {
        var reference = Reference();
        await reference.CreateAsync(Item("p|a"), Ct);
        IStorage<ConformanceItem> readOnly = new ReadOnly(reference);

        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.DeleteAsync("p|a", Ct));
        Assert.Throws<NotSupportedException>(() => { _ = readOnly.DeleteIfExistsAsync("p|a", Ct); });
        Assert.Throws<NotSupportedException>(() => { _ = readOnly.DeleteIfMatchAsync("p|a", "W/\"1\"", Ct); });
        Assert.Throws<NotSupportedException>(() => { _ = readOnly.CreateAtomicAsync([Item("p|b")], Ct); });
        Assert.Throws<NotSupportedException>(() => { _ = readOnly.UpsertAtomicAsync([Item("p|a", "changed")], Ct); });
        Assert.False(readOnly.TryGetCapability<ConformanceItem, IRawTable>(out _));

        Assert.Equal("n", (await readOnly.OneAsync("p|a", Ct))!.Name);
        Assert.Equal(1, await reference.CountAsync(null, Ct));
        // What does not write stays visible.
        Assert.Equal("Reference", readOnly.GetInfo().ProviderName);

        // And the kit agrees: the write capabilities of such a store are "not supported", not failures.
        var results = await ConformanceSuite.RunAllAsync(new ReadOnlyOverReference(), ConformanceTier.Capabilities, Ct);
        Assert.Null(ConformanceSuite.DescribeFailures(results));
        Assert.All(results.Where(r => r.Scenario.Name.StartsWith("Capabilities.ConditionalDelete.", StringComparison.Ordinal)),
            r => Assert.Equal(ConformanceOutcome.NotSupported, r.Outcome));
    }

    /// <summary>A read-only view for the capability tier: scenarios that need a write capability end as "not supported".</summary>
    private sealed class ReadOnlyOverReference : IConformanceStoreFactory
    {
        public string Name => "ReadOnly";

        public IReadOnlyCollection<string> Leniencies => [];

        public Task<ConformanceStore<T>> CreateStoreAsync<T>(ConformanceStoreSettings settings, CancellationToken ct = default)
            where T : class, IEntity, new() =>
            typeof(T) == typeof(ConformanceItem)
                ? Task.FromResult((ConformanceStore<T>)(object)new ConformanceStore<ConformanceItem>(new HidingWrites(Reference())))
                : throw new NotSupportedException("Only the plain entity has a read-only view here.");

        public Task<ConformancePolymorphicStore<TBase>> CreatePolymorphicStoreAsync<TBase>(CancellationToken ct = default)
            where TBase : class => throw new NotSupportedException("No polymorphic store.");
    }

    /// <summary>Hides the conditional delete and nothing else, so every other capability scenario still runs through the decorator.</summary>
    private sealed class HidingWrites(IStorage<ConformanceItem> inner) : DelegatingStorage<ConformanceItem>(inner)
    {
        public override bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
        {
            if (typeof(TCap) == typeof(IConditionalDelete<ConformanceItem>))
            {
                capability = null;
                return false;
            }
            return base.TryGetCapability(out capability);
        }
    }
}
