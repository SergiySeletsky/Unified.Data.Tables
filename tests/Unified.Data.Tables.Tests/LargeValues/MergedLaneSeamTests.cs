using System.Diagnostics;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Seams between the lanes of fix wave 3 (stage x1, item 5): each behaviour below was written in one lane
/// against another lane's older code, and is checked here on the merged code.
/// </summary>
public class MergedLaneSeamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── 5.2: IsKnownOrdinaryColumn (r) with the awareness metadata (s1) ───────────────────────────

    /// <summary>A plain type with a nested object whose constructor counts its runs; never used by another test.</summary>
    public sealed class SeamCountedPlain : Entity
    {
        private static int constructed;

        public SeamCountedPlain() => Interlocked.Increment(ref constructed);

        public static int Constructed => Volatile.Read(ref constructed);

        public string? Title { get; set; }

        public SeamOwner? Owner { get; set; }
    }

    public sealed class SeamOwner
    {
        public string? Name { get; set; }
    }

    [Fact]
    public void PlainTypeProjection_ResolvesNothing_AndConstructsNothing_FromTheFirstUseOfItsStoreType()
    {
        Assert.Equal(0, SeamCountedPlain.Constructed);

        var select = TableStorage<SeamCountedPlain>.BuildSelect(new QueryOptions { Select = ["Title", "Owner_Name", "NoSuchColumn"] });
        using var h = new StorageHarness<SeamCountedPlain>();

        Assert.Equal(["PartitionKey", "RowKey", "Timestamp", "_TypeName", "Title", "Owner_Name", "NoSuchColumn"], select);
        Assert.Equal(0, SeamCountedPlain.Constructed);
    }

    [Fact]
    public async Task AwareTypeProjection_OfAnOrdinaryColumn_SendsANarrowedSelect()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var selects = new List<IEnumerable<string>?>();
        h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Do<IEnumerable<string>?>(s => selects.Add(s)), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable(Mocks.Row("p", "r")));

        await h.Store.QueryAsync(new QueryOptions { Partition = "p", Select = ["Title"] }, Ct);
        await h.Store.QueryAsync(new QueryOptions { Partition = "p", Select = ["Body"] }, Ct);

        Assert.Equal(2, selects.Count);
        Assert.Equal(["PartitionKey", "RowKey", "Timestamp", "_TypeName", "_SplitOf", "_SplitIndex", "_SplitParts", "Title"], selects[0]);
        Assert.Null(selects[1]); // a large value: the whole row
    }

    // ── 5.3: the lazy prior-row select (w2) with the awareness probe (s1) ─────────────────────────

    public sealed class SeamProbedOwner
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    /// <summary>Aware by its declared types and with a nested object, so the store probes a default instance.</summary>
    public sealed class SeamProbedEntity : Entity
    {
        private static readonly List<string> Stacks = [];

        public SeamProbedEntity()
        {
            lock (Stacks)
                Stacks.Add(new StackTrace().ToString());
        }

        public static IReadOnlyList<string> ConstructorStacks
        {
            get
            {
                lock (Stacks)
                    return [.. Stacks];
            }
        }

        public SeamProbedOwner Owner { get; set; } = new();

        public string? Title { get; set; }
    }

    [Fact]
    public async Task NoEntityConstructorRuns_InsideATypeInitializerOfAStore_ForAnAwareTypeToo()
    {
        Assert.True(LargeValueTable.IsLargeValueAware(typeof(SeamProbedEntity)));
        Assert.Empty(SeamProbedEntity.ConstructorStacks);

        // First uses of both store types, then every operation that needs the probe's columns.
        using (var h = new StorageHarness<SeamProbedEntity>())
            Assert.NotNull(h.Store);
        var memory = new InMemoryStorage<SeamProbedEntity>(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });
        _ = TableStorage<SeamProbedEntity>.BuildSelect(new QueryOptions { Select = ["Title"] });
        _ = LargeValueTable.PriorRowSelect(typeof(SeamProbedEntity));
        await memory.CreateAsync(new SeamProbedEntity { Id = "p|a", Owner = new SeamProbedOwner { Notes = new string('n', 70_000) } }, Ct);
        await memory.UpsertAsync(new SeamProbedEntity { Id = "p|a", Title = "t" }, Ct);
        await memory.DeleteAsync("p|a", Ct);

        Assert.NotEmpty(SeamProbedEntity.ConstructorStacks);
        Assert.DoesNotContain(SeamProbedEntity.ConstructorStacks, stack => stack.Contains(".cctor", StringComparison.Ordinal));
    }

    // ── 5.7: the protected comparison (s2) with the per-content turn (b1) ─────────────────────────

    [Fact]
    public async Task ComparingAProtectedContent_NeverReadsTheCallersStream_AndNeverWaitsForAWriteThatHoldsTheContent()
    {
        var source = new CountingStream(RandomBytes(10_000));
        var unprepared = TableBlobContent.FromStream(source, "application/octet-stream");
        var sealedContent = TableBlobContent.FromBytes(RandomBytes(10_000, seed: 3), "application/octet-stream");

        // Another write holds both contents (it is reading or uploading them).
        await unprepared.WaitTurnAsync(Ct);
        await sealedContent.WaitTurnAsync(Ct);
        try
        {
            var compare = Task.Run(() => (
                ProtectedLargeValues.IsUnchanged(sealedContent, unprepared),
                ProtectedLargeValues.IsUnchanged(sealedContent, sealedContent),
                ProtectedLargeValues.DescribeRefusedChange(sealedContent, unprepared)), Ct);
            var (changed, same, message) = await compare.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            Assert.False(changed);
            Assert.True(same);
            Assert.Contains("whose length is not known yet", message, StringComparison.Ordinal);
            Assert.Equal(0, source.Reads);
        }
        finally
        {
            unprepared.EndTurn();
            sealedContent.EndTurn();
        }
    }

    public sealed class SeamDeed : Entity
    {
        [ProtectedProperty("legal")]
        public TableBlobContent? Scan { get; set; }

        public string? Title { get; set; }
    }

    [Fact]
    public async Task AWholeEntityUpdate_WithAProtectedContentAnotherWriteHolds_IsRefusedAtOnce_WithoutReadingIt()
    {
        var store = new InMemoryStorage<SeamDeed>(null, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });
        var stored = await store.CreateAsync(new SeamDeed { Id = "p|d", Scan = TableBlobContent.FromBytes(RandomBytes(2_000), "application/pdf") }, Ct);
        var source = new CountingStream(RandomBytes(10_000, seed: 4));
        var replacement = TableBlobContent.FromStream(source, "application/pdf");
        await replacement.WaitTurnAsync(Ct); // another write holds the new content
        try
        {
            var update = store.UpdateAsync(new SeamDeed { Id = stored.Id, ETag = stored.ETag, Scan = replacement }, Ct);
            var refused = await Record.ExceptionAsync(() => update.WaitAsync(TimeSpan.FromSeconds(5), Ct));

            Assert.IsType<UnauthorizedAccessException>(refused);
            Assert.Equal(0, source.Reads);
        }
        finally
        {
            replacement.EndTurn();
        }
    }

    /// <summary>A seekable stream that counts the reads made of it.</summary>
    internal sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref reads);
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Interlocked.Increment(ref reads);
            return base.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref reads);
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}

