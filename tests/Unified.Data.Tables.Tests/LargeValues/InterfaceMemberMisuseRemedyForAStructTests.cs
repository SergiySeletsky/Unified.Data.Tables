namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The misuse of ruling R11 (<see cref="TableValueSplitAttribute"/> on a member declared on an interface) names, for a
/// struct implementer, only remedies C# accepts and that work: not "or on the class S" (the attribute is not valid on a
/// struct, CS0592); and where a builder update's path holds the struct in a property declared <c>S?</c>, also "declare
/// that property as S", since a marked member of a nullable struct is refused in turn (SERIALIZER-4 of the review of fix
/// wave 10, repro C2). A class implementer keeps the message it had. Each named remedy, followed, stores the value whole on
/// both stores at 10 and 200,000 characters.
/// </summary>
[Collection("OversizedCellPolicy")]
public class InterfaceMemberMisuseRemedyForAStructTests
{
    private const string Table = "MisuseRemedyForAStruct";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly int[] Sizes = [10, 200_000];

    public interface IMarkedNote
    {
        [TableValueSplit(SplitEncoding.Text)]
        string? Notes { get; set; }
    }

    public struct NoteStruct : IMarkedNote
    {
        public NoteStruct()
        {
        }

        public string? Name { get; set; }

        public string? Notes { get; set; }
    }

    public sealed class NoteClass : IMarkedNote
    {
        public string? Notes { get; set; }
    }

    public sealed class StructHolder : Entity
    {
        public NoteStruct Extra { get; set; } = new();
    }

    public sealed class NullableHolder : Entity
    {
        public NoteStruct? Extra { get; set; }

        public string? Title { get; set; }
    }

    public sealed class NullableBelow
    {
        public NoteStruct? Extra { get; set; }
    }

    public sealed class NestedNullableHolder : Entity
    {
        public NullableBelow W { get; set; } = new();
    }

    public sealed class ClassHolder : Entity
    {
        public NoteClass Extra { get; set; } = new();
    }

    private const string Preamble =
        "[TableValueSplit] on IMarkedNote.Notes, a member declared on an interface that {0} implements: .NET does not pass the attributes " +
        "of an interface member to the implementing class, so the attribute cannot be honoured (the value would be stored as an ordinary " +
        "cell, and could be trimmed). ";

    private const string Tail =
        " Where an entity holds a {0} in a property declared as IMarkedNote (or as another type), mark the entity class with " +
        "[TableValueSplit] as well, so that the entity is large-value aware.";

    private static string Format(string text, string type) => string.Format(System.Globalization.CultureInfo.InvariantCulture, text, type);

    // The message for the struct, without the class-level remedy (CS0592 on a struct).
    private static readonly string StructMessage = Format(Preamble, "NoteStruct") +
        "Put [TableValueSplit] on NoteStruct.Notes (the property of the implementing class) and remove it from IMarkedNote." + Format(Tail, "NoteStruct");

    // The message for the struct held in a property declared NoteStruct? on a builder update's path.
    private static string NullableMessage(string holder) => Format(Preamble, "NoteStruct") +
        $"Put [TableValueSplit] on NoteStruct.Notes (the property of the implementing class) and remove it from IMarkedNote, and declare {holder} as NoteStruct." +
        Format(Tail, "NoteStruct");

    // The message for a class, as before.
    private static readonly string ClassMessage = Format(Preamble, "NoteClass") +
        "Put [TableValueSplit] on NoteClass.Notes (the property of the implementing class), or on the class NoteClass, and remove it from " +
        "IMarkedNote." + Format(Tail, "NoteClass");

    // RV10 SERIALIZER-4: "or on the class S" is not named for a struct, on a builder update and on a whole-entity write.
    [Fact]
    public void ForAStruct_TheMisuseNamesNoClassLevelAttribute_AndForAClassTheMessageIsAsBefore()
    {
        Assert.Equal(StructMessage, Assert.Throws<InvalidOperationException>(() => TableEntitySerializer.FlattenProperty(typeof(StructHolder), "Extra_Notes", "x")).Message);
        Assert.Equal(StructMessage, Assert.Throws<InvalidOperationException>(() =>
            new StructHolder { Id = "p|r", Extra = new NoteStruct { Notes = "x" } }.ToTableEntity("p", "r")).Message);
        // A whole-entity write meets the misuse of the struct's type, whatever holds it: the message of the struct.
        Assert.Equal(StructMessage, Assert.Throws<InvalidOperationException>(() =>
            new NullableHolder { Id = "p|r", Extra = new NoteStruct { Notes = "x" } }.ToTableEntity("p", "r")).Message);
        Assert.Equal(ClassMessage, Assert.Throws<InvalidOperationException>(() => TableEntitySerializer.FlattenProperty(typeof(ClassHolder), "Extra_Notes", "x")).Message);
        Assert.Equal(ClassMessage, Assert.Throws<InvalidOperationException>(() => new ClassHolder { Id = "p|r" }.ToTableEntity("p", "r")).Message);
    }

