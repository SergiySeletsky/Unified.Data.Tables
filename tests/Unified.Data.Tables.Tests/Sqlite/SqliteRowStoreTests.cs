using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Sqlite;

namespace Unified.Data.Tables.Tests.Sqlite;

/// <summary>
/// The SQLite row store below the engine: it must answer exactly as the in-memory row store answers
/// (the same rows in the same order for every range and resume key, the same outcome of every commit),
/// since the engine above both is the same code and assumes one behaviour.
/// </summary>
public sealed class SqliteRowStoreTests : IDisposable
{
    private readonly TempDatabase database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => database.Dispose();

    // Keys that tell UTF-16 code-unit order from UTF-8 byte order and from any culture's order: the empty
    // key, a key and its extension, upper and lower case, a character above U+E000 against a supplementary
    // one (the two orders disagree on exactly this pair), unpaired surrogates, and U+FFFF.
    private static readonly string[] Keys =
    [
        "", "a", "A", "aa", "a ", "ab", "B", "b", "Z", "z", "é", "ß", "я", "中", "", "�", "￿", "￿￿",
        "😀", "😀a", "\uD83D", "\uDE00", "a\uD800", "a􏿿", "a￿", "0", "9", "~", "a~0001", "a|b", " ",
    ];

    private static TableEntity Row(string pk, string rk, string? note = null)
    {
        var row = new TableEntity(pk, rk);
        if (note is not null)
            row["Note"] = note;
        return row;
    }

    private async Task<(IRowStore Sqlite, IRowStore Memory)> TwinsAsync(SqliteStorageAccount account)
    {
        var memoryAccount = new InMemoryAccount();
        await account.Database.CreateTableAsync("Twins", Ct);
        await memoryAccount.CreateTableAsync("Twins", Ct);
        return (account.Database.GetTable("Twins"), memoryAccount.GetTable("Twins"));
    }

