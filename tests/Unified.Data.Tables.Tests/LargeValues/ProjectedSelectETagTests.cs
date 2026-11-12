using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A projected query (<see cref="QueryOptions.Select"/>) must not name <c>odata.etag</c> in its
/// <c>$select</c>: the row's version is not a column. The service returns it with every entity, and
/// other Azure SDKs strip the token as one that is not allowed in a select. The entities a projection
/// returns must still carry their ETag.
/// </summary>
public class ProjectedSelectETagTests
{
    private const string ETagToken = "odata.etag";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void BuildSelect_PlainType_AddsTheKeysTheTimestampAndTheDiscriminator_NotTheETagToken()
    {
        var select = TableStorage<TestEntity>.BuildSelect(new QueryOptions { Select = ["Name"] })!;

        Assert.Equal(["PartitionKey", "RowKey", "Timestamp", "_TypeName", "Name"], select);
        Assert.DoesNotContain(ETagToken, select);
    }

    [Fact]
    public void BuildSelect_SplitAwareType_AddsTheSplitColumnsToo_NotTheETagToken()
    {
        var select = TableStorage<SplitTextEntity>.BuildSelect(new QueryOptions { Select = ["Title"] })!;

        Assert.Equal(
            ["PartitionKey", "RowKey", "Timestamp", "_TypeName",
                SystemColumnNames.SplitOf, SystemColumnNames.SplitIndex, SystemColumnNames.SplitParts, "Title"],
            select);
        Assert.DoesNotContain(ETagToken, select);
    }

    // A caller may have copied the token from the 0.8.x documentation, which listed it among the columns
    // a projection carries: it is dropped, and the rest of the projection stands.
    [Fact]
    public void BuildSelect_DropsTheETagTokenACallerNamed()
    {
        Assert.Equal(
            ["PartitionKey", "RowKey", "Timestamp", "_TypeName", "Name"],
            TableStorage<TestEntity>.BuildSelect(new QueryOptions { Select = [ETagToken, "Name"] })!);
        // Still a projection (never "every column") when the token was all the caller named.
        Assert.Equal(
            ["PartitionKey", "RowKey", "Timestamp", "_TypeName"],
            TableStorage<TestEntity>.BuildSelect(new QueryOptions { Select = [ETagToken] })!);
        Assert.Equal(
            ["PartitionKey", "RowKey", "Timestamp", "_TypeName",
                SystemColumnNames.SplitOf, SystemColumnNames.SplitIndex, SystemColumnNames.SplitParts, "Title"],
            TableStorage<SplitTextEntity>.BuildSelect(new QueryOptions { Select = ["Title", ETagToken] })!);
    }

    [Fact]
    public async Task ProjectedQueryPageAndStream_PassASelectWithoutTheETagToken_AndStampTheRowsETag()
    {
        using var h = new StorageHarness<TestEntity>();
        var selects = new List<IEnumerable<string>?>();
        h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Do<IEnumerable<string>?>(s => selects.Add(s)), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable(Mocks.Row("p", "r")));
        var options = new QueryOptions { Partition = "p", Select = ["Name"] };

        var listed = await h.Store.QueryAsync(options, Ct);
        var page = await h.Store.QueryPageAsync(options, Ct);
        var streamed = new List<TestEntity>();
        await foreach (var entity in h.Store.QueryStreamAsync(options, Ct))
            streamed.Add(entity);

