using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// <see cref="AzureOffloadBlobStore"/> against NSubstitute blob clients: create-only uploads carrying the
/// hash and length as metadata with a bounded transfer size, a 409 that is success only for the same
/// hash, 404 mapped to integrity failures, and conditional deletes.
/// </summary>
public class AzureOffloadBlobStoreTests
{
    private const string Name = "T/p/r/File/abc";
    private const string Sha = "abc";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly BlobReference Ref = new(Name, Sha, 3, "image/png", "binary");

    private sealed class Rig
    {
        public Rig()
        {
            Container.GetBlobClient(Name).Returns(Blob);
            Container.CreateIfNotExistsAsync(Arg.Any<PublicAccessType>(), Arg.Any<IDictionary<string, string>>(),
                    Arg.Any<BlobContainerEncryptionScopeOptions>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Response<BlobContainerInfo>>(null!));
            Store = new AzureOffloadBlobStore(Container);
        }

        public BlobContainerClient Container { get; } = Substitute.For<BlobContainerClient>();

        public BlobClient Blob { get; } = Substitute.For<BlobClient>();

        public AzureOffloadBlobStore Store { get; }

        public void Properties(string sha, DateTimeOffset? lastModified = null)
        {
            var properties = BlobsModelFactory.BlobProperties(
                lastModified: lastModified ?? DateTimeOffset.UnixEpoch,
                metadata: new Dictionary<string, string> { ["sha256"] = sha, ["length"] = "3" });
            Blob.GetPropertiesAsync(Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>())
                .Returns(Response.FromValue(properties, new FakeResponse()));
        }
    }

    private static RequestFailedException Status(int status, string code) => new(status, code, code, null);

    [Fact]
    public async Task Upload_IsCreateOnly_WithMetadataAndHeaders()
    {
        var rig = new Rig();
        BlobUploadOptions? sent = null;
        rig.Blob.UploadAsync(Arg.Any<Stream>(), Arg.Do<BlobUploadOptions>(o => sent = o), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Response<BlobContentInfo>>(null!));

        await rig.Store.UploadCreateOnlyAsync(Name, new MemoryStream([1, 2, 3]), Ref, "gzip", Ct);

        Assert.NotNull(sent);
        Assert.Equal(ETag.All, sent!.Conditions.IfNoneMatch);
        Assert.Equal("image/png", sent.HttpHeaders.ContentType);
        Assert.Equal("gzip", sent.HttpHeaders.ContentEncoding);
        Assert.Equal(Sha, sent.Metadata["sha256"]);
        Assert.Equal("3", sent.Metadata["length"]);
        // The content stream is not seekable, so these bound what the uploader buffers.
        Assert.Equal(4 * 1024 * 1024, sent.TransferOptions.MaximumTransferSize);
        Assert.Equal(4 * 1024 * 1024, sent.TransferOptions.InitialTransferSize);
        Assert.Equal(4, sent.TransferOptions.MaximumConcurrency);
        await rig.Container.Received(1).CreateIfNotExistsAsync(Arg.Any<PublicAccessType>(), Arg.Any<IDictionary<string, string>>(),
            Arg.Any<BlobContainerEncryptionScopeOptions>(), CancellationToken.None);
    }

