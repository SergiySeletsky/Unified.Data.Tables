using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.NeutralFailures;

/// <summary>
/// 0.10.0, break B1, for the two other places an Azure exception could leave the library: the blob
/// container that holds offloaded values, and <see cref="PolymorphicTableStorage{TBase}"/>. A failure
/// of the blob store is neutral too, but it never says a row or a table is missing, a duplicate or in
/// conflict: a blob's 404, 409 and 412 are plain <see cref="StorageException"/>s.
/// </summary>
public class BlobAndPolymorphicErrorMappingTests
{
    private const string Name = "T/p/r/File/abc";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly BlobReference Ref = new(Name, "abc", 3, "image/png", "binary");

    private static RequestFailedException Status(int status, string code) => new(status, code, code, null);

    private sealed class BlobRig
    {
        public BlobRig()
        {
            Container.Name.Returns("offload");
            // The store checks that the SDK addresses the name it was given.
            string? requested = null;
            Blob.Name.Returns(_ => requested!);
            Container.GetBlobClient(Arg.Do<string>(name => requested = name)).Returns(Blob);
            Container.CreateIfNotExistsAsync(Arg.Any<PublicAccessType>(), Arg.Any<IDictionary<string, string>>(),
                    Arg.Any<BlobContainerEncryptionScopeOptions>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Response<BlobContainerInfo>>(null!));
            Store = new AzureOffloadBlobStore(Container);
        }

        public BlobContainerClient Container { get; } = Substitute.For<BlobContainerClient>();

        public BlobClient Blob { get; } = Substitute.For<BlobClient>();

        public AzureOffloadBlobStore Store { get; }
    }

    /// <summary>A listing whose first page fails.</summary>
    private sealed class FailingPageable<T>(Exception failure) : AsyncPageable<T> where T : notnull
    {
        public override async IAsyncEnumerable<Page<T>> AsPages(string? continuationToken = null, int? pageSizeHint = null)
        {
            await Task.Yield();
            if (failure is not null)
                throw failure;
            yield break;
        }
    }

    // ── the blob store ─────────────────────────────────────────────────────

    public static TheoryData<int, string, Type> BlobRows => new()
    {
        // Never a missing row or table, a duplicate key or a concurrency conflict.
        { 404, "ContainerNotFound", typeof(StorageException) },
        { 404, "ResourceNotFound", typeof(StorageException) },
        { 409, "BlobImmutableDueToPolicy", typeof(StorageException) },
        { 409, "ContainerBeingDeleted", typeof(StorageException) },
        { 403, "AuthorizationFailure", typeof(StorageException) },
        { 400, "InvalidMetadata", typeof(StorageRequestRejectedException) },
        { 413, "RequestBodyTooLarge", typeof(StorageRequestRejectedException) },
        { 429, "TooManyRequests", typeof(TransientStorageException) },
        { 500, "InternalError", typeof(TransientStorageException) },
        { 503, "ServerBusy", typeof(TransientStorageException) },
    };

