using System.Globalization;
using Azure;
using Azure.Data.Tables;

namespace Unified.Data.Tables.InMemory;

/// <summary>
/// Faithful in-memory <see cref="IPolymorphicStorage{TBase}"/> for tests, dev mode and offline
/// runtime. Rows are stored as serialized <see cref="TableEntity"/>s and round-trip through the REAL
/// <see cref="TableEntitySerializer"/> on every read and write, so code under test exercises
/// production serialization rather than object identity.
/// </summary>
/// <remarks>
/// Semantics mirror <see cref="PolymorphicTableStorage{TBase}"/>: keys used verbatim and
/// case-sensitively, 409 on duplicate insert surfaced as <see cref="DuplicateKeyException"/>, 404 on
/// merging a missing row, idempotent delete, lexical (PartitionKey, RowKey) ordering, and
/// byte-identical validation messages via <c>PolymorphicMessages</c>.
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
public sealed class InMemoryPolymorphicStorage<TBase> : IPolymorphicStorage<TBase>
    where TBase : class
{
    private static readonly string[] ReservedCells = ["PartitionKey", "RowKey", "Timestamp", "odata.etag"];

    private readonly Dictionary<(string PartitionKey, string RowKey), StoredRow> rows = [];
    private readonly object gate = new();
    private readonly ITypeDiscriminator discriminator;
    private long versionCounter;

    /// <summary>Creates a store with the default options.</summary>
    public InMemoryPolymorphicStorage()
        : this(null)
    {
    }

    /// <summary>Creates a store with explicit options.</summary>
    /// <param name="options">Options; null selects the defaults.</param>
    public InMemoryPolymorphicStorage(UnifiedTableStorageOptions? options) =>
        // UnifiedTableStorageOptions.ResolveTypeDiscriminator() is internal to Unified.Data.Tables,
        // which does not (and per the design must not) grant this project InternalsVisibleTo — so
        // the null-coalescing default is duplicated here against the public TypeDiscriminator
        // property instead of calling the internal helper.
        discriminator = options?.TypeDiscriminator ?? AssemblyQualifiedTypeDiscriminator.Instance;

    /// <summary>How many rows the store holds. For assertions.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return rows.Count;
            }
        }
    }

    /// <summary>Removes every row. For test isolation.</summary>
    public void Clear()
    {
        lock (gate)
        {
            rows.Clear();
        }
    }

    /// <inheritdoc />
    public Task EnsureCreatedAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>> InsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        Guard.NotNull(write, nameof(write));
        var row = ToRow(write);

        lock (gate)
        {
            var key = (write.Key.PartitionKey, write.Key.RowKey);
            if (rows.ContainsKey(key))
            {
                throw new DuplicateKeyException(
                    typeof(TBase).Name,
                    write.Key.ToId(),
                    new RequestFailedException(409, "The specified entity already exists."));
            }

            rows[key] = Store(row);
            return Task.FromResult(ToEntry(rows[key]));
        }
    }

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>> UpsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        Guard.NotNull(write, nameof(write));
        var row = ToRow(write);

        lock (gate)
        {
            var key = (write.Key.PartitionKey, write.Key.RowKey);
            rows[key] = Store(row);
            return Task.FromResult(ToEntry(rows[key]));
        }
    }

    /// <inheritdoc />
    public Task<int> InsertBatchAsync(
        IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default)
    {
        Guard.NotNull(writes, nameof(writes));
        if (writes.Count == 0)
            return Task.FromResult(0);

        // Build every row before taking the lock, so validation failures cost nothing.
        var built = writes.Select(w => (w.Key, Row: ToRow(w))).ToList();

        lock (gate)
        {
            foreach (var (key, _) in built)
            {
                if (rows.ContainsKey((key.PartitionKey, key.RowKey)))
                {
                    throw new DuplicateKeyException(
                        typeof(TBase).Name,
                        key.ToId(),
                        new RequestFailedException(409, "The specified entity already exists."));
                }
            }

            foreach (var (key, row) in built)
                rows[(key.PartitionKey, key.RowKey)] = Store(row);
        }

        return Task.FromResult(built.Count);
    }

    /// <inheritdoc />
    public Task MergeColumnsAsync(
        TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct = default)
    {
        ValidateKey(key);
        Guard.NotNull(columns, nameof(columns));
        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "A merge with no columns would be a network round trip that changes nothing.",
                nameof(columns));
        }

        foreach (var column in columns)
            ValidateSystemColumn(column.Key);

        lock (gate)
        {
            if (!rows.TryGetValue((key.PartitionKey, key.RowKey), out var existing))
                throw new RequestFailedException(404, "The specified resource does not exist.");

            // Server-side Merge overlays the supplied cells onto a copy and leaves the rest alone.
            var merged = CopyOf(existing.Data);
            foreach (var column in columns)
                merged[column.Key] = column.Value;

            rows[(key.PartitionKey, key.RowKey)] = Store(merged);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default)
    {
        ValidateKey(key);

        lock (gate)
        {
            return Task.FromResult(
                rows.TryGetValue((key.PartitionKey, key.RowKey), out var stored) ? ToEntry(stored) : null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(
        string? partition = null, int? take = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PolymorphicEntry<TBase>>>(Snapshot(partition, take));

    /// <inheritdoc />
    public async IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(
        string? partition = null,
        int? take = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in Snapshot(partition, take))
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(TableKey key, CancellationToken ct = default)
    {
        ValidateKey(key);

        lock (gate)
        {
            rows.Remove((key.PartitionKey, key.RowKey));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default)
    {
        Guard.NotNull(partition, nameof(partition));

        lock (gate)
        {
            var doomed = rows.Keys
                .Where(k => string.Equals(k.PartitionKey, partition, StringComparison.Ordinal))
                .ToList();

            foreach (var key in doomed)
                rows.Remove(key);

            return Task.FromResult(doomed.Count);
        }
    }

    /// <inheritdoc />
    public Task<int> CountAsync(string? partition = null, CancellationToken ct = default)
    {
        lock (gate)
        {
            return Task.FromResult(partition is null
                ? rows.Count
                : rows.Keys.Count(k => string.Equals(k.PartitionKey, partition, StringComparison.Ordinal)));
        }
    }

    private List<PolymorphicEntry<TBase>> Snapshot(string? partition, int? take)
    {
        if (take is <= 0)
            return [];

        lock (gate)
        {
            var query = rows
                .Where(kv => partition is null
                             || string.Equals(kv.Key.PartitionKey, partition, StringComparison.Ordinal))
                .OrderBy(kv => kv.Key.PartitionKey, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.RowKey, StringComparer.Ordinal)
                .Select(kv => ToEntry(kv.Value));

            if (take is { } limit)
                query = query.Take(limit);

            return query.ToList();
        }
    }

    private StoredRow Store(TableEntity row) =>
        new(CopyOf(row), ++versionCounter, DateTimeOffset.UtcNow);

    private TableEntity ToRow(PolymorphicWrite<TBase> write)
    {
        ValidateKey(write.Key);

        var row = write.Item is null
            ? new TableEntity(write.Key.PartitionKey, write.Key.RowKey)
            : write.Item.ToTableEntity(write.Key.PartitionKey, write.Key.RowKey);

        if (write.Item is not null)
            row[SystemColumnNames.TypeName] = discriminator.ToDiscriminator(write.Item.GetType());

        if (write.SystemColumns is null)
            return row;

        foreach (var column in write.SystemColumns)
        {
            ValidateSystemColumn(column.Key);
            row[column.Key] = column.Value;
        }

        return row;
    }

    private PolymorphicEntry<TBase> ToEntry(StoredRow stored)
    {
        var row = CopyOf(stored.Data);
        row.Timestamp = stored.Timestamp;

        string? storedDiscriminator = null;
        if (row.TryGetValue(SystemColumnNames.TypeName, out var raw)
            && raw is string token
            && token.Length > 0)
        {
            storedDiscriminator = token;
        }

        var item = row.TryFromTableEntity<TBase>(discriminator, out var materialized) ? materialized : null;

        var columns = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var cell in row)
        {
            if (Array.IndexOf(ReservedCells, cell.Key) >= 0)
                continue;

            columns[cell.Key] = cell.Value;
        }

        return new PolymorphicEntry<TBase>(
            new TableKey(row.PartitionKey, row.RowKey),
            item,
            storedDiscriminator,
            stored.ETagString(),
            stored.Timestamp,
            columns);
    }

    // Isolation: a caller mutating a returned row must not reach into the store, and a stored row
    // must not change under a caller who is still holding the object they wrote.
    private static TableEntity CopyOf(TableEntity source)
    {
        var copy = new TableEntity(source.PartitionKey, source.RowKey);
        foreach (var cell in source)
        {
            if (Array.IndexOf(ReservedCells, cell.Key) >= 0)
                continue;

            copy[cell.Key] = cell.Value;
        }

        return copy;
    }

    // Duplicated verbatim from PolymorphicTableStorage<TBase>.ValidateKey — this project cannot see
    // the Azure package's internals, and the repo's parity doctrine (ConcurrencyMessages,
    // PolymorphicMessages) requires the fake and the real store to throw byte-identical messages, so
    // the two copies must be kept in lockstep by hand rather than shared.
    private static void ValidateKey(TableKey key)
    {
        if (string.IsNullOrEmpty(key.PartitionKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.PartitionKey)), nameof(key));
        if (string.IsNullOrEmpty(key.RowKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.RowKey)), nameof(key));
    }

    // Duplicated verbatim from PolymorphicTableStorage<TBase>.ValidateSystemColumn — see the note on
    // ValidateKey above; the paramName is deliberately nameof(columnName) in both copies so a caller
    // comparing ex.ParamName (not just ex.Message) still sees the fake and the real store agree.
    private static void ValidateSystemColumn(string columnName)
    {
        if (!SystemColumnNames.IsSystemColumn(columnName))
            throw new ArgumentException(PolymorphicMessages.NotSystemColumn(columnName), nameof(columnName));

        if (string.Equals(columnName, SystemColumnNames.TypeName, StringComparison.Ordinal))
            throw new ArgumentException(PolymorphicMessages.TypeNameNotMergeable(), nameof(columnName));
    }

    private sealed record StoredRow(TableEntity Data, long Version, DateTimeOffset Timestamp)
    {
        public string ETagString() => $"W/\"{Version.ToString(CultureInfo.InvariantCulture)}\"";
    }
}
