using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables;

/// <summary>
/// How the serializer stores the large values of one write: whether blob offload is available, the
/// table the blobs belong to, columns that must be offloaded and whether the row is an in-process clone.
/// </summary>
/// <param name="Offload">Blob offload settings; null means no tier 3 (no blob offload).</param>
/// <param name="TableName">The store's resolved table name, the first segment of a blob name.</param>
/// <param name="ForceOffload">Flattened base columns that must be offloaded whatever their size.</param>
/// <param name="IsClone">(Named IsClone because a record cannot declare a member called Clone.) True for an in-process copy: no thresholds, values stay an unbounded logical bag.</param>
internal sealed record ValueWriteContext(
    BlobOffloadOptions? Offload,
    string? TableName,
    IReadOnlySet<string> ForceOffload,
    bool IsClone)
{
    private static readonly IReadOnlySet<string> NoColumns = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The public serializer API: no offload, not a clone; split values form an unbounded bag.</summary>
    public static ValueWriteContext Public { get; } = new(null, null, NoColumns, false);

    /// <summary>An in-process copy of an entity: split values stay a bag, <see cref="TableBlobContent"/> is kept by reference.</summary>
    public static ValueWriteContext ForClone { get; } = new(null, null, NoColumns, true);
}

/// <summary>
/// What a read has already resolved outside the row: decoded values of offloaded columns, and how to
/// build a lazy <see cref="TableBlobContent"/> for a reference. Split cells inside the row need neither.
/// </summary>
internal sealed class ValueReadContext
{
    private static readonly IReadOnlyDictionary<string, object> Nothing = new Dictionary<string, object>(StringComparer.Ordinal);

    internal ValueReadContext(
        IReadOnlyDictionary<string, object> resolved, Func<string, BlobReference, TableBlobContent>? lazyContent,
        IReadOnlyCollection<string>? projection = null)
    {
        Resolved = resolved ?? throw new ArgumentNullException(nameof(resolved));
        LazyContent = lazyContent;
        Projection = projection;
    }

    /// <summary>
    /// The caller's projection, or null for the whole entity. An offloaded value the projection does not
    /// name was deliberately not resolved and keeps its default; any other unresolved one is an error.
    /// </summary>
    public IReadOnlyCollection<string>? Projection { get; }

    /// <summary>A read with nothing resolved outside the row.</summary>
    public static ValueReadContext None { get; } = new(Nothing, null);

    /// <summary>Flattened base column to its downloaded, verified value (<c>string</c> or <c>byte[]</c>).</summary>
    public IReadOnlyDictionary<string, object> Resolved { get; }

    /// <summary>Builds the lazy content of a <see cref="TableBlobContent"/> column from its reference; null when none is in scope.</summary>
    public Func<string, BlobReference, TableBlobContent>? LazyContent { get; }
}

/// <summary>A blob the serializer asked to be uploaded before the entity write.</summary>
/// <param name="Column">The flattened base column (<c>Inner_Body</c>).</param>
/// <param name="Reference">The reference the row holds; its path is the blob name.</param>
/// <param name="ContentEncoding">The blob's <c>Content-Encoding</c> (<c>gzip</c>), or null.</param>
/// <param name="OpenSource">
/// Opens the bytes to upload; the stream is disposed after the upload. It is called when the upload
/// runs, and a <see cref="TableBlobContent"/> decides then what it is read from.
/// </param>
internal sealed record PendingBlob(
    string Column,
    BlobReference Reference,
    string? ContentEncoding,
    Func<CancellationToken, Task<Stream>> OpenSource)
{
    /// <summary>The <see cref="TableBlobContent"/> to bind to the blob once uploaded; null for a split value.</summary>
    public TableBlobContent? Content { get; init; }
}
