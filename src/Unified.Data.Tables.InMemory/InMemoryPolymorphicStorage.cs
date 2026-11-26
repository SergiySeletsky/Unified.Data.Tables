using System.Diagnostics.CodeAnalysis;
using Azure.Data.Tables;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.InMemory;

/// <summary>
/// Faithful in-memory <see cref="IPolymorphicStorage{TBase}"/> for tests, dev mode and offline
/// runtime. Rows are stored as serialized <see cref="TableEntity"/>s and round-trip through the REAL
/// <see cref="TableEntitySerializer"/> on every read and write, so code under test exercises
/// production serialization rather than object identity.
/// </summary>
/// <remarks>
/// Semantics mirror <c>PolymorphicTableStorage&lt;TBase&gt;</c>: keys used verbatim and
/// case-sensitively, 409 on duplicate insert AND 400 <c>InvalidDuplicateRow</c> on a key repeated
/// within one batch both surfaced as <see cref="DuplicateKeyException"/>,
/// <see cref="EntityNotFoundException"/> on merging a missing row, idempotent delete, lexical (PartitionKey, RowKey) ordering, a null
/// <see cref="PolymorphicEntry{TBase}.Timestamp"/> on the write paths, cancellation observed on every
/// operation, and byte-identical validation and duplicate-key messages (which is why the store needs
/// a table name). It is the engine every local provider runs (Unified.Data.Tables.Core) over rows held
/// in memory, with the side interfaces <c>PolymorphicTableStorage&lt;TBase&gt;</c> has (see
/// <see cref="TryGetCapability{TCap}"/>) and the service's limits (100 rows and the payload budget a
/// transaction, 64&#160;KiB a cell, 1&#160;MB a row).
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
public sealed class InMemoryPolymorphicStorage<TBase> : IPolymorphicStorage<TBase>, IAtomicInsert<TBase>, IStorageInfo, IStorageCapabilities
    where TBase : class
{
    private readonly InMemoryTable table;
    private readonly RowStorePolymorphicStorage<TBase> engine;

    /// <summary>Creates a store with the default options and a <typeparamref name="TBase"/>-derived name.</summary>
    public InMemoryPolymorphicStorage()
        : this(typeof(TBase).Name, null)
    {
    }

    /// <summary>Creates a store with explicit options and a <typeparamref name="TBase"/>-derived name.</summary>
    /// <param name="options">Options; null selects the defaults.</param>
    public InMemoryPolymorphicStorage(StorageOptions? options)
        : this(typeof(TBase).Name, options)
    {
    }

    /// <summary>Creates a store over one named table.</summary>
    /// <remarks>
    /// The name is not used to route anything (this store holds one table of its own) but it IS what
    /// <see cref="DuplicateKeyException.EntityType"/> and the duplicate-key message report, and
    /// <c>PolymorphicTableStorage&lt;TBase&gt;</c> reports its table name there. Without it the two
    /// could never agree: a polymorphic table name is always explicit and
    /// <typeparamref name="TBase"/> is always a base type, so <c>typeof(TBase).Name</c> is guaranteed
    /// to be the wrong string. The two nameless overloads fall back to it for tests that never
    /// compare the message.
    /// </remarks>
    /// <param name="tableName">The table this store stands in for; also what its errors name.</param>
    /// <param name="options">Options; null selects the defaults.</param>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is null, empty or whitespace.</exception>
    public InMemoryPolymorphicStorage(string tableName, StorageOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var account = new InMemoryAccount();
        engine = new RowStorePolymorphicStorage<TBase>(account, tableName, options);
        table = account.Table(tableName);
        table.Create();
    }

    /// <summary>How many rows the store holds. For assertions.</summary>
    public int Count => table.RowCount;

    /// <summary>Removes every row. For test isolation.</summary>
    public void Clear() => table.Clear();

    /// <inheritdoc />
    public Task EnsureCreatedAsync(CancellationToken ct = default) => engine.EnsureCreatedAsync(ct);

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>> InsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default) =>
        engine.InsertAsync(write, ct);

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>> UpsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default) =>
        engine.UpsertAsync(write, ct);

    /// <inheritdoc />
    public Task<int> InsertBatchAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default) =>
        engine.InsertBatchAsync(writes, ct);

    /// <inheritdoc />
    public Task MergeColumnsAsync(TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct = default) =>
        engine.MergeColumnsAsync(key, columns, ct);

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default) => engine.GetAsync(key, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(string? partition = null, int? take = null, CancellationToken ct = default) =>
        engine.QueryAsync(partition, take, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(string? partition = null, int? take = null, CancellationToken ct = default) =>
        engine.QueryStreamAsync(partition, take, ct);

    /// <inheritdoc />
    public Task DeleteAsync(TableKey key, CancellationToken ct = default) => engine.DeleteAsync(key, ct);

    /// <inheritdoc />
    public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default) => engine.DeletePartitionAsync(partition, ct);

    /// <inheritdoc />
    public Task<int> CountAsync(string? partition = null, CancellationToken ct = default) => engine.CountAsync(partition, ct);

    // ── Side interfaces (0.10.0) ────────────────────────────────────────────

    /// <summary>
    /// The store's implementation of the side interface <typeparamref name="TCap"/>: the store itself for
    /// <see cref="IAtomicInsert{TBase}"/> and <see cref="IStorageInfo"/>; the untyped view of its table
    /// for the experimental <see cref="IRawTable"/>; the tables of its in-memory account for the
    /// experimental <see cref="ITableStore"/>.
    /// </summary>
    /// <typeparam name="TCap">The side interface asked for.</typeparam>
    /// <param name="capability">The implementation to call, or null when the store does not support it.</param>
    /// <returns>True when the store supports <typeparamref name="TCap"/>.</returns>
    public bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
    {
        capability = this as TCap;
        return capability is not null || engine.TryGetCapability(out capability);
    }

    /// <summary>
    /// What the store states about itself: the provider (<c>"InMemory"</c>), its table, the limits it
    /// plans and refuses by (the service's), no read cache, and <see cref="Durability.Volatile"/>. Calls
    /// nothing and never throws.
    /// </summary>
    /// <returns>The store's info; the same value on every call.</returns>
    public StorageInfo GetInfo() => engine.GetInfo();

    /// <inheritdoc />
    /// <remarks>
    /// One atomic step, planned as <c>PolymorphicTableStorage&lt;TBase&gt;</c> plans its one transaction:
    /// one partition, at most 100 rows, each key once, and the payload budget of <see cref="BatchPlanner"/>.
    /// A batch that does not fit is refused with <see cref="BatchNotAtomicException"/>; it is never split.
    /// </remarks>
    public Task InsertAtomicAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default) =>
        engine.InsertAtomicAsync(writes, ct);
}
