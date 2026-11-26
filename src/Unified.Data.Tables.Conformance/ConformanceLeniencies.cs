namespace Unified.Data.Tables.Conformance;

/// <summary>
/// The named leniencies a store factory can declare (<see cref="IConformanceStoreFactory.Leniencies"/>).
/// Each names one rule of the contract; declaring it turns a failure of the scenarios about that rule
/// into a <see cref="ConformanceOutcome.Lenient"/> result. A hand-written fake declares the ones it does
/// not keep yet and removes them one by one; the list that is left is what separates it from a provider.
/// The name of a single scenario (<see cref="ConformanceScenario.Name"/>) is a leniency too, for a
/// difference no name here covers.
/// </summary>
/// <remarks>
/// A leniency never hides a scenario that passes, and never changes what a scenario checks. The scenarios
/// a name covers are those whose <see cref="ConformanceScenario.Leniencies"/> list it
/// (<see cref="ConformanceSuite.CoveredBy"/>).
/// </remarks>
public static class ConformanceLeniencies
{
    /// <summary>
    /// The store does not refuse the wildcard ETag <c>"*"</c> on a whole-entity update. Until 0.10.0 the
    /// Azure provider treated it as "whatever the version" and the in-memory one as a version that never
    /// matches; the contract is that it is refused, because it is neither a version nor a stated
    /// <see cref="ConcurrencyMode.LastWriterWins"/>.
    /// </summary>
    public const string WildcardETagOnUpdate = "wildcard-etag-on-update";

    /// <summary>
    /// The store accepts a cell larger than the 64 KiB a table cell holds (an emulator run with relaxed
    /// checks does), where the contract is a <see cref="StorageRequestRejectedException"/>.
    /// </summary>
    public const string OversizedCellAccepted = "oversized-cell-accepted";

    /// <summary>
    /// The store accepts a key character Azure Tables refuses (<c>/</c>, <c>\</c>, <c>#</c>, <c>?</c>, a
    /// control character), where the contract is a <see cref="StorageRequestRejectedException"/>.
    /// </summary>
    public const string KeyRulesNotEnforced = "key-rules-not-enforced";

    /// <summary>
    /// The store ignores <see cref="QueryOptions.FromRowKey"/> and <see cref="QueryOptions.ToRowKey"/>
    /// and returns the whole partition: what an <see cref="IStorage{T}"/> implementation written against
    /// 0.9.2 or earlier does. A store that refuses a bounded query with
    /// <see cref="NotSupportedException"/> needs no leniency; it is reported as not supported.
    /// </summary>
    public const string RowKeyRangeIgnored = "row-key-range-ignored";

    /// <summary>
    /// The store does not normalize ids (trim, spaces to <c>-</c>, lower case) under
    /// <see cref="IdNormalization.Normalized"/>, or does not treat a one-segment id as partition key and
    /// row key at once.
    /// </summary>
    public const string IdNormalizationDiffers = "id-normalization-differs";

    /// <summary>The store does not return rows in ordinal (PartitionKey, RowKey) order.</summary>
    public const string KeyOrderDiffers = "key-order-differs";

    /// <summary>
    /// The store does not keep the ETag rules: a new ETag on every write, a conflict on a stale one, an
    /// <see cref="InvalidOperationException"/> for an update that needs one and has none, ETags reset by a
    /// batch.
    /// </summary>
    public const string ETagRulesDiffer = "etag-rules-differ";

    /// <summary>
    /// The store does not stamp <see cref="IEntity.CreatedAt"/> and <see cref="IEntity.UpdatedAt"/> as the
    /// providers do, or reports <see cref="IEntity.Timestamp"/> differently (set after a write, missing
    /// on a read).
    /// </summary>
    public const string TimestampsDiffer = "timestamps-differ";

    /// <summary>
    /// The store does not bind a continuation token to the query it was issued for, or accepts a
    /// malformed one.
    /// </summary>
    public const string ContinuationTokenNotBound = "continuation-token-not-bound";

    /// <summary>
    /// The store accepts a predicate Azure Tables cannot filter by (the providers refuse it with
    /// <see cref="NotSupportedException"/>, so that a query that works locally works on Azure).
    /// </summary>
    public const string UntranslatablePredicateAccepted = "untranslatable-predicate-accepted";

    /// <summary>
    /// The store lets a write through, or fails another way, when it is given a token that is already
    /// cancelled.
    /// </summary>
    public const string CancellationNotObserved = "cancellation-not-observed";

    /// <summary>
    /// The store throws another exception type than the provider-neutral one the contract names for a
    /// condition (its own type, a provider's type, a bare <see cref="InvalidOperationException"/>).
    /// </summary>
    public const string FailureTypesDiffer = "failure-types-differ";

    /// <summary>
    /// The store validates arguments differently: it accepts a blank id, a non-positive <c>Take</c>, a
    /// prefix without a partition, or throws another exception type for them.
    /// </summary>
    public const string ArgumentValidationDiffers = "argument-validation-differs";

    /// <summary>
    /// The store does not split a batch into transactions as the providers do: a failing transaction
    /// leaves some of its rows, or a repeated key is not refused.
    /// </summary>
    public const string BatchTransactionsDiffer = "batch-transactions-differ";

    /// <summary>The store hands out, or keeps, an object the caller can still change (an entity, a byte array).</summary>
    public const string RowsNotIsolated = "rows-not-isolated";

    /// <summary>
    /// The store does not honor <see cref="QueryOptions.Select"/>: a projected query returns whole rows.
    /// </summary>
    public const string ProjectionIgnored = "projection-ignored";

    /// <summary>Every name defined here.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        WildcardETagOnUpdate, OversizedCellAccepted, KeyRulesNotEnforced, RowKeyRangeIgnored, IdNormalizationDiffers,
        KeyOrderDiffers, ETagRulesDiffer, TimestampsDiffer, ContinuationTokenNotBound, UntranslatablePredicateAccepted,
        CancellationNotObserved, FailureTypesDiffer, ArgumentValidationDiffers, BatchTransactionsDiffer, RowsNotIsolated,
        ProjectionIgnored,
    ];
}
