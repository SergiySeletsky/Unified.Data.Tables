using System.Linq.Expressions;
using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// <see cref="IStorage{T}.SweepOrphanBlobsAsync"/>: deletes the blobs of a partition that no row
/// references and that are older than the given age, each on condition that it was not modified since
/// it was listed, and never touches a row.
/// </summary>
public class SweepOrphanBlobsTests
{
    private const string Pk = "p";
    private const string Rk = "r";
    private const string Id = "p|r";

    private static readonly string BigText = Ascii(40_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UnifiedTableStorageOptions Offload() => new()
    {
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 },
    };

    private static string Name(string table, string pk, string rk, string column, string sha) =>
        $"{Uri.EscapeDataString(table)}/{Uri.EscapeDataString(pk)}/{Uri.EscapeDataString(rk)}/{Uri.EscapeDataString(column)}/{sha}";

    private static string Seed(InMemoryOffloadBlobStore blobs, string name, TimeSpan age)
    {
        var bytes = Utf8(name);
        blobs.Seed(name, bytes, Sha(bytes), DateTimeOffset.UtcNow - age);
        return name;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(InMemoryOffloadBlobStore? inner = null, UnifiedTableStorageOptions? options = null, bool withBlobs = true)
        {
            Blobs = new RecordingBlobStore(Log, inner);
            H = new StorageHarness<SplitTextEntity>(options: options ?? Offload(), blobs: withBlobs ? Blobs : null);
            H.Table.When(t => t.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()))
                .Do(c =>
                {
                    lock (Log) Log.Add("table:query");
                    Selects.Add(c.ArgAt<IEnumerable<string>?>(2)?.ToList());
                    Filters.Add(c.ArgAt<string>(0));
                });
            H.SetupTransaction();
            H.SetupDelete();
        }

        public List<string> Log { get; } = [];

        public List<List<string>?> Selects { get; } = [];

        public List<string> Filters { get; } = [];

        public RecordingBlobStore Blobs { get; }

        public InMemoryOffloadBlobStore Inner => Blobs.Inner;

        public StorageHarness<SplitTextEntity> H { get; }

        public TableStorage<SplitTextEntity> Store => H.Store;

        public void Rows(params TableEntity[] rows) => H.SetupQueryByFilter(rows);

        public void AssertNoRowWrites()
        {
            H.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
            H.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
            H.Table.DidNotReceiveWithAnyArgs().AddEntityAsync<TableEntity>(default!, default);
            H.Table.DidNotReceiveWithAnyArgs().UpdateEntityAsync<TableEntity>(default!, default, default, default);
            H.Table.DidNotReceiveWithAnyArgs().UpsertEntityAsync<TableEntity>(default!, default, default);
        }

        public void AssertNoTableRead()
        {
            Assert.DoesNotContain(H.Table.ReceivedCalls(), c => c.GetMethodInfo().Name is "QueryAsync" or "GetEntityIfExistsAsync" or "GetEntityAsync");
        }

