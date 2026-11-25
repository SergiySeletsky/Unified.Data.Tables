using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Unified.Data.Tables.Conformance;

/// <summary>
/// The operations of a store a test decorator tells apart: the members of <see cref="IStorage{T}"/> and
/// those of the side interfaces a decorator stays in the path of (<see cref="IConditionalDelete{T}"/>,
/// <see cref="IAtomicBatch{T}"/>).
/// </summary>
public enum StorageMember
{
    /// <summary><see cref="IStorage{T}.DeleteAsync"/>.</summary>
    Delete,

    /// <summary><see cref="IStorage{T}.DeletePartitionAsync"/>.</summary>
    DeletePartition,

    /// <summary><see cref="IStorage{T}.CreateAsync"/>.</summary>
    Create,

    /// <summary><see cref="IStorage{T}.UpsertAsync"/>.</summary>
    Upsert,

    /// <summary>The whole-entity <c>UpdateAsync</c> overloads (with and without a <see cref="ConcurrencyMode"/>).</summary>
    Update,

    /// <summary>The builder <c>UpdateAsync(id, builder)</c>: a merge of named columns.</summary>
    UpdateColumns,

    /// <summary><see cref="IStorage{T}.OneAsync"/>.</summary>
    One,

    /// <summary><see cref="IStorage{T}.ExistsAsync"/>.</summary>
    Exists,

    /// <summary><c>QueryAsync(partition)</c>: the cached partition or table read.</summary>
    QueryPartition,

    /// <summary><c>QueryAsync(QueryOptions)</c>.</summary>
    Query,

    /// <summary><c>QueryAsync(predicate, ...)</c>.</summary>
    QueryPredicate,

    /// <summary><c>QueryStreamAsync(QueryOptions)</c>.</summary>
    QueryStream,

    /// <summary><c>QueryStreamAsync(predicate, ...)</c>.</summary>
    QueryStreamPredicate,

    /// <summary><see cref="IStorage{T}.QueryPageAsync"/>.</summary>
    QueryPage,

    /// <summary><see cref="IStorage{T}.AnyAsync"/>.</summary>
    Any,

    /// <summary><see cref="IStorage{T}.CreateBatchAsync"/>.</summary>
    CreateBatch,

    /// <summary><see cref="IStorage{T}.UpsertBatchAsync"/>.</summary>
    UpsertBatch,

    /// <summary><see cref="IStorage{T}.CountAsync"/>.</summary>
    Count,

    /// <summary><see cref="IConditionalDelete{T}.DeleteIfMatchAsync"/>.</summary>
    DeleteIfMatch,

    /// <summary><see cref="IConditionalDelete{T}.DeleteIfExistsAsync"/>.</summary>
    DeleteIfExists,

    /// <summary><see cref="IAtomicBatch{T}.CreateAtomicAsync"/>.</summary>
    CreateAtomic,

    /// <summary><see cref="IAtomicBatch{T}.UpsertAtomicAsync"/>.</summary>
    UpsertAtomic,
}

/// <summary>
/// A <see cref="DelegatingStorage{T}"/> that tells its subclass about every call: one hook before the
/// wrapped store is called and one after it answered. The base of <see cref="CountingStorage{T}"/> and
/// <see cref="FaultInjectingStorage{T}"/>, and of a test decorator of your own (a gate that holds a call
/// until the test releases it, a recorder).
/// </summary>
/// <remarks>
/// <b>It stays in the path of the side interfaces.</b> A plain <see cref="DelegatingStorage{T}"/> hands
/// out the wrapped store's own <see cref="IConditionalDelete{T}"/> and <see cref="IAtomicBatch{T}"/>, so a
/// conditional delete or an atomic batch would pass the decorator by. This class answers the capability
/// probe for those two with a wrapper that runs the hooks (<see cref="StorageMember.DeleteIfMatch"/>,
/// <see cref="StorageMember.DeleteIfExists"/>, <see cref="StorageMember.CreateAtomic"/>,
/// <see cref="StorageMember.UpsertAtomic"/>) and then calls the wrapped store's capability, when the
/// wrapped store has it; it never claims one the wrapped store lacks. Every other capability
/// (<see cref="IStorageInfo"/>, <see cref="IOrphanBlobSweeper"/>, the experimental raw table) is the
/// wrapped store's own object, and calls through it are not seen.
/// <para>
/// A stream (<see cref="StorageMember.QueryStream"/>, <see cref="StorageMember.QueryStreamPredicate"/>)
/// runs the first hook when its enumeration starts and the second after its last row was handed out.
/// An exception a hook throws is the result of the call.
/// </para>
/// </remarks>
/// <typeparam name="T">The entity type.</typeparam>
public abstract class InterceptingStorage<T> : DelegatingStorage<T> where T : class, IEntity, new()
{
    /// <summary>Creates a decorator over <paramref name="inner"/>.</summary>
    /// <param name="inner">The store every member forwards to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    protected InterceptingStorage(IStorage<T> inner)
        : base(inner)
    {
    }

