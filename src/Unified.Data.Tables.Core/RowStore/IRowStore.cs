using Azure.Data.Tables;

namespace Unified.Data.Tables.Providers;

// The row store: the one thing a local provider (in-memory, SQLite) implements. Everything the typed and
// the polymorphic stores do above stored rows (serialization, large values, the read cache, paging
// cursors, filters, projection, key rules, the service's limits, the provider-neutral failures, the side
// interfaces) is the engine over it (RowStoreStorage<T>, RowStorePolymorphicStorage<TBase>,
// RowStoreRawTable), so every local provider behaves as one.
//
// The shape is the one a database needs, not the one process memory allows: every operation is
// asynchronous, a write is a single call that commits or applies nothing, a range read is pulled a bounded
// page at a time from a resume key, and nothing hands out a reference into the store (a row is a value).

/// <summary>
/// One stored row: its keys and cells as the serializer wrote them (a <see cref="TableEntity"/> without a
/// timestamp or an ETag of its own), the version the store gave its last write and the time of that write.
/// A value by contract: nobody changes <paramref name="Data"/> once a row exists. The engine hands the
/// store a private copy of what a caller gave it, and copies a row's cells before user code or the
/// serializer sees them, so a store may keep and hand out the same instance.
/// </summary>
/// <param name="Data">The keys and the cells; each value one of the eight cell types.</param>
/// <param name="ETag">The opaque version of the row; another one after every write of it.</param>
/// <param name="Timestamp">When the store last wrote the row (UTC).</param>
internal sealed record StoredRow(TableEntity Data, string ETag, DateTimeOffset Timestamp)
{
    /// <summary>The partition key, as stored.</summary>
    public string PartitionKey => Data.PartitionKey;

    /// <summary>The row key, as stored.</summary>
    public string RowKey => Data.RowKey;
}

/// <summary>What one operation of a commit does to its row.</summary>
internal enum RowWriteKind
{
    /// <summary>Store the cells as the whole row (insert or replace, as the condition allows).</summary>
    Put,

    /// <summary>Remove the row.</summary>
    Delete,
}

/// <summary>What must hold for the row before an operation of a commit is applied.</summary>
internal enum RowCondition
{
    /// <summary>Nothing: a put inserts or replaces, a delete of a row that is not there does nothing.</summary>
    None,

    /// <summary>No row has the key (an insert).</summary>
    MustNotExist,

    /// <summary>A row has the key; with <see cref="RowWrite.IfMatch"/>, and it is that version.</summary>
    MustExist,
}

/// <summary>One operation of a commit: a put or a delete of one row of the commit's partition, with its condition.</summary>
/// <param name="Kind">Put or delete.</param>
/// <param name="RowKey">The row key within the commit's partition.</param>
/// <param name="Row">The row of a put, keys included (the store keeps the instance: see <see cref="StoredRow"/>); null for a delete.</param>
/// <param name="Condition">What must hold for the row.</param>
/// <param name="IfMatch">With <see cref="RowCondition.MustExist"/>: the version the row must have; null for any version.</param>
internal sealed record RowWrite(
    RowWriteKind Kind, string RowKey, TableEntity? Row, RowCondition Condition, string? IfMatch = null)
{
    /// <summary>An insert: fails when a row has the key.</summary>
    public static RowWrite Insert(TableEntity row) => new(RowWriteKind.Put, row.RowKey, row, RowCondition.MustNotExist);

    /// <summary>An insert-or-replace.</summary>
    public static RowWrite Upsert(TableEntity row) => new(RowWriteKind.Put, row.RowKey, row, RowCondition.None);

    /// <summary>A replace of a stored row, of the version <paramref name="ifMatch"/> when it is not null.</summary>
    public static RowWrite Replace(TableEntity row, string? ifMatch = null) =>
        new(RowWriteKind.Put, row.RowKey, row, RowCondition.MustExist, ifMatch);

    /// <summary>A delete of a stored row, of the version <paramref name="ifMatch"/> when it is not null.</summary>
    public static RowWrite Delete(string rowKey, string? ifMatch = null) =>
        new(RowWriteKind.Delete, rowKey, null, RowCondition.MustExist, ifMatch);

    /// <summary>A delete that does nothing when no row has the key.</summary>
    public static RowWrite DeleteIfExists(string rowKey) =>
        new(RowWriteKind.Delete, rowKey, null, RowCondition.None);
}

