namespace Unified.Data.Tables;

/// <summary>One planned transaction: a contiguous run of entities and the payload it carries.</summary>
public readonly struct BatchRange(int start, int count, long bytes)
{
    /// <summary>Index of the first entity in this transaction.</summary>
    public int Start { get; } = start;

    /// <summary>How many entities this transaction carries.</summary>
    public int Count { get; } = count;

    /// <summary>Summed serialized size of those entities, in bytes.</summary>
    public long Bytes { get; } = bytes;
}

/// <summary>
/// Thrown when one entity is too large to be sent in any transaction at all, so no amount of
/// splitting can help. Raised BEFORE anything is submitted, which is the entire point: the
/// alternative is discovering it partway through a bulk write that has already committed.
/// </summary>
public sealed class BatchPayloadTooLargeException(int index, long bytes, long limit)
    : Exception($"entity at index {index} serializes to {bytes:N0} B, which exceeds the {limit:N0} B "
        + "transaction budget on its own. It cannot be batched at any chunk size — model it as "
        + "multiple rows, or move the payload to Blob storage and keep a reference here.")
{
    /// <summary>Position of the offending entity in the caller's collection.</summary>
    public int Index { get; } = index;

    /// <summary>Serialized size of that entity.</summary>
    public long Bytes { get; } = bytes;

    /// <summary>The per-transaction budget it exceeded.</summary>
    public long Limit { get; } = limit;
}

/// <summary>
/// Splits a run of entities into Azure Table transactions that respect BOTH documented limits.
///
/// Chunking on entity count alone is the common implementation and it is wrong for any table
/// carrying binary or large text columns: the service caps a transaction at 100 entities AND at
/// 4 MB, and the REST layer applies a 1.333x base64 expansion to every <c>Edm.Binary</c> byte. A
/// count-only chunker therefore sends a legal-looking 100-entity batch that the service rejects
/// with HTTP 413 — and because earlier chunks have already committed, the caller is left with a
/// partially-written table and no clean way to resume.
///
/// Measured against Azurite, 50 entities of 61,440 B (3,072,000 B raw) succeeded and 51 failed, so
/// the default budget sits just below that at 3,000,000 B of RAW payload, leaving headroom for the
/// base64 tax and the request envelope.
/// </summary>
public static class BatchPlanner
{
    /// <summary>Azure's documented per-transaction entity cap.</summary>
    public const int DefaultMaxCount = 100;

    /// <summary>
    /// Raw-payload budget per transaction. Deliberately below the 4 MB service limit: binary
    /// columns are base64-expanded 1.333x on the wire, so 3 MB raw is ~4 MB encoded.
    /// </summary>
    public const long DefaultMaxBytes = 3_000_000;

    /// <summary>
    /// Plans transactions over <paramref name="sizes"/>, in order, capping on both entity count and
    /// summed bytes. Order is preserved, so a caller can zip the ranges back onto its own list.
    /// </summary>
    /// <exception cref="BatchPayloadTooLargeException">
    /// A single entity exceeds <paramref name="maxBytes"/>, so no split can make it sendable.
    /// </exception>
    public static IReadOnlyList<BatchRange> Plan(
        IReadOnlyList<long> sizes, int maxCount = DefaultMaxCount, long maxBytes = DefaultMaxBytes)
    {
        Guard.NotNull(sizes, nameof(sizes));
        if (maxCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "must be positive");
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "must be positive");

        var ranges = new List<BatchRange>();
        if (sizes.Count == 0) return ranges;

        // Fail before planning rather than mid-flush: an unsendable entity discovered on the third
        // transaction has already had two committed ahead of it.
        for (var i = 0; i < sizes.Count; i++)
            if (sizes[i] > maxBytes)
                throw new BatchPayloadTooLargeException(i, sizes[i], maxBytes);

        var start = 0;
        var count = 0;
        var bytes = 0L;

        for (var i = 0; i < sizes.Count; i++)
        {
            var wouldOverflow = count == maxCount || (count > 0 && bytes + sizes[i] > maxBytes);
            if (wouldOverflow)
            {
                ranges.Add(new BatchRange(start, count, bytes));
                start = i;
                count = 0;
                bytes = 0;
            }

            count++;
            bytes += sizes[i];
        }

        if (count > 0) ranges.Add(new BatchRange(start, count, bytes));
        return ranges;
    }
}
