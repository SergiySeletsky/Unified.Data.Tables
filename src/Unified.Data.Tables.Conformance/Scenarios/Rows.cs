namespace Unified.Data.Tables.Conformance.Scenarios;

/// <summary>What the scenario files share: entity builders and small readers.</summary>
internal static class Rows
{
    /// <summary>A token that is already cancelled.</summary>
    public static CancellationToken Cancelled { get; } = new(canceled: true);

    /// <summary>How far a storage-layer stamp may lie from the clock the kit reads around the call.</summary>
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(1);

    public static ConformanceItem Item(string id, string? name = null, int number = 0) =>
        new() { Id = id, Name = name, Number = number };

    public static ConformanceLargeItem Large(string id, string? body, string? title = null) =>
        new() { Id = id, Body = body, Title = title };

    public static ConformanceOrderPlaced Placed(string subject, int quantity = 1) =>
        new() { Subject = subject, Sequence = quantity, Customer = "customer of " + subject, Quantity = quantity };

    public static ConformanceOrderShipped Shipped(string subject) =>
        new() { Subject = subject, Sequence = 7, Carrier = "carrier of " + subject, ShippedAt = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero) };

    /// <summary>A text of <paramref name="length"/> characters that differs by position, so a lost or swapped chunk shows.</summary>
    public static string Text(int length, int seed = 0)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)('a' + ((i + seed + (i / 977)) % 26));
        return new string(chars);
    }

    public static byte[] Bytes(int length, int seed = 0)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)((i * 31) + seed + (i / 251));
        return bytes;
    }

    public static async Task<List<T>> ListAsync<T>(IAsyncEnumerable<T> source, CancellationToken ct)
    {
        var items = new List<T>();
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
            items.Add(item);
        return items;
    }

    public static List<string> Ids<T>(IEnumerable<T> entities) where T : IEntity
    {
        var ids = new List<string>();
        foreach (var entity in entities)
            ids.Add(entity.Id);
        return ids;
    }

    /// <summary>
    /// The row as the table holds it now, or null: read through a bounded query, which is never answered
    /// from a read cache (OneAsync may be, with the version the store's own last write left there).
    /// </summary>
    public static async Task<T?> StoredAsync<T>(IStorage<T> store, string id, CancellationToken ct) where T : class, IEntity, new()
    {
        var (partitionKey, rowKey) = EntityId.Split(id);
        foreach (var entity in await store.QueryAsync(new QueryOptions { Partition = partitionKey, RowKeyPrefix = rowKey }, ct).ConfigureAwait(false))
        {
            if (string.Equals(entity.Id, id, StringComparison.Ordinal))
                return entity;
        }
        return null;
    }

    /// <summary>Every row of a paged query, with the pages checked against the page size.</summary>
    public static async Task<List<T>> PageAllAsync<T>(IStorage<T> store, QueryOptions options, CancellationToken ct)
        where T : class, IEntity, new()
    {
        var size = options.Take ?? 100;
        var all = new List<T>();
        string? token = null;
        for (var pages = 0; ; pages++)
        {
            if (pages > 10_000)
                throw new ConformanceAssertionException("expected paging to end with a null continuation token, but it went on for 10,000 pages");
            var page = await store.QueryPageAsync(options with { ContinuationToken = token }, ct).ConfigureAwait(false);
            Check.NotNull(page, "a page");
            Check.True(page.Items.Count <= size, $"a page holds at most the page size ({size}) rows; it holds {page.Items.Count}");
            Check.Equal(page.ContinuationToken is not null, page.HasMore, "HasMore of a page");
            all.AddRange(page.Items);
            token = page.ContinuationToken;
            if (token is null)
                return all;
        }
    }

    /// <summary>A stamp the storage layer set during a call that ran between <paramref name="before"/> and <paramref name="after"/>.</summary>
    public static void StampedBetween(DateTimeOffset before, DateTimeOffset after, DateTimeOffset stamp, string what)
    {
        if (stamp < before - ClockTolerance || stamp > after + ClockTolerance)
            throw new ConformanceAssertionException(
                $"expected {what} to be stamped by the write (between {Check.Show(before)} and {Check.Show(after)}), but it is {Check.Show(stamp)}");
    }

    /// <summary>The physical row keys of a partition in ordinal order, or null when the factory cannot read raw rows.</summary>
    public static async Task<List<string>?> RawKeysAsync<T>(ConformanceStore<T> store, string partitionKey, CancellationToken ct)
        where T : class, IEntity, new()
    {
        if (store.ReadRawRows is null)
            return null;
        var rows = await store.ReadRawRows(partitionKey, ct).ConfigureAwait(false);
        var keys = new List<string>();
        foreach (var row in rows)
            keys.Add(row.RowKey);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}
