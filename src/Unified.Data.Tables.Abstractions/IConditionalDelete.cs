namespace Unified.Data.Tables;

/// <summary>
/// A delete that reports what it found, and one that is conditional on the row's version. A side
/// interface of <see cref="IStorage{T}"/>: a store that supports it implements it next to
/// <see cref="IStorage{T}"/>, and callers reach it through
/// <see cref="StorageCapabilityExtensions.DeleteIfMatchAsync"/> and
/// <see cref="StorageCapabilityExtensions.DeleteIfExistsAsync"/>, which find it with the capability probe
/// (<see cref="IStorageCapabilities"/>) and throw <see cref="NotSupportedException"/> on a store without it.
/// </summary>
/// <remarks>
/// The contract is the same on every provider. For a type with large values the row's continuation rows
/// and the blobs it references go with it, as in <see cref="IStorage{T}.DeleteAsync"/>. Failures are
/// provider-neutral (see <see cref="StorageException"/>).
/// </remarks>
/// <typeparam name="T">The entity type of the store.</typeparam>
public interface IConditionalDelete<T> where T : class, IEntity, new()
{
    /// <summary>
    /// Delete the row only when it is still the version <paramref name="etag"/> names.
    /// </summary>
    /// <remarks>
    /// A row that is another version throws <see cref="ConcurrencyConflictException"/> and is left as it
    /// is; the call never re-reads and retries on a caller's ETag. A row that does not exist is
    /// <see cref="DeleteStatus.NotFound"/>, or <see cref="DeleteStatus.Indeterminate"/> when an earlier
    /// attempt of this request may have been the one that removed it.
    /// </remarks>
    /// <param name="id">Composite document identifier (<c>"{PartitionKey}|{RowKey}"</c>).</param>
    /// <param name="etag">
    /// The version to delete, as read from <see cref="IEntity.ETag"/>. The wildcard <c>"*"</c> is not a
    /// version: use <see cref="DeleteIfExistsAsync"/> for an unconditional delete.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether this call removed the row.</returns>
    /// <exception cref="ArgumentException"><paramref name="etag"/> is null, empty or <c>"*"</c>.</exception>
    /// <exception cref="ConcurrencyConflictException">The row exists and is another version.</exception>
    Task<DeleteStatus> DeleteIfMatchAsync(string id, string etag, CancellationToken ct = default);

    /// <summary>
    /// Delete the row whatever its version, and report whether there was one:
    /// <see cref="IStorage{T}.DeleteAsync"/> with a result.
    /// </summary>
    /// <param name="id">Composite document identifier (<c>"{PartitionKey}|{RowKey}"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="DeleteStatus.Deleted"/> when this call removed a row, <see cref="DeleteStatus.NotFound"/>
    /// when there was none, <see cref="DeleteStatus.Indeterminate"/> when the row is gone and an earlier
    /// attempt of this request may have removed it.
    /// </returns>
    Task<DeleteStatus> DeleteIfExistsAsync(string id, CancellationToken ct = default);
}
