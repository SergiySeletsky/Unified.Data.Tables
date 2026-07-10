namespace Unified.Data.Tables.Tests;

/// <summary>
/// The shared composite-id conventions in <see cref="EntityId"/> — the single definition every
/// <see cref="IStorage{T}"/> implementation (and app-side id-building code) must agree on.
/// </summary>
public class EntityIdTests
{
    [Theory]
    [InlineData("  Part | Row1 ", "part-|-row1")]
    [InlineData("UPPER", "upper")]
    [InlineData("a b c", "a-b-c")]
    [InlineData("already-normal", "already-normal")]
    public void Normalize_TrimsHyphenatesAndLowercases(string input, string expected)
    {
        Assert.Equal(expected, EntityId.Normalize(input));
    }

    [Fact]
    public void Normalize_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EntityId.Normalize(null!));
    }

    [Theory]
    [InlineData("a|b", "a", "b")]
    [InlineData("a|b|c", "a", "b|c")]          // split on FIRST separator only
    [InlineData("solo", "solo", "solo")]        // no separator → id is both keys
    [InlineData("|row", "", "row")]
    public void Split_SplitsOnFirstSeparator(string id, string partition, string row)
    {
        Assert.Equal((partition, row), EntityId.Split(id));
    }

    [Fact]
    public void Split_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EntityId.Split(null!));
    }

    [Fact]
    public void Combine_JoinsWithSeparator()
    {
        Assert.Equal("vision|exec|agent", EntityId.Combine("vision", "exec|agent"));
    }

    [Fact]
    public void Combine_RoundTripsThroughSplit()
    {
        var (partition, row) = EntityId.Split(EntityId.Combine("pk", "rk|deep"));
        Assert.Equal("pk", partition);
        Assert.Equal("rk|deep", row);
    }
}
