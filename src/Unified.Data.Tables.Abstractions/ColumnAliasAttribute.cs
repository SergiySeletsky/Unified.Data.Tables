namespace Unified.Data.Tables;

/// <summary>
/// Declares a legacy column name that deserializes into a property when the canonical column
/// (the property's own name) is absent from the row. Reads only — writes always use the property
/// name, so rows converge to the canonical schema as they are rewritten. Aliases apply to
/// top-level columns (including their <c>__Json</c>/<c>__GZip</c> variants). For a property marked
/// <see cref="TableValueSplitAttribute"/> and for a <see cref="TableBlobContent"/> property the legacy
/// column is also read as split cells, continuation rows and a blob reference; the blob stays under
/// the legacy name until the entity is saved again.
/// </summary>
/// <remarks>
/// Use the property-level form for properties you own. Use the class-level form (property name +
/// alias) to alias a property inherited from a base type such as <see cref="Entity"/>, e.g.
/// <c>[ColumnAlias(nameof(Entity.CreatedAt), "Created")]</c> on the entity class.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class ColumnAliasAttribute : Attribute
{
    /// <summary>Property-level: <paramref name="legacyColumnName"/> aliases the decorated property.</summary>
    public ColumnAliasAttribute(string legacyColumnName)
    {
        LegacyColumnName = legacyColumnName;
    }

    /// <summary>Class-level: <paramref name="legacyColumnName"/> aliases the (possibly inherited) property named <paramref name="propertyName"/>.</summary>
    public ColumnAliasAttribute(string propertyName, string legacyColumnName)
    {
        PropertyName = propertyName;
        LegacyColumnName = legacyColumnName;
    }

    /// <summary>Target property name for the class-level form; <c>null</c> for the property-level form.</summary>
    public string? PropertyName { get; }

    /// <summary>The legacy column name consulted when the canonical column is absent.</summary>
    public string LegacyColumnName { get; }
}
