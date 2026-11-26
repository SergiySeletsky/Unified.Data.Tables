#pragma warning disable UDT0001 // the experimental table store is part of what an account hands out
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Sqlite;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.Sqlite;

/// <summary>
/// The account of the SQLite provider: what it reports about durability (read back from the database,
/// not assumed), what it records in the file, how a SQLite failure leaves a store, and its DI
/// registration.
/// </summary>
// AddUnifiedSqliteStorage applies its options to the process-wide OversizedCellPolicy, as the other registrations do.
[Collection("OversizedCellPolicy")]
public sealed class SqliteAccountTests : IDisposable
{
    private readonly TempDatabase database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => database.Dispose();

    public sealed class Note : Entity
    {
        public string? Text { get; set; }
    }

    public abstract class Event
    {
        public string? Name { get; set; }
    }

    public sealed class Opened : Event;

    // A connection of the test's own, as another program would open the file.
    private SqliteConnection Raw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    // ── Durability and what the file records ────────────────────────────────

    [Theory]
    [InlineData(SqliteSynchronousMode.Full, Durability.Fsynced, 2L)]
    [InlineData(SqliteSynchronousMode.Normal, Durability.OsBuffered, 1L)]
    public async Task AFileDatabase_ReportsTheDurabilityOfItsSynchronousMode_InWriteAheadMode(
        SqliteSynchronousMode mode, Durability expected, long pragma)
    {
        var account = database.Open(o => o.Synchronous = mode);
        var store = new SqliteStorage<Note>(account);
        var events = new SqlitePolymorphicStorage<Event>(account, "Events");

        Assert.Equal(expected, account.Durability);
        Assert.Equal(expected, store.GetInfo().Durability);
        Assert.Equal(expected, events.GetInfo().Durability);
        Assert.Equal("Sqlite", store.GetInfo().ProviderName);
        Assert.Equal("Note", store.GetInfo().TableName);
        Assert.Equal("Events", events.GetInfo().TableName);
        Assert.Equal(Path.GetFullPath(database.Path), account.DataSource);

        // What was reported is what the connections really run with.
        Assert.Equal("wal", await account.Database.ReadAsync(c => (string)Scalar(c, "PRAGMA journal_mode")!, Ct));
        Assert.Equal(pragma, await account.Database.ReadAsync(c => (long)Scalar(c, "PRAGMA synchronous")!, Ct));
        Assert.Equal(pragma, await account.Database.WriteAsync(c => (long)Scalar(c, "PRAGMA synchronous")!, Ct));
        // Write-ahead logging is a property of the file: any other program that opens it finds it on.
        using var raw = Raw();
        Assert.Equal("wal", Scalar(raw, "PRAGMA journal_mode"));
    }

    [Theory]
    [InlineData(SqliteSynchronousMode.Full)]
    [InlineData(SqliteSynchronousMode.Normal)]
    public void AMemoryDatabase_IsVolatile_WhateverItsSynchronousMode(SqliteSynchronousMode mode)
    {
        var account = database.Open(SqliteStorageOptions.InMemoryDataSource, o => o.Synchronous = mode);

        Assert.Equal(Durability.Volatile, account.Durability);
        Assert.Equal(Durability.Volatile, new SqliteStorage<Note>(account).GetInfo().Durability);
        Assert.Equal(":memory:", account.DataSource);
        Assert.False(Directory.Exists(database.Directory)); // nothing on disk
    }

