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

    /// <summary>Insert a new document.</summary>
    /// <param name="entity">The entity to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entity, including its populated <see cref="Entity.ETag"/>.</returns>
    Task<T> CreateAsync(T entity, CancellationToken ct = default);

    /// <summary>
    /// Replace an existing document in full. If the caller supplies <see cref="Entity.ETag"/>,
    /// strict optimistic concurrency is enforced; otherwise a stale-cache conflict is retried once.
    /// </summary>
    /// <param name="entity">The entity to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entity, including its refreshed <see cref="Entity.ETag"/>.</returns>
    Task<T> UpdateAsync(T entity, CancellationToken ct = default);

    /// <summary>
    /// Apply a builder-driven partial update to a single document by id. Only the declared columns
    /// are written (server-side <c>Merge</c>); untouched columns are preserved, and no read is needed.
    /// </summary>
    /// <param name="id">Composite document identifier.</param>
    /// <param name="builderAction">Configures which properties to mutate.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct = default);

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
    /// is <c>null</c> or whitespace, the entire table is returned.
    /// </summary>
    /// <param name="partition">Optional partition key to scope the query.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default);
}
