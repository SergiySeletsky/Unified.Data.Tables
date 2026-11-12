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
/// <c>UpdateAsync(id, builder)</c> over a cached entity patches a copy of the cached entity (ruling R10 of
/// fix wave 4, which replaces the stamped ETag of ruling R2). The copy carries the version the update
/// returned only when the update was conditional on exactly the cached entity's version, so that the next
/// read hands back the stored ETag and an optimistic update with it succeeds. After an unconditional update
/// the copy keeps the ETag the cached entity had, as on 0.8.3: an optimistic update with it meets a
/// conflict, which evicts, and the next read carries the stored ETag. A response without a version keeps
/// the cached entity warm with its old ETag, as on 0.8.3. Every entity type, plain types included.
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
    // evicts nothing there and the next read goes to the table). conditional: the update names the version
    // just written (the cached one when it is cached). The next read carries the ETag the update returned,
    // and a Strict update with it succeeds, except after an unconditional update over a cached entity:
    // then the read carries the cached entity's own (older) ETag, the Strict update with it conflicts and
    // evicts, and the read after that carries the stored ETag.
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task AfterABuilderUpdate_TheNextReadCarriesTheNewETagOnlyWhenTheCachedContentIsTheRows(bool cachedBefore, bool conditional, bool largeValues)
    {
        if (largeValues)
            await RunAsync<RaceDoc>(cachedBefore, conditional);
        else
            await RunAsync<RacePlainDoc>(cachedBefore, conditional);
    }

    private static async Task RunAsync<T>(bool cachedBefore, bool conditional) where T : Entity, IRaceDoc, new()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<T>(table, cache);
        var written = await store.UpsertAsync(new T { Id = Id, Body = "v1", Version = 1 }, Ct);
        if (cachedBefore)
            Assert.Equal(1, (await store.OneAsync(Id, Ct))!.Version);
        else
            cache.Clear();

        var returned = await store.UpdateAsync(Id, b =>
        {
            b.SetProperty(x => x.Version, 2);
            if (conditional)
                b.WithETag(written.ETag!);
        }, Ct);

        Assert.Equal(table.Stored("p", "a")!.ETag.ToString(), returned);
        var read = await store.OneAsync(Id, Ct);
        Assert.Equal(2, read!.Version);
        if (cachedBefore && !conditional)
        {
            Assert.Equal(written.ETag, read.ETag); // the cached entity's own version, older than the row's
            read.Version = 3;
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpdateAsync(read, ConcurrencyMode.Strict, Ct));
            Assert.Equal(2, (int)table.Stored("p", "a")!["Version"]);
            read = await store.OneAsync(Id, Ct); // the conflict evicted: this read asks the table
            Assert.Equal(2, read!.Version);
        }
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

    // A response without a version: as on 0.8.3, the cached entity stays warm with the update applied and
    // keeps its own ETag (older than the row's, so an optimistic write with it conflicts and evicts).
    [Fact]
    public async Task ABuilderUpdate_WhoseResponseCarriesNoETag_KeepsTheCachedEntityWarmWithItsOwnETag()
    {
        using var h = new StorageHarness<TestEntity>();
        var row = Mocks.Row("p", "a");
        h.SetupGet("p", "a", row);
        Assert.NotNull(await h.Store.OneAsync(Id, Ct)); // cached
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns(new NoETagResponse());

        Assert.Equal(string.Empty, await h.Store.UpdateAsync(Id, b => b.SetProperty(x => x.Value, 7), Ct));

        var served = await h.Store.OneAsync(Id, Ct);
        Assert.Equal(7, served!.Value);
        Assert.Equal(row.ETag.ToString(), served.ETag);
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "a", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
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
