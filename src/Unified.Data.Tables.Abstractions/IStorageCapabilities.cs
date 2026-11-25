using System.Diagnostics.CodeAnalysis;

namespace Unified.Data.Tables;

/// <summary>
/// The capability probe: how a store says which side interfaces (<see cref="IConditionalDelete{T}"/>,
/// <see cref="IAtomicBatch{T}"/>, <see cref="IAtomicInsert{TBase}"/>, <see cref="IStorageInfo"/>,
/// <see cref="IOrphanBlobSweeper"/>, ...) it supports, when that cannot be read off its type.
/// </summary>
/// <remarks>
/// A decorator is the case it exists for. A wrapper that implemented every side interface and forwarded
/// each call would claim capabilities its inner store lacks; one that implemented none would hide those
/// the inner store has. A decorator therefore implements this interface and answers for the store it
/// wraps (<see cref="DelegatingStorage{T}"/> does so). The library itself looks for a capability only
/// through <see cref="StorageCapabilityExtensions.TryGetCapability{T, TCap}(IStorage{T}, out TCap)"/>:
/// a store that implements this interface is asked and its answer is final; only a store that does not
/// implement it is tested with a cast.
/// </remarks>
public interface IStorageCapabilities
{
    /// <summary>The store's implementation of <typeparamref name="TCap"/>, when it supports it.</summary>
    /// <typeparam name="TCap">The side interface asked for.</typeparam>
    /// <param name="capability">The implementation to call, or null when the store does not support it.</param>
    /// <returns>True when the store supports <typeparamref name="TCap"/>.</returns>
    bool TryGetCapability<TCap>([NotNullWhen(true)] out TCap? capability) where TCap : class;
}
