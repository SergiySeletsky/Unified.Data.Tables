using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.InMemory;

/// <summary>
/// An in-memory blob container for offloaded values with the same create-only, content-addressed
/// contract as the Azure one: an existing blob with a matching hash is success, a different hash
/// throws, a touch refreshes <c>Last-Modified</c>, and a conditional delete loses to a newer touch.
/// </summary>
internal sealed class InMemoryOffloadBlobStore : IOffloadBlobStore
{
    private readonly ConcurrentDictionary<string, (byte[] Bytes, BlobReference Ref, string? ContentEncoding, DateTimeOffset LastModified)> blobs =
        new(StringComparer.Ordinal);

    private readonly object clock = new();
    private DateTimeOffset last = DateTimeOffset.MinValue;

    /// <summary>The names of the stored blobs (for tests).</summary>
    internal IReadOnlyCollection<string> Names => blobs.Keys.ToArray();

    /// <inheritdoc />
    public Task<BlobState?> GetStateAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(blobs.TryGetValue(name, out var blob)
            ? new BlobState(blob.Ref.Sha256, blob.Ref.Length, blob.LastModified)
            : null);
    }

    /// <inheritdoc />
    public async Task UploadCreateOnlyAsync(string name, Stream content, BlobReference reference, string? contentEncoding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(reference);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        while (true)
        {
            if (blobs.TryGetValue(name, out var existing))
            {
                if (!string.Equals(existing.Ref.Sha256, reference.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new TableValueIntegrityException(name, $"blob already exists with SHA-256 {existing.Ref.Sha256}, not {reference.Sha256}.");
                if (blobs.TryUpdate(name, existing with { LastModified = Now() }, existing))
                    return;
                continue;
            }
            if (blobs.TryAdd(name, (bytes, reference, contentEncoding, Now())))
                return;
        }
    }

    /// <inheritdoc />
    public Task TouchAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        while (true)
        {
            if (!blobs.TryGetValue(name, out var existing))
                throw new TableValueIntegrityException(name, "blob missing");
            if (blobs.TryUpdate(name, existing with { LastModified = Now() }, existing))
                return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string column, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return blobs.TryGetValue(name, out var blob)
            ? Task.FromResult<Stream>(new MemoryStream(blob.Bytes, writable: false))
            : throw new TableValueIntegrityException(column, "blob missing");
    }

    /// <inheritdoc />
    public Task<bool> DeleteIfUnmodifiedAsync(string name, DateTimeOffset? unmodifiedSince, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!blobs.TryGetValue(name, out var blob))
            return Task.FromResult(false);
        if (unmodifiedSince is { } since && blob.LastModified > since)
            return Task.FromResult(false);
        return Task.FromResult(blobs.TryRemove(KeyValuePair.Create(name, blob)));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        foreach (var (name, blob) in blobs.Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            yield return (name, blob.LastModified);
        }
        await Task.CompletedTask;
    }

    /// <summary>Stores a blob directly, as if an earlier write had uploaded it (for tests).</summary>
    internal void Seed(string name, byte[] bytes, string sha256, DateTimeOffset lastModified) =>
        blobs[name] = (bytes, new BlobReference(name, sha256, bytes.Length, "application/octet-stream", "binary"), null, lastModified);

    /// <summary>Replaces a blob's bytes but keeps its metadata, simulating corruption (for tests).</summary>
    internal void Tamper(string name, byte[] bytes) =>
        blobs[name] = blobs[name] with { Bytes = bytes };

    /// <summary>Deletes a blob unconditionally (for tests).</summary>
    internal void Remove(string name) => blobs.TryRemove(name, out _);

    // A strictly increasing clock, so a touch is always observably newer than the time it follows.
    private DateTimeOffset Now()
    {
        lock (clock)
        {
            var now = DateTimeOffset.UtcNow;
            last = now > last ? now : last.AddTicks(1);
            return last;
        }
    }
}