    /// <summary>Runs before the wrapped store is called. Throwing fails the call; the wrapped store is then not called.</summary>
    /// <param name="member">The operation being called.</param>
    /// <param name="ct">The caller's token.</param>
    /// <returns>A task the call waits for.</returns>
    protected virtual ValueTask OnCallingAsync(StorageMember member, CancellationToken ct) => default;

    /// <summary>
    /// Runs after the wrapped store answered without an exception. Throwing fails the call although the
    /// wrapped store did its work: how a lost response looks to the caller.
    /// </summary>
    /// <param name="member">The operation that was called.</param>
    /// <param name="ct">The caller's token.</param>
    /// <returns>A task the call waits for.</returns>
    protected virtual ValueTask OnCalledAsync(StorageMember member, CancellationToken ct) => default;

    /// <inheritdoc />
    public override bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class
    {
        if (this is TCap own)
        {
            capability = own;
            return true;
        }
        if (typeof(TCap) == typeof(IConditionalDelete<T>))
        {
            capability = Inner.TryGetCapability<T, IConditionalDelete<T>>(out var inner) ? new ConditionalDelete(this, inner) as TCap : null;
            return capability is not null;
        }
        if (typeof(TCap) == typeof(IAtomicBatch<T>))
        {
            capability = Inner.TryGetCapability<T, IAtomicBatch<T>>(out var inner) ? new AtomicBatch(this, inner) as TCap : null;
            return capability is not null;
        }
        return base.TryGetCapability(out capability);
    }

    private async Task<TResult> Run<TResult>(StorageMember member, Func<Task<TResult>> call, CancellationToken ct)
    {
        await OnCallingAsync(member, ct).ConfigureAwait(false);
        var result = await call().ConfigureAwait(false);
        await OnCalledAsync(member, ct).ConfigureAwait(false);
        return result;
    }

