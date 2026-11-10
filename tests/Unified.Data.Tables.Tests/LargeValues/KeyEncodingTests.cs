using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Keys of split-aware entities name their blobs, so they must be well-formed UTF-16: an unpaired
/// surrogate escapes to the same segment as U+FFFD, which would let two keys share a blob and make the
/// table-wide sweep read the wrong partition. Such keys are refused on every split-aware write; a row that
/// already carries one (written before the opt-in) holds no blob and never breaks a sweep or a delete.
/// </summary>
public class KeyEncodingTests
{
    private static readonly string BigText = Ascii(40_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UnifiedTableStorageOptions Options() => new()
    {
        IdNormalization = IdNormalization.AsWritten,
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000, CleanupGrace = TimeSpan.Zero },
    };

    private static async Task AgeAllAsync(InMemoryStorage<SplitTextEntity> store, TimeSpan age)
    {
        foreach (var name in store.OffloadedBlobNames())
        {
            await using var stream = await store.Blobs.OpenReadAsync("Body", name, CancellationToken.None);
            var bytes = await ReadAllAsync(stream);
            store.Blobs.Seed(name, bytes, Sha(bytes), DateTimeOffset.UtcNow - age);
        }
    }

    // Not theory data: the test framework serializes theory arguments as UTF-8, which turns an unpaired
    // surrogate into U+FFFD before the test sees it.
    private static readonly string[] UnpairedSurrogates = ["a\uD800", "\uDC00b", "a\uDC00\uD800"];

    private static readonly string[] UnsplittableIds = ["p\uD800|r", "p|r\uDC00", "p" + (char)0xFFFD + "|r\uD800"];

    [Fact]
    public void EscapeSegment_UnpairedSurrogate_Throws()
    {
        foreach (var value in UnpairedSurrogates)
        {
            Assert.False(SplitCodec.IsWellFormedUtf16(value));
            // Uri.EscapeDataString would write U+FFFD for it: the same segment as a real U+FFFD.
            Assert.Throws<ArgumentException>(() => OffloadBlobNames.EscapeSegment(value));
        }
    }

    [Fact]
    public void EscapeSegment_ReplacementCharacter_StaysInjective()
    {
        Assert.Equal("a%EF%BF%BD", OffloadBlobNames.EscapeSegment("a�"));
        Assert.True(OffloadBlobNames.TryUnescapeSegment("a%EF%BF%BD", out var value));
        Assert.Equal("a�", value);
        // An escaped lone surrogate is not UTF-8: no segment this library writes, and no throw.
        Assert.False(OffloadBlobNames.TryUnescapeSegment("a%ED%A0%80", out _));
    }

    [Fact]
    public async Task InMemory_Write_KeyWithAnUnpairedSurrogate_IsRefused()
    {
        foreach (var id in UnsplittableIds)
            await InMemoryWriteIsRefusedAsync(id);
    }

    private static async Task InMemoryWriteIsRefusedAsync(string id)
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options());

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(new SplitTextEntity { Id = id, Body = BigText }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(new SplitTextEntity { Id = id, Body = BigText }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertBatchAsync([new SplitTextEntity { Id = id, Body = BigText }], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateBatchAsync([new SplitTextEntity { Id = id, Body = BigText }], Ct));

        Assert.Equal(0, store.PhysicalRowCount);
        Assert.Empty(store.OffloadedBlobNames());
    }

    [Fact]
    public async Task Azure_Write_KeyWithAnUnpairedSurrogate_IsRefusedBeforeAnyCall()
    {
        foreach (var id in UnsplittableIds)
            await AzureWriteIsRefusedAsync(id);
    }

    private static async Task AzureWriteIsRefusedAsync(string id)
    {
        var log = new List<string>();
        using var h = new StorageHarness<SplitTextEntity>(options: Options(), blobs: new RecordingBlobStore(log));

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.CreateAsync(new SplitTextEntity { Id = id, Body = BigText }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.UpsertAsync(new SplitTextEntity { Id = id, Body = BigText }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.UpsertBatchAsync([new SplitTextEntity { Id = id, Body = BigText }], Ct));

        Assert.Empty(log);
        await h.Table.DidNotReceiveWithAnyArgs().GetEntityIfExistsAsync<TableEntity>(default!, default!, default, default);
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
        await h.Table.DidNotReceiveWithAnyArgs().AddEntityAsync<TableEntity>(default!, default);
    }

    [Fact]
    public async Task PlainType_KeyWithAnUnpairedSurrogate_IsUnchanged()
    {
        var store = new InMemoryStorage<PlainEntity>(null, Options());

        await store.CreateAsync(new PlainEntity { Id = "p\uD800|r", Body = "x" }, Ct);

        Assert.Equal("x", (await store.OneAsync("p\uD800|r", Ct))!.Body);
    }

    [Fact]
    public async Task InMemory_TableWideSweep_KeepsTheBlobOfAReplacementCharacterKey()
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options());
        await store.CreateAsync(new SplitTextEntity { Id = "t�|r", Body = BigText }, Ct);
        // A row from before the opt-in may carry a key no blob can be named after; it holds no blob.
        store.SeedPhysicalRow(new TableEntity("t�", "legacy\uD800") { ["Title"] = "old" });
        await AgeAllAsync(store, TimeSpan.FromHours(1));

        Assert.Equal(0, await store.SweepOrphanBlobsAsync(TimeSpan.Zero, Ct));
        Assert.Equal(0, await store.SweepOrphanBlobsAsync("t�", TimeSpan.Zero, Ct));
        Assert.Equal(0, await store.SweepOrphanBlobsAsync("t\uD800", TimeSpan.Zero, Ct));

        Assert.Single(store.OffloadedBlobNames());
        Assert.Equal(BigText, (await store.OneAsync("t�|r", Ct))!.Body);
    }

    [Fact]
    public async Task InMemory_DeletePartition_KeyWithAnUnpairedSurrogate_DeletesItsRows_AndNoBlob()
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options());
        await store.CreateAsync(new SplitTextEntity { Id = "t�|r", Body = BigText }, Ct);
        store.SeedPhysicalRow(new TableEntity("t\uD800", "r") { ["Title"] = "old" });
        await AgeAllAsync(store, TimeSpan.FromHours(1));

        Assert.Equal(1, await store.DeletePartitionAsync("t\uD800", Ct));

        Assert.Single(store.OffloadedBlobNames());
        Assert.Equal(BigText, (await store.OneAsync("t�|r", Ct))!.Body);
    }
}
