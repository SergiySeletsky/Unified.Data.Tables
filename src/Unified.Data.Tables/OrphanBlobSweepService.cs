using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unified.Data.Tables;

/// <summary>An entity type whose store the hosted orphan-blob sweep sweeps (one registration per type).</summary>
internal interface IOrphanBlobSweepTarget
{
    Type EntityType { get; }

    /// <summary>The type's store as a sweeper; null when the registered store does not implement the capability.</summary>
    IOrphanBlobSweeper? Resolve(IServiceProvider services);
}

internal sealed class OrphanBlobSweepTarget<T> : IOrphanBlobSweepTarget where T : class, IEntity, new()
{
    public Type EntityType => typeof(T);

    public IOrphanBlobSweeper? Resolve(IServiceProvider services) =>
        services.GetRequiredService<IStorage<T>>() as IOrphanBlobSweeper;
}

/// <summary>
/// Runs the table-wide <see cref="IOrphanBlobSweeper.SweepOrphanBlobsAsync(TimeSpan, CancellationToken)"/> of
/// every registered entity type: after <see cref="OrphanBlobSweepOptions.InitialDelay"/>, then every
/// <see cref="OrphanBlobSweepOptions.Interval"/> after a round ends. One type's failure is logged and does
/// not stop the others or the next round. Each round follows the store the type's <see cref="IStorage{T}"/>
/// resolves to, whatever options are registered: a store built with options (and a blob client) of its
/// own is swept, and one of the library's stores without blob offload is skipped (logged once).
/// </summary>
internal sealed class OrphanBlobSweepService : BackgroundService
{
    private readonly IServiceProvider services;
    private readonly OrphanBlobSweepOptions options;
    private readonly IOrphanBlobSweepTarget[] targets;
    private readonly ILogger logger;

    // The types already reported as skipped for having no blob offload (each is logged once at Information).
    private readonly HashSet<Type> reportedWithoutOffload = [];

    public OrphanBlobSweepService(
        IServiceProvider services,
        IEnumerable<IOrphanBlobSweepTarget> targets,
        OrphanBlobSweepOptions? options = null,
        ILogger<OrphanBlobSweepService>? logger = null)
    {
        this.services = services;
        this.targets = [.. targets];
        this.options = options ?? new OrphanBlobSweepOptions();
        this.logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>The wait between rounds (for tests, which step it).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Never run a round inside the host's StartAsync, whatever the BackgroundService version does.
        await Task.Yield();
        if (targets.Length == 0)
        {
            logger.LogInformation("Orphan blob sweep is not running: no entity type is registered with AddUnifiedOrphanBlobSweep<T>().");
            return;
        }

        var wait = options.InitialDelay;
        while (true)
        {
            try
            {
                await Delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            foreach (var target in targets)
            {
                if (!await SweepAsync(target, stoppingToken).ConfigureAwait(false))
                    return;
            }
            wait = options.Interval;
        }
    }

    // False when the host is stopping.
    private async Task<bool> SweepAsync(IOrphanBlobSweepTarget target, CancellationToken stoppingToken)
    {
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            try
            {
                if (target.Resolve(scope.ServiceProvider) is not { } sweeper)
                {
                    logger.LogWarning("Orphan blob sweep skips {EntityType}: its IStorage<T> does not implement {Capability}.",
                        target.EntityType.Name, nameof(IOrphanBlobSweeper));
                    return true;
                }
                if (sweeper is IBlobOffloadState { HasBlobOffload: false })
                {
                    if (reportedWithoutOffload.Add(target.EntityType))
                        logger.LogInformation("Orphan blob sweep skips {EntityType}: its store has no blob offload (BlobOffload is not set in the options it was built with, or the type has no large-value member).",
                            target.EntityType.Name);
                    else
                        logger.LogDebug("Orphan blob sweep skips {EntityType}: its store has no blob offload.", target.EntityType.Name);
                    return true;
                }
                var deleted = await sweeper.SweepOrphanBlobsAsync(options.OlderThan, stoppingToken).ConfigureAwait(false);
                if (deleted > 0)
                    logger.LogInformation("Orphan blob sweep of {EntityType} deleted {Count} blob(s).", target.EntityType.Name, deleted);
                else
                    logger.LogDebug("Orphan blob sweep of {EntityType} deleted no blobs.", target.EntityType.Name);
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
#pragma warning disable CA1031 // one type's failure (a resolution error, a storage error, a timeout) must not stop the others
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogError(ex, "Orphan blob sweep of {EntityType} failed; the next round retries it.", target.EntityType.Name);
                return true;
            }
        }
    }
}
