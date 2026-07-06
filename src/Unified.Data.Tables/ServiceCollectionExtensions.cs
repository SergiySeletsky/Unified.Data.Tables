using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Unified.Data.Tables;

/// <summary>
/// DI helpers for wiring the unified Azure Table Storage layer.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory cache and the open-generic <see cref="IStorage{T}"/> →
    /// <see cref="TableStorage{T}"/> mapping. The caller must separately register a
    /// <see cref="TableServiceClient"/> (and, if protected properties are used, an
    /// <see cref="IProtectedPropertyAuthorizer"/>).
    /// </summary>
    public static IServiceCollection AddUnifiedTableStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();
        services.TryAddSingleton(typeof(IStorage<>), typeof(TableStorage<>));
        return services;
    }

    /// <summary>
    /// Registers a <see cref="TableServiceClient"/> built from <paramref name="connectionString"/>
    /// alongside the in-memory cache and the open-generic <see cref="IStorage{T}"/> mapping.
    /// </summary>
    public static IServiceCollection AddUnifiedTableStorage(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(_ => new TableServiceClient(connectionString));
        return services.AddUnifiedTableStorage();
    }
}
