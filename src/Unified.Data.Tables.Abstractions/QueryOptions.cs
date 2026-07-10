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
    /// <c>RowKey &gt;= prefix</c> and <c>RowKey &lt; next(prefix)</c>.
    /// </summary>
    public string? RowKeyPrefix { get; init; }

    /// <summary>
    /// Stop after this many entities. Bounds data transfer (page-size hinted), but note Azure
    /// Tables has no server-side "top N of the whole set" — results are always in lexical
    /// RowKey order, so "most recent N" requires order-encoding RowKeys.
    /// </summary>
    public int? Take { get; init; }
}
