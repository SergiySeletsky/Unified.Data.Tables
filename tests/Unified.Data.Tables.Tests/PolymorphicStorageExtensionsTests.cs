using NSubstitute;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the convenience layer. These exist so the interface stays thin (both implementations must
/// mirror it exactly), and so a caller never hand-builds a <see cref="PolymorphicWrite{TBase}"/>
/// for the two common cases.
/// </summary>
public class PolymorphicStorageExtensionsTests
{
    // NSubstitute's Castle proxy is emitted into a separate dynamic assembly, which the CLR never
    // grants access to a `private` nested type regardless of InternalsVisibleTo (that only widens
    // `internal`). Substitute.For<IPolymorphicStorage<Msg>> below needs Msg visible to that
    // assembly, so these are `public`, not `private`.
    public abstract class Msg;

    public sealed class Created : Msg;

    public sealed class Archived : Msg;

    [Fact]
    public async Task InsertAsync_KeyAndItem_ForwardsATypedWrite()
    {
        var store = Substitute.For<IPolymorphicStorage<Msg>>();
        var item = new Created();

        await store.InsertAsync(new TableKey("p", "r"), item, TestContext.Current.CancellationToken);

        await store.Received(1).InsertAsync(
            Arg.Is<PolymorphicWrite<Msg>>(w =>
                w.Key == new TableKey("p", "r") && ReferenceEquals(w.Item, item) && w.SystemColumns == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InsertMarkerAsync_ForwardsATypelessWrite()
    {
        var store = Substitute.For<IPolymorphicStorage<Msg>>();
        var columns = new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsCommitted"] = false };

        await store.InsertMarkerAsync(
            new TableKey("t1", "FlagEntity"), columns, TestContext.Current.CancellationToken);

        await store.Received(1).InsertAsync(
            Arg.Is<PolymorphicWrite<Msg>>(w => w.Item == null && w.SystemColumns != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ItemsOfType_FiltersByRuntimeType_AndSkipsMarkers()
    {
        var entries = new[]
        {
            Entry(new Created()),
            Entry(new Archived()),
            Entry(new Created()),
            Entry(null),
        };

        var created = entries.ItemsOfType<Msg, Created>().ToList();

        Assert.Equal(2, created.Count);
        Assert.All(created, c => Assert.IsType<Created>(c));
    }

    private static PolymorphicEntry<Msg> Entry(Msg? item) =>
        new(new TableKey("p", Guid.NewGuid().ToString()), item, item is null ? null : "t",
            null, null, new Dictionary<string, object>(StringComparer.Ordinal));
}
