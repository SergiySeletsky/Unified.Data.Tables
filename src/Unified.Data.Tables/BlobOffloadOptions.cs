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
    /// A split value whose stored (encoded) form is larger than this many bytes moves to blob storage.
    /// Defaults to 3,145,728 (3 MiB). A smaller value also moves there when its row and continuation
    /// rows would not fit one entity-group transaction.
    /// </summary>
    public long ThresholdBytes { get; set; } = 3_145_728;
}
