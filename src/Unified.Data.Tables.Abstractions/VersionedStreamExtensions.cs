using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Unified.Data.Tables;

/// <summary>
/// Versioned-stream helpers over the core <see cref="IStorage{T}"/> surface — the "append an
/// immutable snapshot at version N, read latest / at / at-or-before" shape of event-sourced read
/// models and state snapshots. Like <see cref="AppendLogExtensions"/>, these are thin compositions
/// over <c>IStorage&lt;T&gt;</c> — no new interface, no separate backend — so caching, outcome
/// verbs, and the in-memory fake work unchanged.
/// </summary>
/// <remarks>
/// Rows are keyed <c>{streamId} | RowKeys.VersionKey(version)</c> (inverted, zero-padded — a wire
/// contract byte-compatible with common hand-rolled inverted-version stores), so Azure Tables'
/// native lexical order IS newest-first: "latest" and "at-or-before" are single bounded reads with
/// no client-side sorting. Ids flow through the store's configured <see cref="IdNormalization"/> —
/// use <see cref="IdNormalization.AsWritten"/> for case-sensitive stream ids (the all-digit version
/// segment is unaffected either way).
/// </remarks>
public static class VersionedStreamExtensions
{
    // One validation for every verb: the stream id IS the partition key, so it can never contain
    // the id separator (Combine would fold everything after the first '|' into the RowKey while
    // the partition-scoped reads query the full stream id — appended rows would be invisible), and
    // it is trimmed so the write path (which normalizes the COMBINED id) and the read paths (which
    // normalize the partition argument alone) agree about edge whitespace in either normalization mode.
    private static string ValidStreamId(string streamId)
    {
        if (string.IsNullOrWhiteSpace(streamId))
            throw new ArgumentException("A stream id is required.", nameof(streamId));
        streamId = streamId.Trim();
#if NETSTANDARD2_0
        if (streamId.IndexOf(EntityId.Separator) >= 0)
#else
        if (streamId.Contains(EntityId.Separator, StringComparison.Ordinal))
#endif
            throw new ArgumentException(
                $"A stream id cannot contain the id separator '{EntityId.Separator}' — the stream id IS the partition key.",
                nameof(streamId));
        return streamId;
    }

    /// <summary>
    /// Append <paramref name="snapshot"/> at its <see cref="IVersionedEntity.Version"/> in the
    /// <paramref name="streamId"/> stream. Versions are immutable — appending an existing version
    /// throws <see cref="DuplicateKeyException"/>. The snapshot's <see cref="IEntity.Id"/> is
    /// assigned for you.
    /// </summary>
    /// <param name="storage">The storage to append to.</param>
    /// <param name="streamId">The stream id (the partition key).</param>
    /// <param name="snapshot">The snapshot to persist (its <see cref="IEntity.Id"/> is overwritten).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted snapshot, including its populated <see cref="IEntity.ETag"/>.</returns>
    public static Task<T> AppendVersionAsync<T>(
        this IStorage<T> storage, string streamId, T snapshot, CancellationToken ct = default)
        where T : class, IVersionedEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        Guard.NotNull(snapshot, nameof(snapshot));
        streamId = ValidStreamId(streamId);

