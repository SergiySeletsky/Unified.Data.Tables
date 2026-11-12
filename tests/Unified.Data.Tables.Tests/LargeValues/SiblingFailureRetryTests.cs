using Azure.Core;
using Azure.Core.Pipeline;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A write with several <see cref="TableBlobContent"/> values that fails, or is cancelled, after the blob
/// of one of them was uploaded: that content is bound to its blob, so a retry with the same entity reuses
/// the blob (no second upload, no orphan), also when its source was a non-seekable stream whose temporary
/// copy is gone by then. A content whose blob the failed write had not uploaded yet keeps the standing
/// design: a forward-only one gives its temporary copy back (no temporary file is left behind) and is
/// refused by every later write; from bytes or a seekable stream it is written again.
/// </summary>
public class SiblingFailureRetryTests
{
    private const int Size = 200_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemoryStorage<TwoBlobEntity> Store() =>
        new(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });

    private static TableBlobContent ForwardOnly(byte[] data) =>
        TableBlobContent.FromStream(new FaultyContentStream(data, seekable: false), "application/octet-stream");

    private static string BlobOf(string rk, string column, byte[] data) =>
        ValueOffloadCoordinator.BlobName(nameof(TwoBlobEntity), "p", rk, column, Sha(data));

    private static async Task AssertReadsAsync(TableBlobContent? content, byte[] expected)
    {
        Assert.NotNull(content);
        await using var stream = await content.OpenReadAsync(Ct);
        Assert.Equal(expected, await ReadAllAsync(stream));
    }

    // Records every upload's blob name and fails the first upload of a column's blob (once) with failure.
    private static List<string> RecordUploads(InMemoryStorage<TwoBlobEntity> store, string failColumn, Func<Exception> failure)
    {
        var uploads = new List<string>();
        var failed = false;
        store.Blobs.BeforeUpload = (name, _) =>
        {
            lock (uploads)
                uploads.Add(name);
            if (!failed && name.Contains("/" + failColumn + "/", StringComparison.Ordinal))
            {
                failed = true;
                throw failure();
            }
            return Task.CompletedTask;
        };
        return uploads;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SiblingUploadFails_RetryWithTheSameEntity_ReusesTheForwardOnlyContentsBlob(bool upsert)
    {
        var store = Store();
        var first = RandomBytes(Size, seed: 201);
        byte[] second = [1, 2, 3];
        var entity = new TwoBlobEntity { Id = "p|r", First = ForwardOnly(first), Second = TableBlobContent.FromBytes(second, "application/octet-stream") };
        var uploads = RecordUploads(store, "Second", () => new IOException("simulated blob store failure"));

        await Assert.ThrowsAsync<IOException>(() => upsert ? store.UpsertAsync(entity, Ct) : store.CreateAsync(entity, Ct));

        // The forward-only content streams the blob uploaded for it; its temporary copy is gone.
        Assert.Equal(BlobOf("r", "First", first), entity.First!.BoundPath);
        Assert.Null(entity.First.Source);
        Assert.False(entity.First.Abandoned);
        uploads.Clear();

        if (upsert) await store.UpsertAsync(entity, Ct);
        else await store.CreateAsync(entity, Ct);

        // Only the blob that failed was sent again.
        Assert.Equal([BlobOf("r", "Second", second)], uploads);
        Assert.Equal(2, store.Blobs.Names.Count);
        var read = await store.OneAsync("p|r", Ct);
        await AssertReadsAsync(read!.First, first);
        await AssertReadsAsync(read.Second, second);
    }

    [Fact]
    public async Task CancelledAfterTheForwardOnlyContentWasUploaded_RetryWithTheSameEntity_ReusesItsBlob()
    {
        var store = Store();
        var first = RandomBytes(Size, seed: 202);
        byte[] second = [4, 5, 6];
        var entity = new TwoBlobEntity { Id = "p|r", First = ForwardOnly(first), Second = TableBlobContent.FromBytes(second, "application/octet-stream") };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var uploads = RecordUploads(store, "Second", () =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpsertAsync(entity, cts.Token));

        Assert.Equal(BlobOf("r", "First", first), entity.First!.BoundPath);
        uploads.Clear();
        await store.UpsertAsync(entity, Ct);

        Assert.Equal([BlobOf("r", "Second", second)], uploads);
        Assert.Equal(2, store.Blobs.Names.Count);
        var read = await store.OneAsync("p|r", Ct);
        await AssertReadsAsync(read!.First, first);
        await AssertReadsAsync(read.Second, second);
    }

    [Fact]
    public async Task TheRowWriteFailsAfterEveryUpload_RetryReusesEveryBlob()
    {
        var store = Store();
        await store.CreateAsync(new TwoBlobEntity { Id = "p|r" }, Ct);
        var first = RandomBytes(Size, seed: 203);
        var second = RandomBytes(Size, seed: 204);
        var entity = new TwoBlobEntity { Id = "p|r", First = ForwardOnly(first), Second = ForwardOnly(second) };
        var uploads = RecordUploads(store, "None", () => new InvalidOperationException("not used"));

        // The row exists: the create fails on its row write, after both blobs were uploaded.
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(entity, Ct));
        Assert.Equal(2, uploads.Count);
        uploads.Clear();

        await store.UpsertAsync(entity, Ct);

        Assert.Empty(uploads);
        Assert.Equal(2, store.Blobs.Names.Count);
        var read = await store.OneAsync("p|r", Ct);
        await AssertReadsAsync(read!.First, first);
        await AssertReadsAsync(read.Second, second);
    }

    [Fact]
    public async Task TwoForwardOnlyContents_TheSecondUploadFails_TheFirstIsReusedAndTheSecondGivesItsCopyBack()
    {
        var store = Store();
        var first = RandomBytes(Size, seed: 205);
        var second = RandomBytes(Size, seed: 206);
        var entity = new TwoBlobEntity { Id = "p|r", First = ForwardOnly(first), Second = ForwardOnly(second) };
        var copies = new List<string>();
        var uploads = new List<string>();
        var failed = false;
        store.Blobs.BeforeUpload = (name, _) =>
        {
            uploads.Add(name);
            if (!failed && name.Contains("/Second/", StringComparison.Ordinal))
            {
                failed = true;
                // Both temporary copies exist while the write runs.
                copies.AddRange(new[] { entity.Second!.Source }.OfType<FileStream>().Select(f => f.Name));
                throw new IOException("simulated blob store failure");
            }
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<IOException>(() => store.UpsertAsync(entity, Ct));

        // The first is bound to its blob; the second (not uploaded) gave its temporary copy back.
        Assert.Equal(BlobOf("r", "First", first), entity.First!.BoundPath);
        Assert.Null(entity.First.Source);
        Assert.Single(copies);
        Assert.False(File.Exists(copies[0]));
        Assert.Null(entity.Second!.Source);
        Assert.True(entity.Second.Abandoned);
        uploads.Clear();

        // Written again as it is: the second is refused before anything is uploaded.
        AbandonedBlobContentTests.AssertAbandoned(await Record.ExceptionAsync(() => store.UpsertAsync(entity, Ct)), "Second");
        Assert.Empty(uploads);

        // Created again from its source: the first is not uploaded again.
        entity.Second = ForwardOnly(second);
        await store.UpsertAsync(entity, Ct);

        Assert.Equal([BlobOf("r", "Second", second)], uploads);
        Assert.Equal(2, store.Blobs.Names.Count);
        var read = await store.OneAsync("p|r", Ct);
        await AssertReadsAsync(read!.First, first);
        await AssertReadsAsync(read.Second, second);
    }

    [Fact]
    public async Task OneForwardOnlyContentOnBothProperties_TheSecondUploadFails_RetryStoresBoth()
    {
        var store = Store();
        var data = RandomBytes(Size, seed: 207);
        var shared = ForwardOnly(data);
        var entity = new TwoBlobEntity { Id = "p|r", First = shared, Second = shared };
        var uploads = RecordUploads(store, "Second", () => new IOException("simulated blob store failure"));

        await Assert.ThrowsAsync<IOException>(() => store.UpsertAsync(entity, Ct));

        Assert.Equal(BlobOf("r", "First", data), shared.BoundPath);
        uploads.Clear();
        await store.UpsertAsync(entity, Ct);

        Assert.Equal([BlobOf("r", "Second", data)], uploads);
        Assert.Equal(2, store.Blobs.Names.Count);
        var read = await store.OneAsync("p|r", Ct);
        await AssertReadsAsync(read!.First, data);
        await AssertReadsAsync(read.Second, data);
    }
}

