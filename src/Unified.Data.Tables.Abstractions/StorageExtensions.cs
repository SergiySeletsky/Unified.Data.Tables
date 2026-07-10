namespace Unified.Data.Tables;

/// <summary>
/// Convenience patterns composed from the core <see cref="IStorage{T}"/> surface — they work
/// against any implementation (Azure, in-memory, custom).
/// </summary>
public static class StorageExtensions
{
    // Base for the between-attempt backoff: a lost attempt N waits N*Base plus up to Base of
    // jitter. Immediate retries tend to re-race the same hot writer; the jitter desynchronizes
    // competing retriers. This is contention smoothing, not an availability retry policy.
    private const int BackoffBaseMs = 20;

#if NETSTANDARD2_0
    private static readonly Random JitterSource = new();

    private static int NextJitterMs()
    {
        lock (JitterSource) return JitterSource.Next(BackoffBaseMs);
    }
#else
    private static int NextJitterMs() => Random.Shared.Next(BackoffBaseMs);
#endif

    /// <summary>
    /// Read-modify-write with optimistic concurrency and bounded retry (compare-and-swap): reads
    /// the entity, applies <paramref name="mutate"/>, and writes with
    /// <see cref="ConcurrencyMode.Strict"/>. When another writer got there first
    /// (<see cref="ConcurrencyConflictException"/>), a brief jittered backoff is awaited, then the
    /// entity is re-read and <paramref name="mutate"/> is re-applied to the FRESH copy — which is
    /// what makes derived values (counters, unions, merges) correct under concurrency, unlike a
    /// plain update that would persist a value computed from a stale read.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The storage to operate on.</param>
    /// <param name="id">Composite document identifier (<c>"{PartitionKey}|{RowKey}"</c>).</param>
    /// <param name="mutate">
    /// The mutation to apply. May run multiple times (once per attempt, each time on a freshly
    /// read entity) — it must not have side effects beyond mutating the passed instance.
    /// </param>
    /// <param name="maxAttempts">Total attempts before the conflict propagates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entity, including its refreshed <see cref="Entity.ETag"/>.</returns>
    /// <exception cref="InvalidOperationException">The entity does not exist.</exception>
    /// <exception cref="ConcurrencyConflictException">Still conflicted after <paramref name="maxAttempts"/> attempts.</exception>
    public static async Task<T> MutateAsync<T>(
        this IStorage<T> storage, string id, Action<T> mutate,
        int maxAttempts = 3, CancellationToken ct = default)
        where T : Entity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        Guard.NotNull(mutate, nameof(mutate));
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "At least one attempt is required.");

        for (var attempt = 1; ; attempt++)
        {
            var entity = await storage.OneAsync(id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Cannot mutate '{id}': the entity does not exist.");

            mutate(entity);

            try
            {
                return await storage.UpdateAsync(entity, ConcurrencyMode.Strict, ct).ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException) when (attempt < maxAttempts)
            {
                // Another writer won the race — back off briefly so competing retriers spread
                // out, then loop: re-read the fresh row and re-apply. The final attempt's
                // conflict propagates without a pointless delay (the filter above excludes it).
                await Task.Delay((attempt * BackoffBaseMs) + NextJitterMs(), ct).ConfigureAwait(false);
            }
        }
    }
}
