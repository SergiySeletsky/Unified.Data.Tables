using System.ComponentModel;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Providers;

/// <summary>
/// Storage-provider SPI, not for application code: lets another <c>IStorage</c> implementation (such as
/// the in-memory store) read a row the way <c>TableStorage&lt;T&gt;</c> reads what the service returns for a
/// projected query (<see cref="QueryOptions.Select"/>) or evaluates a server-side filter, without access to
/// this package's internals. Its shape may change in any minor version.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ProjectedRows
{
    private static readonly Dictionary<string, object> NothingResolved = new(StringComparer.Ordinal);

    /// <summary>
    /// The columns <c>TableStorage&lt;T&gt;</c> selects for <paramref name="options"/>: the caller's
    /// <see cref="QueryOptions.Select"/> plus the system columns it always adds; null when the query reads
    /// whole rows (no projection, or one that names a large value of a large-value aware type).
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="options">The query options; null reads whole rows.</param>
    /// <returns>The selected columns, or null for whole rows.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IReadOnlyList<string>? SelectedColumns<T>(QueryOptions? options) where T : class, IEntity, new() =>
        TableStorage<T>.BuildSelect(options)?.ToList();

    /// <summary>
    /// The entity a projected query of <c>TableStorage&lt;T&gt;</c> returns for <paramref name="row"/>: only
    /// the cells <paramref name="columns"/> names (and the keys) are read, and an offloaded value they do not
    /// name keeps its default. The row's version and write time are not stamped.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="row">The whole stored row.</param>
    /// <param name="columns">The selected columns (<see cref="SelectedColumns{T}"/>).</param>
    /// <returns>The projected entity.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static T Read<T>(TableEntity row, IReadOnlyCollection<string> columns) where T : new()
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(columns);
        var selected = new HashSet<string>(columns, StringComparer.Ordinal);
        var projected = new TableEntity(row.PartitionKey, row.RowKey);
        foreach (var (name, value) in row)
        {
            if (name is not ("PartitionKey" or "RowKey" or "odata.etag" or "Timestamp") && selected.Contains(name))
                projected[name] = value;
        }
        return TableEntitySerializer.FromTableEntity<T>(projected, new ValueReadContext(NothingResolved, null, columns));
    }

    /// <summary>
    /// The entity as a server-side filter sees the stored primary <paramref name="row"/>: every cell but the
    /// cells of split or offloaded values, which keep their defaults. Nothing outside the row is read, so a
    /// row whose large value cannot be read still yields its ordinary members (a filter cannot name a large
    /// value).
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="row">The stored primary row.</param>
    /// <returns>The entity a filter is evaluated against.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static T FilterView<T>(TableEntity row) where T : new()
    {
        ArgumentNullException.ThrowIfNull(row);
        var columns = row.Keys.Where(k => !SplitCodec.TryParseLargeValueColumn(k, out _, out _, out _)).ToList();
        return Read<T>(row, columns);
    }
}