    [Fact]
    public async Task Info_StatesTheServicesLimits_AndWhetherTheReadCacheMayServeStaleRows()
    {
        var account = database.Open();
        var cached = new SqliteStorage<Note>(account).GetInfo();
        var uncached = new SqliteStorage<Note>(account, options: new SqliteStorageOptions { Cache = CachePolicy.Disabled }).GetInfo();
        var perType = new SqliteStorage<Note>(account, options: new SqliteStorageOptions().CacheFor<Note>(CachePolicy.Disabled)).GetInfo();

        // The same default as the Azure provider: a read cache that may serve a stale row.
        Assert.True(cached.ReadCacheMayServeStaleRows);
        Assert.False(uncached.ReadCacheMayServeStaleRows);
        Assert.False(perType.ReadCacheMayServeStaleRows);
        Assert.False(new SqlitePolymorphicStorage<Event>(account, "Events").GetInfo().ReadCacheMayServeStaleRows);
        Assert.Equal((100, 3_000_000L, 64 * 1024L, 1_000_000L, true),
            (cached.MaxBatchRows!.Value, cached.MaxBatchBytes!.Value, cached.MaxCellBytes!.Value, cached.MaxRowBytes!.Value, cached.SinglePartitionTransactions!.Value));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheFile_RecordsItsSchemaVersion_AndKeepsTheRowsInTheDocumentedLayout()
    {
        var account = database.Open();
        var store = new SqliteStorage<Note>(account);
        await store.CreateAsync(new Note { Id = "p|r", Text = "hello" }, Ct);
        account.Dispose();

        using var raw = Raw();
        Assert.Equal(1L, Scalar(raw, "SELECT value FROM \"__meta\" WHERE key = 'schema_version'"));
        Assert.Equal((long)SqliteDatabase.SchemaVersion, Scalar(raw, "SELECT value FROM \"__meta\" WHERE key = 'schema_version'"));
        Assert.Equal(1L, Scalar(raw, "SELECT value FROM \"__meta\" WHERE key = 'etag'"));
        var sql = (string)Scalar(raw, "SELECT sql FROM sqlite_schema WHERE name = 'Note'")!;
        Assert.Contains("pk BLOB NOT NULL, rk BLOB NOT NULL, ver INTEGER NOT NULL, ts INTEGER NOT NULL, cells BLOB NOT NULL", sql);
        Assert.Contains("PRIMARY KEY (pk, rk)", sql);
        Assert.EndsWith("WITHOUT ROWID", sql);
        // The keys: UTF-16, big-endian. The version: the database-wide counter. The cells: format 1.
        Assert.Equal("0070", Scalar(raw, "SELECT hex(pk) FROM \"Note\""));
        Assert.Equal("0072", Scalar(raw, "SELECT hex(rk) FROM \"Note\""));
        Assert.Equal(1L, Scalar(raw, "SELECT ver FROM \"Note\""));
        Assert.Equal((long)SqliteRowCodec.CellsFormat, Scalar(raw, "SELECT instr(cells, x'01') FROM \"Note\""));
        Assert.NotNull(Scalar(raw, "SELECT 1 FROM sqlite_schema WHERE name = '__blobs'"));
    }

    [Fact]
    public async Task ADatabase_IsReopened_WithItsRowsETagsAndTables()
    {
        string etag;
        {
            var account = database.Open();
            var store = new SqliteStorage<Note>(account);
            etag = (await store.CreateAsync(new Note { Id = "p|r", Text = "kept" }, Ct)).ETag!;
            await new SqlitePolymorphicStorage<Event>(account, "Events").InsertAsync(new PolymorphicWrite<Event>(new TableKey("s", "1"), new Opened { Name = "x" }), Ct);
            account.Dispose();
        }
        Assert.False(File.Exists(database.Path + "-wal")); // the last connection to close checkpoints the log into the file

        var reopened = database.Open();
        var again = new SqliteStorage<Note>(reopened);
        var read = (await again.OneAsync("p|r", Ct))!;

        Assert.Equal(("kept", etag), (read.Text, read.ETag));
        Assert.Equal(["Events", "Note"], await reopened.Tables.ListTablesAsync(Ct).ToListAsync(Ct));
        Assert.IsType<Opened>((await new SqlitePolymorphicStorage<Event>(reopened, "Events").GetAsync(new TableKey("s", "1"), Ct))!.Item);
        // The stored ETag still guards the row: the write that names it wins, the next one with it loses.
        read.Text = "changed";
        var updated = await again.UpdateAsync(read, ConcurrencyMode.Strict, Ct);
        Assert.NotEqual(etag, updated.ETag);
        read.ETag = etag;
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => again.UpdateAsync(read, ConcurrencyMode.Strict, Ct));
    }

