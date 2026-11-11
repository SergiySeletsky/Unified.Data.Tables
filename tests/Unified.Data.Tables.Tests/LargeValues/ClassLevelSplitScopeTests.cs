using System.Runtime.Serialization;
using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The scope of a class-level <c>[TableValueSplit]</c>: it opts in every string, byte[] and JSON-stored
/// property of the class, declared or inherited; a derived class inherits it; a property-level
/// attribute wins; a re-declared property keeps its opt-in; and it never reaches the entity id or a
/// member that is not stored as an ordinary column.
/// </summary>
/// <remarks>
/// Values are 40,000 random characters (or 100,000 random bytes): opted in they are split cells; not
/// opted in they would fail the write (<see cref="OversizedCellException"/>), so a property that lost
/// its opt-in cannot pass.
/// </remarks>
// OversizedCellException for an un-opted value depends on the process-wide default policy (Throw).
[Collection("OversizedCellPolicy")]
public class ClassLevelSplitScopeTests
{
    private const string Id = "p|r";

    private static readonly string BigText = Ascii(40_000);
    private static readonly string OtherText = Ascii(40_000, seed: 99);
    private static readonly byte[] BigBytes = RandomBytes(100_000);
    private static readonly List<string> BigItems = Enumerable.Range(0, 2_000).Select(i => Ascii(30, seed: i)).ToList();

    // ── models ───────────────────────────────────────────────────────────────

    public class ScopeBase : Entity
    {
        public string? Inherited { get; set; }

        public byte[]? InheritedBytes { get; set; }

        public List<string>? InheritedItems { get; set; }

        public int InheritedNumber { get; set; }
    }

