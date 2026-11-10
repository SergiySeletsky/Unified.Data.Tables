namespace Unified.Data.Tables;

/// <summary>Blob offload configuration (the offload tier makes this public and wires it to the options).</summary>
internal sealed class BlobOffloadOptions
{
    /// <summary>The blob container that holds offloaded values.</summary>
    public string ContainerName { get; set; } = "unified-tables-values";

    /// <summary>Encoded values larger than this many bytes move to blob storage.</summary>
    public long ThresholdBytes { get; set; } = 3_145_728;
}
