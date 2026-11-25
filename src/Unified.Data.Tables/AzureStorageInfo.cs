using Azure.Data.Tables;

namespace Unified.Data.Tables;

/// <summary>
/// What a store of the Azure provider states about itself (<see cref="StorageInfo"/>), worked out once
/// when the store is constructed, from its client and its options alone: no request is sent.
/// </summary>
internal static class AzureStorageInfo
{
    /// <summary>The provider's name in <see cref="StorageInfo.ProviderName"/>.</summary>
    internal const string ProviderName = "AzureTables";

    /// <summary>The largest value one cell holds: 64 KiB (a string is UTF-16, so 32 K characters).</summary>
    internal const long MaxCellBytes = 64 * 1024;

    // The hosts of Azure Storage's table service: the public cloud, the sovereign clouds, and the
    // DNS-zone endpoints of the public cloud. An account's secondary endpoint has the same suffix.
    private static readonly string[] StorageTableHostSuffixes =
    [
        ".table.core.windows.net",
        ".table.core.chinacloudapi.cn",
        ".table.core.usgovcloudapi.net",
        ".table.core.cloudapi.de",
        ".table.storage.azure.net",
    ];

    /// <summary>
    /// The info of a store over <paramref name="tableName"/>. The limits are the ones the library plans
    /// and refuses by, not a measurement of the endpoint.
    /// </summary>
    /// <param name="serviceClient">The client the store sends its requests with.</param>
    /// <param name="tableName">The table the store reads and writes.</param>
    /// <param name="options">The store's options (for a declared durability).</param>
    /// <param name="readCacheEnabled">Whether reads of the store can be answered from its cache.</param>
    internal static StorageInfo Describe(
        TableServiceClient serviceClient, string tableName, UnifiedTableStorageOptions options, bool readCacheEnabled) => new()
    {
        ProviderName = ProviderName,
        TableName = tableName,
        Durability = options.DeclaredDurability ?? DurabilityOf(serviceClient),
        MaxBatchRows = BatchPlanner.DefaultMaxCount,
        MaxBatchBytes = BatchPlanner.DefaultMaxBytes,
        MaxCellBytes = MaxCellBytes,
        MaxRowBytes = TableRequestSize.MaxEntityBytes,
        ReadCacheMayServeStaleRows = readCacheEnabled,
        SinglePartitionTransactions = true,
    };

    // Replicated only for a host that is positively Azure Storage's table service, which keeps every
    // acknowledged write on several machines. Anything else is not known: an emulator (Azurite on
    // 127.0.0.1 or behind a container's host name), a custom domain, a proxy, Cosmos DB's Table API, a
    // test double that reports no endpoint.
    private static Durability DurabilityOf(TableServiceClient serviceClient)
    {
        string? host;
#pragma warning disable CA1031 // a client that cannot report its endpoint states nothing about durability
        try
        {
            host = serviceClient.Uri?.Host;
        }
        catch (Exception)
        {
            host = null;
        }
#pragma warning restore CA1031
        return IsAzureStorageTableHost(host) ? Durability.Replicated : Durability.Unknown;
    }

    /// <summary>True for <c>{account}.table.core.windows.net</c> and its sovereign-cloud and DNS-zone equivalents.</summary>
    internal static bool IsAzureStorageTableHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
            return false;
        foreach (var suffix in StorageTableHostSuffixes)
        {
            // An account name comes before the suffix: the bare suffix is not a storage host.
            if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
