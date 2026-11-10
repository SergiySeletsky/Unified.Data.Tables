using Azure.Storage.Blobs;

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

    /// <summary>
    /// The blob service that holds the container. In DI it takes precedence over the host's registered
    /// client (<see cref="UseRegisteredServiceClient"/>) and over the one the connection-string or credential
    /// overload of <c>AddUnifiedTableStorage</c> derives. A <c>TableStorage&lt;T&gt;</c> constructed directly
    /// uses it only when no <see cref="BlobServiceClient"/> is passed to the constructor: an explicit
    /// constructor argument wins. <c>null</c> (the default) leaves the choice to those. The in-memory store
    /// ignores it.
    /// </summary>
    public BlobServiceClient? ServiceClient { get; set; }

    /// <summary>
    /// When <c>true</c>, <c>AddUnifiedTableStorage</c> uses the <see cref="BlobServiceClient"/> the host
    /// registers in DI (before or after the call) for offload, unless <see cref="ServiceClient"/> is set.
    /// Defaults to <c>false</c>: the library then never resolves the host's client and uses only
    /// <see cref="ServiceClient"/> or the client its connection-string or credential overload derives, which it
    /// keeps to itself, so the host's own <see cref="BlobServiceClient"/> registration is never taken or replaced.
    /// </summary>
    public bool UseRegisteredServiceClient { get; set; }

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
