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

    /// <summary>
    /// Read the entity, or create it when absent — converging on the winner's row when a
    /// concurrent creator gets there first (<see cref="DuplicateKeyException"/> is absorbed and
    /// the existing row returned). Idempotent create without exception handling at the call site.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The storage to operate on.</param>
    /// <param name="id">Composite document identifier (<c>"{PartitionKey}|{RowKey}"</c>).</param>
    /// <param name="factory">Produces the initial state when the row is absent; <see cref="Entity.Id"/> is set for you.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The existing or newly created entity.</returns>
    public static async Task<T> GetOrCreateAsync<T>(
        this IStorage<T> storage, string id, Func<T> factory, CancellationToken ct = default)
        where T : Entity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        Guard.NotNull(factory, nameof(factory));

        var existing = await storage.OneAsync(id, ct).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var fresh = factory();
        fresh.Id = id;
        try
        {
            return await storage.CreateAsync(fresh, ct).ConfigureAwait(false);
        }
        catch (DuplicateKeyException)
        {
            // Lost the create race — the row exists now; the winner's row IS the desired outcome.
            return await storage.OneAsync(id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"'{id}' reported a duplicate key but could not be read back (deleted concurrently?).");
        }
    }

    /// <summary>
    /// Insert-or-mutate with optimistic concurrency: when the row is absent, <paramref name="create"/>
    /// supplies the initial state; <paramref name="mutate"/> is then applied EXACTLY ONCE per
    /// attempt — to the fresh initial state or to the current row — so a delta like
    /// <c>e =&gt; e.Count++</c> behaves identically on first insert and on every later call.
    /// Create races and update conflicts are absorbed and retried against fresh state.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The storage to operate on.</param>
    /// <param name="id">Composite document identifier (<c>"{PartitionKey}|{RowKey}"</c>).</param>
    /// <param name="create">Produces the initial (pre-mutation) state; <see cref="Entity.Id"/> is set for you.</param>
    /// <param name="mutate">The delta to apply. May run multiple times (once per attempt, each on fresh state) — no side effects beyond the passed instance.</param>
    /// <param name="maxAttempts">Total attempts before the conflict propagates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entity, including its refreshed <see cref="Entity.ETag"/>.</returns>
    /// <exception cref="ConcurrencyConflictException">Still conflicted after <paramref name="maxAttempts"/> attempts.</exception>
    public static async Task<T> MutateOrCreateAsync<T>(
        this IStorage<T> storage, string id, Func<T> create, Action<T> mutate,
        int maxAttempts = 3, CancellationToken ct = default)
        where T : Entity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        Guard.NotNull(create, nameof(create));
        Guard.NotNull(mutate, nameof(mutate));
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "At least one attempt is required.");

        for (var attempt = 1; ; attempt++)
        {
            var entity = await storage.OneAsync(id, ct).ConfigureAwait(false);
            try
            {
                if (entity is null)
                {
                    var fresh = create();
                    fresh.Id = id;
                    mutate(fresh);
                    return await storage.CreateAsync(fresh, ct).ConfigureAwait(false);
                }

                mutate(entity);
                return await storage.UpdateAsync(entity, ConcurrencyMode.Strict, ct).ConfigureAwait(false);
            }
            catch (DuplicateKeyException) when (attempt < maxAttempts)
            {
                // Lost the create race — loop reads the winner's row and mutates THAT.
                await Task.Delay((attempt * BackoffBaseMs) + NextJitterMs(), ct).ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException) when (attempt < maxAttempts)
            {
                await Task.Delay((attempt * BackoffBaseMs) + NextJitterMs(), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Outcome-returning variant of <see cref="MutateAsync{T}"/> for flows where a missing row or
    /// a lost race is an EXPECTED branch, not an error: nothing is thrown for either — the caller
    /// switches on <see cref="MutationResult{T}.Status"/> instead of catching.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The storage to operate on.</param>
    /// <param name="id">Composite document identifier (<c>"{PartitionKey}|{RowKey}"</c>).</param>
    /// <param name="mutate">The mutation to apply (may run once per attempt, each on a fresh read).</param>
    /// <param name="maxAttempts">Total attempts before returning <see cref="MutationStatus.Conflicted"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<MutationResult<T>> TryMutateAsync<T>(
        this IStorage<T> storage, string id, Action<T> mutate,
        int maxAttempts = 3, CancellationToken ct = default)
        where T : Entity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        Guard.NotNull(mutate, nameof(mutate));
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "At least one attempt is required.");

        for (var attempt = 1; ; attempt++)
        {
            var entity = await storage.OneAsync(id, ct).ConfigureAwait(false);
            if (entity is null)
                return new MutationResult<T>(MutationStatus.NotFound, null);

            mutate(entity);

            try
            {
                var persisted = await storage.UpdateAsync(entity, ConcurrencyMode.Strict, ct).ConfigureAwait(false);
                return new MutationResult<T>(MutationStatus.Updated, persisted);
            }
            catch (ConcurrencyConflictException)
            {
                if (attempt >= maxAttempts)
                    return new MutationResult<T>(MutationStatus.Conflicted, null);
                await Task.Delay((attempt * BackoffBaseMs) + NextJitterMs(), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Exactly-once state transition as a RESULT, not an exception: applies
    /// <paramref name="apply"/> only while <paramref name="when"/> holds on the FRESH row,
    /// re-checking after every lost race. The three outcomes of contended transitions —
    /// applied, someone else got there first, row gone — are all expected branches:
    /// <list type="bullet">
    ///   <item><description><see cref="MutationStatus.Updated"/> — this caller performed the transition;</description></item>
    ///   <item><description><see cref="MutationStatus.PreconditionFailed"/> — the fresh row no longer satisfies
    ///   <paramref name="when"/> (typically: another writer already transitioned it); the result carries that row;</description></item>
    ///   <item><description><see cref="MutationStatus.NotFound"/> / <see cref="MutationStatus.Conflicted"/>.</description></item>
    /// </list>
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="storage">The storage to operate on.</param>
    /// <param name="id">Composite document identifier (<c>"{PartitionKey}|{RowKey}"</c>).</param>
    /// <param name="when">The precondition, evaluated on a fresh read each attempt (e.g. <c>g =&gt; g.Status == "open"</c>).</param>
    /// <param name="apply">The transition to apply when the precondition holds.</param>
    /// <param name="maxAttempts">Total attempts before returning <see cref="MutationStatus.Conflicted"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<MutationResult<T>> TryTransitionAsync<T>(
        this IStorage<T> storage, string id, Func<T, bool> when, Action<T> apply,
        int maxAttempts = 3, CancellationToken ct = default)
        where T : Entity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        Guard.NotNull(when, nameof(when));
        Guard.NotNull(apply, nameof(apply));
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "At least one attempt is required.");

        for (var attempt = 1; ; attempt++)
        {
            var entity = await storage.OneAsync(id, ct).ConfigureAwait(false);
            if (entity is null)
                return new MutationResult<T>(MutationStatus.NotFound, null);

            if (!when(entity))
                return new MutationResult<T>(MutationStatus.PreconditionFailed, entity);

            apply(entity);

            try
            {
                var persisted = await storage.UpdateAsync(entity, ConcurrencyMode.Strict, ct).ConfigureAwait(false);
                return new MutationResult<T>(MutationStatus.Updated, persisted);
            }
            catch (ConcurrencyConflictException)
            {
                // A foreign write landed — it may well have BEEN the transition. Loop: the fresh
                // re-read re-evaluates the precondition, so the winner is reported as
                // PreconditionFailed rather than surfacing a raw conflict.
                if (attempt >= maxAttempts)
                    return new MutationResult<T>(MutationStatus.Conflicted, null);
                await Task.Delay((attempt * BackoffBaseMs) + NextJitterMs(), ct).ConfigureAwait(false);
            }
        }
    }
}
