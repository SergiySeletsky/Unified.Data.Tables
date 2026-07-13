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
    /// dev mode, or offline scenarios. No Azure connection is required. Pass
    /// <paramref name="configure"/> to apply the same <see cref="UnifiedTableStorageOptions"/> the
    /// production registration uses (id normalization, oversized-cell policy), so the fake stays
    /// behaviourally in step with <c>TableStorage&lt;T&gt;</c>.
    /// </summary>
    public static IServiceCollection AddUnifiedInMemoryStorage(
        this IServiceCollection services, Action<UnifiedTableStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new UnifiedTableStorageOptions();
        configure?.Invoke(options);

        // First-registration-wins, mirroring AddUnifiedTableStorage: the fake round-trips through
        // the same (static) serializer, so it applies the same process-wide policy.
        var optionsAlreadyRegistered = services.Any(d => d.ServiceType == typeof(UnifiedTableStorageOptions));
        if (!optionsAlreadyRegistered)
            TableEntitySerializer.OversizedCellPolicy = options.OversizedCells;

        services.TryAddSingleton(options);
        services.TryAddSingleton(typeof(IStorage<>), typeof(InMemoryStorage<>));
        return services;
    }
}
