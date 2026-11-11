using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A write whose storage call failed evicts the entity's entry and its partition's lists; when the cache
/// itself throws while it does (a disposed host <see cref="MemoryCache"/> during shutdown), the exception
/// that leaves the store is still the one the storage call produced, or the library's translation of it
/// (<see cref="ConcurrencyConflictException"/> for a 412), and the eviction's failure is logged
/// (CACHE-3 of the fix-wave-3 review).
/// </summary>
public class FailedWriteKeepsItsExceptionWhenEvictionFailsTests
{
    private const string Id = "p|a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableStorage<T> NewStore<T>(RacingTableClient table, IMemoryCache cache, ILogger<TableStorage<T>> logger)
        where T : class, IEntity, new()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(T).Name).Returns(table);
        return new TableStorage<T>(service, cache, logger, authorizer: null, null, (Unified.Data.Tables.Providers.IOffloadBlobStore?)null);
    }

    // The storage call throws after it was sent (a cancellation), while the store's cache is disposed.
    // The reproduction of the review covered create, upsert, update and delete of a plain type; the
    // builder update and the large-value writes go through the same eviction.
    [Theory]
    [InlineData("create", false)]
    [InlineData("upsert", false)]
    [InlineData("update", false)]
    [InlineData("delete", false)]
    [InlineData("builder", false)]
    [InlineData("create", true)]
    [InlineData("upsert", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    [InlineData("builder", true)]
    public async Task AFailedWrite_WhoseEvictionFails_StillThrowsTheStorageException(string write, bool largeValues)
    {
        if (largeValues)
            await RunFailedWriteAsync<RaceDoc>(write);
        else
            await RunFailedWriteAsync<RacePlainDoc>(write);
    }

    private static async Task RunFailedWriteAsync<T>(string write) where T : Entity, IRaceDoc, new()
    {
        var table = new RacingTableClient();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new ListLogger<TableStorage<T>>();
        var store = NewStore<T>(table, cache, logger);
        if (write != "create")
            await store.UpsertAsync(new T { Id = Id, Body = "v1", Version = 1 }, Ct);
        cache.Dispose();

        var cancelled = new TaskCanceledException("the request was cancelled after it was sent");
        table.Committed = _ => throw cancelled;
        var thrown = await Record.ExceptionAsync(() => write switch
        {
            "create" => store.CreateAsync(new T { Id = Id, Body = "v1", Version = 1 }, Ct),
            "upsert" => store.UpsertAsync(new T { Id = Id, Body = "v2", Version = 2 }, Ct),
            "update" => store.UpdateAsync(new T { Id = Id, Body = "v2", Version = 2 }, ConcurrencyMode.LastWriterWins, Ct),
            "builder" => store.UpdateAsync(Id, u => u.SetProperty(x => x.Version, 2), Ct),
            _ => store.DeleteAsync(Id, Ct),
        });
        table.Committed = null;

        Assert.Same(cancelled, thrown);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Error is ObjectDisposedException);
    }

    // The 412 of an optimistic update or a conditional builder update, while the cache is disposed: the
    // store's translation of the 412 leaves (0.8.3 let the cache's ObjectDisposedException through on
    // this path).
    [Theory]
    [InlineData("strict", false)]
    [InlineData("builder", false)]
    [InlineData("strict", true)]
    [InlineData("builder", true)]
    public async Task AConflict_WhoseEvictionFails_StillThrowsTheConflict(string write, bool largeValues)
    {
        if (largeValues)
            await RunConflictAsync<RaceDoc>(write);
        else
            await RunConflictAsync<RacePlainDoc>(write);
    }

    private static async Task RunConflictAsync<T>(string write) where T : Entity, IRaceDoc, new()
    {
        var table = new RacingTableClient();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new ListLogger<TableStorage<T>>();
        var store = NewStore<T>(table, cache, logger);
        var first = await store.UpsertAsync(new T { Id = Id, Body = "v1", Version = 1 }, Ct);
        var stale = first.ETag!;
        await store.UpsertAsync(new T { Id = Id, Body = "v2", Version = 2 }, Ct);
        cache.Dispose();

        var thrown = await Record.ExceptionAsync(() => write == "strict"
            ? store.UpdateAsync(new T { Id = Id, Body = "v3", Version = 3, ETag = stale }, ConcurrencyMode.Strict, Ct)
            : store.UpdateAsync(Id, u => u.SetProperty(x => x.Version, 3).WithETag(stale), Ct));

        Assert.IsType<ConcurrencyConflictException>(thrown);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Error is ObjectDisposedException);
    }

    /// <summary>Records what the store logs.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
