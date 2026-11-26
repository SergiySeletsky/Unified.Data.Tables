using System.Globalization;
using Unified.Data.Tables;
using Unified.Data.Tables.Sqlite;

// The other process of the SQLite multi-process tests (see SqliteMultiProcessTests). Every command opens
// the database file it is given as a second, independent process would: its own account, no read cache
// (what it reads is what is stored), and it reports on standard output, one line a fact.
//
//   increment <db> <id> <count>   <count> optimistic increments of one counter row, retried on a conflict
//   touch <db> <id>               one unconditional rewrite of the row, to move its ETag
//   append <db> <partition> <rows> [normal]  all-or-nothing batches of <rows> rows until it is killed (or
//                                 a minute has passed); with synchronous NORMAL when asked, FULL otherwise
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: increment <db> <id> <count> | touch <db> <id> | append <db> <partition> <rows>");
    return 2;
}

var options = new SqliteStorageOptions
{
    DataSource = args[1],
    Cache = CachePolicy.Disabled,
    Synchronous = args.Length > 4 && args[4] == "normal" ? SqliteSynchronousMode.Normal : SqliteSynchronousMode.Full,
};
using var account = new SqliteStorageAccount(options);
switch (args[0])
{
    case "increment":
    {
        var store = new SqliteStorage<HelperCounter>(account);
        var count = int.Parse(args[3], CultureInfo.InvariantCulture);
        Console.WriteLine("READY");
        var conflicts = 0;
        for (var done = 0; done < count;)
        {
            var row = await store.OneAsync(args[2]) ?? throw new InvalidOperationException("The counter row is not there.");
            row.Value++;
            try
            {
                await store.UpdateAsync(row, ConcurrencyMode.Strict);
                done++;
            }
            catch (ConcurrencyConflictException)
            {
                conflicts++; // the other process wrote first: read again
            }
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"DONE conflicts={conflicts}"));
        return 0;
    }

    case "touch":
    {
        var store = new SqliteStorage<HelperCounter>(account);
        await store.UpdateAsync(args[2], update => update.SetProperty(c => c.Note, "touched by the other process"));
        Console.WriteLine("DONE");
        return 0;
    }

    case "append":
    {
        var store = new SqliteStorage<HelperBatchRow>(account);
        var rows = int.Parse(args[3], CultureInfo.InvariantCulture);
        var payload = new string('x', 2_000);
        // It is meant to be killed. Should the test process die first, it stops by itself after a
        // minute instead of filling the disk.
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var batch = 1; ; batch++)
        {
            if (started.Elapsed > TimeSpan.FromMinutes(1))
                return 3;
            var entities = Enumerable.Range(0, rows)
                .Select(i => new HelperBatchRow
                {
                    Id = string.Create(CultureInfo.InvariantCulture, $"{args[2]}|{batch:D8}-{i:D3}"),
                    Batch = batch,
                    Payload = payload,
                })
                .ToList();
            await store.CreateAtomicAsync(entities);
            // Acknowledged: from here on the batch must survive the death of this process.
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"COMMITTED {batch}"));
        }
    }

    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        return 2;
}

/// <summary>The counter row both processes increment.</summary>
public sealed class HelperCounter : Entity
{
    public long Value { get; set; }

    public string? Note { get; set; }
}

/// <summary>One row of an all-or-nothing batch of the append loop.</summary>
public sealed class HelperBatchRow : Entity
{
    public int Batch { get; set; }

    public string? Payload { get; set; }
}
