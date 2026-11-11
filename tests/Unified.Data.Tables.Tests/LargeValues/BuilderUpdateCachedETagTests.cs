using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// <c>UpdateAsync(id, builder)</c> over a cached entity patches the cached copy. The copy must carry the
/// version the update returned, so that the next read hands back an entity whose <c>ETag</c> is the
/// stored one (an optimistic update with it then succeeds); when the response carried no version, the
/// entry is evicted rather than kept with a version known to be old. (PRE-4 of the cache verdicts;
/// ruling R2: every entity type, plain types included.)
/// </summary>
public class BuilderUpdateCachedETagTests
{
    private const string Id = "p|a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableStorage<T> NewStore<T>(RacingTableClient table, IMemoryCache cache) where T : class, IEntity, new()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(T).Name).Returns(table);
        return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, null, (Unified.Data.Tables.Providers.IOffloadBlobStore?)null);
    }

    // cachedBefore: the entity is in the cache when the update runs (the patch path), or not (the update
    // evicts nothing there and the next read goes to the table). Either way the next read carries the
    // ETag the update returned, which is the stored row's, and a Strict update with it succeeds.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task AfterABuilderUpdate_TheNextReadCarriesTheETagTheUpdateReturned(bool cachedBefore, bool largeValues)
    {
        if (largeValues)
            await RunAsync<RaceDoc>(cachedBefore);
        else
            await RunAsync<RacePlainDoc>(cachedBefore);
    }

    private static async Task RunAsync<T>(bool cachedBefore) where T : Entity, IRaceDoc, new()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<T>(table, cache);
        await store.UpsertAsync(new T { Id = Id, Body = "v1", Version = 1 }, Ct);
        if (cachedBefore)
            Assert.Equal(1, (await store.OneAsync(Id, Ct))!.Version);
        else
            cache.Clear();

        var returned = await store.UpdateAsync(Id, b => b.SetProperty(x => x.Version, 2), Ct);

        Assert.Equal(table.Stored("p", "a")!.ETag.ToString(), returned);
        var read = await store.OneAsync(Id, Ct);
        Assert.Equal(2, read!.Version);
        Assert.Equal(returned, read.ETag);

        read.Version = 3;
        await store.UpdateAsync(read, ConcurrencyMode.Strict, Ct); // no conflict: the version was current
        Assert.Equal(3, (int)table.Stored("p", "a")!["Version"]);
    }

    // The in-memory store answers the same.
    [Fact]
    public async Task InMemory_AfterABuilderUpdate_TheNextReadCarriesTheETagTheUpdateReturned()
    {
        var store = new InMemoryStorage<RacePlainDoc>();
        await store.UpsertAsync(new RacePlainDoc { Id = Id, Body = "v1", Version = 1 }, Ct);
        Assert.Equal(1, (await store.OneAsync(Id, Ct))!.Version);

        var returned = await store.UpdateAsync(Id, b => b.SetProperty(x => x.Version, 2), Ct);

        var read = await store.OneAsync(Id, Ct);
        Assert.Equal(2, read!.Version);
        Assert.Equal(returned, read.ETag);
        read.Version = 3;
        await store.UpdateAsync(read, ConcurrencyMode.Strict, Ct);
    }

    // A response without a version: the patched entity would carry a version known to be old, so the
    // entry goes and the next read fetches the row.
    [Fact]
    public async Task ABuilderUpdate_WhoseResponseCarriesNoETag_EvictsTheCachedEntity()
    {
        using var h = new StorageHarness<TestEntity>();
        var row = Mocks.Row("p", "a");
        h.SetupGet("p", "a", row);
        Assert.NotNull(await h.Store.OneAsync(Id, Ct)); // cached
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns(new NoETagResponse());

        Assert.Equal(string.Empty, await h.Store.UpdateAsync(Id, b => b.SetProperty(x => x.Value, 7), Ct));

        await h.Store.OneAsync(Id, Ct);
        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>("p", "a", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A response that carries no headers at all.</summary>
    private sealed class NoETagResponse : Response
    {
        public override int Status => 204;

        public override string ReasonPhrase => "No Content";

        public override Stream? ContentStream
        {
            get => null;
            set { }
        }

        public override string ClientRequestId
        {
            get => Guid.Empty.ToString();
            set { }
        }

        public override void Dispose()
        {
        }

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
    }
}
