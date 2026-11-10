using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Unified.Data.Tables;

/// <summary>
/// DI helpers for wiring the unified Azure Table Storage layer.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory cache, the configured <see cref="UnifiedTableStorageOptions"/>, and
    /// the open-generic <see cref="IStorage{T}"/> → <see cref="TableStorage{T}"/> mapping. The
    /// caller must separately register a <see cref="TableServiceClient"/> (and, if protected
    /// properties are used, an <see cref="IProtectedPropertyAuthorizer"/>). A host that sets
    /// <see cref="UnifiedTableStorageOptions.BlobOffload"/> registers a <see cref="BlobServiceClient"/>
    /// the same way it registers its <see cref="TableServiceClient"/>; without one, resolving a store
    /// throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public static IServiceCollection AddUnifiedTableStorage(
        this IServiceCollection services, Action<UnifiedTableStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new UnifiedTableStorageOptions();
        configure?.Invoke(options);

        // The serializer is static, so its oversized-cell policy is process-wide state. Mirror
        // TryAddSingleton's first-registration-wins semantics: only the call whose options actually
        // register also applies the policy, so a later bare AddUnifiedTableStorage() from another
        // module cannot silently reset an explicitly configured policy.
        var optionsAlreadyRegistered = services.Any(d => d.ServiceType == typeof(UnifiedTableStorageOptions));
        if (!optionsAlreadyRegistered)
            TableEntitySerializer.OversizedCellPolicy = options.OversizedCells;

        services.AddMemoryCache();
        services.TryAddSingleton(options);
        services.TryAddSingleton(typeof(IStorage<>), typeof(TableStorage<>));
        return services;
    }

    /// <summary>
    /// Registers a <see cref="TableServiceClient"/> and a <see cref="BlobServiceClient"/> (used only by
    /// <see cref="UnifiedTableStorageOptions.BlobOffload"/>) built from <paramref name="connectionString"/>
    /// alongside the cache, options, and the open-generic <see cref="IStorage{T}"/> mapping. A client
    /// already registered is kept, and no blob client is registered for a connection string that has
    /// no Azure Storage blob endpoint (a table-only endpoint with a SAS, a Cosmos DB Table API account).
    /// </summary>
    public static IServiceCollection AddUnifiedTableStorage(
        this IServiceCollection services, string connectionString,
        Action<UnifiedTableStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(_ => new TableServiceClient(connectionString));
        // Only an Azure Storage connection string a blob client can be built from: a table-only one (an
        // endpoint and a SAS) or a Cosmos DB Table API one must keep resolving its stores exactly as before.
        if (CanBuildBlobClient(connectionString))
            services.TryAddSingleton(_ => new BlobServiceClient(connectionString));
        return services.AddUnifiedTableStorage(configure);
    }

    /// <summary>
    /// Registers a <see cref="TableServiceClient"/> authenticated with a <see cref="TokenCredential"/>
    /// (e.g. a managed identity via <c>DefaultAzureCredential</c>) alongside the cache, options,
    /// and the open-generic <see cref="IStorage{T}"/> mapping. When the endpoint's host is an Azure
    /// Storage table host (<c>{account}.table.core.{suffix}</c>, in any cloud), a
    /// <see cref="BlobServiceClient"/> for the same account (<c>.blob.core.</c>) and credential is
    /// registered too, for <see cref="UnifiedTableStorageOptions.BlobOffload"/>; for any other host (Cosmos
    /// DB Table API, an emulator, a custom domain) register the <see cref="BlobServiceClient"/> yourself.
    /// A client already registered is kept.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The table endpoint, e.g. <c>https://{account}.table.core.windows.net</c>.</param>
    /// <param name="credential">The Azure credential to authenticate with.</param>
    /// <param name="configure">Optional options configuration.</param>
    public static IServiceCollection AddUnifiedTableStorage(
        this IServiceCollection services, Uri endpoint, TokenCredential credential,
        Action<UnifiedTableStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        services.TryAddSingleton(_ => new TableServiceClient(endpoint, credential));
        if (BlobEndpointFor(endpoint) is { } blobEndpoint)
            services.TryAddSingleton(_ => new BlobServiceClient(blobEndpoint, credential));
        return services.AddUnifiedTableStorage(configure);
    }

    // True for an Azure Storage connection string with a blob endpoint (explicit, or derived from the
    // account). A string whose table endpoint is not an Azure Storage table host (Cosmos DB Table API, a
    // custom domain) and that names no blob endpoint gets none. Building the client only parses.
    private static bool CanBuildBlobClient(string connectionString)
    {
        string? tableEndpoint = null;
        var blobEndpoint = false;
        foreach (var setting in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = setting.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = setting[..eq].Trim();
            if (key.Equals("BlobEndpoint", StringComparison.OrdinalIgnoreCase)) blobEndpoint = true;
            else if (key.Equals("TableEndpoint", StringComparison.OrdinalIgnoreCase)) tableEndpoint = setting[(eq + 1)..].Trim();
        }
        if (!blobEndpoint && tableEndpoint is not null
            && (!Uri.TryCreate(tableEndpoint, UriKind.Absolute, out var table) || !IsAzureStorageTableHost(table.Host)))
            return false;

        try
        {
            _ = new BlobServiceClient(connectionString);
            return true;
        }
#pragma warning disable CA1031 // the SDK throws several types (even NullReferenceException) for a string without a blob endpoint
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private const string StorageTableHostPart = ".table.core.";

    // An Azure Storage table host in any cloud: {account}.table.core.windows.net, .core.chinacloudapi.cn,
    // .core.usgovcloudapi.net, ... A Cosmos DB Table API host ({account}.table.cosmos.azure.com) is not one.
    private static bool IsAzureStorageTableHost(string host) =>
        host.Contains(StorageTableHostPart, StringComparison.OrdinalIgnoreCase);

    // https://{account}.table.core.{suffix} -> https://{account}.blob.core.{suffix}; null for any other
    // host (Cosmos DB, an emulator, a custom domain), whose blob endpoint cannot be derived.
    private static Uri? BlobEndpointFor(Uri tableEndpoint)
    {
        var host = tableEndpoint.Host;
        var at = host.IndexOf(StorageTableHostPart, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;
        var builder = new UriBuilder(tableEndpoint)
        {
            Host = host[..at] + ".blob.core." + host[(at + StorageTableHostPart.Length)..],
        };
        return builder.Uri;
    }

    /// <summary>
    /// Registers one <see cref="IPolymorphicStorage{TBase}"/> over <paramref name="tableName"/>,
    /// KEYED by that table name. Requires a registered <see cref="TableServiceClient"/> — call an
    /// <c>AddUnifiedTableStorage</c> overload first.
    /// </summary>
    /// <remarks>
    /// Keyed rather than open-generic because one base type routinely addresses several tables (an
    /// event base is both the state-event store and the transaction store), and an open-generic
    /// registration can only bind one. Resolve with
    /// <c>[FromKeyedServices("TableName")] IPolymorphicStorage&lt;TBase&gt;</c>. A key attribute needs
    /// a compile-time constant, so a table name only known at runtime must be resolved imperatively
    /// with <c>IServiceProvider.GetRequiredKeyedService&lt;IPolymorphicStorage&lt;TBase&gt;&gt;(name)</c>.
    /// <para>
    /// <b>One token namespace for the whole process.</b> Unless <paramref name="configure"/> is
    /// supplied, every polymorphic store resolves the single registered
    /// <see cref="UnifiedTableStorageOptions"/> and therefore shares ONE
    /// <see cref="ITypeDiscriminator"/> — tokens are global, not per-table. So
    /// <c>new TypeDiscriminatorMap().MapAssignableTo&lt;IEvent&gt;(asm).MapAssignableTo&lt;ICommand&gt;(asm)</c>
    /// throws at startup on any simple name the two hierarchies share, and two different tables
    /// cannot each map their own <c>Created</c>. Give the colliding types explicit tokens, pass a
    /// naming function, or hand one table its own options through <paramref name="configure"/>.
    /// </para>
    /// </remarks>
    /// <typeparam name="TBase">The common base type the table's rows materialize as.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="tableName">The table this store owns; also the DI key.</param>
    /// <param name="configure">
    /// Options for this table only — chiefly its <see cref="ITypeDiscriminator"/>. Null resolves the
    /// registered <see cref="UnifiedTableStorageOptions"/>, or the defaults when none is registered.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddUnifiedPolymorphicTable<TBase>(
        this IServiceCollection services,
        string tableName,
        Action<UnifiedTableStorageOptions>? configure = null)
        where TBase : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        // A per-table options instance rather than a registration: the discriminator is the only
        // setting a polymorphic store reads, and one table's type map has no business becoming the
        // process-wide default that every IStorage<T> also resolves.
        UnifiedTableStorageOptions? local = null;
        if (configure is not null)
        {
            local = new UnifiedTableStorageOptions();
            configure(local);
        }

        services.TryAddKeyedSingleton<IPolymorphicStorage<TBase>>(tableName, (sp, _) =>
            new PolymorphicTableStorage<TBase>(
                sp.GetRequiredService<TableServiceClient>(),
                tableName,
                sp.GetRequiredService<ILogger<PolymorphicTableStorage<TBase>>>(),
                local ?? sp.GetService<UnifiedTableStorageOptions>()));

        return services;
    }
}
