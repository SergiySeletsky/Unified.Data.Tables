using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.NeutralFailures;

/// <summary>
/// 0.10.0, break B3: the in-memory stores throw the provider-neutral types too, so a test written
/// against the fake describes production. Each test puts the same condition to
/// <see cref="InMemoryStorage{T}"/> and to <see cref="TableStorage{T}"/> (the real SDK answered by a
/// scripted transport with what the service says in that case) and compares what leaves: the same
/// type, the same message, the same <see cref="StorageException.ProviderErrorCode"/>. The one intended
/// difference is <c>MayBeOwnWrite</c>: the fake never sends a request twice, so it is false there.
/// </summary>
public class InMemoryNeutralFailureParityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Error(string code) =>
        "{\"odata.error\":{\"code\":\"" + code + "\",\"message\":{\"lang\":\"en-US\",\"value\":\"" + code + "\"}}}";

    /// <summary>TableStorage over the real SDK, every entity request answered with one service error.</summary>
    private static TableStorage<T> Azure<T>(int status, string code) where T : class, IEntity, new()
    {
        var transport = new ScriptedTableTransport(_ => new ScriptedTableTransport.Answer(status, Error(code), ErrorCode: code));
        return new TableStorage<T>(transport.Service(), new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<T>>.Instance,
            authorizer: null, options: new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled });
    }

    private static async Task<TException> SameOnBothAsync<TException>(Func<Task> memory, Func<Task> azure)
        where TException : StorageException
    {
        var fromMemory = await Assert.ThrowsAsync<TException>(memory);
        var fromAzure = await Assert.ThrowsAsync<TException>(azure);

        Assert.Equal(fromAzure.Message, fromMemory.Message);
        Assert.Equal(fromAzure.ProviderErrorCode, fromMemory.ProviderErrorCode);
        // The fake reports the failure as the service does: both carry one, of the same status.
        Assert.Equal(
            Assert.IsType<RequestFailedException>(fromAzure.InnerException, exactMatch: false).Status,
            Assert.IsType<RequestFailedException>(fromMemory.InnerException, exactMatch: false).Status);
        return fromMemory;
    }

    // ── a row that is not there ────────────────────────────────────────────

    [Fact]
    public async Task Update_OfAMissingRow_ThrowsEntityNotFound_OnBoth()
    {
        var memory = new InMemoryStorage<TestEntity>();
        var azure = Azure<TestEntity>(404, "ResourceNotFound");

        var ex = await SameOnBothAsync<EntityNotFoundException>(
            () => memory.UpdateAsync(new TestEntity { Id = "P|My Row" }, ConcurrencyMode.LastWriterWins, Ct),
            () => azure.UpdateAsync(new TestEntity { Id = "P|My Row" }, ConcurrencyMode.LastWriterWins, Ct));

        Assert.Equal("p|my-row", ex.Id);
        Assert.Equal(nameof(TestEntity), ex.EntityType);
        Assert.Equal("ResourceNotFound", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task BuilderUpdate_OfAMissingRow_ThrowsEntityNotFound_OnBoth()
    {
        var memory = new InMemoryStorage<TestEntity>();
        var azure = Azure<TestEntity>(404, "ResourceNotFound");

        var ex = await SameOnBothAsync<EntityNotFoundException>(
            () => memory.UpdateAsync("P|My Row", b => b.SetProperty(x => x.Name, "n"), Ct),
            () => azure.UpdateAsync("P|My Row", b => b.SetProperty(x => x.Name, "n"), Ct));

        Assert.Equal("p|my-row", ex.Id);
    }

    [Fact]
    public async Task Update_OfAMissingRow_OfATypeWithLargeValues_ThrowsEntityNotFound_OnBoth_CarryingThe404()
    {
        // Such an update reads the row first: both stores find none themselves, so nothing was sent. The
        // service's 404 rides along all the same (0.9.x threw it here), as it does for a plain type.
        var memory = new InMemoryStorage<SplitTextEntity>();
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet(null);

        var fromMemory = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => memory.UpdateAsync(new SplitTextEntity { Id = "p|r", Body = "b" }, ConcurrencyMode.LastWriterWins, Ct));
        var fromAzure = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => h.Store.UpdateAsync(new SplitTextEntity { Id = "p|r", Body = "b" }, ConcurrencyMode.LastWriterWins, Ct));

        Assert.Equal(fromAzure.Message, fromMemory.Message);
        Assert.Equal("p|r", fromMemory.Id);
        Assert.Equal("ResourceNotFound", fromMemory.ProviderErrorCode);
        Assert.Equal("ResourceNotFound", fromAzure.ProviderErrorCode);
        Assert.Equal(404, Assert.IsType<RequestFailedException>(fromMemory.InnerException).Status);
        Assert.Equal(404, Assert.IsType<RequestFailedException>(fromAzure.InnerException).Status);
        Assert.Equal("ResourceNotFound", ((RequestFailedException)fromAzure.InnerException!).ErrorCode);
    }

    [Fact]
    public async Task MergeColumns_OfAMissingRow_ThrowsEntityNotFound_OnBoth()
    {
        var memory = new InMemoryPolymorphicStorage<TestMessage>("TestTable");
        using var h = new PolymorphicHarness<TestMessage>();
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null));
        var key = new TableKey("p", "r");
        var columns = new Dictionary<string, object> { ["_IsPublished"] = true };

        var ex = await SameOnBothAsync<EntityNotFoundException>(
            () => memory.MergeColumnsAsync(key, columns, Ct),
            () => h.Store.MergeColumnsAsync(key, columns, Ct));

        Assert.Equal("p|r", ex.Id);
        Assert.Equal("TestTable", ex.EntityType);
    }

    // ── a request the service refuses ──────────────────────────────────────

    [Theory]
    [InlineData("create")]
    [InlineData("upsert")]
    public async Task AKeyTheServiceRefuses_ThrowsStorageRequestRejected_OnBoth(string write)
    {
        var memory = new InMemoryStorage<TestEntity>();
        var azure = Azure<TestEntity>(400, "InvalidInput");
        Task Call(IStorage<TestEntity> store) => write == "create"
            ? store.CreateAsync(new TestEntity { Id = "p|a#b" }, Ct)
            : store.UpsertAsync(new TestEntity { Id = "p|a#b" }, Ct);

        var ex = await SameOnBothAsync<StorageRequestRejectedException>(() => Call(memory), () => Call(azure));

        Assert.Equal("InvalidInput", ex.ProviderErrorCode);
        Assert.Empty(memory.Snapshot());
    }

    [Fact]
    public async Task APropertyTheServiceRefusesAsTooLarge_ThrowsStorageRequestRejected_OnBoth()
    {
        var memory = new InMemoryStorage<OversizedBinaryCellFidelityTests.BinaryEntity>();
        var azure = Azure<OversizedBinaryCellFidelityTests.BinaryEntity>(400, "PropertyValueTooLarge");
        OversizedBinaryCellFidelityTests.BinaryEntity Oversized() => new() { Id = "p|r", Bytes = new byte[64 * 1024 + 1] };

        var ex = await SameOnBothAsync<StorageRequestRejectedException>(
            () => memory.UpsertAsync(Oversized(), Ct), () => azure.UpsertAsync(Oversized(), Ct));

        Assert.Equal("PropertyValueTooLarge", ex.ProviderErrorCode);
        Assert.Empty(memory.Snapshot());
    }

    [Fact]
    public async Task AnUpsertBatch_ThatNamesARowTwice_ThrowsStorageRequestRejected_OnBoth()
    {
        var memory = new InMemoryStorage<TestEntity>();
        using var h = new StorageHarness<TestEntity>();
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw new TableTransactionFailedException(
                new RequestFailedException(400, "InvalidDuplicateRow", "InvalidDuplicateRow", null)));
        List<TestEntity> Twice() => [new TestEntity { Id = "p|r", Value = 1 }, new TestEntity { Id = "p|r", Value = 2 }];

        var ex = await SameOnBothAsync<StorageRequestRejectedException>(
            () => memory.UpsertBatchAsync(Twice(), Ct), () => h.Store.UpsertBatchAsync(Twice(), Ct));

        Assert.Equal("InvalidDuplicateRow", ex.ProviderErrorCode);
        Assert.IsType<TableTransactionFailedException>(ex.InnerException);
    }

    [Fact]
    public async Task APolymorphicKeyTheServiceRefuses_ThrowsStorageRequestRejected_OnBoth()
    {
        var memory = new InMemoryPolymorphicStorage<TestMessage>("TestTable");
        using var h = new PolymorphicHarness<TestMessage>();
        h.Table.UpsertEntityAsync(Arg.Any<TableEntity>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw new RequestFailedException(400, "bad key", "InvalidInput", null));
        var write = new PolymorphicWrite<TestMessage>(new TableKey("p", "a?b"), new TestCreatedEvent { Id = "e" });

        var ex = await SameOnBothAsync<StorageRequestRejectedException>(
            () => memory.UpsertAsync(write, Ct), () => h.Store.UpsertAsync(write, Ct));

        Assert.Equal("InvalidInput", ex.ProviderErrorCode);
    }

    // ── duplicates and conflicts: the same types, but never the fake's own write ──

    [Fact]
    public async Task ADuplicateCreate_IsTheSameTypeOnBoth_AndOnlyAzureMayBeItsOwnWrite()
    {
        var memory = new InMemoryStorage<TestEntity>();
        await memory.CreateAsync(new TestEntity { Id = "p|r" }, Ct);
        var azure = Azure<TestEntity>(409, "EntityAlreadyExists");

        var fromMemory = await Assert.ThrowsAsync<DuplicateKeyException>(() => memory.CreateAsync(new TestEntity { Id = "p|r" }, Ct));
        var fromAzure = await Assert.ThrowsAsync<DuplicateKeyException>(() => azure.CreateAsync(new TestEntity { Id = "p|r" }, Ct));

        Assert.Equal(fromAzure.Message, fromMemory.Message);
        Assert.Equal(fromAzure.Id, fromMemory.Id);
        Assert.Equal("EntityAlreadyExists", fromAzure.ProviderErrorCode);
        Assert.Equal(fromAzure.ProviderErrorCode, fromMemory.ProviderErrorCode);
        Assert.False(fromMemory.MayBeOwnWrite); // the fake never retries
        Assert.True(fromAzure.MayBeOwnWrite);   // the SDK may have
    }

    [Fact]
    public async Task ADuplicateInACreateBatch_IsNeverTheFakesOwnWrite()
    {
        var memory = new InMemoryStorage<TestEntity>();
        await memory.CreateAsync(new TestEntity { Id = "p|a" }, Ct);

        var existing = await Assert.ThrowsAsync<DuplicateKeyException>(
            () => memory.CreateBatchAsync([new TestEntity { Id = "p|a" }, new TestEntity { Id = "p|b" }], Ct));
        var repeated = await Assert.ThrowsAsync<DuplicateKeyException>(
            () => memory.CreateBatchAsync([new TestEntity { Id = "p|c" }, new TestEntity { Id = "p|c" }], Ct));

        Assert.False(existing.MayBeOwnWrite);
        Assert.False(repeated.MayBeOwnWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALostVersionCheck_IsTheSameTypeOnBoth_AndOnlyAzureMayBeItsOwnWrite(bool builder)
    {
        var memory = new InMemoryStorage<TestEntity>();
        await memory.CreateAsync(new TestEntity { Id = "p|r" }, Ct);
        var azure = Azure<TestEntity>(412, "UpdateConditionNotSatisfied");
        Task Call(IStorage<TestEntity> store) => builder
            ? store.UpdateAsync("p|r", b => b.SetProperty(x => x.Name, "n").WithETag("W/\"stale\""), Ct)
            : store.UpdateAsync(new TestEntity { Id = "p|r", ETag = "W/\"stale\"" }, ConcurrencyMode.Strict, Ct);

        var fromMemory = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Call(memory));
        var fromAzure = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Call(azure));

        Assert.Equal(fromAzure.Message, fromMemory.Message);
        Assert.Equal("UpdateConditionNotSatisfied", fromAzure.ProviderErrorCode);
        Assert.Equal(fromAzure.ProviderErrorCode, fromMemory.ProviderErrorCode);
        Assert.False(fromMemory.MayBeOwnWrite);
        Assert.True(fromAzure.MayBeOwnWrite);
    }

    [Fact]
    public async Task APolymorphicDuplicate_IsNeverTheFakesOwnWrite()
    {
        var memory = new InMemoryPolymorphicStorage<TestMessage>("TestTable");
        var write = new PolymorphicWrite<TestMessage>(new TableKey("p", "r"), new TestCreatedEvent { Id = "e" });
        await memory.InsertAsync(write, Ct);

        var single = await Assert.ThrowsAsync<DuplicateKeyException>(() => memory.InsertAsync(write, Ct));
        var batch = await Assert.ThrowsAsync<DuplicateKeyException>(() => memory.InsertBatchAsync([write], Ct));

        Assert.False(single.MayBeOwnWrite);
        Assert.False(batch.MayBeOwnWrite);
    }

    // ── nothing of the provider leaves the fake ────────────────────────────

    [Fact]
    public async Task NoMemberOfTheFake_ThrowsAnAzureException()
    {
        var store = new InMemoryStorage<TestEntity>();
        var poly = new InMemoryPolymorphicStorage<TestMessage>("TestTable");
        await store.CreateAsync(new TestEntity { Id = "p|r" }, Ct);
        var refusedKey = new PolymorphicWrite<TestMessage>(new TableKey("p", "a/b"), new TestCreatedEvent { Id = "e" });
        var failing = new Func<Task>[]
        {
            () => store.CreateAsync(new TestEntity { Id = "p|r" }, Ct),
            () => store.CreateAsync(new TestEntity { Id = "p|a\\b" }, Ct),
            () => store.UpdateAsync(new TestEntity { Id = "p|missing" }, ConcurrencyMode.LastWriterWins, Ct),
            () => store.UpdateAsync(new TestEntity { Id = "p|r", ETag = "W/\"stale\"" }, ConcurrencyMode.Strict, Ct),
            () => store.UpdateAsync("p|missing", b => b.SetProperty(x => x.Name, "n"), Ct),
            () => store.UpdateAsync("p|a#b", b => b.SetProperty(x => x.Name, "n"), Ct),
            () => store.CreateBatchAsync([new TestEntity { Id = "p|r" }], Ct),
            () => store.CreateBatchAsync([new TestEntity { Id = "p|x?y" }], Ct),
            () => store.UpsertBatchAsync([new TestEntity { Id = "p|x?y" }], Ct),
            () => store.UpsertBatchAsync([new TestEntity { Id = "p|d" }, new TestEntity { Id = "p|d" }], Ct),
            () => poly.InsertAsync(refusedKey, Ct),
            () => poly.UpsertAsync(refusedKey, Ct),
            () => poly.InsertBatchAsync([refusedKey], Ct),
            () => poly.MergeColumnsAsync(new TableKey("p", "missing"), new Dictionary<string, object> { ["_IsPublished"] = true }, Ct),
        };

        foreach (var call in failing)
        {
            var thrown = await Record.ExceptionAsync(call);

            Assert.IsType<StorageException>(thrown, exactMatch: false);
            Assert.IsNotType<RequestFailedException>(thrown, exactMatch: false);
        }
    }
}
