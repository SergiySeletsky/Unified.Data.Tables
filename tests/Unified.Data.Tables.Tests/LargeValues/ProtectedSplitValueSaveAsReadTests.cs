namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A protected value opted in with <c>[TableValueSplit]</c> that is stored as JSON (a list, a dictionary, an
/// object stored as one value) is compared by what it stores: a whole-entity write that carries the value it
/// was read with is not refused, on both stores, with the entity cache warm or cold. A read never hands back
/// the stored instance, so a comparison by reference refused every save of an entity as it was read (review
/// finding dyn-cache). A changed value is still refused without the role. No authorizer is registered: nobody
/// may change a protected value.
/// </summary>
public class ProtectedSplitValueSaveAsReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public sealed class Ledger : Entity
    {
        [ProtectedProperty("legal")]
        [TableValueSplit(SplitEncoding.Text)]
        public List<string>? Lines { get; set; }

        [ProtectedProperty("legal")]
        [TableValueSplit(SplitEncoding.Gzip)]
        public Dictionary<string, int>? Totals { get; set; }

        public string? Title { get; set; }
    }

    private static Ledger Sample(string id, int lines) => new()
    {
        Id = id,
        Title = "t",
        Lines = [.. Enumerable.Range(0, lines).Select(i => "line " + i)],
        Totals = new() { ["a"] = 1, ["b"] = 2 },
    };

    // A small list stays one JSON cell, a large one is split: both are saved back as read.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AnEntitySavedAsItWasRead_IsNotRefused_ForAProtectedSplitList(string kind)
    {
        foreach (var lines in new[] { 3, 8_000 })
        {
            await using var stores = await LargeValueStores.OpenAsync(kind);
            Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
            var id = stores.Id("r" + lines);
            var store = stores.Store<Ledger>();
            await store.CreateAsync(Sample(id, lines), Ct);

            // A cold read (another store, nothing cached), then the same store's warm one.
            foreach (var reader in new[] { stores.Store<Ledger>(), store })
            {
                var read = (await reader.OneAsync(id, Ct))!;
                read.Title = "edited " + lines;
                var update = await Record.ExceptionAsync(() => store.UpdateAsync(read, Ct));
                Assert.True(update is null, $"{lines} lines, UpdateAsync: {update?.GetType().Name}: {update?.Message}");

                var again = (await reader.OneAsync(id, Ct))!;
                var upsert = await Record.ExceptionAsync(() => store.UpsertAsync(again, Ct));
                Assert.True(upsert is null, $"{lines} lines, UpsertAsync: {upsert?.GetType().Name}: {upsert?.Message}");
            }
            Assert.Equal("edited " + lines, (await stores.Store<Ledger>().OneAsync(id, Ct))!.Title);
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AChangedProtectedSplitList_IsStillRefused(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        var store = stores.Store<Ledger>();
        await store.CreateAsync(Sample(id, 8_000), Ct);

        var read = (await store.OneAsync(id, Ct))!;
        read.Lines![10] = "changed";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpdateAsync(read, Ct));

        var other = (await store.OneAsync(id, Ct))!;
        other.Totals!["c"] = 3;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpsertAsync(other, Ct));

        var stored = (await stores.Store<Ledger>().OneAsync(id, Ct))!;
        Assert.Equal("line 10", stored.Lines![10]);
        Assert.False(stored.Totals!.ContainsKey("c"));
    }
}
