using System.Globalization;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.LargeValues;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// InMemory fidelity: <see cref="InMemoryStorage{T}"/> succeeds and fails exactly where
/// <see cref="TableStorage{T}"/> does over the service (here the local Azurite). Every test runs one
/// script on both stores, records what a caller sees (results, and for a failure the exception type,
/// status and error code, and those of its inner exception) and requires the same transcript.
/// </summary>
public sealed class InMemoryFidelityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string OnePart = new('a', 600_000);

    private static CancellationToken Cancelled => new(canceled: true);

    // ── 3. An already-cancelled token commits nothing ──────────────────────────

    [Fact]
    public async Task CancelledToken_PlainType_WritesAndDeletesCommitNothing()
    {
        await FidelityScript.BothAsync(async s =>
        {
            var store = s.Store<TestEntity>();
            await s.StepAsync("seed", () => s.Store<TestEntity>().CreateAsync(s.Plain("a", "one"), Ct));
            var etag = (await s.Store<TestEntity>().OneAsync(s.Id("a"), Ct))!.ETag;
            await s.StepAsync("create b", () => s.Store<TestEntity>().CreateAsync(s.Plain("b", "x"), Cancelled));
            await s.StepAsync("upsert a", () => s.Store<TestEntity>().UpsertAsync(s.Plain("a", "upserted"), Cancelled));
            await s.StepAsync("update a", () => s.Store<TestEntity>().UpdateAsync(s.Plain("a", "updated", etag), Cancelled));
            await s.StepAsync("builder update a", () => s.Store<TestEntity>().UpdateAsync(s.Id("a"), b => b.SetProperty(e => e.Name, "merged"), Cancelled));
            await s.StepAsync("create batch", () => s.Store<TestEntity>().CreateBatchAsync([s.Plain("c", "x")], Cancelled));
            await s.StepAsync("upsert batch", () => s.Store<TestEntity>().UpsertBatchAsync([s.Plain("a", "batched")], Cancelled));
            await s.StepAsync("delete a", () => s.Store<TestEntity>().DeleteAsync(s.Id("a"), Cancelled));
            await s.StepAsync("delete partition", () => s.Store<TestEntity>().DeletePartitionAsync(s.Pk, Cancelled));
            await s.ListAsync<TestEntity>(e => e.Name);
        });
    }

    [Fact]
    public async Task CancelledToken_LargeValueType_WritesAndDeletesCommitNothing()
    {
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("seed", () => s.Store<SplitTextEntity>().CreateAsync(s.Split("a", "one"), Ct));
            var etag = (await s.Store<SplitTextEntity>().OneAsync(s.Id("a"), Ct))!.ETag;
            await s.StepAsync("create b", () => s.Store<SplitTextEntity>().CreateAsync(s.Split("b", OnePart), Cancelled));
            await s.StepAsync("upsert a", () => s.Store<SplitTextEntity>().UpsertAsync(s.Split("a", "upserted"), Cancelled));
            var update = s.Split("a", "updated");
            update.ETag = etag;
            await s.StepAsync("update a", () => s.Store<SplitTextEntity>().UpdateAsync(update, Cancelled));
            await s.StepAsync("create batch", () => s.Store<SplitTextEntity>().CreateBatchAsync([s.Split("c", "x")], Cancelled));
            await s.StepAsync("upsert batch", () => s.Store<SplitTextEntity>().UpsertBatchAsync([s.Split("a", "batched")], Cancelled));
            await s.StepAsync("delete a", () => s.Store<SplitTextEntity>().DeleteAsync(s.Id("a"), Cancelled));
            await s.StepAsync("delete partition", () => s.Store<SplitTextEntity>().DeletePartitionAsync(s.Pk, Cancelled));
            await s.ListAsync<SplitTextEntity>(e => e.Body);
        });
    }

    // ── 4. Keys the service refuses ────────────────────────────────────────────

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a#b")]
    [InlineData("a?b")]
    [InlineData("a\tb")]
    [InlineData("a\u0001b")]
    [InlineData("a\u007fb")]
    [InlineData("a\u009fb")]
    public async Task RefusedRowKeyCharacter_FailsTheSameWay(string rowKey)
    {
        // A request that names the key in its URL (an upsert, an update, a point read or delete) cannot
        // be compared for '/': Azurite routes it to nothing and answers 500, where the key in a request
        // body is refused with the documented 400 InvalidInput. Every other step runs for every key.
        var urlAddressed = !rowKey.Contains('/', StringComparison.Ordinal);
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("create", () => s.Store<TestEntity>().CreateAsync(s.Plain(rowKey, "x"), Ct));
            if (urlAddressed)
                await s.StepAsync("upsert", () => s.Store<TestEntity>().UpsertAsync(s.Plain(rowKey, "x"), Ct));
            await s.StepAsync("create batch", () => s.Store<TestEntity>().CreateBatchAsync([s.Plain(rowKey, "x")], Ct));
            await s.StepAsync("upsert batch", () => s.Store<TestEntity>().UpsertBatchAsync([s.Plain(rowKey, "x")], Ct));
            await s.StepAsync("create batch, refused key second", () => s.Store<TestEntity>().CreateBatchAsync([s.Plain("ok", "x"), s.Plain(rowKey, "x")], Ct));
            if (urlAddressed)
            {
                await s.StepAsync("update", () => s.Store<TestEntity>().UpdateAsync(s.Plain(rowKey, "x"), ConcurrencyMode.LastWriterWins, Ct));
                await s.StepAsync("builder update", () => s.Store<TestEntity>().UpdateAsync(s.Id(rowKey), b => b.SetProperty(e => e.Name, "y"), Ct));
                await s.StepAsync("delete", () => s.Store<TestEntity>().DeleteAsync(s.Id(rowKey), Ct));
                await s.StepAsync("one", async () => (object?)((await s.Store<TestEntity>().OneAsync(s.Id(rowKey), Ct))?.Name ?? "null"));
                await s.StepAsync("exists", async () => (object?)await s.Store<TestEntity>().ExistsAsync(s.Id(rowKey), Ct));
            }
            await s.StepAsync("create split", () => s.Store<SplitTextEntity>().CreateAsync(s.Split(rowKey, "x"), Ct));
            await s.StepAsync("create split, large value", () => s.Store<SplitTextEntity>().CreateAsync(s.Split(rowKey, OnePart), Ct));
            if (urlAddressed)
            {
                await s.StepAsync("upsert split", () => s.Store<SplitTextEntity>().UpsertAsync(s.Split(rowKey, "x"), Ct));
                await s.StepAsync("upsert batch split", () => s.Store<SplitTextEntity>().UpsertBatchAsync([s.Split(rowKey, "x")], Ct));
            }
            await s.StepAsync("create batch split", () => s.Store<SplitTextEntity>().CreateBatchAsync([s.Split(rowKey, "x")], Ct));
            await s.ListAsync<TestEntity>(e => e.Name);
            await s.ListAsync<SplitTextEntity>(e => e.Body);
        });
    }

    [Theory]
    [InlineData("p/x")]
    [InlineData("p#x")]
    [InlineData("p\u0002x")]
    public async Task RefusedPartitionKeyCharacter_FailsTheSameWay(string partitionKey)
    {
        await FidelityScript.BothAsync(async s =>
        {
            var id = s.Pk + partitionKey + "|r";
            await s.StepAsync("create", () => s.Store<TestEntity>().CreateAsync(new TestEntity { Id = id, Name = "x" }, Ct));
            // See RefusedRowKeyCharacter_FailsTheSameWay: a URL-addressed '/' key is not comparable on Azurite.
            if (!partitionKey.Contains('/', StringComparison.Ordinal))
                await s.StepAsync("upsert", () => s.Store<TestEntity>().UpsertAsync(new TestEntity { Id = id, Name = "x" }, Ct));
            await s.StepAsync("upsert batch", () => s.Store<TestEntity>().UpsertBatchAsync([new TestEntity { Id = id, Name = "x" }], Ct));
            await s.StepAsync("create split", () => s.Store<SplitTextEntity>().CreateAsync(new SplitTextEntity { Id = id, Body = "x" }, Ct));
        });
    }

    // ── 5. A repeated key in a plain upsert batch ──────────────────────────────

    [Fact]
    public async Task PlainUpsertBatch_WithARepeatedKey_FailsTheSameWay()
    {
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("upsert batch", () => s.Store<TestEntity>().UpsertBatchAsync(
                [s.Plain("a", "first"), s.Plain("b", "b"), s.Plain("a", "second")], Ct));
            await s.ListAsync<TestEntity>(e => e.Name);
        });
    }

    [Fact]
    public async Task LargeValueUpsertBatch_WithARepeatedKey_WritesTheLastOccurrence()
    {
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("upsert batch", () => s.Store<SplitTextEntity>().UpsertBatchAsync(
                [s.Split("a", "first"), s.Split("b", "b"), s.Split("a", "second")], Ct));
            await s.ListAsync<SplitTextEntity>(e => e.Body);
        });
    }

    // ── 6. A plain create batch commits per transaction ────────────────────────

    [Fact]
    public async Task PlainCreateBatch_AcrossPartitions_KeepsTheEarlierPartitions()
    {
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("seed b|x", () => s.Store<TestEntity>().CreateAsync(s.Plain2("x", "existing"), Ct));
            await s.StepAsync("create batch", () => s.Store<TestEntity>().CreateBatchAsync(
                [s.Plain("a", "a"), s.Plain2("y", "y"), s.Plain2("x", "dup"), s.Plain("c", "c")], Ct));
            await s.ListAsync<TestEntity>(e => e.Name);
            await s.ListAsync<TestEntity>(e => e.Name, s.Pk2);
        });
    }

    [Fact]
    public async Task PlainCreateBatch_RepeatedKeyInALaterPartition_KeepsTheEarlierPartitions()
    {
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("create batch", () => s.Store<TestEntity>().CreateBatchAsync(
                [s.Plain("a", "a"), s.Plain2("y", "y"), s.Plain2("y", "dup")], Ct));
            await s.ListAsync<TestEntity>(e => e.Name);
            await s.ListAsync<TestEntity>(e => e.Name, s.Pk2);
        });
    }

    [Fact]
    public async Task PlainCreateBatch_OverOneHundredRows_KeepsTheEarlierTransactions()
    {
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("seed r150", () => s.Store<TestEntity>().CreateAsync(s.Plain("r150", "existing"), Ct));
            var batch = Enumerable.Range(0, 160).Select(i => s.Plain("r" + i.ToString("D3", CultureInfo.InvariantCulture), "n")).ToList();
            batch.Add(s.Plain("r150", "dup"));
            await s.StepAsync("create batch", () => s.Store<TestEntity>().CreateBatchAsync(batch, Ct));
            await s.StepAsync("count", async () => (object?)await s.Store<TestEntity>().CountAsync(s.Pk, Ct));
        });
    }

    // ── 7. Error codes ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Failures_CarryTheServiceStatusAndErrorCode()
    {
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("create a", () => s.Store<TestEntity>().CreateAsync(s.Plain("a", "a"), Ct));
            await s.StepAsync("create a again", () => s.Store<TestEntity>().CreateAsync(s.Plain("a", "a"), Ct));
            await s.StepAsync("update missing", () => s.Store<TestEntity>().UpdateAsync(s.Plain("m", "m", "W/\"datetime'2020-01-01T00%3A00%3A00.0000000Z'\""), Ct));
            await s.StepAsync("update stale", () => s.Store<TestEntity>().UpdateAsync(s.Plain("a", "a", "W/\"datetime'2020-01-01T00%3A00%3A00.0000000Z'\""), Ct));
            await s.StepAsync("builder missing", () => s.Store<TestEntity>().UpdateAsync(s.Id("m"), b => b.SetProperty(e => e.Name, "x"), Ct));
            await s.StepAsync("builder stale", () => s.Store<TestEntity>().UpdateAsync(s.Id("a"), b => b.SetProperty(e => e.Name, "x").WithETag("W/\"datetime'2020-01-01T00%3A00%3A00.0000000Z'\""), Ct));
            await s.StepAsync("create batch existing", () => s.Store<TestEntity>().CreateBatchAsync([s.Plain("a", "a")], Ct));
            await s.StepAsync("create batch repeated", () => s.Store<TestEntity>().CreateBatchAsync([s.Plain("z", "z"), s.Plain("z", "z")], Ct));
            await s.StepAsync("split create a", () => s.Store<SplitTextEntity>().CreateAsync(s.Split("a", "a"), Ct));
            await s.StepAsync("split create a again", () => s.Store<SplitTextEntity>().CreateAsync(s.Split("a", "a"), Ct));
            await s.StepAsync("split update missing", () =>
            {
                var e = s.Split("m", "m");
                e.ETag = "W/\"datetime'2020-01-01T00%3A00%3A00.0000000Z'\"";
                return s.Store<SplitTextEntity>().UpdateAsync(e, Ct);
            });
            await s.StepAsync("split update stale", () =>
            {
                var e = s.Split("a", "a");
                e.ETag = "W/\"datetime'2020-01-01T00%3A00%3A00.0000000Z'\"";
                return s.Store<SplitTextEntity>().UpdateAsync(e, Ct);
            });
            await s.StepAsync("split create batch existing", () => s.Store<SplitTextEntity>().CreateBatchAsync([s.Split("a", "a")], Ct));
        });
    }

    // ── 8. Rows with a _SplitOf cell under a plain type ────────────────────────

    [Fact]
    public async Task PlainType_SkipsContinuationRowsOnReads_AndCountsThem()
    {
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("create a", () => s.Store<TestEntity>().CreateAsync(s.Plain("a", "a"), Ct));
            var part = new TableEntity(s.Pk, "a~0001") { ["_SplitOf"] = "a", ["Name"] = "part" };
            await s.SeedAsync<TestEntity>(part);
            await s.StepAsync("one part", async () => (object?)((await s.Store<TestEntity>().OneAsync(s.Id("a~0001"), Ct))?.Name ?? "null"));
            await s.StepAsync("exists part", async () => (object?)await s.Store<TestEntity>().ExistsAsync(s.Id("a~0001"), Ct));
            await s.StepAsync("count", async () => (object?)await s.Store<TestEntity>().CountAsync(s.Pk, Ct));
            await s.ListAsync<TestEntity>(e => e.Name);
            await s.StepAsync("bounded", async () => (object?)string.Join(",", (await s.Store<TestEntity>().QueryAsync(new QueryOptions { Partition = s.Pk }, Ct)).Select(e => e.Name)));
            await s.StepAsync("predicate", async () => (object?)string.Join(",", (await s.Store<TestEntity>().QueryAsync(e => e.Value == 0, s.Pk, null, Ct)).Select(e => e.Name)));
            await s.StepAsync("page", async () => (object?)string.Join(",", (await s.Store<TestEntity>().QueryPageAsync(new QueryOptions { Partition = s.Pk }, Ct)).Items.Select(e => e.Name)));
        });
    }

    // ── 2. QueryOptions.Select ─────────────────────────────────────────────────

    [Fact]
    public async Task Select_ProjectsTheRow_OnEveryBoundedRead()
    {
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("create a", () => s.Store<TestEntity>().CreateAsync(new TestEntity { Id = s.Id("a"), Name = "a", Value = 7 }, Ct));
            await s.StepAsync("create nested", () => s.Store<NestedEntity>().CreateAsync(
                new NestedEntity { Id = s.Id("n"), Title = "t", Address = new AddressInfo { City = "c", Country = "k" } }, Ct));
            var select = new QueryOptions { Partition = s.Pk, Select = ["Name"] };
            await s.StepAsync("query", async () => (object?)string.Join(",", (await s.Store<TestEntity>().QueryAsync(select, Ct)).Select(e => $"{e.Id}:{e.Name}:{e.Value}:{e.ETag is not null}:{e.Timestamp is not null}")));
            await s.StepAsync("stream", async () =>
            {
                var items = new List<string>();
                await foreach (var e in s.Store<TestEntity>().QueryStreamAsync(select, Ct))
                    items.Add($"{e.Id}:{e.Name}:{e.Value}");
                return (object?)string.Join(",", items);
            });
            await s.StepAsync("page", async () => (object?)string.Join(",", (await s.Store<TestEntity>().QueryPageAsync(select, Ct)).Items.Select(e => $"{e.Id}:{e.Name}:{e.Value}")));
            await s.StepAsync("value only", async () => (object?)string.Join(",", (await s.Store<TestEntity>().QueryAsync(new QueryOptions { Partition = s.Pk, Select = ["Value"] }, Ct)).Select(e => $"{e.Id}:{e.Name}:{e.Value}")));
            await s.StepAsync("nested city", async () => (object?)string.Join(",", (await s.Store<NestedEntity>().QueryAsync(new QueryOptions { Partition = s.Pk, Select = ["Address_City"] }, Ct)).Select(e => $"{e.Id}:{e.Title}:{e.Address?.City}:{e.Address?.Country}")));
            await s.StepAsync("missing column", async () => (object?)string.Join(",", (await s.Store<TestEntity>().QueryAsync(new QueryOptions { Partition = s.Pk, Select = ["Nope"] }, Ct)).Select(e => $"{e.Id}:{e.Name}:{e.Value}")));
        });
    }

    [Fact]
    public async Task Select_OnALargeValueType_ReadsOnlyWhatItNames()
    {
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("create a", () => s.Store<SplitTextEntity>().CreateAsync(s.Split("a", OnePart), Ct));
            await s.StepAsync("create b", () => s.Store<SplitTextEntity>().CreateAsync(s.Split("b", "small"), Ct));
            await s.DeleteRawAsync<SplitTextEntity>("a~0001"); // a's continuation row is gone: a's value cannot be read
            var title = new QueryOptions { Partition = s.Pk, Select = ["Title"] };
            await s.StepAsync("title", async () => (object?)string.Join(",", (await s.Store<SplitTextEntity>().QueryAsync(title, Ct)).Select(e => $"{e.Id}:{e.Title}:{e.Body?.Length}")));
            await s.StepAsync("title page", async () => (object?)string.Join(",", (await s.Store<SplitTextEntity>().QueryPageAsync(title, Ct)).Items.Select(e => $"{e.Id}:{e.Title}:{e.Body?.Length}")));
            await s.StepAsync("body", async () => (object?)string.Join(",", (await s.Store<SplitTextEntity>().QueryAsync(new QueryOptions { Partition = s.Pk, Select = ["Body"] }, Ct)).Select(e => $"{e.Id}:{e.Title}:{e.Body?.Length}")));
        });
    }

    // ── The polymorphic store: the same batch, key and error-code rules ───────

    [Fact]
    public async Task Polymorphic_FailuresBatchesKeysAndCancellation_MatchTheService()
    {
        await FidelityScript.BothAsync(async s =>
        {
            PolymorphicWrite<TestMessage> W(string pk, string rk) => new(new TableKey(pk, rk), new TestCreatedEvent { Id = rk });
            await s.StepAsync("insert a", () => s.Poly().InsertAsync(W(s.Pk, "a"), Ct));
            await s.StepAsync("insert a again", () => s.Poly().InsertAsync(W(s.Pk, "a"), Ct));
            await s.StepAsync("merge missing", () => s.Poly().MergeColumnsAsync(new TableKey(s.Pk, "m"), new Dictionary<string, object> { ["_Flag"] = true }, Ct));
            await s.StepAsync("insert refused key", () => s.Poly().InsertAsync(W(s.Pk, "x#y"), Ct));
            await s.StepAsync("upsert refused key", () => s.Poly().UpsertAsync(W(s.Pk, "xy"), Ct));
            await s.StepAsync("insert cancelled", () => s.Poly().InsertAsync(W(s.Pk, "c"), Cancelled));
            await s.StepAsync("batch, existing key in the later partition", () => s.Poly().InsertBatchAsync(
                [W(s.Pk, "b1"), W(s.Pk2, "b2"), W(s.Pk2, "b3"), W(s.Pk2, "b2")], Ct));
            await s.StepAsync("batch, stored key", () => s.Poly().InsertBatchAsync([W(s.Pk2, "z"), W(s.Pk, "a")], Ct));
            await s.StepAsync("batch, refused key", () => s.Poly().InsertBatchAsync([W(s.Pk, "q?r")], Ct));
            await s.StepAsync("count pa", async () => (object?)await s.Poly().CountAsync(s.Pk, Ct));
            await s.StepAsync("count pb", async () => (object?)await s.Poly().CountAsync(s.Pk2, Ct));
        });
    }

    // ── 1. A predicate query filters before it reads large values ──────────────

    [Fact]
    public async Task PredicateQuery_OverADamagedRowItDoesNotMatch_Succeeds()
    {
        await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("create a", () => s.Store<SplitTextEntity>().CreateAsync(s.Split("a", OnePart), Ct));
            await s.StepAsync("create b", () => s.Store<SplitTextEntity>().CreateAsync(s.Split("b", "small"), Ct));
            await s.StepAsync("create c", () => s.Store<SplitTextEntity>().CreateAsync(s.Split("c", "small"), Ct));
            await s.DeleteRawAsync<SplitTextEntity>("a~0001");
            await s.StepAsync("b only", async () => (object?)string.Join(",", (await s.Store<SplitTextEntity>().QueryAsync(e => e.Title == "t-b", s.Pk, null, Ct)).Select(e => $"{e.Id}:{e.Body}")));
            await s.StepAsync("not a", async () => (object?)string.Join(",", (await s.Store<SplitTextEntity>().QueryAsync(e => e.Title != "t-a", s.Pk, null, Ct)).Select(e => $"{e.Id}:{e.Body}")));
            await s.StepAsync("any c", async () => (object?)await s.Store<SplitTextEntity>().AnyAsync(e => e.Title == "t-c", s.Pk, Ct));
            await s.StepAsync("stream b", async () =>
            {
                var items = new List<string>();
                await foreach (var e in s.Store<SplitTextEntity>().QueryStreamAsync(e => e.Title == "t-b", s.Pk, null, Ct))
                    items.Add(e.Id);
                return (object?)string.Join(",", items);
            });
            await s.StepAsync("matches a", async () => (object?)string.Join(",", (await s.Store<SplitTextEntity>().QueryAsync(e => e.Title == "t-a", s.Pk, null, Ct)).Select(e => e.Id)));
        });
    }
}

