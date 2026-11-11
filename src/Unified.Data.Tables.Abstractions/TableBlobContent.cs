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

    // Why the source of a content that is not sealed can no longer be read from where its content starts.
    private const string PartlyReadAndNotRewound =
        "its source stream was partly read by a failed write and could not be put back where the content starts";

    private const string PartlyReadForwardOnly =
        "its source stream is not seekable and was partly read by a failed write";

    private const string NotRewindable = "its source stream can no longer be put back where the content starts";

    // Why a sealed content has nothing left it could be uploaded from.
    private const string TemporaryCopyReleased =
        "it was created from a non-seekable stream, and its temporary copy was released by a failed write";

    private readonly byte[]? bytes;
    private long length;
    private string? sha256;
    private Func<CancellationToken, Task<Stream>>? open;
    private string? column;
    private bool originRecorded;
    private string? sourceLost;

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
    /// <remarks>
    /// A write that fails can be retried with the same content when the stream is seekable: every
    /// attempt reads it again from the position it had at the first one. A non-seekable stream is
    /// copied to a temporary file once, and a write that fails releases that copy, so its content
    /// cannot be written again: use a seekable stream (or <see cref="FromBytes"/>) for content whose
    /// write may be retried, for example by <c>MutateAsync</c> after a conflict. A content that can no
    /// longer be written (its temporary copy was released, or its stream was partly read by a failed
    /// write and cannot be put back) makes every later write of it throw
    /// <see cref="InvalidOperationException"/> before anything is uploaded; create it again from the source.
    /// </remarks>
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
            throw new InvalidOperationException(
                sourceLost is not null ? $"This TableBlobContent cannot be read: {sourceLost}. Create the content again from the source."
                : Abandoned ? $"This TableBlobContent cannot be read: {TemporaryCopyReleased}. Create the content again from the source."
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

    /// <summary>
    /// Where the content starts in <see cref="Source"/>: the position a seekable source had when it was
    /// first prepared (see <see cref="RewindSource"/>), 0 for a temporary copy.
    /// </summary>
    internal long SourceOrigin { get; private set; }

    /// <summary>
    /// True once <see cref="SourceOrigin"/> is known. Such a source is read from that position or not at
    /// all, never from wherever a failed attempt left it.
    /// </summary>
    internal bool HasSourceOrigin => originRecorded;

    /// <summary>The bytes of in-memory content.</summary>
    internal byte[]? Bytes => bytes;

    /// <summary>The blob this content was written to or read from; null before the first write.</summary>
    internal string? BoundPath { get; private set; }

    /// <summary>Whether <see cref="Source"/> is a temporary copy this content owns and disposes.</summary>
    private bool OwnsSource { get; set; }

    /// <summary>Records the length and hash of the prepared content.</summary>
    /// <param name="length">The content length in bytes.</param>
    /// <param name="sha256">Lower-hex SHA-256.</param>
    /// <param name="spool">
    /// A temporary copy that replaces a non-seekable source; owned and disposed by this content. Its
    /// content starts at position 0.
    /// </param>
    internal void Seal(long length, string sha256, Stream? spool = null)
    {
        this.length = length;
        Guard.NotNull(sha256, nameof(sha256));
        this.sha256 = sha256;
        if (spool is not null)
        {
            ReleaseSpool();
            Source = spool;
            OwnsSource = true;
            SourceOrigin = 0;
            originRecorded = true;
        }
    }

    /// <summary>
    /// Puts a seekable source where its content starts. The first call records the current position;
    /// every later one seeks back to it, so each prepare attempt and each upload reads the same bytes.
    /// </summary>
    /// <param name="columnName">The column named when the source cannot be put back.</param>
    /// <returns>The position the content starts at.</returns>
    /// <exception cref="InvalidOperationException">
    /// The source can no longer be put where its content starts (it throws, or it is disposed). The
    /// content can no longer be written: this and every later write refuses it.
    /// </exception>
    internal long RewindSource(string columnName)
    {
        var source = Source
            ?? throw new InvalidOperationException($"The TableBlobContent of '{columnName}' has no source stream to read.");
        if (!originRecorded)
        {
            SourceOrigin = source.Position;
            originRecorded = true;
            return SourceOrigin;
        }

        Exception? failure = null;
        if (source.CanSeek)
        {
            try
            {
                source.Position = SourceOrigin;
                return SourceOrigin;
            }
#pragma warning disable CA1031 // whatever the stream throws, it is not where the content starts: the content is given up
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        }

        sourceLost = NotRewindable;
        throw new InvalidOperationException(NotWritable(columnName, NotRewindable), failure);
    }

    /// <summary>
    /// After a prepare attempt over a seekable source failed or was cancelled part-way: puts the source
    /// back where its content starts, so a retry reads all of it. A source that cannot be put back makes
    /// the content unusable (see <see cref="ThrowIfNotWritable"/>) instead of leaving a retry the unread tail.
    /// </summary>
    internal void RestoreSource()
    {
        try
        {
            if (Source is { CanSeek: true } source)
            {
                source.Position = SourceOrigin;
                return;
            }
        }
#pragma warning disable CA1031 // whatever the stream throws, it is not where the content starts: the content is given up
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        sourceLost = PartlyReadAndNotRewound;
    }

    /// <summary>
    /// A forward-only source was partly consumed by a prepare attempt that failed or was cancelled: the
    /// content can no longer be written (see <see cref="ThrowIfNotWritable"/>).
    /// </summary>
    internal void LoseSource() => sourceLost = PartlyReadForwardOnly;

    /// <summary>
    /// Refuses a content that can no longer be written, before anything of it is read or uploaded: one
    /// whose source was partly read by a failed write and cannot be read again from where the content
    /// starts, or one whose temporary copy a failed write released (<see cref="Abandoned"/>).
    /// </summary>
    /// <param name="columnName">The column the content is being written to.</param>
    /// <exception cref="InvalidOperationException">The content has to be created again from its source.</exception>
    internal void ThrowIfNotWritable(string columnName)
    {
        if (sourceLost is not null)
            throw new InvalidOperationException(NotWritable(columnName, sourceLost));
        if (Abandoned)
            throw new InvalidOperationException(NotWritable(columnName, TemporaryCopyReleased)
                + " Content created from a seekable stream or from bytes can be written again after a failed write.");
    }

    private static string NotWritable(string columnName, string reason) =>
        $"The TableBlobContent of '{columnName}' can no longer be written: {reason}. Create the content again from the source.";

    /// <summary>
    /// Opens the bytes a write uploads, decided at the moment of the upload rather than when the write
    /// was planned: the in-memory bytes; the prepared source (the caller's seekable stream, or the
    /// temporary copy), read from where the content starts; or, once the content is bound, the blob it
    /// streams. So an upload that follows the one that bound the content (the same content on another
    /// entity of a batch) still has something to read.
    /// </summary>
    /// <param name="columnName">The column the content is being written to, named by a failure.</param>
    /// <param name="ct">Cancels opening the blob of a bound content.</param>
    /// <returns>A stream the caller disposes; disposing it never closes the source.</returns>
    /// <exception cref="InvalidOperationException">The content can no longer be written (see <see cref="ThrowIfNotWritable"/>).</exception>
    internal async Task<Stream> OpenUploadSourceAsync(string columnName, CancellationToken ct)
    {
        if (bytes is not null)
            return new MemoryStream(bytes, writable: false);
        if (Source is { } source)
        {
            RewindSource(columnName);
            // The caller (or this content, for its temporary copy) owns the stream: hand out a non-closing view.
            return new HashingReadStream(source, null, null, reason => new InvalidOperationException(reason), leaveOpen: true);
        }

        ThrowIfNotWritable(columnName);
        return await OpenReadAsync(ct).ConfigureAwait(false);
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
    /// the content was bound to its blob. The content stays sealed but has nothing left to be uploaded
    /// from: every later write refuses it (<see cref="ThrowIfNotWritable"/>) before anything is uploaded.
    /// </summary>
    internal void ReleasePrepared()
    {
        if (OwnsSource)
        {
            ReleaseSpool();
            Abandoned = true;
        }
    }

    /// <summary>
    /// True when the temporary copy of a non-seekable source was released by a write that failed before
    /// the content was bound to its blob. Such a content can be neither written nor read any more.
    /// </summary>
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
