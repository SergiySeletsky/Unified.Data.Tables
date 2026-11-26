using System.Diagnostics.CodeAnalysis;

namespace Unified.Data.Tables;

/// <summary>
/// The tables of one storage account (one database file on SQLite, one in-memory account): list them,
/// test for one, create and delete one, and open one as untyped rows. For backup, restore, copy and
/// start-up probes, which work below the typed stores.
/// </summary>
/// <remarks>
/// <b>Experimental (UDT0001).</b> The shape may change in any release before 1.0; suppress the diagnostic
/// to use it. Table names are physical names, as the backend stores them. Failures are provider-neutral
/// (see <see cref="StorageException"/>) on every provider.
/// </remarks>
[Experimental(RawTableDiagnostics.Id)]
public interface ITableStore
{
    /// <summary>The names of the account's tables, in the backend's order.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Every table name; follows the backend's paging internally.</returns>
    IAsyncEnumerable<string> ListTablesAsync(CancellationToken ct = default);

    /// <summary>Whether the table exists.</summary>
    /// <param name="tableName">The physical table name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the account has a table of that name.</returns>
    Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default);

    /// <summary>
    /// Create the table when it does not exist. Create-only: an existing table and its rows are never
    /// changed.
    /// </summary>
    /// <param name="tableName">The physical table name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when this call created the table, false when it already existed.</returns>
    /// <exception cref="StorageRequestRejectedException">The backend refuses the name.</exception>
    Task<bool> CreateTableAsync(string tableName, CancellationToken ct = default);

    /// <summary>Delete the table and every row in it.</summary>
    /// <remarks>
    /// On Azure Tables a deleted table's name cannot be created again for some time (the service
    /// reports the table as being deleted); the local providers free the name at once.
    /// </remarks>
    /// <param name="tableName">The physical table name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when this call deleted the table, false when there was none.</returns>
    Task<bool> DeleteTableAsync(string tableName, CancellationToken ct = default);

    /// <summary>
    /// The untyped view of one table. Nothing is called and nothing is created: a table that does not
    /// exist fails the first operation with <see cref="TableNotFoundException"/>.
    /// </summary>
    /// <param name="tableName">The physical table name.</param>
    /// <returns>The table's rows.</returns>
    IRawTable GetTable(string tableName);
}

/// <summary>
/// One table as untyped physical rows with typed cells: read one row, read a row-key range of one
/// partition, and write rows of one partition in an all-or-nothing transaction.
/// </summary>
/// <remarks>
/// <b>Experimental (UDT0001).</b> The shape may change in any release before 1.0; suppress the diagnostic
/// to use it.
/// <para>
/// Rows are physical. No serializer runs, keys are not normalized, a typed store's continuation rows and
/// system columns are rows and cells like any other, and no read cache is consulted or updated: a typed
/// store with a read cache over the same table may serve the previous version of a row written here until
/// its cache entry expires. Offloaded blobs are not followed; a cell that references one holds the
/// reference.
/// </para>
/// <para>
/// The contract is the same on every provider, including the limits, which are those of Azure Tables
/// (see <see cref="SubmitAsync"/>), and failures are provider-neutral (see <see cref="StorageException"/>).
/// </para>
/// </remarks>
[Experimental(RawTableDiagnostics.Id)]
public interface IRawTable
{
    /// <summary>The physical table name.</summary>
    string TableName { get; }

    /// <summary>Read one row, or null when no row has the keys.</summary>
    /// <param name="partitionKey">The partition key, exactly as stored.</param>
    /// <param name="rowKey">The row key, exactly as stored.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The row with its <see cref="RawRow.ETag"/> and <see cref="RawRow.Timestamp"/>, or null.</returns>
    /// <exception cref="TableNotFoundException">The table does not exist.</exception>
    Task<RawRow?> GetAsync(string partitionKey, string rowKey, CancellationToken ct = default);

    /// <summary>
    /// Read the rows of one partition whose row key lies in the range of <paramref name="query"/>, in
    /// row-key order. Streams, following the backend's paging internally; a range whose lower bound is
    /// above its upper bound is empty.
    /// </summary>
    /// <param name="query">The partition, the optional bounds and the optional row limit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rows, each with its <see cref="RawRow.ETag"/> and <see cref="RawRow.Timestamp"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="RawQuery.Take"/> is zero or negative.</exception>
    /// <exception cref="TableNotFoundException">The table does not exist (thrown when the stream is read).</exception>
    IAsyncEnumerable<RawRow> QueryAsync(RawQuery query, CancellationToken ct = default);

    /// <summary>
    /// Apply the operations as one transaction: all of them or none.
    /// </summary>
    /// <remarks>
    /// A transaction is what Azure Tables accepts as one, on every provider: every row in one partition,
    /// at most 100 operations, each row named once, and a payload within the budget the library plans by
    /// (see <see cref="BatchPlanner.DefaultMaxBytes"/>). The request is sized before it is sent; one that
    /// cannot be a single transaction throws <see cref="BatchNotAtomicException"/> and nothing is written.
    /// It is never split.
    /// <para>
    /// When the backend refuses an operation, nothing is applied and the failure of that operation is
    /// thrown: <see cref="DuplicateKeyException"/> for an insert of an existing row,
    /// <see cref="EntityNotFoundException"/> for a replace, merge or delete of a missing row (a delete
    /// here is not the idempotent delete of the typed store), <see cref="ConcurrencyConflictException"/>
    /// for an <see cref="RawOperation.IfMatch"/> that names another version. An empty list writes nothing.
    /// </para>
    /// </remarks>
    /// <param name="operations">The writes, applied in order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// One entry per operation, in order: the row's new ETag, or null for a delete and when the backend
    /// reported none.
    /// </returns>
    /// <exception cref="BatchNotAtomicException">The operations cannot be one transaction; nothing was written.</exception>
    /// <exception cref="DuplicateKeyException">An inserted row already exists; nothing was written.</exception>
    /// <exception cref="EntityNotFoundException">A replaced, merged or deleted row does not exist; nothing was written.</exception>
    /// <exception cref="ConcurrencyConflictException">A row is not the version its condition names; nothing was written.</exception>
    /// <exception cref="TableNotFoundException">The table does not exist.</exception>
    Task<IReadOnlyList<string?>> SubmitAsync(IReadOnlyList<RawOperation> operations, CancellationToken ct = default);
}
