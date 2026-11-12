using System.Globalization;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A member marked <see cref="TableValueSplitAttribute"/> whose declared type is stored as one JSON value (a non-sealed
/// class whose constructors all take parameters), holding at run time a subclass the serializer flattens into columns
/// because it has a public parameterless constructor (OBSERVED-2 of the review of fix wave 7): the attribute was
/// silently ignored and the subclass's members were written as ordinary cells, trimmed under a trimming
/// <see cref="OversizedCellPolicy"/>.
/// <para>
/// Rule B of fix wave 8: a marked value is never written as flattened columns. When a member marked
/// <c>[TableValueSplit]</c> (on the property, or by a class-level attribute that covers it) holds a value the serializer
/// would flatten into columns, the write is refused with <see cref="NotSupportedException"/> before anything is stored
/// or uploaded, on every write path and both stores. A member holding an instance of its declared JSON-stored type, a
/// string, a <c>byte[]</c> or any other value stored as one value keeps working, at every size.
/// </para>
/// </summary>
[Collection("OversizedCellPolicy")]
public class MarkedValueHoldingAnObjectStoredAsColumnsTests
{
    private const string Table = "MarkedValueAsColumns";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A non-sealed concrete class that is one JSON cell by its constructors (its only constructor has a parameter), so a
    // property-level [TableValueSplit] on a property declared with it is valid, and a subclass with a parameterless
    // constructor, which the writer flattens into columns. Body is a constructor parameter too: the library's JSON
    // reader for such a type (ParameterizedConstructorConverter, 0.8.3 code) binds constructor parameters only.
    public class JsonByCtorBase
    {
        public JsonByCtorBase(int seed, string? body = null)
        {
            Seed = seed;
            Body = body;
        }

        public int Seed { get; set; }

        public string? Body { get; set; }
    }

    public sealed class JsonByCtorFlatSub : JsonByCtorBase
    {
        public JsonByCtorFlatSub()
            : base(0)
        {
        }
    }

    public sealed class MarkedJsonHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public JsonByCtorBase Doc { get; set; } = new JsonByCtorFlatSub();
    }

    // The same below a nested object.
    public sealed class Wrap
    {
        [TableValueSplit(SplitEncoding.Text)]
        public JsonByCtorBase Doc { get; set; } = new JsonByCtorFlatSub();
    }

    public sealed class NestedMarkedJsonHolder : Entity
    {
        public Wrap W { get; set; } = new();
    }

    // A positional record: one JSON value by its constructor; its derived record has a parameterless one and flattens.
    public record RecordBase(int Seed, string? Body);

    public sealed record RecordFlatSub() : RecordBase(0, null);

    public sealed class MarkedRecordHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public RecordBase Doc { get; set; } = new RecordFlatSub();
    }

    // A struct with no constructor of its own is stored as one JSON value; a struct cannot be derived from.
    public struct JsonStruct
    {
        public string? Body { get; set; }
    }

    public sealed class MarkedStructHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public JsonStruct Doc { get; set; }
    }

    // A class-level attribute covers Doc by its declared type, but only a value stored as one value: the flattened
    // object is not covered, and its members are stored as its type declares them (the README's runtime-subtype
    // opt-in). Here Body is not marked: one cell.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class ClassLevelJsonHolder : Entity
    {
        public JsonByCtorBase Doc { get; set; } = new JsonByCtorFlatSub();
    }

    // The second remedy the refusal names, followed: Doc declared as the runtime type, the attribute moved to its member.
    public sealed class MovedFlatSub
    {
        public int Seed { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }
    }

    public sealed class MovedAttributeHolder : Entity
    {
        public MovedFlatSub Doc { get; set; } = new();
    }

    // The type another program (or this library before rule B) used to write the same table: Doc's members as cells.
    public sealed class FlatWriter : Entity
    {
        public JsonByCtorFlatSub Doc { get; set; } = new();
    }

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    private static readonly string[] Writes = ["CreateAsync", "UpsertAsync", "UpdateAsync", "UpdateAsync(LastWriterWins)", "CreateBatchAsync", "UpsertBatchAsync"];

    private static readonly OversizedCellPolicy[] Policies = [OversizedCellPolicy.Throw, OversizedCellPolicy.TrimSilently, OversizedCellPolicy.TrimWithMarker];

    public static TheoryData<string, string, OversizedCellPolicy> StoresWritesPolicies()
    {
        var d = new TheoryData<string, string, OversizedCellPolicy>();
        foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
            foreach (var w in Writes)
                foreach (var p in Policies)
                    d.Add(k, w, p);
        return d;
    }

    private static readonly string Large = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 91);

    private static async Task WithPolicyAsync(OversizedCellPolicy policy, Func<Task> action)
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        try
        {
            await action();
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    // Runs one whole-entity write of entity through store: the update forms need a stored row of the same id first.
    private static Task WriteAsync<T>(IStorage<T> store, string write, T entity) where T : class, IEntity, new() => write switch
    {
        "CreateAsync" => store.CreateAsync(entity, Ct),
        "UpsertAsync" => store.UpsertAsync(entity, Ct),
        "UpdateAsync" => store.UpdateAsync(entity, Ct),
        "UpdateAsync(LastWriterWins)" => store.UpdateAsync(entity, ConcurrencyMode.LastWriterWins, Ct),
        "CreateBatchAsync" => store.CreateBatchAsync([entity], Ct),
        _ => store.UpsertBatchAsync([entity], Ct),
    };

    // ── Rule B: refused on every write path, both stores, every policy, at every size ───────────────────────

    [Theory]
    [MemberData(nameof(StoresWritesPolicies))]
    public async Task MarkedMemberHoldingAnObjectStoredAsColumns_IsRefusedBeforeAnythingIsStored_OnEveryWrite(string kind, string write, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<MarkedJsonHolder>(Table);
        var id = stores.Id("w");
        // What the update forms replace: the member holding an instance of its declared type (one JSON value).
        var stored = new MarkedJsonHolder { Id = id, Doc = new JsonByCtorBase(1, "before") };
        if (write.StartsWith("UpdateAsync", StringComparison.Ordinal))
            stored = await store.CreateAsync(stored, Ct);
        foreach (var length in new[] { 10, 200_000 })
        {
            var entity = new MarkedJsonHolder { Id = id, ETag = stored.ETag };
            entity.Doc.Body = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(length, length);
            Exception? thrown = null;
            IReadOnlyList<string> requests = [];
            await WithPolicyAsync(policy, async () =>
                requests = await stores.StorageWritesAsync(store, async () => thrown = await Record.ExceptionAsync(() => WriteAsync(store, write, entity))));
            var rows = await stores.RawRowsAsync<MarkedJsonHolder>(Table);
            var report = $"{kind}, {write}, {policy}, {length}: {thrown?.GetType().Name ?? "accepted"}: {thrown?.Message}; requests [{string.Join(", ", requests)}]; " +
                         $"rows [{string.Join("; ", rows.Select(r => string.Join(",", r.Keys)))}]";
            Assert.True(thrown is NotSupportedException && requests.Count == 0 && (await stores.BlobNamesAsync<MarkedJsonHolder>(Table)).Count == 0, report);
            Assert.Equal(TableEntitySerializer.MarkedValueStoredAsColumns("Doc", typeof(MarkedJsonHolder).GetProperty(nameof(MarkedJsonHolder.Doc))!, typeof(JsonByCtorFlatSub)),
                thrown!.Message);
            // The stored entity is as it was (no row for a create).
            var back = await stores.Store<MarkedJsonHolder>(Table).OneAsync(id, Ct);
            Assert.True(write.StartsWith("UpdateAsync", StringComparison.Ordinal) ? back!.Doc.GetType() == typeof(JsonByCtorBase) && back.Doc.Body == "before" : back is null, report);
        }
    }

    // The message names the column, the declared and the runtime type, says what the attribute applies to, and the
    // remedies; also below a nested object, for a positional record, and through the public serializer.
    [Fact]
    public void TheRefusal_NamesTheColumn_TheDeclaredAndTheRuntimeType_AndWhatToDo()
    {
        var top = Assert.Throws<NotSupportedException>(() => new MarkedJsonHolder { Id = "p|r" }.ToTableEntity("p", "r"));
        Assert.Equal(
            "'Doc' holds a JsonByCtorFlatSub, and MarkedJsonHolder.Doc (declared as JsonByCtorBase) is marked [TableValueSplit]. The serializer " +
            "stores a JsonByCtorFlatSub as columns, one for each of its members, not as one value, and [TableValueSplit] on a property applies to " +
            "one value stored in the property's own cells (a string, a byte[] or an object stored as one JSON value), not to an object stored " +
            "as columns: its members would be stored as JsonByCtorFlatSub declares them, and a trimming OversizedCellPolicy could trim them. " +
            "Store an instance of JsonByCtorBase in MarkedJsonHolder.Doc (it is stored as one JSON value), or declare MarkedJsonHolder.Doc as " +
            "JsonByCtorFlatSub and move [TableValueSplit] from it to the members of JsonByCtorFlatSub that can hold a large value. Nothing was stored.",
            top.Message);
        var nested = Assert.Throws<NotSupportedException>(() => new NestedMarkedJsonHolder { Id = "p|r" }.ToTableEntity("p", "r"));
        Assert.StartsWith("'W_Doc' holds a JsonByCtorFlatSub, and Wrap.Doc (declared as JsonByCtorBase) is marked [TableValueSplit].", nested.Message, StringComparison.Ordinal);
        var record = Assert.Throws<NotSupportedException>(() => new MarkedRecordHolder { Id = "p|r" }.ToTableEntity("p", "r"));
        Assert.StartsWith("'Doc' holds a RecordFlatSub, and MarkedRecordHolder.Doc (declared as RecordBase) is marked [TableValueSplit].", record.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AMarkedMemberBelowANestedObject_OrOfARecord_HoldingAnObjectStoredAsColumns_IsRefused(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var nested = new NestedMarkedJsonHolder { Id = stores.Id("n") };
        nested.W.Doc.Body = Large;
        var nestedThrown = await Record.ExceptionAsync(() => stores.Store<NestedMarkedJsonHolder>(Table).CreateAsync(nested, Ct));
        var record = new MarkedRecordHolder { Id = stores.Id("r"), Doc = new RecordFlatSub { Body = Large } };
        var recordThrown = await Record.ExceptionAsync(() => stores.Store<MarkedRecordHolder>(Table).CreateAsync(record, Ct));
        Assert.True(nestedThrown is NotSupportedException && recordThrown is NotSupportedException,
            $"{kind}: nested {nestedThrown?.GetType().Name ?? "accepted"}, record {recordThrown?.GetType().Name ?? "accepted"}");
        Assert.Empty(await stores.RawRowsAsync<NestedMarkedJsonHolder>(Table));
    }

    // ── What keeps working: one value at every size ─────────────────────────────────────────────────────

    // The member holding an instance of its declared type, of a positional record, a struct stored as JSON: stored as one
    // value (split when large) and read back whole by every read, under every policy, at 10, 200,000 and 700,000
    // characters.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AMarkedMemberHoldingOneValue_IsStoredWhole_AtEverySize(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        foreach (var policy in Policies)
        {
            foreach (var length in new[] { 10, 200_000, 700_000 })
            {
                var body = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(length, length + (int)policy);
                var suffix = $"{(int)policy}-{length}";
                await WithPolicyAsync(policy, async () =>
                {
                    await stores.Store<MarkedJsonHolder>(Table).CreateAsync(new MarkedJsonHolder { Id = stores.Id("c" + suffix), Doc = new JsonByCtorBase(2, body) }, Ct);
                    await stores.Store<MarkedRecordHolder>(Table).CreateAsync(new MarkedRecordHolder { Id = stores.Id("r" + suffix), Doc = new RecordBase(3, body) }, Ct);
                    await stores.Store<MarkedStructHolder>(Table).CreateAsync(new MarkedStructHolder { Id = stores.Id("s" + suffix), Doc = new JsonStruct { Body = body } }, Ct);
                });
                await AssertEveryReadAsync(stores, stores.Id("c" + suffix), (MarkedJsonHolder e) => e.Doc.Body, body, $"{kind}, {policy}, {length}, declared class");
                await AssertEveryReadAsync(stores, stores.Id("r" + suffix), (MarkedRecordHolder e) => e.Doc.Body, body, $"{kind}, {policy}, {length}, record");
                await AssertEveryReadAsync(stores, stores.Id("s" + suffix), (MarkedStructHolder e) => e.Doc.Body, body, $"{kind}, {policy}, {length}, struct");
            }
        }
    }

    private static async Task AssertEveryReadAsync<T>(LargeValueStores stores, string id, Func<T, string?> get, string expected, string name)
        where T : class, IEntity, new()
    {
        var store = stores.Store<T>(Table);
        var reads = new[] { await store.OneAsync(id, Ct), await store.OneAsync(id, Ct), await stores.Store<T>(Table).OneAsync(id, Ct),
            (await stores.Store<T>(Table).QueryAsync(stores.Pk, Ct)).Single(e => e.Id == id) };
        foreach (var read in reads)
            Assert.True(get(read!) == expected, $"{name}: read back {get(read!)?.Length ?? -1} of {expected.Length} characters");
    }

    // A class-level attribute covers only values stored as one value: the flattened object in a property it covers by
    // its declared type is not covered, as before (the README's runtime-subtype opt-in), so its members are stored as
    // its type declares them: an ordinary cell here.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AClassLevelAttribute_DoesNotCoverAnObjectStoredAsColumns_ItsMembersAreStoredAsTheirTypeDeclaresThem(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var entity = new ClassLevelJsonHolder { Id = stores.Id("l") };
        entity.Doc.Body = "ten chars!";
        await stores.Store<ClassLevelJsonHolder>(Table).CreateAsync(entity, Ct);
        var row = (await stores.RawRowsAsync<ClassLevelJsonHolder>(Table)).Single();
        Assert.Equal("ten chars!", row["Doc_Body"]);
        await AssertEveryReadAsync(stores, entity.Id, (ClassLevelJsonHolder e) => e.Doc.Body, "ten chars!", $"{kind}, class-level");
    }

    // The second remedy, followed: the property declared as the runtime type and the attribute moved to its member.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheRemedy_DeclareTheRuntimeTypeAndMoveTheAttributeToItsMembers_StoresTheValueWhole(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("m");
        await stores.Store<MovedAttributeHolder>(Table).CreateAsync(new MovedAttributeHolder { Id = id, Doc = new MovedFlatSub { Body = Large } }, Ct);
        await AssertEveryReadAsync(stores, id, (MovedAttributeHolder e) => e.Doc.Body, Large, $"{kind}, moved attribute");
    }

    // A row whose Doc is stored as columns (written by another type, or before this refusal existed) reads back: the
    // read keeps the default instance the constructor puts in Doc (a JsonByCtorFlatSub) and sets its members, and the
    // store's own copy for its cache is no write. Writing that entity back is refused, before anything is stored.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ARowStoredAsColumns_ReadsBack_AndWritingItBackIsRefused(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("o");
        await stores.Store<FlatWriter>(Table).CreateAsync(new FlatWriter { Id = id, Doc = { Seed = 4, Body = "as columns" } }, Ct);
        await stores.ShareTableAsync<FlatWriter, MarkedJsonHolder>(Table);
        var store = stores.Store<MarkedJsonHolder>(Table);
        var read = await store.OneAsync(id, Ct);
        var again = await store.OneAsync(id, Ct);
        Assert.True(read!.Doc is JsonByCtorFlatSub { Seed: 4, Body: "as columns" } && again!.Doc is JsonByCtorFlatSub { Body: "as columns" },
            $"{kind}: read back {read.Doc.GetType().Name} {read.Doc.Body}");
        var thrown = await Record.ExceptionAsync(() => store.UpdateAsync(read, Ct));
        Assert.IsType<NotSupportedException>(thrown);
        Assert.Equal("as columns", (await stores.Store<FlatWriter>(Table).OneAsync(id, Ct))!.Doc.Body);
    }

    // A builder update of the marked member (whatever it holds) or below it is refused (rule A).
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ABuilderUpdate_OfTheMarkedMember_OrBelowIt_IsRefusedBeforeAnyRequest(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("b");
        var store = stores.Store<MarkedJsonHolder>(Table);
        await store.CreateAsync(new MarkedJsonHolder { Id = id, Doc = new JsonByCtorBase(1, "x") }, Ct);
        foreach (var update in new Action<UpdateBuilder<MarkedJsonHolder>>[]
                 {
                     b => b.SetProperty(x => x.Doc, new JsonByCtorFlatSub { Body = "flat" }),
                     b => b.SetProperty(x => x.Doc, new JsonByCtorBase(2, "json")),
                     b => b.SetProperty(x => x.Doc.Body, "below"),
                     b => b.Updates["Doc_Body"] = "below",
                 })
        {
            Exception? thrown = null;
            var requests = await stores.StorageWritesAsync(store, async () => thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, update, Ct)));
            Assert.True(thrown is NotSupportedException && requests.Count == 0, $"{kind}: {thrown?.GetType().Name ?? "accepted"}; requests [{string.Join(", ", requests)}]");
        }
    }

    // OBSERVED-2 (verifier): a whole-entity write under a trimming policy. Rule 1 / R11 ("a value the user marked is
    // never silently treated as unmarked"): refused, or stored and read back whole. Control: the base instance (stored
    // as JSON, split) round-trips.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task MarkedPropertyOfAJsonByConstructorsClass_HoldingAFlattenedSubclass_IsNeverStoredTrimmed(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<MarkedJsonHolder>(Table);
        var control = new MarkedJsonHolder { Id = stores.Id("c"), Doc = new JsonByCtorBase(1, Large) };
        await store.CreateAsync(control, Ct);
        var controlBack = (await stores.Store<MarkedJsonHolder>(Table).OneAsync(stores.Id("c"), Ct))!.Doc;

        var entity = new MarkedJsonHolder { Id = stores.Id("s") };
        entity.Doc.Body = Large;
        Exception? thrown;
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimSilently;
        try
        {
            thrown = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
        var back = thrown is null ? (await stores.Store<MarkedJsonHolder>(Table).OneAsync(stores.Id("s"), Ct))!.Doc : null;
        var row = (await stores.RawRowsAsync<MarkedJsonHolder>(Table)).SingleOrDefault(r => r.RowKey == "s");
        var cells = row is null ? "" : string.Join(", ", row.Keys.Where(k => k.StartsWith("Doc", StringComparison.Ordinal)).Select(k => $"{k}({(row[k] as string)?.Length})"));
        Assert.True(controlBack.Body == Large && (thrown is not null || back?.Body == Large),
            $"{kind}: aware={TypeMetadataCache.IsLargeValueAware(typeof(MarkedJsonHolder))}; control (base instance, JSON) read back {controlBack.Body?.Length}; " +
            $"subclass, TrimSilently: write {(thrown is null ? "accepted and reported as written" : thrown.GetType().Name)}; read back " +
            $"{back?.GetType().Name} Body {back?.Body?.Length.ToString(CultureInfo.InvariantCulture) ?? "null"} of {Large.Length}; stored cells: [{cells}]");
    }
}
