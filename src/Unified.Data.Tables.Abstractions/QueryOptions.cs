namespace Unified.Data.Tables;

/// <summary>
/// Bounded query specification for <see cref="IStorage{T}.QueryAsync(QueryOptions, CancellationToken)"/>
/// and <see cref="IStorage{T}.QueryStreamAsync(QueryOptions?, CancellationToken)"/>. Deliberately
/// minimal — no predicates and no ordering (Azure Tables returns rows in PartitionKey,RowKey
/// lexical order; encode any other order into your RowKeys).
/// </summary>
public sealed record QueryOptions
{
    /// <summary>
    /// Scope the query to one partition. Required when <see cref="RowKeyPrefix"/> is set —
    /// a cross-partition RowKey range would be a full table scan wearing a filter.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>
    /// Canonical Azure Tables RowKey range scan: returns rows where
    /// <c>RowKey &gt;= prefix</c> and <c>RowKey &lt; next(prefix)</c>. Normalized to the stored form the
    /// same way ids are on write (trim → spaces to <c>'-'</c> → lower-case), so a natural-form prefix
    /// matches the normalized RowKeys writes produce, consistent with <see cref="Partition"/>.
    /// </summary>
    public string? RowKeyPrefix { get; init; }

    /// <summary>
    /// Lower bound of a RowKey range within <see cref="Partition"/>: only rows whose RowKey is at or after
    /// it (after it, when <see cref="FromInclusive"/> is false) are returned. <c>null</c> (the default)
    /// means no lower bound; any other value, the empty string included, is a bound.
    /// </summary>
    /// <remarks>
    /// A range (this property or <see cref="ToRowKey"/>) needs <see cref="Partition"/> and cannot be
    /// combined with <see cref="RowKeyPrefix"/>; either mistake throws <see cref="ArgumentException"/>
    /// (see <see cref="ValidateRowKeyRange"/>). A bound is normalized the way the store normalizes ids
    /// (see <see cref="RowKeyPrefix"/>; used as written under <c>IdNormalization.AsWritten</c>) and
    /// compared with the stored RowKeys in the table's key order, so pick bounds that are already in
    /// stored form when the order matters. A range whose lower bound is above its upper bound is empty.
    /// <para>
    /// Every provider of this library honors the bounds. An <see cref="IStorage{T}"/> implementation
    /// compiled against 0.9.2 or earlier does not know them and ignores them: it returns the whole
    /// partition (or the <see cref="Take"/> first rows of it).
    /// </para>
    /// </remarks>
    public string? FromRowKey { get; init; }

    /// <summary>
    /// Whether a row whose RowKey equals <see cref="FromRowKey"/> is returned. Default <c>true</c>;
    /// ignored while <see cref="FromRowKey"/> is <c>null</c>.
    /// </summary>
    public bool FromInclusive { get; init; } = true;

    /// <summary>
    /// Upper bound of a RowKey range within <see cref="Partition"/>: only rows whose RowKey is before it
    /// (at or before it, when <see cref="ToInclusive"/> is true) are returned. <c>null</c> (the default)
    /// means no upper bound. The rules of <see cref="FromRowKey"/> apply.
    /// </summary>
    public string? ToRowKey { get; init; }

    /// <summary>
    /// Whether a row whose RowKey equals <see cref="ToRowKey"/> is returned. Default <c>false</c>, so
    /// consecutive ranges <c>[a, b)</c>, <c>[b, c)</c> never overlap; ignored while
    /// <see cref="ToRowKey"/> is <c>null</c>.
    /// </summary>
    public bool ToInclusive { get; init; }

    /// <summary>Whether a RowKey range is asked for: <see cref="FromRowKey"/> or <see cref="ToRowKey"/> is set.</summary>
    public bool HasRowKeyRange => FromRowKey is not null || ToRowKey is not null;

    /// <summary>
    /// Checks the RowKey range the way every provider must before it runs the query: a range needs a
    /// <see cref="Partition"/> and cannot be combined with <see cref="RowKeyPrefix"/>. Does nothing when
    /// no bound is set.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A bound is set and <see cref="Partition"/> is null or whitespace, or <see cref="RowKeyPrefix"/> is set too.
    /// </exception>
    public void ValidateRowKeyRange()
    {
        if (!HasRowKeyRange)
            return;
        if (string.IsNullOrWhiteSpace(Partition))
            throw new ArgumentException(
                "FromRowKey/ToRowKey require Partition — a cross-partition RowKey range is a full table scan wearing a filter.",
                "options");
        if (!string.IsNullOrWhiteSpace(RowKeyPrefix))
            throw new ArgumentException(
                "FromRowKey/ToRowKey cannot be combined with RowKeyPrefix: a prefix is itself a RowKey range. " +
                "Express the prefix as bounds, or drop the bounds.",
                "options");
    }

    /// <summary>
    /// Stop after this many entities. Bounds data transfer (page-size hinted), but note Azure
    /// Tables has no server-side "top N of the whole set" — results are always in lexical
    /// RowKey order, so "most recent N" requires order-encoding RowKeys. For
    /// <see cref="IStorage{T}.QueryPageAsync(QueryOptions, System.Threading.CancellationToken)"/>
    /// this is the page size (default 100, clamped to 1..1000).
    /// </summary>
    public int? Take { get; init; }

    /// <summary>
    /// Resume cursor from a prior <see cref="EntityPage{T}"/> — set only for
    /// <see cref="IStorage{T}.QueryPageAsync(QueryOptions, System.Threading.CancellationToken)"/>.
    /// The token is opaque and bound to the query bounds (partition, RowKey prefix or range, page size) it was
    /// issued for; replaying it against a different query throws. Leave <c>null</c> for the first page.
    /// </summary>
    public string? ContinuationToken { get; init; }

    /// <summary>
    /// Columns to fetch. <c>null</c> (the default) means every column, which is what every prior
    /// version did unconditionally.
    ///
    /// This exists because a table whose rows carry a large binary or text payload cannot afford
    /// <c>SELECT *</c> to answer a question about its scalars. Measured on Azurite over 100k
    /// entities carrying a 4 KiB binary column, a projected scan ran at 44,345 entities/s against
    /// 3,808 for the unprojected one — 11.6x. Client-side field
    /// suppression cannot substitute for this: it drops the value only AFTER the full row has
    /// crossed the wire, so the transfer cost is already paid.
    ///
    /// The implementation always adds back the columns deserialization cannot work without
    /// (<c>PartitionKey</c>, <c>RowKey</c>, <c>Timestamp</c> and the type discriminator), so a caller
    /// cannot accidentally project a row into garbage. The row's version needs no selecting: the
    /// service returns it with every entity, so a projected entity still carries its <c>ETag</c>.
    /// Do not name <c>odata.etag</c> here: it is not a column, and it is dropped from the projection.
    /// </summary>
    public IReadOnlyList<string>? Select { get; init; }
}
