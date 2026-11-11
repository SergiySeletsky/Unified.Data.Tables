namespace Unified.Data.Tables;

/// <summary>
/// The reserved column namespace. A leading <see cref="Prefix"/> marks a <em>system column</em>:
/// the storage layer owns it, and it is never fed to a property setter on read.
/// </summary>
/// <remarks>
/// This lives in Abstractions rather than beside the serializer because both the Azure store and
/// the in-memory fake need the predicate, and because a consumer writing sentinel columns has to be
/// able to ask the question too.
/// <para>
/// <b>A property cannot write a system column.</b> <c>'_'</c> is also the column-path delimiter, so
/// the serializer refuses to write any property whose name contains it, a leading one included: a
/// property named <c>_Foo</c> or <c>Foo_Bar</c> fails the write with a
/// <see cref="System.Runtime.Serialization.SerializationException"/> that names the property (since
/// 0.8.1). On read, a column that starts with <see cref="Prefix"/> is skipped instead of being parsed
/// as the property path <c>["Foo"]</c>, so a system column written through a raw column bag never
/// reaches a property named <c>Foo</c>. Keep <see cref="Prefix"/> out of your property names.
/// </para>
/// </remarks>
public static class SystemColumnNames
{
    /// <summary>The character that marks a column as belonging to the storage layer, not the object.</summary>
    public const char Prefix = '_';

    /// <summary>Column holding the stored type discriminator.</summary>
    public const string TypeName = "_TypeName";

    /// <summary>Column on a continuation row naming the row key of the head row it continues.</summary>
    public const string SplitOf = "_SplitOf";

    /// <summary>Column on a continuation row holding its 1-based position among the continuation rows.</summary>
    public const string SplitIndex = "_SplitIndex";

    /// <summary>Column on a head row holding the number of continuation rows that belong to it.</summary>
    public const string SplitParts = "_SplitParts";

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
