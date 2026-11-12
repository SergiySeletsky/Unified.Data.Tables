namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A value a <see cref="TableValueSplitAttribute"/> member stores as JSON cannot carry a string with an unpaired
/// surrogate: System.Text.Json writes U+FFFD in its place, so the value would read back altered with no error
/// (dyn-fuzz, PR #18 deep review). The write is refused instead, on both stores and through the public serializer,
/// at every size and before anything is sent; a string member keeps its lossless <c>utf16le</c> form, and a
/// well-formed surrogate pair is stored as it always was. A type that opts into nothing is untouched.
/// </summary>
[Collection("OversizedCellPolicy")]
public class IllFormedTextInAJsonSplitValueTests
{
    private const string Table = "IllFormedJsonSplit";
    private const string Lone = "a\uD800b";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Doc
    {
        public string? Title { get; set; }

        public Dictionary<string, string> Tags { get; set; } = [];
    }

    public sealed class MarkedList : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public List<string>? Items { get; set; }
    }

    public sealed class MarkedDoc : Entity
    {
        [TableValueSplit(SplitEncoding.Gzip)]
        public List<Doc>? Body { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class ClassLevel : Entity
    {
        public List<string>? Items { get; set; }

        public string? Text { get; set; }
    }

    public sealed class Plain : Entity
    {
        public List<string>? Items { get; set; }
    }

    public static TheoryData<string, int> Cases()
    {
        var d = new TheoryData<string, int>();
        foreach (var kind in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        {
            d.Add(kind, 1);
            d.Add(kind, 20_000);
        }
        return d;
    }

    private static List<string> Items(int count) =>
        [.. Enumerable.Range(0, count).Select(i => i == count / 2 ? Lone : "item-" + i.ToString("D6"))];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task MarkedJsonValue_WithAnUnpairedSurrogate_IsRefusedBeforeAnythingIsSent(string kind, int count)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<MarkedList>(Table);
        var id = stores.Id("r");

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.CreateAsync(new MarkedList { Id = id, Items = Items(count) }, Ct));

        Assert.Contains("Items", ex.Message, StringComparison.Ordinal);
        Assert.Contains("surrogate", ex.Message, StringComparison.Ordinal);
        Assert.Null(await store.OneAsync(id, Ct));
        Assert.Empty(await stores.RawRowsAsync<MarkedList>(Table));
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task MarkedJsonObject_WithAnUnpairedSurrogateInADictionaryKeyOrValue_IsRefused(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<MarkedDoc>(Table);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.CreateAsync(new MarkedDoc { Id = stores.Id("k"), Body = [new Doc { Tags = { [Lone] = "v" } }] }, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.CreateAsync(new MarkedDoc { Id = stores.Id("v"), Body = [new Doc { Title = Lone }] }, Ct));
        Assert.Empty(await stores.RawRowsAsync<MarkedDoc>(Table));
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task ClassLevelSplit_RefusesTheJsonValue_AndKeepsTheStringLossless(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<ClassLevel>(Table);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.CreateAsync(new ClassLevel { Id = stores.Id("j"), Items = [Lone] }, Ct));

        var id = stores.Id("s");
        await store.CreateAsync(new ClassLevel { Id = id, Items = ["fine"], Text = Lone }, Ct);
        var read = (await store.OneAsync(id, Ct))!;
        Assert.Equal(Lone, read.Text);
        Assert.Equal(["fine"], read.Items);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task MarkedJsonValue_WithWellFormedSurrogatePairs_RoundTrips(string kind, int count)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<MarkedList>(Table);
        var id = stores.Id("ok");
        var items = Enumerable.Range(0, count).Select(i => "\U0001F600-" + i).ToList();

        await store.CreateAsync(new MarkedList { Id = id, Items = items }, Ct);

        Assert.Equal(items, (await store.OneAsync(id, Ct))!.Items);
    }

    [Fact]
    public void PublicSerializer_RefusesTheMarkedJsonValue()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            new MarkedList { Id = "p|r", Items = [Lone] }.ToTableEntity("p", "r"));

        Assert.Contains("Items", ex.Message, StringComparison.Ordinal);
    }

    public sealed record Rich(string Name, char Letter, SplitEncoding Mode)
    {
        public string? Missing { get; init; }

        public Dictionary<string, string?> Map { get; init; } = [];

        public Dictionary<char, int> ByChar { get; init; } = [];

        public object? Boxed { get; init; }
    }

    public sealed class MarkedRich : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public List<Rich>? Value { get; set; }
    }

    public sealed class PlainRich : Entity
    {
        public List<Rich>? Value { get; set; }
    }

    /// <summary>The check changes no byte of a well-formed value: the marked cell is the unmarked cell.</summary>
    [Fact]
    public void MarkedJsonCell_IsByteForByteTheUnmarkedCell()
    {
        List<Rich> value =
        [
            new("n\U0001F600é\"\\\n<>&", 'x', SplitEncoding.Gzip)
            {
                Map = { ["k\U0001F600"] = "v", ["nul"] = null },
                ByChar = { ['c'] = 1 },
                Boxed = "boxed\U0001F600",
            },
            new("second", 'é', SplitEncoding.Text),
        ];

        var marked = new MarkedRich { Id = "p|r", Value = value }.ToTableEntity("p", "r").GetString("Value__Json");
        var plain = new PlainRich { Id = "p|r", Value = value }.ToTableEntity("p", "r").GetString("Value__Json");

        Assert.NotNull(marked);
        Assert.Equal(plain, marked);
    }

    /// <summary>A type that opts into nothing writes exactly what 0.8.3 wrote, U+FFFD included.</summary>
    [Fact]
    public void UnmarkedType_IsUnchanged()
    {
        var entity = new Plain { Id = "p|r", Items = [Lone] }.ToTableEntity("p", "r");

        Assert.Equal("""["a\uFFFDb"]""", entity.GetString("Items__Json"));
    }
}
