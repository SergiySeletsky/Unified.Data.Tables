namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A builder update whose column path reaches, through a property declared as a nullable struct (<c>S?</c>) at any
/// depth, a member of <c>S</c> that is a large-value member (marked <see cref="TableValueSplitAttribute"/> on the
/// property, or declared <see cref="TableBlobContent"/>) is refused with
/// <see cref="NotSupportedException"/> before anything is sent or merged, on both stores, for plain and aware entity
/// types, under every policy and at every size: under the serializer's own column name (<c>Extra_Notes</c>: a nullable
/// struct flattens with no <c>Value</c> segment) and under the lambda's (<c>Extra_Value_Notes</c>). The walk of rule A
/// stopped at <c>Nullable&lt;S&gt;</c>, whose members are only <c>HasValue</c> and <c>Value</c>, so <c>Extra_Notes</c> of a
/// plain type was written as an ordinary cell, trimmed under a trimming policy, and read back trimmed or not at all
/// (SERIALIZER-1 of the review of fix wave 8). An ordinary member of <c>S</c> beside the marked one is still written.
/// </summary>
[Collection("OversizedCellPolicy")]
public class BuilderUpdateBelowANullableStructTests
{
    private const string Table = "NullableStructUpdate";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OversizedCellPolicy[] Policies =
        [OversizedCellPolicy.Throw, OversizedCellPolicy.TrimSilently, OversizedCellPolicy.TrimWithMarker];

    public struct Att
    {
        public Att()
        {
        }

        public string? Name { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public struct FileAtt
    {
        public FileAtt()
        {
        }

        public string? Name { get; set; }

        public TableBlobContent? File { get; set; }
    }

    // A class-level [TableValueSplit] cannot be put on a struct (CS0592: the attribute is valid on classes and
    // properties), and a struct has no base class to carry one: the class-level form below a nullable struct cannot be written.

    // Not large-value aware by its declared types; its constructor initialises Extra.
    public sealed class PlainInitialised : Entity
    {
        public Att? Extra { get; set; } = new Att();

        public string? Subject { get; set; }
    }

    // The same, empty on a new entity.
    public sealed class PlainEmpty : Entity
    {
        public Att? Extra { get; set; }
    }

    // Opted in at class level: aware.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class AwareInitialised : Entity
    {
        public Att? Extra { get; set; } = new Att();

        public int Rank { get; set; }
    }

    // The nullable struct below another owner.
    public sealed class Wrapper
    {
        public Att? Extra { get; set; } = new Att();
    }

    public sealed class PlainNested : Entity
    {
        public Wrapper Doc { get; set; } = new();
    }

    public sealed class PlainFile : Entity
    {
        public FileAtt? Extra { get; set; } = new FileAtt();
    }

    public static TheoryData<string, string, string> Refused()
    {
        var d = new TheoryData<string, string, string>();
        foreach (var kind in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
        {
            foreach (var (type, column) in new[]
            {
                (nameof(PlainInitialised), "Extra_Notes"), (nameof(PlainInitialised), "Extra_Value_Notes"),
                (nameof(PlainEmpty), "Extra_Notes"), (nameof(PlainEmpty), "Extra_Value_Notes"),
                (nameof(AwareInitialised), "Extra_Notes"), (nameof(AwareInitialised), "Extra_Value_Notes"),
                (nameof(PlainNested), "Doc_Extra_Notes"), (nameof(PlainNested), "Doc_Extra_Value_Notes"),
                (nameof(PlainFile), "Extra_File"), (nameof(PlainFile), "Extra_Value_File"),
            })
            {
                d.Add(type, column, kind);
            }
        }
        return d;
    }

    // RV8 SERIALIZER-1 (P1, P1b and the verdict's added shapes), through a key of UpdateBuilder.Updates.
    [Theory]
    [MemberData(nameof(Refused))]
    public Task UpdatesKey_BelowANullableStruct_IsRefusedBeforeAnythingIsSent(string type, string column, string kind) => type switch
    {
        nameof(PlainInitialised) => RefusedAsync<PlainInitialised>(kind, column),
        nameof(PlainEmpty) => RefusedAsync<PlainEmpty>(kind, column),
        nameof(AwareInitialised) => RefusedAsync<AwareInitialised>(kind, column),
        nameof(PlainNested) => RefusedAsync<PlainNested>(kind, column),
        nameof(PlainFile) => RefusedAsync<PlainFile>(kind, column),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static async Task RefusedAsync<T>(string kind, string column) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<T>(Table);
        var id = stores.Id("n");
        await store.CreateAsync(new T { Id = id }, Ct);
        var before = (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "n").Keys.Order(StringComparer.Ordinal).ToList();
        var seed = 0;
        foreach (var policy in Policies)
        {
            foreach (var size in new[] { 10, 200_000 })
            {
                object value = column.EndsWith("File", StringComparison.Ordinal)
                    ? TableBlobContent.FromBytes(new byte[size], "application/octet-stream")
                    : BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(size, ++seed);
                Exception? thrown = null;
                IReadOnlyList<string> requests = [];
                await WithPolicyAsync(policy, async () =>
                    requests = await stores.StorageWritesAsync(store, async () =>
                        thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.Updates[column] = value, Ct))));
                var after = (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "n").Keys.Order(StringComparer.Ordinal).ToList();
                var label = $"{typeof(T).Name}, {kind}, Updates[{column}], {policy}, {size}: {thrown?.GetType().Name ?? "accepted"}: {thrown?.Message}; " +
                            $"requests [{string.Join(", ", requests)}]; cells [{string.Join(", ", after)}]";
                Assert.True(thrown is NotSupportedException && requests.Count == 0 && after.SequenceEqual(before), label);
                Assert.Equal(TableEntitySerializer.PartialUpdateRefusal(column), thrown!.Message);
            }
        }
    }

