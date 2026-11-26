#pragma warning disable UDT0001 // the rows of the corpus are put into the database through the experimental raw table
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Data.Tables;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Sqlite;
using Unified.Data.Tables.Tests.Golden;

namespace Unified.Data.Tables.Tests.Sqlite;

/// <summary>
/// The golden corpus of 0.9.2 (Golden/Data/Rows, read here, never written) through the SQLite provider:
/// the rows a release stored on the wire, put into a SQLite database, come out of it as the same rows,
/// cell for cell and type for type, and the stores read them as the very entities the corpus records.
/// </summary>
/// <remarks>
/// A corpus file lists the cells of each row with their Edm type and JSON value, then what a read
/// returned. Where every value is spelled out, the rows are rebuilt from the file itself. Five files
/// abbreviate values too long to print (a hash and a length); for those the entity of the corpus case is
/// built again from its deterministic generators and written through the typed store, and everything the
/// file holds (rows, blobs, the entity read) must come out identical.
/// </remarks>
// The GZip fallback consults the process-wide OversizedCellPolicy; the cases need its default (Throw).
[Collection("OversizedCellPolicy")]
public sealed class SqliteGoldenCorpusTests : IDisposable
{
    private readonly TempDatabase database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => database.Dispose();

    // The files whose rows cannot be rebuilt from their text: covered by RebuiltCase below instead.
    private static readonly string[] Abbreviated =
        ["json-and-gzip-fallbacks", "split-blob-offload", "split-cells", "split-continuation-rows", "split-inline"];

    private static readonly string TestAssemblyVersion = $"Version={typeof(GoldenMessage).Assembly.GetName().Version},";
    private const string VersionPlaceholder = "Version={version of the test assembly},";

    public static IEnumerable<object[]> SpelledOutFiles() =>
        GoldenCases().Where(name => !Abbreviated.Contains(name)).Select(name => new object[] { name });

