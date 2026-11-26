using System.Globalization;
using System.Runtime.CompilerServices;
using Azure.Data.Tables;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.InMemory;

/// <summary>
/// The tables of one in-memory account: what an in-memory store keeps its rows in. Table names are
/// compared ignoring case, as the Table service compares them. Nothing outlives the process
/// (<see cref="Durability.Volatile"/>).
/// </summary>
internal sealed class InMemoryAccount : IRowStoreAccount
{
    /// <summary>The provider name the in-memory stores report (<see cref="StorageInfo.ProviderName"/>).</summary>
    internal const string Provider = "InMemory";

    private readonly Dictionary<string, InMemoryTable> tables = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string ProviderName => Provider;

    /// <inheritdoc />
    public Durability Durability => Durability.Volatile;

    /// <summary>The table of that name, existing or not: one instance per name, so a handle stays valid when the table is created or deleted.</summary>
    internal InMemoryTable Table(string tableName)
    {
        lock (tables)
        {
            if (!tables.TryGetValue(tableName, out var table))
                tables[tableName] = table = new InMemoryTable(tableName);
            return table;
        }
    }

    /// <inheritdoc />
    public IRowStore GetTable(string tableName) => Table(tableName);

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListTablesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        List<string> names;
        lock (tables)
        {
            names = tables.Values.Where(t => t.Exists).Select(t => t.TableName).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            yield return name;
        }
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> TableExistsAsync(string tableName, CancellationToken ct) => new(Table(tableName).Exists);

    /// <inheritdoc />
    public ValueTask<bool> CreateTableAsync(string tableName, CancellationToken ct) => new(Table(tableName).Create());

    /// <inheritdoc />
    public ValueTask<bool> DeleteTableAsync(string tableName, CancellationToken ct) => new(Table(tableName).Drop());
}

/// <summary>
/// The rows of one in-memory table: per partition a dictionary by row key, read in ordinal key order
/// (partition key, then row key), each commit applied under one lock. A row's version is a counter of the
/// table, raised by every write of a row, and its ETag is <c>W/"version"</c>.
/// </summary>
/// <remarks>
/// The row-store operations complete synchronously; the other members are the synchronous view the
/// in-memory stores give their tests (counts, physical rows, seeding a row as is).
/// </remarks>
internal sealed class InMemoryTable(string tableName) : IRowStore
{
    private sealed class Partition
    {
        public Dictionary<string, StoredRow> Rows { get; } = new(StringComparer.Ordinal);

        // The row keys in ordinal order; null after a row was added or removed, until a read sorts them again.
        public string[]? Sorted { get; set; }

        public string[] SortedKeys()
        {
            if (Sorted is null)
            {
                var keys = Rows.Keys.ToArray();
                Array.Sort(keys, StringComparer.Ordinal);
                Sorted = keys;
            }
            return Sorted;
        }
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Partition> partitions = new(StringComparer.Ordinal);
    private string[]? sortedPartitions;
    private long versionCounter;
    private int rowCount;
    private volatile bool exists;

    /// <inheritdoc />
    public string TableName { get; } = tableName;

    /// <summary>Whether the table has been created (and not deleted since).</summary>
    internal bool Exists => exists;

    /// <summary>Creates the table when it does not exist; true when this call created it.</summary>
    internal bool Create()
    {
        lock (gate)
        {
            if (exists)
                return false;
            exists = true;
            return true;
        }
    }

    /// <summary>Deletes the table and its rows; false when there was none.</summary>
    internal bool Drop()
    {
        lock (gate)
        {
            if (!exists)
                return false;
            exists = false;
            RemoveAll();
            return true;
        }
    }

    private static string ETagOf(long version) => $"W/\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    private void RequireTable()
    {
        if (!exists)
            throw new RowStoreTableMissingException(TableName);
    }

