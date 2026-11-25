using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;
using Answer = Unified.Data.Tables.Tests.LargeValues.ScriptedTableTransport.Answer;

namespace Unified.Data.Tables.Tests.Capabilities;

/// <summary>
/// 0.10.0: the Azure provider answers a query with RowKey bounds (<see cref="QueryOptions.FromRowKey"/>,
/// <see cref="QueryOptions.ToRowKey"/>), and a continuation row never comes back from a bounded or a
/// projected scan (break B6). Through the real SDK over a scripted transport: what is asserted is the
/// <c>$filter</c> and the <c>$select</c> the service receives, and what the store makes of the rows it
/// answers with.
/// </summary>
public class AzureRowKeyRangeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string NoRows = "{\"value\":[]}";

    private static string Rows(params string[] rows) => "{\"value\":[" + string.Join(",", rows) + "]}";

    private static string Row(string rowKey, string extra = "") =>
        "{\"odata.etag\":\"W/\\\"1\\\"\",\"PartitionKey\":\"p\",\"RowKey\":\"" + rowKey + "\",\"Timestamp\":\"2024-01-01T00:00:00Z\"" + extra + "}";

    private static (TableStorage<T> Store, ScriptedTableTransport Transport) Scripted<T>(
        Func<ScriptedTableTransport.Sent, Answer> respond, IdNormalization normalization = IdNormalization.Normalized)
        where T : class, IEntity, new()
    {
        var transport = new ScriptedTableTransport(respond);
        var store = new TableStorage<T>(transport.Service(), new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<T>>.Instance,
            authorizer: null, options: new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled, IdNormalization = normalization });
        return (store, transport);
    }

    private static string FilterOf(ScriptedTableTransport transport) =>
        transport.Requests.Last(r => r.Method == "GET").Query.Split('&').Single(p => p.StartsWith("$filter=", StringComparison.Ordinal))["$filter=".Length..];

    public static TheoryData<string?, bool, string?, bool, string> Bounds => new()
    {
        { "k03", true, "k06", false, "PartitionKey eq 'p' and RowKey ge 'k03' and RowKey lt 'k06'" },
        { "k03", false, "k06", true, "PartitionKey eq 'p' and RowKey gt 'k03' and RowKey le 'k06'" },
        { "k03", true, null, false, "PartitionKey eq 'p' and RowKey ge 'k03'" },
        { null, true, "k06", false, "PartitionKey eq 'p' and RowKey lt 'k06'" },
        { null, false, "k06", true, "PartitionKey eq 'p' and RowKey le 'k06'" },
        // The empty string is a bound, not "no bound".
        { "", true, null, false, "PartitionKey eq 'p' and RowKey ge ''" },
        // An apostrophe is escaped as the SDK escapes it in every other filter.
        { "o'k", true, null, false, "PartitionKey eq 'p' and RowKey ge 'o''k'" },
    };

    [Theory]
    [MemberData(nameof(Bounds))]
    public async Task EveryQueryMember_SendsThePartition_ThenEachBoundThatIsSet(string? from, bool fromInclusive, string? to, bool toInclusive, string filter)
    {
        var (store, transport) = Scripted<TestEntity>(_ => new Answer(200, NoRows));
        var options = new QueryOptions { Partition = "p", FromRowKey = from, FromInclusive = fromInclusive, ToRowKey = to, ToInclusive = toInclusive };

        await store.QueryAsync(options, Ct);
        Assert.Equal(filter, FilterOf(transport));
        await foreach (var _ in store.QueryStreamAsync(options, Ct)) { }
        Assert.Equal(filter, FilterOf(transport));
        await store.QueryPageAsync(options, Ct);
        Assert.Equal(filter, FilterOf(transport));
    }

    [Fact]
    public async Task ABound_IsNormalizedAsIdsAre_UnlessIdsAreStoredAsWritten()
    {
        var bounds = new QueryOptions { Partition = "My Partition", FromRowKey = " Row A", ToRowKey = "Row B " };

        var (normalized, sent) = Scripted<TestEntity>(_ => new Answer(200, NoRows));
        await normalized.QueryAsync(bounds, Ct);
        Assert.Equal("PartitionKey eq 'my-partition' and RowKey ge 'row-a' and RowKey lt 'row-b'", FilterOf(sent));

        var (asWritten, sentAsWritten) = Scripted<TestEntity>(_ => new Answer(200, NoRows), IdNormalization.AsWritten);
        await asWritten.QueryAsync(bounds, Ct);
        Assert.Equal("PartitionKey eq 'My Partition' and RowKey ge ' Row A' and RowKey lt 'Row B '", FilterOf(sentAsWritten));
    }

    [Fact]
    public async Task AQueryWithoutBounds_SendsWhatEveryEarlierReleaseSent()
    {
        var (store, transport) = Scripted<TestEntity>(_ => new Answer(200, NoRows));

        await store.QueryAsync(new QueryOptions { Partition = "p" }, Ct);
        Assert.Equal("PartitionKey eq 'p'", FilterOf(transport));
        await store.QueryAsync(new QueryOptions { Partition = "p", RowKeyPrefix = "k" }, Ct);
        Assert.Equal("PartitionKey eq 'p' and RowKey ge 'k' and RowKey lt 'l'", FilterOf(transport));
    }

    [Fact]
    public async Task AnInvalidRange_IsAnArgumentError_BeforeAnyRequest()
    {
        var (store, transport) = Scripted<TestEntity>(_ => new Answer(200, NoRows));

        foreach (var options in new[] { new QueryOptions { ToRowKey = "b" }, new QueryOptions { Partition = "p", RowKeyPrefix = "a", FromRowKey = "a" } })
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync(options, Ct));
            Assert.Equal("options", ex.ParamName);
            Assert.Equal("options", Assert.Throws<ArgumentException>(() => store.QueryStreamAsync(options, Ct)).ParamName);
            Assert.Equal("options", (await Assert.ThrowsAsync<ArgumentException>(() => store.QueryPageAsync(options, Ct))).ParamName);
        }
        Assert.DoesNotContain(transport.Requests, r => r.Method == "GET");
    }

    [Fact]
    public async Task APageToken_IsBoundToTheBounds_AsTheyAreCompared()
    {
        var more = new Dictionary<string, string> { ["x-ms-continuation-NextPartitionKey"] = "p", ["x-ms-continuation-NextRowKey"] = "k04" };
        var (store, transport) = Scripted<TestEntity>(sent => new Answer(200, Rows(Row("k02"), Row("k03")),
            Headers: sent.Query.Contains("NextRowKey", StringComparison.Ordinal) ? null : more));
        var options = new QueryOptions { Partition = "p", FromRowKey = "K02", ToRowKey = "k08", Take = 2 };

        var token = (await store.QueryPageAsync(options, Ct)).ContinuationToken;
        Assert.NotNull(token);

        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryPageAsync(options with { ToRowKey = "k09", ContinuationToken = token }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryPageAsync(options with { FromRowKey = "k01", ContinuationToken = token }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryPageAsync(options with { FromInclusive = false, ContinuationToken = token }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryPageAsync(options with { ToInclusive = true, ContinuationToken = token }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 2, ContinuationToken = token }, Ct));

        // Its own query, also when the bound is spelled another way that normalizes to the same key.
        var next = await store.QueryPageAsync(options with { FromRowKey = " k02", ContinuationToken = token }, Ct);
        Assert.Null(next.ContinuationToken);
        Assert.Contains("NextRowKey=k04", transport.Requests.Last(r => r.Method == "GET").Query, StringComparison.Ordinal);
        Assert.Contains("RowKey ge 'k02' and RowKey lt 'k08'", FilterOf(transport), StringComparison.Ordinal);
    }

    // ── continuation rows never come back as entities ─────────────────────

    private const string PartOfR1 = ",\"_SplitOf\":\"r1\",\"_SplitIndex\":1,\"Body__c1\":\"tail\"";

    [Fact]
    public async Task ABoundedScan_OfATypeWithLargeValues_SkipsAContinuationRowInTheRange()
    {
        // The range holds a primary without parts and the continuation row of another entity, whose
        // primary lies below the lower bound: the continuation row is not an entity and is not assembled.
        var (store, transport) = Scripted<SplitTextEntity>(_ => new Answer(200, Rows(Row("r1~0001", PartOfR1), Row("r2", ",\"Title\":\"two\""))));
        var options = new QueryOptions { Partition = "p", FromRowKey = "r1~", ToRowKey = "r3" };

        var listed = await store.QueryAsync(options, Ct);
        var page = await store.QueryPageAsync(options, Ct);
        var first = await store.QueryAsync(options with { Take = 1 }, Ct);

        Assert.Equal(["p|r2"], listed.Select(e => e.Id));
        Assert.Equal(["p|r2"], page.Items.Select(e => e.Id));
        // The continuation row does not count toward Take.
        Assert.Equal(["p|r2"], first.Select(e => e.Id));
        Assert.All(transport.Requests.Where(r => r.Method == "GET"), r => Assert.Contains("RowKey ge 'r1~' and RowKey lt 'r3'", r.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProjectedScan_OfATypeWithoutLargeValues_SelectsSplitOf_AndSkipsAContinuationRow()
    {
        // Break B6. A type without large values can share a table with one that has them. Until 0.10.0
        // its projected scans did not select _SplitOf and returned the continuation rows as entities.
        var (store, transport) = Scripted<TestEntity>(_ => new Answer(200, Rows(
            Row("r1", ",\"Name\":\"one\""), Row("r1~0001", ",\"_SplitOf\":\"r1\""), Row("r2", ",\"Name\":\"two\""))));
        var projected = new QueryOptions { Partition = "p", Select = ["Name"] };

        var listed = await store.QueryAsync(projected, Ct);
        var page = await store.QueryPageAsync(projected, Ct);
        var bounded = await store.QueryAsync(projected with { FromRowKey = "r1", ToRowKey = "r3" }, Ct);

        Assert.Equal(["p|r1", "p|r2"], listed.Select(e => e.Id));
        Assert.Equal(["one", "two"], listed.Select(e => e.Name));
        Assert.Equal(["p|r1", "p|r2"], page.Items.Select(e => e.Id));
        Assert.Equal(["p|r1", "p|r2"], bounded.Select(e => e.Id));
        Assert.All(transport.Requests.Where(r => r.Method == "GET"),
            r => Assert.Equal("PartitionKey,RowKey,Timestamp,_TypeName,_SplitOf,Name", r.Select));
    }

    [Fact]
    public async Task AProjectedScan_OfATypeWithLargeValues_StillSkipsContinuationRows()
    {
        var (store, _) = Scripted<SplitTextEntity>(_ => new Answer(200, Rows(
            Row("r1", ",\"Title\":\"one\",\"_SplitParts\":1"), Row("r1~0001", PartOfR1))));

        var listed = await store.QueryAsync(new QueryOptions { Partition = "p", Select = ["Title"], FromRowKey = "r1" }, Ct);

        Assert.Equal(["p|r1"], listed.Select(e => e.Id));
        Assert.Equal("one", listed[0].Title);
    }

    [Fact]
    public async Task TheInMemoryStore_ProjectsWithTheSameColumns_AndSkipsContinuationRowsToo()
    {
        var memory = new InMemory.InMemoryStorage<TestEntity>();
        await memory.CreateAsync(new TestEntity { Id = "p|r1", Name = "one" }, Ct);
        var part = new Azure.Data.Tables.TableEntity("p", "r1~0001") { ["_SplitOf"] = "r1" };
        memory.SeedPhysicalRow(part);

        var listed = await memory.QueryAsync(new QueryOptions { Partition = "p", Select = ["Name"] }, Ct);

        Assert.Equal(["p|r1"], listed.Select(e => e.Id));
        Assert.Equal(
            ["PartitionKey", "RowKey", "Timestamp", "_TypeName", "_SplitOf", "Name"],
            Unified.Data.Tables.Providers.ProjectedRows.SelectedColumns<TestEntity>(new QueryOptions { Select = ["Name"] }));
    }
}
