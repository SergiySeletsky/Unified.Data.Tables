namespace Unified.Data.Tables;

/// <summary>
/// Controls the optimistic-concurrency behaviour of
/// <see cref="IStorage{T}.UpdateAsync(T, ConcurrencyMode, System.Threading.CancellationToken)"/>.
/// </summary>
public enum ConcurrencyMode
{
    /// <summary>
    /// Adaptive default (the historical behaviour): when the caller supplies
    /// <see cref="Entity.ETag"/>, strict concurrency is enforced (a conflict surfaces as a 412);
    /// otherwise the storage layer falls back to its cached ETag with one stale-cache retry,
    /// finally to an unconditional write.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Require <see cref="Entity.ETag"/> (throws <see cref="System.InvalidOperationException"/>
    /// when absent); a concurrent modification surfaces as a 412 with no retry.
    /// </summary>
    Strict = 1,

    /// <summary>
    /// Unconditional replace (<c>If-Match: *</c>). Explicit, greppable last-writer-wins — prefer
    /// this over nulling <see cref="Entity.ETag"/>, which silently selects the cached-ETag path.
    /// </summary>
    LastWriterWins = 2,
}
