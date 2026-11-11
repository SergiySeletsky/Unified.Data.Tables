using Azure;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A batch is written transaction by transaction, and a content read from one row may be written into
/// another row of the same batch (keep the previous file in a history entity, exchange two documents'
/// files, move a file). Within one batch call no blob is deleted while an entity of the batch that is not
/// written yet holds a content read from it: the blobs the committed transactions released are deleted
/// once, after the last transaction of the call committed (each still on condition of the grace counted
/// from its own commit); a batch that fails part way deletes none of them (the orphan sweep does), so the
/// entities it did not write keep a readable file and the same batch, sent again, completes
/// (COMPLETENESS-1). Both stores behave the same.
/// </summary>
public class BatchContentAcrossTransactionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BlobOffloadOptions Offload() => new() { ThresholdBytes = 1_000, CleanupGrace = TimeSpan.Zero };

    private static TableBlobContent Bytes(byte[] bytes) => TableBlobContent.FromBytes(bytes, "application/octet-stream");

    private static async Task<byte[]> BytesOfAsync(TableBlobContent content)
    {
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    private static string Outcome(Exception? failure) =>
        failure is null ? "succeeded" : "threw " + failure.GetType().Name + ": " + failure.Message;

    /// <summary>
    /// Two stores over the same rows and blobs: the one that wrote the document, and the one that reads it
    /// later and writes the batch (a TableStorage that wrote the file itself would answer the read from its
    /// cache, with the bytes still in memory; the scenario is a document read back from storage).
    /// </summary>
    private sealed class Stores : IDisposable
    {
        private readonly WriteBatchRig<FileEntity>? rig;

        public Stores(bool inMemory, BlobOffloadOptions offload)
        {
            if (inMemory)
            {
                var memory = new InMemoryStorage<FileEntity>(null, new UnifiedTableStorageOptions { BlobOffload = offload });
                Writer = memory;
                Later = memory;
                Blobs = memory.Blobs;
                return;
            }
            rig = new WriteBatchRig<FileEntity>(offload);
            Writer = rig.NewStore();
            Later = rig.NewStore();
            Blobs = rig.Blobs;
        }

        public IStorage<FileEntity> Writer { get; }

        public IStorage<FileEntity> Later { get; }

        public InMemoryOffloadBlobStore Blobs { get; }

        public void Dispose() => rig?.Dispose();
    }

    // Both entities in one partition, one transaction: every upload of a transaction precedes its commit.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneTransaction_ReplaceTheDocumentsFile_AndKeepThePreviousFileInAHistoryEntity(bool inMemory)
    {
        using var stores = new Stores(inMemory, Offload());
        var previous = RandomBytes(40_000, seed: 1);
        var replacement = RandomBytes(40_000, seed: 2);
        await stores.Writer.CreateAsync(new FileEntity { Id = "p|doc", Title = "v1", File = Bytes(previous) }, Ct);
        var read = (await stores.Later.OneAsync("p|doc", Ct))!;

        await stores.Later.UpsertBatchAsync(
        [
            new FileEntity { Id = "p|doc", Title = "v2", File = Bytes(replacement) },
            new FileEntity { Id = "p|history-1", Title = "v1", File = read.File },
        ], Ct);

        Assert.Equal(replacement, await BytesOfAsync((await stores.Later.OneAsync("p|doc", Ct))!.File!));
        Assert.Equal(previous, await BytesOfAsync((await stores.Later.OneAsync("p|history-1", Ct))!.File!));
    }

    // The history entity in another partition: two transactions, the document's first. The batch stores
    // both, and the blob the document released, which nothing reads any more, is deleted afterwards.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoTransactions_ReplaceTheDocumentsFile_AndKeepThePreviousFileInAHistoryEntity(bool inMemory)
    {
        using var stores = new Stores(inMemory, Offload());
        var previous = RandomBytes(40_000, seed: 1);
        var replacement = RandomBytes(40_000, seed: 2);
        await stores.Writer.CreateAsync(new FileEntity { Id = "docs|doc", Title = "v1", File = Bytes(previous) }, Ct);
        var read = (await stores.Later.OneAsync("docs|doc", Ct))!;

        var failure = await Record.ExceptionAsync(() => stores.Later.UpsertBatchAsync(
        [
            new FileEntity { Id = "docs|doc", Title = "v2", File = Bytes(replacement) },
            new FileEntity { Id = "history|doc-1", Title = "v1", File = read.File },
        ], Ct));

        var history = await stores.Later.OneAsync("history|doc-1", Ct);
        Assert.True(failure is null && history?.File is not null,
            "The batch " + Outcome(failure) + "; the history entity is " + (history is null ? "not stored" : "stored") + ".");
        Assert.Equal(previous, await BytesOfAsync(history!.File!));
        Assert.Equal(replacement, await BytesOfAsync((await stores.Later.OneAsync("docs|doc", Ct))!.File!));
        // The document's previous blob was released and nothing reads it now: the cleanup deleted it.
        Assert.Equal(2, stores.Blobs.Names.Count);
        Assert.DoesNotContain(stores.Blobs.Names, n => n.Contains("/docs/doc/", StringComparison.Ordinal) && n.EndsWith(Sha(previous), StringComparison.Ordinal));
    }

    // The order the caller could choose before: the history entity first.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoTransactions_TheHistoryEntityListedFirst_KeepsThePreviousFile(bool inMemory)
    {
        using var stores = new Stores(inMemory, Offload());
        var previous = RandomBytes(40_000, seed: 1);
        var replacement = RandomBytes(40_000, seed: 2);
        await stores.Writer.CreateAsync(new FileEntity { Id = "docs|doc", Title = "v1", File = Bytes(previous) }, Ct);
        var read = (await stores.Later.OneAsync("docs|doc", Ct))!;

        await stores.Later.UpsertBatchAsync(
        [
            new FileEntity { Id = "history|doc-1", Title = "v1", File = read.File },
            new FileEntity { Id = "docs|doc", Title = "v2", File = Bytes(replacement) },
        ], Ct);

        Assert.Equal(replacement, await BytesOfAsync((await stores.Later.OneAsync("docs|doc", Ct))!.File!));
        Assert.Equal(previous, await BytesOfAsync((await stores.Later.OneAsync("history|doc-1", Ct))!.File!));
        Assert.Equal(2, stores.Blobs.Names.Count);
    }

    // One partition, more rows than one transaction holds: the document is in the first transaction, the
    // history entity in the second.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnePartition_MoreRowsThanOneTransactionHolds_ReplaceTheDocumentsFile_AndKeepThePreviousFileInAHistoryEntity(bool inMemory)
    {
        using var stores = new Stores(inMemory, Offload());
        var previous = RandomBytes(40_000, seed: 1);
        var replacement = RandomBytes(40_000, seed: 2);
        await stores.Writer.CreateAsync(new FileEntity { Id = "p|doc", Title = "v1", File = Bytes(previous) }, Ct);
        var read = (await stores.Later.OneAsync("p|doc", Ct))!;
        var batch = new List<FileEntity> { new() { Id = "p|doc", Title = "v2", File = Bytes(replacement) } };
        for (var i = 0; i < 99; i++)
            batch.Add(new FileEntity { Id = $"p|note-{i:D3}", Title = "note" });
        batch.Add(new FileEntity { Id = "p|history-1", Title = "v1", File = read.File });

        var failure = await Record.ExceptionAsync(() => stores.Later.UpsertBatchAsync(batch, Ct));

        var history = await stores.Later.OneAsync("p|history-1", Ct);
        Assert.True(failure is null && history?.File is not null,
            "The batch of " + batch.Count + " rows in one partition " + Outcome(failure)
            + "; the history entity is " + (history is null ? "not stored" : "stored") + ".");
        Assert.Equal(previous, await BytesOfAsync(history!.File!));
        Assert.Equal(replacement, await BytesOfAsync((await stores.Later.OneAsync("p|doc", Ct))!.File!));
        Assert.Equal(2, stores.Blobs.Names.Count);
    }

    // Two documents in two partitions exchange their files in one batch.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoTransactions_TwoDocumentsExchangeTheirFiles_BothFilesAreStillStored(bool inMemory)
    {
        using var stores = new Stores(inMemory, Offload());
        var first = RandomBytes(40_000, seed: 1);
        var second = RandomBytes(40_000, seed: 2);
        await stores.Writer.CreateAsync(new FileEntity { Id = "a|doc", Title = "first", File = Bytes(first) }, Ct);
        await stores.Writer.CreateAsync(new FileEntity { Id = "b|doc", Title = "second", File = Bytes(second) }, Ct);
        var readA = (await stores.Later.OneAsync("a|doc", Ct))!;
        var readB = (await stores.Later.OneAsync("b|doc", Ct))!;

        var failure = await Record.ExceptionAsync(() => stores.Later.UpsertBatchAsync(
        [
            new FileEntity { Id = "a|doc", Title = "second", File = readB.File },
            new FileEntity { Id = "b|doc", Title = "first", File = readA.File },
        ], Ct));

        Assert.True(failure is null, "The exchange " + Outcome(failure) + ".");
        Assert.Equal(second, await BytesOfAsync((await stores.Later.OneAsync("a|doc", Ct))!.File!));
        Assert.Equal(first, await BytesOfAsync((await stores.Later.OneAsync("b|doc", Ct))!.File!));
        // Each row now holds a blob of its own; the two released ones were deleted after the last commit.
        Assert.Equal(2, stores.Blobs.Names.Count);
    }

    // A file moves from one document to a document in another partition, in one batch.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoTransactions_MoveAFileFromOneDocumentToAnother_TheFileIsStillStored(bool inMemory)
    {
        using var stores = new Stores(inMemory, Offload());
        var file = RandomBytes(40_000, seed: 1);
        await stores.Writer.CreateAsync(new FileEntity { Id = "a|doc", Title = "source", File = Bytes(file) }, Ct);
        var source = (await stores.Later.OneAsync("a|doc", Ct))!;

        var failure = await Record.ExceptionAsync(() => stores.Later.UpsertBatchAsync(
        [
            new FileEntity { Id = "a|doc", Title = "source", File = null },
            new FileEntity { Id = "b|doc", Title = "target", File = source.File },
        ], Ct));

        var atTheTarget = (await stores.Later.OneAsync("b|doc", Ct))?.File;
        Assert.True(failure is null && atTheTarget is not null,
            "The move " + Outcome(failure) + "; the target document " + (atTheTarget is null ? "has no file" : "has it") + ".");
        Assert.Equal(file, await BytesOfAsync(atTheTarget!));
        Assert.Null((await stores.Later.OneAsync("a|doc", Ct))!.File);
        Assert.Single(stores.Blobs.Names);
    }

    // ---- The same call with the library's DEFAULT options (cleanup grace of five minutes) ----

    // Makes the one stored blob look last written that long ago (cleanup compares that time with the grace).
    private static void Age(InMemoryOffloadBlobStore blobs, byte[] bytes, TimeSpan age)
    {
        var name = Assert.Single(blobs.Names);
        blobs.Seed(name, bytes, name[(name.LastIndexOf('/') + 1)..], DateTimeOffset.UtcNow - age);
    }

    private static async Task ReplaceTheFileAndKeepThePreviousOneAsync(bool inMemory, TimeSpan savedAgo, int blobsAfterwards)
    {
        var offload = new BlobOffloadOptions { ThresholdBytes = 1_000 };
        Assert.Equal(TimeSpan.FromMinutes(5), offload.CleanupGrace);
        using var stores = new Stores(inMemory, offload);
        var previous = RandomBytes(40_000, seed: 1);
        var replacement = RandomBytes(40_000, seed: 2);
        await stores.Writer.CreateAsync(new FileEntity { Id = "docs|doc", Title = "v1", File = Bytes(previous) }, Ct);
        Age(stores.Blobs, previous, savedAgo);
        var read = (await stores.Later.OneAsync("docs|doc", Ct))!;
        Assert.Equal(previous, await BytesOfAsync(read.File!));

        var failure = await Record.ExceptionAsync(() => stores.Later.UpsertBatchAsync(
        [
            new FileEntity { Id = "docs|doc", Title = "v2", File = Bytes(replacement) },
            new FileEntity { Id = "history|doc-1", Title = "v1", File = read.File },
        ], Ct));

        var history = await stores.Later.OneAsync("history|doc-1", Ct);
        Assert.True(failure is null && history?.File is not null,
            "The previous file (saved " + savedAgo + " ago, cleanup grace 00:05:00): the batch " + Outcome(failure)
            + "; the history entity is " + (history is null ? "not stored" : "stored") + "; blobs left: " + stores.Blobs.Names.Count + ".");
        Assert.Equal(previous, await BytesOfAsync(history!.File!));
        Assert.Equal(replacement, await BytesOfAsync((await stores.Later.OneAsync("docs|doc", Ct))!.File!));
        // The released blob goes after the batch only when it is older than the grace; otherwise the sweep takes it.
        Assert.Equal(blobsAfterwards, stores.Blobs.Names.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultGrace_TwoTransactions_ADocumentSavedAMinuteAgo_KeepsItsPreviousFile_AndLeavesTheReleasedBlobToTheSweep(bool inMemory) =>
        await ReplaceTheFileAndKeepThePreviousOneAsync(inMemory, TimeSpan.FromMinutes(1), blobsAfterwards: 3);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultGrace_TwoTransactions_ADocumentSavedAnHourAgo_KeepsItsPreviousFile_AndTheReleasedBlobIsDeleted(bool inMemory) =>
        await ReplaceTheFileAndKeepThePreviousOneAsync(inMemory, TimeSpan.FromHours(1), blobsAfterwards: 2);

    // ---- A batch that fails after an earlier transaction committed ----

    // The second transaction fails for a reason of its own (a transient service error on the table store;
    // an exception from the in-memory store's commit hook). The first transaction stays committed. The
    // history entity the caller passed still gives the previous file, and the same batch, sent again,
    // completes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoTransactions_TheSecondFailsForAReasonOfItsOwn_ThePreviousFileSurvives_AndTheBatchCanBeSentAgain(bool inMemory)
    {
        var previous = RandomBytes(40_000, seed: 1);
        var replacement = RandomBytes(40_000, seed: 2);
        FileEntity Document(string title, byte[] bytes) => new() { Id = "docs|doc", Title = title, File = Bytes(bytes) };

        using var rig = inMemory ? null : new WriteBatchRig<FileEntity>(Offload());
        var memory = inMemory ? new InMemoryStorage<FileEntity>(null, new UnifiedTableStorageOptions { BlobOffload = Offload() }) : null;
        IStorage<FileEntity> store = inMemory ? memory! : rig!.NewStore();
        await (inMemory ? store : rig!.NewStore()).CreateAsync(Document("v1", previous), Ct);
        var read = (await store.OneAsync("docs|doc", Ct))!;
        var document = Document("v2", replacement);
        var history = new FileEntity { Id = "history|doc-1", Title = "v1", File = read.File };

        if (inMemory)
        {
            var commits = 0;
            memory!.BeforeLargeCommit = () =>
            {
                if (Interlocked.Increment(ref commits) == 2)
                    throw new IOException("the second transaction fails for a reason of its own");
            };
        }
        else
        {
            // The point read that precedes the history entity's write is answered with a transient error.
            rig!.Table.ResponseInFlight = request => request is { Operation: "get", PartitionKey: "history" }
                ? throw new RequestFailedException(503, "The server is busy.", "ServerBusy", null)
                : Task.CompletedTask;
        }
        var failure = await Record.ExceptionAsync(() => store.UpsertBatchAsync([document, history], Ct));
        if (inMemory)
            memory!.BeforeLargeCommit = null;
        else
            rig!.Table.ResponseInFlight = null;

        Assert.NotNull(failure);
        Assert.Equal("v2", (await store.OneAsync("docs|doc", Ct))!.Title);
        Assert.Null(await store.OneAsync("history|doc-1", Ct));
        var stillThere = await Record.ExceptionAsync(() => BytesOfAsync(history.File!));
        Assert.True(stillThere is null,
            "The previous file is gone: the batch threw " + failure.GetType().Name + ": " + failure.Message
            + "; reading the file of the history entity the caller passed threw " + stillThere?.GetType().Name + ": " + stillThere?.Message);
        Assert.Equal(previous, await BytesOfAsync(history.File!));

        await store.UpsertBatchAsync([document, history], Ct);

        Assert.Equal(replacement, await BytesOfAsync((await store.OneAsync("docs|doc", Ct))!.File!));
        Assert.Equal(previous, await BytesOfAsync((await store.OneAsync("history|doc-1", Ct))!.File!));
    }

    // ---- Through the real SDK clients on Azurite ----

    // A cleanup grace of two seconds and a file older than that: one batch moves a file from a document to
    // a document in another partition, listed source first and target first.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Azurite_OneBatchMovesAFileToADocumentInAnotherPartition_TheFileIsStillStored(bool sourceFirst)
    {
        await using var azurite = await WriteBatchAzurite.OpenAsync();
        Assert.SkipWhen(azurite is null, WriteBatchAzurite.SkipReason);
        var table = azurite!.NewTable("Move");
        var grace = TimeSpan.FromSeconds(2);
        var offload = azurite.Offload(grace, 1_000);
        var source = azurite.Pk + "a|doc";
        var target = azurite.Pk + "b|doc";
        var file = RandomBytes(40_000, seed: 1);
        await azurite.Store<FileEntity>(table, offload, cache: false).CreateAsync(
            new FileEntity { Id = source, Title = "source", File = Bytes(file) }, Ct);
        await azurite.EveryBlobIsOlderThanAsync(grace + TimeSpan.FromSeconds(1), Ct);
        var store = azurite.Store<FileEntity>(table, offload, cache: false);
        var read = (await store.OneAsync(source, Ct))!;
        Assert.Equal(file, await BytesOfAsync(read.File!));

        var withoutIt = new FileEntity { Id = source, Title = "source", File = null };
        var withIt = new FileEntity { Id = target, Title = "target", File = read.File };
        var failure = await Record.ExceptionAsync(() => store.UpsertBatchAsync(sourceFirst ? [withoutIt, withIt] : [withIt, withoutIt], Ct));

        var reader = azurite.Store<FileEntity>(table, offload, cache: false);
        var atTheTarget = (await reader.OneAsync(target, Ct))?.File;
        Assert.True(failure is null && atTheTarget is not null,
            "The move " + Outcome(failure) + "; the target document " + (atTheTarget is null ? "has no file" : "has it")
            + ", blobs left in the container: " + (await azurite.BlobNamesAsync()).Count + ".");
        Assert.Equal(file, await BytesOfAsync(atTheTarget!));
        Assert.Null((await reader.OneAsync(source, Ct))!.File);
        // The source's released blob is older than the grace and nothing reads it now: deleted after the batch.
        Assert.Single(await azurite.BlobNamesAsync());
    }
}
