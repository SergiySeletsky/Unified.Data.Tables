using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Integration tests that exercise the REAL <see cref="TableStorage{T}"/> + serializer against a
/// LOCAL Azurite emulator (connection string <c>UseDevelopmentStorage=true</c>). Unlike the mocked
/// unit tests, these go through the actual Azure.Data.Tables SDK, so they catch serializer /
/// persisted-type mismatches that in-memory mocks cannot.
///
/// They self-skip only when Azurite is not there at all (connection refused, host unreachable), so they
/// are safe to run anywhere; any other probe failure fails them, and CI sets
/// <c>UNIFIED_TABLES_REQUIRE_AZURITE=1</c> so an absent emulator fails them too.
/// To run locally: start Azurite (e.g. <c>azurite --silent</c> or the VS Code Azurite extension),
/// then <c>dotnet test</c>. They never touch any cloud Azure resource.
///
/// Every test works in partitions of its own (see <see cref="Partition"/>), so two test runs that
/// share one emulator do not see, count or delete each other's rows; each test deletes its rows when
/// it ends.
/// </summary>
public sealed class AzuriteTableStorageIntegrationTests : IAsyncLifetime
{
    private const string AzuriteConnectionString = "UseDevelopmentStorage=true";

    private TableServiceClient _service = default!;
    private bool _available;
    private readonly List<(string Table, string PartitionKey, string RowKey)> _cleanup = new();

    // New for every test (xUnit creates the class once per test). With fixed partition names, a second
    // test process on the same Azurite found the first one's rows: creates failed as duplicates, counts
    // were off and one run's partition delete removed the other's rows.
    private readonly string _run = Guid.NewGuid().ToString("N");

    /// <summary>This test's own partition <paramref name="name"/>: the name plus a GUID no other run has.</summary>
    private string Partition(string name) => $"{name}-{_run}";

    // Once per test run: skips only when the emulator is not there at all (see AzuriteProbe); any other
    // probe failure, or an absent emulator with UNIFIED_TABLES_REQUIRE_AZURITE=1, fails the tests.
    private static readonly Lazy<Task<bool>> Probe =
        new(() => AzuriteProbe.ProbeAsync(AzuriteConnectionString, blobs: false, AzuriteProbe.Required));

    public async ValueTask InitializeAsync()
    {
        _available = await Probe.Value;
        if (_available)
            _service = new TableServiceClient(AzuriteConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_available) return;
        foreach (var (table, pk, rk) in _cleanup)
        {
            try { await _service.GetTableClient(table).DeleteEntityAsync(pk, rk); }
            catch (RequestFailedException) { /* already gone */ }
        }
    }

    private TableStorage<T> Store<T>() where T : class, IEntity, new()
        => new(_service, new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<T>>.Instance);

    /// <summary>Replicates TableStorage id normalization so cleanup targets the right row.</summary>
    private void Track<T>(string id) where T : class, IEntity
    {
        var parts = id.Trim().Replace(' ', '-').ToLowerInvariant().Split('|', 2);
        _cleanup.Add((typeof(T).Name, parts[0], parts.Length > 1 ? parts[1] : parts[0]));
    }

    [Fact]
    public async Task Entity_RoundTrips_ThroughRealAzurite()
    {
        Assert.SkipUnless(_available, "Azurite is not running on UseDevelopmentStorage=true");

        var store = Store<TestEntity>();
        var entity = new TestEntity { Id = $"{Partition("integration")}|alice", Name = "Alice", Value = 42 };
        Track<TestEntity>(entity.Id);

        await store.CreateAsync(entity);
        var read = await store.OneAsync(entity.Id);

        Assert.NotNull(read);
        Assert.Equal("Alice", read!.Name);
        Assert.Equal(42, read.Value);
        Assert.False(string.IsNullOrEmpty(read.ETag));
    }

    [Fact]
    public async Task OneAsync_RawDateTimeCell_DeserializesIntoDateTimeOffsetTarget()
    {
        Assert.SkipUnless(_available, "Azurite is not running on UseDevelopmentStorage=true");

        // Bypass the write serializer and insert a row whose date is a plain DateTime — exactly the
        // representation older rows surface as through the SDK. Reading it back into a DateTimeOffset
        // property must not throw (DateTimeOffset is not IConvertible).
        var pk = Partition("legacy-pk");
        const string rk = "legacy-rk";
        var table = _service.GetTableClient(nameof(EntityWithDateTime));
        await table.CreateIfNotExistsAsync();
        var raw = new TableEntity(pk, rk)
        {
            { "Id", $"{pk}|{rk}" },
            { "OffsetDate", new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc) }
        };
        await table.AddEntityAsync(raw);
        _cleanup.Add((nameof(EntityWithDateTime), pk, rk));

        var read = await Store<EntityWithDateTime>().OneAsync($"{pk}|{rk}");

