using Xunit;

namespace Unified.Data.Tables.Conformance.Xunit;

/// <summary>
/// The base of the xUnit v3 conformance classes: it holds the store factory and runs one scenario of
/// <see cref="ConformanceSuite"/> as one test case. Derive from <see cref="StorageConformanceTests"/> to
/// run every tier, or from the class of a single tier
/// (<see cref="LogicalConformanceTests"/>, <see cref="FailureConformanceTests"/>,
/// <see cref="CapabilityConformanceTests"/>, <see cref="PolymorphicConformanceTests"/>,
/// <see cref="LargeValueConformanceTests"/>).
/// </summary>
/// <remarks>
/// A scenario that does not apply (<see cref="ConformanceOutcome.NotSupported"/>) and one covered by a
/// leniency the factory declares (<see cref="ConformanceOutcome.Lenient"/>) are reported as skipped tests,
/// with the reason; a scenario the store fails is a failed test whose message names what was expected.
/// A factory that cannot reach its backend can throw <see cref="NotSupportedException"/> from
/// <see cref="IConformanceStoreFactory.CreateStoreAsync"/> to have its tests skipped.
/// </remarks>
public abstract class ConformanceTestBase
{
    /// <summary>Describes the store under test. Read once per test case.</summary>
    protected abstract IConformanceStoreFactory Factory { get; }

    /// <summary>The names of the scenarios of a tier, as theory rows.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>One row per scenario: its name.</returns>
    protected static IEnumerable<object[]> ScenarioNames(ConformanceTier tier)
    {
        foreach (var scenario in ConformanceSuite.ForTier(tier))
            yield return [scenario.Name];
    }

    /// <summary>Runs the scenario of that name against <see cref="Factory"/>.</summary>
    /// <param name="scenario">A <see cref="ConformanceScenario.Name"/>.</param>
    /// <returns>A task that completes when the scenario passed; it is skipped or fails otherwise.</returns>
    protected async Task RunAsync(string scenario)
    {
        var result = await ConformanceSuite.Get(scenario).RunAsync(Factory, TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (result.Outcome != ConformanceOutcome.Passed)
            throw new ConformanceSkippedException(result);
    }
}

/// <summary>
/// Ends a test case as skipped: xUnit v3 reads any exception whose message starts with its dynamic-skip
/// token as "skip, with the rest of the message as the reason".
/// </summary>
internal sealed class ConformanceSkippedException(ConformanceResult result)
    : Exception("$XunitDynamicSkip$" + result.Outcome + ": " + result.Message);

/// <summary>
/// Every tier of the conformance kit as xUnit v3 theories, one test case per scenario. Derive, return
/// the factory of your store, and the class is the store's contract test.
/// </summary>
public abstract class StorageConformanceTests : ConformanceTestBase
{
    /// <summary>The scenarios of <see cref="ConformanceTier.Logical"/>.</summary>
    public static IEnumerable<object[]> LogicalScenarios => ScenarioNames(ConformanceTier.Logical);

    /// <summary>The scenarios of <see cref="ConformanceTier.Failures"/>.</summary>
    public static IEnumerable<object[]> FailureScenarios => ScenarioNames(ConformanceTier.Failures);

    /// <summary>The scenarios of <see cref="ConformanceTier.Capabilities"/>.</summary>
    public static IEnumerable<object[]> CapabilityScenarios => ScenarioNames(ConformanceTier.Capabilities);

    /// <summary>The scenarios of <see cref="ConformanceTier.Polymorphic"/>.</summary>
    public static IEnumerable<object[]> PolymorphicScenarios => ScenarioNames(ConformanceTier.Polymorphic);

    /// <summary>The scenarios of <see cref="ConformanceTier.LargeValues"/>.</summary>
    public static IEnumerable<object[]> LargeValueScenarios => ScenarioNames(ConformanceTier.LargeValues);

    /// <summary>One scenario of the logical tier.</summary>
    /// <param name="scenario">The scenario name.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [MemberData(nameof(LogicalScenarios), MemberType = typeof(StorageConformanceTests))]
    public Task Logical(string scenario) => RunAsync(scenario);

    /// <summary>One scenario of the failure tier.</summary>
    /// <param name="scenario">The scenario name.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [MemberData(nameof(FailureScenarios), MemberType = typeof(StorageConformanceTests))]
    public Task Failures(string scenario) => RunAsync(scenario);

