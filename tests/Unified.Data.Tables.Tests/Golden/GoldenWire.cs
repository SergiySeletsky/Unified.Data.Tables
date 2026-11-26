using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests.Golden;

/// <summary>
/// Takes rows to the wire and back through the real Azure SDK, and renders them as the text the golden
/// files hold: per row its keys, then one line per cell with the cell's name, Edm type and value exactly
/// as the SDK wrote them into the request body.
/// </summary>
/// <remarks>
/// <para>A cell line is <c>TAB name TAB Edm type TAB value</c>, the cells in ordinal name order (their
/// order in the body carries no meaning to the service). The value is the raw JSON token of the body. A
/// token longer than <see cref="InlineLimit"/> characters is replaced by the SHA-256 of its UTF-8 bytes,
/// its length and its first characters, so a 64 KB chunk is pinned to the byte without a megabyte of
/// checked-in text.</para>
/// <para>One kind of value is not pinned by its bytes: GZip output. The compressed bytes depend on the
/// zlib build of the runtime (and may differ between runtimes, operating systems and CPU architectures),
/// so a GZip cell is pinned by the SHA-256 and length of what it inflates to, and the cells derived from
/// the compressed bytes (a split value's <c>__sha256</c> and <c>__len</c>) by that same content. The
/// number of chunks of a GZip split value is pinned by choosing an input that compresses into one.</para>
/// </remarks>
internal static class GoldenWire
{
    /// <summary>The longest raw JSON token a golden file holds inline.</summary>
    internal const int InlineLimit = 400;

    private const string TypeAnnotation = "@odata.type";