        Assert.Equal(3, selects.Count);
        Assert.All(selects, select => Assert.Equal(["PartitionKey", "RowKey", "Timestamp", "_TypeName", "Name"], select!));
        Assert.All(listed.Concat(page.Items).Concat(streamed), entity => Assert.Equal("W/\"etag1\"", entity.ETag));
    }

    // Parity: the in-memory store has no $select to send; a projected read stamps the row's ETag all the same.
    [Fact]
    public async Task InMemory_ProjectedQueryPageAndStream_StampTheRowsETag()
    {
        var store = new InMemoryStorage<TestEntity>();
        var created = await store.CreateAsync(new TestEntity { Id = "p|r", Name = "n", Value = 1 }, Ct);
        var options = new QueryOptions { Partition = "p", Select = ["Value"] };

        var listed = await store.QueryAsync(options, Ct);
        var page = await store.QueryPageAsync(options, Ct);
        var streamed = new List<TestEntity>();
        await foreach (var entity in store.QueryStreamAsync(options, Ct))
            streamed.Add(entity);

        Assert.False(string.IsNullOrEmpty(created.ETag));
        Assert.Equal(3, listed.Concat(page.Items).Concat(streamed).Count());
        Assert.All(listed.Concat(page.Items).Concat(streamed), entity => Assert.Equal(created.ETag, entity.ETag));
    }
}

/// <summary>
/// The same against a LOCAL Azurite, through the real Azure SDK: the request a projected query, page or
/// stream sends carries no <c>odata.etag</c> in <c>$select</c>, and every entity it returns still has the
/// stored row's ETag. Each test works in its own partition <c>lv-{guid}</c> (and names its own blob
/// container <c>lv-{guid}</c>, which no test here needs created) and deletes both when it ends. The tests
/// skip only through <see cref="AzuriteProbe"/>.
/// </summary>
public sealed class ProjectedSelectETagAzuriteTests : IAsyncLifetime
{
    private const string SkipReason = "Azurite (tables and blobs) is not reachable; set " + TestSupport.AzuriteConnection.Variable + " or start Azurite";

    private static readonly string ConnectionString = TestSupport.AzuriteConnection.ConnectionString;

