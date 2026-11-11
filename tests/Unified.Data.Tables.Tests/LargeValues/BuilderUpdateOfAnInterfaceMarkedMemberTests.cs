using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Ruling R11 on a builder update (fix wave 5): <c>[TableValueSplit]</c> on a member declared on an interface
/// is a misused attribute, and a builder update (<c>UpdateAsync(id, builder)</c>) never writes a cell for such
/// a member. It is refused with the misuse (<see cref="InvalidOperationException"/> naming the interface member
/// and the remedy) before anything is stored, under every <see cref="OversizedCellPolicy"/> and at every size,
/// whether the path reaches the member through a property declared as the interface, through a property
/// declared as a class that implements it, or the entity type implements it itself (SERIALIZER-1 of the
/// review of wave 4). A builder update that meets a misused attribute reports the misuse itself, never the
/// "constructor or a getter failed" refusal (SERIALIZER-4). A type that implements only unmarked interfaces
/// is updated as on 0.8.3: nothing constructed, no getter called.
/// </summary>
[Collection("OversizedCellPolicy")]
public class BuilderUpdateOfAnInterfaceMarkedMemberTests
{
    private const string Table = "BuilderUpdateMarkedMemos";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public static TheoryData<string, string, OversizedCellPolicy, int> ShapesStoresPoliciesAndSizes()
    {
        var data = new TheoryData<string, string, OversizedCellPolicy, int>();
        foreach (var shape in new[] { "interface", "self", "implementing-class" })
        {
            foreach (var kind in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
            {
                foreach (var policy in new[] { OversizedCellPolicy.Throw, OversizedCellPolicy.TrimWithMarker, OversizedCellPolicy.TrimSilently })
                {
                    data.Add(shape, kind, policy, 10);
                    data.Add(shape, kind, policy, 200_000);
                }
            }
        }
        return data;
    }

    private static string Ascii(int length, int seed)
    {
        var rng = new Random(seed);
        return string.Create(length, rng, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)r.Next(0x21, 0x7F);
        });
    }

    public interface IMarkedMemo
    {
        [TableValueSplit(SplitEncoding.Text)]
        string? Notes { get; set; }
    }

    public sealed class MarkedMemoImpl : IMarkedMemo
    {
        public string? Notes { get; set; }
    }

    // The path runs through a property declared as the interface.
    public sealed class MemoByInterface : Entity
    {
        public IMarkedMemo Doc { get; set; } = new MarkedMemoImpl();

        public string? Title { get; set; }
    }

    // The entity type implements the interface itself.
    public sealed class MemoBySelf : Entity, IMarkedMemo
    {
        public string? Notes { get; set; }

        public string? Title { get; set; }
    }

    // The path runs through a property declared as the implementing class.
    public sealed class MemoByImplementingClass : Entity
    {
        public MarkedMemoImpl Doc { get; set; } = new();

        public string? Title { get; set; }
    }

    // Writes the row the update meets (no cell below Doc and no Notes): a type that opts into nothing.
    public sealed class MemoRowWriter : Entity
    {
        public string? Title { get; set; }
    }

    // SERIALIZER-1: the reviewer's R11_BuilderUpdateOfAMemberMarkedOnTheInterface_IsNeverStoredTruncated and
    // R11_BuilderUpdateOfAnEntityThatImplementsTheInterface_IsNeverStoredTruncated, the verifier's Variant_a,
    // Variant_b and Variant_c, at both sizes and under every policy. The verifier also accepted a
    // NotSupportedException; the brief of wave 5 asks for the misuse itself (InvalidOperationException).
    [Theory]
    [MemberData(nameof(ShapesStoresPoliciesAndSizes))]
    public async Task ABuilderUpdateOfAMemberMarkedOnAnInterface_IsRefusedWithTheMisuse_BeforeAnythingIsStored(
        string shape, string kind, OversizedCellPolicy policy, int length)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        await stores.Store<MemoRowWriter>(Table).CreateAsync(new MemoRowWriter { Id = id, Title = "t" }, Ct);
        var notes = Ascii(length, length);

        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        (Exception? Thrown, Azure.Data.Tables.TableEntity Row) result;
        try
        {
            result = shape switch
            {
                "interface" => await UpdateAsync<MemoByInterface>(stores, id, b => b.SetProperty(x => x.Doc.Notes, notes)),
                "self" => await UpdateAsync<MemoBySelf>(stores, id, b => b.SetProperty(x => x.Notes, notes)),
                _ => await UpdateAsync<MemoByImplementingClass>(stores, id, b => b.SetProperty(x => x.Doc.Notes, notes)),
            };
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }

        var (thrown, row) = result;
        var written = string.Join(", ", row.Keys.Where(k => k.Contains("Notes", StringComparison.Ordinal)));
        Assert.True(thrown is InvalidOperationException && written.Length == 0,
            $"{shape}, {policy}, {length} characters: update {(thrown is null ? "accepted and reported as written" : $"threw {thrown.GetType().Name}: {thrown.Message}")}; " +
            $"stored cells: [{written}]");
        Assert.Contains("[TableValueSplit] on IMarkedMemo.Notes", thrown!.Message, StringComparison.Ordinal);
        Assert.Contains("remove it from IMarkedMemo", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("t", row["Title"]);
    }

    // The update through a store of T, with what it wrote (nothing is asked) and the row after it.
    private static async Task<(Exception? Thrown, Azure.Data.Tables.TableEntity Row)> UpdateAsync<T>(
        LargeValueStores stores, string id, Action<UpdateBuilder<T>> update)
        where T : class, IEntity, new()
    {
        await stores.ShareTableAsync<MemoRowWriter, T>(Table);
        var store = stores.Store<T>(Table);
        Exception? thrown = null;
        var writes = await stores.StorageWritesAsync(store, async () => thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, update, Ct)));
        Assert.Empty(writes);
        return (thrown, (await stores.RawRowsAsync<T>(Table)).Single());
    }

    // A whole-entity write of the shape that implements the interface reports the same misuse (the control).
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AWholeEntityWriteOfTheSameShapes_IsRefusedWithTheMisuse(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var self = await Record.ExceptionAsync(() => stores.Store<MemoBySelf>().CreateAsync(new MemoBySelf { Id = stores.Id("s"), Notes = "x" }, Ct));
        var nested = await Record.ExceptionAsync(() => stores.Store<MemoByImplementingClass>().CreateAsync(
            new MemoByImplementingClass { Id = stores.Id("c"), Doc = new MarkedMemoImpl { Notes = "x" } }, Ct));
        Assert.IsType<InvalidOperationException>(self);
        Assert.IsType<InvalidOperationException>(nested);
        Assert.Empty(await stores.RawRowsAsync<MemoBySelf>());
        Assert.Empty(await stores.RawRowsAsync<MemoByImplementingClass>());
    }

    // ── SERIALIZER-4: a large-value aware type reports the misuse itself ──────────────────────────────

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareMemoByInterface : Entity
    {
        public IMarkedMemo Doc { get; set; } = new MarkedMemoImpl();

        public int Rank { get; set; }
    }

    // The member is reached only through the runtime type of the default instance: the property is
    // declared as a base class that implements nothing, so only the read-side resolution meets the class
    // that implements the marked interface.
    public class MemoBase
    {
        public virtual string? Notes { get; set; }
    }

    public sealed class MarkedMemoFromBase : MemoBase, IMarkedMemo
    {
        public override string? Notes { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareMemoByBase : Entity
    {
        public MemoBase Doc { get; set; } = new MarkedMemoFromBase();

        public string? Title { get; set; }
    }

    // Another misused attribute, met only on the runtime type: [TableValueSplit] on an int.
    public class RankedShape
    {
        public virtual int Rank { get; set; }
    }

    public sealed class MisusedRankedShape : RankedShape
    {
        [TableValueSplit(SplitEncoding.Text)]
        public override int Rank { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareMisusedRuntimeShape : Entity
    {
        public RankedShape Shape { get; set; } = new MisusedRankedShape();

        public string? Title { get; set; }
    }

    // SERIALIZER-4: the reviewer's AwareEntity_BuilderUpdateOfAnInterfaceMarkedMember_RefusalMessage (SPI).
    [Fact]
    public void AwareEntity_BuilderUpdateOfAnInterfaceMarkedMember_ReportsTheMisuse_NotAFailedConstructor()
    {
        var thrown = Record.Exception(() =>
            LargeValueRows.FlattenPropertyForUpdate(typeof(AwareMemoByInterface), "Doc_Notes", "short"));
        var misuse = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains("[TableValueSplit] on IMarkedMemo.Notes", misuse.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("constructor or a getter failed", misuse.Message, StringComparison.Ordinal);
    }

    // SERIALIZER-4: the verifier's Serializer4_PublicApi_BuilderUpdate_vs_WholeEntityWrite_Azurite, on both stores,
    // and the same for the two shapes only the read-side resolution meets: the builder update reports the
    // exception a whole-entity write of the type reports, with the same message.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AwareEntity_BuilderUpdateThatMeetsAMisusedAttribute_ReportsWhatAWholeEntityWriteReports(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        await stores.Store<MemoRowWriter>(Table).CreateAsync(new MemoRowWriter { Id = id, Title = "t" }, Ct);

        var (create, update) = await RefusalsAsync(stores, new AwareMemoByInterface { Id = stores.Id("c"), Rank = 1 },
            b => b.SetProperty(x => x.Doc.Notes, "short"));
        Assert.Contains("[TableValueSplit] on IMarkedMemo.Notes", update.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("constructor or a getter failed", update.Message, StringComparison.Ordinal);

        (create, update) = await RefusalsAsync(stores, new AwareMemoByBase { Id = stores.Id("c"), Title = "c" },
            b => b.SetProperty(x => x.Doc.Notes, "short"));
        Assert.Equal(create.Message, update.Message);

        (create, update) = await RefusalsAsync(stores, new AwareMisusedRuntimeShape { Id = stores.Id("c"), Title = "c" },
            b => b.SetProperty(x => x.Shape.Rank, 2));
        Assert.Equal(create.Message, update.Message);

        var row = (await stores.RawRowsAsync<MemoRowWriter>(Table)).Single();
        Assert.Equal("t", row["Title"]);
    }

    // The whole-entity write of entity and the builder update of the row the writer stored: both refused with
    // InvalidOperationException, nothing written.
    private static async Task<(InvalidOperationException Create, InvalidOperationException Update)> RefusalsAsync<T>(
        LargeValueStores stores, T entity, Action<UpdateBuilder<T>> update)
        where T : class, IEntity, new()
    {
        await stores.ShareTableAsync<MemoRowWriter, T>(Table);
        var store = stores.Store<T>(Table);
        var create = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));
        Exception? updated = null;
        var writes = await stores.StorageWritesAsync(store, async () =>
            updated = await Record.ExceptionAsync(() => store.UpdateAsync(stores.Id("r"), update, Ct)));
        Assert.True(updated is InvalidOperationException,
            $"{typeof(T).Name}: whole-entity write: {create?.GetType().Name}: {create?.Message}{Environment.NewLine}" +
            $"builder update: {updated?.GetType().Name}: {updated?.Message}");
        Assert.Empty(writes);
        var rows = await stores.RawRowsAsync<T>(Table);
        Assert.DoesNotContain(Assert.Single(rows).Keys, k => k.StartsWith("Doc", StringComparison.Ordinal) || k.StartsWith("Shape", StringComparison.Ordinal));
        return (Assert.IsType<InvalidOperationException>(create), (InvalidOperationException)updated!);
    }

    // ── Rule 2: a type that meets only unmarked interfaces is updated as on 0.8.3 ─────────────────────

    public interface IPlainMemo
    {
        string? Notes { get; set; }
    }

    public sealed class PlainMemoImpl : IPlainMemo
    {
        internal static int Constructed;
        internal static int NotesRead;
        private string? notes;

        public PlainMemoImpl() => Interlocked.Increment(ref Constructed);

        public string? Notes
        {
            get
            {
                Interlocked.Increment(ref NotesRead);
                return notes;
            }
            set => notes = value;
        }
    }

    public sealed class PlainMemoHolder : Entity, IPlainMemo
    {
        internal static int Constructed;
        internal static int DocRead;
        private IPlainMemo doc = new PlainMemoImpl();

        public PlainMemoHolder() => Interlocked.Increment(ref Constructed);

        public IPlainMemo Doc
        {
            get
            {
                Interlocked.Increment(ref DocRead);
                return doc;
            }
            set => doc = value;
        }

        public PlainMemoImpl? Concrete { get; set; }

        public string? Notes { get; set; }
    }

    // SERIALIZER-1 "must guarantee": the check reads attributes by reflection only; a type that implements no
    // such interface keeps 0.8.3's builder update (the same cells, nothing constructed, no getter called).
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task APlainTypeThatImplementsAnUnmarkedInterface_IsUpdatedAsBefore_WithNothingConstructed(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(PlainMemoHolder)));
        var id = stores.Id("r");
        await stores.Store<MemoRowWriter>(Table).CreateAsync(new MemoRowWriter { Id = id, Title = "t" }, Ct);
        await stores.ShareTableAsync<MemoRowWriter, PlainMemoHolder>(Table);
        var store = stores.Store<PlainMemoHolder>(Table);
        var notes = Ascii(20_000, 7);

        PlainMemoHolder.Constructed = PlainMemoHolder.DocRead = PlainMemoImpl.Constructed = PlainMemoImpl.NotesRead = 0;
        await store.UpdateAsync(id, b => b
            .SetProperty(x => x.Doc.Notes, notes)
            .SetProperty(x => x.Concrete!.Notes, "concrete")
            .SetProperty(x => x.Notes, "self"), Ct);
        Assert.Equal((0, 0, 0, 0), (PlainMemoHolder.Constructed, PlainMemoHolder.DocRead, PlainMemoImpl.Constructed, PlainMemoImpl.NotesRead));
        Assert.False(TypeMetadataCache.HasMetadata(typeof(IPlainMemo)));

        var row = (await stores.RawRowsAsync<PlainMemoHolder>(Table)).Single();
        Assert.Equal(notes, row["Doc_Notes"]);
        Assert.Equal("concrete", row["Concrete_Notes"]);
        Assert.Equal("self", row["Notes"]);
        Assert.Equal("t", row["Title"]);
    }
}
