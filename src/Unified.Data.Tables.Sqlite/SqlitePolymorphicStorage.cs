using System.Diagnostics.CodeAnalysis;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Sqlite;

/// <summary>
/// <see cref="IPolymorphicStorage{TBase}"/> over one named table of a SQLite database
/// (<see cref="SqliteStorageAccount"/>). It is the engine every local provider runs
/// (Unified.Data.Tables.Core) over rows kept in SQLite, so it stores and answers exactly as
/// <c>InMemoryPolymorphicStorage&lt;TBase&gt;</c> does, and rows round-trip through the real
/// <see cref="TableEntitySerializer"/>.
/// </summary>
/// <remarks>
/// Semantics mirror <c>PolymorphicTableStorage&lt;TBase&gt;</c>: keys used verbatim and
/// case-sensitively, a duplicate insert and a key repeated within one batch both surfaced as
/// <see cref="DuplicateKeyException"/>, <see cref="EntityNotFoundException"/> on merging a missing row,
/// idempotent delete, ordinal (PartitionKey, RowKey) ordering, a null
/// <see cref="PolymorphicEntry{TBase}.Timestamp"/> on the write paths, cancellation observed on every
/// operation, the same validation and duplicate-key messages (which name the table), and the service's
/// limits (100 rows and the payload budget a transaction, 64&#160;KiB a cell, 1&#160;MB a row). It has no
/// read cache. A SQLite failure leaves as a provider-neutral <see cref="StorageException"/>, as on
/// <see cref="SqliteStorage{T}"/>.
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
public sealed class SqlitePolymorphicStorage<TBase> : IPolymorphicStorage<TBase>, IAtomicInsert<TBase>, IStorageInfo, IStorageCapabilities
    where TBase : class
{
    private readonly SqliteStorageAccount account;
    private readonly RowStorePolymorphicStorage<TBase> engine;

    /// <summary>Creates a store over one named table of <paramref name="account"/>. Touches nothing: the table is created on first use.</summary>
    /// <param name="account">The database that holds the table.</param>
    /// <param name="tableName">
    /// The table: a name the Table service accepts (a letter, then 2 to 62 letters and digits); any other
    /// name is refused with <see cref="StorageRequestRejectedException"/> when the table is first used.
    /// Also what the store's errors name.
    /// </param>
    /// <param name="options">Options (the type discriminator); null selects the options the account was opened with.</param>
    /// <exception cref="ArgumentNullException"><paramref name="account"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is null, empty or whitespace.</exception>
    public SqlitePolymorphicStorage(SqliteStorageAccount account, string tableName, StorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        this.account = account;
        engine = new RowStorePolymorphicStorage<TBase>(account.Database, tableName, options ?? account.Options);
    }

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
    /// for the experimental <see cref="IRawTable"/>; every table of the database
    /// (<see cref="SqliteStorageAccount.Tables"/>) for the experimental <see cref="ITableStore"/>.
    /// </summary>
    /// <typeparam name="TCap">The side interface asked for.</typeparam>
    /// <param name="capability">The implementation to call, or null when the store does not support it.</param>
    /// <returns>True when the store supports <typeparamref name="TCap"/>.</returns>
    public bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
    {
        capability = this as TCap;
#pragma warning disable UDT0001 // the experimental raw-table API is handed out here, not used
        if (capability is null && typeof(TCap) == typeof(ITableStore))
            capability = account.Tables as TCap;
#pragma warning restore UDT0001
        return capability is not null || engine.TryGetCapability(out capability);
    }

    /// <summary>
    /// What the store states about itself: the provider (<c>"Sqlite"</c>), its table, the limits it plans
    /// and refuses by (the service's), no read cache, and the durability of the database
    /// (<see cref="SqliteStorageAccount.Durability"/>). Calls nothing and never throws.
    /// </summary>
    /// <returns>The store's info; the same value on every call.</returns>
    public StorageInfo GetInfo() => engine.GetInfo();

    /// <inheritdoc />
    /// <remarks>
    /// One transaction of the database, planned as <c>PolymorphicTableStorage&lt;TBase&gt;</c> plans its
    /// one entity-group transaction: one partition, at most 100 rows, each key once, and the payload
    /// budget of <see cref="BatchPlanner"/>. A batch that does not fit is refused with
    /// <see cref="BatchNotAtomicException"/>; it is never split.
    /// </remarks>
    public Task InsertAtomicAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default) =>
        engine.InsertAtomicAsync(writes, ct);
}
