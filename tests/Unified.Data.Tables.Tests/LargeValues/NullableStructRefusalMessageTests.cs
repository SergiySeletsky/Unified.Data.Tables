namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The refusal of a large value below a nullable struct of an entity that is not large-value aware names a
/// remedy that works (fix wave 3, stage x1, item 4; the concern of stage s2). Marking the entity with
/// <c>[TableValueSplit]</c> alone leads to another refusal (a read cannot create the struct of a nullable
/// property); declaring the property with the struct type itself makes the entity aware by its declared
/// types and stores the value. How a nullable struct that flattens reads back (SERIALIZER-4, ruling R6)
/// is not changed.
/// </summary>
public class NullableStructRefusalMessageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public struct Enclosure
    {
        public Enclosure()
        {
        }

        public string? Name { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public sealed class Parcel : Entity
    {
        public Enclosure? Extra { get; set; }

        public string? Subject { get; set; }
    }

    /// <summary>The remedy the message names.</summary>
    public sealed class DeclaredParcel : Entity
    {
        public Enclosure Extra { get; set; }

        public string? Subject { get; set; }
    }

    public sealed class Wrapper
    {
        public Enclosure? Inner { get; set; }
    }

    public sealed class Crate : Entity
    {
        public Wrapper Box { get; set; } = new();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task LargeValueBelowANullableStruct_OfAnEntityThatIsNotAware_IsRefused_WithARemedyThatWorks(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = new string('n', 40_000);

        var refused = await Record.ExceptionAsync(() => stores.Store<Parcel>().CreateAsync(
            new Parcel { Id = stores.Id("r"), Subject = "s", Extra = new Enclosure { Name = "n", Notes = notes } }, Ct));

        var ex = Assert.IsType<NotSupportedException>(refused);
        Assert.Contains("'Extra_Notes'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Declare Parcel.Extra as Enclosure", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nullable", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark Parcel with [TableValueSplit]", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<Parcel>());

        // The remedy works: the value is stored and read back.
        await stores.Store<DeclaredParcel>().CreateAsync(
            new DeclaredParcel { Id = stores.Id("r"), Subject = "s", Extra = new Enclosure { Name = "n", Notes = notes } }, Ct);
        Assert.Equal(notes, (await stores.Store<DeclaredParcel>().OneAsync(stores.Id("r"), Ct))!.Extra.Notes);
    }

    [Fact]
    public void LargeValueBelowANestedNullableStruct_NamesTheNullableProperty()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            new Crate { Id = "p|r", Box = new Wrapper { Inner = new Enclosure { Notes = "x" } } }.ToTableEntity("p", "r"));

        Assert.Contains("'Box_Inner_Notes'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Declare Wrapper.Inner as Enclosure", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark Crate with [TableValueSplit]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyNullableStruct_OfAnEntityThatIsNotAware_IsWrittenAsOn083()
    {
        var row = new Parcel { Id = "p|r", Subject = "s" }.ToTableEntity("p", "r");

        Assert.Equal("s", row["Subject"]);
        Assert.DoesNotContain(row.Keys, k => k.StartsWith("Extra", StringComparison.Ordinal));
    }
}
