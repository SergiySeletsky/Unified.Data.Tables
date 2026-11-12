using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.Serialization;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A correctly marked value below a member that a property declared as a non-sealed class does not store by the
/// writer's rule, while the class the property holds at run time does (SERIALIZER-1 of the review of fix wave 7): an
/// <c>[IgnoreDataMember]</c> abstract or virtual member overridden without the attribute (it is not inherited), a
/// get-only member re-declared with <c>new</c> and a setter, and a base class stored as one JSON value by its
/// constructors whose subclass the writer flattens. The C# compiler resolves <c>x.Doc.Inner.Notes</c> through the
/// declared types to a marked member of a class, so the declared types name it.
/// <para>
/// Rule A of fix wave 8: a builder update never writes a member that is marked, by any name the declared types offer
/// (any accessors, any attributes, inherited, hidden with <c>new</c>, overridden): it is refused with
/// <see cref="NotSupportedException"/> before anything is sent, for every entity type, whether the path is a lambda,
/// a built expression or a key of <see cref="UpdateBuilder{T}.Updates"/>. Rule C: no refusal names a declaration that
/// is already in place, and the remedies a refusal names work when followed.
/// </para>
/// </summary>
[Collection("OversizedCellPolicy")]
public class MarkedValueBelowAMemberOnlyTheRuntimeClassStoresTests
{
    private const string Table = "RuntimeClassStoresMember";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Leaf
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    // [IgnoreDataMember] on an abstract member: the attribute is not inherited (Inherited = false), so the override
    // that implements it is a stored property of the class the writer writes.
    public abstract class DocAbstractIgnored
    {
        [IgnoreDataMember]
        public abstract Leaf Inner { get; set; }
    }

    public sealed class DocAbstractImpl : DocAbstractIgnored
    {
        public override Leaf Inner { get; set; } = new();
    }

    // The same on a virtual member of a base class.
    public class DocVirtualIgnored
    {
        [IgnoreDataMember]
        public virtual Leaf Inner { get; set; } = new();
    }

    public sealed class DocVirtualOverride : DocVirtualIgnored
    {
        public override Leaf Inner { get; set; } = new();
    }

    // A get-only member of a base class, re-declared with `new` and a setter by the class the property holds.
    public class DocGetOnlyBase
    {
        public Leaf Inner { get; } = new();
    }

    public sealed class DocNewSetter : DocGetOnlyBase
    {
        public new Leaf Inner { get; set; } = new();
    }

    // A base class the declared walk takes for one JSON cell (its only constructor has a parameter), whose subclass
    // has a parameterless constructor: the writer flattens the object the property holds, Inner included.
    public class DocCtorBase
    {
        public DocCtorBase(int seed) => Seed = seed;

        public int Seed { get; set; }

        public Leaf Inner { get; set; } = new();
    }

    public sealed class DocCtorSub : DocCtorBase
    {
        public DocCtorSub()
            : base(0)
        {
        }
    }

    public sealed class HolderCtor : Entity
    {
        public DocCtorBase Doc { get; set; } = new DocCtorSub();
        public string? Title { get; set; }
    }

    public sealed class HolderAbstract : Entity
    {
        public DocAbstractIgnored Doc { get; set; } = new DocAbstractImpl();
        public string? Title { get; set; }
    }

    public sealed class HolderVirtual : Entity
    {
        public DocVirtualIgnored Doc { get; set; } = new DocVirtualOverride();
        public string? Title { get; set; }
    }

    public sealed class HolderNewSetter : Entity
    {
        public DocGetOnlyBase Doc { get; set; } = new DocNewSetter();
        public string? Title { get; set; }
    }

    // Control: the property declared as the class that stores the member.
    public sealed class HolderDeclaredImpl : Entity
    {
        public DocAbstractImpl Doc { get; set; } = new();
        public string? Title { get; set; }
    }

    // Control: the class-level remedy (R3) on the abstract shape.
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class HolderAbstractMarked : Entity
    {
        public DocAbstractIgnored Doc { get; set; } = new DocAbstractImpl();
    }