/// <summary>The same retries through <see cref="TableStorage{T}"/> against Azurite, the upload failing in the Blobs SDK pipeline.</summary>
public sealed class AzuriteSiblingFailureRetryTests : AzuriteContentTestBase
{
    private const int Size = 200_000;

    private static TableBlobContent ForwardOnly(byte[] data) =>
        TableBlobContent.FromStream(new FaultyContentStream(data, seekable: false), "application/octet-stream");

    private async Task AssertStoredAsync(string rk, byte[] first, byte[] second)
    {
        var read = await Store<TwoBlobEntity>().OneAsync(Id(rk), Ct);
        Assert.NotNull(read?.First);
        Assert.NotNull(read.Second);
        await using (var stream = await read.First.OpenReadAsync(Ct))
            Assert.Equal(first, await ReadAllAsync(stream));
        await using (var stream = await read.Second.OpenReadAsync(Ct))
            Assert.Equal(second, await ReadAllAsync(stream));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SiblingUploadFails_RetryWithTheSameEntity_ReusesTheForwardOnlyContentsBlob(bool cancel)
    {
        Assert.SkipUnless(Available, SkipReason);
        var first = RandomBytes(Size, seed: 211);
        byte[] second = [1, 2, 3];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var puts = new ColumnPuts(nameof(TwoBlobEntity), "Second") { FailuresLeft = 1, Cancel = cancel ? cts : null };
        var store = Store<TwoBlobEntity>(blobs: BlobServiceWith(puts));
        var entity = new TwoBlobEntity { Id = Id("r"), First = ForwardOnly(first), Second = TableBlobContent.FromBytes(second, "application/octet-stream") };

        var ex = await Record.ExceptionAsync(() => store.UpsertAsync(entity, cts.Token));
        Assert.True(cancel ? ex is OperationCanceledException : ex is IOException, ex?.ToString());
        Assert.NotNull(entity.First!.BoundPath);
        Assert.Single(await BlobsAsync<TwoBlobEntity>());
        var before = puts.Uploads("First");

        await store.UpsertAsync(entity, Ct);

        // The first blob is not sent again, and no blob is left that the row does not reference.
        Assert.Equal(before, puts.Uploads("First"));
        Assert.Equal(2, (await BlobsAsync<TwoBlobEntity>()).Count);
        await AssertStoredAsync("r", first, second);
    }

    [Fact]
    public async Task TwoForwardOnlyContents_TheSecondUploadFails_TheFirstIsReused()
    {
        Assert.SkipUnless(Available, SkipReason);
        var first = RandomBytes(Size, seed: 212);
        var second = RandomBytes(Size, seed: 213);
        var puts = new ColumnPuts(nameof(TwoBlobEntity), "Second") { FailuresLeft = 1 };
        var store = Store<TwoBlobEntity>(blobs: BlobServiceWith(puts));
        var entity = new TwoBlobEntity { Id = Id("r"), First = ForwardOnly(first), Second = ForwardOnly(second) };

        await Assert.ThrowsAsync<IOException>(() => store.UpsertAsync(entity, Ct));
        Assert.NotNull(entity.First!.BoundPath);
        Assert.True(entity.Second!.Abandoned);
        var before = puts.Uploads("First");

        entity.Second = ForwardOnly(second);
        await store.UpsertAsync(entity, Ct);

        Assert.Equal(before, puts.Uploads("First"));
        Assert.Equal(2, (await BlobsAsync<TwoBlobEntity>()).Count);
        await AssertStoredAsync("r", first, second);
    }

    [Fact]
    public async Task TheRowWriteFailsAfterEveryUpload_RetryReusesEveryBlob()
    {
        Assert.SkipUnless(Available, SkipReason);
        var first = RandomBytes(Size, seed: 214);
        var second = RandomBytes(Size, seed: 215);
        var puts = new ColumnPuts(nameof(TwoBlobEntity), "None");
        var store = Store<TwoBlobEntity>(blobs: BlobServiceWith(puts));
        await store.CreateAsync(new TwoBlobEntity { Id = Id("r") }, Ct);
        var entity = new TwoBlobEntity { Id = Id("r"), First = ForwardOnly(first), Second = ForwardOnly(second) };

        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(entity, Ct));
        var before = (puts.Uploads("First"), puts.Uploads("Second"));

        await store.UpsertAsync(entity, Ct);

        Assert.Equal(before, (puts.Uploads("First"), puts.Uploads("Second")));
        Assert.Equal(2, (await BlobsAsync<TwoBlobEntity>()).Count);
        await AssertStoredAsync("r", first, second);
    }

