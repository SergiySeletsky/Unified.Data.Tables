using System.Runtime.Serialization;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Rule 2 beside rule A of fix wave 8: a plain type whose declared types reach no <see cref="TableValueSplitAttribute"/>
/// and no <see cref="TableBlobContent"/> keeps the builder updates of 0.8.3. Its paths run through every shape of
/// SERIALIZER-1 of the review of fix wave 7 (an [IgnoreDataMember] abstract member, a get-only member, a base class stored
/// as one JSON value by its constructors), an interface, <see cref="object"/> and a top-level column; for each: the
/// builder-update flatten gives the cells of the untyped flatten of 0.8.3, nothing is constructed, no static constructor
/// and no getter runs, and on both stores the update sends one table write (no blob, nothing else) and stores those cells.
/// </summary>
public class PlainTypeBuilderUpdateRunsNoUserCodeTests
{
    private const string Table = "PlainBuilderUpdateNoUserCode";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static class Counted
    {
        internal static int Runs;
    }

    public sealed class Leaf
    {
        private string? _v;
        static Leaf() => Counted.Runs++;
        public Leaf() => Counted.Runs++;
        public string? V { get { Counted.Runs++; return _v; } set => _v = value; }
    }

    public interface IOwner
    {
        Leaf Inner { get; }
    }

    public abstract class AbstractIgnoredOwner
    {
        static AbstractIgnoredOwner() => Counted.Runs++;
        [IgnoreDataMember]
        public abstract Leaf Inner { get; set; }
    }

    public class GetOnlyOwner
    {
        static GetOnlyOwner() => Counted.Runs++;
        public GetOnlyOwner() => Counted.Runs++;
        public Leaf Inner { get { Counted.Runs++; return new Leaf(); } }
    }

    public class JsonByCtorOwner
    {
        static JsonByCtorOwner() => Counted.Runs++;
        public JsonByCtorOwner(int seed) => Counted.Runs += seed + 1;
        public Leaf Inner { get { Counted.Runs++; return new Leaf(); } set { } }
    }

    public sealed class PlainHolder : Entity
    {
        private string? _title;
        static PlainHolder() => Counted.Runs++;
        public PlainHolder() => Counted.Runs++;
        public string? Title { get { Counted.Runs++; return _title; } set => _title = value; }
        public IOwner? ByInterface { get; set; }
        public AbstractIgnoredOwner? ByAbstract { get; set; }
        public GetOnlyOwner? ByGetOnly { get; set; }
        public JsonByCtorOwner? ByJsonCtor { get; set; }
        public object? ByObject { get; set; }
    }

    public sealed class RowWriter : Entity
    {
        public string? Title { get; set; }
    }

    private static readonly string[] Paths = ["Title", "ByInterface_Inner_V", "ByAbstract_Inner_V", "ByGetOnly_Inner_V", "ByJsonCtor_Inner_V", "ByObject_Inner_V"];

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task APlainType_ItsBuilderUpdates_RunNoUserCode_GiveTheCellsOf083_AndSendOneTableWrite(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("p");
        await stores.Store<RowWriter>(Table).CreateAsync(new RowWriter { Id = id, Title = "t" }, Ct);
        await stores.ShareTableAsync<RowWriter, PlainHolder>(Table);
        var store = stores.Store<PlainHolder>(Table);
        var runs = Counted.Runs;
        foreach (var path in Paths)
        {
            var cells = LargeValueRows.FlattenPropertyForUpdate(typeof(PlainHolder), path, "v");
            Assert.Equal(TableEntitySerializer.FlattenProperty(path, "v"), cells);
            var requests = await stores.StorageWritesAsync(store, () => store.UpdateAsync(id, b => b.Updates[path] = "v", Ct));
            // The recorder lists the requests of TableStorage; the in-memory store's merge is no request.
            Assert.Equal(kind == LargeValueStores.Azurite ? new[] { "table WRITE" } : Array.Empty<string>(), requests);
            Assert.Equal("v", (await stores.RawRowsAsync<PlainHolder>(Table)).Single()[path]);
        }
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(PlainHolder)));
        Assert.True(Counted.Runs == runs, $"user code ran {Counted.Runs - runs} times (constructors, static constructors, getters)");
    }
}
