using System.Diagnostics.CodeAnalysis;

namespace Unified.Data.Tables;

/// <summary>
/// One row to write: where it goes, what object it holds, and any system columns riding alongside
/// the serialized object.
/// </summary>
/// <remarks>
/// <c>Item</c> is nullable on purpose. A null item writes a TYPELESS MARKER ROW — system columns
/// only, no discriminator, no data columns. That is what lets a commit-flag row share one Entity
/// Group Transaction with the typed rows it guards, and it reads back as an entry whose
/// <see cref="PolymorphicEntry{TBase}.Item"/> is null rather than throwing.
/// <para>
/// Every key in <c>SystemColumns</c> must satisfy
/// <see cref="SystemColumnNames.IsSystemColumn"/> and must not be
/// <see cref="SystemColumnNames.TypeName"/>. The prefix is a wire rule, not decoration — see
/// <see cref="SystemColumnNames"/>.
/// </para>
/// </remarks>
/// <typeparam name="TBase">The common base type this row's object is written as.</typeparam>
/// <param name="Key">The row address.</param>
/// <param name="Item">The object to serialize, or null for a typeless marker row.</param>
/// <param name="SystemColumns">Optional <c>'_'</c>-prefixed cells written alongside the object.</param>
public sealed record PolymorphicWrite<TBase>(
    TableKey Key,
    TBase? Item,
    IReadOnlyDictionary<string, object>? SystemColumns = null)
    where TBase : class
{
    /// <summary>A typed row with no system columns — the common case.</summary>
    /// <param name="key">The row address.</param>
    /// <param name="item">The object to serialize.</param>
    public PolymorphicWrite(TableKey key, TBase item)
        : this(key, item, null)
    {
    }

    /// <summary>
    /// A typeless marker row carrying only system columns — the two-phase-commit flag primitive.
    /// </summary>
    /// <param name="key">The row address.</param>
    /// <param name="systemColumns">The <c>'_'</c>-prefixed cells to write.</param>
    /// <returns>A write with no object.</returns>
    // CA1000 (no static members on generic types) exists because a caller normally can't supply the
    // type argument without already having an instance. That doesn't apply here: TBase names WHAT
    // gets written, not something inferred, so the caller supplies it explicitly at every call site
    // the same way `PolymorphicWrite<TBase>` itself already requires — this factory belongs on the
    // type it constructs, not on an unrelated non-generic helper class.
    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "TBase is the explicit type argument every caller already supplies; a " +
            "companion non-generic class would only relocate the same requirement.")]
    public static PolymorphicWrite<TBase> Marker(
        TableKey key, IReadOnlyDictionary<string, object> systemColumns)
    {
        Guard.NotNull(systemColumns, nameof(systemColumns));
        return new PolymorphicWrite<TBase>(key, null, systemColumns);
    }
}