/// <summary>The seams of <see cref="MergedLaneSeamTests"/> that need the service: TableStorage over Azurite.</summary>
public sealed class AzuriteMergedLaneSeamTests : AzuriteContentTestBase
{
    public sealed class SeamPlainNote : Entity
    {
        public string? Title { get; set; }

        public string? Body { get; set; }
    }

    // ── 5.1: ExistsAsync of a plain type (s1 and r both touched the decision) ─────────────────────

    [Theory]
    [InlineData("none", null)]
    [InlineData("Body__len", 4L)]
    [InlineData("Body__enc", "utf-8")]
    [InlineData("Body__chunks", 1)]
    [InlineData("Body__blob", "container/notes/r.txt")]
    [InlineData("Body__c0", "a cached copy")]
    public async Task PlainExists_CachesARowWithALookalikeColumn_AsOn083(string column, object? value)
    {
        Assert.SkipUnless(Available, SkipReason);
        var store = Store<SeamPlainNote>();
        await store.EnsureCreatedAsync(Ct);
        var table = TableService.GetTableClient(nameof(SeamPlainNote));
        var row = new TableEntity(Pk, "r") { ["Id"] = Id("r"), ["Title"] = "t", ["Body"] = "text" };
        if (value is not null) row[column] = value;
        await table.UpsertEntityAsync(row, TableUpdateMode.Replace, Ct);

        Assert.True(await store.ExistsAsync(Id("r"), Ct));
        // 0.8.3 cached the entity: with the row gone behind the store's back, the next read is answered from the cache.
        await table.DeleteEntityAsync(Pk, "r", ETag.All, Ct);
        var read = await store.OneAsync(Id("r"), Ct);

        Assert.NotNull(read);
        Assert.Equal("t", read.Title);
        Assert.Equal("text", read.Body);
    }

