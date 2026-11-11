using System.Buffers;
using System.Security.Cryptography;
using Azure.Data.Tables;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables;

/// <summary>
/// The blob-offload tier of one table: prepares <see cref="TableBlobContent"/> values, serializes an
/// entity so that its row and continuation rows fit one entity-group transaction (moving the largest
/// split values to blobs until they do), uploads the blobs a write needs, and resolves the blob
/// references of a row on read.
/// </summary>
/// <param name="blobs">The blob store; null when blob offload is not configured.</param>
/// <param name="options">The offload settings; null when blob offload is not configured.</param>
/// <param name="tableName">The store's resolved table name, the first segment of every blob name.</param>
internal sealed class ValueOffloadCoordinator(IOffloadBlobStore? blobs, BlobOffloadOptions? options, string tableName)
{
    /// <summary>The service limit on a blob name.</summary>
    internal const int MaxBlobNameLength = 1_024;

    private const int CopyBufferBytes = 81_920;

    private static readonly IReadOnlyDictionary<string, object> NothingResolved = new Dictionary<string, object>(StringComparer.Ordinal);

    /// <summary>
    /// Called with a row's partition and row key when a lazy content this coordinator materialized (on a
    /// read or a write of that row) found its blob missing: the entity it was handed out with is stale,
    /// and the store stops answering that row from its caches. Null when the store has no cache. It is
    /// called before the integrity failure is thrown, and whatever it throws is swallowed.
    /// </summary>
    internal Action<string, string>? BlobMissing { get; set; }

    /// <summary>
    /// The store's entity type, the type a read of every row creates; null: the runtime type of the entity
    /// written. A subclass written through the store is refused a large value in a column this type does not read.
    /// </summary>
    internal Type? EntityType { get; set; }

    /// <summary>
    /// <c>{table}/{partitionKey}/{rowKey}/{column}/{sha256}</c>, each segment escaped by
    /// <see cref="OffloadBlobNames.EscapeSegment"/> (so no segment is ever <c>.</c>, <c>..</c> or empty).
    /// </summary>
    /// <exception cref="ArgumentException">The name exceeds <see cref="MaxBlobNameLength"/> characters.</exception>
    internal static string BlobName(string table, string pk, string rk, string column, string sha256)
    {
        var name = string.Join('/',
            OffloadBlobNames.EscapeSegment(table),
            OffloadBlobNames.EscapeSegment(pk),
            OffloadBlobNames.EscapeSegment(rk),
            OffloadBlobNames.EscapeSegment(column),
            OffloadBlobNames.EscapeSegment(sha256));
        if (name.Length > MaxBlobNameLength)
            throw new ArgumentException(
                $"The blob name for column '{column}' would be {name.Length} characters; blob names are limited to {MaxBlobNameLength}. Use shorter keys.");
        return name;
    }

