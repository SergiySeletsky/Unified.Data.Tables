using Unified.Data.Tables.Conformance;
using Unified.Data.Tables.Conformance.Xunit;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Tests.ConformanceKit;

/// <summary>The whole conformance kit against <see cref="InMemoryStorage{T}"/> and <see cref="InMemoryPolymorphicStorage{TBase}"/>.</summary>
public sealed class InMemoryConformanceTests : StorageConformanceTests
{
    protected override IConformanceStoreFactory Factory { get; } = new InMemoryConformanceFactory();
}

/// <summary>
/// The whole conformance kit against <see cref="TableStorage{T}"/> and
/// <see cref="PolymorphicTableStorage{TBase}"/> on a local Azurite (self-skipping like the other Azurite
/// suites).
/// </summary>
public sealed class AzuriteConformanceTests : StorageConformanceTests
{
    protected override IConformanceStoreFactory Factory { get; } = new AzuriteConformanceFactory();
}
