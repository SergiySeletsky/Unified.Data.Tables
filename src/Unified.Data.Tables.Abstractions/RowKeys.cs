using System.Globalization;

namespace Unified.Data.Tables;

/// <summary>
/// RowKey encoding helpers. Azure Tables returns rows in lexical (PartitionKey, RowKey) order and
/// has no server-side ORDER BY — any ordering you need must be encoded into the RowKey itself.
/// </summary>
public static class RowKeys
{
    /// <summary>
    /// Encodes a timestamp so that LATER times sort lexically FIRST (fixed-width inverted ticks).
    /// The canonical "most recent N" pattern: write rows with
    /// <c>Id = $"{partition}|{RowKeys.InvertedTicks(now)}-{suffix}"</c>, then read
    /// <c>QueryAsync(new QueryOptions {{ Partition = partition, Take = n }})</c> — no client-side
    /// sorting, no full-partition scan.
    /// </summary>
    public static string InvertedTicks(DateTimeOffset timestamp) =>
        (DateTimeOffset.MaxValue.UtcTicks - timestamp.UtcTicks).ToString("D19", CultureInfo.InvariantCulture);

    /// <summary>Decodes a value produced by <see cref="InvertedTicks"/> back into the UTC timestamp.</summary>
    public static DateTimeOffset FromInvertedTicks(string invertedTicks)
    {
        Guard.NotNull(invertedTicks, nameof(invertedTicks));
        var inverted = long.Parse(invertedTicks, NumberStyles.None, CultureInfo.InvariantCulture);
        return new DateTimeOffset(DateTimeOffset.MaxValue.UtcTicks - inverted, TimeSpan.Zero);
    }

    /// <summary>
    /// Encodes a timestamp so that EARLIER times sort lexically first (fixed-width ticks) — for
    /// chronological streams read oldest-first.
    /// </summary>
    public static string Ticks(DateTimeOffset timestamp) =>
        timestamp.UtcTicks.ToString("D19", CultureInfo.InvariantCulture);
}