    private static readonly JsonSerializerOptions ReadBackJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // For comparing what was written with what was read: a point in time is compared as its instant. The
    // row stores neither the Kind of a DateTime nor the offset of a DateTimeOffset (the "read" line of a
    // golden file shows the form a read returns them in).
    private static readonly JsonSerializerOptions MeaningJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new InstantOfDateTime(), new InstantOfDateTimeOffset() },
    };

    /// <summary>The request body the SDK sends to insert <paramref name="row"/>.</summary>
    internal static async Task<string> ToWireAsync(TableEntity row, CancellationToken ct)
    {
        var transport = new GoldenTransport(_ => GoldenTransport.NoContent);
        await transport.Service().GetTableClient("t").AddEntityAsync(row, ct);
        var sent = Assert.Single(transport.Requests);
        Assert.Equal("POST", sent.Method);
        return sent.Body;
    }

    /// <summary>The request bodies of <paramref name="rows"/>, in order.</summary>
    internal static async Task<IReadOnlyList<string>> ToWireAsync(IEnumerable<TableEntity> rows, CancellationToken ct)
    {
        var bodies = new List<string>();
        foreach (var row in rows)
            bodies.Add(await ToWireAsync(row, ct));
        return bodies;
    }

    /// <summary>
    /// The row the SDK reads when the service returns the stored <paramref name="body"/>: the cells as
    /// written, plus the version and <c>Timestamp</c> the service adds.
    /// </summary>
    internal static async Task<TableEntity> FromWireAsync(string body, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(body);
        var stored = AsReturned(doc.RootElement);
        var transport = new GoldenTransport(_ => new GoldenTransport.Answer(200, stored));
        var response = await transport.Service().GetTableClient("t").GetEntityAsync<TableEntity>(
            doc.RootElement.GetProperty("PartitionKey").GetString(), doc.RootElement.GetProperty("RowKey").GetString(), cancellationToken: ct);
        return response.Value;
    }

    // The entity as the service answers a read under the "minimalmetadata" format the SDK asks for: every
    // token as it was sent, except that a type the reader can infer carries no annotation. Of the types
    // the SDK annotates on a write that is Edm.Double: it comes back as a bare number, always with a
    // fraction ("42.0"), so that it is not taken for an Int32.
    private static string AsReturned(JsonElement row)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("odata.metadata", GoldenTransport.Endpoint + "$metadata#t/@Element");
            json.WriteString("odata.etag", GoldenTransport.ETag);
            foreach (var cell in row.EnumerateObject())
            {
                var annotated = cell.Name.EndsWith(TypeAnnotation, StringComparison.Ordinal) ? cell.Name[..^TypeAnnotation.Length] : null;
                // NaN stays an annotated string ("NaN"): no number spells it.
                if (annotated is not null && cell.Value.GetString() == "Edm.Double" && row.GetProperty(annotated).ValueKind == JsonValueKind.Number)
                    continue;

                json.WritePropertyName(cell.Name);
                var raw = cell.Value.GetRawText();
                var wholeDouble = cell.Value.ValueKind == JsonValueKind.Number
                    && row.TryGetProperty(cell.Name + TypeAnnotation, out var type) && type.GetString() == "Edm.Double"
                    && raw.All(c => c is '-' or (>= '0' and <= '9'));
                json.WriteRawValue(wholeDouble ? raw + ".0" : raw, skipInputValidation: true);
            }
            json.WriteString("Timestamp", "2026-01-02T03:04:05.0000000Z");
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The golden text of <paramref name="bodies"/>: the rows of one write, the primary row first.</summary>
    internal static string Render(IReadOnlyList<string> bodies)
    {
        var docs = bodies.Select(b => JsonDocument.Parse(b)).ToList();
        try
        {
            var gzip = GzipSplitValues(docs);
            var text = new StringBuilder();
            foreach (var doc in docs)
            {
                var root = doc.RootElement;
                text.Append("row PartitionKey=").Append(root.GetProperty("PartitionKey").GetRawText())
                    .Append(" RowKey=").Append(root.GetProperty("RowKey").GetRawText()).Append('\n');

                var cells = root.EnumerateObject()
                    .Where(p => p.Name is not ("PartitionKey" or "RowKey") && !p.Name.EndsWith(TypeAnnotation, StringComparison.Ordinal))
                    .OrderBy(p => p.Name, StringComparer.Ordinal);
                foreach (var cell in cells)
                {
                    var edm = EdmType(root, cell);
                    text.Append('\t').Append(cell.Name).Append('\t').Append(edm).Append('\t')
                        .Append(Value(cell, edm, gzip)).Append('\n');
                }
            }
            return text.ToString();
        }
        finally
        {
            docs.ForEach(d => d.Dispose());
        }
    }

    /// <summary>
    /// One golden line for an object read back: its runtime type and its public state as JSON, long
    /// values replaced by their hash. <c>ETag</c> and <c>Timestamp</c> of an entity are left out: they are
    /// the service's version and clock, not stored cells.
    /// </summary>
    internal static string Describe(object? value) =>
        value is null ? "read\tnull\n" : $"read\t{value.GetType().FullName}\t{Shorten(State(value, ReadBackJson))!.ToJsonString(ReadBackJson)}\n";

    /// <summary>
    /// The public state of <paramref name="value"/> as JSON with every point in time as its instant: what
    /// a value written and the value read back must agree on.
    /// </summary>
    internal static string Meaning(object value) => State(value, MeaningJson).ToJsonString(MeaningJson);

    private static JsonNode State(object value, JsonSerializerOptions options)
    {
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), options)!;
        if (value is IEntity && node is JsonObject entity)
        {
            entity.Remove(nameof(IEntity.ETag));
            entity.Remove(nameof(IEntity.Timestamp));
        }
        return node;
    }

    /// <summary>One golden line for a cell of a row read back as the SDK hands it over: its name, CLR type and value.</summary>
    internal static string DescribeColumn(string name, object value)
    {
        var text = value switch
        {
            string s => JsonSerializer.Serialize(s),
            byte[] bytes => Convert.ToBase64String(bytes),
            DateTimeOffset instant => instant.ToString("O", CultureInfo.InvariantCulture),
            DateTime instant => instant.ToString("O", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
        return $"read-column\t{name}\t{value.GetType().Name}\t{text}\n";
    }

    private static JsonNode? Shorten(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[name];
                    obj.Remove(name);
                    obj[name] = Shorten(child);
                }
                return obj;
            case JsonArray array:
                var json = array.ToJsonString(ReadBackJson);
                if (json.Length > 10 * InlineLimit)
                    return JsonValue.Create($"sha256-of-json={Sha256(Encoding.UTF8.GetBytes(json))},items={array.Count}");
                var copy = new JsonArray();
                foreach (var item in array.ToList())
                {
                    array.Remove(item);
                    copy.Add(Shorten(item));
                }
                return copy;
            case JsonValue value when value.TryGetValue<string>(out var s) && s.Length > InlineLimit:
                return JsonValue.Create($"sha256-of-utf16le={Sha256(Encoding.Unicode.GetBytes(s))},chars={s.Length}");
            default:
                return node;
        }
    }

    // The base columns of the split values stored GZip-compressed, with their compressed bytes put
    // together from every row of the write (the chunks of one value may be spread over its rows).
    private static Dictionary<string, byte[]> GzipSplitValues(List<JsonDocument> docs)
    {
        var bases = docs
            .SelectMany(d => d.RootElement.EnumerateObject())
            .Where(p => p.Name.EndsWith("__enc", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "gzip")
            .Select(p => p.Name[..^"__enc".Length])
            .ToList();

        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var column in bases)
        {
            using var stored = new MemoryStream();
            for (var i = 0; ; i++)
            {
                var chunk = docs
                    .Select(d => d.RootElement.TryGetProperty($"{column}__c{i}", out var c) ? c : (JsonElement?)null)
                    .FirstOrDefault(c => c is not null);
                if (chunk is null)
                    break;
                stored.Write(chunk.Value.GetBytesFromBase64());
            }
            values[column] = stored.ToArray();
        }
        return values;
    }

    private static string EdmType(JsonElement row, JsonProperty cell)
    {
        if (row.TryGetProperty(cell.Name + TypeAnnotation, out var annotation))
            return annotation.GetString()!;
        switch (cell.Value.ValueKind)
        {
            case JsonValueKind.String:
                return "Edm.String";
            case JsonValueKind.True or JsonValueKind.False:
                return "Edm.Boolean";
            case JsonValueKind.Number when cell.Value.TryGetInt32(out _) && cell.Value.GetRawText().All(c => c is '-' or (>= '0' and <= '9')):
                // A number without an annotation is what the service takes for an Int32.
                return "Edm.Int32";
            default:
                Assert.Fail($"The cell '{cell.Name}' is on the wire as {cell.Value.GetRawText()} with no type annotation: its Edm type is not decided by the request.");
                return "";
        }
    }

    private static string Value(JsonProperty cell, string edm, Dictionary<string, byte[]> gzipSplit)
    {
        var name = cell.Name;

        // The JSON/GZip fallback of the serializer: a string cell holding base64 GZip bytes.
        if (name.EndsWith("__GZip", StringComparison.Ordinal) && edm == "Edm.String" && TryInflate(TryBase64(cell.Value.GetString()!), out var inflated))
            return $"gzip-base64{{inflated-sha256={Sha256(inflated)},inflated-bytes={inflated.Length}}}";

        foreach (var (column, stored) in gzipSplit)
        {
            if (!name.StartsWith(column + "__", StringComparison.Ordinal))
                continue;
            var suffix = name[(column.Length + 2)..];
            if (suffix == "sha256")
            {
                Assert.Equal(Sha256(stored), cell.Value.GetString(), ignoreCase: true);
                Assert.True(TryInflate(stored, out var content), $"The GZip split value '{column}' does not inflate.");
                return $"gzip{{inflated-sha256={Sha256(content)},inflated-bytes={content.Length}}}";
            }
            if (suffix == "len")
            {
                Assert.Equal(stored.Length.ToString(CultureInfo.InvariantCulture), cell.Value.GetString());
                return "gzip{the length of the compressed bytes}";
            }
            if (suffix.Length > 1 && suffix[0] == 'c' && suffix[1..].All(char.IsAsciiDigit))
                return $"gzip{{compressed bytes, pinned by {column}__sha256}}";
        }

        var raw = cell.Value.GetRawText();
        if (raw.Length <= InlineLimit)
            return raw;
        var head = char.IsHighSurrogate(raw[47]) ? raw[..47] : raw[..48];
        return $"sha256={Sha256(Encoding.UTF8.GetBytes(raw))},utf8-bytes={Encoding.UTF8.GetByteCount(raw)},head={head}";
    }

    private static byte[]? TryBase64(string text)
    {
        var buffer = new byte[text.Length];
        return Convert.TryFromBase64String(text, buffer, out var written) ? buffer[..written] : null;
    }

    private static bool TryInflate(byte[]? bytes, out byte[] inflated)
    {
        inflated = [];
        if (bytes is not { Length: > 2 } || bytes[0] != 0x1f || bytes[1] != 0x8b)
            return false;
        try
        {
            using var input = new MemoryStream(bytes);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            inflated = output.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // A DateTime of unspecified kind is taken as UTC, as the serializer takes it.
    private sealed class InstantOfDateTime : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteNumberValue((value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).Ticks);
    }

    private sealed class InstantOfDateTimeOffset : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.UtcTicks);
    }
}