/// <summary>
/// One differential script: the same steps on <see cref="InMemoryStorage{T}"/> and on
/// <see cref="TableStorage{T}"/> over Azurite, each in its own new table (so nothing another test does
/// can reach it), with the transcripts compared step by step.
/// </summary>
internal sealed class FidelityScript
{
    private static readonly string ConnectionString = TestSupport.AzuriteConnection.ConnectionString;

    private static readonly Lazy<Task<bool>> Probe =
        new(() => AzuriteProbe.ProbeAsync(ConnectionString, blobs: true, AzuriteProbe.Required));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly bool azure;
    private readonly string suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly Dictionary<Type, object> memory = [];
    private readonly List<string> transcript = [];
    private readonly TableServiceClient? tables;
    private readonly BlobServiceClient? blobs;
    private readonly string container;

    private FidelityScript(bool azure)
    {
        this.azure = azure;
        container = "fid" + suffix;
        if (azure)
        {
            tables = new TableServiceClient(ConnectionString);
            blobs = new BlobServiceClient(ConnectionString);
        }
    }

    public string Pk => "pa";

    public string Pk2 => "pb";

    public string Id(string rk) => Pk + "|" + rk;

    public TestEntity Plain(string rk, string name, string? etag = null) => new() { Id = Id(rk), Name = name, ETag = etag };

