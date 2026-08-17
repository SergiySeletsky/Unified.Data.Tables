namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the read-result shape. The distinction that matters: <c>Item</c> is null ONLY for a
/// deliberate typeless marker row, and <c>Value</c> is the accessor that refuses to pretend a
/// marker row carries an object.
/// </summary>
public class PolymorphicEntryTests
{
    private static PolymorphicEntry<object> Entry(object? item, params (string Name, object Value)[] columns)
    {
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var c in columns)
            dict[c.Name] = c.Value;

        return new PolymorphicEntry<object>(
            new TableKey("p", "r"), item, item is null ? null : "token",
            "W/\"1\"", DateTimeOffset.UnixEpoch, dict);
    }

    [Fact]
    public void Value_TypedRow_ReturnsItem()
    {
        var payload = new object();

        Assert.Same(payload, Entry(payload).Value);
    }

    [Fact]
    public void Value_MarkerRow_Throws()
    {
        var entry = Entry(null, ("_IsCommitted", true));

        var ex = Assert.Throws<InvalidOperationException>(() => entry.Value);
        Assert.Contains("marker row", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Item_MarkerRow_IsNull_AndColumnsSurvive()
    {
        var entry = Entry(null, ("_IsCommitted", true));

        Assert.Null(entry.Item);
        Assert.Null(entry.Discriminator);
        Assert.True(entry.Column<bool>("_IsCommitted"));
    }

    [Fact]
    public void Column_MissingColumn_Throws()
    {
        var entry = Entry(new object());

        Assert.Throws<KeyNotFoundException>(() => entry.Column<bool>("_Nope"));
    }

    [Fact]
    public void TryColumn_MissingColumn_ReturnsFalseAndDefault()
    {
        var entry = Entry(new object());

        Assert.False(entry.TryColumn<bool>("_Nope", out var value));
        Assert.False(value);
    }

    [Fact]
    public void TryColumn_PresentColumn_ReturnsTrueAndValue()
    {
        var entry = Entry(new object(), ("_IsPublished", true));

        Assert.True(entry.TryColumn<bool>("_IsPublished", out var value));
        Assert.True(value);
    }

    [Fact]
    public void Column_WrongType_ThrowsInvalidCast()
    {
        var entry = Entry(new object(), ("_IsPublished", true));

        Assert.Throws<InvalidCastException>(() => entry.Column<int>("_IsPublished"));
    }

    [Fact]
    public void Marker_Factory_ProducesNullItemAndTheGivenColumns()
    {
        var write = PolymorphicWrite<object>.Marker(
            new TableKey("t1", "FlagEntity"),
            new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsCommitted"] = false });

        Assert.Null(write.Item);
        Assert.Equal("FlagEntity", write.Key.RowKey);
        Assert.False((bool)write.SystemColumns!["_IsCommitted"]);
    }

    [Fact]
    public void TypedWrite_Convenience_HasNoSystemColumns()
    {
        var write = new PolymorphicWrite<object>(new TableKey("p", "r"), new object());

        Assert.NotNull(write.Item);
        Assert.Null(write.SystemColumns);
    }
}
