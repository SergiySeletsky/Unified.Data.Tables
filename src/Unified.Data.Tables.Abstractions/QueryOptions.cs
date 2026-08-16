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
    /// The token is opaque and bound to the query bounds (partition, RowKey prefix, page size) it was
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
    /// (<c>PartitionKey</c>, <c>RowKey</c>, <c>Timestamp</c>, <c>odata.etag</c> and the type
    /// discriminator), so a caller cannot accidentally project a row into garbage.
    /// </summary>
    public IReadOnlyList<string>? Select { get; init; }
}