    public TestEntity Plain2(string rk, string name) => new() { Id = Pk2 + "|" + rk, Name = name };

    public SplitTextEntity Split(string rk, string body) => new() { Id = Id(rk), Body = body, Title = "t-" + rk };

    private string TableOf<T>() => typeof(T).Name.Replace("Entity", "", StringComparison.Ordinal) + suffix;

    private UnifiedTableStorageOptions Options<T>() => new()
    {
        TableNameResolver = _ => TableOf<T>(),
        BlobOffload = new BlobOffloadOptions { ContainerName = container, CleanupGrace = TimeSpan.Zero },
    };

    /// <summary>The store of <typeparamref name="T"/>: on Azurite a new store with an empty cache every call.</summary>
    public IStorage<T> Store<T>() where T : class, IEntity, new()
    {
        if (!azure)
        {
            if (!memory.TryGetValue(typeof(T), out var store))
                memory[typeof(T)] = store = new InMemoryStorage<T>(null, Options<T>());
            return (IStorage<T>)store;
        }
        return new TableStorage<T>(tables!, new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<T>>.Instance,
            null, Options<T>(), blobs);
    }

    private InMemoryPolymorphicStorage<TestMessage>? polymorphic;

    /// <summary>The polymorphic store over this script's own table: on Azurite a new store every call.</summary>
    public IPolymorphicStorage<TestMessage> Poly() => azure
        ? new PolymorphicTableStorage<TestMessage>(tables!, "Poly" + suffix, NullLogger<PolymorphicTableStorage<TestMessage>>.Instance)
        : polymorphic ??= new InMemoryPolymorphicStorage<TestMessage>("Poly" + suffix);

