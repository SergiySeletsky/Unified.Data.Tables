using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Unified.Data.Tables;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the hosted orphan-blob sweep (an <see cref="IHostedService"/>) and its schedule. It runs the
    /// table-wide <see cref="IOrphanBlobSweeper.SweepOrphanBlobsAsync(TimeSpan, CancellationToken)"/> of each
    /// type registered with <see cref="AddUnifiedOrphanBlobSweep{T}(IServiceCollection)"/>, one type after
    /// another, first after <see cref="OrphanBlobSweepOptions.InitialDelay"/> and then every
    /// <see cref="OrphanBlobSweepOptions.Interval"/>. A type that fails is logged and retried next round;
    /// shutdown cancels the round in progress. When <see cref="UnifiedTableStorageOptions.BlobOffload"/> is
    /// not configured the service logs that and does nothing. The first call's options win, as with
    /// <c>AddUnifiedTableStorage</c>. Every host instance that registers it sweeps; that is safe (each
    /// delete is conditional) but redundant, so a scaled-out app may register it in one instance only.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional schedule configuration.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range (see <see cref="OrphanBlobSweepOptions"/>).</exception>
    public static IServiceCollection AddUnifiedOrphanBlobSweep(
        this IServiceCollection services, Action<OrphanBlobSweepOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new OrphanBlobSweepOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        return AddSweepService(services);
    }

    /// <summary>
    /// Adds <typeparamref name="T"/>'s table to the hosted orphan-blob sweep (and registers the sweep with
    /// default options when <see cref="AddUnifiedOrphanBlobSweep(IServiceCollection, Action{OrphanBlobSweepOptions}?)"/>
    /// is not called). The sweep resolves <see cref="IStorage{T}"/>, which must implement
    /// <see cref="IOrphanBlobSweeper"/> (<c>TableStorage&lt;T&gt;</c> and <c>InMemoryStorage&lt;T&gt;</c> do;
    /// any other is skipped with a warning). Register each type that has <see cref="TableValueSplitAttribute"/>
    /// or <see cref="TableBlobContent"/> members once; repeated calls for a type are ignored.
    /// </summary>
    /// <typeparam name="T">The entity type whose table to sweep.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddUnifiedOrphanBlobSweep<T>(this IServiceCollection services)
        where T : class, IEntity, new()
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOrphanBlobSweepTarget, OrphanBlobSweepTarget<T>>());
        return AddSweepService(services);
    }

    private static IServiceCollection AddSweepService(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OrphanBlobSweepService>());
        return services;
    }
}
