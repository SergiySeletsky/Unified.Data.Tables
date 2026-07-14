namespace Unified.Data.Tables;

/// <summary>
/// Contract for an append-only, per-stream versioned snapshot — see
/// <see cref="VersionedStreamExtensions"/>. The stream id is the PartitionKey; the
/// <see cref="Version"/> becomes the RowKey via <see cref="RowKeys.VersionKey"/> (inverted, so the
/// newest version sorts first and "latest" is a single bounded read).
/// </summary>
public interface IVersionedEntity : IEntity
{
    /// <summary>The monotonic, non-negative per-stream version of this snapshot.</summary>
    int Version { get; set; }
}

/// <summary>
/// Convenience base for versioned snapshots — <see cref="Entity"/> plus <see cref="Version"/>.
/// Models that cannot take the base class implement <see cref="IVersionedEntity"/> directly.
/// </summary>
public abstract class VersionedEntity : Entity, IVersionedEntity
{
    /// <inheritdoc />
    public int Version { get; set; }
}
