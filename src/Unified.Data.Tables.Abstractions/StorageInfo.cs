namespace Unified.Data.Tables;

/// <summary>What a store can promise about a write it has acknowledged.</summary>
public enum Durability
{
    /// <summary>The store does not say, or cannot know (an emulator, an unrecognized endpoint).</summary>
    Unknown,

    /// <summary>Held in memory only: gone when the process ends.</summary>
    Volatile,

    /// <summary>Handed to the operating system but not forced to disk: survives a process crash, not a power loss.</summary>
    OsBuffered,

    /// <summary>Forced to stable storage on one machine before the write is acknowledged.</summary>
    Fsynced,

    /// <summary>Stored on more than one machine before the write is acknowledged.</summary>
    Replicated,
}

/// <summary>
/// What a store says about itself: which provider it is, how durable an acknowledged write is, and the
/// limits it works within. Read with <see cref="StorageCapabilityExtensions.GetInfo{T}(IStorage{T})"/>,
/// which never throws and returns <see cref="Unknown"/> for a store that does not implement
/// <see cref="IStorageInfo"/>.
/// </summary>
/// <remarks>
/// The limits are what the library plans and refuses by, not a measurement of the endpoint: an emulator
/// may accept more, another service tier less. A null limit means the store states none.
/// <para>
/// Build one with an object initializer. Properties are added in later versions (each with a default that
/// means "not stated"), which is why there is no positional constructor.
/// </para>
/// </remarks>
public sealed record StorageInfo
{
    /// <summary>
    /// The info of a store that states nothing: every property at its default (no names,
    /// <see cref="Tables.Durability.Unknown"/>, no limits, nothing known about caching or transactions).
    /// </summary>
    public static StorageInfo Unknown { get; } = new();

    /// <summary>The provider's name (<c>"AzureTables"</c>, <c>"InMemory"</c>, <c>"Sqlite"</c>), or null when not stated.</summary>
    public string? ProviderName { get; init; }

    /// <summary>The physical table the store reads and writes, or null when not stated.</summary>
    public string? TableName { get; init; }

    /// <summary>What an acknowledged write survives; <see cref="Tables.Durability.Unknown"/> unless the provider can tell.</summary>
    public Durability Durability { get; init; }

    /// <summary>The most physical rows one transaction holds (100 on Azure Tables), or null when not stated.</summary>
    public int? MaxBatchRows { get; init; }

    /// <summary>The payload budget of one transaction in bytes, as the library plans it, or null when not stated.</summary>
    public long? MaxBatchBytes { get; init; }

    /// <summary>The most bytes one cell (one column of one physical row) holds, or null when not stated.</summary>
    public long? MaxCellBytes { get; init; }

    /// <summary>The most bytes one physical row holds, or null when not stated.</summary>
    public long? MaxRowBytes { get; init; }

    /// <summary>
    /// Whether a read can return a row as it was before a write another process made: true when the store
    /// has a read cache that is enabled for the type, false when every read goes to the backend, null
    /// when not stated.
    /// </summary>
    public bool? ReadCacheMayServeStaleRows { get; init; }

    /// <summary>
    /// Whether rows of one partition can be written in one all-or-nothing transaction (the basis of
    /// <see cref="IAtomicBatch{T}"/>), or null when not stated.
    /// </summary>
    public bool? SinglePartitionTransactions { get; init; }
}

/// <summary>
/// A store that describes itself. A side interface of <see cref="IStorage{T}"/> and
/// <see cref="IPolymorphicStorage{TBase}"/>; callers use
/// <see cref="StorageCapabilityExtensions.GetInfo{T}(IStorage{T})"/>, which returns
/// <see cref="StorageInfo.Unknown"/> for a store without it.
/// </summary>
public interface IStorageInfo
{
    /// <summary>
    /// What the store states about itself. Must not throw and must not call the backend: it is read on
    /// start-up paths and from diagnostics.
    /// </summary>
    /// <returns>The store's info; never null.</returns>
    StorageInfo GetInfo();
}
