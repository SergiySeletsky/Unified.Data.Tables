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
/// case-sensitively, 409 on duplicate insert AND 400 <c>InvalidDuplicateRow</c> on a key repeated
/// within one batch both surfaced as <see cref="DuplicateKeyException"/>, 404 on merging a missing
/// row, idempotent delete, lexical (PartitionKey, RowKey) ordering, a null
/// <see cref="PolymorphicEntry{TBase}.Timestamp"/> on the write paths, cancellation observed on every
/// operation, and byte-identical validation and duplicate-key messages (which is why the store needs
/// a table name).
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
public sealed class InMemoryPolymorphicStorage<TBase> : IPolymorphicStorage<TBase>
    where TBase : class
{
    private static readonly string[] ReservedCells = ["PartitionKey", "RowKey", "Timestamp", "odata.etag"];

    private readonly Dictionary<(string PartitionKey, string RowKey), StoredRow> rows = [];
    private readonly object gate = new();
    private readonly ITypeDiscriminator discriminator;
    private readonly string tableName;
    private long versionCounter;

    /// <summary>Creates a store with the default options and a <typeparamref name="TBase"/>-derived name.</summary>
    public InMemoryPolymorphicStorage()
        : this(typeof(TBase).Name, null)
    {
    }

    /// <summary>Creates a store with explicit options and a <typeparamref name="TBase"/>-derived name.</summary>
    /// <param name="options">Options; null selects the defaults.</param>
    public InMemoryPolymorphicStorage(UnifiedTableStorageOptions? options)
        : this(typeof(TBase).Name, options)
    {
    }

    /// <summary>Creates a store over one named table.</summary>
    /// <remarks>
    /// The name is not used to route anything — this store holds one dictionary — but it IS what
    /// <see cref="DuplicateKeyException.EntityType"/> and the duplicate-key message report, and
    /// <see cref="PolymorphicTableStorage{TBase}"/> reports its table name there. Without it the two
    /// could never agree: a polymorphic table name is always explicit and
    /// <typeparamref name="TBase"/> is always a base type, so <c>typeof(TBase).Name</c> is guaranteed
    /// to be the wrong string. The two nameless overloads fall back to it for tests that never
    /// compare the message.
    /// </remarks>
    /// <param name="tableName">The table this store stands in for; also what its errors name.</param>
    /// <param name="options">Options; null selects the defaults.</param>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is null, empty or whitespace.</exception>
    public InMemoryPolymorphicStorage(string tableName, UnifiedTableStorageOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        this.tableName = tableName;

        // UnifiedTableStorageOptions.ResolveTypeDiscriminator() is internal to Unified.Data.Tables,
        // which does not (and per the design must not) grant this project InternalsVisibleTo — so
        // the null-coalescing default is duplicated here against the public TypeDiscriminator
        // property instead of calling the internal helper.
        discriminator = options?.TypeDiscriminator ?? AssemblyQualifiedTypeDiscriminator.Instance;
    }

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

        // The Azure store observes the token on its lazy table-create, AFTER argument validation and
        // row building — so a buffered fake that never looked at it would let a cancelled operation
        // succeed here and fail in production.
        ct.ThrowIfCancellationRequested();

        lock (gate)
        {
            var key = (write.Key.PartitionKey, write.Key.RowKey);
            if (rows.ContainsKey(key))
                throw Duplicate(write.Key);

            rows[key] = Store(row);
            return Task.FromResult(ToEntry(rows[key], timestampKnown: false));
        }
    }

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>> UpsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        Guard.NotNull(write, nameof(write));
        var row = ToRow(write);
        ct.ThrowIfCancellationRequested();

        lock (gate)
        {
            var key = (write.Key.PartitionKey, write.Key.RowKey);
            rows[key] = Store(row);
            return Task.FromResult(ToEntry(rows[key], timestampKnown: false));
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
        var built = new List<(TableKey Key, TableEntity Row)>(writes.Count);
        var withinBatch = new HashSet<(string, string)>();
        foreach (var write in writes)
        {
            var row = ToRow(write);

            // The same key TWICE in one batch. Azure rejects the whole transaction with 400
            // InvalidDuplicateRow; without this check the dictionary would quietly collapse the pair
            // into one row and report the full count, so the fake would return 2 for a batch Azure
            // stores none of. Checked before the existing-row scan because it needs no lock.
            if (!withinBatch.Add((write.Key.PartitionKey, write.Key.RowKey)))
            {
                throw new DuplicateKeyException(
                    tableName,
                    write.Key.ToId(),
                    new RequestFailedException(
                        400,
                        $"InvalidDuplicateRow: duplicate key within the batch. Id: {write.Key.ToId()}"));
            }

            built.Add((write.Key, row));
        }

        ct.ThrowIfCancellationRequested();

        lock (gate)
        {
            foreach (var (key, _) in built)
            {
                if (rows.ContainsKey((key.PartitionKey, key.RowKey)))
                    throw Duplicate(key);
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

        ct.ThrowIfCancellationRequested();

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
        ct.ThrowIfCancellationRequested();

        lock (gate)
        {
            return Task.FromResult(
                rows.TryGetValue((key.PartitionKey, key.RowKey), out var stored) ? ToEntry(stored) : null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(
        string? partition = null, int? take = null, CancellationToken ct = default)
    {
        // take <= 0 short-circuits BEFORE the token is looked at, because PolymorphicTableStorage's
        // QueryStreamAsync yields break before its first (cancellable) service call on that path.
        if (take is <= 0)
            return Task.FromResult<IReadOnlyList<PolymorphicEntry<TBase>>>([]);

        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PolymorphicEntry<TBase>>>(Snapshot(partition, take));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(
        string? partition = null,
        int? take = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (take is <= 0)
            yield break;

        // Azure observes the token on its lazy table-create, before the first row. The per-item check
        // below never runs for an empty result, so without this an empty read ignores cancellation.
        ct.ThrowIfCancellationRequested();

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
        ct.ThrowIfCancellationRequested();

        lock (gate)
        {
            rows.Remove((key.PartitionKey, key.RowKey));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default)
    {
        // Not Guard.NotNull: PolymorphicTableStorage uses ArgumentException.ThrowIfNullOrEmpty, so an
        // EMPTY partition threw on Azure and silently deleted nothing here. Same API, same message.
        ArgumentException.ThrowIfNullOrEmpty(partition);
        ct.ThrowIfCancellationRequested();

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
        ct.ThrowIfCancellationRequested();

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

    // Duplicate-key errors name the TABLE, exactly as PolymorphicTableStorage does — the fake used
    // typeof(TBase).Name, which can never coincide with a real polymorphic table name, so every
    // DuplicateKeyException.Message and .EntityType diverged between the two implementations.
    private DuplicateKeyException Duplicate(TableKey key) =>
        new(tableName,
            key.ToId(),
            new RequestFailedException(409, "The specified entity already exists."));

    // timestampKnown: false on the WRITE paths. A write response carries no service timestamp, so
    // PolymorphicTableStorage returns a null one; a fake that filled it in would green-light a test
    // asserting on a value Azure never sends.
    private PolymorphicEntry<TBase> ToEntry(StoredRow stored, bool timestampKnown = true)
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
            timestampKnown ? stored.Timestamp : null,
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
