using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A store whose entity type is not large-value aware (its declared types opt into nothing) writes through
/// the plain serializer, which never stores a large-value form: a member marked <c>[TableValueSplit]</c> or a
/// <c>TableBlobContent</c> value below a runtime subtype is refused with <see cref="NotSupportedException"/>
/// before anything is stored, whatever its size, on both stores, for single writes, batches and builder
/// updates. The message names the entity, the member and the two remedies. An entity opted in by a
/// class-level <c>[TableValueSplit]</c> stores the same value through the large-value path, within the
/// entity limit, in every process alike, whatever its constructor did when the store type was first used.
/// (Review findings SERIALIZER-5 of fix wave 2; ruling R4 of fix wave 3.)
/// </summary>
public class PlainPathLargeValueRefusalTests
{
    /// <summary>The service's limit on one entity.</summary>
    private const long EntityLimit = 1_048_576;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public static TheoryData<string, int> StoresAndSizes()
    {
        var data = new TheoryData<string, int>();
        foreach (var store in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        foreach (var length in new[] { 10, 1_000, 40_000, 700_000 })
            data.Add(store, length);
        return data;
    }

    private static string Describe(IReadOnlyList<TableEntity> rows) =>
        $"{rows.Count} row(s), the largest {rows.Max(TableRowSize.Estimate):N0} estimated bytes (one entity holds {EntityLimit:N0})";

    private static string Cyrillic(int length, int seed)
    {
        const string Letters = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
        var rng = new Random(seed);
        return string.Create(length, rng, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = Letters[r.Next(Letters.Length)];
        });
    }

    // ── models ─────────────────────────────────────────────────────────────

    public interface IShape
    {
    }

