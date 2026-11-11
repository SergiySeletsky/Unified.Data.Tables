using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>A large-value aware entity of the helper tests.</summary>
public class HelperRaceDoc : Entity, IRaceDoc
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    public int Version { get; set; }

    public string? Tag { get; set; }
}

/// <summary>
/// The read-then-write helpers of <see cref="StorageExtensions"/> and a read that lost its races (CACHE-4
/// of the fix-wave-3 review). Since ruling R5 a read of a type with large values whose row kept changing
/// throws <see cref="ConcurrencyConflictException"/>. In every helper that reads and then writes, that
/// exception counts as a lost race exactly as one raised by the write: it is retried within
/// <c>maxAttempts</c> with the same backoff; when the attempts run out <c>TryMutateAsync</c> and
/// <c>TryTransitionAsync</c> return <see cref="MutationStatus.Conflicted"/> and <c>MutateAsync</c> and
/// <c>MutateOrCreateAsync</c> throw it. Any other exception of the read is not swallowed.
/// </summary>
public class HelpersRetryAReadThatLostItsRacesTests
{
    private const string Id = "p|a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<object> RunHelperAsync<T>(IStorage<T> store, string helper, int maxAttempts = 3)
        where T : class, IEntity, IRaceDoc, new()
    {
        void Mutate(T e) => e.Body = e.Body + "!";
        return helper switch
        {
            "TryMutateAsync" => Box(store.TryMutateAsync(Id, Mutate, maxAttempts, Ct)),
            "TryTransitionAsync" => Box(store.TryTransitionAsync(Id, _ => true, Mutate, maxAttempts, Ct)),
            "MutateAsync" => Box(store.MutateAsync(Id, Mutate, maxAttempts, Ct)),
            _ => Box(store.MutateOrCreateAsync(Id, () => new T(), Mutate, maxAttempts, Ct)),
        };
    }

    private static async Task<object> Box<TResult>(Task<TResult> task) => (await task)!;

    // The reproduction of the review (renamed), InMemoryStorage: a writer replaces the row and the blob
    // goes before each of the first twelve reads opens it. Three reads of five attempts use the twelve lost
    // races up, so a helper that retries the read succeeds on its third attempt.
    [Theory]
    [InlineData("TryMutateAsync")]
    [InlineData("TryTransitionAsync")]
    [InlineData("MutateAsync")]
    [InlineData("MutateOrCreateAsync")]
    public async Task AHelper_RetriesAReadThatLostItsRaces_AndSucceedsOnceTheWriterStops_InMemory(string helper)
    {
        var store = InMemoryWithAWriter(lostRaces: 12);

        object? result = null;
        var thrown = await Record.ExceptionAsync(async () => result = await RunHelperAsync(store, helper));
        store.Blobs.BeforeOpenRead = null;

        Assert.Null(thrown);
        if (result is MutationResult<HelperRaceDoc> outcome)
            Assert.Equal(MutationStatus.Updated, outcome.Status);
        Assert.EndsWith("!", (await store.OneAsync(Id, Ct))!.Body, StringComparison.Ordinal);
    }

    // The writer never stops: every read loses its races. The attempts run out: the Try* helpers return
    // Conflicted (nothing thrown), MutateAsync and MutateOrCreateAsync throw the conflict.
    [Theory]
    [InlineData("TryMutateAsync")]
    [InlineData("TryTransitionAsync")]
    [InlineData("MutateAsync")]
    [InlineData("MutateOrCreateAsync")]
    public async Task AHelper_WhoseReadsKeepLosingTheirRaces_ReportsAConflictWhenItsAttemptsRunOut_InMemory(string helper)
    {
        var store = InMemoryWithAWriter(lostRaces: 1_000);

        object? result = null;
        var thrown = await Record.ExceptionAsync(async () => result = await RunHelperAsync(store, helper, maxAttempts: 2));
        store.Blobs.BeforeOpenRead = null;

        AssertConflicted(helper, result, thrown);
    }

