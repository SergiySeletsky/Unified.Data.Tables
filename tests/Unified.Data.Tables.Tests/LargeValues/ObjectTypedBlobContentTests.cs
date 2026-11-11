namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A <see cref="TableBlobContent"/> is stored only through a property a read maps back as a
/// <see cref="TableBlobContent"/>. One held by a property declared as <c>object</c> (the writer decides by the
/// value, the reader by the declared type) is refused at write with <see cref="NotSupportedException"/> naming
/// the property, before anything is uploaded or stored, on both stores and for every write: stored, every read
/// of the row and of its partition would download it as a value and fail to set it.
/// (Review finding SERIALIZER-10 of fix wave 2.)
/// </summary>
public class ObjectTypedBlobContentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public static TheoryData<string, string> StoresAndWrites()
    {
        var data = new TheoryData<string, string>();
        foreach (var store in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        foreach (var write in new[] { "CreateAsync", "UpsertAsync", "UpdateAsync", "CreateBatchAsync", "UpsertBatchAsync" })
            data.Add(store, write);
        return data;
    }

    private static TableBlobContent File() => TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream");

    /// <summary>Large-value aware by its declared <c>Body</c>.</summary>
    public sealed class Envelope : Entity
    {
        public object? Payload { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }
    }

    public sealed class Folder
    {
        public object? Attachment { get; set; }

        public string? Label { get; set; }
    }

    /// <summary>The object-typed property is a member of a flattened object.</summary>
    public sealed class Cabinet : Entity
    {
        public Folder Inner { get; set; } = new();

        public TableBlobContent? Scan { get; set; }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task FileInAnObjectTypedProperty_IsRefusedOrReadBack_NeverAnUnreadableRow(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");

        var refused = await Record.ExceptionAsync(() => stores.Store<Envelope>().CreateAsync(new Envelope { Id = id, Payload = File() }, Ct));
        if (refused is not null)
        {
            Assert.Empty(await stores.RawRowsAsync<Envelope>());
            return;
        }

        var cells = (await stores.RawRowsAsync<Envelope>())[0].Keys.Where(k => k.StartsWith("Payload", StringComparison.Ordinal));
        var read = await Record.ExceptionAsync(() => stores.Store<Envelope>().OneAsync(id, Ct));
        var query = await Record.ExceptionAsync(() => stores.Store<Envelope>().QueryAsync(stores.Pk, Ct));
        Assert.True(read is null && query is null,
            $"The write was accepted (cells: {string.Join(", ", cells)}; blobs: {(await stores.BlobNamesAsync<Envelope>()).Count}). "
            + $"OneAsync then throws {read?.GetType().Name}: {read?.Message} ({read?.InnerException?.GetType().Name}); "
            + $"QueryAsync of the partition throws {query?.GetType().Name}.");
    }

    [Theory]
    [MemberData(nameof(StoresAndWrites))]
    public async Task FileInAnObjectTypedProperty_IsRefusedByEveryWrite_NamingTheProperty_BeforeAnythingIsUploadedOrStored(string kind, string write)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        var store = stores.Store<Envelope>();
        var entity = new Envelope { Id = id, Body = "b" };
        if (write == "UpdateAsync")
        {
            await store.CreateAsync(new Envelope { Id = id, Body = "before" }, Ct);
            entity = (await store.OneAsync(id, Ct))!;
        }
        var before = await stores.RawRowsAsync<Envelope>();

        entity.Payload = File();
        NotSupportedException? ex = null;
        var requests = await stores.StorageWritesAsync(store, async () => ex = await Assert.ThrowsAsync<NotSupportedException>(() => write switch
        {
            "CreateAsync" => store.CreateAsync(entity, Ct),
            "UpsertAsync" => store.UpsertAsync(entity, Ct),
            "UpdateAsync" => store.UpdateAsync(entity, Ct),
            "CreateBatchAsync" => store.CreateBatchAsync([entity], Ct),
            _ => store.UpsertBatchAsync([entity], Ct),
        }));

        Assert.Contains("'Payload'", ex!.Message, StringComparison.Ordinal);
        Assert.Contains("Declare Envelope.Payload as TableBlobContent", ex.Message, StringComparison.Ordinal);
        Assert.Empty(requests);
        var after = await stores.RawRowsAsync<Envelope>();
        Assert.Equal(before.Select(r => (r.RowKey, r.ETag)), after.Select(r => (r.RowKey, r.ETag)));
        Assert.Empty(await stores.BlobNamesAsync<Envelope>());
        Assert.Equal(write == "UpdateAsync" ? 1 : 0, (await stores.Store<Envelope>().QueryAsync(stores.Pk, Ct)).Count());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task FileInAnObjectTypedMemberOfAFlattenedObject_IsRefused_BeforeAnythingIsStored(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var cabinet = new Cabinet { Id = stores.Id("r"), Scan = File() };
        cabinet.Inner.Attachment = File();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => stores.Store<Cabinet>().CreateAsync(cabinet, Ct));

        Assert.Contains("'Inner_Attachment'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Declare Folder.Attachment as TableBlobContent", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<Cabinet>());
        Assert.Empty(await stores.BlobNamesAsync<Cabinet>());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task FileInAPropertyDeclaredAsTableBlobContent_IsStillStored_AndReadBack(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var cabinet = new Cabinet { Id = stores.Id("r"), Scan = File() };
        cabinet.Inner.Label = "l";

        await stores.Store<Cabinet>().CreateAsync(cabinet, Ct);

        var read = await stores.Store<Cabinet>().OneAsync(stores.Id("r"), Ct);
        await using var stream = await read!.Scan!.OpenReadAsync(Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        Assert.Equal("l", read.Inner.Label);
    }
}
