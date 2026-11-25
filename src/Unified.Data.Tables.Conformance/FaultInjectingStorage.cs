namespace Unified.Data.Tables.Conformance;

/// <summary>When a scripted failure of <see cref="FaultInjectingStorage{T}"/> is raised.</summary>
public enum FaultTiming
{
    /// <summary>
    /// Before the wrapped store is called: the request never arrived. The default failure is a
    /// <see cref="TransientStorageException"/> with <see cref="TransientStorageException.MayHaveCommitted"/>
    /// false.
    /// </summary>
    Before,

    /// <summary>
    /// After the wrapped store did the work: the response was lost. A write has landed, and the caller
    /// sees a <see cref="TransientStorageException"/> with
    /// <see cref="TransientStorageException.MayHaveCommitted"/> true.
    /// </summary>
    After,
}

/// <summary>
/// A decorator that fails scripted calls of the store it wraps, for tests of what code does when storage
/// is unavailable (<see cref="FaultTiming.Before"/>) and when a write landed but its response was lost
/// (<see cref="FaultTiming.After"/>): the retry that then meets its own row as a
/// <see cref="DuplicateKeyException"/> or a <see cref="ConcurrencyConflictException"/>.
/// </summary>
/// <remarks>
/// Script with <see cref="FailNext"/>; calls that are not scripted pass through untouched. The failures of
/// one member are used up in the order they were scripted. A call whose wrapped store throws on its own
/// keeps that exception, and an <see cref="FaultTiming.After"/> failure scripted for it stays in place for
/// the next call. Conditional deletes and atomic batches made through the capability probe can be scripted
/// too (see <see cref="InterceptingStorage{T}"/>). Thread-safe. Works over any <see cref="IStorage{T}"/>,
/// on every provider.
/// </remarks>
/// <typeparam name="T">The entity type.</typeparam>
public class FaultInjectingStorage<T> : InterceptingStorage<T> where T : class, IEntity, new()
{
    private readonly object gate = new();
    private readonly Dictionary<StorageMember, List<Fault>> script = [];

    /// <summary>Creates a fault-injecting decorator over <paramref name="inner"/>.</summary>
    /// <param name="inner">The store every member forwards to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public FaultInjectingStorage(IStorage<T> inner)
        : base(inner)
    {
    }

    /// <summary>
    /// Fails the next <paramref name="calls"/> calls of <paramref name="member"/>, after the failures
    /// already scripted for it.
    /// </summary>
    /// <param name="member">The member to fail.</param>
    /// <param name="timing">Before the wrapped store is called, or after it did the work.</param>
    /// <param name="calls">How many calls fail. At least one.</param>
    /// <param name="failure">
    /// Creates the exception of each failing call; null for a <see cref="TransientStorageException"/>
    /// whose <see cref="TransientStorageException.MayHaveCommitted"/> is false for
    /// <see cref="FaultTiming.Before"/> and true for <see cref="FaultTiming.After"/>.
    /// </param>
    /// <returns>This decorator, to chain scripts.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="calls"/> is zero or negative.</exception>
    public FaultInjectingStorage<T> FailNext(StorageMember member, FaultTiming timing, int calls = 1, Func<Exception>? failure = null)
    {
        if (calls < 1)
            throw new ArgumentOutOfRangeException(nameof(calls), calls, "At least one call must fail.");
        lock (gate)
        {
            if (!script.TryGetValue(member, out var faults))
                script[member] = faults = [];
            faults.Add(new Fault(timing, calls, failure ?? (() => Transient(member, timing))));
        }
        return this;
    }

    /// <summary>How many scripted failures of <paramref name="member"/> have not been raised yet.</summary>
    /// <param name="member">The member.</param>
    /// <returns>The number of calls that will still fail.</returns>
    public int Pending(StorageMember member)
    {
        lock (gate)
        {
            var pending = 0;
            if (script.TryGetValue(member, out var faults))
            {
                foreach (var fault in faults)
                    pending += fault.Remaining;
            }
            return pending;
        }
    }

    /// <summary>Drops every scripted failure that has not been raised.</summary>
    public void Clear()
    {
        lock (gate)
            script.Clear();
    }

    /// <inheritdoc />
    protected override ValueTask OnCallingAsync(StorageMember member, CancellationToken ct)
    {
        if (Take(member, FaultTiming.Before) is { } failure)
            throw failure;
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask OnCalledAsync(StorageMember member, CancellationToken ct)
    {
        if (Take(member, FaultTiming.After) is { } failure)
            throw failure;
        return default;
    }

    // The next scripted failure of the member, when it is due at this point of the call.
    private Exception? Take(StorageMember member, FaultTiming timing)
    {
        Func<Exception> create;
        lock (gate)
        {
            if (!script.TryGetValue(member, out var faults) || faults.Count == 0 || faults[0].Timing != timing)
                return null;
            var fault = faults[0];
            create = fault.Create;
            if (--fault.Remaining == 0)
                faults.RemoveAt(0);
        }
        return create() ?? throw new InvalidOperationException("The scripted failure factory returned null.");
    }

    private static TransientStorageException Transient(StorageMember member, FaultTiming timing) =>
        timing == FaultTiming.Before
            ? new TransientStorageException(
                $"Injected failure: {member} did not reach the store.", mayHaveCommitted: false, retryAfter: null, innerException: null)
            : new TransientStorageException(
                $"Injected failure: {member} reached the store, and its response was lost.", mayHaveCommitted: true, retryAfter: null, innerException: null);

    private sealed class Fault(FaultTiming timing, int remaining, Func<Exception> create)
    {
        public FaultTiming Timing { get; } = timing;

        public int Remaining { get; set; } = remaining;

        public Func<Exception> Create { get; } = create;
    }
}
