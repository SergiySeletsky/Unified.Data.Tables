namespace Unified.Data.Tables;

/// <summary>How a cache entry's TTL is applied.</summary>
public enum CacheExpirationMode
{
    /// <summary>TTL resets on every read — a hot entry never expires. Single-writer-process friendly.</summary>
    Sliding,

    /// <summary>
    /// TTL runs from the write — bounds how stale a read can be, which is the correct mode when
    /// OTHER processes write to the same tables (a sliding entry that keeps being read would keep
    /// serving their writes' pre-image forever).
    /// </summary>
    Absolute,
}

/// <summary>Immutable cache policy for one entity type (or the global default).</summary>
public sealed record CachePolicy
{
    /// <summary>No caching: every read hits Table Storage. For processes that share tables with other writers.</summary>
    public static CachePolicy Disabled { get; } = new() { Enabled = false };

    /// <summary>Sliding-TTL caching (the 0.2.x behaviour at 1 hour).</summary>
    public static CachePolicy Sliding(TimeSpan ttl) => new() { Enabled = true, Ttl = ttl, Mode = CacheExpirationMode.Sliding };

    /// <summary>Absolute-TTL caching — staleness is bounded by <paramref name="ttl"/> regardless of read traffic.</summary>
    public static CachePolicy Absolute(TimeSpan ttl) => new() { Enabled = true, Ttl = ttl, Mode = CacheExpirationMode.Absolute };

    /// <summary>Whether reads are served from (and writes maintain) the in-memory cache.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Entry time-to-live. Interpretation depends on <see cref="Mode"/>.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How <see cref="Ttl"/> is applied.</summary>
    public CacheExpirationMode Mode { get; init; } = CacheExpirationMode.Sliding;
}
