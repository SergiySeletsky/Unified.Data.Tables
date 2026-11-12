using System.Linq.Expressions;
using System.Reflection;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// An interface that inherits one member name from two interfaces has two members of that name, and a column path
/// carries names only (SERIALIZER-3 of the review of fix wave 6): the R11 walk of a builder update followed only
/// the first member <c>Array.Find</c> returned, so whether the update was refused depended on the order in which
/// the interface lists its base interfaces, and a name-only path (a key of <see cref="UpdateBuilder{T}.Updates"/>,
/// or an expression built from the base interface's member) reached the misused branch and stored it trimmed.
/// Every member of the name is considered: the update is refused with the R11 misuse whatever the order and
/// whatever the path came from, before any request, on both stores, under every policy and at every size. With the
/// attribute where it belongs (a class member), every builder update keeps the large-value refusal.
/// </summary>
[Collection("OversizedCellPolicy")]
public class NameInheritedFromTwoInterfacesTests
{
    private const string Table = "IfaceNameFromTwo";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OversizedCellPolicy[] Policies =
        [OversizedCellPolicy.Throw, OversizedCellPolicy.TrimWithMarker, OversizedCellPolicy.TrimSilently];

    public interface IMarked
    {
        [TableValueSplit(SplitEncoding.Text)]
        string? Notes { get; set; }
    }

    public sealed class MarkedImpl : IMarked
    {
        public string? Notes { get; set; }
    }

    public sealed class PlainLeaf
    {
        public string? Notes { get; set; }
    }

    public interface ISideA
    {
        MarkedImpl Inner { get; set; }
    }

    public interface ISideB
    {
        PlainLeaf Inner { get; set; }
    }

    public interface IAB : ISideA, ISideB
    {
    }

    public interface IBA : ISideB, ISideA
    {
    }

    // The public Inner (the column the writer writes) is the misused one.
    public sealed class ABImpl : IAB, IBA
    {
        public MarkedImpl Inner { get; set; } = new();
        PlainLeaf ISideB.Inner { get; set; } = new();
    }

    public sealed class HolderAB : Entity
    {
        public IAB Doc { get; set; } = new ABImpl();
        public string? Title { get; set; }
    }

    public sealed class HolderBA : Entity
    {
        public IBA Doc { get; set; } = new ABImpl();
        public string? Title { get; set; }
    }

    public sealed class RowWriter : Entity
    {
        public string? Title { get; set; }
    }

    // The derived interface re-declares Inner with `new` and another type (C# resolves x.Doc.Inner to it), and the
    // class implements that one explicitly; its public Inner, the column the writer writes, is the misused one.
    public interface INewDer : ISideA
    {
        new PlainLeaf Inner { get; set; }
    }

    public sealed class NewImpl : INewDer
    {
        public MarkedImpl Inner { get; set; } = new();
        PlainLeaf INewDer.Inner { get; set; } = new();
    }

    public sealed class HolderNew : Entity
    {
        public INewDer Doc { get; set; } = new NewImpl();
        public string? Title { get; set; }
    }

    public static TheoryData<string, OversizedCellPolicy> StoresPolicies()
    {
        var d = new TheoryData<string, OversizedCellPolicy>();
        foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
            foreach (var p in Policies)
                d.Add(k, p);
        return d;
    }

    // x => x.Doc.Inner.Notes through the given interface member (the C# compiler reports CS0229 for x.Doc.Inner;
    // the expression API builds it).
    private static Expression<Func<T, string?>> Through<T>(PropertyInfo inner)
    {
        var x = Expression.Parameter(typeof(T), "x");
        var doc = Expression.Property(x, typeof(T).GetProperty("Doc")!);
        var leaf = Expression.Property(doc, inner);
        return Expression.Lambda<Func<T, string?>>(Expression.Property(leaf, inner.PropertyType.GetProperty("Notes")!), x);
    }

