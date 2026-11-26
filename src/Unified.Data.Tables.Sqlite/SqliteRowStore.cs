using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Sqlite;

/// <summary>
/// The rows of one table of a SQLite database: the row store the engine of the local providers runs
/// over. A point read is one indexed lookup, a range read one index scan in key order (the partition and
/// the row-key bounds are the statement's <c>WHERE</c>; what a filter says about other columns is the
/// engine's compiled predicate over the rows this returns), a commit one transaction.
/// </summary>
/// <param name="database">The database that holds the table.</param>
/// <param name="tableName">The table. A name no table can have here (see <see cref="SqliteDatabase.IsTableName"/>) is a table that does not exist.</param>
internal sealed class SqliteRowStore(SqliteDatabase database, string tableName) : IRowStore
{
    // The quoted identifier; null for a name that is never put into a statement.
    private readonly string? table = SqliteDatabase.IsTableName(tableName) ? SqliteDatabase.Quoted(tableName) : null;

    /// <inheritdoc />
    public string TableName { get; } = tableName;

    // A statement over a table that does not exist fails as SQLITE_ERROR, like any other statement SQLite
    // cannot prepare. Whether that is what happened is asked of the database, on the same connection (in
    // a write, inside the same transaction), not read out of the message.
    private TResult OnTable<TResult>(SqliteConnection connection, Func<SqliteConnection, TResult> work)
    {
        if (table is null)
            throw new RowStoreTableMissingException(TableName);
        try
        {
            return work(connection);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteDatabase.SqliteError && !SqliteDatabase.TableExists(connection, TableName))
        {
            throw new RowStoreTableMissingException(TableName, ex);
        }
        catch (FormatException ex)
        {
            throw new StorageException($"A row of the table '{TableName}' cannot be read from the SQLite database: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public ValueTask<StoredRow?> GetAsync(string partitionKey, string rowKey, CancellationToken ct) =>
        database.ReadAsync(connection => OnTable(connection, c =>
        {
            using var command = SqliteDatabase.Command(c, $"SELECT ver, ts, cells FROM {table} WHERE pk = $pk AND rk = $rk");
            command.Parameters.AddWithValue("$pk", SqliteRowCodec.Key(partitionKey));
            command.Parameters.AddWithValue("$rk", SqliteRowCodec.Key(rowKey));
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? Stored(partitionKey, rowKey, reader.GetInt64(0), reader.GetInt64(1), Blob(reader, 2))
                : null;
        }), ct);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<StoredRow>> ReadAsync(RowRange range, int limit, CancellationToken ct) =>
        database.ReadAsync(connection => OnTable(connection, c => Read(c, range, limit)), ct);

    private IReadOnlyList<StoredRow> Read(SqliteConnection connection, RowRange range, int limit)
    {
        using var command = connection.CreateCommand();
        var sql = new StringBuilder($"SELECT pk, rk, ver, ts, cells FROM {table}");
        var where = new List<string>(4);
        var nothing = limit <= 0;
        if (range.PartitionKey is { } only)
        {
            // A resume key of another partition: before it everything is still to come, after it nothing is.
            var order = range.After is { } resume ? string.CompareOrdinal(resume.PartitionKey, only) : -1;
            nothing |= order > 0;
            where.Add("pk = $pk");
            command.Parameters.AddWithValue("$pk", SqliteRowCodec.Key(only));
            if (range.FromRowKey is { } from)
            {
                where.Add(range.FromInclusive ? "rk >= $from" : "rk > $from");
                command.Parameters.AddWithValue("$from", SqliteRowCodec.Key(from));
            }
            if (range.ToRowKey is { } to)
            {
                where.Add(range.ToInclusive ? "rk <= $to" : "rk < $to");
                command.Parameters.AddWithValue("$to", SqliteRowCodec.Key(to));
            }
            if (order == 0)
            {
                where.Add("rk > $after");
                command.Parameters.AddWithValue("$after", SqliteRowCodec.Key(range.After!.Value.RowKey));
            }
        }
        else if (range.After is { } after)
        {
            // Every partition: the bounds do not apply, and the rows after the resume key are those
            // after it in (partition key, row key) order.
            where.Add("(pk, rk) > ($afterPk, $after)");
            command.Parameters.AddWithValue("$afterPk", SqliteRowCodec.Key(after.PartitionKey));
            command.Parameters.AddWithValue("$after", SqliteRowCodec.Key(after.RowKey));
        }
        if (where.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", where));
        // The primary key is (pk, rk) and the keys are stored so that its byte order is the ordinal
        // order of the strings: this is a scan of the index, never a sort.
        sql.Append(" ORDER BY pk, rk LIMIT $limit");
        command.Parameters.AddWithValue("$limit", nothing ? 0L : limit);
        command.CommandText = sql.ToString();

        var page = new List<StoredRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            page.Add(Stored(
                SqliteRowCodec.Key(Blob(reader, 0)), SqliteRowCodec.Key(Blob(reader, 1)),
                reader.GetInt64(2), reader.GetInt64(3), Blob(reader, 4)));
        }
        return page;
    }

    /// <inheritdoc />
    public ValueTask<RowCommitResult> CommitAsync(string partitionKey, IReadOnlyList<RowWrite> writes, CancellationToken ct) =>
        database.WriteAsync(connection => OnTable(connection, c => Commit(c, partitionKey, writes)), ct);

    // Inside the write transaction, which holds the database's write lock: what is read here cannot
    // change before the commit, in this process or in another.
    private RowCommitResult Commit(SqliteConnection connection, string partitionKey, IReadOnlyList<RowWrite> writes)
    {
        var pk = SqliteRowCodec.Key(partitionKey);

        // Every condition holds, or nothing is applied.
        var puts = 0;
        using (var current = SqliteDatabase.Command(connection, $"SELECT ver FROM {table} WHERE pk = $pk AND rk = $rk"))
        {
            current.Parameters.AddWithValue("$pk", pk);
            var rk = current.Parameters.Add("$rk", SqliteType.Blob);
            var asked = false;
            for (var i = 0; i < writes.Count; i++)
            {
                var write = writes[i];
                if (write.Kind == RowWriteKind.Put)
                    puts++;
                if (write.Condition == RowCondition.None)
                    continue;
                rk.Value = SqliteRowCodec.Key(write.RowKey);
                var stored = current.ExecuteScalar();
                asked = true;
                switch (write.Condition)
                {
                    case RowCondition.MustNotExist when stored is not null:
                        return RowCommitResult.Failed(RowCommitFailure.AlreadyExists, i);
                    case RowCondition.MustExist when stored is null:
                        return RowCommitResult.Failed(RowCommitFailure.NotFound, i);
                    case RowCondition.MustExist when write.IfMatch is not null
                        && !string.Equals(SqliteRowCodec.ETag(Convert.ToInt64(stored, CultureInfo.InvariantCulture)), write.IfMatch, StringComparison.Ordinal):
                        return RowCommitResult.Failed(RowCommitFailure.VersionMismatch, i);
                }
            }
            // A commit that asked nothing of the table and will write nothing to it still needs the table.
            if (!asked && writes.Count == 0 && !SqliteDatabase.TableExists(connection, TableName))
                throw new RowStoreTableMissingException(TableName);
        }

        var now = DateTimeOffset.UtcNow;
        var etags = new string?[writes.Count];
        // Each put gets a version of its own from the database-wide counter, in the order of the writes.
        var version = puts == 0 ? 0 : SqliteDatabase.NextVersions(connection, puts) - puts;
        using var put = SqliteDatabase.Command(connection,
            $"INSERT OR REPLACE INTO {table} (pk, rk, ver, ts, cells) VALUES ($pk, $rk, $ver, $ts, $cells)");
        put.Parameters.AddWithValue("$pk", pk);
        var putRk = put.Parameters.Add("$rk", SqliteType.Blob);
        var putVersion = put.Parameters.Add("$ver", SqliteType.Integer);
        put.Parameters.AddWithValue("$ts", now.UtcTicks);
        var putCells = put.Parameters.Add("$cells", SqliteType.Blob);
        using var delete = SqliteDatabase.Command(connection, $"DELETE FROM {table} WHERE pk = $pk AND rk = $rk");
        delete.Parameters.AddWithValue("$pk", pk);
        var deleteRk = delete.Parameters.Add("$rk", SqliteType.Blob);

        for (var i = 0; i < writes.Count; i++)
        {
            var write = writes[i];
            if (write.Kind == RowWriteKind.Delete)
            {
                deleteRk.Value = SqliteRowCodec.Key(write.RowKey);
                delete.ExecuteNonQuery();
            }
            else
            {
                putRk.Value = SqliteRowCodec.Key(write.RowKey);
                putVersion.Value = ++version;
                putCells.Value = SqliteRowCodec.Cells(write.Row!);
                put.ExecuteNonQuery();
                etags[i] = SqliteRowCodec.ETag(version);
            }
        }
        return new RowCommitResult(RowCommitFailure.None, -1, etags, now);
    }

    private static StoredRow Stored(string partitionKey, string rowKey, long version, long ticks, byte[] cells) =>
        new(SqliteRowCodec.Row(partitionKey, rowKey, cells), SqliteRowCodec.ETag(version), new DateTimeOffset(ticks, TimeSpan.Zero));

    private static byte[] Blob(SqliteDataReader reader, int ordinal) => reader.GetFieldValue<byte[]>(ordinal);
}
