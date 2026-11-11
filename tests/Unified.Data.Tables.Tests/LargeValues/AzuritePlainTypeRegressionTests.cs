using System.Globalization;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A plain entity with a <see cref="JsonNode"/> property end to end against a LOCAL Azurite through
/// <see cref="TableStorage{T}"/> and the real Azure SDK client: it stores, reads, updates and deletes, and
/// sends exactly the HTTP requests the published 0.8.3 sends (captured from the NuGet package with a
/// throwaway console project against the same emulator), with blob offload configured or not.
/// <para>
/// Each test works in its own partition <c>lv-{guid}</c>, which it deletes when it ends. A plain type
/// never touches blob storage, so the container <c>lv-{guid}</c> named in its options is never created
/// (it is deleted at the end all the same). The tests skip only through <see cref="TestSupport.AzuriteProbe"/>.
/// </para>
/// </summary>
public sealed class AzuritePlainTypeRegressionTests : IAsyncLifetime
{
    private const string DevelopmentStorage = "UseDevelopmentStorage=true";
    private const string SkipReason = "Azurite (tables and blobs) is not reachable; set "
        + AzuriteLargeValueIntegrationTests.ConnectionStringVariable + " or start Azurite";
    private const string SampleJson = """{"a":1,"b":[true,null,"x"],"c":{"d":"é"}}""";

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable(AzuriteLargeValueIntegrationTests.ConnectionStringVariable) is { Length: > 0 } configured
            ? configured
            : DevelopmentStorage;

