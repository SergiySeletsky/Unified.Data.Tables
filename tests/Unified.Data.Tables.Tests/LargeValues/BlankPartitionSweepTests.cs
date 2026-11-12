using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A partition whose key is empty or whitespace holds offloaded blobs like any other: a write of
/// <c>"   |r"</c> stores the partition <c>"   "</c> under <see cref="IdNormalization.AsWritten"/> and
/// <c>""</c> under <see cref="IdNormalization.Normalized"/> (the id is trimmed). The per-partition sweep
/// takes such a key and normalizes it the same way, so it reaches exactly the partition the write
/// stored; only a null key is refused.
/// </summary>
public class BlankPartitionSweepTests
{
    private static readonly string BigText = Ascii(40_000, seed: 41);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The id written, the mode, the partition the write stores, and the argument the sweep is given.</summary>
    public static TheoryData<IdNormalization, string, string, string> Cases => new()
    {
        { IdNormalization.AsWritten, "   |r", "   ", "   " },
        { IdNormalization.AsWritten, "|r", "", "" },
        { IdNormalization.Normalized, "   |r", "", "   " },
        { IdNormalization.Normalized, "|r", "", "" },
    };

    private static UnifiedTableStorageOptions Options(IdNormalization mode) => new()
    {
        IdNormalization = mode,
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 },
        Cache = CachePolicy.Disabled,
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InMemory_SweepsTheBlankPartition_TheWriteStored(IdNormalization mode, string id, string storedPk, string argument)
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options(mode));
        await store.CreateAsync(new SplitTextEntity { Id = id, Body = BigText }, Ct);
        var live = Assert.Single(store.Blobs.Names);
        Assert.StartsWith($"{nameof(SplitTextEntity)}/{OffloadBlobNames.EscapeSegment(storedPk)}/", live, StringComparison.Ordinal);
        var bytes = Utf8("orphan");
        var orphan = ValueOffloadCoordinator.BlobName(nameof(SplitTextEntity), storedPk, "gone", "Body", Sha(bytes));
        store.Blobs.Seed(orphan, bytes, Sha(bytes), DateTimeOffset.UtcNow - TimeSpan.FromHours(1));

        Assert.Equal(1, await store.SweepOrphanBlobsAsync(argument, TimeSpan.FromMinutes(5), Ct));

        Assert.Equal([live], store.Blobs.Names);
        Assert.Equal(BigText, (await store.OneAsync(id, Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task InMemory_NullPartition_IsStillRefused()
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options(IdNormalization.Normalized));

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SweepOrphanBlobsAsync(null!, TimeSpan.Zero, Ct));
    }
}

/// <summary><see cref="BlankPartitionSweepTests"/> against a LOCAL Azurite, in a table and a container of the test's own.</summary>
public sealed class BlankPartitionSweepAzuriteTests : IAsyncLifetime
{
    private const string SkipReason = "Azurite (tables and blobs) is not reachable; set "
        + TestSupport.AzuriteConnection.Variable + " or start Azurite";

    private static readonly string ConnectionString = TestSupport.AzuriteConnection.ConnectionString;

    private static readonly Lazy<Task<bool>> Probe =
        new(() => TestSupport.AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, TestSupport.AzuriteProbe.Required));

    private static readonly string BigText = Ascii(40_000, seed: 43);

    private readonly string tableName = "bp" + Guid.NewGuid().ToString("N");
    private readonly string containerName = "bp-" + Guid.NewGuid().ToString("N");
    private readonly MemoryCache cache = new(new MemoryCacheOptions());
    private Azure.Data.Tables.TableServiceClient tableService = default!;
    private BlobContainerClient container = default!;
    private BlobServiceClient blobService = default!;
    private bool available;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        available = await Probe.Value;
        if (!available) return;
        tableService = new Azure.Data.Tables.TableServiceClient(ConnectionString);
        blobService = new BlobServiceClient(ConnectionString);
        container = blobService.GetBlobContainerClient(containerName);
    }

    public async ValueTask DisposeAsync()
    {
        cache.Dispose();
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

    private async Task<List<string>> NamesAsync()
    {
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(cancellationToken: Ct))
            names.Add(item.Name);
        return names;
    }

    [Theory]
    [MemberData(nameof(BlankPartitionSweepTests.Cases), MemberType = typeof(BlankPartitionSweepTests))]
    public async Task SweepsTheBlankPartition_TheWriteStored(IdNormalization mode, string id, string storedPk, string argument)
    {
        Assert.SkipUnless(available, SkipReason);
        var store = new TableStorage<SplitTextEntity>(tableService, cache, NullLogger<TableStorage<SplitTextEntity>>.Instance, authorizer: null,
            new UnifiedTableStorageOptions
            {
                IdNormalization = mode,
                TableNameResolver = _ => tableName,
                BlobOffload = new BlobOffloadOptions { ContainerName = containerName, ThresholdBytes = 1_000 },
                Cache = CachePolicy.Disabled,
            },
            blobService);
        await store.CreateAsync(new SplitTextEntity { Id = id, Body = BigText }, Ct);
        var live = Assert.Single(await NamesAsync());
        Assert.StartsWith($"{tableName}/{OffloadBlobNames.EscapeSegment(storedPk)}/", live, StringComparison.Ordinal);
        var bytes = Utf8("orphan");
        var orphan = ValueOffloadCoordinator.BlobName(tableName, storedPk, "gone", "Body", Sha(bytes));
        await using (var content = new MemoryStream(bytes))
        {
            await new AzureOffloadBlobStore(container).UploadCreateOnlyAsync(
                orphan, content, new BlobReference(orphan, Sha(bytes), bytes.LongLength, "application/octet-stream", "binary"), null, Ct);
        }
        // With olderThan = 0 a blob stamped in the sweep's own service second is kept: wait past it.
        var stamped = (await container.GetBlobClient(orphan).GetPropertiesAsync(cancellationToken: Ct)).Value.LastModified;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while ((await container.GetPropertiesAsync(cancellationToken: Ct)).GetRawResponse().Headers.Date is not { } now || now <= stamped)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The blob service clock did not pass the orphan's Last-Modified within 30 s.");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }

        Assert.Equal(1, await store.SweepOrphanBlobsAsync(argument, TimeSpan.Zero, Ct));

        Assert.Equal([live], await NamesAsync());
        Assert.Equal(BigText, (await store.OneAsync(id, Ct))!.Body, StringComparer.Ordinal);
    }
}
