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

    private const string ShaKey = "sha256";
    private const string LengthKey = "length";

    private readonly TableInitializer containerInit =
        new(() => container.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None));

    /// <inheritdoc />
    public async Task<BlobState?> GetStateAsync(string name, CancellationToken ct)
    {
        try
        {
            var properties = (await container.GetBlobClient(name).GetPropertiesAsync(conditions: null, ct).ConfigureAwait(false)).Value;
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
        await containerInit.EnsureAsync(ct).ConfigureAwait(false);

        var blob = container.GetBlobClient(name);
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
        var blob = container.GetBlobClient(name);
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
    public async Task<Stream> OpenReadAsync(string column, string name, CancellationToken ct)
    {
        try
        {
            // allowModifications: false pins the read to one version of the blob.
            return await container.GetBlobClient(name)
                .OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new TableValueIntegrityException(column, $"blob '{name}' is missing.");
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteIfUnmodifiedAsync(string name, DateTimeOffset? unmodifiedSince, CancellationToken ct)
    {
        try
        {
            await container.GetBlobClient(name).DeleteAsync(
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