    // Once per test run: skips only when the emulator is not there at all (see AzuriteProbe).
    private static readonly Lazy<Task<bool>> Probe =
        new(() => TestSupport.AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, TestSupport.AzuriteProbe.Required));

    private readonly string pk = "lv-" + Guid.NewGuid().ToString("N");
    private readonly string containerName = "lv-" + Guid.NewGuid().ToString("N");
    private readonly List<string> tableRequests = [];
    private readonly List<string> blobRequests = [];
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
        tableOptions.AddPolicy(new RequestRecorder(tableRequests, this), HttpPipelinePosition.PerCall);
        var blobOptions = new BlobClientOptions();
        blobOptions.AddPolicy(new RequestRecorder(blobRequests, this), HttpPipelinePosition.PerCall);
        tableService = new TableServiceClient(ConnectionString, tableOptions);
        blobService = new BlobServiceClient(ConnectionString, blobOptions);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cache in caches) cache.Dispose();
        if (!available) return;
        var client = new TableServiceClient(ConnectionString).GetTableClient(nameof(PlainJsonNodeDoc));
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
        await new BlobServiceClient(ConnectionString).GetBlobContainerClient(containerName)
            .DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
    }

    // A new store (and cache) per operation, so every operation reaches the service.
    private TableStorage<PlainJsonNodeDoc> Store(bool withOffload)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        var options = new UnifiedTableStorageOptions
        {
            BlobOffload = withOffload ? new BlobOffloadOptions { ContainerName = containerName } : null,
        };
        return new TableStorage<PlainJsonNodeDoc>(tableService, cache, NullLogger<TableStorage<PlainJsonNodeDoc>>.Instance,
            authorizer: null, options, withOffload ? blobService : null);
    }

    private async Task<string> Requests(Func<Task> operation)
    {
        lock (tableRequests) tableRequests.Clear();
        await operation();
        lock (tableRequests) return string.Join(" ; ", tableRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainJsonNodeDoc_RoundTrips_WithTheRequestsOf083(bool withOffload)
    {
        Assert.SkipUnless(available, SkipReason);
        const string Table = "{account}/PlainJsonNodeDoc";
        const string Row = Table + "(PartitionKey='{pk}',RowKey='r1')";
        const string Format = "?$format=application/json;odata=minimalmetadata";
        const string Ensure = "POST {account}/Tables" + Format;
        var id = pk + "|r1";
        var raw = new TableServiceClient(ConnectionString).GetTableClient(nameof(PlainJsonNodeDoc));

        // Create: one insert, the cells of 0.8.3.
        Assert.Equal(
            $"{Ensure} ; POST {Table}{Format}",
            await Requests(() => Store(withOffload).CreateAsync(new PlainJsonNodeDoc { Id = id, Payload = JsonNode.Parse(SampleJson), Title = "t" }, Ct)));
        var row = (await raw.GetEntityAsync<TableEntity>(pk, "r1", cancellationToken: Ct)).Value;
        Assert.Equal(
            ["CreatedAt", "Id", "PartitionKey", "Payload__Json", "RowKey", "Timestamp", "Title", "UpdatedAt", "odata.etag"],
            row.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(id, row["Id"]);
        Assert.Equal(SampleJson, row["Payload__Json"]);
        Assert.Equal("t", row["Title"]);
        Assert.IsType<DateTimeOffset>(row["CreatedAt"]);
        Assert.IsType<DateTimeOffset>(row["UpdatedAt"]);

        // Read: one point read.
        PlainJsonNodeDoc? read = null;
        Assert.Equal($"{Ensure} ; GET {Row}{Format}", await Requests(async () => read = await Store(withOffload).OneAsync(id, Ct)));
        Assert.IsType<JsonObject>(read!.Payload);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SampleJson), read.Payload));
        Assert.Equal("t", read.Title);
        Assert.False(string.IsNullOrEmpty(read.ETag));

        // Upsert: one unconditional replace, with no read before it.
        Assert.Equal(
            $"{Ensure} ; PUT {Row}{Format}",
            await Requests(() => Store(withOffload).UpsertAsync(new PlainJsonNodeDoc { Id = id, Payload = JsonNode.Parse("[1,2]"), Title = "t2" }, Ct)));

        // Update with the ETag just read: the read, then one conditional replace.
        Assert.Equal(
            $"{Ensure} ; GET {Row}{Format} ; PUT {Row}{Format} If-Match=<etag>",
            await Requests(async () =>
            {
                var store = Store(withOffload);
                var loaded = (await store.OneAsync(id, Ct))!;
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[1,2]"), loaded.Payload));
                loaded.Payload = JsonValue.Create(5);
                await store.UpdateAsync(loaded, Ct);
            }));

        // Builder update: one merge.
        Assert.Equal(
            $"{Ensure} ; PATCH {Row}{Format} If-Match=*",
            await Requests(() => Store(withOffload).UpdateAsync(id, b => b.SetProperty(x => x.Payload, JsonNode.Parse("""{"z":1}""")), Ct)));

        IEnumerable<PlainJsonNodeDoc>? all = null;
        Assert.Equal(
            $"{Ensure} ; GET {Table}(){Format}&$filter=PartitionKey eq '{{pk}}'",
            await Requests(async () => all = await Store(withOffload).QueryAsync(pk, Ct)));
        var only = Assert.Single(all!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"z":1}"""), only.Payload));
        Assert.Equal("t2", only.Title);

        var count = 0;
        Assert.Equal(
            $"{Ensure} ; GET {Table}(){Format}&$select=PartitionKey&$filter=PartitionKey eq '{{pk}}'",
            await Requests(async () => count = await Store(withOffload).CountAsync(pk, Ct)));
        Assert.Equal(1, count);

        Assert.Equal($"{Ensure} ; GET {Row}{Format}", await Requests(() => Store(withOffload).ExistsAsync(id, Ct)));

        // Delete: one unconditional delete, with no read before it.
        Assert.Equal($"{Ensure} ; DELETE {Row}{Format} If-Match=*", await Requests(() => Store(withOffload).DeleteAsync(id, Ct)));

        PlainJsonNodeDoc? gone = null;
        Assert.Equal($"{Ensure} ; GET {Row}{Format}", await Requests(async () => gone = await Store(withOffload).OneAsync(id, Ct)));
        Assert.Null(gone);

        // A plain type never touches blob storage, whether blob offload is configured or not.
        lock (blobRequests) Assert.Empty(blobRequests);
    }

    /// <summary>Records every request as "METHOD path?query [If-Match]", with this test's account and partition masked.</summary>
    private sealed class RequestRecorder(List<string> log, AzuritePlainTypeRegressionTests test) : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            var account = test.tableService.AccountName;
            var text = Uri.UnescapeDataString(message.Request.Uri.Path) + Uri.UnescapeDataString(message.Request.Uri.Query);
            text = text.Replace(test.pk, "{pk}", StringComparison.Ordinal);
            if (text.StartsWith(account + "/", StringComparison.Ordinal))
                text = string.Concat("{account}", text.AsSpan(account.Length));
            var condition = message.Request.Headers.TryGetValue("If-Match", out var ifMatch)
                ? " If-Match=" + (ifMatch == "*" ? "*" : "<etag>")
                : string.Empty;
            lock (log) log.Add(string.Create(CultureInfo.InvariantCulture, $"{message.Request.Method.Method} {text}{condition}"));
        }
    }
}
