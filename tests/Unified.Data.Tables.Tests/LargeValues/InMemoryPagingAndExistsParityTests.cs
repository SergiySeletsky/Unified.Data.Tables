using System.Globalization;
using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Rule 3 for two reads of a type with large values. ROWS-4a: <c>InMemoryStorage.QueryPageAsync</c> pages
/// over the stored rows as the service does for <c>TableStorage</c>: a continuation row (declared or
/// leftover) takes a place in the page and is then skipped, so a page can hold fewer entities than
/// <c>Take</c>, or none, and still carry a cursor. ROWS-4b: <c>TableStorage.ExistsAsync</c> of a row whose
/// <c>_SplitParts</c> cell is not a count answers true (and caches nothing), as the in-memory store does.
/// The transcript tests run one script on both stores and compare what a caller sees.
/// </summary>
public sealed class InMemoryPagingAndExistsParityTests
{
    private static readonly string OnePart = new('a', 600_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ROWS-4a (the verifier's reproduction): an entity whose value needs one continuation row, and two
    // small ones, written by CreateAsync only. TableStorage: [a] with a cursor, then [b, c].
    [Fact]
    public async Task Paging_OverAnEntityWithADeclaredContinuationRow_GivesTheSamePages()
    {
        var memory = await ParityScript.BothAsync(async s =>
        {
            await s.StepAsync("create a (one continuation row)", () => s.Store.CreateAsync(s.Text("a", OnePart), Ct));
            await s.StepAsync("create b", () => s.Store.CreateAsync(s.Text("b", "small"), Ct));
            await s.StepAsync("create c", () => s.Store.CreateAsync(s.Text("c", "small"), Ct));
            await s.RowsAsync();
            await s.PageThroughAsync(take: 2);
        });

        Assert.Contains("page 1: [<pk>|a] more=True", memory);
        Assert.Contains("page 2: [<pk>|b, <pk>|c] more=False", memory);
    }

    // ROWS-4a (the reviewer's reproduction): the same with a leftover continuation row declared by nothing.
    [Fact]
    public async Task Paging_OverAPartitionThatHoldsALeftoverContinuationRow_GivesTheSamePages()
    {
        await ParityScript.BothAsync(async s =>
        {
            await s.StepAsync("create a", () => s.Store.CreateAsync(s.Text("a", "small"), Ct));
            await s.SeedPartsAsync("a", OnePart); // a~0001: a leftover continuation row, declared by nothing
            await s.StepAsync("create b", () => s.Store.CreateAsync(s.Text("b", "small"), Ct));
            await s.StepAsync("create c", () => s.Store.CreateAsync(s.Text("c", "small"), Ct));
            await s.PageThroughAsync(take: 2);
        });
    }

    // ROWS-4a, the cases the requirement names beyond the reproductions: a page that holds no entity at all
    // and still carries a cursor, and a last page that holds only a continuation row.
    [Fact]
    public async Task Paging_WithPagesThatHoldOnlyContinuationRows_GivesTheSamePages()
    {
        var memory = await ParityScript.BothAsync(async s =>
        {
            await s.StepAsync("create a (one continuation row)", () => s.Store.CreateAsync(s.Text("a", OnePart), Ct));
            await s.StepAsync("create b (one continuation row)", () => s.Store.CreateAsync(s.Text("b", OnePart), Ct));
            await s.RowsAsync();
            await s.PageThroughAsync(take: 1);
        });

        Assert.Equal(
            ["page 1: [<pk>|a] more=True", "page 2: [] more=True", "page 3: [<pk>|b] more=True", "page 4: [] more=False"],
            memory.Where(line => line.StartsWith("page ", StringComparison.Ordinal)));
    }

    // In memory only (no emulator needed): the page holds the rows of the store in key order, the
    // continuation rows among them, and resumes after the last row of the page.
    [Fact]
    public async Task InMemory_Paging_CountsContinuationRowsTowardThePage()
    {
        var store = new InMemoryStorage<SplitTextEntity>();
        await store.CreateAsync(new SplitTextEntity { Id = "p|a", Body = OnePart }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = "p|b", Body = "small" }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = "p|c", Body = "small" }, Ct);

        var first = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 2 }, Ct);
        var second = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 2, ContinuationToken = first.ContinuationToken }, Ct);
        var all = await store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 4 }, Ct);

        Assert.Equal(["p|a"], first.Items.Select(e => e.Id));
        Assert.NotNull(first.ContinuationToken);
        Assert.Equal(OnePart, first.Items[0].Body, StringComparer.Ordinal);
        Assert.Equal(["p|b", "p|c"], second.Items.Select(e => e.Id));
        Assert.Null(second.ContinuationToken);
        Assert.Equal(["p|a", "p|b", "p|c"], all.Items.Select(e => e.Id));
        Assert.Null(all.ContinuationToken);
    }

    // ROWS-4b (the reviewer's reproduction). "Does it exist, then delete it" for the damaged row that
    // DeleteAsync can delete; reads of its value keep throwing.
    [Fact]
    public async Task Exists_OfARowWhosePartCountCannotBeRead_AnswersTheSame()
    {
        var memory = await ParityScript.BothAsync(async s =>
        {
            await s.SeedAsync(new TableEntity(s.Pk, "r") { [SystemColumnNames.SplitParts] = "many", ["Title"] = "damaged" });
            await s.StepAsync("exists", async () => await s.Store.ExistsAsync(s.Id("r"), Ct));
            await s.StepAsync("exists again", async () => await s.Store.ExistsAsync(s.Id("r"), Ct));
            await s.StepAsync("one", async () => (object?)((await s.Store.OneAsync(s.Id("r"), Ct))?.Id ?? "null"));
            await s.StepAsync("count", async () => await s.Store.CountAsync(s.Pk, Ct));
            await s.StepAsync("delete", () => s.Store.DeleteAsync(s.Id("r"), Ct));
            await s.StepAsync("exists after the delete", async () => await s.Store.ExistsAsync(s.Id("r"), Ct));
        });

        Assert.Contains("exists: True", memory);
        Assert.Contains("one: threw TableValueIntegrityException", memory);
        Assert.Contains("exists after the delete: False", memory);
    }

    // ROWS-4b at the SDK calls: the answer is true, nothing is cached (the next call asks the service again).
    [Theory]
    [InlineData("many")]
    [InlineData(-1)]
    [InlineData(2.5)]
    public async Task Exists_OfARowWhosePartCountCannotBeRead_IsTrue_AndNotCached(object partCount)
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", new TableEntity("p", "r") { ETag = new ETag("W/\"v1\""), [SystemColumnNames.SplitParts] = partCount });

        Assert.True(await h.Store.ExistsAsync("p|r", Ct));
        Assert.True(await h.Store.ExistsAsync("p|r", Ct));

        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => h.Store.OneAsync("p|r", Ct));
    }
}

