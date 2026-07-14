namespace Unified.Data.Tables;

/// <summary>
/// Controls the optimistic-concurrency behaviour of
/// <see cref="IStorage{T}.UpdateAsync(T, ConcurrencyMode, System.Threading.CancellationToken)"/>.
/// </summary>
public enum ConcurrencyMode
{
    /// <summary>
    /// Adaptive default: when the caller supplies <see cref="Entity.ETag"/>, strict concurrency is
    /// enforced (a conflict surfaces as <see cref="ConcurrencyConflictException"/> with no retry).
    /// Without an ETag there is no version to check, so since 0.6.0 the call throws
    /// <see cref="System.InvalidOperationException"/> — round-trip the ETag, use
    /// <c>MutateAsync</c>, or say <see cref="LastWriterWins"/> explicitly. (The pre-0.6.0
    /// unconditional fallback survives behind <c>UnifiedTableStorageOptions.ImplicitLastWriterWins</c>
    /// as a migration cushion.)
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Require <see cref="Entity.ETag"/> (throws <see cref="System.InvalidOperationException"/>
    /// when absent); a concurrent modification surfaces as a 412 with no retry.
    /// </summary>
    Strict = 1,

    /// <summary>
    /// Unconditional replace (<c>If-Match: *</c>). Explicit, greppable last-writer-wins — the only
    /// supported way to say "make the row look like this object regardless of its current state"
    /// on the update path (nulling <see cref="Entity.ETag"/> under <see cref="Auto"/> throws).
    /// </summary>
    LastWriterWins = 2,
}
