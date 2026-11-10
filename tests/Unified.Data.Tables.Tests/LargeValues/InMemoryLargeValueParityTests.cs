using Azure;
using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>A split-aware entity with one text and one binary value, so one theory covers both encodings.</summary>
public class ParityEntity : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    [TableValueSplit(SplitEncoding.Text)]
    public byte[]? Image { get; set; }

    public string? Title { get; set; }
}

/// <summary>
/// <see cref="InMemoryStorage{T}"/> stores split rows and offloaded blobs with the same tiers, ordering,
/// integrity checks and exceptions as <see cref="TableStorage{T}"/>. Cleanup after a shrink or a
/// delete only removes blobs older than <see cref="BlobOffloadOptions.CleanupGrace"/>, so the tests
/// that assert "no blob left" set the grace to zero (younger blobs are left for the orphan sweep).
/// </summary>
public class InMemoryLargeValueParityTests
{
    private const string Pk = "p";
    private const string Id = "p|r";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>(kind, size, offloaded): text sizes in chars, binary sizes in bytes, default threshold.</summary>
    public static TheoryData<string, int, bool> Sizes => new()
    {
        { "text", 100, false },
        { "text", 32_001, false },
        { "text", 600_000, false },
        // 2,000,000 chars are under the 3 MiB threshold, but the row and its parts exceed one transaction.
        { "text", 2_000_000, true },
        { "text", 3_200_000, true },
        { "bytes", 70_000, false },
        { "bytes", 3_145_729, true },
    };

    private static InMemoryStorage<T> Store<T>(TimeSpan? grace = null, bool offload = true) where T : class, IEntity, new()
    {
        var blobOffload = new BlobOffloadOptions();
        if (grace is { } g)
            blobOffload.CleanupGrace = g;
        return new InMemoryStorage<T>(null, new UnifiedTableStorageOptions { BlobOffload = offload ? blobOffload : null });
    }

    private static ParityEntity Make(string kind, int size, int seed = 7) => kind == "text"
        ? new ParityEntity { Id = Id, Title = "t", Body = Ascii(size, seed) }
        : new ParityEntity { Id = Id, Title = "t", Image = RandomBytes(size, seed) };

