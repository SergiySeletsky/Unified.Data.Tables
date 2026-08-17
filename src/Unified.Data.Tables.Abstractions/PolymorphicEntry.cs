namespace Unified.Data.Tables;

/// <summary>
/// One row read back: its address, the materialized object (typed as <typeparamref name="TBase"/>
/// but the TRUE derived instance), its discriminator, and its raw cells.
/// </summary>
/// <remarks>
/// <see cref="Item"/> is null exactly when the row carries no discriminator — a deliberate marker
/// row. A discriminator that is PRESENT but unresolvable or not assignable to
/// <typeparamref name="TBase"/> is an error and throws during materialization; it is never quietly
/// downgraded to null. "No type was ever written" and "the wrong type was written" are different
/// failures and must not look alike.
/// </remarks>
/// <typeparam name="TBase">The common base type every row materializes as.</typeparam>
public sealed class PolymorphicEntry<TBase>
    where TBase : class
{
    /// <summary>Creates an entry. Implementations construct these; callers read them.</summary>
    /// <param name="key">The row address.</param>
    /// <param name="item">The materialized object, or null for a marker row.</param>
    /// <param name="discriminator">The stored discriminator, or null for a marker row.</param>
    /// <param name="etag">The row's ETag, when the backend reported one.</param>
    /// <param name="timestamp">The service's last-write time.</param>
    /// <param name="columns">The row's raw cells.</param>
    public PolymorphicEntry(
        TableKey key,
        TBase? item,
        string? discriminator,
        string? etag,
        DateTimeOffset? timestamp,
        IReadOnlyDictionary<string, object> columns)
    {
        Guard.NotNull(columns, nameof(columns));
        Key = key;
        Item = item;
        Discriminator = discriminator;
        ETag = etag;
        Timestamp = timestamp;
        Columns = columns;
    }

    /// <summary>The row's address.</summary>
    public TableKey Key { get; }

    /// <summary>The materialized object, or null for a typeless marker row.</summary>
    public TBase? Item { get; }

    /// <summary>The stored discriminator value, or null for a typeless marker row.</summary>
    public string? Discriminator { get; }

    /// <summary>The row's ETag, or null when the backend did not report one.</summary>
    public string? ETag { get; }

    /// <summary>The service's last-write time, or null when the backend did not report one.</summary>
    public DateTimeOffset? Timestamp { get; }

    /// <summary>
    /// Every cell on the row except <c>PartitionKey</c>, <c>RowKey</c>, <c>Timestamp</c> and
    /// <c>odata.etag</c>, exactly as stored — including format suffixes (<c>Tags__Json</c>) and
    /// system columns. This is the raw property bag, not a property view.
    /// </summary>
    public IReadOnlyDictionary<string, object> Columns { get; }

    /// <summary>
    /// <see cref="Item"/>, throwing when the row is a marker. Use this when the call site knows the
    /// row is typed; a marker slipping through as null is a bug worth an exception, not a
    /// <see cref="NullReferenceException"/> three frames later.
    /// </summary>
    /// <exception cref="InvalidOperationException">The row is a typeless marker row.</exception>
    public TBase Value =>
        Item ?? throw new InvalidOperationException(PolymorphicMessages.MarkerHasNoValue(Key));

    /// <summary>Reads a raw column, strictly.</summary>
    /// <typeparam name="TValue">The stored cell type.</typeparam>
    /// <param name="name">The column name.</param>
    /// <returns>The cell value.</returns>
    /// <exception cref="KeyNotFoundException">The column is absent.</exception>
    /// <exception cref="InvalidCastException">The cell is not a <typeparamref name="TValue"/>.</exception>
    public TValue Column<TValue>(string name)
    {
        Guard.NotNull(name, nameof(name));
        if (!Columns.TryGetValue(name, out var raw))
            throw new KeyNotFoundException($"Row '{Key}' has no column '{name}'.");

        return (TValue)raw;
    }

    /// <summary>Reads a raw column, tolerantly — the right accessor for an optional sentinel.</summary>
    /// <typeparam name="TValue">The stored cell type.</typeparam>
    /// <param name="name">The column name.</param>
    /// <param name="value">The cell value, or default when absent or of another type.</param>
    /// <returns>True when the column exists and is a <typeparamref name="TValue"/>.</returns>
    public bool TryColumn<TValue>(string name, out TValue value)
    {
        Guard.NotNull(name, nameof(name));
        if (Columns.TryGetValue(name, out var raw) && raw is TValue typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }
}
