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
/// <b>The rule is enforced on READ only, and that asymmetry is deliberate.</b> The serializer's
/// flatten pass names columns after <c>PropertyInfo.Name</c> verbatim, so a public settable property
/// literally named <c>_Foo</c> still writes a column called <c>_Foo</c>; nothing rejects or renames
/// it. Closing the gap on the write path would mean throwing on (or silently renaming) data that has
/// serialized cleanly in every release, which is a larger break than the read-path fix it would be
/// tidying up after. The consequence to know: such a column is now skipped on read instead of being
/// parsed as property path <c>["Foo"]</c>, so a type declaring both <c>_Foo</c> and <c>Foo</c> no
/// longer cross-loads one into the other. Keep <see cref="Prefix"/> out of your property names.
/// </para>
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
