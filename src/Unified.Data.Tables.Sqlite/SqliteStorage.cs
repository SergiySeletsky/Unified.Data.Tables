using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Sqlite;

/// <summary>
/// <see cref="IStorage{T}"/> over one table of a SQLite database (<see cref="SqliteStorageAccount"/>):
/// for development, single-machine deployments and durable tests. It is the engine every local provider
/// runs (Unified.Data.Tables.Core) over rows kept in SQLite, so it stores and answers exactly as
/// <c>InMemoryStorage&lt;T&gt;</c> does, and rows round-trip through the real
/// <see cref="TableEntitySerializer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Semantics mirror <c>TableStorage&lt;T&gt;</c>: identical id normalization and first-<c>'|'</c> key
/// splitting, <see cref="DuplicateKeyException"/> on duplicate create,
/// <see cref="EntityNotFoundException"/> on updating a missing row,
/// <see cref="ConcurrencyConflictException"/> on a lost version check (also when the other writer is
/// another process), idempotent delete, ordinal (PartitionKey, RowKey) result ordering, and the service's
/// limits and rules (100 rows and the payload budget a transaction, one partition a transaction,
/// 64&#160;KiB a cell, 1&#160;MB a row, the characters a key may hold, the form of a table name), so a
/// table that works here works on Azure. Large values use the same split layout; with
/// <see cref="StorageOptions.Offload"/> set, offloaded values go to the <c>__blobs</c> table of the
/// database.
/// </para>
/// <para>
/// <b>Read cache.</b> As on the Azure provider, and with the same default
/// (<see cref="StorageOptions.Cache"/>: a sliding hour): a point read and a whole-partition or
/// whole-table list are answered from memory until a write through this store evicts them. A row that
/// another store instance, another process or the raw table changed is served stale until then
/// (<see cref="StorageInfo.ReadCacheMayServeStaleRows"/> is true); a conditional write on a stale row
/// fails with <see cref="ConcurrencyConflictException"/>, never overwrites. Use
/// <see cref="CachePolicy.Disabled"/> where several processes write the same rows and reads must be
/// current.
/// </para>
/// <para>
/// <b>Failures.</b> Only the provider-neutral <see cref="StorageException"/> types: a database that
/// stays locked by another writer is <see cref="TransientStorageException"/>
/// (<see cref="TransientStorageException.MayHaveCommitted"/> false), any other SQLite failure a
/// <see cref="StorageException"/> whose <see cref="StorageException.ProviderErrorCode"/> is the SQLite
/// result code (<c>SQLITE_FULL</c>, <c>SQLITE_IOERR</c>, …) and whose inner exception is SQLite's.
/// </para>
/// </remarks>
/// <typeparam name="T">The entity type; implements <see cref="IEntity"/> (derive from <see cref="Entity"/> for the convenience base) and has a public parameterless constructor.</typeparam>
public sealed class SqliteStorage<T> : IStorage<T>, IOrphanBlobSweeper, IBlobOffloadState,
    IStorageCapabilities, IConditionalDelete<T>, IAtomicBatch<T>, IStorageInfo
    where T : class, IEntity, new()
{
    private readonly SqliteStorageAccount account;
    private readonly RowStoreStorage<T> engine;

    /// <summary>
    /// Creates the store of <typeparamref name="T"/> over one table of <paramref name="account"/> (named
    /// by <see cref="StorageOptions.TableNameResolver"/>, or after the type). Touches nothing: the table
    /// is created on first use. The trailing defaults let the DI container select this constructor when
    /// neither optional service is registered.
    /// </summary>
    /// <param name="account">The database that holds the table.</param>
    /// <param name="authorizer">Gates changes to <see cref="ProtectedPropertyAttribute"/>-decorated properties, mirroring <c>TableStorage&lt;T&gt;</c>; null allows none.</param>
    /// <param name="options">
    /// The options this store reads (id normalization, the read cache, blob offload, the clock, the
    /// table name); null selects the options the account was opened with. Where and how the database is
    /// opened is the account's alone.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="account"/> is null.</exception>
    public SqliteStorage(SqliteStorageAccount account, IProtectedPropertyAuthorizer? authorizer = null, StorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        this.account = account;
        var resolved = options ?? account.Options;
        // Offloaded values go to the database's __blobs table, kept apart by the configured container
        // name; the read cache is the one the options ask for, by default the Azure provider's.
        var blobs = resolved.Offload is { } offload ? new SqliteOffloadBlobStore(account.Database, offload.ContainerName) : null;
        engine = new RowStoreStorage<T>(account.Database, resolved, authorizer, blobs);
    }

    // ── IStorage<T> ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<T> CreateAsync(T entity, CancellationToken ct = default) => engine.CreateAsync(entity, ct);

    /// <inheritdoc />
    public Task<T> UpsertAsync(T entity, CancellationToken ct = default) => engine.UpsertAsync(entity, ct);

    /// <inheritdoc />
    public Task<T> UpdateAsync(T entity, CancellationToken ct = default) => engine.UpdateAsync(entity, ct);

    /// <inheritdoc />
    public Task<T> UpdateAsync(T entity, ConcurrencyMode mode, CancellationToken ct = default) => engine.UpdateAsync(entity, mode, ct);

    /// <inheritdoc />
    public Task<string> UpdateAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct = default) =>
        engine.UpdateAsync(id, builderAction, ct);

    /// <inheritdoc />
    public Task<T?> OneAsync(string id, CancellationToken ct = default) => engine.OneAsync(id, ct);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string id, CancellationToken ct = default) => engine.ExistsAsync(id, ct);

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default) => engine.QueryAsync(partition, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The partition, <see cref="QueryOptions.RowKeyPrefix"/> and the range of
    /// <see cref="QueryOptions.FromRowKey"/> and <see cref="QueryOptions.ToRowKey"/> are answered by the
    /// table's primary key, compared ordinally after the store's id normalization exactly as
    /// <c>TableStorage&lt;T&gt;</c> answers them; so do
    /// <see cref="QueryStreamAsync(QueryOptions?, CancellationToken)"/> and <see cref="QueryPageAsync"/>.
    /// </remarks>
    public Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default) => engine.QueryAsync(options, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The partition is answered by the table's primary key; the predicate is validated as
    /// <c>TableStorage&lt;T&gt;</c> validates it and then run, compiled, over the rows read.
    /// </remarks>
    public Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) =>
        engine.QueryAsync(predicate, partition, take, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null, CancellationToken ct = default) =>
        engine.QueryStreamAsync(options, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<T> QueryStreamAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null,
        CancellationToken ct = default) => engine.QueryStreamAsync(predicate, partition, take, ct);

    /// <inheritdoc />
    /// <remarks>
    /// A continuation token names the last row of its page, so it stays valid across restarts of the
    /// process and for as long as the database exists; it is specific to this provider.
    /// </remarks>
    public Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default) => engine.QueryPageAsync(options, ct);

    /// <inheritdoc />
    public Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, string? partition = null, CancellationToken ct = default) =>
        engine.AnyAsync(predicate, partition, ct);

    /// <inheritdoc />
    public Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => engine.CreateBatchAsync(entities, ct);

    /// <inheritdoc />
    public Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => engine.UpsertBatchAsync(entities, ct);

    /// <inheritdoc />
    public Task<int> CountAsync(string? partition = null, CancellationToken ct = default) => engine.CountAsync(partition, ct);

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken ct = default) => engine.DeleteAsync(id, ct);

    /// <inheritdoc />
    public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default) => engine.DeletePartitionAsync(partition, ct);

    // ── Orphan sweep ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<int> SweepOrphanBlobsAsync(string partitionKey, TimeSpan olderThan, CancellationToken ct = default) =>
        engine.SweepOrphanBlobsAsync(partitionKey, olderThan, ct);

    /// <inheritdoc />
    public Task<int> SweepOrphanBlobsAsync(TimeSpan olderThan, CancellationToken ct = default) => engine.SweepOrphanBlobsAsync(olderThan, ct);

    bool IBlobOffloadState.HasBlobOffload => ((IBlobOffloadState)engine).HasBlobOffload;

    // ── Side interfaces (0.10.0) ────────────────────────────────────────────

    /// <summary>
    /// The store's implementation of the side interface <typeparamref name="TCap"/>: the store itself for
    /// <see cref="IConditionalDelete{T}"/>, <see cref="IAtomicBatch{T}"/>, <see cref="IStorageInfo"/> and
    /// <see cref="IOrphanBlobSweeper"/>; the untyped view of its own table for the experimental
    /// <see cref="IRawTable"/> (it bypasses the read cache); every table of the database
    /// (<see cref="SqliteStorageAccount.Tables"/>) for the experimental <see cref="ITableStore"/>. Every
    /// capability the Azure provider has is supported.
    /// </summary>
    /// <typeparam name="TCap">The side interface asked for.</typeparam>
    /// <param name="capability">The implementation to call, or null when the store does not support it.</param>
    /// <returns>True when the store supports <typeparamref name="TCap"/>.</returns>
    public bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
    {
        capability = this as TCap;
#pragma warning disable UDT0001 // the experimental raw-table API is handed out here, not used
        // One table store per account, not per store: the one DI registers.
        if (capability is null && typeof(TCap) == typeof(ITableStore))
            capability = account.Tables as TCap;
#pragma warning restore UDT0001
        return capability is not null || engine.TryGetCapability(out capability);
    }

    /// <summary>
    /// What the store states about itself: the provider (<c>"Sqlite"</c>), its table, the limits it plans
    /// and refuses by (the service's: 100 rows and 3,000,000 bytes a transaction, 64 KiB a cell, 1 MB a
    /// row), whether its read cache may serve a stale row (true unless the cache is disabled for
    /// <typeparamref name="T"/>), and the durability of the database
    /// (<see cref="SqliteStorageAccount.Durability"/>). Calls nothing and never throws.
    /// </summary>
    /// <returns>The store's info; the same value on every call.</returns>
    public StorageInfo GetInfo() => engine.GetInfo();

    /// <inheritdoc />
    /// <remarks>
    /// One transaction of the database. This store never sends a request twice, so the answer is never
    /// <see cref="DeleteStatus.Indeterminate"/>.
    /// </remarks>
    public Task<DeleteStatus> DeleteIfExistsAsync(string id, CancellationToken ct = default) => engine.DeleteIfExistsAsync(id, ct);

    /// <inheritdoc />
    /// <remarks>
    /// One transaction of the database, never repeated on another version: a stored version other than
    /// <paramref name="etag"/> is the <see cref="ConcurrencyConflictException"/>, whichever process wrote
    /// it. Never <see cref="DeleteStatus.Indeterminate"/>.
    /// </remarks>
    public Task<DeleteStatus> DeleteIfMatchAsync(string id, string etag, CancellationToken ct = default) =>
        engine.DeleteIfMatchAsync(id, etag, ct);

    /// <inheritdoc />
    /// <remarks>
    /// One transaction of the database, planned as <c>TableStorage&lt;T&gt;</c> plans its one
    /// entity-group transaction: one partition, at most 100 rows, each row once, the payload budget of
    /// <see cref="BatchPlanner"/>; for a type with large values the continuation rows count, and a value
    /// that would be offloaded is refused. A batch refused before its commit leaves the caller's entities
    /// as they were.
    /// </remarks>
    public Task CreateAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => engine.CreateAtomicAsync(entities, ct);

    /// <inheritdoc />
    /// <remarks>One transaction of the database, planned as <see cref="CreateAtomicAsync"/> is.</remarks>
    public Task UpsertAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => engine.UpsertAtomicAsync(entities, ct);
}
