using System.Diagnostics;
using System.Globalization;
using Unified.Data.Tables.Sqlite;

namespace Unified.Data.Tables.Tests.Sqlite;

/// <summary>
/// Two processes on one database file. The other process is the helper program
/// (tests/Unified.Data.Tables.Sqlite.TestHelper), built beside the tests and started with the dotnet host:
/// a real second process with its own connections, not a second account in this one.
/// </summary>
public sealed class SqliteMultiProcessTests : IDisposable
{
    private readonly TempDatabase database = new();
    private readonly List<Process> started = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // No helper outlives its test, whatever the test's outcome.
        foreach (var process in started)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
            }
            catch (InvalidOperationException)
            {
                // It exited between the check and the kill.
            }
            process.Dispose();
        }
        database.Dispose();
    }

    private Process StartHelper(params string[] arguments)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "Unified.Data.Tables.Sqlite.TestHelper.dll");
        Assert.True(File.Exists(helper), $"The helper program was not built beside the tests: {helper}");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(helper);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The helper process did not start.");
        started.Add(process);
        return process;
    }

    /// <summary>
    /// Opens the file the killed helper left behind. Opening it recovers the write-ahead log, which
    /// truncates the log files. On Windows a handle to them can outlive the killed process for a
    /// moment: the process exits before every handle is released, and the virus scanner opens a file
    /// that was just written. Recovery then fails with SQLITE_IOERR (truncate) after SQLite's own short
    /// retries. That is the platform releasing the file, not the crash recovery under test, so the open
    /// is retried on an I/O error for a bounded time and every other failure is reported at once.
    /// </summary>
    private async Task<SqliteStorageAccount> ReopenAfterKillAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return database.Open(o => o.Cache = CachePolicy.Disabled);
            }
            catch (StorageException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 10 } && DateTime.UtcNow < deadline)
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"Reopen attempt {attempt} hit an I/O error while the file was still held: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
            }
        }
    }

    private static async Task<string> ExitedAsync(Process process)
    {
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, $"The helper process failed ({process.ExitCode}): {await error}{await output}");
        return await output;
    }

    [Fact]
    public async Task TwoProcesses_IncrementingOneRow_LoseNoUpdate_AndEveryLostRaceIsAConcurrencyConflict()
    {
        const int Each = 150;
        const string Id = "counters|shared";
        var account = database.Open(o => o.Cache = CachePolicy.Disabled);
        var store = new SqliteStorage<HelperCounter>(account);
        await store.CreateAsync(new HelperCounter { Id = Id }, Ct);

        // Both sides run the same loop: read, add one, write on condition of the version read. Anything a
        // lost race throws other than ConcurrencyConflictException ends the loop, here or in the helper.
        var helper = StartHelper("increment", database.Path, Id, Each.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("READY", await helper.StandardOutput.ReadLineAsync(Ct));
        var conflicts = 0;
        for (var done = 0; done < Each;)
        {
            var row = (await store.OneAsync(Id, Ct))!;
            row.Value++;
            try
            {
                await store.UpdateAsync(row, ConcurrencyMode.Strict, Ct);
                done++;
            }
            catch (ConcurrencyConflictException conflict)
            {
                Assert.False(conflict.MayBeOwnWrite); // this store never sends a write twice
                conflicts++;
            }
        }
        var report = await ExitedAsync(helper);

        // Every increment of both processes is in the row: none was overwritten by a stale write.
        Assert.Equal(2L * Each, (await store.OneAsync(Id, Ct))!.Value);
        Assert.StartsWith("DONE conflicts=", report.Trim(), StringComparison.Ordinal);
        TestContext.Current.TestOutputHelper?.WriteLine($"conflicts here: {conflicts}; the helper: {report.Trim()}");
    }

    [Fact]
    public async Task AWriteOnAVersionAnotherProcessReplaced_IsAConcurrencyConflict_OnEveryConditionalPath()
    {
        const string Id = "counters|raced";
        var account = database.Open(o => o.Cache = CachePolicy.Disabled);
        var store = new SqliteStorage<HelperCounter>(account);
        var created = await store.CreateAsync(new HelperCounter { Id = Id, Value = 1 }, Ct);
        var stale = (await store.OneAsync(Id, Ct))!;
        Assert.Equal(created.ETag, stale.ETag);

        Assert.Equal("DONE", (await ExitedAsync(StartHelper("touch", database.Path, Id, "-"))).Trim());

        // The other process's write is visible at once, and the version read before it is gone.
        var current = (await store.OneAsync(Id, Ct))!;
        Assert.Equal("touched by the other process", current.Note);
        Assert.NotEqual(stale.ETag, current.ETag);
        stale.Value = 99;
        var update = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpdateAsync(stale, ConcurrencyMode.Strict, Ct));
        Assert.Equal(Id, update.Id);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpdateAsync(Id, u => u.WithETag(stale.ETag!).SetProperty(c => c.Value, 99L), Ct));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.DeleteIfMatchAsync(Id, stale.ETag!, Ct));
        Assert.Equal(1, (await store.OneAsync(Id, Ct))!.Value);

        // With the version the other process left, the same writes go through.
        current.Value = 2;
        await store.UpdateAsync(current, ConcurrencyMode.Strict, Ct);
        Assert.Equal(2, (await store.OneAsync(Id, Ct))!.Value);
    }

    [Fact]
    public async Task WithTheReadCacheOn_ARowAnotherProcessChanged_IsServedStale_AndAConditionalWriteOnItStillConflicts()
    {
        const string Id = "counters|cached";
        var account = database.Open(); // the default: the Azure provider's sliding hour
        var store = new SqliteStorage<HelperCounter>(account);
        await store.CreateAsync(new HelperCounter { Id = Id, Value = 1 }, Ct);
        var cached = (await store.OneAsync(Id, Ct))!;

        Assert.Equal("DONE", (await ExitedAsync(StartHelper("touch", database.Path, Id, "-"))).Trim());

        // Stale, as a TableStorage<T> beside another instance is: the cache knows nothing of the other process.
        var stale = (await store.OneAsync(Id, Ct))!;
        Assert.Null(stale.Note);
        Assert.Equal(cached.ETag, stale.ETag);
        // What must never happen with a stale row: a conditional write on it going through.
        stale.Value = 99;
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpdateAsync(stale, ConcurrencyMode.Strict, Ct));
        // The failed write evicted the entry: the next read is the stored row.
        var fresh = (await store.OneAsync(Id, Ct))!;
        Assert.Equal("touched by the other process", fresh.Note);
        Assert.Equal(1, fresh.Value);
    }

    [Theory]
    [InlineData(SqliteSynchronousMode.Full)]
    [InlineData(SqliteSynchronousMode.Normal)]
    public async Task AProcessKilledInTheMiddleOfItsCommits_LeavesOnlyWholeTransactions_AndEveryAcknowledgedOne(SqliteSynchronousMode mode)
    {
        const int RowsPerBatch = 100;
        const string Partition = "log";
        database.Open(o => o.Synchronous = mode).Dispose(); // the file exists; nobody but the helper has it open

        // The helper commits all-or-nothing batches of 100 rows as fast as it can and reports each one
        // only after its commit returned. It is killed while it does that, without any warning.
        var helper = StartHelper("append", database.Path, Partition, RowsPerBatch.ToString(CultureInfo.InvariantCulture),
            mode == SqliteSynchronousMode.Normal ? "normal" : "full");
        var acknowledged = 0;
        while (acknowledged < 25)
        {
            var line = await helper.StandardOutput.ReadLineAsync(Ct);
            if (line is null)
                Assert.Fail("The helper stopped before it was killed: " + await helper.StandardError.ReadToEndAsync(Ct));
            acknowledged = int.Parse(line["COMMITTED ".Length..], CultureInfo.InvariantCulture);
        }
        helper.Kill(entireProcessTree: true);
        await helper.WaitForExitAsync(Ct);
        // Whatever it still reported before it died was acknowledged too.
        while (await helper.StandardOutput.ReadLineAsync(Ct) is { } line)
            acknowledged = int.Parse(line["COMMITTED ".Length..], CultureInfo.InvariantCulture);

        // Reopened by a process that knows nothing of the dead one: SQLite recovers from the log it left.
        var account = await ReopenAfterKillAsync();
        var store = new SqliteStorage<HelperBatchRow>(account);
        var rows = await store.QueryAsync(new QueryOptions { Partition = Partition }, Ct);
        var batches = rows.GroupBy(r => r.Batch).OrderBy(g => g.Key).ToList();

        // Only whole transactions: every batch that is there is there with all its rows.
        Assert.All(batches, batch => Assert.Equal(RowsPerBatch, batch.Count()));
        // Nothing acknowledged is missing, and nothing follows a gap: the batches are 1..n, and n is the
        // last acknowledged one or the one whose commit the kill let finish but not report.
        Assert.Equal(Enumerable.Range(1, batches.Count), batches.Select(b => b.Key));
        Assert.InRange(batches.Count, acknowledged, acknowledged + 1);
        Assert.Equal(batches.Count * RowsPerBatch, await store.CountAsync(Partition, Ct));
        // The database is whole, and takes writes again.
        Assert.Equal("ok", await account.Database.ReadAsync(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            return (string)command.ExecuteScalar()!;
        }, Ct));
        await store.CreateAsync(new HelperBatchRow { Id = Partition + "|after", Batch = 0 }, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"{mode}: killed after batch {acknowledged} was acknowledged; {batches.Count} batches found.");
    }
}
