namespace Unified.Data.Tables;

/// <summary>
/// Thrown by <see cref="IStorage{T}.CreateAsync"/>/<see cref="IStorage{T}.CreateBatchAsync"/> when
/// a row with the same composite id already exists. Provider-agnostic — the storage backend's own
/// exception (e.g. an Azure 409) is preserved as <see cref="Exception.InnerException"/>.
/// </summary>
/// <remarks>
/// For flows where "already there" is an expected outcome rather than an error, prefer
/// <c>StorageExtensions.GetOrCreateAsync</c> / <c>MutateOrCreateAsync</c>, which absorb this
/// exception and converge on the winner's row.
/// </remarks>
public class DuplicateKeyException : StorageException
{
    /// <summary>
    /// Whether the row that is in the way may be this very call's own write. True unless the provider
    /// observed that no earlier attempt of this request was sent: a provider that retries (the Azure SDK
    /// does, after a timeout or a dropped connection) can have an attempt applied whose response was lost,
    /// and its next attempt then finds the row that attempt created. False means only that no such retry
    /// was seen in this call, not that another writer is proven: a caller's own retry of the whole call
    /// (after a <see cref="TransientStorageException"/>, a redelivered message, a restart) looks the same
    /// as a foreign row. False too for a duplicate found before anything was sent (a key repeated within
    /// one batch) and on a store that never retries (the in-memory one).
    /// </summary>
    public bool MayBeOwnWrite { get; set; }

    /// <summary>The entity type name (usually the table name), when known.</summary>
    public string? EntityType { get; }

    /// <summary>The composite id that already exists, when known.</summary>
    public string? Id { get; }

    /// <summary>Creates the exception with a generic message.</summary>
    public DuplicateKeyException()
        : base("Duplicate key: a row with this id already exists.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public DuplicateKeyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public DuplicateKeyException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a specific entity, preserving the provider exception.</summary>
    public DuplicateKeyException(string entityType, string id, Exception? innerException)
        : base($"Duplicate key creating {entityType} '{id}': a row with this id already exists.", innerException)
    {
        EntityType = entityType;
        Id = id;
    }
}