    private static readonly Lazy<Task<bool>> Probe =
        new(() => AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, AzuriteProbe.Required));

    private readonly string pk = "lv-" + Guid.NewGuid().ToString("N");
    private readonly string containerName = "lv-" + Guid.NewGuid().ToString("N");
    private readonly List<Uri> queries = [];
    private readonly HashSet<string> tables = new(StringComparer.Ordinal);
    private readonly List<MemoryCache> caches = [];

    private TableServiceClient tableService = default!;
    private BlobServiceClient blobService = default!;
    private bool available;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        available = await Probe.Value;
        if (!available) return;
        var tableOptions = new TableClientOptions();
        tableOptions.AddPolicy(new QueryRecorder(queries), HttpPipelinePosition.PerCall);
        tableService = new TableServiceClient(ConnectionString, tableOptions);
        blobService = new BlobServiceClient(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cache in caches) cache.Dispose();
        if (!available) return;
        foreach (var table in tables)
        {
            var client = tableService.GetTableClient(table);
            try
            {
                var filter = TableClient.CreateQueryFilter($"PartitionKey eq {pk}");
                var keys = new List<string>();
                await foreach (var row in client.QueryAsync<TableEntity>(filter, select: ["RowKey"], cancellationToken: CancellationToken.None))
                    keys.Add(row.RowKey);
                foreach (var rk in keys)
                    await client.DeleteEntityAsync(pk, rk, ETag.All, CancellationToken.None);
            }
            catch (RequestFailedException)
            {
                // The table was never created, or a row is already gone.
            }
        }
        await blobService.GetBlobContainerClient(containerName).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
    }

    private TableStorage<T> Store<T>() where T : class, IEntity, new()
    {
        tables.Add(typeof(T).Name);
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        return new TableStorage<T>(tableService, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null,
            new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { ContainerName = containerName } }, blobService);
    }

    /// <summary>The <c>$select</c> of every entity query recorded so far, decoded.</summary>
    private List<string> RecordedSelects()
    {
        lock (queries)
        {
            return queries
                .Select(uri => Uri.UnescapeDataString(uri.Query))
                .Where(query => query.Contains("$select=", StringComparison.Ordinal))
                .ToList();
        }
    }

    private void ForgetRequests()
    {
        lock (queries) queries.Clear();
    }

    /// <summary>The ETag of each stored entity of this test's partition, by id, read without a projection.</summary>
    private async Task<Dictionary<string, string>> StoredETagsAsync<T>()
    {
        var etags = new Dictionary<string, string>(StringComparer.Ordinal);
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {pk}");
        await foreach (var row in tableService.GetTableClient(typeof(T).Name).QueryAsync<TableEntity>(filter, cancellationToken: Ct))
        {
            if (!row.ContainsKey(SystemColumnNames.SplitOf)) // a continuation row is not an entity
                etags[pk + "|" + row.RowKey] = row.ETag.ToString();
        }
        return etags;
    }

    private async Task AssertProjectionsCarryTheStoredETagsAsync<T>(
        TableStorage<T> store, string selected, Action<T> assertProjected) where T : class, IEntity, new()
    {
        var stored = await StoredETagsAsync<T>();
        Assert.Equal(3, stored.Count);
        Assert.All(stored.Values, etag => Assert.False(string.IsNullOrEmpty(etag)));
        var options = new QueryOptions { Partition = pk, Select = [selected] };
        ForgetRequests();

        var listed = await store.QueryAsync(options, Ct);
        var page = await store.QueryPageAsync(options, Ct);
        var streamed = new List<T>();
        await foreach (var entity in store.QueryStreamAsync(options, Ct))
            streamed.Add(entity);

        foreach (var entities in new IReadOnlyList<T>[] { listed, page.Items, streamed })
        {
            Assert.Equal(stored.Keys.Order(StringComparer.Ordinal), entities.Select(e => e.Id).Order(StringComparer.Ordinal));
            Assert.All(entities, entity =>
            {
                Assert.Equal(stored[entity.Id], entity.ETag);
                Assert.NotNull(entity.Timestamp);
                assertProjected(entity);
            });
        }

        // One request per read, each with a $select, none naming the token.
        var selects = RecordedSelects();
        Assert.Equal(3, selects.Count);
        Assert.All(selects, select =>
        {
            Assert.Contains(selected, select, StringComparison.Ordinal);
            Assert.DoesNotContain("odata.etag", select, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task PlainType_ProjectedQueryPageAndStream_SendNoETagToken_AndStillStampTheETag()
    {
        Assert.SkipUnless(available, SkipReason);
        var store = Store<TestEntity>();
        for (var i = 0; i < 3; i++)
            await store.CreateAsync(new TestEntity { Id = $"{pk}|r{i}", Name = new string('n', 2_000), Value = i }, Ct);

        await AssertProjectionsCarryTheStoredETagsAsync(store, nameof(TestEntity.Value), entity =>
        {
            Assert.InRange(entity.Value, 0, 2);
            Assert.True(string.IsNullOrEmpty(entity.Name), "an unselected column must not come back populated");
        });
    }

    [Fact]
    public async Task SplitAwareType_ProjectionThatOmitsTheLargeValue_SendsNoETagToken_AndStillStampsTheETag()
    {
        Assert.SkipUnless(available, SkipReason);
        var store = Store<SplitTextEntity>();
        // One value in one cell, one in several cells, one with a continuation row: none is fetched.
        var sizes = new[] { 100, 40_000, 600_000 };
        for (var i = 0; i < sizes.Length; i++)
            await store.CreateAsync(new SplitTextEntity { Id = $"{pk}|r{i}", Title = "t" + i, Body = new string('b', sizes[i]) }, Ct);

        await AssertProjectionsCarryTheStoredETagsAsync(store, nameof(SplitTextEntity.Title), entity =>
        {
            Assert.StartsWith("t", entity.Title, StringComparison.Ordinal);
            Assert.Null(entity.Body);
        });
    }

    /// <summary>Records the URI of every table GET (entity queries and point reads).</summary>
    private sealed class QueryRecorder(List<Uri> log) : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            if (message.Request.Method != RequestMethod.Get)
                return;
            lock (log) log.Add(message.Request.Uri.ToUri());
        }
    }
}