    // RV10 SERIALIZER-4: a builder update whose path holds the struct in a property declared S? also names declaring that
    // property as S, under both spellings of the column and below another owner.
    [Fact]
    public void ForANullableStructOnTheUpdatePath_TheMisuseNamesDeclaringThePropertyAsTheStruct()
    {
        Assert.Equal(NullableMessage("NullableHolder.Extra"), Assert.Throws<InvalidOperationException>(() => TableEntitySerializer.FlattenProperty(typeof(NullableHolder), "Extra_Notes", "x")).Message);
        Assert.Equal(NullableMessage("NullableHolder.Extra"), Assert.Throws<InvalidOperationException>(() => TableEntitySerializer.FlattenProperty(typeof(NullableHolder), "Extra_Value_Notes", "x")).Message);
        Assert.Equal(NullableMessage("NullableBelow.Extra"), Assert.Throws<InvalidOperationException>(() => TableEntitySerializer.FlattenProperty(typeof(NestedNullableHolder), "W_Extra_Notes", "x")).Message);
        Assert.DoesNotContain("or on the class", NullableMessage("NullableHolder.Extra"), StringComparison.Ordinal);
    }

    // The remedies, followed (RV10 repro C2): the attribute moved from the interface to the struct's property ...
    public interface INote
    {
        string? Notes { get; set; }
    }

    public struct FixedStruct : INote
    {
        public FixedStruct()
        {
        }

        public string? Name { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    // ... and the property declared as the struct (for the S? holder: declared as S, not nullable).
    public sealed class FixedHolder : Entity
    {
        public FixedStruct Extra { get; set; } = new();
    }

    // Only the first remedy followed for an S? holder: refused in turn, which is why the message names the second.
    public sealed class FixedNullableHolder : Entity
    {
        public FixedStruct? Extra { get; set; }
    }

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheNamedRemedies_Followed_StoreTheValueWhole(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);

        // The misuse as a store reports it, before anything is sent (the row of an empty S? holder is stored first).
        var uid = stores.Id("u");
        await stores.Store<NullableHolder>(Table).CreateAsync(new NullableHolder { Id = uid, Title = "t" }, Ct);
        var cells = (await stores.RawRowsAsync<NullableHolder>(Table)).Single(r => r.RowKey == "u").Keys.Order(StringComparer.Ordinal).ToList();
        var misuse = await Record.ExceptionAsync(() => stores.Store<NullableHolder>(Table).UpdateAsync(uid, b => b.Updates["Extra_Notes"] = "x", Ct));
        Assert.IsType<InvalidOperationException>(misuse);
        Assert.Equal(NullableMessage("NullableHolder.Extra"), misuse.Message);
        Assert.Equal(cells, (await stores.RawRowsAsync<NullableHolder>(Table)).Single(r => r.RowKey == "u").Keys.Order(StringComparer.Ordinal).ToList());
        var structMisuse = await Record.ExceptionAsync(() => stores.Store<StructHolder>(Table).CreateAsync(new StructHolder { Id = stores.Id("s") }, Ct));
        Assert.IsType<InvalidOperationException>(structMisuse);
        Assert.Equal(StructMessage, structMisuse.Message);
        Assert.DoesNotContain((await stores.RawRowsAsync<StructHolder>(Table)).Select(r => r.RowKey), k => k == "s");

        foreach (var size in Sizes)
        {
            var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, size + 31);
            var id = stores.Id("f" + size);
            await stores.Store<FixedHolder>(Table).CreateAsync(new FixedHolder { Id = id, Extra = new FixedStruct { Name = "n", Notes = notes } }, Ct);
            var point = (await stores.Store<FixedHolder>(Table).OneAsync(id, Ct))!.Extra;
            var queried = (await stores.Store<FixedHolder>(Table).QueryAsync(stores.Pk, Ct)).Single(e => e.Id == id).Extra;
            foreach (var (read, back) in new[] { ("point read", point), ("query", queried) })
                Assert.True(back.Name == "n" && back.Notes == notes, $"{kind}, {size}, {read}: read back Name={back.Name}, Notes {back.Notes?.Length} of {size}");

            var partial = await Record.ExceptionAsync(() => stores.Store<FixedNullableHolder>(Table).CreateAsync(
                new FixedNullableHolder { Id = stores.Id("p" + size), Extra = new FixedStruct { Name = "n", Notes = notes } }, Ct));
            Assert.IsType<NotSupportedException>(partial);
            Assert.Contains("Declare FixedNullableHolder.Extra as FixedStruct, not nullable", partial.Message, StringComparison.Ordinal);
            Assert.DoesNotContain((await stores.RawRowsAsync<FixedNullableHolder>(Table)).Select(r => r.RowKey), k => k == "p" + size);
        }
    }
}
