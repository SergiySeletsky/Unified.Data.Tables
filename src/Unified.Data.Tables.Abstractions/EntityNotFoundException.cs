namespace Unified.Data.Tables;

/// <summary>
/// Thrown when a write that needs an existing row finds none: an update (whole-entity or
/// <see cref="UpdateBuilder{T}"/>) or a polymorphic <c>MergeColumnsAsync</c> of a row that does not exist
/// or was deleted before the write arrived. Provider-agnostic: the storage backend's own exception (e.g.
/// an Azure 404) is preserved as <see cref="Exception.InnerException"/>.
/// </summary>
/// <remarks>
/// Reads never throw it (a missing row reads as <c>null</c>, <c>false</c> or an empty result) and neither
/// does a delete (deleting a missing row is a no-op). A missing <em>table</em> is a different failure:
/// <see cref="TableNotFoundException"/>. For flows where a row deleted by another writer is an expected
/// outcome, <c>StorageExtensions.TryMutateAsync</c> and <c>TryTransitionAsync</c> report it as
/// <see cref="MutationStatus.NotFound"/> instead.
/// </remarks>
public class EntityNotFoundException : StorageException
{
    /// <summary>The entity type name (usually the table name), when known.</summary>
    public string? EntityType { get; }

    /// <summary>The composite id of the missing row, when known.</summary>
    public string? Id { get; }

    /// <summary>Creates the exception with a generic message.</summary>
    public EntityNotFoundException()
        : base("Entity not found: no row exists at this id.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public EntityNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public EntityNotFoundException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a specific entity, preserving the provider exception.</summary>
    public EntityNotFoundException(string entityType, string id, Exception? innerException)
        : base($"{entityType} '{id}' not found: no row exists at this id.", innerException)
    {
        EntityType = entityType;
        Id = id;
    }
}