    [Fact]
    public void ADatabaseOfANewerSchemaVersion_IsRefusedWhenOpened_AndLeftAsItIs()
    {
        database.Open().Dispose();
        using (var raw = Raw())
            Scalar(raw, "UPDATE \"__meta\" SET value = 99 WHERE key = 'schema_version'");

        var refused = Assert.Throws<StorageException>(() => database.Open());

        Assert.Contains("schema version 99", refused.Message);
        using var after = Raw();
        Assert.Equal(99L, Scalar(after, "SELECT value FROM \"__meta\" WHERE key = 'schema_version'"));
    }

    [Fact]
    public void TheDirectoryOfTheFile_IsCreated_AndOptionsRefuseWhatCannotWork()
    {
        var nested = Path.Combine(database.Directory, "a", "b", "tables.db");

        using (var account = new SqliteStorageAccount(nested))
            Assert.Equal(Path.GetFullPath(nested), account.DataSource);

        Assert.True(File.Exists(nested));
        Assert.Throws<ArgumentException>(() => new SqliteStorageOptions { DataSource = " " });
        Assert.Throws<ArgumentException>(() => new SqliteStorageAccount(""));
        Assert.Throws<ArgumentNullException>(() => new SqliteStorageAccount((SqliteStorageOptions)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteStorageOptions { BusyTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteStorageOptions { BusyTimeout = TimeSpan.FromDays(2) });
        Assert.Equal(TimeSpan.FromSeconds(30), new SqliteStorageOptions().BusyTimeout);
        Assert.Equal(SqliteSynchronousMode.Full, new SqliteStorageOptions().Synchronous);
        // The read cache is the Azure provider's default, not the in-memory provider's "off".
        Assert.Equal(new UnifiedTableStorageOptions().Cache, new SqliteStorageOptions().Cache);
        Assert.True(new SqliteStorageOptions().Cache.Enabled);
    }

    // ── One writer, many readers ────────────────────────────────────────────

    public sealed class Tally : Entity
    {
        public long Value { get; set; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManyWritersAndReadersOfOneProcess_LoseNoUpdate_AndNoReadFails(bool file)
    {
        const int Writers = 8, Readers = 16, Each = 40;
        // The writer gate is an in-process lock that reports a timeout as a transient database lock. This test
        // measures lost updates, not how promptly a loaded runner schedules the writer, so it gives the gate
        // ample headroom: 24 hot tasks plus the other test assemblies can otherwise starve one holder of the
        // gate for the default 30 s on a 4-vCPU runner.
        var account = database.Open(file ? database.Path : SqliteStorageOptions.InMemoryDataSource, o =>
        {
            o.Cache = CachePolicy.Disabled;
            o.BusyTimeout = TimeSpan.FromMinutes(2);
            // The invariant is lost updates, not fsync durability: 320 commits at Full would spend the test
            // waiting on the CI disk (and is what a host wanting speed would configure too).
            o.Synchronous = SqliteSynchronousMode.Normal;
        });
        // Two stores over the account, as two components of a host have: the writers of both take turns.
        SqliteStorage<Tally>[] stores = [new(account), new(account)];
        await RetryingWriteAsync(() => stores[0].CreateAsync(new Tally { Id = "tally|shared" }, Ct), Ct);
        using var done = new CancellationTokenSource();
        long reads = 0, conflicts = 0;

        var readers = Enumerable.Range(0, Readers).Select(i => Task.Run(async () =>
        {
            long last = 0;
            while (!done.IsCancellationRequested)
            {
                var value = (await stores[i % 2].OneAsync("tally|shared", Ct))!.Value;
                Assert.True(value >= last, $"A read went back in time: {value} after {last}."); // a reader sees committed states only, in order
                last = value;
                Assert.InRange((await stores[i % 2].QueryAsync(new QueryOptions { Partition = "tally" }, Ct)).Count, 1, 1 + Writers);
                Interlocked.Increment(ref reads);
                // One tight loop per reader would leave no core for a writer: yield a millisecond per read.
                await Task.Delay(1, Ct);
            }
        }, Ct)).ToArray();
        var writers = Enumerable.Range(0, Writers).Select(i => Task.Run(async () =>
        {
            var store = stores[i % 2];
            await RetryingWriteAsync(() => store.CreateAsync(new Tally { Id = $"tally|own-{i}" }, Ct), Ct);
            for (var n = 0; n < Each;)
            {
                var row = (await RetryingAsync(() => store.OneAsync("tally|shared", Ct), Ct))!;
                row.Value++;
                try
                {
                    await RetryingWriteAsync(() => store.UpdateAsync(row, ConcurrencyMode.Strict, Ct), Ct);
                    n++;
                }
                catch (ConcurrencyConflictException)
                {
                    Interlocked.Increment(ref conflicts);
                }
            }
        }, Ct)).ToArray();

        await Task.WhenAll(writers);
        await done.CancelAsync();
        await Task.WhenAll(readers);

        Assert.Equal(Writers * Each, (await stores[0].OneAsync("tally|shared", Ct))!.Value);
        Assert.Equal(1 + Writers, await stores[1].CountAsync("tally", Ct));
        Assert.True(Interlocked.Read(ref reads) > 0);
        TestContext.Current.TestOutputHelper?.WriteLine($"{reads} reads beside {Writers * Each} increments; {conflicts} lost races, each a ConcurrencyConflictException.");
    }

    // A busy lock is transient by contract: retry until the test's own deadline, so a starved runner is not
    // reported as a lost update. Any other failure propagates at once, and a genuine deadlock still fails.
    private static async Task<T> RetryingAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (TransientStorageException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, ct);
            }
        }
    }

    private static async Task RetryingWriteAsync(Func<Task> operation, CancellationToken ct) =>
        await RetryingAsync(
            async () =>
            {
                await operation();
                return true;
            },
            ct);

    // ── Failures ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADatabaseLockedByAnotherWriter_IsATransientFailure_ThatDidNotCommit_AndReadsGoOn()
    {
        var account = database.Open(o => o.BusyTimeout = TimeSpan.FromSeconds(1));
        var store = new SqliteStorage<Note>(account, options: new SqliteStorageOptions { Cache = CachePolicy.Disabled });
        await store.CreateAsync(new Note { Id = "p|kept", Text = "before" }, Ct);
        using var other = Raw();
        Scalar(other, "BEGIN IMMEDIATE"); // another program holds the write lock and does not let go

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var busy = await Assert.ThrowsAsync<TransientStorageException>(() => store.UpsertAsync(new Note { Id = "p|lost", Text = "never" }, Ct));
        watch.Stop();

        Assert.False(busy.MayHaveCommitted);
        Assert.Equal("SQLITE_BUSY", busy.ProviderErrorCode);
        var inner = Assert.IsType<SqliteException>(busy.InnerException);
        Assert.Equal(5, inner.SqliteErrorCode);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(10)); // it waited for the busy timeout, and no longer than a few of them
        // A reader never waits for a writer.
        Assert.Equal("before", (await store.OneAsync("p|kept", Ct))!.Text);
        Assert.Equal(1, await store.CountAsync(ct: Ct));

        // Once the other writer is done, the same write goes through: nothing was half-applied.
        Scalar(other, "COMMIT");
        await store.UpsertAsync(new Note { Id = "p|lost", Text = "now" }, Ct);
        Assert.Equal(2, await store.CountAsync(ct: Ct));
    }

