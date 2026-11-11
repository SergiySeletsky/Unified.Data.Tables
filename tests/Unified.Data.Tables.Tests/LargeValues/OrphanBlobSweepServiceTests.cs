using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// <c>AddUnifiedOrphanBlobSweep</c>: the hosted service that runs the table-wide
/// <see cref="IOrphanBlobSweeper.SweepOrphanBlobsAsync(TimeSpan, CancellationToken)"/> of every registered
/// entity type on an interval. Time is stepped through the service's delay seam, so every round is
/// deterministic.
/// </summary>
// The rig registers through AddUnifiedInMemoryStorage, which applies its options to the process-wide
// TableEntitySerializer.OversizedCellPolicy: outside this collection it reset the policy underneath
// the policy tests while they ran.
[Collection("OversizedCellPolicy")]
public class OrphanBlobSweepServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BlobOffloadOptions Offload() => new() { ThresholdBytes = 1_000 };

    /// <summary>A delay the test releases: each call is queued until the test steps past it.</summary>
    internal sealed class SteppedDelay
    {
        private readonly Channel<(TimeSpan Delay, TaskCompletionSource Release)> waits =
            Channel.CreateUnbounded<(TimeSpan, TaskCompletionSource)>();

        public List<TimeSpan> Requested { get; } = [];

        public Task Delay(TimeSpan delay, CancellationToken ct)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => release.TrySetCanceled(ct));
            lock (Requested) Requested.Add(delay);
            waits.Writer.TryWrite((delay, release));
            return release.Task;
        }

        /// <summary>Waits until the service is waiting, then lets it run on. Returns the delay it asked for.</summary>
        public async Task<TimeSpan> StepAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(Wait);
            var (delay, release) = await waits.Reader.ReadAsync(timeout.Token);
            release.TrySetResult();
            return delay;
        }

        /// <summary>Waits until the service is waiting again (the round before it has finished), without releasing it.</summary>
        public async Task<TimeSpan> WaitingAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(Wait);
            while (true)
            {
                if (waits.Reader.TryPeek(out var next))
                    return next.Delay;
                await waits.Reader.WaitToReadAsync(timeout.Token);
            }
        }
    }

    internal sealed class CapturingLogger : ILogger<OrphanBlobSweepService>
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }

        public List<(LogLevel Level, string Message, Exception? Error)> Snapshot()
        {
            lock (Entries) return [.. Entries];
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(Action<IServiceCollection> register, BlobOffloadOptions? offload)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILogger<OrphanBlobSweepService>>(Logger);
            register(services);
            services.AddUnifiedInMemoryStorage(o => o.BlobOffload = offload);
            Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            Service = Provider.GetServices<IHostedService>().OfType<OrphanBlobSweepService>().Single();
            Service.Delay = Steps.Delay;
        }

        public CapturingLogger Logger { get; } = new();

        public SteppedDelay Steps { get; } = new();

        public ServiceProvider Provider { get; }

        public OrphanBlobSweepService Service { get; }

        public InMemoryStorage<T> Store<T>() where T : class, IEntity, new() =>
            (InMemoryStorage<T>)Provider.GetRequiredService<IStorage<T>>();

        public Task StartAsync() => Service.StartAsync(Ct);

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            await Provider.DisposeAsync();
        }
    }

    private static string Orphan(InMemoryOffloadBlobStore blobs, string table, string pk, string rk, TimeSpan age)
    {
        var bytes = Utf8(pk + rk);
        var sha = Sha(bytes);
        var name = ValueOffloadCoordinator.BlobName(table, pk, rk, "Body", sha);
        blobs.Seed(name, bytes, sha, DateTimeOffset.UtcNow - age);
        return name;
    }

    /// <summary>Writes an entity whose Body is offloaded, then ages its blob so only its reference protects it.</summary>
    private static async Task<string> LiveAsync(InMemoryStorage<SplitTextEntity> store, string id, string body, TimeSpan age)
    {
        await store.CreateAsync(new SplitTextEntity { Id = id, Body = body }, Ct);
        var name = Assert.Single(store.Blobs.Names, n => n.StartsWith("SplitTextEntity/" + id.Split('|')[0] + "/", StringComparison.Ordinal));
        await using var stream = await store.Blobs.OpenReadAsync("Body", name, CancellationToken.None);
        var bytes = await ReadAllAsync(stream);
        store.Blobs.Seed(name, bytes, Sha(bytes), DateTimeOffset.UtcNow - age);
        return name;
    }

    // ── sweeping ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Round_DeletesOldOrphans_KeepsLiveAndYoungBlobs()
    {
        await using var rig = new Rig(s => s
            .AddUnifiedOrphanBlobSweep(o => o.OlderThan = TimeSpan.FromMinutes(30))
            .AddUnifiedOrphanBlobSweep<SplitTextEntity>(), Offload());
        var store = rig.Store<SplitTextEntity>();
        var body = Ascii(40_000);
        var live = await LiveAsync(store, "p1|r1", body, TimeSpan.FromHours(2));
        var old = Orphan(store.Blobs, "SplitTextEntity", "p1", "gone", TimeSpan.FromHours(2));
        var otherPartition = Orphan(store.Blobs, "SplitTextEntity", "p2", "gone", TimeSpan.FromHours(2));
        var young = Orphan(store.Blobs, "SplitTextEntity", "p1", "inflight", TimeSpan.FromMinutes(1));

        await rig.StartAsync();
        await rig.Steps.StepAsync();        // the initial delay
        await rig.Steps.WaitingAsync();     // the round has finished

        Assert.Equal(new[] { live, young }.Order(StringComparer.Ordinal), store.Blobs.Names.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(old, store.Blobs.Names);
        Assert.DoesNotContain(otherPartition, store.Blobs.Names);
        Assert.Equal(body, (await store.OneAsync("p1|r1", Ct))!.Body, StringComparer.Ordinal);
        Assert.Contains(rig.Logger.Snapshot(), e => e.Level == LogLevel.Information && e.Message.Contains("SplitTextEntity", StringComparison.Ordinal) && e.Message.Contains("deleted 2 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rounds_FollowInitialDelayThenInterval_AndSweepAgain()
    {
        await using var rig = new Rig(s => s
            .AddUnifiedOrphanBlobSweep(o =>
            {
                o.InitialDelay = TimeSpan.FromSeconds(7);
                o.Interval = TimeSpan.FromMinutes(13);
                o.OlderThan = TimeSpan.FromMinutes(30);
            })
            .AddUnifiedOrphanBlobSweep<SplitTextEntity>(), Offload());
        var store = rig.Store<SplitTextEntity>();

        await rig.StartAsync();
        Assert.Equal(TimeSpan.FromSeconds(7), await rig.Steps.StepAsync());
        Assert.Equal(TimeSpan.FromMinutes(13), await rig.Steps.WaitingAsync());

        var later = Orphan(store.Blobs, "SplitTextEntity", "p", "later", TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromMinutes(13), await rig.Steps.StepAsync());
        Assert.Equal(TimeSpan.FromMinutes(13), await rig.Steps.WaitingAsync());

        Assert.DoesNotContain(later, store.Blobs.Names);
    }

    [Fact]
    public async Task Round_PassesOlderThan_ToTheTableWideSweep()
    {
        var sweeper = Substitute.For<IStorage<PlainEntity>, IOrphanBlobSweeper>();
        await using var rig = new Rig(s => s
            .AddSingleton(sweeper)
            .AddUnifiedOrphanBlobSweep(o => o.OlderThan = TimeSpan.FromMinutes(42))
            .AddUnifiedOrphanBlobSweep<PlainEntity>(), Offload());

        await rig.StartAsync();
        await rig.Steps.StepAsync();
        await rig.Steps.WaitingAsync();

        await ((IOrphanBlobSweeper)sweeper).Received(1).SweepOrphanBlobsAsync(TimeSpan.FromMinutes(42), Arg.Any<CancellationToken>());
        await ((IOrphanBlobSweeper)sweeper).DidNotReceive().SweepOrphanBlobsAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // ── isolation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ThrowingStore_IsLogged_AndDoesNotStopOtherTypesOrLaterRounds()
    {
        var failing = Substitute.For<IStorage<PlainEntity>, IOrphanBlobSweeper>();
        ((IOrphanBlobSweeper)failing).SweepOrphanBlobsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AggregateException(new InvalidOperationException("partition p failed")));
        await using var rig = new Rig(s => s
            .AddSingleton(failing)
            .AddSingleton<IStorage<SplitGzipEntity>>(_ => throw new InvalidOperationException("cannot build the store"))
            .AddUnifiedOrphanBlobSweep(o => o.OlderThan = TimeSpan.FromMinutes(30))
            .AddUnifiedOrphanBlobSweep<PlainEntity>()
            .AddUnifiedOrphanBlobSweep<SplitGzipEntity>()
            .AddUnifiedOrphanBlobSweep<SplitTextEntity>(), Offload());
        var store = rig.Store<SplitTextEntity>();
        var first = Orphan(store.Blobs, "SplitTextEntity", "p", "a", TimeSpan.FromHours(1));

        await rig.StartAsync();
        await rig.Steps.StepAsync();
        await rig.Steps.WaitingAsync();

        Assert.DoesNotContain(first, store.Blobs.Names);
        var errors = rig.Logger.Snapshot().Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Contains(errors, e => e.Message.Contains(nameof(PlainEntity), StringComparison.Ordinal) && e.Error is AggregateException);
        Assert.Contains(errors, e => e.Message.Contains(nameof(SplitGzipEntity), StringComparison.Ordinal) && e.Error is InvalidOperationException);

        var second = Orphan(store.Blobs, "SplitTextEntity", "p", "b", TimeSpan.FromHours(1));
        await rig.Steps.StepAsync();
        await rig.Steps.WaitingAsync();

        Assert.DoesNotContain(second, store.Blobs.Names);
        await ((IOrphanBlobSweeper)failing).Received(2).SweepOrphanBlobsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        Assert.False(rig.Service.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task StoreWithoutTheCapability_IsLoggedAsWarning_AndSkipped()
    {
        var plain = Substitute.For<IStorage<PlainEntity>>();
        await using var rig = new Rig(s => s
            .AddSingleton(plain)
            .AddUnifiedOrphanBlobSweep<PlainEntity>()
            .AddUnifiedOrphanBlobSweep<SplitTextEntity>(), Offload());
        var store = rig.Store<SplitTextEntity>();
        var orphan = Orphan(store.Blobs, "SplitTextEntity", "p", "a", TimeSpan.FromDays(1));

        await rig.StartAsync();
        await rig.Steps.StepAsync();
        await rig.Steps.WaitingAsync();

        Assert.DoesNotContain(orphan, store.Blobs.Names);
        Assert.Contains(rig.Logger.Snapshot(), e => e.Level == LogLevel.Warning
            && e.Message.Contains(nameof(PlainEntity), StringComparison.Ordinal)
            && e.Message.Contains(nameof(IOrphanBlobSweeper), StringComparison.Ordinal));
    }

    // ── not configured ────────────────────────────────────────────────────

    [Fact]
    public async Task NoBlobOffload_NeverSweeps_AndTheServiceEnds()
    {
        var sweeper = Substitute.For<IStorage<PlainEntity>, IOrphanBlobSweeper>();
        await using var rig = new Rig(s => s
            .AddSingleton(sweeper)
            .AddUnifiedOrphanBlobSweep(o => o.InitialDelay = TimeSpan.Zero)
            .AddUnifiedOrphanBlobSweep<PlainEntity>(), offload: null);

        await rig.StartAsync();
        await rig.Service.ExecuteTask!.WaitAsync(Wait, Ct);

        Assert.Empty(rig.Steps.Requested);
        await ((IOrphanBlobSweeper)sweeper).DidNotReceiveWithAnyArgs().SweepOrphanBlobsAsync(default(TimeSpan), default);
        Assert.Contains(rig.Logger.Snapshot(), e => e.Level == LogLevel.Information && e.Message.Contains("BlobOffload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoTypeRegistered_TheServiceEnds()
    {
        await using var rig = new Rig(s => s.AddUnifiedOrphanBlobSweep(), Offload());

        await rig.StartAsync();
        await rig.Service.ExecuteTask!.WaitAsync(Wait, Ct);

        Assert.Empty(rig.Steps.Requested);
    }

    // ── shutdown ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_WhileWaiting_EndsTheLoop_AndNoLaterRoundRuns()
    {
        await using var rig = new Rig(s => s.AddUnifiedOrphanBlobSweep<SplitTextEntity>(), Offload());
        var store = rig.Store<SplitTextEntity>();

        await rig.StartAsync();
        await rig.Steps.StepAsync();
        await rig.Steps.WaitingAsync();
        var orphan = Orphan(store.Blobs, "SplitTextEntity", "p", "a", TimeSpan.FromDays(1));

        await rig.Service.StopAsync(Ct).WaitAsync(Wait, Ct);

        Assert.True(rig.Service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Contains(orphan, store.Blobs.Names);
        Assert.DoesNotContain(rig.Logger.Snapshot(), e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Stop_DuringASweep_CancelsIt_WithoutLoggingAFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweeper = Substitute.For<IStorage<PlainEntity>, IOrphanBlobSweeper>();
        ((IOrphanBlobSweeper)sweeper).SweepOrphanBlobsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return 0;
            });
        await using var rig = new Rig(s => s.AddSingleton(sweeper).AddUnifiedOrphanBlobSweep<PlainEntity>(), Offload());

        await rig.StartAsync();
        await rig.Steps.StepAsync();
        await entered.Task.WaitAsync(Wait, Ct);

        await rig.Service.StopAsync(Ct).WaitAsync(Wait, Ct);

        Assert.True(rig.Service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Single(rig.Steps.Requested);
        Assert.DoesNotContain(rig.Logger.Snapshot(), e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task CancellationNotFromShutdown_IsAFailure_AndTheLoopContinues()
    {
        var sweeper = Substitute.For<IStorage<PlainEntity>, IOrphanBlobSweeper>();
        ((IOrphanBlobSweeper)sweeper).SweepOrphanBlobsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("the request timed out"));
        await using var rig = new Rig(s => s.AddSingleton(sweeper).AddUnifiedOrphanBlobSweep<PlainEntity>(), Offload());

        await rig.StartAsync();
        await rig.Steps.StepAsync();
        await rig.Steps.WaitingAsync();

        Assert.Contains(rig.Logger.Snapshot(), e => e.Level == LogLevel.Error && e.Error is TaskCanceledException);
        Assert.False(rig.Service.ExecuteTask!.IsCompleted);
    }

    // ── registration ──────────────────────────────────────────────────────

    [Fact]
    public void Registration_IsIdempotent_PerTypeAndForTheService()
    {
        var services = new ServiceCollection();
        services.AddUnifiedInMemoryStorage();
        services.AddUnifiedOrphanBlobSweep<SplitTextEntity>()
            .AddUnifiedOrphanBlobSweep<SplitTextEntity>()
            .AddUnifiedOrphanBlobSweep(o => o.Interval = TimeSpan.FromMinutes(3))
            .AddUnifiedOrphanBlobSweep(o => o.Interval = TimeSpan.FromMinutes(9))
            .AddUnifiedOrphanBlobSweep<SplitGzipEntity>();
        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IHostedService>().OfType<OrphanBlobSweepService>());
        Assert.Equal(TimeSpan.FromMinutes(3), provider.GetRequiredService<OrphanBlobSweepOptions>().Interval);
        Assert.Equal(
            new[] { typeof(SplitTextEntity), typeof(SplitGzipEntity) },
            provider.GetServices<IOrphanBlobSweepTarget>().Select(t => t.EntityType));
    }

    [Fact]
    public void Defaults_AreDocumented()
    {
        var options = new OrphanBlobSweepOptions();

        Assert.Equal(TimeSpan.FromHours(1), options.Interval);
        Assert.Equal(TimeSpan.FromHours(1), options.OlderThan);
        Assert.Equal(TimeSpan.FromMinutes(1), options.InitialDelay);
    }

    public static TheoryData<string, long> InvalidOptions => new()
    {
        { nameof(OrphanBlobSweepOptions.Interval), 0 },
        { nameof(OrphanBlobSweepOptions.Interval), -1 },
        { nameof(OrphanBlobSweepOptions.Interval), (long)uint.MaxValue },
        { nameof(OrphanBlobSweepOptions.InitialDelay), -1 },
        { nameof(OrphanBlobSweepOptions.InitialDelay), (long)uint.MaxValue },
        { nameof(OrphanBlobSweepOptions.OlderThan), -1 },
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Registration_RefusesOutOfRangeOptions(string property, long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(milliseconds);
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddUnifiedOrphanBlobSweep(o =>
        {
            switch (property)
            {
                case nameof(OrphanBlobSweepOptions.Interval): o.Interval = value; break;
                case nameof(OrphanBlobSweepOptions.InitialDelay): o.InitialDelay = value; break;
                default: o.OlderThan = value; break;
            }
        }));

        Assert.Contains(property, ex.Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    [Fact]
    public void Registration_ZeroDelayAndZeroAge_AreAllowed()
    {
        var services = new ServiceCollection();

        services.AddUnifiedOrphanBlobSweep(o =>
        {
            o.InitialDelay = TimeSpan.Zero;
            o.OlderThan = TimeSpan.Zero;
        });

        Assert.NotEmpty(services);
    }
}
