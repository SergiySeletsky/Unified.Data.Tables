using System.Text.Json;
using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

public class OffloadSerializerTests
{
    private const string Pk = "p";
    private const string Rk = "r";

    private static ValueOffloadCoordinator Coordinator(long threshold = 100_000, InMemoryOffloadBlobStore? store = null) =>
        new(store ?? new InMemoryOffloadBlobStore(), new BlobOffloadOptions { ThresholdBytes = threshold }, "T");

    private static ValueOffloadCoordinator DefaultCoordinator() =>
        new(new InMemoryOffloadBlobStore(), new BlobOffloadOptions(), "T");

    private static JsonElement Reference(TableEntity row, string column) =>
        JsonDocument.Parse(Assert.IsType<string>(row[column + "__blob"])).RootElement;

    private static string[] ColumnsOf(TableEntity row, string baseColumn) =>
        row.Keys.Where(k => k.StartsWith(baseColumn + "__", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void JustBelowThreshold_IsSplit_JustAboveThreshold_IsOffloaded()
    {
        var coordinator = Coordinator();

        var (below, _, belowBlobs) = coordinator.Serialize(new SplitTextEntity { Body = Ascii(99_999) }, Pk, Rk, 0);
        Assert.True(below.ContainsKey("Body__chunks"));
        Assert.False(below.ContainsKey("Body__blob"));
        Assert.Empty(belowBlobs);

        var (above, plan, aboveBlobs) = coordinator.Serialize(new SplitTextEntity { Body = Ascii(100_001), Title = "t" }, Pk, Rk, 0);
        Assert.Equal(["Body__blob"], ColumnsOf(above, "Body"));
        Assert.False(above.ContainsKey("Body"));
        Assert.Equal("t", above["Title"]);
        Assert.Empty(plan.Parts);
        var blob = Assert.Single(aboveBlobs);
        Assert.Equal("Body", blob.Column);
        Assert.Equal(100_001, blob.Reference.Length);
    }

    [Fact]
    public void DefaultThreshold_EightHundredKTokens_IsOffloaded()
    {
        var body = Ascii(3_200_000);
        var (row, _, blobs) = DefaultCoordinator().Serialize(new SplitTextEntity { Body = body, Title = "t" }, Pk, Rk, 0);

        Assert.Equal(["Body__blob"], ColumnsOf(row, "Body"));
        Assert.DoesNotContain(row.Keys, k => k.Contains("__c", StringComparison.Ordinal));
        var reference = Reference(row, "Body");
        Assert.Equal("text", reference.GetProperty("encoding").GetString());
        Assert.Equal("text/plain; charset=utf-8", reference.GetProperty("contentType").GetString());
        Assert.Equal(3_200_000, reference.GetProperty("length").GetInt64());
        Assert.Equal(Sha(Utf8(body)), reference.GetProperty("sha256").GetString());
        Assert.Equal(
            ValueOffloadCoordinator.BlobName("T", Pk, Rk, "Body", Sha(Utf8(body))),
            reference.GetProperty("path").GetString());
        Assert.Null(Assert.Single(blobs).ContentEncoding);
    }

    [Fact]
    public void GzipOffload_HasContentEncodingGzip()
    {
        var (row, _, blobs) = Coordinator(threshold: 1_000).Serialize(new SplitGzipEntity { Body = Ascii(200_000) }, Pk, Rk, 0);

        var reference = Reference(row, "Body");
        Assert.Equal("gzip", reference.GetProperty("encoding").GetString());
        Assert.Equal("text/plain; charset=utf-8", reference.GetProperty("contentType").GetString());
        Assert.Equal("gzip", Assert.Single(blobs).ContentEncoding);
    }

    [Fact]
    public void BinaryOffload_IsOctetStream()
    {
        var bytes = RandomBytes(200_000);
        var (row, _, blobs) = Coordinator().Serialize(new SplitBinaryEntity { Image = bytes }, Pk, Rk, 0);

        var reference = Reference(row, "Image");
        Assert.Equal("binary", reference.GetProperty("encoding").GetString());
        Assert.Equal("application/octet-stream", reference.GetProperty("contentType").GetString());
        Assert.Equal(Sha(bytes), reference.GetProperty("sha256").GetString());
        Assert.Null(Assert.Single(blobs).ContentEncoding);
    }

    [Fact]
    public async Task IllFormedText_OffloadsAsUtf16le()
    {
        var store = new InMemoryOffloadBlobStore();
        var coordinator = Coordinator(store: store);
        var body = Ascii(60_000) + "\uD800" + Ascii(60_000, seed: 3); // an unpaired high surrogate
        var (row, _, blobs) = coordinator.Serialize(new SplitTextEntity { Body = body }, Pk, Rk, 0);

        var reference = Reference(row, "Body");
        Assert.Equal("utf16le", reference.GetProperty("encoding").GetString());
        Assert.Equal("text/plain; charset=utf-16le", reference.GetProperty("contentType").GetString());
        Assert.Equal(body.Length * 2L, reference.GetProperty("length").GetInt64());

        await coordinator.UploadAsync(blobs, CancellationToken.None);
        var read = await coordinator.ResolveAsync(row, typeof(SplitTextEntity), null, CancellationToken.None);
        var back = TableEntitySerializer.FromTableEntity<SplitTextEntity>(row, read);
        Assert.Equal(body, back.Body);
    }

    [Fact]
    public void BelowThreshold_ButNotOneTransaction_IsForcedToBlob()
    {
        var body = Ascii(2_000_000); // 2 MB of UTF-8 is under 3 MiB, but 4 MB as UTF-16 string cells
        var (row, plan, blobs) = DefaultCoordinator().Serialize(new SplitTextEntity { Body = body }, Pk, Rk, 0);

        Assert.Equal(["Body__blob"], ColumnsOf(row, "Body"));
        Assert.Empty(plan.Parts);
        Assert.Equal(2_000_000, Assert.Single(blobs).Reference.Length);
    }

    [Fact]
    public void NeverOffload_TooLargeForOneTransaction_Throws()
    {
        var ex = Assert.Throws<OversizedValueException>(
            () => DefaultCoordinator().Serialize(new NeverOffloadEntity { Body = Ascii(2_000_000) }, Pk, Rk, 0));
        Assert.Equal("Body", ex.ColumnName);
        Assert.Contains("BlobOffload.AboveThreshold", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverOffload_AboveThreshold_StaysSplit()
    {
        var (row, _, blobs) = Coordinator().Serialize(new NeverOffloadEntity { Body = Ascii(200_000) }, Pk, Rk, 0);
        Assert.True(row.ContainsKey("Body__chunks"));
        Assert.Empty(blobs);
    }

    [Fact]
    public void NoOffloadConfigured_ValueNeedingTier3_Throws()
    {
        var coordinator = new ValueOffloadCoordinator(null, null, "T");
        var ex = Assert.Throws<OversizedValueException>(
            () => coordinator.Serialize(new SplitTextEntity { Body = Ascii(2_000_000) }, Pk, Rk, 0));
        Assert.Equal("Body", ex.ColumnName);
        Assert.Contains("Configure UnifiedTableStorageOptions.BlobOffload", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoOffloadConfigured_TableBlobContent_Throws()
    {
        var entity = new BlobEntity { Content = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream") };

        var ex = Assert.Throws<OversizedValueException>(
            () => new ValueOffloadCoordinator(null, null, "T").Serialize(entity, Pk, Rk, 0));
        Assert.Equal("Content", ex.ColumnName);
        Assert.Throws<OversizedValueException>(() => entity.ToTableEntity(Pk, Rk));
    }

    [Fact]
    public void BlobName_IsEscapedAndContentAddressed()
    {
        var sha = new string('a', 64);
        Assert.Equal("T/p%20k/r%2Fk/Inner_Body/" + sha, ValueOffloadCoordinator.BlobName("T", "p k", "r/k", "Inner_Body", sha));
        Assert.Throws<ArgumentException>(() => ValueOffloadCoordinator.BlobName("T", new string('x', 1_000), "r", "Body", sha));
    }

    [Fact]
    public void TableBlobContent_InList_IsRefused()
    {
        var entity = new BlobListEntity { Contents = [TableBlobContent.FromBytes([1], "application/octet-stream")] };
        Assert.Throws<NotSupportedException>(() => Coordinator().Serialize(entity, Pk, Rk, 0));
    }

    [Fact]
    public void TableBlobContent_IsAlwaysOffloaded_EvenWhenTiny()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var entity = new NestedBlobEntity { Inner = new NestedBlobHolder { Content = TableBlobContent.FromBytes(bytes, "image/png") } };
        var (row, _, blobs) = Coordinator().Serialize(entity, Pk, Rk, 0);

        var reference = Reference(row, "Inner_Content");
        Assert.Equal("binary", reference.GetProperty("encoding").GetString());
        Assert.Equal("image/png", reference.GetProperty("contentType").GetString());
        Assert.Equal(3, reference.GetProperty("length").GetInt64());
        Assert.Equal(ValueOffloadCoordinator.BlobName("T", Pk, Rk, "Inner_Content", Sha(bytes)), reference.GetProperty("path").GetString());
        Assert.Equal("Inner_Content", Assert.Single(blobs).Column);
    }

    [Fact]
    public void TableBlobContent_IsLargeValueAware_AndRefusedByPartialUpdatesAndFilters()
    {
        Assert.Equal(["Content"], TypeMetadataCache.LargeValueColumns(typeof(BlobEntity)));
        Assert.Equal(["Inner_Content"], TypeMetadataCache.LargeValueColumns(typeof(NestedBlobEntity)));
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(PlainEntity)));

        var content = TableBlobContent.FromBytes([1], "application/octet-stream");
        Assert.Throws<NotSupportedException>(() => LargeValueRows.FlattenPropertyForUpdate(typeof(BlobEntity), "Content", content));
        Assert.Throws<NotSupportedException>(() => LargeValueRows.FlattenPropertyForUpdate(typeof(NestedBlobEntity), "Inner_Content", content));
        Assert.Throws<NotSupportedException>(
            () => LargeValueRows.FlattenPropertyForUpdate(typeof(NestedBlobEntity), "Inner", new NestedBlobHolder { Content = content }));
        Assert.Throws<NotSupportedException>(() => TableEntitySerializer.FlattenProperty("Content", content));
        Assert.Throws<NotSupportedException>(() => TableFilterTranslator.Translate<BlobEntity>(x => x.Content == null));
    }

    [Fact]
    public void TableBlobContent_PolymorphicWrite_IsDetected()
    {
        var entity = new BlobEntity { Content = TableBlobContent.FromBytes([1], "application/octet-stream") };
        Assert.True(LargeValueRows.CarriesBlobContent(entity));
        Assert.True(LargeValueRows.CarriesBlobContent(new NestedBlobEntity { Inner = new NestedBlobHolder { Content = entity.Content } }));
        Assert.False(LargeValueRows.CarriesBlobContent(new BlobEntity()));
        Assert.False(LargeValueRows.CarriesBlobContent(new PlainEntity { Body = "x" }));
    }

    [Fact]
    public void Clone_KeepsTableBlobContentByReference()
    {
        var content = TableBlobContent.FromBytes([1, 2], "application/octet-stream");
        var row = TableEntitySerializer.ToTableEntity(new BlobEntity { Content = content }, Pk, Rk, ValueWriteContext.ForClone, []);
        Assert.Same(content, row.FromTableEntity<BlobEntity>().Content);
    }

    [Fact]
    public void Materialize_ReferenceWithoutResolution_Throws_NamingTheColumn()
    {
        // A read that cannot resolve an offloaded value must not hand back the property empty: an
        // upsert of that entity would drop the reference and the sweep would then delete the blob.
        var entity = new TextAndBlobEntity { Body = Ascii(200_000) };
        var (row, _, _) = Coordinator().Serialize(entity, Pk, Rk, 0);
        Assert.True(row.ContainsKey("Body__blob"));

        var ex = Assert.Throws<InvalidOperationException>(() => row.FromTableEntity<TextAndBlobEntity>());
        Assert.Contains("'Body'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BlobOffload", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_BlobContentWithoutResolution_Throws()
    {
        var entity = new TextAndBlobEntity { Content = TableBlobContent.FromBytes([9], "application/octet-stream") };
        var (row, _, _) = Coordinator().Serialize(entity, Pk, Rk, 0);
        Assert.True(row.ContainsKey("Content__blob"));

        var ex = Assert.Throws<InvalidOperationException>(() => row.FromTableEntity<TextAndBlobEntity>());
        Assert.Contains("'Content'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_ReferenceInAPlainType_Throws()
    {
        // A type that is not large-value-aware reading a row another (aware) type wrote.
        var (row, _, _) = Coordinator().Serialize(new TextAndBlobEntity { Body = Ascii(200_000) }, Pk, Rk, 0);
        Assert.Throws<InvalidOperationException>(() => row.FromTableEntity<PlainEntity>());
    }

    [Fact]
    public void Materialize_ReferenceAProjectionExcluded_LeavesDefault()
    {
        var entity = new TextAndBlobEntity { Body = Ascii(200_000), Content = TableBlobContent.FromBytes([9], "application/octet-stream") };
        var (row, _, _) = Coordinator().Serialize(entity, Pk, Rk, 0);

        var ctx = new ValueReadContext(new Dictionary<string, object>(StringComparer.Ordinal), null, projection: ["Id"]);
        var back = TableEntitySerializer.FromTableEntity<TextAndBlobEntity>(row, ctx);
        Assert.Null(back.Body);
        Assert.Null(back.Content);
    }

    [Fact]
    public void Materialize_SplitCellsInAPlainType_AreReassembled_NotDropped()
    {
        var text = Ascii(100_000);
        var (row, plan, _) = Coordinator(threshold: 4_000_000).Serialize(new TextAndBlobEntity { Body = text }, Pk, Rk, 0);
        Assert.Empty(plan.Parts);
        Assert.True(row.ContainsKey("Body__chunks"));
        Assert.Equal(text, row.FromTableEntity<PlainEntity>().Body);
    }

    [Fact]
    public void Materialize_SplitPrimaryWithoutItsParts_Throws()
    {
        // A plain read sees only the primary: the chunks on its continuation rows are missing.
        var (row, plan, _) = Coordinator(threshold: 4_000_000).Serialize(new TextAndBlobEntity { Body = Ascii(900_000) }, Pk, Rk, 0);
        Assert.NotEmpty(plan.Parts);
        Assert.Throws<TableValueIntegrityException>(() => plan.Primary.FromTableEntity<PlainEntity>());
    }

    [Fact]
    public void PlainEntity_RowUnchangedByOffloadContext()
    {
        var entity = new PlainEntity { Id = "p|r", Body = Ascii(100) };
        var (row, _, blobs) = Coordinator().Serialize(entity, Pk, Rk, 0);
        var plain = entity.ToTableEntity(Pk, Rk);

        Assert.Empty(blobs);
        Assert.Equal(plain.Keys.OrderBy(k => k, StringComparer.Ordinal), row.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(plain["Body"], row["Body"]);
    }
}
