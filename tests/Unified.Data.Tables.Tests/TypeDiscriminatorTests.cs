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
}
