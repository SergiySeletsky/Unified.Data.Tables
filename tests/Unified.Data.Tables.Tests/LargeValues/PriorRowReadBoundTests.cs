using Azure;
using Unified.Data.Tables.InMemory;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The point read that precedes an upsert, an update, a delete and a batch upsert of an existing row names
/// the part cells and one <c>{column}__blob</c> cell per large-value column of the type. However many
/// large-value columns a type declares, that read stays one the service accepts: beyond the orphan sweep's
/// bounds (<c>MaxSweepSelectColumns</c>, <c>MaxSweepSelectLength</c>) the row is read whole, which also
/// keeps it within the 255 properties the service projects and the emulator's URL limit. What the write
/// learns from the read (version, continuation rows, blobs) is the same either way, and a type below the
/// bound sends the read it sent before (COMPLETENESS-2, SERIALIZER-8).
/// </summary>
public class PriorRowReadBoundTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── a document-shaped entity: sections of blocks of texts, every text opted in ──

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class ReportBlock
    {
        public string? Heading { get; set; }
        public string? Summary { get; set; }
        public string? Body { get; set; }
        public string? Footnote { get; set; }
        public string? Comment { get; set; }
    }

    public sealed class ReportSection
    {
        public ReportBlock? Introduction { get; set; }
        public ReportBlock? Background { get; set; }
        public ReportBlock? Method { get; set; }
        public ReportBlock? Findings { get; set; }
        public ReportBlock? Discussion { get; set; }
        public ReportBlock? Conclusion { get; set; }
        public ReportBlock? Appendix { get; set; }
        public ReportBlock? References { get; set; }
    }

    /// <summary>Fifteen sections: 600 large-value columns.</summary>
    public sealed class WideReport : Entity
    {
        public string? Title { get; set; }
        public ReportSection? Executive { get; set; }
        public ReportSection? Technical { get; set; }
        public ReportSection? Financial { get; set; }
        public ReportSection? Legal { get; set; }
        public ReportSection? Operations { get; set; }
        public ReportSection? Marketing { get; set; }
        public ReportSection? Research { get; set; }
        public ReportSection? Security { get; set; }
        public ReportSection? Compliance { get; set; }
        public ReportSection? Engineering { get; set; }
        public ReportSection? Support { get; set; }
        public ReportSection? Training { get; set; }
        public ReportSection? Logistics { get; set; }
        public ReportSection? Procurement { get; set; }
        public ReportSection? Governance { get; set; }
    }

    /// <summary>Eight sections: 320 large-value columns. More than a projection may name, fewer than the emulator's URL limit.</summary>
    public sealed class MediumReport : Entity
    {
        public string? Title { get; set; }
        public ReportSection? Executive { get; set; }
        public ReportSection? Technical { get; set; }
        public ReportSection? Financial { get; set; }
        public ReportSection? Legal { get; set; }
        public ReportSection? Operations { get; set; }
        public ReportSection? Marketing { get; set; }
        public ReportSection? Research { get; set; }
        public ReportSection? Security { get; set; }
    }

    /// <summary>Five sections: 200 large-value columns.</summary>
    public sealed class SmallReport : Entity
    {
        public string? Title { get; set; }
        public ReportSection? Executive { get; set; }
        public ReportSection? Technical { get; set; }
        public ReportSection? Financial { get; set; }
        public ReportSection? Legal { get; set; }
        public ReportSection? Operations { get; set; }
    }

    private static ReportSection Section(string body) => new() { Findings = new ReportBlock { Heading = "h", Body = body } };

    private static WideReport Report(string id, string title, string body) => new() { Id = id, Title = title, Technical = Section(body) };

    private static string Describe(Exception? ex) => ex switch
    {
        null => "ok",
        RequestFailedException failed => "RequestFailedException " + failed.Status,
        _ => ex.GetType().Name,
    };

    [Fact]
    public void ReportType_ListsSixHundredLargeValueColumns()
    {
        var columns = TypeMetadataCache.LargeValueColumns(typeof(WideReport));

        Assert.Equal(15 * 8 * 5, columns.Count);
        Assert.Contains("Technical_Findings_Body", columns);
    }

    // ── what is sent ──

    /// <summary>Two large-value columns: far below the bound.</summary>
    public sealed class TwoTexts : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    // A type below the bound sends the read it sent before: the part cells and every __blob cell, no odata.etag.
    [Fact]
    public async Task PriorRowRead_OfATypeBelowTheBound_SelectsThePartCellsAndEveryBlobCell_WithoutTheVersion()
    {
        using var rig = new WriteBatchRig<TwoTexts>();
        await rig.Store.CreateAsync(new TwoTexts { Id = "p|r", Body = Ascii(40_000) }, Ct);

        await rig.Store.UpsertAsync(new TwoTexts { Id = "p|r", Body = Ascii(40_000), Notes = "n" }, Ct);
        await rig.Store.DeleteAsync("p|r", Ct);

        string[] expected = [SystemColumnNames.SplitParts, SystemColumnNames.SplitOf, "Body__blob", "Notes__blob"];
        var reads = rig.Table.Requests.Where(r => r.Operation == "get").ToList();
        Assert.Equal(2, reads.Count);
        Assert.All(reads, read => Assert.Equal(expected, read.Select));
    }

    // Above the bound the row is read whole: never more than the 255 properties the service projects
    // ("Query Entities": "The maximum number of returned properties is 255"), never a URL beyond the sweep's bound.
    [Theory]
    [InlineData("medium")]
    [InlineData("wide")]
    [InlineData("dense")]
    public async Task PriorRowRead_OfATypeAboveTheBound_ReadsTheRowWhole_NeverMoreThan255Properties(string type)
    {
        IReadOnlyList<TableRequest> requests;
        switch (type)
        {
            case "medium":
            {
                using var rig = new WriteBatchRig<MediumReport>();
                await rig.Store.CreateAsync(new MediumReport { Id = "p|r", Title = "created", Technical = Section(Ascii(40_000)) }, Ct);
                await rig.Store.UpsertAsync(new MediumReport { Id = "p|r", Title = "upserted", Technical = Section(Ascii(40_000)) }, Ct);
                await rig.Store.DeleteAsync("p|r", Ct);
                requests = rig.Table.Requests;
                break;
            }
            case "wide":
            {
                using var rig = new WriteBatchRig<WideReport>();
                await rig.Store.CreateAsync(Report("p|r", "created", Ascii(40_000)), Ct);
                await rig.Store.UpsertBatchAsync([Report("p|r", "batch", Ascii(40_000))], Ct);
                await rig.Store.DeleteAsync("p|r", Ct);
                requests = rig.Table.Requests;
                break;
            }
            default:
            {
                using var rig = new WriteBatchRig<DenseDoc<DenseSplitLeaf>>();
                DenseDoc<DenseSplitLeaf> Make(string name) => new()
                {
                    Id = "p|r",
                    Root = new Dense01<DenseSplitLeaf> { Name = name, Leaf = new DenseSplitLeaf { Body = Ascii(40_000) } },
                };
                await rig.Store.CreateAsync(Make("one"), Ct);
                await rig.Store.UpsertAsync(Make("two"), Ct);
                requests = rig.Table.Requests;
                break;
            }
        }

        var reads = requests.Where(r => r.Operation == "get").ToList();
        Assert.NotEmpty(reads);
        Assert.All(reads, read => Assert.Null(read.Select));
    }

    // ── what the write learns from a whole row is what it learns from a projection ──

    private static BlobOffloadOptions Offload() => new() { ThresholdBytes = 1_000, CleanupGrace = TimeSpan.Zero };

    [Fact]
    public async Task ARowReadWhole_ReleasesTheBlobsItReferenced_OnAReplaceAndOnADelete()
    {
        using var rig = new WriteBatchRig<WideReport>(Offload());
        await rig.Store.CreateAsync(Report("p|r", "created", Ascii(40_000, seed: 1)), Ct);
        var first = Assert.Single(rig.Blobs.Names);

        await rig.NewStore().UpsertAsync(Report("p|r", "upserted", Ascii(40_000, seed: 2)), Ct);
        var second = Assert.Single(rig.Blobs.Names);
        Assert.NotEqual(first, second);

        await rig.NewStore().UpsertBatchAsync([Report("p|r", "batch", Ascii(40_000, seed: 3))], Ct);
        var third = Assert.Single(rig.Blobs.Names);
        Assert.NotEqual(second, third);

        await rig.NewStore().DeleteAsync("p|r", Ct);
        Assert.Empty(rig.Blobs.Names);
        Assert.Empty(rig.Table.StoredRows());
    }

    [Fact]
    public async Task ARowReadWhole_DeclaresItsContinuationRows_AShrinkingWriteAndADeleteRemoveThem()
    {
        using var rig = new WriteBatchRig<WideReport>();
        var large = Ascii(700_000);
        await rig.Store.CreateAsync(Report("p|r", "created", large), Ct);
        Assert.True(rig.Table.StoredRows().Count > 1, "The value did not need continuation rows.");
        Assert.Equal(large, (await rig.NewStore().OneAsync("p|r", Ct))!.Technical!.Findings!.Body, StringComparer.Ordinal);

        await rig.NewStore().UpsertAsync(Report("p|r", "small", "short"), Ct);
        Assert.Single(rig.Table.StoredRows());
        Assert.Equal("short", (await rig.NewStore().OneAsync("p|r", Ct))!.Technical!.Findings!.Body);

        await rig.NewStore().UpsertAsync(Report("p|r", "large again", large), Ct);
        Assert.True(rig.Table.StoredRows().Count > 1);
        await rig.NewStore().DeleteAsync("p|r", Ct);
        Assert.Empty(rig.Table.StoredRows());
    }

    [Fact]
    public async Task ARowReadWhole_IsReplacedOnConditionOfItsVersion()
    {
        using var rig = new WriteBatchRig<WideReport>();
        await rig.Store.CreateAsync(Report("p|r", "created", Ascii(40_000)), Ct);
        var read = (await rig.NewStore().OneAsync("p|r", Ct))!;
        await rig.NewStore().UpsertAsync(Report("p|r", "another writer", Ascii(40_000)), Ct);

        read.Title = "stale";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => rig.NewStore().UpdateAsync(read, Ct));

        Assert.Equal("another writer", (await rig.NewStore().OneAsync("p|r", Ct))!.Title);
    }

    // ── in memory: the same sequences ──

    [Fact]
    public async Task InMemory_EntityOfATypeWithManyLargeValueColumns_CanBeSavedAgainAndDeleted()
    {
        var store = new InMemoryStorage<WideReport>();
        var body = Ascii(40_000);

        await store.CreateAsync(Report("p|r", "created", body), Ct);
        var read = (await store.OneAsync("p|r", Ct))!;
        read.Title = "updated";
        await store.UpdateAsync(read, Ct);
        await store.UpsertAsync(Report("p|r", "upserted", body), Ct);
        await store.UpsertBatchAsync([Report("p|r", "batch", body)], Ct);
        Assert.Equal("batch", (await store.OneAsync("p|r", Ct))!.Title);
        await store.DeleteAsync("p|r", Ct);
        Assert.Null(await store.OneAsync("p|r", Ct));
    }

    // ── on the emulator, through the real SDK clients ──

    [Fact]
    public async Task Azurite_EntityOfATypeWithManyLargeValueColumns_CanBeSavedAgainAndDeleted()
    {
        await using var azurite = await WriteBatchAzurite.OpenAsync();
        Assert.SkipWhen(azurite is null, WriteBatchAzurite.SkipReason);
        var table = azurite!.NewTable("Report");
        var store = azurite.Store<WideReport>(table, offload: null, cache: false);
        var id = azurite.Id("r");
        var body = Ascii(40_000);

        await store.CreateAsync(Report(id, "created", body), Ct);
        var read = (await store.OneAsync(id, Ct))!;
        Assert.Equal(body, read.Technical!.Findings!.Body, StringComparer.Ordinal);

        read.Title = "updated";
        await store.UpdateAsync(read, Ct);
        await store.UpsertAsync(Report(id, "upserted", body), Ct);
        Assert.Equal("upserted", (await store.OneAsync(id, Ct))!.Title);
        await store.DeleteAsync(id, Ct);
        Assert.Null(await store.OneAsync(id, Ct));
    }

    [Fact]
    public async Task Azurite_EntityOfATypeWithManyLargeValueColumns_CanBeDeletedAndBatchUpserted()
    {
        await using var azurite = await WriteBatchAzurite.OpenAsync();
        Assert.SkipWhen(azurite is null, WriteBatchAzurite.SkipReason);
        var table = azurite!.NewTable("Stuck");
        var store = azurite.Store<WideReport>(table, offload: null, cache: false);
        var id = azurite.Id("r");
        await store.CreateAsync(Report(id, "created", Ascii(40_000)), Ct);

        var batch = await Record.ExceptionAsync(() => store.UpsertBatchAsync([Report(id, "batch", Ascii(40_000))], Ct));
        var afterBatch = (await store.OneAsync(id, Ct))?.Title;
        var delete = await Record.ExceptionAsync(() => store.DeleteAsync(id, Ct));
        var stillThere = await store.OneAsync(id, Ct);

        Assert.True(batch is null && afterBatch == "batch" && delete is null && stillThere is null,
            "UpsertBatchAsync: " + Describe(batch) + " (title afterwards '" + afterBatch + "'); DeleteAsync: " + Describe(delete)
            + "; the entity after DeleteAsync: " + (stillThere is null ? "gone" : "still stored, title '" + stillThere.Title + "'") + ".");
    }

    // An offloaded value of a type above the bound: the replace and the delete release its blobs on the emulator too.
    [Fact]
    public async Task Azurite_EntityOfATypeWithManyLargeValueColumns_ReleasesItsBlobs_OnAReplaceAndOnADelete()
    {
        await using var azurite = await WriteBatchAzurite.OpenAsync();
        Assert.SkipWhen(azurite is null, WriteBatchAzurite.SkipReason);
        var table = azurite!.NewTable("Blobs");
        var offload = azurite.Offload(TimeSpan.Zero, 1_000);
        var id = azurite.Id("r");
        await azurite.Store<WideReport>(table, offload, cache: false).CreateAsync(Report(id, "created", Ascii(40_000, seed: 1)), Ct);
        var first = Assert.Single(await azurite.BlobNamesAsync());
        await azurite.EveryBlobIsOlderThanAsync(TimeSpan.FromSeconds(1), Ct);

        await azurite.Store<WideReport>(table, offload, cache: false).UpsertAsync(Report(id, "upserted", Ascii(40_000, seed: 2)), Ct);
        var second = Assert.Single(await azurite.BlobNamesAsync());
        Assert.NotEqual(first, second);
        await azurite.EveryBlobIsOlderThanAsync(TimeSpan.FromSeconds(1), Ct);

        await azurite.Store<WideReport>(table, offload, cache: false).DeleteAsync(id, Ct);
        Assert.Empty(await azurite.BlobNamesAsync());
    }

    [Fact]
    public async Task Azurite_EntityOfATypeWithTwoHundredLargeValueColumns_CanBeSavedAgainAndDeleted()
    {
        Assert.Equal(5 * 8 * 5, TypeMetadataCache.LargeValueColumns(typeof(SmallReport)).Count);
        await using var azurite = await WriteBatchAzurite.OpenAsync();
        Assert.SkipWhen(azurite is null, WriteBatchAzurite.SkipReason);
        var table = azurite!.NewTable("Small");
        var store = azurite.Store<SmallReport>(table, offload: null, cache: false);
        var id = azurite.Id("r");
        var body = Ascii(40_000);

        await store.CreateAsync(new SmallReport { Id = id, Title = "created", Technical = Section(body) }, Ct);
        var read = (await store.OneAsync(id, Ct))!;
        read.Title = "updated";
        await store.UpdateAsync(read, Ct);
        await store.UpsertAsync(new SmallReport { Id = id, Title = "upserted", Technical = Section(body) }, Ct);
        Assert.Equal("upserted", (await store.OneAsync(id, Ct))!.Title);
        Assert.Equal(body, (await store.OneAsync(id, Ct))!.Technical!.Findings!.Body, StringComparer.Ordinal);
        await store.DeleteAsync(id, Ct);
        Assert.Null(await store.OneAsync(id, Ct));
    }

    [Fact]
    public async Task Azurite_EntityOfATypeWith320LargeValueColumns_CanBeSavedAgainAndDeleted()
    {
        Assert.Equal(8 * 8 * 5, TypeMetadataCache.LargeValueColumns(typeof(MediumReport)).Count);
        await using var azurite = await WriteBatchAzurite.OpenAsync();
        Assert.SkipWhen(azurite is null, WriteBatchAzurite.SkipReason);
        var table = azurite!.NewTable("Medium");
        var store = azurite.Store<MediumReport>(table, offload: null, cache: false);
        var id = azurite.Id("r");

        await store.CreateAsync(new MediumReport { Id = id, Title = "created", Technical = Section(Ascii(40_000)) }, Ct);
        await store.UpsertAsync(new MediumReport { Id = id, Title = "upserted", Technical = Section(Ascii(40_000)) }, Ct);

        Assert.Equal("upserted", (await store.OneAsync(id, Ct))!.Title);
        await store.DeleteAsync(id, Ct);
        Assert.Null(await store.OneAsync(id, Ct));
    }

    // The dense type graph (a generic type that reaches deeper instantiations of itself, one opted-in leaf).
    [Fact]
    public async Task Azurite_DenseGraphEntity_CanBeUpsertedUpdatedAndDeleted_AsInMemory()
    {
        static DenseDoc<DenseSplitLeaf> Make(string id, string name, int seed) => new()
        {
            Id = id,
            Root = new Dense01<DenseSplitLeaf> { Name = name, Leaf = new DenseSplitLeaf { Body = Ascii(40_000, seed) } },
        };

        var memory = new InMemoryStorage<DenseDoc<DenseSplitLeaf>>();
        await memory.CreateAsync(Make("p|r", "one", 1), Ct);
        await memory.UpsertAsync(Make("p|r", "two", 2), Ct);
        var inMemory = (await memory.OneAsync("p|r", Ct))!;
        inMemory.Root!.Name = "three";
        await memory.UpdateAsync(inMemory, Ct);
        Assert.Equal("three", (await memory.OneAsync("p|r", Ct))!.Root!.Name);
        await memory.DeleteAsync("p|r", Ct);
        Assert.Null(await memory.OneAsync("p|r", Ct));

        await using var azurite = await WriteBatchAzurite.OpenAsync();
        Assert.SkipWhen(azurite is null, WriteBatchAzurite.SkipReason);
        var table = azurite!.NewTable("Dense");
        var store = azurite.Store<DenseDoc<DenseSplitLeaf>>(table, offload: null, cache: false);
        var id = azurite.Id("r");

        await store.CreateAsync(Make(id, "one", 1), Ct);
        Assert.Equal("one", (await store.OneAsync(id, Ct))!.Root!.Name);
        var upsert = await Record.ExceptionAsync(() => store.UpsertAsync(Make(id, "two", 2), Ct));
        var read = (await store.OneAsync(id, Ct))!;
        read.Root!.Name = "three";
        var update = await Record.ExceptionAsync(() => store.UpdateAsync(read, Ct));
        var afterUpdate = (await store.OneAsync(id, Ct))?.Root?.Name;
        var delete = await Record.ExceptionAsync(() => store.DeleteAsync(id, Ct));

        Assert.True(upsert is null && update is null && afterUpdate == "three" && delete is null,
            $"upsert {Describe(upsert)}, update {Describe(update)} (name afterwards '{afterUpdate}'), delete {Describe(delete)}.");
        Assert.Null(await store.OneAsync(id, Ct));
    }
}
