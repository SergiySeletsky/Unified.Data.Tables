namespace Unified.Data.Tables;

/// <summary>
/// The outcome of a <c>Try*</c> storage verb. These are EXPECTED branches of concurrent
/// programs — losing a race, finding the row gone, a precondition no longer holding — which is
/// why they are returned rather than thrown. Exceptions remain reserved for programmer errors
/// and infrastructure failures.
/// </summary>
public enum MutationStatus
{
    /// <summary>The write was applied; the result carries the persisted entity.</summary>
    Updated,

    /// <summary>The entity does not exist.</summary>
    NotFound,

    /// <summary>
    /// The transition's precondition did not hold on the FRESH row (e.g. another writer already
    /// performed the transition); the result carries that fresh row so the caller can inspect it.
    /// </summary>
    PreconditionFailed,

    /// <summary>Every attempt lost its optimistic-concurrency race (a genuinely hot row).</summary>
    Conflicted,
}

/// <summary>
/// Result of a <c>Try*</c> storage verb: a status plus, where meaningful, an entity —
/// the persisted row for <see cref="MutationStatus.Updated"/>, the fresh current row for
/// <see cref="MutationStatus.PreconditionFailed"/>, <c>null</c> otherwise.
/// </summary>
public sealed record MutationResult<T>(MutationStatus Status, T? Entity)
{
    /// <summary>Whether the write was applied.</summary>
    public bool Succeeded => Status == MutationStatus.Updated;
}
