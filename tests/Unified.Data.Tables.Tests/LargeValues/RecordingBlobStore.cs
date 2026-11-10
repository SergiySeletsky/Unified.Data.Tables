using System.Runtime.CompilerServices;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Forwards to a real <see cref="InMemoryOffloadBlobStore"/> and appends each call to a log the table
/// mock also appends to (<c>blob:get</c>, <c>blob:upload</c>, <c>blob:touch</c>, <c>blob:read</c>,
/// <c>blob:delete</c>, <c>blob:list</c>), so a test can assert the order of blob and table calls.
/// </summary>
internal sealed class RecordingBlobStore(List<string> log, InMemoryOffloadBlobStore? inner = null) : IOffloadBlobStore
{
    public InMemoryOffloadBlobStore Inner { get; } = inner ?? new InMemoryOffloadBlobStore();

    public List<string> Log { get; } = log;

    /// <summary>The blob names passed to each call, in order (<c>op name</c>).</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Read uploads to their end and count the bytes, but keep nothing (for very large content).</summary>
    public bool DiscardUploads { get; init; }

    /// <summary>Bytes the last discarded upload carried.</summary>
    public long DiscardedBytes { get; private set; }

    /// <summary>Thrown by every delete when set.</summary>
    public Exception? DeleteFailure { get; set; }

    /// <summary>Thrown by every state read when set.</summary>
    public Exception? GetStateFailure { get; set; }

    /// <summary>Runs before each state read (lets a test change a source between prepare and upload).</summary>
    public Action<string>? OnGetState { get; set; }

    private void Record(string op, string name)
    {
        lock (Log) Log.Add("blob:" + op);
        lock (Calls) Calls.Add(op + " " + name);
    }

    public int Count(string op)
    {
        lock (Log) return Log.Count(l => l == "blob:" + op);
    }

    public Task<BlobState?> GetStateAsync(string name, CancellationToken ct)
    {
        Record("get", name);
        OnGetState?.Invoke(name);
        if (GetStateFailure is { } failure) throw failure;
        return Inner.GetStateAsync(name, ct);
    }

    public async Task UploadCreateOnlyAsync(string name, Stream content, BlobReference reference, string? contentEncoding, CancellationToken ct)
    {
        Record("upload", name);
        if (!DiscardUploads)
        {
            await Inner.UploadCreateOnlyAsync(name, content, reference, contentEncoding, ct);
            return;
        }

        var buffer = new byte[81_920];
        long total = 0;
        int n;
        while ((n = await content.ReadAsync(buffer, ct)) > 0)
            total += n;
        DiscardedBytes = total;
    }

    public Task TouchAsync(string name, CancellationToken ct)
    {
        Record("touch", name);
        return Inner.TouchAsync(name, ct);
    }

    public Task<Stream> OpenReadAsync(string column, string name, CancellationToken ct)
    {
        Record("read", name);
        return Inner.OpenReadAsync(column, name, ct);
    }

    public Task<bool> DeleteIfUnmodifiedAsync(string name, DateTimeOffset? unmodifiedSince, CancellationToken ct)
    {
        Record("delete", name);
        if (DeleteFailure is { } failure) throw failure;
        return Inner.DeleteIfUnmodifiedAsync(name, unmodifiedSince, ct);
    }

    public async IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        Record("list", prefix);
        await foreach (var item in Inner.ListAsync(prefix, ct))
            yield return item;
    }
}
