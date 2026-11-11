using Azure.Storage.Blobs.Models;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A read of a <see cref="TableBlobContent"/> is verified as soon as the expected number of bytes has
/// been delivered, not only when a further read finds the end of the stream: the read that would hand
/// out the final byte compares the SHA-256 first and throws instead of returning data that does not
/// match. So a caller that reads exactly <c>Length</c> bytes never gets changed content. More bytes than
/// expected, or fewer, still fail.
/// </summary>
public class ExactLengthReadTests
{
    private const int Size = 10_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The four read overloads the stream implements.</summary>
    public enum ReadMode
    {
        SyncArray,
        SyncSpan,
        AsyncArray,
        AsyncMemory,
    }

    public static TheoryData<ReadMode, int> ModesAndChunks()
    {
        var data = new TheoryData<ReadMode, int>();
        foreach (var mode in Enum.GetValues<ReadMode>())
        {
            foreach (var chunk in new[] { 1, 7, 4_096, Size, Size + 100 })
                data.Add(mode, chunk);
        }
        return data;
    }

    public static TheoryData<ReadMode> Modes() => [.. Enum.GetValues<ReadMode>()];

    private static async Task<int> ReadAsync(Stream stream, byte[] buffer, int offset, int count, ReadMode mode)
    {
#pragma warning disable CA1835 // the array overload is one of the overloads under test
        return mode switch
        {
            ReadMode.SyncArray => stream.Read(buffer, offset, count),
            ReadMode.SyncSpan => stream.Read(buffer.AsSpan(offset, count)),
            ReadMode.AsyncArray => await stream.ReadAsync(buffer, offset, count, Ct),
            _ => await stream.ReadAsync(buffer.AsMemory(offset, count), Ct),
        };
#pragma warning restore CA1835
    }

    private static HashingReadStream Verifying(byte[] stored, byte[] expected) =>
        new(new MemoryStream(stored, writable: false), expected.Length, Sha(expected),
            reason => new TableValueIntegrityException("File", reason), leaveOpen: false);

    private static byte[] Tampered(byte[] bytes, int at)
    {
        var copy = (byte[])bytes.Clone();
        copy[at] ^= 0xFF;
        return copy;
    }

