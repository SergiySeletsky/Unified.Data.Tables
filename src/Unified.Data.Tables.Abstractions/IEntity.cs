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
    DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp bumped on every write.</summary>
    DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Azure Tables row version used for optimistic concurrency. Populated on read and
    /// round-tripped back on update. <c>null</c> until the entity has been persisted or read.
    /// </summary>
    string? ETag { get; set; }

    /// <summary>
    /// The service-managed last-write time of the row, as reported by Azure Tables. Populated on
    /// read; reset to <c>null</c> on write (write responses do not include it, so the value is
    /// unknown until the next read). Unlike <see cref="UpdatedAt"/>, this is bumped by ANY storage
    /// write — including migrations and backfills — and can never be set by the client, so it
    /// reflects storage truth rather than domain modification time.
    /// </summary>
    DateTimeOffset? Timestamp { get; set; }
}
