using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

public class ValueOffloadCoordinatorTests
{
    private const string Pk = "p";
    private const string Rk = "r";

    private static ValueOffloadCoordinator Coordinator(CountingOffloadBlobStore store) =>
        new(store, new BlobOffloadOptions { ThresholdBytes = 100_000 }, "T");

    [Fact]
    public async Task Upload_SameNameSameHash_TouchesAndSkipsBytes()
    {
        var store = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = Coordinator(store);
        var body = Ascii(200_000);
        var bytes = Utf8(body);
        var name = ValueOffloadCoordinator.BlobName("T", Pk, Rk, "Body", Sha(bytes));
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        store.Inner.Seed(name, bytes, Sha(bytes), old);

        var (row, _, blobs) = coordinator.Serialize(new SplitTextEntity { Body = body }, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);

        Assert.Equal(0, store.UploadCalls);
        Assert.Equal(1, store.TouchCalls);
        var state = await store.Inner.GetStateAsync(name, CancellationToken.None);
        Assert.True(state!.LastModified > old);

        var ctx = await coordinator.ResolveAsync(row, typeof(SplitTextEntity), null, CancellationToken.None);
        Assert.Equal(body, TableEntitySerializer.FromTableEntity<SplitTextEntity>(row, ctx).Body);
    }

