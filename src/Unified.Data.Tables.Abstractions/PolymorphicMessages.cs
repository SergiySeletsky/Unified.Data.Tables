namespace Unified.Data.Tables;

/// <summary>
/// Shared error text for the polymorphic contract, so the Azure store and the in-memory fake throw
/// byte-identical messages — a test written against either implementation documents the same
/// contract. Mirrors <see cref="ConcurrencyMessages"/>.
/// </summary>
internal static class PolymorphicMessages
{
    internal static string MarkerHasNoValue(TableKey key) =>
        $"Row '{key}' is a typeless marker row — it carries system columns only and no " +
        $"'{SystemColumnNames.TypeName}', so it has no object to return. Read Item (which is null " +
        "for a marker) or the raw Columns instead of Value.";

    internal static string NotAssignable(string discriminator, Type baseType) =>
        $"Stored type '{discriminator}' is not assignable to '{baseType.FullName}'. A polymorphic " +
        "read never materializes a type outside its base — this is the gate that stops a stored " +
        "type name from becoming an arbitrary-type deserialization. Point the store at the right " +
        "base type, or register the type on a TypeDiscriminatorMap for the base you intend.";

    internal static string Unresolvable(string discriminator) =>
        $"Stored type '{discriminator}' could not be resolved. With the default " +
        "AssemblyQualifiedTypeDiscriminator this usually means the assembly was renamed, moved or " +
        "strong-named since the row was written — which is exactly why assembly-qualified " +
        "discriminators are discouraged for new tables. Register a TypeDiscriminatorMap with a " +
        "stable token and call WithAssemblyQualifiedFallback() to keep reading legacy rows.";

    internal static string NotSystemColumn(string columnName) =>
        $"Column '{columnName}' is not a system column. A raw column written alongside a " +
        $"serialized object must start with '{SystemColumnNames.Prefix}': the serializer owns the " +
        "un-prefixed column namespace, so an un-prefixed sentinel would collide with a real " +
        "property and be silently overwritten on the next write.";

    internal static string TypeNameNotMergeable() =>
        $"'{SystemColumnNames.TypeName}' cannot be merged. Re-typing an existing row would leave " +
        "the previous type's data columns stranded on it, readable by nothing. Delete the row and " +
        "insert the new shape instead.";

    internal static string EmptyKey(string part) =>
        $"{part} must be a non-empty string. Azure Tables has no concept of an absent key, and an " +
        "empty one addresses a real (and almost certainly unintended) row.";
}
