using System.ComponentModel;
using System.Text.Json;

namespace Unified.Data.Tables.Providers;

/// <summary>
/// Storage-provider SPI, not for application code: the blob container that holds offloaded values.
/// Blobs are content-addressed (their name ends with the SHA-256 of their bytes) and written
/// create-only. Its shape may change in any minor version.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IOffloadBlobStore
{
    /// <summary>Storage-provider SPI, not for application code. The blob's metadata, or null when it does not exist.</summary>
    /// <param name="name">The blob name.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The state, or null.</returns>
    Task<BlobState?> GetStateAsync(string name, CancellationToken ct);

    /// <summary>
    /// Storage-provider SPI, not for application code. Uploads <paramref name="content"/> unless the
    /// blob exists. An existing blob whose <c>sha256</c> metadata matches <paramref name="reference"/>
    /// is success (its <c>Last-Modified</c> is refreshed); one with a different hash throws.
    /// </summary>
    /// <param name="name">The blob name.</param>
    /// <param name="content">The bytes to store, read to its end.</param>
    /// <param name="reference">The reference the row will hold: hash, length and content type of the bytes.</param>
    /// <param name="contentEncoding">The <c>Content-Encoding</c> header (<c>gzip</c>), or null.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>A task that completes when the blob exists.</returns>
    /// <exception cref="TableValueIntegrityException">The blob exists with a different hash.</exception>
    Task UploadCreateOnlyAsync(string name, Stream content, BlobReference reference, string? contentEncoding, CancellationToken ct);

    /// <summary>
    /// Storage-provider SPI, not for application code. Refreshes the blob's <c>Last-Modified</c>
    /// (re-sets its metadata) so an orphan sweep that listed it earlier will not delete it.
    /// </summary>
    /// <param name="name">The blob name.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>A task that completes when the blob was touched.</returns>
    /// <exception cref="TableValueIntegrityException">The blob does not exist.</exception>
    Task TouchAsync(string name, CancellationToken ct);

    /// <summary>Storage-provider SPI, not for application code. Opens the blob's stored bytes for reading.</summary>
    /// <param name="column">The column the blob belongs to, named by an integrity failure.</param>
    /// <param name="name">The blob name.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>A stream the caller disposes.</returns>
    /// <exception cref="TableValueIntegrityException">The blob is missing.</exception>
    Task<Stream> OpenReadAsync(string column, string name, CancellationToken ct);

    /// <summary>
    /// Storage-provider SPI, not for application code. Deletes the blob unless it was modified after
    /// <paramref name="unmodifiedSince"/> (any time when null).
    /// </summary>
    /// <param name="name">The blob name.</param>
    /// <param name="unmodifiedSince">The <c>If-Unmodified-Since</c> condition, or null for none.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>True when deleted; false when it did not exist (404) or was modified since (412).</returns>
    Task<bool> DeleteIfUnmodifiedAsync(string name, DateTimeOffset? unmodifiedSince, CancellationToken ct);

    /// <summary>
    /// Storage-provider SPI, not for application code. The storage service's current time (for Azure,
    /// the <c>Date</c> header of a response), or null when it cannot be told. Cleanup and orphan-sweep
    /// cutoffs are compared with blob <c>Last-Modified</c> times, which the service stamps, so they are
    /// taken from this clock rather than from the client's, which may run ahead.
    /// </summary>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The service time, or null.</returns>
    Task<DateTimeOffset?> GetServiceTimeAsync(CancellationToken ct);

    /// <summary>Storage-provider SPI, not for application code. Lists the blobs whose name starts with <paramref name="prefix"/>.</summary>
    /// <param name="prefix">The name prefix.</param>
    /// <param name="ct">Cancels the listing.</param>
    /// <returns>Each blob's name and <c>Last-Modified</c>.</returns>
    IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListAsync(string prefix, CancellationToken ct);
}

/// <summary>Storage-provider SPI, not for application code. A stored blob's metadata.</summary>
/// <param name="Sha256">The <c>sha256</c> metadata: lower-hex SHA-256 of the stored bytes.</param>
/// <param name="Length">The <c>length</c> metadata: stored byte count.</param>
/// <param name="LastModified">The blob's <c>Last-Modified</c>.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record BlobState(string Sha256, long Length, DateTimeOffset LastModified);

/// <summary>
/// Storage-provider SPI, not for application code. The reference a row's <c>{Prop}__blob</c> cell
/// holds, as JSON <c>{"path","sha256","length","contentType","encoding"}</c>.
/// </summary>
/// <param name="Path">The blob name.</param>
/// <param name="Sha256">Lower-hex SHA-256 of the stored bytes.</param>
/// <param name="Length">Stored byte count.</param>
/// <param name="ContentType">The blob's <c>Content-Type</c>.</param>
/// <param name="Encoding">How the stored bytes decode: <c>text</c>, <c>gzip</c>, <c>binary</c> or <c>utf16le</c>.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record BlobReference(string Path, string Sha256, long Length, string ContentType, string Encoding)
{
    /// <summary>Storage-provider SPI, not for application code. The reference cell's JSON.</summary>
    /// <returns>Compact JSON with camelCase keys.</returns>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("path", Path);
            writer.WriteString("sha256", Sha256);
            writer.WriteNumber("length", Length);
            writer.WriteString("contentType", ContentType);
            writer.WriteString("encoding", Encoding);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Storage-provider SPI, not for application code. Parses a reference cell.</summary>
    /// <param name="column">The base column, named by an integrity failure.</param>
    /// <param name="json">The cell's JSON.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="TableValueIntegrityException">The JSON is malformed or a field is missing or of the wrong type.</exception>
    public static BlobReference Parse(string column, string json)
    {
        ArgumentNullException.ThrowIfNull(column);
        try
        {
            using var doc = JsonDocument.Parse(json ?? throw new TableValueIntegrityException(column, "the blob reference is missing."));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new TableValueIntegrityException(column, "the blob reference is not a JSON object.");
            var length = Field(column, root, "length", JsonValueKind.Number);
            if (!length.TryGetInt64(out var bytes) || bytes < 0)
                throw new TableValueIntegrityException(column, "the blob reference length is not a non-negative integer.");
            return new BlobReference(
                Text(column, root, "path"),
                Text(column, root, "sha256"),
                bytes,
                Text(column, root, "contentType"),
                Text(column, root, "encoding"));
        }
        catch (JsonException ex)
        {
            throw new TableValueIntegrityException(column, "the blob reference is not valid JSON: " + ex.Message);
        }
    }

    private static JsonElement Field(string column, JsonElement root, string name, JsonValueKind kind) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == kind
            ? value
            : throw new TableValueIntegrityException(column, $"the blob reference has no valid '{name}'.");

    private static string Text(string column, JsonElement root, string name) =>
        Field(column, root, name, JsonValueKind.String).GetString() is { Length: > 0 } s
            ? s
            : throw new TableValueIntegrityException(column, $"the blob reference has no valid '{name}'.");
}
