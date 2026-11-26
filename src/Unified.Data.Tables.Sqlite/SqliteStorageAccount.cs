using System.Diagnostics.CodeAnalysis;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Sqlite;

/// <summary>
/// One SQLite database as a storage account: the tables of every <see cref="SqliteStorage{T}"/> and
/// <see cref="SqlitePolymorphicStorage{TBase}"/> built over it, their offloaded values and the counter
/// their ETags come from. Create one per database file and share it between the stores (the DI
/// registration of <c>AddUnifiedSqliteStorage</c> does); dispose it to close the file.
/// </summary>
/// <remarks>
/// <para>
/// The database is opened by the constructor: the file and its directory are created when they are not
/// there, write-ahead logging is switched on, and the two internal tables (<c>__meta</c>,
/// <c>__blobs</c>) are created. A table of a store is created on the store's first use, as on every
/// provider.
/// </para>
/// <para>
/// <b>Concurrency.</b> One writer at a time and any number of readers, which never wait for the writer.
/// Several processes on one machine may open the same file: a write waits for another writer's lock for
/// <see cref="SqliteStorageOptions.BusyTimeout"/> and then fails with
/// <see cref="TransientStorageException"/> (<see cref="TransientStorageException.MayHaveCommitted"/>
/// false: nothing was written). SQLite does its work on the calling thread; an operation's task is
/// usually complete when it is returned.
/// </para>
/// <para>
/// <b>Not supported:</b> a database on a network share (SQLite's locks are not reliable there), and a
/// database written by a newer version of this package (refused when it is opened).
/// </para>
/// <para>
/// <b>Not emulated:</b> throttling, latency, SDK retries, short pages and credentials. A store here never
/// sends a request twice, so <see cref="DuplicateKeyException.MayBeOwnWrite"/> is always false and a
/// delete never answers <see cref="DeleteStatus.Indeterminate"/>.
/// </para>
/// </remarks>
public sealed class SqliteStorageAccount : IDisposable, IAsyncDisposable
{
#pragma warning disable UDT0001 // the experimental raw-table API is handed out here, not used
    private readonly RowStoreTableStore tableStore;
#pragma warning restore UDT0001

    /// <summary>Opens (and, when it does not exist, creates) the database at <paramref name="dataSource"/> with the default options.</summary>
    /// <param name="dataSource">The path of the database file, or <c>:memory:</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="dataSource"/> is null, empty or whitespace.</exception>
    /// <exception cref="StorageException">The database cannot be opened, or was written by a newer version of this package.</exception>
    public SqliteStorageAccount(string dataSource)
        : this(new SqliteStorageOptions { DataSource = dataSource })
    {
    }

    /// <summary>Opens (and, when it does not exist, creates) the database <paramref name="options"/> names.</summary>
    /// <param name="options">
    /// Where and how the database is opened, and the options the stores over this account read unless
    /// they are given their own.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="StorageException">The database cannot be opened, or was written by a newer version of this package.</exception>
    public SqliteStorageAccount(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Database = new SqliteDatabase(options);
#pragma warning disable UDT0001
        tableStore = new RowStoreTableStore(Database);
#pragma warning restore UDT0001
    }

    /// <summary>The options the account was opened with; what a store over it reads when it is given none of its own.</summary>
    internal SqliteStorageOptions Options { get; }

    /// <summary>The database below the stores.</summary>
    internal SqliteDatabase Database { get; }

    /// <summary>The full path of the database file, or <c>:memory:</c>.</summary>
    public string DataSource => Database.DataSource;

    /// <summary>
    /// What an acknowledged write survives, as the stores over this account report it in
    /// <see cref="StorageInfo.Durability"/>. Read back from the open database (<c>PRAGMA journal_mode</c>
    /// and <c>PRAGMA synchronous</c>), not assumed from the options: <see cref="Durability.Fsynced"/>
    /// with <see cref="SqliteSynchronousMode.Full"/>, <see cref="Durability.OsBuffered"/> with
    /// <see cref="SqliteSynchronousMode.Normal"/>, <see cref="Durability.Volatile"/> for a
    /// <c>:memory:</c> database.
    /// </summary>
    public Durability Durability => Database.Durability;

    /// <summary>
    /// The tables of the database as untyped rows: list, test, create and delete tables and open one as
    /// an <see cref="IRawTable"/>, as the Azure provider's <see cref="ITableStore"/> does over an Azure
    /// Tables account. The same instance the DI registration resolves and a store's
    /// <c>TryGetCapability&lt;ITableStore&gt;</c> answers with: it sees every table of the database.
    /// </summary>
    [Experimental(RawTableDiagnostics.Id)]
    public ITableStore Tables => tableStore;

    /// <summary>
    /// Closes the database. Operations of the stores over this account fail afterwards with
    /// <see cref="ObjectDisposedException"/>. A <c>:memory:</c> database is gone.
    /// </summary>
    public void Dispose() => Database.Dispose();

    /// <summary>Closes the database (see <see cref="Dispose"/>).</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
