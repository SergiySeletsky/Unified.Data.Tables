using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Unified.Data.Tables.Tests.TestSupport;

/// <summary>
/// Builds a <see cref="TableStorage{T}"/> wired to NSubstitute mocks of the Azure Tables SDK,
/// plus small helpers for the responses those mocks return. Keeps every storage test terse and
/// focused on behaviour rather than SDK plumbing.
/// </summary>
public static class Mocks
{
    public static NullableResponse<TableEntity> Found(TableEntity entity)
    {
        var nr = Substitute.For<NullableResponse<TableEntity>>();
        nr.HasValue.Returns(true);
        nr.Value.Returns(entity);
        return nr;
    }

    public static NullableResponse<TableEntity> NotFound()
    {
        var nr = Substitute.For<NullableResponse<TableEntity>>();
        nr.HasValue.Returns(false);
        return nr;
    }

    public static Response EtagResponse(string etag = "W/\"etag1\"") => new FakeResponse(etag);

    public static AsyncPageable<TableEntity> Pageable(params TableEntity[] entities)
    {
        var page = Page<TableEntity>.FromValues(entities, continuationToken: null, new FakeResponse());
        return AsyncPageable<TableEntity>.FromPages(new[] { page });
    }

    public static TableEntity Row(string partitionKey, string rowKey, string name = "test", int value = 42,
        DateTimeOffset? timestamp = null)
    {
        var te = new TableEntity(partitionKey, rowKey) { ETag = new ETag("W/\"etag1\"") };
        te["Name"] = name;
        te["Value"] = value;
        te["Id"] = partitionKey == rowKey ? partitionKey : $"{partitionKey}|{rowKey}";
        te["CreatedAt"] = DateTimeOffset.UtcNow;
        te["UpdatedAt"] = DateTimeOffset.UtcNow;
        if (timestamp is not null)
            te.Timestamp = timestamp;
        return te;
    }
}

/// <summary>
/// Builds a <see cref="TableStorage{T}"/> wired to NSubstitute mocks of the Azure Tables SDK, plus
/// terse helpers to arrange the common SDK calls.
/// </summary>
public sealed class StorageHarness<T> : IDisposable where T : Entity, new()
{
    public TableServiceClient Service { get; }
    public TableClient Table { get; }
    public IMemoryCache Cache { get; }
    public TableStorage<T> Store { get; }

    private readonly bool ownsCache;

    /// <param name="cache">
    /// Optional cache to inject — pass a size-limited cache to exercise <c>Size</c> on entries, or a
    /// shared cache across two harnesses to exercise cache-key isolation. When omitted, the harness
    /// owns and disposes a default unbounded cache.
    /// </param>
    public StorageHarness(IProtectedPropertyAuthorizer? authorizer = null, UnifiedTableStorageOptions? options = null,
        IMemoryCache? cache = null)
    {
        Service = Substitute.For<TableServiceClient>();
        Table = Substitute.For<TableClient>();
        Service.GetTableClient(typeof(T).Name).Returns(Table);
        // Table creation is lazy (first operation awaits it); an unmocked substitute would return
        // a null Task and NRE on await, so every harness store gets a completed init by default.
        Table.CreateIfNotExistsAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response<Azure.Data.Tables.Models.TableItem>>(null!));
        ownsCache = cache is null;
        Cache = cache ?? new MemoryCache(new MemoryCacheOptions());
        Store = new TableStorage<T>(Service, Cache, NullLogger<TableStorage<T>>.Instance, authorizer, options);
    }

    // Only dispose a cache we created — a caller-injected (possibly shared) cache outlives us.
    public void Dispose()
    {
        if (ownsCache && Cache is IDisposable d)
            d.Dispose();
    }

    // ── Common mock setups ───────────────────────────────────────────────────

    public void SetupAdd(string etag = "W/\"etag1\"") =>
        Table.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>()).Returns(Mocks.EtagResponse(etag));

    public void SetupUpdate(string etag = "W/\"etag1\"") =>
        Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
             .Returns(Mocks.EtagResponse(etag));

    public void SetupUpsert(string etag = "W/\"etag1\"") =>
        Table.UpsertEntityAsync(Arg.Any<TableEntity>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
             .Returns(Mocks.EtagResponse(etag));

    /// <summary>The OData filter string passed to the most recent string-filter QueryAsync call.</summary>
    public string? LastQueryFilter { get; private set; }

    /// <summary>
    /// Mocks the string-filter QueryAsync overload (used by QueryOptions/stream/count paths and
    /// the no-partition scan) and captures the filter each call passes.
    /// </summary>
    public void SetupQueryByFilter(params TableEntity[] entities) =>
        Table.QueryAsync<TableEntity>(Arg.Do<string?>(f => LastQueryFilter = f), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
             .Returns(Mocks.Pageable(entities));

    public void SetupDelete() =>
        Table.DeleteEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>())
             .Returns(new FakeResponse());

    public void SetupGet(TableEntity? entity)
    {
        // Build the (substitute-backed) response BEFORE calling Returns(), otherwise the nested
        // Substitute.For inside Mocks.Found/NotFound corrupts NSubstitute's last-call context.
        var response = entity is null ? Mocks.NotFound() : Mocks.Found(entity);
        Table.GetEntityIfExistsAsync<TableEntity>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
             .Returns(response);
    }

    public void SetupGet(string partitionKey, string rowKey, TableEntity? entity)
    {
        var response = entity is null ? Mocks.NotFound() : Mocks.Found(entity);
        Table.GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
             .Returns(response);
    }

    public void SetupQueryAll(params TableEntity[] entities) =>
        Table.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
             .Returns(Mocks.Pageable(entities));

    public void SetupQueryByPartition(params TableEntity[] entities) =>
        Table.QueryAsync<TableEntity>(
                Arg.Any<System.Linq.Expressions.Expression<Func<TableEntity, bool>>>(),
                Arg.Any<int?>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
             .Returns(Mocks.Pageable(entities));

    public void SetupTransaction()
    {
        var resp = Substitute.For<Response<IReadOnlyList<Response>>>();
        Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
             .Returns(resp);
    }
}
