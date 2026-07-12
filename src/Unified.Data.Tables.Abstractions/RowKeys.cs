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

    // Separates a sub-stream discriminator from the ticks segment. '~' (U+007E) sorts above every
    // digit and ASCII letter, so a bare-partition stream and its "{sub}~..." sub-streams never
    // interleave under a RowKey-prefix scan.
    private const char SubStreamSeparator = '~';

    /// <summary>
    /// Builds a time-ordered append RowKey: <c>[{subStream}~]{invertedTicks}[-{uniquifier}]</c>.
    /// Uses <see cref="InvertedTicks"/> so later events sort lexically FIRST; the optional
    /// <paramref name="subStream"/> isolates one stream within a partition (read back via
    /// <see cref="SubStreamPrefix"/>), and the optional <paramref name="uniquifier"/> disambiguates
    /// events written within the same 100&#8209;ns tick.
    /// </summary>
    public static string AppendKey(DateTimeOffset timestamp, string? subStream = null, string? uniquifier = null)
    {
        var prefix = string.IsNullOrEmpty(subStream) ? string.Empty : SubStreamPrefix(subStream!);
        var suffix = string.IsNullOrEmpty(uniquifier) ? string.Empty : "-" + uniquifier;
        return prefix + InvertedTicks(timestamp) + suffix;
    }

    /// <summary>
    /// The RowKey prefix that isolates one sub-stream — pass as
    /// <see cref="QueryOptions.RowKeyPrefix"/> to read only that stream's events.
    /// </summary>
    public static string SubStreamPrefix(string subStream)
    {
        Guard.NotNull(subStream, nameof(subStream));
        return subStream + SubStreamSeparator;
    }

    /// <summary>
    /// Decodes a RowKey produced by <see cref="AppendKey"/> back into its timestamp and sub-stream.
    /// Returns <c>false</c> (and default outputs) when the key is not in append format.
    /// </summary>
    public static bool TryParseAppendKey(string rowKey, out DateTimeOffset timestamp, out string? subStream)
    {
        timestamp = default;
        subStream = null;
        if (string.IsNullOrEmpty(rowKey))
            return false;

        var rest = rowKey;
        // Split on the LAST '~': the ticks (digits) and uniquifier (hex) never contain '~', so the
        // final '~' is the real boundary even when the sub-stream itself contains one.
        var sep = rowKey.LastIndexOf(SubStreamSeparator);
        if (sep >= 0)
        {
            subStream = rowKey.Substring(0, sep);
            rest = rowKey.Substring(sep + 1);
        }

        if (rest.Length < 19)
        {
            subStream = null;
            return false;
        }

        var ticksPart = rest.Substring(0, 19);
        for (var i = 0; i < 19; i++)
        {
            if (!char.IsDigit(ticksPart[i]))
            {
                subStream = null;
                return false;
            }
        }

        try
        {
            timestamp = FromInvertedTicks(ticksPart);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            subStream = null;
            return false;
        }
    }
}
