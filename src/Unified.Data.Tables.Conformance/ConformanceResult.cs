namespace Unified.Data.Tables.Conformance;

/// <summary>The part of the contract a scenario belongs to.</summary>
public enum ConformanceTier
{
    /// <summary>
    /// What every <see cref="IStorage{T}"/> member and every <see cref="StorageExtensions"/> verb does to
    /// rows: create, read, upsert, update, merge, delete, queries, paging, streams, key order, id
    /// normalization, ETags, timestamps and cancellation.
    /// </summary>
    Logical,

    /// <summary>
    /// Which provider-neutral exception each condition raises (a missing row, a duplicate, a conflict, a
    /// rejected request, a refused key), and what a caller sees after a transient failure or a lost
    /// response.
    /// </summary>
    Failures,

    /// <summary>
    /// The side interfaces a store may expose through the capability probe: conditional delete, atomic
    /// batch, atomic insert, storage info, RowKey range bounds and the raw table. A store without a
    /// capability is reported as <see cref="ConformanceOutcome.NotSupported"/>; one that exposes it must
    /// pass every scenario of it.
    /// </summary>
    Capabilities,

    /// <summary>Every <see cref="IPolymorphicStorage{TBase}"/> member, discriminators and marker rows.</summary>
    Polymorphic,

    /// <summary>
    /// Values larger than one cell and larger than one row, and batches of them, as a caller sees them
    /// (what is read back, what is counted, what a failed batch leaves). The physical layout is checked
    /// only where the factory can read raw rows.
    /// </summary>
    LargeValues,
}

/// <summary>How one scenario ended.</summary>
public enum ConformanceOutcome
{
    /// <summary>The store did what the contract says.</summary>
    Passed,

    /// <summary>
    /// The store did something else. <see cref="ConformanceScenario.RunAsync"/> throws
    /// <see cref="ConformanceAssertionException"/> for it; <see cref="ConformanceSuite.RunAllAsync(IConformanceStoreFactory, CancellationToken)"/>
    /// returns it as a result.
    /// </summary>
    Failed,

    /// <summary>
    /// The scenario did not apply: the store does not expose the capability it is about, or the factory
    /// cannot build the store it needs (no polymorphic store, no blob offload). Not a failure.
    /// </summary>
    NotSupported,

    /// <summary>
    /// The store did something else than the contract says, and the factory declared a leniency that
    /// covers the scenario (<see cref="IConformanceStoreFactory.Leniencies"/>): reported, not failed.
    /// </summary>
    Lenient,
}

/// <summary>What one scenario found on one store.</summary>
public sealed class ConformanceResult
{
    internal ConformanceResult(ConformanceScenario scenario, ConformanceOutcome outcome, string? message, Exception? exception)
    {
        Scenario = scenario;
        Outcome = outcome;
        Message = message;
        Exception = exception;
    }

    /// <summary>The scenario that ran.</summary>
    public ConformanceScenario Scenario { get; }

    /// <summary>How it ended.</summary>
    public ConformanceOutcome Outcome { get; }

    /// <summary>
    /// Why: what the store did instead (<see cref="ConformanceOutcome.Failed"/> and
    /// <see cref="ConformanceOutcome.Lenient"/>), or what is missing
    /// (<see cref="ConformanceOutcome.NotSupported"/>). Null when the scenario passed.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// The <see cref="ConformanceAssertionException"/> behind a <see cref="ConformanceOutcome.Failed"/> or
    /// <see cref="ConformanceOutcome.Lenient"/> result (its inner exception is what the store threw, when
    /// it threw); null otherwise.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>True unless the scenario <see cref="ConformanceOutcome.Failed"/>.</summary>
    public bool IsAcceptable => Outcome != ConformanceOutcome.Failed;

    /// <summary>One line: the outcome, the scenario name and, when there is one, the message.</summary>
    /// <returns>For example <c>NotSupported Capabilities.ConditionalDelete.IfMatch: the store exposes no IConditionalDelete</c>.</returns>
    public override string ToString() =>
        Message is null ? $"{Outcome} {Scenario.Name}" : $"{Outcome} {Scenario.Name}: {Message}";
}

/// <summary>
/// A store did not do what the contract says. The message names the scenario, what was expected and what
/// the store did; when the store threw, that exception is the <see cref="Exception.InnerException"/>.
/// The kit's own exception type: it depends on no test framework, and every framework reports an
/// exception it does not know as a failed test with this message.
/// </summary>
public sealed class ConformanceAssertionException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public ConformanceAssertionException()
        : base("The store does not conform to the contract.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was expected and what the store did.</param>
    public ConformanceAssertionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was expected and what the store did.</param>
    /// <param name="innerException">What the store threw, when it threw.</param>
    public ConformanceAssertionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
