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

    /// <summary>
    /// Stores a plain row (no split cells, no <c>_SplitOf</c>) at <paramref name="rk"/> in this test's partition
    /// of <typeparamref name="T"/>'s table, as a type that never opted in, or an older version, would have written it.
    /// </summary>
    protected abstract Task SeedForeignRowAsync<T>(string rk, string title) where T : class, IEntity, new();

    /// <summary>The raw <c>Title</c> cell of the stored row at <paramref name="rk"/>; null when the row or the cell is absent.</summary>
    protected abstract Task<string?> PhysicalTitleAsync<T>(string rk) where T : class, IEntity, new();

    /// <summary>
    /// From now on, records after each committed large-value batch transaction how many blobs of this test's
    /// partition exist (<see cref="BlobsAtCommits"/>).
    /// </summary>
    protected abstract void RecordBlobsAtEachCommit<T>() where T : class, IEntity, new();

    /// <summary>The blob counts <see cref="RecordBlobsAtEachCommit{T}"/> recorded, one per committed transaction.</summary>
    protected abstract IReadOnlyList<int> BlobsAtCommits { get; }

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
        // Each 600,000-char value is two rows of 1.2 MB of payload: two fit one transaction's 3,000,000-byte budget, three do not.
        var batch = Enumerable.Range(0, 6).Select(i => Text($"r{i}", new string((char)('a' + i), 600_000))).ToList();

        await store.CreateBatchAsync(batch, Ct);

        // Packed by unit: r0 and r1 with their parts, r2 and r3, r4 and r5 (one transaction per entity would be 6).
        Assert.Equal(3, Transactions);
        for (var i = 0; i < 6; i++)
            Assert.Equal(batch[i].Body, (await store.OneAsync(Id($"r{i}"), Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal(12, (await PhysicalRowKeysAsync<SplitTextEntity>()).Count);
    }

    [Fact]
    public async Task CreateBatch_SecondTransactionFails_TheFirstKeepsWholeUnits_AndTheSecondLeavesNoRow()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await store.CreateAsync(Text("r3", "original"), Ct);
        ResetTransactions();
        var batch = Enumerable.Range(0, 6).Select(i => Text($"r{i}", new string((char)('a' + i), 600_000))).ToList();

        var ex = await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateBatchAsync(batch, Ct));

        Assert.Equal(Id("r3"), ex.Id);
        Assert.Equal(2, Transactions);
        // The first transaction committed r0 and r1, each with its continuation row; the second (r2, r3)
        // failed as a whole: no primary and no orphan continuation row of r2, r3 untouched, and the third
        // transaction (r4, r5) was never sent.
        Assert.Equal(["r0", "r0~0001", "r1", "r1~0001", "r3"], await PhysicalRowKeysAsync<SplitTextEntity>());
        for (var i = 0; i < 2; i++)
            Assert.Equal(batch[i].Body, (await store.OneAsync(Id($"r{i}"), Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal("original", (await store.OneAsync(Id("r3"), Ct))!.Body);
    }

    [Fact]
    public async Task UpsertBatch_BlobsOfALaterTransaction_AreUploadedOnlyAfterTheEarlierOneCommits()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        // 100 one-action rows fill the first transaction; the offloaded row is the second one's.
        var batch = Enumerable.Range(0, 100).Select(i => Text($"r{i:D3}", "v" + i)).Append(Text("r100", OffloadText)).ToList();
        RecordBlobsAtEachCommit<SplitTextEntity>();

        await store.UpsertBatchAsync(batch, Ct);

        // A blob's age is measured from its upload: uploading it before the first commit would widen the
        // window in which an orphan sweep can delete it before the row that references it exists.
        Assert.Equal([0, 1], BlobsAtCommits);
        Assert.Equal(OffloadText, (await store.OneAsync(Id("r100"), Ct))!.Body, StringComparer.Ordinal);
    }

    // ── foreign rows at a continuation key ────────────────────────────────

    [Fact]
    public async Task Upsert_NewValueNeedsAContinuationRowWhereAForeignRowIs_Throws_AndKeepsThatRow()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await SeedForeignRowAsync<SplitTextEntity>("r1~0001", "foreign");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(Text("r1", SplitText), Ct));

        Assert.Contains("r1~0001", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["r1~0001"], await PhysicalRowKeysAsync<SplitTextEntity>());
        Assert.Equal("foreign", await PhysicalTitleAsync<SplitTextEntity>("r1~0001"));
    }

    [Fact]
    public async Task Upsert_GrowingAStoredRowOntoAForeignRow_Throws_AndKeepsBothRows()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await store.CreateAsync(Text("r1", "small"), Ct);
        await SeedForeignRowAsync<SplitTextEntity>("r1~0001", "foreign");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(Text("r1", SplitText), Ct));

        Assert.Equal(["r1", "r1~0001"], await PhysicalRowKeysAsync<SplitTextEntity>());
        Assert.Equal("small", (await store.OneAsync(Id("r1"), Ct))!.Body);
        Assert.Equal("foreign", await PhysicalTitleAsync<SplitTextEntity>("r1~0001"));
    }

    [Fact]
    public async Task UpsertBatch_ContinuationRowOnAForeignRow_FailsItsTransaction_AndKeepsThatRow()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await SeedForeignRowAsync<SplitTextEntity>("r2~0001", "foreign");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertBatchAsync([Text("r1", "a"), Text("r2", SplitText)], Ct));

        Assert.Contains("r2~0001", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["r2~0001"], await PhysicalRowKeysAsync<SplitTextEntity>());
        Assert.Equal("foreign", await PhysicalTitleAsync<SplitTextEntity>("r2~0001"));
    }

    [Fact]
    public async Task Create_ContinuationRowOnAForeignRow_Throws_AndKeepsThatRow()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await SeedForeignRowAsync<SplitTextEntity>("r1~0001", "foreign");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(Text("r1", SplitText), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateBatchAsync([Text("r1", SplitText)], Ct));

        Assert.Equal(["r1~0001"], await PhysicalRowKeysAsync<SplitTextEntity>());
        Assert.Equal("foreign", await PhysicalTitleAsync<SplitTextEntity>("r1~0001"));
    }

    [Fact]
    public async Task Upsert_RewritingItsOwnContinuationRows_StillReplacesThem()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await store.CreateAsync(Text("r1", SplitText), Ct);
        var other = new string('z', 600_000);

        await store.UpsertAsync(Text("r1", other), Ct);
        Assert.Equal(other, (await store.OneAsync(Id("r1"), Ct))!.Body, StringComparer.Ordinal);
        await store.UpsertBatchAsync([Text("r1", SplitText)], Ct);

        Assert.Equal(["r1", "r1~0001"], await PhysicalRowKeysAsync<SplitTextEntity>());
        Assert.Equal(SplitText, (await store.OneAsync(Id("r1"), Ct))!.Body, StringComparer.Ordinal);
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

        // The new value needs a continuation row: the write is a transaction, then the blob cleanup.
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

    [Fact]
    public async Task DeletePartition_TokenCancelledAfterTheCommit_StillCleansUp()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await store.CreateAsync(Text("r1", OffloadText), Ct);
        await store.CreateAsync(Text("r2", SplitText), Ct);
        Assert.Single(await BlobNamesAsync<SplitTextEntity>());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        CancelAfterNextCommit<SplitTextEntity>(cts);

        Assert.Equal(2, await store.DeletePartitionAsync(Pk, cts.Token));

        Assert.True(cts.IsCancellationRequested);
        Assert.Empty(await PhysicalRowKeysAsync<SplitTextEntity>());
        Assert.Empty(await BlobNamesAsync<SplitTextEntity>());
    }

    [Fact]
    public async Task CancelAfterNextCommit_IsNotTriggeredByAFailedTransaction()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>();
        await store.CreateAsync(Text("r2", "original"), Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        CancelAfterNextCommit<SplitTextEntity>(cts);

        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateBatchAsync([Text("r1", SplitText), Text("r2", "b")], cts.Token));

        // The failed transaction committed nothing, so the request after it is not cancelled.
        Assert.Equal(1, await store.CountAsync(Pk, cts.Token));
        Assert.False(cts.IsCancellationRequested);
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

    protected override Task SeedForeignRowAsync<T>(string rk, string title)
    {
        Store<T>();
        Typed<T>().SeedPhysicalRow(new TableEntity(Pk, rk) { ["Title"] = title });
        return Task.CompletedTask;
    }

    protected override Task<string?> PhysicalTitleAsync<T>(string rk) =>
        Task.FromResult(Typed<T>().PhysicalRow(Pk, rk) is { } row && row.TryGetValue("Title", out var title) ? title as string : null);

    private readonly List<int> blobsAtCommits = [];

    protected override IReadOnlyList<int> BlobsAtCommits => blobsAtCommits;

    protected override void RecordBlobsAtEachCommit<T>()
    {
        Store<T>();
        var store = Typed<T>();
        var prefix = $"{typeof(T).Name}/{Pk}/";
        store.AfterLargeCommit = () => blobsAtCommits.Add(store.OffloadedBlobNames().Count(n => n.StartsWith(prefix, StringComparison.Ordinal)));
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
        // A token a test armed and disposed must not be cancelled by the cleanup below.
        observer.CancelAfterNextCommit = null;
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

    protected override async Task SeedForeignRowAsync<T>(string rk, string title)
    {
        tables.Add(typeof(T).Name);
        var client = tableService.GetTableClient(typeof(T).Name);
        await client.CreateIfNotExistsAsync(Ct);
        await client.AddEntityAsync(new TableEntity(Pk, rk) { ["Title"] = title }, Ct);
    }

    protected override async Task<string?> PhysicalTitleAsync<T>(string rk)
    {
        var row = await tableService.GetTableClient(typeof(T).Name).GetEntityIfExistsAsync<TableEntity>(Pk, rk, cancellationToken: Ct);
        return row.HasValue && row.Value!.TryGetValue("Title", out var title) ? title as string : null;
    }

    protected override IReadOnlyList<int> BlobsAtCommits => observer.BlobsAtCommits;

    protected override void RecordBlobsAtEachCommit<T>() => observer.RecordBlobsUnder = "/" + typeof(T).Name + "/" + Pk + "/";

    /// <summary>
    /// Shared by the table and the blob pipelines: counts the entity-group transactions sent
    /// (<c>POST .../$batch</c>) and, once armed, waits for the first successful table write (a batch whose
    /// changeset succeeded, an insert, an update or a delete; not a table creation) and cancels the token
    /// when the NEXT request (table or blob) is sent, so the commit itself completes and only what follows
    /// it sees the cancelled token. A <c>$batch</c> answers 202 even when its changeset failed (the failure
    /// is an inner status of the multipart body), so its body decides whether it committed.
    /// </summary>
    private sealed class CommitObserver
    {
        private int batches;
        private bool committed;
        private readonly HashSet<string> uploaded = new(StringComparer.Ordinal);

        /// <summary>When set, the blob paths containing it that were written are counted at each committed transaction.</summary>
        public string? RecordBlobsUnder { get; set; }

        public List<int> BlobsAtCommits { get; } = [];

        // Only for a response the transport has buffered (every response a per-call policy sees).
        private static bool Committed(HttpMessage message)
        {
            var request = message.Request;
            if (message.Response.Status is < 200 or >= 300)
                return false;
            if (request.Method != RequestMethod.Post && request.Method != RequestMethod.Put && request.Method != RequestMethod.Delete)
                return false;
            var path = request.Uri.Path;
            if (request.Method == RequestMethod.Post && path.EndsWith("/Tables", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!path.EndsWith("$batch", StringComparison.Ordinal))
                return true;
            var statuses = System.Text.RegularExpressions.Regex.Matches(message.Response.Content.ToString(), @"HTTP/1\.1 (\d{3})");
            return statuses.Count > 0 && statuses.All(m => m.Groups[1].Value[0] == '2');
        }

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
                lock (owner)
                {
                    if (!table)
                    {
                        if (owner.RecordBlobsUnder is { } under
                            && message.Request.Method == RequestMethod.Put
                            && message.Response.Status is >= 200 and < 300
                            && message.Request.Uri.Path.Contains(under, StringComparison.Ordinal))
                            owner.uploaded.Add(message.Request.Uri.Path);
                        return;
                    }
                    if (!Committed(message))
                        return;
                    if (owner.CancelAfterNextCommit is not null)
                        owner.committed = true;
                    if (owner.RecordBlobsUnder is not null && message.Request.Uri.Path.EndsWith("$batch", StringComparison.Ordinal))
                        owner.BlobsAtCommits.Add(owner.uploaded.Count);
                }
            }
        }
    }
}