    /// <summary>Stores a raw row as is.</summary>
    public async Task SeedAsync<T>(TableEntity row) where T : class, IEntity, new()
    {
        if (!azure)
        {
            ((InMemoryStorage<T>)Store<T>()).SeedPhysicalRow(row);
            return;
        }
        var client = tables!.GetTableClient(TableOf<T>());
        await client.CreateIfNotExistsAsync(Ct);
        await client.UpsertEntityAsync(row, TableUpdateMode.Replace, Ct);
    }

    /// <summary>Deletes one stored row of <see cref="Pk"/> without the store.</summary>
    public async Task DeleteRawAsync<T>(string rowKey) where T : class, IEntity, new()
    {
        if (!azure)
        {
            ((InMemoryStorage<T>)Store<T>()).RemovePhysicalRow(Pk, rowKey);
            return;
        }
        await tables!.GetTableClient(TableOf<T>()).DeleteEntityAsync(Pk, rowKey, ETag.All, Ct);
    }

    public void Note(string line) => transcript.Add(line);

    public async Task StepAsync(string name, Func<Task<object?>> step)
    {
        try
        {
            var value = await step();
            Note(value is null or IEntity ? $"{name}: ok" : $"{name}: {Convert.ToString(value, CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Note($"{name}: threw {Describe(ex)}");
        }
    }

    public Task StepAsync(string name, Func<Task> step) => StepAsync(name, async () =>
    {
        await step();
        return null;
    });

    /// <summary>Records the entities of the partition (a fresh read), each as id=value.</summary>
    public Task ListAsync<T>(Func<T, object?> value, string? partition = null) where T : class, IEntity, new() =>
        StepAsync($"list {typeof(T).Name} {partition ?? Pk}", async () =>
            (object?)("[" + string.Join(", ", (await Store<T>().QueryAsync(partition ?? Pk, Ct))
                .Select(e => e.Id + "=" + Shorten(value(e)))) + "]"));

    private static string Shorten(object? value) => value switch
    {
        null => "null",
        string { Length: > 40 } s => $"<{s.Length} chars>",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Describe(Exception ex)
    {
        if (ex is OperationCanceledException)
            return "OperationCanceledException";
        var text = ex.GetType().Name + Codes(ex) + ex switch
        {
            DuplicateKeyException d => $"[{d.Id}]",
            ConcurrencyConflictException c => $"[{c.Id}]",
            _ => "",
        };
        if (ex.InnerException is { } inner && ex is not RequestFailedException)
            text += " <- " + inner.GetType().Name + Codes(inner);
        return text;
    }

    private static string Codes(Exception ex) => ex is RequestFailedException r
        ? $"({r.Status.ToString(CultureInfo.InvariantCulture)} {r.ErrorCode ?? "no-code"})"
        : "";

    private async Task CleanupAsync()
    {
        if (!azure)
            return;
        foreach (var type in new[] { "TestEntity", "SplitTextEntity", "NestedEntity", "PolyEntity" })
        {
            try
            {
                await tables!.DeleteTableAsync(type.Replace("Entity", "", StringComparison.Ordinal) + suffix, CancellationToken.None);
            }
            catch (RequestFailedException)
            {
                // never created
            }
        }
        await blobs!.GetBlobContainerClient(container).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
    }

    private static async Task<List<string>> RunAsync(bool azure, Func<FidelityScript, Task> scenario)
    {
        var script = new FidelityScript(azure);
        try
        {
            await scenario(script);
        }
        finally
        {
            await script.CleanupAsync();
        }
        return script.transcript;
    }

    /// <summary>Runs the scenario on both stores and requires the same transcript; returns it.</summary>
    public static async Task<List<string>> BothAsync(Func<FidelityScript, Task> scenario)
    {
        Assert.SkipUnless(await Probe.Value, LargeValueStores.SkipReason);
        var service = await RunAsync(azure: true, scenario);
        var fake = await RunAsync(azure: false, scenario);
        var diff = new List<string>();
        for (var i = 0; i < Math.Max(fake.Count, service.Count); i++)
        {
            var m = i < fake.Count ? fake[i] : "(nothing)";
            var a = i < service.Count ? service[i] : "(nothing)";
            if (!string.Equals(m, a, StringComparison.Ordinal))
                diff.Add($"step {i}:\n    InMemoryStorage: {m}\n    TableStorage:    {a}");
        }
        Assert.True(diff.Count == 0, "The two stores differ:\n" + string.Join("\n", diff)
            + "\n--- TableStorage transcript ---\n" + string.Join("\n", service));
        return fake;
    }
}
