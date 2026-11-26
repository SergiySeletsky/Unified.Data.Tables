using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Azure;
using Microsoft.Data.Sqlite;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Sqlite;

/// <summary>
/// One SQLite database as the account of a row store: its connections, its tables, its ETag counter and
/// its schema version. Everything the stores do goes through <see cref="ReadAsync{TResult}"/> or
/// <see cref="WriteAsync{TResult}"/>, which is also where a SQLite failure becomes a provider-neutral one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Connections.</b> The database owns its connections instead of using the ADO.NET pool, whose idle
/// connections keep the file open until a process-wide <c>ClearAllPools</c>: one writer connection, used
/// by one write at a time (writes of this process queue on a semaphore, not on SQLite's lock), and reader
/// connections, one per read under way, of which a few are kept for reuse. In write-ahead mode a reader never waits for the writer and sees
/// the last committed state. A <c>:memory:</c> database exists in one connection only, so there every
/// operation, reads included, takes its turn on that one.
/// </para>
/// <para>
/// <b>Other processes.</b> A write begins with <c>BEGIN IMMEDIATE</c>, which takes the database's write
/// lock or waits for it (<c>busy_timeout</c>) and then fails with <c>SQLITE_BUSY</c>; nothing has been
/// written at that point. A transaction that first reads and then writes could not wait: SQLite refuses
/// to upgrade it once another writer has committed.
/// </para>
/// <para>
/// <b>Layout.</b> Per table <c>(pk BLOB, rk BLOB, ver INTEGER, ts INTEGER, cells BLOB, PRIMARY KEY (pk, rk))
/// WITHOUT ROWID</c> (see <see cref="SqliteRowCodec"/>); <c>__meta</c> holds the schema version and the
/// ETag counter; <c>__blobs</c> the offloaded values. A table name is one the Table service accepts (a
/// letter, then 2 to 62 letters and digits), checked before it is ever put, quoted, into SQL; so no user
/// table can be named like an internal one.
/// </para>
/// </remarks>
internal sealed partial class SqliteDatabase : IRowStoreAccount, IDisposable
{
    /// <summary>The layout version this build writes and reads, recorded in <c>__meta</c> when a database is created.</summary>
    internal const int SchemaVersion = 1;

    /// <summary>The provider name the SQLite stores report (<see cref="StorageInfo.ProviderName"/>).</summary>
    internal const string Provider = "Sqlite";

    internal const string MetaTable = "\"__meta\"";
    internal const string BlobsTable = "\"__blobs\"";

    // How many reader connections are kept open for reuse; a burst of concurrent reads opens more and
    // closes them again.
    private const int MaxIdleReaders = 16;

    // SQLITE_ERROR: among others a statement over a table that does not exist.
    internal const int SqliteError = 1;

    // What the Table service accepts as a table name: a letter, then 2 to 62 letters and digits.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{2,62}\z")]
    private static partial Regex TableNameRule();

    private readonly string connectionString;
    private readonly bool inMemory;
    private readonly TimeSpan busyTimeout;
    private readonly string synchronous;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly object readersGate = new();
    private readonly Stack<SqliteConnection> idleReaders = new();
    private readonly SqliteConnection writer;
    private readonly object clockGate = new();
    private DateTimeOffset lastBlobTime = DateTimeOffset.MinValue;
    private bool disposed;

