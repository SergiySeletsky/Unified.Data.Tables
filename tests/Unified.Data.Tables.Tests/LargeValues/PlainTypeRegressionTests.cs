using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A type that opts into nothing (no <c>[TableValueSplit]</c>, no <see cref="TableBlobContent"/>)
/// behaves as it did on the published 0.8.3: the store constructs, the same cells are written with the
/// same SDK calls, and the same values are read.
/// </summary>
/// <remarks>
/// Every expected cell, SDK call and exception type below was captured from the NuGet package
/// <c>Unified.Data.Tables</c> 0.8.3 (and <c>Unified.Data.Tables.InMemory</c> 0.8.3) with a throwaway
/// console project over these same model types; none is derived from the current code.
/// </remarks>
// The DI test registers stores, which writes the process-wide TableEntitySerializer.OversizedCellPolicy.
[Collection("OversizedCellPolicy")]
public class PlainTypeRegressionTests
{
    private const string Pk = "p";
    private const string Rk = "r";
    private const string Id = "p|r";
    private const string SampleJson = """{"a":1,"b":[true,null,"x"],"c":{"d":"é"}}""";

    private static readonly DateTimeOffset Stamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static JsonNode Sample() => JsonNode.Parse(SampleJson)!;

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static T Stamped<T>(T entity)
        where T : IEntity
    {
        entity.Id = Id;
        entity.CreatedAt = Stamp;
        entity.UpdatedAt = Stamp;
        return entity;
    }

