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
public class DuplicateKeyException : Exception
{
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
