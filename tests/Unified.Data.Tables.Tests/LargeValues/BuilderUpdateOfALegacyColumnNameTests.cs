namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A builder update whose column is a legacy name of a property (<see cref="ColumnAliasAttribute"/>, property-level or
/// class-level) that is a large-value member (marked <see cref="TableValueSplitAttribute"/>, covered by a class-level
/// attribute, or declared <see cref="TableBlobContent"/>) is refused with <see cref="NotSupportedException"/>
/// (<see cref="TableEntitySerializer.PartialUpdateRefusal"/> of the legacy name) before anything is sent or merged, on
/// both stores, under every policy and at every size, exactly as an update of the property's own name is: a read maps
/// the legacy column onto the property when the row has no cell of its own name, so the update wrote the marked value
/// as one ordinary cell, trimmed under a trimming policy, and the marked property read it back trimmed (SERIALIZER-2 of
/// the review of fix wave 8). A legacy name of an ordinary property is still written and read back.
/// </summary>
[Collection("OversizedCellPolicy")]
public class BuilderUpdateOfALegacyColumnNameTests
{
    private const string Table = "LegacyNameUpdate";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OversizedCellPolicy[] Policies =
        [OversizedCellPolicy.Throw, OversizedCellPolicy.TrimSilently, OversizedCellPolicy.TrimWithMarker];

    public sealed class PropertyAlias : Entity
    {
        public string? Title { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        [ColumnAlias("Body")]
        public string? Content { get; set; }
    }

    [ColumnAlias(nameof(Content), "Body")]
    public sealed class ClassAlias : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Content { get; set; }
    }

    public sealed class OrdinaryAlias : Entity
    {
        public string? Title { get; set; }

        [ColumnAlias("Body")]
        public string? Content { get; set; }
    }

    // Not large-value aware: the content is in a property declared as object, under its legacy name.
    public sealed class ObjectFileAlias : Entity
    {
        [ColumnAlias("OldFile")]
        public object? File { get; set; }
    }

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    // RV8 SERIALIZER-2, P2: the property-level form.
    [Theory]
    [MemberData(nameof(Stores))]
    public Task UpdatesKey_OfThePropertyLevelLegacyName_OfAMarkedProperty_IsRefused(string kind) =>
        RefusedAsync<PropertyAlias>(kind, x => x.Content);

    // RV8 SERIALIZER-2, the verifier's class-level form ([ColumnAlias("Content", "Body")] on the entity), on both stores.
    [Theory]
    [MemberData(nameof(Stores))]
    public Task UpdatesKey_OfTheClassLevelLegacyName_OfAMarkedProperty_IsRefused(string kind) =>
        RefusedAsync<ClassAlias>(kind, x => x.Content);

    private static async Task RefusedAsync<T>(string kind, Func<T, string?> read) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<T>(Table);
        var id = stores.Id("a");
        await store.CreateAsync(new T { Id = id }, Ct);
        var seed = 0;
        foreach (var policy in Policies)
        {
            foreach (var size in new[] { 10, 200_000 })
            {
                // An incompressible value: a repeated character would go out as Body__GZip and read back whole.
                var value = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, ++seed);
                foreach (var column in new[] { "Body", "Content" })
                {
                    Exception? thrown = null;
                    IReadOnlyList<string> requests = [];
                    var before = TableEntitySerializer.OversizedCellPolicy;
                    TableEntitySerializer.OversizedCellPolicy = policy;
                    try
                    {
                        requests = await stores.StorageWritesAsync(store, async () =>
                            thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.Updates[column] = value, Ct)));
                    }
                    finally
                    {
                        TableEntitySerializer.OversizedCellPolicy = before;
                    }
                    var row = (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "a");
                    var back = read((await stores.Store<T>(Table).OneAsync(id, Ct))!);
                    Assert.True(thrown is NotSupportedException && requests.Count == 0 && !row.Keys.Any(k => k.StartsWith("Body", StringComparison.Ordinal) || k.StartsWith("Content", StringComparison.Ordinal)) && back is null,
                        $"{typeof(T).Name}, {kind}, {policy}, {size}, Updates[\"{column}\"]: {thrown?.GetType().Name ?? "accepted and reported as written"}; " +
                        $"requests [{string.Join(", ", requests)}]; cells [{string.Join(", ", row.Keys)}]; Content read back {back?.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"} of {size}");
                    Assert.Equal(TableEntitySerializer.PartialUpdateRefusal(column), thrown!.Message);
                }
            }
        }
    }

    // A legacy name of an ordinary property is still written, and a read maps it onto the property.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task UpdatesKey_OfTheLegacyName_OfAnOrdinaryProperty_IsWrittenAndReadBack(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<OrdinaryAlias>(Table);
        var id = stores.Id("o");
        await store.CreateAsync(new OrdinaryAlias { Id = id, Title = "t" }, Ct);
        await store.UpdateAsync(id, b => b.Updates["Body"] = "legacy", Ct);
        var row = (await stores.RawRowsAsync<OrdinaryAlias>(Table)).Single(r => r.RowKey == "o");
        Assert.Equal("legacy", row["Body"]);
        Assert.Equal("legacy", (await stores.Store<OrdinaryAlias>(Table).OneAsync(id, Ct))!.Content);
    }

    // The refusal of a TableBlobContent in a plain type's builder update names the declaration of the property the
    // legacy name maps to (the declared types give it object), not a generic "replace the entity" that the plain store
    // would refuse as well.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task UpdatesKey_OfTheLegacyName_HoldingATableBlobContent_NamesThePropertysDeclaration(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<ObjectFileAlias>(Table);
        var id = stores.Id("f");
        await store.CreateAsync(new ObjectFileAlias { Id = id }, Ct);
        var thrown = await Record.ExceptionAsync(() =>
            store.UpdateAsync(id, b => b.Updates["OldFile"] = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream"), Ct));
        Assert.IsType<NotSupportedException>(thrown);
        Assert.Equal(TableEntitySerializer.PlainPathRefusal(typeof(ObjectFileAlias), [], typeof(ObjectFileAlias), "File", partial: true, contentInAnotherType: true),
            thrown.Message);
        Assert.Contains("Declare ObjectFileAlias.File as TableBlobContent", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(await stores.RawRowsAsync<ObjectFileAlias>(Table), r => r.Keys.Any(k => k.StartsWith("OldFile", StringComparison.Ordinal)));
    }
}
