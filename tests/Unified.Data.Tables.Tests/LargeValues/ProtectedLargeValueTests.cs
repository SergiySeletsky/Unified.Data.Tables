using NSubstitute;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The protected-property check of a whole-entity write, for a property that is a large value (a
/// <see cref="TableBlobContent"/>, or a value opted in with <c>[TableValueSplit]</c>):
/// <list type="bullet">
/// <item>An update or upsert that carries the protected file it was read with (the same blob: the same hash,
/// length and content type), or a protected opted-in <c>byte[]</c> with the same bytes, is not refused, on both
/// stores and whatever the entity cache of <c>TableStorage</c> holds. Another file or other bytes are still
/// refused without the role, and so is a content whose hash is not known yet (a stream never prepared). The
/// comparison downloads no blob. (Review finding SERIALIZER-L4 of fix wave 2.)</item>
/// <item>The refusal names the property and gives only the kind and length of the stored and the new value,
/// never the values themselves. A property that is not a large value keeps the comparison and the message of
/// 0.8.3 exactly. (Review finding SERIALIZER-11 of fix wave 2.)</item>
/// </list>
/// No authorizer is registered in these stores: nobody may change a protected value.
/// </summary>
public class ProtectedLargeValueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    private static string Ascii(int length, int seed)
    {
        var rng = new Random(seed);
        return string.Create(length, rng, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)r.Next(0x21, 0x7F);
        });
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public sealed class Deed : Entity
    {
        [ProtectedProperty("legal")]
        public TableBlobContent? Scan { get; set; }

        public string? Title { get; set; }
    }

    public sealed class Seal : Entity
    {
        [ProtectedProperty("legal")]
        [TableValueSplit(SplitEncoding.Text)] // a byte[] is stored as raw binary cells whatever the encoding
        public byte[]? Image { get; set; }

        public string? Title { get; set; }
    }

    public sealed class Contract : Entity
    {
        [ProtectedProperty("legal")]
        [TableValueSplit(SplitEncoding.Text)]
        public string? Terms { get; set; }

        public string? Title { get; set; }
    }

    /// <summary>A plain type: its protected property is no large value.</summary>
    public sealed class Account : Entity
    {
        [ProtectedProperty("admin")]
        public string? Owner { get; set; }

        [ProtectedProperty("admin")]
        public byte[]? Key { get; set; }

        public string? Title { get; set; }
    }

    // ── SERIALIZER-L4 ─────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task L4_UpdateThatLeavesAProtectedFileUntouched_IsNotRefused(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        await stores.Store<Deed>().CreateAsync(
            new Deed { Id = id, Title = "t", Scan = TableBlobContent.FromBytes([1, 2, 3], "application/pdf") }, Ct);

        var read = await stores.Store<Deed>().OneAsync(id, Ct);
        read!.Title = "edited"; // the protected file is not touched
        var update = await Record.ExceptionAsync(() => stores.Store<Deed>().UpdateAsync(read, Ct));
        var upsert = await Record.ExceptionAsync(() => stores.Store<Deed>().UpsertAsync(read, Ct));

        Assert.True(update is null && upsert is null,
            $"UpdateAsync: {update?.GetType().Name ?? "ok"}; UpsertAsync: {upsert?.GetType().Name ?? "ok"}. {update?.Message ?? upsert?.Message}");
        Assert.Equal("edited", (await stores.Store<Deed>().OneAsync(id, Ct))!.Title);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task L4_UpdateThatLeavesAProtectedSplitByteArrayUntouched_IsNotRefused(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        var image = Bytes(100_000, 3);
        await stores.Store<Seal>().CreateAsync(new Seal { Id = id, Title = "t", Image = image }, Ct);

        var read = await stores.Store<Seal>().OneAsync(id, Ct);
        Assert.Equal(image, read!.Image);
        read.Title = "edited"; // the protected value is not touched
        var update = await Record.ExceptionAsync(() => stores.Store<Seal>().UpdateAsync(read, Ct));
        Assert.True(update is null, $"UpdateAsync: {update?.GetType().Name}: {update?.Message}");

        var again = await stores.Store<Seal>().OneAsync(id, Ct);
        again!.Title = "upserted";
        var upsert = await Record.ExceptionAsync(() => stores.Store<Seal>().UpsertAsync(again, Ct));
        Assert.True(upsert is null, $"UpsertAsync: {upsert?.GetType().Name}: {upsert?.Message}");
        Assert.Equal("upserted", (await stores.Store<Seal>().OneAsync(id, Ct))!.Title);
    }

    /// <summary>One store, so its entity cache is warm: the outcome does not depend on what the cache holds.</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task UpdateThatLeavesAProtectedFileUntouched_IsNotRefused_WithAWarmCacheEither(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        var store = stores.Store<Deed>();
        await store.CreateAsync(new Deed { Id = id, Title = "t", Scan = TableBlobContent.FromBytes([1, 2, 3], "application/pdf") }, Ct);

        var read = await store.OneAsync(id, Ct);
        read!.Title = "edited";
        await store.UpdateAsync(read, Ct);
        var again = await store.OneAsync(id, Ct);
        again!.Title = "upserted";
        await store.UpsertAsync(again, Ct);

        Assert.Equal("upserted", (await stores.Store<Deed>().OneAsync(id, Ct))!.Title);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ComparingAProtectedFile_DownloadsNoBlob(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        await stores.Store<Deed>().CreateAsync(new Deed { Id = id, Title = "t", Scan = TableBlobContent.FromBytes([1, 2, 3], "application/pdf") }, Ct);
        var store = stores.Store<Deed>();
        var read = await store.OneAsync(id, Ct);
        read!.Title = "edited";

        var reads = await stores.BlobReadsAsync(store, () => store.UpdateAsync(read, Ct));

        Assert.Equal(0, reads);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AnotherFile_OrOtherBytes_OrAContentWhoseHashIsNotKnownYet_AreStillRefusedWithoutTheRole(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var deedId = stores.Id("deed");
        var sealId = stores.Id("seal");
        var image = Bytes(100_000, 3);
        await stores.Store<Deed>().CreateAsync(new Deed { Id = deedId, Title = "t", Scan = TableBlobContent.FromBytes([1, 2, 3], "application/pdf") }, Ct);
        await stores.Store<Seal>().CreateAsync(new Seal { Id = sealId, Title = "t", Image = image }, Ct);

        // Another file.
        var deed = await stores.Store<Deed>().OneAsync(deedId, Ct);
        deed!.Scan = TableBlobContent.FromBytes([1, 2, 4], "application/pdf");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stores.Store<Deed>().UpdateAsync(deed, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stores.Store<Deed>().UpsertAsync(deed, Ct));

        // The same bytes under another content type is another file.
        deed.Scan = TableBlobContent.FromBytes([1, 2, 3], "image/png");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stores.Store<Deed>().UpdateAsync(deed, Ct));

        // A stream whose hash is not known yet counts as changed, even with the same bytes; it is never read to compare.
        using var source = new MemoryStream([1, 2, 3]);
        deed.Scan = TableBlobContent.FromStream(source, "application/pdf");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stores.Store<Deed>().UpdateAsync(deed, Ct));
        Assert.Equal(0, source.Position);

        // Removing the file is a change too.
        deed.Scan = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stores.Store<Deed>().UpdateAsync(deed, Ct));

        // Other bytes, of the same length and of another length.
        var seal = await stores.Store<Seal>().OneAsync(sealId, Ct);
        var other = (byte[])image.Clone();
        other[^1] ^= 0xFF;
        seal!.Image = other;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stores.Store<Seal>().UpdateAsync(seal, Ct));
        seal.Image = image[..^1];
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stores.Store<Seal>().UpsertAsync(seal, Ct));

        // Nothing was changed.
        var storedDeed = await stores.Store<Deed>().OneAsync(deedId, Ct);
        Assert.Equal("t", storedDeed!.Title);
        Assert.Equal(3, storedDeed.Scan!.Length);
        Assert.Equal(image, (await stores.Store<Seal>().OneAsync(sealId, Ct))!.Image);
    }

    /// <summary>The same bytes in a new content are the same blob: its hash and length are known once it is made from bytes.</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheSameFileMadeAgainFromTheSameBytes_IsNotAChange(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        await stores.Store<Deed>().CreateAsync(new Deed { Id = id, Title = "t", Scan = TableBlobContent.FromBytes([1, 2, 3], "application/pdf") }, Ct);

        var read = await stores.Store<Deed>().OneAsync(id, Ct);
        read!.Title = "edited";
        read.Scan = TableBlobContent.FromBytes([1, 2, 3], "application/pdf");
        await stores.Store<Deed>().UpdateAsync(read, Ct);

        Assert.Equal("edited", (await stores.Store<Deed>().OneAsync(id, Ct))!.Title);
    }

    [Fact]
    public async Task AnAuthorisedCaller_StillChangesAProtectedFile()
    {
        var authorizer = Substitute.For<IProtectedPropertyAuthorizer>();
        authorizer.IsAllowed("legal").Returns(true);
        var store = new InMemoryStorage<Deed>(authorizer, new UnifiedTableStorageOptions { BlobOffload = new BlobOffloadOptions() });
        await store.CreateAsync(new Deed { Id = "p|r", Title = "t", Scan = TableBlobContent.FromBytes([1, 2, 3], "application/pdf") }, Ct);

        var read = await store.OneAsync("p|r", Ct);
        read!.Scan = TableBlobContent.FromBytes([9], "application/pdf");
        await store.UpdateAsync(read, Ct);

        Assert.Equal(1, (await store.OneAsync("p|r", Ct))!.Scan!.Length);
    }

    // ── SERIALIZER-11 ─────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task RefusedChangeOfAProtectedLargeValue_DoesNotQuoteTheValuesWhole(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        var stored = Ascii(700_000, 1);
        await stores.Store<Contract>().CreateAsync(new Contract { Id = id, Title = "t", Terms = stored }, Ct);

        var read = await stores.Store<Contract>().OneAsync(id, Ct);
        var written = Ascii(650_000, 2);
        read!.Terms = written;
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stores.Store<Contract>().UpdateAsync(read, Ct));

        Assert.True(ex.Message.Length < 2_000, $"the message is {ex.Message.Length:N0} characters long: it quotes the stored value and the new value whole.");
        Assert.DoesNotContain(stored[..20], ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(written[..20], ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            "Property 'Terms' on Contract is protected (requires roles: legal). The current caller is not authorised to change it "
            + "from a string of 700,000 characters to a string of 650,000 characters (the values of a large-value property are not quoted).",
            ex.Message);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task RefusedChangeOfAProtectedFileOrSplitByteArray_GivesTheirLengthsOnly(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        await stores.Store<Deed>().CreateAsync(new Deed { Id = stores.Id("d"), Title = "t", Scan = TableBlobContent.FromBytes([1, 2, 3], "application/pdf") }, Ct);
        await stores.Store<Seal>().CreateAsync(new Seal { Id = stores.Id("s"), Title = "t", Image = Bytes(100_000, 3) }, Ct);

        var deed = await stores.Store<Deed>().OneAsync(stores.Id("d"), Ct);
        deed!.Scan = null;
        var file = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stores.Store<Deed>().UpdateAsync(deed, Ct));
        Assert.EndsWith("from a TableBlobContent of 3 bytes to null (the values of a large-value property are not quoted).", file.Message, StringComparison.Ordinal);

        var seal = await stores.Store<Seal>().OneAsync(stores.Id("s"), Ct);
        seal!.Image = Bytes(10, 4);
        var bytes = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stores.Store<Seal>().UpdateAsync(seal, Ct));
        Assert.EndsWith("from a byte[] of 100,000 bytes to a byte[] of 10 bytes (the values of a large-value property are not quoted).", bytes.Message, StringComparison.Ordinal);
    }

    /// <summary>Rule 2: a property that is not a large value keeps the comparison and the message of 0.8.3, on both stores.</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task RefusedChangeOfAPlainProtectedProperty_KeepsTheMessageOf083(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        await stores.Store<Account>().CreateAsync(new Account { Id = id, Owner = "alice", Title = "t" }, Ct);

        var read = await stores.Store<Account>().OneAsync(id, Ct);
        read!.Owner = "bob";
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stores.Store<Account>().UpdateAsync(read, Ct));

        Assert.Equal(
            "Property 'Owner' on Account is protected (requires roles: admin). The current caller is not authorised to change it from 'alice' to 'bob'.",
            ex.Message);
    }

    /// <summary>
    /// A plain <c>byte[]</c> keeps 0.8.3's message. Since ruling R13 (fix wave 5) no read hands back the stored
    /// array, so it is compared by content: an untouched array is not a change, a changed one is refused.
    /// </summary>
    [Fact]
    public async Task APlainProtectedByteArray_IsComparedByContent_AndKeepsTheMessageOf083()
    {
        var store = new InMemoryStorage<Account>();
        await store.CreateAsync(new Account { Id = "p|r", Owner = "alice", Key = [1, 2, 3] }, Ct);

        var read = await store.OneAsync("p|r", Ct);
        read!.Title = "edited";
        await store.UpdateAsync(read, Ct);

        var again = await store.OneAsync("p|r", Ct);
        again!.Key = [1, 2, 3];
        await store.UpdateAsync(again, Ct); // a new array with the same bytes is not a change

        again = await store.OneAsync("p|r", Ct);
        again!.Key = [1, 2, 4];
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpdateAsync(again, Ct));
        Assert.Equal(
            "Property 'Key' on Account is protected (requires roles: admin). The current caller is not authorised to change it from 'System.Byte[]' to 'System.Byte[]'.",
            ex.Message);
    }
}
