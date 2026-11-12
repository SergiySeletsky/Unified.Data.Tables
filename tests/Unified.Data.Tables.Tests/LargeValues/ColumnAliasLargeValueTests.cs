using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// <see cref="ColumnAliasAttribute"/> on an opted-in property: a value stored under the legacy column is
/// read in every form it can have there (one cell, the <c>__Json</c> and <c>__GZip</c> cells of a type that
/// had not opted in, split cells with their manifest, continuation rows, an offloaded <c>__blob</c>
/// reference), on <see cref="InMemoryStorage{T}"/> and on <see cref="TableStorage{T}"/> over Azurite. A read
/// leaves the row and the blob where they are; a save through the new type stores the value under the
/// new column, removes the old cells, and releases the old blob only after the new row has committed.
/// </summary>
public class ColumnAliasLargeValueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Inline = "inline";
    private const string Cells = "cells";
    private const string Rows = "rows";
    private const string Blob = "blob";

    // ── models: the type that wrote the rows, and the type that reads them after a rename ──

    public class AliasTextV1 : Entity
    {
        public string? Title { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }
    }

    /// <summary>The property-level form: <c>[ColumnAlias("Old")]</c> on the renamed property.</summary>
    public class AliasTextV2 : Entity
    {
        public string? Title { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        [ColumnAlias("Body")]
        public string? Content { get; set; }
    }

    /// <summary>The class-level form: <c>[ColumnAlias("Property", "Old")]</c>.</summary>
    [ColumnAlias(nameof(Content), "Body")]
    public class AliasTextV2ClassLevel : Entity
    {
        public string? Title { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Content { get; set; }
    }

    public class AliasBytesV1 : Entity
    {
        public string? Title { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public byte[]? Image { get; set; }
    }

    public class AliasBytesV2 : Entity
    {
        public string? Title { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        [ColumnAlias("Image")]
        public byte[]? Picture { get; set; }
    }

    public class AliasJsonV1 : Entity
    {
        public string? Title { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public List<string>? Tags { get; set; }
    }

    public class AliasJsonV2 : Entity
    {
        public string? Title { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        [ColumnAlias("Tags")]
        public List<string>? Labels { get; set; }
    }

    public class AliasFileV1 : Entity
    {
        public string? Title { get; set; }

        public TableBlobContent? File { get; set; }
    }

    public class AliasFileV2 : Entity
    {
        public string? Title { get; set; }

        [ColumnAlias("File")]
        public TableBlobContent? Attachment { get; set; }
    }

    /// <summary>A type that had not opted in: its large values are <c>__GZip</c> and <c>__Json</c> cells.</summary>
    public class AliasPlainV1 : Entity
    {
        public string? Title { get; set; }

        public string? Body { get; set; }

        public List<string>? Tags { get; set; }
    }

    public class AliasOptedInV2 : Entity
    {
        public string? Title { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        [ColumnAlias("Body")]
        public string? Content { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        [ColumnAlias("Tags")]
        public List<string>? Labels { get; set; }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static int TextLength(string form) => form switch
    {
        Inline => 500,
        Cells => 100_000,
        Rows => 700_000,
        _ => 3_500_000,
    };

    private static int BinaryLength(string form) => form switch
    {
        Inline => 500,
        Cells => 200_000,
        Rows => 1_500_000,
        _ => 3_500_000,
    };

    private static List<string> Labels(string form) =>
        Enumerable.Range(0, form switch { Inline => 5, Cells => 1_000, Rows => 7_000, _ => 34_000 })
            .Select(i => "label-" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 88))
            .ToList();

    /// <summary>The names of the cells of <paramref name="column"/> (its own cell and every suffixed one) on any stored row.</summary>
    private static List<string> CellsOf(IReadOnlyList<TableEntity> rows, string column) =>
        rows.SelectMany(r => r.Keys)
            .Where(k => k == column || k.StartsWith(column + "__", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

    /// <summary>The stored rows hold <paramref name="column"/> in <paramref name="form"/>, and nothing of <paramref name="legacy"/>.</summary>
    private static void AssertStoredUnder(IReadOnlyList<TableEntity> rows, string column, string legacy, string form, bool json = false)
    {
        Assert.Empty(CellsOf(rows, legacy));
        var primary = rows[0];
        switch (form)
        {
            case Inline:
                Assert.Single(rows);
                Assert.Equal([json ? column + "__Json" : column], CellsOf(rows, column));
                break;
            case Cells:
                Assert.Single(rows);
                Assert.InRange(Assert.IsType<int>(primary[column + "__chunks"]), 2, 250);
                Assert.False(primary.ContainsKey(column));
                break;
            case Rows:
                Assert.InRange(rows.Count, 2, 99);
                Assert.Equal(rows.Count - 1, primary["_SplitParts"]);
                Assert.NotEmpty(LargeValueStores.ChunkKeys(rows[1], column));
                break;
            default:
                Assert.Single(rows);
                Assert.Equal([column + "__blob"], CellsOf(rows, column));
                break;
        }
    }

    /// <summary>
    /// Saves <paramref name="entity"/> through the new type and checks what the save did to storage: with
    /// an offloaded value, the blob under the new column is uploaded before the row is written and the blob
    /// under the legacy column is deleted after it; otherwise no blob is involved.
    /// </summary>
    private static async Task SaveAndAssertBlobsAsync<T>(
        LargeValueStores stores, string table, T entity, string column, string legacy, string form)
        where T : class, IEntity, new()
    {
        var before = await stores.BlobNamesAsync<T>(table);
        await stores.PastTheNewestBlobAsync<T>(table);
        var store = stores.Store<T>(table);

        var writes = await stores.StorageWritesAsync(store, () => store.UpdateAsync(entity, Ct));

        var after = await stores.BlobNamesAsync<T>(table);
        if (form != Blob)
        {
            Assert.Empty(before);
            Assert.Empty(after);
            Assert.Equal(["table WRITE"], writes);
            return;
        }

        var old = Assert.Single(before);
        var renamed = Assert.Single(after);
        Assert.StartsWith($"{table}/{stores.Pk}/r1/{legacy}/", old, StringComparison.Ordinal);
        Assert.Equal(old.Replace($"/{legacy}/", $"/{column}/", StringComparison.Ordinal), renamed);
        Assert.Equal(["blob PUT " + renamed, "table WRITE", "blob DELETE " + old], writes);
    }

    // ── string, both alias forms, every size, both stores ──────────────────

    public static TheoryData<string, string, string> StoresFormsAndAliasForms()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var store in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        foreach (var form in new[] { Inline, Cells, Rows, Blob })
        foreach (var alias in new[] { "property", "class" })
            data.Add(store, form, alias);
        return data;
    }

    [Theory]
    [MemberData(nameof(StoresFormsAndAliasForms))]
    public async Task RenamedString_IsReadFromTheLegacyColumn_AndSavedUnderTheNewOne(string store, string form, string alias)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, cleanupGrace: TimeSpan.Zero);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var body = Ascii(TextLength(form));

        if (alias == "property")
            await RenamedStringAsync<AliasTextV2>(stores!, "AliasTextProperty", form, body, e => e.Content, (e, t) => e.Title = t, e => e.Title);
        else
            await RenamedStringAsync<AliasTextV2ClassLevel>(stores!, "AliasTextClass", form, body, e => e.Content, (e, t) => e.Title = t, e => e.Title);
    }

    private static async Task RenamedStringAsync<T>(
        LargeValueStores stores, string table, string form, string body,
        Func<T, string?> content, Action<T, string> setTitle, Func<T, string?> title)
        where T : class, IEntity, new()
    {
        var id = stores.Id("r1");
        await stores.Store<AliasTextV1>(table).CreateAsync(new AliasTextV1 { Id = id, Title = "before", Body = body }, Ct);
        await stores.ShareTableAsync<AliasTextV1, T>(table);
        var legacyRows = await stores.RawRowsAsync<T>(table);
        Assert.Empty(CellsOf(legacyRows, "Content"));
        Assert.NotEmpty(CellsOf(legacyRows, "Body"));

        // Read through the renamed property: the value comes from the legacy column, whatever its form.
        var read = await stores.Store<T>(table).OneAsync(id, Ct);
        Assert.NotNull(read);
        Assert.Equal(body, content(read), StringComparer.Ordinal);
        Assert.Equal("before", title(read));
        Assert.Equal(body, content(Assert.Single(await stores.Store<T>(table).QueryAsync(stores.Pk, Ct))), StringComparer.Ordinal);

        // A projection that names the renamed property returns the value.
        var projected = Assert.Single(await stores.Store<T>(table).QueryAsync(
            new QueryOptions { Partition = stores.Pk, Select = ["Content"] }, Ct));
        Assert.Equal(body, content(projected), StringComparer.Ordinal);

        // Reading moved nothing: the row and the blob are where the old type left them.
        Assert.Equal(CellsOf(legacyRows, "Body"), CellsOf(await stores.RawRowsAsync<T>(table), "Body"));

        // Change an unrelated property and save through the new type.
        setTitle(read, "after");
        await SaveAndAssertBlobsAsync(stores, table, read, "Content", "Body", form);

        var again = await stores.Store<T>(table).OneAsync(id, Ct);
        Assert.NotNull(again);
        Assert.Equal("after", title(again));
        Assert.Equal(body, content(again), StringComparer.Ordinal);
        AssertStoredUnder(await stores.RawRowsAsync<T>(table), "Content", "Body", form);
    }

    // ── byte[] ─────────────────────────────────────────────────────────────

    public static TheoryData<string, string> StoresAndForms()
    {
        var data = new TheoryData<string, string>();
        foreach (var store in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        foreach (var form in new[] { Inline, Cells, Rows, Blob })
            data.Add(store, form);
        return data;
    }

    [Theory]
    [MemberData(nameof(StoresAndForms))]
    public async Task RenamedBytes_AreReadFromTheLegacyColumn_AndSavedUnderTheNewOne(string store, string form)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, cleanupGrace: TimeSpan.Zero);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        const string Table = "AliasBytes";
        var id = stores!.Id("r1");
        var image = RandomBytes(BinaryLength(form));
        await stores.Store<AliasBytesV1>(Table).CreateAsync(new AliasBytesV1 { Id = id, Title = "before", Image = image }, Ct);
        await stores.ShareTableAsync<AliasBytesV1, AliasBytesV2>(Table);

        var read = await stores.Store<AliasBytesV2>(Table).OneAsync(id, Ct);
        Assert.Equal(image, read!.Picture);
        var projected = Assert.Single(await stores.Store<AliasBytesV2>(Table).QueryAsync(
            new QueryOptions { Partition = stores.Pk, Select = ["Picture"] }, Ct));
        Assert.Equal(image, projected.Picture);

        read.Title = "after";
        await SaveAndAssertBlobsAsync(stores, Table, read, "Picture", "Image", form);

        var again = await stores.Store<AliasBytesV2>(Table).OneAsync(id, Ct);
        Assert.Equal("after", again!.Title);
        Assert.Equal(image, again.Picture);
        AssertStoredUnder(await stores.RawRowsAsync<AliasBytesV2>(Table), "Picture", "Image", form);
    }

    // ── a JSON-stored collection ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(StoresAndForms))]
    public async Task RenamedCollection_IsReadFromTheLegacyColumn_AndSavedUnderTheNewOne(string store, string form)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, cleanupGrace: TimeSpan.Zero);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        const string Table = "AliasJson";
        var id = stores!.Id("r1");
        var tags = Labels(form);
        await stores.Store<AliasJsonV1>(Table).CreateAsync(new AliasJsonV1 { Id = id, Title = "before", Tags = tags }, Ct);
        await stores.ShareTableAsync<AliasJsonV1, AliasJsonV2>(Table);

        var read = await stores.Store<AliasJsonV2>(Table).OneAsync(id, Ct);
        Assert.Equal(tags, read!.Labels);

        read.Title = "after";
        await SaveAndAssertBlobsAsync(stores, Table, read, "Labels", "Tags", form);

        var again = await stores.Store<AliasJsonV2>(Table).OneAsync(id, Ct);
        Assert.Equal("after", again!.Title);
        Assert.Equal(tags, again.Labels);
        AssertStoredUnder(await stores.RawRowsAsync<AliasJsonV2>(Table), "Labels", "Tags", form, json: true);
    }

    // ── a renamed TableBlobContent ─────────────────────────────────────────

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task RenamedFile_IsReadFromTheLegacyReference_AndSavedUnderTheNewColumn(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, cleanupGrace: TimeSpan.Zero);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        const string Table = "AliasFile";
        var id = stores!.Id("r1");
        var bytes = RandomBytes(300_000);
        await stores.Store<AliasFileV1>(Table).CreateAsync(
            new AliasFileV1 { Id = id, Title = "before", File = TableBlobContent.FromBytes(bytes, "image/png") }, Ct);
        await stores.ShareTableAsync<AliasFileV1, AliasFileV2>(Table);
        var old = Assert.Single(await stores.BlobNamesAsync<AliasFileV2>(Table));
        Assert.Equal($"{Table}/{stores.Pk}/r1/File/{Sha(bytes)}", old);

        // The read binds the content to the blob where it is, without downloading it.
        var reader = stores.Store<AliasFileV2>(Table);
        AliasFileV2? read = null;
        Assert.Equal(0, await stores.BlobReadsAsync(reader, async () => read = await reader.OneAsync(id, Ct)));
        Assert.NotNull(read!.Attachment);
        Assert.Equal(bytes.Length, read.Attachment.Length);
        Assert.Equal("image/png", read.Attachment.ContentType);
        await using (var stream = await read.Attachment.OpenReadAsync(Ct))
            Assert.Equal(bytes, await ReadAllAsync(stream));
        Assert.Equal([old], await stores.BlobNamesAsync<AliasFileV2>(Table));

        read.Title = "after";
        await SaveAndAssertBlobsAsync(stores, Table, read, "Attachment", "File", Blob);

        var again = await stores.Store<AliasFileV2>(Table).OneAsync(id, Ct);
        Assert.Equal("after", again!.Title);
        await using (var stream = await again.Attachment!.OpenReadAsync(Ct))
            Assert.Equal(bytes, await ReadAllAsync(stream));
        AssertStoredUnder(await stores.RawRowsAsync<AliasFileV2>(Table), "Attachment", "File", Blob);
    }

    // ── the cells of a type that had not opted in ──────────────────────────

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task GZipAndJsonCells_OfATypeThatHadNotOptedIn_AreReadThroughTheAlias_AndSavedSplit(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, cleanupGrace: TimeSpan.Zero);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        const string Table = "AliasPlain";
        var id = stores!.Id("r1");
        // Compressible text: the plain type stores it as one __GZip cell (it would refuse random text of this size).
        var body = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 2_500));
        var tags = Labels(Inline);
        await stores.Store<AliasPlainV1>(Table).CreateAsync(new AliasPlainV1 { Id = id, Title = "before", Body = body, Tags = tags }, Ct);
        await stores.ShareTableAsync<AliasPlainV1, AliasOptedInV2>(Table);
        var legacy = Assert.Single(await stores.RawRowsAsync<AliasOptedInV2>(Table));
        Assert.True(legacy.ContainsKey("Body__GZip"));
        Assert.True(legacy.ContainsKey("Tags__Json"));

        var read = await stores.Store<AliasOptedInV2>(Table).OneAsync(id, Ct);
        Assert.Equal(body, read!.Content, StringComparer.Ordinal);
        Assert.Equal(tags, read.Labels);

        read.Title = "after";
        await stores.Store<AliasOptedInV2>(Table).UpdateAsync(read, Ct);

        var again = await stores.Store<AliasOptedInV2>(Table).OneAsync(id, Ct);
        Assert.Equal(body, again!.Content, StringComparer.Ordinal);
        Assert.Equal(tags, again.Labels);
        var rows = await stores.RawRowsAsync<AliasOptedInV2>(Table);
        AssertStoredUnder(rows, "Content", "Body", Cells);
        AssertStoredUnder(rows, "Labels", "Tags", Inline, json: true);
    }

    // ── the canonical column wins, in every form ───────────────────────────

    [Fact]
    public void CanonicalColumn_WinsOverALegacyLargeValue_AndALegacyLargeValueOverALegacyCell()
    {
        var legacy = Ascii(40_000, seed: 1);
        var row = new AliasTextV1 { Id = "p|r", Body = legacy }.ToTableEntity("p", "r");
        Assert.True(row.ContainsKey("Body__chunks"));

        // Only the legacy split cells: read through the alias.
        Assert.Equal(legacy, row.FromTableEntity<AliasTextV2>().Content, StringComparer.Ordinal);
        Assert.Equal(legacy, row.FromTableEntity<AliasTextV2ClassLevel>().Content, StringComparer.Ordinal);

        // A canonical cell beside them (a row saved by the new type that kept stale legacy cells): it wins.
        row["Content"] = "canonical";
        Assert.Equal("canonical", row.FromTableEntity<AliasTextV2>().Content);

        // Canonical split cells win as well.
        var current = Ascii(50_000, seed: 2);
        var both = new AliasTextV2 { Id = "p|r", Content = current }.ToTableEntity("p", "r");
        foreach (var (key, value) in new AliasTextV1 { Id = "p|r", Body = legacy }.ToTableEntity("p", "r"))
            if (key.StartsWith("Body", StringComparison.Ordinal)) both[key] = value;
        Assert.Equal(current, both.FromTableEntity<AliasTextV2>().Content, StringComparer.Ordinal);

        // A legacy cell beside legacy split cells: the split value wins, as it does under the canonical name.
        var mixed = new AliasTextV1 { Id = "p|r", Body = legacy }.ToTableEntity("p", "r");
        mixed["Body"] = "stale cell";
        Assert.Equal(legacy, mixed.FromTableEntity<AliasTextV2>().Content, StringComparer.Ordinal);
    }

    [Fact]
    public void LegacySplitValue_ThatIsDamaged_FailsTheRead_ThroughTheAliasToo()
    {
        var row = new AliasTextV1 { Id = "p|r", Body = Ascii(40_000) }.ToTableEntity("p", "r");
        row["Body__c1"] = "tampered";

        var ex = Assert.Throws<TableValueIntegrityException>(() => row.FromTableEntity<AliasTextV2>());

        Assert.Equal("Body", ex.ColumnName);
    }

    [Fact]
    public void LegacyBlobReference_ThatNothingResolved_FailsThePlainRead_NeverReadsAsNull()
    {
        var reference = new BlobReference("T/p/r/Body/" + new string('a', 64), new string('a', 64), 10, "text/plain; charset=utf-8", "text");
        var row = new TableEntity("p", "r") { ["Id"] = "p|r", ["Body__blob"] = reference.ToJson() };

        var ex = Assert.Throws<InvalidOperationException>(() => row.FromTableEntity<AliasTextV2>());

        Assert.Contains("'Body'", ex.Message, StringComparison.Ordinal);
    }

    // ── which legacy references a read resolves ────────────────────────────

    // A value too large for one cell and above the (lowered) threshold: the row holds a reference to a blob.
    private static async Task<(TableEntity Row, CountingOffloadBlobStore Blobs, ValueOffloadCoordinator Coordinator)> OffloadedLegacyRowAsync(string body)
    {
        var blobs = new CountingOffloadBlobStore(new InMemoryOffloadBlobStore());
        var coordinator = new ValueOffloadCoordinator(blobs, new BlobOffloadOptions { ThresholdBytes = 1_000 }, "T");
        var (row, _, pending) = coordinator.Serialize(new AliasTextV1 { Id = "p|r", Title = "t", Body = body }, "p", "r", 0);
        await coordinator.UploadAsync(pending, Ct);
        Assert.True(row.ContainsKey("Body__blob"));
        return (row, blobs, coordinator);
    }

    [Fact]
    public async Task LegacyReference_IsInScope_WhenTheProjectionNamesTheRenamedPropertyOrTheLegacyColumn()
    {
        var body = Ascii(40_000);
        var (row, blobs, coordinator) = await OffloadedLegacyRowAsync(body);

        foreach (var projection in new string[]?[] { null, ["Content"], ["Body"] })
        {
            var ctx = await coordinator.ResolveAsync(row, typeof(AliasTextV2), projection, Ct);
            Assert.Equal(body, ctx.Resolved["Body"]);
            Assert.Equal(body, TableEntitySerializer.FromTableEntity<AliasTextV2>(row, ctx).Content, StringComparer.Ordinal);
        }
        Assert.Equal(3, blobs.OpenReadCalls);

        // A projection that names neither: nothing is downloaded and the property keeps its default.
        var omitted = await coordinator.ResolveAsync(row, typeof(AliasTextV2), ["Title"], Ct);
        Assert.Empty(omitted.Resolved);
        Assert.Null(TableEntitySerializer.FromTableEntity<AliasTextV2>(row, omitted).Content);
        Assert.Equal(3, blobs.OpenReadCalls);
    }

    [Fact]
    public async Task LegacyReference_BesideTheCanonicalOne_IsNeitherDownloadedNorRead()
    {
        var legacy = Ascii(40_000, seed: 1);
        var current = Ascii(50_000, seed: 2);
        var (row, blobs, coordinator) = await OffloadedLegacyRowAsync(legacy);
        var (canonical, _, pending) = coordinator.Serialize(new AliasTextV2 { Id = "p|r", Content = current }, "p", "r", 0);
        await coordinator.UploadAsync(pending, Ct);
        row["Content__blob"] = canonical["Content__blob"];

        var ctx = await coordinator.ResolveAsync(row, typeof(AliasTextV2), null, Ct);

        Assert.Equal(["Content"], ctx.Resolved.Keys);
        Assert.Equal(1, blobs.OpenReadCalls);
        Assert.Equal(current, TableEntitySerializer.FromTableEntity<AliasTextV2>(row, ctx).Content, StringComparer.Ordinal);
    }

    // ── the type's large-value columns include the legacy ones ─────────────

    [Fact]
    public void LegacyColumns_OfRenamedLargeValueProperties_AreAmongTheTypesLargeValueColumns()
    {
        Assert.Equal(["Content", "Body"], TypeMetadataCache.LargeValueColumns(typeof(AliasTextV2)));
        Assert.Equal(["Content", "Body"], TypeMetadataCache.LargeValueColumns(typeof(AliasTextV2ClassLevel)));
        Assert.Equal(["Attachment", "File"], TypeMetadataCache.LargeValueColumns(typeof(AliasFileV2)));
        Assert.Equal(["Content", "Labels", "Body", "Tags"], TypeMetadataCache.LargeValueColumns(typeof(AliasOptedInV2)));
        // An alias of an ordinary property is no large-value column (every Entity has two of those).
        Assert.Equal(["Body"], TypeMetadataCache.LargeValueColumns(typeof(AliasTextV1)));
    }

    // ── delete ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LargeValueStores.Memory)]
    [InlineData(LargeValueStores.Azurite)]
    public async Task DeleteThroughTheNewType_RemovesTheLegacyRowsAndItsBlob(string store)
    {
        await using var stores = await LargeValueStores.OpenAsync(store, cleanupGrace: TimeSpan.Zero);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        const string Table = "AliasTextProperty";
        var blob = stores!.Id("blob");
        var rows = stores.Id("rows");
        await stores.Store<AliasTextV1>(Table).CreateAsync(new AliasTextV1 { Id = blob, Body = Ascii(3_500_000) }, Ct);
        await stores.Store<AliasTextV1>(Table).CreateAsync(new AliasTextV1 { Id = rows, Body = Ascii(700_000) }, Ct);
        await stores.ShareTableAsync<AliasTextV1, AliasTextV2>(Table);
        Assert.Single(await stores.BlobNamesAsync<AliasTextV2>(Table));
        await stores.PastTheNewestBlobAsync<AliasTextV2>(Table);

        await stores.Store<AliasTextV2>(Table).DeleteAsync(blob, Ct);
        await stores.Store<AliasTextV2>(Table).DeleteAsync(rows, Ct);

        Assert.Empty(await stores.RawRowsAsync<AliasTextV2>(Table));
        Assert.Empty(await stores.BlobNamesAsync<AliasTextV2>(Table));
    }
}