    private static void AssertSameValue(ParityEntity expected, ParityEntity? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Body, actual.Body);
        Assert.Equal(expected.Image, actual.Image);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task RoundTrip_Create_One_Query(string kind, int size, bool offloaded)
    {
        var store = Store<ParityEntity>();
        var entity = Make(kind, size);

        await store.CreateAsync(entity, Ct);

        AssertSameValue(entity, await store.OneAsync(Id, Ct));
        var queried = (await store.QueryAsync(Pk, Ct)).ToList();
        Assert.Single(queried);
        AssertSameValue(entity, queried[0]);
        Assert.Single(await store.QueryAsync(new QueryOptions { Partition = Pk }, Ct));
        Assert.Single((await store.QueryPageAsync(new QueryOptions { Partition = Pk }, Ct)).Items);
        Assert.Single(await store.QueryAsync(e => e.Title == "t", Pk, ct: Ct));
        Assert.Single(store.Snapshot());
        Assert.Equal(1, store.Count);
        Assert.Equal(1, await store.CountAsync(Pk, Ct));
        Assert.True(await store.ExistsAsync(Id, Ct));
        Assert.Equal(offloaded ? 1 : 0, store.OffloadedBlobNames().Count);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task Upsert_Shrink_LeavesNoPartsOrBlobs(string kind, int size, bool offloaded)
    {
        _ = offloaded;
        var store = Store<ParityEntity>(grace: TimeSpan.Zero);
        await store.CreateAsync(Make(kind, size), Ct);

        var small = Make(kind, 10, seed: 3);
        await store.UpsertAsync(small, Ct);

        Assert.Equal(1, store.PhysicalRowCount);
        Assert.Empty(store.OffloadedBlobNames());
        AssertSameValue(small, await store.OneAsync(Id, Ct));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task ConcurrentReplace_OneWins_ValueIntact(string kind, int size, bool offloaded)
    {
        _ = offloaded;
        var store = Store<ParityEntity>();
        await store.CreateAsync(Make(kind, size, seed: 1), Ct);
        var a = await store.OneAsync(Id, Ct);
        var b = await store.OneAsync(Id, Ct);
        Assert.Equal(a!.ETag, b!.ETag);
        var first = Make(kind, size, seed: 2);
        var second = Make(kind, size, seed: 3);
        (a.Body, a.Image) = (first.Body, first.Image);
        (b.Body, b.Image) = (second.Body, second.Image);

        var attempts = new[] { a, b }.Select(e => Task.Run(async () =>
        {
            try
            {
                await store.UpdateAsync(e, Ct);
                return e;
            }
            catch (ConcurrencyConflictException)
            {
                return null;
            }
        }, Ct)).ToArray();
        var outcomes = await Task.WhenAll(attempts);

        var winner = Assert.Single(outcomes, o => o is not null);
        Assert.Single(outcomes, o => o is null);
        AssertSameValue(winner!, await store.OneAsync(Id, Ct));
        Assert.Equal(1, store.Count);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task Delete_RemovesRowPartsAndBlobs(string kind, int size, bool offloaded)
    {
        _ = offloaded;
        var store = Store<ParityEntity>(grace: TimeSpan.Zero);
        await store.CreateAsync(Make(kind, size), Ct);

        await store.DeleteAsync(Id, Ct);

        Assert.Null(await store.OneAsync(Id, Ct));
        Assert.Equal(0, store.PhysicalRowCount);
        Assert.Empty(store.OffloadedBlobNames());
    }

    [Fact]
    public async Task Replace_AcrossTiers_KeepsOnlyWhatTheNewRowNeeds()
    {
        var store = Store<ParityEntity>(grace: TimeSpan.Zero);
        await store.CreateAsync(Make("text", 600_000), Ct);
        var split = store.PhysicalRowCount;
        Assert.True(split > 1);

        // Split -> blob: the continuation rows go in the same step, one blob appears.
        var blob = await store.OneAsync(Id, Ct);
        blob!.Body = Ascii(3_200_000, seed: 5);
        await store.UpdateAsync(blob, Ct);
        Assert.Equal(1, store.PhysicalRowCount);
        Assert.Single(store.OffloadedBlobNames());

        // Blob -> split: the blob is released after the swap, the continuation rows come back.
        var back = await store.OneAsync(Id, Ct);
        var expected = Ascii(600_000, seed: 6);
        back!.Body = expected;
        await store.UpdateAsync(back, Ct);
        Assert.Equal(split, store.PhysicalRowCount);
        Assert.Empty(store.OffloadedBlobNames());
        Assert.Equal(expected, (await store.OneAsync(Id, Ct))!.Body);
    }

    [Fact]
    public async Task Delete_WithinGrace_KeepsTheBlobForTheSweep()
    {
        var store = Store<ParityEntity>();
        await store.CreateAsync(Make("text", 3_200_000), Ct);

        await store.DeleteAsync(Id, Ct);

        Assert.Equal(0, store.PhysicalRowCount);
        Assert.Single(store.OffloadedBlobNames());
    }

    [Fact]
    public async Task TableBlobContent_RoundTrip_IsLazy()
    {
        const int size = 10 * 1024 * 1024;
        var store = Store<FileEntity>();
        await store.CreateAsync(new FileEntity
        {
            Id = Id,
            Title = "f",
            File = TableBlobContent.FromStream(new GeneratedStream(size, seekable: true), "application/octet-stream"),
        }, Ct);
        var name = Assert.Single(store.OffloadedBlobNames());

        // Corrupt the blob: reading the entity must still succeed, because it does not download the content.
        var expected = await ReadAllAsync(new GeneratedStream(size, seekable: true));
        var read = await store.OneAsync(Id, Ct);
        Assert.NotNull(read?.File);
        Assert.Equal(size, read.File.Length);
        await using (var stream = await read.File.OpenReadAsync(Ct))
            Assert.Equal(expected, await ReadAllAsync(stream));

        store.Blobs.Tamper(name, RandomBytes(size));
        var lazy = await store.OneAsync(Id, Ct);
        Assert.Equal("f", lazy!.Title);
        await using var tampered = await lazy.File!.OpenReadAsync(Ct);
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadAllAsync(tampered));
    }

    [Fact]
    public async Task TamperedBlob_ThrowsIntegrity()
    {
        var store = Store<ParityEntity>();
        var entity = Make("text", 3_200_000);
        await store.CreateAsync(entity, Ct);
        var name = Assert.Single(store.OffloadedBlobNames());

        store.Blobs.Tamper(name, Utf8(Ascii(3_200_000, seed: 99)));

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => store.OneAsync(Id, Ct));
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => store.QueryAsync(Pk, Ct));
    }

