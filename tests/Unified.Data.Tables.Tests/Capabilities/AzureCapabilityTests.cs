#pragma warning disable UDT0001 // the experimental raw-table API is what some of these tests are about

using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.Capabilities;

/// <summary>
/// 0.10.0: what the Azure provider adds beside <see cref="IStorage{T}"/>, over a stubbed table client:
/// the capability probe, all-or-nothing batches (planned before anything is sent), the conditional delete
/// of a type with large values, the store's info, the wildcard ETag rule (break B5), the clock option and
/// a partition delete that loses a race for one of its rows. The same capabilities run against Azurite
/// through the conformance kit (<c>AzuriteConformanceTests</c>).
/// </summary>
public class AzureCapabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RequestFailedException NotFound(string code = "ResourceNotFound") => new(404, "not found", code, null);

    // ── the probe ─────────────────────────────────────────────────────────

    [Fact]
    public void TheTypedStore_ExposesEveryCapability_ThroughTheProbe()
    {
        using var h = new StorageHarness<TestEntity>();
        IStorage<TestEntity> store = h.Store;

        Assert.True(store.TryGetCapability<TestEntity, IConditionalDelete<TestEntity>>(out var delete));
        Assert.Same(h.Store, delete);
        Assert.True(store.TryGetCapability<TestEntity, IAtomicBatch<TestEntity>>(out var batch));
        Assert.Same(h.Store, batch);
        Assert.True(store.TryGetCapability<TestEntity, IStorageInfo>(out var info));
        Assert.Same(h.Store, info);
        Assert.True(store.TryGetCapability<TestEntity, IOrphanBlobSweeper>(out var sweeper));
        Assert.Same(h.Store, sweeper);
        Assert.True(store.TryGetCapability<TestEntity, IRawTable>(out var raw));
        Assert.Equal("TestEntity", raw.TableName);
        Assert.True(store.TryGetCapability<TestEntity, IRawTable>(out var rawAgain));
        Assert.Same(raw, rawAgain);
        Assert.True(store.TryGetCapability<TestEntity, ITableStore>(out var tables));
        Assert.IsType<AzureTableStore>(tables);
        // Not a side interface of a typed store.
        Assert.False(store.TryGetCapability<TestEntity, IAtomicInsert<TestMessage>>(out _));
        Assert.False(store.TryGetCapability<TestEntity, IDisposable>(out _));
    }

    [Fact]
    public void ThePolymorphicStore_ExposesAtomicInsert_Info_AndTheRawTable()
    {
        using var h = new PolymorphicHarness<TestMessage>();
        IPolymorphicStorage<TestMessage> store = h.Store;

        Assert.True(store.TryGetCapability<TestMessage, IAtomicInsert<TestMessage>>(out var insert));
        Assert.Same(h.Store, insert);
        Assert.True(store.TryGetCapability<TestMessage, IStorageInfo>(out _));
        Assert.True(store.TryGetCapability<TestMessage, IRawTable>(out var raw));
        Assert.Equal("TestTable", raw.TableName);
        Assert.True(store.TryGetCapability<TestMessage, ITableStore>(out _));
        Assert.False(store.TryGetCapability<TestMessage, IAtomicBatch<TestEntity>>(out _));
    }

    [Fact]
    public void ADecorator_HandsOutTheAzureStoresCapabilities()
    {
        using var h = new StorageHarness<TestEntity>();
        IStorage<TestEntity> decorated = new Passthrough(h.Store);

        Assert.True(decorated.TryGetCapability<TestEntity, IConditionalDelete<TestEntity>>(out var delete));
        Assert.Same(h.Store, delete);
        Assert.Equal("AzureTables", decorated.GetInfo().ProviderName);
    }

    private sealed class Passthrough(IStorage<TestEntity> inner) : DelegatingStorage<TestEntity>(inner);

    // ── storage info ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://acct.table.core.windows.net/", Durability.Replicated)]
    [InlineData("https://acct-secondary.table.core.windows.net/", Durability.Replicated)]
    [InlineData("https://ACCT.TABLE.CORE.WINDOWS.NET/", Durability.Replicated)]
    [InlineData("https://acct.table.core.chinacloudapi.cn/", Durability.Replicated)]
    [InlineData("https://acct.table.core.usgovcloudapi.net/", Durability.Replicated)]
    [InlineData("https://acct.z13.table.storage.azure.net/", Durability.Replicated)]
    // Everything else is not known: the emulator, however it is addressed, a custom domain, Cosmos DB,
    // and a host that only looks like a storage host.
    [InlineData("http://127.0.0.1:10002/devstoreaccount1", Durability.Unknown)]
    [InlineData("https://tables.example.com/", Durability.Unknown)]
    [InlineData("https://acct.table.cosmos.azure.com/", Durability.Unknown)]
    [InlineData("https://acct.table.core.windows.net.example.com/", Durability.Unknown)]
    [InlineData("https://table.core.windows.net/", Durability.Unknown)]
    public void Durability_IsReplicated_OnlyOnAnAzureStorageTableHost(string endpoint, Durability expected)
    {
        var service = new TableServiceClient(new Uri(endpoint), new TableSharedKeyCredential("acct", Convert.ToBase64String(new byte[32])));
        var typed = new TableStorage<TestEntity>(service, new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<TestEntity>>.Instance);
        var polymorphic = new PolymorphicTableStorage<TestMessage>(service, "Events", NullLogger<PolymorphicTableStorage<TestMessage>>.Instance);

        Assert.Equal(expected, typed.GetInfo().Durability);
        Assert.Equal(expected, polymorphic.GetInfo().Durability);
    }

    [Theory]
    [InlineData("acct.table.core.windows.net", true)]
    [InlineData("acct.table.core.cloudapi.de", true)]
    // The emulator behind a container's host name, with an account name of the host's choosing.
    [InlineData("azurite", false)]
    [InlineData("localhost", false)]
    [InlineData("acct.blob.core.windows.net", false)]
    [InlineData(".table.core.windows.net", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAHostWithAnAccountBeforeAStorageTableSuffix_IsAnAzureStorageTableHost(string? host, bool expected) =>
        Assert.Equal(expected, AzureStorageInfo.IsAzureStorageTableHost(host));

    [Fact]
    public void Info_StatesTheTable_TheLimitsTheLibraryPlansBy_AndTheCache()
    {
        var service = new TableServiceClient("UseDevelopmentStorage=true");
        var options = new UnifiedTableStorageOptions { TableNameResolver = _ => "Renamed" }.CacheFor<SimpleEntity>(CachePolicy.Disabled);
        var cached = new TableStorage<TestEntity>(service, new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<TestEntity>>.Instance, null, options);
        var uncached = new TableStorage<SimpleEntity>(service, new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<SimpleEntity>>.Instance, null, options);
        var polymorphic = new PolymorphicTableStorage<TestMessage>(service, "Events", NullLogger<PolymorphicTableStorage<TestMessage>>.Instance);

        var info = cached.GetInfo();

        Assert.Equal(new StorageInfo
        {
            ProviderName = "AzureTables",
            TableName = "Renamed",
            Durability = Durability.Unknown,
            MaxBatchRows = 100,
            MaxBatchBytes = 3_000_000,
            MaxCellBytes = 65_536,
            MaxRowBytes = 1_000_000,
            ReadCacheMayServeStaleRows = true,
            SinglePartitionTransactions = true,
        }, info);
        Assert.Same(info, cached.GetInfo());
        Assert.Equal(info, ((IStorage<TestEntity>)cached).GetInfo());
        Assert.False(uncached.GetInfo().ReadCacheMayServeStaleRows);
        // The polymorphic store has no read cache.
        Assert.Equal(info with { TableName = "Events", ReadCacheMayServeStaleRows = false }, polymorphic.GetInfo());
    }

    [Fact]
    public void ADeclaredDurability_Overrides_WhatTheHostSays()
    {
        var emulator = new TableServiceClient("UseDevelopmentStorage=true");
        var azure = new TableServiceClient(new Uri("https://acct.table.core.windows.net/"), new TableSharedKeyCredential("acct", Convert.ToBase64String(new byte[32])));

        Assert.Equal(Durability.OsBuffered, new TableStorage<TestEntity>(emulator, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TableStorage<TestEntity>>.Instance, null, new UnifiedTableStorageOptions { DeclaredDurability = Durability.OsBuffered }).GetInfo().Durability);
        Assert.Equal(Durability.Unknown, new PolymorphicTableStorage<TestMessage>(azure, "Events",
            NullLogger<PolymorphicTableStorage<TestMessage>>.Instance, new UnifiedTableStorageOptions { DeclaredDurability = Durability.Unknown }).GetInfo().Durability);
    }

    [Fact]
    public void Info_OfAStoreOverAClientThatReportsNoEndpoint_StatesNoDurability()
    {
        using var h = new StorageHarness<TestEntity>();

        Assert.Equal(Durability.Unknown, h.Store.GetInfo().Durability);
        Assert.Equal("TestEntity", h.Store.GetInfo().TableName);
    }

    // ── all-or-nothing batches ────────────────────────────────────────────

    private static List<TestEntity> Items(int count, string partition = "p") =>
        Enumerable.Range(0, count).Select(i => new TestEntity { Id = $"{partition}|r{i:D3}", Name = "n" + i, Value = i }).ToList();

    private static List<IReadOnlyList<TableTransactionAction>> RecordTransactions(TableClient table)
    {
        var sent = new List<IReadOnlyList<TableTransactionAction>>();
        var response = Substitute.For<Response<IReadOnlyList<Response>>>();
        table.SubmitTransactionAsync(Arg.Do<IEnumerable<TableTransactionAction>>(a => sent.Add([.. a])), Arg.Any<CancellationToken>()).Returns(response);
        return sent;
    }

    [Fact]
    public async Task AnAtomicBatch_IsOneTransaction_OfUpToAHundredRows()
    {
        using var h = new StorageHarness<TestEntity>();
        var sent = RecordTransactions(h.Table);
        var created = Items(100);
        created[0].ETag = "W/\"stale\"";

        await h.Store.CreateAtomicAsync(created, Ct);
        await h.Store.UpsertAtomicAsync(Items(3), Ct);
        await h.Store.CreateAtomicAsync([], Ct);
        await h.Store.UpsertAtomicAsync([], Ct);

        Assert.Equal(2, sent.Count);
        Assert.Equal(100, sent[0].Count);
        Assert.All(sent[0], a => Assert.Equal(TableTransactionActionType.Add, a.ActionType));
        Assert.All(sent[1], a => Assert.Equal(TableTransactionActionType.UpsertReplace, a.ActionType));
        // As after any batch: the row versions are unknown, so no entity keeps one.
        Assert.All(created, e => Assert.Null(e.ETag));
        Assert.All(created, e => Assert.Equal(created[0].CreatedAt, e.UpdatedAt));
    }

    public static TheoryData<string> BatchesThatCannotBeOneTransaction => ["two partitions", "101 rows", "a row twice", "a row twice by another spelling", "too many bytes"];

    [Theory]
    [MemberData(nameof(BatchesThatCannotBeOneTransaction))]
    public async Task ABatchThatCannotBeOneTransaction_IsRefused_BeforeAnythingIsSent_AndLeavesTheEntities(string why)
    {
        using var h = new StorageHarness<TestEntity>();
        var sent = RecordTransactions(h.Table);
        var batch = Items(2);
        switch (why)
        {
            case "two partitions": batch.Add(new TestEntity { Id = "q|r000" }); break;
            case "101 rows": batch = Items(101); break;
            case "a row twice": batch.Add(new TestEntity { Id = "p|r000", Name = "again" }); break;
            case "a row twice by another spelling": batch.Add(new TestEntity { Id = " P|R000 " }); break;
            default:
                batch = Items(60);
                foreach (var entity in batch)
                    entity.Name = new string('x', 30_000); // 60,000 bytes a row as UTF-16: 3.6 MB in all
                break;
        }
        var stamps = batch.Select(e => (e.Id, e.CreatedAt, e.UpdatedAt, e.ETag)).ToList();

        var create = await Assert.ThrowsAsync<BatchNotAtomicException>(() => h.Store.CreateAtomicAsync(batch, Ct));
        await Assert.ThrowsAsync<BatchNotAtomicException>(() => ((IStorage<TestEntity>)h.Store).UpsertAtomicAsync(batch, Ct));

        Assert.Empty(sent);
        Assert.Contains("Nothing was written", create.Message, StringComparison.Ordinal);
        Assert.Equal(stamps, batch.Select(e => (e.Id, e.CreatedAt, e.UpdatedAt, e.ETag)));
    }

    [Fact]
    public async Task AnAtomicCreate_ThatMeetsAStoredRow_ThrowsDuplicateKey_NamingTheRow()
    {
        using var h = new StorageHarness<TestEntity>();
        var failure = new TableTransactionFailedException(new RequestFailedException(409, "1:The specified entity already exists.", "EntityAlreadyExists", null));
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);

        var duplicate = await Assert.ThrowsAsync<DuplicateKeyException>(() => h.Store.CreateAtomicAsync(Items(3), Ct));

        Assert.Same(failure, duplicate.InnerException);
        Assert.Equal(failure.FailedTransactionActionIndex is int i ? $"p|r{i:D3}" : "p|?", duplicate.Id);
    }

    [Fact]
    public async Task AnAtomicBatch_BadArguments_AreArgumentErrors()
    {
        using var h = new StorageHarness<TestEntity>();

        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.CreateAtomicAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.CreateAtomicAsync([null!], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.UpsertAtomicAsync([new TestEntity { Id = " " }], Ct));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Store.CreateAtomicAsync(Items(1), cancelled.Token));
    }

    [Fact]
    public async Task AnAtomicBatch_OfLargeValues_CountsContinuationRows_AndIsNeverSplit()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var sent = RecordTransactions(h.Table);
        h.SetupQueryAll();
        // Each value needs a primary and continuation rows; two of them fit one transaction, forty do not.
        static SplitTextEntity Large(int i) => new() { Id = $"p|r{i:D2}", Body = new string((char)('a' + (i % 26)), 600_000) };

        await h.Store.CreateAtomicAsync([Large(0), Large(1)], Ct);

        var transaction = Assert.Single(sent);
        Assert.True(transaction.Count > 2, "the continuation rows are rows of the transaction");
        Assert.Equal(2, transaction.Count(a => !a.Entity.RowKey.Contains('~', StringComparison.Ordinal)));

        sent.Clear();
        var tooMany = Enumerable.Range(0, 40).Select(Large).ToList();
        var refused = await Assert.ThrowsAsync<BatchNotAtomicException>(() => h.Store.CreateAtomicAsync(tooMany, Ct));

        Assert.Empty(sent);
        Assert.Contains("transactions", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAtomicBatch_WhoseValueWouldGoToBlobStorage_IsRefused_BeforeAnyUpload()
    {
        var blobs = new RecordingBlobStore([]);
        using var h = new StorageHarness<SplitTextEntity>(options: new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() }, blobs: blobs);
        var sent = RecordTransactions(h.Table);
        h.SetupQueryAll();
        // Past what continuation rows hold: the value is offloaded to a blob, which no transaction covers.
        var offloaded = new SplitTextEntity { Id = "p|big", Body = string.Concat(Enumerable.Range(0, 400_000).Select(i => (char)('a' + (i * 7 % 26)) + i.ToString("x5"))) };

        var refused = await Assert.ThrowsAsync<BatchNotAtomicException>(
            () => h.Store.CreateAtomicAsync([new SplitTextEntity { Id = "p|small", Body = "x" }, offloaded], Ct));

        Assert.Contains("Blob storage", refused.Message, StringComparison.Ordinal);
        Assert.Empty(sent);
        Assert.Empty(blobs.Calls);
    }

    [Fact]
    public async Task APolymorphicAtomicInsert_IsOneTransaction_OrIsRefusedBeforeAnythingIsSent()
    {
        using var h = new PolymorphicHarness<TestMessage>();
        var sent = RecordTransactions(h.Table);
        static PolymorphicWrite<TestMessage> Write(string pk, string rk) => new(new TableKey(pk, rk), new TestCommand { Id = rk, Operation = "go" });

        await h.Store.InsertAtomicAsync([Write("p", "a"), Write("p", "b")], Ct);
        await h.Store.InsertAtomicAsync([], Ct);
        Assert.Equal(2, Assert.Single(sent).Count);

        sent.Clear();
        await Assert.ThrowsAsync<BatchNotAtomicException>(() => h.Store.InsertAtomicAsync([Write("p", "a"), Write("q", "a")], Ct));
        await Assert.ThrowsAsync<BatchNotAtomicException>(() => h.Store.InsertAtomicAsync([Write("p", "a"), Write("p", "b"), Write("p", "a")], Ct));
        await Assert.ThrowsAsync<BatchNotAtomicException>(
            () => ((IPolymorphicStorage<TestMessage>)h.Store).InsertAtomicAsync(Enumerable.Range(0, 101).Select(i => Write("p", "r" + i)).ToList(), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Store.InsertAtomicAsync(null!, Ct));
        Assert.Empty(sent);

        var failure = new TableTransactionFailedException(new RequestFailedException(409, "1:The specified entity already exists.", "EntityAlreadyExists", null));
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        var duplicate = await Assert.ThrowsAsync<DuplicateKeyException>(() => h.Store.InsertAtomicAsync([Write("p", "a"), Write("p", "b")], Ct));
        Assert.Same(failure, duplicate.InnerException);
    }

    // ── conditional delete of a type with large values (the read-then-delete path) ──

    private static TableEntity Stored(string rk, string etag, int parts = 0)
    {
        var row = new TableEntity("p", rk) { ETag = new ETag(etag) };
        if (parts > 0)
            row["_SplitParts"] = parts;
        return row;
    }

    private static TableEntity Part(string rk, int index)
    {
        var row = new TableEntity("p", $"{rk}~{index:D4}") { ETag = new ETag("W/\"part\"") };
        row["_SplitOf"] = rk;
        return row;
    }

    [Fact]
    public async Task ALargeConditionalDelete_OfAnEntityThatIsNotThere_IsNotFound_AndSendsNoDelete()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet(null);
        h.SetupQueryAll();

        Assert.Equal(DeleteStatus.NotFound, await h.Store.DeleteIfExistsAsync("p|r", Ct));
        Assert.Equal(DeleteStatus.NotFound, await h.Store.DeleteIfMatchAsync("p|r", "W/\"1\"", Ct));

        await h.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
    }

    [Fact]
    public async Task ALargeDeleteIfMatch_OnAnotherVersion_IsAConflict_FoundBeforeAnythingIsSent()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet(Stored("r", "W/\"2\""));

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => h.Store.DeleteIfMatchAsync("p|r", "W/\"1\"", Ct));

        Assert.Null(conflict.InnerException);
        Assert.False(conflict.MayBeOwnWrite);
        await h.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ALargeDeleteIfMatch_DeletesOnTheCallersVersion_AndDoesNotLoopOnA412()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet(Stored("r", "W/\"1\""));
        h.SetupDelete();

        Assert.Equal(DeleteStatus.Deleted, await h.Store.DeleteIfMatchAsync("p|r", "W/\"1\"", Ct));
        await h.Table.Received(1).DeleteEntityAsync("p", "r", new ETag("W/\"1\""), Arg.Any<CancellationToken>());

        // The row changed between this call's read and its delete: the service's 412 is the conflict.
        h.Table.ClearReceivedCalls();
        var changed = new RequestFailedException(412, "changed", "UpdateConditionNotSatisfied", null);
        h.Table.DeleteEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>()).ThrowsAsync(changed);
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => h.Store.DeleteIfMatchAsync("p|r", "W/\"1\"", Ct));

        Assert.Same(changed, conflict.InnerException);
        await h.Table.Received(1).DeleteEntityAsync("p", "r", new ETag("W/\"1\""), Arg.Any<CancellationToken>());
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ALargeConditionalDelete_WhoseTransactionFindsTheRowGone_IsIndeterminate_WhenItsAttemptsWereNotObserved()
    {
        // The entity has a continuation row, so it is deleted in a transaction, which throws its 404.
        // A stubbed client has no retry observation: an earlier attempt of the transaction may have been
        // the one that removed the entity, so the call cannot say "not found".
        using var h = new StorageHarness<SplitTextEntity>();
        var found = Mocks.Found(Stored("r", "W/\"1\"", parts: 1));
        var gone = Mocks.NotFound();
        h.Table.GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()).Returns(found, gone);
        h.Table.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable(Part("r", 1)), _ => Mocks.Pageable());
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TableTransactionFailedException(NotFound()));
        h.Store.LargeValueRetryDelay = (_, _) => Task.CompletedTask;

        Assert.Equal(DeleteStatus.Indeterminate, await h.Store.DeleteIfExistsAsync("p|r", Ct));

        await h.Table.Received(1).SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ALargeConditionalDelete_RemovesTheContinuationRows_InOneTransaction_OnTheCallersVersion()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var sent = RecordTransactions(h.Table);
        h.SetupGet(Stored("r", "W/\"1\"", parts: 2));
        h.SetupQueryAll(Part("r", 1), Part("r", 2));

        Assert.Equal(DeleteStatus.Deleted, await h.Store.DeleteIfMatchAsync("p|r", "W/\"1\"", Ct));

        var transaction = Assert.Single(sent);
        Assert.Equal(["r", "r~0001", "r~0002"], transaction.Select(a => a.Entity.RowKey));
        Assert.All(transaction, a => Assert.Equal(TableTransactionActionType.Delete, a.ActionType));
        Assert.Equal(new ETag("W/\"1\""), transaction[0].ETag);
    }

    // ── break B5: the wildcard ETag is not a version ──────────────────────

    [Theory]
    [InlineData(ConcurrencyMode.Auto)]
    [InlineData(ConcurrencyMode.Strict)]
    public async Task AWholeEntityUpdate_WithTheWildcardETag_IsRefused_OnBothProviders_BeforeAnythingIsSent(ConcurrencyMode mode)
    {
        using var plain = new StorageHarness<TestEntity>();
        using var large = new StorageHarness<SplitTextEntity>();
        using var guarded = new StorageHarness<ProtectedEntity>();
        var memory = new InMemoryStorage<TestEntity>();
        var memoryLarge = new InMemoryStorage<SplitTextEntity>();
        await memory.CreateAsync(new TestEntity { Id = "p|r", Name = "first" }, Ct);
        await memoryLarge.CreateAsync(new SplitTextEntity { Id = "p|r", Title = "first" }, Ct);

        var azure = await Assert.ThrowsAsync<ArgumentException>(() => plain.Store.UpdateAsync(new TestEntity { Id = "p|r", ETag = "*" }, mode, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => large.Store.UpdateAsync(new SplitTextEntity { Id = "p|r", ETag = "*" }, mode, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => guarded.Store.UpdateAsync(new ProtectedEntity { Id = "p|r", ETag = "*" }, mode, Ct));
        var fake = await Assert.ThrowsAsync<ArgumentException>(() => memory.UpdateAsync(new TestEntity { Id = "p|r", Name = "second", ETag = "*" }, mode, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => memoryLarge.UpdateAsync(new SplitTextEntity { Id = "p|r", Title = "second", ETag = "*" }, mode, Ct));

        Assert.Equal("entity", azure.ParamName);
        Assert.Contains("ConcurrencyMode.LastWriterWins", azure.Message, StringComparison.Ordinal);
        Assert.Equal(azure.Message, fake.Message);
        Assert.Empty(plain.Table.ReceivedCalls());
        Assert.Empty(large.Table.ReceivedCalls());
        // Refused before the read that guards a protected property.
        Assert.Empty(guarded.Table.ReceivedCalls());
        Assert.Equal("first", (await memory.OneAsync("p|r", Ct))!.Name);
        Assert.Equal("first", (await memoryLarge.OneAsync("p|r", Ct))!.Title);
    }

    [Fact]
    public async Task LastWriterWins_NeverLooksAtTheETag_SoTheWildcardIsHarmlessThere()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate();
        var memory = new InMemoryStorage<TestEntity>();
        await memory.CreateAsync(new TestEntity { Id = "p|r", Name = "first" }, Ct);

        await h.Store.UpdateAsync(new TestEntity { Id = "p|r", ETag = "*" }, ConcurrencyMode.LastWriterWins, Ct);
        await memory.UpdateAsync(new TestEntity { Id = "p|r", Name = "second", ETag = "*" }, ConcurrencyMode.LastWriterWins, Ct);

        await h.Table.Received(1).UpdateEntityAsync(Arg.Any<TableEntity>(), ETag.All, TableUpdateMode.Replace, Arg.Any<CancellationToken>());
        Assert.Equal("second", (await memory.OneAsync("p|r", Ct))!.Name);
    }

    // ── the clock ─────────────────────────────────────────────────────────

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task EveryWrite_StampsCreatedAtAndUpdatedAt_FromTheOptionsClock()
    {
        var clock = new FixedClock(new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero));
        using var h = new StorageHarness<TestEntity>(options: new UnifiedTableStorageOptions { TimeProvider = clock });
        h.SetupAdd();
        h.SetupUpsert();
        h.SetupUpdate();
        h.SetupTransaction();
        TableEntity? merged = null;
        h.Table.UpdateEntityAsync(Arg.Do<TableEntity>(e => merged = e), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>())
            .Returns(Mocks.EtagResponse());

        var created = await h.Store.CreateAsync(new TestEntity { Id = "p|a" }, Ct);
        Assert.Equal(clock.Now, created.CreatedAt);
        Assert.Equal(clock.Now, created.UpdatedAt);

        clock.Now = clock.Now.AddDays(1);
        var updated = await h.Store.UpdateAsync(created, Ct);
        Assert.Equal(clock.Now.AddDays(-1), updated.CreatedAt);
        Assert.Equal(clock.Now, updated.UpdatedAt);

        var upserted = await h.Store.UpsertAsync(new TestEntity { Id = "p|b", CreatedAt = default }, Ct);
        Assert.Equal(clock.Now, upserted.CreatedAt);
        Assert.Equal(clock.Now, upserted.UpdatedAt);

        await h.Store.UpdateAsync("p|a", b => b.SetProperty(e => e.Name, "x"), Ct);
        Assert.Equal(clock.Now, merged!["UpdatedAt"]);

        var batch = new[] { new TestEntity { Id = "p|c" }, new TestEntity { Id = "p|d" } };
        await h.Store.CreateBatchAsync(batch, Ct);
        await h.Store.UpsertAtomicAsync(batch, Ct);
        Assert.All(batch, e => Assert.Equal((clock.Now, clock.Now), (e.CreatedAt, e.UpdatedAt)));
    }

    [Fact]
    public void TheClock_DefaultsToTheSystemClock_AndCannotBeNull()
    {
        var options = new StorageOptions();

        Assert.Same(TimeProvider.System, options.TimeProvider);
        Assert.Throws<ArgumentNullException>(() => options.TimeProvider = null!);
        Assert.Same(TimeProvider.System, new UnifiedTableStorageOptions().TimeProvider);
    }

    // ── a partition delete that loses a race for one of its rows ──────────

    [Fact]
    public async Task APartitionDelete_WhoseTransactionMeetsARowAlreadyDeleted_DeletesTheRestOneByOne()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByPartition(Mocks.Row("p", "a"), Mocks.Row("p", "b"), Mocks.Row("p", "c"));
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TableTransactionFailedException(NotFound()));
        // The SDK returns the 404 of a row that is gone instead of throwing it.
        var goneResponse = Substitute.For<Response>();
        goneResponse.Status.Returns(404);
        h.Table.DeleteEntityAsync("p", Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>()).Returns(new FakeResponse());
        h.Table.DeleteEntityAsync("p", "b", Arg.Any<ETag>(), Arg.Any<CancellationToken>()).Returns(goneResponse);

        var deleted = await h.Store.DeletePartitionAsync("p", Ct);

        // The row another writer removed is not this call's delete.
        Assert.Equal(2, deleted);
        foreach (var rowKey in new[] { "a", "b", "c" })
            await h.Table.Received(1).DeleteEntityAsync("p", rowKey, ETag.All, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APartitionDelete_OfATableThatIsGone_StillFails_AsTableNotFound()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByPartition(Mocks.Row("p", "a"));
        var failure = new TableTransactionFailedException(NotFound("TableNotFound"));
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);

        var thrown = await Assert.ThrowsAsync<TableNotFoundException>(() => h.Store.DeletePartitionAsync("p", Ct));

        Assert.Same(failure, thrown.InnerException);
        await h.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task APolymorphicPartitionDelete_WhoseTransactionMeetsARowAlreadyDeleted_DeletesTheRestOneByOne()
    {
        using var h = new PolymorphicHarness<TestMessage>();
        h.Table.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Mocks.Pageable(new TableEntity("p", "a"), new TableEntity("p", "b")));
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TableTransactionFailedException(NotFound()));
        var goneResponse = Substitute.For<Response>();
        goneResponse.Status.Returns(404);
        h.Table.DeleteEntityAsync("p", "a", Arg.Any<ETag>(), Arg.Any<CancellationToken>()).Returns(goneResponse);
        h.Table.DeleteEntityAsync("p", "b", Arg.Any<ETag>(), Arg.Any<CancellationToken>()).Returns(new FakeResponse());

        var deleted = await h.Store.DeletePartitionAsync("p", Ct);

        Assert.Equal(1, deleted);
        await h.Table.Received(1).DeleteEntityAsync("p", "a", ETag.All, Arg.Any<CancellationToken>());
        await h.Table.Received(1).DeleteEntityAsync("p", "b", ETag.All, Arg.Any<CancellationToken>());
    }

    // ── the raw table plans before it sends ───────────────────────────────

    [Fact]
    public async Task ARawTransaction_ThatCannotBeOne_IsRefused_BeforeAnyRequest()
    {
        var transport = new ScriptedTableTransport(_ => throw new InvalidOperationException("No request is expected."));
        var table = new AzureTableStore(transport.Service()).GetTable("Raw");
        static RawRow Row(string pk, string rk, int bytes = 0) =>
            new(pk, rk, bytes == 0 ? null : new Dictionary<string, RawCell> { ["B"] = RawCell.FromBinary(new byte[bytes]) });

        Assert.Empty(await table.SubmitAsync([], Ct));
        await Assert.ThrowsAsync<BatchNotAtomicException>(() => table.SubmitAsync([RawOperation.Insert(Row("p", "a")), RawOperation.Insert(Row("q", "a"))], Ct));
        await Assert.ThrowsAsync<BatchNotAtomicException>(() => table.SubmitAsync([RawOperation.Insert(Row("p", "a")), RawOperation.Delete("p", "a")], Ct));
        await Assert.ThrowsAsync<BatchNotAtomicException>(
            () => table.SubmitAsync(Enumerable.Range(0, 101).Select(i => RawOperation.Insert(Row("p", "r" + i))).ToList(), Ct));
        // 60 rows of 60,000 bytes: 3.6 MB, above the 3,000,000 bytes one transaction is planned for.
        var heavy = await Assert.ThrowsAsync<BatchNotAtomicException>(
            () => table.SubmitAsync(Enumerable.Range(0, 60).Select(i => RawOperation.Insert(Row("p", "r" + i, 60_000))).ToList(), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => table.SubmitAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => table.SubmitAsync([null!], Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.QueryAsync(new RawQuery("p") { Take = 0 }, Ct));
        Assert.Throws<ArgumentNullException>(() => table.QueryAsync(null!, Ct));

        Assert.Contains("bytes", heavy.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ARawQuery_SendsTheKeysAsWritten_AndARawRead_TellsAMissingTableFromAMissingRow()
    {
        var transport = new ScriptedTableTransport(sent => sent.IsEntity
            ? sent.Path.Contains("RowKey='gone'", StringComparison.Ordinal)
                ? new ScriptedTableTransport.Answer(404, "{\"odata.error\":{\"code\":\"ResourceNotFound\"}}", ErrorCode: "ResourceNotFound")
                : new ScriptedTableTransport.Answer(404, "{\"odata.error\":{\"code\":\"TableNotFound\"}}", ErrorCode: "TableNotFound")
            : new ScriptedTableTransport.Answer(200,
                "{\"value\":[{\"odata.etag\":\"W/\\\"7\\\"\",\"PartitionKey\":\"My P\",\"RowKey\":\"K1\",\"Timestamp\":\"2024-01-01T00:00:00Z\",\"N\":5,\"S\":\"text\"}]}"));
        var table = new AzureTableStore(transport.Service()).GetTable("Raw");

        var rows = new List<RawRow>();
        await foreach (var row in table.QueryAsync(new RawQuery("My P") { FromRowKey = "K1", ToRowKey = "K9", ToInclusive = true, Take = 5 }, Ct))
            rows.Add(row);

        var read = Assert.Single(rows);
        Assert.Equal(("My P", "K1", "W/\"7\""), (read.PartitionKey, read.RowKey, read.ETag));
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), read.Timestamp);
        Assert.Equal(["N", "S"], read.Cells.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(RawCell.FromInt32(5), read.Cells["N"]);
        Assert.Contains("$filter=PartitionKey eq 'My P' and RowKey ge 'K1' and RowKey le 'K9'", transport.Requests.Single(r => r.Method == "GET").Query, StringComparison.Ordinal);
        // No table is created by a table opened by name.
        Assert.DoesNotContain(transport.Requests, r => r.Method == "POST");

        Assert.Null(await table.GetAsync("p", "gone", Ct));
        var missing = await Assert.ThrowsAsync<TableNotFoundException>(() => table.GetAsync("p", "r", Ct));
        Assert.Equal("Raw", missing.TableName);
        Assert.IsType<RequestFailedException>(missing.InnerException);
    }
}
