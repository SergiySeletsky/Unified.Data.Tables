using System.Diagnostics.CodeAnalysis;

namespace Unified.Data.Tables;

/// <summary>
/// Persistence contract for a table holding MANY concrete types under one common base,
/// discriminated by the <see cref="SystemColumnNames.TypeName"/> column and read back as
/// <typeparamref name="TBase"/> with the true derived instance intact.
/// </summary>
/// <remarks>
/// A sibling of <see cref="IStorage{T}"/>, not a replacement. <see cref="IStorage{T}"/>'s
/// <c>where T : class, IEntity, new()</c> cannot express this and relaxing it would not help: an
/// abstract or interface base fails <c>new()</c>, and a message or event base typically does not
/// implement <see cref="IEntity"/> at all. <typeparamref name="TBase"/> is therefore constrained
/// only to <c>class</c>.
/// <para>
/// <b>The store owns its table.</b> There is no server-side type filter, so every enumerating
/// operation returns, counts or deletes every row in scope regardless of discriminator. Point two
/// stores at one table and each will see the other's rows.
/// </para>
/// <para>
/// Rows are immutable facts plus mutable system columns. There is no whole-row update, no
/// <see cref="ConcurrencyMode"/>, no <see cref="ProtectedPropertyAttribute"/> enforcement and no
/// caching — see <see cref="MergeColumnsAsync"/> for the one supported mutation.
/// </para>
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
public interface IPolymorphicStorage<TBase>
    where TBase : class
{
    /// <summary>
    /// Eagerly create the underlying table. It is otherwise created lazily on first use; call this
    /// from host startup for fail-fast semantics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the table exists.</returns>
    Task EnsureCreatedAsync(CancellationToken ct = default);

    /// <summary>
    /// Insert one row. Strict by design — an existing key throws
    /// <see cref="DuplicateKeyException"/>, because a polymorphic table is normally an append-only
    /// log whose de-duplication guarantee IS the insert.
    /// </summary>
    /// <remarks>
    /// The returned entry carries the <see cref="PolymorphicEntry{TBase}.ETag"/> the backend reported
    /// for the write, but its <see cref="PolymorphicEntry{TBase}.Timestamp"/> is <b>null</b>: a write
    /// response does not include the service's last-write time, so it is unknown until the row is
    /// read back. Same doctrine as <see cref="IEntity.Timestamp"/> after <see cref="IStorage{T}"/>'s
    /// <c>CreateAsync</c>.
    /// </remarks>
    /// <param name="write">The row to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The written row, read back as an entry.</returns>
    /// <exception cref="DuplicateKeyException">A row already exists at that key.</exception>
    Task<PolymorphicEntry<TBase>> InsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default);

    /// <summary>Insert-or-replace one row, unconditionally.</summary>
    /// <remarks>
    /// As with <see cref="InsertAsync"/>, the returned entry's
    /// <see cref="PolymorphicEntry{TBase}.Timestamp"/> is null — a write does not report one.
    /// </remarks>
    /// <param name="write">The row to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The written row, read back as an entry.</returns>
    Task<PolymorphicEntry<TBase>> UpsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default);

    /// <summary>
    /// Transactional insert of a HETEROGENEOUS set — each write may carry a different concrete type
    /// (and so a different discriminator), or no type at all. Grouped by partition and chunked by
    /// <see cref="BatchPlanner"/> on both entity count and payload bytes, so a batch is atomic
    /// <em>per chunk</em> only.
    /// </summary>
    /// <remarks>
    /// Strict, exactly like <see cref="InsertAsync"/>: a key that already exists in the table AND a
    /// key repeated twice <em>within one batch</em> both throw <see cref="DuplicateKeyException"/>.
    /// Azure reports the two differently (409 versus 400 <c>InvalidDuplicateRow</c>); both are
    /// mapped, so the in-memory fake and the Azure store fail the same way rather than one storing a
    /// single row and reporting two.
    /// </remarks>
    /// <param name="writes">The rows to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many rows were written.</returns>
    /// <exception cref="BatchPayloadTooLargeException">One row is too large to batch at any size.</exception>
    /// <exception cref="DuplicateKeyException">
    /// A key already exists in the table, or the same key appears twice within
    /// <paramref name="writes"/>.
    /// </exception>
    Task<int> InsertBatchAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default);

    /// <summary>
    /// Blind, unconditional server-side merge of system columns onto an existing row — no prior
    /// read, no ETag. The "mark as published / committed" primitive.
    /// </summary>
    /// <remarks>
    /// Every column name must satisfy <see cref="SystemColumnNames.IsSystemColumn"/>, and
    /// <see cref="SystemColumnNames.TypeName"/> is rejected: re-typing a row would strand the
    /// previous type's data columns on it.
    /// <para>
    /// <b>Merging a row that does not exist throws <c>Azure.RequestFailedException</c> with
    /// <c>Status == 404</c></b> — on the in-memory implementation too, which raises the same type so
    /// a test written against the fake still describes production. This is the one place a provider
    /// type surfaces through an otherwise provider-agnostic contract: there is no read to turn the
    /// miss into a null, and inventing a provider-neutral exception here would make the two
    /// implementations disagree with every other 404 in the library. Documented rather than wrapped;
    /// catching it means referencing <c>Azure.Core</c>.
    /// </para>
    /// </remarks>
    /// <param name="key">The row to patch.</param>
    /// <param name="columns">The system columns to set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the merge is applied.</returns>
    /// <exception cref="ArgumentException">A column name is not a system column, or is the discriminator.</exception>
    /// <exception cref="T:Azure.RequestFailedException">
    /// No row exists at <paramref name="key"/> (<c>Status == 404</c>). Referenced by documentation ID
    /// because this assembly deliberately takes no Azure dependency.
    /// </exception>
    // The "T:" prefix is the only way to put an <exception> element on the XML docs for a type this
    // assembly cannot reference: an ordinary cref would be CS1574. CA1200 exists to catch a prefix
    // used by accident, where the compiler would otherwise have validated the target — here there is
    // deliberately nothing to validate against, and the alternative (dropping the tag) would hide the
    // one provider exception that escapes this contract from every IntelliSense exception list.
    [SuppressMessage("Documentation", "CA1200:Avoid using cref tags with a prefix",
        Justification = "Azure.RequestFailedException is not referenceable from this dependency-free assembly.")]
    Task MergeColumnsAsync(TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct = default);

    /// <summary>Read one row, or null when it does not exist.</summary>
    /// <param name="key">The row address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The entry, or null.</returns>
    Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default);

    /// <summary>Buffered read of a partition, or of the whole table when no partition is given.</summary>
    /// <param name="partition">The partition to read, or null for the whole table.</param>
    /// <param name="take">Maximum rows to return, or null for all of them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rows, in lexical (PartitionKey, RowKey) order.</returns>
    Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(
        string? partition = null, int? take = null, CancellationToken ct = default);

    /// <summary>
    /// Streaming read. Follows continuation tokens internally, so a caller never writes the
    /// <c>do { } while (continuationToken != null)</c> loop — nor omits it and silently truncates
    /// at one segment.
    /// </summary>
    /// <param name="partition">The partition to read, or null for the whole table.</param>
    /// <param name="take">Maximum rows to yield, or null for all of them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rows, in lexical (PartitionKey, RowKey) order.</returns>
    IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(
        string? partition = null, int? take = null, CancellationToken ct = default);

    /// <summary>Delete one row. Deleting a missing row is a no-op.</summary>
    /// <param name="key">The row address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the row is gone.</returns>
    Task DeleteAsync(TableKey key, CancellationToken ct = default);

    /// <summary>Delete every row in a partition, whatever its type.</summary>
    /// <param name="partition">The partition to clear.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many rows were deleted.</returns>
    Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default);

    /// <summary>
    /// Count rows in a partition, or in the whole table. Azure Tables has no server-side count, so
    /// this is a keys-only scan — O(n) round trips.
    /// </summary>
    /// <param name="partition">The partition to count, or null for the whole table.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The row count.</returns>
    Task<int> CountAsync(string? partition = null, CancellationToken ct = default);
}
