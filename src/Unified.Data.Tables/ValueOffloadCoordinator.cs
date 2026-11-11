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
    internal async Task PrepareAsync(object entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        foreach (var (column, content) in TableEntitySerializer.FindBlobContents(entity))
        {
            if (content.IsSealed) continue;
            var source = content.Source
                ?? throw new InvalidOperationException($"The TableBlobContent of '{column}' has no content to read.");
            if (source.CanSeek)
            {
                var origin = source.Position;
                var (length, sha) = await CopyHashingAsync(source, null, ct);
                source.Position = origin;
                content.Seal(length, sha, origin);
            }
            else
            {
                var spool = new FileStream(
                    Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBufferBytes,
                    FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                try
                {
                    var (length, sha) = await CopyHashingAsync(source, spool, ct);
                    await spool.FlushAsync(ct);
                    spool.Position = 0;
                    content.Seal(length, sha, 0, spool);
                }
                catch
                {
                    await spool.DisposeAsync();
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Serializes <paramref name="entity"/> and plans its rows. While the plan does not fit one
    /// entity-group transaction (with <paramref name="extraActions"/> more actions), the largest split
    /// value whose property allows offload is moved to a blob and the entity re-serialized.
    /// </summary>
    /// <exception cref="OversizedValueException">No value is left that may be offloaded.</exception>
    internal (TableEntity Row, SplitRowPlan Plan, IReadOnlyList<PendingBlob> Blobs) Serialize(object entity, string pk, string rk, int extraActions)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var type = entity.GetType();
        var force = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var pending = new List<PendingBlob>();
            var ctx = new ValueWriteContext(options, tableName, force, IsClone: false);
            var row = TableEntitySerializer.ToTableEntity(entity, pk, rk, ctx, pending);
            var plan = SplitRowLayout.Plan(row);
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
                if (TableEntitySerializer.SplitOptionFor(type, column)?.Offload == BlobOffload.Never)
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
            throw new OversizedValueException(
                best ?? never ?? rk,
                best is not null ? bestLength : TableRowSize.Estimate(plan.Primary) + plan.Parts.Sum(TableRowSize.Estimate),
                options is null ? reason + " and blob offload is not configured" : reason);
        }
    }

    /// <summary>
    /// Makes every pending blob exist: one with the same name and hash is touched (refreshing its
    /// <c>Last-Modified</c>) and its bytes are not sent again; an absent one is uploaded create-only
    /// through a read that verifies the bytes still match what was prepared. Each
    /// <see cref="TableBlobContent"/> is then bound to its blob.
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
            var name = blob.Reference.Path;
            var column = blob.Column;
            blob.Content?.Bind(name, c => OpenLazyAsync(store, column, name, c), column);
        }
    }

    /// <summary>
    /// Releases the temporary copies <see cref="PrepareAsync"/> made for the contents of
    /// <paramref name="entity"/> that were not bound to a blob (the write failed before uploading them).
    /// </summary>
    internal static void ReleasePrepared(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        foreach (var (_, content) in TableEntitySerializer.FindBlobContents(entity))
            content.ReleasePrepared();
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

    /// <summary>
    /// Resolves the blob references of <paramref name="row"/> that are in scope (every one, or those a
    /// projection names): a <see cref="TableBlobContent"/> target becomes lazy content; any other target
    /// is downloaded and verified into <see cref="ValueReadContext.Resolved"/>.
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
            if (projectedColumns is not null && !Projects(projectedColumns, column))
                continue;
            var path = TableEntityValue.Create(column, string.Empty).Path;
            if (TypeMetadataCache.ResolvePath(entityType, path) is not { } target)
                continue;
            if (target.PropertyType == typeof(TableBlobContent))
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
            lazy = (column, reference) => TableBlobContent.Bound(
                reference.Length, reference.Sha256, reference.ContentType, reference.Path,
                c => store is not null
                    ? OpenLazyAsync(store, column, reference.Path, c)
                    : throw new BlobOffloadNotConfiguredException(tableName, column),
                column);
        }
        return new ValueReadContext(resolved ?? NothingResolved, lazy, projectedColumns);
    }

    // A lazy TableBlobContent opens its blob long after the row was read, perhaps from the entity cache:
    // a replace or delete since then may have deleted the blob (blobs are content-addressed, so it can
    // only be missing, never hold other bytes). Say so instead of reporting a bare missing blob.
    private static async Task<Stream> OpenLazyAsync(IOffloadBlobStore store, string column, string path, CancellationToken ct)
    {
        try
        {
            return await store.OpenReadAsync(column, path, ct);
        }
        catch (TableValueIntegrityException ex)
        {
            throw new TableValueIntegrityException(column,
                $"blob '{path}' could not be opened ({ex.Reason}). The row may have been replaced or deleted since this " +
                "entity was read (a cached entity can outlive its blob): read the entity again.");
        }
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
    /// Every reader of a row's references goes through here.
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
