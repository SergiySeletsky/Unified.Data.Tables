using System.Globalization;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables;

/// <summary>
/// The blob container that holds a table's offloaded values in Azure Blob Storage. Blobs are written
/// create-only (<c>If-None-Match: *</c>) with their SHA-256 and length as metadata; an existing blob
/// with the same hash counts as written. The container is created on the first upload, once, with the
/// same coalesced, retry-after-failure semantics as table creation.
/// </summary>
/// <param name="container">The container; nothing is called on it until the first operation.</param>
internal sealed class AzureOffloadBlobStore(BlobContainerClient container) : IOffloadBlobStore
{
    /// <summary>The partition size the uploader buffers. The content stream is not seekable, so it is copied in parts.</summary>
    internal const int TransferBytes = 4 * 1024 * 1024;

    /// <summary>How many partitions are in flight at once; with <see cref="TransferBytes"/> it bounds the upload's memory.</summary>
    internal const int TransferConcurrency = 4;

    /// <summary>
    /// How many times in a row a read opens the blob again after the service refused a range because the
    /// blob's ETag had changed, before that failure is reported. After the first of them the read no
    /// longer depends on the ETag, so each further one in a row takes a request that failed and was
    /// retried by the SDK; a read that delivers bytes starts the count again.
    /// </summary>
    internal const int MaxReadReopens = 5;

    private const string ShaKey = "sha256";
    private const string LengthKey = "length";

