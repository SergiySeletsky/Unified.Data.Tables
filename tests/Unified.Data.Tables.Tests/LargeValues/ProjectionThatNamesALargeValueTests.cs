using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A projection (<see cref="QueryOptions.Select"/>) of a split-aware type is narrowed only when every
/// requested name is known to be an ordinary column; otherwise the whole row is fetched and the whole
/// value returned. A name that names a large value counts however it is spelled: the property's column,
/// an object that owns it, one of its storage cells (CACHE-8), or a column the type's static column list
/// does not hold (below a repeated type or a runtime subtype, SERIALIZER-6). A projection never returns a
/// part of a value and never reports an intact row as corrupt. A plain type's projection is sent as in
/// 0.8.3.
/// </summary>
public class ProjectionThatNamesALargeValueTests
{
    private const string Id = "p|a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    private static TableStorage<T> NewStore<T>(RacingTableClient table, IMemoryCache cache, UnifiedTableStorageOptions? options = null, IOffloadBlobStore? blobs = null)
        where T : class, IEntity, new()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(T).Name).Returns(table);
        return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, options, blobs);
    }

    // ── CACHE-8: a storage cell of a large value names that value ───────────────────────────────────────

    // CACHE-8 (reproduction of the review, renamed and held to the decision "fix in code"): Body__c0,
    // Body__chunks, Body__sha256, Body__enc and Body__len are the cells a caller sees in the table for a
    // value split over cells of its row. Naming one of them fetched that cell alone and the query reported
    // the intact row as corrupt. It now names the value: the whole row is fetched and the whole value
    // returned, by the query, the page and the stream.
    [Theory]
    [InlineData("Body__c0")]
    [InlineData("Body__chunks")]
    [InlineData("Body__sha256")]
    [InlineData("Body__enc")]
    [InlineData("Body__len")]
    public async Task Projection_ThatNamesOneStoredCellOfASplitValue_ReturnsTheWholeValue(string cell)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<RaceDoc>(table, cache);
        var body = Ascii(40_000);
        await store.UpsertAsync(new RaceDoc { Id = Id, Body = body, Version = 1 }, Ct);
        Assert.Contains(cell, table.Stored("p", "a")!.Keys);
        var projection = new QueryOptions { Partition = "p", Select = [cell] };

        var returned = new List<RaceDoc>();
        returned.AddRange(await store.QueryAsync(projection, Ct));
        returned.AddRange((await store.QueryPageAsync(projection, Ct)).Items);
        await foreach (var entity in store.QueryStreamAsync(projection, Ct))
            returned.Add(entity);

        Assert.Equal(3, returned.Count);
        Assert.All(returned, entity => Assert.Equal(body, entity.Body, StringComparer.Ordinal));
        Assert.All(table.Requests.Where(r => r.IsEntityQuery), r => Assert.Null(r.Select));
        Assert.Equal(body, (await store.OneAsync(Id, Ct))!.Body, StringComparer.Ordinal);
    }

    // CACHE-8 through the real SDK (reproduction of the review): the same cells, on both stores.
    [Theory]
    [InlineData(LargeValueStores.Memory, "Body__c0")]
    [InlineData(LargeValueStores.Memory, "Body__sha256")]
    [InlineData(LargeValueStores.Memory, "Body__chunks")]
    [InlineData(LargeValueStores.Azurite, "Body__c0")]
    [InlineData(LargeValueStores.Azurite, "Body__sha256")]
    [InlineData(LargeValueStores.Azurite, "Body__chunks")]
    public async Task Projection_ThatNamesOneStoredCellOfASplitValue_ReturnsTheWholeValue_OnBothStores(string kind, string cell)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var body = new string('b', 40_000);
        await stores.Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = stores.Id("r0"), Title = "t0", Body = body }, Ct);

        var returned = await stores.Store<SplitTextEntity>().QueryAsync(new QueryOptions { Partition = stores.Pk, Select = [cell] }, Ct);

        Assert.Equal(body, Assert.Single(returned).Body, StringComparer.Ordinal);
    }

    // The control of the review, kept: the reference cell of an offloaded value, named alone, leaves the
    // property at its default (it never returns a part of the value), and is not cached as a whole entity.
    [Fact]
    public async Task Projection_ThatNamesTheBlobReferenceCellAlone_LeavesThePropertyAtItsDefault()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var options = new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 } };
        var store = NewStore<RaceDoc>(table, cache, options, new InMemoryOffloadBlobStore());
        await store.UpsertAsync(new RaceDoc { Id = Id, Body = Ascii(40_000), Version = 1 }, Ct);
        Assert.Contains("Body__blob", table.Stored("p", "a")!.Keys);
        var projection = new QueryOptions { Partition = "p", Select = ["Body__blob"] };

        Assert.Null(Assert.Single(await store.QueryAsync(projection, Ct)).Body);
        Assert.Null(Assert.Single((await store.QueryPageAsync(projection, Ct)).Items).Body);
        Assert.Equal(40_000, (await store.OneAsync(Id, Ct))!.Body!.Length);
    }

    /// <summary>A split-aware type with a text value and a JSON-stored collection, both opted in.</summary>
    public class ProjectedCellsDoc : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public List<string>? Tags { get; set; }

        public string? Title { get; set; }
    }

    // The table of the verifier's probe (VerifierProjectionProbeTests), pinned as the decision makes it: a
    // value whose row holds it in one cell ("small") and one split over cells ("large"). Every name of the
    // value, its cells included and the __Json cell of the opted-in list, returns the whole value of both
    // rows; a projection that names neither value leaves both at their default and is narrowed.
    [Theory]
    [InlineData("Body")]
    [InlineData("Tags")]
    [InlineData("Tags__Json")]
    [InlineData("Body__c0")]
    [InlineData("Body__chunks")]
    [InlineData("Tags__c0")]
    [InlineData("Tags__sha256")]
    [InlineData("Body__c0", "Body__c1", "Body__chunks", "Body__sha256", "Body__enc", "Body__len")]
    [InlineData("Title")]
    public async Task Projection_ThatNamesAnOptedInValueOrOneOfItsCells_ReturnsTheWholeValueOfEveryRow(params string[] select)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<ProjectedCellsDoc>(table, cache);
        var bigBody = new string('b', 40_000);
        var bigTags = Enumerable.Range(0, 9_000).Select(i => "tag-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        await store.UpsertAsync(new ProjectedCellsDoc { Id = "p|small", Title = "t", Body = "short", Tags = ["x", "y"] }, Ct);
        await store.UpsertAsync(new ProjectedCellsDoc { Id = "p|large", Title = "t", Body = bigBody, Tags = bigTags }, Ct);
        Assert.Contains("Tags__c0", table.Stored("p", "large")!.Keys);
        Assert.Contains("Tags__Json", table.Stored("p", "small")!.Keys);
        var namesAValue = select is not ["Title"];

        foreach (var rk in new[] { "small", "large" })
        {
            var entity = Assert.Single(await store.QueryAsync(new QueryOptions { Partition = "p", RowKeyPrefix = rk, Select = select }, Ct));
            var sent = table.Requests.Last(r => r.IsEntityQuery).Select;
            Assert.Equal("t", entity.Title);
            if (namesAValue)
            {
                Assert.Null(sent);
                Assert.Equal(rk == "small" ? "short" : bigBody, entity.Body, StringComparer.Ordinal);
                Assert.Equal(rk == "small" ? ["x", "y"] : bigTags, entity.Tags);
            }
            else
            {
                Assert.NotNull(sent);
                Assert.Null(entity.Body);
                Assert.Null(entity.Tags);
            }
        }
    }

    // ── SERIALIZER-6: a large value the type's column list does not hold ───────────────────────────────

    public sealed class ChainNode
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }

        public ChainNode? Next { get; set; }
    }

    /// <summary>Its column list holds <c>Head_Body</c> only: a path ends where a type repeats.</summary>
    public sealed class ProjectedChain : Entity
    {
        public ChainNode? Head { get; set; }

        public string? Title { get; set; }
    }

    // SERIALIZER-6 (reproduction of the review). Head.Next.Body is written split and read back whole by an
    // unprojected read; a projection that names its column was sent narrowed to a column the row does not
    // have and read null on TableStorage, while InMemoryStorage returned the value.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ProjectionThatNamesALargeValueBelowARepeatedType_ReturnsTheValue_OnBothStores(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var body = Ascii(40_000, 3);
        await stores.Store<ProjectedChain>().CreateAsync(
            new ProjectedChain { Id = stores.Id("r"), Title = "t", Head = new ChainNode { Body = "head", Next = new ChainNode { Body = body } } }, Ct);

        var whole = await stores.Store<ProjectedChain>().OneAsync(stores.Id("r"), Ct);
        Assert.Equal(body, whole!.Head!.Next!.Body);

        // The column of the value, and the objects that own it.
        foreach (var name in new[] { "Head_Next_Body", "Head_Next", "Head" })
        {
            var projected = await stores.Store<ProjectedChain>().QueryAsync(new QueryOptions { Partition = stores.Pk, Select = [name] }, Ct);
            Assert.Equal(body, Assert.Single(projected).Head?.Next?.Body);
        }
    }

    [Fact]
    public void BuildSelect_FetchesTheWholeRow_ForEveryLargeValueColumnARequestNames()
    {
        // Listed: the whole row is fetched (null = no $select).
        Assert.Null(TableStorage<ProjectedChain>.BuildSelect(new QueryOptions { Select = ["Head_Body"] }));
        // Not listed, yet a large-value column of the same type, and its storage cells.
        Assert.Null(TableStorage<ProjectedChain>.BuildSelect(new QueryOptions { Select = ["Head_Next_Body"] }));
        Assert.Null(TableStorage<ProjectedChain>.BuildSelect(new QueryOptions { Select = ["Head_Next_Next_Body"] }));
        Assert.Null(TableStorage<ProjectedChain>.BuildSelect(new QueryOptions { Select = ["Head_Next_Body__c0"] }));
        Assert.Null(TableStorage<ProjectedChain>.BuildSelect(new QueryOptions { Select = ["Title", "Head_Next_Body"] }));
        // A name that resolves to no member may be a large value: not known to be ordinary.
        Assert.Null(TableStorage<ProjectedChain>.BuildSelect(new QueryOptions { Select = ["NoSuchColumn"] }));
    }

    [Fact]
    public void BuildSelect_NarrowsAProjection_WhoseEveryNameIsAnOrdinaryColumn()
    {
        Assert.Equal(
            ["PartitionKey", "RowKey", "Timestamp", "_TypeName", "_SplitOf", "_SplitIndex", "_SplitParts", "Title"],
            TableStorage<ProjectedChain>.BuildSelect(new QueryOptions { Select = ["Title", "odata.etag", "PartitionKey", "_SplitParts"] }));
        Assert.Equal(
            ["PartitionKey", "RowKey", "Timestamp", "_TypeName", "_SplitOf", "_SplitIndex", "_SplitParts", "Title", "Body__blob"],
            TableStorage<SplitTextEntity>.BuildSelect(new QueryOptions { Select = ["Title", "Body__blob"] }));
    }

    /// <summary>A plain type whose constructor counts its runs.</summary>
    public sealed class CountedPlainDoc : Entity
    {
        private static int constructed;

        public CountedPlainDoc() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public string? Title { get; set; }

        public PlainOwner? Owner { get; set; }
    }

    public sealed class PlainOwner
    {
        public string? Name { get; set; }
    }

    // Rule 2: a plain type's projection is sent exactly as in 0.8.3, whatever it names, and nothing is
    // resolved or constructed to decide it.
    [Fact]
    public void BuildSelect_OfAPlainType_IsSentAsWritten_WithoutConstructingTheEntity()
    {
        // The store type is initialized first: what deciding awareness may do there is not this test's
        // subject (ruling R3, stage s1); BuildSelect itself must construct nothing.
        Assert.NotNull(TableStorage<CountedPlainDoc>.BuildSelect(new QueryOptions { Select = ["Title"] }));
        var before = CountedPlainDoc.Constructed;

        Assert.Equal(
            ["PartitionKey", "RowKey", "Timestamp", "_TypeName", "NoSuchColumn", "Owner_Name", "Body__c0", "Owner"],
            TableStorage<CountedPlainDoc>.BuildSelect(new QueryOptions { Select = ["NoSuchColumn", "Owner_Name", "Body__c0", "Owner", "odata.etag"] }));
        Assert.Equal(
            ["PartitionKey", "RowKey", "Timestamp", "_TypeName", "Value"],
            TableStorage<TestEntity>.BuildSelect(new QueryOptions { Select = ["Value"] }));

        Assert.Equal(before, CountedPlainDoc.Constructed);
    }

    public interface IPaymentMethod
    {
    }

    public sealed class CardMethod : IPaymentMethod
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    /// <summary>Its constructor creates the runtime subtype; the entity's own constructor does not.</summary>
    public sealed class SettlementOf
    {
        public IPaymentMethod Method { get; set; } = new CardMethod();
    }

    public sealed class ProjectedPurchase : Entity
    {
        /// <summary>Makes the type split-aware, so its store takes the split-aware paths.</summary>
        [TableValueSplit(SplitEncoding.Text)]
        public string? Memo { get; set; }

        /// <summary>Empty on a new entity, so the store's column list does not hold <c>Settlement_Method_Notes</c>.</summary>
        public SettlementOf? Settlement { get; set; }
    }

    // SERIALIZER-6 in the shape of the implementers' concern "(a)" (the verifier's VerifierTests.S6_): a
    // large value below a runtime subtype that only a nested object's constructor creates, in a type that
    // is split-aware through another member.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ProjectionThatNamesALargeValueBelowARuntimeSubtype_ReturnsTheValue_OnBothStores(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = Ascii(40_000, 11);
        await stores.Store<ProjectedPurchase>().CreateAsync(
            new ProjectedPurchase { Id = stores.Id("r"), Memo = "m", Settlement = new SettlementOf { Method = new CardMethod { Notes = notes } } }, Ct);

        Assert.DoesNotContain("Settlement_Method_Notes", TypeMetadataCache.LargeValueColumns(typeof(ProjectedPurchase)));
        var whole = await stores.Store<ProjectedPurchase>().OneAsync(stores.Id("r"), Ct);
        Assert.Equal(notes, ((CardMethod)whole!.Settlement!.Method).Notes);

        foreach (var name in new[] { "Settlement_Method_Notes", "Settlement_Method", "Settlement" })
        {
            var projected = await stores.Store<ProjectedPurchase>().QueryAsync(
                new QueryOptions { Partition = stores.Pk, Select = [name] }, Ct);
            Assert.Equal(notes, (Assert.Single(projected).Settlement?.Method as CardMethod)?.Notes);
        }
    }
}
