using System.ComponentModel;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Providers;

/// <summary>
/// Storage-provider SPI, not for application code: lets another <c>IStorage</c>/<c>IPolymorphicStorage</c>
/// implementation (such as the in-memory stores) apply the same large-value rules as this package
/// without access to its internals. Its shape may change in any minor version.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LargeValueRows
{
    /// <summary>
    /// Storage-provider SPI, not for application code. True when <paramref name="row"/> holds any cell
    /// of a split or offloaded value (<c>{Prop}__c{n}</c>, <c>__chunks</c>, <c>__sha256</c>,
    /// <c>__enc</c>, <c>__len</c>, <c>__blob</c>).
    /// </summary>
    /// <param name="row">A row produced by <see cref="TableEntitySerializer"/>.</param>
    /// <returns>Whether the row carries large-value cells.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool HasLargeValueCells(TableEntity row) => TableEntitySerializer.HasLargeValueCells(row);

    /// <summary>
    /// Storage-provider SPI, not for application code. Flattens one partial-update (Merge) value of
    /// <paramref name="ownerType"/> into its columns, refusing a large-value member the way
    /// <c>TableStorage&lt;T&gt;.UpdateAsync</c> does: a path that names a <see cref="TableValueSplitAttribute"/>
    /// or <see cref="TableBlobContent"/> member, or a nested object that carries one.
    /// </summary>
    /// <param name="ownerType">The entity type the update targets.</param>
    /// <param name="columnPath">The flattened column path, e.g. <c>"Inner_Body"</c>.</param>
    /// <param name="value">The new value.</param>
    /// <returns>The columns to merge.</returns>
    /// <exception cref="NotSupportedException">The update would write a large value, or whether it would
    /// cannot be decided (for a large-value aware type whose constructor or a getter throws while the path is
    /// resolved as a read would resolve it; the failure is the inner exception).</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Dictionary<string, object> FlattenPropertyForUpdate(Type ownerType, string columnPath, object value) =>
        TableEntitySerializer.FlattenProperty(ownerType, columnPath, value);

    /// <summary>
    /// Storage-provider SPI, not for application code. Serializes <paramref name="entity"/> as a store of
    /// <paramref name="storeType"/> writes it on the plain path (one row, no large-value form): what
    /// <see cref="TableEntitySerializer.ToTableEntity(object, string, string, bool)"/> writes, except that
    /// the type a read of the row creates is <paramref name="storeType"/>, not the runtime type of
    /// <paramref name="entity"/>. When <paramref name="storeType"/> is not large-value aware, a value in a
    /// large-value member is refused wherever it is met, also in a member only a subclass declares.
    /// </summary>
    /// <param name="entity">The entity to write: an instance of <paramref name="storeType"/> or of a subclass.</param>
    /// <param name="partitionKey">Its partition key.</param>
    /// <param name="rowKey">Its row key.</param>
    /// <param name="storeType">The store's entity type.</param>
    /// <returns>The row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storeType"/> is null.</exception>
    /// <exception cref="NotSupportedException">The entity holds a value the plain path cannot store.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TableEntity ToPlainTableEntity(object entity, string partitionKey, string rowKey, Type storeType) =>
        TableEntitySerializer.ToPlainTableEntity(entity, partitionKey, rowKey, storeType);

    /// <summary>
    /// Storage-provider SPI, not for application code. True when <paramref name="item"/> holds a
    /// <see cref="TableBlobContent"/> in a flattened column (it needs a blob upload to be written).
    /// </summary>
    /// <param name="item">The object about to be serialized.</param>
    /// <returns>Whether it carries blob content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool CarriesBlobContent(object item) => TableEntitySerializer.CarriesBlobContent(item);

    /// <summary>
    /// Storage-provider SPI, not for application code. The <see cref="NotSupportedException"/> message
    /// a polymorphic store uses when an item would need split or offloaded cells.
    /// </summary>
    /// <param name="itemType">The type of the item being written.</param>
    /// <returns>The message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="itemType"/> is null.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static string PolymorphicWriteRefusal(Type itemType)
    {
        ArgumentNullException.ThrowIfNull(itemType);
        return TableEntitySerializer.PolymorphicWriteRefusal(itemType);
    }
}