    // The abstract shape with a TableBlobContent leaf.
    public sealed class FileLeaf
    {
        public TableBlobContent? File { get; set; }
    }

    public abstract class FileDocAbstractIgnored
    {
        [IgnoreDataMember]
        public abstract FileLeaf Inner { get; set; }
    }

    public sealed class FileDocAbstractImpl : FileDocAbstractIgnored
    {
        public override FileLeaf Inner { get; set; } = new();
    }

    public sealed class FileHolderAbstract : Entity
    {
        public FileDocAbstractIgnored Doc { get; set; } = new FileDocAbstractImpl();
        public string? Title { get; set; }
    }

    private static readonly OversizedCellPolicy[] TrimPolicies = [OversizedCellPolicy.TrimSilently, OversizedCellPolicy.TrimWithMarker];

    private static readonly OversizedCellPolicy[] Policies = [OversizedCellPolicy.Throw, .. TrimPolicies];

    private static readonly string[] Kinds = [LargeValueStores.Memory, LargeValueStores.Azurite];

    internal static readonly string[] ShapeNames =
        ["abstract [IgnoreDataMember]", "virtual [IgnoreDataMember]", "get-only, new with a setter", "base stored as JSON by its constructors, subclass flattened"];

    public static TheoryData<string, string, OversizedCellPolicy> ShapesStoresTrimPolicies()
    {
        var d = new TheoryData<string, string, OversizedCellPolicy>();
        foreach (var s in ShapeNames)
            foreach (var k in Kinds)
                foreach (var p in TrimPolicies)
                    d.Add(s, k, p);
        return d;
    }

    public static TheoryData<string, string, OversizedCellPolicy> ShapesStoresPolicies()
    {
        var d = new TheoryData<string, string, OversizedCellPolicy>();
        foreach (var s in ShapeNames)
            foreach (var k in Kinds)
                foreach (var p in Policies)
                    d.Add(s, k, p);
        return d;
    }

    public static TheoryData<string, string> ShapesStores()
    {
        var d = new TheoryData<string, string>();
        foreach (var s in ShapeNames)
            foreach (var k in Kinds)
                d.Add(s, k);
        return d;
    }

    public static TheoryData<string> Stores() => [.. Kinds];

    private static readonly string Notes = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(200_000, 71);

    // ── SERIALIZER-1 (reviewer): the builder update written as C# ────────────────────────────────────────

    // The type creates its own row (Notes empty), then sets Notes with an ordinary builder update written as C#,
    // as an application does. Rule 1: refused, or stored whole; never stored trimmed and reported as written.
    [Theory]
    [MemberData(nameof(ShapesStoresTrimPolicies))]
    public Task BuilderUpdate_OfACorrectlyMarkedValue_BelowAMemberOnlyTheRuntimeClassStores_IsNeverStoredTrimmed(string shape, string kind, OversizedCellPolicy policy) =>
        shape switch
        {
            "abstract [IgnoreDataMember]" => UpdateAsync<HolderAbstract>(shape, kind, policy, b => b.SetProperty(x => x.Doc.Inner.Notes, Notes), x => x.Doc.Inner.Notes),
            "virtual [IgnoreDataMember]" => UpdateAsync<HolderVirtual>(shape, kind, policy, b => b.SetProperty(x => x.Doc.Inner.Notes, Notes), x => x.Doc.Inner.Notes),
            "get-only, new with a setter" => UpdateAsync<HolderNewSetter>(shape, kind, policy, b => b.SetProperty(x => x.Doc.Inner.Notes, Notes),
                x => ((DocNewSetter)x.Doc).Inner.Notes),
            _ => UpdateAsync<HolderCtor>(shape, kind, policy, b => b.SetProperty(x => x.Doc.Inner.Notes, Notes), x => x.Doc.Inner.Notes),
        };

