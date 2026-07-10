namespace Unified.Data.Tables;

/// <summary>
/// The single definition of the composite-id conventions used by every <see cref="IStorage{T}"/>
/// implementation: <see cref="Entity.Id"/> is <c>"{PartitionKey}|{RowKey}"</c>, split on the FIRST
/// <c>'|'</c> (so a row key may itself contain <c>'|'</c>), and ids are normalized on write.
/// </summary>
public static class EntityId
{
    /// <summary>The partition/row separator within a composite id.</summary>
    public const char Separator = '|';

    /// <summary>Normalizes an id the way the storage layer does on write: trim → spaces to <c>'-'</c> → lower-case.</summary>
    public static string Normalize(string id)
    {
        ThrowIfNull(id);
        return id.Trim().Replace(' ', '-').ToLowerInvariant();
    }

    /// <summary>
    /// Splits a composite id on the first <see cref="Separator"/>. An id without a separator uses
    /// the whole id as both partition and row key.
    /// </summary>
    public static (string PartitionKey, string RowKey) Split(string id)
    {
        ThrowIfNull(id);
        var idx = id.IndexOf(Separator);
        return idx < 0 ? (id, id) : (id.Substring(0, idx), id.Substring(idx + 1));
    }

    private static void ThrowIfNull(string id)
    {
#if NETSTANDARD2_0
        if (id is null) throw new ArgumentNullException(nameof(id));
#else
        ArgumentNullException.ThrowIfNull(id);
#endif
    }

    /// <summary>Combines a partition and row key into a composite id.</summary>
    public static string Combine(string partitionKey, string rowKey) => $"{partitionKey}{Separator}{rowKey}";
}
