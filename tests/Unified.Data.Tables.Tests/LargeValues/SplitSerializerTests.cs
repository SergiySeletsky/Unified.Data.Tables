using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

// PlainEntity_BehaviourUnchanged depends on the process-wide default OversizedCellPolicy (Throw).
[Collection("OversizedCellPolicy")]
public class SplitSerializerTests
{
    private const string Pk = "p";
    private const string Rk = "r";

    private static string RandomText(int length, int seed = 42)
    {
        var rng = new Random(seed);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)rng.Next(0x21, 0x7E);
        return new string(chars);
    }

    private static string[] LargeValueColumnsOf(TableEntity row, string baseColumn) =>
        row.Keys.Where(k => k.StartsWith(baseColumn + "__", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void SmallValue_OnOptedInProperty_IsAnOrdinaryColumn()
    {
        var body = new string('x', 100);
        var row = new SplitTextEntity { Id = "p|r", Body = body }.ToTableEntity(Pk, Rk);

        Assert.Equal(body, Assert.IsType<string>(row["Body"]));
        Assert.Empty(LargeValueColumnsOf(row, "Body"));
        Assert.Equal(body, row.FromTableEntity<SplitTextEntity>().Body);
    }

    [Fact]
    public void TextValue_At32000_IsOneCell_At32001_IsTwoChunks()
    {
        var atLimit = RandomText(32_000);
        var one = new SplitTextEntity { Body = atLimit }.ToTableEntity(Pk, Rk);
        Assert.Equal(atLimit, Assert.IsType<string>(one["Body"]));
        Assert.Empty(LargeValueColumnsOf(one, "Body"));
        Assert.Equal(atLimit, one.FromTableEntity<SplitTextEntity>().Body);

        var over = RandomText(32_001);
        var two = new SplitTextEntity { Body = over }.ToTableEntity(Pk, Rk);
        Assert.False(two.ContainsKey("Body"));
        Assert.IsType<string>(two["Body__c0"]);
        Assert.IsType<string>(two["Body__c1"]);
        Assert.False(two.ContainsKey("Body__c2"));
        Assert.Equal(2, two["Body__chunks"]);
        Assert.Equal(over, two.FromTableEntity<SplitTextEntity>().Body);
    }

    [Fact]
    public void TextSplit_NeverUsesGzip()
    {
        var body = new string('a', 254_000); // highly compressible, still never gzipped under Text
        var row = new SplitTextEntity { Body = body }.ToTableEntity(Pk, Rk);

        Assert.DoesNotContain(row.Keys, k => k.EndsWith("__GZip", StringComparison.Ordinal));
        Assert.Equal(8, row["Body__chunks"]);
        Assert.Equal("text", row["Body__enc"]);
        for (var i = 0; i < 8; i++)
            Assert.IsType<string>(row[$"Body__c{i}"]);
        Assert.Equal(body, row.FromTableEntity<SplitTextEntity>().Body);
    }

    [Fact]
    public void GzipSplit_RoundTrips()
    {
        var body = string.Concat(Enumerable.Repeat("The quick brown fox. ", 20_000));
        var row = new SplitGzipEntity { Body = body }.ToTableEntity(Pk, Rk);

        Assert.Equal("gzip", row["Body__enc"]);
        Assert.IsType<byte[]>(row["Body__c0"]);
        Assert.DoesNotContain(row.Keys, k => k.EndsWith("__GZip", StringComparison.Ordinal));
        Assert.Equal(body, row.FromTableEntity<SplitGzipEntity>().Body);
    }

    [Fact]
    public void GzipValue_ThatFitsOneCell_IsAnOrdinaryStringColumn()
    {
        foreach (var body in new[] { "hello", RandomText(32_000) })
        {
            var row = new SplitGzipEntity { Body = body }.ToTableEntity(Pk, Rk);
            Assert.Equal(body, Assert.IsType<string>(row["Body"]));
            Assert.Empty(LargeValueColumnsOf(row, "Body"));
            Assert.Equal(body, row.FromTableEntity<SplitGzipEntity>().Body);
        }
    }

    [Fact]
    public void GzipValue_Over32000Chars_IsGzipSplit()
    {
        var body = RandomText(32_001);
        var row = new SplitGzipEntity { Body = body }.ToTableEntity(Pk, Rk);

        Assert.False(row.ContainsKey("Body"));
        Assert.Equal("gzip", row["Body__enc"]);
        Assert.IsType<byte[]>(row["Body__c0"]);
        Assert.Equal(body, row.FromTableEntity<SplitGzipEntity>().Body);
    }

    [Fact]
    public void GzipJsonFallback_ThatFits_StaysJsonColumn_LargerIsGzipSplit()
    {
        var few = new List<string> { "a", "b" };
        var small = new SplitGzipJsonEntity { Items = few }.ToTableEntity(Pk, Rk);
        Assert.IsType<string>(small["Items__Json"]);
        Assert.False(small.ContainsKey("Items__chunks"));
        Assert.Equal(few, small.FromTableEntity<SplitGzipJsonEntity>().Items);

        var many = Enumerable.Range(0, 5_000).Select(i => RandomText(50, i)).ToList();
        var large = new SplitGzipJsonEntity { Items = many }.ToTableEntity(Pk, Rk);
        Assert.False(large.ContainsKey("Items__Json"));
        Assert.Equal("gzip", large["Items__enc"]);
        Assert.Equal(many, large.FromTableEntity<SplitGzipJsonEntity>().Items);
    }

    [Fact]
    public void BinarySplit_RoundTrips()
    {
        var image = new byte[1_048_576];
        new Random(7).NextBytes(image);
        var row = new SplitBinaryEntity { Image = image }.ToTableEntity(Pk, Rk);

        Assert.False(row.ContainsKey("Image"));
        Assert.Equal("binary", row["Image__enc"]);
        Assert.Equal(16, row["Image__chunks"]);
        Assert.Equal(1_048_576L, row["Image__len"]);
        Assert.Equal(image, row.FromTableEntity<SplitBinaryEntity>().Image);

        var small = new byte[] { 1, 2, 3 };
        var smallRow = new SplitBinaryEntity { Image = small }.ToTableEntity(Pk, Rk);
        Assert.Equal(small, Assert.IsType<byte[]>(smallRow["Image"]));
        Assert.Empty(LargeValueColumnsOf(smallRow, "Image"));
    }

    [Fact]
    public void JsonFallbackSplit_RoundTrips()
    {
        var items = Enumerable.Range(0, 5_000).Select(i => RandomText(50, i)).ToList();
        var row = new SplitJsonEntity { Items = items }.ToTableEntity(Pk, Rk);

        Assert.False(row.ContainsKey("Items__Json"));
        Assert.False(row.ContainsKey("Items__GZip"));
        Assert.Equal("text", row["Items__enc"]);
        Assert.True((int)row["Items__chunks"] > 1);
        Assert.Equal(items, row.FromTableEntity<SplitJsonEntity>().Items);

        var few = new List<string> { "a", "b" };
        var smallRow = new SplitJsonEntity { Items = few }.ToTableEntity(Pk, Rk);
        Assert.IsType<string>(smallRow["Items__Json"]);
        Assert.Equal(few, smallRow.FromTableEntity<SplitJsonEntity>().Items);
    }

    [Fact]
    public void NestedSplit_RoundTrips()
    {
        var body = RandomText(100_000);
        var row = new SplitNestedEntity { Inner = new SplitInner { Body = body, Count = 3 } }.ToTableEntity(Pk, Rk);

        Assert.Equal(4, row["Inner_Body__chunks"]);
        Assert.Equal(3, row["Inner_Count"]);
        var back = row.FromTableEntity<SplitNestedEntity>();
        Assert.Equal(body, back.Inner!.Body);
        Assert.Equal(3, back.Inner.Count);
    }

    [Fact]
    public void PlainEntity_BehaviourUnchanged()
    {
        var lowEntropy = new string('z', 100_000);
        var row = new PlainEntity { Body = lowEntropy }.ToTableEntity(Pk, Rk);
        Assert.IsType<string>(row["Body__GZip"]);
        Assert.False(row.ContainsKey("Body"));
        Assert.DoesNotContain(row.Keys, k => SplitCodec.TryParseLargeValueColumn(k, out _, out _, out _));
        Assert.Equal(lowEntropy, row.FromTableEntity<PlainEntity>().Body);

        Assert.Throws<OversizedCellException>(() =>
            new PlainEntity { Body = RandomText(100_000) }.ToTableEntity(Pk, Rk));
    }

    [Fact]
    public void Materialize_CorruptChunk_Throws()
    {
        var row = new SplitTextEntity { Body = RandomText(70_000) }.ToTableEntity(Pk, Rk);
        var chunk = (string)row["Body__c1"];
        row["Body__c1"] = (chunk[0] == 'a' ? "b" : "a") + chunk[1..];

        var ex = Assert.Throws<TableValueIntegrityException>(() => row.FromTableEntity<SplitTextEntity>());
        Assert.Equal("Body", ex.ColumnName);
    }

    [Fact]
    public void LargeValueColumns_AreNeverFedToPropertySetters()
    {
        // A row carrying split cells for a property must not leak __chunks/__len/... into anything else.
        var row = new SplitTextEntity { Body = RandomText(40_000), Title = "t" }.ToTableEntity(Pk, Rk);
        var back = row.FromTableEntity<SplitTextEntity>();
        Assert.Equal("t", back.Title);
    }

    [Fact]
    public async Task PolymorphicWrite_OfSplitValue_IsRefused()
    {
        var key = new TableKey(Pk, Rk);
        var big = new SplitTextEntity { Body = RandomText(40_000) };

        var memory = new InMemoryPolymorphicStorage<object>("T");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            memory.InsertAsync(new PolymorphicWrite<object>(key, big)));

        using var harness = new PolymorphicHarness<object>();
        harness.SetupAdd();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            harness.Store.InsertAsync(new PolymorphicWrite<object>(key, big)));
        Assert.Null(harness.LastWrittenEntity);

        // A small value on the same type writes ordinary columns and stays allowed.
        await memory.InsertAsync(new PolymorphicWrite<object>(key, new SplitTextEntity { Body = "small" }));
    }

    [Fact]
    public void LinqFilter_OnSplitProperty_IsRefused()
    {
        Assert.Throws<NotSupportedException>(() =>
            TableFilterTranslator.Translate<SplitTextEntity>(x => x.Body == "a"));
        Assert.Throws<NotSupportedException>(() =>
            TableFilterTranslator.Translate<SplitNestedEntity>(x => x.Inner!.Body == "a"));

        // Ordinary members of the same type stay filterable.
        Assert.Equal("Title eq 'a'", TableFilterTranslator.Translate<SplitTextEntity>(x => x.Title == "a"));
        Assert.Equal("Inner_Count eq 3", TableFilterTranslator.Translate<SplitNestedEntity>(x => x.Inner!.Count == 3));
    }

    [Fact]
    public async Task FlattenProperty_OfSplitProperty_IsRefused()
    {
        // The owning type names the column; a nested whole-object write carries its split member.
        var direct = Assert.Throws<NotSupportedException>(() =>
            TableEntitySerializer.FlattenProperty(typeof(SplitTextEntity), "Body", "x"));
        Assert.Contains("'Body' is a large value", direct.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() =>
            TableEntitySerializer.FlattenProperty(typeof(SplitNestedEntity), "Inner_Body", "x"));
        Assert.Throws<NotSupportedException>(() =>
            TableEntitySerializer.FlattenProperty("Inner", new SplitInner { Body = "x" }));

        // Ordinary members still flatten.
        Assert.Equal("t", TableEntitySerializer.FlattenProperty(typeof(SplitTextEntity), "Title", "t")["Title"]);
        Assert.Equal(3, TableEntitySerializer.FlattenProperty(typeof(SplitNestedEntity), "Inner_Count", 3)["Inner_Count"]);

        // The TableStorage<T> builder path refuses before any SDK call.
        using var harness = new StorageHarness<SplitTextEntity>();
        harness.SetupUpdate();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            harness.Store.UpdateAsync("p|r", b => b.SetProperty(x => x.Body, "x")));
    }

    [Fact]
    public async Task InMemoryStorage_UpdateOfSplitProperty_IsRefused_AndValueUnchanged()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        var original = RandomText(40_000);
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = original, Title = "t" });

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.UpdateAsync("p|r", b => b.SetProperty(x => x.Body, "new")));
        Assert.Contains("'Body' is a large value", ex.Message, StringComparison.Ordinal);

        var nested = new InMemoryStorage<SplitNestedEntity>();
        await nested.CreateAsync(new SplitNestedEntity { Id = "p|r", Inner = new SplitInner { Body = "old" } });
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            nested.UpdateAsync("p|r", b => b.SetProperty(x => x.Inner!.Body, "new")));

        var back = await store.OneAsync("p|r");
        Assert.Equal(original, back!.Body);

        // Ordinary members of the same type still merge.
        await store.UpdateAsync("p|r", b => b.SetProperty(x => x.Title, "t2"));
        back = await store.OneAsync("p|r");
        Assert.Equal("t2", back!.Title);
        Assert.Equal(original, back.Body);
    }

    [Fact]
    public void IsLargeValueAware_TrueForAttributeOrNestedAttribute_FalseForPlain()
    {
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(SplitTextEntity)));
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(SplitNestedEntity)));
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(ClassLevelSplitEntity)));
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(PlainEntity)));
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(TestEntity)));

        Assert.Equal(["Inner_Body"], TypeMetadataCache.LargeValueColumns(typeof(SplitNestedEntity)));
        Assert.Equal(["Body", "Items"], TypeMetadataCache.LargeValueColumns(typeof(ClassLevelSplitEntity)).OrderBy(c => c, StringComparer.Ordinal));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(typeof(PlainEntity)));
    }

    [Fact]
    public void ClassLevelAttribute_CoversDeclaredStringAndJsonProperties_NotInheritedId()
    {
        var meta = TypeMetadataCache.GetMetadata(typeof(ClassLevelSplitEntity));
        Assert.True(meta.SplitOptions.ContainsKey("Body"));
        Assert.True(meta.SplitOptions.ContainsKey("Items"));
        Assert.False(meta.SplitOptions.ContainsKey("Number"));
        Assert.False(meta.SplitOptions.ContainsKey("Id"));

        var body = RandomText(50_000);
        var row = new ClassLevelSplitEntity { Body = body, Number = 5 }.ToTableEntity(Pk, Rk);
        Assert.Equal(2, row["Body__chunks"]);
        Assert.Equal(body, row.FromTableEntity<ClassLevelSplitEntity>().Body);
    }

    [Fact]
    public void PropertyLevelAttribute_OnScalar_FailsOnFirstUse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TypeMetadataCache.GetMetadata(typeof(SplitOnScalarEntity)));
        Assert.Contains("SplitOnScalarEntity.Number", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(SplitOnInterfaceEntity), "SplitOnInterfaceEntity.Shape")]
    [InlineData(typeof(SplitOnAbstractEntity), "SplitOnAbstractEntity.Shape")]
    public void PropertyLevelAttribute_OnInterfaceOrAbstractType_IsRefused(Type type, string member)
    {
        // Such a member flattens or not depending on the runtime value; a flattened one would ignore the
        // attribute, so an opted-in value could be trimmed under OversizedCells = Trim.
        var ex = Assert.Throws<NotSupportedException>(() => TypeMetadataCache.GetMetadata(type));
        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyLevelAttribute_OnInterfaceCollection_IsAccepted()
    {
        var meta = TypeMetadataCache.GetMetadata(typeof(SplitOnInterfaceListEntity));
        Assert.True(meta.SplitOptions.ContainsKey("Items"));
        var items = Enumerable.Range(0, 5_000).Select(i => RandomText(20) + i).ToList();
        var row = new SplitOnInterfaceListEntity { Items = items }.ToTableEntity(Pk, Rk);
        Assert.True(row.ContainsKey("Items__chunks"));
        Assert.Equal(items, row.FromTableEntity<SplitOnInterfaceListEntity>().Items);
    }

    [Fact]
    public void HasLargeValueCells_DetectsSplitCellsOnly()
    {
        Assert.True(TableEntitySerializer.HasLargeValueCells(
            new SplitTextEntity { Body = RandomText(40_000) }.ToTableEntity(Pk, Rk)));
        Assert.False(TableEntitySerializer.HasLargeValueCells(
            new SplitTextEntity { Body = "small" }.ToTableEntity(Pk, Rk)));
        Assert.False(TableEntitySerializer.HasLargeValueCells(
            new PlainEntity { Body = new string('z', 100_000) }.ToTableEntity(Pk, Rk)));
    }

    [Fact]
    public void IllFormedText_IsStoredAsUtf16le_AndReadsBackCodeUnitForCodeUnit()
    {
        var body = "abc" + '\uD800' + "def";
        var row = new SplitTextEntity { Body = body }.ToTableEntity(Pk, Rk);

        Assert.Equal("utf16le", row["Body__enc"]);
        Assert.Equal(body, row.FromTableEntity<SplitTextEntity>().Body, StringComparer.Ordinal);
    }

    [Fact]
    public void EmptyValue_IsAnOrdinaryColumn()
    {
        var row = new SplitTextEntity { Body = string.Empty }.ToTableEntity(Pk, Rk);
        Assert.Equal(string.Empty, row["Body"]);
        Assert.Equal(string.Empty, row.FromTableEntity<SplitTextEntity>().Body);
    }
}