    [Theory]
    [MemberData(nameof(BlobRows))]
    public async Task AFailedBlobDelete_IsNeutral_AndNeverARowOrTableFailure(int status, string code, Type expected)
    {
        var rig = new BlobRig();
        var failure = Status(status, code);
        rig.Blob.DeleteAsync(Arg.Any<DeleteSnapshotsOption>(), Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        var thrown = await Record.ExceptionAsync(() => rig.Store.DeleteIfUnmodifiedAsync(Name, DateTimeOffset.UnixEpoch, Ct));

        if (status == 404)
        {
            // A blob that is gone is what a delete wants: not a failure at all (unchanged).
            Assert.Null(thrown);
            return;
        }
        Assert.NotNull(thrown);
        Assert.Equal(expected, thrown.GetType());
        var neutral = (StorageException)thrown;
        Assert.Same(failure, neutral.InnerException);
        Assert.Equal(code, neutral.ProviderErrorCode);
        Assert.Contains("offload", neutral.Message, StringComparison.Ordinal);
        if (neutral is TransientStorageException transient)
            Assert.True(transient.MayHaveCommitted); // a delete changes the store
    }

    [Theory]
    [MemberData(nameof(BlobRows))]
    public async Task AFailedBlobUpload_IsNeutral_AndNeverARowOrTableFailure(int status, string code, Type expected)
    {
        var rig = new BlobRig();
        var failure = Status(status, code);
        rig.Blob.UploadAsync(Arg.Any<Stream>(), Arg.Any<BlobUploadOptions>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        // A 409 makes the store look at the blob that is there: that read fails the same way.
        rig.Blob.GetPropertiesAsync(Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);

        var thrown = await Record.ExceptionAsync(() => rig.Store.UploadCreateOnlyAsync(Name, new MemoryStream([1, 2, 3]), Ref, null, Ct));

        Assert.NotNull(thrown);
        Assert.Equal(expected, thrown.GetType());
        Assert.Same(failure, thrown.InnerException);
        Assert.IsNotType<EntityNotFoundException>(thrown);
        Assert.IsNotType<TableNotFoundException>(thrown);
        Assert.IsNotType<DuplicateKeyException>(thrown);
        Assert.IsNotType<ConcurrencyConflictException>(thrown);
    }

    [Fact]
    public async Task BlobReads_SayNothingMayHaveCommitted()
    {
        var rig = new BlobRig();
        var failure = Status(503, "ServerBusy");
        rig.Blob.GetPropertiesAsync(Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        rig.Blob.OpenReadAsync(Arg.Any<BlobOpenReadOptions>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        rig.Container.GetBlobsAsync(Arg.Any<GetBlobsOptions>(), Arg.Any<CancellationToken>()).Returns(new FailingPageable<BlobItem>(failure));
        rig.Container.GetBlobsByHierarchyAsync(Arg.Any<BlobTraits>(), Arg.Any<BlobStates>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FailingPageable<BlobHierarchyItem>(failure));

        var reads = new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
        {
            ["state"] = () => rig.Store.GetStateAsync(Name, Ct),
            ["open"] = () => rig.Store.OpenReadAsync("File", Name, Ct),
            ["list"] = async () =>
            {
                await foreach (var _ in rig.Store.ListAsync("T/p/", Ct)) { }
            },
            ["list prefixes"] = async () =>
            {
                await foreach (var _ in rig.Store.ListPrefixesAsync("T/", Ct)) { }
            },
        };
        foreach (var (name, read) in reads)
        {
            var thrown = await Record.ExceptionAsync(read);

            var transient = Assert.IsType<TransientStorageException>(thrown);
            Assert.False(transient.MayHaveCommitted, name);
            Assert.Same(failure, transient.InnerException);
        }
    }

    [Fact]
    public async Task ABlobTimeout_IsTransient_ButTheCallersCancellationIsNot()
    {
        var rig = new BlobRig();
        rig.Blob.GetPropertiesAsync(Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("network timeout"));

        var timeout = await Assert.ThrowsAsync<TransientStorageException>(() => rig.Store.GetStateAsync(Name, Ct));
        Assert.IsType<TaskCanceledException>(timeout.InnerException);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Store.GetStateAsync(Name, cts.Token));
    }

    [Fact]
    public async Task ABlobThatIsGone_IsStillAnIntegrityFailureOrNoState_AsBefore()
    {
        var rig = new BlobRig();
        rig.Blob.GetPropertiesAsync(Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>()).ThrowsAsync(Status(404, "BlobNotFound"));
        rig.Blob.OpenReadAsync(Arg.Any<BlobOpenReadOptions>(), Arg.Any<CancellationToken>()).ThrowsAsync(Status(404, "BlobNotFound"));

        Assert.Null(await rig.Store.GetStateAsync(Name, Ct));
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => rig.Store.TouchAsync(Name, Ct));
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => rig.Store.OpenReadAsync("File", Name, Ct));
    }

    [Fact]
    public async Task ThroughTableStorage_AMissingBlobContainer_IsNeverAMissingRowOrTable()
    {
        // The real blob store under TableStorage: the upload of an offloaded value meets a 404.
        var rig = new BlobRig();
        var failure = Status(404, "ContainerNotFound");
        rig.Blob.GetPropertiesAsync(Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>()).ThrowsAsync(Status(404, "BlobNotFound"));
        rig.Blob.UploadAsync(Arg.Any<Stream>(), Arg.Any<BlobUploadOptions>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        using var h = new StorageHarness<FileEntity>(
            options: new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 } }, blobs: rig.Store);
        h.SetupGet(null);
        h.SetupAdd();

        var thrown = await Record.ExceptionAsync(() => h.Store.UpsertAsync(
            new FileEntity { Id = "p|r", File = TableBlobContent.FromBytes(new byte[5_000], "application/octet-stream") }, Ct));

        Assert.NotNull(thrown);
        Assert.Equal(typeof(StorageException), thrown.GetType());
        Assert.Same(failure, thrown.InnerException);
        Assert.Equal("ContainerNotFound", ((StorageException)thrown).ProviderErrorCode);
        await h.Table.DidNotReceiveWithAnyArgs().AddEntityAsync<TableEntity>(default!, default);
    }

