using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins type resolution — the seam that decides what a stored type name is allowed to become.
/// </summary>
public class TypeDiscriminatorTests
{
    [Fact]
    public void AssemblyQualified_RoundTrips()
    {
        var sut = AssemblyQualifiedTypeDiscriminator.Instance;

        var token = sut.ToDiscriminator(typeof(TestCreatedEvent));

        Assert.Equal(typeof(TestCreatedEvent), sut.Resolve(token, typeof(TestMessage)));
    }

    [Fact]
    public void AssemblyQualified_Token_IsTheAssemblyQualifiedName()
    {
        Assert.Equal(
            typeof(TestCreatedEvent).AssemblyQualifiedName,
            AssemblyQualifiedTypeDiscriminator.Instance.ToDiscriminator(typeof(TestCreatedEvent)));
    }

    [Fact]
    public void AssemblyQualified_UnknownToken_ThrowsTypeLoadWithGuidance()
    {
        var ex = Assert.Throws<TypeLoadException>(() =>
            AssemblyQualifiedTypeDiscriminator.Instance.Resolve("No.Such.Type, No.Such.Asm", typeof(TestMessage)));

        Assert.Contains("TypeDiscriminatorMap", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssemblyQualified_ResolveIsCached_SameInstanceReturned()
    {
        var sut = AssemblyQualifiedTypeDiscriminator.Instance;
        var token = sut.ToDiscriminator(typeof(TestArchivedEvent));

        Assert.Same(sut.Resolve(token, typeof(TestMessage)), sut.Resolve(token, typeof(TestMessage)));
    }

    [Fact]
    public void Map_ShortToken_RoundTripsAndOmitsAssemblyIdentity()
    {
        var sut = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created");

        Assert.Equal("created", sut.ToDiscriminator(typeof(TestCreatedEvent)));
        Assert.Equal(typeof(TestCreatedEvent), sut.Resolve("created", typeof(TestMessage)));
        Assert.DoesNotContain(",", sut.ToDiscriminator(typeof(TestCreatedEvent)), StringComparison.Ordinal);
    }

    [Fact]
    public void Map_UnregisteredType_ThrowsOnWriteWithGuidance()
    {
        var sut = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created");

        var ex = Assert.Throws<InvalidOperationException>(() => sut.ToDiscriminator(typeof(TestArchivedEvent)));
        Assert.Contains(nameof(TypeDiscriminatorMap.Map), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_UnknownToken_ThrowsTypeLoad()
    {
        var sut = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created");

        Assert.Throws<TypeLoadException>(() => sut.Resolve("nope", typeof(TestMessage)));
    }

    [Fact]
    public void Map_DuplicateToken_ThrowsAtRegistration()
    {
        var sut = new TypeDiscriminatorMap().Map<TestCreatedEvent>("dup");

        Assert.Throws<ArgumentException>(() => sut.Map<TestArchivedEvent>("dup"));
    }

    [Fact]
    public void Map_SameTypeTwice_ThrowsAtRegistration()
    {
        var sut = new TypeDiscriminatorMap().Map<TestCreatedEvent>("a");

        Assert.Throws<ArgumentException>(() => sut.Map<TestCreatedEvent>("b"));
    }

    [Fact]
    public void MapAssignableTo_BulkRegisters_TheHierarchy()
    {
        var sut = new TypeDiscriminatorMap()
            .MapAssignableTo<TestMessage>(typeof(TestCreatedEvent).Assembly);

        Assert.Equal(nameof(TestCreatedEvent), sut.ToDiscriminator(typeof(TestCreatedEvent)));
        Assert.Equal(typeof(TestArchivedEvent), sut.Resolve(nameof(TestArchivedEvent), typeof(TestMessage)));
    }

    [Fact]
    public void AssemblyQualifiedFallback_DisabledByDefault_ThenEnabled()
    {
        var token = typeof(TestCreatedEvent).AssemblyQualifiedName!;
        var strict = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created");

        Assert.Throws<TypeLoadException>(() => strict.Resolve(token, typeof(TestMessage)));

        var lenient = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created").WithAssemblyQualifiedFallback();

        Assert.Equal(typeof(TestCreatedEvent), lenient.Resolve(token, typeof(TestMessage)));
        Assert.Equal("created", lenient.ToDiscriminator(typeof(TestCreatedEvent)));
    }
}