    // ── the stream ─────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ModesAndChunks))]
    public async Task RightLengthWrongBytes_TheReadThatWouldDeliverTheFinalByte_Throws(ReadMode mode, int chunk)
    {
        var expected = RandomBytes(Size);
        // The first byte differs: every chunk size sees the difference long before the end.
        await using var stream = Verifying(Tampered(expected, 0), expected);
        var buffer = new byte[Size + 200];
        var total = 0;

        while (true)
        {
            int n;
            try
            {
                n = await ReadAsync(stream, buffer, 0, chunk, mode);
            }
            catch (TableValueIntegrityException ex)
            {
                Assert.Contains("SHA-256 mismatch", ex.Reason, StringComparison.Ordinal);
                Assert.Equal("File", ex.ColumnName);
                break;
            }
            total += n;
            Assert.True(n > 0, "the stream ended without failing");
            Assert.True(total < Size, $"{total} bytes were handed out: the final byte left the stream unverified");
        }

        // Only the read that reached the expected length failed: everything before it was delivered.
        Assert.True(total + chunk >= Size, $"failed after {total} bytes with reads of {chunk}: too early");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task RightLengthWrongLastByte_ReadExactlyLength_Throws(ReadMode mode)
    {
        var expected = RandomBytes(Size);
        await using var stream = Verifying(Tampered(expected, Size - 1), expected);
        var buffer = new byte[Size];

        // Exactly Length bytes, as a caller that knows the length reads them.
        if (mode is ReadMode.SyncArray or ReadMode.SyncSpan)
            Assert.Throws<TableValueIntegrityException>(() => stream.ReadExactly(buffer));
        else
            await Assert.ThrowsAsync<TableValueIntegrityException>(() => stream.ReadExactlyAsync(buffer, Ct).AsTask());
    }

    [Theory]
    [MemberData(nameof(ModesAndChunks))]
    public async Task MatchingContent_IsDeliveredWhole_ThenTheStreamEnds(ReadMode mode, int chunk)
    {
        var expected = RandomBytes(Size);
        await using var stream = Verifying(expected, expected);
        var buffer = new byte[Size + 200];
        var total = 0;

        while (total < Size)
        {
            var n = await ReadAsync(stream, buffer, total, Math.Min(chunk, buffer.Length - total), mode);
            Assert.True(n > 0);
            total += n;
        }

        Assert.Equal(Size, total);
        Assert.Equal(expected, buffer[..Size]);
        Assert.Equal(Size, stream.Position);
        Assert.Null(stream.Mismatch);
        // The end is confirmed by the next read, which also marks the stream verified.
        Assert.Equal(0, await ReadAsync(stream, buffer, 0, chunk, mode));
        Assert.True(stream.Verified);
        Assert.Equal(0, await ReadAsync(stream, buffer, 0, chunk, mode));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task LongerThanExpected_TheExpectedBytesVerify_ReadingOnThrows(ReadMode mode)
    {
        var expected = RandomBytes(Size);
        await using var stream = Verifying([.. expected, 1, 2, 3, 4, 5], expected);
        var buffer = new byte[Size];

        // The first Length bytes are the value (their hash matches); the bytes after them are never handed out.
        var total = 0;
        while (total < Size)
            total += await ReadAsync(stream, buffer, total, Size - total, mode);
        Assert.Equal(expected, buffer);

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadAsync(stream, new byte[16], 0, 16, mode));
        Assert.Contains("expected " + Size, ex.Reason, StringComparison.Ordinal);
        Assert.False(stream.Verified);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task LongerThanExpected_OneReadSpanningTheEnd_ThrowsWithoutData(ReadMode mode)
    {
        var expected = RandomBytes(Size);
        await using var stream = Verifying([.. expected, 1, 2, 3, 4, 5], expected);

        // One read that the inner stream answers with more than Length bytes: none of them is returned.
        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadAsync(stream, new byte[Size + 200], 0, Size + 200, mode));
        Assert.Contains("expected " + Size, ex.Reason, StringComparison.Ordinal);
        Assert.Equal(0, stream.Position);
    }

    [Theory]
    [MemberData(nameof(ModesAndChunks))]
    public async Task ShorterThanExpected_ThrowsAtTheEndOfTheStream(ReadMode mode, int chunk)
    {
        var expected = RandomBytes(Size);
        await using var stream = Verifying(expected[..(Size / 2)], expected);
        var buffer = new byte[Size + 200];
        var total = 0;

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(async () =>
        {
            while (true)
            {
                var n = await ReadAsync(stream, buffer, 0, chunk, mode);
                Assert.True(n > 0, "a short stream must fail, not end");
                total += n;
            }
        });

        Assert.Equal(Size / 2, total);
        Assert.Contains($"read {Size / 2} bytes, expected {Size}", ex.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task AfterAFailedCheck_EveryLaterReadThrowsTheSameException(ReadMode mode)
    {
        var expected = RandomBytes(Size);
        await using var stream = Verifying(Tampered(expected, Size - 1), expected);
        var buffer = new byte[Size];

        var first = await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadAsync(stream, buffer, 0, Size, mode));
        var second = await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadAsync(stream, buffer, 0, Size, mode));

        Assert.Same(first, second);
        Assert.Same(first, stream.Mismatch);
        Assert.False(stream.Verified);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task EmptyContent_VerifiesAtTheFirstRead_AndRefusesAnyByte(ReadMode mode)
    {
        await using (var empty = Verifying([], []))
        {
            Assert.Equal(0, await ReadAsync(empty, new byte[8], 0, 8, mode));
            Assert.True(empty.Verified);
        }

        await using var notEmpty = Verifying([1, 2, 3], []);
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadAsync(notEmpty, new byte[8], 0, 8, mode));
    }

    [Fact]
    public async Task ZeroByteRead_NeitherVerifiesNorFails()
    {
        var expected = RandomBytes(Size);
        await using var stream = Verifying(Tampered(expected, 0), expected);

        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty, Ct));
        Assert.Equal(0, stream.Read([], 0, 0));
        Assert.Null(stream.Mismatch);
        Assert.False(stream.Verified);
    }

    // ── the upload side (a store reads the content it is given through this stream) ─────

    [Fact]
    public async Task UploadSide_AConsumerThatStopsAtTheLength_IsStillDrainedToTheEnd()
    {
        var expected = RandomBytes(Size);
        await using var matching = Verifying(expected, expected);
        await matching.ReadExactlyAsync(new byte[Size], Ct);
        Assert.False(matching.Verified);
        await matching.DrainAndVerifyAsync(Ct);
        Assert.True(matching.Verified);

        // The source grew after it was prepared: the drain finds the extra bytes.
        await using var longer = Verifying([.. expected, 9], expected);
        await longer.ReadExactlyAsync(new byte[Size], Ct);
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => longer.DrainAndVerifyAsync(Ct));
        Assert.NotNull(longer.Mismatch);
    }

    [Fact]
    public async Task UploadSide_ChangedSource_SetsMismatch_WhenTheFinalBytesAreRead()
    {
        var expected = RandomBytes(Size);
        await using var changed = Verifying(Tampered(expected, 100), expected);
        var buffer = new byte[Size];

        // A consumer (an SDK pipeline) that swallows what the content stream throws.
        try
        {
            await changed.ReadExactlyAsync(buffer, Ct);
        }
        catch (TableValueIntegrityException)
        {
        }

        Assert.NotNull(changed.Mismatch);
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => changed.DrainAndVerifyAsync(Ct));
    }

    [Fact]
    public async Task NoExpectedValues_IsAPlainPassThrough()
    {
        var bytes = RandomBytes(Size);
        var inner = new MemoryStream(bytes, writable: false);
        await using (var view = new HashingReadStream(inner, null, null, r => new InvalidOperationException(r), leaveOpen: true))
            Assert.Equal(bytes, await ReadAllAsync(view));

        Assert.True(inner.CanRead); // leaveOpen: the inner stream is the caller's
    }

    // ── through the stores ─────────────────────────────────────────────────

    private static InMemoryStorage<FileEntity> InMemory() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });

    [Fact]
    public async Task InMemory_TamperedBlobOfTheRightLength_ReadExactlyLength_Throws()
    {
        var bytes = RandomBytes(Size);
        var store = InMemory();
        await store.CreateAsync(new FileEntity { Id = "p|r", File = TableBlobContent.FromBytes(bytes, "application/octet-stream") }, Ct);
        store.Blobs.Tamper(Assert.Single(store.Blobs.Names), Tampered(bytes, Size - 1));

        var read = await store.OneAsync("p|r", Ct);
        await using var stream = await read!.File!.OpenReadAsync(Ct);
        var buffer = new byte[read.File.Length];

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => stream.ReadExactlyAsync(buffer, Ct).AsTask());
        Assert.Equal("File", ex.ColumnName);
    }

    [Fact]
    public async Task InMemory_IntactBlob_ReadExactlyLength_ReturnsTheBytes()
    {
        var bytes = RandomBytes(Size);
        var store = InMemory();
        await store.CreateAsync(new FileEntity { Id = "p|r", File = TableBlobContent.FromBytes(bytes, "application/octet-stream") }, Ct);

        var read = await store.OneAsync("p|r", Ct);
        await using var stream = await read!.File!.OpenReadAsync(Ct);
        var buffer = new byte[read.File.Length];
        await stream.ReadExactlyAsync(buffer, Ct);

        Assert.Equal(bytes, buffer);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], Ct));
    }
}

