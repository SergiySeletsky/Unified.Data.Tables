namespace Unified.Data.Tables;

/// <summary>
/// Schedule of the hosted orphan-blob sweep registered by
/// <see cref="ServiceCollectionExtensions.AddUnifiedOrphanBlobSweep(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{OrphanBlobSweepOptions}?)"/>.
/// </summary>
public sealed class OrphanBlobSweepOptions
{
    /// <summary>The longest delay the sweep accepts for <see cref="Interval"/> and <see cref="InitialDelay"/> (about 49.7 days).</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    /// <summary>
    /// The pause between the end of one round and the start of the next. Defaults to 1 hour; greater than
    /// zero and at most <see cref="MaxDelay"/>.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Only blobs last modified longer ago than this are deleted (the <c>olderThan</c> of
    /// <see cref="IOrphanBlobSweeper.SweepOrphanBlobsAsync(TimeSpan, CancellationToken)"/>). Defaults to 1 hour;
    /// must not be negative and must exceed the longest write, from its blob upload to its row write.
    /// </summary>
    public TimeSpan OlderThan { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The delay before the first round after the host starts. Defaults to 1 minute, so a restart does not
    /// sweep during startup but a host restarted more often than <see cref="Interval"/> still sweeps; zero
    /// to at most <see cref="MaxDelay"/>.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        if (Interval <= TimeSpan.Zero || Interval > MaxDelay)
            throw new ArgumentOutOfRangeException(nameof(Interval), Interval,
                $"{nameof(OrphanBlobSweepOptions)}.{nameof(Interval)} must be greater than zero and at most {MaxDelay}.");
        if (InitialDelay < TimeSpan.Zero || InitialDelay > MaxDelay)
            throw new ArgumentOutOfRangeException(nameof(InitialDelay), InitialDelay,
                $"{nameof(OrphanBlobSweepOptions)}.{nameof(InitialDelay)} must be between zero and {MaxDelay}.");
        if (OlderThan < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(OlderThan), OlderThan,
                $"{nameof(OrphanBlobSweepOptions)}.{nameof(OlderThan)} must not be negative.");
    }
}
