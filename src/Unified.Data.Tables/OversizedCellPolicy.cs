namespace Unified.Data.Tables;

/// <summary>
/// What the serializer does when a property's serialized payload exceeds the Azure Tables 64 KB
/// cell cap even after GZip compression. An oversized cell fails the WHOLE entity write
/// (<c>PropertyValueTooLarge</c>), so something has to give — this policy decides what, and how
/// loudly. Configure via <see cref="UnifiedTableStorageOptions.OversizedCells"/> or
/// <see cref="TableEntitySerializer.OversizedCellPolicy"/>.
/// </summary>
public enum OversizedCellPolicy
{
    /// <summary>
    /// Default. Trim the payload to fit (largest list prefix / capped string; a non-list payload is
    /// dropped) and record what happened in a sibling <c>{Column}__Truncated</c> marker cell
    /// (e.g. <c>"kept 125 of 2000 items"</c>), so the loss is visible in the data itself.
    /// </summary>
    TrimWithMarker,

    /// <summary>
    /// Fail the write with <see cref="System.Runtime.Serialization.SerializationException"/> —
    /// for data where silent loss is never acceptable and the caller wants to remodel instead
    /// (e.g. row-per-item).
    /// </summary>
    Throw,

    /// <summary>The pre-0.5.3 behaviour: trim (or drop) with no marker and no signal.</summary>
    TrimSilently,
}
