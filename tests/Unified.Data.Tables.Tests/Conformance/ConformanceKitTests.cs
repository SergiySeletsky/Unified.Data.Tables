using Unified.Data.Tables.Conformance;
using Unified.Data.Tables.Conformance.Xunit;

namespace Unified.Data.Tables.Tests.ConformanceKit;

/// <summary>
/// The whole kit against the reference implementation of the 0.10.0 capabilities
/// (<see cref="ReferenceStorage{T}"/>): what proves the capability tier before a provider implements it.
/// </summary>
public sealed class ReferenceConformanceTests : StorageConformanceTests
{
    protected override IConformanceStoreFactory Factory { get; } = new ReferenceConformanceFactory();
}

/// <summary>
/// The kit itself: what it lists, what each outcome means, and which scenarios each store of this
/// repository passes. The xUnit classes report "not supported" and "lenient" as skipped tests, so the
/// exact set of scenarios a provider does not pass is pinned here: a scenario that stops passing, and a
/// leniency that is no longer needed, both fail.
/// </summary>
public class ConformanceKitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] CapabilityScenarios =
        [.. ConformanceSuite.ForTier(ConformanceTier.Capabilities).Select(s => s.Name)];

    // The capability scenarios that run on every store: they are about the probe's answer for a store
    // without the capability.
    private static readonly string[] RunWithoutTheCapability =
    [
        "Capabilities.StorageInfo.GetInfo_NeverThrows",
        "Capabilities.StorageInfo.Polymorphic_GetInfo_NeverThrows",
    ];

    private static string Describe(IEnumerable<ConformanceResult> results) =>
        string.Join(Environment.NewLine, results.Where(r => r.Outcome != ConformanceOutcome.Passed).Select(r => r.ToString()));

    private static IEnumerable<string> Names(IEnumerable<ConformanceResult> results, ConformanceOutcome outcome) =>
        results.Where(r => r.Outcome == outcome).Select(r => r.Scenario.Name).Order(StringComparer.Ordinal);

    // ── the list ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryTier_HasScenarios_WithUniqueStableNames()
    {
        Assert.Equal(ConformanceSuite.All.Count, ConformanceSuite.All.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var tier in Enum.GetValues<ConformanceTier>())
        {
            var scenarios = ConformanceSuite.ForTier(tier);
            Assert.NotEmpty(scenarios);
            Assert.All(scenarios, s =>
            {
                Assert.Equal(tier, s.Tier);
                Assert.StartsWith(tier + ".", s.Name, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(s.Description));
                Assert.Same(s, ConformanceSuite.Get(s.Name));
                Assert.Equal(s.Name, s.ToString());
            });
        }
        Assert.Equal(ConformanceSuite.All, Enum.GetValues<ConformanceTier>().SelectMany(ConformanceSuite.ForTier));
        Assert.Throws<ArgumentException>(() => ConformanceSuite.Get("Logical.NoSuchScenario"));
    }

    [Fact]
    public void EveryNamedLeniency_CoversAScenario_AndEveryScenarioLeniencyIsNamed()
    {
        Assert.Equal(ConformanceLeniencies.All.Count, ConformanceLeniencies.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ConformanceLeniencies.All, leniency => Assert.NotEmpty(ConformanceSuite.CoveredBy(leniency)));
        Assert.All(ConformanceSuite.All.SelectMany(s => s.Leniencies), leniency => Assert.Contains(leniency, ConformanceLeniencies.All));

        // A scenario's own name is the leniency for that scenario alone.
        var one = ConformanceSuite.Get("Logical.Create.StoresTheRow_AndReturnsItWithAnETag");
        Assert.Equal([one], ConformanceSuite.CoveredBy(one.Name));
        Assert.Empty(ConformanceSuite.CoveredBy("no-such-leniency"));
    }

    // ── which scenarios each store passes ─────────────────────────────────

    [Fact]
    public async Task TheInMemoryProvider_PassesEveryScenario_ButTheCapabilitiesItDoesNotHaveYet()
    {
        var results = await ConformanceSuite.RunAllAsync(new InMemoryConformanceFactory(), Ct);

        Assert.True(ConformanceSuite.DescribeFailures(results) is null, ConformanceSuite.DescribeFailures(results));
        Assert.Equal(ConformanceSuite.All.Count, results.Count);
        // No side interface is implemented yet (the in-memory package of 0.10.0 adds them all).
        Assert.Equal(CapabilityScenarios.Except(RunWithoutTheCapability).Order(StringComparer.Ordinal), Names(results, ConformanceOutcome.NotSupported));
        // No declared difference is left: the wildcard ETag on an update is refused (B5).
        Assert.Empty(Names(results, ConformanceOutcome.Lenient));
        Assert.Empty(ConformanceSuite.UnneededLeniencies(new InMemoryConformanceFactory(), results));
    }

    [Fact]
    public async Task TheAzureProvider_OnAzurite_PassesEveryScenario_TheCapabilitiesIncluded()
    {
        Assert.SkipUnless(await AzuriteConformanceFactory.AvailableAsync(), AzuriteConformanceFactory.SkipReason);

        var results = await ConformanceSuite.RunAllAsync(new AzuriteConformanceFactory(), Ct);

        Assert.True(ConformanceSuite.DescribeFailures(results) is null, ConformanceSuite.DescribeFailures(results));
        Assert.Equal(ConformanceSuite.All.Count, results.Count);
        // Passed, not "not supported" and not lenient: the Azure provider exposes every capability through
        // the probe (conditional delete, atomic batch and insert, storage info, the raw table and the
        // table store) and answers a query with RowKey bounds, so every scenario of the kit runs and passes.
        Assert.True(results.All(r => r.Outcome == ConformanceOutcome.Passed), Describe(results));
        Assert.Empty(ConformanceSuite.UnneededLeniencies(new AzuriteConformanceFactory(), results));
    }

    [Fact]
    public async Task TheReferenceImplementation_PassesEveryCapabilityScenario()
    {
        var results = await ConformanceSuite.RunAllAsync(new ReferenceConformanceFactory(), ConformanceTier.Capabilities, Ct);

        Assert.Equal(CapabilityScenarios.Length, results.Count);
        // Passed, not "not supported": the reference exposes every capability through the probe.
        Assert.True(results.All(r => r.Outcome == ConformanceOutcome.Passed), Describe(results));
    }

    // ── a store that exposes a capability must pass all of its scenarios ──

    [Theory]
    [InlineData(ReferenceFlaws.RangeIgnoresTheBounds, "Capabilities.Range.Bounds_SelectTheRowKeysBetweenThem")]
    [InlineData(ReferenceFlaws.RangeIgnoresTheBounds, "Capabilities.Range.StreamAndPages_HonourTheBounds")]
    [InlineData(ReferenceFlaws.RangeIgnoresTheBounds, "Capabilities.Range.Bounds_AreNormalizedLikeIds")]
    [InlineData(ReferenceFlaws.DeleteIfMatchIgnoresTheETag, "Capabilities.ConditionalDelete.IfMatch_DeletesThatVersionOnly")]
    [InlineData(ReferenceFlaws.AtomicBatchSplitsPartitions, "Capabilities.AtomicBatch.BatchThatCannotBeOneTransaction_ThrowsBatchNotAtomic_AndStoresNothing")]
    public async Task AStoreThatGetsACapabilityWrong_FailsTheScenarioAboutIt(ReferenceFlaws flaw, string scenario)
    {
        var broken = new ReferenceConformanceFactory(flaw);

        var failure = await Assert.ThrowsAsync<ConformanceAssertionException>(() => ConformanceSuite.Get(scenario).RunAsync(broken, Ct));

        // Readable on its own: the store, the scenario, and what was expected.
        Assert.StartsWith($"[{broken.Name}] {scenario}: expected ", failure.Message, StringComparison.Ordinal);
        var result = await ConformanceSuite.Get(scenario).TryRunAsync(broken, Ct);
        Assert.Equal(ConformanceOutcome.Failed, result.Outcome);
        Assert.False(result.IsAcceptable);
        Assert.StartsWith($"[{broken.Name}] {scenario}: expected ", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImplementerThatIgnoresTheRangeBounds_IsReported_OnceItDeclaresTheLeniency()
    {
        // What an IStorage<T> written against 0.9.2 does with FromRowKey/ToRowKey: the whole partition.
        var declared = new ReferenceConformanceFactory(ReferenceFlaws.RangeIgnoresTheBounds, ConformanceLeniencies.RowKeyRangeIgnored);

        var results = await ConformanceSuite.RunAllAsync(declared, ConformanceTier.Capabilities, Ct);

        Assert.Null(ConformanceSuite.DescribeFailures(results));
        var lenient = results.Where(r => r.Outcome == ConformanceOutcome.Lenient).ToList();
        Assert.Contains(lenient, r => r.Scenario.Name == "Capabilities.Range.Bounds_SelectTheRowKeysBetweenThem");
        Assert.All(lenient, r =>
        {
            Assert.True(r.IsAcceptable);
            Assert.Contains(ConformanceLeniencies.RowKeyRangeIgnored, r.Scenario.Leniencies);
            Assert.StartsWith($"(leniency '{ConformanceLeniencies.RowKeyRangeIgnored}') [", r.Message, StringComparison.Ordinal);
            Assert.IsType<ConformanceAssertionException>(r.Exception);
        });
        // A leniency never hides a scenario that passes: the argument checks still pass as themselves.
        Assert.Equal(ConformanceOutcome.Passed, results.Single(r => r.Scenario.Name == "Capabilities.Range.NeedsAPartition_AndExcludesAPrefix").Outcome);
    }

    [Fact]
    public async Task ALeniencyTheStoreNoLongerNeeds_IsListedForRemoval()
    {
        // A fake that was fixed and still declares what it used to get wrong.
        const string fixedSince = "Logical.Create.StoresTheRow_AndReturnsItWithAnETag";
        var declared = new ReferenceConformanceFactory(ReferenceFlaws.None, ConformanceLeniencies.RowKeyRangeIgnored, fixedSince, "no-such-leniency");

        var results = await ConformanceSuite.RunAllAsync(declared, Ct);

        Assert.Null(ConformanceSuite.DescribeFailures(results));
        Assert.Equal([ConformanceLeniencies.RowKeyRangeIgnored, fixedSince, "no-such-leniency"], ConformanceSuite.UnneededLeniencies(declared, results));
    }

    [Fact]
    public async Task AScenarioName_IsALeniencyForThatScenarioAlone()
    {
        const string scenario = "Capabilities.ConditionalDelete.IfMatch_DeletesThatVersionOnly";
        var declared = new ReferenceConformanceFactory(ReferenceFlaws.DeleteIfMatchIgnoresTheETag, scenario);

        var result = await ConformanceSuite.Get(scenario).RunAsync(declared, Ct);

        Assert.Equal(ConformanceOutcome.Lenient, result.Outcome);
        Assert.StartsWith($"Lenient {scenario}: (leniency '{scenario}') ", result.ToString(), StringComparison.Ordinal);
    }

    // ── outcomes ──────────────────────────────────────────────────────────

    private sealed class NoStores(Exception failure) : IConformanceStoreFactory
    {
        public string Name => "none";

        public IReadOnlyCollection<string> Leniencies => [];

        public Task<ConformanceStore<T>> CreateStoreAsync<T>(ConformanceStoreSettings settings, CancellationToken ct = default)
            where T : class, IEntity, new() => throw failure;

        public Task<ConformancePolymorphicStore<TBase>> CreatePolymorphicStoreAsync<TBase>(CancellationToken ct = default)
            where TBase : class => throw failure;
    }

    [Fact]
    public async Task AFactoryThatCannotBuildAStore_IsNotSupported_AndOneThatFailsIsAFailure()
    {
        var unsupported = await ConformanceSuite.RunAllAsync(new NoStores(new NotSupportedException("no backend here")), Ct);

        Assert.All(unsupported, r =>
        {
            Assert.Equal(ConformanceOutcome.NotSupported, r.Outcome);
            Assert.True(r.IsAcceptable);
            Assert.Null(r.Exception);
            Assert.Contains("no backend here", r.Message, StringComparison.Ordinal);
        });

        var broken = await ConformanceSuite.Get("Polymorphic.Insert.StoresTheRow_AsTheTypeWritten")
            .TryRunAsync(new NoStores(new InvalidOperationException("the backend is down")), Ct);
        Assert.Equal(ConformanceOutcome.Failed, broken.Outcome);
        Assert.Contains("threw System.InvalidOperationException where the contract expects none: the backend is down", broken.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(broken.Exception!.InnerException);
    }

    [Fact]
    public async Task ACancelledRun_ThrowsOperationCanceled_NotAFailedResult()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConformanceSuite.RunAllAsync(new InMemoryConformanceFactory(), cts.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ConformanceSuite.RunAllAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ConformanceSuite.All[0].RunAsync(null!, Ct));
    }

    [Fact]
    public async Task AStoreIsDisposed_WhenItsScenarioEnds_AlsoWhenItFails()
    {
        var disposed = 0;
        var factory = new Tracking(() => disposed++);

        await ConformanceSuite.Get("Logical.One.MissingRow_ReturnsNull").RunAsync(factory, Ct);
        Assert.Equal(1, disposed);
        // Two stores in one scenario: both are released.
        await ConformanceSuite.Get("Logical.Count.CountsTheTableAndAPartition").RunAsync(factory, Ct);
        Assert.Equal(3, disposed);
        // A scenario that fails (a store that reads nothing back): released all the same.
        var failing = new Tracking(() => disposed++, readsNothing: true);
        Assert.Equal(ConformanceOutcome.Failed, (await ConformanceSuite.Get("Logical.Create.StoresTheRow_AndReturnsItWithAnETag").TryRunAsync(failing, Ct)).Outcome);
        Assert.Equal(4, disposed);
    }

    // A store whose point read finds nothing: what makes a scenario fail for the test above.
    private sealed class ReadsNothing<T>(IStorage<T> inner) : DelegatingStorage<T>(inner) where T : class, IEntity, new()
    {
        public override Task<T?> OneAsync(string id, CancellationToken ct = default) => Task.FromResult<T?>(null);
    }

    private sealed class Tracking(Action onDispose, bool readsNothing = false) : IConformanceStoreFactory
    {
        public string Name => "tracking";

        public IReadOnlyCollection<string> Leniencies => [];

        public Task<ConformanceStore<T>> CreateStoreAsync<T>(ConformanceStoreSettings settings, CancellationToken ct = default)
            where T : class, IEntity, new()
        {
            IStorage<T> store = InMemoryConformanceFactory.Create<T>(settings);
            return Task.FromResult(new ConformanceStore<T>(readsNothing ? new ReadsNothing<T>(store) : store, dispose: () =>
            {
                onDispose();
                return ValueTask.CompletedTask;
            }));
        }

        public Task<ConformancePolymorphicStore<TBase>> CreatePolymorphicStoreAsync<TBase>(CancellationToken ct = default)
            where TBase : class => throw new NotSupportedException();
    }
}
