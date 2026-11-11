namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A refused change of a protected property never quotes a large value whole, also when the large value is a
/// member, at any depth, of the object the property holds (SERIALIZER-6): the message names the property and
/// describes the kind of each value. A protected property whose value holds no large-value member keeps the
/// message of 0.8.3 character for character.
/// </summary>
public class ProtectedObjectHoldingALargeValueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public sealed record SealedNote
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public sealed record Folder
    {
        public string? Label { get; set; }

        public SealedNote? Inner { get; set; }
    }

    public sealed record Address
    {
        public string? City { get; set; }
    }

    public sealed class Deed : Entity
    {
        [ProtectedProperty("legal")]
        public SealedNote? Terms { get; set; }

        [ProtectedProperty("legal")]
        public Folder? Archive { get; set; }

        [ProtectedProperty("legal")]
        public Address? Office { get; set; }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task RefusedChangeOfAProtectedObject_DoesNotQuoteTheLargeValueItHolds(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<Deed>();
        var id = stores.Id("r");
        await store.CreateAsync(new Deed { Id = id, Terms = new SealedNote { Notes = new string('a', 700_000) } }, Ct);
        var read = (await store.OneAsync(id, Ct))!;
        read.Terms = new SealedNote { Notes = new string('b', 650_000) };

        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpdateAsync(read, Ct));
        Assert.True(refused.Message.Length < 2_000,
            $"The refusal is {refused.Message.Length:N0} characters long and quotes the protected large value: {refused.Message[..Math.Min(300, refused.Message.Length)]}...");
        Assert.Contains("Property 'Terms' on Deed is protected (requires roles: legal).", refused.Message, StringComparison.Ordinal);
        Assert.Contains("SealedNote", refused.Message, StringComparison.Ordinal);
        Assert.Equal(new string('a', 700_000), (await store.OneAsync(id, Ct))!.Terms!.Notes);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task RefusedChangeOfAProtectedObject_DoesNotQuoteALargeValueDeeperDown(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<Deed>();
        var id = stores.Id("r");
        await store.CreateAsync(new Deed { Id = id, Archive = new Folder { Label = "l", Inner = new SealedNote { Notes = new string('a', 300_000) } } }, Ct);
        var read = (await store.OneAsync(id, Ct))!;
        read.Archive = new Folder { Label = "l", Inner = new SealedNote { Notes = new string('b', 300_000) } };

        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpdateAsync(read, Ct));
        Assert.True(refused.Message.Length < 2_000, $"The refusal is {refused.Message.Length:N0} characters long: {refused.Message[..Math.Min(300, refused.Message.Length)]}...");
        Assert.Contains("Property 'Archive' on Deed is protected", refused.Message, StringComparison.Ordinal);
    }

    // A protected object that holds no large-value member keeps the message of 0.8.3, also on an aware type.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task RefusedChangeOfAProtectedObjectWithoutALargeValue_KeepsThe083Message(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<Deed>();
        var id = stores.Id("r");
        await store.CreateAsync(new Deed { Id = id, Office = new Address { City = "Lviv" } }, Ct);
        var read = (await store.OneAsync(id, Ct))!;
        var stored = read.Office;
        read.Office = new Address { City = "Kyiv" };

        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpdateAsync(read, Ct));
        Assert.Equal(
            $"Property 'Office' on Deed is protected (requires roles: legal). The current caller is not authorised to change it from '{stored}' to '{read.Office}'.",
            refused.Message);
    }
}