    [Fact]
    public async Task AWriteWaitingForAnotherWriter_GoesThrough_WhenTheLockIsReleasedInTime()
    {
        var account = database.Open(o => o.BusyTimeout = TimeSpan.FromSeconds(20));
        var store = new SqliteStorage<Note>(account);
        await store.CreateAsync(new Note { Id = "p|a" }, Ct);
        using var other = Raw();
        Scalar(other, "BEGIN IMMEDIATE");

        var write = Task.Run(() => store.CreateAsync(new Note { Id = "p|b" }, Ct), Ct);
        await Task.Delay(300, Ct);
        Assert.False(write.IsCompleted);
        Scalar(other, "COMMIT");

        await write;
        Assert.Equal(2, await store.CountAsync(ct: Ct));
    }

    [Fact]
    public async Task AnyOtherSqliteFailure_IsAStorageException_WithTheSqliteCode_AndTheSqliteExceptionInside()
    {
        // A file that is not a database.
        Directory.CreateDirectory(database.Directory);
        await File.WriteAllTextAsync(database.Path, new string('x', 4096), Ct);

        var notADatabase = Assert.Throws<StorageException>(() => database.Open());

        Assert.IsNotType<TransientStorageException>(notADatabase);
        Assert.Equal("SQLITE_NOTADB", notADatabase.ProviderErrorCode);
        Assert.Equal(26, Assert.IsType<SqliteException>(notADatabase.InnerException).SqliteErrorCode);
        Assert.Contains("SQLITE_NOTADB", notADatabase.Message);
    }

