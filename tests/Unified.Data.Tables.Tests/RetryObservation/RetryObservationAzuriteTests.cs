using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.RetryObservation;

/// <summary>
/// The lost-response cases of <see cref="RetryObservationTests"/> against the local Azurite, through the
/// real SDK and its retry policy: the service applies the first attempt, its response is dropped, and the
/// SDK's own retry gets the answer a second writer would get. The store reports that it may have been its
/// own write, because it saw the first attempt go out. Includes the transaction paths a scripted
/// transport does not cover (an all-or-nothing batch, the delete of an entity with continuation rows).
/// </summary>
public sealed class RetryObservationAzuriteTests : AzuriteCleanupTestBase
{
    /// <summary>Lets the first request that <paramref name="matches"/> reach the service, then loses its response.</summary>
    private sealed class LoseFirstResponse(Func<Request, bool> matches) : HttpPipelinePolicy
    {
        private int fired;

        public int Fired => Volatile.Read(ref fired);

        private bool Lose(HttpMessage message) => matches(message.Request) && Interlocked.Exchange(ref fired, 1) == 0;

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            await ProcessNextAsync(message, pipeline);
            if (Lose(message))
                throw new IOException("Simulated: the connection dropped after the service applied the request.");
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            ProcessNext(message, pipeline);
            if (Lose(message))
                throw new IOException("Simulated: the connection dropped after the service applied the request.");
        }
    }

    private static bool IsRow(Request request) => request.Uri.Path.Contains("(PartitionKey=", StringComparison.Ordinal);

    private static bool IsBatch(Request request) => request.Uri.Path.EndsWith("$batch", StringComparison.Ordinal);

    /// <summary>A client that loses one response and observes its own retries, as the library's own clients do.</summary>
    private static TableServiceClient ClientThatLoses(LoseFirstResponse lose, bool observe = true)
    {
        var options = new TableClientOptions();
        options.Retry.Delay = TimeSpan.FromMilliseconds(10);
        options.Retry.MaxRetries = 3;
        // After the observation: the observation sees the attempt end without a response.
        if (observe)
            options.AddRetryObservation();
        options.AddPolicy(lose, HttpPipelinePosition.PerRetry);
        return new TableServiceClient(AzuriteConnection.ConnectionString, options);
    }

    [Fact]
    public async Task Azurite_ACreateWhoseResponseIsLost_IsReportedAsADuplicateThatMayBeItsOwn()
    {
        SkipUnlessAvailable();
        var lose = new LoseFirstResponse(r => r.Method == RequestMethod.Post && r.Uri.Path.EndsWith("/" + nameof(SimpleEntity), StringComparison.Ordinal));
        var store = SplitOnlyStore<SimpleEntity>(ClientThatLoses(lose));
        await store.EnsureCreatedAsync(Ct);

        var duplicate = await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new SimpleEntity { Id = Id("c"), Count = 7 }, Ct));

        Assert.Equal(1, lose.Fired);
        Assert.True(duplicate.MayBeOwnWrite);
        // It was: the row is the one this call wrote.
        Assert.Equal(7, (await store.OneAsync(Id("c"), Ct))!.Count);

        // The same key again, sent once: a duplicate that is not this call's own.
        var foreign = await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new SimpleEntity { Id = Id("c") }, Ct));
        Assert.False(foreign.MayBeOwnWrite);
    }

    [Fact]
    public async Task Azurite_AnUpdateWhoseResponseIsLost_IsReportedAsAConflictThatMayBeItsOwn()
    {
        SkipUnlessAvailable();
        var lose = new LoseFirstResponse(r => r.Method == RequestMethod.Put && IsRow(r));
        var store = SplitOnlyStore<SimpleEntity>(ClientThatLoses(lose));
        var created = await store.CreateAsync(new SimpleEntity { Id = Id("u"), Count = 1 }, Ct);
        created.Count = 2;

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpdateAsync(created, Ct));

        Assert.Equal(1, lose.Fired);
        Assert.True(conflict.MayBeOwnWrite);
        Assert.Equal(2, (await store.OneAsync(Id("u"), Ct))!.Count);

        // The stale version again, sent once: another version is in the way, and no retry was seen.
        var stale = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpdateAsync(created, Ct));
        Assert.False(stale.MayBeOwnWrite);
    }

    [Fact]
    public async Task Azurite_ADeleteWhoseResponseIsLost_IsIndeterminate_AndTheRowIsGone()
    {
        SkipUnlessAvailable();
        var lose = new LoseFirstResponse(r => r.Method == RequestMethod.Delete && IsRow(r));
        var store = SplitOnlyStore<SimpleEntity>(ClientThatLoses(lose));
        await store.CreateAsync(new SimpleEntity { Id = Id("d") }, Ct);
        var second = await store.CreateAsync(new SimpleEntity { Id = Id("e") }, Ct);

        Assert.Equal(DeleteStatus.Indeterminate, await store.DeleteIfExistsAsync(Id("d"), Ct));

        Assert.Equal(1, lose.Fired);
        Assert.False(await store.ExistsAsync(Id("d"), Ct));
        // Sent once from here on: the answers are definite.
        Assert.Equal(DeleteStatus.NotFound, await store.DeleteIfExistsAsync(Id("d"), Ct));
        Assert.Equal(DeleteStatus.Deleted, await store.DeleteIfMatchAsync(Id("e"), second.ETag!, Ct));
        Assert.Equal(DeleteStatus.NotFound, await store.DeleteIfMatchAsync(Id("e"), second.ETag!, Ct));
    }

    [Fact]
    public async Task Azurite_ADeleteOverAClientWithoutTheObservation_CannotSayNotFound()
    {
        SkipUnlessAvailable();
        var store = SplitOnlyStore<SimpleEntity>();
        await store.CreateAsync(new SimpleEntity { Id = Id("d") }, Ct);

        Assert.Equal(DeleteStatus.Deleted, await store.DeleteIfExistsAsync(Id("d"), Ct));
        Assert.Equal(DeleteStatus.Indeterminate, await store.DeleteIfExistsAsync(Id("d"), Ct));
    }

    [Fact]
    public async Task Azurite_ADeleteOfAnEntityWithContinuationRows_WhoseTransactionResponseIsLost_IsIndeterminate_AndNothingIsLeft()
    {
        SkipUnlessAvailable();
        // Armed after the create, which is a transaction too.
        var armed = false;
        var lose = new LoseFirstResponse(r => armed && IsBatch(r));
        var store = SplitOnlyStore<SplitTextEntity>(ClientThatLoses(lose));
        // Larger than one row: a primary and continuation rows, deleted in one transaction.
        await store.CreateAsync(new SplitTextEntity { Id = Id("big"), Body = new string('x', 600_000) }, Ct);
        Assert.True((await RawRowsAsync(nameof(SplitTextEntity))).Count > 1);
        armed = true;

        var status = await store.DeleteIfExistsAsync(Id("big"), Ct);

        Assert.Equal(1, lose.Fired);
        Assert.Equal(DeleteStatus.Indeterminate, status);
        Assert.Empty(await RawRowsAsync(nameof(SplitTextEntity)));
        // No delete is sent for an entity that is not there: a definite answer, whatever was retried before.
        Assert.Equal(DeleteStatus.NotFound, await store.DeleteIfExistsAsync(Id("big"), Ct));
    }

    [Fact]
    public async Task Azurite_APartitionDeleteWhoseTransactionResponseIsLost_CountsTheRowsItDeleted()
    {
        SkipUnlessAvailable();
        var lose = new LoseFirstResponse(IsBatch);
        var store = SplitOnlyStore<SimpleEntity>(ClientThatLoses(lose));
        foreach (var rk in new[] { "a", "b", "c" })
            await store.CreateAsync(new SimpleEntity { Id = Id(rk) }, Ct);

        // The transaction is applied, its response is lost, and the SDK's retry is refused for a row that
        // is gone: the rows are this call's own deletes, all of them.
        var deleted = await store.DeletePartitionAsync(Pk, Ct);

        Assert.Equal(1, lose.Fired);
        Assert.Equal(3, deleted);
        Assert.Equal(0, await store.CountAsync(Pk, Ct));
    }

    [Fact]
    public async Task Azurite_APartitionDeleteThatLosesARaceForOneRow_DoesNotCountThatRow_WithOrWithoutTheObservation()
    {
        SkipUnlessAvailable();
        foreach (var observe in new[] { true, false })
        {
            // Before the transaction goes out, another writer deletes one of the listed rows.
            var racing = SplitOnlyStore<SimpleEntity>();
            var race = new BeforeFirst(IsBatch, () => racing.DeleteAsync(Id("b"), Ct));
            var options = new TableClientOptions();
            if (observe)
                options.AddRetryObservation();
            options.AddPolicy(race, HttpPipelinePosition.PerCall);
            var store = SplitOnlyStore<SimpleEntity>(new TableServiceClient(AzuriteConnection.ConnectionString, options));
            foreach (var rk in new[] { "a", "b", "c" })
                await store.CreateAsync(new SimpleEntity { Id = Id(rk) }, Ct);

            var deleted = await store.DeletePartitionAsync(Pk, Ct);

            Assert.Equal(2, deleted);
            Assert.Equal(0, await store.CountAsync(Pk, Ct));
        }
    }

    [Fact]
    public async Task Azurite_APolymorphicPartitionDeleteWhoseTransactionResponseIsLost_CountsTheRowsItDeleted()
    {
        SkipUnlessAvailable();
        var lose = new LoseFirstResponse(IsBatch);
        var table = "Poly" + Guid.NewGuid().ToString("N");
        var store = new PolymorphicTableStorage<TestMessage>(
            ClientThatLoses(lose), table, Microsoft.Extensions.Logging.Abstractions.NullLogger<PolymorphicTableStorage<TestMessage>>.Instance);
        try
        {
            foreach (var rk in new[] { "a", "b" })
                await store.InsertAsync(new PolymorphicWrite<TestMessage>(new TableKey(Pk, rk), new TestCommand { Id = rk, Operation = "go" }), Ct);

            var deleted = await store.DeletePartitionAsync(Pk, Ct);

            Assert.Equal(1, lose.Fired);
            Assert.Equal(2, deleted);
            Assert.Equal(0, await store.DeletePartitionAsync(Pk, Ct));
        }
        finally
        {
            await TableService.DeleteTableAsync(table, CancellationToken.None);
        }
    }

    /// <summary>Runs an action once, before the first request that <paramref name="matches"/> is sent.</summary>
    private sealed class BeforeFirst(Func<Request, bool> matches, Func<Task> action) : HttpPipelinePolicy
    {
        private int fired;

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (matches(message.Request) && Interlocked.Exchange(ref fired, 1) == 0)
                await action();
            await ProcessNextAsync(message, pipeline);
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (matches(message.Request) && Interlocked.Exchange(ref fired, 1) == 0)
                action().GetAwaiter().GetResult();
            ProcessNext(message, pipeline);
        }
    }

    [Fact]
    public async Task Azurite_AnAtomicCreateWhoseResponseIsLost_IsReportedAsADuplicateThatMayBeItsOwn()
    {
        SkipUnlessAvailable();
        var lose = new LoseFirstResponse(IsBatch);
        var store = SplitOnlyStore<SimpleEntity>(ClientThatLoses(lose));
        await store.EnsureCreatedAsync(Ct);
        var batch = new[] { new SimpleEntity { Id = Id("a"), Count = 1 }, new SimpleEntity { Id = Id("b"), Count = 2 } };

        var duplicate = await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAtomicAsync(batch, Ct));

        Assert.Equal(1, lose.Fired);
        Assert.True(duplicate.MayBeOwnWrite);
        Assert.Equal(2, await store.CountAsync(Pk, Ct));

        var again = await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAtomicAsync(batch, Ct));
        Assert.False(again.MayBeOwnWrite);
    }
}