    // ── IRowStore ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public ValueTask<StoredRow?> GetAsync(string partitionKey, string rowKey, CancellationToken ct)
    {
        lock (gate)
        {
            RequireTable();
            return new(partitions.TryGetValue(partitionKey, out var partition) && partition.Rows.TryGetValue(rowKey, out var row) ? row : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<StoredRow>> ReadAsync(RowRange range, int limit, CancellationToken ct)
    {
        lock (gate)
        {
            RequireTable();
            var page = new List<StoredRow>(Math.Min(limit, Math.Max(rowCount, 1)));
            if (range.PartitionKey is { } only)
            {
                // A resume key of another partition: before it everything is still to come, after it nothing is.
                var after = range.After;
                var order = after is null ? 0 : string.CompareOrdinal(after.Value.PartitionKey, only);
                if (order <= 0 && partitions.TryGetValue(only, out var partition))
                    Fill(page, partition, range, after is { } resume && order == 0 ? resume.RowKey : null, limit);
            }
            else
            {
                var names = sortedPartitions ??= SortedPartitions();
                var start = 0;
                if (range.After is { } after)
                {
                    start = Array.BinarySearch(names, after.PartitionKey, StringComparer.Ordinal);
                    if (start < 0)
                        start = ~start;
                }
                for (var i = start; i < names.Length && page.Count < limit; i++)
                {
                    var resume = range.After is { } a && string.Equals(a.PartitionKey, names[i], StringComparison.Ordinal) ? a.RowKey : null;
                    Fill(page, partitions[names[i]], RowRange.All, resume, limit);
                }
            }
            return new(page);
        }
    }

    private string[] SortedPartitions()
    {
        var names = partitions.Keys.ToArray();
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    // Appends the rows of one partition that lie within the bounds and after the resume key, in order.
    private static void Fill(List<StoredRow> page, Partition partition, RowRange range, string? afterRowKey, int limit)
    {
        var keys = partition.SortedKeys();
        var index = 0;
        if (range.FromRowKey is { } from)
            index = Math.Max(index, First(keys, from, range.FromInclusive));
        if (afterRowKey is not null)
            index = Math.Max(index, First(keys, afterRowKey, inclusive: false));
        for (; index < keys.Length && page.Count < limit; index++)
        {
            if (range.ToRowKey is { } to)
            {
                var c = string.CompareOrdinal(keys[index], to);
                if (c > 0 || (c == 0 && !range.ToInclusive))
                    break;
            }
            page.Add(partition.Rows[keys[index]]);
        }
    }

    // The index of the first key at (inclusive) or above the bound.
    private static int First(string[] keys, string bound, bool inclusive)
    {
        var index = Array.BinarySearch(keys, bound, StringComparer.Ordinal);
        if (index < 0)
            return ~index;
        return inclusive ? index : index + 1;
    }

    /// <inheritdoc />
    public ValueTask<RowCommitResult> CommitAsync(string partitionKey, IReadOnlyList<RowWrite> writes, CancellationToken ct)
    {
        lock (gate)
        {
            RequireTable();
            partitions.TryGetValue(partitionKey, out var partition);

            // Every condition holds, or nothing is applied.
            for (var i = 0; i < writes.Count; i++)
            {
                var write = writes[i];
                StoredRow? current = null;
                partition?.Rows.TryGetValue(write.RowKey, out current);
                switch (write.Condition)
                {
                    case RowCondition.MustNotExist when current is not null:
                        return new(RowCommitResult.Failed(RowCommitFailure.AlreadyExists, i));
                    case RowCondition.MustExist when current is null:
                        return new(RowCommitResult.Failed(RowCommitFailure.NotFound, i));
                    case RowCondition.MustExist when write.IfMatch is not null && !string.Equals(current!.ETag, write.IfMatch, StringComparison.Ordinal):
                        return new(RowCommitResult.Failed(RowCommitFailure.VersionMismatch, i));
                }
            }

            var now = DateTimeOffset.UtcNow;
            var etags = new string?[writes.Count];
            for (var i = 0; i < writes.Count; i++)
            {
                var write = writes[i];
                if (write.Kind == RowWriteKind.Delete)
                    RemoveRow(partitionKey, write.RowKey);
                else
                    etags[i] = Put(write.Row!, now).ETag;
            }
            return new(new RowCommitResult(RowCommitFailure.None, -1, etags, now));
        }
    }

    // Under the gate: stores the row as its next version.
    private StoredRow Put(TableEntity data, DateTimeOffset now)
    {
        if (!partitions.TryGetValue(data.PartitionKey, out var partition))
        {
            partitions[data.PartitionKey] = partition = new Partition();
            sortedPartitions = null;
        }
        var stored = new StoredRow(data, ETagOf(++versionCounter), now);
        if (!partition.Rows.ContainsKey(data.RowKey))
        {
            partition.Sorted = null;
            rowCount++;
        }
        partition.Rows[data.RowKey] = stored;
        return stored;
    }

    // Under the gate.
    private bool RemoveRow(string partitionKey, string rowKey)
    {
        if (!partitions.TryGetValue(partitionKey, out var partition) || !partition.Rows.Remove(rowKey))
            return false;
        partition.Sorted = null;
        rowCount--;
        if (partition.Rows.Count == 0)
        {
            partitions.Remove(partitionKey);
            sortedPartitions = null;
        }
        return true;
    }

    // Under the gate.
    private void RemoveAll()
    {
        partitions.Clear();
        sortedPartitions = null;
        rowCount = 0;
    }

    // ── The synchronous view of the in-memory stores and their tests ────────

    /// <summary>How many rows the table holds, continuation rows included.</summary>
    internal int RowCount
    {
        get
        {
            lock (gate)
            {
                return rowCount;
            }
        }
    }

    /// <summary>How many rows <paramref name="counted"/> holds for.</summary>
    internal int CountRows(Func<StoredRow, bool> counted)
    {
        lock (gate)
        {
            return partitions.Values.Sum(p => p.Rows.Values.Count(counted));
        }
    }

    /// <summary>Removes every row; the table stays.</summary>
    internal void Clear()
    {
        lock (gate)
        {
            RemoveAll();
        }
    }

    /// <summary>The row keys stored in the partition, in order.</summary>
    internal IReadOnlyList<string> RowKeys(string partitionKey)
    {
        lock (gate)
        {
            return partitions.TryGetValue(partitionKey, out var partition) ? [.. partition.SortedKeys()] : [];
        }
    }

    /// <summary>The stored row at the keys; null when absent.</summary>
    internal StoredRow? Row(string partitionKey, string rowKey)
    {
        lock (gate)
        {
            return partitions.TryGetValue(partitionKey, out var partition) && partition.Rows.TryGetValue(rowKey, out var row) ? row : null;
        }
    }

    /// <summary>Stores a row as is, as its next version (the table is created when it is not there).</summary>
    internal void Seed(TableEntity row)
    {
        lock (gate)
        {
            exists = true;
            Put(row, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Removes a row as is; false when there was none.</summary>
    internal bool Remove(string partitionKey, string rowKey)
    {
        lock (gate)
        {
            return RemoveRow(partitionKey, rowKey);
        }
    }
}