    [Fact]
    public async Task AFailureInsideAnOperation_LeavesEveryStorageMemberAsANeutralException_NeverAsASqliteOne()
    {
        var account = database.Open();
        var store = new SqliteStorage<Note>(account, options: new SqliteStorageOptions { Cache = CachePolicy.Disabled });
        await store.CreateAsync(new Note { Id = "p|r", Text = "x" }, Ct);
        // Someone replaces the table with one of another shape behind the store: every statement of the
        // store now fails inside SQLite.
        using (var raw = Raw())
            Scalar(raw, "DROP TABLE \"Note\"; CREATE TABLE \"Note\" (other INTEGER)");

        Func<Task>[] members =
        [
            () => store.OneAsync("p|r", Ct),
            () => store.ExistsAsync("p|r", Ct),
            () => store.QueryAsync("p", Ct),
            () => store.CountAsync(ct: Ct),
            () => store.CreateAsync(new Note { Id = "p|new" }, Ct),
            () => store.UpsertAsync(new Note { Id = "p|r" }, Ct),
            () => store.UpdateAsync("p|r", u => u.SetProperty(n => n.Text, "y"), Ct),
            () => store.DeleteAsync("p|r", Ct),
            () => store.DeleteIfExistsAsync("p|r", Ct),
            () => store.DeletePartitionAsync("p", Ct),
            () => store.CreateBatchAsync([new Note { Id = "p|b" }], Ct),
            () => store.CreateAtomicAsync([new Note { Id = "p|c" }], Ct),
            () => store.QueryPageAsync(new QueryOptions { Partition = "p" }, Ct),
            async () => { await foreach (var _ in store.QueryStreamAsync(ct: Ct)) { } },
        ];
        foreach (var member in members)
        {
            var failure = await Assert.ThrowsAsync<StorageException>(member);
            Assert.Equal("SQLITE_ERROR", failure.ProviderErrorCode);
            Assert.IsType<SqliteException>(failure.InnerException);
        }
    }

    [Fact]
    public async Task ATableDroppedBehindTheStore_IsTableNotFound_NotAMissingRow()
    {
        var account = database.Open();
        var store = new SqliteStorage<Note>(account, options: new SqliteStorageOptions { Cache = CachePolicy.Disabled });
        await store.CreateAsync(new Note { Id = "p|r" }, Ct);
        Assert.True(await account.Tables.DeleteTableAsync("Note", Ct));

        var missing = await Assert.ThrowsAsync<TableNotFoundException>(() => store.OneAsync("p|r", Ct));

        Assert.Equal("TableNotFound", missing.ProviderErrorCode);
        await Assert.ThrowsAsync<TableNotFoundException>(() => store.UpsertAsync(new Note { Id = "p|r" }, Ct));
    }

    [Fact]
    public async Task ADisposedAccount_RefusesEveryOperation_AndReleasesTheFile()
    {
        var account = database.Open();
        var store = new SqliteStorage<Note>(account);
        await store.CreateAsync(new Note { Id = "p|r" }, Ct);
        await store.OneAsync("p|other", Ct); // a reader connection is open as well

        await account.DisposeAsync();
        account.Dispose(); // twice is fine

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.UpsertAsync(new Note { Id = "p|r" }, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.OneAsync("p|other", Ct));
        File.Delete(database.Path); // no connection holds it (this throws on Windows when one does)
        Assert.False(File.Exists(database.Path));
    }

    // ── Registration ────────────────────────────────────────────────────────

