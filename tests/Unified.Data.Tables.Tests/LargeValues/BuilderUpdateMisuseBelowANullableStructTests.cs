using System.Globalization;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The misuse of ruling R11 (<see cref="TableValueSplitAttribute"/> on a member declared on an interface) reached by a
/// builder update through a property declared as a nullable struct <c>S?</c> whose struct implements that interface is
/// refused with the R11 <see cref="InvalidOperationException"/> before anything is sent or merged, exactly as through a
/// property declared <c>S</c>: under the serializer's column name (<c>Extra_Notes</c>, <c>Doc_Extra_Notes</c>) and under
/// the lambda's (<c>Extra_Value_Notes</c>), with the property empty or initialised, for plain and aware entity types, on
/// both stores, under every policy and at every size. The misuse decision of the builder-update walk asked
/// <c>Nullable&lt;S&gt;</c>, which implements nothing, so <c>Updates["Extra_Notes"]</c> of a plain type was accepted and
/// stored trimmed (SERIALIZER-1 of the review of fix wave 9).
/// </summary>
[Collection("OversizedCellPolicy")]
public class BuilderUpdateMisuseBelowANullableStructTests
{
    private const string Table = "NullableStructMisuse";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OversizedCellPolicy[] Policies =
        [OversizedCellPolicy.Throw, OversizedCellPolicy.TrimSilently, OversizedCellPolicy.TrimWithMarker];

    private static readonly int[] Sizes = [10, 200_000];

    public interface IMarkedNotes
    {
        [TableValueSplit(SplitEncoding.Text)]
        string? Notes { get; set; }
    }

    public struct MisStruct : IMarkedNotes
    {
        public MisStruct()
        {
        }

        public string? Name { get; set; }

        public string? Notes { get; set; }
    }

    public sealed class PlainStruct : Entity
    {
        public MisStruct Extra { get; set; } = new MisStruct();
    }

    public sealed class PlainNullable : Entity
    {
        public MisStruct? Extra { get; set; } = new MisStruct();
    }

    public sealed class PlainNullableEmpty : Entity
    {
        public MisStruct? Extra { get; set; }

        public string? Title { get; set; }
    }

    public sealed class NullableBelow
    {
        public MisStruct? Extra { get; set; } = new MisStruct();
    }

    public sealed class PlainNullableNested : Entity
    {
        public NullableBelow Doc { get; set; } = new();
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareNullable : Entity
    {
        public MisStruct? Extra { get; set; } = new MisStruct();
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareNullableEmpty : Entity
    {
        public MisStruct? Extra { get; set; }

        public string? Title { get; set; }
    }

    private static readonly (Type Type, string Column)[] Shapes =
    [
        (typeof(PlainStruct), "Extra_Notes"),
        (typeof(PlainNullable), "Extra_Notes"), (typeof(PlainNullable), "Extra_Value_Notes"),
        (typeof(PlainNullableEmpty), "Extra_Notes"), (typeof(PlainNullableEmpty), "Extra_Value_Notes"),
        (typeof(PlainNullableNested), "Doc_Extra_Notes"), (typeof(PlainNullableNested), "Doc_Extra_Value_Notes"),
        (typeof(AwareNullable), "Extra_Notes"), (typeof(AwareNullable), "Extra_Value_Notes"),
        (typeof(AwareNullableEmpty), "Extra_Notes"), (typeof(AwareNullableEmpty), "Extra_Value_Notes"),
    ];

    // The R11 message for the struct, as the property declared S reports it.
    private static string StructMisuse()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => TableEntitySerializer.FlattenProperty(typeof(PlainStruct), "Extra_Notes", "x"));
        Assert.Contains("[TableValueSplit] on IMarkedNotes.Notes", thrown.Message, StringComparison.Ordinal);
        return thrown.Message;
    }

