using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;
using Answer = Unified.Data.Tables.Tests.LargeValues.ScriptedTableTransport.Answer;

namespace Unified.Data.Tables.Tests.RetryObservation;

/// <summary>
/// 0.10.0: a store over a client with the retry observation knows how many attempts of a request the
/// SDK sent. Through the real SDK and its real retry policy over a scripted transport: a response that
/// never arrives is retried, and the answer of the retry is read in the knowledge that an earlier attempt
/// may have been applied. A request that was sent exactly once is reported as such, and a client without
/// the observation is answered conservatively, as before 0.10.0.
/// </summary>
public class RetryObservationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string V1 = "W/\"datetime'2024-01-01T00%3A00%3A00.0000001Z'\"";

    private static Answer Lost() => ScriptedTableTransport.Lost();

    private static Answer Ok() => new(204, ETag: V1);

    private static Answer Failure(int status, string? code) =>
        code is null
            ? new Answer(status)
            : new Answer(status, "{\"odata.error\":{\"code\":\"" + code + "\",\"message\":{\"lang\":\"en-US\",\"value\":\"" + code + "\"}}}", ErrorCode: code);

    private static Answer Duplicate() => Failure(409, "EntityAlreadyExists");

    private static Answer Mismatch() => Failure(412, "UpdateConditionNotSatisfied");

    private static Answer Missing() => Failure(404, "ResourceNotFound");

    /// <summary>A transport that answers the row requests in order, one scripted answer per attempt.</summary>
    private static ScriptedTableTransport Scripted(params Func<Answer>[] answers)
    {
        var next = new Queue<Func<Answer>>(answers);
        return new ScriptedTableTransport(_ =>
        {
            Func<Answer> answer;
            lock (next)
                answer = next.Count > 0 ? next.Dequeue() : throw new InvalidOperationException("The test scripted no answer for this attempt.");
            return answer();
        });
    }

    private static TableStorage<TestEntity> Store(ScriptedTableTransport transport, bool observe = true) =>
        new(transport.RetryingService(observe), new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<TestEntity>>.Instance,
            authorizer: null, options: new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled });

    private static TestEntity Row(string? etag = null) => new() { Id = "p|r", Name = "n", ETag = etag };

    // ── create: a lost response, then 409 on the retry ────────────────────

    [Fact]
    public async Task Create_WhoseResponseIsLost_AndWhoseRetryFindsTheRow_MayBeItsOwnWrite()
    {
        var transport = Scripted(Lost, Duplicate);

        var duplicate = await Assert.ThrowsAsync<DuplicateKeyException>(() => Store(transport).CreateAsync(Row(), Ct));

        Assert.True(duplicate.MayBeOwnWrite);
        Assert.Equal("EntityAlreadyExists", duplicate.ProviderErrorCode);
        Assert.Equal(2, transport.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/TestEntity", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Create_SentOnce_AndRefusedAsADuplicate_IsNotItsOwnWrite()
    {
        var duplicate = await Assert.ThrowsAsync<DuplicateKeyException>(() => Store(Scripted(Duplicate)).CreateAsync(Row(), Ct));

        Assert.False(duplicate.MayBeOwnWrite);
    }

    [Fact]
    public async Task Create_OverAClientWithoutTheObservation_MayAlwaysBeItsOwnWrite()
    {
        var duplicate = await Assert.ThrowsAsync<DuplicateKeyException>(
            () => Store(Scripted(Duplicate), observe: false).CreateAsync(Row(), Ct));

        Assert.True(duplicate.MayBeOwnWrite);
    }

    [Fact]
    public async Task TheObservation_AddedTwice_CountsEachAttemptOnce()
    {
        var transport = Scripted(Duplicate);
        var options = new TableClientOptions { Transport = transport }.AddRetryObservation().AddRetryObservation();
        var store = new TableStorage<TestEntity>(
            new TableServiceClient(new Uri("https://acct.table.core.windows.net/"), new TableSharedKeyCredential("acct", Convert.ToBase64String(new byte[32])), options),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<TestEntity>>.Instance);

        var duplicate = await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(Row(), Ct));

        Assert.False(duplicate.MayBeOwnWrite);
        Assert.Throws<ArgumentNullException>(() => ((TableClientOptions)null!).AddRetryObservation());
    }

    // ── update: a lost response, then 412 on the retry ────────────────────

    [Fact]
    public async Task Update_WhoseResponseIsLost_AndWhoseRetryFailsTheVersionCheck_MayBeItsOwnWrite()
    {
        var transport = Scripted(Lost, Mismatch);

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Store(transport).UpdateAsync(Row(V1), Ct));

        Assert.True(conflict.MayBeOwnWrite);
        Assert.Equal(2, transport.EntityRequests.Count(r => r.Method == "PUT"));
    }

    [Fact]
    public async Task Update_SentOnce_AndRefusedOnItsVersion_IsNotItsOwnWrite()
    {
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Store(Scripted(Mismatch)).UpdateAsync(Row(V1), Ct));

        Assert.False(conflict.MayBeOwnWrite);
        Assert.Equal("UpdateConditionNotSatisfied", conflict.ProviderErrorCode);
    }

    [Fact]
    public async Task ConditionalMerge_ReportsARetriedRequest_AndOnlyThat()
    {
        var retried = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Store(Scripted(Lost, Mismatch)).UpdateAsync("p|r", b => b.SetProperty(e => e.Name, "x").WithETag(V1), Ct));
        var once = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Store(Scripted(Mismatch)).UpdateAsync("p|r", b => b.SetProperty(e => e.Name, "x").WithETag(V1), Ct));
        var unobserved = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Store(Scripted(Mismatch), observe: false).UpdateAsync("p|r", b => b.SetProperty(e => e.Name, "x").WithETag(V1), Ct));

        Assert.True(retried.MayBeOwnWrite);
        Assert.False(once.MayBeOwnWrite);
        Assert.True(unobserved.MayBeOwnWrite);
    }

    [Fact]
    public async Task AWriteThatSucceedsOnItsRetry_Succeeds()
    {
        var transport = Scripted(Lost, Ok);

        var created = await Store(transport).CreateAsync(Row(), Ct);

        Assert.Equal(V1, created.ETag);
    }

    // ── delete: a lost response, then 404 on the retry ────────────────────

    [Fact]
    public async Task DeleteIfExists_WhoseResponseIsLost_AndWhoseRetryFindsNoRow_IsIndeterminate()
    {
        var transport = Scripted(Lost, Missing);

        var status = await Store(transport).DeleteIfExistsAsync("p|r", Ct);

        Assert.Equal(DeleteStatus.Indeterminate, status);
        Assert.Equal(2, transport.EntityRequests.Count(r => r.Method == "DELETE"));
    }

    [Fact]
    public async Task DeleteIfExists_SentOnce_SaysWhetherThereWasARow()
    {
        Assert.Equal(DeleteStatus.Deleted, await Store(Scripted(Ok)).DeleteIfExistsAsync("p|r", Ct));
        Assert.Equal(DeleteStatus.NotFound, await Store(Scripted(Missing)).DeleteIfExistsAsync("p|r", Ct));
        // The retry removed it: this call's own delete, beyond doubt.
        Assert.Equal(DeleteStatus.Deleted, await Store(Scripted(Lost, Ok)).DeleteIfExistsAsync("p|r", Ct));
    }

    [Fact]
    public async Task DeleteIfExists_OverAClientWithoutTheObservation_CannotSayNotFound()
    {
        var store = Store(Scripted(Missing, Ok), observe: false);

        Assert.Equal(DeleteStatus.Indeterminate, await store.DeleteIfExistsAsync("p|r", Ct));
        Assert.Equal(DeleteStatus.Deleted, await store.DeleteIfExistsAsync("p|r", Ct));
    }

    [Fact]
    public async Task DeleteIfExists_OfARowInATableThatIsGone_IsNotFound_WhateverWasSent()
    {
        Answer TableGone() => Failure(404, "TableNotFound");

        Assert.Equal(DeleteStatus.NotFound, await Store(Scripted(Lost, TableGone)).DeleteIfExistsAsync("p|r", Ct));
        Assert.Equal(DeleteStatus.NotFound, await Store(Scripted(TableGone), observe: false).DeleteIfExistsAsync("p|r", Ct));
    }

    [Fact]
    public async Task DeleteIfMatch_SendsTheCallersVersion_AndNeverLoopsOnAMismatch()
    {
        var transport = Scripted(Mismatch);

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Store(transport).DeleteIfMatchAsync("P|R", V1, Ct));

        var sent = Assert.Single(transport.EntityRequests);
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal(V1, sent.IfMatch);
        Assert.Contains("PartitionKey='p',RowKey='r'", sent.Path, StringComparison.Ordinal);
        Assert.False(conflict.MayBeOwnWrite);
        Assert.Equal("p|r", conflict.Id);
    }

    [Fact]
    public async Task DeleteIfMatch_ReportsDeleted_NotFound_AndIndeterminate()
    {
        Assert.Equal(DeleteStatus.Deleted, await Store(Scripted(Ok)).DeleteIfMatchAsync("p|r", V1, Ct));
        Assert.Equal(DeleteStatus.NotFound, await Store(Scripted(Missing)).DeleteIfMatchAsync("p|r", V1, Ct));
        Assert.Equal(DeleteStatus.Indeterminate, await Store(Scripted(Lost, Missing)).DeleteIfMatchAsync("p|r", V1, Ct));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData(null)]
    public async Task DeleteIfMatch_WithoutAVersion_IsRefused_BeforeAnyRequest(string? etag)
    {
        var transport = Scripted();
        var store = Store(transport);

        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteIfMatchAsync("p|r", etag!, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ((IStorage<TestEntity>)store).DeleteIfMatchAsync("p|r", etag!, Ct));

        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task DeleteAsync_StaysANoOp_ForARowThatIsNotThere()
    {
        var transport = Scripted(Missing, Lost, Missing);
        var store = Store(transport);

        await store.DeleteAsync("p|r", Ct);
        await store.DeleteAsync("p|r", Ct);

        Assert.Equal(3, transport.EntityRequests.Count);
    }

    // ── transient failures: may the write have committed? ─────────────────

    [Fact]
    public async Task AWrite_WhoseEveryAttemptWasRejectedAsBusy_HasNotCommitted()
    {
        Answer Busy() => Failure(503, "ServerBusy");
        var transport = Scripted(Busy, Busy, Busy, Busy);

        var transient = await Assert.ThrowsAsync<TransientStorageException>(() => Store(transport).CreateAsync(Row(), Ct));

        Assert.False(transient.MayHaveCommitted);
        Assert.Contains("No attempt of it was applied", transient.Message, StringComparison.Ordinal);
        Assert.Equal(4, transport.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/TestEntity", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AWrite_ThrottledOnEveryAttempt_HasNotCommitted()
    {
        Answer Throttled() => Failure(429, "TooManyRequests");

        var transient = await Assert.ThrowsAsync<TransientStorageException>(
            () => Store(Scripted(Throttled, Throttled, Throttled, Throttled)).UpsertAsync(Row(), Ct));

        Assert.False(transient.MayHaveCommitted);
    }

    public static TheoryData<string> AmbiguousOutcomes => ["lost", "lost then busy", "timed out on the server", "bad gateway", "unavailable without a code"];

    [Theory]
    [MemberData(nameof(AmbiguousOutcomes))]
    public async Task AWrite_WithAnAttemptThatMayHaveReachedTheService_MayHaveCommitted(string outcome)
    {
        Answer Busy() => Failure(503, "ServerBusy");
        Func<Answer>[] answers = outcome switch
        {
            "lost" => [Lost, Lost, Lost, Lost],
            "lost then busy" => [Lost, Busy, Busy, Busy],
            // The service documents 500 OperationTimedOut as "may or may not have succeeded".
            "timed out on the server" => [.. Enumerable.Repeat<Func<Answer>>(() => Failure(500, "OperationTimedOut"), 4)],
            "bad gateway" => [.. Enumerable.Repeat<Func<Answer>>(() => Failure(502, null), 4)],
            // A 503 that does not name the service's own "busy" may come from a gateway that had already forwarded the request.
            _ => [.. Enumerable.Repeat<Func<Answer>>(() => Failure(503, null), 4)],
        };

        var transient = await Assert.ThrowsAsync<TransientStorageException>(() => Store(Scripted(answers)).CreateAsync(Row(), Ct));

        Assert.True(transient.MayHaveCommitted);
        Assert.Contains("It may have been applied", transient.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrite_OverAClientWithoutTheObservation_MayAlwaysHaveCommitted()
    {
        Answer Busy() => Failure(503, "ServerBusy");

        var transient = await Assert.ThrowsAsync<TransientStorageException>(
            () => Store(Scripted(Busy, Busy, Busy, Busy), observe: false).CreateAsync(Row(), Ct));

        Assert.True(transient.MayHaveCommitted);
    }

    [Fact]
    public async Task ARead_NeverMayHaveCommitted()
    {
        Answer Busy() => Failure(503, "ServerBusy");

        var transient = await Assert.ThrowsAsync<TransientStorageException>(() => Store(Scripted(Busy, Busy, Busy, Busy)).OneAsync("p|r", Ct));

        Assert.False(transient.MayHaveCommitted);
    }

    // ── a retried read never labels the write that follows it ─────────────

    [Fact]
    public async Task AWriteSentOnce_AfterAReadThatWasRetried_IsNotItsOwnWrite()
    {
        // A protected property makes the update read the row first; that read's response is lost once.
        // The replace itself is sent once and refused: the retried read says nothing about it.
        const string stored = "{\"odata.etag\":\"W/\\\"1\\\"\",\"PartitionKey\":\"p\",\"RowKey\":\"r\",\"Name\":\"n\",\"Salary\":1.0}";
        var transport = Scripted(Lost, () => new Answer(200, stored, ETag: "W/\"1\""), Mismatch);
        var store = new TableStorage<ProtectedEntity>(transport.RetryingService(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TableStorage<ProtectedEntity>>.Instance, authorizer: null, options: new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled });

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpdateAsync(new ProtectedEntity { Id = "p|r", Name = "n", Salary = 1m, ETag = "W/\"1\"" }, Ct));

        Assert.False(conflict.MayBeOwnWrite);
        Assert.Equal(2, transport.EntityRequests.Count(r => r.Method == "GET"));
        Assert.Single(transport.EntityRequests, r => r.Method == "PUT");
    }

    // ── the polymorphic store ─────────────────────────────────────────────

    [Fact]
    public async Task PolymorphicInsert_ReportsARetriedRequest_AndOnlyThat()
    {
        static PolymorphicTableStorage<TestMessage> Events(ScriptedTableTransport transport, bool observe = true) =>
            new(transport.RetryingService(observe), "Events", NullLogger<PolymorphicTableStorage<TestMessage>>.Instance);
        var write = new PolymorphicWrite<TestMessage>(new TableKey("p", "r"), new TestCommand { Id = "c", Operation = "go" });

        var retried = await Assert.ThrowsAsync<DuplicateKeyException>(() => Events(Scripted(Lost, Duplicate)).InsertAsync(write, Ct));
        var once = await Assert.ThrowsAsync<DuplicateKeyException>(() => Events(Scripted(Duplicate)).InsertAsync(write, Ct));
        var unobserved = await Assert.ThrowsAsync<DuplicateKeyException>(() => Events(Scripted(Duplicate), observe: false).InsertAsync(write, Ct));

        Assert.True(retried.MayBeOwnWrite);
        Assert.False(once.MayBeOwnWrite);
        Assert.True(unobserved.MayBeOwnWrite);
    }
}