    private async Task Run(StorageMember member, Func<Task> call, CancellationToken ct)
    {
        await OnCallingAsync(member, ct).ConfigureAwait(false);
        await call().ConfigureAwait(false);
        await OnCalledAsync(member, ct).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<T> Stream(StorageMember member, Func<IAsyncEnumerable<T>> call, [EnumeratorCancellation] CancellationToken ct)
    {
        await OnCallingAsync(member, ct).ConfigureAwait(false);
        await foreach (var entity in call().WithCancellation(ct).ConfigureAwait(false))
            yield return entity;
        await OnCalledAsync(member, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task DeleteAsync(string id, CancellationToken ct = default) =>
        Run(StorageMember.Delete, () => base.DeleteAsync(id, ct), ct);

    /// <inheritdoc />
    public override Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default) =>
        Run(StorageMember.DeletePartition, () => base.DeletePartitionAsync(partition, ct), ct);

    /// <inheritdoc />
    public override Task<T> CreateAsync(T entity, CancellationToken ct = default) =>
        Run(StorageMember.Create, () => base.CreateAsync(entity, ct), ct);

    /// <inheritdoc />
    public override Task<T> UpsertAsync(T entity, CancellationToken ct = default) =>
        Run(StorageMember.Upsert, () => base.UpsertAsync(entity, ct), ct);

    /// <inheritdoc />
    public override Task<T> UpdateAsync(T entity, CancellationToken ct = default) =>
        Run(StorageMember.Update, () => base.UpdateAsync(entity, ct), ct);

    /// <inheritdoc />
    public override Task<T> UpdateAsync(T entity, ConcurrencyMode mode, CancellationToken ct = default) =>
        Run(StorageMember.Update, () => base.UpdateAsync(entity, mode, ct), ct);

    /// <inheritdoc />
    public override Task<string> UpdateAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct = default) =>
        Run(StorageMember.UpdateColumns, () => base.UpdateAsync(id, builderAction, ct), ct);

    /// <inheritdoc />
    public override Task<T?> OneAsync(string id, CancellationToken ct = default) =>
        Run(StorageMember.One, () => base.OneAsync(id, ct), ct);

    /// <inheritdoc />
    public override Task<bool> ExistsAsync(string id, CancellationToken ct = default) =>
        Run(StorageMember.Exists, () => base.ExistsAsync(id, ct), ct);

    /// <inheritdoc />
    public override Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default) =>
        Run(StorageMember.QueryPartition, () => base.QueryAsync(partition, ct), ct);

    /// <inheritdoc />
    public override Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default) =>
        Run(StorageMember.Query, () => base.QueryAsync(options, ct), ct);

    /// <inheritdoc />
    public override Task<IReadOnlyList<T>> QueryAsync(
        Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) =>
        Run(StorageMember.QueryPredicate, () => base.QueryAsync(predicate, partition, take, ct), ct);

    /// <inheritdoc />
    public override IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null, CancellationToken ct = default) =>
        Stream(StorageMember.QueryStream, () => base.QueryStreamAsync(options, ct), ct);

    /// <inheritdoc />
    public override IAsyncEnumerable<T> QueryStreamAsync(
        Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) =>
        Stream(StorageMember.QueryStreamPredicate, () => base.QueryStreamAsync(predicate, partition, take, ct), ct);

    /// <inheritdoc />
    public override Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default) =>
        Run(StorageMember.QueryPage, () => base.QueryPageAsync(options, ct), ct);

    /// <inheritdoc />
    public override Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, string? partition = null, CancellationToken ct = default) =>
        Run(StorageMember.Any, () => base.AnyAsync(predicate, partition, ct), ct);

    /// <inheritdoc />
    public override Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) =>
        Run(StorageMember.CreateBatch, () => base.CreateBatchAsync(entities, ct), ct);

    /// <inheritdoc />
    public override Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) =>
        Run(StorageMember.UpsertBatch, () => base.UpsertBatchAsync(entities, ct), ct);

    /// <inheritdoc />
    public override Task<int> CountAsync(string? partition = null, CancellationToken ct = default) =>
        Run(StorageMember.Count, () => base.CountAsync(partition, ct), ct);

    private sealed class ConditionalDelete(InterceptingStorage<T> owner, IConditionalDelete<T> inner) : IConditionalDelete<T>
    {
        public Task<DeleteStatus> DeleteIfMatchAsync(string id, string etag, CancellationToken ct = default) =>
            owner.Run(StorageMember.DeleteIfMatch, () => inner.DeleteIfMatchAsync(id, etag, ct), ct);

        public Task<DeleteStatus> DeleteIfExistsAsync(string id, CancellationToken ct = default) =>
            owner.Run(StorageMember.DeleteIfExists, () => inner.DeleteIfExistsAsync(id, ct), ct);
    }

    private sealed class AtomicBatch(InterceptingStorage<T> owner, IAtomicBatch<T> inner) : IAtomicBatch<T>
    {
        public Task CreateAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) =>
            owner.Run(StorageMember.CreateAtomic, () => inner.CreateAtomicAsync(entities, ct), ct);

        public Task UpsertAtomicAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) =>
            owner.Run(StorageMember.UpsertAtomic, () => inner.UpsertAtomicAsync(entities, ct), ct);
    }
}