/// <summary>Why a commit applied nothing.</summary>
internal enum RowCommitFailure
{
    /// <summary>The commit was applied.</summary>
    None,

    /// <summary>An operation needs no row at its key, and one is stored.</summary>
    AlreadyExists,

    /// <summary>An operation needs a row at its key, and none is stored.</summary>
    NotFound,

    /// <summary>An operation needs a version of its row, and another one is stored.</summary>
    VersionMismatch,
}

/// <summary>
/// The outcome of a commit: applied as a whole (<see cref="Failure"/> is <see cref="RowCommitFailure.None"/>,
/// with the new version of each put and the time of the commit), or not at all, naming the first
/// operation whose condition did not hold.
/// </summary>
/// <param name="Failure">None when the commit was applied.</param>
/// <param name="FailedIndex">The index of the first operation whose condition did not hold; -1 when applied.</param>
/// <param name="ETags">One entry per operation when applied: the new version of a put, null for a delete.</param>
/// <param name="Timestamp">The time the store stamped on the rows of an applied commit.</param>
internal sealed record RowCommitResult(RowCommitFailure Failure, int FailedIndex, IReadOnlyList<string?> ETags, DateTimeOffset Timestamp)
{
    /// <summary>True when the commit was applied.</summary>
    public bool Committed => Failure == RowCommitFailure.None;

    /// <summary>A commit that applied nothing because of operation <paramref name="index"/>.</summary>
    public static RowCommitResult Failed(RowCommitFailure failure, int index) => new(failure, index, [], default);
}

/// <summary>
/// The rows a range read covers, in the store's order: ordinal partition key, then ordinal row key
/// (UTF-16 code units, as <see cref="string.CompareOrdinal(string, string)"/> orders them).
/// </summary>
/// <param name="PartitionKey">The one partition to read; null for every partition (the bounds then do not apply).</param>
/// <param name="FromRowKey">The lowest row key, or null for the start of the partition.</param>
/// <param name="FromInclusive">Whether a row whose key equals <paramref name="FromRowKey"/> is in the range.</param>
/// <param name="ToRowKey">The highest row key, or null for the end of the partition.</param>
/// <param name="ToInclusive">Whether a row whose key equals <paramref name="ToRowKey"/> is in the range.</param>
/// <param name="After">The resume key: only rows after it (in the store's order) are returned; null to start at the first row of the range.</param>
internal sealed record RowRange(
    string? PartitionKey = null,
    string? FromRowKey = null, bool FromInclusive = true,
    string? ToRowKey = null, bool ToInclusive = false,
    (string PartitionKey, string RowKey)? After = null)
{
    /// <summary>Every row of the table.</summary>
    public static RowRange All { get; } = new();

    /// <summary>The rows of <paramref name="partitionKey"/> whose row key starts with <paramref name="prefix"/> (ordinal).</summary>
    public static RowRange Prefix(string partitionKey, string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return new RowRange(partitionKey);
        // The smallest string above every string with the prefix: the prefix without its trailing
        // U+FFFF characters, its last character raised by one (none when the prefix is all U+FFFF).
        var end = prefix.Length;
        while (end > 0 && prefix[end - 1] == char.MaxValue)
            end--;
        var upper = end == 0 ? null : string.Concat(prefix.AsSpan(0, end - 1), ((char)(prefix[end - 1] + 1)).ToString());
        return new RowRange(partitionKey, prefix, true, upper, false);
    }

    /// <summary>True when <paramref name="rowKey"/> lies within the row-key bounds.</summary>
    public bool Holds(string rowKey)
    {
        if (FromRowKey is not null)
        {
            var c = string.CompareOrdinal(rowKey, FromRowKey);
            if (c < 0 || (c == 0 && !FromInclusive))
                return false;
        }
        if (ToRowKey is not null)
        {
            var c = string.CompareOrdinal(rowKey, ToRowKey);
            if (c > 0 || (c == 0 && !ToInclusive))
                return false;
        }
        return true;
    }
}

