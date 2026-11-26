using Azure.Data.Tables;
using Unified.Data.Tables.Conformance;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Sqlite;

namespace Unified.Data.Tables.Tests.Sqlite;

/// <summary>
/// A database file of one test, in a directory of its own under the temp directory (never in the source
/// tree), with every account the test opened on it. Disposing closes the accounts and removes the
/// directory.
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly List<SqliteStorageAccount> accounts = [];

    public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "udt-sqlite-tests", Guid.NewGuid().ToString("N"));

    public string Path => System.IO.Path.Combine(Directory, "tables.db");

    /// <summary>Opens the file as one more account (a second one is what another process would hold).</summary>
    public SqliteStorageAccount Open(Action<SqliteStorageOptions>? configure = null) => Open(Path, configure);

    /// <summary>Opens <paramref name="dataSource"/> (<c>:memory:</c>, say) as an account this instance disposes.</summary>
    public SqliteStorageAccount Open(string dataSource, Action<SqliteStorageOptions>? configure = null)
    {
        var options = new SqliteStorageOptions { DataSource = dataSource };
        configure?.Invoke(options);
        var account = new SqliteStorageAccount(options);
        accounts.Add(account);
        return account;
    }

    public void Dispose()
    {
        foreach (var account in accounts)
            account.Dispose();
        try
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A helper process that is still exiting holds the file for a moment; the temp directory is cleaned by the system.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The SQLite provider as the conformance kit sees it: every scenario store is a
/// <see cref="SqliteStorage{T}"/> (or <see cref="SqlitePolymorphicStorage{TBase}"/>) over a database of
/// its own, a file or <c>:memory:</c>, with the provider's default read cache (the Azure provider's), and
/// its physical rows readable.
/// </summary>
internal sealed class SqliteConformanceFactory(bool file, CachePolicy? cache = null) : IConformanceStoreFactory
{
    public string Name => (file ? "Sqlite (file)" : "Sqlite (:memory:)") + (cache is null ? "" : ", read cache " + (cache.Enabled ? "on" : "off"));

    // What the SQLite provider does differently from the contract: nothing.
    public IReadOnlyCollection<string> Leniencies { get; } = [];

    public Task<ConformanceStore<T>> CreateStoreAsync<T>(ConformanceStoreSettings settings, CancellationToken ct = default)
        where T : class, IEntity, new()
    {
        var database = new TempDatabase();
        var account = database.Open(file ? database.Path : SqliteStorageOptions.InMemoryDataSource, options =>
        {
            options.IdNormalization = settings.IdNormalization;
            options.Offload = settings.BlobOffload ? new OffloadOptions { CleanupGrace = TimeSpan.Zero } : null;
            if (cache is not null)
                options.Cache = cache;
        });
        var store = new SqliteStorage<T>(account);
        return Task.FromResult(new ConformanceStore<T>(store, RawRows(account, store.GetInfo().TableName!), () =>
        {
            database.Dispose();
            return default;
        }));
    }

    public Task<ConformancePolymorphicStore<TBase>> CreatePolymorphicStoreAsync<TBase>(CancellationToken ct = default)
        where TBase : class
    {
        var database = new TempDatabase();
        var account = database.Open(file ? database.Path : SqliteStorageOptions.InMemoryDataSource);
        return Task.FromResult(new ConformancePolymorphicStore<TBase>(
            new SqlitePolymorphicStorage<TBase>(account, "ConformanceEvents"), () =>
            {
                database.Dispose();
                return default;
            }));
    }

    // Below the engine: the rows as the database holds them.
    private static ConformanceRawRowReader RawRows(SqliteStorageAccount account, string table) =>
        async (partitionKey, ct) =>
        {
            if (!await account.Database.TableExistsAsync(table, ct))
                return [];
            var rows = await account.Database.GetTable(table).ReadAsync(new RowRange(partitionKey), int.MaxValue, ct);
            return [.. rows.Select(row => new ConformanceRawRow(row.PartitionKey, row.RowKey, Cells(row.Data)))];
        };

    /// <summary>The columns of a stored row, without the keys, the timestamp and the ETag.</summary>
    internal static Dictionary<string, object> Cells(TableEntity row) =>
        row.Where(cell => cell.Key is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag") && cell.Value is not null)
            .ToDictionary(cell => cell.Key, cell => cell.Value, StringComparer.Ordinal);
}
