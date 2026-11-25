using System.Runtime.Serialization;

namespace Unified.Data.Tables;

/// <summary>
/// A property's value does not fit Azure Tables' 64 KB per-cell limit and
/// <see cref="OversizedCellPolicy.Throw"/> is in effect.
///
/// Derives from <see cref="SerializationException"/> deliberately: earlier versions raised a bare
/// <c>SerializationException</c> here, so existing <c>catch (SerializationException)</c> keeps
/// working unchanged. What it adds is the ability to react to THIS failure specifically — which
/// column, how big it actually was, and what the limit is — rather than to every serialization
/// failure alike, and to distinguish "one property is too fat" from "this graph has a cycle".
/// </summary>
public sealed class OversizedCellException : SerializationException
{
    /// <param name="columnName">The column whose value overflowed.</param>
    /// <param name="actualBytes">Encoded size of the value, after any compression attempt.</param>
    /// <param name="limitBytes">The per-cell limit it exceeded.</param>
    public OversizedCellException(string columnName, int actualBytes, int limitBytes)
        : base($"Property '{columnName}' serializes to {actualBytes:N0} B, which exceeds the "
            + $"{limitBytes:N0} B Azure Tables cell limit even after compression "
            + "(OversizedCellPolicy.Throw). Model it as multiple rows, or move the payload to Blob "
            + "storage and keep a reference here. To trim instead — which loses data silently — set "
            + "UnifiedTableStorageOptions.OversizedCells to TrimWithMarker or TrimSilently.")
    {
        ColumnName = columnName;
        ActualBytes = actualBytes;
        LimitBytes = limitBytes;
    }

    /// <summary>The column whose value overflowed.</summary>
    public string ColumnName { get; }

    /// <summary>Encoded size of the value, after any compression attempt.</summary>
    public int ActualBytes { get; }

    /// <summary>The per-cell limit it exceeded.</summary>
    public int LimitBytes { get; }
}
