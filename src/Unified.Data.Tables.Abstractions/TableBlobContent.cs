using System.Security.Cryptography;

namespace Unified.Data.Tables;

/// <summary>
/// Binary content of any size that an entity property stores in blob storage rather than in table
/// cells. The row keeps only a reference (path, SHA-256, length, content type). It is always
/// offloaded, whatever its size, and reading the entity never downloads it: the content is fetched
/// when <see cref="OpenReadAsync"/> is called and verified when that stream reaches its end.
/// </summary>
/// <remarks>
/// Requires blob offload to be configured on the storage (<c>UnifiedTableStorageOptions.BlobOffload</c>).
/// A <see cref="TableBlobContent"/> property cannot be filtered on, written by a partial update, or
/// placed inside a collection or a JSON-stored object.
/// </remarks>
public sealed class TableBlobContent
{

    private readonly byte[]? bytes;
    private long length;
    private string? sha256;
    private Func<CancellationToken, Task<Stream>>? open;
    private string? column;

    private TableBlobContent(string contentType, byte[]? bytes, Stream? source)
    {
        ContentType = contentType;
        this.bytes = bytes;
        Source = source;
    }

    /// <summary>The content length in bytes.</summary>
    /// <exception cref="InvalidOperationException">The content was created from a stream that has not been written yet.</exception>
    public long Length => IsSealed ? length : throw UnknownUntilWritten("length");

    /// <summary>Lower-case hex SHA-256 of the content.</summary>
    /// <exception cref="InvalidOperationException">The content was created from a stream that has not been written yet.</exception>
    public string Sha256 => sha256 ?? throw UnknownUntilWritten("SHA-256");

    /// <summary>The MIME type the blob is stored with, e.g. <c>image/png</c>.</summary>
    public string ContentType { get; }

    /// <summary>
    /// Content to be read from <paramref name="content"/> when the entity is written. The stream is
    /// kept as given: it is neither read nor disposed here, and the caller keeps owning it. A seekable
    /// stream is read from its current position; it must not change until the write completes.
    /// </summary>
    /// <param name="content">A readable stream.</param>
    /// <param name="contentType">The MIME type to store the blob with.</param>
    /// <returns>The content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="contentType"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="content"/> is not readable, or <paramref name="contentType"/> is empty.</exception>
    public static TableBlobContent FromStream(Stream content, string contentType)
    {
        Guard.NotNull(content, nameof(content));
        if (!content.CanRead) throw new ArgumentException("The stream must be readable.", nameof(content));
        return new TableBlobContent(CheckContentType(contentType), null, content);
    }

    /// <summary>Content held in memory; its length and SHA-256 are known at once.</summary>
    /// <param name="content">The bytes. The array is used as is, not copied; do not change it afterwards.</param>
    /// <param name="contentType">The MIME type to store the blob with.</param>
    /// <returns>The content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="contentType"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="contentType"/> is empty.</exception>
    public static TableBlobContent FromBytes(byte[] content, string contentType)
    {
        Guard.NotNull(content, nameof(content));
        var result = new TableBlobContent(CheckContentType(contentType), content, null);
        result.Seal(content.Length, Hex.Lower(HashOf(content)));
        return result;
    }

    /// <summary>
    /// Opens the content for reading. In-memory content returns a read-only stream over the bytes.
    /// Written content, or content read from a row, streams the blob lazily; the stream throws
    /// <see cref="TableValueIntegrityException"/> at its end when the length or SHA-256 does not match.
    /// </summary>
    /// <param name="ct">Cancels opening the blob.</param>
    /// <returns>A stream the caller disposes.</returns>
    /// <exception cref="InvalidOperationException">The content was created from a stream that has not been written yet.</exception>
    public async Task<Stream> OpenReadAsync(CancellationToken ct = default)
    {
        if (bytes is not null)
            return new MemoryStream(bytes, writable: false);
        if (open is null)
            throw new InvalidOperationException(Abandoned
                ? "This TableBlobContent was created from a non-seekable stream, and its temporary copy was released when a write failed before uploading it. Create it again from the source."
                : "A TableBlobContent created from a stream can be read once the entity has been written.");

        var name = column ?? BoundPath!;
        var expectedLength = length;
        var expectedSha = sha256;
        var stream = await open(ct).ConfigureAwait(false);
        return new HashingReadStream(stream, expectedLength, expectedSha,
            reason => new TableValueIntegrityException(name, reason), leaveOpen: false);
    }

