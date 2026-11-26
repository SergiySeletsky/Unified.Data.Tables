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
    /// <remarks>
    /// The names are those the service projects for the <c>$select</c> <c>TableStorage&lt;T&gt;</c> sends,
    /// which joins the entries with commas: an entry that holds a comma names each of its parts, and
    /// the white space around a name is not part of it.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IReadOnlyList<string>? SelectedColumns<T>(QueryOptions? options) where T : class, IEntity, new() =>
        BuildSelect<T>(options)?
            .SelectMany(entry => entry.Split(','))
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The columns a projected query of <typeparamref name="T"/> selects, or null for every column (<see cref="ProjectionColumns{T}.Build"/>).</summary>
    internal static IEnumerable<string>? BuildSelect<T>(QueryOptions? options) => ProjectionColumns<T>.Build(options);

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

/// <summary>
/// The projection rule of one entity type, shared by every provider: which columns a
/// <see cref="QueryOptions.Select"/> sends, and when it reads whole rows instead.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
internal static class ProjectionColumns<T>
{
    // True when T has a member that may be stored as split cells, continuation rows or a blob.
    private static readonly bool LargeValueAware = TypeMetadataCache.IsLargeValueAware(typeof(T));

    /// <summary>
    /// Expands a caller's <see cref="QueryOptions.Select"/> with the columns deserialization cannot
    /// work without, and returns <c>null</c> (meaning every column) when no projection was asked for.
    ///
    /// The trap this closes: a projection of <c>["Content"]</c> is a perfectly valid Tables query
    /// that comes back with no PartitionKey, no RowKey, no Timestamp and no type discriminator, and
    /// deserializes into a silently wrong entity. Projection is a transfer optimisation, never a
    /// change to what an entity IS, so the system columns are not the caller's to omit.
    ///
    /// The row's version is not one of them: <c>odata.etag</c> is not a column. The service returns it
    /// with every entity, selected or not, and a <c>$select</c> that names it may be refused (other Azure
    /// SDKs strip the token), so it is never sent, and one a caller named is dropped.
    /// </summary>
    internal static IEnumerable<string>? Build(QueryOptions? options)
    {
        if (options?.Select is not { Count: > 0 } requested) return null;
        // A large value spans cells and continuation rows: naming it fetches (and assembles) the whole row.
        if (LargeValueAware && SelectsLargeValue(requested)) return null;

        var columns = new List<string>(requested.Count + SystemColumns.Length + SplitSystemColumns.Length);
        columns.AddRange(SystemColumns);
        // _SplitOf is selected for every type, so a continuation row is recognised and skipped whatever
        // type scans the table: a type without large values can share a table with one that has them
        // (until 0.10.0 its projected scans returned those rows as entities).
        columns.AddRange(LargeValueAware ? SplitSystemColumns : PartRowMarker);
        foreach (var column in requested)
        {
            if (!string.Equals(column, ETagToken, StringComparison.Ordinal) && !columns.Contains(column, StringComparer.Ordinal))
                columns.Add(column);
        }

        return columns;
    }

    /// <summary>Columns every projection carries whether the caller asked for them or not.</summary>
    private static readonly string[] SystemColumns = ["PartitionKey", "RowKey", "Timestamp", "_TypeName"];

    /// <summary>The name under which the service returns a row's version; never part of a <c>$select</c>.</summary>
    private const string ETagToken = "odata.etag";

    /// <summary>
    /// True when a projection of a split-aware <typeparamref name="T"/> must fetch the whole row: when
    /// one of the requested names is not known to be an ordinary column. A large value spans cells and
    /// continuation rows (or a blob), so a projection that names it, an object that owns it, or one of
    /// its storage cells (<c>{Prop}__c0</c>, <c>__chunks</c>, <c>__sha256</c>, <c>__enc</c>, <c>__len</c>,
    /// or the <c>{Prop}__Json</c> / <c>{Prop}__GZip</c> cell of an opted-in property) fetches and
    /// assembles the whole row and returns the whole value; so does a name that cannot be resolved,
    /// because it may be a large value the type's column list does not hold (below a repeated type, or a
    /// runtime subtype). Only a projection whose every name is a system column or resolves, through the
    /// declared types and the read-side resolution of runtime owners, to an ordinary member is narrowed.
    /// The blob reference cell <c>{Prop}__blob</c> named alone is narrowed as before and leaves the
    /// property at its default.
    /// </summary>
    private static bool SelectsLargeValue(IReadOnlyCollection<string> requested)
    {
        foreach (var name in requested)
        {
            if (!IsKnownOrdinaryColumn(name))
                return true;
        }
        return false;
    }

    /// <summary>See <see cref="SelectsLargeValue"/>: true only for a name known to be an ordinary column.</summary>
    private static bool IsKnownOrdinaryColumn(string name)
    {
        if (Array.IndexOf(SystemColumns, name) >= 0 || Array.IndexOf(SplitSystemColumns, name) >= 0
            || string.Equals(name, ETagToken, StringComparison.Ordinal))
            return true;
        // A storage cell names its value; the reference cell alone is the one exception, kept as it was.
        if (SplitCodec.TryParseLargeValueColumn(name, out _, out var kind, out _))
            return kind == LargeValueCellKind.BlobReference;
        foreach (var column in TypeMetadataCache.LargeValueColumns(typeof(T)))
        {
            if (string.Equals(name, column, StringComparison.Ordinal)
                || column.StartsWith(name + TableEntityValue.PathDelimiter, StringComparison.Ordinal))
                return false;
        }

        // Not on the list: resolved as a read resolves it (the type is split-aware, so a default instance
        // may be constructed, ruling R3). A name that resolves to no member, to a large-value member, to an
        // object that may own one, or whose resolution fails, is not known to be ordinary.
#pragma warning disable CA1031 // a name that cannot be resolved is simply not known to be ordinary
        try
        {
            if (TypeMetadataCache.IsLargeValueOnRead(typeof(T), name))
                return false;
            return TypeMetadataCache.ReadTarget(typeof(T), name) is { } leaf && !MayOwnColumns(leaf.PropertyType);
        }
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>True when a member of <paramref name="type"/> may be stored as columns of its own (an object the serializer flattens).</summary>
    private static bool MayOwnColumns(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type == typeof(byte[]) || TableEntityValue.IsPrimitive(type) || underlying.IsEnum)
            return false;
        return TableEntitySerializer.FlattensToColumns(type);
    }

    /// <summary>The one column every projection adds so a continuation row can be recognised and skipped.</summary>
    private static readonly string[] PartRowMarker = [SystemColumnNames.SplitOf];

    /// <summary>Columns a split-aware projection adds so continuation rows can be recognised and skipped.</summary>
    private static readonly string[] SplitSystemColumns =
        [SystemColumnNames.SplitOf, SystemColumnNames.SplitIndex, SystemColumnNames.SplitParts];
}