    // The lambda form names Extra_Value_Notes (Doc_Extra_Value_Notes below another owner): refused the same way.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Lambda_BelowANullableStruct_IsRefusedBeforeAnythingIsSent(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var large = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 3);
        var plain = stores.Store<PlainInitialised>(Table);
        var id = stores.Id("l1");
        await plain.CreateAsync(new PlainInitialised { Id = id }, Ct);
        var top = await WithPolicyAsync(OversizedCellPolicy.TrimSilently, () =>
            Record.ExceptionAsync(() => plain.UpdateAsync(id, b => b.SetProperty(x => x.Extra!.Value.Notes, large), Ct)));
        Assert.True(top is NotSupportedException, $"{kind}, x => x.Extra.Value.Notes: {top?.GetType().Name ?? "accepted"}");
        Assert.Equal(TableEntitySerializer.PartialUpdateRefusal("Extra_Value_Notes"), top!.Message);
        var nested = stores.Store<PlainNested>(Table);
        var nid = stores.Id("l2");
        await nested.CreateAsync(new PlainNested { Id = nid }, Ct);
        var below = await WithPolicyAsync(OversizedCellPolicy.TrimSilently, () =>
            Record.ExceptionAsync(() => nested.UpdateAsync(nid, b => b.SetProperty(x => x.Doc.Extra!.Value.Notes, large), Ct)));
        Assert.True(below is NotSupportedException, $"{kind}, x => x.Doc.Extra.Value.Notes: {below?.GetType().Name ?? "accepted"}");
        Assert.Equal(TableEntitySerializer.PartialUpdateRefusal("Doc_Extra_Value_Notes"), below!.Message);
    }

    // RV8 SERIALIZER-1, P1b: the whole-entity write of the same value is refused, and so is the builder update now; the
    // two write paths agree.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task EmptyNullableStruct_WholeEntityWriteAndBuilderUpdate_AreBothRefused(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var large = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 7);
        var store = stores.Store<PlainEmpty>(Table);
        var id = stores.Id("ne");
        await store.CreateAsync(new PlainEmpty { Id = id }, Ct);
        var whole = await Record.ExceptionAsync(() => store.UpsertAsync(new PlainEmpty { Id = id, Extra = new Att { Notes = large } }, Ct));
        var update = await WithPolicyAsync(OversizedCellPolicy.TrimSilently, () =>
            Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.Updates["Extra_Notes"] = large, Ct)));
        Assert.True(whole is NotSupportedException && update is NotSupportedException,
            $"{kind}: whole-entity upsert {whole?.GetType().Name ?? "accepted"}; Updates[Extra_Notes] {update?.GetType().Name ?? "accepted"}");
        Assert.Null((await stores.Store<PlainEmpty>(Table).OneAsync(id, Ct))!.Extra);
    }

    // An ordinary member of S beside the marked one is still accepted, and written as the plain serializer writes it.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AnOrdinaryMemberBesideTheMarkedOne_IsStillWritten(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        foreach (var (column, key) in new[] { ("Extra_Name", "c1"), ("Extra_Value_Name", "c2") })
        {
            var store = stores.Store<PlainInitialised>(Table);
            var id = stores.Id(key);
            await store.CreateAsync(new PlainInitialised { Id = id }, Ct);
            await store.UpdateAsync(id, b => b.Updates[column] = "n1", Ct);
            var row = (await stores.RawRowsAsync<PlainInitialised>(Table)).Single(r => r.RowKey == key);
            Assert.Equal("n1", row[column]);
            Assert.Equal(TableEntitySerializer.FlattenProperty(column, "n1"),
                Unified.Data.Tables.Providers.LargeValueRows.FlattenPropertyForUpdate(typeof(PlainInitialised), column, "n1"));
        }
        // The serializer's own column reads back into the struct.
        var back = (await stores.Store<PlainInitialised>(Table).OneAsync(stores.Id("c1"), Ct))!;
        Assert.Equal("n1", back.Extra?.Name);
        // And on an aware type.
        var aware = stores.Store<AwareInitialised>(Table);
        var aid = stores.Id("aware");
        await aware.CreateAsync(new AwareInitialised { Id = aid }, Ct);
        await aware.UpdateAsync(aid, b => b.Updates["Extra_Name"] = "n2", Ct);
        Assert.Equal("n2", (await stores.Store<AwareInitialised>(Table).OneAsync(aid, Ct))!.Extra?.Name);
    }

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    private static async Task<T> WithPolicyAsync<T>(OversizedCellPolicy policy, Func<ValueTask<T>> action)
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        try
        {
            return await action();
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    private static async Task WithPolicyAsync(OversizedCellPolicy policy, Func<Task> action)
    {
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        try
        {
            await action();
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }
}