    // ── the polymorphic store ──────────────────────────────────────────────

    private static readonly TableKey Key = new("p", "r");

    private static PolymorphicWrite<TestMessage> Write(string rowKey = "r") =>
        new(new TableKey("p", rowKey), new TestCreatedEvent { Id = "e" });

    /// <summary>Every member, with whether it may change stored data.</summary>
    public static TheoryData<string, bool> PolymorphicMembers => new()
    {
        { "ensure", true }, { "insert", true }, { "upsert", true }, { "insert batch", true }, { "merge", true },
        { "delete", true }, { "delete partition", true },
        { "get", false }, { "query", false }, { "stream", false }, { "count", false },
    };

    private static Task CallAsync(PolymorphicTableStorage<TestMessage> store, string member) => member switch
    {
        "ensure" => store.EnsureCreatedAsync(Ct),
        "insert" => store.InsertAsync(Write(), Ct),
        "upsert" => store.UpsertAsync(Write(), Ct),
        "insert batch" => store.InsertBatchAsync([Write("a"), Write("b")], Ct),
        "merge" => store.MergeColumnsAsync(Key, new Dictionary<string, object> { ["_IsPublished"] = true }, Ct),
        "delete" => store.DeleteAsync(Key, Ct),
        "delete partition" => store.DeletePartitionAsync("p", Ct),
        "get" => store.GetAsync(Key, Ct),
        "query" => store.QueryAsync("p", null, Ct),
        "stream" => DrainAsync(store.QueryStreamAsync("p", null, Ct)),
        _ => store.CountAsync("p", Ct),
    };

    private static async Task DrainAsync<T>(IAsyncEnumerable<T> items)
    {
        await foreach (var _ in items) { }
    }