    // The same on TableStorage: a writer commits a new version between the reader's fetch of the main row
    // and its query of the continuation rows. Twelve lost races take two reads of five attempts (six
    // whole-row reads each); the third attempt of the helper succeeds.
    [Theory]
    [InlineData("TryMutateAsync")]
    [InlineData("TryTransitionAsync")]
    [InlineData("MutateAsync")]
    [InlineData("MutateOrCreateAsync")]
    public async Task AHelper_RetriesAReadThatLostItsRaces_AndSucceedsOnceTheWriterStops_TableStorage(string helper)
    {
        var (table, reader, readerCache, writerCache) = await TableStorageWithAWriterAsync(lostRaces: 12);
        using (readerCache)
        using (writerCache)
        {
            object? result = null;
            var thrown = await Record.ExceptionAsync(async () => result = await RunHelperAsync(reader, helper));
            table.ResponseInFlight = null;

            Assert.Null(thrown);
            if (result is MutationResult<HelperRaceDoc> outcome)
                Assert.Equal(MutationStatus.Updated, outcome.Status);
            Assert.EndsWith("!", (await reader.OneAsync(Id, Ct))!.Body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("TryMutateAsync")]
    [InlineData("TryTransitionAsync")]
    [InlineData("MutateAsync")]
    [InlineData("MutateOrCreateAsync")]
    public async Task AHelper_WhoseReadsKeepLosingTheirRaces_ReportsAConflictWhenItsAttemptsRunOut_TableStorage(string helper)
    {
        var (table, reader, readerCache, writerCache) = await TableStorageWithAWriterAsync(lostRaces: 1_000);
        using (readerCache)
        using (writerCache)
        {
            object? result = null;
            var thrown = await Record.ExceptionAsync(async () => result = await RunHelperAsync(reader, helper, maxAttempts: 2));
            table.ResponseInFlight = null;

            AssertConflicted(helper, result, thrown);
        }
    }

    // The helper logic on its own, against any IStorage: a read's conflict is an attempt (the read is
    // asked again, up to maxAttempts times), and the mutation runs only on an entity that was read.
    [Theory]
    [InlineData("TryMutateAsync")]
    [InlineData("TryTransitionAsync")]
    [InlineData("MutateAsync")]
    [InlineData("MutateOrCreateAsync")]
    public async Task AReadsConflict_CountsAsAnAttempt_OfEveryHelper(string helper)
    {
        var storage = Substitute.For<IStorage<HelperRaceDoc>>();
        var conflict = new ConcurrencyConflictException(nameof(HelperRaceDoc), Id, new InvalidOperationException("the row kept changing"));
        storage.OneAsync(Id, Arg.Any<CancellationToken>()).Throws(conflict);

        object? result = null;
        var thrown = await Record.ExceptionAsync(async () => result = await RunHelperAsync(storage, helper, maxAttempts: 3));

        AssertConflicted(helper, result, thrown);
        if (thrown is not null)
            Assert.Same(conflict, thrown);
        await storage.Received(3).OneAsync(Id, Arg.Any<CancellationToken>());
        await storage.DidNotReceiveWithAnyArgs().UpdateAsync(default(HelperRaceDoc)!, default, default);
        await storage.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    // Any other failure of the read is not a lost race: it leaves the helper at once, unchanged.
    [Theory]
    [InlineData("TryMutateAsync", "integrity")]
    [InlineData("TryTransitionAsync", "integrity")]
    [InlineData("MutateAsync", "integrity")]
    [InlineData("MutateOrCreateAsync", "integrity")]
    [InlineData("TryMutateAsync", "cancelled")]
    [InlineData("TryTransitionAsync", "cancelled")]
    [InlineData("MutateAsync", "cancelled")]
    [InlineData("MutateOrCreateAsync", "cancelled")]
    public async Task AReadsOtherFailure_IsNotSwallowedByAnyHelper(string helper, string failure)
    {
        var storage = Substitute.For<IStorage<HelperRaceDoc>>();
        Exception error = failure == "integrity"
            ? new TableValueIntegrityException("Body", "the stored value does not match its hash")
            : new OperationCanceledException("cancelled");
        storage.OneAsync(Id, Arg.Any<CancellationToken>()).Throws(error);

        var thrown = await Record.ExceptionAsync(() => RunHelperAsync(storage, helper));

        Assert.Same(error, thrown);
        await storage.Received(1).OneAsync(Id, Arg.Any<CancellationToken>());
    }

    private static void AssertConflicted(string helper, object? result, Exception? thrown)
    {
        if (helper.StartsWith("Try", StringComparison.Ordinal))
        {
            Assert.Null(thrown);
            Assert.Equal(MutationStatus.Conflicted, Assert.IsType<MutationResult<HelperRaceDoc>>(result).Status);
        }
        else
        {
            Assert.IsType<ConcurrencyConflictException>(thrown);
        }
    }

    private static InMemoryStorage<HelperRaceDoc> InMemoryWithAWriter(int lostRaces)
    {
        var store = new InMemoryStorage<HelperRaceDoc>(options: new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 },
        });
        store.LargeValueRules.RetryDelay = (_, _) => Task.CompletedTask;
        store.CreateAsync(new HelperRaceDoc { Id = Id, Body = Ascii(40_000, seed: 1), Version = 1 }, Ct).GetAwaiter().GetResult();

        var version = 1;
        var writing = false;
        store.Blobs.BeforeOpenRead = async (name, ct) =>
        {
            if (writing || version > lostRaces)
                return;
            writing = true;
            try
            {
                await store.UpsertAsync(new HelperRaceDoc { Id = Id, Body = Ascii(40_000, seed: ++version), Version = version }, ct);
                store.Blobs.Remove(name);
            }
            finally
            {
                writing = false;
            }
        };
        return store;
    }

    private static async Task<(RacingTableClient Table, TableStorage<HelperRaceDoc> Reader, MemoryCache ReaderCache, MemoryCache WriterCache)>
        TableStorageWithAWriterAsync(int lostRaces)
    {
        var table = new RacingTableClient();
        var readerCache = new MemoryCache(new MemoryCacheOptions());
        var writerCache = new MemoryCache(new MemoryCacheOptions());
        var reader = NewStore(table, readerCache);
        var writer = NewStore(table, writerCache);
        await writer.UpsertAsync(RowsDoc(1), Ct);

        var version = 1;
        var writing = false;
        table.ResponseInFlight = async request =>
        {
            if (writing || version > lostRaces || !request.IsWholeRowPointRead)
                return;
            writing = true;
            try
            {
                await writer.UpsertAsync(RowsDoc(++version), Ct);
            }
            finally
            {
                writing = false;
            }
        };
        return (table, reader, readerCache, writerCache);
    }

    private static TableStorage<HelperRaceDoc> NewStore(RacingTableClient table, IMemoryCache cache)
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(nameof(HelperRaceDoc)).Returns(table);
        var store = new TableStorage<HelperRaceDoc>(service, cache, NullLogger<TableStorage<HelperRaceDoc>>.Instance);
        store.LargeValueRetryDelay = (_, _) => Task.CompletedTask;
        return store;
    }

    private static HelperRaceDoc RowsDoc(int version) => new() { Id = Id, Body = new string((char)('a' + (version % 26)), 600_000), Version = version };
}
