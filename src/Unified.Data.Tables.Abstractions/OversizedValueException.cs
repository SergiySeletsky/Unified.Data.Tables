using System.Runtime.Serialization;

namespace Unified.Data.Tables;

/// <summary>
/// A value marked <see cref="TableValueSplitAttribute"/> is too large to store in table rows and
/// cannot be offloaded to blob storage.
/// </summary>
/// <remarks>
/// Derives from <see cref="SerializationException"/> so existing <c>catch (SerializationException)</c>
/// handlers around entity writes keep working.
/// </remarks>
public sealed class OversizedValueException : SerializationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="columnName">The column whose value is too large.</param>
    /// <param name="encodedBytes">Size of the stored (encoded) value in bytes.</param>
    /// <param name="reason">Why the value cannot be stored; also decides which fix the message names.</param>
    public OversizedValueException(string columnName, long encodedBytes, string reason)
        : base($"Property '{columnName}' encodes to {encodedBytes:N0} B and cannot be stored: {reason}. "
            + (reason.Contains("BlobOffload.Never")
                ? "Set the property's Offload to BlobOffload.AboveThreshold and configure UnifiedTableStorageOptions.BlobOffload."
                : "Configure UnifiedTableStorageOptions.BlobOffload so large values can be stored in blob storage."))
    {
        ColumnName = columnName;
        EncodedBytes = encodedBytes;
        Reason = reason;
    }

    /// <summary>The column whose value is too large.</summary>
    public string ColumnName { get; }

    /// <summary>Size of the stored (encoded) value in bytes.</summary>
    public long EncodedBytes { get; }

    /// <summary>Why the value cannot be stored.</summary>
    public string Reason { get; }
}
