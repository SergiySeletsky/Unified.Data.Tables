using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Batches of a split-aware type follow the plain batch contract: each entity's rows (primary,
/// continuation rows, stale-part deletes) are one indivisible unit, units are packed into entity-group
/// transactions per partition, and each transaction is atomic. The same tests run on
/// <see cref="InMemoryStorage{T}"/> and on <see cref="TableStorage{T}"/> against Azurite, so the two
/// providers behave identically. Also covers the post-commit robustness of single writes: a token
/// cancelled after the commit no longer reports the committed write as failed, nor skips blob cleanup.
/// </summary>
public abstract class LargeBatchContract : IAsyncLifetime
{
    protected readonly string Pk = "lb-" + Guid.NewGuid().ToString("N");

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string SplitText = new('s', 600_000);   // two rows, one transaction
    private static readonly string OffloadText = Ascii(3_200_000, seed: 3); // above the threshold: a blob

    public abstract ValueTask InitializeAsync();

    public abstract ValueTask DisposeAsync();

    /// <summary>Skips the test when the provider is not there (Azurite not running).</summary>
    protected abstract void SkipUnlessAvailable();

    /// <summary>A store of <typeparamref name="T"/> with blob offload and a zero cleanup grace.</summary>
    protected abstract IStorage<T> Store<T>() where T : class, IEntity, new();

    /// <summary>How many entity-group transactions the stores have submitted since <see cref="ResetTransactions"/>.</summary>
    protected abstract int Transactions { get; }

    protected abstract void ResetTransactions();

    /// <summary>The row keys stored in this test's partition of <typeparamref name="T"/>'s table, continuation rows included, in order.</summary>
    protected abstract Task<List<string>> PhysicalRowKeysAsync<T>() where T : class, IEntity, new();

    /// <summary>The offloaded blobs of this test's partition of <typeparamref name="T"/>'s table.</summary>
    protected abstract Task<List<string>> BlobNamesAsync<T>() where T : class, IEntity, new();

    /// <summary>Cancels <paramref name="cts"/> once the next large-value commit has succeeded, before anything that follows it.</summary>
    protected abstract void CancelAfterNextCommit<T>(CancellationTokenSource cts) where T : class, IEntity, new();

    /// <summary>The offload blob store's service time, asked with <paramref name="ct"/>.</summary>
    protected abstract Task<DateTimeOffset?> ServiceTimeAsync(CancellationToken ct);

    private string Id(string rk) => Pk + "|" + rk;

    private SplitTextEntity Text(string rk, string body) => new() { Id = Id(rk), Body = body, Title = "t-" + rk };

    // ── atomic transactions ───────────────────────────────────────────────

    [Fact]
    public async Task CreateBatch_TwoSmallRowsOnePartition_IsOneTransaction()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        ResetTransactions();

        var n = await store.CreateBatchAsync([Text("r1", "a"), Text("r2", "b")], Ct);

