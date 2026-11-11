using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// An instance of a subclass written through a store of its base type (<c>IStorage&lt;Report&gt;</c> given a
/// <c>DetailedReport</c>). A read through that store creates the base type, so it can never return a value
/// the subclass stored outside the cell of its property. Review finding SERIALIZER-3 of fix wave 2, with
/// ruling R4 of fix wave 3:
/// <list type="bullet">
/// <item>A base type that is not large-value aware takes the plain path, which never stores a large-value
/// form: a value in a member the subclass opts in (<c>[TableValueSplit]</c>, <c>TableBlobContent</c>) is
/// refused with <see cref="NotSupportedException"/> before anything is stored, whatever its size, on every
/// write path of both stores. The message names the runtime type, the store's type and the remedy.</item>
/// <item>A base type that is large-value aware refuses a value of a column only the subclass declares
/// whenever the value would be stored outside its cell (split cells, continuation rows, a blob reference,
/// every <c>TableBlobContent</c>); a value of one cell is stored as 0.8.3 stores it.</item>
/// <item>A subclass that opts into nothing, or whose opted-in members hold nothing, is written as on
/// 0.8.3.</item>
/// </list>
/// </summary>
public class SubclassThroughBaseTypedStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public static TheoryData<string, int> StoresAndSizes()
    {
        var data = new TheoryData<string, int>();
        foreach (var store in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        foreach (var length in new[] { 0, 10, 40_000, 700_000 })
            data.Add(store, length); // 0: a file instead of a text value
        return data;
    }

    // ── models ─────────────────────────────────────────────────────────────

    /// <summary>Opts into nothing.</summary>
    public class Report : Entity
    {
        public string? Title { get; set; }
    }

    public sealed class DetailedReport : Report
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }
    }

    public sealed class ScannedReport : Report
    {
        public TableBlobContent? Scan { get; set; }
    }

    /// <summary>Opts into nothing either.</summary>
    public sealed class AnnotatedReport : Report
    {
        public string? Note { get; set; }
    }

    /// <summary>Large-value aware itself.</summary>
    public class Article : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Summary { get; set; }

        public string? Title { get; set; }
    }

    public sealed class LongArticle : Article
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }

        public TableBlobContent? Scan { get; set; }
    }

    private static Report Subclass(string id, int length) => length == 0
        ? new ScannedReport { Id = id, Title = "t", Scan = TableBlobContent.FromBytes(RandomBytes(5_000), "application/pdf") }
        : new DetailedReport { Id = id, Title = "t", Body = Ascii(length, seed: length) };

    private static void AssertSubclassRefusal(Exception? refused, string runtimeType, string storeType, string column)
    {
        var ex = Assert.IsType<NotSupportedException>(refused);
        Assert.Contains($"'{column}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(runtimeType, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"IStorage<{storeType}>", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"IStorage<{runtimeType}>", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not configured", ex.Message, StringComparison.Ordinal);
    }

    // ── a base type that opts into nothing ─────────────────────────────────

    [Theory]
    [MemberData(nameof(StoresAndSizes))]
    public async Task PlainBaseStore_RefusesAValueInAMemberTheSubclassOptsIn_AtEverySize_InEveryWrite_BeforeAnythingIsStored(string kind, int length)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<Report>();
        var stored = await store.CreateAsync(new Report { Id = stores.Id("kept"), Title = "kept" }, Ct);
        var before = await stores.RawRowsAsync<Report>();
        var runtimeType = length == 0 ? nameof(ScannedReport) : nameof(DetailedReport);
        var column = length == 0 ? nameof(ScannedReport.Scan) : nameof(DetailedReport.Body);

        var writes = new (string Name, Func<Task> Write)[]
        {
            ("CreateAsync", () => store.CreateAsync(Subclass(stores.Id("r"), length), Ct)),
            ("UpsertAsync", () => store.UpsertAsync(Subclass(stores.Id("r"), length), Ct)),
            ("UpdateAsync", () =>
            {
                var replacement = Subclass(stored.Id, length);
                replacement.ETag = stored.ETag;
                return store.UpdateAsync(replacement, Ct);
            }),
            ("UpdateAsync (last writer wins)", () => store.UpdateAsync(Subclass(stored.Id, length), ConcurrencyMode.LastWriterWins, Ct)),
            ("UpdateAsync (strict)", () =>
            {
                var replacement = Subclass(stored.Id, length);
                replacement.ETag = stored.ETag;
                return store.UpdateAsync(replacement, ConcurrencyMode.Strict, Ct);
            }),
            ("UpsertAsync over a row", () => store.UpsertAsync(Subclass(stored.Id, length), Ct)),
            ("CreateBatchAsync", () => store.CreateBatchAsync([new Report { Id = stores.Id("b0") }, Subclass(stores.Id("b1"), length)], Ct)),
            ("UpsertBatchAsync", () => store.UpsertBatchAsync([new Report { Id = stores.Id("b0") }, Subclass(stores.Id("b1"), length)], Ct)),
        };
        foreach (var (name, write) in writes)
        {
            Exception? refused = null;
            var sent = await stores.StorageWritesAsync(store, async () => refused = await Record.ExceptionAsync(write));
            Assert.True(refused is NotSupportedException, $"{name}: {refused?.GetType().Name ?? "stored"} {refused?.Message}");
            Assert.True(sent.Count == 0, $"{name} sent: {string.Join(", ", sent)}");
            AssertSubclassRefusal(refused, runtimeType, nameof(Report), column);
        }

        var after = await stores.RawRowsAsync<Report>();
        Assert.Equal(before.Select(r => r.RowKey), after.Select(r => r.RowKey));
        Assert.Equal("kept", after[0]["Title"]);
        Assert.DoesNotContain(after[0].Keys, k => k.StartsWith(column, StringComparison.Ordinal));
        Assert.Empty(await stores.BlobNamesAsync<Report>());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task PlainBaseStore_WritesASubclassWhoseOptedInMembersHoldNothing_AsOn083(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<Report>();

        await store.CreateAsync(new DetailedReport { Id = stores.Id("a"), Title = "a" }, Ct);
        await store.UpsertAsync(new ScannedReport { Id = stores.Id("b"), Title = "b" }, Ct);
        await store.CreateBatchAsync([new DetailedReport { Id = stores.Id("c"), Title = "c" }], Ct);

        var rows = await stores.RawRowsAsync<Report>();
        Assert.Equal(["a", "b", "c"], rows.Select(r => r.RowKey));
        Assert.All(rows, r => Assert.Equal(r.RowKey, r["Title"]));
        Assert.All(rows, r => Assert.DoesNotContain(r.Keys, k => k.StartsWith("Body", StringComparison.Ordinal) || k.StartsWith("Scan", StringComparison.Ordinal)));
        Assert.Equal("b", (await store.OneAsync(stores.Id("b"), Ct))!.Title);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task PlainBaseStore_WritesASubclassThatOptsIntoNothing_AsOn083(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<Report>();

        await store.CreateAsync(new AnnotatedReport { Id = stores.Id("a"), Title = "a", Note = "n" }, Ct);
        await store.UpsertBatchAsync([new AnnotatedReport { Id = stores.Id("b"), Title = "b", Note = "m" }], Ct);

        var rows = await stores.RawRowsAsync<Report>();
        Assert.Equal(["n", "m"], rows.Select(r => (string)r["Note"]));
        var read = await store.OneAsync(stores.Id("a"), Ct);
        Assert.Equal("a", read!.Title);
        Assert.IsType<Report>(read);
    }

    [Fact]
    public void PlainSerializer_OfASubclassThatOptsIn_KeepsItsLargeValueForm_AsTheRuntimeTypeIsAware()
    {
        // The public serializer knows no store: it serializes the object as its runtime type, which is aware.
        var row = new DetailedReport { Id = "p|r", Title = "t", Body = Ascii(40_000) }.ToTableEntity("p", "r");

        Assert.Contains("Body__chunks", row.Keys);
    }

    // ── a base type that is large-value aware itself ───────────────────────

    [Theory]
    [InlineData(LargeValueStores.Memory, 40_000)]
    [InlineData(LargeValueStores.Memory, 3_500_000)]
    [InlineData(LargeValueStores.Memory, 0)]
    [InlineData(LargeValueStores.Azurite, 40_000)]
    [InlineData(LargeValueStores.Azurite, 3_500_000)]
    [InlineData(LargeValueStores.Azurite, 0)]
    public async Task AwareBaseStore_RefusesAValueOfAColumnOnlyTheSubclassDeclares_ThatWouldLeaveItsCell(string kind, int length)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind, cleanupGrace: TimeSpan.Zero);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<Article>();
        var stored = await store.CreateAsync(new Article { Id = stores.Id("kept"), Title = "kept", Summary = "s" }, Ct);
        var before = await stores.RawRowsAsync<Article>();
        LongArticle Make(string id) => length == 0
            ? new LongArticle { Id = id, Title = "t", Summary = "s", Scan = TableBlobContent.FromBytes(RandomBytes(5_000), "application/pdf") }
            : new LongArticle { Id = id, Title = "t", Summary = "s", Body = Ascii(length, seed: 2) };
        var column = length == 0 ? nameof(LongArticle.Scan) : nameof(LongArticle.Body);

        var writes = new (string Name, Func<Task> Write)[]
        {
            ("CreateAsync", () => store.CreateAsync(Make(stores.Id("r")), Ct)),
            ("UpsertAsync", () => store.UpsertAsync(Make(stores.Id("r")), Ct)),
            ("UpdateAsync", () =>
            {
                var replacement = Make(stored.Id);
                replacement.ETag = stored.ETag;
                return store.UpdateAsync(replacement, Ct);
            }),
            ("UpdateAsync (last writer wins)", () => store.UpdateAsync(Make(stored.Id), ConcurrencyMode.LastWriterWins, Ct)),
            ("UpsertAsync over a row", () => store.UpsertAsync(Make(stored.Id), Ct)),
            ("CreateBatchAsync", () => store.CreateBatchAsync([new Article { Id = stores.Id("b0") }, Make(stores.Id("b1"))], Ct)),
            ("UpsertBatchAsync", () => store.UpsertBatchAsync([new Article { Id = stores.Id("b0") }, Make(stores.Id("b1"))], Ct)),
        };
        foreach (var (name, write) in writes)
        {
            Exception? refused = null;
            var sent = await stores.StorageWritesAsync(store, async () => refused = await Record.ExceptionAsync(write));
            Assert.True(refused is NotSupportedException, $"{name}: {refused?.GetType().Name ?? "stored"} {refused?.Message}");
            Assert.True(sent.Count == 0, $"{name} sent: {string.Join(", ", sent)}");
            AssertSubclassRefusal(refused, nameof(LongArticle), nameof(Article), column);
        }

        var after = await stores.RawRowsAsync<Article>();
        Assert.Equal(before.Select(r => r.RowKey), after.Select(r => r.RowKey));
        Assert.Equal("kept", after[0]["Title"]);
        Assert.Empty(await stores.BlobNamesAsync<Article>());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AwareBaseStore_StoresAOneCellValueOfASubclassColumn_AsAnOrdinaryCell_AsOn083(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<Article>();
        var id = stores.Id("r");

        await store.CreateAsync(new LongArticle { Id = id, Title = "t", Summary = Ascii(40_000), Body = "short" }, Ct);

        var row = Assert.Single(await stores.RawRowsAsync<Article>());
        Assert.Equal("short", row["Body"]);
        Assert.DoesNotContain(row.Keys, k => k.StartsWith("Body__", StringComparison.Ordinal));
        var read = await store.OneAsync(id, Ct);
        Assert.Equal(Ascii(40_000), read!.Summary);
        Assert.IsType<Article>(read);
    }

    // ── the reproduction of the review (SERIALIZER-3), with its offloaded scenario ───────────────

    [Fact]
    public async Task OffloadedValueOfASubclass_ThroughAnAwareBaseInMemoryStore_IsRefused_AndNothingIsStored()
    {
        var options = new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero } };
        var store = new InMemoryStorage<Article>(null, options);

        var refused = await Record.ExceptionAsync(() =>
            store.CreateAsync(new LongArticle { Id = "p|r", Title = "t", Summary = "s", Body = Ascii(3_500_000, 2) }, Ct));

        AssertSubclassRefusal(refused, nameof(LongArticle), nameof(Article), nameof(LongArticle.Body));
        Assert.Equal(0, store.PhysicalRowCount);
        Assert.Empty(store.OffloadedBlobNames());
    }
}
