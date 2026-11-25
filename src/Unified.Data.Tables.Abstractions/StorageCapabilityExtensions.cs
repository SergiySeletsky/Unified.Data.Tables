using System.Diagnostics.CodeAnalysis;

namespace Unified.Data.Tables;

/// <summary>
/// The side interfaces of a store, reached from an <see cref="IStorage{T}"/> or an
/// <see cref="IPolymorphicStorage{TBase}"/>: the capability probe, and one forwarding method per
/// operation that throws <see cref="NotSupportedException"/> when the store lacks the capability.
/// </summary>
/// <remarks>
/// Every method finds the capability the same way. A store that implements
/// <see cref="IStorageCapabilities"/> is asked, and its answer is final, so a decorator
/// (<see cref="DelegatingStorage{T}"/>) exposes exactly what the store it wraps supports. Only a store that
/// does not implement the probe is tested with a cast to the side interface. Code that must work on any
/// store (a third-party implementer, an older decorator, a mock) calls
/// <see cref="TryGetCapability{T, TCap}(IStorage{T}, out TCap)"/> and falls back when it returns false.
/// </remarks>
public static class StorageCapabilityExtensions
{
    /// <summary>The store's implementation of the side interface <typeparamref name="TCap"/>, when it supports it.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <typeparam name="TCap">The side interface asked for, for example <see cref="IConditionalDelete{T}"/>.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="capability">The implementation to call, or null when the store does not support it.</param>
    /// <returns>True when the store supports <typeparamref name="TCap"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public static bool TryGetCapability<T, TCap>(this IStorage<T> storage, [NotNullWhen(true)] out TCap? capability)
        where T : class, IEntity, new()
        where TCap : class
    {
        Guard.NotNull(storage, nameof(storage));
        return Find(storage, out capability);
    }

    /// <summary>The polymorphic store's implementation of the side interface <typeparamref name="TCap"/>, when it supports it.</summary>
    /// <typeparam name="TBase">The store's base type.</typeparam>
    /// <typeparam name="TCap">The side interface asked for, for example <see cref="IAtomicInsert{TBase}"/>.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="capability">The implementation to call, or null when the store does not support it.</param>
    /// <returns>True when the store supports <typeparamref name="TCap"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public static bool TryGetCapability<TBase, TCap>(this IPolymorphicStorage<TBase> storage, [NotNullWhen(true)] out TCap? capability)
        where TBase : class
        where TCap : class
    {
        Guard.NotNull(storage, nameof(storage));
        return Find(storage, out capability);
    }