        Assert.Equal(2, n);
        Assert.Equal(1, Transactions);
        Assert.Equal("a", (await store.OneAsync(Id("r1"), Ct))!.Body);
        Assert.Equal("b", (await store.OneAsync(Id("r2"), Ct))!.Body);
    }

    [Fact]
    public async Task CreateBatch_RowsThatNeedSplitting_ShareOneTransactionWithTheirParts()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        ResetTransactions();

        await store.CreateBatchAsync([Text("r1", "small"), Text("r2", SplitText)], Ct);

        Assert.Equal(1, Transactions);
        Assert.Equal(["r1", "r2", "r2~0001"], await PhysicalRowKeysAsync<SplitTextEntity>());
        Assert.Equal(SplitText, (await store.OneAsync(Id("r2"), Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal(2, await store.CountAsync(Pk, Ct));
    }

    [Fact]
    public async Task CreateBatch_RowThatNeedsOffload_UploadsItsBlobAndSharesTheTransaction()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        ResetTransactions();

        await store.CreateBatchAsync([Text("r1", "small"), Text("r2", OffloadText)], Ct);

        Assert.Equal(1, Transactions);
        Assert.Single(await BlobNamesAsync<SplitTextEntity>());
        Assert.Equal(["r1", "r2"], await PhysicalRowKeysAsync<SplitTextEntity>());
        Assert.Equal(OffloadText, (await store.OneAsync(Id("r2"), Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task CreateBatch_RepeatedKey_ThrowsDuplicateKey_BeforeAnythingIsWritten()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        ResetTransactions();

        var ex = await Assert.ThrowsAsync<DuplicateKeyException>(
            () => store.CreateBatchAsync([Text("r1", "a"), Text("r2", "b"), Text("r1", "c")], Ct));

        Assert.Equal(Id("r1"), ex.Id);
        Assert.Equal(0, Transactions);
        Assert.Empty(await PhysicalRowKeysAsync<SplitTextEntity>());
    }

    [Fact]
    public async Task CreateBatch_ExistingKey_FailsItsTransactionAtomically()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await store.CreateAsync(Text("r2", "original"), Ct);

        var ex = await Assert.ThrowsAsync<DuplicateKeyException>(
            () => store.CreateBatchAsync([Text("r1", SplitText), Text("r2", "b"), Text("r3", "c")], Ct));

        Assert.Equal(Id("r2"), ex.Id);
        // r1 (and its continuation row) and r3 were in the same transaction: nothing of it committed.
        Assert.Equal(["r2"], await PhysicalRowKeysAsync<SplitTextEntity>());
        Assert.Equal("original", (await store.OneAsync(Id("r2"), Ct))!.Body);
    }

    [Fact]
    public async Task CreateBatch_MoreThan100Rows_SpansTransactions()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        ResetTransactions();

        var n = await store.CreateBatchAsync(Enumerable.Range(0, 150).Select(i => Text($"r{i:D3}", "v" + i)).ToList(), Ct);

        Assert.Equal(150, n);
        Assert.Equal(2, Transactions);
        Assert.Equal(150, await store.CountAsync(Pk, Ct));
    }

    [Fact]
    public async Task CreateBatch_ExistingKeyInSecondTransaction_KeepsTheFirstCommitted()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await store.CreateAsync(Text("r120", "original"), Ct);
        ResetTransactions();

        var ex = await Assert.ThrowsAsync<DuplicateKeyException>(
            () => store.CreateBatchAsync(Enumerable.Range(0, 150).Select(i => Text($"r{i:D3}", "v" + i)).ToList(), Ct));

        Assert.Equal(Id("r120"), ex.Id);
        Assert.Equal(2, Transactions);
        var keys = await PhysicalRowKeysAsync<SplitTextEntity>();
        // The first transaction (r000..r099) committed; the second (r100..r149) failed as a whole.
        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"r{i:D3}").Append("r120"), keys);
        Assert.Equal("original", (await store.OneAsync(Id("r120"), Ct))!.Body);
    }

    [Fact]
    public async Task CreateBatch_SplitRowsNeverStraddleTransactions()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        ResetTransactions();
        // Each 600,000-char value is two rows of ~1.2 MB on the wire: three fit one transaction's budget, six do not.
        var batch = Enumerable.Range(0, 6).Select(i => Text($"r{i}", new string((char)('a' + i), 600_000))).ToList();

        await store.CreateBatchAsync(batch, Ct);

        Assert.True(Transactions >= 2, $"expected the batch to span transactions, saw {Transactions}");
        for (var i = 0; i < 6; i++)
            Assert.Equal(batch[i].Body, (await store.OneAsync(Id($"r{i}"), Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal(12, (await PhysicalRowKeysAsync<SplitTextEntity>()).Count);
    }

    [Fact]
    public async Task UpsertBatch_ShrinksSplitAndOffloadedRows_InOneTransaction_AndReleasesBlobs()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await store.CreateAsync(Text("r1", OffloadText), Ct);
        await store.CreateAsync(Text("r2", SplitText), Ct);
        Assert.Single(await BlobNamesAsync<SplitTextEntity>());
        ResetTransactions();

        await store.UpsertBatchAsync([Text("r1", "now small"), Text("r2", "also small"), Text("r3", "new")], Ct);

        Assert.Equal(1, Transactions);
        Assert.Equal(["r1", "r2", "r3"], await PhysicalRowKeysAsync<SplitTextEntity>());
        Assert.Equal("now small", (await store.OneAsync(Id("r1"), Ct))!.Body);
        Assert.Equal("also small", (await store.OneAsync(Id("r2"), Ct))!.Body);
        Assert.Empty(await BlobNamesAsync<SplitTextEntity>());
    }

    [Fact]
    public async Task UpsertBatch_RepeatedKey_LastOccurrenceWins()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        ResetTransactions();

        var n = await store.UpsertBatchAsync([Text("r1", "first"), Text("r2", "x"), Text("r1", "last")], Ct);

        Assert.Equal(3, n);
        Assert.Equal(1, Transactions);
        Assert.Equal("last", (await store.OneAsync(Id("r1"), Ct))!.Body);
        Assert.Equal(2, await store.CountAsync(Pk, Ct));
    }

    [Fact]
    public async Task UpsertBatch_ResetsETags()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        var batch = new[] { Text("r1", "a"), Text("r2", SplitText) };

        await store.UpsertBatchAsync(batch, Ct);

        Assert.All(batch, e => Assert.Null(e.ETag));
    }

    [Fact]
    public async Task CreateBatch_TwoPartitions_AreTwoTransactions()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        ResetTransactions();
        var other = Pk + "x";

        try
        {
            await store.CreateBatchAsync([Text("r1", "a"), new SplitTextEntity { Id = other + "|r1", Body = "b" }], Ct);

            Assert.Equal(2, Transactions);
            Assert.Equal("b", (await store.OneAsync(other + "|r1", Ct))!.Body);
        }
        finally
        {
            await store.DeletePartitionAsync(other, CancellationToken.None);
        }
    }

    // ── post-commit robustness ────────────────────────────────────────────

    [Fact]
    public async Task Upsert_TokenCancelledAfterTheCommit_ReportsSuccess_AndStillCleansUp()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await store.CreateAsync(Text("r1", OffloadText), Ct);
        Assert.Single(await BlobNamesAsync<SplitTextEntity>());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        CancelAfterNextCommit<SplitTextEntity>(cts);

        // The new value needs a continuation row: the write is a transaction, then a version re-read.
        await store.UpsertAsync(Text("r1", SplitText), cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(SplitText, (await store.OneAsync(Id("r1"), Ct))!.Body, StringComparer.Ordinal);
        Assert.Empty(await BlobNamesAsync<SplitTextEntity>());
    }

    [Fact]
    public async Task UpsertBatch_TokenCancelledAfterTheCommit_ReportsSuccess_AndStillCleansUp()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await store.CreateAsync(Text("r1", OffloadText), Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        CancelAfterNextCommit<SplitTextEntity>(cts);

        var n = await store.UpsertBatchAsync([Text("r1", "small"), Text("r2", "b")], cts.Token);

        Assert.Equal(2, n);
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal("small", (await store.OneAsync(Id("r1"), Ct))!.Body);
        Assert.Empty(await BlobNamesAsync<SplitTextEntity>());
    }

    [Fact]
    public async Task BlobStore_ServiceTime_WithACancelledToken_ThrowsOperationCanceled()
    {
        SkipUnlessAvailable();
        Store<SplitTextEntity>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServiceTimeAsync(cts.Token));
        Assert.NotNull(await ServiceTimeAsync(Ct));
    }

    [Fact]
    public async Task Delete_TokenCancelledAfterTheCommit_StillCleansUp()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await store.CreateAsync(Text("r1", OffloadText), Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        CancelAfterNextCommit<SplitTextEntity>(cts);

        await store.DeleteAsync(Id("r1"), cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Empty(await PhysicalRowKeysAsync<SplitTextEntity>());
        Assert.Empty(await BlobNamesAsync<SplitTextEntity>());
    }
}