    [Fact]
    public async Task Upload_CreatesContainerOnce()
    {
        var rig = new Rig();
        rig.Blob.UploadAsync(Arg.Any<Stream>(), Arg.Any<BlobUploadOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Response<BlobContentInfo>>(null!));

        await rig.Store.UploadCreateOnlyAsync(Name, new MemoryStream([1, 2, 3]), Ref, null, Ct);
        await rig.Store.UploadCreateOnlyAsync(Name, new MemoryStream([1, 2, 3]), Ref, null, Ct);

        await rig.Container.Received(1).CreateIfNotExistsAsync(Arg.Any<PublicAccessType>(), Arg.Any<IDictionary<string, string>>(),
            Arg.Any<BlobContainerEncryptionScopeOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_409_SameSha_IsSuccess()
    {
        var rig = new Rig();
        rig.Blob.UploadAsync(Arg.Any<Stream>(), Arg.Any<BlobUploadOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Status(409, "BlobAlreadyExists"));
        rig.Properties(Sha);

        await rig.Store.UploadCreateOnlyAsync(Name, new MemoryStream([1, 2, 3]), Ref, null, Ct);

        // An existing blob of the same content is refreshed so a sweep that listed it leaves it alone.
        await rig.Blob.Received(1).SetMetadataAsync(
            Arg.Is<IDictionary<string, string>>(m => m["sha256"] == Sha), Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_409_DifferentSha_ThrowsIntegrity()
    {
        var rig = new Rig();
        rig.Blob.UploadAsync(Arg.Any<Stream>(), Arg.Any<BlobUploadOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Status(409, "BlobAlreadyExists"));
        rig.Properties("other");

        await Assert.ThrowsAsync<TableValueIntegrityException>(() =>
            rig.Store.UploadCreateOnlyAsync(Name, new MemoryStream([1, 2, 3]), Ref, null, Ct));
    }

    [Fact]
    public async Task GetState_ReadsMetadata_404IsNull()
    {
        var rig = new Rig();
        var modified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        rig.Properties(Sha, modified);

        var state = await rig.Store.GetStateAsync(Name, Ct);

        Assert.Equal(new BlobState(Sha, 3, modified), state);

        rig.Blob.GetPropertiesAsync(Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Status(404, "BlobNotFound"));
        Assert.Null(await rig.Store.GetStateAsync(Name, Ct));
    }

    [Fact]
    public async Task Touch_404_ThrowsIntegrity()
    {
        var rig = new Rig();
        rig.Properties(Sha);
        rig.Blob.SetMetadataAsync(Arg.Any<IDictionary<string, string>>(), Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Status(404, "BlobNotFound"));

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => rig.Store.TouchAsync(Name, Ct));
    }

    [Fact]
    public async Task OpenRead_404_ThrowsIntegrity()
    {
        var rig = new Rig();
        rig.Blob.OpenReadAsync(Arg.Any<BlobOpenReadOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Status(404, "BlobNotFound"));

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => rig.Store.OpenReadAsync("File", Name, Ct));
        Assert.Contains("File", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRead_ReturnsTheBlobStream()
    {
        var rig = new Rig();
        var content = new MemoryStream([1, 2, 3]);
        rig.Blob.OpenReadAsync(Arg.Any<BlobOpenReadOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<Stream>(content));

        Assert.Same(content, await rig.Store.OpenReadAsync("File", Name, Ct));
    }

    [Fact]
    public async Task Delete_PassesIfUnmodifiedSince()
    {
        var rig = new Rig();
        var since = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        BlobRequestConditions? conditions = null;
        rig.Blob.DeleteAsync(Arg.Any<DeleteSnapshotsOption>(), Arg.Do<BlobRequestConditions>(c => conditions = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Response>(new FakeResponse()));

        Assert.True(await rig.Store.DeleteIfUnmodifiedAsync(Name, since, Ct));
        Assert.Equal(since, conditions!.IfUnmodifiedSince);

        Assert.True(await rig.Store.DeleteIfUnmodifiedAsync(Name, null, Ct));
        Assert.Null(conditions!.IfUnmodifiedSince);
    }

    [Theory]
    [InlineData(404, "BlobNotFound")]
    [InlineData(412, "ConditionNotMet")]
    public async Task Delete_MissingOrModified_IsFalse(int status, string code)
    {
        var rig = new Rig();
        rig.Blob.DeleteAsync(Arg.Any<DeleteSnapshotsOption>(), Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Status(status, code));

        Assert.False(await rig.Store.DeleteIfUnmodifiedAsync(Name, DateTimeOffset.UtcNow, Ct));
    }

    [Fact]
    public async Task List_YieldsNamesAndLastModified()
    {
        var rig = new Rig();
        var modified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var item = BlobsModelFactory.BlobItem(name: Name, properties: BlobsModelFactory.BlobItemProperties(accessTierInferred: false, lastModified: modified));
        var page = Page<BlobItem>.FromValues([item], null, new FakeResponse());
        GetBlobsOptions? options = null;
        rig.Container.GetBlobsAsync(Arg.Do<GetBlobsOptions>(o => options = o), Arg.Any<CancellationToken>())
            .Returns(AsyncPageable<BlobItem>.FromPages([page]));

        var listed = new List<(string, DateTimeOffset)>();
        await foreach (var entry in rig.Store.ListAsync("T/p/", Ct))
            listed.Add(entry);

        Assert.Equal([(Name, modified)], listed);
        Assert.Equal("T/p/", options!.Prefix);
    }
}
