using System.Collections.Generic;

namespace Unified.Data.Tables;

/// <summary>
/// One server-returned page of a query plus an opaque, query-bound cursor for the next page. Returned
/// by <see cref="IStorage{T}.QueryPageAsync(QueryOptions, System.Threading.CancellationToken)"/>.
/// (Named to avoid a clash with <c>Azure.Page&lt;T&gt;</c>.)
/// </summary>
/// <remarks>
/// The cursor in <see cref="ContinuationToken"/> is bound to the exact query bounds it was issued for
/// (partition, RowKey prefix, page size) — replaying it against a different query throws. There is
/// deliberately no total count: Azure Tables has no server-side count, so a total would force a second
/// full scan. Drive grids and infinite scroll off <see cref="HasMore"/> and the cursor; use
/// <see cref="IStorage{T}.CountAsync(string?, System.Threading.CancellationToken)"/> only when a count
/// is genuinely needed. <see cref="Items"/> may be shorter than the requested page size even when more
/// pages remain (the backend's page size is a hint), and <see cref="HasMore"/> may be <c>true</c> even when
/// the following page turns out empty (Azure can return a trailing cursor) — a correct loop fetches until
/// <see cref="HasMore"/> is <c>false</c>, never keying off <see cref="Items"/> count.
/// </remarks>
/// <typeparam name="T">The entity type.</typeparam>
/// <param name="Items">The entities in this page, in lexical (PartitionKey, RowKey) order.</param>
/// <param name="ContinuationToken">Opaque cursor for the next page, or <c>null</c> when the scan is exhausted.</param>
public sealed record EntityPage<T>(IReadOnlyList<T> Items, string? ContinuationToken)
{
    /// <summary>Whether another page is available — feed <see cref="ContinuationToken"/> back to fetch it.</summary>
    public bool HasMore => ContinuationToken is not null;
}