    // RV9 SERIALIZER-1, P2a: where the two stores meet (both call this before anything is sent or merged).
    [Fact]
    public void FlattenPropertyForUpdate_BelowANullableStruct_ReportsTheMisuseAsForTheStruct()
    {
        var expected = StructMisuse();
        var lines = new List<string>();
        foreach (var policy in Policies)
        {
            foreach (var size in Sizes)
            {
                var value = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, 5);
                foreach (var (type, column) in Shapes)
                {
                    var before = TableEntitySerializer.OversizedCellPolicy;
                    TableEntitySerializer.OversizedCellPolicy = policy;
                    try
                    {
                        var cells = TableEntitySerializer.FlattenProperty(type, column, value);
                        lines.Add($"{type.Name}.{column}, {policy}, {size}: accepted; cells [{string.Join(", ", cells.Select(kv => $"{kv.Key}({(kv.Value as string)?.Length})"))}]");
                    }
                    catch (InvalidOperationException e) when (e.Message == expected)
                    {
                    }
                    catch (Exception e)
                    {
                        lines.Add($"{type.Name}.{column}, {policy}, {size}: {e.GetType().Name}: {e.Message}");
                    }
                    finally
                    {
                        TableEntitySerializer.OversizedCellPolicy = before;
                    }
                }
            }
        }
        Assert.True(lines.Count == 0, string.Join(" | ", lines));
    }

    public static TheoryData<string, string, string> ShapesStores()
    {
        var d = new TheoryData<string, string, string>();
        foreach (var kind in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
            foreach (var (type, column) in Shapes)
                d.Add(type.Name, column, kind);
        return d;
    }

    // RV9 SERIALIZER-1, P2c made general: every shape, a key of UpdateBuilder.Updates, both stores, every policy and size.
    [Theory]
    [MemberData(nameof(ShapesStores))]
    public Task UpdatesKey_BelowANullableStruct_IsRefusedWithTheMisuse_BeforeAnythingIsSent(string type, string column, string kind) => type switch
    {
        nameof(PlainStruct) => RefusedAsync<PlainStruct>(kind, column),
        nameof(PlainNullable) => RefusedAsync<PlainNullable>(kind, column),
        nameof(PlainNullableEmpty) => RefusedAsync<PlainNullableEmpty>(kind, column),
        nameof(PlainNullableNested) => RefusedAsync<PlainNullableNested>(kind, column),
        nameof(AwareNullable) => RefusedAsync<AwareNullable>(kind, column),
        nameof(AwareNullableEmpty) => RefusedAsync<AwareNullableEmpty>(kind, column),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public sealed class RowWriter : Entity
    {
        public string? Title { get; set; }
    }

    private static async Task RefusedAsync<T>(string kind, string column) where T : class, IEntity, new()
    {
        var expected = StructMisuse();
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        // The row is written by another type: the whole-entity write of an initialised misused struct is itself refused.
        var id = stores.Id("m");
        await stores.Store<RowWriter>(Table).CreateAsync(new RowWriter { Id = id, Title = "t" }, Ct);
        await stores.ShareTableAsync<RowWriter, T>(Table);
        var store = stores.Store<T>(Table);
        var before = (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "m").Keys.Order(StringComparer.Ordinal).ToList();
        var seed = 0;
        foreach (var policy in Policies)
        {
            foreach (var size in Sizes)
            {
                var value = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, ++seed);
                Exception? thrown = null;
                IReadOnlyList<string> requests = [];
                var previous = TableEntitySerializer.OversizedCellPolicy;
                TableEntitySerializer.OversizedCellPolicy = policy;
                try
                {
                    requests = await stores.StorageWritesAsync(store, async () =>
                        thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.Updates[column] = value, Ct)));
                }
                finally
                {
                    TableEntitySerializer.OversizedCellPolicy = previous;
                }
                var after = (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "m").Keys.Order(StringComparer.Ordinal).ToList();
                Assert.True(thrown is InvalidOperationException && thrown.Message == expected && requests.Count == 0 && after.SequenceEqual(before),
                    $"{typeof(T).Name}, {kind}, Updates[{column}], {policy}, {size}: {thrown?.GetType().Name ?? "accepted and reported as written"}: " +
                    $"{thrown?.Message}; requests [{string.Join(", ", requests)}]; cells [{string.Join(", ", after)}]");
            }
        }
    }

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    // RV9 SERIALIZER-1, P2c end to end: the entity is created with Extra null (accepted), then a builder update under
    // either spelling, by a key and by the lambda, is refused with the misuse; nothing is stored, the entity reads back.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task EmptyNullableStruct_CreatedThenUpdated_IsRefusedUnderEitherSpelling(string kind)
    {
        var expected = StructMisuse();
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<PlainNullableEmpty>(Table);
        var big = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 9);
        var id = stores.Id("e");
        await store.CreateAsync(new PlainNullableEmpty { Id = id, Title = "t" }, Ct);
        var lines = new List<string>();
        foreach (var policy in Policies)
        {
            foreach (var (label, update) in new (string, Action<UpdateBuilder<PlainNullableEmpty>>)[]
            {
                ("Updates[Extra_Notes]", b => b.Updates["Extra_Notes"] = big),
                ("Updates[Extra_Value_Notes]", b => b.Updates["Extra_Value_Notes"] = big),
                ("x => x.Extra.Value.Notes", b => b.SetProperty(x => x.Extra!.Value.Notes, big)),
            })
            {
                var previous = TableEntitySerializer.OversizedCellPolicy;
                TableEntitySerializer.OversizedCellPolicy = policy;
                Exception? thrown;
                try
                {
                    thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, update, Ct));
                }
                finally
                {
                    TableEntitySerializer.OversizedCellPolicy = previous;
                }
                var row = (await stores.RawRowsAsync<PlainNullableEmpty>(Table)).Single(r => r.RowKey == "e");
                var back = (await stores.Store<PlainNullableEmpty>(Table).OneAsync(id, Ct))!;
                if (!(thrown is InvalidOperationException && thrown.Message == expected) || row.Keys.Any(k => k.StartsWith("Extra", StringComparison.Ordinal)) || back.Extra is not null || back.Title != "t")
                {
                    lines.Add($"{policy} {label}: {thrown?.GetType().Name ?? "accepted and reported as written"}; cells [{string.Join(", ", row.Keys)}]; " +
                              $"Extra.Notes {back.Extra?.Notes?.Length.ToString(CultureInfo.InvariantCulture) ?? "null"}");
                }
            }
        }
        Assert.True(lines.Count == 0, kind + ": " + string.Join(" | ", lines));
    }

    // RV9 P5 (control, rule 2): a plain type with a nullable struct that implements no marked interface, a legacy name and
    // static constructors: no constructor, getter or type initializer runs; the cells equal the untyped flatten.
    internal static class Counters
    {
        public static int Ctor;
        public static int Getter;
        public static int TypeInit;
    }

    public struct CountedStruct
    {
        static CountedStruct() => Counters.TypeInit++;

        public CountedStruct() => Counters.Ctor++;

        private string? name;

        public string? Name
        {
            readonly get
            {
                Counters.Getter++;
                return name;
            }
            set => name = value;
        }
    }

    public sealed class PlainCounted : Entity
    {
        static PlainCounted() => Counters.TypeInit++;

        public PlainCounted() => Counters.Ctor++;

        private string? title;

        [ColumnAlias("OldTitle")]
        public string? Title
        {
            get
            {
                Counters.Getter++;
                return title;
            }
            set => title = value;
        }

        public CountedStruct? Extra { get; set; }
    }

    [Fact]
    public void APlainTypeThatReachesNoMisuse_RunsNoUserCode_AndKeepsItsCells()
    {
        var before = (Counters.Ctor, Counters.Getter, Counters.TypeInit);
        foreach (var column in new[] { "Title", "OldTitle", "OldTitle__Json", "Extra_Name", "Extra_Value_Name", "Extra" })
        {
            object value = column == "Extra" ? "x" : "v";
            Assert.Equal(TableEntitySerializer.FlattenProperty(column, value), TableEntitySerializer.FlattenProperty(typeof(PlainCounted), column, value));
        }
        Assert.Equal(before, (Counters.Ctor, Counters.Getter, Counters.TypeInit));
    }
}
