using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The <see cref="InvalidOperationException"/> of a large-value store with blob offload and no blob client
/// is true for every way the client can be missing (fix wave 3, stage x1, item 4; the concern of stage c2):
/// a store constructed without one, options that ask for the host's client when none reaches the store,
/// and a connection-string call whose blob endpoint is malformed beside options registered through a
/// factory. It never tells a host that did what it advises to do it again without saying why that was not
/// enough.
/// </summary>
[Collection("OversizedCellPolicy")]
public class MissingOffloadClientMessageTests
{
    private const string MalformedBlobEndpoint =
        "BlobEndpoint=not a uri;AccountName=acct;AccountKey=YWJj;TableEndpoint=https://acct.table.core.windows.net/";

    private static TableServiceClient TableService()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(Arg.Any<string>()).Returns(Substitute.For<TableClient>());
        return service;
    }

    private static InvalidOperationException Construct(BlobOffloadOptions offload) =>
        Assert.Throws<InvalidOperationException>(() => new TableStorage<FileEntity>(TableService(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TableStorage<FileEntity>>.Instance, authorizer: null, new UnifiedTableStorageOptions { BlobOffload = offload }));

    private static void AssertTrueForEveryWay(string message)
    {
        Assert.Contains("no BlobServiceClient was supplied", message, StringComparison.Ordinal);
        Assert.Contains("BlobOffloadOptions.ServiceClient", message, StringComparison.Ordinal);
        Assert.Contains("pass one to the constructor", message, StringComparison.Ordinal);
        // The connection-string route is named with the condition under which it supplies a client.
        Assert.Contains("missing or malformed", message, StringComparison.Ordinal);
        Assert.DoesNotContain("overload of AddUnifiedTableStorage that derives one,", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoreConstructedWithoutAClient_SaysEveryWayToSupplyOne()
    {
        var ex = Construct(new BlobOffloadOptions());

        AssertTrueForEveryWay(ex.Message);
        Assert.Contains("FileEntity", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsThatAskForTheHostsClient_SayThatNoneReachedTheStore()
    {
        var ex = Construct(new BlobOffloadOptions { UseRegisteredServiceClient = true });

        AssertTrueForEveryWay(ex.Message);
        Assert.Contains("UseRegisteredServiceClient is set, but no BlobServiceClient reached this store", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConnectionStringWithAMalformedBlobEndpoint_BesideOptionsRegisteredThroughAFactory_GetsATrueMessage()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TableService());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(_ => new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });
        services.AddUnifiedTableStorage(MalformedBlobEndpoint);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStorage<FileEntity>>());

        AssertTrueForEveryWay(ex.Message);
    }
}
