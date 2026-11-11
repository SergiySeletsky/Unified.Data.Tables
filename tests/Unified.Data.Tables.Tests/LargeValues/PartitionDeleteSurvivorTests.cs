using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A partition delete lists the partition's rows, deletes them, and then cleans up the blobs stored
/// under the partition. A row another writer creates after the listing survives the delete, so its blob
/// must survive the cleanup too, however long the row deletes took: after the rows are deleted, a blob
/// is deleted only if no row that is in the partition NOW references it. With no survivor the behaviour
/// is as before: every blob older than <see cref="BlobOffloadOptions.CleanupGrace"/> goes, younger ones
/// stay for the orphan sweep.
/// </summary>
public class PartitionDeleteSurvivorTests
{
    private const string Pk = "p";

    private static readonly string Text = Ascii(40_000, seed: 81);
    private static readonly string OtherText = Ascii(40_000, seed: 82);
    private static readonly string SurvivorText = Ascii(40_000, seed: 83);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UnifiedTableStorageOptions Options(TimeSpan? grace = null)
    {
        var offload = new BlobOffloadOptions { ThresholdBytes = 1_000 };
        if (grace is { } g) offload.CleanupGrace = g;
        return new UnifiedTableStorageOptions { BlobOffload = offload, Cache = CachePolicy.Disabled };
    }

    private static SplitTextEntity Row(string rk, string body) => new() { Id = Pk + "|" + rk, Body = body, Title = "t-" + rk };

    private static string BlobOf(TableEntity row) => BlobReference.Parse("Body", (string)row["Body__blob"]).Path;

    /// <summary>Makes a stored blob look last written <paramref name="age"/> ago.</summary>
    private static async Task AgeAsync(InMemoryOffloadBlobStore blobs, string name, TimeSpan age)
    {
        await using var stream = await blobs.OpenReadAsync("x", name, CancellationToken.None);
        var bytes = await ReadAllAsync(stream);
        blobs.Seed(name, bytes, Sha(bytes), DateTimeOffset.UtcNow - age);
    }

    // ── InMemoryStorage ───────────────────────────────────────────────────

