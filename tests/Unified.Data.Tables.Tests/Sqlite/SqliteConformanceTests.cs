using Unified.Data.Tables.Conformance;
using Unified.Data.Tables.Conformance.Xunit;
using Unified.Data.Tables.Sqlite;

namespace Unified.Data.Tables.Tests.Sqlite;

/// <summary>
/// The whole conformance kit against <see cref="SqliteStorage{T}"/> and
/// <see cref="SqlitePolymorphicStorage{TBase}"/> over a database file, with the provider's default read
/// cache (the one the Azure provider has).
/// </summary>
public sealed class SqliteFileConformanceTests : StorageConformanceTests
{
    protected override IConformanceStoreFactory Factory { get; } = new SqliteConformanceFactory(file: true);
}

/// <summary>The whole conformance kit against the SQLite provider over a <c>:memory:</c> database.</summary>
public sealed class SqliteMemoryConformanceTests : StorageConformanceTests
{
    protected override IConformanceStoreFactory Factory { get; } = new SqliteConformanceFactory(file: false);
}

/// <summary>
/// The whole conformance kit against the SQLite provider over a database file with the read cache
/// switched off: the contract holds with the cache and without it.
/// </summary>
public sealed class SqliteUncachedConformanceTests : StorageConformanceTests
{
    protected override IConformanceStoreFactory Factory { get; } = new SqliteConformanceFactory(file: true, CachePolicy.Disabled);
}

/// <summary>
/// The acceptance rule of the provider: on a file and on <c>:memory:</c> every scenario of every tier
/// passes. None is skipped as "not supported", and none is excused by a leniency.
/// </summary>
public sealed class SqliteConformanceOutcomeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryScenarioOfEveryTier_Passes_NoneIsNotSupportedOrLenient(bool file)
    {
        var factory = new SqliteConformanceFactory(file);

        var results = await ConformanceSuite.RunAllAsync(factory, TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.Empty(results.Where(r => r.Outcome != ConformanceOutcome.Passed).Select(r => r.ToString()));
        Assert.Empty(factory.Leniencies);
    }
}
