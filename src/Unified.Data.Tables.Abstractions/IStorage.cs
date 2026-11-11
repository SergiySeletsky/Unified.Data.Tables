using System.Linq.Expressions;

namespace Unified.Data.Tables;

/// <summary>
/// Generic persistence contract over a single Azure Table (one table per entity type
/// <typeparamref name="T"/>). Implemented by <c>TableStorage&lt;T&gt;</c> in the Unified.Data.Tables package.
/// </summary>
/// <typeparam name="T">The entity type; implements <see cref="IEntity"/> (derive from <see cref="Entity"/> for the convenience base) and has a public parameterless constructor.</typeparam>
/// <remarks>
/// A type with large values is one whose declared types have a <see cref="TableValueSplitAttribute"/>
/// or <see cref="TableBlobContent"/> member, or that carries the attribute on its class. For such a type
/// every read that returns entities (<see cref="OneAsync"/>, the <c>QueryAsync</c> and
/// <c>QueryStreamAsync</c> overloads, <see cref="QueryPageAsync"/>, <see cref="AnyAsync"/>) reads a row's
/// continuation rows and blobs after its main row. When a writer replaced the row in between, the read is
/// retried from the new version, five times in all; when the row changed during each attempt it throws
/// <see cref="ConcurrencyConflictException"/> (nothing is damaged; repeat the read), and only a row whose
/// version did not change is reported as <see cref="TableValueIntegrityException"/>. In a query, a stream
/// or a page that exception ends the whole call.
/// </remarks>
public interface IStorage<T> where T : class, IEntity, new()
{
    /// <summary>
    /// Delete a single document by its composite id. For a type with large values (a
    /// <see cref="TableValueSplitAttribute"/> or <see cref="TableBlobContent"/> property) the call reads
    /// the row first, to find its continuation rows and blobs, and deletes it on condition of the version
    /// it read: a delete that loses a race to another writer reads again and retries after a short
    /// pause, and can throw <see cref="ConcurrencyConflictException"/> after five lost races. For such a
    /// type, when no row has the id, the call also looks for continuation rows left behind for it and
    /// removes them.
    /// </summary>
    /// <param name="id">Composite document identifier (<c>"{PartitionKey}|{RowKey}"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Delete every document in a partition in batched transactions of up to 100 rows. Atomic per
    /// transaction only: when one fails or the token is cancelled, the call throws and the earlier
    /// transactions stay committed; calling it again finishes the delete. For a type with large values a
    /// row and its continuation rows are always deleted in the same transaction, and an entity written
    /// while the delete runs is, after it, either whole or gone: continuation rows listed without their
    /// main row are deleted last, only when no main row exists for them and each is still the version
    /// that was listed (a row that changed is left alone, and the call does not fail because of it). A
    /// type without large values deletes every listed row by key, as before; on a table that a type with
    /// large values also writes, such a delete can remove continuation rows of an entity written while it
    /// runs.
    /// </summary>
    /// <param name="partition">The partition key whose entities should be removed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entities deleted (continuation rows are not counted).</returns>
    Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default);

    /// <summary>
    /// Insert a new document; throws <see cref="DuplicateKeyException"/> when the id already
    /// exists (use <c>StorageExtensions.GetOrCreateAsync</c> when that is an expected outcome).
    /// For a type with large values the call reads nothing first; it takes back continuation rows the
    /// entity left behind earlier when the new value needs their keys, and ends in
    /// <see cref="ConcurrencyConflictException"/> only if those rows keep changing or vanishing during its
    /// five attempts. A row of another writer at such a key is never overwritten: the call throws
    /// <see cref="InvalidOperationException"/> naming the key and stores nothing.
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
    /// For a type with large values (a <see cref="TableValueSplitAttribute"/> or
    /// <see cref="TableBlobContent"/> property) it is not a single round trip: the call reads the row
    /// first, to find the continuation rows and blobs the new value replaces, and writes on condition of
    /// the version it read. The caller's ETag is still ignored and the last writer still wins, but an
    /// upsert that loses a race to another writer reads again and retries after a short pause, and can
    /// throw <see cref="ConcurrencyConflictException"/> after five lost races. A continuation row the
    /// entity left behind earlier is taken back when the new value needs its key; one that another caller
    /// removes meanwhile counts as a lost race. A row of another writer at the key of a new continuation
    /// row is never overwritten: the call throws <see cref="InvalidOperationException"/> naming the key and
    /// stores nothing.
    /// </summary>
    /// <param name="entity">The entity to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entity, including its refreshed <see cref="Entity.ETag"/>.</returns>
    Task<T> UpsertAsync(T entity, CancellationToken ct = default);

    /// <summary>
    /// Replace an existing document in full using <see cref="ConcurrencyMode.Auto"/> semantics:
    /// the caller-supplied <see cref="Entity.ETag"/> is enforced (a conflict throws
    /// <see cref="ConcurrencyConflictException"/>). An entity WITHOUT an ETag throws
    /// <see cref="InvalidOperationException"/> — there is no version to check; round-trip the ETag,
    /// use <c>StorageExtensions.MutateAsync</c>, or say <see cref="ConcurrencyMode.LastWriterWins"/>
    /// explicitly.
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
    /// For a type with a <see cref="TableValueSplitAttribute"/> or <see cref="TableBlobContent"/> property,
    /// a <see cref="ConcurrencyMode.LastWriterWins"/> update reads the row first and can throw
    /// <see cref="ConcurrencyConflictException"/> after five lost races.
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

    /// <summary>
    /// Check whether a document with the given composite id exists. A large value is never assembled or
    /// downloaded to answer: a row with continuation rows or an offloaded value, or one whose part count is
    /// damaged (it exists and can be deleted, but not read), answers <c>true</c> and is not cached.
    /// </summary>
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
    /// <see cref="QueryOptions.ContinuationToken"/> to resume. Never cached. For a type with large values a
    /// continuation row takes a place in a page, so a page can hold fewer entities than the page size, or
    /// none, and still carry a cursor: keep paging until it is null.
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
    /// For a type with large values (split cells, continuation rows, offloaded blobs) each entity's rows
    /// are one unit that never straddles two chunks, so a chunk may hold fewer than 100 entities; a key
    /// repeated within the batch throws <see cref="DuplicateKeyException"/> before anything is written.
    /// </summary>
    /// <param name="entities">The entities to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entities written.</returns>
    Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default);

    /// <summary>
    /// Transactional insert-or-replace, grouped by partition and chunked at 100 entities per
    /// transaction. Last writer wins per row. Every entity's <see cref="Entity.ETag"/> is reset to
    /// <c>null</c> — re-read before optimistic updates. For a type with large values each entity's rows
    /// (including deletes of stale continuation rows) are one unit that never straddles two chunks, and a
    /// key repeated within the batch is written once, with its last occurrence. Such a type reads each row
    /// first and replaces it conditionally: a chunk that loses a race to another writer is re-read and
    /// retried after a short pause, and after five lost races the call throws
    /// <see cref="ConcurrencyConflictException"/> (chunks already written stay written). The blobs the
    /// replaced rows no longer reference are deleted once, after the last transaction; a batch that fails
    /// part way deletes none of them (the orphan sweep does), so the same batch can be sent again.
    /// </summary>
    /// <param name="entities">The entities to upsert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The number of entities passed: a row named more than once counts each time and is written once.
    /// </returns>
    Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default);

    /// <summary>
    /// Count rows in the table, or in one partition. Azure Tables has no server-side count — this
    /// enumerates keys-only pages (a transfer-light projection), so it is O(n) round trips.
    /// </summary>
    /// <param name="partition">Optional partition key to scope the count.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountAsync(string? partition = null, CancellationToken ct = default);
}
