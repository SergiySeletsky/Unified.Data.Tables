using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>A plain entity with a byte[] (R13 tests).</summary>
public class SharedBytesDoc : Entity
{
    public byte[]? Data { get; set; }

    public int Version { get; set; }
}

/// <summary>A large-value aware entity with an ordinary byte[] beside a split string (R13 tests).</summary>
public class SharedBytesSplitDoc : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    public byte[]? Data { get; set; }

    public int Version { get; set; }
}

/// <summary>A nested object that holds a byte[] (R13 tests).</summary>
public class SharedBytesInner
{
    public byte[]? Blob { get; set; }

    public string? Label { get; set; }
}

/// <summary>A plain entity with a byte[] below a nested object (R13 tests).</summary>
public class SharedBytesNestedDoc : Entity
{
    public SharedBytesInner? Inner { get; set; }

    public int Version { get; set; }
}

/// <summary>A large-value aware entity whose byte[] is itself the large value (R13 tests).</summary>
public class SharedBytesLargeDoc : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public byte[]? Image { get; set; }

    public int Version { get; set; }
}

/// <summary>A plain entity with a protected byte[] (R13 tests).</summary>
public class SharedBytesProtectedDoc : Entity
{
    [ProtectedProperty("admin")]
    public byte[]? Key { get; set; }

    public string? Title { get; set; }
}

/// <summary>Nested collections passed to the builder (the control of the R13 tests).</summary>
public class SharedCollectionsInner
{
    public List<string>? Items { get; set; }

    public Dictionary<string, string>? Map { get; set; }
}

/// <summary>A plain entity with nested collections (the control of the R13 tests).</summary>
public class SharedCollectionsDoc : Entity
{
    public SharedCollectionsInner? Inner { get; set; }

    public Dictionary<string, string>? Map { get; set; }

    public string[]? Names { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// Ruling R13 (CACHE-R4-3, CACHE-L2): no byte[] held by a cache entry of <see cref="TableStorage{T}"/>, or by a
/// row <see cref="InMemoryStorage{T}"/> stores, is the array a caller passed to the store or received from it,
/// on every put and every hit. A caller that reuses its buffer, or changes an array it read, changes nothing
/// the store answers or stores. (The fake table client shares arrays with its own rows; the Azurite class
/// shows what the table holds.)
/// </summary>
public class NoByteArraySharedWithTheStoreTests
{
    private const string Id = "p|a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableStorage<T> NewStore<T>(RacingTableClient table, IMemoryCache cache)
        where T : class, IEntity, new()
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(T).Name).Returns(table);
        return new TableStorage<T>(service, cache, NullLogger<TableStorage<T>>.Instance, authorizer: null, null,
            (Unified.Data.Tables.Providers.IOffloadBlobStore?)null);
    }

    // CACHE-R4-3: a byte[] passed to the builder and changed afterwards (nothing written) must not reach
    // what the store answers; nor must a reader's change of the array it received.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABuilderUpdate_WithAByteArray_DoesNotLetTheCallersLaterChangesReachTheCachedEntity(bool conditional)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<SharedBytesDoc>(table, cache);
        await store.UpsertAsync(new SharedBytesDoc { Id = Id, Data = [1, 2, 3], Version = 1 }, Ct);
        var read = (await store.OneAsync(Id, Ct))!;

        var mine = new byte[] { 7, 7, 7 };
        await store.UpdateAsync(Id, u =>
        {
            u.SetProperty(x => x.Data, mine);
            if (conditional)
                u.WithETag(read.ETag!);
        }, Ct);
        mine[0] = 99; // never written

