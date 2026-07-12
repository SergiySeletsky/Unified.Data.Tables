using System.Linq.Expressions;

namespace Unified.Data.Tables;

/// <summary>
/// Generic persistence contract over a single Azure Table (one table per entity type
/// <typeparamref name="T"/>). Implemented by <c>TableStorage&lt;T&gt;</c> in the Unified.Data.Tables package.
/// </summary>
/// <typeparam name="T">The entity type; must derive from <see cref="Entity"/> and have a public parameterless constructor.</typeparam>
public interface IStorage<T> where T : Entity, new()
{
    /// <summary>Delete a single document by its composite id.</summary>
    /// <param name="id">Composite document identifier (<c>"{PartitionKey}|{RowKey}"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Delete every document in a partition using batched transactions (up to 100 entities per batch).
    /// </summary>
    /// <param name="partition">The partition key whose entities should be removed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entities deleted.</returns>
    Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default);

    /// <summary>
    /// Insert a new document; throws <see cref="DuplicateKeyException"/> when the id already
    /// exists (use <c>StorageExtensions.GetOrCreateAsync</c> when that is an expected outcome).
    /// </summary>
    /// <param name="entity">The entity to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entity, including its populated <see cref="Entity.ETag"/>.</returns>
    Task<T> CreateAsync(T entity, CancellationToken ct = default);

    /// <summary>
    /// Insert-or-replace in a single round trip. Unconditional by design — no ETag check, last
    /// writer wins ("make the row look like this object"). Callers who need optimistic concurrency
    /// use <see cref="UpdateAsync(T, CancellationToken)"/>. <see cref="Entity.CreatedAt"/> is
    /// preserved when the caller supplies it (e.g. a read-modify-write flow) and stamped with
    /// UtcNow when left at its default; <see cref="Entity.UpdatedAt"/> is always stamped.
    /// </summary>
    /// <param name="entity">The entity to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entity, including its refreshed <see cref="Entity.ETag"/>.</returns>
    Task<T> UpsertAsync(T entity, CancellationToken ct = default);

    /// <summary>
    /// Replace an existing document in full using <see cref="ConcurrencyMode.Auto"/> semantics:
    /// if the caller supplies <see cref="Entity.ETag"/>, strict optimistic concurrency is
    /// enforced (a conflict throws <see cref="ConcurrencyConflictException"/>); otherwise a
    /// stale-cache conflict is retried once.
    /// </summary>
    /// <param name="entity">The entity to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entity, including its refreshed <see cref="Entity.ETag"/>.</returns>
    Task<T> UpdateAsync(T entity, CancellationToken ct = default);

    /// <summary>
    /// Replace an existing document in full with explicit concurrency semantics — see
    /// <see cref="ConcurrencyMode"/>. A failed concurrency check throws
    /// <see cref="ConcurrencyConflictException"/>. Prefer builder-based
    /// <see cref="UpdateAsync(string, Action{UpdateBuilder{T}}, CancellationToken)"/> for
    /// disjoint-field mutation (race-safe by construction), and
    /// <c>StorageExtensions.MutateAsync</c> for values derived from the current row.
    /// </summary>
    /// <param name="entity">The entity to persist.</param>
    /// <param name="mode">The optimistic-concurrency behaviour to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entity, including its refreshed <see cref="Entity.ETag"/>.</returns>
    Task<T> UpdateAsync(T entity, ConcurrencyMode mode, CancellationToken ct = default);

    /// <summary>
    /// Apply a builder-driven partial update to a single document by id. Only the declared columns
    /// are written (server-side <c>Merge</c>); untouched columns are preserved, and no read is
    /// needed — concurrent updates to disjoint columns never conflict. When the builder carries
    /// <see cref="UpdateBuilder{T}.WithETag"/>, the merge is conditional and a lost race throws
    /// <see cref="ConcurrencyConflictException"/>.
    /// </summary>
    /// <param name="id">Composite document identifier.</param>
    /// <param name="builderAction">Configures which properties to mutate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The row's new ETag (empty when the backend did not report one).</returns>
    Task<string> UpdateAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct = default);

    /// <summary>Get a single document by its composite id, or <c>null</c> when it does not exist.</summary>
    /// <param name="id">Composite document identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<T?> OneAsync(string id, CancellationToken ct = default);

    /// <summary>Check whether a document with the given composite id exists.</summary>
    /// <param name="id">Composite document identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ExistsAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Query documents, optionally scoped to a single partition. When <paramref name="partition"/>
    /// is <c>null</c> or whitespace, the entire table is returned. This is the cached read path.
    /// </summary>
    /// <param name="partition">Optional partition key to scope the query.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default);

    /// <summary>
    /// Bounded query — partition scope, RowKey-prefix range, and/or Take. Unlike
    /// <see cref="QueryAsync(string?, CancellationToken)"/>, results are never cached.
    /// </summary>
    /// <param name="options">The query bounds.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default);

    /// <summary>
    /// Query documents with a <b>server-side</b> LINQ predicate translated to an Azure Tables OData
    /// filter (see <c>TableFilterTranslator</c>). Optionally scope to one <paramref name="partition"/>
    /// and cap with <paramref name="take"/>. Never cached; an unsupported predicate throws
    /// <see cref="System.NotSupportedException"/>.
    /// </summary>
    /// <param name="predicate">The filter, e.g. <c>x =&gt; x.Status == Status.Open &amp;&amp; x.Count &gt; 0</c>.</param>
    /// <param name="partition">Optional partition scope.</param>
    /// <param name="take">Optional maximum number of results.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default);

    /// <summary>
    /// Streaming variant of <see cref="QueryAsync(QueryOptions, CancellationToken)"/> for large
    /// result sets: never caches, never buffers. <c>null</c> options streams the whole table.
    /// </summary>
    /// <param name="options">Optional query bounds.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Streaming variant of <see cref="QueryAsync(Expression{Func{T, bool}}, string?, int?, CancellationToken)"/>
    /// — never caches, never buffers.
    /// </summary>
    /// <param name="predicate">The server-side filter predicate.</param>
    /// <param name="partition">Optional partition scope.</param>
    /// <param name="take">Optional maximum number of results.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<T> QueryStreamAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default);

    /// <summary>
    /// Fetch one server page plus an opaque cursor for the next page — the resumable paging primitive
    /// for grids and infinite scroll. <paramref name="options"/>'s <see cref="QueryOptions.Take"/> is
    /// the page size (default 100, clamped to 1..1000); pass a prior page's
    /// <see cref="QueryOptions.ContinuationToken"/> to resume. Never cached.
    /// </summary>
    /// <param name="options">Query bounds; <see cref="QueryOptions.Take"/> is the page size and
    /// <see cref="QueryOptions.ContinuationToken"/> the resume cursor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One page of results and, when more remain, a cursor bound to these exact bounds.</returns>
    Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default);

    /// <summary>
    /// Whether any document matches the server-side <paramref name="predicate"/> — a
    /// <c>Take(1)</c> existence check that stops at the first hit.
    /// </summary>
    /// <param name="predicate">The server-side filter predicate.</param>
    /// <param name="partition">Optional partition scope.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, string? partition = null, CancellationToken ct = default);

    /// <summary>
    /// Transactional inserts, grouped by partition and chunked at 100 entities per transaction.
    /// Atomic per chunk only. Any existing key fails its chunk with
    /// <see cref="DuplicateKeyException"/>. Batch sub-responses are not correlated back, so every
    /// entity's <see cref="Entity.ETag"/> is reset to <c>null</c> — re-read before optimistic updates.
    /// </summary>
    /// <param name="entities">The entities to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entities written.</returns>
    Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default);

    /// <summary>
    /// Transactional insert-or-replace, grouped by partition and chunked at 100 entities per
    /// transaction. Last writer wins per row. Every entity's <see cref="Entity.ETag"/> is reset to
    /// <c>null</c> — re-read before optimistic updates.
    /// </summary>
    /// <param name="entities">The entities to upsert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entities written.</returns>
    Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default);

    /// <summary>
    /// Count rows in the table, or in one partition. Azure Tables has no server-side count — this
    /// enumerates keys-only pages (a transfer-light projection), so it is O(n) round trips.
    /// </summary>
    /// <param name="partition">Optional partition key to scope the count.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountAsync(string? partition = null, CancellationToken ct = default);
}
