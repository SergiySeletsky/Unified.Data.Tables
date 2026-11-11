namespace Unified.Data.Tables;

/// <summary>
/// Thrown when an optimistic-concurrency check fails: the row was modified (or deleted) by another
/// writer between the read that produced the caller's <see cref="Entity.ETag"/> and the write.
/// Raised by <see cref="ConcurrencyMode.Strict"/> updates, <see cref="ConcurrencyMode.Auto"/>
/// updates with a caller-supplied ETag, and ETag-conditional partial updates
/// (<see cref="UpdateBuilder{T}.WithETag"/>). Provider-agnostic — the storage backend's own
/// exception (e.g. an Azure 412) is preserved as <see cref="Exception.InnerException"/>.
/// For a type with large values (a <see cref="TableValueSplitAttribute"/> or
/// <see cref="TableBlobContent"/> property) it is also thrown by a write, delete or batch that lost
/// its condition to concurrent writers five times, and by a read (a point read, a query, a stream or a
/// page) whose row another writer replaced during each of its five attempts, so that its continuation
/// rows or blobs belonged to another version every time. Nothing stored is damaged then and the read
/// can be repeated; its <see cref="Exception.InnerException"/> is the
/// <see cref="TableValueIntegrityException"/> of the last attempt.
/// </summary>
/// <remarks>
/// The canonical recovery is re-read → re-apply → retry, which
/// <c>StorageExtensions.MutateAsync</c> packages up; HTTP surfaces typically map this to 409.
/// </remarks>
public class ConcurrencyConflictException : Exception
{
    /// <summary>The entity type name (usually the table name), when known.</summary>
    public string? EntityType { get; }

    /// <summary>The composite id of the contested row, when known.</summary>
    public string? Id { get; }

    /// <summary>Creates the exception with a generic message.</summary>
    public ConcurrencyConflictException()
        : base("Concurrency conflict: the row was modified by another writer since it was read.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public ConcurrencyConflictException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a specific entity, preserving the provider exception.</summary>
    public ConcurrencyConflictException(string entityType, string id, Exception? innerException)
        : base($"Concurrency conflict updating {entityType} '{id}': the row was modified by another writer since it was read.",
            innerException)
    {
        EntityType = entityType;
        Id = id;
    }
}