    /// <summary>
    /// What the store states about itself (<see cref="IStorageInfo"/>). Never throws for a store:
    /// one that does not support <see cref="IStorageInfo"/> (a third-party implementer, a mock), returns
    /// null from it or fails in it reads as <see cref="StorageInfo.Unknown"/>.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <returns>The store's info, or <see cref="StorageInfo.Unknown"/>; never null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public static StorageInfo GetInfo<T>(this IStorage<T> storage) where T : class, IEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        return InfoOf(storage);
    }

    /// <summary>
    /// What the polymorphic store states about itself (<see cref="IStorageInfo"/>). Never throws for a
    /// store: one that does not support <see cref="IStorageInfo"/>, returns null from it or fails in it
    /// reads as <see cref="StorageInfo.Unknown"/>.
    /// </summary>
    /// <typeparam name="TBase">The store's base type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <returns>The store's info, or <see cref="StorageInfo.Unknown"/>; never null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public static StorageInfo GetInfo<TBase>(this IPolymorphicStorage<TBase> storage) where TBase : class
    {
        Guard.NotNull(storage, nameof(storage));
        return InfoOf(storage);
    }

    /// <summary>
    /// <see cref="IConditionalDelete{T}.DeleteIfMatchAsync"/> of <paramref name="storage"/>: delete the row
    /// only when it is still the version <paramref name="etag"/> names. On every provider that supports
    /// it, another version throws <see cref="ConcurrencyConflictException"/> and a missing row is
    /// <see cref="DeleteStatus.NotFound"/> (or <see cref="DeleteStatus.Indeterminate"/> after a lost
    /// response).
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="id">Composite document identifier (<c>"{PartitionKey}|{RowKey}"</c>).</param>
    /// <param name="etag">The version to delete; not null, empty or <c>"*"</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether this call removed the row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="etag"/> is null, empty or <c>"*"</c> (checked before the capability).</exception>
    /// <exception cref="NotSupportedException"><paramref name="storage"/> does not support <see cref="IConditionalDelete{T}"/>.</exception>
    /// <exception cref="ConcurrencyConflictException">The row exists and is another version.</exception>
    public static Task<DeleteStatus> DeleteIfMatchAsync<T>(
        this IStorage<T> storage, string id, string etag, CancellationToken ct = default)
        where T : class, IEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        ETagCondition.Require(etag, nameof(etag));
        return Require<IConditionalDelete<T>>(storage, "conditional delete").DeleteIfMatchAsync(id, etag, ct);
    }

    /// <summary>
    /// <see cref="IConditionalDelete{T}.DeleteIfExistsAsync"/> of <paramref name="storage"/>: delete the
    /// row whatever its version and report whether there was one.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="id">Composite document identifier (<c>"{PartitionKey}|{RowKey}"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether this call removed a row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="NotSupportedException"><paramref name="storage"/> does not support <see cref="IConditionalDelete{T}"/>.</exception>
    public static Task<DeleteStatus> DeleteIfExistsAsync<T>(
        this IStorage<T> storage, string id, CancellationToken ct = default)
        where T : class, IEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        return Require<IConditionalDelete<T>>(storage, "conditional delete").DeleteIfExistsAsync(id, ct);
    }

    /// <summary>
    /// <see cref="IAtomicBatch{T}.CreateAtomicAsync"/> of <paramref name="storage"/>: insert every entity
    /// in one transaction or none of them. A batch that cannot be one transaction throws
    /// <see cref="BatchNotAtomicException"/> before anything is written, on every provider that supports it.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="entities">The entities to insert; all in one partition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when every entity is stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="NotSupportedException"><paramref name="storage"/> does not support <see cref="IAtomicBatch{T}"/>.</exception>
    /// <exception cref="BatchNotAtomicException">The batch cannot be one transaction; nothing was written.</exception>
    /// <exception cref="DuplicateKeyException">A row with one of the ids already exists; nothing was written.</exception>
    public static Task CreateAtomicAsync<T>(
        this IStorage<T> storage, IReadOnlyCollection<T> entities, CancellationToken ct = default)
        where T : class, IEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        return Require<IAtomicBatch<T>>(storage, "all-or-nothing batches").CreateAtomicAsync(entities, ct);
    }

    /// <summary>
    /// <see cref="IAtomicBatch{T}.UpsertAtomicAsync"/> of <paramref name="storage"/>: insert or replace
    /// every entity in one transaction or none of them. A batch that cannot be one transaction throws
    /// <see cref="BatchNotAtomicException"/> before anything is written, on every provider that supports it.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="entities">The entities to upsert; all in one partition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when every entity is stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="NotSupportedException"><paramref name="storage"/> does not support <see cref="IAtomicBatch{T}"/>.</exception>
    /// <exception cref="BatchNotAtomicException">The batch cannot be one transaction; nothing was written.</exception>
    public static Task UpsertAtomicAsync<T>(
        this IStorage<T> storage, IReadOnlyCollection<T> entities, CancellationToken ct = default)
        where T : class, IEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        return Require<IAtomicBatch<T>>(storage, "all-or-nothing batches").UpsertAtomicAsync(entities, ct);
    }

    /// <summary>
    /// <see cref="IAtomicInsert{TBase}.InsertAtomicAsync"/> of <paramref name="storage"/>: insert every
    /// row in one transaction or none of them. A batch that cannot be one transaction throws
    /// <see cref="BatchNotAtomicException"/> before anything is written, on every provider that supports it.
    /// </summary>
    /// <typeparam name="TBase">The store's base type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="writes">The rows to insert; all in one partition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when every row is stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="NotSupportedException"><paramref name="storage"/> does not support <see cref="IAtomicInsert{TBase}"/>.</exception>
    /// <exception cref="BatchNotAtomicException">The batch cannot be one transaction; nothing was written.</exception>
    /// <exception cref="DuplicateKeyException">A row already exists at one of the keys; nothing was written.</exception>
    public static Task InsertAtomicAsync<TBase>(
        this IPolymorphicStorage<TBase> storage, IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default)
        where TBase : class
    {
        Guard.NotNull(storage, nameof(storage));
        return Require<IAtomicInsert<TBase>>(storage, "all-or-nothing inserts").InsertAtomicAsync(writes, ct);
    }

    // The one place a capability is looked up. A store that answers the probe is never cast: a decorator
    // is an IStorage<T> of its own, and a cast would report what the wrapper's type implements instead of
    // what the wrapped store can do.
    internal static bool Find<TCap>(object store, [NotNullWhen(true)] out TCap? capability) where TCap : class
    {
        if (store is IStorageCapabilities probe)
        {
            if (probe.TryGetCapability(out capability) && capability is not null)
                return true;
            capability = null;
            return false;
        }

        capability = store as TCap;
        return capability is not null;
    }

    internal static TCap Require<TCap>(object store, string what) where TCap : class =>
        Find<TCap>(store, out var capability)
            ? capability
            : throw new NotSupportedException(
                $"{store.GetType().FullName} does not support {what}: neither it nor the store it wraps " +
                $"implements {typeof(TCap).Name}. Check with TryGetCapability before the call when the store may be " +
                "a third-party implementer, a decorator or a mock.");

    private static StorageInfo InfoOf(object store)
    {
        try
        {
            return Find<IStorageInfo>(store, out var info) ? info.GetInfo() ?? StorageInfo.Unknown : StorageInfo.Unknown;
        }
#pragma warning disable CA1031 // the contract is "never throws": a store that fails to describe itself states nothing
        catch (Exception)
#pragma warning restore CA1031
        {
            return StorageInfo.Unknown;
        }
    }
}

/// <summary>The rule for a caller-supplied version in a conditional operation, shared by the providers.</summary>
internal static class ETagCondition
{
    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="etag"/> names one version.</summary>
    public static void Require(string? etag, string paramName)
    {
        if (string.IsNullOrEmpty(etag))
            throw new ArgumentException(
                "A conditional delete needs the row's ETag; use DeleteIfExistsAsync to delete whatever the version.", paramName);
        if (etag == "*")
            throw new ArgumentException(
                "The wildcard ETag \"*\" is not a version; use DeleteIfExistsAsync to delete whatever the version.", paramName);
    }
}