    private static PolymorphicHarness<TestMessage> FailingEverything(Exception failure, string member)
    {
        var h = new PolymorphicHarness<TestMessage>();
        var t = h.Table;
        if (member == "ensure")
            t.CreateIfNotExistsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<Response<Azure.Data.Tables.Models.TableItem>>(failure));
        t.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>()).Returns<Response>(_ => throw failure);
        t.UpsertEntityAsync(Arg.Any<TableEntity>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>()).Returns<Response>(_ => throw failure);
        t.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>()).Returns<Response>(_ => throw failure);
        t.DeleteEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>()).Returns<Response>(_ => throw failure);
        t.GetEntityIfExistsAsync<TableEntity>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<NullableResponse<TableEntity>>(_ => throw failure);
        t.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw failure);
        if (member == "delete partition")
        {
            // The listing succeeds; the delete transaction is what fails.
            t.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(Mocks.Pageable(new TableEntity("p", "r") { ETag = new ETag("W/\"1\"") }));
        }
        else
        {
            t.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(new FailingPageable<TableEntity>(failure));
        }
        return h;
    }

    [Theory]
    [MemberData(nameof(PolymorphicMembers))]
    public async Task EveryPolymorphicMember_MapsAnOutage_AndOnlyAWriteMayHaveCommitted(string member, bool write)
    {
        var failure = new RequestFailedException(503, "busy", "ServerBusy", null);
        using var h = FailingEverything(failure, member);

        var thrown = await Assert.ThrowsAsync<TransientStorageException>(() => CallAsync(h.Store, member));

        Assert.Same(failure, thrown.InnerException);
        Assert.Equal(write, thrown.MayHaveCommitted);
        Assert.Equal("ServerBusy", thrown.ProviderErrorCode);
    }

    [Theory]
    [MemberData(nameof(PolymorphicMembers))]
    public async Task EveryPolymorphicMember_ReportsAMissingTable_AsTableNotFound(string member, bool write)
    {
        var failure = new RequestFailedException(404, "The table specified does not exist.", "TableNotFound", null);
        using var h = FailingEverything(failure, member);

        var thrown = await Record.ExceptionAsync(() => CallAsync(h.Store, member));

        // The delete too, since 0.10.0: only a missing ROW makes it a no-op (see the next test). Until then
        // it swallowed every 404, the table's among them.
        var table = Assert.IsType<TableNotFoundException>(thrown);
        Assert.Equal("TestTable", table.TableName);
        Assert.Same(failure, table.InnerException);
        _ = write; // the same type for a read and a write
    }

    [Theory]
    [InlineData("ResourceNotFound")]
    [InlineData("EntityNotFound")]
    [InlineData(null)]
    public async Task Delete_OfAMissingRow_IsStillANoOp_WhenTheClientThrowsIts404(string? code)
    {
        using var h = FailingEverything(new RequestFailedException(404, "The specified resource does not exist.", code, null), "delete");

        Assert.Null(await Record.ExceptionAsync(() => CallAsync(h.Store, "delete")));
    }

    [Fact]
    public async Task MergeColumns_OfAMissingRow_ThrowsEntityNotFound_WithTheServices404Inside()
    {
        // The contract 0.10.0 reverses: until then this was the documented raw RequestFailedException(404).
        var failure = new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null);
        using var h = FailingEverything(failure, "merge");

        var thrown = await Assert.ThrowsAsync<EntityNotFoundException>(() => CallAsync(h.Store, "merge"));

        Assert.Equal("p|r", thrown.Id);
        Assert.Equal("TestTable", thrown.EntityType);
        Assert.Equal("ResourceNotFound", thrown.ProviderErrorCode);
        Assert.Same(failure, thrown.InnerException);
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("insert batch")]
    public async Task APolymorphicInsert_OfAnExistingKey_IsADuplicateKey_ThatMayBeTheCallsOwnWrite(string member)
    {
        Exception failure = new RequestFailedException(409, "exists", "EntityAlreadyExists", null);
        if (member == "insert batch")
            failure = new TableTransactionFailedException((RequestFailedException)failure);
        using var h = FailingEverything(failure, member);

        var thrown = await Assert.ThrowsAsync<DuplicateKeyException>(() => CallAsync(h.Store, member));

        Assert.True(thrown.MayBeOwnWrite);
        Assert.Same(failure, thrown.InnerException);
        Assert.Equal("EntityAlreadyExists", thrown.ProviderErrorCode);
    }

    [Fact]
    public async Task APolymorphicStream_CancelledByTheCaller_IsNotMapped()
    {
        using var cts = new CancellationTokenSource();
        using var h = new PolymorphicHarness<TestMessage>();
        h.Table.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<AsyncPageable<TableEntity>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in h.Store.QueryStreamAsync("p").WithCancellation(cts.Token)) { }
        });
    }
}
