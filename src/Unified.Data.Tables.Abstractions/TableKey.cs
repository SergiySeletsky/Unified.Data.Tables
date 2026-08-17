namespace Unified.Data.Tables;

/// <summary>
/// An explicit Azure Tables row address. <see cref="IStorage{T}"/> derives its keys from
/// <see cref="IEntity.Id"/>; a polymorphic table cannot, because its rows are ordered by things the
/// stored object does not carry — an aggregate version, an inverted tick count, an ambient
/// transaction id, or a literal marker key. Passing the pair explicitly IS the key strategy: the
/// caller computes it, so every scheme is expressible and none needs a hook.
/// </summary>
/// <remarks>
/// Keys are used <b>verbatim</b>. <see cref="IdNormalization"/> is never applied to a
/// <see cref="TableKey"/>, and that is deliberate rather than an oversight: a polymorphic row key is
/// frequently a case-sensitive payload (a base-32 id, a zero-padded tick count), and
/// <see cref="EntityId.Normalize"/> would lower-case it into a <em>different row</em>, silently
/// orphaning existing data.
/// <para>
/// This type does not validate. Nothing in this library validates ids, and making this the one
/// exception would put the in-memory fake and <see cref="Entity"/> under different rules. The
/// storage implementations reject null and empty keys at the boundary instead.
/// </para>
/// </remarks>
/// <param name="PartitionKey">The row's partition key, verbatim.</param>
/// <param name="RowKey">The row's row key, verbatim.</param>
public readonly record struct TableKey(string PartitionKey, string RowKey)
{
    /// <summary>
    /// Bridges the <see cref="IStorage{T}"/> composite-id convention: splits on the FIRST
    /// <c>'|'</c>, so a row key may itself contain <c>'|'</c>.
    /// </summary>
    /// <param name="id">A composite <c>"{PartitionKey}|{RowKey}"</c> id, or a single-segment id.</param>
    /// <returns>The address that id refers to.</returns>
    public static TableKey FromId(string id)
    {
        var split = EntityId.Split(id);
        return new TableKey(split.PartitionKey, split.RowKey);
    }

    /// <summary>
    /// The composite id addressing this row, in <see cref="EntityId"/>'s canonical spelling —
    /// the single-segment form when both keys are equal, so <c>"a|a"</c> and <c>"a"</c> agree.
    /// </summary>
    /// <returns>The composite id.</returns>
    public string ToId() =>
        string.Equals(PartitionKey, RowKey, StringComparison.Ordinal)
            ? PartitionKey
            : EntityId.Combine(PartitionKey, RowKey);

    /// <inheritdoc />
    public override string ToString() => ToId();
}
