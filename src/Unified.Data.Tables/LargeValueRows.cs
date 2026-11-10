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
}
