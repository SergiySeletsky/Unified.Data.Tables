using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Unified.Data.Tables;

/// <summary>Which part of a large value a column belongs to.</summary>
internal enum LargeValueCellKind
{
    /// <summary><c>{Prop}__c{n}</c>: one chunk of the stored bytes.</summary>
    Chunk,

    /// <summary><c>{Prop}__chunks</c>: the number of chunk cells.</summary>
    Count,

    /// <summary><c>{Prop}__sha256</c>: lower-hex SHA-256 of the stored bytes.</summary>
    Sha256,

    /// <summary><c>{Prop}__enc</c>: <c>text</c>, <c>gzip</c>, <c>binary</c> or <c>utf16le</c>.</summary>
    Encoding,

    /// <summary><c>{Prop}__len</c>: stored byte count.</summary>
    Length,

    /// <summary><c>{Prop}__blob</c>: reference to an offloaded blob.</summary>
    BlobReference,
}

/// <summary>
/// The pure chunk codec for lossless large values: encoding, hashing, cutting into cells and
/// verified reassembly. No I/O and no static state.
/// </summary>
internal static class SplitCodec
{
    /// <summary>Largest string cell this codec writes, in UTF-16 chars (under the 32,768-char service limit).</summary>
    internal const int MaxTextCellChars = 32_000;

    /// <summary>Largest binary cell this codec writes, in bytes (the service limit).</summary>
    internal const int MaxBinaryCellBytes = 65_536;

    /// <summary>Suffix of the chunk-count cell.</summary>
    internal const string ChunksSuffix = "__chunks";

    /// <summary>Suffix of the blob-reference cell.</summary>
    internal const string BlobSuffix = "__blob";

