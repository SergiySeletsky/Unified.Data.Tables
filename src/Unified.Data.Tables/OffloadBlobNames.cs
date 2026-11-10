using System.ComponentModel;

namespace Unified.Data.Tables.Providers;

/// <summary>
/// Storage-provider SPI, not for application code. The naming of offloaded-value blobs: <c>{table}/{partitionKey}/{rowKey}/{column}/{sha256}</c>, each
/// segment escaped so that the Blobs SDK and the service address exactly that name.
/// </summary>
/// <remarks>
/// <para>
/// The SDK builds a blob's URI from its name and collapses the dot segments of that URI: a name
/// <c>t/p/./Body/x</c> is stored as <c>t/p/Body/x</c>, and <c>t/../r/Body/x</c> as <c>r/Body/x</c>,
/// outside the table's prefix. A <c>'%'</c> in a name, on the other hand, is sent escaped and stored as
/// written. So a segment is <see cref="Uri.EscapeDataString(string)"/> of its value with every
/// <c>'.'</c> also escaped (<c>%2E</c>), and an empty value is the segment <c>%</c>, which escaping
/// never produces. No segment is then empty, <c>.</c>, <c>..</c> or ends with a dot. Only
/// well-formed UTF-16 is escaped: <see cref="Uri.EscapeDataString(string)"/> writes an unpaired surrogate
/// as U+FFFD, the same segment as a real U+FFFD, so <see cref="EscapeSegment"/> refuses one. The encoding
/// is then injective: <see cref="TryUnescapeSegment"/> accepts exactly the segments
/// <see cref="EscapeSegment"/> writes.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class OffloadBlobNames
{
    /// <summary>The segment an empty value is written as; a lone <c>'%'</c> is never an escape.</summary>
    private const string EmptySegment = "%";

    private const int Sha256HexLength = 64;

    /// <summary>Storage-provider SPI, not for application code. The blob-name segment of <paramref name="value"/>.</summary>
    /// <param name="value">The key, column or table name.</param>
    /// <returns>The escaped segment.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> has an unpaired surrogate (it is not well-formed UTF-16).</exception>
    public static string EscapeSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!SplitCodec.IsWellFormedUtf16(value))
            throw new ArgumentException(
                "A blob-name segment must be well-formed UTF-16: this value has an unpaired surrogate, which would be escaped as U+FFFD and name another value's blob.",
                nameof(value));
        return value.Length == 0
            ? EmptySegment
            : Uri.EscapeDataString(value).Replace(".", "%2E", StringComparison.Ordinal);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Unescapes <paramref name="segment"/> once; false when it is not a segment
    /// <see cref="EscapeSegment"/> writes (so it was not written by this library, and a value
    /// unescaped from it would not name the same blob again).
    /// </summary>
    /// <param name="segment">One segment of a listed blob name.</param>
    /// <param name="value">The unescaped value, or empty when false.</param>
    /// <returns>True when the segment is canonical.</returns>
    public static bool TryUnescapeSegment(string segment, out string value)
    {
        ArgumentNullException.ThrowIfNull(segment);
        value = segment == EmptySegment ? string.Empty : Uri.UnescapeDataString(segment);
        if (segment.Length > 0 && SplitCodec.IsWellFormedUtf16(value)
            && string.Equals(EscapeSegment(value), segment, StringComparison.Ordinal))
            return true;
        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. True when <paramref name="name"/> has the exact offload shape under <paramref name="partitionPrefix"/>
    /// (<c>{table}/{partitionKey}/</c>): three more segments, the row key and the column as
    /// <see cref="EscapeSegment"/> writes them and a lower-hex SHA-256. Sweeps and partition deletes
    /// delete nothing else.
    /// </summary>
    /// <param name="name">A listed blob name.</param>
    /// <param name="partitionPrefix">The partition prefix it was listed under.</param>
    /// <returns>True for a name of the offload shape.</returns>
    public static bool IsOffloadBlobName(string name, string partitionPrefix)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(partitionPrefix);
        if (!name.StartsWith(partitionPrefix, StringComparison.Ordinal))
            return false;
        var segments = name[partitionPrefix.Length..].Split('/');
        return segments.Length == 3
            && TryUnescapeSegment(segments[0], out _)
            && TryUnescapeSegment(segments[1], out var column) && column.Length > 0
            && IsSha256Hex(segments[2]);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. Throws when the SDK would not address <paramref name="name"/> as given: a <c>.</c> or <c>..</c>
    /// segment is collapsed out of the request URI, so the blob read, written or deleted would be
    /// another one. Every store checks each name it is given, so a naming regression fails loudly
    /// (in memory too) instead of silently diverging from what the rows reference.
    /// </summary>
    /// <param name="name">The blob name.</param>
    /// <exception cref="ArgumentException">A segment of the name is <c>.</c> or <c>..</c>.</exception>
    public static void ThrowIfNotAddressable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var segment in name.Split('/'))
        {
            if (segment is "." or "..")
                throw new ArgumentException(
                    $"The blob name '{name}' has a '{segment}' segment, which the Blobs SDK collapses: the request would address another blob.",
                    nameof(name));
        }
    }

    private static bool IsSha256Hex(string segment)
    {
        if (segment.Length != Sha256HexLength)
            return false;
        foreach (var c in segment)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}
