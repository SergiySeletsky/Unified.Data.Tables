using System.Security.Cryptography;

namespace Unified.Data.Tables;

/// <summary>
/// A read-only pass-through stream that hashes what it hands out and checks the byte count and SHA-256
/// against the expected values. The hash is compared as soon as the expected number of bytes is in: the
/// read that would hand out the final byte throws instead of returning it when the content does not
/// match, so a caller that reads exactly the expected length never gets changed content. Bytes beyond
/// the expected length are never handed out, and a stream that ends early fails at its end. A mismatch
/// throws the exception <c>onMismatch</c> builds from the reason, so a reader never mistakes changed or
/// partial content for the real thing. With no expected values it is a plain non-closing (when
/// <c>leaveOpen</c>) wrapper.
/// </summary>
internal sealed class HashingReadStream : Stream
{
    private readonly Stream inner;
    private readonly long? expectedLength;
    private readonly string? expectedSha256;
    private readonly Func<string, Exception> onMismatch;
    private readonly bool leaveOpen;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long read;
    private bool hashChecked;
    private bool verified;

    internal HashingReadStream(Stream inner, long? expectedLength, string? expectedSha256, Func<string, Exception> onMismatch, bool leaveOpen)
    {
        Guard.NotNull(inner, nameof(inner));
        Guard.NotNull(onMismatch, nameof(onMismatch));
        this.inner = inner;
        this.expectedLength = expectedLength;
        this.expectedSha256 = expectedSha256;
        this.onMismatch = onMismatch;
        this.leaveOpen = leaveOpen;
    }

    /// <summary>True once the end of the inner stream was reached and the content matched.</summary>
    internal bool Verified => verified;

    /// <summary>
    /// The exception a failed check raised, kept so a caller can report it even when the consumer (an
    /// SDK pipeline) wrapped, retried past or swallowed it; null while no check failed. Once set, every
    /// later read throws it again: no more data is handed out.
    /// </summary>
    internal Exception? Mismatch { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => expectedLength ?? throw new NotSupportedException();

    public override long Position
    {
        get => read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Account(buffer, offset, count, inner.Read(buffer, offset, count));

#pragma warning disable CA1835 // the byte[] overload is the only one on netstandard2.0; net10.0 also overrides the Memory one
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Account(buffer, offset, count, await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));
#pragma warning restore CA1835

#if NET
    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        return Account(buffer, n);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Account(buffer.Span, n);
    }

    private int Account(ReadOnlySpan<byte> buffer, int n)
    {
        if (n > 0)
        {
            BeforeDelivering(n);
            hash.AppendData(buffer.Slice(0, n));
            AfterDelivering(n);
        }
        else if (buffer.Length > 0)
        {
            VerifyEnd();
        }
        return n;
    }
#endif

    /// <summary>Reads whatever the consumer left unread and verifies; throws on a mismatch.</summary>
    internal async Task DrainAndVerifyAsync(CancellationToken ct)
    {
        if (verified) return;
        var buffer = new byte[8192];
#pragma warning disable CA1835 // see ReadAsync above
        while (await ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false) > 0)
        {
        }
#pragma warning restore CA1835
    }

    private int Account(byte[] buffer, int offset, int count, int n)
    {
        if (n > 0)
        {
            BeforeDelivering(n);
            hash.AppendData(buffer, offset, n);
            AfterDelivering(n);
        }
        else if (count > 0)
        {
            VerifyEnd();
        }
        return n;
    }

    // Before n more bytes are handed out: none after a failed check, and none beyond the expected length.
    private void BeforeDelivering(int n)
    {
        if (Mismatch is not null) throw Mismatch;
        if (expectedLength is long length && read + n > length)
            throw Mismatch = onMismatch($"the content is longer than the expected {length} bytes");
    }

    // After n bytes were hashed: the hash is compared the moment the last expected byte is in, so the
    // read that would hand out that byte throws instead when the content does not match.
    private void AfterDelivering(int n)
    {
        read += n;
        if (expectedLength is long length && read == length)
            CheckHash();
    }

    // The inner stream ended: it must have held exactly the expected bytes.
    private void VerifyEnd()
    {
        if (verified) return;
        // A failed check is final: report that outcome again rather than a new one.
        if (Mismatch is not null) throw Mismatch;
        if (expectedLength is long length && read != length)
            throw Mismatch = onMismatch($"read {read} bytes, expected {length}");
        CheckHash();
        verified = true;
    }

    // Compares the SHA-256 of everything handed out so far; the hash is consumed, so it runs once.
    private void CheckHash()
    {
        if (hashChecked) return;
        var actual = Hex.Lower(hash.GetHashAndReset());
        if (expectedSha256 is not null && !string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw Mismatch = onMismatch($"SHA-256 mismatch (expected {expectedSha256}, computed {actual})");
        hashChecked = true;
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
        {
            hash.Dispose();
            if (!leaveOpen) inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Lower-case hex without the net5+ <c>Convert.ToHexString</c>.</summary>
internal static class Hex
{
    internal static string Lower(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[2 * i] = Digit(bytes[i] >> 4);
            chars[(2 * i) + 1] = Digit(bytes[i] & 0xF);
        }
        return new string(chars);
    }

    private static char Digit(int v) => (char)(v < 10 ? '0' + v : 'a' + (v - 10));
}