    [Fact]
    public async Task AddUnifiedSqliteStorage_RegistersTheStoresOverOneAccount_TheOptionsAndTheTableStore()
    {
        var services = new ServiceCollection();
        services.AddUnifiedSqliteStorage(database.Path, o =>
        {
            o.Synchronous = SqliteSynchronousMode.Normal;
            o.IdNormalization = IdNormalization.AsWritten;
        });
        services.AddUnifiedSqlitePolymorphicTable<Event>("Events");
        services.AddUnifiedSqlitePolymorphicTable<Event>("Mapped", o => o.TypeDiscriminator = new TypeDiscriminatorMap().Map<Opened>("opened"));
        SqliteStorageAccount account;
        await using (var provider = services.BuildServiceProvider())
        {
            var notes = provider.GetRequiredService<IStorage<Note>>();
            account = provider.GetRequiredService<SqliteStorageAccount>();
            var events = provider.GetRequiredKeyedService<IPolymorphicStorage<Event>>("Events");
            var mapped = provider.GetRequiredKeyedService<IPolymorphicStorage<Event>>("Mapped");

            Assert.IsType<SqliteStorage<Note>>(notes);
            Assert.IsType<SqlitePolymorphicStorage<Event>>(events);
            Assert.Same(notes, provider.GetRequiredService<IStorage<Note>>());
            Assert.Same(provider.GetRequiredService<SqliteStorageOptions>(), provider.GetRequiredService<StorageOptions>());
            Assert.Equal(Durability.OsBuffered, notes.GetInfo().Durability);

            await notes.CreateAsync(new Note { Id = "Mixed|Case" }, Ct);
            await events.InsertAsync(new PolymorphicWrite<Event>(new TableKey("s", "1"), new Opened()), Ct);
            var entry = await mapped.InsertAsync(new PolymorphicWrite<Event>(new TableKey("s", "1"), new Opened()), Ct);

            Assert.NotNull(await notes.OneAsync("Mixed|Case", Ct));
            Assert.Null(await notes.OneAsync("mixed|case", Ct)); // the registered options reached the store
            Assert.Equal("opened", entry.Discriminator);
            // One table store for the account: the one in DI, the one a store's probe answers with, and it sees every table.
            var tables = provider.GetRequiredService<ITableStore>();
            Assert.Same(account.Tables, tables);
            Assert.True(notes.TryGetCapability<Note, ITableStore>(out var probed));
            Assert.Same(tables, probed);
            Assert.True(events.TryGetCapability<Event, ITableStore>(out var probedByEvents));
            Assert.Same(tables, probedByEvents);
            Assert.Equal(["Events", "Mapped", "Note"], await tables.ListTablesAsync(Ct).ToListAsync(Ct));
            Assert.NotNull(await tables.GetTable("Note").GetAsync("Mixed", "Case", Ct));
        }

        // The container owned the account: disposing it closed the database.
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await account.Database.TableExistsAsync("Note", Ct));
    }

    [Fact]
    public void AddUnifiedSqliteStorage_FirstRegistrationWins_AndThePolymorphicTableNeedsTheAccount()
    {
        var services = new ServiceCollection();
        services.AddUnifiedSqliteStorage(SqliteStorageOptions.InMemoryDataSource, o => o.BusyTimeout = TimeSpan.FromSeconds(7));
        services.AddUnifiedSqliteStorage(database.Path, o => o.BusyTimeout = TimeSpan.FromSeconds(9));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromSeconds(7), provider.GetRequiredService<SqliteStorageOptions>().BusyTimeout);
        Assert.Equal(":memory:", provider.GetRequiredService<SqliteStorageAccount>().DataSource);
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddUnifiedSqliteStorage(" "));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddUnifiedSqlitePolymorphicTable<Event>(" "));

        using var alone = new ServiceCollection().AddUnifiedSqlitePolymorphicTable<Event>("Events").BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => alone.GetRequiredKeyedService<IPolymorphicStorage<Event>>("Events"));
    }

    [Fact]
    public void ThePackage_ReferencesNeitherTheAzureProviderNorBlobStorage()
    {
        var referenced = typeof(SqliteStorageAccount).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.Contains("Unified.Data.Tables.Core", referenced);
        Assert.Contains("Microsoft.Data.Sqlite", referenced);
        Assert.DoesNotContain("Unified.Data.Tables", referenced);
        Assert.DoesNotContain("Azure.Storage.Blobs", referenced);
    }
}