        Assert.NotNull(read);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero), read!.OffsetDate);
    }

    [Fact]
    public async Task PartialUpdate_ThroughRealAzurite_TouchesOnlyDeclaredColumns()
    {
        Assert.SkipUnless(_available, "Azurite is not running on UseDevelopmentStorage=true");

        var store = Store<TestEntity>();
        var entity = new TestEntity { Id = $"{Partition("integration")}|patch", Name = "before", Value = 5 };
        Track<TestEntity>(entity.Id);
        await store.CreateAsync(entity);

        await store.UpdateAsync(entity.Id, b => b.SetProperty(x => x.Name, "after"));

        // Fresh store (cold cache) → forces a real read-back from Azurite.
        var read = await Store<TestEntity>().OneAsync(entity.Id);
        Assert.NotNull(read);
        Assert.Equal("after", read!.Name);
        Assert.Equal(5, read.Value);   // Merge left the untouched column intact
    }

    [Fact]
    public async Task DeletePartitionAsync_RemovesAllRowsInPartition()
    {
        Assert.SkipUnless(_available, "Azurite is not running on UseDevelopmentStorage=true");

        var store = Store<TestEntity>();
        var p = Partition("delpart");
        Track<TestEntity>($"{p}|a");
        Track<TestEntity>($"{p}|b");
        await store.CreateAsync(new TestEntity { Id = $"{p}|a", Name = "A" });
        await store.CreateAsync(new TestEntity { Id = $"{p}|b", Name = "B" });

        var deleted = await store.DeletePartitionAsync(p);

        Assert.Equal(2, deleted);
        Assert.Empty(await Store<TestEntity>().QueryAsync(p));
    }

    [Fact]
    public async Task QueryAsync_ServerSidePredicate_RoundTripsThroughRealAzurite()
    {
        Assert.SkipUnless(_available, "Azurite is not running on UseDevelopmentStorage=true");

        var store = Store<TestEntity>();
        var p = Partition("linqfilter");
        for (var i = 0; i < 6; i++)
        {
            var id = $"{p}|{i:D2}";
            Track<TestEntity>(id);
            await store.CreateAsync(new TestEntity { Id = id, Name = i % 2 == 0 ? "even" : "odd", Value = i });
        }

        // Translated to a server-side OData $filter — proves the translation matches real Azure semantics.
        var highEvens = await Store<TestEntity>().QueryAsync(x => x.Value >= 2 && x.Name == "even", partition: p);

        Assert.Equal(new[] { 2, 4 }, highEvens.OrderBy(x => x.Value).Select(x => x.Value));
    }

    [Fact]
    public async Task QueryPageAsync_PagesThroughRealAzurite_WithContinuationTokens()
    {
        Assert.SkipUnless(_available, "Azurite is not running on UseDevelopmentStorage=true");

        var store = Store<TestEntity>();
        var p = Partition("paging");
        const int total = 25;
        for (var i = 0; i < total; i++)
        {
            var id = $"{p}|{i:D3}";
            Track<TestEntity>(id);
            await store.CreateAsync(new TestEntity { Id = id, Value = i });
        }

        var seen = new List<int>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Store<TestEntity>().QueryPageAsync(
                new QueryOptions { Partition = p, Take = 10, ContinuationToken = cursor });
            seen.AddRange(page.Items.Select(x => x.Value));
            cursor = page.ContinuationToken;
            Assert.True(++pages <= 50, "paging did not terminate");
        }
        while (cursor is not null);

        // Azure page sizes are advisory, so assert coverage, not an exact page count.
        Assert.Equal(total, seen.Count);
        Assert.Equal(Enumerable.Range(0, total), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task UpsertBatch_SplitsOnPayloadSize_SoALargeBatchIsNotRejectedWith413()
    {
        // Assert.Skip, not `if (!_available) return;` — a silent early return reports PASS for a
        // test that never ran, which is indistinguishable from the fix working.
        Assert.SkipUnless(_available, "Azurite is not reachable on UseDevelopmentStorage=true.");

        // The regression this closes. Azure caps a transaction at 100 entities AND 4 MB, and the
        // REST layer base64-expands binary 1.333x. Chunking on count alone sends a legal-looking
        // 100-entity batch that the service rejects with 413 — after earlier chunks have already
        // committed, leaving a half-written table. 60 x 60,000 chars is ~7 MB of payload, i.e.
        // comfortably one "legal" 100-entity chunk under the old planner and comfortably over the
        // transaction limit.
        var store = Store<TestEntity>();
        var p = Partition("batch413");
        var blob = new string('x', 60_000);
        var entities = Enumerable.Range(0, 60)
            .Select(i => new TestEntity { Id = $"{p}|row{i:D3}", Name = blob, Value = i })
            .ToArray();

        foreach (var e in entities) Track<TestEntity>(e.Id);

        var written = await store.UpsertBatchAsync(entities);

        Assert.Equal(entities.Length, written);

        // Round-trip: every row is readable and intact, so the split did not lose or truncate one.
        var readBack = await store.QueryAsync(new QueryOptions { Partition = p });
        Assert.Equal(entities.Length, readBack.Count);
        Assert.All(readBack, e => Assert.Equal(60_000, e.Name.Length));
    }

    [Fact]
    public async Task Query_WithProjection_OmitsTheUnselectedColumn()
    {
        Assert.SkipUnless(_available, "Azurite is not reachable on UseDevelopmentStorage=true.");

        // Projection is the difference between reading a scalar and dragging every large payload
        // in the partition across the wire with it.
        var store = Store<TestEntity>();
        var p = Partition("projection");
        var entity = new TestEntity { Id = $"{p}|row1", Name = new string('y', 40_000), Value = 42 };
        Track<TestEntity>(entity.Id);
        await store.UpsertAsync(entity);

        var projected = await store.QueryAsync(
            new QueryOptions { Partition = p, Select = ["Value"] });

        var row = Assert.Single(projected);
        Assert.Equal(42, row.Value);
        Assert.Equal($"{p}|row1", row.Id);              // keys survive — they are added back
        Assert.True(string.IsNullOrEmpty(row.Name), "an unselected column must not come back populated");
    }
}
