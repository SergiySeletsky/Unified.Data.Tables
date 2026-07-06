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
/// They self-skip when Azurite is not reachable, so they are safe to run anywhere (CI, other devs).
/// To run locally: start Azurite (e.g. <c>azurite --silent</c> or the VS Code Azurite extension),
/// then <c>dotnet test</c>. They never touch any cloud Azure resource.
/// </summary>
public sealed class AzuriteTableStorageIntegrationTests : IAsyncLifetime
{
    private const string AzuriteConnectionString = "UseDevelopmentStorage=true";

    private TableServiceClient _service = default!;
    private bool _available;
    private readonly List<(string Table, string PartitionKey, string RowKey)> _cleanup = new();

    public async ValueTask InitializeAsync()
    {
        try
        {
            _service = new TableServiceClient(AzuriteConnectionString);
            // Probe with a real network call. If Azurite is down this throws and every test skips.
            await foreach (var _ in _service.QueryAsync(maxPerPage: 1))
                break;
            _available = true;
        }
        catch (Exception)
        {
            _available = false;
        }
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

    private TableStorage<T> Store<T>() where T : Entity, new()
        => new(_service, new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<T>>.Instance);

    /// <summary>Replicates TableStorage id normalization so cleanup targets the right row.</summary>
    private void Track<T>(string id) where T : Entity
    {
        var parts = id.Trim().Replace(' ', '-').ToLowerInvariant().Split('|', 2);
        _cleanup.Add((typeof(T).Name, parts[0], parts.Length > 1 ? parts[1] : parts[0]));
    }

    [Fact]
    public async Task Entity_RoundTrips_ThroughRealAzurite()
    {
        Assert.SkipUnless(_available, "Azurite is not running on UseDevelopmentStorage=true");

        var store = Store<TestEntity>();
        var entity = new TestEntity { Id = "integration|alice", Name = "Alice", Value = 42 };
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
        const string pk = "legacy-pk";
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
        var entity = new TestEntity { Id = "integration|patch", Name = "before", Value = 5 };
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
        Track<TestEntity>("delpart|a");
        Track<TestEntity>("delpart|b");
        await store.CreateAsync(new TestEntity { Id = "delpart|a", Name = "A" });
        await store.CreateAsync(new TestEntity { Id = "delpart|b", Name = "B" });

        var deleted = await store.DeletePartitionAsync("delpart");

        Assert.Equal(2, deleted);
        Assert.Empty(await Store<TestEntity>().QueryAsync("delpart"));
    }
}
