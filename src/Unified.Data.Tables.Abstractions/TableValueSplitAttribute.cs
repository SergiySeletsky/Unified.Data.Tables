namespace Unified.Data.Tables;

/// <summary>
/// Opts a property (or every string/byte[] property of a class) into lossless storage of values of
/// any size: the value is kept inline when it fits one cell, otherwise split across cells and rows,
/// and finally offloaded to blob storage. Nothing is ever trimmed.
/// </summary>
/// <param name="encoding">How the value is encoded once it has to be split.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, Inherited = true)]
public sealed class TableValueSplitAttribute(SplitEncoding encoding) : Attribute
{
    /// <summary>How the value is encoded once it has to be split.</summary>
    public SplitEncoding Encoding { get; } = encoding;

    /// <summary>Whether the value may be offloaded to blob storage. Defaults to <see cref="BlobOffload.AboveThreshold"/>.</summary>
    public BlobOffload Offload { get; set; }
}
