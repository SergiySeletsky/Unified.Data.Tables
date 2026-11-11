using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Every order of up to three <c>AddUnifiedTableStorage</c> calls (three overloads, four ways to configure
/// blob offload each), with and without a <c>BlobServiceClient</c> of the host, and with the in-memory
/// registration before or after them: the offload client is the one the documented order of choice gives,
/// and nothing is registered twice. The reviewer's matrix of the registration question (lens "cache" of the
/// review of fix wave 2), which passed then and must keep passing with the fix of CACHE-1; the options a
/// host registers itself are covered by <see cref="RegisteredOptionsOffloadClientTests"/>.
/// </summary>
[Collection("OversizedCellPolicy")]
public class OffloadRegistrationOrderMatrixTests
{
    private enum Overload
    {
        Plain,
        ConnectionString,
        Credential,
    }

    private enum Offload
    {
        None,
        Set,
        Explicit,
        UseHost,
    }

    private sealed record Call(Overload Overload, Offload Offload);

    private static readonly string[] ConnectionStrings =
    [
        "UseDevelopmentStorage=true",
        "DefaultEndpointsProtocol=https;AccountName=acct1;AccountKey=YWJj;EndpointSuffix=core.windows.net",
        "DefaultEndpointsProtocol=https;AccountName=acct2;AccountKey=YWJj;EndpointSuffix=core.windows.net",
    ];

    private static readonly string[] ConnectionAccounts = ["devstoreaccount1", "acct1", "acct2"];

    private static readonly Uri[] Endpoints =
    [
        new("https://cred0.table.core.windows.net"),
        new("https://cred1.table.core.windows.net"),
        new("https://cred2.table.core.windows.net"),
    ];

    private sealed class DummyCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private static BlobServiceClient Recording()
    {
        var blobs = Substitute.For<BlobServiceClient>();
        blobs.GetBlobContainerClient(Arg.Any<string>()).Returns(Substitute.For<BlobContainerClient>());
        return blobs;
    }

    private static IEnumerable<Call> AllCalls() =>
        from overload in Enum.GetValues<Overload>()
        from offload in Enum.GetValues<Offload>()
        select new Call(overload, offload);

    /// <summary>The client the documented order of choice picks: a name, or "none".</summary>
    private static string Expected(IReadOnlyList<Call> calls, bool hostHasAClient)
    {
        var first = calls[0];
        if (first.Offload == Offload.None)
            return "none"; // the registered options do not ask for blob offload: no later call changes that
        // The first call supplies the client when it can; that choice is kept, also when it yields none.
        if (Supplies(first, 0, hostHasAClient, out var chosen, out var choiceMade) || choiceMade)
            return chosen;
        // Otherwise the first later call that can.
        for (var i = 1; i < calls.Count; i++)
        {
            if (Supplies(calls[i], i, hostHasAClient, out chosen, out choiceMade) || choiceMade)
                return chosen;
        }
        return "none";
    }

    // What one call contributes by the rule "its ServiceClient, else the host's client when it says so, else
    // the client its overload derives". choiceMade: the call registered a choice that later calls must keep
    // (the host's client, even when the host has none).
    private static bool Supplies(Call call, int ordinal, bool hostHasAClient, out string chosen, out bool choiceMade)
    {
        choiceMade = false;
        chosen = "none";
        switch (call.Offload)
        {
            case Offload.Explicit:
                chosen = "explicit" + ordinal;
                return true;
            case Offload.UseHost:
                choiceMade = true;
                chosen = hostHasAClient ? "host" : "none";
                return hostHasAClient;
        }
        switch (call.Overload)
        {
            case Overload.ConnectionString:
                chosen = ConnectionAccounts[ordinal];
                return true;
            case Overload.Credential:
                chosen = "cred" + ordinal;
                return true;
            default:
                return false;
        }
    }

    private static string Describe(IEnumerable<Call> calls, bool hostHasAClient, string inMemory) =>
        string.Join(" then ", calls.Select(c => $"{c.Overload}/{c.Offload}")) + (hostHasAClient ? " (host has a client)" : " (host has none)") + inMemory;

    private static void Check(IReadOnlyList<Call> calls, bool hostHasAClient, int inMemoryAt, List<string> wrong)
    {
        var services = new ServiceCollection();
        var tables = Substitute.For<TableServiceClient>();
        tables.GetTableClient(Arg.Any<string>()).Returns(Substitute.For<TableClient>());
        services.AddSingleton(tables);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var host = Recording();
        if (hostHasAClient)
            services.AddSingleton(host);
        var explicitClients = calls.Select(_ => Recording()).ToArray();
        UnifiedTableStorageOptions? firstOptions = null;
        var what = Describe(calls, hostHasAClient, inMemoryAt < 0 ? "" : $", in-memory registered at position {inMemoryAt}");

        try
        {
            for (var i = 0; i <= calls.Count; i++)
            {
                if (i == inMemoryAt)
                    services.AddUnifiedInMemoryStorage(o => firstOptions ??= o);
                if (i == calls.Count)
                    break;
                var call = calls[i];
                var ordinal = i;
                Action<UnifiedTableStorageOptions> configure = o =>
                {
                    firstOptions ??= o;
                    o.BlobOffload = call.Offload switch
                    {
                        Offload.Set => new BlobOffloadOptions(),
                        Offload.Explicit => new BlobOffloadOptions { ServiceClient = explicitClients[ordinal] },
                        Offload.UseHost => new BlobOffloadOptions { UseRegisteredServiceClient = true },
                        _ => null,
                    };
                };
                switch (call.Overload)
                {
                    case Overload.Plain:
                        services.AddUnifiedTableStorage(configure);
                        break;
                    case Overload.ConnectionString:
                        services.AddUnifiedTableStorage(ConnectionStrings[ordinal], configure);
                        break;
                    default:
                        services.AddUnifiedTableStorage(Endpoints[ordinal], new DummyCredential(), configure);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            wrong.Add($"{what}: registration threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        using var provider = services.BuildServiceProvider();

        // The first registration's options are the registered ones, registered once.
        if (!ReferenceEquals(firstOptions, provider.GetRequiredService<UnifiedTableStorageOptions>()))
            wrong.Add($"{what}: the registered options are not the first call's");
        foreach (var (type, name) in new[] { (typeof(UnifiedTableStorageOptions), "options"), (typeof(IStorage<>), "IStorage<>"), (typeof(TableServiceClient), "TableServiceClient"), (typeof(IMemoryCache), "IMemoryCache") })
        {
            var count = services.Count(d => !d.IsKeyedService && d.ServiceType == type);
            if (count > 1)
                wrong.Add($"{what}: {name} is registered {count} times");
        }
        if (services.Count(d => d.IsKeyedService && d.ServiceType == typeof(BlobServiceClient)) > 1)
            wrong.Add($"{what}: the offload client is registered more than once");
        // The library never registers or replaces the unkeyed BlobServiceClient.
        var unkeyed = provider.GetService<BlobServiceClient>();
        if (!ReferenceEquals(unkeyed, hostHasAClient ? host : null))
            wrong.Add($"{what}: the unkeyed BlobServiceClient is not the host's own registration");

        // With the in-memory store registered first the stores are in-memory ones and need no blob client:
        // only that they resolve is checked (what the later table calls do then is finding CACHE-4).
        var inMemoryWins = inMemoryAt == 0;
        if (inMemoryWins)
        {
            if (provider.GetRequiredService<IStorage<FileEntity>>() is not InMemoryStorage<FileEntity>)
                wrong.Add($"{what}: the first IStorage<> registration (in-memory) did not win");
            return;
        }

        var keyed = provider.GetKeyedService<BlobServiceClient>(ServiceCollectionExtensions.OffloadBlobServiceClientKey);
        var actual = keyed is null ? "none"
            : ReferenceEquals(keyed, host) ? "host"
            : Array.IndexOf(explicitClients, keyed) is >= 0 and var index ? "explicit" + index
            : keyed.AccountName.StartsWith("cred", StringComparison.Ordinal) ? keyed.AccountName
            : keyed.AccountName;
        var expected = Expected(calls, hostHasAClient);
        if (actual != expected)
            wrong.Add($"{what}: the offload client is '{actual}', the documented order gives '{expected}'");

        // A plain store always resolves; a large-value store resolves exactly when offload is off or a client was found.
        if (provider.GetRequiredService<IStorage<PlainEntity>>() is not TableStorage<PlainEntity>)
            wrong.Add($"{what}: a plain store did not resolve as TableStorage");
        var offloadOn = calls[0].Offload != Offload.None;
        try
        {
            _ = provider.GetRequiredService<IStorage<FileEntity>>();
            if (offloadOn && expected == "none")
                wrong.Add($"{what}: a large-value store resolved although offload is on and no client was found");
        }
        catch (InvalidOperationException ex)
        {
            if (!offloadOn || expected != "none")
                wrong.Add($"{what}: a large-value store failed to resolve: {ex.Message}");
            else if (!ex.Message.Contains("no BlobServiceClient was supplied", StringComparison.Ordinal))
                wrong.Add($"{what}: a large-value store failed with another message: {ex.Message}");
        }
    }

    [Fact]
    public void EveryOrderOfUpToThreeCalls_FollowsTheDocumentedOrderOfChoice()
    {
        var all = AllCalls().ToList();
        var wrong = new List<string>();
        var cases = 0;
        foreach (var hostHasAClient in new[] { false, true })
        {
            foreach (var first in all)
            {
                Check([first], hostHasAClient, -1, wrong);
                cases++;
                foreach (var second in all)
                {
                    Check([first, second], hostHasAClient, -1, wrong);
                    cases++;
                    // Three calls: only the sequences in which the first two could not supply a client are
                    // interesting (otherwise the third changes nothing, which the two-call cases show).
                    if (first.Offload == Offload.Set && first.Overload == Overload.Plain
                        && second.Overload == Overload.Plain && second.Offload is Offload.None or Offload.Set)
                    {
                        foreach (var third in all)
                        {
                            Check([first, second, third], hostHasAClient, -1, wrong);
                            cases++;
                        }
                    }
                }
            }
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {cases} registration sequences differ:\n" + string.Join("\n", wrong.Take(40)));
        Assert.Equal(360, cases);
    }

    [Fact]
    public void InMemoryRegistrationBeforeOrAfter_TheFirstStoreRegistrationWins_AndNothingIsRegisteredTwice()
    {
        var all = AllCalls().ToList();
        var wrong = new List<string>();
        foreach (var hostHasAClient in new[] { false, true })
        {
            foreach (var call in all)
            {
                // Well-formed strings only: a later table call after the in-memory store derives a client
                // nobody uses (CACHE-4), which this control does not look at.
                Check([call], hostHasAClient, 0, wrong);
                // The in-memory call comes second: the table registration is the first, and nothing changes.
                Check([call], hostHasAClient, 1, wrong);
            }
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} registration sequences differ:\n" + string.Join("\n", wrong.Take(40)));
    }
}
