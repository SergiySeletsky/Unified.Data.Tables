namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The refusal of rule B (a member marked <see cref="TableValueSplitAttribute"/> holding a value the serializer stores as
/// columns) names only remedies that work, and each one it names, followed, stores the value and reads it back whole at
/// 10 and 200,000 characters on both stores:
/// <list type="bullet">
/// <item>"store an instance of the declared type" only when an instance of it is read back whole: the library's JSON
/// reader constructs such a type through one constructor and sets nothing else, so only when every member its JSON stores
/// is a parameter of that constructor (SERIALIZER-3 of the review of fix wave 8: a settable member outside the
/// constructor was stored and read back null);</item>
/// <item>for a property declared as a nullable struct, the declared type named as <c>S?</c>, and only the remedy that can
/// be followed (every non-null value of <c>S?</c> is an <c>S</c>, which is stored as columns): declare it as <c>S</c> and
/// move the attribute to the members of <c>S</c> (SERIALIZER-4).</item>
/// </list>
/// </summary>
public class MarkedValueAsColumnsRefusalRemediesTests
{
    private const string Table = "MarkedValueRemedies";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly int[] Sizes = [10, 200_000];

    // Stored as one JSON value by its constructor, with a settable member no constructor parameter binds.
    public class UnboundBase
    {
        public UnboundBase(int seed) => Seed = seed;

        public int Seed { get; set; }

        public string? Body { get; set; }
    }

    public sealed class UnboundFlatSub : UnboundBase
    {
        public UnboundFlatSub()
            : base(0)
        {
        }
    }

    public sealed class UnboundHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public UnboundBase? Doc { get; set; }
    }

    // The remedy the refusal names, followed: Doc declared as the runtime type, the attribute moved to its members (here
    // by a class-level attribute on the runtime type, which covers the member it inherits).
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class UnboundFlatSubMarked : UnboundBase
    {
        public UnboundFlatSubMarked()
            : base(0)
        {
        }
    }

    public sealed class UnboundMovedHolder : Entity
    {
        public UnboundFlatSubMarked? Doc { get; set; }
    }

    // Stored as one JSON value by its constructor, which binds every member it stores.
    public class BoundBase
    {
        public BoundBase(int seed, string? body = null)
        {
            Seed = seed;
            Body = body;
        }

        public int Seed { get; set; }

        public string? Body { get; set; }
    }

    public sealed class BoundFlatSub : BoundBase
    {
        public BoundFlatSub()
            : base(0)
        {
        }
    }

    public sealed class BoundHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public BoundBase? Doc { get; set; }
    }

    // A struct with a parameterless constructor is stored as columns; the property is declared as its nullable form.
    public struct FlatStruct
    {
        public FlatStruct()
        {
        }

        public string? Body { get; set; }
    }

    public sealed class NullableHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public FlatStruct? Doc { get; set; }
    }

    // The remedy for it, followed: Doc declared as the struct, the attribute moved to its member.
    public struct MarkedFlatStruct
    {
        public MarkedFlatStruct()
        {
        }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }
    }

    public sealed class StructHolder : Entity
    {
        public MarkedFlatStruct Doc { get; set; } = new();
    }

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    private const string FirstRemedy = "Store an instance of";

    // RV8 SERIALIZER-3, P6 corrected to the requirement: the first remedy is not named, and the one that is, followed,
    // stores and reads the value back whole.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ADeclaredTypeWithAMemberItsConstructorDoesNotBind_TheRefusalNamesOnlyTheRemedyThatWorks(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refused = await Record.ExceptionAsync(() =>
            stores.Store<UnboundHolder>(Table).CreateAsync(new UnboundHolder { Id = stores.Id("r"), Doc = new UnboundFlatSub { Seed = 4, Body = "hello" } }, Ct));
        Assert.IsType<NotSupportedException>(refused);
        Assert.Equal(
            "'Doc' holds a UnboundFlatSub, and UnboundHolder.Doc (declared as UnboundBase) is marked [TableValueSplit]. The serializer " +
            "stores a UnboundFlatSub as columns, one for each of its members, not as one value, and [TableValueSplit] on a property applies to " +
            "one value stored in the property's own cells (a string, a byte[] or an object stored as one JSON value), not to an object stored " +
            "as columns: its members would be stored as UnboundFlatSub declares them, and a trimming OversizedCellPolicy could trim them. " +
            "Declare UnboundHolder.Doc as UnboundFlatSub and move [TableValueSplit] from it to the members of UnboundFlatSub that can hold a " +
            "large value. Nothing was stored.",
            refused.Message);
        Assert.Equal(refused.Message, TableEntitySerializer.MarkedValueStoredAsColumns("Doc", typeof(UnboundHolder).GetProperty(nameof(UnboundHolder.Doc))!, typeof(UnboundFlatSub)));

        // The remedy named, followed.
        foreach (var size in Sizes)
        {
            var id = stores.Id("m" + size);
            var body = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, size);
            await stores.Store<UnboundMovedHolder>(Table).CreateAsync(new UnboundMovedHolder { Id = id, Doc = new UnboundFlatSubMarked { Seed = 4, Body = body } }, Ct);
            var back = (await stores.Store<UnboundMovedHolder>(Table).OneAsync(id, Ct))!.Doc;
            Assert.True(back is { Seed: 4 } && back.Body == body, $"{kind}, {size}: read back Seed={back?.Seed}, Body {back?.Body?.Length} of {size}");
        }
    }

    // RV8 SERIALIZER-3: a declared type whose constructor binds every member it stores: the first remedy is named, and both
    // remedies, followed, store and read the value back whole.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ADeclaredTypeWhoseConstructorBindsEveryMember_TheRefusalNamesBothRemedies_AndBothWork(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refused = await Record.ExceptionAsync(() =>
            stores.Store<BoundHolder>(Table).CreateAsync(new BoundHolder { Id = stores.Id("r"), Doc = new BoundFlatSub { Seed = 4, Body = "hello" } }, Ct));
        Assert.IsType<NotSupportedException>(refused);
        Assert.Contains("Store an instance of BoundBase in BoundHolder.Doc (it is stored as one JSON value), or declare BoundHolder.Doc as BoundFlatSub " +
                        "and move [TableValueSplit] from it to the members of BoundFlatSub that can hold a large value.", refused.Message, StringComparison.Ordinal);
        foreach (var size in Sizes)
        {
            var id = stores.Id("b" + size);
            var body = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, size + 1);
            await stores.Store<BoundHolder>(Table).CreateAsync(new BoundHolder { Id = id, Doc = new BoundBase(4, body) }, Ct);
            var back = (await stores.Store<BoundHolder>(Table).OneAsync(id, Ct))!.Doc;
            Assert.True(back is { Seed: 4 } && back.Body == body && back.GetType() == typeof(BoundBase), $"{kind}, {size}: read back Seed={back?.Seed}, Body {back?.Body?.Length} of {size}");
        }
    }

    // RV8 SERIALIZER-4, P3: the declared type named as S?, the first remedy not named; the remedy named, followed.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ANullableStruct_TheRefusalNamesItReadably_AndOnlyTheRemedyThatCanBeFollowed(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refused = await Record.ExceptionAsync(() =>
            stores.Store<NullableHolder>(Table).CreateAsync(new NullableHolder { Id = stores.Id("s"), Doc = new FlatStruct { Body = "hello" } }, Ct));
        Assert.IsType<NotSupportedException>(refused);
        Assert.Equal(
            "'Doc' holds a FlatStruct, and NullableHolder.Doc (declared as FlatStruct?) is marked [TableValueSplit]. The serializer " +
            "stores a FlatStruct as columns, one for each of its members, not as one value, and [TableValueSplit] on a property applies to " +
            "one value stored in the property's own cells (a string, a byte[] or an object stored as one JSON value), not to an object stored " +
            "as columns: its members would be stored as FlatStruct declares them, and a trimming OversizedCellPolicy could trim them. " +
            "Declare NullableHolder.Doc as FlatStruct and move [TableValueSplit] from it to the members of FlatStruct that can hold a " +
            "large value. Nothing was stored.",
            refused.Message);
        Assert.Empty(await stores.RawRowsAsync<NullableHolder>(Table));

        foreach (var size in Sizes)
        {
            var id = stores.Id("v" + size);
            var body = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, size + 2);
            await stores.Store<StructHolder>(Table).CreateAsync(new StructHolder { Id = id, Doc = new MarkedFlatStruct { Body = body } }, Ct);
            var back = (await stores.Store<StructHolder>(Table).OneAsync(id, Ct))!.Doc;
            Assert.True(back.Body == body, $"{kind}, {size}: read back Body {back.Body?.Length} of {size}");
        }
    }

    // The first remedy is named exactly when an instance of the declared type is read back whole.
    [Fact]
    public void TheFirstRemedy_IsNamedOnlyForADeclaredTypeWhoseConstructorBindsEveryMember()
    {
        static string Message<T>(Type runtime) => TableEntitySerializer.MarkedValueStoredAsColumns("Doc", typeof(T).GetProperty("Doc")!, runtime);
        Assert.DoesNotContain(FirstRemedy, Message<UnboundHolder>(typeof(UnboundFlatSub)), StringComparison.Ordinal);
        Assert.Contains(FirstRemedy, Message<BoundHolder>(typeof(BoundFlatSub)), StringComparison.Ordinal);
        Assert.DoesNotContain(FirstRemedy, Message<NullableHolder>(typeof(FlatStruct)), StringComparison.Ordinal);
        Assert.DoesNotContain("Nullable`1", Message<NullableHolder>(typeof(FlatStruct)), StringComparison.Ordinal);
    }
}