    /// <summary>
    /// Seals every unsealed <see cref="TableBlobContent"/> of <paramref name="entity"/>: a seekable
    /// source is hashed in one pass and rewound; a non-seekable one is copied while hashing to a
    /// delete-on-close temporary file that becomes the upload source. Content is never buffered in memory.
    /// </summary>
    /// <remarks>
    /// An attempt that fails or is cancelled never leaves a later one the unread tail of a source. A
    /// seekable source is put back where its content starts, so a retry reads all of it; one that cannot
    /// be put back, and a forward-only one that was partly consumed, make the content unusable: this and
    /// every later write of it throws before anything is read or uploaded. So does a content whose
    /// temporary copy an earlier failed write released (<see cref="ReleasePrepared"/>), and a new content
    /// whose stream, or a stream below it that a framework wrapper exposes, a write read part-way or could
    /// not put back where its content started. A new content over a stream that a write which has not
    /// committed (<see cref="Committed"/>) read to its end is read, and refused when it delivers nothing.
    /// <para>
    /// A content held by several writes at once is prepared by one of them; the others wait for it and
    /// find it prepared. Each content is waited for on its own, never two at a time.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A content can no longer be written: its source was partly read by a failed write and cannot be
    /// read again from where the content starts, its temporary copy was released by a failed write, its
    /// source stream (or a stream below it) was read part-way or could not be put back by another write,
    /// or a write that has not committed read it to its end and it delivers nothing now.
    /// </exception>
    internal async Task PrepareAsync(object entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var contents = TableEntitySerializer.FindBlobContents(entity);
        // Counted before anything can fail: the write's ReleasePrepared ends each of them once.
        foreach (var (_, content) in contents)
            content.Enlist();

        // A subclass written through a store of its base type: a content in a column the base type does not
        // read is refused before its source is read (the serializer refuses it too, after this point). After the
        // contents are counted: the write's ReleasePrepared, which runs whatever is thrown here, ends each once.
        if (EntityType is { } readType && entity.GetType() != readType)
        {
            foreach (var (column, _) in contents)
            {
                var property = column.Split(TableEntityValue.PathDelimiter)[0];
                if (!TypeMetadataCache.ReadsBack(readType, property))
                {
                    // A file in a top-level property that is not declared TableBlobContent: a read gives the
                    // content back only to a property declared as one, so that declaration is the remedy named.
                    var contentIn = column == property ? DeclaredAsAnotherType(entity.GetType(), property) : null;
                    throw new NotSupportedException(
                        TableEntitySerializer.SubclassThroughBaseStoreRefusal(entity.GetType(), readType, column, plainStore: false, contentIn));
                }
            }
        }

        // Refused before any source is read and any blob uploaded. A content another write is using is
        // waited for (one write at a time hashes, copies, uploads from or binds a content).
        foreach (var (column, content) in contents)
        {
            await content.WaitTurnAsync(ct);
            try
            {
                content.ThrowIfNotWritable(column);
                // A source prepared by an earlier write that failed must still be there to upload from: one the
                // caller has closed since is found now, not after other blobs of this write were uploaded.
                if (content.IsSealed && content.Source is not null)
                    content.RewindSource(column);
            }
            finally
            {
                content.EndTurn();
            }
        }

        foreach (var (column, content) in contents)
        {
            await content.WaitTurnAsync(ct);
            try
            {
                // Another write may have prepared the content, or given it up, while this one waited.
                content.ThrowIfNotWritable(column);
                if (content.IsSealed) continue;
                var source = content.Source
                    ?? throw new InvalidOperationException($"The TableBlobContent of '{column}' has no content to read.");
                // Before the first read: a write cancelled by now has not touched the source.
                ct.ThrowIfCancellationRequested();
                // A stream a write has read part-way, or could not put back, is refused before this content
                // reads any of it. One that a write which has not committed read to its end is read: a new
                // payload is stored, nothing at all is refused (the retry that would store an empty file).
                var consumed = !content.HasSourceOrigin && content.ClaimSource(column);
                // A source that was seekable when it was first prepared stays on this path: it is read from
                // the recorded origin or not at all, never from wherever it stands now.
                if (content.HasSourceOrigin || source.CanSeek)
                    await HashSeekableAsync(column, content, source, consumed, ct);
                else
                    await SpoolAsync(column, content, source, consumed, ct);
            }
            finally
            {
                content.EndTurn();
            }
        }
    }

    // Hashes a seekable source in one pass from where its content starts and puts it back there: for
    // the upload when the pass succeeded, for a retry when it failed or was cancelled.
    private static async Task HashSeekableAsync(string column, TableBlobContent content, Stream source, bool consumed, CancellationToken ct)
    {
        var origin = content.RewindSource(column);
        long length;
        string sha;
        try
        {
            (length, sha) = await CopyHashingAsync(source, null, ct);
            source.Position = origin;
        }
        catch
        {
            content.RestoreSource();
            throw;
        }
        if (consumed && length == 0)
            throw content.RefuseEmptyAfterConsumed(column);
        content.Seal(length, sha);
    }