    /// <summary>The attribute sits on the derived class: it covers what the class inherits, too.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public class ScopeAnnotatedDerived : ScopeBase
    {
        public string? Own { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public class ScopeAnnotatedBase : Entity
    {
        public string? BaseText { get; set; }
    }

    /// <summary>No attribute of its own: it inherits the class-level attribute of its base.</summary>
    public class ScopeInheriting : ScopeAnnotatedBase
    {
        public string? DerivedText { get; set; }
    }

    /// <summary>Declares the attribute again, with another encoding.</summary>
    [TableValueSplit(SplitEncoding.Gzip)]
    public class ScopeRedeclaring : ScopeAnnotatedBase
    {
        public string? DerivedText { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public class ScopePropertyWins : Entity
    {
        [TableValueSplit(SplitEncoding.Gzip, Offload = BlobOffload.Never)]
        public string? Special { get; set; }

        public string? Ordinary { get; set; }
    }

    public class ScopeVirtualBase : Entity
    {
        [TableValueSplit(SplitEncoding.Gzip, Offload = BlobOffload.Never)]
        public virtual string? Body { get; set; }

        [TableValueSplit(SplitEncoding.Gzip, Offload = BlobOffload.Never)]
        public string? Notes { get; set; }
    }

    public class ScopeOverriding : ScopeVirtualBase
    {
        public override string? Body { get; set; }
    }

    public class ScopeHiding : ScopeVirtualBase
    {
        public new string? Notes { get; set; }
    }

    /// <summary>Hides a property that carries its own attribute, under a class-level attribute.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public class ScopeHidingAnnotated : ScopeVirtualBase
    {
        public new string? Notes { get; set; }

        public string? Extra { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public class ScopeClassLevelVirtualBase : Entity
    {
        public virtual string? Body { get; set; }

        public string? Notes { get; set; }
    }

    public class ScopeClassLevelRedeclaring : ScopeClassLevelVirtualBase
    {
        public override string? Body { get; set; }

        public new string? Notes { get; set; }
    }

    /// <summary>Implements <see cref="IEntity"/> directly: the class declares the id itself.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public class ScopeDirectEntity : IEntity
    {
        public string Id { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public string? ETag { get; set; }

        public DateTimeOffset? Timestamp { get; set; }

        public string? Body { get; set; }

        public string? Title { get; set; }

        [IgnoreDataMember]
        public string? Ignored { get; set; }

        public string? Computed => Title is null ? null : Title + "!";
    }

    /// <summary>A flattened object under a class-level attribute, with a member that is never a column.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public class ScopeIndexed
    {
        private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

        public string? Label { get; set; }

        public string? this[string key]
        {
            get => values.GetValueOrDefault(key);
            set => values[key] = value;
        }
    }

    public class ScopeIndexedDoc : Entity
    {
        public ScopeIndexed? Bag { get; set; }
    }

    /// <summary>A property named with the column-path delimiter, which the writer refuses.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public class ScopeDelimiterNamed : Entity
    {
        public string? Label { get; set; }

        public string? Foo_Bar { get; set; }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static UnifiedTableStorageOptions Offloading() =>
        new() { BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 } };

    private static string[] OptedIn(Type type) =>
        TypeMetadataCache.GetMetadata(type).SplitOptions.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    private static string[] Columns(Type type) =>
        TypeMetadataCache.LargeValueColumns(type).OrderBy(c => c, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Creates the entity in a <see cref="TableStorage{T}"/> (over a mocked <see cref="TableClient"/>) and
    /// in an <see cref="InMemoryStorage{T}"/>, reads it back from each, and hands each store's stored row
    /// and read entity to <paramref name="assert"/>.
    /// </summary>
    private static async Task OnBothStores<T>(Func<T> make, Action<TableEntity, T> assert, UnifiedTableStorageOptions? options = null)
        where T : class, IEntity, new()
    {
        var blobs = options?.BlobOffload is null ? null : new InMemoryOffloadBlobStore();
        TableEntity? written = null;
        using (var writer = new StorageHarness<T>(options: options, blobs: blobs))
        {
            writer.Table.AddEntityAsync(Arg.Do<TableEntity>(e => written = e), Arg.Any<CancellationToken>()).Returns(Mocks.EtagResponse());
            await writer.Store.CreateAsync(make());
            // 40,000 characters fit one row: one insert, no transaction.
            Assert.Equal(["CreateIfNotExistsAsync", "AddEntityAsync"], writer.Table.ReceivedCalls().Select(c => c.GetMethodInfo().Name));
        }
        Assert.NotNull(written);
        written.ETag = new ETag("W/\"etag1\"");
        using (var reader = new StorageHarness<T>(options: options, blobs: blobs))
        {
            reader.SetupGet(written);
            assert(written, (await reader.Store.OneAsync(Id))!);
        }

        var memory = new InMemoryStorage<T>(null, options);
        await memory.CreateAsync(make());
        assert(memory.PhysicalRow("p", "r")!, (await memory.OneAsync(Id))!);
    }

    private static void AssertSplit(TableEntity row, string column, string encoding)
    {
        Assert.True(row.ContainsKey(column + "__chunks"), $"'{column}' is not stored as split cells: {string.Join(", ", row.Keys)}");
        Assert.Equal(encoding, row[column + "__enc"]);
        Assert.False(row.ContainsKey(column));
        Assert.False(row.ContainsKey(column + "__GZip"));
        Assert.False(row.ContainsKey(column + "__Json"));
        Assert.False(row.ContainsKey(column + "__blob"));
    }

    // ── a class-level attribute covers declared and inherited properties ─────

    [Fact]
    public async Task ClassLevelAttribute_CoversThePropertiesTheClassInherits()
    {
        Assert.Equal(["Inherited", "InheritedBytes", "InheritedItems", "Own"], OptedIn(typeof(ScopeAnnotatedDerived)));
        Assert.Equal(["Inherited", "InheritedBytes", "InheritedItems", "Own"], Columns(typeof(ScopeAnnotatedDerived)));
        // The base class on its own opts into nothing.
        Assert.Empty(OptedIn(typeof(ScopeBase)));
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(ScopeBase)));

        await OnBothStores(
            () => new ScopeAnnotatedDerived
            {
                Id = Id,
                Inherited = BigText,
                InheritedBytes = BigBytes,
                InheritedItems = BigItems,
                InheritedNumber = 7,
                Own = OtherText,
            },
            (row, read) =>
            {
                AssertSplit(row, "Inherited", "text");
                AssertSplit(row, "InheritedBytes", "binary");
                AssertSplit(row, "InheritedItems", "text");
                AssertSplit(row, "Own", "text");
                Assert.Equal(7, row["InheritedNumber"]);
                Assert.Equal(BigText, read.Inherited);
                Assert.Equal(BigBytes, read.InheritedBytes);
                Assert.Equal(BigItems, read.InheritedItems);
                Assert.Equal(OtherText, read.Own);
                Assert.Equal(7, read.InheritedNumber);
            });

        // An opted-in property may be stored as split cells, so it cannot be filtered; a scalar still can.
        Assert.Throws<NotSupportedException>(() => TableFilterTranslator.Translate<ScopeAnnotatedDerived>(x => x.Inherited == "a"));
        Assert.Equal("InheritedNumber eq 7", TableFilterTranslator.Translate<ScopeAnnotatedDerived>(x => x.InheritedNumber == 7));
    }

    [Fact]
    public async Task DerivedClass_InheritsTheClassLevelAttributeOfItsBase_ForAllItsProperties()
    {
        Assert.Equal(["BaseText", "DerivedText"], OptedIn(typeof(ScopeInheriting)));
        Assert.Equal(SplitEncoding.Text, TypeMetadataCache.GetMetadata(typeof(ScopeInheriting)).SplitOptions["DerivedText"].Encoding);

        await OnBothStores(
            () => new ScopeInheriting { Id = Id, BaseText = BigText, DerivedText = OtherText },
            (row, read) =>
            {
                AssertSplit(row, "BaseText", "text");
                AssertSplit(row, "DerivedText", "text");
                Assert.Equal(BigText, read.BaseText);
                Assert.Equal(OtherText, read.DerivedText);
            });
    }

    [Fact]
    public async Task DerivedClassThatDeclaresTheAttributeAgain_KeepsTheBasePropertiesOptedIn()
    {
        // The nearest class-level attribute applies to every property of the class, inherited ones included.
        var options = TypeMetadataCache.GetMetadata(typeof(ScopeRedeclaring)).SplitOptions;
        Assert.Equal(["BaseText", "DerivedText"], options.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(SplitEncoding.Gzip, options["BaseText"].Encoding);
        Assert.Equal(SplitEncoding.Gzip, options["DerivedText"].Encoding);
        // The base class keeps its own attribute.
        Assert.Equal(SplitEncoding.Text, TypeMetadataCache.GetMetadata(typeof(ScopeAnnotatedBase)).SplitOptions["BaseText"].Encoding);

        await OnBothStores(
            () => new ScopeRedeclaring { Id = Id, BaseText = BigText, DerivedText = OtherText },
            (row, read) =>
            {
                AssertSplit(row, "BaseText", "gzip");
                AssertSplit(row, "DerivedText", "gzip");
                Assert.Equal(BigText, read.BaseText);
                Assert.Equal(OtherText, read.DerivedText);
            });
    }

    // ── a property-level attribute wins ──────────────────────────────────────

    [Fact]
    public async Task PropertyLevelAttribute_WinsOverTheClassLevelOne_EncodingAndOffload()
    {
        var options = TypeMetadataCache.GetMetadata(typeof(ScopePropertyWins)).SplitOptions;
        Assert.Equal(SplitEncoding.Gzip, options["Special"].Encoding);
        Assert.Equal(BlobOffload.Never, options["Special"].Offload);
        Assert.Equal(SplitEncoding.Text, options["Ordinary"].Encoding);
        Assert.Equal(BlobOffload.AboveThreshold, options["Ordinary"].Offload);

        // Encoding: the property's own Gzip, not the class's Text.
        await OnBothStores(
            () => new ScopePropertyWins { Id = Id, Special = BigText, Ordinary = OtherText },
            (row, read) =>
            {
                AssertSplit(row, "Special", "gzip");
                AssertSplit(row, "Ordinary", "text");
                Assert.Equal(BigText, read.Special);
                Assert.Equal(OtherText, read.Ordinary);
            });

        // Offload: with a 1,000-byte threshold the class-level property goes to a blob, the property
        // that says Never stays in the table.
        await OnBothStores(
            () => new ScopePropertyWins { Id = Id, Special = BigText, Ordinary = OtherText },
            (row, read) =>
            {
                AssertSplit(row, "Special", "gzip");
                Assert.True(row.ContainsKey("Ordinary__blob"));
                Assert.False(row.ContainsKey("Ordinary__chunks"));
                Assert.Equal(BigText, read.Special);
                Assert.Equal(OtherText, read.Ordinary);
            },
            Offloading());
    }

    // ── a re-declared property keeps its opt-in ──────────────────────────────

    [Fact]
    public async Task OverriddenProperty_KeepsThePropertyLevelOptInOfItsBase()
    {
        var options = TypeMetadataCache.GetMetadata(typeof(ScopeOverriding)).SplitOptions;
        Assert.Equal(["Body", "Notes"], options.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(SplitEncoding.Gzip, options["Body"].Encoding);
        Assert.Equal(BlobOffload.Never, options["Body"].Offload);

        await OnBothStores(
            () => new ScopeOverriding { Id = Id, Body = BigText, Notes = OtherText },
            (row, read) =>
            {
                AssertSplit(row, "Body", "gzip");
                AssertSplit(row, "Notes", "gzip");
                Assert.Equal(BigText, read.Body);
                Assert.Equal(OtherText, read.Notes);
            },
            Offloading());
    }

    [Fact]
    public async Task HiddenProperty_KeepsThePropertyLevelOptInOfTheOneItHides()
    {
        var options = TypeMetadataCache.GetMetadata(typeof(ScopeHiding)).SplitOptions;
        Assert.Equal(["Body", "Notes"], options.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(SplitEncoding.Gzip, options["Notes"].Encoding);
        Assert.Equal(BlobOffload.Never, options["Notes"].Offload);
        Assert.Equal(["Body", "Notes"], Columns(typeof(ScopeHiding)));

        // Offload is configured: only the Never of the hidden property keeps the value in the table.
        await OnBothStores(
            () => new ScopeHiding { Id = Id, Body = BigText, Notes = OtherText },
            (row, read) =>
            {
                AssertSplit(row, "Body", "gzip");
                AssertSplit(row, "Notes", "gzip");
                Assert.Equal(BigText, read.Body);
                Assert.Equal(OtherText, read.Notes);
                Assert.Null(((ScopeVirtualBase)read).Notes);
            },
            Offloading());
    }

    [Fact]
    public async Task HiddenProperty_KeepsItsPropertyLevelOptIn_UnderAClassLevelAttribute()
    {
        var options = TypeMetadataCache.GetMetadata(typeof(ScopeHidingAnnotated)).SplitOptions;
        Assert.Equal(["Body", "Extra", "Notes"], options.Keys.OrderBy(k => k, StringComparer.Ordinal));
        // The opt-in that applied to the hidden property (Gzip, Never) wins over the class's Text.
        Assert.Equal(SplitEncoding.Gzip, options["Notes"].Encoding);
        Assert.Equal(BlobOffload.Never, options["Notes"].Offload);
        Assert.Equal(SplitEncoding.Gzip, options["Body"].Encoding);
        Assert.Equal(SplitEncoding.Text, options["Extra"].Encoding);

        await OnBothStores(
            () => new ScopeHidingAnnotated { Id = Id, Notes = BigText, Extra = OtherText },
            (row, read) =>
            {
                AssertSplit(row, "Notes", "gzip");
                AssertSplit(row, "Extra", "text");
                Assert.Equal(BigText, read.Notes);
                Assert.Equal(OtherText, read.Extra);
            });
    }

    [Fact]
    public async Task RedeclaredProperties_KeepTheClassLevelOptInOfTheirBase()
    {
        Assert.Equal(["Body", "Notes"], OptedIn(typeof(ScopeClassLevelRedeclaring)));

        await OnBothStores(
            () => new ScopeClassLevelRedeclaring { Id = Id, Body = BigText, Notes = OtherText },
            (row, read) =>
            {
                AssertSplit(row, "Body", "text");
                AssertSplit(row, "Notes", "text");
                Assert.Equal(BigText, read.Body);
                Assert.Equal(OtherText, read.Notes);
            });
    }

    // ── never the entity id, ETag, Timestamp or a member that is not a column ─

    [Fact]
    public async Task ClassLevelAttribute_NeverOptsInTheEntityId_OrAMemberThatIsNotAColumn()
    {
        // Body and Title are the only ordinary string columns; Id, ETag, Timestamp, the ignored and the
        // computed property are not opted in.
        Assert.Equal(["Body", "Title"], OptedIn(typeof(ScopeDirectEntity)));
        Assert.Equal(["Body", "Title"], Columns(typeof(ScopeDirectEntity)));
        // An inherited Entity.Id is not opted in either, now that inherited properties are covered.
        Assert.DoesNotContain("Id", OptedIn(typeof(ScopeAnnotatedDerived)));
        Assert.DoesNotContain("Id", OptedIn(typeof(ClassLevelSplitEntity)));

        // The id stays an ordinary, filterable key column.
        Assert.Equal("(PartitionKey eq 'p' and RowKey eq 'r')", TableFilterTranslator.Translate<ScopeDirectEntity>(x => x.Id == Id));
        Assert.Equal("(PartitionKey eq 'p' and RowKey eq 'r')", TableFilterTranslator.Translate<ScopeAnnotatedDerived>(x => x.Id == Id));

        await OnBothStores(
            () => new ScopeDirectEntity { Id = Id, Body = BigText, Title = "t", Ignored = "x" },
            (row, read) =>
            {
                AssertSplit(row, "Body", "text");
                Assert.Equal(Id, row["Id"]);
                Assert.Equal("t", row["Title"]);
                Assert.DoesNotContain(row.Keys, k => k.StartsWith("Ignored", StringComparison.Ordinal) || k.StartsWith("Computed", StringComparison.Ordinal));
                Assert.DoesNotContain(row.Keys, k => k.StartsWith("ETag", StringComparison.Ordinal) || k.StartsWith("Id__", StringComparison.Ordinal));
                Assert.Equal(Id, read.Id);
                Assert.Equal(BigText, read.Body);
            });
    }

    [Fact]
    public async Task FilterOnTheId_OfAClassLevelOptedInEntity_WorksOnBothStores()
    {
        // TableStorage<T>: the filter reaches the service as a key filter.
        using var harness = new StorageHarness<ScopeDirectEntity>();
        harness.SetupQueryByFilter();
        Assert.Empty(await harness.Store.QueryAsync(x => x.Id == Id, "p"));
        Assert.Equal("PartitionKey eq 'p' and ((PartitionKey eq 'p' and RowKey eq 'r'))", harness.LastQueryFilter);
        Assert.False(await harness.Store.AnyAsync(x => x.Id == Id));
        Assert.Equal("(PartitionKey eq 'p' and RowKey eq 'r')", harness.LastQueryFilter);

        // InMemoryStorage<T>: the same predicate finds the entity, whose large value is intact.
        var memory = new InMemoryStorage<ScopeDirectEntity>();
        await memory.CreateAsync(new ScopeDirectEntity { Id = Id, Body = BigText, Title = "t" });
        await memory.CreateAsync(new ScopeDirectEntity { Id = "p|other", Body = "small", Title = "u" });
        var found = Assert.Single(await memory.QueryAsync(x => x.Id == Id, "p"));
        Assert.Equal(BigText, found.Body);
        Assert.True(await memory.AnyAsync(x => x.Id == Id));
        Assert.False(await memory.AnyAsync(x => x.Id == "p|missing"));
    }

    [Fact]
    public void ClassLevelAttribute_NeverOptsInAnIndexer()
    {
        Assert.Equal(["Label"], OptedIn(typeof(ScopeIndexed)));
        Assert.Equal(["Bag_Label"], Columns(typeof(ScopeIndexedDoc)));
    }

    [Fact]
    public void ClassLevelAttribute_NeverOptsInAPropertyTheWriterRefuses()
    {
        // '_' is the column-path delimiter: the writer refuses a property named with it, so it is no
        // column and the class-level attribute does not list it as one.
        Assert.Equal(["Label"], OptedIn(typeof(ScopeDelimiterNamed)));
        Assert.Equal(["Label"], Columns(typeof(ScopeDelimiterNamed)));
        Assert.Throws<SerializationException>(() => new ScopeDelimiterNamed { Id = Id, Label = "x" }.ToTableEntity("p", "r"));
    }
}
