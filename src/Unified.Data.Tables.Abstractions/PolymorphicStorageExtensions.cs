namespace Unified.Data.Tables;

/// <summary>
/// Convenience over <see cref="IPolymorphicStorage{TBase}"/>. Kept outside the interface — as with
/// <c>StorageExtensions</c> and <c>AppendLogExtensions</c> — so both implementations stay thin and
/// neither can drift from the other on sugar.
/// </summary>
public static class PolymorphicStorageExtensions
{
    /// <summary>Insert a typed row without hand-building a <see cref="PolymorphicWrite{TBase}"/>.</summary>
    /// <typeparam name="TBase">The store's base type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="key">The row address.</param>
    /// <param name="item">The object to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The written row, read back as an entry.</returns>
    public static Task<PolymorphicEntry<TBase>> InsertAsync<TBase>(
        this IPolymorphicStorage<TBase> storage, TableKey key, TBase item, CancellationToken ct = default)
        where TBase : class
    {
        Guard.NotNull(storage, nameof(storage));
        return storage.InsertAsync(new PolymorphicWrite<TBase>(key, item), ct);
    }

    /// <summary>Upsert a typed row without hand-building a <see cref="PolymorphicWrite{TBase}"/>.</summary>
    /// <typeparam name="TBase">The store's base type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="key">The row address.</param>
    /// <param name="item">The object to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The written row, read back as an entry.</returns>
    public static Task<PolymorphicEntry<TBase>> UpsertAsync<TBase>(
        this IPolymorphicStorage<TBase> storage, TableKey key, TBase item, CancellationToken ct = default)
        where TBase : class
    {
        Guard.NotNull(storage, nameof(storage));
        return storage.UpsertAsync(new PolymorphicWrite<TBase>(key, item), ct);
    }

    /// <summary>Insert a typeless marker row carrying only system columns.</summary>
    /// <typeparam name="TBase">The store's base type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="key">The row address.</param>
    /// <param name="columns">The system columns to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The written row, read back as an entry with a null <c>Item</c>.</returns>
    public static Task<PolymorphicEntry<TBase>> InsertMarkerAsync<TBase>(
        this IPolymorphicStorage<TBase> storage,
        TableKey key,
        IReadOnlyDictionary<string, object> columns,
        CancellationToken ct = default)
        where TBase : class
    {
        Guard.NotNull(storage, nameof(storage));
        return storage.InsertAsync(PolymorphicWrite<TBase>.Marker(key, columns), ct);
    }

    /// <summary>
    /// The derived instances of one concrete type from a mixed read, skipping marker rows. The
    /// point of a polymorphic store is that the runtime type survives the round trip; this is how a
    /// caller uses it.
    /// </summary>
    /// <typeparam name="TBase">The store's base type.</typeparam>
    /// <typeparam name="TDerived">The concrete type to select.</typeparam>
    /// <param name="entries">Entries from a read.</param>
    /// <returns>The matching derived instances.</returns>
    public static IEnumerable<TDerived> ItemsOfType<TBase, TDerived>(
        this IEnumerable<PolymorphicEntry<TBase>> entries)
        where TBase : class
        where TDerived : class, TBase
    {
        Guard.NotNull(entries, nameof(entries));
        foreach (var entry in entries)
        {
            if (entry.Item is TDerived derived)
                yield return derived;
        }
    }
}
