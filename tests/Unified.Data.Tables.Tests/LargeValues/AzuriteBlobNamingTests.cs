using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.Providers;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Blob names of offloaded values against a LOCAL Azurite through the real Blobs SDK, which collapses
/// <c>.</c> and <c>..</c> path segments: keys that would form such segments round-trip, the orphan
/// sweeps keep their live blobs, deletes remove them, and nothing lands outside
/// <c>{table}/{partitionKey}/</c>. Every test uses its own table and its own container, and deletes both.
/// Skips like <see cref="AzuriteLargeValueIntegrationTests"/> (only when the emulator is unreachable).
/// </summary>
public sealed class AzuriteBlobNamingTests : IAsyncLifetime
{
    private const string SkipReason = "Azurite (tables and blobs) is not reachable; set "
        + AzuriteLargeValueIntegrationTests.ConnectionStringVariable + " or start Azurite";

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable(AzuriteLargeValueIntegrationTests.ConnectionStringVariable) is { Length: > 0 } configured
            ? configured
            : "UseDevelopmentStorage=true";

    private static readonly Lazy<Task<bool>> Probe =
        new(() => TestSupport.AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, TestSupport.AzuriteProbe.Required));

    private static readonly string BigText = Ascii(40_000);

    private readonly string tableName = "bn" + Guid.NewGuid().ToString("N");
    private readonly string containerName = "bn-" + Guid.NewGuid().ToString("N");
    private readonly List<MemoryCache> caches = [];

    private TableServiceClient tableService = default!;
    private BlobServiceClient blobService = default!;
    private BlobContainerClient container = default!;
    private bool available;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        available = await Probe.Value;
        if (!available) return;
        tableService = new TableServiceClient(ConnectionString);
        blobService = new BlobServiceClient(ConnectionString);
        container = blobService.GetBlobContainerClient(containerName);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cache in caches) cache.Dispose();
        if (!available) return;
        try
        {
            await tableService.DeleteTableAsync(tableName, CancellationToken.None);
        }
        catch (Azure.RequestFailedException)
        {
            // Never created.
        }
        await container.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
    }

    private TableStorage<SplitTextEntity> Store()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        return new TableStorage<SplitTextEntity>(tableService, cache, NullLogger<TableStorage<SplitTextEntity>>.Instance,
            authorizer: null,
            new UnifiedTableStorageOptions
            {
                IdNormalization = IdNormalization.AsWritten,
                TableNameResolver = _ => tableName,
                BlobOffload = new BlobOffloadOptions { ContainerName = containerName, ThresholdBytes = 1_000, CleanupGrace = TimeSpan.Zero },
            },
            blobService);
    }

    /// <summary>Every blob of the container, as the service lists it.</summary>
    private async Task<List<BlobItem>> AllBlobsAsync()
    {
        var items = new List<BlobItem>();
        if (!(await container.ExistsAsync(Ct)).Value)
            return items;
        await foreach (var item in container.GetBlobsAsync(new GetBlobsOptions(), Ct))
            items.Add(item);
        return items;
    }

    // With olderThan = 0 a blob stamped in the same service second as the sweep is kept: wait until the
    // service clock has passed the newest blob's Last-Modified (see AzuriteLargeValueIntegrationTests).
    private async Task PastTheServiceSecondAsync()
    {
        var blobs = await AllBlobsAsync();
        Assert.NotEmpty(blobs);
        var newest = blobs.Max(b => b.Properties.LastModified ?? throw new InvalidOperationException($"'{b.Name}' has no Last-Modified."));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var serviceNow = (await container.GetPropertiesAsync(cancellationToken: Ct)).GetRawResponse().Headers.Date
                ?? throw new InvalidOperationException("The blob service sent no Date header.");
            if (serviceNow > newest)
                return;
            Assert.True(DateTimeOffset.UtcNow < deadline, "The blob service clock did not pass the newest blob's Last-Modified within 30 s.");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }

    private async Task UploadAsync(string name)
    {
        var bytes = Utf8(name);
        var sha = Sha(bytes);
        await using var content = new MemoryStream(bytes);
        await new AzureOffloadBlobStore(container).UploadCreateOnlyAsync(
            name, content, new BlobReference(name, sha, bytes.LongLength, "application/octet-stream", "binary"), null, Ct);
    }

    [Theory]
    [MemberData(nameof(BlobNamingTests.AwkwardKeys), MemberType = typeof(BlobNamingTests))]
    public async Task AwkwardKeys_RoundTrip_SweepKeeps_DeleteRemoves(string key)
    {
        Assert.SkipUnless(available, SkipReason);
        var store = Store();
        var asPartition = new SplitTextEntity { Id = key + "|r", Body = BigText };
        var asRow = new SplitTextEntity { Id = "p|" + key, Body = Ascii(40_000, seed: 3) };
        await store.CreateAsync(asPartition, Ct);
        await store.CreateAsync(asRow, Ct);

        // The service stores exactly the names the rows reference, all under {table}/{pk}/.
        var segment = OffloadBlobNames.EscapeSegment(key);
        var names = (await AllBlobsAsync()).Select(b => b.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains(names, n => n.StartsWith($"{tableName}/{segment}/r/Body/", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith($"{tableName}/p/{segment}/Body/", StringComparison.Ordinal));

        await PastTheServiceSecondAsync();
        Assert.Equal(0, await store.SweepOrphanBlobsAsync(key, TimeSpan.Zero, Ct));
        Assert.Equal(0, await store.SweepOrphanBlobsAsync("p", TimeSpan.Zero, Ct));
        Assert.Equal(0, await store.SweepOrphanBlobsAsync(TimeSpan.Zero, Ct));
        Assert.Equal(names, (await AllBlobsAsync()).Select(b => b.Name).Order(StringComparer.Ordinal));

        Assert.Equal(BigText, (await store.OneAsync(asPartition.Id, Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal(asRow.Body, (await store.OneAsync(asRow.Id, Ct))!.Body, StringComparer.Ordinal);

        await store.DeleteAsync(asPartition.Id, Ct);
        await store.DeleteAsync(asRow.Id, Ct);
        Assert.Empty(await AllBlobsAsync());
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task DeletePartition_OfADotKey_RemovesItsBlobsOnly(string key)
    {
        Assert.SkipUnless(available, SkipReason);
        var store = Store();
        await store.CreateAsync(new SplitTextEntity { Id = key + "|r", Body = BigText }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = BigText }, Ct);
        await PastTheServiceSecondAsync();

        Assert.Equal(1, await store.DeletePartitionAsync(key, Ct));

        Assert.StartsWith($"{tableName}/p/r/Body/", Assert.Single(await AllBlobsAsync()).Name, StringComparison.Ordinal);
        Assert.Equal(BigText, (await store.OneAsync("p|r", Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task TableWideSweep_DeletesOrphansOfEveryPartition_KeepsLiveAndForeignBlobs()
    {
        Assert.SkipUnless(available, SkipReason);
        var store = Store();
        await store.CreateAsync(new SplitTextEntity { Id = ".|r", Body = BigText }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = BigText }, Ct);
        var live = (await AllBlobsAsync()).Select(b => b.Name).ToList();
        var orphans = new[] { ".", "..", "p", "q", "" }
            .Select(pk => ValueOffloadCoordinator.BlobName(tableName, pk, "gone", "Body", Sha(Utf8("orphan " + pk))))
            .ToList();
        foreach (var name in orphans)
            await UploadAsync(name);
        // Not the offload shape, or not this table: never deleted.
        string[] foreign =
        [
            $"{tableName}/p/readme.txt",
            $"{tableName}/p/r/Body/not-a-hash",
            $"{tableName}/a.b/gone/Body/{Sha(Utf8("raw dot"))}",
            ValueOffloadCoordinator.BlobName("othertable", "p", "gone", "Body", Sha(Utf8("other"))),
        ];
        foreach (var name in foreign)
            await UploadAsync(name);
        await PastTheServiceSecondAsync();

        Assert.Equal(orphans.Count, await store.SweepOrphanBlobsAsync(TimeSpan.Zero, Ct));

        Assert.Equal(live.Concat(foreign).Order(StringComparer.Ordinal),
            (await AllBlobsAsync()).Select(b => b.Name).Order(StringComparer.Ordinal));
        Assert.Equal(BigText, (await store.OneAsync(".|r", Ct))!.Body, StringComparer.Ordinal);

        // A partition delete leaves the foreign names of its prefix alone as well.
        await store.DeletePartitionAsync("p", Ct);
        Assert.Contains($"{tableName}/p/readme.txt", (await AllBlobsAsync()).Select(b => b.Name));
        Assert.Contains($"{tableName}/p/r/Body/not-a-hash", (await AllBlobsAsync()).Select(b => b.Name));
    }

    [Fact]
    public async Task ListPrefixes_ReturnsEachPartitionPrefixOnce()
    {
        Assert.SkipUnless(available, SkipReason);
        var store = new AzureOffloadBlobStore(container);
        Assert.Empty(await Collect(store.ListPrefixesAsync($"{tableName}/", Ct))); // no container yet

        foreach (var pk in new[] { ".", "p", "p", "" })
            await UploadAsync(ValueOffloadCoordinator.BlobName(tableName, pk, Guid.NewGuid().ToString("N"), "Body", Sha(Utf8(pk + Guid.NewGuid()))));
        await UploadAsync($"{tableName}/loose");

        Assert.Equal(
            new[] { $"{tableName}/%/", $"{tableName}/%2E/", $"{tableName}/p/" }.Order(StringComparer.Ordinal),
            (await Collect(store.ListPrefixesAsync($"{tableName}/", Ct))).Order(StringComparer.Ordinal));

        static async Task<List<string>> Collect(IAsyncEnumerable<string> source)
        {
            var list = new List<string>();
            await foreach (var item in source) list.Add(item);
            return list;
        }
    }
}
