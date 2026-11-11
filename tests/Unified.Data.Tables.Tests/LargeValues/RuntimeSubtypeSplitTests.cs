using System.Linq.Expressions;
using System.Runtime.Serialization;
using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A <see cref="TableValueSplitAttribute"/> (or <see cref="TableBlobContent"/>) member of a runtime
/// subtype: the declared type of the property that owns the object (an interface, an abstract class or
/// a base class) does not have the member, the object the entity's constructor puts there does. A read
/// maps a column to a property through the instance that is already there, so whatever a write stores as
/// split cells, continuation rows or a blob comes back at every size, on <see cref="InMemoryStorage{T}"/>
/// and on <see cref="TableStorage{T}"/> over Azurite. A shape a read cannot map back is refused by the
/// write, never read as null.
/// </summary>
public class RuntimeSubtypeSplitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── models ─────────────────────────────────────────────────────────────

    /// <summary>The large members of a runtime shape, so one test body reads them whatever the holder.</summary>
    public interface IRuntimeLargeMembers
    {
        string? Notes { get; set; }

        byte[]? Image { get; set; }

        List<string>? Tags { get; set; }
    }

    /// <summary>An entity whose <c>Shape</c> property holds a runtime subtype with large members.</summary>
    public interface IRuntimeHolder
    {
        string? Title { get; set; }

        /// <summary>The object in <c>Shape</c>, seen through its large members.</summary>
        IRuntimeLargeMembers Large { get; }
    }

    /// <summary>An interface that declares nothing: every column of its implementations is a runtime column.</summary>
    public interface IRuntimeShape
    {
    }

    public sealed class RuntimeCircle : IRuntimeShape, IRuntimeLargeMembers
    {
        public double Radius { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public byte[]? Image { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public List<string>? Tags { get; set; }
    }

    /// <summary>An interface-typed property with a default instance (the shape of the review finding).</summary>
    public sealed class InterfaceHolder : Entity, IRuntimeHolder
    {
        public IRuntimeShape Shape { get; set; } = new RuntimeCircle();

        public string? Title { get; set; }

        public IRuntimeLargeMembers Large => (IRuntimeLargeMembers)Shape;
    }

    public abstract class RuntimeShapeBase
    {
        public string? Label { get; set; }
    }

    public sealed class RuntimeSquare : RuntimeShapeBase, IRuntimeLargeMembers
    {
        public double Side { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public byte[]? Image { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public List<string>? Tags { get; set; }
    }

    /// <summary>An abstract-base-typed property with a default instance.</summary>
    public sealed class AbstractHolder : Entity, IRuntimeHolder
    {
        public RuntimeShapeBase Shape { get; set; } = new RuntimeSquare();

        public string? Title { get; set; }

        public IRuntimeLargeMembers Large => (IRuntimeLargeMembers)Shape;
    }

    public class RuntimeBox
    {
        public string? Label { get; set; }
    }

    public sealed class RuntimeCrate : RuntimeBox, IRuntimeLargeMembers
    {
        public int Weight { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public byte[]? Image { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public List<string>? Tags { get; set; }
    }

    /// <summary>A base-class-typed property whose default instance is a subclass.</summary>
    public sealed class BaseClassHolder : Entity, IRuntimeHolder
    {
        public RuntimeBox Shape { get; set; } = new RuntimeCrate();

        public string? Title { get; set; }

        public IRuntimeLargeMembers Large => (IRuntimeLargeMembers)Shape;
    }

    /// <summary>A runtime subtype two levels down: the wrapper is a plain default instance.</summary>
    public sealed class RuntimeWrapper
    {
        public IRuntimeShape Inner { get; set; } = new RuntimeCircle();

        public string? Name { get; set; }
    }

    public sealed class NestedHolder : Entity
    {
        public RuntimeWrapper Wrapper { get; set; } = new();

        public string? Title { get; set; }
    }

    /// <summary>A runtime subtype whose members are stored gzip-compressed once they are split.</summary>
    public sealed class RuntimeGzipShape : IRuntimeShape
    {
        [TableValueSplit(SplitEncoding.Gzip)]
        public string? Notes { get; set; }

        [TableValueSplit(SplitEncoding.Gzip)]
        public List<string>? Tags { get; set; }
    }

    public sealed class GzipHolder : Entity
    {
        public IRuntimeShape Shape { get; set; } = new RuntimeGzipShape();

        public string? Title { get; set; }
    }

    /// <summary>A nested object, absent on a new entity, whose own constructor creates the runtime subtype.</summary>
    public sealed class RuntimeMid
    {
        public IRuntimeShape Shape { get; set; } = new RuntimeCircle();

        public string? Name { get; set; }
    }

    public sealed class MidHolder : Entity
    {
        public RuntimeMid? Mid { get; set; }

        public string? Title { get; set; }
    }

    /// <summary>The default instance opts into nothing: the holder stays a plain type.</summary>
    public sealed class RuntimePlainShape : IRuntimeShape
    {
        public string? Name { get; set; }
    }

    public sealed class PlainDefaultHolder : Entity
    {
        public IRuntimeShape Shape { get; set; } = new RuntimePlainShape();

        public string? Title { get; set; }
    }

    /// <summary>A base-class-typed property with no default instance: a read creates the base class.</summary>
    public sealed class NoDefaultBaseHolder : Entity
    {
        public RuntimeBox? Shape { get; set; }

        public string? Title { get; set; }
    }

    /// <summary>An interface-typed property with no default instance: a read cannot create it (as on 0.8.3).</summary>
    public sealed class NoDefaultInterfaceHolder : Entity
    {
        public IRuntimeShape? Shape { get; set; }

        public string? Title { get; set; }
    }

    /// <summary>A shape whose only stored member is the large one.</summary>
    public sealed class RuntimeNotesOnly : IRuntimeShape
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    /// <summary>The member is declared by the interface (without the attribute) and opted in by the implementation.</summary>
    public interface IRuntimeNoted
    {
        string? Notes { get; set; }

        string? Label { get; set; }
    }

    public sealed class RuntimeNoted : IRuntimeNoted
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }

        public string? Label { get; set; }
    }

    public sealed class NotedHolder : Entity
    {
        public IRuntimeNoted Shape { get; set; } = new RuntimeNoted();

        public string? Title { get; set; }
    }

    /// <summary>The noted shape created by the constructor of a nested object that a new entity does not have.</summary>
    public sealed class RuntimeNotedMid
    {
        public IRuntimeNoted Shape { get; set; } = new RuntimeNoted();

        public string? Name { get; set; }
    }

    public sealed class NotedMidHolder : Entity
    {
        public RuntimeNotedMid? Mid { get; set; }

        public string? Title { get; set; }
    }

    /// <summary>A file on a runtime subtype.</summary>
    public sealed class RuntimeAttachment : IRuntimeShape
    {
        public string? Name { get; set; }

        public TableBlobContent? File { get; set; }
    }

    public sealed class AttachmentHolder : Entity
    {
        public IRuntimeShape Shape { get; set; } = new RuntimeAttachment();

        public string? Title { get; set; }
    }

    /// <summary>One value that may never be offloaded beside one that may.</summary>
    public sealed class RuntimePinned : IRuntimeShape
    {
        [TableValueSplit(SplitEncoding.Text, Offload = BlobOffload.Never)]
        public string? Pinned { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public sealed class PinnedHolder : Entity
    {
        public IRuntimeShape Shape { get; set; } = new RuntimePinned();

        public string? Title { get; set; }
    }

    /// <summary>An entity whose constructor fails: deciding awareness must not let that out.</summary>
    public sealed class ThrowingConstructorHolder : Entity
    {
        public ThrowingConstructorHolder() => throw new InvalidOperationException("not constructible here");

        public IRuntimeShape? Shape { get; set; }
    }

    /// <summary>Counts how often deciding awareness constructs it.</summary>
    public sealed class CountedFlatDoc : Entity
    {
        private static int constructed;

        public CountedFlatDoc() => Interlocked.Increment(ref constructed);

        public static int Constructed() => Volatile.Read(ref constructed);

        public string? Name { get; set; }
    }

    public sealed class CountedNestedDoc : Entity
    {
        private static int constructed;

        public CountedNestedDoc() => Interlocked.Increment(ref constructed);

        public static int Constructed() => Volatile.Read(ref constructed);

        public IRuntimeShape? Shape { get; set; }
    }

    // ── values and forms ───────────────────────────────────────────────────

    private const string Inline = "inline";
    private const string Cells = "cells";
    private const string Rows = "rows";
    private const string Blob = "blob";

    private static string Alphanumeric(int length, int seed)
    {
        const string Letters = "abcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random(seed);
        return string.Create(length, random, (span, rng) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = Letters[rng.Next(Letters.Length)];
        });
    }

    /// <summary>
    /// A value of <paramref name="kind"/> that the default settings store in <paramref name="form"/>: one
    /// cell, split cells of one row, continuation rows, or a blob. Text sizes are those of the review
    /// finding (1,000 / 40,000 / 700,000 / 3,500,000 characters).
    /// </summary>
    private static object Value(string kind, string form, int seed = 7) => (kind, form) switch
    {
        ("string", Inline) => Ascii(1_000, seed),
        ("string", Cells) => Ascii(40_000, seed),
        ("string", Rows) => Ascii(700_000, seed),
        ("string", Blob) => Ascii(3_500_000, seed),
        ("bytes", Inline) => RandomBytes(1_000, seed),
        ("bytes", Cells) => RandomBytes(200_000, seed),
        ("bytes", Rows) => RandomBytes(1_500_000, seed),
        ("bytes", Blob) => RandomBytes(3_500_000, seed),
        // JSON text of about 1,000 / 41,000 / 721,000 / 3,500,000 characters.
        ("json", Inline) => Items(10, seed),
        ("json", Cells) => Items(400, seed),
        ("json", Rows) => Items(7_000, seed),
        ("json", Blob) => Items(34_000, seed),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), $"{kind}/{form}"),
    };

    private static List<string> Items(int count, int seed) =>
        Enumerable.Range(0, count).Select(i => Alphanumeric(100, seed + i)).ToList();

    private static string Member(string kind) => kind switch
    {
        "string" => "Notes",
        "bytes" => "Image",
        _ => "Tags",
    };

    private static void Set(IRuntimeLargeMembers members, string kind, object value)
    {
        switch (kind)
        {
            case "string":
                members.Notes = (string)value;
                break;
            case "bytes":
                members.Image = (byte[])value;
                break;
            default:
                members.Tags = (List<string>)value;
                break;
        }
    }

    private static void AssertValue(IRuntimeLargeMembers members, string kind, object expected)
    {
        switch (kind)
        {
            case "string":
                Assert.Equal((string)expected, members.Notes, StringComparer.Ordinal);
                break;
            case "bytes":
                Assert.Equal((byte[])expected, members.Image);
                break;
            default:
                Assert.Equal((List<string>)expected, members.Tags);
                break;
        }
    }

    /// <summary>The stored rows and blobs hold <paramref name="column"/> in <paramref name="form"/>.</summary>
    private static async Task AssertStoredAsync<T>(LargeValueStores stores, string column, string kind, string form)
        where T : class, IEntity, new()
    {
        var rows = await stores.RawRowsAsync<T>();
        var blobs = await stores.BlobNamesAsync<T>();
        var primary = rows[0];
        Assert.Equal("r1", primary.RowKey);
        var inlineCell = kind == "json" ? column + "__Json" : column;

        switch (form)
        {
            case Inline:
                Assert.Single(rows);
                Assert.True(primary.ContainsKey(inlineCell), $"no cell '{inlineCell}'");
                Assert.DoesNotContain(primary.Keys, k => k.StartsWith(column + "__c", StringComparison.Ordinal) || k == column + "__blob");
                Assert.Empty(blobs);
                break;
            case Cells:
                Assert.Single(rows);
                Assert.False(primary.ContainsKey(inlineCell));
                Assert.InRange(Assert.IsType<int>(primary[column + "__chunks"]), 2, 250);
                Assert.False(primary.ContainsKey("_SplitParts"));
                Assert.Empty(blobs);
                break;
            case Rows:
                Assert.InRange(rows.Count, 2, 99);
                Assert.Equal(rows.Count - 1, primary["_SplitParts"]);
                Assert.Equal("r1~0001", rows[1].RowKey);
                Assert.Equal("r1", rows[1]["_SplitOf"]);
                Assert.NotEmpty(LargeValueStores.ChunkKeys(rows[1], column));
                Assert.Empty(blobs);
                break;
            default:
                Assert.Single(rows);
                var reference = BlobReference.Parse(column, Assert.IsType<string>(primary[column + "__blob"]));
                Assert.Equal($"{typeof(T).Name}/{stores.Pk}/r1/{column}/{reference.Sha256}", reference.Path);
                Assert.Equal([reference.Path], blobs);
                Assert.DoesNotContain(primary.Keys, k => k.StartsWith(column + "__c", StringComparison.Ordinal));
                break;
        }
    }

    // ── every shape, member kind and size, on both stores ──────────────────

    public static TheoryData<string, string, string, string> EveryShapeKindAndForm()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var store in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        foreach (var shape in new[] { "interface", "abstract", "base" })
        foreach (var kind in new[] { "string", "bytes", "json" })
        foreach (var form in new[] { Inline, Cells, Rows, Blob })
            data.Add(store, shape, kind, form);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryShapeKindAndForm))]
    public async Task MemberOfARuntimeSubtype_RoundTrips_AtEverySize(string store, string shape, string kind, string form)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);

        var roundTrip = shape switch
        {
            "interface" => RoundTripAsync<InterfaceHolder>(stores!, kind, form, e => e.Title == "before"),
            "abstract" => RoundTripAsync<AbstractHolder>(stores!, kind, form, e => e.Title == "before"),
            _ => RoundTripAsync<BaseClassHolder>(stores!, kind, form, e => e.Title == "before"),
        };
        await roundTrip;
    }

    private static async Task RoundTripAsync<T>(LargeValueStores stores, string kind, string form, Expression<Func<T, bool>> titleIsBefore)
        where T : Entity, IRuntimeHolder, new()
    {
        var id = stores.Id("r1");
        var column = "Shape_" + Member(kind);
        var value = Value(kind, form);
        var entity = new T { Id = id, Title = "before" };
        Set(entity.Large, kind, value);

        await stores.Store<T>().CreateAsync(entity, Ct);
        await AssertStoredAsync<T>(stores, column, kind, form);

        var read = await stores.Store<T>().OneAsync(id, Ct);
        Assert.NotNull(read);
        Assert.Equal("before", read.Title);
        AssertValue(read.Large, kind, value);
        // Every other read path maps the column the same way.
        AssertValue(Assert.Single(await stores.Store<T>().QueryAsync(stores.Pk, Ct)).Large, kind, value);
        AssertValue(Assert.Single(await stores.Store<T>().QueryAsync(new QueryOptions { Partition = stores.Pk }, Ct)).Large, kind, value);
        AssertValue(Assert.Single((await stores.Store<T>().QueryPageAsync(new QueryOptions { Partition = stores.Pk }, Ct)).Items).Large, kind, value);
        AssertValue(Assert.Single(await stores.Store<T>().QueryAsync(titleIsBefore, stores.Pk, ct: Ct)).Large, kind, value);

        // Change another property and save: the large value is still there.
        read.Title = "after";
        await stores.Store<T>().UpdateAsync(read, Ct);

        var again = await stores.Store<T>().OneAsync(id, Ct);
        Assert.NotNull(again);
        Assert.Equal("after", again.Title);
        AssertValue(again.Large, kind, value);
        await AssertStoredAsync<T>(stores, column, kind, form);
    }

    [Theory]
    [InlineData(LargeValueStores.Memory, Cells)]
    [InlineData(LargeValueStores.Memory, Rows)]
    [InlineData(LargeValueStores.Memory, Blob)]
    [InlineData(LargeValueStores.Azurite, Cells)]
    [InlineData(LargeValueStores.Azurite, Rows)]
    [InlineData(LargeValueStores.Azurite, Blob)]
    public async Task MemberOfARuntimeSubtypeTwoLevelsDown_RoundTrips(string store, string form)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        var notes = (string)Value("string", form);
        var entity = new NestedHolder { Id = id, Title = "t" };
        ((RuntimeCircle)entity.Wrapper.Inner).Notes = notes;

        await stores.Store<NestedHolder>().CreateAsync(entity, Ct);
        await AssertStoredAsync<NestedHolder>(stores, "Wrapper_Inner_Notes", "string", form);

        var read = await stores.Store<NestedHolder>().OneAsync(id, Ct);
        Assert.Equal(notes, Assert.IsType<RuntimeCircle>(read!.Wrapper.Inner).Notes, StringComparer.Ordinal);

        read.Wrapper.Name = "renamed";
        await stores.Store<NestedHolder>().UpdateAsync(read, Ct);
        var again = await stores.Store<NestedHolder>().OneAsync(id, Ct);
        Assert.Equal("renamed", again!.Wrapper.Name);
        Assert.Equal(notes, Assert.IsType<RuntimeCircle>(again.Wrapper.Inner).Notes, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task UpsertAndBatchWrites_OfARuntimeMember_RoundTrip_AndLeaveNoStaleRowsOrBlobs(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, cleanupGrace: TimeSpan.Zero);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        InterfaceHolder Make(string rowKey, string form, int seed)
        {
            var entity = new InterfaceHolder { Id = stores!.Id(rowKey), Title = form };
            entity.Large.Notes = (string)Value("string", form, seed);
            return entity;
        }

        async Task AssertReadsAsync(InterfaceHolder expected)
        {
            var read = await stores!.Store<InterfaceHolder>().OneAsync(expected.Id, Ct);
            Assert.Equal(expected.Large.Notes, read!.Large.Notes, StringComparer.Ordinal);
            Assert.Equal(expected.Title, read.Title);
        }

        // Upsert as insert, then over itself in every other form: blob, continuation rows, cells, one cell.
        var id = stores!.Id("r1");
        foreach (var (form, seed) in new[] { (Rows, 1), (Blob, 2), (Rows, 3), (Cells, 4), (Inline, 5) })
        {
            await stores.PastTheNewestBlobAsync<InterfaceHolder>();
            var entity = Make("r1", form, seed);
            await stores.Store<InterfaceHolder>().UpsertAsync(entity, Ct);
            await AssertReadsAsync(entity);
            await AssertStoredAsync<InterfaceHolder>(stores, "Shape_Notes", "string", form);
        }

        // Batches: created, then upserted over.
        var batch = new[] { Make("b1", Rows, 11), Make("b2", Cells, 12), Make("b3", Blob, 13) };
        Assert.Equal(3, await stores.Store<InterfaceHolder>().CreateBatchAsync(batch, Ct));
        foreach (var entity in batch)
            await AssertReadsAsync(entity);
        var replaced = new[] { Make("b1", Cells, 21), Make("b2", Rows, 22), Make("b3", Inline, 23) };
        await stores.PastTheNewestBlobAsync<InterfaceHolder>();
        Assert.Equal(3, await stores.Store<InterfaceHolder>().UpsertBatchAsync(replaced, Ct));
        foreach (var entity in replaced)
            await AssertReadsAsync(entity);
        Assert.Empty(await stores.BlobNamesAsync<InterfaceHolder>());
        Assert.Equal(4, await stores.Store<InterfaceHolder>().CountAsync(stores.Pk, Ct));
        Assert.Equal(
            ["b1", "b2", "b2~0001", "b3", "r1"],
            (await stores.RawRowsAsync<InterfaceHolder>()).Select(r => r.RowKey));
    }

    [Theory]
    [InlineData(LargeValueStores.Memory, 40_000)]
    [InlineData(LargeValueStores.Memory, 700_000)]
    [InlineData(LargeValueStores.Memory, 6_000_000)]
    [InlineData(LargeValueStores.Azurite, 40_000)]
    [InlineData(LargeValueStores.Azurite, 700_000)]
    [InlineData(LargeValueStores.Azurite, 6_000_000)]
    public async Task GzipMemberOfARuntimeSubtype_RoundTrips(string store, int length)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        var notes = Ascii(length);
        var tags = Items(length / 100, seed: 5);

        await stores.Store<GzipHolder>().CreateAsync(
            new GzipHolder { Id = id, Title = "before", Shape = new RuntimeGzipShape { Notes = notes, Tags = tags } }, Ct);

        var primary = (await stores.RawRowsAsync<GzipHolder>())[0];
        Assert.False(primary.ContainsKey("Shape_Notes"));
        Assert.False(primary.ContainsKey("Shape_Tags__Json"));
        var read = await stores.Store<GzipHolder>().OneAsync(id, Ct);
        var shape = Assert.IsType<RuntimeGzipShape>(read!.Shape);
        Assert.Equal(notes, shape.Notes, StringComparer.Ordinal);
        Assert.Equal(tags, shape.Tags);

        read.Title = "after";
        await stores.Store<GzipHolder>().UpdateAsync(read, Ct);
        shape = Assert.IsType<RuntimeGzipShape>((await stores.Store<GzipHolder>().OneAsync(id, Ct))!.Shape);
        Assert.Equal(notes, shape.Notes, StringComparer.Ordinal);
        Assert.Equal(tags, shape.Tags);
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task RuntimeSubtypeCreatedByANestedObject_IsReadBack_AsLongAsTheValueFitsItsRow(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        var notes = Ascii(40_000);

        // Mid is null on a new MidHolder, so the type's columns do not show Mid_Shape_Notes and the store
        // takes the plain path: the value is split into cells of the one row. A read creates the Mid, whose
        // constructor creates the circle, and maps the cells back.
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(MidHolder)));
        await stores.Store<MidHolder>().CreateAsync(
            new MidHolder { Id = id, Title = "t", Mid = new RuntimeMid { Name = "m", Shape = new RuntimeCircle { Radius = 1, Notes = notes } } }, Ct);

        var row = Assert.Single(await stores.RawRowsAsync<MidHolder>());
        Assert.Equal(2, row["Mid_Shape_Notes__chunks"]);
        var read = await stores.Store<MidHolder>().OneAsync(id, Ct);
        Assert.Equal(notes, Assert.IsType<RuntimeCircle>(read!.Mid!.Shape).Notes, StringComparer.Ordinal);

        read.Title = "after";
        await stores.Store<MidHolder>().UpdateAsync(read, Ct);
        var again = await stores.Store<MidHolder>().OneAsync(id, Ct);
        Assert.Equal("after", again!.Title);
        Assert.Equal(notes, Assert.IsType<RuntimeCircle>(again.Mid!.Shape).Notes, StringComparer.Ordinal);
    }

    // ── the type's large-value columns ─────────────────────────────────────

    [Fact]
    public void HolderOfARuntimeSubtype_IsLargeValueAware_AndListsTheColumnsAReadMaps()
    {
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(InterfaceHolder)));
        Assert.True(LargeValueTable.IsLargeValueAware(typeof(InterfaceHolder)));
        Assert.Equal(["Shape_Notes", "Shape_Image", "Shape_Tags"], TypeMetadataCache.LargeValueColumns(typeof(InterfaceHolder)));
        Assert.Equal(["Shape_Notes", "Shape_Image", "Shape_Tags"], TypeMetadataCache.LargeValueColumns(typeof(AbstractHolder)));
        Assert.Equal(["Shape_Notes", "Shape_Image", "Shape_Tags"], TypeMetadataCache.LargeValueColumns(typeof(BaseClassHolder)));
        Assert.Equal(
            ["Wrapper_Inner_Notes", "Wrapper_Inner_Image", "Wrapper_Inner_Tags"],
            TypeMetadataCache.LargeValueColumns(typeof(NestedHolder)));
        Assert.Equal(["Shape_File"], TypeMetadataCache.LargeValueColumns(typeof(AttachmentHolder)));
        Assert.Equal(["Shape_Notes"], TypeMetadataCache.LargeValueColumns(typeof(NotedHolder)));
    }

    [Fact]
    public void HolderWhoseDefaultInstanceOptsIntoNothing_StaysAPlainType()
    {
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(PlainDefaultHolder)));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(typeof(PlainDefaultHolder)));
        // No default instance: the declared types alone decide, as before.
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(NoDefaultBaseHolder)));
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(NoDefaultInterfaceHolder)));
        // The runtime types themselves are large-value aware; they are not entity types and are never constructed here.
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(RuntimeCircle)));
        Assert.Equal(["Notes", "Image", "Tags"], TypeMetadataCache.LargeValueColumns(typeof(RuntimeCircle)));
    }

    [Fact]
    public void DecidingAwareness_ConstructsOnlyAnEntityWhoseDefaultInstancesCanMatter_AndNeverThrows()
    {
        // No property that flattens into columns: no default instance can add a column, nothing is constructed.
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(CountedFlatDoc)));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(typeof(CountedFlatDoc)));
        Assert.Equal(0, CountedFlatDoc.Constructed());

        // A property that may hold a runtime subtype: constructed once, and the answer is kept.
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(CountedNestedDoc)));
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(CountedNestedDoc)));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(typeof(CountedNestedDoc)));
        Assert.Equal(1, CountedNestedDoc.Constructed());

        // A constructor that fails is not the store's problem: the declared types decide.
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(ThrowingConstructorHolder)));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(typeof(ThrowingConstructorHolder)));
        Assert.NotNull(new InMemoryStorage<ThrowingConstructorHolder>());
    }

    // ── the serializer on its own ──────────────────────────────────────────

    [Fact]
    public void Serializer_ReadsTheSplitCellsOfARuntimeSubtypeBack()
    {
        var notes = Ascii(40_000);
        var image = RandomBytes(200_000);
        var entity = new InterfaceHolder { Id = "p|r", Title = "t" };
        entity.Large.Notes = notes;
        entity.Large.Image = image;

        var row = entity.ToTableEntity("p", "r");
        Assert.Equal(2, row["Shape_Notes__chunks"]);
        Assert.Equal(4, row["Shape_Image__chunks"]);

        var read = row.FromTableEntity<InterfaceHolder>();
        Assert.Equal(notes, read.Large.Notes, StringComparer.Ordinal);
        Assert.Equal(image, read.Large.Image);
        Assert.Equal("t", read.Title);
    }

    // ── projections ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LargeValueStores.Memory, Cells)]
    [InlineData(LargeValueStores.Memory, Rows)]
    [InlineData(LargeValueStores.Memory, Blob)]
    [InlineData(LargeValueStores.Azurite, Cells)]
    [InlineData(LargeValueStores.Azurite, Rows)]
    [InlineData(LargeValueStores.Azurite, Blob)]
    public async Task Projection_ThatNamesTheRuntimeMember_OrItsOwner_ReturnsTheWholeValue(string store, string form)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = (string)Value("string", form);
        var entity = new InterfaceHolder { Id = stores!.Id("r1"), Title = "t" };
        entity.Large.Notes = notes;
        await stores.Store<InterfaceHolder>().CreateAsync(entity, Ct);

        foreach (var select in new[] { "Shape_Notes", "Shape" })
        {
            var projected = Assert.Single(await stores.Store<InterfaceHolder>().QueryAsync(
                new QueryOptions { Partition = stores.Pk, Select = [select] }, Ct));
            Assert.Equal(notes, projected.Large.Notes, StringComparer.Ordinal);
        }
    }

    // ── the blob of a runtime member is released with its row ──────────────

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task ReplacedOrDeletedRow_ReleasesTheBlobOfItsRuntimeMember(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, cleanupGrace: TimeSpan.Zero);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        var first = new InterfaceHolder { Id = id };
        first.Large.Notes = Ascii(3_500_000, seed: 1);
        await stores.Store<InterfaceHolder>().CreateAsync(first, Ct);
        var firstBlob = Assert.Single(await stores.BlobNamesAsync<InterfaceHolder>());
        await stores.PastTheNewestBlobAsync<InterfaceHolder>();

        // Replace with other content: the new blob exists before the row commits, the old one goes after it.
        var second = new InterfaceHolder { Id = id };
        second.Large.Notes = Ascii(3_500_000, seed: 2);
        var replacing = stores.Store<InterfaceHolder>();
        var writes = await stores.StorageWritesAsync(replacing, () => replacing.UpsertAsync(second, Ct));
        var secondBlob = Assert.Single(await stores.BlobNamesAsync<InterfaceHolder>());
        Assert.NotEqual(firstBlob, secondBlob);
        Assert.Equal(["blob PUT " + secondBlob, "table WRITE", "blob DELETE " + firstBlob], writes);
        Assert.Equal(second.Large.Notes, (await stores.Store<InterfaceHolder>().OneAsync(id, Ct))!.Large.Notes, StringComparer.Ordinal);

        // A sweep keeps the blob the row references.
        await stores.PastTheNewestBlobAsync<InterfaceHolder>();
        Assert.Equal(0, await ((IOrphanBlobSweeper)stores.Store<InterfaceHolder>()).SweepOrphanBlobsAsync(stores.Pk, TimeSpan.Zero, Ct));
        Assert.Equal([secondBlob], await stores.BlobNamesAsync<InterfaceHolder>());

        await stores.Store<InterfaceHolder>().DeleteAsync(id, Ct);
        Assert.Empty(await stores.BlobNamesAsync<InterfaceHolder>());
        Assert.Empty(await stores.RawRowsAsync<InterfaceHolder>());
    }

    // ── a file on a runtime subtype ────────────────────────────────────────

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task FileOnARuntimeSubtype_IsStoredAsABlob_AndReadLazily(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        var bytes = RandomBytes(300_000);
        var entity = new AttachmentHolder
        {
            Id = id,
            Title = "before",
            Shape = new RuntimeAttachment { Name = "a.bin", File = TableBlobContent.FromBytes(bytes, "application/octet-stream") },
        };

        await stores.Store<AttachmentHolder>().CreateAsync(entity, Ct);
        var blob = Assert.Single(await stores.BlobNamesAsync<AttachmentHolder>());
        Assert.Equal($"AttachmentHolder/{stores.Pk}/r1/Shape_File/{Sha(bytes)}", blob);

        // Reading the entity does not download the file; opening it does.
        var reader = stores.Store<AttachmentHolder>();
        AttachmentHolder? read = null;
        Assert.Equal(0, await stores.BlobReadsAsync(reader, async () => read = await reader.OneAsync(id, Ct)));
        var file = Assert.IsType<RuntimeAttachment>(read!.Shape).File;
        Assert.NotNull(file);
        Assert.Equal(bytes.Length, file.Length);
        await using (var stream = await file.OpenReadAsync(Ct))
            Assert.Equal(bytes, await ReadAllAsync(stream));

        read.Title = "after";
        await stores.Store<AttachmentHolder>().UpdateAsync(read, Ct);
        var again = await stores.Store<AttachmentHolder>().OneAsync(id, Ct);
        Assert.Equal("after", again!.Title);
        await using (var stream = await Assert.IsType<RuntimeAttachment>(again.Shape).File!.OpenReadAsync(Ct))
            Assert.Equal(bytes, await ReadAllAsync(stream));
        Assert.Equal([blob], await stores.BlobNamesAsync<AttachmentHolder>());
    }

    // ── offload rules follow the runtime member's attribute ────────────────

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task RowTooLargeForOneTransaction_OffloadsTheRuntimeMemberThatAllowsIt_NotTheOneThatForbidsIt(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        // 2.6 MB + 0.8 MB of stored text: more than one transaction holds, each below the offload threshold.
        var pinned = Alphanumeric(1_300_000, seed: 1);
        var notes = Alphanumeric(400_000, seed: 2);
        var entity = new PinnedHolder { Id = id, Shape = new RuntimePinned { Pinned = pinned, Notes = notes } };

        await stores.Store<PinnedHolder>().CreateAsync(entity, Ct);

        var rows = await stores.RawRowsAsync<PinnedHolder>();
        Assert.True(rows[0].ContainsKey("Shape_Notes__blob"));
        Assert.True(rows[0].ContainsKey("Shape_Pinned__chunks"));
        Assert.StartsWith($"PinnedHolder/{stores.Pk}/r1/Shape_Notes/", Assert.Single(await stores.BlobNamesAsync<PinnedHolder>()), StringComparison.Ordinal);
        var read = Assert.IsType<RuntimePinned>((await stores.Store<PinnedHolder>().OneAsync(id, Ct))!.Shape);
        Assert.Equal(pinned, read.Pinned, StringComparer.Ordinal);
        Assert.Equal(notes, read.Notes, StringComparer.Ordinal);
    }

    // ── partial updates ────────────────────────────────────────────────────

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task PartialUpdate_OfARuntimeMemberThatIsALargeValue_IsRefused(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        var notes = Ascii(40_000);
        await stores.Store<NotedHolder>().CreateAsync(
            new NotedHolder { Id = id, Shape = new RuntimeNoted { Notes = notes, Label = "l1" } }, Ct);

        // The interface declares Notes without the attribute; the object a read finds there opts it in. A
        // merged plain cell beside the split cells would read back the old value.
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            stores.Store<NotedHolder>().UpdateAsync(id, b => b.SetProperty(x => x.Shape.Notes, "new"), Ct));
        Assert.Contains("Shape_Notes", ex.Message, StringComparison.Ordinal);

        // An ordinary member beside it still merges.
        await stores.Store<NotedHolder>().UpdateAsync(id, b => b.SetProperty(x => x.Shape.Label, "l2"), Ct);
        var read = await stores.Store<NotedHolder>().OneAsync(id, Ct);
        Assert.Equal("l2", read!.Shape.Label);
        Assert.Equal(notes, read.Shape.Notes, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task PartialUpdate_OfALargeValueBelowAnObjectOnlyAReadCreates_IsRefused(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        var notes = Ascii(40_000);

        // Mid is null on a new entity, so the type lists no large-value column and its store takes the
        // plain path; the value is still stored as split cells (in the one row) and read back.
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(NotedMidHolder)));
        await stores.Store<NotedMidHolder>().CreateAsync(
            new NotedMidHolder { Id = id, Mid = new RuntimeNotedMid { Name = "m", Shape = new RuntimeNoted { Notes = notes, Label = "l1" } } }, Ct);
        Assert.Equal(2, Assert.Single(await stores.RawRowsAsync<NotedMidHolder>())["Mid_Shape_Notes__chunks"]);

        // A merged plain cell beside those split cells would read back the old value: refused here too.
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            stores.Store<NotedMidHolder>().UpdateAsync(id, b => b.SetProperty(x => x.Mid!.Shape.Notes, "new"), Ct));
        Assert.Contains("Mid_Shape_Notes", ex.Message, StringComparison.Ordinal);

        await stores.Store<NotedMidHolder>().UpdateAsync(id, b => b.SetProperty(x => x.Mid!.Shape.Label, "l2"), Ct);
        await stores.Store<NotedMidHolder>().UpdateAsync(id, b => b.SetProperty(x => x.Mid!.Name, "m2"), Ct);
        var read = await stores.Store<NotedMidHolder>().OneAsync(id, Ct);
        Assert.Equal("m2", read!.Mid!.Name);
        Assert.Equal("l2", read.Mid.Shape.Label);
        Assert.Equal(notes, read.Mid.Shape.Notes, StringComparer.Ordinal);
    }

    // ── shapes a read cannot map back ──────────────────────────────────────

    [Theory]
    [InlineData(LargeValueStores.Memory, Cells)]
    [InlineData(LargeValueStores.Memory, Rows)]
    [InlineData(LargeValueStores.Memory, Blob)]
    [InlineData(LargeValueStores.Azurite, Cells)]
    [InlineData(LargeValueStores.Azurite, Rows)]
    [InlineData(LargeValueStores.Azurite, Blob)]
    public async Task SubclassBelowABaseTypedPropertyWithNoDefaultInstance_LargeValueIsRefused_AndNothingIsStored(string store, string form)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        NoDefaultBaseHolder Make() => new() { Id = id, Title = "t", Shape = new RuntimeCrate { Weight = 3, Notes = (string)Value("string", form) } };

        // A read creates a RuntimeBox there, which has no Notes: the value would be stored and never read.
        foreach (var write in new Func<IStorage<NoDefaultBaseHolder>, Task>[]
                 {
                     s => s.CreateAsync(Make(), Ct),
                     s => s.UpsertAsync(Make(), Ct),
                     s => s.CreateBatchAsync([Make()], Ct),
                     s => s.UpsertBatchAsync([Make()], Ct),
                 })
        {
            var ex = await Assert.ThrowsAsync<NotSupportedException>(() => write(stores.Store<NoDefaultBaseHolder>()));
            Assert.Contains("'Shape_Notes'", ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(NoDefaultBaseHolder), ex.Message, StringComparison.Ordinal);
        }

        Assert.Empty(await stores.RawRowsAsync<NoDefaultBaseHolder>());
        Assert.Empty(await stores.BlobNamesAsync<NoDefaultBaseHolder>());
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task SubclassBelowABaseTypedPropertyWithNoDefaultInstance_ValueOfOneCell_IsAnOrdinaryCell_AsOn083(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        var notes = (string)Value("string", Inline);

        // Nothing of the large-value feature is involved: the subclass's members are ordinary cells, which
        // a read drops with every other member the base class does not have (as on 0.8.3).
        await stores.Store<NoDefaultBaseHolder>().CreateAsync(
            new NoDefaultBaseHolder { Id = id, Shape = new RuntimeCrate { Weight = 3, Label = "l", Notes = notes } }, Ct);

        var row = Assert.Single(await stores.RawRowsAsync<NoDefaultBaseHolder>());
        Assert.Equal(notes, row["Shape_Notes"]);
        Assert.Equal(3, row["Shape_Weight"]);
        var read = await stores.Store<NoDefaultBaseHolder>().OneAsync(id, Ct);
        Assert.Equal(typeof(RuntimeBox), read!.Shape!.GetType());
        Assert.Equal("l", read.Shape.Label);
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task DefaultInstanceOfAnotherType_LargeValueOfTheWrittenType_IsRefused(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");

        // The constructor puts a RuntimePlainShape in Shape; a read keeps it, and it has no Notes.
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => stores.Store<PlainDefaultHolder>().CreateAsync(
            new PlainDefaultHolder { Id = id, Shape = new RuntimeCircle { Notes = Ascii(40_000) } }, Ct));

        Assert.Contains("'Shape_Notes'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<PlainDefaultHolder>());
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task FileOnASubclassAReadCannotMapBack_IsRefused(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => stores.Store<PlainDefaultHolder>().CreateAsync(
            new PlainDefaultHolder
            {
                Id = id,
                Shape = new RuntimeAttachment { File = TableBlobContent.FromBytes(RandomBytes(10), "application/octet-stream") },
            }, Ct));

        Assert.Contains("'Shape_File'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<PlainDefaultHolder>());
        Assert.Empty(await stores.BlobNamesAsync<PlainDefaultHolder>());
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task InterfaceTypedPropertyWithNoDefaultInstance_FailsOnRead_AsItsOrdinaryCellsDo_NeverNull(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);

        // Ordinary cells beside the large value: the read fails where 0.8.3 fails.
        var mixed = stores!.Id("mixed");
        await stores.Store<NoDefaultInterfaceHolder>().CreateAsync(
            new NoDefaultInterfaceHolder { Id = mixed, Shape = new RuntimeCircle { Radius = 2, Notes = Ascii(40_000) } }, Ct);
        var ex = await Assert.ThrowsAsync<SerializationException>(() => stores.Store<NoDefaultInterfaceHolder>().OneAsync(mixed, Ct));
        Assert.Contains("interface", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);

        // Only the large value: the same failure, not an entity whose Shape is silently null.
        var only = stores.Id("only");
        await stores.Store<NoDefaultInterfaceHolder>().CreateAsync(
            new NoDefaultInterfaceHolder { Id = only, Shape = new RuntimeNotesOnly { Notes = Ascii(40_000) } }, Ct);
        ex = await Assert.ThrowsAsync<SerializationException>(() => stores.Store<NoDefaultInterfaceHolder>().OneAsync(only, Ct));
        Assert.Contains("Shape_Notes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("interface", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── the cache copy ─────────────────────────────────────────────────────

    [Fact]
    public async Task CachedEntity_KeepsTheLargeValueOfItsRuntimeMember()
    {
        await using var stores = await LargeValueStores.OpenAsync(LargeValueStores.Azurite);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores!.Id("r1");
        var notes = Ascii(40_000);
        var entity = new InterfaceHolder { Id = id };
        entity.Large.Notes = notes;

        // One store: the second read is served from its cache, which holds a serializer round trip of the entity.
        var store = stores.Store<InterfaceHolder>();
        await store.CreateAsync(entity, Ct);
        Assert.Equal(notes, (await store.OneAsync(id, Ct))!.Large.Notes, StringComparer.Ordinal);
        Assert.Equal(notes, (await store.OneAsync(id, Ct))!.Large.Notes, StringComparer.Ordinal);
    }
}
