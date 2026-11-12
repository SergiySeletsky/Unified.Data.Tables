using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unified.Data.Tables.Providers;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The hosted orphan sweep against a LOCAL Azurite: the registered <see cref="IStorage{T}"/> is a
/// <see cref="TableStorage{T}"/> built with options and a blob client of its own, and the container has
/// no <see cref="UnifiedTableStorageOptions"/> at all. The sweep follows the store it resolves, so the
/// store's orphan is deleted.
/// </summary>
public sealed class HostedSweepFollowsTheStoreAzuriteTests : AzuriteCleanupTestBase
{
    [Fact]
    public async Task StoreWithItsOwnOptionsAndClient_IsSwept_WithoutRegisteredOptions()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        var body = Ascii(40_000, seed: 5);
        await store.CreateAsync(new SplitTextEntity { Id = Id("live"), Body = body }, Ct);
        var live = Assert.Single(await BlobNamesAsync());
        var bytes = Utf8("orphan");
        var orphan = ValueOffloadCoordinator.BlobName(nameof(SplitTextEntity), Pk, "gone", "Body", Sha(bytes));
        await using (var content = new MemoryStream(bytes))
        {
            await new AzureOffloadBlobStore(Container).UploadCreateOnlyAsync(
                orphan, content, new BlobReference(orphan, Sha(bytes), bytes.LongLength, "application/octet-stream", "binary"), null, Ct);
        }
        await BlobsOlderThanAsync(TimeSpan.Zero);

        var logger = new OrphanBlobSweepServiceTests.CapturingLogger();
        var services = new ServiceCollection();
        services.AddSingleton<IStorage<SplitTextEntity>>(store);
        await using var provider = services.BuildServiceProvider();
        var steps = new OrphanBlobSweepServiceTests.SteppedDelay();
        var service = new OrphanBlobSweepService(provider, [new OrphanBlobSweepTarget<SplitTextEntity>()],
            new OrphanBlobSweepOptions { InitialDelay = TimeSpan.Zero, OlderThan = TimeSpan.Zero }, logger)
        {
            Delay = steps.Delay,
        };
        try
        {
            await service.StartAsync(Ct);
            await steps.StepAsync();
            await steps.WaitingAsync();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        Assert.Equal([live], await BlobNamesAsync());
        Assert.Equal(body, (await store.OneAsync(Id("live"), Ct))!.Body, StringComparer.Ordinal);
        Assert.DoesNotContain(logger.Snapshot(), e => e.Level >= LogLevel.Warning);
    }
}
