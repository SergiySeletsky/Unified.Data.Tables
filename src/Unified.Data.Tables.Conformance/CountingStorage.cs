namespace Unified.Data.Tables.Conformance;

/// <summary>
/// A decorator that counts the calls of each member of the store it wraps, for tests that assert how
/// often code reaches storage ("the second read is served from my cache", "one batch, not a hundred
/// writes"). Thread-safe; resettable.
/// </summary>
/// <remarks>
/// A call is counted when it is made, whether or not the wrapped store then fails it; a stream is counted
/// when its enumeration starts. Conditional deletes and atomic batches made through the capability probe
/// are counted too (see <see cref="InterceptingStorage{T}"/>). Works over any <see cref="IStorage{T}"/>,
/// on every provider.
/// </remarks>
/// <typeparam name="T">The entity type.</typeparam>
public class CountingStorage<T> : InterceptingStorage<T> where T : class, IEntity, new()
{
#if NETSTANDARD2_0
    private static readonly StorageMember[] Members = (StorageMember[])Enum.GetValues(typeof(StorageMember));
#else
    private static readonly StorageMember[] Members = Enum.GetValues<StorageMember>();
#endif

    private readonly long[] counts = new long[Members.Length];

    /// <summary>Creates a counting decorator over <paramref name="inner"/>.</summary>
    /// <param name="inner">The store every member forwards to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public CountingStorage(IStorage<T> inner)
        : base(inner)
    {
    }

    /// <summary>How many calls of every member together were made since the last <see cref="Reset"/>.</summary>
    public long Total
    {
        get
        {
            long total = 0;
            for (var i = 0; i < counts.Length; i++)
                total += Interlocked.Read(ref counts[i]);
            return total;
        }
    }

    /// <summary>How many calls of <paramref name="member"/> were made since the last <see cref="Reset"/>.</summary>
    /// <param name="member">The member.</param>
    /// <returns>The number of calls.</returns>
    public long Count(StorageMember member) => Interlocked.Read(ref counts[(int)member]);

    /// <summary>The members called at least once since the last <see cref="Reset"/>, with their counts.</summary>
    /// <returns>A copy; later calls do not change it.</returns>
    public IReadOnlyDictionary<StorageMember, long> Snapshot()
    {
        var snapshot = new Dictionary<StorageMember, long>();
        foreach (var member in Members)
        {
            var count = Count(member);
            if (count != 0)
                snapshot[member] = count;
        }
        return snapshot;
    }

    /// <summary>Sets every count back to zero.</summary>
    public void Reset()
    {
        for (var i = 0; i < counts.Length; i++)
            Interlocked.Exchange(ref counts[i], 0);
    }

    /// <inheritdoc />
    protected override ValueTask OnCallingAsync(StorageMember member, CancellationToken ct)
    {
        Interlocked.Increment(ref counts[(int)member]);
        return default;
    }
}