    private static async Task<(Exception? Thrown, IReadOnlyList<string> Writes, Azure.Data.Tables.TableEntity Row)> SeededUpdateAsync<T>(
        LargeValueStores stores, OversizedCellPolicy policy, Action<UpdateBuilder<T>> update) where T : class, IEntity, new()
    {
        var id = stores.Id("r");
        await stores.Store<RowWriter>(Table).CreateAsync(new RowWriter { Id = id, Title = "t" }, Ct);
        await stores.ShareTableAsync<RowWriter, T>(Table);
        var store = stores.Store<T>(Table);
        Exception? thrown = null;
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        IReadOnlyList<string> writes;
        try
        {
            writes = await stores.StorageWritesAsync(store, async () =>
                thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, update, Ct)));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
        return (thrown, writes, (await stores.RawRowsAsync<T>(Table)).Single());
    }

    // R11: a builder update never writes a cell for a member marked on an interface. Here the column the update
    // names is, for the writer, ABImpl.Inner.Notes, i.e. IMarked.Notes.
    [Theory]
    [MemberData(nameof(StoresPolicies))]
    public async Task BuilderUpdate_ThroughTheMisusedBranch_IsRefused_WhateverTheOrderOfTheInheritedInterfaces(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 9);
        var path = Through<HolderBA>(typeof(ISideA).GetProperty("Inner")!);
        var (thrown, writes, row) = await SeededUpdateAsync<HolderBA>(stores, policy, b => b.SetProperty(path, notes));
        Assert.True(thrown is InvalidOperationException && !row.Keys.Any(k => k.Contains("Notes", StringComparison.Ordinal)),
            BuilderUpdateThroughAnInheritedInterfaceMemberTests.Describe("IBA, through ISideA.Inner", policy, notes.Length, thrown, writes, row));
    }

    // The order in which an interface lists its base interfaces no longer decides which branch the path walk checks:
    // both orders are refused (the probe of the review asserted the order decided it).
    [Fact]
    public void TheOrderOfTheInheritedInterfaces_DoesNotDecideThePathWalk()
    {
        var ab = string.Join(", ", typeof(IAB).GetInterfaces().Select(i => i.Name));
        var ba = string.Join(", ", typeof(IBA).GetInterfaces().Select(i => i.Name));
        var listedAb = string.Join(", ", TypeMetadataCache.DeclaredProperties(typeof(IAB)).Select(p => $"{p.DeclaringType!.Name}.{p.Name}:{p.PropertyType.Name}"));
        var listedBa = string.Join(", ", TypeMetadataCache.DeclaredProperties(typeof(IBA)).Select(p => $"{p.DeclaringType!.Name}.{p.Name}:{p.PropertyType.Name}"));
        var whole = Record.Exception(() => TableEntitySerializer.ToTableEntity(new HolderBA { Id = "a|b" }, "a", "b"));
        var abRefused = Record.Exception(() => TypeMetadataCache.RefuseInterfaceMemberSplitOnPath(typeof(HolderAB), "Doc_Inner_Notes"));
        var baRefused = Record.Exception(() => TypeMetadataCache.RefuseInterfaceMemberSplitOnPath(typeof(HolderBA), "Doc_Inner_Notes"));
        Assert.True(abRefused is InvalidOperationException && baRefused is InvalidOperationException && abRefused.Message == baRefused.Message,
            $"IAB.GetInterfaces: [{ab}] lists [{listedAb}] -> path walk: {abRefused?.GetType().Name ?? "accepted"}: {abRefused?.Message}\n" +
            $"IBA.GetInterfaces: [{ba}] lists [{listedBa}] -> path walk: {baRefused?.GetType().Name ?? "accepted"}: {baRefused?.Message}\n" +
            $"whole-entity write of HolderBA: {whole?.GetType().Name}: {whole?.Message[..Math.Min(140, whole.Message.Length)]}");
        Assert.Contains("[TableValueSplit] on IMarked.Notes", baRefused!.Message, StringComparison.Ordinal);
    }

    // The must-guarantee beyond the reproduction: both orders of the base interfaces, and every way a path can be
    // given (an expression through either base interface's member, a key of the public Updates dictionary).
    public static TheoryData<string, string, string, OversizedCellPolicy> OrdersSourcesStoresPolicies()
    {
        var d = new TheoryData<string, string, string, OversizedCellPolicy>();
        foreach (var order in new[] { "IAB", "IBA" })
            foreach (var source in new[] { "through ISideA.Inner", "through ISideB.Inner", "Updates dictionary" })
                foreach (var k in new[] { LargeValueStores.Memory, LargeValueStores.Azurite })
                    foreach (var p in Policies)
                        d.Add(order, source, k, p);
        return d;
    }

    [Theory]
    [MemberData(nameof(OrdersSourcesStoresPolicies))]
    public Task BuilderUpdate_OfTheInheritedName_IsRefused_WhateverTheOrderAndThePathSource(string order, string source, string kind, OversizedCellPolicy policy) =>
        order == "IAB"
            ? RefusedEverySourceAsync<HolderAB>(order, source, kind, policy)
            : RefusedEverySourceAsync<HolderBA>(order, source, kind, policy);

    private static async Task RefusedEverySourceAsync<T>(string order, string source, string kind, OversizedCellPolicy policy) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        foreach (var length in new[] { 10, 200_000 })
        {
            var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(length, 19);
            Action<UpdateBuilder<T>> update = source switch
            {
                "through ISideA.Inner" => b => b.SetProperty(Through<T>(typeof(ISideA).GetProperty("Inner")!), notes),
                "through ISideB.Inner" => b => b.SetProperty(Through<T>(typeof(ISideB).GetProperty("Inner")!), notes),
                _ => b => b.Updates["Doc_Inner_Notes"] = notes,
            };
            var id = stores.Id("o" + length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await stores.Store<RowWriter>(Table).CreateAsync(new RowWriter { Id = id, Title = "t" }, Ct);
            await stores.ShareTableAsync<RowWriter, T>(Table);
            var store = stores.Store<T>(Table);
            Exception? thrown = null;
            var before = TableEntitySerializer.OversizedCellPolicy;
            TableEntitySerializer.OversizedCellPolicy = policy;
            IReadOnlyList<string> writes;
            try
            {
                writes = await stores.StorageWritesAsync(store, async () =>
                    thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, update, Ct)));
            }
            finally
            {
                TableEntitySerializer.OversizedCellPolicy = before;
            }
            var row = (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == id.Split('|')[1]);
            Assert.True(thrown is InvalidOperationException && writes.Count == 0 && !row.Keys.Any(k => k.Contains("Notes", StringComparison.Ordinal)),
                BuilderUpdateThroughAnInheritedInterfaceMemberTests.Describe($"{order}, {source}", policy, length, thrown, writes, row));
            Assert.Contains("[TableValueSplit] on IMarked.Notes", thrown!.Message, StringComparison.Ordinal);
        }
    }

    // Scenario A of the finding (`new` with another type): the lambda names the re-declared, unmarked member, but the
    // writer stores the public, misused one under the same column. Checking every member of the name refuses it too.
    // This asks for more than the verdict requires (the verdict rules scenario A not a defect); it is kept because the
    // rule (every member of a name counts) gives it.
    [Theory]
    [MemberData(nameof(StoresPolicies))]
    public async Task BuilderUpdate_ThroughANewRedeclaredMember_WhoseColumnIsTheMisusedOne_IsRefused(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 10);
        var whole = Record.Exception(() => TableEntitySerializer.ToTableEntity(new HolderNew { Id = "a|b" }, "a", "b"));
        var (thrown, writes, row) = await SeededUpdateAsync<HolderNew>(stores, policy, b => b.SetProperty(x => x.Doc.Inner.Notes, notes));
        Assert.True(thrown is InvalidOperationException && !row.Keys.Any(k => k.Contains("Notes", StringComparison.Ordinal)),
            BuilderUpdateThroughAnInheritedInterfaceMemberTests.Describe("INewDer (new-redeclared), C# lambda", policy, notes.Length, thrown, writes, row)
            + $" | whole-entity write: {whole?.GetType().Name}");
    }

    // ── The verifier's cases ────────────────────────────────────────────────────────────────────────────

    public interface ISnMarked
    {
        [TableValueSplit(SplitEncoding.Text)]
        string? Notes { get; set; }
    }

    public sealed class SnMarkedImpl : ISnMarked
    {
        public string? Notes { get; set; }
    }

    public sealed class SnPlainLeaf
    {
        public string? Notes { get; set; }
    }

    public interface ISnSideA
    {
        SnMarkedImpl Inner { get; set; }
    }

    public interface ISnSideB
    {
        SnPlainLeaf Inner { get; set; }
    }

    // The name inherited from two interfaces, the unmarked one first.
    public interface ISnBA : ISnSideB, ISnSideA
    {
    }

    public sealed class SnBAImpl : ISnBA
    {
        public SnMarkedImpl Inner { get; set; } = new();
        SnPlainLeaf ISnSideB.Inner { get; set; } = new();
    }

    public sealed class SnHolderBA : Entity
    {
        public ISnBA Doc { get; set; } = new SnBAImpl();
        public string? Title { get; set; }
    }

    // Reachability: C# reports x.Doc.Inner as ambiguous (CS0229), and the builder refuses a cast inside the path,
    // so no lambda a user writes reaches the second branch; a built expression or the Updates dictionary does.
    [Fact]
    public void ACastInsideTheUpdatedPath_IsRefusedByTheBuilder()
    {
        var builder = new UpdateBuilder<SnHolderBA>();
        var thrown = Record.Exception(() => builder.SetProperty(x => ((ISnSideA)x.Doc).Inner.Notes, "v"));
        Assert.IsType<ArgumentException>(thrown);
    }

    // The public UpdateBuilder.Updates dictionary (a column path by name, as a generic PATCH handler fills it): the
    // store writes SnBAImpl.Inner (the misused branch) under this column. The type creates its own row (Doc empty).
    [Theory]
    [MemberData(nameof(StoresPolicies))]
    public async Task TheUpdatesDictionary_ThroughTheMisusedBranchOfAnInheritedName_IsRefused(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("s3");
        var store = stores.Store<SnHolderBA>(Table);
        await store.CreateAsync(new SnHolderBA { Id = id, Title = "t", Doc = null! }, Ct);
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 64);
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        Exception? thrown;
        try
        {
            thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.Updates["Doc_Inner_Notes"] = notes, Ct));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
        var row = (await stores.RawRowsAsync<SnHolderBA>(Table)).Single(r => r.RowKey == "s3");
        var cells = row.Keys.Where(k => k.Contains("Notes", StringComparison.Ordinal)).Select(k => $"{k}({(row[k] as string)?.Length} chars)").ToList();
        Assert.True(thrown is InvalidOperationException && cells.Count == 0,
            $"{kind}, {policy}: update {(thrown is null ? "accepted and reported as written" : thrown.GetType().Name)}; stored cells: [{string.Join(", ", cells)}]");
    }

    // With a CORRECT attribute (on the class member): an interface inheriting Inner from two interfaces (unmarked
    // first) is aware, its value is stored and read back whole, and builder updates through either branch and the
    // Updates dictionary keep the large-value refusal (NotSupportedException).
    public sealed class SnLeafOk
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public interface ISnOkA
    {
        SnLeafOk Inner { get; set; }
    }

    public interface ISnOkBA : ISnSideB, ISnOkA
    {
    }

    public sealed class SnOkImpl : ISnOkBA
    {
        public SnLeafOk Inner { get; set; } = new();
        SnPlainLeaf ISnSideB.Inner { get; set; } = new();
    }

    public sealed class SnOkHolder : Entity
    {
        public ISnOkBA Doc { get; set; } = new SnOkImpl();
        public string? Title { get; set; }
    }

    [Theory]
    [MemberData(nameof(StoresPolicies))]
    public async Task CorrectAttribute_SameNameOnTwoInheritedInterfaces_StoresWhole_AndEveryBuilderUpdateIsRefused(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("l3");
        var notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 66);
        var problems = new List<string>();
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        try
        {
            if (!TypeMetadataCache.IsLargeValueAware(typeof(SnOkHolder)))
                problems.Add("not aware");
            var store = stores.Store<SnOkHolder>(Table);
            var entity = new SnOkHolder { Id = id, Title = "t" };
            ((SnOkImpl)entity.Doc).Inner.Notes = notes;
            await store.CreateAsync(entity, Ct);
            foreach (var read in new[] { await store.OneAsync(id, Ct), await store.OneAsync(id, Ct), await stores.Store<SnOkHolder>(Table).OneAsync(id, Ct) })
            {
                if (((SnOkImpl)read!.Doc).Inner.Notes != notes)
                    problems.Add($"read {((SnOkImpl)read.Doc).Inner.Notes?.Length}");
            }
            foreach (var (name, set) in new (string, Action<UpdateBuilder<SnOkHolder>>)[]
                     {
                         ("through ISnOkA.Inner", b => b.SetProperty(Through<SnOkHolder>(typeof(ISnOkA).GetProperty("Inner")!), "x")),
                         ("through ISnSideB.Inner", b => b.SetProperty(Through<SnOkHolder>(typeof(ISnSideB).GetProperty("Inner")!), "x")),
                         ("Updates dictionary", b => b.Updates["Doc_Inner_Notes"] = "x"),
                     })
            {
                var thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, set, Ct));
                if (thrown is not NotSupportedException)
                    problems.Add($"update {name}: {thrown?.GetType().Name ?? "accepted"}");
            }
            var after = (await stores.Store<SnOkHolder>(Table).OneAsync(id, Ct))!;
            if (((SnOkImpl)after.Doc).Inner.Notes != notes)
                problems.Add($"after the updates {((SnOkImpl)after.Doc).Inner.Notes?.Length}");
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
        Assert.True(problems.Count == 0, $"{kind}, {policy}: " + string.Join("; ", problems));
    }
}
