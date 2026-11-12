using System.Text.Json;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A builder update whose column is a legacy name (<see cref="ColumnAliasAttribute"/>) of a large-value property in any
/// form a read maps onto that property (<c>Old__Json</c>, <c>Old__GZip</c>, and the split-cell and blob-reference cells of
/// <c>Old</c>) is refused with <see cref="NotSupportedException"/> before anything is sent or merged, on both stores, under
/// every policy and at every size, exactly as the property's own name in the same form is. Only the bare legacy name was
/// compared, so <c>Updates["Body__Json"]</c> was accepted, trimmed under a trimming policy, and the entity then failed every
/// read (SERIALIZER-3 of the review of fix wave 9). The same forms of a legacy name of an ordinary property are written as
/// before, and <c>Old__Json</c> of one reads back.
/// </summary>
[Collection("OversizedCellPolicy")]
public class BuilderUpdateOfALegacyColumnNameInEveryReadFormTests
{
    private const string Table = "LegacyNameForms";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly int[] Sizes = [10, 200_000];

    private static readonly OversizedCellPolicy[] Policies =
        [OversizedCellPolicy.Throw, OversizedCellPolicy.TrimSilently, OversizedCellPolicy.TrimWithMarker];

    /// <summary>The suffixes of the forms a read maps a legacy column in (beside the bare name).</summary>
    internal static readonly string[] ReadForms = ["__Json", "__GZip", "__c0", "__c1", "__chunks", "__sha256", "__enc", "__len", "__blob"];

    public sealed class PropertyAliasHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        [ColumnAlias("Body")]
        public string? Content { get; set; }
    }

    [ColumnAlias("Content", "Body")]
    public sealed class ClassAliasHolder : Entity
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Content { get; set; }
    }

    public sealed class OrdinaryAliasHolder : Entity
    {
        [ColumnAlias("Body")]
        public string? Content { get; set; }
    }

    public static TheoryData<string, string, string> FormsStores()
    {
        var d = new TheoryData<string, string, string>();
        foreach (var kind in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
            foreach (var holder in new[] { nameof(PropertyAliasHolder), nameof(ClassAliasHolder) })
                foreach (var form in ReadForms)
                    d.Add(holder, form, kind);
        return d;
    }

    // RV9 SERIALIZER-3, P3 tightened to the requirement (refused at every size), for every form a read maps.
    [Theory]
    [MemberData(nameof(FormsStores))]
    public Task TheLegacyNameInAReadForm_IsRefusedAsTheOwnNameInThatFormIs(string holder, string form, string kind) => holder switch
    {
        nameof(PropertyAliasHolder) => RefusedAsync<PropertyAliasHolder>(form, kind),
        nameof(ClassAliasHolder) => RefusedAsync<ClassAliasHolder>(form, kind),
        _ => throw new ArgumentOutOfRangeException(nameof(holder)),
    };

    private static async Task RefusedAsync<T>(string form, string kind) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<T>(Table);
        var id = stores.Id("f");
        await store.CreateAsync(new T { Id = id }, Ct);
        var content = typeof(T).GetProperty("Content")!;
        foreach (var policy in Policies)
        {
            foreach (var size in Sizes)
            {
                var value = JsonSerializer.Serialize(BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, size + (int)policy));
                foreach (var column in new[] { "Body" + form, "Content" + form })
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
                    var row = (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "f");
                    Assert.True(thrown is NotSupportedException && requests.Count == 0
                                && !row.Keys.Any(k => k.StartsWith("Body", StringComparison.Ordinal) || k.StartsWith("Content", StringComparison.Ordinal)),
                        $"{typeof(T).Name}, {kind}, {policy}, {size}, Updates[{column}]: {thrown?.GetType().Name ?? "accepted"}: {thrown?.Message}; " +
                        $"requests [{string.Join(", ", requests)}]; cells [{string.Join(", ", row.Keys)}]");
                    Assert.Equal(TableEntitySerializer.PartialUpdateRefusal(column), thrown!.Message);
                    Assert.Null(content.GetValue((await stores.Store<T>(Table).OneAsync(id, Ct))!));
                }
            }
        }
    }

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    // The control: the legacy name of an ordinary property in each of those forms is written as before, and its JSON form
    // reads back into the property.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheLegacyNameOfAnOrdinaryProperty_InEveryReadForm_IsWrittenAsBefore(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<OrdinaryAliasHolder>(Table);
        foreach (var form in ReadForms)
        {
            var rowKey = "o" + form.Trim('_').ToLowerInvariant();
            var id = stores.Id(rowKey);
            await store.CreateAsync(new OrdinaryAliasHolder { Id = id }, Ct);
            var value = form == "__Json" ? JsonSerializer.Serialize("hello") : "hello";
            var thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.Updates["Body" + form] = value, Ct));
            Assert.True(thrown is null, $"{kind}, Updates[Body{form}]: {thrown?.GetType().Name}: {thrown?.Message}");
            var rows = await stores.RawRowsAsync<OrdinaryAliasHolder>(Table);
            var row = rows.Single(r => r.RowKey == rowKey);
            Assert.Equal(value, row["Body" + form]);
            Assert.Equal(TableEntitySerializer.FlattenProperty("Body" + form, value), TableEntitySerializer.FlattenProperty(typeof(OrdinaryAliasHolder), "Body" + form, value));
            if (form == "__Json")
                Assert.Equal("hello", (await stores.Store<OrdinaryAliasHolder>(Table).OneAsync(id, Ct))!.Content);
        }
    }
}
