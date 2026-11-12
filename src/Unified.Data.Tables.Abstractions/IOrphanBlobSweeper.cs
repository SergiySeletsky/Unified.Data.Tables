namespace Unified.Data.Tables;

/// <summary>
/// The orphan-blob sweep of a store that offloads large values to blob storage. Implemented by
/// <c>TableStorage&lt;T&gt;</c> and <c>InMemoryStorage&lt;T&gt;</c>; an <see cref="IStorage{T}"/> reaches it
/// through <see cref="OrphanBlobSweeperExtensions"/>.
/// </summary>
/// <remarks>
/// A write uploads (or touches) its blobs before it writes the row that references them, and a failed
/// write never deletes them, so a crash between the two leaves an orphan blob that only a sweep removes.
/// Each delete is conditional on the blob not having been modified since it was listed, so a blob a
/// concurrent write touches to reuse it survives. Choose <c>olderThan</c> longer than any write takes
/// from its blob upload to its row write: a younger blob may belong to a write still in flight. Ages are
/// measured on the blob service's clock. Only blobs of the exact offload shape
/// <c>{table}/{partitionKey}/{rowKey}/{column}/{sha256}</c> are ever deleted. Rows are never read beyond
/// their keys and blob references, and never changed.
/// </remarks>
public interface IOrphanBlobSweeper
{
    /// <summary>
    /// Deletes the blobs offloaded under <c>{table}/{partitionKey}/</c> that no row of the partition
    /// references and whose Last-Modified is older than <paramref name="olderThan"/>.
    /// </summary>
    /// <remarks>
    /// An orphan whose delete fails does not stop the others. Once every orphan was tried, the failures
    /// are thrown together as an <see cref="AggregateException"/> (one inner exception per blob) whose
    /// message says how many blobs were deleted; the next sweep retries the rest. An empty or whitespace
    /// key is a partition like any other (a write of <c>"|r"</c>, or of <c>"   |r"</c> under
    /// <c>IdNormalization.AsWritten</c>, stores one) and is normalized as ids are on write.
    /// </remarks>
    /// <param name="partitionKey">The partition whose blobs to sweep (normalized as ids are on write).</param>
    /// <param name="olderThan">Only blobs last modified longer ago than this are deleted; must not be negative.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of blobs deleted; 0 when blob offload is not configured.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="partitionKey"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="olderThan"/> is negative.</exception>
    /// <exception cref="AggregateException">One or more orphans could not be deleted.</exception>
    Task<int> SweepOrphanBlobsAsync(string partitionKey, TimeSpan olderThan, CancellationToken ct = default);

    /// <summary>
    /// Sweeps every partition of the table that holds offloaded blobs, as
    /// <see cref="SweepOrphanBlobsAsync(string, TimeSpan, CancellationToken)"/> does one: the partitions
    /// are listed from the blob container (<c>{table}/</c> with the delimiter <c>/</c>), and one cutoff,
    /// read from the service clock before the listing, applies to all of them.
    /// </summary>
    /// <remarks>
    /// A partition that fails does not stop the others, nor does an orphan that cannot be deleted stop the
    /// rest of its partition. Once every partition was tried, the failures are thrown together as an
    /// <see cref="AggregateException"/> (one inner exception per failed partition; a partition whose
    /// listing succeeded but some deletes failed contributes an <see cref="AggregateException"/> of those)
    /// whose message says how many blobs were deleted, in every partition; blobs deleted before that stay deleted, and the
    /// next sweep retries the rest. Cancellation ends the sweep with an <see cref="OperationCanceledException"/>.
    /// </remarks>
    /// <param name="olderThan">Only blobs last modified longer ago than this are deleted; must not be negative.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of blobs deleted; 0 when blob offload is not configured.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="olderThan"/> is negative.</exception>
    /// <exception cref="AggregateException">One or more partitions could not be swept.</exception>
    Task<int> SweepOrphanBlobsAsync(TimeSpan olderThan, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IOrphanBlobSweeper"/> on an <see cref="IStorage{T}"/>: forwards to the store when it
/// implements the capability and throws <see cref="NotSupportedException"/> otherwise.
/// </summary>
public static class OrphanBlobSweeperExtensions
{
    /// <summary>
    /// <see cref="IOrphanBlobSweeper.SweepOrphanBlobsAsync(string, TimeSpan, CancellationToken)"/> of
    /// <paramref name="storage"/>.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="partitionKey">The partition whose blobs to sweep.</param>
    /// <param name="olderThan">The minimum age of a deleted blob; must not be negative.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of blobs deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="NotSupportedException"><paramref name="storage"/> does not implement <see cref="IOrphanBlobSweeper"/>.</exception>
    public static Task<int> SweepOrphanBlobsAsync<T>(
        this IStorage<T> storage, string partitionKey, TimeSpan olderThan, CancellationToken ct = default)
        where T : class, IEntity, new() =>
        Sweeper(storage).SweepOrphanBlobsAsync(partitionKey, olderThan, ct);

    /// <summary>
    /// <see cref="IOrphanBlobSweeper.SweepOrphanBlobsAsync(TimeSpan, CancellationToken)"/> of
    /// <paramref name="storage"/>: every partition of the table.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="olderThan">The minimum age of a deleted blob; must not be negative.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of blobs deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="NotSupportedException"><paramref name="storage"/> does not implement <see cref="IOrphanBlobSweeper"/>.</exception>
    public static Task<int> SweepOrphanBlobsAsync<T>(
        this IStorage<T> storage, TimeSpan olderThan, CancellationToken ct = default)
        where T : class, IEntity, new() =>
        Sweeper(storage).SweepOrphanBlobsAsync(olderThan, ct);

    private static IOrphanBlobSweeper Sweeper<T>(IStorage<T> storage) where T : class, IEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        return storage as IOrphanBlobSweeper
            ?? throw new NotSupportedException($"{storage.GetType().FullName} does not implement {nameof(IOrphanBlobSweeper)}.");
    }
}

/// <summary>
/// Whether a store that implements <see cref="IOrphanBlobSweeper"/> offloads to blob storage at all, so
/// the hosted sweep can skip one that has nothing to sweep (internal: the library's own stores only).
/// </summary>
internal interface IBlobOffloadState
{
    /// <summary>True when the store offloads values to (and so may leave orphans in) a blob container.</summary>
    bool HasBlobOffload { get; }
}
