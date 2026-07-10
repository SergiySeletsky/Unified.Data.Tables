using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Unified.Data.Tables.InMemory;

/// <summary>
/// DI helpers for the in-memory storage backend.
/// </summary>
public static class InMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the open-generic <see cref="IStorage{T}"/> → <see cref="InMemoryStorage{T}"/>
    /// mapping as singletons — a drop-in replacement for <c>AddUnifiedTableStorage</c> in tests,
    /// dev mode, or offline scenarios. No Azure connection is required.
    /// </summary>
    public static IServiceCollection AddUnifiedInMemoryStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(typeof(IStorage<>), typeof(InMemoryStorage<>));
        return services;
    }
}
