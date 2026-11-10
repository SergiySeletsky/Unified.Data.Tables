using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A read that races a concurrent replace or delete: the primary row is read first, its continuation
/// rows and blobs after, so a writer committing in between makes the parts or the blob belong to another
/// version. Such a read re-reads the primary and, when its version changed, retries the whole read; only an
/// unchanged version is a genuine integrity failure. Mixed-version data is never returned.
/// </summary>
public class ReadRetryTests
{
    private const string Pk = "p";
    private const string Rk = "r";
    private const string Id = "p|r";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<TableEntity> SplitRows(string body, string etag)
    {
        var entity = new SplitTextEntity { Id = Id, Body = body, Title = etag };
        var logical = TableEntitySerializer.ToTableEntity(entity, Pk, Rk, ValueWriteContext.Public, []);
        var plan = SplitRowLayout.Plan(logical);
        Assert.NotEmpty(plan.Parts);
        plan.Primary.ETag = new ETag(etag);
        var rows = new List<TableEntity> { plan.Primary };
        rows.AddRange(plan.Parts);
        return rows;
    }

    private static void PrimaryReads(StorageHarness<SplitTextEntity> h, params TableEntity?[] versions)
    {
        var responses = versions.Select(v => v is null ? Mocks.NotFound() : Mocks.Found(v)).ToArray();
        h.Table.GetEntityIfExistsAsync<TableEntity>(Pk, Rk, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(responses[0], responses[1..]);
    }

    private static void PartQueries(StorageHarness<SplitTextEntity> h, params TableEntity[] parts) =>
        h.Table.QueryAsync<TableEntity>(
                Arg.Is<string?>(f => f != null && f.Contains("_SplitOf eq", StringComparison.Ordinal)),
                Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable(parts));

    [Fact]
    public async Task SplitPrimaryV1_PartsV2_RetriesAndReturnsTheConsistentV2Value()
    {
        var v1 = SplitRows(new string('a', 600_000), "W/\"v1\"");
        var v2 = SplitRows(new string('b', 600_000), "W/\"v2\"");
        using var h = new StorageHarness<SplitTextEntity>();
        PrimaryReads(h, v1[0], v2[0]);
        PartQueries(h, [.. v2.Skip(1)]);

        var read = await h.Store.OneAsync(Id, Ct);

        Assert.Equal(new string('b', 600_000), read!.Body, StringComparer.Ordinal);
        Assert.Equal("W/\"v2\"", read.Title);
        Assert.Equal("W/\"v2\"", read.ETag);
    }

    [Fact]
    public async Task GenuineCorruption_WithAnUnchangedVersion_StillThrows()
    {
        var v1 = SplitRows(new string('a', 600_000), "W/\"v1\"");
        var chunk = v1[1].Keys.First(k => k.Contains("__c", StringComparison.Ordinal));
        v1[1][chunk] = "x" + ((string)v1[1][chunk])[1..];
        using var h = new StorageHarness<SplitTextEntity>();
        PrimaryReads(h, v1[0], v1[0], v1[0], v1[0]);
        PartQueries(h, [.. v1.Skip(1)]);

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => h.Store.OneAsync(Id, Ct));
        // One read, one re-read that found the same version: no further attempts.
        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>(Pk, Rk, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VersionKeepsChanging_GivesUpAfterThreeAttempts()
    {
        var v1 = SplitRows(new string('a', 600_000), "W/\"v1\"");
        var v2 = SplitRows(new string('b', 600_000), "W/\"v2\"");
        var v3 = SplitRows(new string('c', 600_000), "W/\"v3\"");
        var v4 = SplitRows(new string('d', 600_000), "W/\"v4\"");
        using var h = new StorageHarness<SplitTextEntity>();
        PrimaryReads(h, v1[0], v2[0], v3[0], v4[0]);
        PartQueries(h, [.. v4.Skip(1)]);

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => h.Store.OneAsync(Id, Ct));
        await h.Table.Received(3).GetEntityIfExistsAsync<TableEntity>(Pk, Rk, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueriedPrimaryV1_PartsV2_RetriesThroughAPointRead()
    {
        var v1 = SplitRows(new string('a', 600_000), "W/\"v1\"");
        var v2 = SplitRows(new string('b', 600_000), "W/\"v2\"");
        using var h = new StorageHarness<SplitTextEntity>();
        PrimaryReads(h, v2[0]);
        h.Table.QueryAsync<TableEntity>(
                Arg.Is<string?>(f => f != null && f.Contains("_SplitOf eq", StringComparison.Ordinal)),
                Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable([.. v2.Skip(1)]));
        h.Table.QueryAsync<TableEntity>(
                Arg.Is<string?>(f => f == null || !f.Contains("_SplitOf eq", StringComparison.Ordinal)),
                Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable(v1[0]));

        var items = await h.Store.QueryAsync(new QueryOptions { Partition = Pk }, Ct);

        Assert.Equal(new string('b', 600_000), Assert.Single(items).Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task DeletedBetweenPrimaryAndParts_ReadsAsAbsent()
    {
        var v1 = SplitRows(new string('a', 600_000), "W/\"v1\"");
        using var h = new StorageHarness<SplitTextEntity>();
        PrimaryReads(h, v1[0], null);
        PartQueries(h);

        Assert.Null(await h.Store.OneAsync(Id, Ct));
    }

    // ── blobs ──────────────────────────────────────────────────────────────

    private static readonly UnifiedTableStorageOptions Offload = new()
    {
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 },
        Cache = CachePolicy.Disabled,
    };

    private static async Task<TableEntity> OffloadedRow(InMemoryOffloadBlobStore blobs, object entity, string etag)
    {
        var coordinator = new ValueOffloadCoordinator(blobs, Offload.BlobOffload, "T");
        var (row, plan, pending) = coordinator.Serialize(entity, Pk, Rk, 0);
        Assert.Empty(plan.Parts);
        Assert.NotEmpty(pending);
        await coordinator.UploadAsync(pending, Ct);
        row.ETag = new ETag(etag);
        return row;
    }

    [Fact]
    public async Task BlobDeletedByAConcurrentReplace_RetriesAndReturnsTheNewValue()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var v1 = await OffloadedRow(blobs, new SplitTextEntity { Id = Id, Body = Ascii(40_000, seed: 1) }, "W/\"v1\"");
        var v2 = await OffloadedRow(blobs, new SplitTextEntity { Id = Id, Body = Ascii(40_000, seed: 2) }, "W/\"v2\"");
        // The replace deleted v1's blob after its commit.
        blobs.Remove(BlobReference.Parse("Body", (string)v1["Body__blob"]).Path);
        using var h = new StorageHarness<SplitTextEntity>(options: Offload, blobs: blobs);
        PrimaryReads(h, v1, v2);

        var read = await h.Store.OneAsync(Id, Ct);

        Assert.Equal(Ascii(40_000, seed: 2), read!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task MissingBlob_WithAnUnchangedVersion_StillThrows()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var v1 = await OffloadedRow(blobs, new SplitTextEntity { Id = Id, Body = Ascii(40_000, seed: 1) }, "W/\"v1\"");
        blobs.Remove(BlobReference.Parse("Body", (string)v1["Body__blob"]).Path);
        using var h = new StorageHarness<SplitTextEntity>(options: Offload, blobs: blobs);
        PrimaryReads(h, v1, v1, v1);

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => h.Store.OneAsync(Id, Ct));
    }

    [Fact]
    public async Task InMemory_BlobDeletedByAReplaceDuringTheRead_RetriesAndReturnsTheNewValue()
    {
        var store = new InMemoryStorage<SplitTextEntity>(options: new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 },
        });
        await store.CreateAsync(new SplitTextEntity { Id = Id, Body = Ascii(40_000, seed: 1) }, Ct);
        var v1Blob = Assert.Single(store.Blobs.Names);
        var replaced = false;
        store.Blobs.BeforeOpenRead = async (name, ct) =>
        {
            if (replaced || name != v1Blob) return;
            replaced = true;
            // A concurrent replace commits and deletes the blob the reader's snapshot references.
            await store.UpsertAsync(new SplitTextEntity { Id = Id, Body = Ascii(40_000, seed: 2) }, ct);
            store.Blobs.Remove(v1Blob);
        };

        var read = await store.OneAsync(Id, Ct);

        Assert.True(replaced);
        Assert.Equal(Ascii(40_000, seed: 2), read!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task LazyContent_WhoseBlobWasDeletedAfterTheRead_ThrowsAClearError()
    {
        // A cached entity can outlive its row: the lazy content then names a blob a replace deleted.
        var blobs = new InMemoryOffloadBlobStore();
        var content = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream");
        var v1 = await OffloadedRow(blobs, new FileEntity { Id = Id, File = content }, "W/\"v1\"");
        using var h = new StorageHarness<FileEntity>(options: Offload, blobs: blobs);
        h.SetupGet(Pk, Rk, v1);
        var read = await h.Store.OneAsync(Id, Ct);
        blobs.Remove(BlobReference.Parse("File", (string)v1["File__blob"]).Path);

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(() => read!.File!.OpenReadAsync(Ct));
        Assert.Contains("read the entity again", ex.Message, StringComparison.Ordinal);
    }
}
