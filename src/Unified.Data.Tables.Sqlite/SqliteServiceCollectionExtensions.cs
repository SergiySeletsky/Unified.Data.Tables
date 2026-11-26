using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Unified.Data.Tables.Sqlite;

/// <summary>
/// DI helpers for the SQLite storage backend.
/// </summary>
public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// Registers the open-generic <see cref="IStorage{T}"/> → <see cref="SqliteStorage{T}"/> mapping as
    /// singletons over one database file: a drop-in replacement for <c>AddUnifiedTableStorage</c> in
    /// development, on a single machine, or in tests that need the data to outlive the process. No Azure
    /// connection is required. Also registered: the <see cref="SqliteStorageAccount"/> (one per
    /// container, closed when the container is disposed), the options (as
    /// <see cref="SqliteStorageOptions"/> and as <see cref="StorageOptions"/>) and the experimental
    /// <see cref="ITableStore"/> over every table of the database.
    /// </summary>
    /// <remarks>
    /// First registration wins, as for <c>AddUnifiedTableStorage</c>: when options are already
    /// registered, a second call changes nothing. The database is opened (and created) when the first
    /// store is resolved.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="dataSource">The path of the database file (created with its directory when missing), or <c>:memory:</c>.</param>
    /// <param name="configure">
    /// Configures the options: the ones the production registration sets (id normalization, the
    /// oversized-cell policy, the read cache, blob offload), so the stores stay in step with
    /// <c>TableStorage&lt;T&gt;</c>, and SQLite's own (<see cref="SqliteStorageOptions.Synchronous"/>,
    /// <see cref="SqliteStorageOptions.BusyTimeout"/>).
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="dataSource"/> is null, empty or whitespace.</exception>
    public static IServiceCollection AddUnifiedSqliteStorage(
        this IServiceCollection services, string dataSource, Action<SqliteStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSource);

        var options = new SqliteStorageOptions { DataSource = dataSource };
        configure?.Invoke(options);

        // First-registration-wins, mirroring AddUnifiedTableStorage and AddUnifiedInMemoryStorage: the
        // stores round-trip through the same (static) serializer, so the registration applies the same
        // process-wide policy, unless options are registered already. Only an unkeyed registration
        // counts, as for TryAddSingleton. Options another provider registered under a type of its own
        // that derives from StorageOptions count too, and are then also what the stores resolve as
        // StorageOptions (the database itself is always opened by the SqliteStorageOptions).
        if (!services.Any(d => !d.IsKeyedService && d.ServiceType == typeof(StorageOptions)))
        {
            var derived = services.LastOrDefault(d => !d.IsKeyedService && d.ServiceType.IsSubclassOf(typeof(StorageOptions)))?.ServiceType;
            if (derived is null)
                TableEntitySerializer.OversizedCellPolicy = options.OversizedCells;
            else
                services.AddSingleton(sp => (StorageOptions)sp.GetRequiredService(derived));
        }

        services.TryAddSingleton(options);
        // The base type, for whatever resolves the provider-neutral options (a store given none of its
        // own, the Identity stores' registration).
        services.TryAddSingleton<StorageOptions>(sp => sp.GetRequiredService<SqliteStorageOptions>());
        services.TryAddSingleton(sp => new SqliteStorageAccount(sp.GetRequiredService<SqliteStorageOptions>()));
        services.TryAddSingleton(typeof(IStorage<>), typeof(SqliteStorage<>));
#pragma warning disable UDT0001 // the experimental raw-table API is registered here, not used
        services.TryAddSingleton<ITableStore>(sp => sp.GetRequiredService<SqliteStorageAccount>().Tables);
#pragma warning restore UDT0001
        return services;
    }

    /// <summary>
    /// Registers one <see cref="IPolymorphicStorage{TBase}"/> backed by
    /// <see cref="SqlitePolymorphicStorage{TBase}"/>, KEYED by <paramref name="tableName"/>: the drop-in
    /// replacement for <c>AddUnifiedPolymorphicTable</c>. The key is still the table name, so a host
    /// swaps the registration line and nothing at the injection sites changes. The table lives in the
    /// database <see cref="AddUnifiedSqliteStorage"/> registered, which must be called as well.
    /// </summary>
    /// <remarks>
    /// Pass <paramref name="configure"/> whenever production configures a
    /// <see cref="StorageOptions.TypeDiscriminator"/> for this table only; without it the store reads the
    /// registered <see cref="StorageOptions"/>.
    /// </remarks>
    /// <typeparam name="TBase">The common base type the table's rows materialize as.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="tableName">The table name (a letter, then 2 to 62 letters and digits); also the DI key.</param>
    /// <param name="configure">
    /// Options for this table only. Null resolves the registered <see cref="StorageOptions"/>, or the
    /// options of the account when none is registered.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is null, empty or whitespace.</exception>
    public static IServiceCollection AddUnifiedSqlitePolymorphicTable<TBase>(
        this IServiceCollection services,
        string tableName,
        Action<StorageOptions>? configure = null)
        where TBase : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        // A per-table options instance rather than a registration: the discriminator is the only setting
        // a polymorphic store reads, and one table's type map has no business becoming the process-wide
        // default that every IStorage<T> also resolves.
        StorageOptions? local = null;
        if (configure is not null)
        {
            local = new StorageOptions();
            configure(local);
        }

        services.TryAddKeyedSingleton<IPolymorphicStorage<TBase>>(tableName, (sp, _) =>
            new SqlitePolymorphicStorage<TBase>(
                sp.GetRequiredService<SqliteStorageAccount>(), tableName, local ?? sp.GetService<StorageOptions>()));

        return services;
    }
}
