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

    /// <summary><c>{table}/{partitionKey}/{rowKey}/{column}/{sha256}</c>, each segment escaped.</summary>
    /// <exception cref="ArgumentException">The name exceeds <see cref="MaxBlobNameLength"/> characters.</exception>
    internal static string BlobName(string table, string pk, string rk, string column, string sha256)
    {
        var name = string.Join('/',
            Uri.EscapeDataString(table),
            Uri.EscapeDataString(pk),
            Uri.EscapeDataString(rk),
            Uri.EscapeDataString(column),
            Uri.EscapeDataString(sha256));
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
    /// <exception cref="InvalidOperationException">Blob offload is not configured, or a source changed after it was prepared.</exception>
    /// <exception cref="TableValueIntegrityException">A blob of that name exists with a different hash.</exception>
    internal async Task UploadAsync(IReadOnlyList<PendingBlob> pending, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.Count == 0) return;
        var store = blobs ?? throw new InvalidOperationException("Blob offload is not configured.");

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
                await UploadOneAsync(store, blob, ct);

            var column = blob.Column;
            blob.Content?.Bind(name, c => store.OpenReadAsync(column, name, c), column);
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
        await store.UploadCreateOnlyAsync(blob.Reference.Path, verified, blob.Reference, blob.ContentEncoding, ct);
        // A store that stopped reading before the end still gets the content checked.
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
            var store = blobs ?? throw new TableValueIntegrityException(column,
                "the value is stored in blob storage, but blob offload is not configured.");
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
                    ? store.OpenReadAsync(column, reference.Path, c)
                    : throw new TableValueIntegrityException(column, "the value is stored in blob storage, but blob offload is not configured."),
                column);
        }
        return new ValueReadContext(resolved ?? NothingResolved, lazy);
    }

    /// <summary>Every blob reference of <paramref name="row"/>.</summary>
    /// <exception cref="TableValueIntegrityException">A reference cell is malformed.</exception>
    internal static IReadOnlyList<BlobReference> References(TableEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var references = new List<BlobReference>();
        foreach (var (key, value) in row)
        {
            if (SplitCodec.TryParseLargeValueColumn(key, out var column, out var kind, out _) && kind == LargeValueCellKind.BlobReference)
                references.Add(BlobReference.Parse(column,
                    value as string ?? throw new TableValueIntegrityException(column, "the blob reference is not a string.")));
        }
        return references;
    }

    // A projection names the column itself, or an object that owns it (the same rule BuildSelect uses).
    private static bool Projects(IReadOnlyCollection<string> projected, string column)
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
