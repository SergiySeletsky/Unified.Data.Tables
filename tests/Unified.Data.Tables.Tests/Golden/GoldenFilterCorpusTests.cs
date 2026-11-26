using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unified.Data.Tables.Tests.Golden;

/// <summary>
/// Golden corpus, part (c): OData filters. The <c>$filter</c> strings <see cref="TableFilterTranslator"/>
/// emits for a spread of predicates, and the ones <see cref="TableStorage{T}"/> puts on the request URL
/// for a partition, a RowKey prefix and a predicate, are held to checked-in files.
/// </summary>
/// <remarks>
/// A line is the source text of the call, a tab, and the filter. A filter with a character outside
/// printable ASCII is written as a JSON string; a request with no <c>$filter</c> as <c>(no $filter)</c>.
/// </remarks>
public class GoldenFilterCorpusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Translator_Filters_AreTheCheckedInOnes()
    {
        var name = "captured";
        var limit = 41;
        var token = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        var when = new DateTime(2024, 2, 29, 23, 59, 58, DateTimeKind.Utc).AddTicks(7654321);
        var seen = new DateTimeOffset(2025, 3, 14, 17, 9, 26, TimeSpan.FromHours(2)).AddTicks(5358979);
        GoldenColour? colour = GoldenColour.Blue;

        var text = new StringBuilder("# TableFilterTranslator.Translate<GoldenFilterEntity>(predicate)\n");
        void Add(Expression<Func<GoldenFilterEntity, bool>> predicate, [CallerArgumentExpression(nameof(predicate))] string source = "") =>
            text.Append(source).Append('\t').Append(Show(TableFilterTranslator.Translate(predicate))).Append('\n');

        // Strings.
        Add(x => x.Name == "alice");
        Add(x => "alice" == x.Name);
        Add(x => x.Name == "O'Brien's");
        Add(x => x.Name == "");
        Add(x => x.Name == "100% a&b <c> \"d\" e/f\\g");
        Add(x => x.Name == "Zażółć 中文 😀");
        Add(x => x.Name == name);
        Add(x => x.Name == name + "-suffix");
        Add(x => x.Note == "set");

        // Booleans.
        Add(x => x.Active);
        Add(x => !x.Active);
        Add(x => x.Active == false);
        Add(x => x.Active != true);
        Add(x => x.Verified == true);

        // Integers.
        Add(x => x.Count == 3);
        Add(x => x.Count != 3);
        Add(x => x.Count < 3);
        Add(x => x.Count <= 3);
        Add(x => x.Count > 3);
        Add(x => x.Count >= 3);
        Add(x => 3 < x.Count);
        Add(x => 3 >= x.Count);
        Add(x => x.Count == -1);
        Add(x => x.Count == int.MinValue);
        Add(x => x.Count == limit + 1);
        Add(x => x.Rank == 2);
        Add(x => x.Rank > 2);
        Add(x => x.Small == 4000000000u);
        Add(x => x.Small < 10);
        Add(x => x.Big == 1234567890123L);
        Add(x => x.Big >= long.MinValue);
        Add(x => x.Big < 5);
        Add(x => x.Huge == 18446744073709551615UL);
        Add(x => x.Huge != 7UL);

        // Floating point and decimal (a decimal is compared as the double it is stored as).
        Add(x => x.Ratio > 0.5);
        Add(x => x.Ratio == 1.0);
        Add(x => x.Ratio < 1e-7);
        Add(x => x.Ratio >= -1.7976931348623157E+308);
        Add(x => x.Ratio != 0.1);
        Add(x => x.Price == 19.99m);
        Add(x => x.Price >= 1000m);
        Add(x => x.Price < 0.000001m);

        // Guid, dates, enums.
        Add(x => x.Token == token);
        Add(x => x.Token != Guid.Empty);
        Add(x => x.When >= when);
        Add(x => x.When < new DateTime(2024, 1, 1));
        Add(x => x.Seen < seen);
        Add(x => x.Seen >= GoldenData.Instant);
        Add(x => x.CreatedAt >= GoldenData.Instant && x.UpdatedAt < GoldenData.Later);
        Add(x => x.Colour == GoldenColour.Green);
        Add(x => x.Colour != GoldenColour.Red);
        Add(x => x.Colour == colour);

        // Nested members: the flattened column.
        Add(x => x.Home.City == "Kyiv");
        Add(x => x.Home.Street.Number > 10);
        Add(x => x.Home.Street.Name == "Rynok");
        Add(x => x.Home.Floor == 3);
        Add(x => x.Home.Door == GoldenColour.Blue);

        // Id equality: a key filter.
        Add(x => x.Id == "tenant-a|ord-0001");
        Add(x => x.Id == "solo");
        Add(x => x.Id == "tenant-a|ord|with|pipes");
        Add(x => "it's|quoted" == x.Id);

        // Combinations.
        Add(x => x.Name == "a" && x.Count > 1);
        Add(x => x.Name == "a" || x.Name == "b");
        Add(x => x.Name == "a" && x.Count > 1 && x.Active);
        Add(x => x.Name == "a" || x.Name == "b" && x.Count > 1);
        Add(x => (x.Name == "a" || x.Name == "b") && !(x.Count < 5) && x.Active);
        Add(x => !(x.Active && x.Count == 0));
        Add(x => x.Id == "tenant-a|ord-0001" || x.Id == "tenant-a|ord-0002");
        Add(x => x.Home.City == "Kyiv" && (x.Colour == GoldenColour.Green || x.Price >= 10m) && x.Seen < seen);

        GoldenFile.AssertMatches("Filters/translator.txt", text.ToString());
    }

    [Fact]
    public async Task Store_Filters_AreTheCheckedInOnes()
    {
        var transport = new GoldenTransport(_ => new GoldenTransport.Answer(200, "{\"value\":[]}"));
        var store = new TableStorage<GoldenFilterEntity>(
            transport.Service(), new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<GoldenFilterEntity>>.Instance);

        var text = new StringBuilder("# The $filter TableStorage<GoldenFilterEntity> puts on the request URL (store is that store).\n");
        async Task Add(Func<Task> call, [CallerArgumentExpression(nameof(call))] string source = "")
        {
            transport.Requests.Clear();
            await call();
            var query = Assert.Single(transport.Requests, r => r.Method == "GET");
            Assert.Equal("/GoldenFilterEntity()", query.Path);
            text.Append(source).Append('\t').Append(Show(query.Query.GetValueOrDefault("$filter"))).Append('\n');
        }

        // A partition.
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "tenant-a" }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = " Tenant A " }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "o'brien's" }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "100%&a=b+c/d?e#f" }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "юнікод-中文" }, Ct));
        await Add(() => store.QueryAsync("tenant-a", Ct));
        await Add(() => store.CountAsync("tenant-a", Ct));
        await Add(() => store.QueryStreamAsync(new QueryOptions { Partition = "tenant-a", Take = 5 }, Ct).ToListAsync(Ct).AsTask());
        await Add(() => store.QueryPageAsync(new QueryOptions { Partition = "tenant-a", Take = 5 }, Ct));

        // A partition and a RowKey prefix: RowKey >= prefix and RowKey < the next prefix.
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "ord-" }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = " Tenant A ", RowKeyPrefix = "Order 1" }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "a" }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "it's" }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "ord|2025|" }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "z~" }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "é" }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "a" + char.MaxValue }, Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "tenant-a", RowKeyPrefix = new string(char.MaxValue, 2) }, Ct));
        await Add(() => store.QueryStreamAsync(new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "ord-", Take = 5 }, Ct).ToListAsync(Ct).AsTask());
        await Add(() => store.QueryPageAsync(new QueryOptions { Partition = "tenant-a", RowKeyPrefix = "ord-", Take = 5 }, Ct));

        // No partition: the whole table.
        await Add(() => store.QueryAsync(new QueryOptions(), Ct));
        await Add(() => store.QueryAsync(new QueryOptions { Partition = "  " }, Ct));
        await Add(() => store.QueryAsync((string?)null, Ct));
        await Add(() => store.CountAsync(null, Ct));
        await Add(() => store.QueryPageAsync(new QueryOptions { Take = 5 }, Ct));

        // A predicate, alone and within a partition.
        await Add(() => store.QueryAsync(x => x.Name == "alice" && x.Count > 1, null, null, Ct));
        await Add(() => store.QueryAsync(x => x.Name == "alice" && x.Count > 1, "tenant-a", null, Ct));
        await Add(() => store.QueryAsync(x => x.Name == "alice" || x.Active, " Tenant A ", 5, Ct));
        await Add(() => store.QueryStreamAsync(x => x.Colour == GoldenColour.Green, "tenant-a", null, Ct).ToListAsync(Ct).AsTask());
        await Add(() => store.AnyAsync(x => x.Price >= 10m, "tenant-a", Ct));
        await Add(() => store.AnyAsync(x => x.Id == "tenant-a|ord-0001", null, Ct));

        GoldenFile.AssertMatches("Filters/store-queries.txt", text.ToString());
    }

    private static string Show(string? filter) =>
        filter is null ? "(no $filter)"
        : filter.All(c => c is >= ' ' and <= '~') ? filter
        : JsonSerializer.Serialize(filter).Replace(EscapedApostrophe, "'", StringComparison.Ordinal);

    // The apostrophe every filter is full of is left as it is (JSON does not need it escaped).
    private static readonly string EscapedApostrophe = (char)0x5C + "u0027";
}