/// <summary>The batch contract on <see cref="InMemoryStorage{T}"/>.</summary>
public sealed class InMemoryLargeBatchContractTests : LargeBatchContract
{
    private readonly List<object> stores = [];

    public override ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected override void SkipUnlessAvailable()
    {
    }

    private InMemoryStorage<T> Typed<T>() where T : class, IEntity, new() =>
        stores.OfType<InMemoryStorage<T>>().Single();

    protected override IStorage<T> Store<T>()
    {
        if (stores.OfType<InMemoryStorage<T>>().SingleOrDefault() is { } existing)
            return existing;
        var store = new InMemoryStorage<T>(null, new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { CleanupGrace = TimeSpan.Zero },
        });
        stores.Add(store);
        return store;
    }

    protected override int Transactions => stores.OfType<InMemoryStorage<SplitTextEntity>>().Sum(s => s.BatchTransactions);

    protected override void ResetTransactions()
    {
        foreach (var store in stores.OfType<InMemoryStorage<SplitTextEntity>>())
            store.BatchTransactions = 0;
    }

    protected override Task<List<string>> PhysicalRowKeysAsync<T>() =>
        Task.FromResult(Typed<T>().PhysicalRowKeys(Pk).ToList());

    protected override Task<List<string>> BlobNamesAsync<T>() =>
        Task.FromResult(Typed<T>().OffloadedBlobNames().Where(n => n.StartsWith($"{typeof(T).Name}/{Pk}/", StringComparison.Ordinal)).ToList());

    protected override Task<DateTimeOffset?> ServiceTimeAsync(CancellationToken ct) =>
        Typed<SplitTextEntity>().Blobs.GetServiceTimeAsync(ct);

    protected override void CancelAfterNextCommit<T>(CancellationTokenSource cts)
    {
        var store = Typed<T>();
        store.AfterLargeCommit = () =>
        {
            store.AfterLargeCommit = null;
            cts.Cancel();
        };
    }
}

