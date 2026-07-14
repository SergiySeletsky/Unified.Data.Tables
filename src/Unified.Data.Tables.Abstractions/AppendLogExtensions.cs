using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Unified.Data.Tables;

/// <summary>
/// Append-log / event-stream helpers over the core <see cref="IStorage{T}"/> surface — the "append an
/// event, read the newest N in order" shape for events, chat messages, agent runs, and audit rows.
/// Built entirely on <see cref="IStorage{T}.CreateAsync"/> and the bounded
/// <see cref="IStorage{T}.QueryAsync(QueryOptions, CancellationToken)"/>, so they work against any
/// implementation (Azure, in-memory, custom) with no interface change.
/// </summary>
/// <remarks>
/// Rows are keyed with <see cref="RowKeys.AppendKey"/> (inverted ticks), so Azure Tables' native
/// lexical RowKey order IS newest-first — <see cref="RecentAsync"/> is a single bounded partition scan
/// with no client-side sorting. An optional sub-stream discriminator lets one partition hold several
/// independent streams (e.g. per-session chat) that <see cref="RecentAsync"/> isolates by RowKey prefix.
/// Newest-first holds WITHIN a stream; a bare read across multiple sub-streams is grouped by sub-stream,
/// so pass a sub-stream when you need one stream's global newest-N.
/// </remarks>
public static class AppendLogExtensions
{
    /// <summary>
    /// Append <paramref name="entity"/> to the <paramref name="partition"/> stream with a time-ordered,
    /// collision-resistant RowKey (inverted ticks + a random uniquifier), so later appends sort first.
    /// The entity's <see cref="Entity.Id"/> is assigned for you.
    /// </summary>
    /// <param name="storage">The storage to append to.</param>
    /// <param name="partition">The stream's partition key.</param>
    /// <param name="entity">The event to append (its <see cref="Entity.Id"/> is overwritten).</param>
    /// <param name="subStream">Optional sub-stream discriminator isolating one stream within the partition.</param>
    /// <param name="at">The event timestamp; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entity, including its populated <see cref="Entity.ETag"/>.</returns>
    public static Task<T> AppendAsync<T>(
        this IStorage<T> storage, string partition, T entity,
        string? subStream = null, DateTimeOffset? at = null, CancellationToken ct = default)
        where T : class, IEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        Guard.NotNull(entity, nameof(entity));
        if (string.IsNullOrWhiteSpace(partition))
            throw new ArgumentException("A partition is required for an append.", nameof(partition));

        // Match the id normalization CreateAsync will apply, so RecentAsync's prefix lines up.
        var normalizedPartition = EntityId.Normalize(partition);
        var normalizedSubStream = string.IsNullOrWhiteSpace(subStream) ? null : EntityId.Normalize(subStream!);
        var uniquifier = Guid.NewGuid().ToString("N").Substring(0, 8);

        var rowKey = RowKeys.AppendKey(at ?? DateTimeOffset.UtcNow, normalizedSubStream, uniquifier);
        entity.Id = EntityId.Combine(normalizedPartition, rowKey);
        return storage.CreateAsync(entity, ct);
    }

    /// <summary>
    /// Read the most recent <paramref name="count"/> events. For a SINGLE stream (all bare appends, or a
    /// specific <paramref name="subStream"/>) this is newest-first from one bounded partition scan with no
    /// client-side sort. Across MULTIPLE sub-streams, a bare read (no <paramref name="subStream"/>) is
    /// ordered by sub-stream THEN time — not globally newest-first — because the <c>"{subStream}~"</c>
    /// prefix sorts ahead of the ticks; pass the <paramref name="subStream"/> to read one stream's newest N.
    /// </summary>
    /// <param name="storage">The storage to read from.</param>
    /// <param name="partition">The stream's partition key.</param>
    /// <param name="count">Maximum number of events to return (must be positive).</param>
    /// <param name="subStream">Optional sub-stream discriminator (must match the one used on append). Omit
    /// it to read the whole partition — which includes events that WERE written with a sub-stream.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<IReadOnlyList<T>> RecentAsync<T>(
        this IStorage<T> storage, string partition, int count,
        string? subStream = null, CancellationToken ct = default)
        where T : class, IEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        if (string.IsNullOrWhiteSpace(partition))
            throw new ArgumentException("A partition is required.", nameof(partition));
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be positive.");

        var normalizedPartition = EntityId.Normalize(partition);
        var prefix = string.IsNullOrWhiteSpace(subStream)
            ? null
            : RowKeys.SubStreamPrefix(EntityId.Normalize(subStream!));

        return storage.QueryAsync(
            new QueryOptions { Partition = normalizedPartition, RowKeyPrefix = prefix, Take = count }, ct);
    }
}
