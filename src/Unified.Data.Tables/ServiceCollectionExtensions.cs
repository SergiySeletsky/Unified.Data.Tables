using Azure.Core;
using Azure.Data.Tables;
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
    /// properties are used, an <see cref="IProtectedPropertyAuthorizer"/>).
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
    /// Registers a <see cref="TableServiceClient"/> built from <paramref name="connectionString"/>
    /// alongside the cache, options, and the open-generic <see cref="IStorage{T}"/> mapping.
    /// </summary>
    public static IServiceCollection AddUnifiedTableStorage(
        this IServiceCollection services, string connectionString,
        Action<UnifiedTableStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(_ => new TableServiceClient(connectionString));
        return services.AddUnifiedTableStorage(configure);
    }

    /// <summary>
    /// Registers a <see cref="TableServiceClient"/> authenticated with a <see cref="TokenCredential"/>
    /// (e.g. a managed identity via <c>DefaultAzureCredential</c>) alongside the cache, options,
    /// and the open-generic <see cref="IStorage{T}"/> mapping.
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
        return services.AddUnifiedTableStorage(configure);
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
    /// <c>[FromKeyedServices("TableName")] IPolymorphicStorage&lt;TBase&gt;</c>.
    /// </remarks>
    /// <typeparam name="TBase">The common base type the table's rows materialize as.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="tableName">The table this store owns; also the DI key.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddUnifiedPolymorphicTable<TBase>(
        this IServiceCollection services, string tableName)
        where TBase : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        services.TryAddKeyedSingleton<IPolymorphicStorage<TBase>>(tableName, (sp, _) =>
            new PolymorphicTableStorage<TBase>(
                sp.GetRequiredService<TableServiceClient>(),
                tableName,
                sp.GetRequiredService<ILogger<PolymorphicTableStorage<TBase>>>(),
                sp.GetService<UnifiedTableStorageOptions>()));

        return services;
    }
}
