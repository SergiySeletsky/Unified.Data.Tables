namespace Unified.Data.Tables.Conformance;

/// <summary>
/// Describes the store under test to the kit: how to build a fresh one, and where it is knowingly not
/// conforming. Implement it once per store (a provider, a decorator over one, a hand-written fake) and
/// hand it to <see cref="ConformanceScenario.RunAsync"/> or <see cref="ConformanceSuite.RunAllAsync(IConformanceStoreFactory, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// The kit owns the entity types (<see cref="ConformanceItem"/>, <see cref="ConformanceLargeItem"/>,
/// <see cref="ConformanceEvent"/>), so the factory only needs to build an <see cref="IStorage{T}"/> of
/// whatever <c>T</c> it is asked for.
/// <para>
/// <b>Isolation.</b> Every call returns a store no other call shares rows with: a new in-memory instance,
/// a new table, a new database file. A scenario writes, counts and deletes as if the table were its own,
/// and several scenarios may run at once. What the store allocated is released when the returned
/// <see cref="ConformanceStore{T}"/> is disposed, which the kit does at the end of each scenario.
/// </para>
/// </remarks>
public interface IConformanceStoreFactory
{
    /// <summary>The name of the store under test, for messages (<c>"InMemory"</c>, <c>"AzureTables"</c>).</summary>
    string Name { get; }

    /// <summary>
    /// The named leniencies this store declares: a scenario that fails and is covered by one of them is
    /// reported as <see cref="ConformanceOutcome.Lenient"/> instead of failing. A leniency is one of the
    /// <see cref="ConformanceLeniencies"/> names (each covers the scenarios about one rule), or the
    /// <see cref="ConformanceScenario.Name"/> of a single scenario. A conforming store returns an empty
    /// collection.
    /// </summary>
    IReadOnlyCollection<string> Leniencies { get; }

    /// <summary>
    /// Builds a fresh, isolated store of <typeparamref name="T"/>, configured as
    /// <paramref name="settings"/> asks.
    /// </summary>
    /// <typeparam name="T">A kit-owned entity type.</typeparam>
    /// <param name="settings">How the store is configured (id normalization, blob offload).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The store, with the optional raw-row reader and its clean-up.</returns>
    /// <exception cref="NotSupportedException">
    /// The store cannot be configured that way (it has no blob offload, it always normalizes ids). The
    /// scenario is then reported as <see cref="ConformanceOutcome.NotSupported"/>.
    /// </exception>
    Task<ConformanceStore<T>> CreateStoreAsync<T>(ConformanceStoreSettings settings, CancellationToken ct = default)
        where T : class, IEntity, new();

    /// <summary>
    /// Builds a fresh, isolated polymorphic store of <typeparamref name="TBase"/>.
    /// </summary>
    /// <typeparam name="TBase">A kit-owned base type.</typeparam>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The store with its clean-up.</returns>
    /// <exception cref="NotSupportedException">
    /// There is no polymorphic store to test. Every scenario of the polymorphic tier is then reported as
    /// <see cref="ConformanceOutcome.NotSupported"/>.
    /// </exception>
    Task<ConformancePolymorphicStore<TBase>> CreatePolymorphicStoreAsync<TBase>(CancellationToken ct = default)
        where TBase : class;
}

/// <summary>How a scenario asks for its store to be configured.</summary>
public sealed record ConformanceStoreSettings
{
    /// <summary>The settings most scenarios use: normalized ids, no blob offload.</summary>
    public static ConformanceStoreSettings Default { get; } = new();

    /// <summary>How the store treats the ids it is given. Default <see cref="Tables.IdNormalization.Normalized"/>.</summary>
    public IdNormalization IdNormalization { get; init; } = IdNormalization.Normalized;

    /// <summary>
    /// Whether the store must be able to move a value that is too large for table rows to a blob. When
    /// false (the default) the store is configured without blob offload.
    /// </summary>
    public bool BlobOffload { get; init; }
}

