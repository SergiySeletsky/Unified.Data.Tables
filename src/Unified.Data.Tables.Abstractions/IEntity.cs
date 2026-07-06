namespace Unified.Data.Tables;

/// <summary>
/// Contract for entities persisted through <see cref="IStorage{T}"/>. The <see cref="Id"/>
/// is a composite <c>"{PartitionKey}|{RowKey}"</c> string (see <see cref="Entity"/> for the
/// key convention and how it is split on write).
/// </summary>
public interface IEntity
{
    /// <summary>Composite identifier in the form <c>"{PartitionKey}|{RowKey}"</c>.</summary>
    string Id { get; set; }

    /// <summary>UTC timestamp set when the row is first created.</summary>
    DateTimeOffset Created { get; set; }

    /// <summary>UTC timestamp bumped on every write.</summary>
    DateTimeOffset Modified { get; set; }

    /// <summary>
    /// Azure Tables row version used for optimistic concurrency. Populated on read and
    /// round-tripped back on update. <c>null</c> until the entity has been persisted or read.
    /// </summary>
    string? ETag { get; set; }
}