    /// <summary>One scenario of the capability tier.</summary>
    /// <param name="scenario">The scenario name.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [MemberData(nameof(CapabilityScenarios), MemberType = typeof(StorageConformanceTests))]
    public Task Capabilities(string scenario) => RunAsync(scenario);

    /// <summary>One scenario of the polymorphic tier.</summary>
    /// <param name="scenario">The scenario name.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [MemberData(nameof(PolymorphicScenarios), MemberType = typeof(StorageConformanceTests))]
    public Task Polymorphic(string scenario) => RunAsync(scenario);

    /// <summary>One scenario of the large-value tier.</summary>
    /// <param name="scenario">The scenario name.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [MemberData(nameof(LargeValueScenarios), MemberType = typeof(StorageConformanceTests))]
    public Task LargeValues(string scenario) => RunAsync(scenario);
}

/// <summary>The logical tier alone: every <see cref="IStorage{T}"/> member and the <see cref="StorageExtensions"/> verbs.</summary>
public abstract class LogicalConformanceTests : ConformanceTestBase
{
    /// <summary>The scenarios of <see cref="ConformanceTier.Logical"/>.</summary>
    public static IEnumerable<object[]> Scenarios => ScenarioNames(ConformanceTier.Logical);

    /// <summary>One scenario of the logical tier.</summary>
    /// <param name="scenario">The scenario name.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [MemberData(nameof(Scenarios), MemberType = typeof(LogicalConformanceTests))]
    public Task Logical(string scenario) => RunAsync(scenario);
}

/// <summary>The failure tier alone: the provider-neutral exception of each condition, transient failures and lost responses.</summary>
public abstract class FailureConformanceTests : ConformanceTestBase
{
    /// <summary>The scenarios of <see cref="ConformanceTier.Failures"/>.</summary>
    public static IEnumerable<object[]> Scenarios => ScenarioNames(ConformanceTier.Failures);

    /// <summary>One scenario of the failure tier.</summary>
    /// <param name="scenario">The scenario name.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [MemberData(nameof(Scenarios), MemberType = typeof(FailureConformanceTests))]
    public Task Failures(string scenario) => RunAsync(scenario);
}

/// <summary>The capability tier alone: the side interfaces the store exposes through the capability probe.</summary>
public abstract class CapabilityConformanceTests : ConformanceTestBase
{
    /// <summary>The scenarios of <see cref="ConformanceTier.Capabilities"/>.</summary>
    public static IEnumerable<object[]> Scenarios => ScenarioNames(ConformanceTier.Capabilities);

    /// <summary>One scenario of the capability tier.</summary>
    /// <param name="scenario">The scenario name.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [MemberData(nameof(Scenarios), MemberType = typeof(CapabilityConformanceTests))]
    public Task Capabilities(string scenario) => RunAsync(scenario);
}

/// <summary>The polymorphic tier alone: every <see cref="IPolymorphicStorage{TBase}"/> member.</summary>
public abstract class PolymorphicConformanceTests : ConformanceTestBase
{
    /// <summary>The scenarios of <see cref="ConformanceTier.Polymorphic"/>.</summary>
    public static IEnumerable<object[]> Scenarios => ScenarioNames(ConformanceTier.Polymorphic);

    /// <summary>One scenario of the polymorphic tier.</summary>
    /// <param name="scenario">The scenario name.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [MemberData(nameof(Scenarios), MemberType = typeof(PolymorphicConformanceTests))]
    public Task Polymorphic(string scenario) => RunAsync(scenario);
}

/// <summary>The large-value tier alone: values larger than one cell and one row, and batches of them.</summary>
public abstract class LargeValueConformanceTests : ConformanceTestBase
{
    /// <summary>The scenarios of <see cref="ConformanceTier.LargeValues"/>.</summary>
    public static IEnumerable<object[]> Scenarios => ScenarioNames(ConformanceTier.LargeValues);

    /// <summary>One scenario of the large-value tier.</summary>
    /// <param name="scenario">The scenario name.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [MemberData(nameof(Scenarios), MemberType = typeof(LargeValueConformanceTests))]
    public Task LargeValues(string scenario) => RunAsync(scenario);
}
