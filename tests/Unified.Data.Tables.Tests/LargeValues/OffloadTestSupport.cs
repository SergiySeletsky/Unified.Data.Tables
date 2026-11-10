using System.Security.Cryptography;
using System.Text;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Tests.LargeValues;

internal static class OffloadTestSupport
{
    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string Ascii(int length, int seed = 7)
    {
        var rng = new Random(seed);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)rng.Next(0x21, 0x7E);
        return new string(chars);
    }

    internal static byte[] RandomBytes(int length, int seed = 11)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    internal static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    internal static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}

/// <summary>Counts the calls a test cares about and forwards them to a real in-memory store.</summary>
internal sealed class CountingOffloadBlobStore(InMemoryOffloadBlobStore inner) : IOffloadBlobStore
{
    public InMemoryOffloadBlobStore Inner { get; } = inner;

    public int GetStateCalls { get; private set; }

    public int UploadCalls { get; private set; }

    public int TouchCalls { get; private set; }

    public int OpenReadCalls { get; private set; }

    public int TotalCalls => GetStateCalls + UploadCalls + TouchCalls + OpenReadCalls;

    public Task<BlobState?> GetStateAsync(string name, CancellationToken ct)
    {
        GetStateCalls++;
        return Inner.GetStateAsync(name, ct);
    }

    public Task UploadCreateOnlyAsync(string name, Stream content, BlobReference reference, string? contentEncoding, CancellationToken ct)
    {
        UploadCalls++;
        return Inner.UploadCreateOnlyAsync(name, content, reference, contentEncoding, ct);
    }

    public Task TouchAsync(string name, CancellationToken ct)
    {
        TouchCalls++;
        return Inner.TouchAsync(name, ct);
    }

    public Task<Stream> OpenReadAsync(string column, string name, CancellationToken ct)
    {
        OpenReadCalls++;
        return Inner.OpenReadAsync(column, name, ct);
    }

    public Task<bool> DeleteIfUnmodifiedAsync(string name, DateTimeOffset? unmodifiedSince, CancellationToken ct) =>
        Inner.DeleteIfUnmodifiedAsync(name, unmodifiedSince, ct);

    public IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListAsync(string prefix, CancellationToken ct) =>
        Inner.ListAsync(prefix, ct);

    public Task<DateTimeOffset?> GetServiceTimeAsync(CancellationToken ct) => Inner.GetServiceTimeAsync(ct);
}

/// <summary>A deterministic, allocation-free generated stream (optionally non-seekable).</summary>
internal sealed class GeneratedStream(long length, bool seekable) : Stream
{
    private long position;

    public override bool CanRead => true;

    public override bool CanSeek => seekable;

    public override bool CanWrite => false;

    public override long Length => seekable ? length : throw new NotSupportedException();

    public override long Position
    {
        get => seekable ? position : throw new NotSupportedException();
        set
        {
            if (!seekable) throw new NotSupportedException();
            position = value;
        }
    }

    internal static byte At(long i) => (byte)((i * 31) ^ (i >> 9));

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = (int)Math.Min(buffer.Length, length - position);
        for (var i = 0; i < n; i++)
            buffer[i] = At(position + i);
        position += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!seekable) throw new NotSupportedException();
        position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            _ => length + offset,
        };
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