    /// <summary>Opens the database, creating the file (and its directory) and the internal tables when they are not there.</summary>
    /// <exception cref="StorageException">The database cannot be opened, or was written by a newer version of the library.</exception>
    public SqliteDatabase(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        inMemory = string.Equals(options.DataSource, SqliteStorageOptions.InMemoryDataSource, StringComparison.Ordinal);
        DataSource = inMemory ? options.DataSource : Path.GetFullPath(options.DataSource);
        // Whole seconds: the ADO.NET provider retries a busy statement until its command timeout, which
        // counts in seconds, so SQLite's own busy handler is given the same span.
        busyTimeout = TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(options.BusyTimeout.TotalSeconds)));
        synchronous = options.Synchronous == SqliteSynchronousMode.Normal ? "NORMAL" : "FULL";
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DataSource,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = (int)busyTimeout.TotalSeconds,
        }.ToString();

        if (!inMemory && Path.GetDirectoryName(DataSource) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        SqliteConnection? connection = null;
        try
        {
            connection = OpenConnection();
            // Write-ahead logging is a property of the file and stays with it; a :memory: database has
            // no log to write ahead to.
            if (!inMemory)
                Scalar(connection, "PRAGMA journal_mode = WAL");
            Durability = ReadDurability(connection);
            EnsureSchema(connection);
            writer = connection;
        }
        catch (SqliteException ex)
        {
            connection?.Dispose();
            throw SqliteErrors.Map(ex);
        }
        catch
        {
            connection?.Dispose();
            throw;
        }
    }

    /// <summary>The full path of the database file, or <c>:memory:</c>.</summary>
    public string DataSource { get; }

    /// <inheritdoc />
    public string ProviderName => Provider;

    /// <summary>
    /// What an acknowledged write survives, read back from the writer connection when the database was
    /// opened (<c>PRAGMA journal_mode</c>, <c>PRAGMA synchronous</c>), not assumed from the options.
    /// </summary>
    public Durability Durability { get; }

    // ── Connections ─────────────────────────────────────────────────────────

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            // Both are settings of the connection, not of the file: every connection gets them.
            Execute(connection, string.Create(CultureInfo.InvariantCulture,
                $"PRAGMA busy_timeout = {(long)busyTimeout.TotalMilliseconds}; PRAGMA synchronous = {synchronous};"));
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // :memory: outlives nothing. Otherwise a commit that is flushed before it is acknowledged (FULL, EXTRA)
    // survives a power loss, and one that is only handed to the operating system (NORMAL, OFF) a crash of
    // the process.
    private Durability ReadDurability(SqliteConnection connection)
    {
        var journal = Convert.ToString(Scalar(connection, "PRAGMA journal_mode"), CultureInfo.InvariantCulture);
        var level = Convert.ToInt64(Scalar(connection, "PRAGMA synchronous"), CultureInfo.InvariantCulture);
        if (inMemory || string.Equals(journal, "memory", StringComparison.OrdinalIgnoreCase))
            return Durability.Volatile;
        return level >= 2 && !string.Equals(journal, "off", StringComparison.OrdinalIgnoreCase)
            ? Durability.Fsynced
            : Durability.OsBuffered;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        long? version;
        try
        {
            version = ReadSchemaVersion(connection);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteError)
        {
            version = null; // no __meta table yet: a new database
        }

        if (version is null)
        {
            // Another process may be creating the same database: under the write lock, and every
            // statement harmless when the other one got there first.
            Execute(connection, "BEGIN IMMEDIATE");
            try
            {
                Execute(connection,
                    $"CREATE TABLE IF NOT EXISTS {MetaTable} (key TEXT NOT NULL PRIMARY KEY, value INTEGER NOT NULL) WITHOUT ROWID; " +
                    string.Create(CultureInfo.InvariantCulture, $"INSERT OR IGNORE INTO {MetaTable} (key, value) VALUES ('schema_version', {SchemaVersion}); ") +
                    $"INSERT OR IGNORE INTO {MetaTable} (key, value) VALUES ('etag', 0); " +
                    $"CREATE TABLE IF NOT EXISTS {BlobsTable} (container TEXT NOT NULL, name TEXT NOT NULL, sha256 TEXT NOT NULL, length INTEGER NOT NULL, " +
                    "content_type TEXT NOT NULL, encoding TEXT NOT NULL, content_encoding TEXT, modified INTEGER NOT NULL, content BLOB NOT NULL, " +
                    "PRIMARY KEY (container, name));");
                Execute(connection, "COMMIT");
            }
            catch
            {
                Rollback(connection);
                throw;
            }
            version = ReadSchemaVersion(connection);
        }

        if (version != SchemaVersion)
            throw new StorageException(
                $"The SQLite database has schema version {version}; this version of Unified.Data.Tables.Sqlite reads and writes version {SchemaVersion}. " +
                "It was written by another version of the library: open it with that version, or migrate it.");
    }

    private static long? ReadSchemaVersion(SqliteConnection connection) =>
        Scalar(connection, $"SELECT value FROM {MetaTable} WHERE key = 'schema_version'") is { } value
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

    private SqliteConnection RentReader()
    {
        lock (readersGate)
        {
            ObjectDisposedException.ThrowIf(disposed, typeof(SqliteStorageAccount));
            if (idleReaders.TryPop(out var idle))
                return idle;
        }
        return OpenConnection();
    }

    private void ReturnReader(SqliteConnection connection, bool healthy)
    {
        lock (readersGate)
        {
            if (healthy && !disposed && idleReaders.Count < MaxIdleReaders)
            {
                idleReaders.Push(connection);
                return;
            }
        }
        connection.Dispose();
    }

    // The turn of one write (and, for a :memory: database, of one read) on the writer connection. Writers
    // of this process wait here as long as they would wait for another process's lock.
    private async ValueTask EnterWriterAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), typeof(SqliteStorageAccount));
        if (!await writeGate.WaitAsync(busyTimeout, ct).ConfigureAwait(false))
            throw SqliteErrors.Busy(null);
        if (Volatile.Read(ref disposed))
        {
            writeGate.Release();
            throw new ObjectDisposedException(nameof(SqliteStorageAccount));
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> on a connection that sees the last committed state. A SQLite failure
    /// leaves as <see cref="StorageException"/> (<see cref="TransientStorageException"/> when the database
    /// was locked).
    /// </summary>
    internal async ValueTask<TResult> ReadAsync<TResult>(Func<SqliteConnection, TResult> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (inMemory)
        {
            await EnterWriterAsync(ct).ConfigureAwait(false);
            try
            {
                return read(writer);
            }
            catch (SqliteException ex)
            {
                throw SqliteErrors.Map(ex);
            }
            finally
            {
                writeGate.Release();
            }
        }

        SqliteConnection? connection = null;
        var healthy = false;
        try
        {
            connection = RentReader();
            var result = read(connection);
            healthy = true;
            return result;
        }
        catch (SqliteException ex)
        {
            throw SqliteErrors.Map(ex);
        }
        catch (StorageException)
        {
            healthy = true; // the store's own answer (a missing table, an unreadable row), not a broken connection
            throw;
        }
        catch (RowStoreTableMissingException)
        {
            healthy = true;
            throw;
        }
        finally
        {
            if (connection is not null)
                ReturnReader(connection, healthy);
        }
    }

    /// <summary>
    /// Runs <paramref name="write"/> as one transaction that holds the database's write lock from its
    /// first statement: committed when it returns, rolled back when it throws. A database that stays
    /// locked by another writer for the busy timeout is <see cref="TransientStorageException"/> with
    /// <see cref="TransientStorageException.MayHaveCommitted"/> false.
    /// </summary>
    internal async ValueTask<TResult> WriteAsync<TResult>(Func<SqliteConnection, TResult> write, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnterWriterAsync(ct).ConfigureAwait(false);
        try
        {
            Execute(writer, "BEGIN IMMEDIATE");
            TResult result;
            try
            {
                result = write(writer);
                Execute(writer, "COMMIT");
            }
            catch
            {
                Rollback(writer);
                throw;
            }
            return result;
        }
        catch (SqliteException ex)
        {
            throw SqliteErrors.Map(ex);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static void Rollback(SqliteConnection connection)
    {
        try
        {
            Execute(connection, "ROLLBACK");
        }
        catch (SqliteException)
        {
            // Nothing to roll back (SQLite already ended the transaction), or the connection is beyond
            // use: the failure that brought us here is the one to report.
        }
    }

    internal static SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    internal static int Execute(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        return command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        return command.ExecuteScalar();
    }

    // ── The ETag counter and the blob clock ─────────────────────────────────

    /// <summary>
    /// Inside a write transaction: takes the next <paramref name="count"/> values of the database-wide
    /// version counter and returns the last of them. The counter is a row of the database, raised in the
    /// transaction that uses the values, so no two commits (of any process, before or after a restart, on
    /// any table) ever share a version, and a version is never reused for a row that was deleted and
    /// created again.
    /// </summary>
    internal static long NextVersions(SqliteConnection connection, int count)
    {
        using var command = Command(connection, $"UPDATE {MetaTable} SET value = value + $count WHERE key = 'etag' RETURNING value");
        command.Parameters.AddWithValue("$count", count);
        return command.ExecuteScalar() is { } last
            ? Convert.ToInt64(last, CultureInfo.InvariantCulture)
            : throw new StorageException("The SQLite database has no ETag counter (the 'etag' row of __meta is missing); it is damaged.");
    }

    /// <summary>
    /// A time for a blob's last-modified stamp: never at or before one this account has handed out, nor
    /// at or before <paramref name="after"/> (the stamp the blob has, which another process may have
    /// set), so a touch is always observably newer than what it follows.
    /// </summary>
    internal DateTimeOffset NextBlobTime(DateTimeOffset? after = null)
    {
        lock (clockGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now <= lastBlobTime)
                now = lastBlobTime.AddTicks(1);
            if (after is { } floor && now <= floor)
                now = floor.AddTicks(1);
            lastBlobTime = now;
            return now;
        }
    }

    // ── IRowStoreAccount ────────────────────────────────────────────────────

    /// <summary>Whether <paramref name="tableName"/> is a name a table can have here (the Table service's rule).</summary>
    internal static bool IsTableName(string? tableName) => tableName is not null && TableNameRule().IsMatch(tableName);

    /// <summary>The identifier of a table in SQL: the validated name, quoted. Only ever called with a name <see cref="IsTableName"/> accepts.</summary>
    internal static string Quoted(string tableName) => "\"" + tableName + "\"";

    /// <summary>On a connection: whether the table exists. Names are compared ignoring case, as SQLite and the Table service compare them.</summary>
    internal static bool TableExists(SqliteConnection connection, string tableName)
    {
        if (!IsTableName(tableName))
            return false;
        using var command = Command(connection, "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = $name COLLATE NOCASE");
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListTablesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var names = await ReadAsync(static connection =>
        {
            var found = new List<string>();
            using var command = Command(connection, "SELECT name FROM sqlite_schema WHERE type = 'table'");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // The internal tables, and anything else that is not a table of ours, have other names.
                var name = reader.GetString(0);
                if (IsTableName(name))
                    found.Add(name);
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }, ct).ConfigureAwait(false);
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            yield return name;
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TableExistsAsync(string tableName, CancellationToken ct) =>
        ReadAsync(connection => TableExists(connection, tableName), ct);

    /// <inheritdoc />
    /// <exception cref="StorageRequestRejectedException">The name is not one the Table service accepts.</exception>
    public ValueTask<bool> CreateTableAsync(string tableName, CancellationToken ct)
    {
        // A table that can be created here can be created on Azure: the service's rule for the name,
        // and its failure. It is also what makes the name safe to quote into the statement.
        if (!IsTableName(tableName))
            throw ServiceErrors.Neutral(
                new RequestFailedException(400, "The specified resource name contains invalid characters.", "InvalidResourceName", null),
                tableName ?? "?", id: null);
        return WriteAsync(connection =>
        {
            if (TableExists(connection, tableName))
                return false;
            Execute(connection,
                $"CREATE TABLE {Quoted(tableName)} (pk BLOB NOT NULL, rk BLOB NOT NULL, ver INTEGER NOT NULL, ts INTEGER NOT NULL, cells BLOB NOT NULL, " +
                "PRIMARY KEY (pk, rk)) WITHOUT ROWID");
            return true;
        }, ct);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteTableAsync(string tableName, CancellationToken ct)
    {
        if (!IsTableName(tableName))
        {
            ct.ThrowIfCancellationRequested();
            return new(false);
        }
        return WriteAsync(connection =>
        {
            if (!TableExists(connection, tableName))
                return false;
            Execute(connection, $"DROP TABLE {Quoted(tableName)}");
            return true;
        }, ct);
    }

    /// <inheritdoc />
    public IRowStore GetTable(string tableName) => new SqliteRowStore(this, tableName);

    /// <summary>Closes every connection. The last one to close a file database checkpoints its write-ahead log into the file.</summary>
    public void Dispose()
    {
        lock (readersGate)
        {
            if (disposed)
                return;
            Volatile.Write(ref disposed, true);
            while (idleReaders.TryPop(out var idle))
                idle.Dispose();
        }
        // After the write that is under way, if any.
        writeGate.Wait();
        try
        {
            writer.Dispose();
        }
        finally
        {
            writeGate.Release();
        }
    }
}

/// <summary>What a SQLite failure is to a caller of a store: never a <see cref="SqliteException"/>.</summary>
internal static class SqliteErrors
{
    private static readonly string[] PrimaryCodes =
    [
        "SQLITE_OK", "SQLITE_ERROR", "SQLITE_INTERNAL", "SQLITE_PERM", "SQLITE_ABORT", "SQLITE_BUSY", "SQLITE_LOCKED", "SQLITE_NOMEM",
        "SQLITE_READONLY", "SQLITE_INTERRUPT", "SQLITE_IOERR", "SQLITE_CORRUPT", "SQLITE_NOTFOUND", "SQLITE_FULL", "SQLITE_CANTOPEN",
        "SQLITE_PROTOCOL", "SQLITE_EMPTY", "SQLITE_SCHEMA", "SQLITE_TOOBIG", "SQLITE_CONSTRAINT", "SQLITE_MISMATCH", "SQLITE_MISUSE",
        "SQLITE_NOLFS", "SQLITE_AUTH", "SQLITE_FORMAT", "SQLITE_RANGE", "SQLITE_NOTADB",
    ];

    /// <summary>The name of a primary result code (<c>SQLITE_BUSY</c>), as <see cref="StorageException.ProviderErrorCode"/> carries it.</summary>
    internal static string CodeName(int primaryCode) =>
        primaryCode >= 0 && primaryCode < PrimaryCodes.Length
            ? PrimaryCodes[primaryCode]
            : "SQLITE_" + primaryCode.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The neutral failure of <paramref name="ex"/>, which is its inner exception: a locked database
    /// (<c>SQLITE_BUSY</c>, <c>SQLITE_LOCKED</c>) is <see cref="TransientStorageException"/>, anything
    /// else <see cref="StorageException"/>, each with the name of the SQLite result code.
    /// </summary>
    internal static StorageException Map(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6
            ? Busy(ex)
            : new StorageException(
                $"The SQLite database failed the request ({CodeName(ex.SqliteErrorCode)}, extended code {ex.SqliteExtendedErrorCode.ToString(CultureInfo.InvariantCulture)}): {ex.Message}",
                CodeName(ex.SqliteErrorCode), ex);

    /// <summary>
    /// The database stayed locked for the busy timeout. A statement that is refused the lock has written
    /// nothing, and a transaction that does not get as far as its commit is rolled back, so the
    /// operation is known not to have committed.
    /// </summary>
    internal static TransientStorageException Busy(SqliteException? ex) =>
        new("The SQLite database is locked by another writer and stayed locked for the busy timeout. Nothing was written; the operation can be tried again.",
            mayHaveCommitted: false, retryAfter: null, ex)
        {
            ProviderErrorCode = CodeName(ex?.SqliteErrorCode ?? 5),
        };
}
