#pragma warning disable UDT0001 // the experimental table store is what one of these tests resolves

using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.Capabilities;

/// <summary>
/// 0.10.0: what <c>AddUnifiedTableStorage</c> registers and builds beside the stores: the client it builds
/// runs the <see cref="UnifiedTableStorageOptions.ConfigureTableClient"/> hook and then gets the library's
/// retry observation, and the experimental <see cref="ITableStore"/> is registered over the registered
/// client. (In the policy collection: every registration writes the process-wide oversized-cell policy.)
/// </summary>
[Collection("OversizedCellPolicy")]
public class AzureRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheClientTheLibraryBuilds_RunsTheConfigureHookFirst_ThenObservesRetries()
    {
        // The hook swaps the transport in; that the store then reports a request sent once as "not my own
        // write" shows the library added its observation after the hook ran.
        var transport = new ScriptedTableTransport(_ => new ScriptedTableTransport.Answer(409,
            "{\"odata.error\":{\"code\":\"EntityAlreadyExists\"}}", ErrorCode: "EntityAlreadyExists"));
        var hookRuns = 0;
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddUnifiedTableStorage("UseDevelopmentStorage=true", o => o.ConfigureTableClient = client =>
        {
            hookRuns++;
            client.Transport = transport;
            client.Retry.MaxRetries = 0;
        });
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(0, hookRuns);
        var store = provider.GetRequiredService<IStorage<TestEntity>>();
        var duplicate = await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new TestEntity { Id = "p|r" }, Ct));

        Assert.Equal(1, hookRuns);
        Assert.False(duplicate.MayBeOwnWrite);
        Assert.NotEmpty(transport.Requests);
    }

    [Fact]
    public async Task TheCredentialOverload_RunsTheHookToo_AndAHostsOwnClientIsLeftAlone()
    {
        var hookRuns = 0;
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddUnifiedTableStorage(new Uri("https://acct.table.core.windows.net"), Substitute.For<Azure.Core.TokenCredential>(),
            o => o.ConfigureTableClient = _ => hookRuns++);
        await using (var provider = services.BuildServiceProvider())
        {
            Assert.Equal("acct", provider.GetRequiredService<TableServiceClient>().AccountName);
            Assert.Equal(1, hookRuns);
        }

        var hosts = new TableServiceClient("UseDevelopmentStorage=true");
        var own = new ServiceCollection();
        own.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        own.AddSingleton(hosts);
        own.AddUnifiedTableStorage("UseDevelopmentStorage=true", o => o.ConfigureTableClient = _ => hookRuns++);
        await using var ownProvider = own.BuildServiceProvider();

        Assert.Same(hosts, ownProvider.GetRequiredService<TableServiceClient>());
        Assert.Equal(1, hookRuns);
    }

    [Fact]
    public async Task AddUnifiedTableStorage_RegistersTheTableStore_OverTheRegisteredClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddUnifiedTableStorage("UseDevelopmentStorage=true");
        await using var provider = services.BuildServiceProvider();

        var tables = provider.GetRequiredService<ITableStore>();

        Assert.IsType<AzureTableStore>(tables);
        Assert.Same(tables, provider.GetRequiredService<ITableStore>());
        Assert.Equal("Orders", tables.GetTable("Orders").TableName);
        Assert.Throws<ArgumentNullException>(() => new AzureTableStore(null!));
        Assert.Throws<ArgumentException>(() => tables.GetTable(" "));
    }
}
