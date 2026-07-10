using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Equality, transience and default-timestamp behaviour of the <see cref="Entity"/> base class.
/// </summary>
public class EntityTests
{
    [Fact]
    public void Equals_SameReference_ReturnsTrue()
    {
        var entity = new TestEntity { Id = "All|test" };
        Assert.True(entity.Equals(entity));
    }

    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        var entity = new TestEntity { Id = "All|test" };
        Assert.False(entity.Equals((Entity?)null));
    }

    [Fact]
    public void Equals_NullObject_ReturnsFalse()
    {
        var entity = new TestEntity { Id = "All|test" };
        Assert.False(entity.Equals((object?)null));
    }

    [Fact]
    public void Equals_SameId_SameType_ReturnsTrue()
    {
        var a = new TestEntity { Id = "All|test" };
        var b = new TestEntity { Id = "All|test" };
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void Equals_DifferentId_ReturnsFalse()
    {
        var a = new TestEntity { Id = "All|test1" };
        var b = new TestEntity { Id = "All|test2" };
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Equals_TransientEntity_EmptyId_ReturnsFalse()
    {
        var a = new TestEntity { Id = "" };
        var b = new TestEntity { Id = "" };
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Equals_TransientEntity_WhitespaceId_ReturnsFalse()
    {
        var a = new TestEntity { Id = "  " };
        var b = new TestEntity { Id = "  " };
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Equals_OneTransient_ReturnsFalse()
    {
        var a = new TestEntity { Id = "All|test" };
        var b = new TestEntity { Id = "" };
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Equals_DifferentNonAssignableTypes_SameId_ReturnsFalse()
    {
        var a = new TestEntity { Id = "All|test" };
        var b = new OtherEntity { Id = "All|test" };
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Equals_Object_SameId_ReturnsTrue()
    {
        var a = new TestEntity { Id = "All|test" };
        object b = new TestEntity { Id = "All|test" };
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void Equals_Object_NonEntity_ReturnsFalse()
    {
        var a = new TestEntity { Id = "All|test" };
        Assert.False(a.Equals("not an entity"));
    }

    [Fact]
    public void GetHashCode_SameId_ReturnsSameHash()
    {
        var a = new TestEntity { Id = "All|test" };
        var b = new TestEntity { Id = "All|test" };
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentId_ReturnsDifferentHash()
    {
        var a = new TestEntity { Id = "All|test1" };
        var b = new TestEntity { Id = "All|test2" };
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void IEquatable_EqualsMethod_Works()
    {
        IEquatable<Entity> a = new TestEntity { Id = "All|test" };
        var b = new TestEntity { Id = "All|test" };
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void CreatedAt_DefaultsToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var entity = new TestEntity();
        var after = DateTimeOffset.UtcNow;
        Assert.InRange(entity.CreatedAt, before, after);
    }

    [Fact]
    public void UpdatedAt_DefaultsToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var entity = new TestEntity();
        var after = DateTimeOffset.UtcNow;
        Assert.InRange(entity.UpdatedAt, before, after);
    }

    [Fact]
    public void ETag_DefaultsToNull()
    {
        Assert.Null(new TestEntity().ETag);
    }

    [Fact]
    public void Timestamp_DefaultsToNull()
    {
        Assert.Null(new TestEntity().Timestamp);
    }

    [Fact]
    public void Entity_ImplementsIEntity()
    {
        IEntity entity = new TestEntity { Id = "All|test" };
        Assert.Equal("All|test", entity.Id);
    }
}
