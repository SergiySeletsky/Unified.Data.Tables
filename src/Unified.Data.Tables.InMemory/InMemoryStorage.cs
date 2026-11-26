using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Azure.Data.Tables;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.InMemory;

/// <summary>
/// Faithful in-memory <see cref="IStorage{T}"/> for tests, dev mode, and offline runtime. Rows are
/// stored as serialized <see cref="TableEntity"/>s and round-trip through the REAL
/// <see cref="TableEntitySerializer"/> on every read and write, so code under test exercises
/// production serialization behaviour (decimal-as-double, enum-as-string, <c>Parent_Child</c>
/// flattening, <c>__Json</c>/<c>__GZip</c> cells, 64&#160;KB handling) instead of hiding it.
/// Semantics mirror <c>TableStorage&lt;T&gt;</c>: identical id normalization and first-<c>'|'</c>
/// key splitting, <see cref="DuplicateKeyException"/> on duplicate create,
/// <see cref="EntityNotFoundException"/> on updating a missing row, ETag simulation with
/// <see cref="ConcurrencyConflictException"/> on a lost version check, idempotent delete, lexical
/// (PartitionKey, RowKey) result ordering, and the service's limits (100 rows and the payload budget a
/// transaction, 64&#160;KiB a cell, 1&#160;MB a row). It is the engine every local provider runs
/// (Unified.Data.Tables.Core) over rows held in memory, with every 0.10.0 side interface
/// <c>TableStorage&lt;T&gt;</c> has (see <see cref="TryGetCapability{TCap}"/>). Like
/// <c>TableStorage&lt;T&gt;</c> it throws only the
/// provider-neutral <see cref="StorageException"/> types for what the service would refuse; it never
/// sends a request twice, so <see cref="DuplicateKeyException.MayBeOwnWrite"/> is always false here.
/// </summary>
/// <typeparam name="T">The entity type; implements <see cref="IEntity"/> (derive from <see cref="Entity"/> for the convenience base) and has a public parameterless constructor.</typeparam>
public sealed class InMemoryStorage<T> : IStorage<T>, IOrphanBlobSweeper, IBlobOffloadState,
    IStorageCapabilities, IConditionalDelete<T>, IAtomicBatch<T>, IStorageInfo
    where T : class, IEntity, new()
{
    private static readonly bool LargeValueAware = LargeValueTable.IsLargeValueAware(typeof(T));

    // The table this store keeps its rows in (in an account of its own), and the engine every local
    // provider runs over such a table.
    private readonly InMemoryTable table;
    private readonly RowStoreStorage<T> engine;

    /// <summary>Creates a store without protected-property enforcement.</summary>
    public InMemoryStorage()
        : this(null, null)
    {
    }

    /// <summary>
    /// Creates a store that gates <see cref="ProtectedPropertyAttribute"/>-decorated properties
    /// through <paramref name="authorizer"/>, mirroring <c>TableStorage&lt;T&gt;</c>.
    /// </summary>
    public InMemoryStorage(IProtectedPropertyAuthorizer? authorizer)
        : this(authorizer, null)
    {
    }

    /// <summary>
    /// Creates a store, optionally gating <see cref="ProtectedPropertyAttribute"/>-decorated
    /// properties through <paramref name="authorizer"/> and honouring
    /// <see cref="StorageOptions.IdNormalization"/>, <see cref="StorageOptions.TimeProvider"/> and the
    /// other options a store reads, mirroring <c>TableStorage&lt;T&gt;</c> so a green fake test holds
    /// against Azure. <see cref="StorageOptions.Cache"/> is not read: this store has no read cache. The
    /// trailing defaults let the DI container select this constructor even when neither optional
    /// service is registered.
    /// </summary>
    public InMemoryStorage(IProtectedPropertyAuthorizer? authorizer = null, StorageOptions? options = null)
    {
        var account = new InMemoryAccount();
        // Blob offload gets this store's own in-memory container (no blob service), named under the
        // table name TableStorage<T> would resolve, so blob names match the real store's. No read cache:
        // every row of the table is written through this store, and a read returns what is stored.
        engine = new RowStoreStorage<T>(account, options, authorizer, Blobs, CachePolicy.Disabled);
        table = account.Table(engine.TableName);
        table.Create();
    }

    /// <summary>The offloaded-value blobs of this store (for tests); empty when blob offload is not configured.</summary>
    internal InMemoryOffloadBlobStore Blobs { get; } = new();

    /// <summary>Stored rows including continuation rows (for tests); <see cref="Count"/> counts entities.</summary>
    internal int PhysicalRowCount => table.RowCount;

    /// <summary>The row keys stored in <paramref name="partitionKey"/>, continuation rows included, in order (for tests).</summary>
    internal IReadOnlyList<string> PhysicalRowKeys(string partitionKey) => table.RowKeys(partitionKey);

    /// <summary>
    /// How many entity-group transactions the batch writes and the partition deletes of a
    /// large-value-aware type have attempted, committed or not (for tests; TableStorage sends one
    /// batch request each).
    /// </summary>
    internal int BatchTransactions
    {
        get => engine.BatchTransactions;
        set => engine.BatchTransactions = value;
    }

    /// <summary>
    /// Runs after each large-value write or delete has committed, before its blob cleanup (for tests);
    /// a batch write and a partition delete run it after each of their transactions.
    /// </summary>
    internal Action? AfterLargeCommit
    {
        get => engine.AfterLargeCommit;
        set => engine.AfterLargeCommit = value;
    }

    /// <summary>
    /// Runs before each attempt of a large-value write (single or batch transaction) commits, after it
    /// has read, planned and uploaded (for tests that let another writer in between a write's read and
    /// its commit).
    /// </summary>
    internal Action? BeforeLargeCommit
    {
        get => engine.BeforeLargeCommit;
        set => engine.BeforeLargeCommit = value;
    }

    /// <summary>The large-value rules this store shares with TableStorage (for tests that replace their retry wait).</summary>
    internal LargeValueTable LargeValueRules => engine.LargeValueRules;

    /// <summary>A copy of the stored row at these keys, continuation rows included (for tests); null when absent.</summary>
    internal TableEntity? PhysicalRow(string partitionKey, string rowKey) =>
        table.Row(partitionKey, rowKey) is { } row ? RowCells.Copy(row.Data) : null;

    /// <summary>The ETag and the last-write time of the stored row at these keys, continuation rows included (for tests); null when absent.</summary>
    internal (string ETag, DateTimeOffset Timestamp)? PhysicalVersion(string partitionKey, string rowKey) =>
        table.Row(partitionKey, rowKey) is { } row ? (row.ETag, row.Timestamp) : null;

    /// <summary>Stores a raw row as is (for tests that stage damaged or foreign rows).</summary>
    internal void SeedPhysicalRow(TableEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        table.Seed(RowCells.Copy(row));
    }

    /// <summary>Removes a raw row as is (for tests that damage a stored entity); false when there was none.</summary>
    internal bool RemovePhysicalRow(string partitionKey, string rowKey) => table.Remove(partitionKey, rowKey);

    // ── Test conveniences (not part of IStorage) ────────────────────────────

    /// <summary>Number of entities currently stored (continuation rows of split values are not counted).</summary>
    public int Count => LargeValueAware ? table.CountRows(row => !LargeValueTable.IsPartRow(row.Data)) : table.RowCount;

    /// <summary>
    /// Names of the blobs that currently hold offloaded values (ordinal order), for assertions; empty
    /// when blob offload is not configured. A blob a replace or delete released stays listed until its
    /// cleanup grace (<see cref="OffloadOptions.CleanupGrace"/>) has passed.
    /// </summary>
    public IReadOnlyList<string> OffloadedBlobNames() =>
        Blobs.Names.OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>Removes all rows.</summary>
    public void Clear() => table.Clear();

    /// <summary>Deserialized copies of every stored row (lexical key order), for assertions.</summary>
    public IReadOnlyList<T> Snapshot() =>
        // The in-memory rows and blobs complete synchronously, so this never blocks on I/O.
        engine.SnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();

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
    /// <see cref="QueryOptions.FromRowKey"/> and <see cref="QueryOptions.ToRowKey"/> select the row keys
    /// between them within <see cref="QueryOptions.Partition"/>, compared ordinally after the store's id
    /// normalization, exactly as <c>TableStorage&lt;T&gt;</c> answers them; so do
    /// <see cref="QueryStreamAsync(QueryOptions?, CancellationToken)"/> and <see cref="QueryPageAsync"/>.
    /// </remarks>
    public Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default) => engine.QueryAsync(options, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) =>
        engine.QueryAsync(predicate, partition, take, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null, CancellationToken ct = default) =>
        engine.QueryStreamAsync(options, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<T> QueryStreamAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null,
        CancellationToken ct = default) => engine.QueryStreamAsync(predicate, partition, take, ct);

    /// <inheritdoc />
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
    /// <see cref="IRawTable"/>; the tables of its in-memory account (its own table, and any created
    /// through the capability) for the experimental <see cref="ITableStore"/>. Every capability the
    /// Azure provider has is supported.
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
    /// plans and refuses by (the service's: 100 rows and 3,000,000 bytes a transaction, 64 KiB a cell,
    /// 1 MB a row), no read cache (<see cref="StorageInfo.ReadCacheMayServeStaleRows"/> is false), and
    /// <see cref="Durability.Volatile"/>: nothing outlives the process. Calls nothing and never throws.
    /// </summary>
    /// <returns>The store's info; the same value on every call.</returns>
    public StorageInfo GetInfo() => engine.GetInfo();

    /// <inheritdoc />
    /// <remarks>
    /// One atomic step. This store never sends a request twice, so the answer is never
    /// <see cref="DeleteStatus.Indeterminate"/>.
    /// </remarks>
    public Task<DeleteStatus> DeleteIfExistsAsync(string id, CancellationToken ct = default) => engine.DeleteIfExistsAsync(id, ct);

    /// <inheritdoc />
    /// <remarks>
    /// One atomic step, never repeated on another version: a stored version other than
    /// <paramref name="etag"/> is the <see cref="ConcurrencyConflictException"/>. Never
    /// <see cref="DeleteStatus.Indeterminate"/>.
    /// </remarks>
    public Task<DeleteStatus> DeleteIfMatchAsync(string id, string etag, CancellationToken ct = default) =>
        engine.DeleteIfMatchAsync(id, etag, ct);

    /// <inheritdoc />
    /// <remarks>
    /// One atomic step, planned as <c>TableStorage&lt;T&gt;</c> plans its one transaction: one partition,
    /// at most 100 rows, each row once, the payload budget of <see cref="BatchPlanner"/>; for a type with
    /// large values the continuation rows count, and a value that would be offloaded is refused. A batch
    /// that is refused leaves the caller's entities as they were.
    /// </remarks>
    public Task CreateAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => engine.CreateAtomicAsync(entities, ct);

    /// <inheritdoc />
    /// <remarks>One atomic step, planned as <see cref="CreateAtomicAsync"/> is.</remarks>
    public Task UpsertAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => engine.UpsertAtomicAsync(entities, ct);
}
