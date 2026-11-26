using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Unified.Data.Tables;

/// <summary>
/// The base class of an <see cref="IStorage{T}"/> decorator (counting, logging, fault injection, tenant
/// scoping): every member forwards to the wrapped store and is virtual, so a subclass overrides only what
/// it changes.
/// </summary>
/// <remarks>
/// <b>Warning: a decorator that restricts or observes writes must override
/// <see cref="TryGetCapability{TCap}"/>.</b> Overriding the <see cref="IStorage{T}"/> members is not
/// enough: the side interfaces write too (<see cref="IConditionalDelete{T}"/> deletes,
/// <see cref="IAtomicBatch{T}"/> creates and replaces, <see cref="IOrphanBlobSweeper"/> deletes blobs, the
/// experimental raw table writes rows), and by default the probe hands out the wrapped store's own
/// implementation of them, which never calls back into the decorator. A read-only, tenant-scoping,
/// auditing or fault-injecting decorator that overrides only <see cref="DeleteAsync"/> is bypassed by
/// <c>DeleteIfExistsAsync</c>. Either hide those capabilities (return false for them) or stand in for
/// them (implement the side interface on the decorator and forward); both are described below.
/// <para>
/// <b>Capabilities.</b> The class implements <see cref="IStorageCapabilities"/> and no side interface
/// (<see cref="IConditionalDelete{T}"/>, <see cref="IAtomicBatch{T}"/>, <see cref="IStorageInfo"/>,
/// <see cref="IOrphanBlobSweeper"/>, ...). <see cref="TryGetCapability{TCap}"/> answers with what the
/// wrapped store supports, so a decorator over a store with conditional delete exposes it and one over a
/// store without it does not. The capability handed out is the wrapped store's own object: a call made
/// through it does not pass through the decorator. A subclass that must see those calls implements the
/// side interface itself (it is then found before the wrapped store is asked) and forwards to the
/// wrapped store's capability; one that must hide a capability overrides
/// <see cref="TryGetCapability{TCap}"/> and returns false for it.
/// </para>
/// <para>
/// A decorator that implements <see cref="IStorage{T}"/> by hand, without this class or
/// <see cref="IStorageCapabilities"/>, keeps working; it exposes only the side interfaces its own type
/// implements.
/// </para>
/// </remarks>
/// <typeparam name="T">The entity type.</typeparam>
public abstract class DelegatingStorage<T> : IStorage<T>, IStorageCapabilities where T : class, IEntity, new()
{
    /// <summary>Creates a decorator over <paramref name="inner"/>.</summary>
    /// <param name="inner">The store every member forwards to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    protected DelegatingStorage(IStorage<T> inner)
    {
        Guard.NotNull(inner, nameof(inner));
        Inner = inner;
    }

    /// <summary>The wrapped store.</summary>
    protected IStorage<T> Inner { get; }

    /// <summary>
    /// The implementation of <typeparamref name="TCap"/> behind this decorator: the decorator itself when
    /// its own type implements <typeparamref name="TCap"/>, otherwise whatever the wrapped store answers
    /// (through its own probe when it has one, by its type when it does not).
    /// </summary>
    /// <typeparam name="TCap">The side interface asked for.</typeparam>
    /// <param name="capability">The implementation to call, or null when it is not supported.</param>
    /// <returns>True when <typeparamref name="TCap"/> is supported.</returns>
    public virtual bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
    {
        if (this is TCap own)
        {
            capability = own;
            return true;
        }
        return StorageCapabilityExtensions.Find(Inner, out capability);
    }

    /// <inheritdoc />
    public virtual Task DeleteAsync(string id, CancellationToken ct = default) => Inner.DeleteAsync(id, ct);

    /// <inheritdoc />
    public virtual Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default) =>
        Inner.DeletePartitionAsync(partition, ct);

    /// <inheritdoc />
    public virtual Task<T> CreateAsync(T entity, CancellationToken ct = default) => Inner.CreateAsync(entity, ct);

    /// <inheritdoc />
    public virtual Task<T> UpsertAsync(T entity, CancellationToken ct = default) => Inner.UpsertAsync(entity, ct);

    /// <inheritdoc />
    public virtual Task<T> UpdateAsync(T entity, CancellationToken ct = default) => Inner.UpdateAsync(entity, ct);

    /// <inheritdoc />
    public virtual Task<T> UpdateAsync(T entity, ConcurrencyMode mode, CancellationToken ct = default) =>
        Inner.UpdateAsync(entity, mode, ct);

    /// <inheritdoc />
    public virtual Task<string> UpdateAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct = default) =>
        Inner.UpdateAsync(id, builderAction, ct);

    /// <inheritdoc />
    public virtual Task<T?> OneAsync(string id, CancellationToken ct = default) => Inner.OneAsync(id, ct);

    /// <inheritdoc />
    public virtual Task<bool> ExistsAsync(string id, CancellationToken ct = default) => Inner.ExistsAsync(id, ct);

    /// <inheritdoc />
    public virtual Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default) =>
        Inner.QueryAsync(partition, ct);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default) =>
        Inner.QueryAsync(options, ct);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<T>> QueryAsync(
        Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) =>
        Inner.QueryAsync(predicate, partition, take, ct);

    /// <inheritdoc />
    public virtual IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null, CancellationToken ct = default) =>
        Inner.QueryStreamAsync(options, ct);

    /// <inheritdoc />
    public virtual IAsyncEnumerable<T> QueryStreamAsync(
        Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) =>
        Inner.QueryStreamAsync(predicate, partition, take, ct);

    /// <inheritdoc />
    public virtual Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default) =>
        Inner.QueryPageAsync(options, ct);

    /// <inheritdoc />
    public virtual Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, string? partition = null, CancellationToken ct = default) =>
        Inner.AnyAsync(predicate, partition, ct);

    /// <inheritdoc />
    public virtual Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) =>
        Inner.CreateBatchAsync(entities, ct);

    /// <inheritdoc />
    public virtual Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) =>
        Inner.UpsertBatchAsync(entities, ct);

    /// <inheritdoc />
    public virtual Task<int> CountAsync(string? partition = null, CancellationToken ct = default) =>
        Inner.CountAsync(partition, ct);
}
