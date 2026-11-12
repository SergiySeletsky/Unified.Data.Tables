using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

public class TableBlobContentTests
{
    private const string Pk = "p";
    private const string Rk = "r";

    private static ValueOffloadCoordinator Coordinator(InMemoryOffloadBlobStore store) =>
        new(store, new BlobOffloadOptions(), "T");

    [Fact]
    public async Task FromBytes_KnowsLengthAndSha()
    {
        var bytes = RandomBytes(1_000);
        var content = TableBlobContent.FromBytes(bytes, "image/png");

        Assert.Equal(1_000, content.Length);
        Assert.Equal(Sha(bytes), content.Sha256);
        Assert.Equal("image/png", content.ContentType);

        await using var stream = await content.OpenReadAsync();
        Assert.False(stream.CanWrite);
        Assert.Equal(bytes, await ReadAllAsync(stream));
    }

    [Fact]
    public async Task FromStream_LengthBeforeWrite_Throws()
    {
        using var source = new MemoryStream([1, 2, 3]);
        var content = TableBlobContent.FromStream(source, "application/octet-stream");

        Assert.Equal("application/octet-stream", content.ContentType);
        Assert.Contains("known once the entity has been written", Assert.Throws<InvalidOperationException>(() => content.Length).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => content.Sha256);
        await Assert.ThrowsAsync<InvalidOperationException>(() => content.OpenReadAsync());
        Assert.Equal(0, source.Position); // the caller's stream is neither read nor disposed
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task PrepareAsync_SeekableStream_HashesWithoutBuffering()
    {
        const long size = 100L * 1024 * 1024;
        var source = new GeneratedStream(size, seekable: true);
        var entity = new BlobEntity { Content = TableBlobContent.FromStream(source, "application/octet-stream") };
        var coordinator = Coordinator(new InMemoryOffloadBlobStore());

        await coordinator.PrepareAsync(entity, CancellationToken.None); // warm up JIT and pools
        entity.Content = TableBlobContent.FromStream(source, "application/octet-stream");
        source.Position = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        await coordinator.PrepareAsync(entity, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 16L * 1024 * 1024, $"allocated {allocated:N0} B");
        Assert.Equal(0, source.Position);
        Assert.Equal(size, entity.Content.Length);
        Assert.Equal(64, entity.Content.Sha256.Length);
    }

    [Fact]
    public async Task PrepareAsync_NonSeekableStream_SpoolsToTempFile()
    {
        const int size = 300_000;
        var expected = new byte[size];
        for (var i = 0; i < size; i++) expected[i] = GeneratedStream.At(i);

        var store = new InMemoryOffloadBlobStore();
        var coordinator = Coordinator(store);
        var entity = new BlobEntity { Content = TableBlobContent.FromStream(new GeneratedStream(size, seekable: false), "application/octet-stream") };

        await coordinator.PrepareAsync(entity, CancellationToken.None);
        Assert.Equal(size, entity.Content.Length);
        Assert.Equal(Sha(expected), entity.Content.Sha256);
        Assert.IsType<FileStream>(entity.Content.Source);

        var (_, _, blobs) = coordinator.Serialize(entity, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);

        var name = Assert.Single(store.Names);
        Assert.Equal(name, entity.Content.BoundPath);
        await using var read = await entity.Content.OpenReadAsync();
        Assert.Equal(expected, await ReadAllAsync(read));
    }

    [Fact]
    public async Task OpenRead_TamperedBlob_ThrowsAtEndOfStream()
    {
        var bytes = RandomBytes(200_000);
        var store = new InMemoryOffloadBlobStore();
        var coordinator = Coordinator(store);
        var (row, _, blobs) = coordinator.Serialize(new BlobEntity { Content = TableBlobContent.FromBytes(bytes, "application/octet-stream") }, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);

        var tampered = (byte[])bytes.Clone();
        tampered[^1] ^= 0xFF;
        store.Tamper(Assert.Single(store.Names), tampered);

        var ctx = await coordinator.ResolveAsync(row, typeof(BlobEntity), null, CancellationToken.None);
        var back = TableEntitySerializer.FromTableEntity<BlobEntity>(row, ctx);
        Assert.Equal(200_000, back.Content!.Length);

        await using var stream = await back.Content.OpenReadAsync();
        var buffer = new byte[1_000];
        Assert.Equal(1_000, await stream.ReadAsync(buffer)); // the first reads succeed...
        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => stream.CopyToAsync(Stream.Null));
        Assert.Equal("Content", ex.ColumnName); // ...end-of-stream verification fails
    }

    [Fact]
    public async Task OpenRead_TruncatedBlob_ThrowsAtEndOfStream()
    {
        var bytes = RandomBytes(10_000);
        var store = new InMemoryOffloadBlobStore();
        var coordinator = Coordinator(store);
        var (row, _, blobs) = coordinator.Serialize(new BlobEntity { Content = TableBlobContent.FromBytes(bytes, "application/octet-stream") }, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);
        store.Tamper(Assert.Single(store.Names), bytes[..5_000]);

        var back = TableEntitySerializer.FromTableEntity<BlobEntity>(
            row, await coordinator.ResolveAsync(row, typeof(BlobEntity), null, CancellationToken.None));
        await using var stream = await back.Content!.OpenReadAsync();
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public void OversizedValueException_WordingFollowsExplicitFlag()
    {
        var notConfigured = new OversizedValueException("Body", 10, "too large");
        Assert.False(notConfigured.OffloadForbiddenByProperty);
        Assert.Contains("Configure UnifiedTableStorageOptions.BlobOffload", notConfigured.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BlobOffload.AboveThreshold", notConfigured.Message, StringComparison.Ordinal);

        // The reason text no longer chooses the fix: only the explicit flag does.
        var forbidden = new OversizedValueException("Body", 10, "too large", offloadForbiddenByProperty: true);
        Assert.True(forbidden.OffloadForbiddenByProperty);
        Assert.Contains("Set the property's Offload to BlobOffload.AboveThreshold", forbidden.Message, StringComparison.Ordinal);

        var mentionsNever = new OversizedValueException("Body", 10, "the property says BlobOffload.Never");
        Assert.DoesNotContain("Set the property's Offload", mentionsNever.Message, StringComparison.Ordinal);
    }
}
