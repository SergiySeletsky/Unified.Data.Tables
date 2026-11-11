using Unified.Data.Tables.Providers;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// How an entity opts in whose large values are members of an object whose type is known only at run time
/// (fix wave 3, stage x1, item 2). The class-level <c>[TableValueSplit]</c> also covers the entity's own
/// <c>string</c>, <c>byte[]</c> and JSON-stored properties, which then cannot be filtered on the server or
/// changed by a builder partial update. <c>[TableValueSplit]</c> on the runtime-typed property itself keeps
/// its documented meaning (a misuse, refused). An entity that marks one of its own properties instead (or
/// declares a <c>TableBlobContent</c> property) is large-value aware by its declared types too, stores the
/// value below the runtime subtype at every size, and keeps every other property ordinary.
/// </summary>
public class RuntimeSubtypeOptInScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, int> StoresAndSizes()
    {
        var data = new TheoryData<string, int>();
        foreach (var store in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        foreach (var length in new[] { 10, 40_000, 700_000, 3_500_000 })
            data.Add(store, length);
        return data;
    }

    public interface ISketchShape
    {
    }

    public sealed class Arc : ISketchShape
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    /// <summary>Opted in by one of its own properties: <c>Title</c> stays an ordinary column.</summary>
    public sealed class Sketch : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Caption { get; set; }

        public string? Title { get; set; }

        public ISketchShape Shape { get; set; } = new Arc();
    }

    /// <summary>Opted in by the class: <c>Title</c> is covered as well.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class ClassOptedSketch : Entity
    {
        public string? Title { get; set; }

        public ISketchShape Shape { get; set; } = new Arc();
    }

    /// <summary>The attribute on the runtime-typed property itself: a documented misuse.</summary>
    public sealed class PropertyOptedSketch : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public ISketchShape Shape { get; set; } = new Arc();
    }

    [Fact]
    public void TheAttributeOnTheRuntimeTypedProperty_IsStillRefusedAsAMisuse()
    {
        Assert.False(LargeValueTable.IsLargeValueAware(typeof(PropertyOptedSketch)));
        var ex = Assert.Throws<NotSupportedException>(() => new PropertyOptedSketch { Id = "p|r" }.ToTableEntity("p", "r"));
        Assert.Contains("PropertyOptedSketch.Shape", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(StoresAndSizes))]
    public async Task EntityOptedInByOneOfItsOwnProperties_StoresTheValueBelowTheRuntimeSubtype_AndKeepsItsOtherPropertiesOrdinary(string kind, int length)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        Assert.True(LargeValueTable.IsLargeValueAware(typeof(Sketch)));
        var store = stores.Store<Sketch>();
        var id = stores.Id("r");
        var notes = Ascii(length, seed: length);

        await store.CreateAsync(new Sketch { Id = id, Title = "first", Shape = new Arc { Notes = notes } }, Ct);

        // Round trip at every size.
        var read = await store.OneAsync(id, Ct);
        Assert.Equal(notes, Assert.IsType<Arc>(read!.Shape).Notes);
        // Title is an ordinary column: a server-side filter works, and so does a builder update.
        var found = await store.QueryAsync(x => x.Title == "first", stores.Pk, ct: Ct);
        Assert.Equal(id, Assert.Single(found).Id);
        await store.UpdateAsync(id, b => b.SetProperty(x => x.Title, "second"), Ct);
        var updated = await stores.Store<Sketch>().OneAsync(id, Ct);
        Assert.Equal("second", updated!.Title);
        Assert.Equal(notes, Assert.IsType<Arc>(updated.Shape).Notes);
    }

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task EntityOptedInByTheClass_StoresTheValue_AndItsOwnStringIsALargeProperty(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<ClassOptedSketch>();
        var id = stores.Id("r");
        var notes = Ascii(40_000);

        await store.CreateAsync(new ClassOptedSketch { Id = id, Title = "first", Shape = new Arc { Notes = notes } }, Ct);

        Assert.Equal(notes, Assert.IsType<Arc>((await store.OneAsync(id, Ct))!.Shape).Notes);
        // The cost of the class-level opt-in: Title is covered too.
        await Assert.ThrowsAsync<NotSupportedException>(() => store.QueryAsync(x => x.Title == "first", stores.Pk, ct: Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => store.UpdateAsync(id, b => b.SetProperty(x => x.Title, "second"), Ct));
    }
}
