namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A batch of a type with large values that is refused for a row key of the continuation-row shape
/// (<c>{key}~0001</c>) is refused before any entity of it is stamped: the caller's entities keep their
/// ETag and times, on both stores (ROWS-4d of the fix-wave-2 review). A plain type's batch has no such
/// key check and stamps as 0.8.3 does.
/// </summary>
public sealed class RefusedBatchLeavesEntitiesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, bool> StoresAndKinds()
    {
        var data = new TheoryData<string, bool>();
        foreach (var store in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        {
            data.Add(store, false);
            data.Add(store, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(StoresAndKinds))]
    public async Task ABatchRefusedForAReservedKey_LeavesTheCallersEntitiesAsTheyWere(string store, bool upsert)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, offload: false);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var created = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var good = new SplitTextEntity { Id = stores!.Id("r"), Body = "small", ETag = "W/\"mine\"", CreatedAt = created, UpdatedAt = created };
        var bad = new SplitTextEntity { Id = stores.Id("x~0001"), Body = "small", ETag = "W/\"also mine\"", CreatedAt = created, UpdatedAt = created };
        var target = stores.Store<SplitTextEntity>();

        await Assert.ThrowsAsync<ArgumentException>(() => upsert
            ? target.UpsertBatchAsync([good, bad], Ct)
            : target.CreateBatchAsync([good, bad], Ct));

        Assert.Equal("W/\"mine\"", good.ETag);
        Assert.Equal(created, good.CreatedAt);
        Assert.Equal(created, good.UpdatedAt);
        Assert.Equal("W/\"also mine\"", bad.ETag);
        Assert.Equal(created, bad.UpdatedAt);
        Assert.Empty(await stores.RawRowsAsync<SplitTextEntity>());
    }
}
