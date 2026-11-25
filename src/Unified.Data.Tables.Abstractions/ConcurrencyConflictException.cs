namespace Unified.Data.Tables;

/// <summary>
/// Thrown when an optimistic-concurrency check fails: the row was modified (or deleted) by another
/// writer between the read that produced the caller's <see cref="Entity.ETag"/> and the write.
/// Raised by <see cref="ConcurrencyMode.Strict"/> updates, <see cref="ConcurrencyMode.Auto"/>
/// updates with a caller-supplied ETag, and ETag-conditional partial updates
/// (<see cref="UpdateBuilder{T}.WithETag"/>). Provider-agnostic: the storage backend's own exception
/// (e.g. an Azure 412) is preserved as <see cref="Exception.InnerException"/> when the backend refused
/// the write; a conflict the store finds in its own read of the row has none.
/// For a large-value aware type (one whose declared types have a <see cref="TableValueSplitAttribute"/>
/// or <see cref="TableBlobContent"/> member) it is also thrown by <c>UpsertAsync</c>, a
/// <see cref="ConcurrencyMode.LastWriterWins"/> update, <c>DeleteAsync</c> and <c>UpsertBatchAsync</c>
/// that lost their condition to concurrent writers five times; by <c>CreateAsync</c> and
/// <c>CreateBatchAsync</c> only while they take back leftover continuation rows that keep changing or
/// vanishing; and by a read (a point read, a query, a stream or a page) whose row another writer
/// replaced during each of its five attempts, so that its continuation rows or blobs belonged to
/// another version every time. Nothing stored is damaged then and the read can be repeated; its
/// <see cref="Exception.InnerException"/> is the <see cref="TableValueIntegrityException"/> of the
/// last attempt, and <see cref="EntityType"/> and <see cref="Id"/> are null.
/// </summary>
/// <remarks>
/// The canonical recovery is re-read → re-apply → retry, which
/// <c>StorageExtensions.MutateAsync</c> packages up; HTTP surfaces typically map this to 409.
/// </remarks>
public class ConcurrencyConflictException : StorageException
{
    /// <summary>
    /// Whether the change that failed the version check may be this very call's own write. True unless
    /// the provider observed that no earlier attempt of this request was sent: a provider that retries
    /// (the Azure SDK does, after a timeout or a dropped connection) can have an attempt applied whose
    /// response was lost, and its next attempt then fails the condition against the version that attempt
    /// wrote. False means only that no such retry was seen in this call, not that another writer is
    /// proven: a caller's own retry of the whole call looks the same as a foreign write. False too for a
    /// conflict a read reports, for one the store finds in its own read before it sent a write, and on a
    /// store that never retries (the in-memory one).
    /// </summary>
    public bool MayBeOwnWrite { get; set; }

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
