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
    /// <paramref name="configure"/> to apply the same <see cref="StorageOptions"/> the
    /// production registration uses (id normalization, oversized-cell policy), so the fake stays
    /// behaviourally in step with <c>TableStorage&lt;T&gt;</c>.
    /// </summary>
    public static IServiceCollection AddUnifiedInMemoryStorage(
        this IServiceCollection services, Action<StorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new StorageOptions();
        configure?.Invoke(options);

        // First-registration-wins, mirroring AddUnifiedTableStorage: the fake round-trips through
        // the same (static) serializer, so it applies the same process-wide policy. Only an unkeyed
        // registration counts, as for TryAddSingleton: a keyed one of the host is never what a store receives.
        var optionsAlreadyRegistered = KeepRegisteredOptions(services);
        if (!optionsAlreadyRegistered)
        {
            TableEntitySerializer.OversizedCellPolicy = options.OversizedCells;
            // Chosen when the container resolves them, not now: options the host registers itself under a
            // type derived from StorageOptions (UnifiedTableStorageOptions) AFTER this call are still the
            // ones the stores receive, as they were while the stores took that type. A provider's own
            // options registered beside these later (a second provider's call) are not.
            var registration = ServiceDescriptor.Singleton<StorageOptions>(sp => HostRegisteredOptions(services, sp) ?? options);
            ProviderRegisteredOptions.NoteFallback(registration, options);
            services.Add(registration);
        }

        services.TryAddSingleton(typeof(IStorage<>), typeof(InMemoryStorage<>));
        return services;
    }

    /// <summary>
    /// Registers one <see cref="IPolymorphicStorage{TBase}"/> backed by
    /// <see cref="InMemoryPolymorphicStorage{TBase}"/>, KEYED by <paramref name="tableName"/> — the
    /// drop-in replacement for <c>AddUnifiedPolymorphicTable</c>. The key is still the table name so
    /// a host swaps the registration line and nothing at the injection sites changes.
    /// </summary>
    /// <remarks>
    /// Pass <paramref name="configure"/> whenever production configures an
    /// <see cref="StorageOptions.TypeDiscriminator"/>. Without it the fake resolves the
    /// registered <see cref="StorageOptions"/> (or, when none is registered, the options the host
    /// registered under a type derived from it, such as <c>UnifiedTableStorageOptions</c>, in any order),
    /// and a test host that registers ONLY this (no options, no <see cref="AddUnifiedInMemoryStorage"/>)
    /// has none — so it silently falls back to
    /// <c>AssemblyQualifiedTypeDiscriminator</c> while production uses a
    /// <c>TypeDiscriminatorMap</c>. Nothing fails; the tokens simply differ, in the one place no
    /// assertion looks.
    /// </remarks>
    /// <typeparam name="TBase">The common base type the table's rows materialize as.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="tableName">The logical table name; also the DI key.</param>
    /// <param name="configure">
    /// Options for this table only. Null resolves the registered
    /// <see cref="StorageOptions"/>, or the defaults when none is registered.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddUnifiedInMemoryPolymorphicTable<TBase>(
        this IServiceCollection services,
        string tableName,
        Action<StorageOptions>? configure = null)
        where TBase : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var local = Configured(configure);
        if (local is null)
            KeepRegisteredOptions(services);

        // The table name reaches the store, not just the DI key: it is what DuplicateKeyException
        // names, and the fake must name the same string the Azure store would.
        services.TryAddKeyedSingleton<IPolymorphicStorage<TBase>>(tableName, (sp, _) =>
            new InMemoryPolymorphicStorage<TBase>(
                tableName, local ?? sp.GetService<StorageOptions>() ?? HostRegisteredOptions(services, sp)));

        return services;
    }

    // True when options are already registered (unkeyed), so the caller's own are not the ones the stores
    // are given. Options the host or another provider registered under a type of their own that derives
    // from StorageOptions (the Azure provider's UnifiedTableStorageOptions, which this package can no
    // longer name) count, as they did while that was the one options type: they are then also registered
    // as StorageOptions, the type the in-memory stores resolve, so the stores still receive them.
    private static bool KeepRegisteredOptions(IServiceCollection services)
    {
        if (services.Any(d => !d.IsKeyedService && d.ServiceType == typeof(StorageOptions)))
            return true;
        var derived = services.LastOrDefault(d => !d.IsKeyedService && d.ServiceType.IsSubclassOf(typeof(StorageOptions)))?.ServiceType;
        if (derived is null)
            return false;
        services.AddSingleton(sp => (StorageOptions)sp.GetRequiredService(derived));
        return true;
    }

    // At resolve time: the options the host registered itself (unkeyed) under a type derived from
    // StorageOptions, the last of them as the container resolves it, whenever that was registered. Options
    // a provider's registration call created are not the host's and are skipped (see ProviderRegisteredOptions).
    private static StorageOptions? HostRegisteredOptions(IServiceCollection services, IServiceProvider sp)
    {
        var derived = services.LastOrDefault(d =>
            !d.IsKeyedService
            && d.ServiceType.IsSubclassOf(typeof(StorageOptions))
            && !ProviderRegisteredOptions.IsProviderOwned(d.ImplementationInstance))?.ServiceType;
        return derived is null ? null : (StorageOptions?)sp.GetService(derived);
    }

    // A per-table options instance rather than a registration: the discriminator is the only setting
    // a polymorphic store reads, and one table's type map has no business becoming the process-wide
    // default that every IStorage<T> also resolves.
    private static StorageOptions? Configured(Action<StorageOptions>? configure)
    {
        if (configure is null)
            return null;

        var options = new StorageOptions();
        configure(options);
        return options;
    }
}