    private static async Task UpdateAsync<T>(string shape, string kind, OversizedCellPolicy policy, Action<UpdateBuilder<T>> update, Func<T, string?> read)
        where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("u");
        var store = stores.Store<T>(Table);
        await store.CreateAsync(new T { Id = id }, Ct);
        Exception? thrown;
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        try
        {
            thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, update, Ct));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
        var back = read((await stores.Store<T>(Table).OneAsync(id, Ct))!);
        var row = (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "u");
        var cells = string.Join(", ", row.Keys.Where(k => k.Contains("Notes", StringComparison.Ordinal)).Select(k => $"{k}({(row[k] as string)?.Length} chars)"));
        Assert.True(thrown is not null || back == Notes,
            $"{shape}, {kind}, {policy}: aware={TypeMetadataCache.IsLargeValueAware(typeof(T))}; update " +
            $"{(thrown is null ? "accepted and reported as written" : thrown.GetType().Name)}; read back {back?.Length.ToString(CultureInfo.InvariantCulture) ?? "null"} " +
            $"of {Notes.Length} characters; stored cells: [{cells}]");
    }

    // ── SERIALIZER-1 (verifier): the same through a name-only key of UpdateBuilder.Updates ──────────────────

    [Theory]
    [MemberData(nameof(ShapesStores))]
    public Task UpdatesKey_BelowAMemberOnlyTheRuntimeClassStores_IsNeverStoredTrimmed(string shape, string kind) =>
        shape switch
        {
            "abstract [IgnoreDataMember]" => UpdatesKeyAsync<HolderAbstract>(shape, kind, x => x.Doc.Inner.Notes),
            "virtual [IgnoreDataMember]" => UpdatesKeyAsync<HolderVirtual>(shape, kind, x => x.Doc.Inner.Notes),
            "get-only, new with a setter" => UpdatesKeyAsync<HolderNewSetter>(shape, kind, x => ((DocNewSetter)x.Doc).Inner.Notes),
            _ => UpdatesKeyAsync<HolderCtor>(shape, kind, x => x.Doc.Inner.Notes),
        };

    private static async Task UpdatesKeyAsync<T>(string shape, string kind, Func<T, string?> read) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("k");
        var store = stores.Store<T>(Table);
        await store.CreateAsync(new T { Id = id }, Ct);
        Exception? thrown;
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimSilently;
        try
        {
            thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.Updates["Doc_Inner_Notes"] = Notes, Ct));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
        var back = read((await stores.Store<T>(Table).OneAsync(id, Ct))!);
        Assert.True(thrown is not null || back == Notes,
            $"{shape}, {kind}, TrimSilently, Updates[\"Doc_Inner_Notes\"]: update {(thrown is null ? "accepted and reported as written" : thrown.GetType().Name)}; " +
            $"read back {back?.Length.ToString(CultureInfo.InvariantCulture) ?? "null"} of {Notes.Length} characters");
    }

    // ── Rule A, as the verdict requires it: NotSupportedException, no request, no cell, every source, size, policy ─

    [Theory]
    [MemberData(nameof(ShapesStoresPolicies))]
    public Task BuilderUpdate_OfTheMarkedValue_ByEveryNameOfThePath_IsRefusedWithNotSupportedException_BeforeAnyRequest(string shape, string kind, OversizedCellPolicy policy) =>
        shape switch
        {
            "abstract [IgnoreDataMember]" => RefusedAsync<HolderAbstract>(shape, kind, policy, (b, v) => b.SetProperty(x => x.Doc.Inner.Notes, v)),
            "virtual [IgnoreDataMember]" => RefusedAsync<HolderVirtual>(shape, kind, policy, (b, v) => b.SetProperty(x => x.Doc.Inner.Notes, v)),
            "get-only, new with a setter" => RefusedAsync<HolderNewSetter>(shape, kind, policy, (b, v) => b.SetProperty(x => x.Doc.Inner.Notes, v)),
            _ => RefusedAsync<HolderCtor>(shape, kind, policy, (b, v) => b.SetProperty(x => x.Doc.Inner.Notes, v)),
        };

    private static async Task RefusedAsync<T>(string shape, string kind, OversizedCellPolicy policy, Action<UpdateBuilder<T>, string> lambda)
        where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("r");
        var store = stores.Store<T>(Table);
        await store.CreateAsync(new T { Id = id }, Ct);
        var sources = new (string Name, Action<UpdateBuilder<T>, string> Set)[]
        {
            ("lambda", lambda),
            ("built expression", (b, v) => b.SetProperty(BuiltPath<T>("Doc", "Inner", "Notes"), v)),
            ("Updates key", (b, v) => b.Updates["Doc_Inner_Notes"] = v),
        };
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        try
        {
            foreach (var (source, set) in sources)
            {
                foreach (var length in new[] { 10, 200_000 })
                {
                    var value = BuilderUpdateThroughAnInheritedInterfaceMemberTests.Ascii(length, length);
                    Exception? thrown = null;
                    var writes = await stores.StorageWritesAsync(store, async () =>
                        thrown = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => set(b, value), Ct)));
                    var row = (await stores.RawRowsAsync<T>(Table)).Single(r => r.RowKey == "r");
                    Assert.True(thrown is NotSupportedException && writes.Count == 0 && !row.Keys.Any(k => k.Contains("Notes", StringComparison.Ordinal)),
                        BuilderUpdateThroughAnInheritedInterfaceMemberTests.Describe($"{shape}, {kind}, {source}", policy, length, thrown, writes, row));
                    Assert.Equal(TableEntitySerializer.PartialUpdateRefusal("Doc_Inner_Notes"), thrown!.Message);
                }
            }
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    // x => (object)x.A.B.C, built through the declared members the C# compiler resolves (the most derived first).
    internal static Expression<Func<T, object>> BuiltPath<T>(params string[] segments)
    {
        var x = Expression.Parameter(typeof(T), "x");
        Expression body = x;
        foreach (var segment in segments)
            body = Expression.Property(body, body.Type.GetProperties().First(p => p.Name == segment && p.DeclaringType == MostDerived(body.Type, segment)));
        return Expression.Lambda<Func<T, object>>(Expression.Convert(body, typeof(object)), x);
    }

    private static Type? MostDerived(Type type, string name)
    {
        for (var cur = type; cur is not null; cur = cur.BaseType)
        {
            if (cur.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly) is not null)
                return cur;
        }
        return null;
    }

    // ── Rule C: the plain-store refusal names no declaration that is already in place ──────────────────────

    // The plain-store refusal of a whole-entity write names no declaration that is already in place: Doc.Inner IS
    // declared as Leaf, the type that carries the member.
    [Theory]
    [MemberData(nameof(ShapesStores))]
    public Task ThePlainStoreRefusal_NamesNoDeclarationThatIsAlreadyInPlace(string shape, string kind) =>
        shape switch
        {
            "abstract [IgnoreDataMember]" => WholeAsync(shape, kind, new HolderAbstract { Doc = new DocAbstractImpl { Inner = { Notes = "ten chars!" } } }),
            "virtual [IgnoreDataMember]" => WholeAsync(shape, kind, new HolderVirtual { Doc = new DocVirtualOverride { Inner = { Notes = "ten chars!" } } }),
            "get-only, new with a setter" => WholeAsync(shape, kind, new HolderNewSetter { Doc = new DocNewSetter { Inner = { Notes = "ten chars!" } } }),
            _ => WholeAsync(shape, kind, new HolderCtor { Doc = new DocCtorSub { Inner = { Notes = "ten chars!" } } }),
        };

    private static async Task WholeAsync<T>(string shape, string kind, T entity) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        entity.Id = stores.Id("w");
        var thrown = await Record.ExceptionAsync(() => stores.Store<T>(Table).CreateAsync(entity, Ct));
        Assert.True(thrown is null || !thrown.Message.Contains($"declare {typeof(T).Name}.Doc.Inner with the type that carries the member ({nameof(Leaf)})", StringComparison.Ordinal),
            $"{shape}, {kind}: {thrown?.GetType().Name}: {thrown?.Message}");
    }

    // A TableBlobContent below the same member: both refusals come before anything is written, and neither names a
    // declaration that is already in place: not the column path "Doc_Inner_File" as TableBlobContent (FileLeaf.File
    // IS a TableBlobContent), not Doc.Inner as FileLeaf (it is). SERIALIZER-1 of the review of fix wave 7 (verifier).
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TableBlobContent_BelowAMemberOnlyTheRuntimeClassStores_IsRefused_AndNoRefusalNamesADeclarationInPlace(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var id = stores.Id("f");
        var store = stores.Store<FileHolderAbstract>(Table);
        await store.CreateAsync(new FileHolderAbstract { Id = id }, Ct);
        var content = TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream");
        Exception? update = null;
        var writes = await stores.StorageWritesAsync(store, async () =>
            update = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.SetProperty(x => x.Doc.Inner.File, content), Ct)));
        var entity = new FileHolderAbstract { Id = stores.Id("g") };
        ((FileDocAbstractImpl)entity.Doc).Inner.File = TableBlobContent.FromBytes([4, 5, 6], "application/octet-stream");
        var whole = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));
        var report = $"{kind}: builder update {update?.GetType().Name ?? "accepted"} (writes [{string.Join(", ", writes)}]): {update?.Message}\n" +
                     $"whole-entity write {whole?.GetType().Name ?? "accepted"}: {whole?.Message}";
        Assert.True(update is NotSupportedException && writes.Count == 0, report);
        Assert.True(!update!.Message.Contains($"Declare {nameof(FileHolderAbstract)}.Doc_Inner_File as TableBlobContent", StringComparison.Ordinal)
                    && (whole is null || !whole.Message.Contains($"declare {nameof(FileHolderAbstract)}.Doc.Inner with the type that carries the member ({nameof(FileLeaf)})", StringComparison.Ordinal)),
            report);
    }

    // ── Rule C: every remedy, followed ────────────────────────────────────────────────────────────────────
    //
    // For each shape the candidates of the brief: mark the entity class; initialise the property (already in place in
    // every shape: Doc holds the runtime class on a new entity); declare the property with the runtime type; remove
    // [IgnoreDataMember] (shapes 1 and 2) or the `new` re-declaration (shape 3); and for shapes 3 and 4 the change of
    // the declared class that the writer's rule asks for (a setter on the base member; a parameterless constructor on
    // the base class).

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class HolderVirtualMarked : Entity
    {
        public DocVirtualIgnored Doc { get; set; } = new DocVirtualOverride();
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class HolderNewSetterMarked : Entity
    {
        public DocGetOnlyBase Doc { get; set; } = new DocNewSetter();
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class HolderCtorMarked : Entity
    {
        public DocCtorBase Doc { get; set; } = new DocCtorSub();
    }

    public sealed class HolderVirtualDeclared : Entity
    {
        public DocVirtualOverride Doc { get; set; } = new();
    }

    public sealed class HolderNewSetterDeclared : Entity
    {
        public DocNewSetter Doc { get; set; } = new();
    }

    public sealed class HolderCtorDeclared : Entity
    {
        public DocCtorSub Doc { get; set; } = new();
    }

    public abstract class DocAbstractStored
    {
        public abstract Leaf Inner { get; set; }
    }

    public sealed class DocAbstractStoredImpl : DocAbstractStored
    {
        public override Leaf Inner { get; set; } = new();
    }

    public sealed class HolderAbstractNoIgnore : Entity
    {
        public DocAbstractStored Doc { get; set; } = new DocAbstractStoredImpl();
    }

    public class DocVirtualStored
    {
        public virtual Leaf Inner { get; set; } = new();
    }

    public sealed class DocVirtualStoredOverride : DocVirtualStored
    {
        public override Leaf Inner { get; set; } = new();
    }

    public sealed class HolderVirtualNoIgnore : Entity
    {
        public DocVirtualStored Doc { get; set; } = new DocVirtualStoredOverride();
    }

    // Shape 3 without the re-declaration: the class the property holds inherits the get-only member, which the writer
    // does not store.
    public sealed class DocNoNewSub : DocGetOnlyBase
    {
    }

    public sealed class HolderNoNew : Entity
    {
        public DocGetOnlyBase Doc { get; set; } = new DocNoNewSub();
    }

    public class DocBaseWithSetter
    {
        public Leaf Inner { get; set; } = new();
    }

    public sealed class DocBaseWithSetterSub : DocBaseWithSetter
    {
    }

    public sealed class HolderBaseWithSetter : Entity
    {
        public DocBaseWithSetter Doc { get; set; } = new DocBaseWithSetterSub();
    }

    public class DocCtorBaseWithDefault
    {
        public DocCtorBaseWithDefault()
        {
        }

        public DocCtorBaseWithDefault(int seed) => Seed = seed;

        public int Seed { get; set; }

        public Leaf Inner { get; set; } = new();
    }

    public sealed class DocCtorWithDefaultSub : DocCtorBaseWithDefault
    {
    }

    public sealed class HolderCtorWithDefault : Entity
    {
        public DocCtorBaseWithDefault Doc { get; set; } = new DocCtorWithDefaultSub();
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class FileHolderAbstractMarked : Entity
    {
        public FileDocAbstractIgnored Doc { get; set; } = new FileDocAbstractImpl();
    }

    public sealed class FileHolderDeclared : Entity
    {
        public FileDocAbstractImpl Doc { get; set; } = new();
    }

    /// <summary>
    /// One remedy of one shape: the type that follows it, how to put the value in a new entity of it, how to read it
    /// back, whether a path can be written as C# through the declared types, and whether it works (stores the value
    /// whole, read back whole by every read).
    /// </summary>
    public sealed record Remedy(string Shape, string Name, Type Entity, Action<object, object> Set, Func<object, object?> Get, bool Works, bool Named);

    private static Leaf InnerOf(object doc) => (Leaf)doc.GetType().GetProperty(nameof(DocAbstractImpl.Inner), typeof(Leaf))!.GetValue(doc)!;

    private static object DocOf(object entity) => entity.GetType().GetProperty("Doc")!.GetValue(entity)!;

    private static readonly Action<object, object> SetNotes = (e, v) => InnerOf(DocOf(e)).Notes = (string)v;

    private static readonly Func<object, object?> GetNotes = e => InnerOf(DocOf(e)).Notes;

    private static readonly Action<object, object> SetFile = (e, v) => ((FileLeaf)DocOf(e).GetType().GetProperty("Inner", typeof(FileLeaf))!.GetValue(DocOf(e))!).File = (TableBlobContent)v;

    private static readonly Func<object, object?> GetFile = e => ((FileLeaf)DocOf(e).GetType().GetProperty("Inner", typeof(FileLeaf))!.GetValue(DocOf(e))!).File;

    public static readonly Remedy[] Remedies =
    [
        new("abstract [IgnoreDataMember]", "mark the entity class", typeof(HolderAbstractMarked), SetNotes, GetNotes, Works: true, Named: true),
        new("abstract [IgnoreDataMember]", "declare the property with the runtime type", typeof(HolderDeclaredImpl), SetNotes, GetNotes, Works: true, Named: true),
        new("abstract [IgnoreDataMember]", "remove [IgnoreDataMember]", typeof(HolderAbstractNoIgnore), SetNotes, GetNotes, Works: true, Named: false),
        new("virtual [IgnoreDataMember]", "mark the entity class", typeof(HolderVirtualMarked), SetNotes, GetNotes, Works: true, Named: true),
        new("virtual [IgnoreDataMember]", "declare the property with the runtime type", typeof(HolderVirtualDeclared), SetNotes, GetNotes, Works: true, Named: true),
        new("virtual [IgnoreDataMember]", "remove [IgnoreDataMember]", typeof(HolderVirtualNoIgnore), SetNotes, GetNotes, Works: true, Named: false),
        new("get-only, new with a setter", "mark the entity class", typeof(HolderNewSetterMarked), SetNotes, GetNotes, Works: true, Named: true),
        new("get-only, new with a setter", "declare the property with the runtime type", typeof(HolderNewSetterDeclared), SetNotes, GetNotes, Works: true, Named: true),
        new("get-only, new with a setter", "remove the new re-declaration", typeof(HolderNoNew), SetNotes, GetNotes, Works: false, Named: false),
        new("get-only, new with a setter", "give the base member a setter", typeof(HolderBaseWithSetter), SetNotes, GetNotes, Works: true, Named: false),
        new("base stored as JSON by its constructors, subclass flattened", "mark the entity class", typeof(HolderCtorMarked), SetNotes, GetNotes, Works: true, Named: true),
        new("base stored as JSON by its constructors, subclass flattened", "declare the property with the runtime type", typeof(HolderCtorDeclared), SetNotes, GetNotes, Works: true, Named: true),
        new("base stored as JSON by its constructors, subclass flattened", "give the base class a parameterless constructor", typeof(HolderCtorWithDefault), SetNotes, GetNotes, Works: true, Named: false),
        new("TableBlobContent, abstract [IgnoreDataMember]", "mark the entity class", typeof(FileHolderAbstractMarked), SetFile, GetFile, Works: true, Named: true),
        new("TableBlobContent, abstract [IgnoreDataMember]", "declare the property with the runtime type", typeof(FileHolderDeclared), SetFile, GetFile, Works: true, Named: true),
    ];

    public static TheoryData<string, string, string> RemediesStores()
    {
        var d = new TheoryData<string, string, string>();
        foreach (var r in Remedies)
            foreach (var k in Kinds)
                d.Add(r.Shape, r.Name, k);
        return d;
    }

    // Every candidate followed: the ones that work store the value whole (200,000 characters, or a file) and every
    // read returns it (point read twice, the second from the cache, a new store, a query), and a builder update of it
    // is refused; the one that does not work stores nothing of it (the class it holds does not store the member, as
    // the writer's rule says for every type), and a builder update of it is refused too (rule A).
    [Theory]
    [MemberData(nameof(RemediesStores))]
    public Task EveryRemedy_Followed_StoresTheValueWhole_OrIsOneTheRefusalDoesNotName(string shape, string remedy, string kind)
    {
        var r = Remedies.Single(x => x.Shape == shape && x.Name == remedy);
        return (Task)typeof(MarkedValueBelowAMemberOnlyTheRuntimeClassStoresTests).GetMethod(nameof(FollowedAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(r.Entity).Invoke(null, [r, kind])!;
    }

    private static async Task FollowedAsync<T>(Remedy r, string kind) where T : class, IEntity, new()
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var file = r.Shape.StartsWith("TableBlobContent", StringComparison.Ordinal);
        var bytes = new byte[70_000];
        new Random(5).NextBytes(bytes);
        var id = stores.Id("x");
        var store = stores.Store<T>(Table);
        var entity = new T { Id = id };
        r.Set(entity, file ? TableBlobContent.FromBytes(bytes, "application/octet-stream") : Notes);
        var thrown = await Record.ExceptionAsync(() => store.CreateAsync(entity, Ct));
        Assert.True(thrown is null, $"{r.Shape}, {r.Name}, {kind}: {thrown?.GetType().Name}: {thrown?.Message}");
        var reads = new[] { await store.OneAsync(id, Ct), await store.OneAsync(id, Ct), await stores.Store<T>(Table).OneAsync(id, Ct),
            (await stores.Store<T>(Table).QueryAsync(stores.Pk, Ct)).Single() };
        foreach (var read in reads)
        {
            var back = r.Get(read!);
            if (!r.Works)
                Assert.True(back is null, $"{r.Shape}, {r.Name}, {kind}: a value read back for a member the class does not store");
            else if (file)
            {
                await using var stream = await ((TableBlobContent)back!).OpenReadAsync(Ct);
                Assert.Equal(bytes, await OffloadTestSupport.ReadAllAsync(stream));
            }
            else
                Assert.True((string?)back == Notes, $"{r.Shape}, {r.Name}, {kind}: read back {((string?)back)?.Length ?? -1} of {Notes.Length} characters");
        }
        var column = file ? "Doc_Inner_File" : "Doc_Inner_Notes";
        var update = await Record.ExceptionAsync(() => store.UpdateAsync(id, b => b.Updates[column] = file ? TableBlobContent.FromBytes([1], "application/octet-stream") : "x", Ct));
        Assert.True(update is NotSupportedException, $"{r.Shape}, {r.Name}, {kind}: builder update {update?.GetType().Name ?? "accepted"}");
    }

    // The plain-store refusal of each shape names exactly the remedies above that work and are named (the class-level
    // attribute, and the property declared with the runtime type), and nothing that is in place.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ThePlainStoreRefusal_NamesTheRemediesThatWork(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        foreach (var (entity, runtime, set) in new (IEntity, Type, Action<object>)[]
                 {
                     (new HolderAbstract(), typeof(DocAbstractImpl), e => SetNotes(e, "ten chars!")),
                     (new HolderVirtual(), typeof(DocVirtualOverride), e => SetNotes(e, "ten chars!")),
                     (new HolderNewSetter(), typeof(DocNewSetter), e => SetNotes(e, "ten chars!")),
                     (new HolderCtor(), typeof(DocCtorSub), e => SetNotes(e, "ten chars!")),
                     (new FileHolderAbstract(), typeof(FileDocAbstractImpl), e => SetFile(e, TableBlobContent.FromBytes([1, 2], "application/octet-stream"))),
                 })
        {
            var name = entity.GetType().Name;
            entity.Id = stores.Id(name.ToLowerInvariant());
            set(entity);
            var thrown = await (Task<Exception?>)typeof(MarkedValueBelowAMemberOnlyTheRuntimeClassStoresTests)
                .GetMethod(nameof(CreateRecordedAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(entity.GetType()).Invoke(null, [stores, entity])!;
            Assert.True(thrown is NotSupportedException
                        && thrown.Message.Contains($"Mark {name} with [TableValueSplit], or declare {name}.Doc as {runtime.Name}, so that {name} is large-value aware by its declared types.", StringComparison.Ordinal)
                        && !thrown.Message.Contains("with the type that carries the member", StringComparison.Ordinal),
                $"{name}, {kind}: {thrown?.GetType().Name}: {thrown?.Message}");
        }
    }

    private static async Task<Exception?> CreateRecordedAsync<T>(LargeValueStores stores, T entity) where T : class, IEntity, new()
    {
        var thrown = await Record.ExceptionAsync(() => stores.Store<T>(Table).CreateAsync(entity, Ct));
        Assert.DoesNotContain((await stores.RawRowsAsync<T>(Table)).Select(r => r.RowKey), k => k == entity.Id.Split('|')[1]);
        return thrown;
    }

    // ── Controls: the remedies that work ──────────────────────────────────────────────────────────────────

    // Declaring the property as the implementation makes the type aware: the value is stored and read back whole and
    // its builder update is refused; the class-level attribute does the same.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Control_TheDeclarationAndTheClassLevelAttribute_StoreTheValueWhole_AndRefuseItsBuilderUpdate(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var declared = stores.Store<HolderDeclaredImpl>(Table);
        await declared.CreateAsync(new HolderDeclaredImpl { Id = stores.Id("d"), Doc = new DocAbstractImpl { Inner = { Notes = Notes } } }, Ct);
        var d1 = (await stores.Store<HolderDeclaredImpl>(Table).OneAsync(stores.Id("d"), Ct))!.Doc.Inner.Notes;
        var du = await Record.ExceptionAsync(() => declared.UpdateAsync(stores.Id("d"), b => b.SetProperty(x => x.Doc.Inner.Notes, "x"), Ct));

        var marked = stores.Store<HolderAbstractMarked>(Table);
        await marked.CreateAsync(new HolderAbstractMarked { Id = stores.Id("m"), Doc = new DocAbstractImpl { Inner = { Notes = Notes } } }, Ct);
        var m1 = (await stores.Store<HolderAbstractMarked>(Table).OneAsync(stores.Id("m"), Ct))!.Doc.Inner.Notes;
        var mu = await Record.ExceptionAsync(() => marked.UpdateAsync(stores.Id("m"), b => b.SetProperty(x => x.Doc.Inner.Notes, "x"), Ct));
        Assert.True(d1 == Notes && du is NotSupportedException && m1 == Notes && mu is NotSupportedException,
            $"{kind}: declared {d1?.Length} / update {du?.GetType().Name ?? "accepted"}; class-level {m1?.Length} / update {mu?.GetType().Name ?? "accepted"}");
    }
}
