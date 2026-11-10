namespace Unified.Data.Tables;

/// <summary>
/// Blob offload for large values: where values that are too large for table rows are stored, and
/// when a value marked <see cref="TableValueSplitAttribute"/> moves there. A <see cref="TableBlobContent"/>
/// property is always stored there. Set <see cref="UnifiedTableStorageOptions.BlobOffload"/> to enable it.
/// </summary>
public sealed class BlobOffloadOptions
{
    /// <summary>The blob container that holds offloaded values. Defaults to <c>unified-tables-values</c>.</summary>
    public string ContainerName { get; set; } = "unified-tables-values";

    /// <summary>The largest <see cref="ThresholdBytes"/>: the 4 MiB payload limit of one entity-group transaction.</summary>
    public const long MaxThresholdBytes = 4_194_304;

    private long thresholdBytes = 3_145_728;

    /// <summary>
    /// A split value whose stored (encoded) form is larger than this many bytes moves to blob storage.
    /// Defaults to 3,145,728 (3 MiB); between 1 and <see cref="MaxThresholdBytes"/>. A smaller value
    /// also moves there when its row and continuation rows would not fit one entity-group transaction.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1 or above <see cref="MaxThresholdBytes"/>.</exception>
    public long ThresholdBytes
    {
        get => thresholdBytes;
        set => thresholdBytes = value is >= 1 and <= MaxThresholdBytes
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"ThresholdBytes must be between 1 and {MaxThresholdBytes:N0} (one transaction's payload limit).");
    }

    private TimeSpan cleanupGrace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How recently a blob may have been written or touched and still be deleted by the cleanup that
    /// follows a replace, an upsert, a delete or a partition delete. Defaults to 5 minutes. Every write
    /// that references a blob uploads or touches it first, so a blob a concurrent writer is about to
    /// reference has a recent <c>Last-Modified</c>: cleanup deletes a blob only on condition that it was
    /// not modified within this grace, and leaves a recently touched one for
    /// <c>SweepOrphanBlobsAsync</c>. Keep it longer than a write takes from its blob touch to its row commit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan CleanupGrace
    {
        get => cleanupGrace;
        set => cleanupGrace = value >= TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "CleanupGrace cannot be negative.");
    }
}