    /// <summary>True once the length and SHA-256 are known.</summary>
    internal bool IsSealed => sha256 is not null;

    /// <summary>The caller's stream, or the spooled copy of it; null for in-memory or bound content.</summary>
    internal Stream? Source { get; private set; }

    /// <summary>Where the source content starts in <see cref="Source"/>.</summary>
    internal long SourceOrigin { get; private set; }

    /// <summary>The bytes of in-memory content.</summary>
    internal byte[]? Bytes => bytes;

    /// <summary>The blob this content was written to or read from; null before the first write.</summary>
    internal string? BoundPath { get; private set; }

    /// <summary>Whether <see cref="Source"/> is a temporary copy this content owns and disposes.</summary>
    private bool OwnsSource { get; set; }

    /// <summary>Records the length and hash of the prepared content.</summary>
    /// <param name="length">The content length in bytes.</param>
    /// <param name="sha256">Lower-hex SHA-256.</param>
    /// <param name="sourceOrigin">Where the content starts in the source stream.</param>
    /// <param name="spool">A temporary copy that replaces a non-seekable source; owned and disposed by this content.</param>
    internal void Seal(long length, string sha256, long sourceOrigin = 0, Stream? spool = null)
    {
        this.length = length;
        Guard.NotNull(sha256, nameof(sha256));
        this.sha256 = sha256;
        SourceOrigin = sourceOrigin;
        if (spool is not null)
        {
            ReleaseSpool();
            Source = spool;
            OwnsSource = true;
        }
    }

    /// <summary>Points the content at the blob it was written to; reads now stream that blob.</summary>
    /// <param name="path">The blob name.</param>
    /// <param name="open">Opens the blob.</param>
    /// <param name="columnName">The column named by an integrity failure; defaults to <paramref name="path"/>.</param>
    internal void Bind(string path, Func<CancellationToken, Task<Stream>> open, string? columnName = null)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(open, nameof(open));
        BoundPath = path;
        this.open = open;
        Abandoned = false;
        column = columnName;
        ReleaseSpool();
        Source = null;
    }

    /// <summary>Content read from a row: known length, hash and type, streamed lazily from its blob.</summary>
    internal static TableBlobContent Bound(
        long length, string sha256, string contentType, string path, Func<CancellationToken, Task<Stream>> open, string? columnName = null)
    {
        var result = new TableBlobContent(contentType, null, null);
        result.Seal(length, sha256);
        result.Bind(path, open, columnName);
        return result;
    }

    /// <summary>
    /// Deletes the temporary copy a write made of a non-seekable source, when the write failed before
    /// the content was bound to its blob. The content stays sealed: a later write finds its blob if the
    /// upload got that far, and otherwise reports that the content can no longer be read.
    /// </summary>
    internal void ReleasePrepared()
    {
        if (OwnsSource)
        {
            ReleaseSpool();
            Abandoned = true;
        }
    }

    /// <summary>True when the temporary copy of a non-seekable source was released before being uploaded.</summary>
    internal bool Abandoned { get; private set; }

    private void ReleaseSpool()
    {
        if (OwnsSource)
        {
            Source?.Dispose();
            Source = null;
            OwnsSource = false;
        }
    }

    private static InvalidOperationException UnknownUntilWritten(string what) =>
        new($"The {what} of a TableBlobContent created from a stream is known once the entity has been written.");

    private static byte[] HashOf(byte[] content)
    {
#if NETSTANDARD2_0
        using var sha = SHA256.Create();
        return sha.ComputeHash(content);
#else
        return SHA256.HashData(content);
#endif
    }

    private static string CheckContentType(string contentType)
    {
        Guard.NotNull(contentType, nameof(contentType));
        if (contentType.Trim().Length == 0) throw new ArgumentException("A content type is required.", nameof(contentType));
        return contentType;
    }
}
