namespace Unified.Data.Tables;

/// <summary>
/// The reserved column namespace. A leading <see cref="Prefix"/> marks a <em>system column</em>:
/// never produced from a property, never fed to a property setter.
/// </summary>
/// <remarks>
/// This lives in Abstractions rather than beside the serializer because both the Azure store and
/// the in-memory fake need the predicate, and because a consumer writing sentinel columns has to be
/// able to ask the question too.
/// </remarks>
public static class SystemColumnNames
{
    /// <summary>The character that marks a column as belonging to the storage layer, not the object.</summary>
    public const char Prefix = '_';

    /// <summary>Column holding the stored type discriminator.</summary>
    public const string TypeName = "_TypeName";

    /// <summary>
    /// True when <paramref name="columnName"/> is a system column. Used on every read path to keep
    /// a system column out of a same-named property, and on every write path to validate a raw
    /// column bag.
    /// </summary>
    /// <param name="columnName">The column name to test.</param>
    /// <returns>True when the name starts with <see cref="Prefix"/>.</returns>
    public static bool IsSystemColumn(string columnName) =>
        !string.IsNullOrEmpty(columnName) && columnName[0] == Prefix;
}
