using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A partition delete of a split-aware type that overlaps a write of one of the entities it listed: an
/// upsert that grows the entity (adds continuation rows the listing did not see) or shrinks it (deletes
/// continuation rows the listing did see) commits after the listing and before the transaction that
/// deletes the entity. After the delete returns no continuation row is left without its main row, the
/// call does not fail because a listed row is gone, and the rest of the partition is deleted. Against
/// the local Azurite through the real SDK, and in memory.
/// </summary>
public sealed class PartitionDeleteConcurrentWriteTests : AzuriteCleanupTestBase
{
    private static readonly string OnePart = new('a', 600_000);
    private static readonly string TwoParts = new('b', 1_000_000);

    private const string TableName = nameof(SplitTextEntity);

    private async Task<List<string>> RowKeysAsync() => (await RawRowsAsync(TableName)).ConvertAll(r => r.RowKey);

    // The bodies by name, so a case reads as its shape: no continuation row, one, or two.
    private static string Body(string shape) => shape switch
    {
        "one part" => OnePart,
        "two parts" => TwoParts,
        _ => "small",
    };

    public static TheoryData<string, string> Grows => new()
    {
        { "no part", "one part" },
        { "no part", "two parts" },
        { "one part", "two parts" },
    };

    public static TheoryData<string, string> Shrinks => new()
    {
        { "two parts", "no part" },
        { "two parts", "one part" },
        { "one part", "no part" },
    };

    [Theory]
    [MemberData(nameof(Grows))]
    public async Task Azurite_EntityGrownBeforeTheDeletesTransaction_LeavesNoContinuationRowBehind(string before, string after)
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        await writer.CreateAsync(new SplitTextEntity { Id = Id("g1"), Body = Body(before) }, Ct);
        await writer.CreateAsync(new SplitTextEntity { Id = Id("z"), Body = "small" }, Ct);

        var interleaved = new RowRequestInterleaving.BeforeTransaction(1,
            () => writer.UpsertAsync(new SplitTextEntity { Id = Id("g1"), Body = Body(after) }, CancellationToken.None));
        var deleter = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await deleter.EnsureCreatedAsync(Ct);

        var deleted = await deleter.DeletePartitionAsync(Pk, Ct);

        Assert.Equal(1, interleaved.Fired);
        Assert.Equal(2, deleted);
        Assert.Empty(await RowKeysAsync());
        Assert.Null(await SplitOnlyStore<SplitTextEntity>().OneAsync(Id("g1"), Ct));
    }

    [Theory]
    [MemberData(nameof(Shrinks))]
    public async Task Azurite_EntityShrunkBeforeTheDeletesTransaction_TheDeleteCompletes_AndThePartitionIsEmpty(string before, string after)
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        await writer.CreateAsync(new SplitTextEntity { Id = Id("g1"), Body = Body(before) }, Ct);
        await writer.CreateAsync(new SplitTextEntity { Id = Id("z"), Body = "small" }, Ct);

        var interleaved = new RowRequestInterleaving.BeforeTransaction(1,
            () => writer.UpsertAsync(new SplitTextEntity { Id = Id("g1"), Body = Body(after) }, CancellationToken.None));
        var deleter = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await deleter.EnsureCreatedAsync(Ct);

        var deleted = await deleter.DeletePartitionAsync(Pk, Ct);

        Assert.Equal(1, interleaved.Fired);
        Assert.Equal(2, deleted);
        Assert.Empty(await RowKeysAsync());
    }

    /// <summary>The entity whose write raced the delete shares a transaction with a hundred others: they all still go.</summary>
    [Fact]
    public async Task Azurite_EntityGrownInALaterTransaction_EveryOtherEntityIsStillDeleted()
    {
        SkipUnlessAvailable();
        var writer = SplitOnlyStore<SplitTextEntity>();
        await writer.CreateBatchAsync(Enumerable.Range(0, 100).Select(i => new SplitTextEntity { Id = Id($"a{i:D3}"), Body = "v" }).ToList(), Ct);
        await writer.CreateAsync(new SplitTextEntity { Id = Id("g1"), Body = "small" }, Ct);
        await writer.CreateBatchAsync(Enumerable.Range(0, 50).Select(i => new SplitTextEntity { Id = Id($"h{i:D3}"), Body = "v" }).ToList(), Ct);

        var interleaved = new RowRequestInterleaving.BeforeTransaction(2,
            () => writer.UpsertAsync(new SplitTextEntity { Id = Id("g1"), Body = OnePart }, CancellationToken.None));
        var deleter = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        await deleter.EnsureCreatedAsync(Ct);

        Assert.Equal(151, await deleter.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(1, interleaved.Fired);
        Assert.Empty(await RowKeysAsync());
    }

    // ── InMemoryStorage: the same guarantees ────────────────────────────────

    private static async Task<InMemoryStorage<SplitTextEntity>> InMemoryWithRacingUpsertAsync(string before, string after)
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        // A hundred one-row entities fill the delete's first transaction; g1 and z come in the second.
        await store.CreateBatchAsync(Enumerable.Range(0, 100).Select(i => new SplitTextEntity { Id = $"p|a{i:D3}", Body = "v" }).ToList(), Ct);
        await store.CreateAsync(new SplitTextEntity { Id = "p|g1", Body = Body(before) }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = "p|z", Body = "small" }, Ct);
        var fired = 0;
        store.AfterLargeCommit = () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
                store.UpsertAsync(new SplitTextEntity { Id = "p|g1", Body = Body(after) }, CancellationToken.None).GetAwaiter().GetResult();
        };
        return store;
    }

    [Theory]
    [MemberData(nameof(Grows))]
    public async Task InMemory_EntityGrownBeforeTheDeletesTransaction_LeavesNoContinuationRowBehind(string before, string after)
    {
        var store = await InMemoryWithRacingUpsertAsync(before, after);

        Assert.Equal(102, await store.DeletePartitionAsync("p", Ct));

        Assert.Empty(store.PhysicalRowKeys("p"));
        Assert.Null(await store.OneAsync("p|g1", Ct));
    }

    [Theory]
    [MemberData(nameof(Shrinks))]
    public async Task InMemory_EntityShrunkBeforeTheDeletesTransaction_TheDeleteCompletes_AndThePartitionIsEmpty(string before, string after)
    {
        var store = await InMemoryWithRacingUpsertAsync(before, after);

        Assert.Equal(102, await store.DeletePartitionAsync("p", Ct));

        Assert.Empty(store.PhysicalRowKeys("p"));
    }
}