    [Fact]
    public async Task RemovedBlob_ThrowsIntegrity()
    {
        var store = Store<ParityEntity>();
        await store.CreateAsync(Make("bytes", 3_145_729), Ct);
        var name = Assert.Single(store.OffloadedBlobNames());

        store.Blobs.Remove(name);

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => store.OneAsync(Id, Ct));
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => store.QueryAsync(new QueryOptions { Partition = Pk }, Ct));
    }

    [Fact]
    public async Task NoOffloadConfigured_Throws_LikeTableStorage()
    {
        var store = Store<ParityEntity>(offload: false);

        await Assert.ThrowsAsync<OversizedValueException>(() => store.CreateAsync(Make("text", 3_200_000), Ct));

        Assert.Equal(0, store.PhysicalRowCount);
        // A split value that fits one transaction needs no blob store.
        var split = Make("text", 600_000);
        await store.CreateAsync(split, Ct);
        AssertSameValue(split, await store.OneAsync(Id, Ct));
        Assert.True(store.PhysicalRowCount > 1);
    }

    [Fact]
    public async Task Exceptions_MatchTableStorage()
    {
        var store = Store<ParityEntity>();
        await store.CreateAsync(Make("text", 600_000), Ct);

        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(Make("text", 600_000), Ct));
        var missing = Make("text", 600_000);
        missing.Id = "p|other";
        var notFound = await Assert.ThrowsAsync<RequestFailedException>(() => store.UpdateAsync(missing, ConcurrencyMode.LastWriterWins, Ct));
        Assert.Equal(404, notFound.Status);

        var reserved = Make("text", 10);
        reserved.Id = "p|r~0001";
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(reserved, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(reserved, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync("p|r~0001", Ct));
        // A continuation row is not an entity: addressing it by id finds nothing.
        Assert.Null(await store.OneAsync("p|r~0001", Ct));
        Assert.False(await store.ExistsAsync("p|r~0001", Ct));
    }

    [Fact]
    public async Task Counts_AreLogicalRows()
    {
        var store = Store<ParityEntity>(grace: TimeSpan.Zero);
        await store.CreateAsync(Make("text", 600_000), Ct);
        var second = Make("text", 3_200_000);
        second.Id = "p|s";
        await store.CreateAsync(second, Ct);

        Assert.True(store.PhysicalRowCount > 2);
        Assert.Equal(2, store.Count);
        Assert.Equal(2, await store.CountAsync(ct: Ct));
        Assert.Equal(2, store.Snapshot().Count);

        Assert.Equal(2, await store.DeletePartitionAsync(Pk, Ct));
        Assert.Equal(0, store.PhysicalRowCount);
        Assert.Empty(store.OffloadedBlobNames());
    }

    [Fact]
    public async Task MissingPart_ThrowsIntegrity()
    {
        var store = Store<ParityEntity>();
        await store.CreateAsync(Make("text", 600_000), Ct);
        var physical = store.PhysicalRowCount;
        Assert.True(physical > 1);

        // Overwrite the first continuation row with one that no longer belongs to the primary.
        store.SeedPhysicalRow(new TableEntity(Pk, "r~0001"));

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => store.OneAsync(Id, Ct));
    }

    [Fact]
    public async Task TableNameResolver_NamesTheBlobs()
    {
        var store = new InMemoryStorage<ParityEntity>(null, new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions(),
            TableNameResolver = _ => "Custom",
        });

        await store.CreateAsync(Make("text", 3_200_000), Ct);

        Assert.StartsWith("Custom/p/r/Body/", Assert.Single(store.OffloadedBlobNames()), StringComparison.Ordinal);
    }

    // ── review fix round 1 ─────────────────────────────────────────────────

    [Fact]
    public async Task PartialUpdate_PartRowId_IsRefused_PrimaryIntact()
    {
        var store = Store<ParityEntity>();
        var entity = Make("text", 600_000);
        await store.CreateAsync(entity, Ct);
        var physical = store.PhysicalRowCount;
        Assert.True(physical > 1);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UpdateAsync("p|r~0001", b => b.SetProperty(x => x.Title, "injected"), Ct));

        Assert.Equal(physical, store.PhysicalRowCount);
        AssertSameValue(entity, await store.OneAsync(Id, Ct));

        // A plain type has no continuation rows: its '~' ids are ordinary and stay updatable.
        var plain = Store<PlainEntity>();
        await plain.CreateAsync(new PlainEntity { Id = "p|r~0001", Body = "a" }, Ct);
        await plain.UpdateAsync("p|r~0001", b => b.SetProperty(x => x.Body, "b"), Ct);
        Assert.Equal("b", (await plain.OneAsync("p|r~0001", Ct))!.Body);
    }

    [Fact]
    public async Task StrayCellOnAPartRow_NeverSurfacesOnThePrimary()
    {
        var store = Store<ParityEntity>();
        var entity = Make("text", 600_000);
        await store.CreateAsync(entity, Ct);
        var part = store.PhysicalRow(Pk, "r~0001");
        Assert.NotNull(part);
        part["Title"] = "injected";
        store.SeedPhysicalRow(part);

        AssertSameValue(entity, await store.OneAsync(Id, Ct));
    }

    [Fact]
    public async Task StrictReplace_LosesToAWriteCommittedDuringItsUpload_ThrowsConflict()
    {
        var store = Store<ParityEntity>();
        await store.CreateAsync(Make("text", 10), Ct);
        var read = await store.OneAsync(Id, Ct);

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Blobs.BeforeUpload = async (_, _) =>
        {
            store.Blobs.BeforeUpload = null; // pause the first upload only
            paused.SetResult();
            await release.Task;
        };

        var loser = Make("text", 3_200_000, seed: 2);
        loser.ETag = read!.ETag;
        var slow = Task.Run(() => store.UpdateAsync(loser, ConcurrencyMode.Strict, Ct), Ct);
        await paused.Task;

        // A competing replace of the same version commits while the first write is uploading.
        var winner = Make("text", 20, seed: 3);
        winner.ETag = read.ETag;
        await store.UpdateAsync(winner, ConcurrencyMode.Strict, Ct);
        release.SetResult();

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => slow);
        AssertSameValue(winner, await store.OneAsync(Id, Ct));
        // The loser's blob was uploaded before its commit failed: it stays for the orphan sweep.
        Assert.Single(store.OffloadedBlobNames());
    }

    [Fact]
    public async Task Upsert_LosingTheCommitCheck_RetriesAndWins()
    {
        var store = Store<ParityEntity>();
        await store.CreateAsync(Make("text", 10), Ct);

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Blobs.BeforeUpload = async (_, _) =>
        {
            store.Blobs.BeforeUpload = null;
            paused.SetResult();
            await release.Task;
        };

        var last = Make("text", 3_200_000, seed: 4);
        var slow = Task.Run(() => store.UpsertAsync(last, Ct), Ct);
        await paused.Task;
        await store.UpsertAsync(Make("text", 20, seed: 5), Ct);
        release.SetResult();
        await slow;

        // The re-read saw the competing version, re-planned and committed on its second attempt.
        AssertSameValue(last, await store.OneAsync(Id, Ct));
        Assert.Equal(1, store.PhysicalRowCount);
    }

    [Fact]
    public async Task FailedWrite_WithOffloadedValue_LeavesItsUploadedBlob()
    {
        var store = Store<ParityEntity>(grace: TimeSpan.Zero);
        await store.CreateAsync(Make("text", 10), Ct);

        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(Make("text", 3_200_000), Ct));

        // The upload precedes the entity write; a failed write never deletes a blob (the sweep does).
        Assert.Single(store.OffloadedBlobNames());
        Assert.Equal("t", (await store.OneAsync(Id, Ct))!.Title);
        Assert.Equal(10, (await store.OneAsync(Id, Ct))!.Body!.Length);
    }

    [Fact]
    public async Task PlainType_IsUnchanged()
    {
        var store = Store<PlainEntity>();
        await store.CreateAsync(new PlainEntity { Id = Id, Body = "x" }, Ct);

        Assert.Equal(1, store.PhysicalRowCount);
        Assert.Empty(store.OffloadedBlobNames());
        Assert.Equal("x", (await store.OneAsync(Id, Ct))!.Body);
    }
}
