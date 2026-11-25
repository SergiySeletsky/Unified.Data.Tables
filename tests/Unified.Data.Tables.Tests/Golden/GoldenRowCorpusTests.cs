using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.Golden;

/// <summary>
/// Golden corpus, part (a): the rows 0.9.2 stores. Each case serializes a fixed entity through the entry
/// point a store writes through, sends the rows through the real Azure SDK into a transport that records
/// the request body, and holds the cells of that body (name, Edm type, value) to a checked-in file. It
/// then hands the same body back to the SDK as the service would return it, reads the entity, and holds
/// what was read to the file as well; writing the entity that was read must give the same bytes again.
/// </summary>
/// <remarks>
/// <para>Not in the corpus, with the reason: <c>CreatedAt</c>/<c>UpdatedAt</c> as a store stamps them
/// (the clock; the corpus fixes them on the entity instead, which is what the store serializes after
/// stamping), a row's ETag and <c>Timestamp</c> (the service's, never stored cells), GZip bytes (see
/// <see cref="GoldenWire"/>; a GZip value is therefore not offloaded here, its blob is named by those bytes),
/// <see cref="TableBlobContent"/> (its row cell is the same reference cell an offloaded value leaves), and the
/// trim policies of <see cref="OversizedCellPolicy"/> (process-wide state other tests set).</para>
/// <para>Rows of one write go on the wire in one entity-group transaction; the corpus sends each as an
/// insert of its own, because a transaction body carries random boundaries. The JSON of an entity is the
/// same in both.</para>
/// </remarks>
// The GZip fallback consults the process-wide OversizedCellPolicy; the cases need its default (Throw).
[Collection("OversizedCellPolicy")]
public class GoldenRowCorpusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // CR LF, TAB, U+0001, U+007F (DEL), U+00A0 (no-break space) and U+2028 (line separator), by code so
    // that no editor or tool can rewrite them.
    private static readonly string ControlText =
        "line one" + (char)0x0D + (char)0x0A + "line two" + (char)0x09 + "tabbed" + (char)0x01 + (char)0x7F + (char)0xA0 + (char)0x2028;

    // ── Typed stores of a type that is not large-value aware: one plain row ──────────────────────────

    [Fact]
    public Task Scalars() => PlainAsync(
        "scalars", "Every scalar type, one property each. Native cells, except the ones with no Edm type of their own.",
        new GoldenScalars
        {
            Id = "golden|scalars",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Later,
            ETag = "W/\"never-a-column\"",
            Timestamp = GoldenData.Later,
            Text = "Zażółć \"gęślą\" jaźń — it's <b>bold</b> & 100% \\ / 😀",
            EmptyText = "",
            Flag = true,
            Int32 = -123_456_789,
            Int64 = 1_234_567_890_123_456_789,
            Double = 3.141592653589793,
            Decimal = 1234.56m,
            Guid = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
            DateTime = new DateTime(2024, 2, 29, 23, 59, 58, DateTimeKind.Utc).AddTicks(7654321),
            DateTimeOffset = GoldenData.Instant,
            DateTimeOffsetWithOffset = new DateTimeOffset(2025, 3, 14, 17, 9, 26, TimeSpan.FromHours(2)).AddTicks(5358979),
            TimeSpan = new TimeSpan(1, 2, 3, 4, 567),
            Bytes = [0, 1, 2, 3, 127, 128, 254, 255],
            Colour = GoldenColour.Green,
            Access = GoldenAccess.Read | GoldenAccess.Admin,
            UInt32 = 4_000_000_000,
            UInt64 = 9_000_000_000_000_000_000,
            Byte = 200,
            Int16 = -12_345,
            Single = 1.5f,
            Char = 'Ж',
        });

    [Fact]
    public Task NullableScalars_Set() => PlainAsync(
        "nullable-scalars-set", "The nullable form of every scalar, each holding a value: the cells of the non-nullable form.",
        new GoldenNullableScalars
        {
            Id = "golden|nullable-set",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Instant,
            Text = "text",
            Flag = false,
            Int32 = 0,
            Int64 = -1,
            Double = -2.5,
            Decimal = 0.5m,
            Guid = new Guid("7c9e6679-7425-40de-944b-e07fc1f90ae7"),
            DateTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DateTimeOffset = GoldenData.Later,
            TimeSpan = TimeSpan.FromMinutes(90),
            Bytes = [42],
            Colour = GoldenColour.Blue,
            UInt32 = 7,
            UInt64 = 8,
        });

    [Fact]
    public Task NullableScalars_Unset() => PlainAsync(
        "nullable-scalars-unset", "The nullable form of every scalar, all null: no cell is stored for any of them.",
        new GoldenNullableScalars { Id = "golden|nullable-unset", CreatedAt = GoldenData.Instant, UpdatedAt = GoldenData.Instant });

    [Fact]
    public Task ScalarEdges() => PlainAsync(
        "scalar-edges",
        "Values at the edge of their type, and the ones stored as something else: a default date as 1601-01-01, an unsigned " +
        "value above the signed range wrapped to a negative Int64, NaN as an annotated string. All of them read back as written.",
        new GoldenScalarEdges
        {
            Id = "golden|scalar-edges",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Instant,
            Int32Min = int.MinValue,
            Int32Max = int.MaxValue,
            Int64Min = long.MinValue,
            Int64Max = long.MaxValue,
            UInt32Max = uint.MaxValue,
            UInt64Max = ulong.MaxValue,
            DoubleTenth = 0.1,
            DoubleWhole = 42.0,
            DoubleNegativeZero = -0.0,
            DoubleLarge = 1.7976931348623157E+308,
            DoubleEpsilon = double.Epsilon,
            DoubleNaN = double.NaN,
            DecimalMoney = 1234.56m,
            DecimalCents = 19.99m,
            DecimalTenth = 0.1m,
            DecimalWhole = 1000m,
            DecimalNegative = -0.000001m,
            DateTimeDefault = default,
            DateTimeUnspecified = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Unspecified),
            DateTimeOffsetDefault = default,
            GuidEmpty = Guid.Empty,
            BytesEmpty = [],
            TimeSpanNegative = -new TimeSpan(0, 0, 0, 1, 500),
            TimeSpanDays = TimeSpan.FromDays(400),
            TextWhitespace = "  leading and trailing  ",
            TextControl = ControlText,
        });

    // No golden row: the serializer produces the cell, and the SDK refuses to write it into a request
    // body, so nothing reaches the wire. Pinned because a provider that is not the SDK would store it.
    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task NonFiniteDouble_NeverReachesTheWire(double value)
    {
        var row = LargeValueRows.ToPlainTableEntity(
            new GoldenNullableScalars { Id = "golden|non-finite", Double = value }, "golden", "non-finite", typeof(GoldenNullableScalars));
        Assert.Equal(value, row["Double"]);

        await Assert.ThrowsAsync<ArgumentException>(() => GoldenWire.ToWireAsync(row, Ct));
    }

    [Fact]
    public Task DecimalBeyondDoublePrecision() => PlainAsync(
        "decimal-beyond-double-precision",
        "A decimal with more digits than a double holds: stored as the nearest double and read back rounded to 15 significant " +
        "digits, so writing what was read stores another double. The second row is that rewrite.",
        new GoldenNullableScalars
        {
            Id = "golden|decimal-precision",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Instant,
            Decimal = 123456789.123456789123456789m,
        },
        readsAsWritten: false,
        rewritesTheSame: false);

    [Fact]
    public Task Nested() => PlainAsync(
        "nested", "Nested objects: flattened to Parent_Child columns at every depth; a positional record and a list are one JSON cell; a null object stores nothing.",
        new GoldenNested
        {
            Id = "golden|nested",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Later,
            Title = "Nested",
            Home = new GoldenAddress
            {
                City = "Kyiv",
                Country = "UA",
                Floor = 3,
                Door = GoldenColour.Blue,
                Street = new GoldenStreet { Name = "Khreshchatyk", Number = 22 },
                Tags = ["home", "primary"],
            },
            Work = null,
            Position = new GoldenPoint(50.4501, 30.5234),
            Previous = null,
        });

    [Fact]
    public Task Collections() => PlainAsync(
        "collections", "Collections and dictionaries: one __Json cell each, an empty one included; a null one stores nothing.",
        new GoldenCollections
        {
            Id = "golden|collections",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Instant,
            Tags = ["alpha", "beta", "with \"quotes\"", "üñí©ødé", ""],
            NoTags = [],
            NullTags = null,
            Numbers = [3, 1, 4, 1, 5, 9, 2, 6],
            Scores = new Dictionary<string, int> { ["first"] = 1, ["second"] = -2, ["Third Key"] = 3 },
            Streets = [new GoldenStreet { Name = "A", Number = 1 }, new GoldenStreet { Name = "B", Number = 2 }],
            Places = new Dictionary<string, GoldenPoint> { ["origin"] = new(0, 0), ["north"] = new(89.5, -0.25) },
            Colours = [GoldenColour.Red, GoldenColour.Blue],
            Labels = new Dictionary<string, string> { ["env"] = "prod", ["tier"] = "gold" },
        });

    [Fact]
    public Task Fallbacks() => PlainAsync(
        "json-and-gzip-fallbacks",
        "A list as __Json; a list whose JSON exceeds one cell and a string over 32,000 chars as __GZip (a string cell of base64 GZip bytes).",
        new GoldenFallbacks
        {
            Id = "golden|fallbacks",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Instant,
            SmallList = ["one", "two", "three"],
            LargeList = GoldenData.Items(2_000),
            ShortText = GoldenData.Text(32_000, seed: 11),
            LongText = GoldenData.Compressible(50_000),
        });

    [Fact]
    public async Task ColumnAliases()
    {
        var entity = new GoldenAliased
        {
            Id = "golden|aliases",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Later,
            NewName = "renamed",
            Tags = ["x", "y"],
            Label = "label",
        };
        var body = await PlainAsync(
            "column-aliases", "Renamed members: a write uses only the current column names.", entity);

        // The same row as a release before the renames stored it: every alias is read, in each cell form.
        var legacy = body
            .Replace("\"NewName\"", "\"OldName\"", StringComparison.Ordinal)
            .Replace("\"Tags__Json\"", "\"OldTags__Json\"", StringComparison.Ordinal)
            .Replace("\"Label\"", "\"Caption\"", StringComparison.Ordinal)
            .Replace("\"CreatedAt", "\"Created", StringComparison.Ordinal)
            .Replace("\"UpdatedAt", "\"Modified", StringComparison.Ordinal);
        Assert.NotEqual(body, legacy);
        var read = (await GoldenWire.FromWireAsync(legacy, Ct)).FromTableEntity<GoldenAliased>();

        GoldenFile.AssertMatches(
            "Rows/column-aliases.legacy-row.txt",
            "# The row of column-aliases.txt under the old column names, and what a read of it returns.\n" +
            GoldenWire.Render([legacy]) + GoldenWire.Describe(read));
        Assert.Equal(GoldenWire.Meaning(entity), GoldenWire.Meaning(read));
    }

    [Fact]
    public Task InterfaceEntity() => PlainAsync(
        "interface-entity", "An IEntity that does not derive Entity: the same columns; ETag, Timestamp and a get-only member are not stored.",
        new GoldenInterfaceEntity
        {
            Id = "golden|interface",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Later,
            ETag = "W/\"never-a-column\"",
            Timestamp = GoldenData.Later,
            Name = "interface only",
        });

    [Fact]
    public Task SingleKeyId() => PlainAsync(
        "single-key-id", "An id without a separator: PartitionKey and RowKey are both the id.",
        new GoldenNullableScalars { Id = "solo", CreatedAt = GoldenData.Instant, UpdatedAt = GoldenData.Instant, Text = "solo" });

    // ── Typed stores of a large-value aware type: a primary row and its continuation rows ────────────

    [Fact]
    public Task Split_ValuesThatFitOneCell() => SplitAsync(
        "split-inline", "[TableValueSplit] values small enough for one cell: ordinary cells, no split metadata.",
        new GoldenSplit
        {
            Id = "golden|split-inline",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Instant,
            Title = "inline",
            Body = GoldenData.Text(32_000, seed: 21),
            Image = GoldenData.Bytes(1_000, seed: 22),
            Items = ["a", "b"],
            Zipped = "short",
        },
        expectedRows: 1);

    [Fact]
    public Task Split_ValuesAcrossCells() => SplitAsync(
        "split-cells",
        "[TableValueSplit] values split across cells of the one row: text in 32,000-char chunks (never inside a surrogate pair), " +
        "binary in 64 KiB chunks, a list as JSON text chunks, and a GZip value.",
        new GoldenSplit
        {
            Id = "golden|split-cells",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Instant,
            Title = "cells",
            Body = GoldenData.Text(70_000, seed: 31),
            Image = GoldenData.Bytes(100_000, seed: 32),
            Items = GoldenData.Items(40, 1_000, seed: 33),
            Zipped = GoldenData.Compressible(300_000),
        },
        expectedRows: 1);

    [Fact]
    public Task Split_ValuesAcrossContinuationRows() => SplitAsync(
        "split-continuation-rows",
        "[TableValueSplit] values too large for one row: the chunks move to continuation rows (RowKey~0001, ...) that name their entity in _SplitOf.",
        new GoldenSplit
        {
            Id = "golden|split-rows",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Instant,
            Title = "rows",
            Body = GoldenData.Text(700_000, seed: 41),
            Image = GoldenData.Bytes(300_000, seed: 42),
            Items = null,
            Zipped = null,
        },
        expectedRows: null);

    [Fact]
    public Task Split_ValuesOffloadedToBlobs() => SplitAsync(
        "split-blob-offload",
        "[TableValueSplit] values with blob offload configured (threshold 50,000 bytes): a value whose stored form is larger " +
        "leaves a __blob reference cell and a blob named {table}/{partition}/{row}/{column}/{sha256}; a smaller one stays in cells.",
        new GoldenSplit
        {
            Id = "golden|split-offload",
            CreatedAt = GoldenData.Instant,
            UpdatedAt = GoldenData.Instant,
            Title = "offload",
            Body = GoldenData.Text(60_000, seed: 51),
            Image = GoldenData.Bytes(70_000, seed: 52),
            Items = GoldenData.Items(40, 1_000, seed: 53),
            Zipped = null,
        },
        expectedRows: 1,
        new BlobOffloadOptions { ThresholdBytes = 50_000 });

    // ── The public serializer and the polymorphic store ──────────────────────────────────────────────

    [Fact]
    public async Task PersistType()
    {
        var item = new GoldenOrderCancelled { Id = "order-7", Created = GoldenData.Instant, Reason = "out of stock", Severity = GoldenColour.Green };
        var row = item.ToTableEntity("golden", "persist-type", persistType: true);
        var body = await GoldenWire.ToWireAsync(row, Ct);

        var read = (await GoldenWire.FromWireAsync(body, Ct)).FromTableEntity();

        GoldenFile.AssertMatches(
            "Rows/persist-type.txt",
            "# ToTableEntity(persistType: true): the assembly-qualified type name in _TypeName; FromTableEntity() creates that type.\n" +
            TestAssemblyVersion(GoldenWire.Render([body])) + GoldenWire.Describe(read));
        Assert.Equal(GoldenWire.Meaning(item), GoldenWire.Meaning(read));
    }

    [Fact]
    public Task Polymorphic_DefaultDiscriminator() => PolymorphicAsync(
        "polymorphic-default-discriminator",
        "A polymorphic entry under the default discriminator (the assembly-qualified type name), with system columns.",
        new PolymorphicWrite<GoldenMessage>(
            new TableKey("stream-1", "0000000001"),
            new GoldenOrderPlaced
            {
                Id = "order-1",
                Created = GoldenData.Instant,
                Customer = "ACME",
                Total = 99.95m,
                Lines = ["2 x widget", "1 x gadget"],
                ShipTo = new GoldenAddress { City = "Lviv", Country = "UA", Street = new GoldenStreet { Name = "Rynok", Number = 1 } },
            },
            new Dictionary<string, object> { ["_IsPublished"] = false, ["_Sequence"] = 1L, ["_Source"] = "golden" }),
        options: null);

    [Fact]
    public Task Polymorphic_MappedDiscriminator() => PolymorphicAsync(
        "polymorphic-mapped-discriminator",
        "A polymorphic entry under a TypeDiscriminatorMap: the mapped name in _TypeName.",
        new PolymorphicWrite<GoldenMessage>(
            new TableKey("stream-1", "0000000002"),
            new GoldenOrderCancelled { Id = "order-1", Created = GoldenData.Later, Reason = "customer request", Severity = GoldenColour.Red }),
        new UnifiedTableStorageOptions
        {
            TypeDiscriminator = new TypeDiscriminatorMap().Map<GoldenOrderPlaced>("order.placed").Map<GoldenOrderCancelled>("order.cancelled"),
        });

    [Fact]
    public Task Polymorphic_Marker() => PolymorphicAsync(
        "polymorphic-marker",
        "A marker row: system columns only, no item and no _TypeName.",
        PolymorphicWrite<GoldenMessage>.Marker(
            new TableKey("stream-1", "head"),
            new Dictionary<string, object> { ["_Version"] = 2, ["_UpdatedAt"] = GoldenData.Later }),
        options: null);

    // ── The cases' machinery ─────────────────────────────────────────────────────────────────────────

    // The default discriminator is the assembly-qualified type name, which holds the version of the
    // assembly the type lives in: here the test assembly, versioned with the package. That version is
    // the one part of the token the corpus does not pin, or every release would change the file.
    private static string TestAssemblyVersion(string text) =>
        text.Replace(
            $"Version={typeof(GoldenMessage).Assembly.GetName().Version},", "Version={version of the test assembly},", StringComparison.Ordinal);

    // A store of a type that is not large-value aware writes every entity through ToPlainTableEntity
    // (TableStorage<T> and InMemoryStorage<T> alike) and reads it with FromTableEntity<T>.
    private static async Task<string> PlainAsync<T>(
        string name, string about, T entity, bool readsAsWritten = true, bool rewritesTheSame = true)
        where T : class, IEntity, new()
    {
        Assert.False(LargeValueTable.IsLargeValueAware(typeof(T)));
        var (pk, rk) = EntityId.Split(entity.Id);
        var body = await GoldenWire.ToWireAsync(LargeValueRows.ToPlainTableEntity(entity, pk, rk, typeof(T)), Ct);

        var read = (await GoldenWire.FromWireAsync(body, Ct)).FromTableEntity<T>();

        // A row read and written again is the same row, byte for byte; where 0.9.2 does not hold that,
        // the rewritten row is part of the file.
        var again = await GoldenWire.ToWireAsync(LargeValueRows.ToPlainTableEntity(read, pk, rk, typeof(T)), Ct);

        GoldenFile.AssertMatches(
            $"Rows/{name}.txt",
            $"# {about}\n" + GoldenWire.Render([body]) + GoldenWire.Describe(read) + (rewritesTheSame ? "" : GoldenWire.Render([again])));
        if (readsAsWritten)
            Assert.Equal(GoldenWire.Meaning(entity), GoldenWire.Meaning(read));
        if (rewritesTheSame)
            Assert.Equal(GoldenWire.Render([body]), GoldenWire.Render([again]));
        else
            Assert.NotEqual(GoldenWire.Render([body]), GoldenWire.Render([again]));
        return body;
    }

    // A store of a large-value aware type plans every write with LargeValueTable, uploads the blobs the
    // plan names (with blob offload configured), reads the primary row with its continuation rows,
    // assembles them and reads the logical row, its offloaded values downloaded.
    private static async Task SplitAsync<T>(string name, string about, T entity, int? expectedRows, BlobOffloadOptions? offload = null)
        where T : class, IEntity, new()
    {
        Assert.True(LargeValueTable.IsLargeValueAware(typeof(T)));
        var blobs = offload is null ? null : new InMemoryOffloadBlobStore();
        var large = new LargeValueTable(typeof(T).Name, offload, blobs, typeof(T));
        var (pk, rk) = EntityId.Split(entity.Id);
        var (bodies, blobPaths) = await PlanAsync(large, entity, pk, rk);
        Assert.Equal(blobs is null, blobPaths.Count == 0);
        if (expectedRows is { } rows)
            Assert.Equal(rows, bodies.Count);
        else
            Assert.True(bodies.Count > 1, "The case is meant to need continuation rows.");

        var stored = new List<TableEntity>();
        foreach (var body in bodies)
            stored.Add(await GoldenWire.FromWireAsync(body, Ct));
        Assert.Equal(bodies.Count - 1, LargeValueTable.PartCount(stored[0]));
        Assert.All(stored.Skip(1), part => Assert.True(LargeValueTable.IsPartOf(part, pk, rk)));
        var read = await large.ReadAsync<T>(LargeValueTable.Assemble(stored[0], stored.Skip(1)), Ct);

        // The blobs the row references: each one's name, and what it holds.
        var text = $"# {about}\n" + GoldenWire.Render(bodies);
        foreach (var path in blobPaths.Order(StringComparer.Ordinal))
        {
            await using var content = await blobs!.OpenReadAsync("golden", path, Ct);
            using var held = new MemoryStream();
            await content.CopyToAsync(held, Ct);
            text += $"blob\t{path}\tsha256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(held.ToArray()))},bytes={held.Length}\n";
        }

        GoldenFile.AssertMatches($"Rows/{name}.txt", text + GoldenWire.Describe(read));
        Assert.Equal(GoldenWire.Meaning(entity), GoldenWire.Meaning(read));

        Assert.Equal(GoldenWire.Render(bodies), GoldenWire.Render((await PlanAsync(large, read, pk, rk)).Bodies));
    }

    private static async Task<(IReadOnlyList<string> Bodies, IReadOnlyList<string> BlobPaths)> PlanAsync(
        LargeValueTable large, object entity, string pk, string rk)
    {
        await large.PrepareAsync(entity, Ct);
        var plan = await large.PlanAsync(
            entity, pk, rk, storedPartCount: 0, _ => Task.FromResult<IReadOnlyList<string>>([]), Ct);
        await large.UploadAsync(plan, Ct);
        return (await GoldenWire.ToWireAsync([plan.Primary, .. plan.Parts], Ct), plan.BlobPaths);
    }

    // Through the real PolymorphicTableStorage<TBase>: the row it hands the SDK for an insert, and the
    // entry it returns when the SDK reads that row back.
    private static async Task PolymorphicAsync(
        string name, string about, PolymorphicWrite<GoldenMessage> write, UnifiedTableStorageOptions? options)
    {
        using var harness = new PolymorphicHarness<GoldenMessage>("GoldenMessages", options);
        harness.SetupAdd();
        await harness.Store.InsertAsync(write, Ct);
        var body = await GoldenWire.ToWireAsync(harness.LastWrittenEntity!, Ct);

        harness.SetupGet(await GoldenWire.FromWireAsync(body, Ct));
        var entry = await harness.Store.GetAsync(write.Key, Ct);

        Assert.NotNull(entry);
        var text = $"# {about}\n" + GoldenWire.Render([body]) + GoldenWire.Describe(entry.Item)
            + $"read-discriminator\t{entry.Discriminator ?? "null"}\n";
        foreach (var column in entry.Columns.OrderBy(c => c.Key, StringComparer.Ordinal))
            text += GoldenWire.DescribeColumn(column.Key, column.Value);
        GoldenFile.AssertMatches($"Rows/{name}.txt", TestAssemblyVersion(text));

        Assert.Equal(write.Key, entry.Key);
        if (write.Item is null)
            Assert.Null(entry.Item);
        else
            Assert.Equal(GoldenWire.Meaning(write.Item), GoldenWire.Meaning(entry.Item!));
        foreach (var column in write.SystemColumns ?? new Dictionary<string, object>())
            Assert.Equal(column.Value, entry.Columns[column.Key]);
    }
}