    public sealed class Circle : IShape
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }

        public TableBlobContent? File { get; set; }
    }

    /// <summary>A runtime subtype with no large-value member.</summary>
    public sealed class Square : IShape
    {
        public double Side { get; set; }
    }

    /// <summary>Plain by its declared types: its only large-value member is below the interface-typed <c>Shape</c>.</summary>
    public sealed class Holder : Entity
    {
        private static int constructed;

        public Holder() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public IShape Shape { get; set; } = new Circle();

        public string? Title { get; set; }
    }

    /// <summary>The same entity, opted in.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class OptedInHolder : Entity
    {
        public IShape Shape { get; set; } = new Circle();

        public string? Title { get; set; }
    }

    /// <summary>A plain type whose <c>object</c>-typed property can hold a <see cref="TableBlobContent"/>.</summary>
    public sealed class Attachment : Entity
    {
        public object? File { get; set; }
    }

    [Fact]
    public void PlainSerializer_RefusesAFileInAnObjectTypedProperty_OfAPlainType()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            new Attachment { Id = "p|r", File = TableBlobContent.FromBytes([1], "application/octet-stream") }.ToTableEntity("p", "r"));

        Assert.Contains("Attachment.File (column 'File')", ex.Message, StringComparison.Ordinal);
        Assert.Contains("declare Attachment.File as TableBlobContent", ex.Message, StringComparison.Ordinal);
    }

    public sealed class Payment
    {
        public IShape Method { get; set; } = new Circle();
    }

    /// <summary>The owner of the large value is created only by the constructor of a nested object a new entity does not have.</summary>
    public sealed class Order : Entity
    {
        public Payment? Payment { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class OptedInOrder : Entity
    {
        public Payment? Payment { get; set; }
    }

    public record PayloadBase(string Kind);

    public sealed record TextPayload() : PayloadBase("text")
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Text { get; set; }
    }

    /// <summary>The declared type is stored as JSON, the object in it flattens.</summary>
    public sealed class Message : Entity
    {
        public PayloadBase Payload { get; set; } = new TextPayload();
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class OptedInMessage : Entity
    {
        public PayloadBase Payload { get; set; } = new TextPayload();
    }

    public static class Tenant
    {
        public static string? Current { get; set; }
    }

    /// <summary>A constructor that fails until the host has set its tenant.</summary>
    public sealed class Letter : Entity
    {
        public Letter() => Owner = Tenant.Current ?? throw new InvalidOperationException("no tenant yet");

        public string Owner { get; set; }

        public IShape Body { get; set; } = new Circle();
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class OptedInLetter : Entity
    {
        public OptedInLetter() => Owner = Tenant.Current ?? throw new InvalidOperationException("no tenant yet");

        public string Owner { get; set; }

        public IShape Body { get; set; } = new Circle();
    }

    public sealed class Memo : Entity
    {
        public Memo() => Owner = Tenant.Current ?? throw new InvalidOperationException("no tenant yet");

        public string Owner { get; set; }

        public IShape Body { get; set; } = new Circle();
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class OptedInMemo : Entity
    {
        public OptedInMemo() => Owner = Tenant.Current ?? throw new InvalidOperationException("no tenant yet");

        public string Owner { get; set; }

        public IShape Body { get; set; } = new Circle();
    }

    public static class Startup
    {
        public static bool Done { get; set; }
    }

    /// <summary>The entity as the instance of an application sees it in which the constructor succeeded at first use.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class Dossier : Entity
    {
        public IShape Body { get; set; } = new Circle();

        public string? Title { get; set; }
    }

    /// <summary>
    /// The same entity (same table, same columns) as another instance sees it, in which the constructor threw
    /// at the first use of the store type. (One process cannot hold both moments for one type, so the second
    /// instance is a second type mapped to the same table.)
    /// </summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class DossierInALateInstance : Entity
    {
        public DossierInALateInstance()
        {
            if (!Startup.Done)
                throw new InvalidOperationException("start-up has not finished");
        }

        public IShape Body { get; set; } = new Circle();

        public string? Title { get; set; }
    }

    private static void AssertRefusal(Exception? refused, string entity, string member, string holder, string column)
    {
        var ex = Assert.IsType<NotSupportedException>(refused);
        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
        Assert.Contains("below " + holder, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"'{column}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"Mark {entity} with [TableValueSplit]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("with the type that carries the member", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not configured", ex.Message, StringComparison.Ordinal);
    }

    // ── the plain path refuses, whatever the size ──────────────────────────

    [Theory]
    [MemberData(nameof(StoresAndSizes))]
    public async Task EntityThatIsNotAware_LargeValueBelowARuntimeSubtype_IsRefusedBeforeAnythingIsStored_InEveryWrite(string kind, int length)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(Holder)));
        var store = stores.Store<Holder>();
        var notes = Ascii(length, seed: length);

        // An entity without the member is stored as on 0.8.3 (and is replaced below).
        var plain = new Holder { Id = stores.Id("kept"), Title = "kept", Shape = new Square { Side = 2 } };
        await store.CreateAsync(plain, Ct);
        var before = await stores.RawRowsAsync<Holder>();
        var read = await store.OneAsync(plain.Id, Ct);

        Holder Make(string rowKey) => new() { Id = stores.Id(rowKey), Title = "t", Shape = new Circle { Notes = notes } };
        var created = Holder.Constructed;
        var writes = new (string Name, Func<Task> Write)[]
        {
            ("CreateAsync", () => store.CreateAsync(Make("r"), Ct)),
            ("UpsertAsync", () => store.UpsertAsync(Make("r"), Ct)),
            ("UpdateAsync", () =>
            {
                read!.Shape = new Circle { Notes = notes };
                return store.UpdateAsync(read, Ct);
            }),
            ("UpsertAsync over a row", () => store.UpsertAsync(Make("kept"), Ct)),
            ("CreateBatchAsync", () => store.CreateBatchAsync([Make("b1"), Make("b2")], Ct)),
            ("UpsertBatchAsync", () => store.UpsertBatchAsync([Make("b1")], Ct)),
        };
        foreach (var (name, write) in writes)
        {
            Exception? refused = null;
            // On Azurite every table and blob request that writes is recorded: none is sent.
            var sent = await stores.StorageWritesAsync(store, async () => refused = await Record.ExceptionAsync(write));
            Assert.True(refused is NotSupportedException, $"{name}: {refused?.GetType().Name ?? "stored"}");
            Assert.True(sent.Count == 0, $"{name} sent: {string.Join(", ", sent)}");
            AssertRefusal(refused, nameof(Holder), "Circle.Notes", "Holder.Shape", "Shape_Notes");
        }

        // Nothing was stored or changed, nothing was uploaded, and the store constructed no Holder of its own.
        var after = await stores.RawRowsAsync<Holder>();
        Assert.Equal(before.Select(r => r.RowKey), after.Select(r => r.RowKey));
        Assert.Equal(before[0]["Title"], after[0]["Title"]);
        Assert.Empty(await stores.BlobNamesAsync<Holder>());
        Assert.Equal(created + 6, Holder.Constructed);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task EntityThatIsNotAware_BuilderUpdateWithAValueThatHoldsALargeValue_IsRefused(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        var store = stores.Store<Holder>();
        await store.CreateAsync(new Holder { Id = id, Title = "t", Shape = new Square { Side = 1 } }, Ct);
        var before = Holder.Constructed;

        var refused = await Record.ExceptionAsync(() =>
            store.UpdateAsync(id, b => b.SetProperty(x => x.Shape, new Circle { Notes = "short" }), Ct));

        var ex = Assert.IsType<NotSupportedException>(refused);
        Assert.Contains("Circle.Notes below Holder.Shape", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Mark Holder with [TableValueSplit]", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, Holder.Constructed);

        // An ordinary member still merges as on 0.8.3.
        await store.UpdateAsync(id, b => b.SetProperty(x => x.Title, "u"), Ct);
        var row = Assert.Single(await stores.RawRowsAsync<Holder>());
        Assert.Equal("u", row["Title"]);
        Assert.Equal(1.0, row["Shape_Side"]);
        Assert.DoesNotContain(row.Keys, k => k.StartsWith("Shape_Notes", StringComparison.Ordinal));
    }

    [Fact]
    public void PlainSerializer_RefusesALargeValueBelowARuntimeSubtype_AndAPartialUpdateOfOne()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            new Holder { Id = "p|r", Shape = new Circle { Notes = "ten chars!" } }.ToTableEntity("p", "r"));
        Assert.Contains("Circle.Notes below Holder.Shape (column 'Shape_Notes')", ex.Message, StringComparison.Ordinal);

        // A member without a value writes nothing: there is nothing to refuse.
        Assert.DoesNotContain(new Holder { Id = "p|r", Shape = new Circle() }.ToTableEntity("p", "r").Keys, k => k.StartsWith("Shape", StringComparison.Ordinal));

        ex = Assert.Throws<NotSupportedException>(() =>
            TableEntitySerializer.FlattenProperty(typeof(Holder), "Shape", new Circle { Notes = "n" }));
        Assert.Contains("Mark Holder with [TableValueSplit]", ex.Message, StringComparison.Ordinal);

        ex = Assert.Throws<NotSupportedException>(() =>
            TableEntitySerializer.FlattenProperty(typeof(Holder), "Shape", new Circle { File = TableBlobContent.FromBytes([1], "application/octet-stream") }));
        Assert.Contains("below Holder.Shape", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Mark Holder with [TableValueSplit]", ex.Message, StringComparison.Ordinal);

        // Without such a member the row is that of 0.8.3.
        var row = new Holder { Id = "p|r", Title = "t", Shape = new Square { Side = 3 } }.ToTableEntity("p", "r");
        Assert.Equal(3.0, row["Shape_Side"]);
        Assert.Equal("t", row["Title"]);
        Assert.Equal(["Shape_Side"], row.Keys.Where(k => k.StartsWith("Shape", StringComparison.Ordinal)));
    }

    [Fact]
    public void PlainSerializer_OfAnObjectWhoseOwnTypeIsAware_KeepsItsLargeValueCells()
    {
        // A caller of the serializer without a store (and the polymorphic stores, which refuse such rows themselves).
        var notes = Ascii(40_000);
        var row = new OptedInHolder { Id = "p|r", Shape = new Circle { Notes = notes } }.ToTableEntity("p", "r");
        Assert.Equal(2, row["Shape_Notes__chunks"]);
        Assert.Equal(notes, ((Circle)row.FromTableEntity<OptedInHolder>().Shape).Notes);

        var circle = new Circle { Notes = notes }.ToTableEntity("p", "r");
        Assert.Equal(2, circle["Notes__chunks"]);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task EntityThatIsNotAware_FileBelowARuntimeSubtype_IsRefused_NotBlamedOnTheOffloadConfiguration(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind); // blob offload IS configured
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);

        var refused = await Record.ExceptionAsync(() => stores.Store<Order>().CreateAsync(new Order
        {
            Id = stores.Id("f"),
            Payment = new Payment { Method = new Circle { File = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") } },
        }, Ct));

        AssertRefusal(refused, nameof(Order), "Circle.File", "Order.Payment.Method", "Payment_Method_File");
        Assert.Empty(await stores.RawRowsAsync<Order>());
        Assert.Empty(await stores.BlobNamesAsync<Order>());
    }

    /// <summary>
    /// The three shapes the one-time probe missed: an owner only a nested object's constructor creates, a
    /// declared type that is JSON-stored while the object in it flattens, a constructor that fails when the
    /// store type is first used. Not aware by their declared types: refused, never stored as one oversized row.
    /// </summary>
    [Theory]
    [InlineData(LargeValueStores.Memory, "nested")]
    [InlineData(LargeValueStores.Memory, "json")]
    [InlineData(LargeValueStores.Memory, "constructor")]
    [InlineData(LargeValueStores.Azurite, "nested")]
    [InlineData(LargeValueStores.Azurite, "json")]
    [InlineData(LargeValueStores.Azurite, "constructor")]
    public async Task ShapesTheProbeMissed_OfAnEntityThatIsNotAware_AreRefused(string kind, string shape)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var value = Ascii(700_000, seed: 3);

        switch (shape)
        {
            case "nested":
                AssertRefusal(
                    await Record.ExceptionAsync(() => stores.Store<Order>().CreateAsync(
                        new Order { Id = stores.Id("r"), Payment = new Payment { Method = new Circle { Notes = value } } }, Ct)),
                    nameof(Order), "Circle.Notes", "Order.Payment.Method", "Payment_Method_Notes");
                Assert.Empty(await stores.RawRowsAsync<Order>());
                break;
            case "json":
                AssertRefusal(
                    await Record.ExceptionAsync(() => stores.Store<Message>().CreateAsync(
                        new Message { Id = stores.Id("r"), Payload = new TextPayload { Text = value } }, Ct)),
                    nameof(Message), "TextPayload.Text", "Message.Payload", "Payload_Text");
                Assert.Empty(await stores.RawRowsAsync<Message>());
                break;
            default:
                Tenant.Current = null;
                var store = stores.Store<Letter>();
                await store.DeleteAsync(stores.Id("none"), Ct); // the first use of the store type: no tenant is set yet
                Tenant.Current = "contoso";
                AssertRefusal(
                    await Record.ExceptionAsync(() => store.CreateAsync(new Letter { Id = stores.Id("r"), Body = new Circle { Notes = value } }, Ct)),
                    nameof(Letter), "Circle.Notes", "Letter.Body", "Body_Notes");
                Assert.Empty(await stores.RawRowsAsync<Letter>());
                break;
        }
    }

    // ── opted in, the same shapes are stored through the large-value path ──

    [Theory]
    [InlineData(LargeValueStores.Memory, "nested")]
    [InlineData(LargeValueStores.Memory, "json")]
    [InlineData(LargeValueStores.Memory, "constructor")]
    [InlineData(LargeValueStores.Azurite, "nested")]
    [InlineData(LargeValueStores.Azurite, "json")]
    [InlineData(LargeValueStores.Azurite, "constructor")]
    public async Task ShapesTheProbeMissed_OfAnOptedInEntity_AreStoredWithinTheEntityLimit_AndReadBack(string kind, string shape)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var value = Ascii(700_000, seed: 4);
        var id = stores.Id("r");

        switch (shape)
        {
            case "nested":
            {
                var store = stores.Store<OptedInOrder>();
                await store.CreateAsync(new OptedInOrder { Id = id, Payment = new Payment { Method = new Circle { Notes = value } } }, Ct);
                var rows = await stores.RawRowsAsync<OptedInOrder>();
                Assert.True(rows.All(r => TableRowSize.Estimate(r) <= EntityLimit), Describe(rows));
                var read = await store.OneAsync(id, Ct);
                Assert.Equal(value, ((Circle)read!.Payment!.Method).Notes);
                await store.UpdateAsync(read, Ct);
                Assert.Equal(value, ((Circle)(await stores.Store<OptedInOrder>().OneAsync(id, Ct))!.Payment!.Method).Notes);
                break;
            }
            case "json":
            {
                var store = stores.Store<OptedInMessage>();
                await store.CreateAsync(new OptedInMessage { Id = id, Payload = new TextPayload { Text = value } }, Ct);
                var rows = await stores.RawRowsAsync<OptedInMessage>();
                Assert.True(rows.All(r => TableRowSize.Estimate(r) <= EntityLimit), Describe(rows));
                var read = await store.OneAsync(id, Ct);
                Assert.Equal(value, ((TextPayload)read!.Payload).Text);
                await store.UpdateAsync(read, Ct);
                Assert.Equal(value, ((TextPayload)(await stores.Store<OptedInMessage>().OneAsync(id, Ct))!.Payload).Text);
                break;
            }
            default:
            {
                Tenant.Current = null;
                var store = stores.Store<OptedInLetter>();
                await store.DeleteAsync(stores.Id("none"), Ct); // the first use of the store type: no tenant is set yet
                Tenant.Current = "contoso";
                await store.CreateAsync(new OptedInLetter { Id = id, Body = new Circle { Notes = value } }, Ct);
                var rows = await stores.RawRowsAsync<OptedInLetter>();
                Assert.True(rows.All(r => TableRowSize.Estimate(r) <= EntityLimit), Describe(rows));
                Assert.True(rows.Count > 1 || rows[0].ContainsKey("Body_Notes__blob"), Describe(rows));
                Assert.Equal(value, ((Circle)(await store.OneAsync(id, Ct))!.Body).Notes);
                break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task OptedInEntity_FileBelowARuntimeSubtypeOnlyANestedConstructorCreates_IsStoredAsABlob_AndReadBack(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("f");
        var bytes = RandomBytes(3);

        await stores.Store<OptedInOrder>().CreateAsync(new OptedInOrder
        {
            Id = id,
            Payment = new Payment { Method = new Circle { File = TableBlobContent.FromBytes(bytes, "application/octet-stream") } },
        }, Ct);

        Assert.Single(await stores.BlobNamesAsync<OptedInOrder>());
        var file = ((Circle)(await stores.Store<OptedInOrder>().OneAsync(id, Ct))!.Payment!.Method).File;
        Assert.NotNull(file);
        await using var stream = await file.OpenReadAsync(Ct);
        Assert.Equal(bytes, await ReadAllAsync(stream));
    }

    /// <summary>
    /// 700,000 Cyrillic characters (4.2 MB on the wire): the emulator refuses such a row with HTTP 413 when
    /// it is sent unplanned. Not aware: refused by the library before anything is stored. Opted in: planned
    /// (offloaded), read back and saved again on both stores.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ValueThatNeedsABlob_IsRefusedOrPlanned_NeverSentAsOneOversizedRow(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind); // blob offload is configured
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        var notes = Cyrillic(700_000, 5);

        var refused = await Record.ExceptionAsync(() => stores.Store<Order>().CreateAsync(
            new Order { Id = id, Payment = new Payment { Method = new Circle { Notes = notes } } }, Ct));
        Assert.IsType<NotSupportedException>(refused);
        Assert.Empty(await stores.RawRowsAsync<Order>());

        await stores.Store<OptedInOrder>().CreateAsync(
            new OptedInOrder { Id = id, Payment = new Payment { Method = new Circle { Notes = notes } } }, Ct);
        var read = await stores.Store<OptedInOrder>().OneAsync(id, Ct);
        Assert.Equal(notes, ((Circle)read!.Payment!.Method).Notes);
        await stores.Store<OptedInOrder>().UpdateAsync(read, Ct);
        Assert.Equal(notes, ((Circle)(await stores.Store<OptedInOrder>().OneAsync(id, Ct))!.Payment!.Method).Notes);
    }

    // ── awareness does not depend on the moment ────────────────────────────

    [Fact]
    public void Awareness_IsDecidedByTheDeclaredTypes_WhetherOrNotTheConstructorCouldRunAtFirstUse()
    {
        Tenant.Current = null;
        // What a DI container does when a service that depends on the store is resolved at startup.
        using (new StorageHarness<Memo>())
        using (new StorageHarness<OptedInMemo>())
        {
        }

        var plainBefore = TypeMetadataCache.IsLargeValueAware(typeof(Memo));
        var optedInBefore = TypeMetadataCache.IsLargeValueAware(typeof(OptedInMemo));
        Tenant.Current = "contoso";
        Assert.NotNull(new Memo().Body);

        Assert.Equal((false, true), (plainBefore, optedInBefore));
        Assert.Equal((false, true), (TypeMetadataCache.IsLargeValueAware(typeof(Memo)), TypeMetadataCache.IsLargeValueAware(typeof(OptedInMemo))));
        Assert.Throws<NotSupportedException>(() => new Memo { Id = "p|r", Body = new Circle { Notes = "n" } }.ToTableEntity("p", "r"));
    }

    [Fact]
    public async Task TwoInstancesOfOneApplication_SeeTheSameEntities_WhenTheConstructorFailedAtFirstUseInOneOfThem()
    {
        await using var stores = await LargeValueStores.OpenAsync(LargeValueStores.Azurite);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        const string Table = nameof(Dossier);
        Startup.Done = false;
        _ = stores.Store<DossierInALateInstance>(Table); // first use of the store type: the constructor throws
        Startup.Done = true;

        // The healthy instance stores one entity with a 700,000-character value: a main row and a continuation row.
        var id = stores.Id("r");
        await stores.Store<Dossier>(Table).CreateAsync(new Dossier { Id = id, Title = "t", Body = new Circle { Notes = Ascii(700_000, seed: 9) } }, Ct);
        Assert.Equal(["r", "r~0001"], (await stores.RawRowsAsync<Dossier>(Table)).Select(r => r.RowKey));

        // The other instance, over the same table, sees one entity, reads it and deletes all of it.
        var late = stores.Store<DossierInALateInstance>(Table);
        var count = await late.CountAsync(stores.Pk, Ct);
        var projected = await late.QueryAsync(new QueryOptions { Partition = stores.Pk, Select = ["Title"] }, Ct);
        var read = await late.OneAsync(id, Ct);
        await late.DeleteAsync(id, Ct);
        var left = (await stores.RawRowsAsync<Dossier>(Table)).Select(r => r.RowKey).ToList();

        Assert.True(count == 1 && projected.Count == 1 && read is not null && left.Count == 0,
            $"It counts {count} entities for the one that is stored; a projected query returns {projected.Count}; "
            + $"OneAsync {(read is null ? "returns null" : "reads it")}; after its DeleteAsync the rows [{string.Join(", ", left)}] are left.");
        Assert.Equal(700_000, ((Circle)read!.Body).Notes!.Length);
    }

    [Fact]
    public async Task InMemoryStore_OfAnOptedInEntity_WhoseConstructorFailedAtFirstUse_PlansItsWrites()
    {
        Tenant.Current = null;
        var store = new InMemoryStorage<OptedInLetter>(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });
        await store.DeleteAsync("p|none", Ct); // the first use of the store type: no tenant is set yet

        Tenant.Current = "contoso";
        var notes = Ascii(700_000, seed: 3);
        await store.CreateAsync(new OptedInLetter { Id = "p|r", Body = new Circle { Notes = notes } }, Ct);

        var rows = store.PhysicalRowKeys("p").Select(rk => store.PhysicalRow("p", rk)!).ToList();
        Assert.Equal(notes, ((Circle)(await store.OneAsync("p|r", Ct))!.Body).Notes);
        Assert.True(rows.All(r => TableRowSize.Estimate(r) <= EntityLimit), Describe(rows));
    }
}
