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

    /// <summary>An in-process copy of an entity (the offload fields are filled by the offload tier).</summary>
    public static ValueWriteContext ForClone { get; } = new(null, null, NoColumns, true);
}

/// <summary>
/// What a read has already resolved outside the row (downloaded or lazy blob values). Empty until
/// blob offload exists; split cells inside the row need no context.
/// </summary>
internal sealed class ValueReadContext
{
    private ValueReadContext()
    {
    }

    /// <summary>A read with nothing resolved outside the row.</summary>
    public static ValueReadContext None { get; } = new();
}

/// <summary>A blob the serializer asked to be uploaded before the entity write (filled by the offload tier).</summary>
internal sealed class PendingBlob
{
    private PendingBlob()
    {
    }
}