/// <summary>The same guarantee against Azurite: the blob the service returns is what gets verified.</summary>
public sealed class AzuriteExactLengthReadTests : AzuriteContentTestBase
{
    [Fact]
    public async Task TamperedBlobOfTheRightLength_ReadExactlyLength_Throws()
    {
        Assert.SkipUnless(Available, SkipReason);
        const int size = 200_000;
        var bytes = RandomBytes(size, seed: 71);
        await Store<FileEntity>().CreateAsync(new FileEntity { Id = Id("x1"), File = TableBlobContent.FromBytes(bytes, "application/octet-stream") }, Ct);
        var item = Assert.Single(await BlobsAsync<FileEntity>());

        // Same length, same metadata, different last byte.
        var tampered = (byte[])bytes.Clone();
        tampered[^1] ^= 0xFF;
        await Container.GetBlobClient(item.Name).UploadAsync(new BinaryData(tampered), new BlobUploadOptions
        {
            Metadata = item.Metadata,
            HttpHeaders = new BlobHttpHeaders { ContentType = item.Properties.ContentType },
        }, Ct);

        var read = await Store<FileEntity>().OneAsync(Id("x1"), Ct);
        Assert.Equal(size, read!.File!.Length);
        await using var stream = await read.File.OpenReadAsync(Ct);
        var buffer = new byte[size];

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => stream.ReadExactlyAsync(buffer, Ct).AsTask());
    }

    [Fact]
    public async Task IntactBlob_ReadExactlyLength_ReturnsTheBytes()
    {
        Assert.SkipUnless(Available, SkipReason);
        const int size = 200_000;
        var bytes = RandomBytes(size, seed: 72);
        await Store<FileEntity>().CreateAsync(new FileEntity { Id = Id("x2"), File = TableBlobContent.FromBytes(bytes, "application/octet-stream") }, Ct);

        var read = await Store<FileEntity>().OneAsync(Id("x2"), Ct);
        await using var stream = await read!.File!.OpenReadAsync(Ct);
        var buffer = new byte[size];
        await stream.ReadExactlyAsync(buffer, Ct);

        Assert.Equal(bytes, buffer);
    }
}
