namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A builder update of an aware type asks whether the column is a large value on read, which constructs a
/// default instance of the entity. The answer must never depend on whether that constructor failed at an
/// earlier moment: an answer obtained while it threw is not remembered, and while it throws a builder update
/// of a column that may be a large value is refused before anything is written (SERIALIZER-1). Each store
/// case has its own entity types: the answers are remembered for the process.
/// </summary>
public class BuilderUpdateWhileTheConstructorFailsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public class FlakyShape
    {
        public virtual string? Notes { get; set; }
    }

    public sealed class FlakyCircle : FlakyShape
    {
        [TableValueSplit(SplitEncoding.Text)]
        public override string? Notes { get; set; }
    }

    // Opted in by the class-level attribute (ruling R3); its large value is below a base-typed property.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class MemoryFlakyHolder : Entity
    {
        private static volatile bool failing;

        public MemoryFlakyHolder()
        {
            if (failing)
                throw new InvalidOperationException("the host is not ready yet");
        }

        public static bool Failing { get => failing; set => failing = value; }

        public FlakyShape Shape { get; set; } = new FlakyCircle();

        public int Rank { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AzuriteFlakyHolder : Entity
    {
        private static volatile bool failing;

        public AzuriteFlakyHolder()
        {
            if (failing)
                throw new InvalidOperationException("the host is not ready yet");
        }

        public static bool Failing { get => failing; set => failing = value; }

        public FlakyShape Shape { get; set; } = new FlakyCircle();

        public int Rank { get; set; }
    }

    // The same type, whose constructor never fails (the control).
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class StableHolder : Entity
    {
        public FlakyShape Shape { get; set; } = new FlakyCircle();

        public int Rank { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class ProjectionFlakyHolder : Entity
    {
        private static volatile bool failing;

        public ProjectionFlakyHolder()
        {
            if (failing)
                throw new InvalidOperationException("the host is not ready yet");
        }

        public static bool Failing { get => failing; set => failing = value; }

        public FlakyShape Shape { get; set; } = new FlakyCircle();

        public int Rank { get; set; }
    }

    // The projection decision uses the same answers: a name whose resolution throws is not known to be an
    // ordinary column, so a projection that names it reads the whole row (the safe side), and once the
    // constructor works the runtime member is known to be a large value. Nothing of the failure is remembered.
    [Fact]
    public void ProjectionOfAColumnWhoseResolutionFails_IsNotNarrowed()
    {
        var isKnownOrdinary = typeof(TableStorage<ProjectionFlakyHolder>).GetMethod(
            "IsKnownOrdinaryColumn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        bool Ordinary(string name) => (bool)isKnownOrdinary.Invoke(null, [name])!;

        ProjectionFlakyHolder.Failing = true;
        try
        {
            Assert.False(Ordinary("Shape_Notes"));
            Assert.False(Ordinary("Rank"));
        }
        finally
        {
            ProjectionFlakyHolder.Failing = false;
        }

        Assert.False(Ordinary("Shape_Notes"));
        Assert.True(Ordinary("Rank"));
        Assert.Contains("Shape_Notes", TypeMetadataCache.LargeValueColumns(typeof(ProjectionFlakyHolder)));
        Assert.True(TypeMetadataCache.IsLargeValueOnRead(typeof(ProjectionFlakyHolder), "Shape_Notes"));
    }

    private static string Ascii(int length, int seed)
    {
        var rng = new Random(seed);
        return string.Create(length, rng, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)r.Next(0x21, 0x7F);
        });
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task BuilderUpdateOfARuntimeSubtypesLargeValue_IsRefused_AlsoAfterTheConstructorFailedOnce(string kind)
    {
        if (kind == LargeValueStores.Memory)
            await RunAsync<MemoryFlakyHolder>(kind, f => MemoryFlakyHolder.Failing = f,
                (store, id, notes) => store.UpdateAsync(id, b => b.SetProperty(x => x.Shape.Notes, notes), Ct));
        else
            await RunAsync<AzuriteFlakyHolder>(kind, f => AzuriteFlakyHolder.Failing = f,
                (store, id, notes) => store.UpdateAsync(id, b => b.SetProperty(x => x.Shape.Notes, notes), Ct));
    }

    private static async Task RunAsync<T>(string kind, Action<bool> failing, Func<IStorage<T>, string, string, Task> updateNotes)
        where T : Entity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<T>();
        var id = stores.Id("r");
        var large = Ascii(40_000, 3);

        T Make(int rank, string notes)
        {
            var entity = new T { Id = id };
            typeof(T).GetProperty("Rank")!.SetValue(entity, rank);
            typeof(T).GetProperty("Shape")!.SetValue(entity, new FlakyCircle { Notes = notes });
            return entity;
        }

        static FlakyShape ShapeOf(T entity) => (FlakyShape)typeof(T).GetProperty("Shape")!.GetValue(entity)!;

        // 0. A row exists, whose member holds a one-cell value.
        await store.CreateAsync(Make(1, "first"), Ct);

        // 1. While the constructor throws, the library cannot tell whether the column is a large value: the
        //    builder update is refused, and nothing is written.
        failing(true);
        Exception? whileFailing;
        try
        {
            whileFailing = await Record.ExceptionAsync(() => updateNotes(store, id, "short"));
        }
        finally
        {
            failing(false);
        }
        var refused = Assert.IsType<NotSupportedException>(whileFailing);
        Assert.Contains("constructor or a getter", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Shape_Notes", refused.Message, StringComparison.Ordinal);
        Assert.Equal("first", ShapeOf((await store.OneAsync(id, Ct))!).Notes);

        // 2. The constructor works again: a whole-entity write stores a large value in the member (split cells).
        await store.UpsertAsync(Make(2, large), Ct);

        // 3. A builder update of the member is refused as for the control type: partial updates cannot write a
        //    large value. It is never merged as a plain cell beside the split cells.
        var builder = await Record.ExceptionAsync(() => updateNotes(store, id, "after"));
        var cells = (await stores.RawRowsAsync<T>()).SelectMany(r => r.Keys).Where(k => k.StartsWith("Shape", StringComparison.Ordinal)).ToList();
        Assert.True(builder is NotSupportedException,
            $"The builder update of the large value: {builder?.GetType().Name ?? "accepted (reported as written)"}. Stored cells: {string.Join(", ", cells)}.");
        Assert.DoesNotContain("Shape_Notes", cells);
        Assert.Equal(large, ShapeOf((await store.OneAsync(id, Ct))!).Notes);

        // The control: the same steps, with a constructor that never fails.
        var control = stores.Store<StableHolder>();
        var controlId = stores.Id("c");
        await control.CreateAsync(new StableHolder { Id = controlId, Rank = 1, Shape = new FlakyCircle { Notes = "first" } }, Ct);
        await Assert.ThrowsAsync<NotSupportedException>(() => control.UpdateAsync(controlId, b => b.SetProperty(x => x.Shape.Notes, "short"), Ct));
        await control.UpsertAsync(new StableHolder { Id = controlId, Rank = 2, Shape = new FlakyCircle { Notes = large } }, Ct);
        await Assert.ThrowsAsync<NotSupportedException>(() => control.UpdateAsync(controlId, b => b.SetProperty(x => x.Shape.Notes, "after"), Ct));
    }
}
