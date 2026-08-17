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

    /// <summary>
    /// Registers one <see cref="IPolymorphicStorage{TBase}"/> backed by
    /// <see cref="InMemoryPolymorphicStorage{TBase}"/>, KEYED by <paramref name="tableName"/> — the
    /// drop-in replacement for <c>AddUnifiedPolymorphicTable</c>. The key is still the table name so
    /// a host swaps the registration line and nothing at the injection sites changes.
    /// </summary>
    /// <typeparam name="TBase">The common base type the table's rows materialize as.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="tableName">The logical table name; also the DI key.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddUnifiedInMemoryPolymorphicTable<TBase>(
        this IServiceCollection services, string tableName)
        where TBase : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        services.TryAddKeyedSingleton<IPolymorphicStorage<TBase>>(tableName, (sp, _) =>
            new InMemoryPolymorphicStorage<TBase>(sp.GetService<UnifiedTableStorageOptions>()));

        return services;
    }
}
