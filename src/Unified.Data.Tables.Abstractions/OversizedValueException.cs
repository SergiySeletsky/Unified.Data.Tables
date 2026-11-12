using System.Runtime.Serialization;

namespace Unified.Data.Tables;

/// <summary>
/// A value marked <see cref="TableValueSplitAttribute"/> is too large to store in table rows and
/// cannot be offloaded to blob storage, the cells of an entity that cannot leave its main row (its
/// ordinary cells among them) exceed one entity, or the row and the deletes of the continuation rows
/// stored under its key do not fit one entity-group transaction. The main row's cells exceed one entity
/// either by their bytes or by their number: a split value keeps four cells there, so the 252
/// properties of one entity can run out first. The message ends with what can be done.
/// </summary>
/// <remarks>
/// Derives from <see cref="SerializationException"/> so existing <c>catch (SerializationException)</c>
/// handlers around entity writes keep working.
/// </remarks>
public sealed class OversizedValueException : SerializationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="columnName">The column whose value is too large; see <see cref="ColumnName"/>.</param>
    /// <param name="encodedBytes">Size of the stored (encoded) value in bytes.</param>
    /// <param name="reason">Why the value cannot be stored.</param>
    /// <param name="offloadForbiddenByProperty">
    /// True when blob offload is configured but the property forbids it (<see cref="BlobOffload.Never"/>);
    /// decides which fix the message names.
    /// </param>
    public OversizedValueException(string columnName, long encodedBytes, string reason, bool offloadForbiddenByProperty = false)
        : base($"Property '{columnName}' encodes to {encodedBytes:N0} B and cannot be stored: {reason}. "
            + (offloadForbiddenByProperty
                ? "Set the property's Offload to BlobOffload.AboveThreshold and configure UnifiedTableStorageOptions.BlobOffload."
                : "Configure UnifiedTableStorageOptions.BlobOffload so large values can be stored in blob storage."))
    {
        ColumnName = columnName;
        EncodedBytes = encodedBytes;
        Reason = reason;
        OffloadForbiddenByProperty = offloadForbiddenByProperty;
    }

    /// <summary>Creates the exception for a value that blob offload cannot help with.</summary>
    /// <param name="columnName">The column whose value is too large; see <see cref="ColumnName"/>.</param>
    /// <param name="encodedBytes">Size of the stored (encoded) value in bytes.</param>
    /// <param name="reason">Why the value cannot be stored.</param>
    /// <param name="remedy">What to do about it, as a sentence; the message ends with it instead of the advice about blob offload.</param>
    public OversizedValueException(string columnName, long encodedBytes, string reason, string remedy)
        : base($"Property '{columnName}' encodes to {encodedBytes:N0} B and cannot be stored: {reason}. {remedy}")
    {
        ColumnName = columnName;
        EncodedBytes = encodedBytes;
        Reason = reason;
    }

    /// <summary>
    /// True when blob offload is configured and the property's <see cref="TableValueSplitAttribute.Offload"/>
    /// forbids it (<see cref="BlobOffload.Never"/>). False in every other case, also when the property forbids
    /// offload and none is configured.
    /// </summary>
    public bool OffloadForbiddenByProperty { get; }

    /// <summary>
    /// The column whose value is too large. When the bytes of the cells that must stay on the main row are
    /// what does not fit, the column of the largest of them; when their number is (the main row has no
    /// property left for the cells of its split values), the column of the largest split value.
    /// </summary>
    public string ColumnName { get; }

    /// <summary>Size of the stored (encoded) value in bytes.</summary>
    public long EncodedBytes { get; }

    /// <summary>Why the value cannot be stored.</summary>
    public string Reason { get; }
}
