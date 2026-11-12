namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A large-value member below a nullable struct reached through runtime owners at any depth (SERIALIZER-5 of
/// the review of wave 4): the refusal of the plain store names only declarations that, followed together, let
/// a whole-entity write store the value and a read return it: the struct property declared non-nullable, and
/// every property on the path whose declared type differs from the runtime type of its value, declared with
/// that runtime type. It never names a declaration that is already in place.
/// </summary>
public class NullableStructBelowNestedRuntimeOwnersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public interface INestedShape
    {
    }

    public interface INestedInner
    {
    }

    public struct NestedAttachment
    {
        public NestedAttachment()
        {
        }

        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    // ── Two levels: the struct's owner is not itself the runtime-typed object ─────────────────────────

    public sealed class NestedInner
    {
        public NestedAttachment? Extra { get; set; }
    }

    public sealed class NestedBox : INestedShape
    {
        public NestedInner Inner { get; set; } = new();
    }

    public sealed class NestedParcel : Entity
    {
        public INestedShape Shape { get; set; } = new NestedBox();
    }

    // The declarations the refusal names, followed: Shape declared as the box type, Extra not nullable.
    public sealed class NestedInnerDeclared
    {
        public NestedAttachment Extra { get; set; }
    }

    public sealed class NestedBoxDeclared
    {
        public NestedInnerDeclared Inner { get; set; } = new();
    }

    public sealed class NestedParcelDeclared : Entity
    {
        public NestedBoxDeclared Shape { get; set; } = new();
    }

    // SERIALIZER-5: the reviewer's NullableStructTwoLevelsBelowARuntimeOwner_RemedyNamedWorks, rewritten (as the
    // README of the reproduction says) to follow the message the fix produces, with the verifier's
    // Serializer5_DeclaringTheOutermostRuntimeHolder_StoresAndReadsBack as the followed declarations.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TwoLevelsBelowARuntimeOwner_TheRefusalNamesTheRuntimeHolder_AndTheNamedDeclarationsWork(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refusal = await Assert.ThrowsAsync<NotSupportedException>(() => stores.Store<NestedParcel>().CreateAsync(new NestedParcel
        {
            Id = stores.Id("r"),
            Shape = new NestedBox { Inner = new NestedInner { Extra = new NestedAttachment { Notes = "x" } } },
        }, Ct));

        Assert.Contains("Declare NestedInner.Extra as NestedAttachment, not nullable, and declare NestedParcel.Shape with the type " +
                        "that has it (NestedBox), so that", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("declare NestedParcel.Shape.Inner", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<NestedParcel>());

        var notes = new string('n', 40_000);
        await stores.Store<NestedParcelDeclared>().CreateAsync(new NestedParcelDeclared
        {
            Id = stores.Id("r"),
            Shape = new NestedBoxDeclared { Inner = new NestedInnerDeclared { Extra = new NestedAttachment { Notes = notes } } },
        }, Ct);
        Assert.Equal(notes, (await stores.Store<NestedParcelDeclared>().OneAsync(stores.Id("r"), Ct))!.Shape.Inner.Extra.Notes);
    }

    // ── Two runtime holders on the path ────────────────────────────────────────────────────────────

    public sealed class RuntimeInner : INestedInner
    {
        public NestedAttachment? Extra { get; set; }
    }

    public sealed class RuntimeBox : INestedShape
    {
        public INestedInner Inner { get; set; } = new RuntimeInner();
    }

    public sealed class TwoHolderParcel : Entity
    {
        public INestedShape Shape { get; set; } = new RuntimeBox();
    }

    public sealed class RuntimeInnerDeclared
    {
        public NestedAttachment Extra { get; set; }
    }

    public sealed class RuntimeBoxDeclared
    {
        public RuntimeInnerDeclared Inner { get; set; } = new();
    }

    public sealed class TwoHolderParcelDeclared : Entity
    {
        public RuntimeBoxDeclared Shape { get; set; } = new();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TwoRuntimeHoldersOnThePath_TheRefusalNamesBoth_AndTheNamedDeclarationsWork(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
        var refusal = await Assert.ThrowsAsync<NotSupportedException>(() => stores.Store<TwoHolderParcel>().CreateAsync(new TwoHolderParcel
        {
            Id = stores.Id("r"),
            Shape = new RuntimeBox { Inner = new RuntimeInner { Extra = new NestedAttachment { Notes = "x" } } },
        }, Ct));

        Assert.Contains("Declare RuntimeInner.Extra as NestedAttachment, not nullable, and declare TwoHolderParcel.Shape with the " +
                        "type that has it (RuntimeBox), and declare TwoHolderParcel.Shape.Inner with the type that has it (RuntimeInner), so that",
            refusal.Message, StringComparison.Ordinal);
        Assert.Empty(await stores.RawRowsAsync<TwoHolderParcel>());

        var notes = new string('m', 40_000);
        await stores.Store<TwoHolderParcelDeclared>().CreateAsync(new TwoHolderParcelDeclared
        {
            Id = stores.Id("r"),
            Shape = new RuntimeBoxDeclared { Inner = new RuntimeInnerDeclared { Extra = new NestedAttachment { Notes = notes } } },
        }, Ct);
        Assert.Equal(notes, (await stores.Store<TwoHolderParcelDeclared>().OneAsync(stores.Id("r"), Ct))!.Shape.Inner.Extra.Notes);
    }
}