/// <summary>
/// Runs one scripted sequence of calls on <c>InMemoryStorage</c> and on <c>TableStorage</c> over Azurite and
/// records what a caller can observe of each step, with the partition name masked, so the two transcripts
/// can be compared line by line (the review's <c>RowsScript</c>).
/// </summary>
file sealed class ParityScript(LargeValueStores stores, List<string> transcript)
{
    private static readonly string ConnectionString = TestSupport.AzuriteConnection.ConnectionString;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LargeValueStores Stores { get; } = stores;

    public string Pk => Stores.Pk;

    public IStorage<SplitTextEntity> Store => Stores.Store<SplitTextEntity>();

    public string Id(string rk) => Stores.Id(rk);

    public SplitTextEntity Text(string rk, string body) => new() { Id = Id(rk), Body = body, Title = "t-" + rk };

    /// <summary>Stores <paramref name="row"/> as given in the table, without the store.</summary>
    public async Task SeedAsync(TableEntity row)
    {
        var store = Stores.Store<SplitTextEntity>();
        if (store is InMemoryStorage<SplitTextEntity> memory)
        {
            memory.SeedPhysicalRow(row);
            return;
        }
        var client = new TableServiceClient(ConnectionString).GetTableClient(typeof(SplitTextEntity).Name);
        await client.CreateIfNotExistsAsync(Ct);
        await client.UpsertEntityAsync(row, TableUpdateMode.Replace, Ct);
    }

    /// <summary>Stores the continuation rows the store writes for <paramref name="body"/> under <paramref name="rk"/>, declared by nothing.</summary>
    public async Task SeedPartsAsync(string rk, string body)
    {
        var logical = TableEntitySerializer.ToTableEntity(
            new SplitTextEntity { Id = Pk + "|" + rk, Body = body }, Pk, rk, ValueWriteContext.Public, []);
        foreach (var part in SplitRowLayout.Plan(logical).Parts)
        {
            part["Title"] = "leftover";
            await SeedAsync(part);
        }
    }

    public void Note(string line) => transcript.Add(line.Replace(Pk, "<pk>", StringComparison.Ordinal));

    /// <summary>Runs a step and records its outcome: "name: ok" / "name: value" / "name: threw ExceptionType".</summary>
    public async Task StepAsync(string name, Func<Task<object?>> step)
    {
        try
        {
            var value = await step();
            Note(value is null ? $"{name}: ok" : $"{name}: {Convert.ToString(value, CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Note($"{name}: threw {ex.GetType().Name}");
        }
    }

    public Task StepAsync(string name, Func<Task> step) => StepAsync(name, async () =>
    {
        await step();
        return null;
    });

    /// <summary>Pages through the partition with page size <paramref name="take"/> and records every page.</summary>
    public async Task PageThroughAsync(int take)
    {
        string? token = null;
        var page = 0;
        do
        {
            var result = await Store.QueryPageAsync(new QueryOptions { Partition = Pk, Take = take, ContinuationToken = token }, Ct);
            Note($"page {++page}: [{string.Join(", ", result.Items.Select(e => e.Id))}] more={result.ContinuationToken is not null}");
            token = result.ContinuationToken;
        }
        while (token is not null);
    }

    /// <summary>Records the stored row keys of the partition, continuation rows included.</summary>
    public async Task RowsAsync() =>
        Note($"rows: [{string.Join(", ", (await Stores.RawRowsAsync<SplitTextEntity>()).Select(r => r.RowKey))}]");

    private static async Task<List<string>?> RunAsync(string kind, Func<ParityScript, Task> scenario)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind, offload: true, cleanupGrace: TimeSpan.Zero);
        if (stores is null)
            return null;
        var transcript = new List<string>();
        await scenario(new ParityScript(stores, transcript));
        return transcript;
    }

    /// <summary>Runs the scenario on both stores and requires the same transcript; returns it (the in-memory one).</summary>
    public static async Task<List<string>> BothAsync(Func<ParityScript, Task> scenario)
    {
        var azurite = await RunAsync(LargeValueStores.Azurite, scenario);
        Assert.SkipUnless(azurite is not null, LargeValueStores.SkipReason);
        var memory = (await RunAsync(LargeValueStores.Memory, scenario))!;
        var diff = new List<string>();
        for (var i = 0; i < Math.Max(memory.Count, azurite.Count); i++)
        {
            var m = i < memory.Count ? memory[i] : "(nothing)";
            var a = i < azurite.Count ? azurite[i] : "(nothing)";
            if (!string.Equals(m, a, StringComparison.Ordinal))
                diff.Add($"step {i}:\n    InMemoryStorage: {m}\n    TableStorage:    {a}");
        }
        Assert.True(diff.Count == 0, "The two stores differ:\n" + string.Join("\n", diff)
            + "\n--- InMemoryStorage transcript ---\n" + string.Join("\n", memory)
            + "\n--- TableStorage transcript ---\n" + string.Join("\n", azurite));
        return memory;
    }
}
