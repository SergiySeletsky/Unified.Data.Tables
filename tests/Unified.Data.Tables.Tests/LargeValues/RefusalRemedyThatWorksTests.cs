using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A refusal of a large value names only remedies that work for the operation and the shape: a builder update
/// is told that a partial update never writes a large value (SERIALIZER-3); a file in a property not declared
/// <see cref="TableBlobContent"/> is told to declare it so, and nothing that leads to another refusal
/// (SERIALIZER-4); a value below a nullable struct reached through a runtime type is told what a nullable
/// struct allows (SERIALIZER-5). The reviewer's twins of these tests, which assert that following a remedy
/// makes the refused operation succeed, ask for more than the verdicts require and are not brought in.
/// </summary>
public class RefusalRemedyThatWorksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    private static UnifiedTableStorageOptions Offload() => new() { BlobOffload = new BlobOffloadOptions() };

    private static TableBlobContent File() => TableBlobContent.FromBytes([1, 2, 3], "application/octet-stream");

    public interface IRemedyShape
    {
    }

    public sealed class RemedyCircle : IRemedyShape
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public sealed class PlainEnvelope : Entity
    {
        public object? Payload { get; set; }

        public IRemedyShape? Shape { get; set; }
    }

    // The remedy for a file in PlainEnvelope.Payload.
    public sealed class DeclaredEnvelope : Entity
    {
        public TableBlobContent? Payload { get; set; }
    }

    // ── SERIALIZER-3: a builder update on the plain path ─────────────────────

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task BuilderUpdate_PlainPathRefusal_SaysAPartialUpdateCannotWriteTheValue(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var plain = stores.Store<PlainEnvelope>();
        var id = stores.Id("r");
        await plain.CreateAsync(new PlainEnvelope { Id = id }, Ct);
        var file = await Assert.ThrowsAsync<NotSupportedException>(() => plain.UpdateAsync(id,
            b => b.SetProperty(x => x.Payload, (object)File()), Ct));
        var notes = await Assert.ThrowsAsync<NotSupportedException>(() => plain.UpdateAsync(id,
            b => b.SetProperty(x => x.Shape, (IRemedyShape)new RemedyCircle { Notes = "ten chars." }), Ct));

        foreach (var refusal in new[] { file, notes })
        {
            Assert.True(refusal.Message.Contains("replace the entity", StringComparison.OrdinalIgnoreCase),
                $"A builder-update refusal names only remedies that leave a builder update refused: {refusal.Message}");
            Assert.Contains("partial update", refusal.Message, StringComparison.Ordinal);
        }
        // The file: only the declaration that lets a read give it back; the member: how to make the type aware.
        Assert.Contains("Declare PlainEnvelope.Payload as TableBlobContent", file.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark PlainEnvelope with [TableValueSplit]", file.Message, StringComparison.Ordinal);
        Assert.Contains("Mark PlainEnvelope with [TableValueSplit]", notes.Message, StringComparison.Ordinal);
        Assert.DoesNotContain((await stores.RawRowsAsync<PlainEnvelope>()).SelectMany(r => r.Keys),
            k => k.StartsWith("Payload", StringComparison.Ordinal) || k.StartsWith("Shape", StringComparison.Ordinal));
    }

    // A whole-entity write keeps the whole-entity remedies (no partial-update wording).
    [Fact]
    public void WholeEntityWrite_PlainPathRefusal_KeepsTheAwarenessRemedies()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            new PlainEnvelope { Id = "p|r", Shape = new RemedyCircle { Notes = "x" } }.ToTableEntity("p", "r"));
        Assert.Contains("Mark PlainEnvelope with [TableValueSplit], or declare PlainEnvelope.Shape with the type that carries the member (RemedyCircle)",
            ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("partial update", ex.Message, StringComparison.Ordinal);
    }

    // ── SERIALIZER-4: a file in an object-typed property ─────────────────────

    public class BaseEnvelope : Entity
    {
        public object? Payload { get; set; }
    }

    public sealed class DerivedEnvelope : BaseEnvelope
    {
    }

    // The remedy the subclass refusal names, followed: the base declares the property as TableBlobContent.
    public class DeclaredBaseEnvelope : Entity
    {
        public TableBlobContent? Payload { get; set; }
    }

    public sealed class DeclaredDerivedEnvelope : DeclaredBaseEnvelope
    {
    }

    [Fact]
    public async Task FileInAnObjectTypedProperty_RefusalNamesOnlyTheRemedyThatWorks()
    {
        var plain = new InMemoryStorage<PlainEnvelope>(null, Offload());
        var plainRefusal = await Assert.ThrowsAsync<NotSupportedException>(() =>
            plain.CreateAsync(new PlainEnvelope { Id = "p|1", Payload = File() }, Ct));
        var baseStore = new InMemoryStorage<BaseEnvelope>(null, Offload());
        var subclassRefusal = await Assert.ThrowsAsync<NotSupportedException>(() =>
            baseStore.CreateAsync(new DerivedEnvelope { Id = "p|1", Payload = File() }, Ct));

        Assert.True(!plainRefusal.Message.Contains("Mark PlainEnvelope with [TableValueSplit]", StringComparison.Ordinal)
                    && subclassRefusal.Message.Contains("as TableBlobContent", StringComparison.Ordinal),
            $"Plain refusal: {plainRefusal.Message}\n\nSubclass refusal: {subclassRefusal.Message}");
        Assert.Contains("Declare PlainEnvelope.Payload as TableBlobContent", plainRefusal.Message, StringComparison.Ordinal);
        Assert.Contains("declare BaseEnvelope.Payload as TableBlobContent", subclassRefusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark DerivedEnvelope with [TableValueSplit]", subclassRefusal.Message, StringComparison.Ordinal);

        // Each named remedy, followed, stores the file and reads it back.
        var declared = new InMemoryStorage<DeclaredEnvelope>(null, Offload());
        await declared.CreateAsync(new DeclaredEnvelope { Id = "p|1", Payload = File() }, Ct);
        Assert.NotNull((await declared.OneAsync("p|1", Ct))!.Payload);
        var declaredBase = new InMemoryStorage<DeclaredBaseEnvelope>(null, Offload());
        await declaredBase.CreateAsync(new DeclaredDerivedEnvelope { Id = "p|1", Payload = File() }, Ct);
        Assert.NotNull((await declaredBase.OneAsync("p|1", Ct))!.Payload);
    }

    // ── SERIALIZER-5: a nullable struct below a runtime subtype ──────────────

    public struct Attachment
    {
        public Attachment()
        {
        }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    public sealed class BoxShape : IRemedyShape
    {
        public Attachment? Extra { get; set; }
    }

    public sealed class Parcel : Entity
    {
        public IRemedyShape Shape { get; set; } = new BoxShape();
    }

    // The declarations the refusal names, followed.
    public sealed class DeclaredBoxShape
    {
        public Attachment Extra { get; set; }
    }

    public sealed class DeclaredParcel : Entity
    {
        public DeclaredBoxShape Shape { get; set; } = new();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task NullableStructBelowARuntimeSubtype_RefusalSaysWhatANullableStructAllows(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refusal = await Assert.ThrowsAsync<NotSupportedException>(() => stores.Store<Parcel>().CreateAsync(new Parcel
        {
            Id = stores.Id("r"),
            Shape = new BoxShape { Extra = new Attachment { Notes = "short" } },
        }, Ct));

        Assert.True(refusal.Message.Contains("nullable", StringComparison.OrdinalIgnoreCase)
                    && !refusal.Message.Contains("Mark Parcel with [TableValueSplit], or", StringComparison.Ordinal),
            $"The refusal's first remedy leads to another refusal (large value) or to a value that reads back null (R6): {refusal.Message}");
        Assert.Contains("cannot be stored as a large value", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Declare BoxShape.Extra as Attachment, not nullable, and declare Parcel.Shape with the type that has it (BoxShape)",
            refusal.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<Parcel>());

        // The declarations it names, followed: a large value is stored and read back.
        var notes = new string('n', 40_000);
        await stores.Store<DeclaredParcel>().CreateAsync(new DeclaredParcel
        {
            Id = stores.Id("r"),
            Shape = new DeclaredBoxShape { Extra = new Attachment { Notes = notes } },
        }, Ct);
        Assert.Equal(notes, (await stores.Store<DeclaredParcel>().OneAsync(stores.Id("r"), Ct))!.Shape.Extra.Notes);
    }
}