        snapshot.Id = EntityId.Combine(streamId, RowKeys.VersionKey(snapshot.Version));
        return storage.CreateAsync(snapshot, ct);
    }

    /// <summary>The snapshot at exactly <paramref name="version"/>, or <c>null</c> when that version was never appended.</summary>
    /// <param name="storage">The storage to read from.</param>
    /// <param name="streamId">The stream id.</param>
    /// <param name="version">The exact version to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<T?> AtVersionAsync<T>(
        this IStorage<T> storage, string streamId, int version, CancellationToken ct = default)
        where T : class, IVersionedEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        streamId = ValidStreamId(streamId);

        return storage.OneAsync(EntityId.Combine(streamId, RowKeys.VersionKey(version)), ct);
    }

    /// <summary>
    /// The newest snapshot in the stream, or <c>null</c> for an empty stream — a single bounded
    /// read (inverted version keys sort the newest first).
    /// </summary>
    /// <param name="storage">The storage to read from.</param>
    /// <param name="streamId">The stream id.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<T?> LatestAsync<T>(
        this IStorage<T> storage, string streamId, CancellationToken ct = default)
        where T : class, IVersionedEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        streamId = ValidStreamId(streamId);

        var page = await storage.QueryAsync(new QueryOptions { Partition = streamId, Take = 1 }, ct).ConfigureAwait(false);
        return page.Count > 0 ? page[0] : null;
    }

    /// <summary>
    /// The newest snapshot whose version is &lt;= <paramref name="version"/> ("state as of"), or
    /// <c>null</c> when the stream has no snapshot at or below it. A single server-side bounded
    /// read: the version filter runs in the service and the inverted key order makes the first
    /// match the highest qualifying version. NOTE: unlike the key-addressed reads, this filters on
    /// the <c>Version</c> COLUMN — every row this pack writes carries it, but a legacy row written
    /// by another layer may not, and Azure Tables excludes rows lacking a filtered column. Backfill
    /// the column before using "state as of" over foreign rows.
    /// </summary>
    /// <param name="storage">The storage to read from.</param>
    /// <param name="streamId">The stream id.</param>
    /// <param name="version">The inclusive upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<T?> AtOrBeforeAsync<T>(
        this IStorage<T> storage, string streamId, int version, CancellationToken ct = default)
        where T : class, IVersionedEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        streamId = ValidStreamId(streamId);

        var matches = await storage.QueryAsync(x => x.Version <= version, streamId, take: 1, ct).ConfigureAwait(false);
        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>
    /// Stream the snapshot history newest-first (optionally capped at <paramref name="take"/>) —
    /// never cached, never buffered.
    /// </summary>
    /// <param name="storage">The storage to read from.</param>
    /// <param name="streamId">The stream id.</param>
    /// <param name="take">Optional maximum number of snapshots.</param>
    /// <param name="ct">Cancellation token.</param>
    public static IAsyncEnumerable<T> HistoryAsync<T>(
        this IStorage<T> storage, string streamId, int? take = null, CancellationToken ct = default)
        where T : class, IVersionedEntity, new()
    {
        Guard.NotNull(storage, nameof(storage));
        streamId = ValidStreamId(streamId);

        return storage.QueryStreamAsync(new QueryOptions { Partition = streamId, Take = take }, ct);
    }

    /// <summary>Throwing variant of <see cref="AtVersionAsync{T}"/>.</summary>
    /// <param name="storage">The storage to read from.</param>
    /// <param name="streamId">The stream id.</param>
    /// <param name="version">The exact version to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">The stream has no snapshot at that version.</exception>
    public static async Task<T> GetAtVersionAsync<T>(
        this IStorage<T> storage, string streamId, int version, CancellationToken ct = default)
        where T : class, IVersionedEntity, new()
        => await storage.AtVersionAsync(streamId, version, ct).ConfigureAwait(false)
           ?? throw new KeyNotFoundException($"{typeof(T).Name} stream '{streamId}' has no version {version}.");

    /// <summary>Throwing variant of <see cref="LatestAsync{T}"/>.</summary>
    /// <param name="storage">The storage to read from.</param>
    /// <param name="streamId">The stream id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">The stream has no versions at all.</exception>
    public static async Task<T> GetLatestAsync<T>(
        this IStorage<T> storage, string streamId, CancellationToken ct = default)
        where T : class, IVersionedEntity, new()
        => await storage.LatestAsync(streamId, ct).ConfigureAwait(false)
           ?? throw new KeyNotFoundException($"{typeof(T).Name} stream '{streamId}' has no versions.");

    /// <summary>Throwing variant of <see cref="AtOrBeforeAsync{T}"/>.</summary>
    /// <param name="storage">The storage to read from.</param>
    /// <param name="streamId">The stream id.</param>
    /// <param name="version">The inclusive upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">The stream has no snapshot at or below that version.</exception>
    public static async Task<T> GetAtOrBeforeAsync<T>(
        this IStorage<T> storage, string streamId, int version, CancellationToken ct = default)
        where T : class, IVersionedEntity, new()
        => await storage.AtOrBeforeAsync(streamId, version, ct).ConfigureAwait(false)
           ?? throw new KeyNotFoundException($"{typeof(T).Name} stream '{streamId}' has no version at or below {version}.");
}