    /// <summary>A value as it is stored: encoded bytes, the <c>__enc</c> tag and the SHA-256 of the bytes.</summary>
    internal readonly record struct StoredPayload(byte[] Bytes, string Enc, string Sha256);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>True when <paramref name="s"/> has no unpaired surrogate.</summary>
    internal static bool IsWellFormedUtf16(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Encodes a <see cref="string"/> or <c>byte[]</c> into its stored form.</summary>
    internal static StoredPayload ToStored(object payload, SplitEncoding encoding)
    {
        byte[] bytes;
        string enc;
        switch (payload)
        {
            case byte[] raw:
                bytes = raw;
                enc = "binary";
                break;
            case string s when !IsWellFormedUtf16(s):
                bytes = Utf16LeBytes(s);
                enc = "utf16le";
                break;
            case string s when encoding == SplitEncoding.Gzip:
                bytes = Gzip(Encoding.UTF8.GetBytes(s));
                enc = "gzip";
                break;
            case string s:
                bytes = Encoding.UTF8.GetBytes(s);
                enc = "text";
                break;
            default:
                throw new ArgumentException("Payload must be a string or byte[].", nameof(payload));
        }
        return new StoredPayload(bytes, enc, Hash(bytes));
    }

    /// <summary>Verifies the hash of <paramref name="bytes"/> and turns them back into the original value.</summary>
    internal static object FromStored(string column, ReadOnlySpan<byte> bytes, string enc, string expectedSha256)
    {
        var actual = Hash(bytes);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new TableValueIntegrityException(column, $"SHA-256 mismatch (expected {expectedSha256}, computed {actual}).");
        switch (enc)
        {
            case "text":
                return StrictUtf8.GetString(bytes);
            case "utf16le":
                return bytes.Length % 2 == 0 ? Utf16LeString(bytes) : throw new TableValueIntegrityException(column, "UTF-16LE data has an odd byte count.");
            case "binary":
                return bytes.ToArray();
            case "gzip":
                try
                {
                    using var input = new MemoryStream(bytes.ToArray());
                    using var gz = new GZipStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    gz.CopyTo(output);
                    return StrictUtf8.GetString(output.ToArray());
                }
                catch (InvalidDataException ex)
                {
                    throw new TableValueIntegrityException(column, "gzip data is corrupt: " + ex.Message);
                }
            default:
                throw new TableValueIntegrityException(column, $"unknown encoding '{enc}'.");
        }
    }

    /// <summary>
    /// True when the value can be stored as one plain cell with no split metadata. The encoding only
    /// governs how a value that does not fit is split: a well-formed string of at most
    /// <see cref="MaxTextCellChars"/> chars stays one ordinary string cell under either encoding.
    /// </summary>
    internal static bool FitsOneCell(object payload, SplitEncoding encoding) => payload switch
    {
        string s => s.Length <= MaxTextCellChars && IsWellFormedUtf16(s),
        byte[] b => b.Length <= MaxBinaryCellBytes,
        _ => false,
    };

    /// <summary>
    /// Returns the exclusive end index of a chunk that starts at <paramref name="start"/> and holds at
    /// most <paramref name="maxChars"/> chars, stepping back one char rather than separate a surrogate pair.
    /// </summary>
    internal static int SafeCut(string s, int start, int maxChars)
    {
        var end = Math.Min(s.Length, start + maxChars);
        if (end < s.Length && end > start + 1 && char.IsHighSurrogate(s[end - 1]) && char.IsLowSurrogate(s[end]))
            end--;
        return end;
    }

    /// <summary>
    /// Cuts the value into chunk cells and appends <c>__chunks</c>, <c>__sha256</c>, <c>__enc</c> and
    /// <c>__len</c>. At least one chunk is always written.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, object>> Encode(string column, object payload, SplitEncoding encoding) =>
        Encode(column, payload, ToStored(payload, encoding));

    /// <summary><see cref="Encode(string, object, SplitEncoding)"/> for a value already encoded by <see cref="ToStored"/>.</summary>
    internal static IReadOnlyList<KeyValuePair<string, object>> Encode(string column, object payload, StoredPayload stored)
    {
        var cells = new List<KeyValuePair<string, object>>();
        if (stored.Enc == "text")
        {
            var s = (string)payload;
            var start = 0;
            do
            {
                var end = SafeCut(s, start, MaxTextCellChars);
                cells.Add(new($"{column}__c{cells.Count}", s.Substring(start, end - start)));
                start = end;
            }
            while (start < s.Length);
        }
        else
        {
            var bytes = stored.Bytes;
            var start = 0;
            do
            {
                var n = Math.Min(MaxBinaryCellBytes, bytes.Length - start);
                cells.Add(new($"{column}__c{cells.Count}", bytes.AsSpan(start, n).ToArray()));
                start += n;
            }
            while (start < bytes.Length);
        }
        var chunkCount = cells.Count;
        cells.Add(new($"{column}__chunks", chunkCount));
        cells.Add(new($"{column}__sha256", stored.Sha256));
        cells.Add(new($"{column}__enc", stored.Enc));
        cells.Add(new($"{column}__len", (long)stored.Bytes.Length));
        return cells;
    }

    /// <summary>Reassembles and verifies a value from its cells; never returns partial data.</summary>
    internal static object Decode(string column, IReadOnlyDictionary<string, object> cells)
    {
        var count = ReadInt(column, cells, "__chunks");
        var enc = Get(column, cells, "__enc") as string ?? throw new TableValueIntegrityException(column, "'__enc' is not a string.");
        var sha = Get(column, cells, "__sha256") as string ?? throw new TableValueIntegrityException(column, "'__sha256' is not a string.");
        var length = ReadLong(column, cells, "__len");
        if (count < 1) throw new TableValueIntegrityException(column, $"invalid chunk count {count}.");

        using var buffer = new MemoryStream();
        for (var i = 0; i < count; i++)
        {
            var cell = Get(column, cells, $"__c{i}");
            byte[] part = cell switch
            {
                string t when enc == "text" => Encoding.UTF8.GetBytes(t),
                byte[] b when enc != "text" => b,
                _ => throw new TableValueIntegrityException(column, $"chunk {i} has the wrong type for encoding '{enc}'."),
            };
            buffer.Write(part, 0, part.Length);
        }
        if (cells.ContainsKey($"{column}__c{count}"))
            throw new TableValueIntegrityException(column, $"more chunks are stored than the declared {count}.");
        if (buffer.Length != length)
            throw new TableValueIntegrityException(column, $"stored length {buffer.Length} differs from declared {length}.");
        return FromStored(column, buffer.ToArray(), enc, sha);
    }

    /// <summary>Recognises the columns this feature owns: <c>__c{n}</c>, <c>__chunks</c>, <c>__sha256</c>, <c>__enc</c>, <c>__len</c>, <c>__blob</c>.</summary>
    internal static bool TryParseLargeValueColumn(string column, out string baseColumn, out LargeValueCellKind kind, out int index)
    {
        baseColumn = string.Empty;
        kind = default;
        index = 0;
        var at = column.LastIndexOf("__", StringComparison.Ordinal);
        if (at <= 0) return false;
        var suffix = column.Substring(at + 2);
        LargeValueCellKind parsed;
        var parsedIndex = 0;
        if (suffix == "chunks") parsed = LargeValueCellKind.Count;
        else if (suffix == "sha256") parsed = LargeValueCellKind.Sha256;
        else if (suffix == "enc") parsed = LargeValueCellKind.Encoding;
        else if (suffix == "len") parsed = LargeValueCellKind.Length;
        else if (suffix == "blob") parsed = LargeValueCellKind.BlobReference;
        else if (suffix.Length > 1 && suffix[0] == 'c'
            && int.TryParse(suffix.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out parsedIndex))
            parsed = LargeValueCellKind.Chunk;
        else return false;
        baseColumn = column.Substring(0, at);
        kind = parsed;
        index = parsedIndex;
        return true;
    }

    private static object Get(string column, IReadOnlyDictionary<string, object> cells, string suffix) =>
        cells.TryGetValue(column + suffix, out var v) && v is not null
            ? v
            : throw new TableValueIntegrityException(column, $"cell '{column}{suffix}' is missing.");

    private static int ReadInt(string column, IReadOnlyDictionary<string, object> cells, string suffix) =>
        Get(column, cells, suffix) switch
        {
            int i => i,
            long l when l is >= 0 and <= int.MaxValue => (int)l,
            _ => throw new TableValueIntegrityException(column, $"cell '{column}{suffix}' is not an integer."),
        };

    private static long ReadLong(string column, IReadOnlyDictionary<string, object> cells, string suffix) =>
        Get(column, cells, suffix) switch
        {
            long l => l,
            int i => i,
            _ => throw new TableValueIntegrityException(column, $"cell '{column}{suffix}' is not an integer."),
        };

    // Encoding.Unicode would replace an unpaired surrogate with U+FFFD, so the code units are copied by hand.
    private static byte[] Utf16LeBytes(string s)
    {
        var bytes = new byte[s.Length * 2];
        for (var i = 0; i < s.Length; i++)
        {
            bytes[2 * i] = (byte)s[i];
            bytes[(2 * i) + 1] = (byte)(s[i] >> 8);
        }
        return bytes;
    }

    private static string Utf16LeString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % 2 != 0)
            throw new InvalidDataException("UTF-16LE data has an odd byte count.");
        return string.Create(bytes.Length / 2, bytes.ToArray(), static (span, b) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = (char)(b[2 * i] | (b[(2 * i) + 1] << 8));
        });
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