    [Fact]
    public async Task InMemory_RowCreatedWhileThePartitionIsDeleted_KeepsItsBlob()
    {
        // Grace zero: every blob that exists when the cleanup runs is already old enough for it.
        var store = new InMemoryStorage<SplitTextEntity>(null, Options(TimeSpan.Zero));
        await store.CreateAsync(Row("d1", Text), Ct);
        await store.CreateAsync(Row("d2", OtherText), Ct);
        // Another writer commits a row in the partition once the delete's rows are gone, before its blob cleanup.
        store.AfterLargeCommit = () =>
        {
            store.AfterLargeCommit = null;
            store.CreateAsync(Row("survivor", SurvivorText), CancellationToken.None).GetAwaiter().GetResult();
        };

        Assert.Equal(2, await store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(["survivor"], store.PhysicalRowKeys(Pk));
        var survivorBlob = BlobOf(store.PhysicalRow(Pk, "survivor")!);
        Assert.Equal([survivorBlob], store.OffloadedBlobNames());
        Assert.Equal(SurvivorText, (await store.OneAsync("p|survivor", Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task InMemory_RowDeletesOutlastTheGrace_TheSurvivorsBlobIsOlderThanTheGrace_AndIsKept()
    {
        // The default grace of five minutes, and a delete that (as far as the blobs can tell) took ten:
        // when the cleanup runs, the survivor's blob is older than the grace, like the deleted rows' blobs.
        var store = new InMemoryStorage<SplitTextEntity>(null, Options());
        await store.CreateAsync(Row("d1", Text), Ct);
        var doomed = Assert.Single(store.OffloadedBlobNames());
        store.AfterLargeCommit = () =>
        {
            store.AfterLargeCommit = null;
            store.CreateAsync(Row("survivor", SurvivorText), CancellationToken.None).GetAwaiter().GetResult();
            foreach (var name in store.OffloadedBlobNames())
                AgeAsync(store.Blobs, name, TimeSpan.FromMinutes(10)).GetAwaiter().GetResult();
        };

        Assert.Equal(1, await store.DeletePartitionAsync(Pk, Ct));

        var survivorBlob = BlobOf(store.PhysicalRow(Pk, "survivor")!);
        Assert.Equal([survivorBlob], store.OffloadedBlobNames());
        Assert.DoesNotContain(doomed, store.OffloadedBlobNames());
        Assert.Equal(SurvivorText, (await store.OneAsync("p|survivor", Ct))!.Body, StringComparer.Ordinal);
    }

    [Fact]
    public async Task InMemory_NoSurvivor_BlobsOlderThanTheGraceGo_YoungerOnesStayForTheSweep()
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options());
        await store.CreateAsync(Row("old", Text), Ct);
        var oldBlob = Assert.Single(store.OffloadedBlobNames());
        await AgeAsync(store.Blobs, oldBlob, TimeSpan.FromHours(1));
        await store.CreateAsync(Row("young", OtherText), Ct);
        var youngBlob = store.OffloadedBlobNames().Single(n => n != oldBlob);
        // An orphan a crashed write left under the partition, older than the grace: it goes too.
        var orphan = ValueOffloadCoordinator.BlobName(nameof(SplitTextEntity), Pk, "crashed", "Body", Sha(Utf8("orphan")));
        store.Blobs.Seed(orphan, Utf8("orphan"), Sha(Utf8("orphan")), DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(2, await store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(0, store.PhysicalRowCount);
        Assert.Equal([youngBlob], store.OffloadedBlobNames());

        await AgeAsync(store.Blobs, youngBlob, TimeSpan.FromMinutes(10));
        Assert.Equal(1, await store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));
        Assert.Empty(store.OffloadedBlobNames());
    }

    [Fact]
    public async Task InMemory_EmptyPartition_StillCleansUpItsOrphans_AndLeavesOtherPartitionsAlone()
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options(TimeSpan.Zero));
        await store.CreateAsync(new SplitTextEntity { Id = "other|r", Body = Text }, Ct);
        var otherPartition = Assert.Single(store.OffloadedBlobNames());
        var orphan = ValueOffloadCoordinator.BlobName(nameof(SplitTextEntity), Pk, "crashed", "Body", Sha(Utf8("orphan")));
        store.Blobs.Seed(orphan, Utf8("orphan"), Sha(Utf8("orphan")), DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(0, await store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal([otherPartition], store.OffloadedBlobNames());
    }

    // ── the shared core ───────────────────────────────────────────────────

    private static async IAsyncEnumerable<TableEntity> Rows(IEnumerable<TableEntity> rows, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            yield return row;
        }
        await Task.CompletedTask;
    }

    private static string Seed(InMemoryOffloadBlobStore blobs, string rk, string column, string token, TimeSpan age)
    {
        var name = ValueOffloadCoordinator.BlobName("T", Pk, rk, column, Sha(Utf8(token)));
        blobs.Seed(name, Utf8(token), Sha(Utf8(token)), DateTimeOffset.UtcNow - age);
        return name;
    }

    private static TableEntity Referencing(string rk, string column, string blob) => new(Pk, rk)
    {
        [column + "__blob"] = new BlobReference(blob, new string('a', 64), 1, "application/octet-stream", "binary").ToJson(),
    };

    [Fact]
    public async Task Core_DeletesOnlyBlobsNoRowOfThePartitionReferencesNow()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var table = new LargeValueTable("T", new BlobOffloadOptions(), blobs);
        var hour = TimeSpan.FromHours(1);
        var referenced = Seed(blobs, "survivor", "Body", "s", hour);
        var replaced = Seed(blobs, "survivor", "Body", "an older value of the survivor", hour);
        var deletedRows = Seed(blobs, "gone", "Body", "g", hour);
        var young = Seed(blobs, "gone", "Body", "written a moment ago", TimeSpan.FromSeconds(5));
        var selects = new List<IReadOnlyList<string>?>();

        await table.DeletePartitionBlobsAsync(Pk, typeof(SplitTextEntity),
            (select, ct) =>
            {
                selects.Add(select);
                return Rows([Referencing("survivor", "Body", referenced)], ct);
            },
            onListFailure: ex => Assert.Fail(ex.ToString()), onFailure: (name, ex) => Assert.Fail(name + ": " + ex), Ct);

        Assert.Equal(new[] { referenced, young }.Order(StringComparer.Ordinal), blobs.Names.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(replaced, blobs.Names);
        Assert.DoesNotContain(deletedRows, blobs.Names);
        // One read of the rows, projected onto the keys and the reference cells.
        var select = Assert.Single(selects);
        Assert.Equal(["PartitionKey", "RowKey", "_SplitOf", "Body__blob"], select);
    }

    [Fact]
    public async Task Core_ReferenceInAColumnTheTypeDoesNotDeclare_StillKeepsItsBlob()
    {
        // Another type sharing the table wrote the surviving row: its reference column is read as well.
        var blobs = new InMemoryOffloadBlobStore();
        var table = new LargeValueTable("T", new BlobOffloadOptions(), blobs);
        var foreign = Seed(blobs, "x", "Inner_Doc", "f", TimeSpan.FromHours(1));
        IReadOnlyList<string>? select = null;

        await table.DeletePartitionBlobsAsync(Pk, typeof(SplitTextEntity),
            (s, ct) =>
            {
                select = s;
                return Rows([Referencing("x", "Inner_Doc", foreign)], ct);
            }, null, null, Ct);

        Assert.Equal([foreign], blobs.Names);
        Assert.Contains("Inner_Doc__blob", select!);
    }

    [Fact]
    public async Task Core_UnreadableReferenceOfASurvivor_KeepsEveryBlobOfThatCell()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var table = new LargeValueTable("T", new BlobOffloadOptions(), blobs);
        var guarded = Seed(blobs, "survivor", "Body", "s", TimeSpan.FromHours(1));
        var orphan = Seed(blobs, "gone", "Body", "g", TimeSpan.FromHours(1));

        await table.DeletePartitionBlobsAsync(Pk, typeof(SplitTextEntity),
            (_, ct) => Rows([new TableEntity(Pk, "survivor") { ["Body__blob"] = "{not json" }], ct), null, null, Ct);

        Assert.Equal([guarded], blobs.Names);
        Assert.DoesNotContain(orphan, blobs.Names);
    }

    [Fact]
    public async Task Core_NoBlobOldEnough_DoesNotReadTheRows()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var table = new LargeValueTable("T", new BlobOffloadOptions(), blobs);
        var young = Seed(blobs, "gone", "Body", "g", TimeSpan.FromSeconds(5));
        var reads = 0;

        await table.DeletePartitionBlobsAsync(Pk, typeof(SplitTextEntity),
            (_, ct) =>
            {
                reads++;
                return Rows([], ct);
            }, null, null, Ct);

        Assert.Equal(0, reads);
        Assert.Equal([young], blobs.Names);
    }

    [Fact]
    public async Task Core_RowsCannotBeRead_DeletesNothing_AndReportsIt()
    {
        var blobs = new InMemoryOffloadBlobStore();
        var table = new LargeValueTable("T", new BlobOffloadOptions(), blobs);
        var old = Seed(blobs, "gone", "Body", "g", TimeSpan.FromHours(1));
        var listFailures = new List<Exception>();
        var failures = new List<string>();

        await table.DeletePartitionBlobsAsync(Pk, typeof(SplitTextEntity),
            (_, _) => throw new RequestFailedException(503, "the table service is busy"),
            listFailures.Add, (name, _) => failures.Add(name), Ct);

        Assert.Equal([old], blobs.Names);
        Assert.IsType<RequestFailedException>(Assert.Single(listFailures));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Core_ABlobTouchedAfterItWasListed_IsKept()
    {
        // A writer reuses an old blob for a row it has not committed yet: it touches the blob first.
        var inner = new InMemoryOffloadBlobStore();
        var blobs = new RecordingBlobStore([], inner);
        var table = new LargeValueTable("T", new BlobOffloadOptions(), blobs);
        var name = Seed(inner, "gone", "Body", "g", TimeSpan.FromHours(1));
        blobs.BeforeDelete = n => inner.TouchAsync(n, CancellationToken.None);

        await table.DeletePartitionBlobsAsync(Pk, typeof(SplitTextEntity), (_, ct) => Rows([], ct), null, null, Ct);

        Assert.Equal([name], inner.Names);
        Assert.Equal([false], blobs.DeleteResults);
    }

    // ── TableStorage (mocked table) ────────────────────────────────────────

    private sealed class Rig : IDisposable
    {
        public Rig(InMemoryOffloadBlobStore inner, TimeSpan? grace = null, ILogger<TableStorage<SplitTextEntity>>? logger = null)
        {
            Blobs = new RecordingBlobStore(Log, inner);
            H = new StorageHarness<SplitTextEntity>(options: Options(grace), logger: logger, blobs: Blobs);
            H.Table.When(t => t.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>()))
                .Do(_ => Add("table:tx"));
            H.Table.When(t => t.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()))
                .Do(c =>
                {
                    Add("table:rows");
                    Selects.Add(c.ArgAt<IEnumerable<string>?>(2)?.ToList());
                });
            H.SetupTransaction();
        }

        public List<string> Log { get; } = [];

        public List<List<string>?> Selects { get; } = [];

        public RecordingBlobStore Blobs { get; }

        public StorageHarness<SplitTextEntity> H { get; }

        public TableStorage<SplitTextEntity> Store => H.Store;

        /// <summary>The rows the delete lists (the expression-filter query).</summary>
        public void Listed(params TableEntity[] rows) => H.SetupQueryByPartition(rows);

        /// <summary>The rows that are in the partition once the delete's transactions are done (the string-filter query).</summary>
        public void RowsAfterTheDelete(params TableEntity[] rows) => H.SetupQueryByFilter(rows);

        private void Add(string entry)
        {
            lock (Log) Log.Add(entry);
        }

        public void Dispose() => H.Dispose();
    }