    /// <summary>
    /// Counts the blob uploads (Put Blob or Put Block) of each column below a table, and fails (or cancels)
    /// the first ones of <c>failColumn</c>.
    /// </summary>
    private sealed class ColumnPuts(string table, string failColumn) : HttpPipelineSynchronousPolicy
    {
        private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);

        public int FailuresLeft { get; set; }

        public CancellationTokenSource? Cancel { get; init; }

        public int Uploads(string column)
        {
            lock (counts)
                return counts.GetValueOrDefault(column);
        }

        public override void OnSendingRequest(HttpMessage message)
        {
            var uri = message.Request.Uri;
            var path = uri.Path;
            var at = path.IndexOf("/" + table + "/", StringComparison.Ordinal);
            if (message.Request.Method != RequestMethod.Put || at < 0 || uri.Query.Contains("comp=metadata", StringComparison.Ordinal))
                return;
            // {table}/{pk}/{rk}/{column}/{sha}
            var segments = path[(at + 1)..].Split('/');
            if (segments.Length < 5)
                return;
            var column = segments[3];
            lock (counts)
                counts[column] = counts.GetValueOrDefault(column) + 1;
            if (column == failColumn && FailuresLeft > 0)
            {
                FailuresLeft--;
                if (Cancel is { } cts)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }
                throw new IOException("simulated blob upload failure");
            }
        }
    }
}
