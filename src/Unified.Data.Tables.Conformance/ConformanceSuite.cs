using Unified.Data.Tables.Conformance.Scenarios;

namespace Unified.Data.Tables.Conformance;

/// <summary>
/// The scenarios of the kit: all of them, by tier and by name, and a helper that runs a set and returns
/// one result per scenario. Framework-neutral: loop over <see cref="ForTier"/> from a theory of any test
/// framework and call <see cref="ConformanceScenario.RunAsync"/> (it throws on a failure), or call
/// <see cref="RunAllAsync(IConformanceStoreFactory, CancellationToken)"/> once and assert on the results.
/// </summary>
public static class ConformanceSuite
{
    private static readonly Dictionary<string, ConformanceScenario> ByName = Index();

    /// <summary>Every scenario, ordered by tier and then as declared.</summary>
    public static IReadOnlyList<ConformanceScenario> All { get; } = Build();

    /// <summary>The scenarios of one tier, as declared.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The scenarios; never empty for a defined tier.</returns>
    public static IReadOnlyList<ConformanceScenario> ForTier(ConformanceTier tier)
    {
        var scenarios = new List<ConformanceScenario>();
        foreach (var scenario in All)
        {
            if (scenario.Tier == tier)
                scenarios.Add(scenario);
        }
        return scenarios;
    }

    /// <summary>The scenario of that name.</summary>
    /// <param name="name">A <see cref="ConformanceScenario.Name"/>.</param>
    /// <returns>The scenario.</returns>
    /// <exception cref="ArgumentException">No scenario has the name.</exception>
    public static ConformanceScenario Get(string name) =>
        name is not null && ByName.TryGetValue(name, out var scenario)
            ? scenario
            : throw new ArgumentException($"The conformance kit has no scenario named '{name}'.", nameof(name));

    /// <summary>The scenarios a leniency covers: those that list it, or the one it names.</summary>
    /// <param name="leniency">A <see cref="ConformanceLeniencies"/> name or a scenario name.</param>
    /// <returns>The covered scenarios; empty for a name the kit does not know.</returns>
    public static IReadOnlyList<ConformanceScenario> CoveredBy(string leniency)
    {
        var covered = new List<ConformanceScenario>();
        foreach (var scenario in All)
        {
            if (string.Equals(scenario.Name, leniency, StringComparison.Ordinal) || scenario.Covers(leniency))
                covered.Add(scenario);
        }
        return covered;
    }

    /// <summary>Runs every scenario against the store and returns one result each. Never throws for a failing scenario.</summary>
    /// <param name="factory">Describes the store under test.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The results, in the order of <see cref="All"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static Task<IReadOnlyList<ConformanceResult>> RunAllAsync(IConformanceStoreFactory factory, CancellationToken ct = default) =>
        RunAllAsync(factory, All, ct);

