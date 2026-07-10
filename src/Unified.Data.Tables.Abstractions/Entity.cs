using System.ComponentModel.DataAnnotations;

namespace Unified.Data.Tables;

/// <summary>
/// Base class for all entities persisted via <see cref="IStorage{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Id convention:</b> <see cref="Id"/> is a composite string <c>"{PartitionKey}|{RowKey}"</c>
/// split on the first <c>'|'</c>. Everything left of the first separator becomes the Azure Tables
/// partition key; everything to the right becomes the row key (so a row key may itself contain
/// <c>'|'</c>, e.g. <c>"vision|execution|agent"</c> → partition <c>"vision"</c>, row
/// <c>"execution|agent"</c>). The storage layer normalizes ids on write
/// (<c>trim → replace spaces with '-' → ToLowerInvariant</c>).
/// </para>
/// <para>
/// <see cref="CreatedAt"/> is stamped on insert and <see cref="UpdatedAt"/> on every write, both by
/// the storage layer. <see cref="ETag"/> carries the Azure Tables row version for optimistic
/// concurrency and is excluded from row serialization by name, so it never becomes a column.
/// <see cref="Timestamp"/> is the service-managed last-write time — populated on read, reset to
/// <c>null</c> on write, and likewise never serialized as a column.
/// </para>
/// </remarks>
public abstract class Entity : IEntity, IEquatable<Entity>
{
    /// <inheritdoc />
    [Key]
    [DataType(DataType.Text)]
    public virtual string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    [DataType(DataType.DateTime)]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    [DataType(DataType.DateTime)]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public string? ETag { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? Timestamp { get; set; }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Entity);

    /// <summary>
    /// Two entities are equal when both are non-transient (have an <see cref="Id"/>), share the
    /// same <see cref="Id"/>, and one type is assignable to the other.
    /// </summary>
    public virtual bool Equals(Entity? other)
    {
        if (other == null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (!IsTransient(this) &&
            !IsTransient(other) &&
            Equals(Id, other.Id))
        {
            var otherType = other.GetUnproxiedType();
            var thisType = GetUnproxiedType();
            return thisType.IsAssignableFrom(otherType) ||
                   otherType.IsAssignableFrom(thisType);
        }

        return false;
    }

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);

    private static bool IsTransient(Entity? obj) => obj != null && string.IsNullOrWhiteSpace(obj.Id);

    private Type GetUnproxiedType() => GetType();
}