    private static IEnumerable<string> GoldenCases()
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "Golden", "Data", "Rows");
        return Directory.GetFiles(probe, "*.txt").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal);
    }

    [Fact]
    public void EveryFileOfTheCorpus_IsCoveredByOneOfTheTwoWays()
    {
        var cases = GoldenCases().ToList();

        Assert.True(cases.Count >= 20, "The row corpus was not copied beside the tests.");
        Assert.All(Abbreviated, name => Assert.Contains(name, cases));
        // A file is rebuilt from its text exactly when every value in it is spelled out.
        foreach (var name in cases)
            Assert.Equal(!Abbreviated.Contains(name), Parse(Lines(name)) is not null);
    }

    [Theory]
    [MemberData(nameof(SpelledOutFiles))]
    public async Task ARowOfTheCorpus_PutIntoSqlite_ComesOutTheSameRow_AndIsReadAsTheSameEntity(string name)
    {
        var lines = Lines(name);
        var firstRead = lines.FindIndex(l => l.StartsWith("read\t", StringComparison.Ordinal));
        var stored = lines.Take(firstRead).ToList();
        var rows = Parse(stored)!;
        var read = lines[firstRead].Split('\t');
        var type = read[1] == "null" ? null : typeof(GoldenMessage).Assembly.GetType(read[1], throwOnError: true)!;
        var polymorphic = name.StartsWith("polymorphic-", StringComparison.Ordinal);
        var table = polymorphic ? "GoldenMessages" : name == "persist-type" ? "PersistType" : type!.Name;

        var account = database.Open();
        await account.Tables.CreateTableAsync(table, Ct);
        await account.Tables.GetTable(table).SubmitAsync([.. rows.Select(row => RawOperation.Insert(row))], Ct);
        account.Dispose();

        // The rows in the file again, from another account: on the wire they are what the corpus holds.
        var reopened = database.Open();
        var physical = await reopened.Database.GetTable(table).ReadAsync(RowRange.All, 100, Ct);
        var rendered = GoldenWire.Render(await GoldenWire.ToWireAsync(physical.Select(r => r.Data), Ct));
        Assert.Equal(string.Join("\n", stored) + "\n", rendered.Replace(TestAssemblyVersion, VersionPlaceholder, StringComparison.Ordinal));

        // And the store reads them as the entity the corpus says a read returns.
        var expected = string.Join("\n", lines.Skip(firstRead).TakeWhile(l => l.StartsWith("read", StringComparison.Ordinal))) + "\n";
        string actual;
        if (polymorphic)
        {
            var options = name == "polymorphic-mapped-discriminator"
                ? new StorageOptions { TypeDiscriminator = new TypeDiscriminatorMap().Map<GoldenOrderPlaced>("order.placed").Map<GoldenOrderCancelled>("order.cancelled") }
                : null;
            var entry = (await new SqlitePolymorphicStorage<GoldenMessage>(reopened, table, options).GetAsync(new TableKey(rows[0].PartitionKey, rows[0].RowKey), Ct))!;
            actual = GoldenWire.Describe(entry.Item) + $"read-discriminator\t{entry.Discriminator ?? "null"}\n"
                + string.Concat(entry.Columns.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => GoldenWire.DescribeColumn(c.Key, c.Value)));
        }
        else if (name == "persist-type")
        {
            actual = GoldenWire.Describe(physical[0].Data.FromTableEntity());
        }
        else
        {
            var id = rows[0].Cells.TryGetValue("Id", out var cell) ? cell.AsString() : rows[0].PartitionKey + "|" + rows[0].RowKey;
            var entity = await (Task<object?>)typeof(SqliteGoldenCorpusTests)
                .GetMethod(nameof(ReadTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type!).Invoke(null, [reopened, id])!;
            actual = GoldenWire.Describe(entity);
        }
        Assert.Equal(expected, actual.Replace(TestAssemblyVersion, VersionPlaceholder, StringComparison.Ordinal));
    }

    private static async Task<object?> ReadTypedAsync<T>(SqliteStorageAccount account, string id) where T : class, IEntity, new() =>
        await new SqliteStorage<T>(account).OneAsync(id, Ct);

    [Fact]
    public Task Fallbacks_WrittenThroughTheTypedStore_AreTheCorpusFile() => RebuiltCase(
        "json-and-gzip-fallbacks",
        new GoldenFallbacks
        {
            Id = "golden|fallbacks",
            SmallList = ["one", "two", "three"],
            LargeList = GoldenData.Items(2_000),
            ShortText = GoldenData.Text(32_000, seed: 11),
            LongText = GoldenData.Compressible(50_000),
        });

    [Fact]
    public Task SplitInline_WrittenThroughTheTypedStore_IsTheCorpusFile() => RebuiltCase(
        "split-inline",
        new GoldenSplit
        {
            Id = "golden|split-inline",
            Title = "inline",
            Body = GoldenData.Text(32_000, seed: 21),
            Image = GoldenData.Bytes(1_000, seed: 22),
            Items = ["a", "b"],
            Zipped = "short",
        });

    [Fact]
    public Task SplitCells_WrittenThroughTheTypedStore_AreTheCorpusFile() => RebuiltCase(
        "split-cells",
        new GoldenSplit
        {
            Id = "golden|split-cells",
            Title = "cells",
            Body = GoldenData.Text(70_000, seed: 31),
            Image = GoldenData.Bytes(100_000, seed: 32),
            Items = GoldenData.Items(40, 1_000, seed: 33),
            Zipped = GoldenData.Compressible(300_000),
        });

    [Fact]
    public Task SplitContinuationRows_WrittenThroughTheTypedStore_AreTheCorpusFile() => RebuiltCase(
        "split-continuation-rows",
        new GoldenSplit
        {
            Id = "golden|split-rows",
            Title = "rows",
            Body = GoldenData.Text(700_000, seed: 41),
            Image = GoldenData.Bytes(300_000, seed: 42),
            Items = null,
            Zipped = null,
        });

    [Fact]
    public Task SplitBlobOffload_WrittenThroughTheTypedStore_IsTheCorpusFile_WithItsBlobsInTheDatabase() => RebuiltCase(
        "split-blob-offload",
        new GoldenSplit
        {
            Id = "golden|split-offload",
            Title = "offload",
            Body = GoldenData.Text(60_000, seed: 51),
            Image = GoldenData.Bytes(70_000, seed: 52),
            Items = GoldenData.Items(40, 1_000, seed: 53),
            Zipped = null,
        },
        new OffloadOptions { ThresholdBytes = 50_000 });

    // The entity of a corpus case, written by the typed store (which stamps CreatedAt and UpdatedAt from
    // its clock: the instant the corpus fixed on the entity), then everything the file records taken out
    // of the database again: the physical rows, the blobs they refer to, and the entity a read returns.
    private async Task RebuiltCase<T>(string name, T entity, OffloadOptions? offload = null) where T : class, IEntity, new()
    {
        void Configure(SqliteStorageOptions options)
        {
            options.TimeProvider = new FixedClock(GoldenData.Instant);
            options.Offload = offload;
        }
        var (pk, rk) = EntityId.Split(entity.Id);
        var account = database.Open(Configure);
        await new SqliteStorage<T>(account).CreateAsync(entity, Ct);
        account.Dispose();

        var reopened = database.Open(Configure);
        var physical = await reopened.Database.GetTable(typeof(T).Name).ReadAsync(new RowRange(pk), 100, Ct);
        var text = GoldenWire.Render(await GoldenWire.ToWireAsync(physical.Select(r => r.Data), Ct));
        if (offload is not null)
        {
            var blobs = new SqliteOffloadBlobStore(reopened.Database, offload.ContainerName);
            var names = new List<string>();
            await foreach (var (blob, _) in blobs.ListAsync(typeof(T).Name + "/", Ct))
                names.Add(blob);
            Assert.NotEmpty(names);
            foreach (var blob in names)
            {
                await using var content = await blobs.OpenReadAsync("golden", blob, Ct);
                using var held = new MemoryStream();
                await content.CopyToAsync(held, Ct);
                text += $"blob\t{blob}\tsha256={Convert.ToHexStringLower(SHA256.HashData(held.ToArray()))},bytes={held.Length}\n";
            }
        }
        var read = await new SqliteStorage<T>(reopened).OneAsync(entity.Id, Ct);
        text += GoldenWire.Describe(read);

        Assert.Equal(string.Join("\n", Lines(name)) + "\n", text);
        Assert.Equal(rk, physical[0].RowKey);
    }

    // ── Reading a corpus file ───────────────────────────────────────────────

    private static List<string> Lines(string name) => [.. GoldenFile.Lines($"Rows/{name}.txt")];

    // The rows a file lists, each cell as the type its Edm type reads as; null when a value is abbreviated.
    private static List<RawRow>? Parse(IEnumerable<string> lines)
    {
        var rows = new List<RawRow>();
        (string Pk, string Rk, Dictionary<string, RawCell> Cells)? open = null;
        void Close()
        {
            if (open is { } row)
                rows.Add(new RawRow(row.Pk, row.Rk, row.Cells));
        }
        foreach (var raw in lines)
        {
            var line = raw.Replace(VersionPlaceholder, TestAssemblyVersion, StringComparison.Ordinal);
            if (line.StartsWith("row PartitionKey=", StringComparison.Ordinal))
            {
                Close();
                var keys = line["row PartitionKey=".Length..];
                var split = keys.IndexOf(" RowKey=", StringComparison.Ordinal);
                open = (JsonSerializer.Deserialize<string>(keys[..split])!, JsonSerializer.Deserialize<string>(keys[(split + " RowKey=".Length)..])!, new(StringComparer.Ordinal));
            }
            else if (line.StartsWith('\t') && open is { } row)
            {
                var parts = line.Split('\t', 4);
                JsonElement value;
                try
                {
                    value = JsonSerializer.Deserialize<JsonElement>(parts[3]);
                }
                catch (JsonException)
                {
                    return null; // "sha256=…,utf8-bytes=…": too long to print
                }
                row.Cells[parts[1]] = parts[2] switch
                {
                    "Edm.String" => RawCell.FromString(value.GetString()!),
                    "Edm.Binary" => RawCell.FromBinary(value.GetBytesFromBase64()),
                    "Edm.Boolean" => RawCell.FromBoolean(value.GetBoolean()),
                    "Edm.DateTime" => RawCell.FromDateTime(DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
                    "Edm.Double" => RawCell.FromDouble(value.ValueKind == JsonValueKind.String
                        ? double.Parse(value.GetString()!, CultureInfo.InvariantCulture)
                        : value.GetDouble()),
                    "Edm.Guid" => RawCell.FromGuid(value.GetGuid()),
                    "Edm.Int32" => RawCell.FromInt32(value.GetInt32()),
                    "Edm.Int64" => RawCell.FromInt64(long.Parse(value.GetString()!, CultureInfo.InvariantCulture)),
                    var other => throw new InvalidOperationException($"The corpus holds a cell of the unknown type {other}."),
                };
            }
            else if (line.StartsWith("read", StringComparison.Ordinal) || line.StartsWith("blob", StringComparison.Ordinal))
            {
                Close();
                open = null;
            }
        }
        Close();
        return rows;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