        var served = (await store.OneAsync(Id, Ct))!;
        Assert.Equal(new byte[] { 7, 7, 7 }, served.Data);
        served.Data![1] = 98; // a reader's change, never written
        Assert.Equal(new byte[] { 7, 7, 7 }, (await store.OneAsync(Id, Ct))!.Data);
    }

    // CACHE-R4-3: conditional on the cached version; a MutateAsync of another column must not store the
    // bytes the caller changed after the builder update returned.
    [Fact]
    public async Task AConditionalBuilderUpdate_WithAByteArray_NeverLetsMutateAsyncStoreBytesThatWereNeverWritten()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<SharedBytesDoc>(table, cache);
        await store.UpsertAsync(new SharedBytesDoc { Id = Id, Data = [1, 2, 3], Version = 1 }, Ct);
        var read = (await store.OneAsync(Id, Ct))!;

        var mine = new byte[] { 7, 7, 7 };
        await store.UpdateAsync(Id, u => u.SetProperty(x => x.Data, mine).WithETag(read.ETag!), Ct);
        Assert.Equal(new byte[] { 7, 7, 7 }, (byte[])table.Stored("p", "a")!["Data"]);
        mine[0] = 99; // the caller reuses its buffer; nothing is written

        await store.MutateAsync(Id, e => e.Version = 2, ct: Ct);
        Assert.Equal(new byte[] { 7, 7, 7 }, (byte[])table.Stored("p", "a")!["Data"]);
    }

    // CACHE-R4-3: a reader that changes the array it received does not change what the next reader is answered.
    [Fact]
    public async Task AReader_ChangingTheByteArrayItReceived_DoesNotChangeTheCachedEntity()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<SharedBytesDoc>(table, cache);
        await store.UpsertAsync(new SharedBytesDoc { Id = Id, Data = [1, 2, 3], Version = 1 }, Ct);
        var served = (await store.OneAsync(Id, Ct))!;
        served.Data![1] = 98;
        Assert.Equal(new byte[] { 1, 2, 3 }, (await store.OneAsync(Id, Ct))!.Data);
    }

    // CACHE-R4-3: the same for a write and a read of a large-value type (every put is a private copy).
    [Fact]
    public async Task AReadOrWrite_WithAByteArray_DoesNotLetTheCallersLaterChangesReachTheCachedEntity()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<SharedBytesSplitDoc>(table, cache);
        var mine = new byte[] { 1, 2, 3 };
        await store.UpsertAsync(new SharedBytesSplitDoc { Id = Id, Body = "b", Data = mine, Version = 1 }, Ct);
        mine[0] = 99;
        var served = (await store.OneAsync(Id, Ct))!;
        Assert.Equal(new byte[] { 1, 2, 3 }, served.Data);
        served.Data![1] = 98;
        Assert.Equal(new byte[] { 1, 2, 3 }, (await store.OneAsync(Id, Ct))!.Data);
    }

    // R13 "at any depth": a byte[] below a nested object, through a write, a read and a query.
    [Fact]
    public async Task AByteArrayBelowANestedObject_IsNeverSharedWithTheCachedEntity()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<SharedBytesNestedDoc>(table, cache);
        var mine = new byte[] { 1, 2, 3 };
        await store.UpsertAsync(new SharedBytesNestedDoc { Id = Id, Inner = new SharedBytesInner { Blob = mine, Label = "l" }, Version = 1 }, Ct);
        mine[0] = 99; // the writer reuses its buffer

        var served = (await store.OneAsync(Id, Ct))!; // the write's entry
        Assert.Equal(new byte[] { 1, 2, 3 }, served.Inner!.Blob);
        mine[0] = 1; // the fake client's own row shares the writer's array: put it back before the fake is read
        served.Inner.Blob![1] = 98; // a reader's change
        var listed = (await store.QueryAsync("p", Ct)).Single();
        Assert.Equal(new byte[] { 1, 2, 3 }, listed.Inner!.Blob);
        listed.Inner.Blob![2] = 97;
        Assert.Equal(new byte[] { 1, 2, 3 }, (await store.OneAsync(Id, Ct))!.Inner!.Blob);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await store.QueryAsync("p", Ct)).Single().Inner!.Blob);
    }

    // R13 for a large-value byte[]: one that fits one cell and one that is split.
    [Theory]
    [InlineData(16)]
    [InlineData(200_000)]
    public async Task ALargeValueByteArray_IsNeverSharedWithTheCachedEntity(int length)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<SharedBytesLargeDoc>(table, cache);
        var original = Bytes(length);
        var mine = (byte[])original.Clone();
        await store.UpsertAsync(new SharedBytesLargeDoc { Id = Id, Image = mine, Version = 1 }, Ct);
        mine[0] ^= 0xFF; // the writer reuses its buffer

        var served = (await store.OneAsync(Id, Ct))!;
        Assert.Equal(original, served.Image);
        served.Image![1] ^= 0xFF; // a reader's change
        Assert.Equal(original, (await store.OneAsync(Id, Ct))!.Image);
    }

    // Nested lists, dictionaries and arrays passed to the builder: the control (JSON cells are copies).
    [Fact]
    public async Task ABuilderUpdate_WithNestedCollections_DoesNotLetTheCallersLaterChangesReachTheCachedEntity()
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = NewStore<SharedCollectionsDoc>(table, cache);
        await store.UpsertAsync(new SharedCollectionsDoc { Id = Id, Version = 1 }, Ct);
        _ = await store.OneAsync(Id, Ct);

        var inner = new SharedCollectionsInner { Items = ["a"], Map = new() { ["k"] = "v" } };
        var map = new Dictionary<string, string> { ["x"] = "y" };
        var names = new[] { "n" };
        await store.UpdateAsync(Id, u => u.SetProperty(x => x.Inner, inner).SetProperty(x => x.Map, map).SetProperty(x => x.Names, names), Ct);
        inner.Items!.Add("never");
        inner.Map!["k"] = "never";
        map["x"] = "never";
        names[0] = "never";

        var served = (await store.OneAsync(Id, Ct))!;
        Assert.Equal(["a"], served.Inner!.Items!);
        Assert.Equal("v", served.Inner.Map!["k"]);
        Assert.Equal("y", served.Map!["x"]);
        Assert.Equal(["n"], served.Names!);
    }

    // CACHE-L2: InMemoryStorage and the writer's buffer.
    [Fact]
    public async Task InMemory_AWriter_ReusingTheByteArrayItWrote_DoesNotChangeTheStoredRow()
    {
        var memory = new InMemoryStorage<SharedBytesDoc>();
        var buffer = new byte[] { 1, 2, 3 };
        await memory.UpsertAsync(new SharedBytesDoc { Id = Id, Data = buffer, Version = 1 }, Ct);
        buffer[0] = 99;
        Assert.Equal(new byte[] { 1, 2, 3 }, (await memory.OneAsync(Id, Ct))!.Data);
    }

    // CACHE-L2: InMemoryStorage and the reader's array.
    [Fact]
    public async Task InMemory_AReader_ChangingTheByteArrayItReceived_DoesNotChangeTheStoredRow()
    {
        var memory = new InMemoryStorage<SharedBytesDoc>();
        await memory.UpsertAsync(new SharedBytesDoc { Id = Id, Data = [1, 2, 3], Version = 1 }, Ct);
        var first = (await memory.OneAsync(Id, Ct))!;
        first.Data![1] = 98;
        Assert.Equal(new byte[] { 1, 2, 3 }, (await memory.OneAsync(Id, Ct))!.Data);
    }

    // CACHE-L2: InMemoryStorage and the builder's array.
    [Fact]
    public async Task InMemory_ABuilderUpdate_WithAByteArray_DoesNotLetTheCallersLaterChangesReachTheStoredRow()
    {
        var memory = new InMemoryStorage<SharedBytesDoc>();
        await memory.UpsertAsync(new SharedBytesDoc { Id = Id, Data = [1, 2, 3], Version = 1 }, Ct);
        var mine = new byte[] { 7, 7, 7 };
        await memory.UpdateAsync(Id, u => u.SetProperty(x => x.Data, mine), Ct);
        mine[0] = 99;
        Assert.Equal(new byte[] { 7, 7, 7 }, (await memory.OneAsync(Id, Ct))!.Data);
    }

    // CACHE-L2 at depth and for every read: a nested byte[], through a write, a point read, a query and a page.
    [Fact]
    public async Task InMemory_AByteArrayBelowANestedObject_IsNeverSharedWithTheStoredRow()
    {
        var memory = new InMemoryStorage<SharedBytesNestedDoc>();
        var mine = new byte[] { 1, 2, 3 };
        await memory.UpsertAsync(new SharedBytesNestedDoc { Id = Id, Inner = new SharedBytesInner { Blob = mine, Label = "l" }, Version = 1 }, Ct);
        mine[0] = 99;
        (await memory.OneAsync(Id, Ct))!.Inner!.Blob![1] = 98;
        (await memory.QueryAsync("p", Ct)).Single().Inner!.Blob![2] = 97;
        (await memory.QueryPageAsync(new QueryOptions { Partition = "p", Take = 10 }, Ct)).Items.Single().Inner!.Blob![0] = 96;
        Assert.Equal(new byte[] { 1, 2, 3 }, (await memory.OneAsync(Id, Ct))!.Inner!.Blob);
    }

    // CACHE-L2 for a large-value byte[]: one that fits one cell and one that is split.
    [Theory]
    [InlineData(16)]
    [InlineData(200_000)]
    public async Task InMemory_ALargeValueByteArray_IsNeverSharedWithTheStoredRow(int length)
    {
        var memory = new InMemoryStorage<SharedBytesLargeDoc>();
        var original = Bytes(length);
        var mine = (byte[])original.Clone();
        await memory.UpsertAsync(new SharedBytesLargeDoc { Id = Id, Image = mine, Version = 1 }, Ct);
        mine[0] ^= 0xFF;
        (await memory.OneAsync(Id, Ct))!.Image![1] ^= 0xFF;
        Assert.Equal(original, (await memory.OneAsync(Id, Ct))!.Image);
    }

    // A consequence of R13 for a [ProtectedProperty] byte[]: a read never hands back the stored array, so the
    // check compares the arrays by content. An untouched array is not a change (as on 0.8.3 for an entity read
    // through a warm cache, and in memory); a changed one is refused with 0.8.3's message. Both stores.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProtectedByteArray_ThatARead_ModifyWriteLeavesAlone_IsNotAChange(bool inMemory)
    {
        var table = new RacingTableClient();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        IStorage<SharedBytesProtectedDoc> store = inMemory ? new InMemoryStorage<SharedBytesProtectedDoc>() : NewStore<SharedBytesProtectedDoc>(table, cache);
        await store.CreateAsync(new SharedBytesProtectedDoc { Id = Id, Key = [1, 2, 3] }, Ct);

        var read = (await store.OneAsync(Id, Ct))!;
        read.Title = "edited";
        await store.UpdateAsync(read, Ct); // the protected array is untouched

        var again = (await store.OneAsync(Id, Ct))!;
        Assert.Equal("edited", again.Title);
        again.Key = [1, 2, 4];
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpdateAsync(again, Ct));
        Assert.Equal(
            "Property 'Key' on SharedBytesProtectedDoc is protected (requires roles: admin). The current caller is not authorised to change it from 'System.Byte[]' to 'System.Byte[]'.",
            ex.Message);
    }

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }
}
