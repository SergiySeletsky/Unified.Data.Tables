using System.Runtime.Serialization;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A large value that no read would map back is refused at write with <see cref="NotSupportedException"/>
/// naming the column, before anything is stored, on both stores. That includes a <c>[TableValueSplit]</c>
/// member of a struct held in a NULLABLE property: a read cannot create the owner (the creator of
/// <c>Nullable&lt;T&gt;</c> yields null), so a stored value would make the row, and every query of its
/// partition, unreadable. A check that cannot decide (resolving the column throws) refuses too. A value that
/// fits one cell stays an ordinary cell, as for every member of such a struct (SERIALIZER-4, ruling R6: not
/// changed here), and a struct in a property that is not nullable keeps round-tripping.
/// (Review finding SERIALIZER-7 of fix wave 2.)
/// </summary>
public class NullableStructLargeValueTests
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

    private static string Ascii(int length, int seed)
    {
        var rng = new Random(seed);
        return string.Create(length, rng, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)r.Next(0x21, 0x7F);
        });
    }

    public struct Attachment
    {
        public Attachment()
        {
        }

        public string? Name { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    /// <summary>The reviewer's shape: not large-value aware by its declared types (the walk does not look below <c>Nullable&lt;T&gt;</c>).</summary>
    public sealed class Mail : Entity
    {
        public Attachment? Extra { get; set; }

        public string? Subject { get; set; }
    }

    /// <summary>The same shape, large-value aware by its class-level attribute: its writes take the large-value path.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class OptedInMail : Entity
    {
        public Attachment? Extra { get; set; }

        public int Rank { get; set; }
    }

    /// <summary>The remedy the refusal names: the nullable property holds an instance from the constructor.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class InitialisedMail : Entity
    {
        public Attachment? Extra { get; set; } = new Attachment();

        public int Rank { get; set; }
    }

    /// <summary>The same struct in a property that is not nullable.</summary>
    public sealed class Letter : Entity
    {
        public Attachment Extra { get; set; }

        public string? Subject { get; set; }
    }

    public interface IShape
    {
    }

    public sealed class Circle : IShape
    {
        public double Radius { get; set; }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    /// <summary>Aware by its class-level attribute; its interface-typed property has no instance a read could start from.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class Drawing : Entity
    {
        public IShape? Shape { get; set; }

        public int Rank { get; set; }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task LargeValueInANullableStruct_IsRefusedOrReadBack_NeverAnUnreadableRow(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = Ascii(40_000, 1);

        var refused = await Record.ExceptionAsync(() => stores.Store<Mail>().CreateAsync(
            new Mail { Id = stores.Id("r"), Subject = "s", Extra = new Attachment { Name = "n", Notes = notes } }, Ct));
        if (refused is not null)
        {
            Assert.IsType<NotSupportedException>(refused);
            Assert.Empty(await stores.RawRowsAsync<Mail>());
            return;
        }

        // Stored. Then the row must be readable: by its id, and as part of its partition.
        var one = await Record.ExceptionAsync(() => stores.Store<Mail>().OneAsync(stores.Id("r"), Ct));
        var query = await Record.ExceptionAsync(() => stores.Store<Mail>().QueryAsync(stores.Pk, Ct));
        Assert.True(one is null && query is null,
            $"The write was accepted; OneAsync then throws {one?.GetType().Name}: {one?.Message} ({one?.InnerException?.GetType().Name}), "
            + $"and QueryAsync of the partition throws {query?.GetType().Name}.");
        Assert.Equal(notes, (await stores.Store<Mail>().OneAsync(stores.Id("r"), Ct))!.Extra?.Notes);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task LargeValueInANullableStruct_IsRefusedOrReadBack_NeverAnUnreadableRow_WhenTheEntityIsOptedIn(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = Ascii(40_000, 1);

        var refused = await Record.ExceptionAsync(() => stores.Store<OptedInMail>().CreateAsync(
            new OptedInMail { Id = stores.Id("r"), Rank = 1, Extra = new Attachment { Name = "n", Notes = notes } }, Ct));
        if (refused is not null)
        {
            Assert.IsType<NotSupportedException>(refused);
            Assert.Empty(await stores.RawRowsAsync<OptedInMail>());
            return;
        }

        var one = await Record.ExceptionAsync(() => stores.Store<OptedInMail>().OneAsync(stores.Id("r"), Ct));
        var query = await Record.ExceptionAsync(() => stores.Store<OptedInMail>().QueryAsync(stores.Pk, Ct));
        Assert.True(one is null && query is null,
            $"The write was accepted; OneAsync then throws {one?.GetType().Name}: {one?.Message} ({one?.InnerException?.GetType().Name}), "
            + $"and QueryAsync of the partition throws {query?.GetType().Name}.");
        Assert.Equal(notes, (await stores.Store<OptedInMail>().OneAsync(stores.Id("r"), Ct))!.Extra?.Notes);
    }

    [Theory]
    [MemberData(nameof(StoresAndWrites))]
    public async Task LargeValueInANullableStruct_IsRefusedByEveryWrite_NamingTheColumn_BeforeAnythingIsStored(string kind, string write)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        var store = stores.Store<OptedInMail>();
        var entity = new OptedInMail { Id = id, Rank = 1 };
        if (write == "UpdateAsync")
        {
            await store.CreateAsync(new OptedInMail { Id = id, Rank = 1, Extra = new Attachment { Name = "before" } }, Ct);
            entity = (await store.OneAsync(id, Ct))!;
        }
        var before = await stores.RawRowsAsync<OptedInMail>();

        entity.Extra = new Attachment { Name = "n", Notes = Ascii(700_000, 2) };
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => write switch
        {
            "CreateAsync" => store.CreateAsync(entity, Ct),
            "UpsertAsync" => store.UpsertAsync(entity, Ct),
            "UpdateAsync" => store.UpdateAsync(entity, Ct),
            "CreateBatchAsync" => store.CreateBatchAsync([entity], Ct),
            _ => store.UpsertBatchAsync([entity], Ct),
        });

        Assert.Contains("'Extra_Notes'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nullable", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime type", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 1_000, ex.Message);
        var after = await stores.RawRowsAsync<OptedInMail>();
        Assert.Equal(before.Select(r => (r.RowKey, r.ETag)), after.Select(r => (r.RowKey, r.ETag)));
        Assert.Empty(await stores.BlobNamesAsync<OptedInMail>());
        var read = await stores.Store<OptedInMail>().QueryAsync(stores.Pk, Ct);
        Assert.Equal(write == "UpdateAsync" ? 1 : 0, read.Count());
    }

    /// <summary>The reviewer's shape, not aware by its declared types: the plain path refuses the member at every size (ruling R4).</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task LargeValueInANullableStruct_OfAnEntityThatIsNotAware_IsRefused_BeforeAnythingIsStored(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);

        foreach (var size in new[] { 1_000, 40_000 })
        {
            var ex = await Assert.ThrowsAsync<NotSupportedException>(() => stores.Store<Mail>().CreateAsync(
                new Mail { Id = stores.Id("r" + size), Subject = "s", Extra = new Attachment { Name = "n", Notes = Ascii(size, 1) } }, Ct));
            Assert.Contains("'Extra_Notes'", ex.Message, StringComparison.Ordinal);
        }
        Assert.Empty(await stores.RawRowsAsync<Mail>());
    }

    /// <summary>A value of one cell is an ordinary cell of the struct, written as on 0.8.3 (how it reads back is SERIALIZER-4, not changed).</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ValueOfOneCellInANullableStruct_IsWrittenAsAnOrdinaryCell(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = Ascii(1_000, 3);

        await stores.Store<OptedInMail>().CreateAsync(new OptedInMail { Id = stores.Id("r"), Rank = 1, Extra = new Attachment { Name = "n", Notes = notes } }, Ct);

        var row = Assert.Single(await stores.RawRowsAsync<OptedInMail>());
        Assert.Equal(notes, row["Extra_Notes"]);
        Assert.DoesNotContain(row.Keys, k => k.StartsWith("Extra_Notes__", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task LargeValueInAStructThatIsNotNullable_RoundTrips(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = Ascii(700_000, 4);

        await stores.Store<Letter>().CreateAsync(new Letter { Id = stores.Id("r"), Subject = "s", Extra = new Attachment { Name = "n", Notes = notes } }, Ct);

        var read = await stores.Store<Letter>().OneAsync(stores.Id("r"), Ct);
        Assert.Equal(notes, read!.Extra.Notes);
        Assert.Equal("n", read.Extra.Name);
        Assert.Equal(notes, Assert.Single(await stores.Store<Letter>().QueryAsync(stores.Pk, Ct)).Extra.Notes);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task LargeValueInANullableStruct_ThatTheConstructorInitialises_RoundTrips(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = Ascii(700_000, 6);

        await stores.Store<InitialisedMail>().CreateAsync(
            new InitialisedMail { Id = stores.Id("r"), Rank = 1, Extra = new Attachment { Name = "n", Notes = notes } }, Ct);

        var read = await stores.Store<InitialisedMail>().OneAsync(stores.Id("r"), Ct);
        Assert.Equal(notes, read!.Extra?.Notes);
        Assert.Equal("n", read.Extra?.Name);
        Assert.Equal(notes, Assert.Single(await stores.Store<InitialisedMail>().QueryAsync(stores.Pk, Ct)).Extra?.Notes);
    }

    /// <summary>
    /// The check cannot decide when resolving the column throws (an interface-typed property with no
    /// instance: a read has nothing to create). It refuses; it never lets the value through to a row whose
    /// every read fails.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task LargeValueBelowAnOwnerAReadCannotCreate_IsRefused_BeforeAnythingIsStored(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => stores.Store<Drawing>().CreateAsync(
            new Drawing { Id = stores.Id("r"), Rank = 1, Shape = new Circle { Radius = 2, Notes = Ascii(40_000, 5) } }, Ct));

        Assert.Contains("'Shape_Notes'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<Drawing>());
        Assert.Empty(await stores.BlobNamesAsync<Drawing>());
    }

    [Fact]
    public void TheCheck_RefusesWhenTheResolutionThrows_AndNeverConstructsAPlainType()
    {
        Assert.False(TypeMetadataCache.ReadsBack(typeof(OptedInMail), "Extra_Notes"));
        Assert.False(TypeMetadataCache.ReadsBack(typeof(Drawing), "Shape_Notes"));
        Assert.True(TypeMetadataCache.ReadsBack(typeof(Letter), "Extra_Notes"));
        // The read itself still reports what it cannot resolve (it is not changed).
        Assert.Throws<SerializationException>(() => new Azure.Data.Tables.TableEntity("p", "r") { ["Shape_Radius"] = 2.0 }.FromTableEntity<Drawing>());
    }
}