    // Copies a forward-only source, while hashing it, to a delete-on-close temporary file that becomes
    // the upload source. A copy that fails or is cancelled has consumed part of the source for good. A
    // source whose chain an uncommitted write read to its end (consumed) and that delivers nothing is refused.
    private static async Task SpoolAsync(string column, TableBlobContent content, Stream source, bool consumed, CancellationToken ct)
    {
        var spool = new FileStream(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBufferBytes,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        long length;
        string sha;
        try
        {
            (length, sha) = await CopyHashingAsync(source, spool, ct);
            await spool.FlushAsync(ct);
            spool.Position = 0;
        }
        catch
        {
            await spool.DisposeAsync();
            content.LoseSource();
            throw;
        }
        if (consumed && length == 0)
        {
            await spool.DisposeAsync();
            throw content.RefuseEmptyAfterConsumed(column);
        }
        content.EndSourceRead(complete: true);
        content.Seal(length, sha, spool);
    }

    /// <summary>
    /// Serializes <paramref name="entity"/> and plans its rows.
    /// <para>
    /// An opted-in value that fits one cell stays in its cell. When the cells that must stay on the
    /// primary row then exceed one entity, the largest of those values are moved to split form (their
    /// property's encoding, the normal split cells), so their chunks can go to continuation rows, until
    /// the primary row fits; a row that needed this is also brought within the request budget of one
    /// entity (<see cref="SplitRowLayout.MaxEntityWireBytes"/>) as far as moving such values can, like
    /// every row split cells are placed on. A row that fits one entity is left as it is.
    /// </para>
    /// <para>
    /// While the plan does not fit one entity-group transaction (with <paramref name="extraActions"/>
    /// more actions), the largest split value whose property allows offload is moved to a blob and the
    /// entity re-serialized.
    /// </para>
    /// </summary>
    /// <exception cref="OversizedValueException">
    /// Nothing more can be moved: the cells that are left on the primary row exceed one entity, or no
    /// value is left that may be offloaded. The exception names a column of the entity.
    /// </exception>
    internal (TableEntity Row, SplitRowPlan Plan, IReadOnlyList<PendingBlob> Blobs) Serialize(object entity, string pk, string rk, int extraActions)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var force = new HashSet<string>(StringComparer.Ordinal);
        var split = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var pending = new List<PendingBlob>();
            var inline = new List<InlineValue>();
            var ctx = new ValueWriteContext(options, tableName, force, IsClone: false) { ForceSplit = split, Inline = inline, ReadType = EntityType };
            var row = TableEntitySerializer.ToTableEntity(entity, pk, rk, ctx, pending);
            if (SplitRowLayout.TryPlan(row, out var overflow) is not { } plan)
            {
                // The cells that must stay on the primary row exceed one entity, and opted-in values of
                // one cell each are among them: move the largest, so their chunks can leave the row.
                var gains = inline.ConvertAll(value => (value.Column, Gain: SplitRowLayout.PromotionGain(row, value).Bytes));
                var moved = SplitRowLayout.ChoosePromotions(overflow!, gains);
                if (moved.Count == 0)
                    throw SplitRowLayout.PrimaryOverflowError(overflow!, gains.Count(g => g.Gain > 0));
                split.UnionWith(moved);
                continue;
            }

            // A row values were moved off is a split row: its primary row is kept within the request
            // budget of one entity too, as far as moving more of them can. (Cells that cannot be moved
            // are held to the stored size alone, as on every row.)
            if (split.Count > 0 && inline.Count > 0)
            {
                var first = SplitRowLayout.FirstRowWireExcess(plan);
                if (first.Excess > 0)
                {
                    var gains = inline.ConvertAll(value => (value.Column, Gain: SplitRowLayout.PromotionGain(row, value)));
                    var moved = SplitRowLayout.ChoosePromotions(
                        first.Properties, first.Excess, gains.Where(g => g.Gain.Bytes > 0).Select(g => (g.Column, g.Gain.Wire)));
                    if (moved.Count > 0)
                    {
                        split.UnionWith(moved);
                        continue;
                    }
                }
            }

            if (SplitRowLayout.FitsOneTransaction(plan, extraActions))
                return (row, plan, pending);

            string? best = null;
            long bestLength = -1;
            string? never = null;
            long neverLength = -1;
            foreach (var (key, value) in row)
            {
                if (!SplitCodec.TryParseLargeValueColumn(key, out var column, out var kind, out _) || kind != LargeValueCellKind.Length)
                    continue;
                if (force.Contains(column)) continue;
                var length = value switch { long l => l, int i => i, _ => 0L };
                if (TableEntitySerializer.SplitOptionFor(entity, column)?.Offload == BlobOffload.Never)
                {
                    if (length > neverLength) (never, neverLength) = (column, length);
                }
                else if (length > bestLength)
                {
                    (best, bestLength) = (column, length);
                }
            }

            if (best is not null && options is not null)
            {
                force.Add(best);
                continue;
            }

            const string reason = "the row and its continuation rows do not fit one entity-group transaction";
            if (best is null && never is not null)
                throw new OversizedValueException(never, neverLength,
                    reason + " and the property does not allow blob offload (BlobOffload.Never)",
                    offloadForbiddenByProperty: options is not null);
            // A split value that may be offloaded is left, and there is nowhere to offload it to.
            if (best is not null)
                throw new OversizedValueException(best, bestLength, reason + " and blob offload is not configured");
            // No split value is left at all: the deletes of the stored row's continuation rows do not fit.
            throw SplitRowLayout.TransactionOverflowError(plan, extraActions);
        }
    }

    /// <summary>
    /// Makes every pending blob exist: one with the same name and hash is touched (refreshing its
    /// <c>Last-Modified</c>) and its bytes are not sent again; an absent one is uploaded create-only
    /// through a read that verifies the bytes still match what was prepared. Each
    /// <see cref="TableBlobContent"/> is then bound to its blob; one that already streams another blob (or
    /// a blob of the same name through another blob store) stays bound there, and the property it was
    /// written from gets a content bound to this one.
    /// An upload reads a content's source, and a content is bound, while this write holds the content,
    /// so concurrent writes of one content take turns; a caller's seekable stream is put back where the
    /// content starts after every upload, whether it succeeded or not.
    /// </summary>
    /// <exception cref="BlobOffloadNotConfiguredException">Blob offload is not configured.</exception>
    /// <exception cref="InvalidOperationException">A source changed after it was prepared.</exception>
    /// <exception cref="TableValueIntegrityException">A blob of that name exists with a different hash.</exception>
    internal async Task UploadAsync(IReadOnlyList<PendingBlob> pending, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.Count == 0) return;
        var store = blobs ?? throw new BlobOffloadNotConfiguredException(tableName, pending[0].Column);

        foreach (var blob in pending)
        {
            var name = blob.Reference.Path;
            var state = await store.GetStateAsync(name, ct);
            var exists = false;
            if (state is not null)
            {
                if (!string.Equals(state.Sha256, blob.Reference.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new TableValueIntegrityException(blob.Column,
                        $"blob '{name}' already exists with SHA-256 {state.Sha256}, not {blob.Reference.Sha256}.");
                try
                {
                    await store.TouchAsync(name, ct);
                    exists = true;
                }
                catch (TableValueIntegrityException)
                {
                    // Deleted (by an orphan sweep) between the state read and the touch: upload it again.
                }
            }

            if (!exists)
                await UploadWithRetryAsync(store, blob, ct);
        }

        // Bind only once every blob exists: one content assigned to two properties is uploaded twice
        // from the same prepared source, and binding releases that source.
        foreach (var blob in pending)
        {
            if (blob.Content is not { } content)
                continue;
            var name = blob.Reference.Path;
            var column = blob.Column;
            var keys = RowKeysOf(name);
            Func<CancellationToken, Task<Stream>> open = c => OpenLazyAsync(store, column, name, keys, c);
            // Bound while this write holds the content: of several writes binding it at once, exactly one
            // binds the shared instance. The property is assigned after the turn ends (no lock across a setter).
            // A content bound through another store keeps that store's blob, even under the same name.
            bool bound;
            await content.WaitTurnAsync(ct);
            try
            {
                bound = content.Bind(name, open, column, store);
            }
            finally
            {
                content.EndTurn();
            }
            if (bound)
                continue;

            // The content already streams another blob: it was read from another row, or it is on another
            // property or entity as well. That instance is shared (the entity it came from holds it, and so
            // does each cached copy of that entity), so it stays bound where it is, and the entity being
            // written gets a content of its own, bound to the blob its own row references. A property that
            // cannot be assigned keeps the instance as it is.
            // It also forgets, when its write commits, the streams the shared instance read (R9).
            blob.Assign?.Invoke(TableBlobContent.Bound(
                blob.Reference.Length, blob.Reference.Sha256, blob.Reference.ContentType, name, open, column, store).KeepingStreamsOf(content));
        }
    }

    /// <summary>
    /// Releases the temporary copies <see cref="PrepareAsync"/> made for the contents of
    /// <paramref name="entity"/> that were not bound to a blob (the write failed before uploading them).
    /// Such a content has nothing left to be uploaded from: <see cref="PrepareAsync"/> refuses it in every
    /// later write. A content from a seekable stream or from bytes is not affected and can be written again.
    /// </summary>
    internal static void ReleasePrepared(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        foreach (var (_, content) in TableEntitySerializer.FindBlobContents(entity))
            content.ReleasePrepared();
    }

    /// <summary>
    /// The row (or the transaction) that writes <paramref name="entity"/> committed: the callers' streams
    /// its contents read to their end are forgotten, so a later content over the same stream object is a
    /// new payload (ruling R9 point 1). A write that fails leaves them listed (see <see cref="PrepareAsync"/>).
    /// </summary>
    internal static void Committed(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        foreach (var (_, content) in TableEntitySerializer.FindBlobContents(entity))
            content.Committed();
    }

    /// <summary>How many times a create-only upload that lost its blob to a concurrent delete is retried in all.</summary>
    internal const int MaxUploadAttempts = 3;

    // A create-only upload can conflict with an existing blob (an orphan of the same content) that an
    // orphan sweep then deletes before the store reads it back, so the store reports an integrity
    // failure for a blob that is simply gone. When the blob is indeed gone, upload again (from a newly
    // opened source); a blob that exists with another hash, or any other failure, is not retried. The
    // sweep can also delete the blob between this method's own state read and its touch: that, too, is
    // a blob that is gone, and counts as one more attempt.
    private static async Task UploadWithRetryAsync(IOffloadBlobStore store, PendingBlob blob, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await UploadOneAsync(store, blob, ct);
                return;
            }
            catch (TableValueIntegrityException) when (attempt < MaxUploadAttempts)
            {
                // Swept between the conflict and the state read: try again. Re-created meanwhile with the
                // same content: done once touched, or try again when it is swept before the touch.
                // Otherwise (another hash, or a different failure) report it.
                var state = await store.GetStateAsync(blob.Reference.Path, ct);
                if (state is not null)
                {
                    if (!string.Equals(state.Sha256, blob.Reference.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw;
                    if (await TryTouchAsync(store, blob.Reference.Path, ct))
                        return;
                }
            }
        }
    }

    // Refreshes an existing blob's Last-Modified so a pending sweep keeps it; false when it is gone.
    private static async Task<bool> TryTouchAsync(IOffloadBlobStore store, string name, CancellationToken ct)
    {
        try
        {
            await store.TouchAsync(name, ct);
            return true;
        }
        catch (TableValueIntegrityException)
        {
            return false;
        }
    }

    private static async Task UploadOneAsync(IOffloadBlobStore store, PendingBlob blob, CancellationToken ct)
    {
        // One upload at a time reads a content's prepared source (two reading one stream at once would
        // each get part of it), and none while another write hashes, copies or binds the content.
        var content = blob.Content;
        if (content is not null)
            await content.WaitTurnAsync(ct);
        try
        {
            var column = blob.Column;
            var source = await blob.OpenSource(ct);
            await using var verified = new HashingReadStream(source, blob.Reference.Length, blob.Reference.Sha256,
                reason => new InvalidOperationException(
                    $"The content of '{column}' changed after it was prepared for writing ({reason})."),
                leaveOpen: false);
            try
            {
                await store.UploadCreateOnlyAsync(blob.Reference.Path, verified, blob.Reference, blob.ContentEncoding, ct);
            }
            catch when (verified.Mismatch is not null)
            {
                // The SDK pipeline wraps (or retries past) what the content stream throws: report the cause.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(verified.Mismatch).Throw();
                throw;
            }
            // A store that swallowed the failure, or stopped reading before the end, still gets the content checked.
            if (verified.Mismatch is { } mismatch)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(mismatch).Throw();
            await verified.DrainAndVerifyAsync(ct);
        }
        finally
        {
            if (content is not null)
            {
                // Done reading the caller's stream, whatever happened: it goes back where the content starts.
                content.EndUploadRead();
                content.EndTurn();
            }
        }
    }

    /// <summary>
    /// Resolves the blob references of <paramref name="row"/> that are in scope (every one, or those a
    /// projection names): a <see cref="TableBlobContent"/> target becomes lazy content; any other target
    /// is downloaded and verified into <see cref="ValueReadContext.Resolved"/>. A reference's target is
    /// the property the read of the row sets its column on, which follows the runtime type of the
    /// entity's default instances, not only the declared property types, and the
    /// <see cref="ColumnAliasAttribute"/> of a renamed property (a reference under the legacy column).
    /// </summary>
    /// <param name="row">The (assembled) row.</param>
    /// <param name="entityType">The type the row is read as, which decides each reference's target.</param>
    /// <param name="projectedColumns">The caller's projection, or null for the whole entity.</param>
    /// <param name="ct">Cancels downloads.</param>
    /// <exception cref="TableValueIntegrityException">A blob is missing, malformed, or fails verification.</exception>
    /// <exception cref="BlobOffloadNotConfiguredException">A reference is in scope and blob offload is not configured.</exception>
    internal async Task<ValueReadContext> ResolveAsync(
        TableEntity row, Type entityType, IReadOnlyCollection<string>? projectedColumns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(entityType);
        Dictionary<string, object>? resolved = null;
        var anyLazy = false;
        foreach (var (key, value) in row)
        {
            if (!SplitCodec.TryParseLargeValueColumn(key, out var column, out var kind, out _) || kind != LargeValueCellKind.BlobReference)
                continue;
            if (TableEntitySerializer.LargeValueTarget(entityType, row, column) is not { } target)
                continue;
            // A projection names properties: a legacy column is in scope when its (renamed) property is.
            if (projectedColumns is not null && !Projects(projectedColumns, column) && !Projects(projectedColumns, target.Column))
                continue;
            if (target.Type == typeof(TableBlobContent))
            {
                anyLazy = true;
                continue;
            }

            var reference = BlobReference.Parse(column,
                value as string ?? throw new TableValueIntegrityException(column, "the blob reference is not a string."));
            var store = blobs ?? throw new BlobOffloadNotConfiguredException(tableName, column);
            var bytes = await DownloadAsync(store, column, reference, ct);
            resolved ??= new Dictionary<string, object>(StringComparer.Ordinal);
            resolved[column] = SplitCodec.FromStored(column, bytes, reference.Encoding, reference.Sha256);
        }

        Func<string, BlobReference, TableBlobContent>? lazy = null;
        if (anyLazy)
        {
            var store = blobs;
            (string, string)? keys = row.PartitionKey is { } pk && row.RowKey is { } rk ? (pk, rk) : null;
            // The content remembers the store it reads through (BLOBS-3); null when this store cannot open it.
            lazy = (column, reference) => TableBlobContent.Bound(
                reference.Length, reference.Sha256, reference.ContentType, reference.Path,
                c => store is not null
                    ? OpenLazyAsync(store, column, reference.Path, keys, c)
                    : throw new BlobOffloadNotConfiguredException(tableName, column),
                column, store);
        }
        return new ValueReadContext(resolved ?? NothingResolved, lazy, projectedColumns);
    }

    // A lazy TableBlobContent opens its blob long after the row was read, perhaps from the entity cache:
    // a replace or delete since then may have deleted the blob (blobs are content-addressed, so it can
    // only be missing, never hold other bytes). The store then stops answering the row from its caches
    // (BLOBS-4), so reading the entity again reads the table; the failure itself still reaches the caller.
    private async Task<Stream> OpenLazyAsync(IOffloadBlobStore store, string column, string path, (string Pk, string Rk)? keys, CancellationToken ct)
    {
        try
        {
            return await store.OpenReadAsync(column, path, ct);
        }
        catch (TableValueIntegrityException ex)
        {
            ForgetRow(keys);
            throw new TableValueIntegrityException(column,
                $"blob '{path}' could not be opened ({ex.Reason}). The row may have been replaced or deleted since this " +
                "entity was read: read the entity again (the store that returned it no longer answers it from its cache).");
        }
    }

    // Never throws into, or replaces, the integrity failure being reported.
    private void ForgetRow((string Pk, string Rk)? keys)
    {
        if (keys is not { } row || BlobMissing is not { } forget)
            return;
        try
        {
            forget(row.Pk, row.Rk);
        }
#pragma warning disable CA1031 // the eviction is a courtesy to the next read; the caller gets the integrity failure
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    // The public property of that name (the most derived declaration, when a subclass hides one of its
    // base) when it is not declared TableBlobContent; null when it is, or when there is none. Never throws.
    private static System.Reflection.PropertyInfo? DeclaredAsAnotherType(Type type, string name)
    {
        System.Reflection.PropertyInfo? found = null;
        foreach (var candidate in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (candidate.Name != name || candidate.GetIndexParameters().Length != 0)
                continue;
            if (found?.DeclaringType is not { } declaring || (candidate.DeclaringType is { } other && declaring.IsAssignableFrom(other)))
                found = candidate;
        }
        return found is not null && found.PropertyType != typeof(TableBlobContent) ? found : null;
    }

    // The partition and row key a blob name of this library holds ({table}/{pk}/{rk}/{column}/{sha256});
    // null for a name of another shape.
    private static (string Pk, string Rk)? RowKeysOf(string path)
    {
        var segments = path.Split('/');
        return segments.Length == 5
            && OffloadBlobNames.TryUnescapeSegment(segments[1], out var pk)
            && OffloadBlobNames.TryUnescapeSegment(segments[2], out var rk)
                ? (pk, rk)
                : null;
    }

    /// <summary>The most reference columns a sweep names in a projection before it reads whole rows instead.</summary>
    internal const int MaxSweepSelectColumns = 200;

    /// <summary>
    /// The longest projection (the URL-encoded, comma-joined <c>$select</c> list) a sweep sends before it
    /// reads whole rows instead, so the query URL stays well within the service's limits.
    /// </summary>
    internal const int MaxSweepSelectLength = 2_000;

    /// <summary>The service limit on a property name.</summary>
    private const int MaxPropertyNameLength = 255;

    /// <summary><c>{table}/{partitionKey}/</c>, each segment escaped: the prefix of every blob of a partition.</summary>
    internal static string PartitionPrefix(string table, string pk) =>
        TablePrefix(table) + OffloadBlobNames.EscapeSegment(pk) + "/";

    /// <summary><c>{table}/{partitionKey}/{rowKey}/</c>, each segment escaped: the prefix of every blob of a row.</summary>
    internal static string RowPrefix(string table, string pk, string rk) =>
        PartitionPrefix(table, pk) + OffloadBlobNames.EscapeSegment(rk) + "/";

    /// <summary><c>{table}/</c>, escaped: the prefix of every blob of the table.</summary>
    internal static string TablePrefix(string table) => OffloadBlobNames.EscapeSegment(table) + "/";

    /// <summary>
    /// Deletes the blobs under <paramref name="pk"/> that no row of the partition references and whose
    /// Last-Modified is before <paramref name="cutoff"/> (<see cref="OrphansAsync"/>); returns how many were
    /// deleted (0 when blob offload is not configured). Each delete is conditional on the blob not having
    /// been modified since it was listed, so a blob a concurrent write touches to reuse it (before writing
    /// its row) is kept.
    /// </summary>
    /// <param name="pk">The (normalized) partition key.</param>
    /// <param name="cutoff">Only blobs last modified before this instant are candidates.</param>
    /// <param name="declaredColumns">The large-value columns of the store's entity type.</param>
    /// <param name="partitionRows">
    /// Every row of the partition (continuation rows included), projected onto the given columns, or
    /// whole when the projection is null. Called at most once.
    /// </param>
    /// <param name="ct">Cancels the listing, the read and the deletes.</param>
    internal async Task<int> SweepAsync(
        string pk, DateTimeOffset cutoff, IReadOnlyList<string> declaredColumns,
        Func<IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TableEntity>> partitionRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pk);
        ArgumentNullException.ThrowIfNull(declaredColumns);
        ArgumentNullException.ThrowIfNull(partitionRows);
        if (blobs is null)
            return 0;

        // The orphans, each deleted only if it was not modified since it was listed.
        var deleted = 0;
        foreach (var (name, lastModified) in await OrphansAsync(pk, cutoff, includeCutoff: false, declaredColumns, partitionRows, ct))
        {
            if (await blobs.DeleteIfUnmodifiedAsync(name, lastModified, ct))
                deleted++;
        }
        return deleted;
    }

    /// <summary>
    /// The blobs under <paramref name="pk"/> that are old enough and that no row of the partition
    /// references, each with the Last-Modified it was listed at (the condition its delete must carry).
    /// Only names of the exact offload shape (<see cref="OffloadBlobNames.IsOffloadBlobName"/>) are
    /// candidates; anything else is left alone. The blobs are listed first and the rows are read (once)
    /// only when some blob is old enough, so every row that is in the partition when the rows are read
    /// keeps the blobs it references. Nothing is deleted here. Both the orphan sweep and the cleanup
    /// after a partition delete decide through this method which blobs may go.
    /// </summary>
    /// <param name="pk">The (normalized) partition key.</param>
    /// <param name="cutoff">Blobs last modified before this instant are old enough.</param>
    /// <param name="includeCutoff">
    /// True when a blob last modified exactly at <paramref name="cutoff"/> is old enough too, as for the
    /// cleanup after a write (whose deletes are conditional on "not modified since the cutoff"); false
    /// for the sweep, which takes only blobs strictly older than its age.
    /// </param>
    /// <param name="declaredColumns">The large-value columns of the store's entity type.</param>
    /// <param name="partitionRows">
    /// Every row of the partition (continuation rows included), projected onto the given columns, or
    /// whole when the projection is null. Called at most once.
    /// </param>
    /// <param name="ct">Cancels the listing and the read.</param>
    internal async Task<IReadOnlyList<(string Name, DateTimeOffset LastModified)>> OrphansAsync(
        string pk, DateTimeOffset cutoff, bool includeCutoff, IReadOnlyList<string> declaredColumns,
        Func<IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TableEntity>> partitionRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pk);
        ArgumentNullException.ThrowIfNull(declaredColumns);
        ArgumentNullException.ThrowIfNull(partitionRows);
        var candidates = new List<(string Name, DateTimeOffset LastModified)>();
        if (blobs is null)
            return candidates;

        // (1) The candidates: the partition's blobs that are old enough, with the time they were listed at.
        var prefix = PartitionPrefix(tableName, pk);
        await foreach (var (name, lastModified) in blobs.ListAsync(prefix, ct).WithCancellation(ct))
        {
            if ((lastModified < cutoff || (includeCutoff && lastModified == cutoff)) && OffloadBlobNames.IsOffloadBlobName(name, prefix))
                candidates.Add((name, lastModified));
        }
        if (candidates.Count == 0)
            return candidates;

        // (2) The references: one read of the partition's keys and reference cells.
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        // {table}/{pk}/{rk}/{column}/ of each cell whose reference cannot be read.
        var guarded = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var row in partitionRows(SweepSelect(prefix, candidates, declaredColumns), ct).WithCancellation(ct))
        {
            // A selected cell the row does not have comes back as null: no reference (for example a value
            // replaced by an inline one), so the blobs it once referenced are orphans. ReferenceCells skips it.
            foreach (var (column, cell) in ReferenceCells(row))
            {
                try
                {
                    referenced.Add(ParseReference(column, cell).Path);
                }
                catch (TableValueIntegrityException)
                {
                    // An unreadable reference names no blob, but it may have named one this cell wrote:
                    // keep every blob of the cell rather than delete one a repair could still recover.
                    // A key with an unpaired surrogate never names a blob (writes refuse one): nothing to keep.
                    if (SplitCodec.IsWellFormedUtf16(row.RowKey ?? string.Empty))
                        guarded.Add(prefix + OffloadBlobNames.EscapeSegment(row.RowKey ?? string.Empty) + "/" + OffloadBlobNames.EscapeSegment(column) + "/");
                }
            }
        }

        // (3) The orphans: the candidates no row references.
        candidates.RemoveAll(c => referenced.Contains(c.Name) || IsGuarded(c.Name, prefix.Length, guarded));
        return candidates;
    }

    // True when a '/'-terminated prefix of the name (past the partition prefix) is a guarded cell:
    // one set lookup per name segment.
    private static bool IsGuarded(string name, int start, HashSet<string> guarded)
    {
        if (guarded.Count == 0)
            return false;
        for (var slash = name.IndexOf('/', start); slash >= 0; slash = name.IndexOf('/', slash + 1))
        {
            if (guarded.Contains(name[..(slash + 1)]))
                return true;
        }
        return false;
    }

    // The projection that reads every cell that could reference a candidate. A blob is named after the
    // row and the column whose cell references it ({table}/{pk}/{rk}/{column}/{sha256}), so each
    // candidate's own name says which {column}__blob cell to read, whatever entity type wrote it (another
    // type sharing the table, or a runtime subtype of a member the store's type declares as a base,
    // abstract or interface type). The type's declared columns are read as well. Every candidate has that
    // shape (SweepAsync filters the others out); a column that is not a valid property name, too many
    // columns, or a select list longer than MaxSweepSelectLength reads whole rows (null) instead.
    private static List<string>? SweepSelect(
        string prefix, List<(string Name, DateTimeOffset LastModified)> candidates, IReadOnlyList<string> declaredColumns)
    {
        var columns = new List<string>(declaredColumns);
        var seen = new HashSet<string>(declaredColumns, StringComparer.Ordinal);
        foreach (var (name, _) in candidates)
        {
            var segments = name[prefix.Length..].Split('/');
            if (segments.Length != 3 || !OffloadBlobNames.TryUnescapeSegment(segments[1], out var column))
                return null;
            if (!IsReferenceColumn(column))
                return null;
            if (seen.Add(column))
                columns.Add(column);
        }
        if (columns.Count > MaxSweepSelectColumns)
            return null;

        var select = new List<string>(3 + columns.Count) { "PartitionKey", "RowKey", SystemColumnNames.SplitOf };
        select.AddRange(columns.Select(c => c + SplitCodec.BlobSuffix));
        return SelectLength(select) > MaxSweepSelectLength ? null : select;
    }

    /// <summary>The length of <paramref name="select"/> as a URL-encoded, comma-joined <c>$select</c> value.</summary>
    internal static int SelectLength(IReadOnlyList<string> select) =>
        Uri.EscapeDataString(string.Join(',', select)).Length;

    // A column whose reference cell is a valid table property name (letters, digits and '_', not
    // starting with a digit); any other segment cannot be a column this library wrote.
    private static bool IsReferenceColumn(string column)
    {
        if (column.Length == 0 || column.Length + SplitCodec.BlobSuffix.Length > MaxPropertyNameLength || char.IsDigit(column[0]))
            return false;
        foreach (var c in column)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }
        return true;
    }

    /// <summary>Every blob reference of <paramref name="row"/>; a null reference cell is no reference.</summary>
    /// <exception cref="TableValueIntegrityException">A reference cell is malformed.</exception>
    internal static IReadOnlyList<BlobReference> References(TableEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var references = new List<BlobReference>();
        foreach (var (column, cell) in ReferenceCells(row))
            references.Add(ParseReference(column, cell));
        return references;
    }

    /// <summary>
    /// The <c>{column}__blob</c> cells of <paramref name="row"/> that hold something, with their base
    /// column. A null cell is not one of them: the service returns a selected property the row does not
    /// have as null (the storage emulator omits it), so null means "no reference", never a malformed one.
    /// The readers that decide which blobs a row references (the stored row's blob paths before a write
    /// or delete, the orphan sweep, the cleanup after a partition delete, <see cref="References"/>) all
    /// go through here.
    /// </summary>
    internal static IEnumerable<(string Column, object Cell)> ReferenceCells(TableEntity row)
    {
        foreach (var (key, value) in row)
        {
            if (value is not null
                && SplitCodec.TryParseLargeValueColumn(key, out var column, out var kind, out _)
                && kind == LargeValueCellKind.BlobReference)
                yield return (column, value);
        }
    }

    /// <summary>The reference a non-null <c>{column}__blob</c> cell holds.</summary>
    /// <exception cref="TableValueIntegrityException">The cell is not a string, or not a reference.</exception>
    internal static BlobReference ParseReference(string column, object cell) =>
        BlobReference.Parse(column,
            cell as string ?? throw new TableValueIntegrityException(column, "the blob reference is not a string."));

    // A projection names the column itself, or an object that owns it (the same rule BuildSelect uses).
    internal static bool Projects(IReadOnlyCollection<string> projected, string column)
    {
        foreach (var name in projected)
        {
            if (string.Equals(name, column, StringComparison.Ordinal)
                || column.StartsWith(name + TableEntityValue.PathDelimiter, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static async Task<byte[]> DownloadAsync(IOffloadBlobStore store, string column, BlobReference reference, CancellationToken ct)
    {
        if (reference.Length > Array.MaxLength)
            throw new TableValueIntegrityException(column, $"the blob is {reference.Length:N0} B, too large to read into memory.");
        await using var stream = await store.OpenReadAsync(column, reference.Path, ct);
        var bytes = new byte[reference.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = await stream.ReadAsync(bytes.AsMemory(read), ct);
            if (n == 0)
                throw new TableValueIntegrityException(column, $"the blob holds {read} bytes, expected {reference.Length}.");
            read += n;
        }
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, ct) != 0)
            throw new TableValueIntegrityException(column, $"the blob holds more than the expected {reference.Length} bytes.");
        return bytes;
    }

    // Reads source to its end, hashing; copies into target when one is given.
    private static async Task<(long Length, string Sha256)> CopyHashingAsync(Stream source, Stream? target, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long length = 0;
            int n;
            while ((n = await source.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), ct)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                if (target is not null)
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                length += n;
            }
            return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