    [Fact]
    public async Task PlainExists_OfARowWithAGenuineLargeValue_AnswersTrue_AndCachesNothing()
    {
        Assert.SkipUnless(Available, SkipReason);
        var store = Store<SeamPlainNote>();
        await store.EnsureCreatedAsync(Ct);
        var table = TableService.GetTableClient(nameof(SeamPlainNote));
        var reference = new BlobReference(nameof(SeamPlainNote) + "/" + Pk + "/r/Body/" + new string('a', 64), new string('a', 64), 5, "text/plain; charset=utf-8", "text");
        await table.UpsertEntityAsync(new TableEntity(Pk, "r") { ["Id"] = Id("r"), ["Title"] = "t", ["Body__blob"] = reference.ToJson() }, TableUpdateMode.Replace, Ct);

        Assert.True(await store.ExistsAsync(Id("r"), Ct));
        await table.DeleteEntityAsync(Pk, "r", ETag.All, Ct);
        Assert.Null(await store.OneAsync(Id("r"), Ct));
    }

    // ── 5.4: the eviction after a missing blob (b2) with the rewritten cache section (c1) ─────────

    private static async Task<byte[]> ReadFileAsync(TableBlobContent content)
    {
        await using var stream = await content.OpenReadAsync(Ct);
        return await ReadAllAsync(stream);
    }

    private async Task PastTheNewestBlobAsync()
    {
        var blobs = await BlobsAsync<FileEntity>();
        if (blobs.Count == 0) return;
        var newest = blobs.Max(b => b.Properties.LastModified!.Value);
        while ((await Container.GetPropertiesAsync(cancellationToken: Ct)).GetRawResponse().Headers.Date <= newest)
            await Task.Delay(200, Ct);
    }

    [Fact]
    public async Task AfterALazyOpenFindsItsBlobMissing_TheNextReadGoesToTheTable_AndAWriteThatCompletedMeanwhileIsNotLost()
    {
        Assert.SkipUnless(Available, SkipReason);
        var id = Id("seam");
        var instanceA = Store<FileEntity>(Offload(TimeSpan.Zero));
        var instanceB = Store<FileEntity>(Offload(TimeSpan.Zero));
        await instanceB.CreateAsync(new FileEntity { Id = id, Title = "t", File = TableBlobContent.FromBytes(RandomBytes(5_000, seed: 1), "application/octet-stream") }, Ct);
        var stale = await instanceA.OneAsync(id, Ct); // cached by A; its content streams the first blob

        // Another instance replaces the file (the first blob is deleted); then A writes its own new file.
        await PastTheNewestBlobAsync();
        await instanceB.UpsertAsync(new FileEntity { Id = id, Title = "t", File = TableBlobContent.FromBytes(RandomBytes(5_000, seed: 2), "application/octet-stream") }, Ct);
        var mine = RandomBytes(5_000, seed: 3);
        await instanceA.UpsertAsync(new FileEntity { Id = id, Title = "t", File = TableBlobContent.FromBytes(mine, "application/octet-stream") }, Ct);

        // The stale content finds its blob missing: A stops answering the row from its cache.
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => ReadFileAsync(stale!.File!));
        // B changes the row behind A's back (Title only): only a read of the table sees it.
        await instanceB.UpdateAsync(id, b => b.SetProperty(x => x.Title, "changed by B"), Ct);