    private readonly TableInitializer containerInit =
        new(() => container.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None));

    /// <inheritdoc />
    public async Task<BlobState?> GetStateAsync(string name, CancellationToken ct)
    {
        try
        {
            var properties = (await Blob(name).GetPropertiesAsync(conditions: null, ct).ConfigureAwait(false)).Value;
            return ToState(name, properties);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task UploadCreateOnlyAsync(string name, Stream content, BlobReference reference, string? contentEncoding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(reference);
        var blob = Blob(name);
        await containerInit.EnsureAsync(ct).ConfigureAwait(false);

        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = reference.ContentType, ContentEncoding = contentEncoding },
            Metadata = Metadata(reference.Sha256, reference.Length),
            TransferOptions = new StorageTransferOptions
            {
                InitialTransferSize = TransferBytes,
                MaximumTransferSize = TransferBytes,
                MaximumConcurrency = TransferConcurrency,
            },
        };

        try
        {
            await blob.UploadAsync(content, options, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // The blob exists: success when it holds the same bytes, which a touch keeps from a pending sweep.
            var state = await GetStateAsync(name, ct).ConfigureAwait(false)
                ?? throw new TableValueIntegrityException(name, "the blob reported as existing could not be read.");
            if (!string.Equals(state.Sha256, reference.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new TableValueIntegrityException(name, $"blob already exists with SHA-256 {state.Sha256}, not {reference.Sha256}.");
            await TouchAsync(name, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task TouchAsync(string name, CancellationToken ct)
    {
        var blob = Blob(name);
        try
        {
            // Setting metadata is what refreshes Last-Modified; the same values are written back.
            var properties = (await blob.GetPropertiesAsync(conditions: null, ct).ConfigureAwait(false)).Value;
            var metadata = new Dictionary<string, string>(properties.Metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            await blob.SetMetadataAsync(metadata, conditions: null, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new TableValueIntegrityException(name, "blob missing");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The SDK's stream reads the blob in ranges, each conditional on the ETag the blob had when the stream
    /// was opened. A blob's name contains the SHA-256 of its bytes and blobs are written create-only, so
    /// the bytes under a name never change: its ETag changes only when a write that reuses the content
    /// re-sets its metadata (<see cref="TouchAsync"/>), and the blob can be deleted. So when a range is
    /// refused because the ETag changed, the read goes on from the same position without that condition,
    /// and a blob that is gone by then fails the read with <see cref="TableValueIntegrityException"/>, as
    /// a missing blob fails the open. The caller verifies length and hash in any case.
    /// </remarks>
    public async Task<Stream> OpenReadAsync(string column, string name, CancellationToken ct)
    {
        var blob = Blob(name);
        return new BlobReadStream(blob, column, name, await OpenAsync(blob, column, name, 0, pinned: true, ct).ConfigureAwait(false));
    }

    // pinned (allowModifications: false): every range the stream reads is conditional on the ETag the
    // blob has now, and the stream's buffer is no larger than the blob. A read that is under way goes
    // on unpinned instead, from the position it reached.
    private static BlobOpenReadOptions ReadFrom(long position, bool pinned) => new(allowModifications: !pinned) { Position = position };

    private static async Task<Stream> OpenAsync(BlobClient blob, string column, string name, long position, bool pinned, CancellationToken ct)
    {
        try
        {
            return await blob.OpenReadAsync(ReadFrom(position, pinned), ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw Missing(column, name, reading: !pinned);
        }
    }

    private static Stream Open(BlobClient blob, string column, string name, long position, bool pinned)
    {
        try
        {
            return blob.OpenRead(ReadFrom(position, pinned), CancellationToken.None);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw Missing(column, name, reading: !pinned);
        }
    }

    // reading: the blob was open already, so a blob that is not there now was deleted meanwhile.
    private static TableValueIntegrityException Missing(string column, string name, bool reading) =>
        new(column, reading ? $"blob '{name}' was deleted while it was being read." : $"blob '{name}' is missing.");

    /// <summary>
    /// A forward-only read of one blob over the SDK's stream, which starts out pinned to the blob's ETag.
    /// When the service refuses a range because the ETag changed (412: the blob's metadata was re-set,
    /// its bytes are the same), the read goes on through an unpinned stream opened at the position
    /// reached, so no later change of the ETag interrupts it again. A blob that was deleted (404, from a
    /// range or from that open) fails the read with <see cref="TableValueIntegrityException"/>. Any other
    /// failure surfaces as it is.
    /// </summary>
    private sealed class BlobReadStream(BlobClient blob, string column, string name, Stream opened) : Stream
    {
        private Stream inner = opened;
        private long position;

        // Known once the read goes on unpinned: where the blob ends. Read at its end, the SDK's unpinned
        // stream asks the service whether the blob has grown; these blobs never grow, and the blob may
        // be gone by then although every byte of it was delivered.
        private long? end;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            for (var reopened = 0; ; reopened++)
            {
                if (position >= end)
                    return 0;
                try
                {
                    var n = inner.Read(buffer, offset, count);
                    position += n;
                    return n;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    throw Missing(column, name, reading: true);
                }
                catch (RequestFailedException ex) when (ex.Status == 412 && reopened < MaxReadReopens)
                {
                    // The ETag moved on (or, for some services, the blob is gone: the open says so).
                    var next = Open(blob, column, name, position, pinned: false);
                    inner.Dispose();
                    Continue(next);
                }
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            for (var reopened = 0; ; reopened++)
            {
                if (position >= end)
                    return 0;
                try
                {
                    var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    position += n;
                    return n;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    throw Missing(column, name, reading: true);
                }
                catch (RequestFailedException ex) when (ex.Status == 412 && reopened < MaxReadReopens)
                {
                    // The ETag moved on (or, for some services, the blob is gone: the open says so).
                    var next = await OpenAsync(blob, column, name, position, pinned: false, cancellationToken).ConfigureAwait(false);
                    await inner.DisposeAsync().ConfigureAwait(false);
                    Continue(next);
                }
            }
        }

        // The read goes on through an unpinned stream, up to the blob's length as that stream knows it.
        private void Continue(Stream unpinned)
        {
            inner = unpinned;
            try
            {
                end = unpinned.Length;
            }
            catch (NotSupportedException)
            {
                end = null;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteIfUnmodifiedAsync(string name, DateTimeOffset? unmodifiedSince, CancellationToken ct)
    {
        try
        {
            await Blob(name).DeleteAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                new BlobRequestConditions { IfUnmodifiedSince = unmodifiedSince },
                ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>The <c>Date</c> header of a container properties request (the 404 of a container not yet
    /// created carries one too); null when the service sent none.</remarks>
    public async Task<DateTimeOffset?> GetServiceTimeAsync(CancellationToken ct)
    {
        try
        {
            var response = await container.GetPropertiesAsync(conditions: null, ct).ConfigureAwait(false);
            return response?.GetRawResponse()?.Headers.Date;
        }
        catch (RequestFailedException ex)
        {
            return ex.GetRawResponse()?.Headers.Date;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        await using var pages = container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool more;
            try
            {
                more = await pages.MoveNextAsync().ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // No container yet: nothing was ever offloaded.
                yield break;
            }
            if (!more) yield break;
            var item = pages.Current;
            // The service always lists Last-Modified. An item without one cannot be deleted conditionally
            // (any If-Unmodified-Since would be a guess), so it is skipped rather than given a fake time.
            if (item.Properties?.LastModified is not { } lastModified)
                continue;
            yield return (item.Name, lastModified);
        }
    }

    /// <inheritdoc />
    /// <remarks>A hierarchical listing with the delimiter <c>/</c>; nothing when the container does not exist.</remarks>
    public async IAsyncEnumerable<string> ListPrefixesAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        await using var pages = container.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", prefix, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool more;
            try
            {
                more = await pages.MoveNextAsync().ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                yield break;
            }
            if (!more) yield break;
            if (pages.Current is { IsPrefix: true, Prefix: { } listed })
                yield return listed;
        }
    }

    // The client of the blob named exactly name: the SDK collapses '.' and '..' segments of the URI it
    // builds, which would address another blob than the one a row references.
    private BlobClient Blob(string name)
    {
        OffloadBlobNames.ThrowIfNotAddressable(name);
        var blob = container.GetBlobClient(name);
        if (!string.Equals(blob.Name, name, StringComparison.Ordinal))
            throw new ArgumentException($"The Blobs SDK addresses the blob name '{name}' as '{blob.Name}'.", nameof(name));
        return blob;
    }

    private static Dictionary<string, string> Metadata(string sha256, long length) => new(StringComparer.OrdinalIgnoreCase)
    {
        [ShaKey] = sha256,
        [LengthKey] = length.ToString(CultureInfo.InvariantCulture),
    };

    private static BlobState ToState(string name, BlobProperties properties)
    {
        var metadata = properties.Metadata;
        if (metadata is null || !metadata.TryGetValue(ShaKey, out var sha) || string.IsNullOrEmpty(sha))
            throw new TableValueIntegrityException(name, "the blob has no sha256 metadata.");
        var length = metadata.TryGetValue(LengthKey, out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : properties.ContentLength;
        return new BlobState(sha, length, properties.LastModified);
    }
}
