using System.Runtime.Serialization;

namespace Unified.Data.Tables;

/// <summary>
/// A stored large value could not be reassembled faithfully: a chunk, part or blob is missing, or
/// the SHA-256 of the stored bytes does not match. Partial data is never returned.
/// </summary>
/// <remarks>
/// Derives from <see cref="SerializationException"/> so existing <c>catch (SerializationException)</c>
/// handlers around entity reads keep working.
/// </remarks>
public sealed class TableValueIntegrityException : SerializationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="columnName">The column whose value failed verification.</param>
    /// <param name="reason">What was wrong.</param>
    public TableValueIntegrityException(string columnName, string reason)
        : base($"Stored value of column '{columnName}' failed its integrity check: {reason}")
    {
        ColumnName = columnName;
        Reason = reason;
    }

    /// <summary>The column whose value failed verification.</summary>
    public string ColumnName { get; }

    /// <summary>What was wrong.</summary>
    public string Reason { get; }
}