    /// <summary>Runs the scenarios of one tier and returns one result each. Never throws for a failing scenario.</summary>
    /// <param name="factory">Describes the store under test.</param>
    /// <param name="tier">The tier to run.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The results, in declaration order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static Task<IReadOnlyList<ConformanceResult>> RunAllAsync(
        IConformanceStoreFactory factory, ConformanceTier tier, CancellationToken ct = default) =>
        RunAllAsync(factory, ForTier(tier), ct);

    /// <summary>
    /// Runs the given scenarios one after another and returns one result each. Never throws for a failing
    /// scenario: it is a <see cref="ConformanceOutcome.Failed"/> result.
    /// </summary>
    /// <param name="factory">Describes the store under test.</param>
    /// <param name="scenarios">The scenarios to run.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The results, in the order given.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static async Task<IReadOnlyList<ConformanceResult>> RunAllAsync(
        IConformanceStoreFactory factory, IEnumerable<ConformanceScenario> scenarios, CancellationToken ct = default)
    {
        Arg.NotNull(factory, nameof(factory));
        Arg.NotNull(scenarios, nameof(scenarios));

        var results = new List<ConformanceResult>();
        foreach (var scenario in scenarios)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await scenario.TryRunAsync(factory, ct).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>
    /// The failed results as one readable block (one line per scenario), or null when none failed: the
    /// message for a single "the store conforms" assertion over <see cref="RunAllAsync(IConformanceStoreFactory, CancellationToken)"/>.
    /// </summary>
    /// <param name="results">Results of a run.</param>
    /// <returns>The failures, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="results"/> is null.</exception>
    public static string? DescribeFailures(IEnumerable<ConformanceResult> results)
    {
        Arg.NotNull(results, nameof(results));
        var lines = new List<string>();
        foreach (var result in results)
        {
            if (result.Outcome == ConformanceOutcome.Failed)
                lines.Add(result.Message ?? result.Scenario.Name);
        }
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The leniencies a factory declares that a run did not need: no scenario they cover ended as
    /// <see cref="ConformanceOutcome.Lenient"/>. For a fake that is being brought up to the contract this
    /// is the list to delete from its factory; pass the results of a run of every scenario
    /// (<see cref="RunAllAsync(IConformanceStoreFactory, CancellationToken)"/>), since a leniency whose
    /// scenarios did not run always looks unneeded.
    /// </summary>
    /// <param name="factory">The factory the results were produced with.</param>
    /// <param name="results">Results of a run.</param>
    /// <returns>The declared leniencies no result needed, in the order declared; empty when all were needed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> UnneededLeniencies(IConformanceStoreFactory factory, IEnumerable<ConformanceResult> results)
    {
        Arg.NotNull(factory, nameof(factory));
        Arg.NotNull(results, nameof(results));
        var lenient = new List<ConformanceScenario>();
        foreach (var result in results)
        {
            if (result.Outcome == ConformanceOutcome.Lenient)
                lenient.Add(result.Scenario);
        }

        var unneeded = new List<string>();
        foreach (var leniency in factory.Leniencies ?? [])
        {
            var needed = false;
            foreach (var scenario in lenient)
                needed |= string.Equals(scenario.Name, leniency, StringComparison.Ordinal) || scenario.Covers(leniency);
            if (!needed)
                unneeded.Add(leniency);
        }
        return unneeded;
    }

    private static List<ConformanceScenario> Build()
    {
        var all = new List<ConformanceScenario>(ByName.Count);
        all.AddRange(ByName.Values);
        return all;
    }

    private static Dictionary<string, ConformanceScenario> Index()
    {
        // Declaration order is kept: a Dictionary enumerates in insertion order while nothing is removed.
        var index = new Dictionary<string, ConformanceScenario>(StringComparer.Ordinal);
        var builder = new ScenarioBuilder(index);
        LogicalScenarios.Add(builder);
        FailureScenarios.Add(builder);
        CapabilityScenarios.Add(builder);
        PolymorphicScenarios.Add(builder);
        LargeValueScenarios.Add(builder);
        return index;
    }
}

/// <summary>Collects the scenarios as the tier files declare them, and refuses a name used twice.</summary>
internal sealed class ScenarioBuilder(Dictionary<string, ConformanceScenario> index)
{
    private ConformanceTier tier;

    public ScenarioBuilder Tier(ConformanceTier value)
    {
        tier = value;
        return this;
    }

    public void Add(string name, string description, Func<ConformanceContext, Task> body) =>
        Add(name, description, [], body);

    public void Add(string name, string description, string leniency, Func<ConformanceContext, Task> body) =>
        Add(name, description, [leniency], body);

    public void Add(string name, string description, string[] leniencies, Func<ConformanceContext, Task> body)
    {
        var full = tier + "." + name;
        if (index.ContainsKey(full))
            throw new InvalidOperationException($"Two conformance scenarios are named '{full}'.");
        index.Add(full, new ConformanceScenario(tier, full, description, leniencies, body));
    }
}
