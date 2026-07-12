using System;
using System.Globalization;
using System.Text;

namespace Unified.Data.Tables;

/// <summary>
/// Encodes a provider continuation token together with a fingerprint of the query bounds it was issued
/// for, so a page-2 cursor replayed against a different query (different partition, prefix, or page
/// size) fails loudly instead of silently returning wrong rows.
/// </summary>
internal static class PageCursor
{
    /// <summary>
    /// A stable fingerprint of the bounds a cursor is valid for. Each string component is
    /// length-prefixed so distinct bounds can never concatenate to the same fingerprint (e.g.
    /// partition "a" + prefix "b" must not collide with partition "ab" + no prefix).
    /// </summary>
    public static string Fingerprint(string? partition, string? rowKeyPrefix, int pageSize) =>
        $"{LengthPrefixed(partition)}|{LengthPrefixed(rowKeyPrefix)}|{pageSize.ToString(CultureInfo.InvariantCulture)}";

    private static string LengthPrefixed(string? s) =>
        s is null ? "~" : s.Length.ToString(CultureInfo.InvariantCulture) + ":" + s;

    /// <summary>Wrap a provider-specific inner token with its bounds fingerprint.</summary>
    public static string Encode(string fingerprint, string innerToken) =>
        Base64(fingerprint) + "." + Base64(innerToken);

    /// <summary>
    /// Unwrap a cursor, verifying it was issued for <paramref name="fingerprint"/>. Throws
    /// <see cref="ArgumentException"/> on a malformed token or a bounds mismatch.
    /// </summary>
    public static string Decode(string cursor, string fingerprint)
    {
        Guard.NotNull(cursor, nameof(cursor));
        var dot = cursor.IndexOf('.');
        if (dot <= 0)
            throw new ArgumentException("Malformed continuation token.", nameof(cursor));

        string encodedFingerprint;
        string innerToken;
        try
        {
            encodedFingerprint = Unbase64(cursor.Substring(0, dot));
            innerToken = Unbase64(cursor.Substring(dot + 1));
        }
        catch (FormatException)
        {
            throw new ArgumentException("Malformed continuation token.", nameof(cursor));
        }

        if (!string.Equals(encodedFingerprint, fingerprint, StringComparison.Ordinal))
            throw new ArgumentException(
                "This continuation token was issued for a different query (partition, RowKey prefix, or page " +
                "size changed). Restart paging from the first page.", nameof(cursor));

        return innerToken;
    }

    private static string Base64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    private static string Unbase64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));
}
