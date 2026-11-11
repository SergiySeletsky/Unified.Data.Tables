using System.Security.Cryptography;

namespace Unified.Data.Tables;

/// <summary>
/// Binary content of any size that an entity property stores in blob storage rather than in table
/// cells. The row keeps only a reference (path, SHA-256, length, content type). It is always
/// offloaded, whatever its size, and reading the entity never downloads it: the content is fetched
/// when <see cref="OpenReadAsync"/> is called and verified by the time that stream has delivered it:
/// the read that would return the last byte throws instead when the content does not match.
/// </summary>
/// <remarks>
/// <para>
/// Requires blob offload to be configured on the storage (<c>UnifiedTableStorageOptions.BlobOffload</c>).
/// A <see cref="TableBlobContent"/> property cannot be filtered on, written by a partial update, or
/// placed inside a collection or a JSON-stored object. It is stored only through a property declared as
/// <see cref="TableBlobContent"/>: in a property declared as <see cref="object"/> the write throws
/// <see cref="NotSupportedException"/> naming the property, before anything is uploaded.
/// </para>
/// <para>
/// One content may be assigned to several properties or entities, and a content read from one row may
/// be written into another: every row gets a blob of its own. A content that already streams a blob
/// keeps streaming that one, through the store it was read or written through. When it is written
/// somewhere else (another row, or the same row through another store, whose container holds a blob
/// of the same name), the property that was written is given a new <see cref="TableBlobContent"/> that
/// streams the blob of its own row in its own store, so the entity it came from (and any cached copy of
/// it) is not affected by what later happens to the other blob. A content read by a store without blob
/// offload is the exception: it becomes readable through the store that saves it to its own row.
/// </para>
/// <para>
/// A property whose owner the entity holds by value (a struct) cannot be given a new content: it keeps
/// its content object when the entity is written into another row. The row written still references a
/// blob of its own, but the written entity, and the store's cached copy of it, go on streaming the blob
/// of the row the content came from until the entity is read from the table.
/// </para>
/// <para>
/// When the blob of a content is gone (the row was replaced or deleted since the entity was read,
/// perhaps by another process), <see cref="OpenReadAsync"/> throws
/// <see cref="TableValueIntegrityException"/>, and the store that returned the entity stops answering
/// that row from its cache: reading the entity again returns the current row.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SemaphoreSlim that orders writes of one content never allocates its wait handle (only Wait(0) and WaitAsync are used), so it holds nothing to dispose.")]
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

    // Why a NEW content over a stream that a write of this library has read cannot be written.
    private const string ForwardOnlyAlreadyRead =
        "its source stream is not seekable and a write has already read from it, so the bytes it delivered cannot be read again. "
        + "Create the content once and assign that instance on every attempt, use a seekable stream or TableBlobContent.FromBytes, "
        + "or open the source again for each attempt (for example inside the MutateAsync delegate).";

    private const string SeekableNotPutBack =
        "a write read its source stream and could not put it back where that write's content started (the stream can no longer be repositioned). "
        + "Open the source again, or use TableBlobContent.FromBytes.";

    // The callers' streams that a write has read and that no new content can be read from as the
    // caller expects: a forward-only stream once read at all, a seekable one that could not be put
    // back. Weak keys: a stream is not kept alive by being listed here.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Stream, string> UnusableStreams = new();

    private readonly byte[]? bytes;
    private long length;
    private string? sha256;
    // The blob this content streams: its name, the column an integrity failure names, and the way to
    // open it, published together so that a reader never pairs the name of one blob with the opener of another.
    private Binding? binding;
    private bool originRecorded;
    private string? sourceLost;

    // One write at a time hashes, copies, uploads from or binds this content (BLOBS-1). Held only
    // while the content's own source or state is used, never while entity code runs.
    private readonly SemaphoreSlim turn = new(1, 1);
    private readonly object sync = new();
    // The writes that prepared this content and have not released it yet (see ReleasePrepared).
    private int writers;

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

    /// <summary>The MIME type recorded in the row for this content, e.g. <c>image/png</c>.</summary>
    /// <remarks>
    /// The blob's own <c>Content-Type</c> header is set when its bytes are first uploaded. Saving the same
    /// bytes again into the same property with another type changes the type recorded in the row (and
    /// this property of a later read) but not the header of the existing blob.
    /// </remarks>
    public string ContentType { get; }

    /// <summary>
    /// Content to be read from <paramref name="content"/> when the entity is written. The stream is
    /// kept as given: it is neither read nor disposed here, and the caller keeps owning it. A seekable
    /// stream is read from its current position; it must not change until the write completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A seekable stream is read from the position it had when the content was first written, and the
    /// library puts it back there whenever it is done reading it (after hashing, after an upload, and
    /// after a write that failed or was cancelled). So a write that fails before its upload finished can
    /// be retried with the same content, and a retry that creates a new content over the same stream (for
    /// example inside a <c>MutateAsync</c> delegate that runs again after a conflict) stores the same
    /// bytes. A stream that can no longer be put back (seeking throws, or it was disposed) makes the
    /// content, and any new content over that stream, throw <see cref="InvalidOperationException"/> when
    /// written.
    /// </para>
    /// <para>
    /// Once an attempt has uploaded the content's blob, the content streams that blob and no longer reads
    /// the stream (seekable or not): a retry with the same content, for example after the row write
    /// failed, reuses that blob. So the retry has
    /// to come before the blob is collected (by an orphan sweep whose age has passed, or a partition
    /// delete's cleanup); after that the write throws <see cref="TableValueIntegrityException"/> saying
    /// that the blob this content was uploaded to is gone, nothing is written, and the content must be
    /// created again (over the same seekable stream, which stands where the content started).
    /// </para>
    /// <para>
    /// A non-seekable stream is copied to a temporary file once. A write that fails after it began to read
    /// the stream and before the content was uploaded releases that copy (or has partly read the stream),
    /// and the content cannot be written again; a write that fails later, for example on its row write,
    /// leaves the content bound to its blob (above). A stream that a write has read cannot be read again,
    /// so a new content over it is refused with <see cref="InvalidOperationException"/> before anything
    /// is uploaded, and so are two contents over one such stream written by one write. For content whose write may be retried, create the content once and assign
    /// that instance on every attempt, use a seekable stream or <see cref="FromBytes"/>, or open the
    /// source again for each attempt. A content that can no longer be written (its temporary copy was
    /// released, or its stream was partly read by a failed write and cannot be put back) makes every
    /// later write of it throw <see cref="InvalidOperationException"/> before anything is uploaded;
    /// create it again from the source.
    /// </para>
    /// <para>
    /// One content may be held by several writes at the same time (one content on several entities
    /// written concurrently): they take turns reading its source (a non-seekable one is still copied
    /// only once), and every write that succeeds stores the whole content.
    /// </para>
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
    /// Written content, or content read from a row, streams the blob lazily and verifies it: the read
    /// that would return the last byte of the expected length compares the SHA-256 first and throws
    /// <see cref="TableValueIntegrityException"/> instead when it does not match, so reading exactly
    /// <see cref="Length"/> bytes is verified too. A blob that holds more bytes than expected, or fewer,
    /// fails the read with the same exception, and so does a blob deleted while it is read, unless the
    /// stream already holds its bytes. The entity being saved again while the stream is read (which
    /// touches the blob) does not disturb the read.
    /// </summary>
    /// <param name="ct">Cancels opening the blob.</param>
    /// <returns>A stream the caller disposes.</returns>
    /// <exception cref="InvalidOperationException">
    /// The content was created from a stream and has not been written yet, or can no longer be written
    /// or read because a write of it failed (see <see cref="FromStream"/>).
    /// </exception>
    /// <exception cref="TableValueIntegrityException">
    /// The blob is missing (the row was replaced or deleted since the entity was read). The store that
    /// returned the entity no longer answers that row from its cache, so reading the entity again returns
    /// the current row.
    /// </exception>
    public async Task<Stream> OpenReadAsync(CancellationToken ct = default)
    {
        if (bytes is not null)
            return new MemoryStream(bytes, writable: false);
        if (Volatile.Read(ref binding) is not { } bound)
            throw new InvalidOperationException(
                sourceLost is not null ? $"This TableBlobContent cannot be read: {sourceLost}. Create the content again from the source."
                : Abandoned ? $"This TableBlobContent cannot be read: {TemporaryCopyReleased}. Create the content again from the source."
                : "A TableBlobContent created from a stream can be read once the entity has been written.");

        var name = bound.Column ?? bound.Path;
        var expectedLength = length;
        var expectedSha = sha256;
        var stream = await bound.Open(ct).ConfigureAwait(false);
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
    internal string? BoundPath => Volatile.Read(ref binding)?.Path;

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
        MarkUnusable(source);
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
        if (Source is { } lost)
            MarkUnusable(lost);
    }

    /// <summary>
    /// After an upload read the prepared source, whether it succeeded, failed or was cancelled: puts the
    /// caller's seekable stream back where the content starts, so that whoever reads it next (another
    /// upload, or a new content created over the same stream by a retry) starts at the same place. A
    /// stream that can no longer be put there makes this content unusable (see
    /// <see cref="ThrowIfNotWritable"/>) and refuses every new content over it (see <see cref="ClaimSource"/>).
    /// </summary>
    internal void EndUploadRead()
    {
        if (OwnsSource || Source is not { } source)
            return;
        try
        {
            if (source.CanSeek)
            {
                source.Position = SourceOrigin;
                return;
            }
        }
#pragma warning disable CA1031 // whatever the stream throws, it is not where the content starts
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        sourceLost = NotRewindable;
        MarkUnusable(source);
    }

    /// <summary>
    /// Before a content reads its source for the first time: refuses a stream that a write has already
    /// read and could not leave as the caller gave it (a forward-only stream read at all, a seekable one
    /// that could not be put back), so a new content over it never stores the rest of it, or nothing.
    /// A forward-only stream is listed from now on.
    /// </summary>
    /// <param name="columnName">The column the content is being written to.</param>
    /// <exception cref="InvalidOperationException">The stream cannot deliver this content's bytes.</exception>
    internal void ClaimSource(string columnName)
    {
        var source = Source
            ?? throw new InvalidOperationException($"The TableBlobContent of '{columnName}' has no source stream to read.");
        lock (UnusableStreams)
        {
            if (UnusableStreams.TryGetValue(source, out var why))
                throw new InvalidOperationException($"The TableBlobContent of '{columnName}' cannot be written: {why}");
            if (!source.CanSeek)
                UnusableStreams.Add(source, ForwardOnlyAlreadyRead);
        }
    }

    // A caller's stream that a write read and could not put back (a temporary copy is nobody else's).
    private void MarkUnusable(Stream source)
    {
        if (OwnsSource)
            return;
        lock (UnusableStreams)
        {
            if (!UnusableStreams.TryGetValue(source, out _))
                UnusableStreams.Add(source, SeekableNotPutBack);
        }
    }

    /// <summary>Waits until no other write is using this content; pair with <see cref="EndTurn"/>.</summary>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task that completes once this write holds the content.</returns>
    internal Task WaitTurnAsync(CancellationToken ct) => turn.WaitAsync(ct);

    /// <summary>Lets the next write use this content.</summary>
    internal void EndTurn() => turn.Release();

    /// <summary>
    /// Counts a write that is about to prepare this content; it calls <see cref="ReleasePrepared"/>
    /// once when it ends. The temporary copy of a forward-only source is released only by the last one.
    /// </summary>
    internal void Enlist()
    {
        lock (sync)
            writers++;
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
    /// <exception cref="TableValueIntegrityException">
    /// The content is bound and the blob it streams is gone: it has to be created again from the source.
    /// </exception>
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
        try
        {
            return await OpenReadAsync(ct).ConfigureAwait(false);
        }
        catch (TableValueIntegrityException) when (Volatile.Read(ref binding) is { } gone)
        {
            // This is a write, not a read of a cached entity (BLOBS-5): the bytes are nowhere else.
            throw new TableValueIntegrityException(columnName,
                $"the content cannot be written: the blob this content was uploaded to or read from, '{gone.Path}', is gone "
                + "(an orphan sweep or a partition delete's cleanup may have removed it after an earlier write of this content failed, "
                + "or the row it was read from was replaced or deleted), and the content keeps no other copy of its bytes. "
                + "Create the content again from the source.");
        }
    }

    /// <summary>
    /// Points the content at the blob it was written to; reads now stream that blob. A content that
    /// already streams another blob (it was read from another row, or written to another property or
    /// entity before) is left as it is: the entity it came from holds this instance, and so does every
    /// cached copy of that entity, and all of them would otherwise read the new blob from now on and
    /// fail once that blob is deleted.
    /// </summary>
    /// <param name="path">The blob name.</param>
    /// <param name="open">Opens the blob.</param>
    /// <param name="columnName">The column named by an integrity failure; null names <paramref name="path"/>.</param>
    /// <param name="store">
    /// The blob store <paramref name="open"/> reads, compared by reference (never over the network): two
    /// stores of one entity type over two containers or accounts give a blob the same name, and a content
    /// keeps the blob of the store it was bound through. Null when the store that materialized the content
    /// has no blob offload and cannot open it.
    /// </param>
    /// <returns>
    /// True when the content serves reads of <paramref name="path"/>: it is bound to that blob now, or was
    /// already through the same <paramref name="store"/> or through a store that could not open it (it
    /// then reads the blob through <paramref name="open"/>), or it keeps its bytes in memory and never
    /// reads a blob. False when it streams another blob, or a blob of the same name through another
    /// store, and was not changed; the caller then gives the written entity a content of its own.
    /// </returns>
    /// <remarks>
    /// A content that may be shared with another write is bound only while the binding write holds it
    /// (<see cref="WaitTurnAsync"/>), so of several writes that bind one content that streams no blob
    /// yet exactly one binds it and every other one is told to give its entity a content of its own.
    /// </remarks>
    internal bool Bind(string path, Func<CancellationToken, Task<Stream>> open, string? columnName, object? store)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(open, nameof(open));
        if (Volatile.Read(ref binding) is { } bound)
        {
            if (bytes is not null)
                return true;
            if (!string.Equals(bound.Path, path, StringComparison.Ordinal))
                return false;
            // The same name in another store's container is another blob (BLOBS-3): the entity this
            // content came from, and every cached copy of it, keep reading the blob of their own store.
            if (bound.Store is not null && !ReferenceEquals(bound.Store, store))
                return false;
        }
        else
        {
            // The source is not needed any more: it is where the content started (EndUploadRead), and
            // a stream that could not be put back no longer stops this content from being written.
            Abandoned = false;
            sourceLost = null;
            ReleaseSpool();
            Source = null;
        }

        // Name, column, opener and store in one step: a concurrent OpenReadAsync sees all of one binding.
        // For a content already bound to this blob only the way to it changes (the row may have been read
        // by a store that could not open it, and written by one that can).
        Volatile.Write(ref binding, new Binding(path, columnName, open, store));
        return true;
    }

    /// <summary>
    /// The blob a content streams: its name, the column an integrity failure names, the way to open it,
    /// and the blob store it is opened through (null when that store has no blob offload).
    /// </summary>
    private sealed class Binding(string path, string? column, Func<CancellationToken, Task<Stream>> open, object? store)
    {
        public string Path { get; } = path;

        public string? Column { get; } = column;

        public Func<CancellationToken, Task<Stream>> Open { get; } = open;

        public object? Store { get; } = store;
    }

    /// <summary>Content read from a row: known length, hash and type, streamed lazily from its blob.</summary>
    /// <param name="length">The content length in bytes.</param>
    /// <param name="sha256">Lower-hex SHA-256.</param>
    /// <param name="contentType">The type recorded in the row.</param>
    /// <param name="path">The blob name.</param>
    /// <param name="open">Opens the blob.</param>
    /// <param name="columnName">The column named by an integrity failure; null names <paramref name="path"/>.</param>
    /// <param name="store">The blob store <paramref name="open"/> reads (see <see cref="Bind"/>); null when the reading store has no blob offload.</param>
    internal static TableBlobContent Bound(
        long length, string sha256, string contentType, string path, Func<CancellationToken, Task<Stream>> open, string? columnName, object? store)
    {
        var result = new TableBlobContent(contentType, null, null);
        result.Seal(length, sha256);
        result.Bind(path, open, columnName, store);
        return result;
    }

    /// <summary>
    /// Ends a write that prepared this content (<see cref="Enlist"/>). The last such write deletes the
    /// temporary copy made of a non-seekable source when no write bound the content to its blob: the
    /// content stays sealed but has nothing left to be uploaded from, and every later write refuses it
    /// (<see cref="ThrowIfNotWritable"/>) before anything is uploaded. While another write still holds
    /// the content, the copy is kept for it.
    /// </summary>
    internal void ReleasePrepared()
    {
        lock (sync)
        {
            if (writers > 0)
                writers--;
            if (writers > 0 || !OwnsSource)
                return;
            // A write reading the copy right now (counted out by a caller that released more than it
            // prepared) keeps it; that write's own release deletes it.
            if (!turn.Wait(0))
                return;
            try
            {
                ReleaseSpool();
                Abandoned = true;
            }
            finally
            {
                turn.Release();
            }
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