    [Fact]
    public async Task Upload_SameNameDifferentHash_ThrowsIntegrity()
    {
        var store = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = Coordinator(store);
        var body = Ascii(200_000);
        var name = ValueOffloadCoordinator.BlobName("T", Pk, Rk, "Body", Sha(Utf8(body)));
        store.Inner.Seed(name, [1, 2, 3], Sha([1, 2, 3]), DateTimeOffset.UtcNow);

        var (_, _, blobs) = coordinator.Serialize(new SplitTextEntity { Body = body }, Pk, Rk, 0);
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => coordinator.UploadAsync(blobs, CancellationToken.None));
    }

    [Fact]
    public async Task Upload_SourceChangedAfterPrepare_Throws()
    {
        var store = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = Coordinator(store);
        var data = RandomBytes(50_000);
        var source = new MemoryStream(data);
        var entity = new BlobEntity { Content = TableBlobContent.FromStream(source, "application/octet-stream") };

        await coordinator.PrepareAsync(entity, CancellationToken.None);
        data[100] ^= 0xFF; // the caller changes the content after it was prepared
        var (_, _, blobs) = coordinator.Serialize(entity, Pk, Rk, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.UploadAsync(blobs, CancellationToken.None));
        Assert.Empty(store.Inner.Names);
        Assert.Null(entity.Content.BoundPath);
    }

    [Fact]
    public async Task Upload_BindsContentToItsBlob()
    {
        var store = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = Coordinator(store);
        var data = RandomBytes(5_000);
        var entity = new BlobEntity { Content = TableBlobContent.FromStream(new MemoryStream(data), "image/png") };

        await coordinator.PrepareAsync(entity, CancellationToken.None);
        var (_, _, blobs) = coordinator.Serialize(entity, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);

        Assert.Equal(ValueOffloadCoordinator.BlobName("T", Pk, Rk, "Content", Sha(data)), entity.Content.BoundPath);
        await using var stream = await entity.Content.OpenReadAsync();
        Assert.Equal(data, await ReadAllAsync(stream));
    }

    [Fact]
    public async Task Upload_ContentBoundElsewhere_CopiesFromItsSourceBlob()
    {
        var store = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = Coordinator(store);
        var data = RandomBytes(5_000);
        var (row, _, blobs) = coordinator.Serialize(new BlobEntity { Content = TableBlobContent.FromBytes(data, "image/png") }, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);
        var read = TableEntitySerializer.FromTableEntity<BlobEntity>(
            row, await coordinator.ResolveAsync(row, typeof(BlobEntity), null, CancellationToken.None));

        var (_, _, copyBlobs) = coordinator.Serialize(new BlobEntity { Content = read.Content }, Pk, "other", 0);
        await coordinator.UploadAsync(copyBlobs, CancellationToken.None);

        Assert.Equal(2, store.Inner.Names.Count);
        var copyName = ValueOffloadCoordinator.BlobName("T", Pk, "other", "Content", Sha(data));
        await using var stream = await store.Inner.OpenReadAsync("Content", copyName, CancellationToken.None);
        Assert.Equal(data, await ReadAllAsync(stream));
    }

    [Fact]
    public async Task Resolve_MissingBlob_ThrowsIntegrity()
    {
        var store = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = Coordinator(store);
        var (row, _, blobs) = coordinator.Serialize(new SplitTextEntity { Body = Ascii(200_000) }, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);
        store.Inner.Remove(Assert.Single(store.Inner.Names));

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(
            () => coordinator.ResolveAsync(row, typeof(SplitTextEntity), null, CancellationToken.None));
        Assert.Equal("Body", ex.ColumnName);
    }

    [Fact]
    public async Task Resolve_TamperedBlob_ThrowsIntegrity()
    {
        var store = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = Coordinator(store);
        var (row, _, blobs) = coordinator.Serialize(new SplitTextEntity { Body = Ascii(200_000) }, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);
        store.Inner.Tamper(Assert.Single(store.Inner.Names), Utf8(Ascii(200_000, seed: 99)));

        await Assert.ThrowsAsync<TableValueIntegrityException>(
            () => coordinator.ResolveAsync(row, typeof(SplitTextEntity), null, CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_ProjectionWithoutProperty_TouchesNoBlob()
    {
        var store = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = Coordinator(store);
        var (row, _, blobs) = coordinator.Serialize(new SplitTextEntity { Body = Ascii(200_000), Title = "t" }, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);
        var callsBefore = store.TotalCalls;

        var ctx = await coordinator.ResolveAsync(row, typeof(SplitTextEntity), ["Title"], CancellationToken.None);

        Assert.Equal(callsBefore, store.TotalCalls);
        var back = TableEntitySerializer.FromTableEntity<SplitTextEntity>(row, ctx);
        Assert.Equal("t", back.Title);
        Assert.Null(back.Body);
    }

    [Fact]
    public async Task Resolve_ProjectionWithProperty_ReturnsWholeValue()
    {
        var store = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = Coordinator(store);
        var body = Ascii(200_000);
        var (row, _, blobs) = coordinator.Serialize(new SplitTextEntity { Body = body }, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);

        var ctx = await coordinator.ResolveAsync(row, typeof(SplitTextEntity), ["Body"], CancellationToken.None);
        Assert.Equal(body, TableEntitySerializer.FromTableEntity<SplitTextEntity>(row, ctx).Body);
    }

    [Fact]
    public async Task Resolve_TableBlobContent_DoesNotDownload()
    {
        var store = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = Coordinator(store);
        var data = RandomBytes(20_000);
        var (row, _, blobs) = coordinator.Serialize(new BlobEntity { Content = TableBlobContent.FromBytes(data, "image/png"), Title = "t" }, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);

        var ctx = await coordinator.ResolveAsync(row, typeof(BlobEntity), null, CancellationToken.None);
        var back = TableEntitySerializer.FromTableEntity<BlobEntity>(row, ctx);

        Assert.Equal(0, store.OpenReadCalls);
        Assert.Equal(20_000, back.Content!.Length);
        Assert.Equal(Sha(data), back.Content.Sha256);
        Assert.Equal("image/png", back.Content.ContentType);
        Assert.Equal(0, store.OpenReadCalls);

        await using var stream = await back.Content.OpenReadAsync();
        Assert.Equal(1, store.OpenReadCalls);
        Assert.Equal(data, await ReadAllAsync(stream));
    }

    [Fact]
    public void References_ListsEveryBlobReference()
    {
        var store = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var entity = new TextAndBlobEntity { Body = Ascii(200_000), Content = TableBlobContent.FromBytes([1, 2], "image/png") };
        var (row, _, _) = Coordinator(store).Serialize(entity, Pk, Rk, 0);

        var references = ValueOffloadCoordinator.References(row);
        Assert.Equal(2, references.Count);
        Assert.Contains(references, r => r.Encoding == "text" && r.Length == 200_000);
        Assert.Contains(references, r => r.Encoding == "binary" && r.ContentType == "image/png");
    }

    [Fact]
    public void BlobReference_RoundTripsAndRejectsMalformed()
    {
        var reference = new Providers.BlobReference("T/p/r/Body/abc", "abc", 12, "text/plain; charset=utf-8", "text");
        Assert.Equal(
            """{"path":"T/p/r/Body/abc","sha256":"abc","length":12,"contentType":"text/plain; charset=utf-8","encoding":"text"}""",
            reference.ToJson());
        Assert.Equal(reference, Providers.BlobReference.Parse("Body", reference.ToJson()));

        Assert.Throws<TableValueIntegrityException>(() => Providers.BlobReference.Parse("Body", "not json"));
        Assert.Throws<TableValueIntegrityException>(() => Providers.BlobReference.Parse("Body", """{"path":"x"}"""));
        Assert.Throws<TableValueIntegrityException>(() => Providers.BlobReference.Parse("Body", """{"path":"x","sha256":"a","length":"1","contentType":"c","encoding":"text"}"""));
    }
}
