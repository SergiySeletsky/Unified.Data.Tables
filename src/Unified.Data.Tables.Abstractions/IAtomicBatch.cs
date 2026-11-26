namespace Unified.Data.Tables;

/// <summary>
/// All-or-nothing batch writes. A side interface of <see cref="IStorage{T}"/>, reached through
/// <see cref="StorageCapabilityExtensions.CreateAtomicAsync"/> and
/// <see cref="StorageCapabilityExtensions.UpsertAtomicAsync"/>, which throw
/// <see cref="NotSupportedException"/> on a store without it.
/// </summary>
/// <remarks>
/// <see cref="IStorage{T}.CreateBatchAsync"/> and <see cref="IStorage{T}.UpsertBatchAsync"/> split a
/// batch into as many transactions as it needs and are atomic per transaction only. These members never
/// split: the batch is one transaction or it is refused. On every provider the limits are those of an
/// Azure Tables transaction (one partition, at most 100 rows, the payload budget of
/// <see cref="BatchPlanner"/>, each row once), so a batch that is atomic on a local provider is atomic
/// on Azure. For a type with large values the continuation rows of an entity count as rows of the
/// transaction.
/// </remarks>
/// <typeparam name="T">The entity type of the store.</typeparam>
public interface IAtomicBatch<T> where T : class, IEntity, new()
{
    /// <summary>
    /// Insert every entity in one transaction, or none of them. An empty batch writes nothing.
    /// </summary>
    /// <param name="entities">The entities to insert; all in one partition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when every entity is stored.</returns>
    /// <exception cref="BatchNotAtomicException">
    /// The batch cannot be one transaction (more than one partition, too many rows, too many bytes, or a
    /// row named twice). Thrown before anything is written.
    /// </exception>
    /// <exception cref="DuplicateKeyException">A row with one of the ids already exists; nothing was written.</exception>
    Task CreateAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default);

    /// <summary>
    /// Insert or replace every entity in one transaction, or none of them; the last writer wins per row,
    /// as in <see cref="IStorage{T}.UpsertAsync"/>. An empty batch writes nothing.
    /// </summary>
    /// <param name="entities">The entities to upsert; all in one partition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when every entity is stored.</returns>
    /// <exception cref="BatchNotAtomicException">
    /// The batch cannot be one transaction (more than one partition, too many rows, too many bytes, or a
    /// row named twice). Thrown before anything is written.
    /// </exception>
    Task UpsertAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default);
}

/// <summary>
/// All-or-nothing insert on a polymorphic store. A side interface of
/// <see cref="IPolymorphicStorage{TBase}"/>, reached through
/// <see cref="StorageCapabilityExtensions.InsertAtomicAsync"/>, which throws
/// <see cref="NotSupportedException"/> on a store without it.
/// </summary>
/// <typeparam name="TBase">The base type of the store.</typeparam>
public interface IAtomicInsert<TBase> where TBase : class
{
    /// <summary>
    /// Insert every row in one transaction, or none of them. Unlike
    /// <see cref="IPolymorphicStorage{TBase}.InsertBatchAsync"/> the batch is never split. An empty batch
    /// writes nothing. The limits are those of an Azure Tables transaction on every provider: one
    /// partition, at most 100 rows, the payload budget of <see cref="BatchPlanner"/>, each key once.
    /// </summary>
    /// <param name="writes">The rows to insert; all in one partition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when every row is stored.</returns>
    /// <exception cref="BatchNotAtomicException">
    /// The batch cannot be one transaction (more than one partition, too many rows, too many bytes, or a
    /// key named twice). Thrown before anything is written.
    /// </exception>
    /// <exception cref="DuplicateKeyException">A row already exists at one of the keys; nothing was written.</exception>
    Task InsertAtomicAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default);
}