    // The whole row 0.8.3 writes: the keys, the Entity columns and the type's own cells.
    private static Dictionary<string, object> Row(params (string Name, object Value)[] own)
    {
        var cells = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["PartitionKey"] = Pk,
            ["RowKey"] = Rk,
            ["Id"] = Id,
            ["CreatedAt"] = Stamp,
            ["UpdatedAt"] = Stamp,
        };
        foreach (var (name, value) in own)
            cells[name] = value;
        return cells;
    }

    // Same column names, same CLR types (so the same EDM types on the wire) and same values. A row a
    // store wrote carries the store's own CreatedAt/UpdatedAt stamps, so only their type is compared.
    private static void AssertCells(Dictionary<string, object> expected, TableEntity row, bool stamped)
    {
        var columns = row.Keys.Where(k => k is not ("odata.etag" or "Timestamp")).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), columns);
        foreach (var (name, value) in expected)
        {
            Assert.IsType(value.GetType(), row[name]);
            if (stamped && name is "CreatedAt" or "UpdatedAt")
                continue;
            Assert.Equal(value, row[name]);
        }
    }

    private static void AssertJson(string expectedJson, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), actual), $"Expected {expectedJson}, got {actual?.ToJsonString() ?? "null"}.");

    private static string[] CallNames(TableClient table) =>
        table.ReceivedCalls().Select(c => c.GetMethodInfo().Name).ToArray();

    /// <summary>
    /// Runs one plain entity through the serializer, a <see cref="TableStorage{T}"/> over a mocked
    /// <see cref="TableClient"/> and an <see cref="InMemoryStorage{T}"/>, and requires the cells 0.8.3
    /// writes, the SDK calls 0.8.3 makes and the values 0.8.3 reads in each.
    /// </summary>
    private static async Task AssertStoredAs083<T>(
        Func<T> make,
        (string Name, object Value)[] cells,
        Action<T> read,
        Action<T>? change = null,
        Action<T>? changed = null,
        Action<T>? cached = null)
        where T : class, IEntity, new()
    {
        var expected = Row(cells);

        // The serializer.
        var row = make().ToTableEntity(Pk, Rk);
        AssertCells(expected, row, stamped: false);
        read(row.FromTableEntity<T>());

        // TableStorage<T>: constructs, and each write is the single SDK call of 0.8.3 with the same cells.
        using (var harness = new StorageHarness<T>())
        {
            var written = new List<TableEntity>();
            harness.Table.AddEntityAsync(Arg.Do<TableEntity>(written.Add), Arg.Any<CancellationToken>())
                .Returns(Mocks.EtagResponse());
            harness.Table.UpsertEntityAsync(Arg.Do<TableEntity>(written.Add), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
                .Returns(Mocks.EtagResponse());
            harness.Table.UpdateEntityAsync(Arg.Do<TableEntity>(written.Add), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
                .Returns(Mocks.EtagResponse());

            await harness.Store.CreateAsync(make());
            await harness.Store.UpsertAsync(make());
            var versioned = make();
            versioned.ETag = "W/\"v1\"";
            await harness.Store.UpdateAsync(versioned);

            Assert.Equal(["CreateIfNotExistsAsync", "AddEntityAsync", "UpsertEntityAsync", "UpdateEntityAsync"], CallNames(harness.Table));
            Assert.Equal(3, written.Count);
            Assert.All(written, w => AssertCells(expected, w, stamped: true));
        }

        // TableStorage<T>: one point read, then the entity cache (a clone of what was read).
        using (var harness = new StorageHarness<T>())
        {
            var stored = make().ToTableEntity(Pk, Rk);
            stored.ETag = new ETag("W/\"etag1\"");
            harness.SetupGet(stored);

            read((await harness.Store.OneAsync(Id))!);
            (cached ?? read)((await harness.Store.OneAsync(Id))!);

            Assert.Equal(["CreateIfNotExistsAsync", "GetEntityIfExistsAsync"], CallNames(harness.Table));
        }

        // InMemoryStorage<T>: create, read, update, upsert.
        var memory = new InMemoryStorage<T>();
        await memory.CreateAsync(make());
        AssertCells(expected, memory.PhysicalRow(Pk, Rk)!, stamped: true);
        var loaded = (await memory.OneAsync(Id))!;
        read(loaded);
        if (change is not null)
        {
            change(loaded);
            await memory.UpdateAsync(loaded);
            changed!((await memory.OneAsync(Id))!);
        }
        await memory.UpsertAsync(make());
        AssertCells(expected, memory.PhysicalRow(Pk, Rk)!, stamped: true);
        read((await memory.OneAsync(Id))!);
        Assert.Equal(1, memory.PhysicalRowCount);
    }

    /// <summary>
    /// A value 0.8.3 could not write still fails the same way, before anything is sent: the stores
    /// construct (as on 0.8.3) and only the write of that value throws.
    /// </summary>
    private static async Task AssertWriteFailsAs083<T, TException>(Func<T> make, string? message = null)
        where T : class, IEntity, new()
        where TException : Exception
    {
        var thrown = Assert.Throws<TException>(() => make().ToTableEntity(Pk, Rk));
        if (message is not null)
            Assert.Contains(message, thrown.Message, StringComparison.Ordinal);

        using var harness = new StorageHarness<T>();
        harness.SetupAdd();
        await Assert.ThrowsAsync<TException>(() => harness.Store.CreateAsync(make()));
        Assert.Equal(["CreateIfNotExistsAsync"], CallNames(harness.Table));

        var memory = new InMemoryStorage<T>();
        await Assert.ThrowsAsync<TException>(() => memory.CreateAsync(make()));
        Assert.Equal(0, memory.PhysicalRowCount);
    }

    // ── System.Text.Json DOM and element members ─────────────────────────────

    [Fact]
    public async Task JsonNodeProperty_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainJsonNodeDoc { Payload = Sample(), Title = "t" }),
            [("Payload__Json", SampleJson), ("Title", "t")],
            read =>
            {
                Assert.IsType<JsonObject>(read.Payload);
                AssertJson(SampleJson, read.Payload);
                Assert.Equal("t", read.Title);
            },
            change => change.Payload = JsonNode.Parse("[1,2]"),
            changed =>
            {
                Assert.IsType<JsonArray>(changed.Payload);
                AssertJson("[1,2]", changed.Payload);
                Assert.Equal("t", changed.Title);
            });
    }

    [Theory]
    [InlineData("""[1,"two",null]""")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("true")]
    public async Task JsonNodeProperty_HoldingAnArrayOrAValue_IsStoredAs083(string json)
    {
        await AssertStoredAs083(
            () => Stamped(new PlainJsonNodeDoc { Payload = JsonNode.Parse(json) }),
            [("Payload__Json", json)],
            read => AssertJson(json, read.Payload));
    }

    [Fact]
    public async Task JsonNodeProperty_WhenNull_WritesNoCell()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainJsonNodeDoc { Title = "t" }),
            [("Title", "t")],
            read =>
            {
                Assert.Null(read.Payload);
                Assert.Equal("t", read.Title);
            },
            change => change.Payload = JsonValue.Create(5),
            changed => AssertJson("5", changed.Payload));
    }

    [Fact]
    public async Task JsonObjectProperty_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainJsonObjectDoc { Payload = new JsonObject { ["a"] = 1, ["b"] = "x" } }),
            [("Payload__Json", """{"a":1,"b":"x"}""")],
            read => AssertJson("""{"a":1,"b":"x"}""", read.Payload),
            change => change.Payload!["z"] = true,
            changed => AssertJson("""{"a":1,"b":"x","z":true}""", changed.Payload));
    }

    [Fact]
    public async Task JsonArrayProperty_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainJsonArrayDoc { Payload = new JsonArray(1, "two", null) }),
            [("Payload__Json", """[1,"two",null]""")],
            read => AssertJson("""[1,"two",null]""", read.Payload),
            change => change.Payload!.Add(3),
            changed => AssertJson("""[1,"two",null,3]""", changed.Payload));
    }

    [Fact]
    public async Task JsonValueProperty_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainJsonValueDoc { Payload = JsonValue.Create(42) }),
            [("Payload__Json", "42")],
            read => Assert.Equal(42, read.Payload!.GetValue<int>()),
            change => change.Payload = JsonValue.Create("s"),
            changed => Assert.Equal("s", changed.Payload!.GetValue<string>()));

        await AssertStoredAs083(
            () => Stamped(new PlainJsonValueDoc { Payload = JsonValue.Create("text") }),
            [("Payload__Json", "\"text\"")],
            read => Assert.Equal("text", read.Payload!.GetValue<string>()));

        await AssertStoredAs083(
            () => Stamped(new PlainJsonValueDoc { Payload = JsonValue.Create(true) }),
            [("Payload__Json", "true")],
            read => Assert.True(read.Payload!.GetValue<bool>()));

        await AssertStoredAs083(
            () => Stamped(new PlainJsonValueDoc()),
            [],
            read => Assert.Null(read.Payload));
    }

    [Fact]
    public async Task JsonElementProperty_IsStoredAs083()
    {
        const string json = """{"a":1,"b":[true,null,"x"]}""";
        await AssertStoredAs083(
            () => Stamped(new PlainJsonElementDoc { Payload = Element(json) }),
            [("Payload__Json", json)],
            read =>
            {
                Assert.Equal(JsonValueKind.Object, read.Payload.ValueKind);
                Assert.Equal(json, read.Payload.GetRawText());
            },
            change => change.Payload = Element("[1]"),
            changed => Assert.Equal("[1]", changed.Payload.GetRawText()));

        // An unset JsonElement has no JSON form: 0.8.3 refuses the write.
        await AssertWriteFailsAs083<PlainJsonElementDoc, InvalidOperationException>(() => Stamped(new PlainJsonElementDoc()));
    }

    [Fact]
    public async Task NullableJsonElementProperty_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainNullableJsonElementDoc { Payload = Element("""["x",2]""") }),
            [("Payload__Json", """["x",2]""")],
            read => Assert.Equal("""["x",2]""", read.Payload!.Value.GetRawText()),
            change => change.Payload = null,
            changed => Assert.Null(changed.Payload));

        await AssertStoredAs083(
            () => Stamped(new PlainNullableJsonElementDoc()),
            [],
            read => Assert.Null(read.Payload));
    }

    [Fact]
    public async Task NestedObjectWithAJsonNode_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainNestedJsonDoc { Inner = new PlainJsonHolder { Name = "n", Data = Sample() } }),
            [("Inner_Name", "n"), ("Inner_Data__Json", SampleJson)],
            read =>
            {
                Assert.Equal("n", read.Inner!.Name);
                AssertJson(SampleJson, read.Inner.Data);
            },
            change => change.Inner!.Data = JsonNode.Parse("7"),
            changed =>
            {
                Assert.Equal("n", changed.Inner!.Name);
                AssertJson("7", changed.Inner.Data);
            });

        await AssertStoredAs083(
            () => Stamped(new PlainNestedJsonDoc { Inner = new PlainJsonHolder { Name = "n" } }),
            [("Inner_Name", "n")],
            read =>
            {
                Assert.Equal("n", read.Inner!.Name);
                Assert.Null(read.Inner.Data);
            });

        await AssertStoredAs083(
            () => Stamped(new PlainNestedJsonDoc()),
            [],
            read => Assert.Null(read.Inner));
    }

    [Fact]
    public async Task DictionaryOfJsonNode_IsStoredAs083()
    {
        const string json = """{"k":{"a":1,"b":[true,null,"x"],"c":{"d":"é"}},"n":5}""";
        await AssertStoredAs083(
            () => Stamped(new PlainJsonDictionaryDoc { Map = new Dictionary<string, JsonNode> { ["k"] = Sample(), ["n"] = JsonValue.Create(5) } }),
            [("Map__Json", json)],
            read =>
            {
                Assert.Equal(["k", "n"], read.Map!.Keys.OrderBy(k => k, StringComparer.Ordinal));
                AssertJson(SampleJson, read.Map["k"]);
                AssertJson("5", read.Map["n"]);
            },
            change => change.Map!["z"] = JsonValue.Create("zz"),
            changed =>
            {
                Assert.Equal(3, changed.Map!.Count);
                AssertJson("\"zz\"", changed.Map["z"]);
            });
    }

    [Fact]
    public async Task ListOfJsonNode_IsStoredAs083()
    {
        const string json = """[{"a":1,"b":[true,null,"x"],"c":{"d":"é"}},5,[1]]""";
        await AssertStoredAs083(
            () => Stamped(new PlainJsonListDoc { Items = [Sample(), JsonValue.Create(5), JsonNode.Parse("[1]")!] }),
            [("Items__Json", json)],
            read =>
            {
                Assert.Equal(3, read.Items!.Count);
                AssertJson(SampleJson, read.Items[0]);
                AssertJson("5", read.Items[1]);
                AssertJson("[1]", read.Items[2]);
            },
            change => change.Items!.Add(JsonValue.Create(9)),
            changed =>
            {
                Assert.Equal(4, changed.Items!.Count);
                AssertJson("9", changed.Items[3]);
            });
    }

    // ── Indexers ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ObjectWithAGetOnlyIndexer_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainReadOnlyIndexerDoc { Bag = new PlainReadOnlyIndexed { Label = "abc" } }),
            [("Bag_Label", "abc")],
            read => Assert.Equal("abc", read.Bag!.Label),
            change => change.Bag!.Label = "xyz",
            changed => Assert.Equal("xyz", changed.Bag!.Label));
    }

    [Fact]
    public async Task ObjectWithAReadWriteIndexer_WhenNull_IsStoredAs083_AndAValueFailsAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainIndexerDoc()),
            [],
            read => Assert.Null(read.Bag));

        // 0.8.3 reads the indexer as a property and fails; it never stored such a value.
        await AssertWriteFailsAs083<PlainIndexerDoc, TargetParameterCountException>(
            () => Stamped(new PlainIndexerDoc { Bag = new PlainIndexed { Label = "abc" } }));
    }

    [Fact]
    public async Task ObjectWithTwoIndexers_WhenNull_IsStoredAs083_AndAValueFailsAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainTwoIndexerDoc()),
            [],
            read => Assert.Null(read.Bag));

        await AssertWriteFailsAs083<PlainTwoIndexerDoc, ArgumentException>(
            () => Stamped(new PlainTwoIndexerDoc { Bag = new PlainTwoIndexed { Label = "abc" } }), "Key: Item");
    }

    [Fact]
    public async Task InterfaceThatDeclaresTwoIndexers_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainLookupDoc { Lookup = new PlainLookup { Label = "abc" } }),
            [("Lookup_Label", "abc")],
            read => Assert.Equal("abc", Assert.IsType<PlainLookup>(read.Lookup).Label),
            change => change.Lookup!.Label = "xyz",
            changed => Assert.Equal("xyz", changed.Lookup!.Label));
    }

    // ── Hidden (new) properties ──────────────────────────────────────────────

    [Fact]
    public async Task ObjectHidingABasePropertyWithTheSameType_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainHiddenSameTypeDoc { Thing = new PlainHiddenSameType { Tag = "derived", Rank = 3 } }),
            [("Thing_Tag", "derived"), ("Thing_Rank", 3)],
            read =>
            {
                Assert.Equal("derived", read.Thing!.Tag);
                Assert.Null(((PlainHiddenBase)read.Thing).Tag);
                Assert.Equal(3, read.Thing.Rank);
            },
            change => change.Thing!.Tag = "changed",
            changed => Assert.Equal("changed", changed.Thing!.Tag));
    }

    [Fact]
    public async Task ObjectHidingABasePropertyWithAnotherType_WhenNull_IsStoredAs083_AndAValueFailsAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainHiddenOtherTypeDoc()),
            [],
            read => Assert.Null(read.Thing));

        await AssertWriteFailsAs083<PlainHiddenOtherTypeDoc, ArgumentException>(
            () => Stamped(new PlainHiddenOtherTypeDoc { Thing = new PlainHiddenOtherType { Tag = 7, Rank = 3 } }), "Key: Tag");
    }

    [Fact]
    public async Task EntityHidingABasePropertyWithAnotherType_StoresConstruct_AndWritesFailAs083()
    {
        // 0.8.3 never stored this type (two properties named Rank), but its stores construct, and the
        // operations that do not serialize (exists, delete) work.
        await AssertWriteFailsAs083<PlainHidingEntity, ArgumentException>(() => Stamped(new PlainHidingEntity { Rank = 5 }), "Key: Rank");

        var memory = new InMemoryStorage<PlainHidingEntity>();
        Assert.False(await memory.ExistsAsync(Id));
        await memory.DeleteAsync(Id);
        Assert.Equal(0, await memory.CountAsync());
    }

    [Fact]
    public async Task EntityWithAnInvalidColumnAlias_StoresConstruct_AndWritesFailAs083()
    {
        // The metadata of this type cannot be built. 0.8.3 says so on the first read or write, with the
        // alias message; the stores construct and the operations that do not serialize work.
        await AssertWriteFailsAs083<PlainBadAliasDoc, InvalidOperationException>(
            () => Stamped(new PlainBadAliasDoc { Name = "n" }),
            "Class-level [ColumnAlias] on PlainBadAliasDoc must use the (propertyName, legacyColumnName) constructor.");

        var memory = new InMemoryStorage<PlainBadAliasDoc>();
        Assert.False(await memory.ExistsAsync(Id));
        await memory.DeleteAsync(Id);
        Assert.Equal(0, await memory.CountAsync());
    }

    // ── Self-referencing types ───────────────────────────────────────────────

    [Fact]
    public async Task SelfReferencingNestedType_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainTreeDoc
            {
                Root = new PlainTreeNode { Name = "a", Next = new PlainTreeNode { Name = "b", Next = new PlainTreeNode { Name = "c" } } },
            }),
            [("Root_Name", "a"), ("Root_Next_Name", "b"), ("Root_Next_Next_Name", "c")],
            read =>
            {
                Assert.Equal("a", read.Root!.Name);
                Assert.Equal("b", read.Root.Next!.Name);
                Assert.Equal("c", read.Root.Next.Next!.Name);
                Assert.Null(read.Root.Next.Next.Next);
            },
            change => change.Root!.Next!.Next = null,
            changed =>
            {
                Assert.Equal("b", changed.Root!.Next!.Name);
                Assert.Null(changed.Root.Next.Next);
            });
    }

    [Fact]
    public async Task SelfReferencingEntity_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainLinkedDoc { Name = "a", Next = Stamped(new PlainLinkedDoc { Name = "b" }) }),
            [("Name", "a"), ("Next_Id", Id), ("Next_CreatedAt", Stamp), ("Next_UpdatedAt", Stamp), ("Next_Name", "b")],
            read =>
            {
                Assert.Equal("a", read.Name);
                Assert.Equal("b", read.Next!.Name);
                Assert.Equal(Id, read.Next.Id);
                Assert.Equal(Stamp, read.Next.CreatedAt);
                Assert.Null(read.Next.Next);
            },
            change => change.Next = null,
            changed =>
            {
                Assert.Equal("a", changed.Name);
                Assert.Null(changed.Next);
            });

        // An object that reaches itself is refused, as on 0.8.3.
        await AssertWriteFailsAs083<PlainLinkedDoc, SerializationException>(
            () =>
            {
                var self = Stamped(new PlainLinkedDoc { Name = "a" });
                self.Next = self;
                return self;
            },
            "Circular reference");
    }

    // ── object, Type and delegate members ────────────────────────────────────

    [Fact]
    public async Task ObjectTypedProperty_IsStoredAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainObjectDoc { Value = "text" }),
            [("Value", "text")],
            read => Assert.Equal("text", Assert.IsType<string>(read.Value)),
            change => change.Value = 7,
            changed => Assert.Equal(7, Assert.IsType<int>(changed.Value)));

        await AssertStoredAs083(
            () => Stamped(new PlainObjectDoc { Value = 42 }),
            [("Value", 42)],
            read => Assert.Equal(42, Assert.IsType<int>(read.Value)));

        await AssertStoredAs083(
            () => Stamped(new PlainObjectDoc { Value = 42L }),
            [("Value", 42L)],
            read => Assert.Equal(42L, Assert.IsType<long>(read.Value)));

        await AssertStoredAs083(
            () => Stamped(new PlainObjectDoc { Value = true }),
            [("Value", true)],
            read => Assert.True(Assert.IsType<bool>(read.Value)));

        await AssertStoredAs083(
            () => Stamped(new PlainObjectDoc { Value = 1.5 }),
            [("Value", 1.5)],
            read => Assert.Equal(1.5, Assert.IsType<double>(read.Value)));

        // A collection is one JSON cell; with no declared type to read into it comes back as a JsonElement.
        await AssertStoredAs083(
            () => Stamped(new PlainObjectDoc { Value = new List<int> { 1, 2 } }),
            [("Value__Json", "[1,2]")],
            read => Assert.Equal("[1,2]", Assert.IsType<JsonElement>(read.Value).GetRawText()));

        // An object that flattens writes Value_* columns, which a property typed object cannot read
        // back: the read leaves a bare object there, and the entity cache's copy of it has none.
        await AssertStoredAs083(
            () => Stamped(new PlainObjectDoc { Value = new PlainJsonHolder { Name = "n" } }),
            [("Value_Name", "n")],
            read => Assert.IsType<object>(read.Value),
            cached: copy => Assert.Null(copy.Value));

        await AssertStoredAs083(
            () => Stamped(new PlainObjectDoc()),
            [],
            read => Assert.Null(read.Value));
    }

    [Fact]
    public async Task TypeTypedProperty_WhenNull_IsStoredAs083_AndAValueFailsAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainTypeDoc { Title = "t" }),
            [("Title", "t")],
            read =>
            {
                Assert.Null(read.Kind);
                Assert.Equal("t", read.Title);
            },
            change => change.Title = "t2",
            changed => Assert.Equal("t2", changed.Title));

        // 0.8.3 does not store a System.Type: its JSON fallback refuses it.
        await AssertWriteFailsAs083<PlainTypeDoc, NotSupportedException>(
            () => Stamped(new PlainTypeDoc { Kind = typeof(string), Title = "t" }));
    }

    [Fact]
    public async Task DelegateTypedProperty_WhenNull_IsStoredAs083_AndAValueFailsAs083()
    {
        await AssertStoredAs083(
            () => Stamped(new PlainDelegateDoc { Title = "t" }),
            [("Title", "t")],
            read =>
            {
                Assert.Null(read.Compute);
                Assert.Equal("t", read.Title);
            },
            change => change.Title = "t2",
            changed => Assert.Equal("t2", changed.Title));

        // 0.8.3 does not store a delegate: its JSON fallback refuses it.
        await AssertWriteFailsAs083<PlainDelegateDoc, NotSupportedException>(
            () => Stamped(new PlainDelegateDoc { Compute = () => 1, Title = "t" }));
    }

    // ── Polymorphic stores (the CarriesBlobContent check before every write) ──

    [Fact]
    public async Task PolymorphicWrite_OfAnItemWithAJsonNode_IsStoredAs083()
    {
        var key = new TableKey(Pk, Rk);
        var bare = new TableKey(Pk, "r2");
        var typeName = typeof(PlainJsonEvent).AssemblyQualifiedName!;

        var memory = new InMemoryPolymorphicStorage<PlainEventBase>("Events");
        await memory.InsertAsync(new PolymorphicWrite<PlainEventBase>(key, new PlainJsonEvent { Name = "evt", Payload = Sample() }));
        var entry = (await memory.GetAsync(key))!;
        Assert.Equal(["Name", "Payload__Json", "_TypeName"], entry.Columns.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("evt", entry.Columns["Name"]);
        Assert.Equal(SampleJson, entry.Columns["Payload__Json"]);
        Assert.Equal(typeName, entry.Columns["_TypeName"]);
        var item = Assert.IsType<PlainJsonEvent>(entry.Value);
        Assert.Equal("evt", item.Name);
        AssertJson(SampleJson, item.Payload);

        var upserted = await memory.UpsertAsync(new PolymorphicWrite<PlainEventBase>(bare, new PlainJsonEvent { Name = "e2" }));
        Assert.Equal(["Name", "_TypeName"], upserted.Columns.Keys.OrderBy(k => k, StringComparer.Ordinal));

        using var harness = new PolymorphicHarness<PlainEventBase>("Events");
        harness.SetupAdd();
        harness.SetupUpsert();
        await harness.Store.InsertAsync(new PolymorphicWrite<PlainEventBase>(key, new PlainJsonEvent { Name = "evt", Payload = Sample() }));
        var written = harness.LastWrittenEntity!;
        Assert.Equal(
            ["Name", "PartitionKey", "Payload__Json", "RowKey", "_TypeName"],
            written.Keys.Where(k => k != "odata.etag").OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("evt", written["Name"]);
        Assert.Equal(SampleJson, written["Payload__Json"]);
        Assert.Equal(typeName, written["_TypeName"]);

        await harness.Store.UpsertAsync(new PolymorphicWrite<PlainEventBase>(bare, new PlainJsonEvent { Name = "e2" }));
        Assert.Equal(
            ["Name", "PartitionKey", "RowKey", "_TypeName"],
            harness.LastWrittenEntity!.Keys.Where(k => k != "odata.etag").OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(["CreateIfNotExistsAsync", "AddEntityAsync", "UpsertEntityAsync"], CallNames(harness.Table));

        var stored = new PlainJsonEvent { Name = "evt", Payload = Sample() }.ToTableEntity(Pk, Rk);
        stored["_TypeName"] = typeName;
        stored.ETag = new ETag("W/\"etag1\"");
        harness.SetupGet(stored);
        var read = Assert.IsType<PlainJsonEvent>((await harness.Store.GetAsync(key))!.Value);
        AssertJson(SampleJson, read.Payload);
    }

    // ── The SDK calls of a plain store ───────────────────────────────────────

    // One argument of an SDK call, as the 0.8.3 probe printed it.
    private static string FormatArgument(object? argument) => argument switch
    {
        null => "null",
        TableEntity e => "TableEntity{" + string.Join(",", e.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => k is "UpdatedAt" or "CreatedAt" ? k : k + "=" + e[k])) + "}",
        string s => "\"" + s + "\"",
        CancellationToken => "ct",
        Expression => "expr",
        IEnumerable<string> select => "[" + string.Join(",", select) + "]",
        IEnumerable<TableTransactionAction> actions => "actions[" + string.Join(",", actions.Select(a => a.ActionType + ":" + a.Entity.RowKey + ":" + a.ETag)) + "]",
        _ => argument.ToString() ?? string.Empty,
    };

    // Every IStorage<T> operation against a TableClient mocked as StorageHarness mocks it, one line per
    // operation: the SDK calls it made, in order, with their arguments.
    private static async Task<List<string>> RecordSdkCalls<T>(Func<T> make, params Action<UpdateBuilder<T>>[] patches)
        where T : class, IEntity, new()
    {
        using var harness = new StorageHarness<T>();
        harness.SetupAdd();
        harness.SetupUpsert();
        harness.SetupUpdate();
        harness.SetupDelete();
        harness.SetupGet(null);
        harness.SetupTransaction();
        harness.SetupQueryByPartition();
        harness.SetupQueryByFilter();
        var lines = new List<string>();

        async Task Step(string name, Func<Task> operation)
        {
            harness.Table.ClearReceivedCalls();
            await operation();
            var calls = harness.Table.ReceivedCalls()
                .Select(c => c.GetMethodInfo().Name + "(" + string.Join(", ", c.GetArguments().Select(FormatArgument)) + ")");
            lines.Add(name + ": " + string.Join(" ; ", calls));
        }

        await Step("CreateAsync", () => harness.Store.CreateAsync(make()));
        await Step("UpsertAsync", () => harness.Store.UpsertAsync(make()));
        await Step("UpdateAsync(LastWriterWins)", () => harness.Store.UpdateAsync(make(), ConcurrencyMode.LastWriterWins));
        await Step("UpdateAsync(ETag)", () =>
        {
            var entity = make();
            entity.ETag = "W/\"v1\"";
            return harness.Store.UpdateAsync(entity);
        });
        await Step("OneAsync", () => harness.Store.OneAsync("p|other"));
        await Step("ExistsAsync", () => harness.Store.ExistsAsync("p|other"));
        await Step("DeleteAsync", () => harness.Store.DeleteAsync(Id));
        await Step("CreateBatchAsync", () => harness.Store.CreateBatchAsync([make()]));
        await Step("UpsertBatchAsync", () => harness.Store.UpsertBatchAsync([make()]));
        foreach (var patch in patches)
            await Step("UpdateAsync(builder)", () => harness.Store.UpdateAsync(Id, patch));
        await Step("QueryAsync(partition)", () => harness.Store.QueryAsync(Pk));
        await Step("QueryAsync(options)", () => harness.Store.QueryAsync(new QueryOptions { Partition = Pk, RowKeyPrefix = "r", Take = 5 }));
        await Step("QueryAsync(select)", () => harness.Store.QueryAsync(new QueryOptions { Partition = Pk, Select = ["Id"] }));
        await Step("QueryPageAsync", () => harness.Store.QueryPageAsync(new QueryOptions { Partition = Pk, Take = 5 }));
        await Step("QueryAsync(predicate)", () => harness.Store.QueryAsync(x => x.Id == Id, Pk));
        await Step("AnyAsync", () => harness.Store.AnyAsync(x => x.Id == Id, Pk));
        await Step("CountAsync", () => harness.Store.CountAsync(Pk));
        await Step("DeletePartitionAsync", () => harness.Store.DeletePartitionAsync(Pk));
        return lines;
    }

    // What 0.8.3 sends for an entity whose serialized cells are `cells` and whose builder updates merge `patches`.
    // One line differs from 0.8.3 on purpose: a projected query no longer names odata.etag in $select
    // (0.8.3 sent [PartitionKey,RowKey,Timestamp,odata.etag,_TypeName,Id]). The service returns that
    // annotation with every entity whatever $select names, so the rows read back are the same.
    private static string[] SdkCallsOf083(string cells, params string[] patches) =>
    [
        $"CreateAsync: CreateIfNotExistsAsync(ct) ; AddEntityAsync(TableEntity{{{cells}}}, ct)",
        $"UpsertAsync: UpsertEntityAsync(TableEntity{{{cells}}}, Replace, ct)",
        $"UpdateAsync(LastWriterWins): UpdateEntityAsync(TableEntity{{{cells}}}, *, Replace, ct)",
        $"UpdateAsync(ETag): UpdateEntityAsync(TableEntity{{{cells}}}, W/\"v1\", Replace, ct)",
        "OneAsync: GetEntityIfExistsAsync(\"p\", \"other\", null, ct)",
        "ExistsAsync: GetEntityIfExistsAsync(\"p\", \"other\", null, ct)",
        "DeleteAsync: DeleteEntityAsync(\"p\", \"r\", , ct)",
        "CreateBatchAsync: SubmitTransactionAsync(actions[Add:r:*], ct)",
        "UpsertBatchAsync: SubmitTransactionAsync(actions[UpsertReplace:r:*], ct)",
        .. patches.Select(p => $"UpdateAsync(builder): UpdateEntityAsync(TableEntity{{{p}}}, *, Merge, ct)"),
        "QueryAsync(partition): QueryAsync(expr, null, null, ct)",
        "QueryAsync(options): QueryAsync(\"PartitionKey eq 'p' and RowKey ge 'r' and RowKey lt 's'\", 5, null, ct)",
        "QueryAsync(select): QueryAsync(\"PartitionKey eq 'p'\", null, [PartitionKey,RowKey,Timestamp,_TypeName,Id], ct)",
        "QueryPageAsync: QueryAsync(\"PartitionKey eq 'p'\", 5, null, ct)",
        "QueryAsync(predicate): QueryAsync(\"PartitionKey eq 'p' and ((PartitionKey eq 'p' and RowKey eq 'r'))\", null, null, ct)",
        "AnyAsync: QueryAsync(\"PartitionKey eq 'p' and ((PartitionKey eq 'p' and RowKey eq 'r'))\", 1, null, ct)",
        "CountAsync: QueryAsync(\"PartitionKey eq 'p'\", null, [PartitionKey], ct)",
        "DeletePartitionAsync: QueryAsync(expr, null, [PartitionKey,RowKey], ct)",
    ];

    [Fact]
    public async Task StoreOfAJsonNodeDoc_MakesTheSdkCallsOf083()
    {
        var calls = await RecordSdkCalls(
            () => Stamped(new PlainJsonNodeDoc { Payload = Sample(), Title = "t" }),
            b => b.SetProperty(x => x.Title, "t2"),
            b => b.SetProperty(x => x.Payload, JsonNode.Parse("[1]")));

        Assert.Equal(
            SdkCallsOf083(
                $"CreatedAt,Id=p|r,PartitionKey=p,Payload__Json={SampleJson},RowKey=r,Title=t,UpdatedAt",
                "PartitionKey=p,RowKey=r,Title=t2,UpdatedAt",
                "PartitionKey=p,Payload__Json=[1],RowKey=r,UpdatedAt"),
            calls);
    }

    [Fact]
    public async Task StoreOfANestedJsonDoc_MakesTheSdkCallsOf083()
    {
        var calls = await RecordSdkCalls(
            () => Stamped(new PlainNestedJsonDoc { Inner = new PlainJsonHolder { Name = "n", Data = Sample() } }),
            b => b.SetProperty(x => x.Inner!.Name, "n2"),
            b => b.SetProperty(x => x.Inner!.Data, JsonNode.Parse("[1]")));

        Assert.Equal(
            SdkCallsOf083(
                $"CreatedAt,Id=p|r,Inner_Data__Json={SampleJson},Inner_Name=n,PartitionKey=p,RowKey=r,UpdatedAt",
                "Inner_Name=n2,PartitionKey=p,RowKey=r,UpdatedAt",
                "Inner_Data__Json=[1],PartitionKey=p,RowKey=r,UpdatedAt"),
            calls);
    }

    [Fact]
    public async Task StoreOfADocWithAnIndexedInterface_MakesTheSdkCallsOf083()
    {
        // The builder update walks the declared type of Lookup (an interface with two indexers).
        var calls = await RecordSdkCalls(
            () => Stamped(new PlainLookupDoc { Lookup = new PlainLookup { Label = "abc" } }),
            b => b.SetProperty(x => x.Lookup!.Label, "v2"));

        Assert.Equal(
            SdkCallsOf083(
                "CreatedAt,Id=p|r,Lookup_Label=abc,PartitionKey=p,RowKey=r,UpdatedAt",
                "Lookup_Label=v2,PartitionKey=p,RowKey=r,UpdatedAt"),
            calls);
    }

    [Fact]
    public async Task StoreOfASelfReferencingDoc_MakesTheSdkCallsOf083()
    {
        var calls = await RecordSdkCalls(
            () => Stamped(new PlainTreeDoc { Root = new PlainTreeNode { Name = "a" } }),
            b => b.SetProperty(x => x.Root!.Next!.Name, "deep"));

        Assert.Equal(
            SdkCallsOf083(
                "CreatedAt,Id=p|r,PartitionKey=p,Root_Name=a,RowKey=r,UpdatedAt",
                "PartitionKey=p,Root_Next_Name=deep,RowKey=r,UpdatedAt"),
            calls);
    }

    [Fact]
    public async Task InMemoryBuilderUpdate_ThroughDeclaredTypes_BehavesAs083()
    {
        var lookups = new InMemoryStorage<PlainLookupDoc>();
        await lookups.CreateAsync(Stamped(new PlainLookupDoc { Lookup = new PlainLookup { Label = "abc" } }));
        await lookups.UpdateAsync(Id, b => b.SetProperty(x => x.Lookup!.Label, "v2"));
        Assert.Equal("v2", (await lookups.OneAsync(Id))!.Lookup!.Label);

        var nested = new InMemoryStorage<PlainNestedJsonDoc>();
        await nested.CreateAsync(Stamped(new PlainNestedJsonDoc { Inner = new PlainJsonHolder { Name = "n", Data = Sample() } }));
        await nested.UpdateAsync(Id, b => b.SetProperty(x => x.Inner!.Data, JsonNode.Parse("[1]")));
        var inner = (await nested.OneAsync(Id))!.Inner!;
        Assert.Equal("n", inner.Name);
        AssertJson("[1]", inner.Data);

        var nodes = new InMemoryStorage<PlainJsonNodeDoc>();
        await nodes.CreateAsync(Stamped(new PlainJsonNodeDoc { Payload = Sample(), Title = "t" }));
        await nodes.UpdateAsync(Id, b => b.SetProperty(x => x.Payload, JsonNode.Parse("[1]")));
        var node = (await nodes.OneAsync(Id))!;
        Assert.Equal("t", node.Title);
        AssertJson("[1]", node.Payload);
    }

    // ── Dependency injection ─────────────────────────────────────────────────

    [Fact]
    public void DependencyInjection_ResolvesStoresOfPlainTypes()
    {
        // Registration writes the options through to the process-wide serializer policy: put it back.
        var previousPolicy = TableEntitySerializer.OversizedCellPolicy;
        try
        {
            foreach (var withOffload in new[] { false, true })
            {
                var azure = new ServiceCollection();
                var service = Substitute.For<TableServiceClient>();
                service.GetTableClient(Arg.Any<string>()).Returns(Substitute.For<TableClient>());
                azure.AddSingleton(service);
                azure.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
                // Blob offload configured with no blob client: a plain type needs none.
                azure.AddUnifiedTableStorage(o => o.BlobOffload = withOffload ? new BlobOffloadOptions() : null);
                using var provider = azure.BuildServiceProvider();
                Assert.IsType<TableStorage<PlainJsonNodeDoc>>(provider.GetRequiredService<IStorage<PlainJsonNodeDoc>>());
                Assert.IsType<TableStorage<PlainJsonValueDoc>>(provider.GetRequiredService<IStorage<PlainJsonValueDoc>>());
                Assert.IsType<TableStorage<PlainNestedJsonDoc>>(provider.GetRequiredService<IStorage<PlainNestedJsonDoc>>());
                Assert.IsType<TableStorage<PlainHiddenOtherTypeDoc>>(provider.GetRequiredService<IStorage<PlainHiddenOtherTypeDoc>>());
            }

            var fake = new ServiceCollection();
            fake.AddUnifiedInMemoryStorage();
            using var fakes = fake.BuildServiceProvider();
            Assert.IsType<InMemoryStorage<PlainJsonNodeDoc>>(fakes.GetRequiredService<IStorage<PlainJsonNodeDoc>>());
            Assert.IsType<InMemoryStorage<PlainJsonValueDoc>>(fakes.GetRequiredService<IStorage<PlainJsonValueDoc>>());
            Assert.IsType<InMemoryStorage<PlainNestedJsonDoc>>(fakes.GetRequiredService<IStorage<PlainNestedJsonDoc>>());
            Assert.IsType<InMemoryStorage<PlainHiddenOtherTypeDoc>>(fakes.GetRequiredService<IStorage<PlainHiddenOtherTypeDoc>>());
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = previousPolicy;
        }
    }

    // ── Deciding whether a type is large-value aware ─────────────────────────

    public static TheoryData<Type> TypesThatOptIntoNothing() =>
    [
        typeof(PlainJsonNodeDoc), typeof(PlainJsonObjectDoc), typeof(PlainJsonArrayDoc), typeof(PlainJsonValueDoc),
        typeof(PlainJsonElementDoc), typeof(PlainNullableJsonElementDoc), typeof(PlainNestedJsonDoc),
        typeof(PlainJsonDictionaryDoc), typeof(PlainJsonListDoc), typeof(PlainReadOnlyIndexerDoc), typeof(PlainIndexerDoc),
        typeof(PlainTwoIndexerDoc), typeof(PlainLookupDoc), typeof(PlainHiddenSameTypeDoc), typeof(PlainHiddenOtherTypeDoc),
        typeof(PlainHidingEntity), typeof(PlainTreeDoc), typeof(PlainLinkedDoc), typeof(PlainObjectDoc), typeof(PlainTypeDoc),
        typeof(PlainDelegateDoc), typeof(PlainJsonEvent), typeof(PlainEventBase), typeof(PlainBadAliasDoc),
        // Not entity types at all: the answer is still "no", never an exception.
        typeof(JsonNode), typeof(JsonValue), typeof(JsonObject), typeof(JsonArray), typeof(JsonElement), typeof(JsonElement?),
        typeof(IPlainLookup), typeof(PlainTwoIndexed), typeof(PlainHiddenOtherType),
        typeof(object), typeof(string), typeof(int), typeof(int?), typeof(byte[]), typeof(Guid), typeof(DayOfWeek), typeof(void),
        typeof(Type), typeof(Delegate), typeof(Func<int>), typeof(Action), typeof(Exception), typeof(Uri), typeof(Version),
        typeof(List<JsonNode>), typeof(Dictionary<string, JsonNode>), typeof(List<>), typeof(Dictionary<,>), typeof(Nullable<>),
        typeof(int[]), typeof(int[,]), typeof(IEnumerable<string>), typeof(IDisposable), typeof(ValueTuple<int, string>),
        typeof(Tuple<int, string>), typeof(KeyValuePair<string, JsonNode>), typeof(Span<int>), typeof(int).MakePointerType(), typeof(int).MakeByRefType(),
        typeof(Lazy<JsonNode>), typeof(Task<JsonNode>), typeof(CancellationToken), typeof(DateTimeOffset), typeof(TimeSpan),
        typeof(Entity), typeof(IEntity), typeof(VersionedEntity), typeof(TableEntity), typeof(Stream), typeof(MemberInfo),
    ];

    [Theory]
    [MemberData(nameof(TypesThatOptIntoNothing))]
    public void TypeThatOptsIntoNothing_IsNotLargeValueAware_AndTheAnswerNeverThrows(Type type)
    {
        Assert.False(TypeMetadataCache.IsLargeValueAware(type));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(type));
        Assert.False(LargeValueTable.IsLargeValueAware(type));
    }

    private static Type[] LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partly)
        {
            return partly.Types.Where(t => t is not null).ToArray()!;
        }
    }

    [Fact]
    public void EveryFrameworkType_IsNotLargeValueAware_AndTheAnswerNeverThrows()
    {
        // "Any type": thousands of them, public or not, as nobody would write them in a test model
        // (open generics, pointers, by-ref-like structs, COM and runtime internals, types with several
        // indexers, hidden members, cyclic and deep graphs).
        Assembly[] assemblies =
        [
            typeof(object).Assembly, typeof(JsonNode).Assembly, typeof(Uri).Assembly, typeof(Queue<>).Assembly,
            typeof(Enumerable).Assembly, typeof(System.Net.Http.HttpClient).Assembly, typeof(TableEntity).Assembly,
            typeof(Response).Assembly, typeof(Azure.Storage.Blobs.BlobClient).Assembly,
        ];

        var checkedTypes = 0;
        foreach (var assembly in assemblies)
        {
            foreach (var type in LoadableTypes(assembly))
            {
                bool aware;
                try
                {
                    aware = TypeMetadataCache.IsLargeValueAware(type);
                }
                catch (Exception ex)
                {
                    Assert.Fail($"IsLargeValueAware({type}) threw {ex.GetType().Name}: {ex.Message}");
                    return;
                }
                Assert.False(aware, $"{type} is reported as large-value aware.");
                checkedTypes++;
            }
        }
        Assert.InRange(checkedTypes, 5_000, int.MaxValue);
    }

    [Fact]
    public void ItemThatOptsIntoNothing_CarriesNoBlobContent()
    {
        Assert.False(LargeValueRows.CarriesBlobContent(new PlainJsonEvent { Name = "evt", Payload = Sample() }));
        Assert.False(LargeValueRows.CarriesBlobContent(new PlainJsonNodeDoc { Payload = JsonValue.Create(1) }));
        Assert.False(LargeValueRows.CarriesBlobContent(new PlainHiddenOtherType { Tag = 1 }));
        Assert.False(LargeValueRows.CarriesBlobContent(new PlainTwoIndexed { Label = "x" }));
        Assert.False(LargeValueRows.CarriesBlobContent(Sample()));
        Assert.False(LargeValueRows.CarriesBlobContent("text"));
        Assert.False(LargeValueRows.CarriesBlobContent(typeof(string)));
    }

    // Types no other test touches, so what the serializer has cached for them is this test's doing.
    private sealed class UntouchedDoc : Entity
    {
        public UntouchedInner? Inner { get; set; }

        public IUntouchedShape? Shape { get; set; }

        public UntouchedBase? Base { get; set; }

        public UntouchedJsonStored? Stored { get; set; }

        public JsonNode? Node { get; set; }
    }

    private sealed class UntouchedInner
    {
        public string? Name { get; set; }

        public UntouchedLeaf? Leaf { get; set; }
    }

    private sealed class UntouchedLeaf
    {
        public string? Name { get; set; }
    }

    private interface IUntouchedShape
    {
        string? Name { get; set; }
    }

    private abstract class UntouchedBase
    {
        public string? Name { get; set; }
    }

    // Parameterized constructor only: stored as one JSON cell.
    private sealed class UntouchedJsonStored(string name)
    {
        public string Name { get; } = name;
    }

    [Fact]
    public void DecidingAwareness_BuildsNoFlatteningMetadata()
    {
        Type[] reachable =
        [
            typeof(UntouchedDoc), typeof(UntouchedInner), typeof(UntouchedLeaf), typeof(IUntouchedShape), typeof(UntouchedBase),
            typeof(UntouchedJsonStored), typeof(JsonNode),
        ];

        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(UntouchedDoc)));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(typeof(UntouchedDoc)));
        using (var harness = new StorageHarness<UntouchedDoc>())
            Assert.NotNull(harness.Store);
        Assert.NotNull(new InMemoryStorage<UntouchedDoc>());

        // As on 0.8.3, constructing a store reflects over nothing the first write would not: metadata
        // is built when a value of the type is first serialized, and only for types that flatten.
        Assert.All(reachable, type => Assert.False(TypeMetadataCache.HasMetadata(type), type.Name));

        new UntouchedDoc { Id = Id, Inner = new UntouchedInner { Name = "n" }, Node = Sample() }.ToTableEntity(Pk, Rk);
        Assert.True(TypeMetadataCache.HasMetadata(typeof(UntouchedDoc)));
        Assert.True(TypeMetadataCache.HasMetadata(typeof(UntouchedInner)));
        Assert.False(TypeMetadataCache.HasMetadata(typeof(UntouchedLeaf)));
        Assert.False(TypeMetadataCache.HasMetadata(typeof(JsonNode)));
        Assert.False(TypeMetadataCache.HasMetadata(typeof(IUntouchedShape)));
        Assert.False(TypeMetadataCache.HasMetadata(typeof(UntouchedBase)));
        Assert.False(TypeMetadataCache.HasMetadata(typeof(UntouchedJsonStored)));
    }

    // A Type whose reflection fails (a type from an assembly that cannot be loaded behaves like this).
    private sealed class UnreflectableMarker
    {
        public string? Name { get; set; }
    }

    private sealed class UnreflectableType() : TypeDelegator(typeof(UnreflectableMarker))
    {
        public override PropertyInfo[] GetProperties(BindingFlags bindingAttr) =>
            throw new TypeLoadException("Could not load a member type.");
    }

    [Fact]
    public void TypeWhoseReflectionFails_IsNotLargeValueAware_AndTheAnswerDoesNotThrow()
    {
        var type = new UnreflectableType();

        Assert.False(TypeMetadataCache.IsLargeValueAware(type));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(type));
        Assert.False(LargeValueTable.IsLargeValueAware(type));
    }

    /// <summary>An opted-in entity that also has the members that broke plain types.</summary>
    public class SplitWithJsonMembersDoc : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }

        public JsonNode? Payload { get; set; }

        public PlainJsonHolder? Inner { get; set; }

        public IPlainLookup? Lookup { get; set; } = new PlainLookup();
    }

    [Fact]
    public async Task OptedInType_WithJsonMembers_IsAware_AndRoundTripsOnBothStores()
    {
        // The same walk decides awareness for an opted-in type: JsonNode and the indexed interface
        // must not break it either.
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(SplitWithJsonMembersDoc)));
        Assert.Equal(["Body"], TypeMetadataCache.LargeValueColumns(typeof(SplitWithJsonMembersDoc)));

        var body = OffloadTestSupport.Ascii(40_000);
        SplitWithJsonMembersDoc Make() => new()
        {
            Id = Id,
            Body = body,
            Payload = Sample(),
            Inner = new PlainJsonHolder { Name = "n", Data = JsonNode.Parse("[1]") },
            Lookup = new PlainLookup { Label = "abc" },
        };

        static void AssertStored(TableEntity row, SplitWithJsonMembersDoc read, string expectedBody)
        {
            Assert.Equal(2, row["Body__chunks"]);
            Assert.Equal(SampleJson, row["Payload__Json"]);
            Assert.Equal("[1]", row["Inner_Data__Json"]);
            Assert.Equal("n", row["Inner_Name"]);
            Assert.Equal("abc", row["Lookup_Label"]);
            Assert.Equal(expectedBody, read.Body);
            AssertJson(SampleJson, read.Payload);
            AssertJson("[1]", read.Inner!.Data);
            Assert.Equal("abc", read.Lookup!.Label);
        }

        TableEntity? written = null;
        using (var writer = new StorageHarness<SplitWithJsonMembersDoc>())
        {
            writer.Table.AddEntityAsync(Arg.Do<TableEntity>(e => written = e), Arg.Any<CancellationToken>()).Returns(Mocks.EtagResponse());
            await writer.Store.CreateAsync(Make());
        }
        Assert.NotNull(written);
        written.ETag = new ETag("W/\"etag1\"");
        using (var reader = new StorageHarness<SplitWithJsonMembersDoc>())
        {
            reader.SetupGet(written);
            AssertStored(written, (await reader.Store.OneAsync(Id))!, body);
        }

        var memory = new InMemoryStorage<SplitWithJsonMembersDoc>();
        await memory.CreateAsync(Make());
        AssertStored(memory.PhysicalRow(Pk, Rk)!, (await memory.OneAsync(Id))!, body);

        // A builder update of an ordinary member through the declared types still merges (the path
        // check builds no metadata for the indexed interface either); one of the large member is refused.
        await memory.UpdateAsync(Id, b => b.SetProperty(x => x.Inner!.Name, "n2"));
        await memory.UpdateAsync(Id, b => b.SetProperty(x => x.Lookup!.Label, "v2"));
        var merged = (await memory.OneAsync(Id))!;
        Assert.Equal("n2", merged.Inner!.Name);
        Assert.Equal("v2", merged.Lookup!.Label);
        await Assert.ThrowsAsync<NotSupportedException>(() => memory.UpdateAsync(Id, b => b.SetProperty(x => x.Body, "new")));
        Assert.Equal(body, (await memory.OneAsync(Id))!.Body);

        using var harness = new StorageHarness<SplitWithJsonMembersDoc>();
        TableEntity? patch = null;
        harness.Table.UpdateEntityAsync(Arg.Do<TableEntity>(e => patch = e), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns(Mocks.EtagResponse());
        await harness.Store.UpdateAsync(Id, b => b.SetProperty(x => x.Lookup!.Label, "v2"));
        Assert.Equal("v2", patch!["Lookup_Label"]);
        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Store.UpdateAsync(Id, b => b.SetProperty(x => x.Body, "new")));
    }

    private sealed class PartlyUnreflectableMarker
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }

        public string? Broken { get; set; }

        public TableBlobContent? File { get; set; }
    }

    // The marker's properties, one of which cannot say what its type is.
    private sealed class PartlyUnreflectableType() : TypeDelegator(typeof(PartlyUnreflectableMarker))
    {
        public override PropertyInfo[] GetProperties(BindingFlags bindingAttr) =>
            base.GetProperties(bindingAttr).Select(p => p.Name == "Broken" ? new UnreflectableProperty(p) : p).ToArray();
    }

    private sealed class PlainUnreflectableMarker
    {
        public string? Name { get; set; }

        public string? Broken { get; set; }
    }

    private sealed class PlainUnreflectableType() : TypeDelegator(typeof(PlainUnreflectableMarker))
    {
        public override PropertyInfo[] GetProperties(BindingFlags bindingAttr) =>
            base.GetProperties(bindingAttr).Select(p => p.Name == "Broken" ? new UnreflectableProperty(p) : p).ToArray();
    }

    [Fact]
    public void FlatteningMetadataOfAPlainType_ResolvesNoPropertyType_AsOn083()
    {
        // 0.8.3 built a type's metadata from property names and attributes alone. A plain type must
        // not start failing there because one of its property types cannot be loaded.
        var metadata = TypeMetadataCache.GetMetadata(new PlainUnreflectableType());

        Assert.Equal(["Broken", "Name"], metadata.PropertyMap.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Empty(metadata.SplitOptions);
    }

    private sealed class UnreflectableProperty(PropertyInfo inner) : PropertyInfo
    {
        public override Type PropertyType => throw new TypeLoadException("Could not load the property type.");

        public override PropertyAttributes Attributes => inner.Attributes;

        public override bool CanRead => inner.CanRead;

        public override bool CanWrite => inner.CanWrite;

        public override string Name => inner.Name;

        public override Type? DeclaringType => inner.DeclaringType;

        public override Type? ReflectedType => inner.ReflectedType;

        public override MethodInfo[] GetAccessors(bool nonPublic) => inner.GetAccessors(nonPublic);

        public override MethodInfo? GetGetMethod(bool nonPublic) => inner.GetGetMethod(nonPublic);

        public override MethodInfo? GetSetMethod(bool nonPublic) => inner.GetSetMethod(nonPublic);

        public override ParameterInfo[] GetIndexParameters() => inner.GetIndexParameters();

        public override object? GetValue(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? index, System.Globalization.CultureInfo? culture) =>
            inner.GetValue(obj, invokeAttr, binder, index, culture);

        public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, object?[]? index, System.Globalization.CultureInfo? culture) =>
            inner.SetValue(obj, value, invokeAttr, binder, index, culture);

        public override object[] GetCustomAttributes(bool inherit) => inner.GetCustomAttributes(inherit);

        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => inner.GetCustomAttributes(attributeType, inherit);

        public override bool IsDefined(Type attributeType, bool inherit) => inner.IsDefined(attributeType, inherit);
    }

    [Fact]
    public void PropertyWhoseReflectionFails_DoesNotHideTheLargeValueMembersBesideIt()
    {
        var type = new PartlyUnreflectableType();

        // One unreadable property must not turn an opted-in type into a plain one.
        Assert.True(TypeMetadataCache.IsLargeValueAware(type));
        Assert.Equal(["Body", "File"], TypeMetadataCache.LargeValueColumns(type));
    }

    [Fact]
    public async Task MisusedAttribute_NoLongerBreaksStoreConstruction_AndStillFailsOnFirstUse()
    {
        // Deciding awareness never throws; the misuse is reported by the first read or write, with the
        // documented exception rather than a TypeInitializationException that poisons the store type.
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(SplitOnScalarEntity)));
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(SplitOnInterfaceEntity)));
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(SplitOnAbstractEntity)));

        var scalar = new InMemoryStorage<SplitOnScalarEntity>();
        var onScalar = await Assert.ThrowsAsync<InvalidOperationException>(() => scalar.CreateAsync(new SplitOnScalarEntity { Id = Id }));
        Assert.Contains("SplitOnScalarEntity.Number", onScalar.Message, StringComparison.Ordinal);

        using var harness = new StorageHarness<SplitOnInterfaceEntity>();
        harness.SetupAdd();
        var onInterface = await Assert.ThrowsAsync<NotSupportedException>(() => harness.Store.CreateAsync(new SplitOnInterfaceEntity { Id = Id }));
        Assert.Contains("SplitOnInterfaceEntity.Shape", onInterface.Message, StringComparison.Ordinal);
        Assert.Equal(["CreateIfNotExistsAsync"], CallNames(harness.Table));
    }
}
