namespace Unified.Data.Tables.Sqlite;

/// <summary>
/// When SQLite waits for the operating system to have written a commit to the disk
/// (<c>PRAGMA synchronous</c>), which decides what an acknowledged write survives. Has no effect on a
/// <c>:memory:</c> database, which survives nothing.
/// </summary>
public enum SqliteSynchronousMode
{
    /// <summary>
    /// <c>NORMAL</c>: a commit is handed to the operating system and not waited for. It survives a crash
    /// of the process; the last commits before a power loss or an operating-system crash may be lost
    /// (the database stays consistent). The store reports <see cref="Durability.OsBuffered"/>. Roughly
    /// ten times the write throughput of <see cref="Full"/>.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// <c>FULL</c> (the default): every commit is flushed to the disk before it is acknowledged, so it
    /// survives a power loss as far as the disk honours a flush. The store reports
    /// <see cref="Durability.Fsynced"/>.
    /// </summary>
    Full = 2,
}

/// <summary>
/// Options of the SQLite provider: the <see cref="StorageOptions"/> every provider reads (id
/// normalization, the read cache, the oversized-cell policy, blob offload, the clock, the table-name
/// resolver, the type discriminator) and where and how the database is opened.
/// </summary>
/// <remarks>
/// <see cref="StorageOptions.Cache"/> has the default the Azure provider has (a sliding hour), so a row
/// another store instance or another process changed is served stale exactly as an Azure deployment would
/// serve it; set <see cref="CachePolicy.Disabled"/> to always read the database.
/// <see cref="StorageOptions.Offload"/> moves values too large for table rows into the <c>__blobs</c>
/// table of the same database (<see cref="OffloadOptions.ContainerName"/> keeps the values of differently
/// configured stores apart).
/// </remarks>
public class SqliteStorageOptions : StorageOptions
{
    /// <summary>The <see cref="DataSource"/> of a database that lives in memory and is gone when its account is disposed.</summary>
    public const string InMemoryDataSource = ":memory:";

    private string dataSource = "unified-tables.db";
    private TimeSpan busyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The path of the database file (created, with its directory, when it does not exist), or
    /// <see cref="InMemoryDataSource"/>. A relative path is resolved against the current directory.
    /// Default <c>unified-tables.db</c>.
    /// </summary>
    /// <remarks>
    /// One file is one account: every table of every store over it, the offloaded values and the ETag
    /// counter. The file must be on a local disk: SQLite's locks are not reliable on a network share
    /// (SMB, NFS), which is not supported. In write-ahead mode two more files sit beside it
    /// (<c>-wal</c>, <c>-shm</c>); copy the database only through SQLite's backup, or while no process
    /// has it open.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is null, empty or whitespace.</exception>
    public string DataSource
    {
        get => dataSource;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            dataSource = value;
        }
    }

    /// <summary>
    /// When a commit is flushed to the disk; decides the <see cref="Durability"/> the stores report.
    /// Default <see cref="SqliteSynchronousMode.Full"/>.
    /// </summary>
    public SqliteSynchronousMode Synchronous { get; set; } = SqliteSynchronousMode.Full;

    /// <summary>
    /// How long a write waits for the write lock another writer holds (in this process or in another
    /// one on the same machine) before it fails with <see cref="TransientStorageException"/>
    /// (<see cref="TransientStorageException.MayHaveCommitted"/> false). Rounded up to whole seconds, at
    /// least one. Default 30 seconds. Reads never wait for a writer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive, or longer than a day.</exception>
    public TimeSpan BusyTimeout
    {
        get => busyTimeout;
        set => busyTimeout = value > TimeSpan.Zero && value <= TimeSpan.FromDays(1)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "BusyTimeout must be positive and at most one day.");
    }

    /// <summary>
    /// Overrides the cache policy for one entity type. Returns the options for chaining.
    /// </summary>
    /// <typeparam name="T">The entity type the policy applies to.</typeparam>
    /// <param name="policy">The policy.</param>
    /// <returns>These options.</returns>
    public new SqliteStorageOptions CacheFor<T>(CachePolicy policy) where T : class, IEntity, new()
    {
        base.CacheFor<T>(policy);
        return this;
    }
}
