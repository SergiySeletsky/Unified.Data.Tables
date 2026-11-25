using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.NeutralFailures;

/// <summary>
/// 0.10.0, break B1: no Azure exception leaves a member of <see cref="TableStorage{T}"/>. Whatever the
/// SDK throws is mapped once, by <c>AzureStorageErrors</c>, to a provider-neutral
/// <see cref="StorageException"/> whose inner exception is the SDK's own. The first half runs the real
/// SDK over a scripted transport (what the service answers is what the SDK parses); the second half
/// stubs the table client for the failures that have no HTTP answer (a timeout, used-up retries).
/// </summary>
public class AzureErrorMappingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Error(string code) =>
        "{\"odata.error\":{\"code\":\"" + code + "\",\"message\":{\"lang\":\"en-US\",\"value\":\"" + code + "\"}}}";

    private static ScriptedTableTransport.Answer Failure(int status, string? code, IReadOnlyDictionary<string, string>? headers = null) =>
        code is null
            ? new ScriptedTableTransport.Answer(status, Headers: headers)
            : new ScriptedTableTransport.Answer(status, Error(code), ErrorCode: code, Headers: headers);

    private static (TableStorage<TestEntity> Store, ScriptedTableTransport Transport, MemoryCache Cache) Scripted(
        Func<ScriptedTableTransport.Sent, ScriptedTableTransport.Answer> respond)
    {
        var transport = new ScriptedTableTransport(respond);
        var cache = new MemoryCache(new MemoryCacheOptions());
        return (new TableStorage<TestEntity>(transport.Service(), cache, NullLogger<TableStorage<TestEntity>>.Instance), transport, cache);
    }

    /// <summary>Status and service error code, and the neutral type each maps to.</summary>
    public static TheoryData<int, string?, Type> MappingRows => new()
    {
        // A missing row: the two codes the service documents, and a 404 without a code.
        { 404, "ResourceNotFound", typeof(EntityNotFoundException) },
        { 404, "EntityNotFound", typeof(EntityNotFoundException) },
        { 404, null, typeof(EntityNotFoundException) },
        // A missing table is never a missing row.
        { 404, "TableNotFound", typeof(TableNotFoundException) },
        // A 404 that names anything else is neither.
        { 404, "SomeOtherResourceNotFound", typeof(StorageException) },
        { 409, "EntityAlreadyExists", typeof(DuplicateKeyException) },
        // A 409 that is not about a row (a table being deleted) is no duplicate key.
        { 409, "TableBeingDeleted", typeof(StorageException) },
        { 412, "UpdateConditionNotSatisfied", typeof(ConcurrencyConflictException) },
        { 400, "InvalidInput", typeof(StorageRequestRejectedException) },
        { 400, "PropertyValueTooLarge", typeof(StorageRequestRejectedException) },
        { 413, "RequestBodyTooLarge", typeof(StorageRequestRejectedException) },
        { 408, "OperationTimedOut", typeof(TransientStorageException) },
        { 429, "TooManyRequests", typeof(TransientStorageException) },
        { 500, "InternalError", typeof(TransientStorageException) },
        { 502, null, typeof(TransientStorageException) },
        { 503, "ServerBusy", typeof(TransientStorageException) },
        { 504, null, typeof(TransientStorageException) },
        // Anything else: the base type.
        { 401, "InvalidAuthenticationInfo", typeof(StorageException) },
        { 403, "AuthorizationFailure", typeof(StorageException) },
        { 405, "MethodNotAllowed", typeof(StorageException) },
    };

    [Theory]
    [MemberData(nameof(MappingRows))]
    public async Task AWrite_ThatTheServiceFails_ThrowsTheNeutralType_WithTheSdksExceptionInside(int status, string? code, Type expected)
    {
        var (store, transport, cache) = Scripted(_ => Failure(status, code));
        using var _ = cache;

        var thrown = await Record.ExceptionAsync(() => store.UpsertAsync(new TestEntity { Id = "p|r", Name = "n" }, Ct));

        Assert.NotNull(thrown);
        Assert.Equal(expected, thrown.GetType());
        var neutral = Assert.IsType<StorageException>(thrown, exactMatch: false);
        Assert.Equal(code, neutral.ProviderErrorCode);
        var sdk = Assert.IsType<RequestFailedException>(neutral.InnerException);
        Assert.Equal(status, sdk.Status);
        Assert.Single(transport.EntityRequests); // mapped, not retried
        switch (neutral)
        {
            case TransientStorageException transient:
                Assert.True(transient.MayHaveCommitted); // a write: an earlier attempt may have been applied
                break;
            case DuplicateKeyException duplicate:
                Assert.True(duplicate.MayBeOwnWrite); // the SDK's retries are not observed here
                Assert.Equal("p|r", duplicate.Id);
                break;
            case ConcurrencyConflictException conflict:
                Assert.True(conflict.MayBeOwnWrite);
                Assert.Equal("p|r", conflict.Id);
                break;
            case EntityNotFoundException missing:
                Assert.Equal("p|r", missing.Id);
                Assert.Equal(nameof(TestEntity), missing.EntityType);
                break;
            case TableNotFoundException table:
                Assert.Equal(nameof(TestEntity), table.TableName);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(MappingRows))]
    public async Task ARead_ThatTheServiceFails_ThrowsTheNeutralType_AndNeverSaysItMayHaveCommitted(int status, string? code, Type expected)
    {
        var (store, _, cache) = Scripted(_ => Failure(status, code));
        using var __ = cache;

        // A query: the SDK's point read turns every 404 into "no row" by itself.
        var thrown = await Record.ExceptionAsync(() => store.QueryAsync("p", Ct));

        Assert.NotNull(thrown);
        Assert.Equal(expected, thrown.GetType());
        var neutral = Assert.IsType<StorageException>(thrown, exactMatch: false);
        Assert.Equal(status, Assert.IsType<RequestFailedException>(neutral.InnerException).Status);
        if (neutral is TransientStorageException transient)
            Assert.False(transient.MayHaveCommitted);
        if (neutral is DuplicateKeyException duplicate)
            Assert.False(duplicate.MayBeOwnWrite);
        if (neutral is ConcurrencyConflictException conflict)
            Assert.False(conflict.MayBeOwnWrite);
    }

    [Theory]
    [InlineData("stream by options")]
    [InlineData("stream by predicate")]
    [InlineData("list by options")]
    [InlineData("list by predicate")]
    [InlineData("page")]
    [InlineData("any")]
    [InlineData("count")]
    public async Task EveryReadThatEnumeratesTheTable_MapsItsFailure(string read)
    {
        var (store, _, cache) = Scripted(_ => Failure(503, "ServerBusy"));
        using var __ = cache;
        var options = new QueryOptions { Partition = "p" };

        var thrown = await Assert.ThrowsAsync<TransientStorageException>(async () =>
        {
            switch (read)
            {
                case "stream by options":
                    await foreach (var _ in store.QueryStreamAsync(options, Ct)) { }
                    break;
                case "stream by predicate":
                    await foreach (var _ in store.QueryStreamAsync(e => e.Value > 1, "p", null, Ct)) { }
                    break;
                case "list by options":
                    await store.QueryAsync(options, Ct);
                    break;
                case "list by predicate":
                    await store.QueryAsync(e => e.Value > 1, "p", null, Ct);
                    break;
                case "page":
                    await store.QueryPageAsync(options, Ct);
                    break;
                case "any":
                    await store.AnyAsync(e => e.Value > 1, "p", Ct);
                    break;
                default:
                    await store.CountAsync("p", Ct);
                    break;
            }
        });

        Assert.False(thrown.MayHaveCommitted);
        Assert.Equal("ServerBusy", thrown.ProviderErrorCode);
        Assert.Equal(503, Assert.IsType<RequestFailedException>(thrown.InnerException).Status);
    }

    [Theory]
    [InlineData("one")]
    [InlineData("exists")]
    public async Task APointRead_ThatTheServiceFails_IsMapped(string read)
    {
        var (store, _, cache) = Scripted(_ => Failure(500, "InternalError"));
        using var __ = cache;

        var thrown = await Assert.ThrowsAsync<TransientStorageException>(
            () => read == "one" ? store.OneAsync("p|r", Ct) : store.ExistsAsync("p|r", Ct));

        Assert.False(thrown.MayHaveCommitted);
        Assert.Equal(500, Assert.IsType<RequestFailedException>(thrown.InnerException).Status);
    }

    [Fact]
    public async Task EveryWrite_MapsItsFailure_AndSaysItMayHaveCommitted()
    {
        // Reads find a row (the conditional writes need one); every write is answered 503.
        var (store, _, cache) = Scripted(sent => sent.Method == "GET"
            ? new ScriptedTableTransport.Answer(200, "{\"value\":[{\"odata.etag\":\"W/\\\"v1\\\"\",\"PartitionKey\":\"p\",\"RowKey\":\"r\"}]}")
            : Failure(503, "ServerBusy"));
        using var __ = cache;
        var entity = new TestEntity { Id = "p|r", ETag = "W/\"v1\"" };
        var writes = new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
        {
            ["create"] = () => store.CreateAsync(new TestEntity { Id = "p|r" }, Ct),
            ["upsert"] = () => store.UpsertAsync(new TestEntity { Id = "p|r" }, Ct),
            ["update"] = () => store.UpdateAsync(entity, Ct),
            ["update with a mode"] = () => store.UpdateAsync(entity, ConcurrencyMode.LastWriterWins, Ct),
            ["builder update"] = () => store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "n"), Ct),
            ["create batch"] = () => store.CreateBatchAsync([new TestEntity { Id = "p|a" }, new TestEntity { Id = "p|b" }], Ct),
            ["upsert batch"] = () => store.UpsertBatchAsync([new TestEntity { Id = "p|a" }, new TestEntity { Id = "p|b" }], Ct),
            ["delete"] = () => store.DeleteAsync("p|r", Ct),
            ["delete partition"] = () => store.DeletePartitionAsync("p", Ct),
        };

        foreach (var (name, write) in writes)
        {
            var thrown = await Record.ExceptionAsync(write);

            var transient = Assert.IsType<TransientStorageException>(thrown);
            Assert.True(transient.MayHaveCommitted, name);
            Assert.Equal(503, Assert.IsType<RequestFailedException>(transient.InnerException, exactMatch: false).Status);
        }
    }

    [Fact]
    public async Task EnsureCreated_ThatTheServiceFails_IsMapped()
    {
        using var h = new StorageHarness<TestEntity>();
        h.Table.CreateIfNotExistsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response<Azure.Data.Tables.Models.TableItem>>(new RequestFailedException(403, "no", "AuthorizationFailure", null)));

        var thrown = await Assert.ThrowsAsync<StorageException>(() => h.Store.EnsureCreatedAsync(Ct));

        Assert.Equal("AuthorizationFailure", thrown.ProviderErrorCode);
        Assert.Equal(403, Assert.IsType<RequestFailedException>(thrown.InnerException).Status);
    }

    // ── the create and update paths that name the row themselves ───────────

    [Fact]
    public async Task Create_OfAnExistingRow_IsADuplicateKey_ThatMayBeTheCallsOwnWrite()
    {
        var (store, _, cache) = Scripted(_ => Failure(409, "EntityAlreadyExists"));
        using var __ = cache;

        var thrown = await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(new TestEntity { Id = "p|r" }, Ct));

        // The SDK may have sent the insert twice (a lost response): the row in the way can be its own.
        Assert.True(thrown.MayBeOwnWrite);
        Assert.Equal("EntityAlreadyExists", thrown.ProviderErrorCode);
        Assert.Equal(409, Assert.IsType<RequestFailedException>(thrown.InnerException).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConditionalWrite_ThatLostItsCondition_IsAConflict_ThatMayBeTheCallsOwnWrite(bool builder)
    {
        var (store, _, cache) = Scripted(_ => Failure(412, "UpdateConditionNotSatisfied"));
        using var __ = cache;

        var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => builder
            ? store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "n").WithETag("W/\"v1\""), Ct)
            : store.UpdateAsync(new TestEntity { Id = "p|r", ETag = "W/\"v1\"" }, ConcurrencyMode.Strict, Ct));

        Assert.True(thrown.MayBeOwnWrite);
        Assert.Equal("UpdateConditionNotSatisfied", thrown.ProviderErrorCode);
        Assert.Equal("p|r", thrown.Id);
    }

    [Theory]
    [InlineData("update", "ResourceNotFound")]
    [InlineData("update", null)]
    [InlineData("builder", "ResourceNotFound")]
    [InlineData("builder", "EntityNotFound")]
    public async Task AnUpdate_OfARowThatIsNotThere_ThrowsEntityNotFound_NamingTheRowAsItIsStored(string write, string? code)
    {
        var (store, _, cache) = Scripted(_ => Failure(404, code));
        using var __ = cache;

        // The id is given in its natural form; the row it names is stored under the normalized one.
        var thrown = await Assert.ThrowsAsync<EntityNotFoundException>(() => write == "builder"
            ? store.UpdateAsync("P|My Row", b => b.SetProperty(x => x.Name, "n"), Ct)
            : store.UpdateAsync(new TestEntity { Id = "P|My Row" }, ConcurrencyMode.LastWriterWins, Ct));

        Assert.Equal("p|my-row", thrown.Id);
        Assert.Equal(nameof(TestEntity), thrown.EntityType);
        Assert.Equal(code, thrown.ProviderErrorCode);
        Assert.Equal(404, Assert.IsType<RequestFailedException>(thrown.InnerException).Status);
    }

    [Fact]
    public async Task AnUpdate_WhenTheTableIsGone_ThrowsTableNotFound_NeverEntityNotFound()
    {
        var (store, _, cache) = Scripted(_ => Failure(404, "TableNotFound"));
        using var __ = cache;

        var update = await Record.ExceptionAsync(() => store.UpdateAsync(new TestEntity { Id = "p|r" }, ConcurrencyMode.LastWriterWins, Ct));
        var builder = await Record.ExceptionAsync(() => store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "n"), Ct));
        var query = await Record.ExceptionAsync(() => store.QueryAsync("p", Ct));

        foreach (var thrown in new[] { update, builder, query })
        {
            var table = Assert.IsType<TableNotFoundException>(thrown);
            Assert.Equal(nameof(TestEntity), table.TableName);
            Assert.Equal("TableNotFound", table.ProviderErrorCode);
            Assert.IsNotType<EntityNotFoundException>(thrown, exactMatch: false);
        }
    }

    [Fact]
    public async Task ACreateBatch_WithAnExistingKey_IsADuplicateKey_ThatMayBeTheCallsOwnWrite()
    {
        using var h = new StorageHarness<TestEntity>();
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw new TableTransactionFailedException(
                new RequestFailedException(409, "exists", "EntityAlreadyExists", null)));

        var thrown = await Assert.ThrowsAsync<DuplicateKeyException>(
            () => h.Store.CreateBatchAsync([new TestEntity { Id = "p|a" }, new TestEntity { Id = "p|b" }], Ct));

        Assert.True(thrown.MayBeOwnWrite);
        Assert.IsType<TableTransactionFailedException>(thrown.InnerException);
    }

    [Fact]
    public async Task ABatchTransaction_ThatTheServiceRejects_IsMapped_WithTheTransactionsFailureInside()
    {
        using var h = new StorageHarness<TestEntity>();
        var failure = new TableTransactionFailedException(new RequestFailedException(413, "too large", "RequestBodyTooLarge", null));
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw failure);

        var thrown = await Assert.ThrowsAsync<StorageRequestRejectedException>(
            () => h.Store.UpsertBatchAsync([new TestEntity { Id = "p|a" }, new TestEntity { Id = "p|b" }], Ct));

        Assert.Same(failure, thrown.InnerException);
        Assert.Equal("RequestBodyTooLarge", thrown.ProviderErrorCode);
    }

    // ── Retry-After ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Retry-After", "7", 7000)]
    [InlineData("retry-after-ms", "1500", 1500)]
    [InlineData("x-ms-retry-after-ms", "250", 250)]
    public async Task AThrottledRequest_CarriesTheWaitTheServiceAskedFor(string header, string value, int expectedMs)
    {
        var (store, _, cache) = Scripted(_ => Failure(429, "TooManyRequests", new Dictionary<string, string> { [header] = value }));
        using var __ = cache;

        var thrown = await Assert.ThrowsAsync<TransientStorageException>(() => store.UpsertAsync(new TestEntity { Id = "p|r" }, Ct));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), thrown.RetryAfter);
    }

    [Fact]
    public async Task ARetryAfterGivenAsADate_IsCountedFromTheServicesClock()
    {
        var (store, _, cache) = Scripted(_ => Failure(503, "ServerBusy", new Dictionary<string, string>
        {
            ["Date"] = "Wed, 21 Oct 2026 07:28:00 GMT",
            ["Retry-After"] = "Wed, 21 Oct 2026 07:28:30 GMT",
        }));
        using var __ = cache;

        var thrown = await Assert.ThrowsAsync<TransientStorageException>(() => store.UpsertAsync(new TestEntity { Id = "p|r" }, Ct));

        Assert.Equal(TimeSpan.FromSeconds(30), thrown.RetryAfter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("soon")]
    [InlineData("-5")]
    public async Task WithoutAUsableHint_RetryAfterIsNull(string? value)
    {
        var headers = value is null ? null : new Dictionary<string, string> { ["Retry-After"] = value };
        var (store, _, cache) = Scripted(_ => Failure(503, "ServerBusy", headers));
        using var __ = cache;

        var thrown = await Assert.ThrowsAsync<TransientStorageException>(() => store.UpsertAsync(new TestEntity { Id = "p|r" }, Ct));

        Assert.Null(thrown.RetryAfter);
    }

    // ── failures without an HTTP answer ────────────────────────────────────

    [Fact]
    public async Task ATimeoutTheSdkGaveUpOn_IsTransient_AndAWriteMayHaveCommitted()
    {
        // The SDK's network timeout is a cancellation the caller's token did not ask for.
        var (store, _, cache) = Scripted(_ => throw new TaskCanceledException("The operation was cancelled because it exceeded the configured timeout."));
        using var __ = cache;

        var write = await Assert.ThrowsAsync<TransientStorageException>(() => store.UpsertAsync(new TestEntity { Id = "p|r" }, Ct));
        var read = await Assert.ThrowsAsync<TransientStorageException>(() => store.OneAsync("p|r", Ct));

        Assert.True(write.MayHaveCommitted);
        Assert.IsType<OperationCanceledException>(write.InnerException, exactMatch: false);
        Assert.Null(write.RetryAfter);
        Assert.False(read.MayHaveCommitted);
    }

    [Fact]
    public async Task TheCallersOwnCancellation_IsNotMapped()
    {
        using var h = new StorageHarness<TestEntity>();
        using var cts = new CancellationTokenSource();
        var cancelled = new OperationCanceledException(cts.Token);
        h.Table.UpsertEntityAsync(Arg.Any<TableEntity>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ =>
            {
                cts.Cancel(); // cancelled while the request is in flight
                throw cancelled;
            });

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Store.UpsertAsync(new TestEntity { Id = "p|r" }, cts.Token));

        Assert.Same(cancelled, thrown);
    }

    [Fact]
    public async Task AStream_CancelledThroughWithCancellation_IsNotMapped()
    {
        using var h = new StorageHarness<TestEntity>();
        using var cts = new CancellationTokenSource();
        h.Table.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<AsyncPageable<TableEntity>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        // The token reaches the stream only through the enumerator, not through the method's parameter.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in h.Store.QueryStreamAsync(new QueryOptions { Partition = "p" }).WithCancellation(cts.Token)) { }
        });
    }

    [Fact]
    public async Task AStream_ThatTimesOutWhileTheCallerDidNotCancel_IsTransient()
    {
        using var h = new StorageHarness<TestEntity>();
        h.Table.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<AsyncPageable<TableEntity>>(_ => throw new TaskCanceledException("network timeout"));

        var thrown = await Assert.ThrowsAsync<TransientStorageException>(async () =>
        {
            await foreach (var _ in h.Store.QueryStreamAsync(new QueryOptions { Partition = "p" }).WithCancellation(Ct)) { }
        });

        Assert.False(thrown.MayHaveCommitted);
    }

    [Fact]
    public async Task TheSdksRetriesUsedUp_IsTransient_WithTheAggregateInside()
    {
        using var h = new StorageHarness<TestEntity>();
        // What Azure.Core's retry policy throws once several attempts failed without a response.
        var aggregate = new AggregateException(
            "Retry failed after 4 tries. Retry settings can be adjusted in ClientOptions.Retry or by configuring a custom retry policy.",
            new RequestFailedException(0, "connection reset"), new TaskCanceledException("network timeout"));
        h.Table.UpsertEntityAsync(Arg.Any<TableEntity>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw aggregate);

        var thrown = await Assert.ThrowsAsync<TransientStorageException>(() => h.Store.UpsertAsync(new TestEntity { Id = "p|r" }, Ct));

        Assert.Same(aggregate, thrown.InnerException);
        Assert.True(thrown.MayHaveCommitted);
    }

    [Fact]
    public async Task ARequestThatGotNoResponse_IsTransient()
    {
        using var h = new StorageHarness<TestEntity>();
        var failure = new RequestFailedException(0, "No such host is known.", new IOException("dns"));
        h.Table.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>()).Returns<Response>(_ => throw failure);

        var thrown = await Assert.ThrowsAsync<TransientStorageException>(() => h.Store.CreateAsync(new TestEntity { Id = "p|r" }, Ct));

        Assert.Same(failure, thrown.InnerException);
        Assert.True(thrown.MayHaveCommitted);
    }

    [Fact]
    public async Task WhatIsNotTheProvidersFailure_IsLeftAsItIs()
    {
        using var h = new StorageHarness<TestEntity>();
        var bug = new InvalidOperationException("not the service");
        h.Table.UpsertEntityAsync(Arg.Any<TableEntity>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw bug);

        Assert.Same(bug, await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.UpsertAsync(new TestEntity { Id = "p|r" }, Ct)));
        // Argument validation is unchanged too.
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.UpsertAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.OneAsync(" ", Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.DeleteAsync("", Ct));
    }

    [Fact]
    public async Task ANeutralFailure_IsNotWrappedASecondTime()
    {
        // A protected-property check reads the row through OneAsync inside the update: the read's
        // failure (a read's: nothing was written) leaves the update as it is.
        using var h = new StorageHarness<ProtectedEntity>();
        h.Table.GetEntityIfExistsAsync<TableEntity>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<NullableResponse<TableEntity>>(_ => throw new RequestFailedException(503, "busy", "ServerBusy", null));

        var thrown = await Assert.ThrowsAsync<TransientStorageException>(
            () => h.Store.UpsertAsync(new ProtectedEntity { Id = "p|r", Secret = "s" }, Ct));

        Assert.IsType<RequestFailedException>(thrown.InnerException);
        Assert.False(thrown.MayHaveCommitted);
    }

    public sealed class ProtectedEntity : Entity
    {
        [ProtectedProperty("admin")]
        public string Secret { get; set; } = "";
    }
}