/// <summary>
/// One physical row as the store keeps it, for the scenarios that check the layout below the typed store
/// (continuation rows of a large value). Kit-owned, so that a factory needs no experimental API to
/// produce it.
/// </summary>
public sealed class ConformanceRawRow
{
    /// <summary>Creates a row.</summary>
    /// <param name="partitionKey">The partition key, exactly as stored.</param>
    /// <param name="rowKey">The row key, exactly as stored.</param>
    /// <param name="cells">The row's columns by name, without the keys, the timestamp and the ETag.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ConformanceRawRow(string partitionKey, string rowKey, IReadOnlyDictionary<string, object> cells)
    {
        PartitionKey = partitionKey ?? throw new ArgumentNullException(nameof(partitionKey));
        RowKey = rowKey ?? throw new ArgumentNullException(nameof(rowKey));
        Cells = cells ?? throw new ArgumentNullException(nameof(cells));
    }

    /// <summary>The partition key, exactly as stored.</summary>
    public string PartitionKey { get; }

    /// <summary>The row key, exactly as stored.</summary>
    public string RowKey { get; }

    /// <summary>The row's columns by name, without the keys, the timestamp and the ETag.</summary>
    public IReadOnlyDictionary<string, object> Cells { get; }
}

/// <summary>
/// Reads the physical rows of one partition of a store's table, continuation rows included, in any order.
/// </summary>
/// <param name="partitionKey">The partition key, exactly as stored.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>Every row stored in the partition.</returns>
public delegate Task<IReadOnlyList<ConformanceRawRow>> ConformanceRawRowReader(string partitionKey, CancellationToken ct);

/// <summary>A store built for one scenario, with what the factory knows about it.</summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class ConformanceStore<T> : IAsyncDisposable where T : class, IEntity, new()
{
    private readonly Func<ValueTask>? dispose;

    /// <summary>Wraps a store.</summary>
    /// <param name="storage">The store under test.</param>
    /// <param name="readRawRows">
    /// Reads the table's physical rows, or null when the factory cannot. Scenarios that check the layout
    /// below the typed store skip that part of their checks without it.
    /// </param>
    /// <param name="dispose">Releases what the factory allocated for the store (a table, a file), or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public ConformanceStore(IStorage<T> storage, ConformanceRawRowReader? readRawRows = null, Func<ValueTask>? dispose = null)
    {
        Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        ReadRawRows = readRawRows;
        this.dispose = dispose;
    }

    /// <summary>The store under test.</summary>
    public IStorage<T> Storage { get; }

    /// <summary>Reads the table's physical rows; null when the factory cannot.</summary>
    public ConformanceRawRowReader? ReadRawRows { get; }

    /// <summary>Releases what the factory allocated for the store.</summary>
    /// <returns>A task that completes when the clean-up is done.</returns>
    public ValueTask DisposeAsync() => dispose is null ? default : dispose();
}

/// <summary>A polymorphic store built for one scenario.</summary>
/// <typeparam name="TBase">The base type of the store.</typeparam>
public sealed class ConformancePolymorphicStore<TBase> : IAsyncDisposable where TBase : class
{
    private readonly Func<ValueTask>? dispose;

    /// <summary>Wraps a store.</summary>
    /// <param name="storage">The store under test.</param>
    /// <param name="dispose">Releases what the factory allocated for the store (a table, a file), or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public ConformancePolymorphicStore(IPolymorphicStorage<TBase> storage, Func<ValueTask>? dispose = null)
    {
        Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.dispose = dispose;
    }

    /// <summary>The store under test.</summary>
    public IPolymorphicStorage<TBase> Storage { get; }

    /// <summary>Releases what the factory allocated for the store.</summary>
    /// <returns>A task that completes when the clean-up is done.</returns>
    public ValueTask DisposeAsync() => dispose is null ? default : dispose();
}