        public void Dispose() => H.Dispose();
    }

    /// <summary>The row a create of a split text value writes (its blob left in <paramref name="inner"/>, aged).</summary>
    private static async Task<TableEntity> WrittenRow(InMemoryOffloadBlobStore inner, string text, TimeSpan age)
    {
        var written = new List<TableEntity>();
        using var harness = new StorageHarness<SplitTextEntity>(options: Offload(), blobs: inner);
        harness.Table.When(t => t.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>()))
            .Do(c => written.Add(c.Arg<TableEntity>()));
        harness.SetupAdd("W/\"v1\"");
        await harness.Store.CreateAsync(new SplitTextEntity { Id = Id, Body = text }, Ct);
        var row = Assert.Single(written);
        var path = BlobReference.Parse("Body", (string)row["Body__blob"]).Path;
        await using var stream = await inner.OpenReadAsync("Body", path, CancellationToken.None);
        var bytes = await ReadAllAsync(stream);
        inner.Seed(path, bytes, Sha(bytes), DateTimeOffset.UtcNow - age);
        return row;
    }

    // ── TableStorage ──────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_CrashBeforeEntityWrite_DeletesOrphan_AfterGrace()
    {
        using var rig = new Rig();
        var orphan = Seed(rig.Inner, Name("SplitTextEntity", Pk, Rk, "Body", "aa"), TimeSpan.FromMinutes(10));
        rig.Rows();

        Assert.Equal(1, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.DoesNotContain(orphan, rig.Inner.Names);
        rig.AssertNoRowWrites();
    }

    [Fact]
    public async Task Sweep_InFlightWrite_WithinGrace_IsKept()
    {
        using var rig = new Rig();
        var young = Seed(rig.Inner, Name("SplitTextEntity", Pk, Rk, "Body", "aa"), TimeSpan.FromSeconds(10));
        rig.Rows();

        Assert.Equal(0, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.Contains(young, rig.Inner.Names);
        Assert.Equal(0, rig.Blobs.Count("delete"));
    }

    [Fact]
    public async Task Sweep_KeepsReferencedBlob_DeletesReplacedOne()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, BigText, TimeSpan.FromHours(1));
        var referenced = BlobReference.Parse("Body", (string)row["Body__blob"]).Path;
        var replaced = Seed(inner, Name("SplitTextEntity", Pk, Rk, "Body", "0ld"), TimeSpan.FromHours(1));
        using var rig = new Rig(inner);
        rig.Rows(row);

        Assert.Equal(1, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.Equal([referenced], inner.Names);
        Assert.DoesNotContain(replaced, inner.Names);
        Assert.Equal([$"delete {replaced}"], rig.Blobs.Calls.Where(c => c.StartsWith("delete", StringComparison.Ordinal)));
        rig.AssertNoRowWrites();
    }

    [Fact]
    public async Task Sweep_ReadsOnlyKeysAndReferenceCells_InOneQuery_AfterListing()
    {
        var inner = new InMemoryOffloadBlobStore();
        var row = await WrittenRow(inner, BigText, TimeSpan.FromHours(1));
        Seed(inner, Name("SplitTextEntity", Pk, Rk, "Body", "0ld"), TimeSpan.FromHours(1));
        using var rig = new Rig(inner);
        rig.Rows(row);

        await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct);

        Assert.Equal(["blob:list", "table:query", "blob:delete"], rig.Log);
        var select = Assert.Single(rig.Selects);
        Assert.NotNull(select);
        Assert.Equal(["PartitionKey", "RowKey", "_SplitOf", "Body__blob"], select);
        Assert.Equal("PartitionKey eq 'p'", Assert.Single(rig.Filters));
    }

    [Fact]
    public async Task Sweep_BlobTouchedAfterListing_IsKept()
    {
        using var rig = new Rig();
        var listedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        var name = Name("SplitTextEntity", Pk, Rk, "Body", "aa");
        rig.Inner.Seed(name, [1], Sha([1]), listedAt);
        rig.Rows();
        // A concurrent write reuses the blob: it touches it after the sweep listed it, before the delete.
        rig.Blobs.BeforeDelete = n => rig.Inner.TouchAsync(n, CancellationToken.None);

        Assert.Equal(0, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.Contains(name, rig.Inner.Names);
        Assert.Equal([listedAt], rig.Blobs.DeleteConditions);
        Assert.Equal([false], rig.Blobs.DeleteResults);
    }

    [Fact]
    public async Task Sweep_OtherPartitionsBlobs_AreUntouched()
    {
        using var rig = new Rig();
        var mine = Seed(rig.Inner, Name("SplitTextEntity", Pk, Rk, "Body", "aa"), TimeSpan.FromHours(1));
        var other = Seed(rig.Inner, Name("SplitTextEntity", "other", Rk, "Body", "bb"), TimeSpan.FromHours(1));
        var longer = Seed(rig.Inner, Name("SplitTextEntity", "pp", Rk, "Body", "cc"), TimeSpan.FromHours(1));
        var otherTable = Seed(rig.Inner, Name("OtherTable", Pk, Rk, "Body", "dd"), TimeSpan.FromHours(1));
        rig.Rows();

        Assert.Equal(1, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.DoesNotContain(mine, rig.Inner.Names);
        Assert.Equal(new[] { other, longer, otherTable }.Order(StringComparer.Ordinal), rig.Inner.Names.Order(StringComparer.Ordinal));
        Assert.Contains("list SplitTextEntity/p/", rig.Blobs.Calls);
    }

    [Fact]
    public async Task Sweep_PartitionIsNormalizedLikeDeletePartition()
    {
        using var rig = new Rig();
        var name = Seed(rig.Inner, Name("SplitTextEntity", "my-part", Rk, "Body", "aa"), TimeSpan.FromHours(1));
        rig.Rows();

        Assert.Equal(1, await rig.Store.SweepOrphanBlobsAsync(" My Part ", TimeSpan.FromMinutes(5), Ct));

        Assert.DoesNotContain(name, rig.Inner.Names);
        Assert.Contains("list SplitTextEntity/my-part/", rig.Blobs.Calls);
    }

    [Fact]
    public async Task Sweep_NoCandidates_DoesNotReadTheTable()
    {
        using var rig = new Rig();
        Seed(rig.Inner, Name("SplitTextEntity", Pk, Rk, "Body", "aa"), TimeSpan.FromSeconds(10));
        rig.Rows();

        Assert.Equal(0, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        rig.AssertNoTableRead();
        Assert.Equal(["blob:list"], rig.Log);
    }

    [Fact]
    public async Task Sweep_ReferenceInAColumnTheTypeDoesNotDeclare_IsKept()
    {
        // Another entity type sharing the table (or a runtime subtype of a member) references a blob in a
        // column SplitTextEntity does not declare: the sweep still reads that cell and keeps the blob.
        using var rig = new Rig();
        var foreign = Seed(rig.Inner, Name("SplitTextEntity", Pk, "x", "Inner_Doc", "aa"), TimeSpan.FromHours(1));
        var row = new TableEntity(Pk, "x")
        {
            ["Inner_Doc__blob"] = new BlobReference(foreign, "aa", 1, "application/octet-stream", "binary").ToJson(),
        };
        rig.Rows(row);

        Assert.Equal(0, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.Contains(foreign, rig.Inner.Names);
        var select = Assert.Single(rig.Selects);
        Assert.NotNull(select);
        Assert.Contains("Inner_Doc__blob", select);
        Assert.Contains("Body__blob", select);
    }

    [Fact]
    public async Task Sweep_MalformedReference_KeepsTheBlobsOfThatCell()
    {
        using var rig = new Rig();
        var guarded = Seed(rig.Inner, Name("SplitTextEntity", Pk, Rk, "Body", "aa"), TimeSpan.FromHours(1));
        var orphan = Seed(rig.Inner, Name("SplitTextEntity", Pk, "gone", "Body", "bb"), TimeSpan.FromHours(1));
        rig.Rows(new TableEntity(Pk, Rk) { ["Body__blob"] = "{not json" });

        Assert.Equal(1, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.Equal([guarded], rig.Inner.Names);
        Assert.DoesNotContain(orphan, rig.Inner.Names);
    }

    [Fact]
    public async Task Sweep_AbsentReferenceCell_ComesBackNull_AndItsOrphanIsDeleted()
    {
        // Azure returns a selected property the row does not have as null: the row's value was replaced by
        // an inline one, so the blob it once referenced is an orphan, not a guarded cell.
        using var rig = new Rig();
        var orphan = Seed(rig.Inner, Name("SplitTextEntity", Pk, Rk, "Body", "aa"), TimeSpan.FromHours(1));
        rig.Rows(new TableEntity(Pk, Rk) { ["Body__blob"] = null, ["_SplitOf"] = null });

        Assert.Equal(1, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.DoesNotContain(orphan, rig.Inner.Names);
    }

    [Fact]
    public async Task Sweep_NonStringReferenceCell_KeepsTheBlobsOfThatCell()
    {
        using var rig = new Rig();
        var guarded = Seed(rig.Inner, Name("SplitTextEntity", Pk, Rk, "Body", "aa"), TimeSpan.FromHours(1));
        var sibling = Seed(rig.Inner, Name("SplitTextEntity", Pk, "other", "Body", "bb"), TimeSpan.FromHours(1));
        rig.Rows(new TableEntity(Pk, Rk) { ["Body__blob"] = 42 }, new TableEntity(Pk, "other") { ["Body__blob"] = null });

        Assert.Equal(1, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.Equal([guarded], rig.Inner.Names);
        Assert.DoesNotContain(sibling, rig.Inner.Names);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Sweep_SelectListLongerThanTheLimit_ReadsWholeRows(int overLimit, bool projected)
    {
        // Reference columns sized so the select list is exactly at the limit, or one character over it.
        var target = ValueOffloadCoordinator.MaxSweepSelectLength + overLimit;
        var columns = new List<string>();
        var length = ValueOffloadCoordinator.SelectLength(["PartitionKey", "RowKey", "_SplitOf", "Body__blob"]);
        string Column(int len) => ("C" + columns.Count.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).PadRight(len, 'x');
        while (target - length > 258)
        {
            columns.Add(Column(200));
            length += 3 + 200 + SplitCodec.BlobSuffix.Length;
        }
        columns.Add(Column(target - length - 3 - SplitCodec.BlobSuffix.Length));

        using var rig = new Rig();
        foreach (var column in columns)
            Seed(rig.Inner, Name("SplitTextEntity", Pk, Rk, column, "aa"), TimeSpan.FromHours(1));
        rig.Rows();

        Assert.Equal(columns.Count, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        var select = Assert.Single(rig.Selects);
        if (projected)
        {
            Assert.NotNull(select);
            Assert.Equal(target, ValueOffloadCoordinator.SelectLength(select));
        }
        else
        {
            Assert.Null(select);
        }
    }

    [Fact]
    public async Task Sweep_UnrecognizedBlobName_SelectsEveryColumn()
    {
        // A blob under the prefix whose name does not follow {table}/{pk}/{rk}/{column}/{sha} cannot say
        // which cell would reference it, so the sweep reads whole rows rather than guess a projection.
        using var rig = new Rig();
        var odd = Seed(rig.Inner, "SplitTextEntity/p/stray", TimeSpan.FromHours(1));
        rig.Rows();

        Assert.Equal(1, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.DoesNotContain(odd, rig.Inner.Names);
        Assert.Null(Assert.Single(rig.Selects));
    }

    [Fact]
    public async Task Sweep_NoOffloadConfigured_ReturnsZero()
    {
        using var rig = new Rig(options: new UnifiedTableStorageOptions(), withBlobs: false);

        Assert.Equal(0, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        rig.AssertNoTableRead();
    }

    [Fact]
    public async Task Sweep_NegativeOlderThan_Throws()
    {
        using var rig = new Rig();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromTicks(-1), Ct));
        Assert.Empty(rig.Log);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sweep_BlankPartition_Throws(string? partition)
    {
        using var rig = new Rig();

        await Assert.ThrowsAsync<ArgumentNullException>(() => rig.Store.SweepOrphanBlobsAsync(partition!, TimeSpan.Zero, Ct));
        Assert.Empty(rig.Log);
    }

    [Fact]
    public async Task Sweep_HugeOlderThan_DeletesNothing()
    {
        using var rig = new Rig();
        var old = Seed(rig.Inner, Name("SplitTextEntity", Pk, Rk, "Body", "aa"), TimeSpan.FromDays(3650));
        rig.Rows();

        Assert.Equal(0, await rig.Store.SweepOrphanBlobsAsync(Pk, TimeSpan.MaxValue, Ct));

        Assert.Contains(old, rig.Inner.Names);
    }

    // ── InMemoryStorage ───────────────────────────────────────────────────

    private static InMemoryStorage<SplitTextEntity> Memory(bool offload = true) =>
        new(null, offload ? Offload() : new UnifiedTableStorageOptions());

    [Fact]
    public async Task InMemory_Sweep_CrashBeforeEntityWrite_DeletesOrphan_AfterGrace()
    {
        var store = Memory();
        var orphan = Seed(store.Blobs, Name("SplitTextEntity", Pk, Rk, "Body", "aa"), TimeSpan.FromMinutes(10));

        Assert.Equal(1, await store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.DoesNotContain(orphan, store.Blobs.Names);
    }

    [Fact]
    public async Task InMemory_Sweep_InFlightWrite_WithinGrace_IsKept()
    {
        var store = Memory();
        var young = Seed(store.Blobs, Name("SplitTextEntity", Pk, Rk, "Body", "aa"), TimeSpan.FromSeconds(10));

        Assert.Equal(0, await store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.Contains(young, store.Blobs.Names);
    }

    [Fact]
    public async Task InMemory_Sweep_KeepsReferencedBlob_DeletesReplacedOne()
    {
        var store = Memory();
        await store.CreateAsync(new SplitTextEntity { Id = Id, Body = BigText }, Ct);
        var referenced = Assert.Single(store.Blobs.Names);
        await using (var stream = await store.Blobs.OpenReadAsync("Body", referenced, CancellationToken.None))
        {
            var bytes = await ReadAllAsync(stream);
            store.Blobs.Seed(referenced, bytes, Sha(bytes), DateTimeOffset.UtcNow.AddHours(-1));
        }
        var replaced = Seed(store.Blobs, Name("SplitTextEntity", Pk, Rk, "Body", "0ld"), TimeSpan.FromHours(1));

        Assert.Equal(1, await store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));

        Assert.Equal([referenced], store.Blobs.Names);
        Assert.DoesNotContain(replaced, store.Blobs.Names);
        Assert.Equal(BigText, (await store.OneAsync(Id, Ct))!.Body);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task InMemory_Sweep_OtherPartitionsBlobs_AreUntouched()
    {
        var store = Memory();
        var mine = Seed(store.Blobs, Name("SplitTextEntity", Pk, Rk, "Body", "aa"), TimeSpan.FromHours(1));
        var other = Seed(store.Blobs, Name("SplitTextEntity", "other", Rk, "Body", "bb"), TimeSpan.FromHours(1));

        Assert.Equal(1, await store.SweepOrphanBlobsAsync(" P ", TimeSpan.FromMinutes(5), Ct));

        Assert.Equal([other], store.Blobs.Names);
        Assert.DoesNotContain(mine, store.Blobs.Names);
    }

    [Fact]
    public async Task InMemory_Sweep_NoOffloadConfigured_ReturnsZero()
    {
        var store = Memory(offload: false);

        Assert.Equal(0, await store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromMinutes(5), Ct));
    }

    [Fact]
    public async Task InMemory_Sweep_InvalidArguments_Throw()
    {
        var store = Memory();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SweepOrphanBlobsAsync(Pk, TimeSpan.FromTicks(-1), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SweepOrphanBlobsAsync(" ", TimeSpan.Zero, Ct));
    }

    // ── default interface member ──────────────────────────────────────────

    [Fact]
    public async Task Sweep_PlainImplementer_DefaultThrowsNotSupported()
    {
        IStorage<SplitTextEntity> storage = new PlainStorage();

        await Assert.ThrowsAsync<NotSupportedException>(() => storage.SweepOrphanBlobsAsync(Pk, TimeSpan.Zero, Ct));
    }

    /// <summary>An implementer written before the member existed: it does not implement it.</summary>
    private sealed class PlainStorage : IStorage<SplitTextEntity>
    {
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SplitTextEntity> CreateAsync(SplitTextEntity entity, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SplitTextEntity> UpsertAsync(SplitTextEntity entity, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SplitTextEntity> UpdateAsync(SplitTextEntity entity, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SplitTextEntity> UpdateAsync(SplitTextEntity entity, ConcurrencyMode mode, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> UpdateAsync(string id, Action<UpdateBuilder<SplitTextEntity>> builderAction, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SplitTextEntity?> OneAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ExistsAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IEnumerable<SplitTextEntity>> QueryAsync(string? partition = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SplitTextEntity>> QueryAsync(QueryOptions options, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SplitTextEntity>> QueryAsync(Expression<Func<SplitTextEntity, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<SplitTextEntity> QueryStreamAsync(QueryOptions? options = null, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<SplitTextEntity> QueryStreamAsync(Expression<Func<SplitTextEntity, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EntityPage<SplitTextEntity>> QueryPageAsync(QueryOptions options, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> AnyAsync(Expression<Func<SplitTextEntity, bool>> predicate, string? partition = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateBatchAsync(IReadOnlyCollection<SplitTextEntity> entities, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> UpsertBatchAsync(IReadOnlyCollection<SplitTextEntity> entities, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CountAsync(string? partition = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
