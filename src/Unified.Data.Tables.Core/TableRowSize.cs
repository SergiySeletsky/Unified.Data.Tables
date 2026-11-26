using Azure.Data.Tables;

namespace Unified.Data.Tables;

// Shared by TableStorage<T> and PolymorphicTableStorage<TBase>. Extracted rather than duplicated
// because the two batch planners must measure identically: a divergence would let one store send a
// transaction the other considers oversized, and the failure surfaces as an HTTP 413 partway
// through a bulk write that has already committed earlier chunks.
internal static class TableRowSize
{
    /// <summary>The service limit on the payload of one entity-group transaction (4 MiB).</summary>
    internal const long ServiceTransactionPayloadBytes = TableRequestSize.MaxTransactionPayloadBytes;

    /// <summary>
    /// The wire budget a large-value transaction is planned on: the service limit less 64 KiB for the
    /// multipart envelope and request headers that <see cref="ActionWireOverhead"/> approximates.
    /// </summary>
    internal const long MaxTransactionWireBytes = TableRequestSize.MaxTransactionWireBytes;

    /// <summary>Per-action multipart framing, request line and headers (If-Match, Content-Type...), generously.</summary>
    internal const long ActionWireOverhead = 1_024;

    /// <summary>
    /// Serialized size of one row, for transaction planning. Binary and string columns dominate; the
    /// fixed per-property and per-entity overhead is approximated rather than computed exactly,
    /// because the budget already sits well under the service limit to absorb it.
    /// </summary>
    internal static long Estimate(TableEntity row)
    {
        // Azure's own accounting: ~88 B of entity overhead plus 8 B per property, before values.
        var bytes = 88L + (row.Count * 8L);
        foreach (var key in row.Keys)
        {
            bytes += key.Length * 2L;
            bytes += row[key] switch
            {
                byte[] binary => binary.Length,
                BinaryData binary => binary.ToMemory().Length,
                string text => text.Length * 2L,
                _ => 8L,
            };
        }

        return bytes;
    }

    /// <summary>
    /// An upper bound of the JSON body the Azure SDK writes for <paramref name="row"/>: strings
    /// escaped as its default encoder escapes them (see <see cref="EscapedJsonLength"/>), binary
    /// base64-encoded, and an <c>@odata.type</c> annotation for every typed value. Used only on the
    /// large-value paths; the plain batch planner keeps <see cref="Estimate"/>.
    /// </summary>
    internal static long WireSize(TableEntity row)
    {
        var bytes = 2L; // {}
        foreach (var (key, value) in row)
            bytes += PropertyWireSize(key, value);
        return bytes;
    }

    /// <summary>The share of <see cref="WireSize"/> one property contributes: its name, value and type annotation.</summary>
    internal static long PropertyWireSize(string key, object? value)
    {
        var name = EscapedJsonLength(key);
        var bytes = name + 4; // "key": and the comma
        bytes += value switch
        {
            null => 4,
            string text => 2 + EscapedJsonLength(text),
            byte[] binary => 2 + Base64Length(binary.LongLength),
            BinaryData binary => 2 + Base64Length(binary.ToMemory().Length),
            bool => 5,
            int => 11,
            double => 26,
            _ => 48, // long, DateTimeOffset, DateTime, Guid: quoted text
        };
        // Azure.Data.Tables annotates every value but null, string, bool and int: Edm.Int64, Edm.DateTime,
        // Edm.Guid, Edm.Binary, and Edm.Double too (12.11.0 writes "key@odata.type":"Edm.Double" for each).
        if (value is not (null or string or bool or int))
            bytes += name + 32; // "key@odata.type":"Edm.Xxx",
        return bytes;
    }

    /// <summary>The wire cost of one transaction action that writes <paramref name="row"/>.</summary>
    internal static long ActionWireSize(TableEntity row) =>
        ActionWireOverhead + UrlKeyLength(row.PartitionKey) + UrlKeyLength(row.RowKey) + WireSize(row);

    /// <summary>The wire cost of one transaction action that deletes the row at the given keys (no body).</summary>
    internal static long DeleteActionWireSize(string? partitionKey, string? rowKey) =>
        ActionWireOverhead + UrlKeyLength(partitionKey) + UrlKeyLength(rowKey);

    /// <summary>
    /// The length of <paramref name="text"/> as a JSON string body (without quotes) as
    /// <c>System.Text.Json</c>'s default encoder writes it, or more: printable ASCII other than
    /// <c>" \ &lt; &gt; &amp; ' + `</c> is one byte, every other UTF-16 code unit (non-ASCII, control or
    /// HTML-sensitive) six bytes (<c>\uXXXX</c>), so a surrogate pair is twelve.
    /// </summary>
    internal static long EscapedJsonLength(string? text)
    {
        if (text is null) return 0;
        long bytes = 0;
        foreach (var c in text)
            bytes += IsSafeAscii(c) ? 1 : 6;
        return bytes;
    }

    private static bool IsSafeAscii(char c) =>
        c is >= ' ' and <= '~' and not ('"' or '\\' or '<' or '>' or '&' or '\'' or '+' or '`');

    private static long Base64Length(long bytes) => (bytes + 2) / 3 * 4;

    // A key in the request URL: percent-encoded UTF-8 (at most 3 B per UTF-8 byte), quotes doubled.
    private static long UrlKeyLength(string? key)
    {
        if (key is null) return 0;
        long bytes = 0;
        foreach (var c in key)
            bytes += c == '\'' ? 6 : c < 0x80 ? 3 : 9;
        return bytes;
    }
}
