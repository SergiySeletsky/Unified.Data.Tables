namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// <see cref="TableValueSplitAttribute"/> on a member declared on an interface is a misused attribute (ruling
/// R11; SERIALIZER-2): .NET does not pass an interface member's attributes to the implementing class, so the
/// writer could never honour it. It makes no type large-value aware and lists no column, and the serializer
/// refuses, on every use, a type that implements such an interface before anything is stored, whatever the
/// value's size and the <see cref="OversizedCellPolicy"/>. An opted-in value is never trimmed.
/// </summary>
[Collection("OversizedCellPolicy")]
public class InterfaceMemberSplitAttributeMisuseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public static TheoryData<string, OversizedCellPolicy> StoresAndPolicies() => new()
    {
        { LargeValueStores.Memory, OversizedCellPolicy.Throw },
        { LargeValueStores.Memory, OversizedCellPolicy.TrimWithMarker },
        { LargeValueStores.Memory, OversizedCellPolicy.TrimSilently },
        { LargeValueStores.Azurite, OversizedCellPolicy.Throw },
        { LargeValueStores.Azurite, OversizedCellPolicy.TrimWithMarker },
        { LargeValueStores.Azurite, OversizedCellPolicy.TrimSilently },
    };

    private static string Ascii(int length, int seed)
    {
        var rng = new Random(seed);
        return string.Create(length, rng, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)r.Next(0x21, 0x7F);
        });
    }

    // ── the misuse ────────────────────────────────────────────────────────────

    public interface IMarkedNote
    {
        [TableValueSplit(SplitEncoding.Text)]
        string? Notes { get; set; }
    }

    public sealed class MarkedNoteImpl : IMarkedNote
    {
        public string? Notes { get; set; }
    }

    public sealed class MarkedNoteHolder : Entity
    {
        public IMarkedNote Doc { get; set; } = new MarkedNoteImpl();

        public string? Title { get; set; }
    }

    // A plain entity whose runtime value implements such an interface.
    public sealed class ObjectHolder : Entity
    {
        public object? Payload { get; set; }
    }

    // The same columns written by a type that opts into nothing (to give a read a row to fill).
    public sealed class OrdinaryNote
    {
        public string? Notes { get; set; }
    }

    public sealed class OrdinaryNoteHolder : Entity
    {
        public OrdinaryNote Doc { get; set; } = new();

        public string? Title { get; set; }
    }

    private static void AssertTheMisuse(Exception? thrown)
    {
        Assert.NotNull(thrown);
        var misuse = thrown is InvalidOperationException { Message: var m } && m.Contains("[TableValueSplit]", StringComparison.Ordinal)
            ? thrown
            : thrown.InnerException;
        var refused = Assert.IsType<InvalidOperationException>(misuse);
        Assert.Contains("IMarkedNote.Notes", refused.Message, StringComparison.Ordinal);
        Assert.Contains("interface", refused.Message, StringComparison.Ordinal);
        Assert.Contains("MarkedNoteImpl.Notes", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InterfaceMemberAttribute_DoesNotMakeTheTypeAware_AndListsNoColumn()
    {
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(MarkedNoteHolder)));
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(IMarkedNote)));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(typeof(MarkedNoteHolder)));
    }

    [Theory]
    [MemberData(nameof(StoresAndPolicies))]
    public async Task InterfaceMemberAttribute_IsRefusedAsAMisuse_BeforeAnythingIsStored_AtAnySize(string kind, OversizedCellPolicy policy)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<MarkedNoteHolder>();
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = policy;
        try
        {
            foreach (var length in new[] { 10, 200_000 })
            {
                // Reported on every use, not only the first.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    AssertTheMisuse(await Record.ExceptionAsync(() => store.CreateAsync(
                        new MarkedNoteHolder { Id = stores.Id("r"), Title = "t", Doc = new MarkedNoteImpl { Notes = Ascii(length, 7) } }, Ct)));
                    AssertTheMisuse(await Record.ExceptionAsync(() => store.UpsertAsync(
                        new MarkedNoteHolder { Id = stores.Id("r"), Title = "t", Doc = new MarkedNoteImpl { Notes = Ascii(length, 8) } }, Ct)));
                }
            }
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }

        Assert.Empty(await stores.RawRowsAsync<MarkedNoteHolder>());
        Assert.Empty(await stores.BlobNamesAsync<MarkedNoteHolder>());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task InterfaceMemberAttribute_UnderATrimmingPolicy_IsNeverStoredTruncated(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var notes = Ascii(200_000, 8);
        var store = stores.Store<MarkedNoteHolder>();
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimWithMarker;
        try
        {
            var thrown = await Record.ExceptionAsync(() => store.CreateAsync(
                new MarkedNoteHolder { Id = stores.Id("r"), Title = "t", Doc = new MarkedNoteImpl { Notes = notes } }, Ct));
            if (thrown is null)
            {
                var read = (await store.OneAsync(stores.Id("r"), Ct))!.Doc.Notes;
                var cells = string.Join(", ", (await stores.RawRowsAsync<MarkedNoteHolder>()).SelectMany(r => r.Keys).Where(k => k.StartsWith("Doc", StringComparison.Ordinal)));
                Assert.True(read == notes,
                    $"Accepted and reported as written, but a {notes.Length:N0}-character value of a [TableValueSplit] member reads back as {read?.Length:N0} characters (cells: {cells}).");
            }
            else
            {
                AssertTheMisuse(thrown);
                Assert.Empty(await stores.RawRowsAsync<MarkedNoteHolder>());
            }
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task PlainEntityWhoseRuntimeValueImplementsSuchAnInterface_IsRefused_NotTrimmed(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<ObjectHolder>();
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimWithMarker;
        try
        {
            AssertTheMisuse(await Record.ExceptionAsync(() => store.CreateAsync(
                new ObjectHolder { Id = stores.Id("r"), Payload = new MarkedNoteImpl { Notes = Ascii(200_000, 9) } }, Ct)));
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }

        Assert.Empty(await stores.RawRowsAsync<ObjectHolder>());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task InterfaceMemberAttribute_IsReported_ByAReadThatFillsAnImplementingType(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        const string table = "InterfaceMemberSplitRead";
        await stores.Store<OrdinaryNoteHolder>(table).CreateAsync(
            new OrdinaryNoteHolder { Id = stores.Id("r"), Title = "t", Doc = new OrdinaryNote { Notes = "short" } }, Ct);
        await stores.ShareTableAsync<OrdinaryNoteHolder, MarkedNoteHolder>(table);

        var thrown = await Record.ExceptionAsync(() => stores.Store<MarkedNoteHolder>(table).OneAsync(stores.Id("r"), Ct));
        Assert.NotNull(thrown);
        var chain = new List<Exception>();
        for (var e = thrown; e is not null; e = e.InnerException)
            chain.Add(e);
        AssertTheMisuse(chain.Find(e => e is InvalidOperationException && e.Message.Contains("[TableValueSplit]", StringComparison.Ordinal)));
    }

    // ── the remedy, followed: the attribute on the property of the implementing class ─────────────────

    public interface IPlainNote
    {
        string? Notes { get; set; }
    }

    public sealed class SplitNoteImpl : IPlainNote
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    // The implementation is held in an interface-typed property: the entity opts in at class level (R3).
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class SplitNoteHolder : Entity
    {
        public IPlainNote Doc { get; set; } = new SplitNoteImpl();

        public int Rank { get; set; }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheRemedyFollowed_AttributeOnTheImplementingClass_RoundTripsAtEverySize(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<SplitNoteHolder>();
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimWithMarker;
        try
        {
            foreach (var length in new[] { 10, 40_000, 700_000 })
            {
                var notes = Ascii(length, length);
                await store.UpsertAsync(new SplitNoteHolder { Id = stores.Id("r"), Rank = 1, Doc = new SplitNoteImpl { Notes = notes } }, Ct);
                Assert.Equal(notes, (await store.OneAsync(stores.Id("r"), Ct))!.Doc.Notes);
            }
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }

    // ── what the code does for an abstract class member and a base class member (pinned, unchanged) ────

    public abstract class AbstractNote
    {
        [TableValueSplit(SplitEncoding.Text)]
        public abstract string? Notes { get; set; }
    }

    public sealed class AbstractNoteImpl : AbstractNote
    {
        public override string? Notes { get; set; }
    }

    public class BaseNote
    {
        [TableValueSplit(SplitEncoding.Text)]
        public virtual string? Notes { get; set; }
    }

    public sealed class DerivedNote : BaseNote
    {
        public override string? Notes { get; set; }
    }

    public sealed class AbstractAndBaseNoteHolder : Entity
    {
        public AbstractNote Abstract { get; set; } = new AbstractNoteImpl();

        public BaseNote Base { get; set; } = new DerivedNote();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AbstractAndBaseClassMemberAttribute_IsHonoured_AndNeverTrimmed(string kind)
    {
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(AbstractAndBaseNoteHolder)));
        Assert.Equal(["Abstract_Notes", "Base_Notes"], TypeMetadataCache.LargeValueColumns(typeof(AbstractAndBaseNoteHolder)));

        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var store = stores.Store<AbstractAndBaseNoteHolder>();
        var before = TableEntitySerializer.OversizedCellPolicy;
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimWithMarker;
        try
        {
            var a = Ascii(200_000, 1);
            var b = Ascii(200_000, 2);
            await store.CreateAsync(new AbstractAndBaseNoteHolder
            {
                Id = stores.Id("r"),
                Abstract = new AbstractNoteImpl { Notes = a },
                Base = new DerivedNote { Notes = b },
            }, Ct);
            var read = (await store.OneAsync(stores.Id("r"), Ct))!;
            Assert.Equal(a, read.Abstract.Notes);
            Assert.Equal(b, read.Base.Notes);
        }
        finally
        {
            TableEntitySerializer.OversizedCellPolicy = before;
        }
    }
}