    private static string Show(IEnumerable<StoredRow> rows) =>
        string.Join(" ", rows.Select(r => string.Join("", (r.PartitionKey + "/" + r.RowKey).Select(c => c is > ' ' and < '\u007f' ? c.ToString() : $"\\u{(int)c:X4}"))));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryRangeAndResumeKey_ReturnsTheRowsOfTheInMemoryRowStore_InItsOrder(bool file)
    {
        var account = database.Open(file ? database.Path : SqliteStorageOptions.InMemoryDataSource);
        var (sqlite, memory) = await TwinsAsync(account);
        string[] partitions = ["", "p", "P", "p2", "￿", "😀"];
        foreach (var pk in partitions)
        {
            foreach (var chunk in Keys.Chunk(20))
            {
                var writes = chunk.Select(rk => RowWrite.Insert(Row(pk, rk))).ToList();
                Assert.True((await sqlite.CommitAsync(pk, writes, Ct)).Committed);
                Assert.True((await memory.CommitAsync(pk, [.. chunk.Select(rk => RowWrite.Insert(Row(pk, rk)))], Ct)).Committed);
            }
        }

        // The whole table, in pages of every size that splits it differently.
        foreach (var size in new[] { 1, 7, 1000 })
        {
            (string, string)? after = null;
            var all = new List<StoredRow>();
            while (true)
            {
                var page = await sqlite.ReadAsync(RowRange.All with { After = after }, size, Ct);
                var expected = await memory.ReadAsync(RowRange.All with { After = after }, size, Ct);
                Assert.Equal(Show(expected), Show(page));
                all.AddRange(page);
                if (page.Count < size)
                    break;
                after = (page[^1].PartitionKey, page[^1].RowKey);
            }
            Assert.Equal(partitions.Length * Keys.Length, all.Count);
            // The order is the ordinal order of the strings, which is what the engine's cursors assume.
            var sorted = all.OrderBy(r => r.PartitionKey, StringComparer.Ordinal).ThenBy(r => r.RowKey, StringComparer.Ordinal);
            Assert.Equal(Show(sorted), Show(all));
        }

        // Every bound pair, inclusive and exclusive, from every resume key of the partition, of a
        // partition before it and of one after it.
        var random = new Random(20260);
        var bounds = Keys.Append(null).ToArray();
        for (var i = 0; i < 1500; i++)
        {
            var pk = partitions[random.Next(partitions.Length)];
            var range = new RowRange(
                pk,
                bounds[random.Next(bounds.Length)], random.Next(2) == 0,
                bounds[random.Next(bounds.Length)], random.Next(2) == 0,
                random.Next(3) == 0 ? null : (partitions[random.Next(partitions.Length)], Keys[random.Next(Keys.Length)]));
            var limit = random.Next(1, 40);

            var expected = await memory.ReadAsync(range, limit, Ct);
            var actual = await sqlite.ReadAsync(range, limit, Ct);

            Assert.True(Show(expected) == Show(actual), $"{range} limit {limit}: expected [{Show(expected)}], SQLite returned [{Show(actual)}]");
        }

        // A prefix is a range (RowRange.Prefix), U+FFFF at its end included.
        foreach (var prefix in new[] { "a", "a￿", "￿", "😀", "\uD83D", "" })
        {
            var expected = await memory.ReadAsync(RowRange.Prefix("p", prefix), 1000, Ct);
            Assert.Equal(Show(expected), Show(await sqlite.ReadAsync(RowRange.Prefix("p", prefix), 1000, Ct)));
            Assert.All(expected, row => Assert.StartsWith(prefix, row.RowKey, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ARow_ReadsBackWithTheVeryCellsAndTypesItWasWrittenWith()
    {
        var account = database.Open();
        await account.Database.CreateTableAsync("Cells", Ct);
        var table = account.Database.GetTable("Cells");
        var written = new TableEntity("p\uD800", "r￿")
        {
            ["Text"] = "Zażółć 😀 \"quoted\" \0 nul",
            ["Empty"] = "",
            ["UnpairedSurrogate"] = "a\uD83Db\uDE00",
            ["Bytes"] = new byte[] { 0, 1, 127, 128, 255 },
            ["NoBytes"] = Array.Empty<byte>(),
            ["Binary"] = new BinaryData([9, 8, 7]),
            ["True"] = true,
            ["False"] = false,
            ["Instant"] = new DateTimeOffset(2025, 3, 14, 15, 9, 26, TimeSpan.Zero).AddTicks(5358979),
            ["WithOffset"] = new DateTimeOffset(2025, 3, 14, 17, 9, 26, TimeSpan.FromHours(-5.5)).AddTicks(1),
            ["MinInstant"] = DateTimeOffset.MinValue,
            ["Utc"] = new DateTime(2024, 2, 29, 23, 59, 58, DateTimeKind.Utc).AddTicks(7654321),
            ["Local"] = new DateTime(2024, 2, 29, 23, 59, 58, DateTimeKind.Local),
            ["Unspecified"] = new DateTime(2024, 2, 29, 23, 59, 58, DateTimeKind.Unspecified),
            ["Double"] = 3.141592653589793,
            ["NaN"] = double.NaN,
            ["NegativeZero"] = -0.0,
            ["Infinity"] = double.NegativeInfinity,
            ["Guid"] = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
            ["Int32"] = int.MinValue,
            ["Int64"] = long.MaxValue,
            ["Nothing"] = null,
            ["Zażółć\uD800"] = "a column name that is not ASCII, nor well-formed",
        };

        Assert.True((await table.CommitAsync(written.PartitionKey, [RowWrite.Insert(written)], Ct)).Committed);
        // Through another account, so nothing but the file carries the row over.
        var read = (await database.Open().Database.GetTable("Cells").GetAsync(written.PartitionKey, written.RowKey, Ct))!.Data;

        Assert.Equal(written.Keys.ToList(), read.Keys.ToList()); // the same cells in the same order
        foreach (var cell in written)
        {
            var value = read[cell.Key];
            Assert.Equal(cell.Value?.GetType(), value?.GetType());
            switch (cell.Value)
            {
                case BinaryData binary:
                    Assert.Equal(binary.ToArray(), ((BinaryData)value!).ToArray());
                    break;
                case DateTimeOffset instant:
                    Assert.Equal(instant, (DateTimeOffset)value!);
                    Assert.Equal(instant.Offset, ((DateTimeOffset)value!).Offset);
                    break;
                case DateTime date:
                    Assert.Equal(date, (DateTime)value!);
                    Assert.Equal(date.Kind, ((DateTime)value!).Kind);
                    break;
                case double number:
                    Assert.Equal(BitConverter.DoubleToInt64Bits(number), BitConverter.DoubleToInt64Bits((double)value!));
                    break;
                default:
                    Assert.Equal(cell.Value, value);
                    break;
            }
        }
    }

    [Fact]
    public async Task ACellOfATypeThatIsNotATableCell_IsRefused_AndNothingIsWritten()
    {
        var account = database.Open();
        await account.Database.CreateTableAsync("Cells", Ct);
        var table = account.Database.GetTable("Cells");

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await table.CommitAsync("p", [RowWrite.Insert(Row("p", "ok")), RowWrite.Insert(new TableEntity("p", "bad") { ["Money"] = 1.5m })], Ct));

        Assert.Empty(await table.ReadAsync(RowRange.All, 10, Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACommit_AppliesEverythingOrNothing_AndNamesTheFirstConditionThatDoesNotHold(bool file)
    {
        var account = database.Open(file ? database.Path : SqliteStorageOptions.InMemoryDataSource);
        var (sqlite, memory) = await TwinsAsync(account);
        foreach (var store in new[] { sqlite, memory })
        {
            var first = await store.CommitAsync("p", [RowWrite.Insert(Row("p", "a", "1")), RowWrite.Upsert(Row("p", "b", "1"))], Ct);
            Assert.True(first.Committed);
            Assert.Equal(2, first.ETags.Count);
            Assert.NotEqual(first.ETags[0], first.ETags[1]);
            var etagOfA = first.ETags[0]!;

            // Operation 2 fails: the insert of "c" before it and the delete of "b" after it are not applied.
            var refused = await store.CommitAsync("p",
                [RowWrite.Insert(Row("p", "c")), RowWrite.Delete("b"), RowWrite.Insert(Row("p", "a", "2")), RowWrite.Delete("missing")], Ct);
            Assert.Equal((RowCommitFailure.AlreadyExists, 2), (refused.Failure, refused.FailedIndex));
            Assert.Equal(["a", "b"], (await store.ReadAsync(new RowRange("p"), 10, Ct)).Select(r => r.RowKey));

            Assert.Equal((RowCommitFailure.NotFound, 1),
                await Outcome(store.CommitAsync("p", [RowWrite.DeleteIfExists("missing"), RowWrite.Replace(Row("p", "missing"))], Ct)));
            Assert.Equal((RowCommitFailure.NotFound, 0), await Outcome(store.CommitAsync("p", [RowWrite.Delete("missing")], Ct)));
            Assert.Equal((RowCommitFailure.VersionMismatch, 0),
                await Outcome(store.CommitAsync("p", [RowWrite.Replace(Row("p", "a", "x"), "W/\"not-the-version\"")], Ct)));
            Assert.Equal((RowCommitFailure.VersionMismatch, 0), await Outcome(store.CommitAsync("p", [RowWrite.Delete("a", "\0")], Ct)));
            Assert.Equal("1", (await store.GetAsync("p", "a", Ct))!.Data["Note"]);

            // The version the row has: replaced, and the row has another version afterwards.
            var replaced = await store.CommitAsync("p", [RowWrite.Replace(Row("p", "a", "2"), etagOfA), RowWrite.Delete("b")], Ct);
            Assert.True(replaced.Committed);
            Assert.Null(replaced.ETags[1]);
            var stored = (await store.GetAsync("p", "a", Ct))!;
            Assert.Equal(("2", replaced.ETags[0]), (stored.Data["Note"], stored.ETag));
            Assert.NotEqual(etagOfA, stored.ETag);
            Assert.Equal(replaced.Timestamp, stored.Timestamp);
            Assert.Equal(TimeSpan.Zero, stored.Timestamp.Offset);
            Assert.Null(await store.GetAsync("p", "b", Ct));
            Assert.Equal((RowCommitFailure.VersionMismatch, 0), await Outcome(store.CommitAsync("p", [RowWrite.Delete("a", etagOfA)], Ct)));
        }

        static async Task<(RowCommitFailure, int)> Outcome(ValueTask<RowCommitResult> commit)
        {
            var result = await commit;
            Assert.False(result.Committed);
            return (result.Failure, result.FailedIndex);
        }
    }

    [Fact]
    public async Task AnETag_IsNeverIssuedTwice_AcrossTablesAccountsRestartsAndARowCreatedAgain()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long last = 0;

        async Task WriteAsync(SqliteStorageAccount account, string table, params RowWrite[] writes)
        {
            await account.Database.CreateTableAsync(table, Ct);
            var result = await account.Database.GetTable(table).CommitAsync("p", writes, Ct);
            Assert.True(result.Committed);
            foreach (var etag in result.ETags.Where(e => e is not null))
            {
                Assert.True(seen.Add(etag!), $"The ETag {etag} was issued twice.");
                // Monotonic over the whole database: W/"n" with n always above every earlier one.
                var version = long.Parse(etag![3..^1], System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(version > last, $"{etag} does not follow {last}.");
                last = version;
            }
        }

        var first = database.Open();
        var second = database.Open(); // as another process holds the file
        await WriteAsync(first, "Alpha", RowWrite.Insert(Row("p", "a")), RowWrite.Insert(Row("p", "b")));
        await WriteAsync(second, "Beta", RowWrite.Insert(Row("p", "a")));
        await WriteAsync(first, "Alpha", RowWrite.Delete("a"));
        await WriteAsync(second, "Alpha", RowWrite.Insert(Row("p", "a"))); // the same key again: not its old version
        Assert.True(await first.Database.DeleteTableAsync("Beta", Ct));
        await WriteAsync(first, "Beta", RowWrite.Insert(Row("p", "a"))); // the same table again
        first.Dispose();
        second.Dispose();

        await WriteAsync(database.Open(), "Alpha", RowWrite.Upsert(Row("p", "a")), RowWrite.Upsert(Row("p", "c")));
        Assert.Equal(7, seen.Count);
    }

    [Fact]
    public async Task ATableThatDoesNotExist_IsReportedAsMissing_ByEveryOperation_AndNeverCreated()
    {
        var account = database.Open();
        foreach (var name in new[] { "NeverCreated", "no such table", "x\"; DROP TABLE \"__meta", "__meta", "sqlite_schema", "ab" })
        {
            var table = account.Database.GetTable(name);
            Assert.Equal(name, table.TableName);
            await Assert.ThrowsAsync<RowStoreTableMissingException>(async () => await table.GetAsync("p", "r", Ct));
            await Assert.ThrowsAsync<RowStoreTableMissingException>(async () => await table.ReadAsync(RowRange.All, 10, Ct));
            await Assert.ThrowsAsync<RowStoreTableMissingException>(async () => await table.CommitAsync("p", [RowWrite.Upsert(Row("p", "r"))], Ct));
            await Assert.ThrowsAsync<RowStoreTableMissingException>(async () => await table.CommitAsync("p", [RowWrite.Delete("r")], Ct));
            await Assert.ThrowsAsync<RowStoreTableMissingException>(async () => await table.CommitAsync("p", [], Ct));
            Assert.False(await account.Database.TableExistsAsync(name, Ct));
            Assert.False(await account.Database.DeleteTableAsync(name, Ct));
        }
        Assert.Empty(await account.Database.ListTablesAsync(Ct).ToListAsync(Ct));
        // The internal tables are still there and still work.
        Assert.True(await account.Database.CreateTableAsync("Fine", Ct));
        Assert.True((await account.Database.GetTable("Fine").CommitAsync("p", [RowWrite.Insert(Row("p", "r"))], Ct)).Committed);
    }

    [Theory]
    [InlineData("no such table")]
    [InlineData("x\"; DROP TABLE \"__meta\"; --")]
    [InlineData("__blobs")]
    [InlineData("sqlite_master")]
    [InlineData("1Digit")]
    [InlineData("ab")]
    [InlineData("With_Underscore")]
    [InlineData("Tabłe")]
    [InlineData("")]
    public async Task ATableNameTheServiceRefuses_IsRefusedTheSameWay_AndNeverReachesSql(string name)
    {
        var account = database.Open();

        var refused = await Assert.ThrowsAsync<StorageRequestRejectedException>(async () => await account.Database.CreateTableAsync(name, Ct));

        Assert.Equal("InvalidResourceName", refused.ProviderErrorCode);
        Assert.Empty(await account.Database.ListTablesAsync(Ct).ToListAsync(Ct));
        Assert.True(await account.Database.CreateTableAsync("StillWorks", Ct));
    }

    [Fact]
    public async Task TableNames_AreComparedIgnoringCase_AndKeywordsAreOrdinaryNames()
    {
        var account = database.Open();

        Assert.True(await account.Database.CreateTableAsync("Order", Ct));
        Assert.False(await account.Database.CreateTableAsync("ORDER", Ct));
        Assert.True(await account.Database.CreateTableAsync("select", Ct));
        Assert.True(await account.Database.CreateTableAsync("Table", Ct));
        Assert.True((await account.Database.GetTable("order").CommitAsync("p", [RowWrite.Insert(Row("p", "r"))], Ct)).Committed);

        Assert.NotNull(await account.Database.GetTable("ORDER").GetAsync("p", "r", Ct));
        Assert.True(await account.Database.TableExistsAsync("oRdEr", Ct));
        Assert.Equal(["Order", "select", "Table"], await account.Database.ListTablesAsync(Ct).ToListAsync(Ct));
        Assert.True(await account.Database.DeleteTableAsync("SELECT", Ct));
        Assert.False(await account.Database.DeleteTableAsync("select", Ct));
        Assert.Equal(["Order", "Table"], await account.Database.ListTablesAsync(Ct).ToListAsync(Ct));
    }

    [Fact]
    public async Task ACancelledToken_StopsEveryOperation_BeforeItTouchesTheDatabase()
    {
        var account = database.Open();
        await account.Database.CreateTableAsync("Cancelled", Ct);
        var table = account.Database.GetTable("Cancelled");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await table.GetAsync("p", "r", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await table.ReadAsync(RowRange.All, 1, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await table.CommitAsync("p", [RowWrite.Upsert(Row("p", "r"))], cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await account.Database.CreateTableAsync("Other", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await account.Database.DeleteTableAsync("Cancelled", cancelled.Token));

        Assert.Empty(await table.ReadAsync(RowRange.All, 1, Ct));
        Assert.False(await account.Database.TableExistsAsync("Other", Ct));
    }
}