/// <summary>
/// The physical rows of one table. Implementations are safe for concurrent use; a commit is atomic and
/// isolated (a reader sees all of it or none of it).
/// </summary>
internal interface IRowStore
{
    /// <summary>The physical table name.</summary>
    string TableName { get; }

    /// <summary>The row at the keys, or null when there is none.</summary>
    /// <exception cref="RowStoreTableMissingException">The table does not exist.</exception>
    ValueTask<StoredRow?> GetAsync(string partitionKey, string rowKey, CancellationToken ct);

    /// <summary>
    /// At most <paramref name="limit"/> rows of <paramref name="range"/>, in order, starting after its
    /// resume key. Fewer rows than <paramref name="limit"/> means the range is exhausted; otherwise the
    /// caller reads on with the last row's keys as the resume key.
    /// </summary>
    /// <exception cref="RowStoreTableMissingException">The table does not exist.</exception>
    ValueTask<IReadOnlyList<StoredRow>> ReadAsync(RowRange range, int limit, CancellationToken ct);

    /// <summary>
    /// Applies the operations, all rows of <paramref name="partitionKey"/> and each row at most once, as
    /// one atomic step: when the condition of any of them does not hold, nothing is applied and the first
    /// such operation is named.
    /// </summary>
    /// <exception cref="RowStoreTableMissingException">The table does not exist.</exception>
    ValueTask<RowCommitResult> CommitAsync(string partitionKey, IReadOnlyList<RowWrite> writes, CancellationToken ct);
}

/// <summary>
/// The tables of one account (one in-memory account, one SQLite database file), and what the backend
/// states about itself.
/// </summary>
internal interface IRowStoreAccount
{
    /// <summary>The provider's name, as <see cref="StorageInfo.ProviderName"/> reports it.</summary>
    string ProviderName { get; }

    /// <summary>How durable an acknowledged write is, as <see cref="StorageInfo.Durability"/> reports it.</summary>
    Durability Durability { get; }

    /// <summary>The names of the account's tables.</summary>
    IAsyncEnumerable<string> ListTablesAsync(CancellationToken ct);

    /// <summary>Whether the account has the table.</summary>
    ValueTask<bool> TableExistsAsync(string tableName, CancellationToken ct);

    /// <summary>Creates the table when it does not exist (create-only); true when this call created it.</summary>
    ValueTask<bool> CreateTableAsync(string tableName, CancellationToken ct);

    /// <summary>Deletes the table and its rows; false when there was none.</summary>
    ValueTask<bool> DeleteTableAsync(string tableName, CancellationToken ct);

    /// <summary>
    /// The rows of the table of that name. Touches nothing: the operations of the returned store throw
    /// <see cref="RowStoreTableMissingException"/> while the table does not exist.
    /// </summary>
    IRowStore GetTable(string tableName);
}

/// <summary>A row store was asked for the rows of a table that does not exist; the engine reports it as <see cref="TableNotFoundException"/>.</summary>
internal sealed class RowStoreTableMissingException : Exception
{
    public RowStoreTableMissingException()
        : this("?")
    {
    }

    public RowStoreTableMissingException(string tableName)
        : base($"The table '{tableName}' does not exist.")
    {
        TableName = tableName;
    }

    public RowStoreTableMissingException(string tableName, Exception? innerException)
        : base($"The table '{tableName}' does not exist.", innerException)
    {
        TableName = tableName;
    }

    /// <summary>The table that is not there.</summary>
    public string TableName { get; } = "?";
}