    /// <summary>The row a create writes, with its blob left in <paramref name="inner"/> and aged by an hour.</summary>
    private static async Task<TableEntity> StoredRowAsync(InMemoryOffloadBlobStore inner, SplitTextEntity entity)
    {
        var written = new List<TableEntity>();
        using var h = new StorageHarness<SplitTextEntity>(options: Options(), blobs: inner);
        h.Table.When(t => t.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>()))
            .Do(c => written.Add(c.Arg<TableEntity>()));
        h.SetupAdd("W/\"v1\"");
        await h.Store.CreateAsync(entity, Ct);
        var row = Assert.Single(written);
        row.ETag = new ETag("W/\"v1\"");
        await AgeAsync(inner, BlobOf(row), TimeSpan.FromHours(1));
        return row;
    }

    [Fact]
    public async Task TableStorage_SurvivingRow_KeepsItsBlob_TheDeletedRowsBlobsGo()
    {
        var inner = new InMemoryOffloadBlobStore();
        var d1 = await StoredRowAsync(inner, Row("d1", Text));
        var d2 = await StoredRowAsync(inner, Row("d2", OtherText));
        var survivor = await StoredRowAsync(inner, Row("survivor", SurvivorText));
        using var rig = new Rig(inner);
        rig.Listed(d1, d2);
        rig.RowsAfterTheDelete(survivor);

        Assert.Equal(2, await rig.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal([BlobOf(survivor)], inner.Names);
        // Rows first, then the blobs are listed, then the rows that are there now are read, then the deletes.
        Assert.Equal(["table:tx", "blob:list", "table:rows", "blob:delete", "blob:delete"], rig.Log);
        Assert.Equal(["PartitionKey", "RowKey", "_SplitOf", "Body__blob"], Assert.Single(rig.Selects));
    }

    [Fact]
    public async Task TableStorage_NoSurvivor_BlobsOlderThanTheGraceGo_YoungerOnesStay()
    {
        var inner = new InMemoryOffloadBlobStore();
        var old = await StoredRowAsync(inner, Row("old", Text));
        var young = await StoredRowAsync(inner, Row("young", OtherText));
        await AgeAsync(inner, BlobOf(young), TimeSpan.FromSeconds(5));
        using var rig = new Rig(inner);
        rig.Listed(old, young);
        rig.RowsAfterTheDelete();

        Assert.Equal(2, await rig.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal([BlobOf(young)], inner.Names);
        Assert.Equal(["delete " + BlobOf(old)], rig.Blobs.Calls.Where(c => c.StartsWith("delete", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TableStorage_RowsCannotBeReadAfterTheDelete_TheDeleteStillSucceeds_AndNoBlobGoes()
    {
        var inner = new InMemoryOffloadBlobStore();
        var d1 = await StoredRowAsync(inner, Row("d1", Text));
        var logger = new ListLogger();
        using var rig = new Rig(inner, logger: logger);
        rig.Listed(d1);
        rig.H.Table.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new RequestFailedException(503, "the table service is busy"));

        Assert.Equal(1, await rig.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal([BlobOf(d1)], inner.Names);
        Assert.Equal(0, rig.Blobs.Count("delete"));
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.IsType<RequestFailedException>(warning.Error);
        Assert.Contains("orphan sweep", warning.Message, StringComparison.Ordinal);
    }

    private sealed class ListLogger : ILogger<TableStorage<SplitTextEntity>>
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

/// <summary>
/// <see cref="PartitionDeleteSurvivorTests"/> against a local Azurite: a pipeline policy on the deleting
/// store's table client lets a second store create a row between the delete's row listing and its first
/// delete transaction, and then holds the delete back for longer than the cleanup grace.
/// </summary>
public sealed class AzuritePartitionDeleteSurvivorTests : AzuriteCleanupTestBase
{
    private const string TableName = nameof(SplitTextEntity);

    private static readonly string Text = Ascii(40_000, seed: 91);
    private static readonly string OtherText = Ascii(40_000, seed: 92);
    private static readonly string SurvivorText = Ascii(40_000, seed: 93);

    private SplitTextEntity Row(string rk, string body) => new() { Id = Id(rk), Body = body, Title = "t-" + rk };

    private string BlobName(string rk, string body) => $"{TableName}/{Pk}/{rk}/Body/{Sha(Utf8(body))}";

    [Fact]
    public async Task RowCreatedAfterTheListing_KeepsItsBlob_WhenTheRowDeletesOutlastTheGrace()
    {
        SkipUnlessAvailable();
        var grace = TimeSpan.FromSeconds(1);
        var writer = Store<SplitTextEntity>(grace);
        await writer.CreateAsync(Row("d1", Text), Ct);
        await writer.CreateAsync(Row("d2", OtherText), Ct);
        Assert.Equal(2, (await BlobNamesAsync()).Count);

        // Between the delete's row listing and its first transaction, another writer creates a row with
        // an offloaded value; the delete then takes longer than the grace.
        var interleave = new BeforeFirstBatch(async () =>
        {
            await writer.CreateAsync(Row("survivor", SurvivorText), CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
        });
        var deleting = Store<SplitTextEntity>(grace, service: TableServiceWith(interleave));

        Assert.Equal(2, await deleting.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(1, interleave.Fired);
        Assert.Equal(["survivor"], (await RawRowsAsync(TableName)).Select(r => r.RowKey));
        // The surviving row reads back whole, and the blobs of the deleted rows are gone.
        Assert.Equal(SurvivorText, (await writer.OneAsync(Id("survivor"), Ct))!.Body, StringComparer.Ordinal);
        Assert.Equal([BlobName("survivor", SurvivorText)], await BlobNamesAsync());
    }

    [Fact]
    public async Task NoSurvivor_BlobsOlderThanTheGraceGo_YoungerOnesStayForTheSweep()
    {
        SkipUnlessAvailable();
        // Long enough that the delete below, which follows the young row's create at once, still finds
        // that row's blob younger than the grace even on a busy emulator.
        var grace = TimeSpan.FromSeconds(6);
        var store = Store<SplitTextEntity>(grace);
        await store.CreateAsync(Row("old", Text), Ct);
        await BlobsOlderThanAsync(grace);
        await store.CreateAsync(Row("young", OtherText), Ct);

        Assert.Equal(2, await store.DeletePartitionAsync(Pk, Ct));

        Assert.Empty(await RawRowsAsync(TableName));
        Assert.Equal([BlobName("young", OtherText)], await BlobNamesAsync());

        // The orphan sweep collects the young one once it is old enough.
        await BlobsOlderThanAsync(TimeSpan.Zero);
        Assert.Equal(1, await store.SweepOrphanBlobsAsync(Pk, TimeSpan.Zero, Ct));
        Assert.Empty(await BlobNamesAsync());
    }

    /// <summary>Runs an action once, before the first entity-group transaction (<c>POST .../$batch</c>) is sent.</summary>
    private sealed class BeforeFirstBatch(Func<Task> action) : HttpPipelinePolicy
    {
        private int fired;

        public int Fired => Volatile.Read(ref fired);

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (IsBatch(message) && Interlocked.Exchange(ref fired, 1) == 0)
                await action();
            await ProcessNextAsync(message, pipeline);
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (IsBatch(message) && Interlocked.Exchange(ref fired, 1) == 0)
                action().GetAwaiter().GetResult();
            ProcessNext(message, pipeline);
        }

        private static bool IsBatch(HttpMessage message) =>
            message.Request.Method == RequestMethod.Post && message.Request.Uri.Path.EndsWith("$batch", StringComparison.Ordinal);
    }
}