/// <summary>The batch contract on <see cref="TableStorage{T}"/> against a local Azurite (self-skips as the other Azurite suites do).</summary>
public sealed class AzuriteLargeBatchContractTests : LargeBatchContract
{
    private const string SkipReason = "Azurite (tables and blobs) is not reachable; set "
        + AzuriteLargeValueIntegrationTests.ConnectionStringVariable + " or start Azurite";

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable(AzuriteLargeValueIntegrationTests.ConnectionStringVariable) is { Length: > 0 } configured
            ? configured
            : "UseDevelopmentStorage=true";

    private static readonly Lazy<Task<bool>> Probe =
        new(() => TestSupport.AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, TestSupport.AzuriteProbe.Required));

    private readonly string containerName = "lb-" + Guid.NewGuid().ToString("N");
    private readonly CommitObserver observer = new();
    private readonly HashSet<string> tables = new(StringComparer.Ordinal);
    private readonly List<MemoryCache> caches = [];
    private readonly Dictionary<Type, object> stores = [];
    private TableServiceClient tableService = default!;
    private BlobContainerClient container = default!;
    private BlobServiceClient blobService = default!;
    private bool available;

    public override async ValueTask InitializeAsync()
    {
        available = await Probe.Value;
        if (!available) return;
        var tableOptions = new TableClientOptions();
        tableOptions.AddPolicy(observer.Table, HttpPipelinePosition.PerCall);
        var blobOptions = new BlobClientOptions();
        blobOptions.AddPolicy(observer.Blob, HttpPipelinePosition.PerCall);
        tableService = new TableServiceClient(ConnectionString, tableOptions);
        blobService = new BlobServiceClient(ConnectionString, blobOptions);
        container = blobService.GetBlobContainerClient(containerName);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var cache in caches) cache.Dispose();
        if (!available) return;
        foreach (var table in tables)
        {
            var client = tableService.GetTableClient(table);
            try
            {
                var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Pk}");
                var keys = new List<string>();
                await foreach (var row in client.QueryAsync<TableEntity>(filter, select: ["RowKey"], cancellationToken: CancellationToken.None))
                    keys.Add(row.RowKey);
                foreach (var rk in keys)
                    await client.DeleteEntityAsync(Pk, rk, ETag.All, CancellationToken.None);
            }
            catch (RequestFailedException)
            {
                // The table was never created, or a row is already gone.
            }
        }
        await container.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
    }

    protected override void SkipUnlessAvailable() => Assert.SkipUnless(available, SkipReason);

    protected override IStorage<T> Store<T>()
    {
        if (stores.TryGetValue(typeof(T), out var existing))
            return (IStorage<T>)existing;
        tables.Add(typeof(T).Name);
        var cache = new MemoryCache(new MemoryCacheOptions());
        caches.Add(cache);
        var store = new TableStorage<T>(tableService, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null,
            new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions { ContainerName = containerName, CleanupGrace = TimeSpan.Zero } },
            blobService);
        stores[typeof(T)] = store;
        return store;
    }

    protected override int Transactions => observer.Batches;

    protected override void ResetTransactions() => observer.Batches = 0;

    protected override async Task<List<string>> PhysicalRowKeysAsync<T>()
    {
        var keys = new List<string>();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Pk}");
        await foreach (var row in tableService.GetTableClient(typeof(T).Name).QueryAsync<TableEntity>(filter, select: ["RowKey"], cancellationToken: Ct))
            keys.Add(row.RowKey);
        return keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    protected override async Task<List<string>> BlobNamesAsync<T>()
    {
        var names = new List<string>();
        if (!(await container.ExistsAsync(Ct)).Value)
            return names;
        await foreach (var item in container.GetBlobsAsync(new GetBlobsOptions { Prefix = $"{typeof(T).Name}/{Pk}/" }, Ct))
            names.Add(item.Name);
        return names;
    }

    protected override void CancelAfterNextCommit<T>(CancellationTokenSource cts) => observer.CancelAfterNextCommit = cts;

    protected override Task<DateTimeOffset?> ServiceTimeAsync(CancellationToken ct) =>
        new AzureOffloadBlobStore(container).GetServiceTimeAsync(ct);

    /// <summary>
    /// Shared by the table and the blob pipelines: counts the entity-group transactions sent
    /// (<c>POST .../$batch</c>) and, once armed, waits for the first successful table write (a batch, an
    /// insert, an update or a delete) and cancels the token when the NEXT request (table or blob) is sent,
    /// so the commit itself completes and only what follows it sees the cancelled token.
    /// </summary>
    private sealed class CommitObserver
    {
        private int batches;
        private bool committed;

        public int Batches
        {
            get => Volatile.Read(ref batches);
            set => Volatile.Write(ref batches, value);
        }

        public CancellationTokenSource? CancelAfterNextCommit { get; set; }

        public HttpPipelinePolicy Table => new Policy(this, table: true);

        public HttpPipelinePolicy Blob => new Policy(this, table: false);

        private sealed class Policy(CommitObserver owner, bool table) : HttpPipelineSynchronousPolicy
        {
            public override void OnSendingRequest(HttpMessage message)
            {
                lock (owner)
                {
                    if (table && message.Request.Method == RequestMethod.Post && message.Request.Uri.Path.EndsWith("$batch", StringComparison.Ordinal))
                        owner.batches++;
                    if (owner.committed && owner.CancelAfterNextCommit is { } cts)
                    {
                        owner.CancelAfterNextCommit = null;
                        owner.committed = false;
                        cts.Cancel();
                    }
                }
            }

            public override void OnReceivedResponse(HttpMessage message)
            {
                var method = message.Request.Method;
                lock (owner)
                {
                    if (table && owner.CancelAfterNextCommit is not null
                        && message.Response.Status is >= 200 and < 300
                        && (method == RequestMethod.Post || method == RequestMethod.Put || method == RequestMethod.Delete))
                        owner.committed = true;
                }
            }
        }
    }
}