        var again = await instanceA.OneAsync(id, Ct);
        Assert.Equal("changed by B", again!.Title);
        Assert.Equal(mine, await ReadFileAsync(again.File!));
    }
}

/// <summary>5.5: a write retried internally (a lost race) and then successful stays cached under its final version.</summary>
public sealed class AzuriteRetriedWriteCacheSeamTests : AzuriteCleanupTestBase
{
    /// <summary>Runs an action once, just before the first table write sent after <see cref="Arm"/>.</summary>
    private sealed class BeforeFirstWrite(Func<Task> action) : HttpPipelinePolicy
    {
        private int armed;
        private int writes;

        public int Writes => Volatile.Read(ref writes);

        public void Arm() => Interlocked.Exchange(ref armed, 1);

        private static bool IsWrite(HttpMessage message) =>
            message.Request.Method != RequestMethod.Get && !message.Request.Uri.Path.EndsWith("/Tables", StringComparison.Ordinal);

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (IsWrite(message) && Volatile.Read(ref armed) != 0)
            {
                Interlocked.Increment(ref writes);
                if (Interlocked.CompareExchange(ref armed, 2, 1) == 1)
                    await action();
            }
            await ProcessNextAsync(message, pipeline);
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (IsWrite(message) && Volatile.Read(ref armed) != 0)
            {
                Interlocked.Increment(ref writes);
                if (Interlocked.CompareExchange(ref armed, 2, 1) == 1)
                    action().GetAwaiter().GetResult();
            }
            ProcessNext(message, pipeline);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWriteThatLostARaceAndWasRetried_LeavesItsFinalVersionCached(bool largeValue)
    {
        SkipUnlessAvailable();
        var id = Id("race");
        var other = Store<SplitTextEntity>(); // another instance: no cache
        var policy = new BeforeFirstWrite(() => other.UpsertAsync(new SplitTextEntity { Id = id, Title = "other", Body = "o" }, Ct));
        _ = Table(nameof(SplitTextEntity));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TableStorage<SplitTextEntity>(TableServiceWith(policy), cache, NullLogger<TableStorage<SplitTextEntity>>.Instance,
            authorizer: null, new UnifiedTableStorageOptions(), blobServiceClient: null);
        await store.CreateAsync(new SplitTextEntity { Id = id, Title = "first", Body = "b" }, Ct);
        var body = largeValue ? new string('x', 200_000) : "final";

        policy.Arm();
        var written = await store.UpsertAsync(new SplitTextEntity { Id = id, Title = "final", Body = body }, Ct);

        Assert.True(policy.Writes >= 2, $"the write was not retried ({policy.Writes} write request(s))");
        var current = (await Table(nameof(SplitTextEntity)).GetEntityAsync<TableEntity>(Pk, "race", cancellationToken: Ct)).Value;
        Assert.Equal("final", current["Title"]);
        Assert.Equal(current.ETag.ToString(), written.ETag);
        // Cached under its final version: a change behind the store's back is not seen, and the ETag is the table's.
        await other.UpdateAsync(id, b => b.SetProperty(x => x.Title, "later"), Ct);
        var read = await store.OneAsync(id, Ct);
        Assert.Equal("final", read!.Title);
        Assert.Equal(body, read.Body);
        Assert.Equal(current.ETag.ToString(), read.ETag);
    }
}
