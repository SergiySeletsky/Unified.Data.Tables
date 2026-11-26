using System.Linq.Expressions;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>An entity with a date of each kind that may be left unset.</summary>
public sealed class DatedRow : Entity
{
    public DateTime When { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>
/// An unset date is stored as 1601-01-01 (the service holds no earlier date) and read back as the
/// default. A filter that compares a date with the default must find the unset rows on Azure as the
/// local providers find them (they evaluate the predicate on the value read back): the translator
/// compares a default constant as 1601-01-01, and refuses a constant it cannot compare faithfully.
/// </summary>
public sealed class DefaultDateFilterTests : IAsyncLifetime
{
    private static readonly string ConnectionString = AzuriteConnection.ConnectionString;

    private static readonly Lazy<Task<bool>> Probe =
        new(() => AzuriteProbe.ProbeAsync(ConnectionString, blobs: false, AzuriteProbe.Required));

    private static readonly DateTime Set = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string table = "DatedRows" + Guid.NewGuid().ToString("N")[..16];
    private TableServiceClient service = default!;
    private bool available;

    public async ValueTask InitializeAsync()
    {
        available = await Probe.Value;
        if (available)
            service = new TableServiceClient(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (available)
            await service.DeleteTableAsync(table);
    }

    [Fact]
    public void ADefaultDate_IsComparedAsTheCellAnUnsetDateIsStoredAs()
    {
        Assert.Equal("When eq datetime'1601-01-01T00:00:00Z'", TableFilterTranslator.Translate<DatedRow>(x => x.When == default));
        Assert.Equal("At eq datetime'1601-01-01T00:00:00Z'", TableFilterTranslator.Translate<DatedRow>(x => x.At == default));
        Assert.Equal("When gt datetime'1601-01-01T00:00:00Z'", TableFilterTranslator.Translate<DatedRow>(x => x.When > DateTime.MinValue));
        Assert.Equal("At ne datetime'1601-01-01T00:00:00Z'", TableFilterTranslator.Translate<DatedRow>(x => x.At != DateTimeOffset.MinValue));
    }

    [Theory]
    [InlineData(1601, 1, 1)]
    [InlineData(1500, 6, 15)]
    [InlineData(1, 1, 2)]
    public void ADateAfterTheDefault_UpTo1601_IsRefused(int year, int month, int day)
    {
        var date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        var offset = new DateTimeOffset(date);

        Assert.Throws<NotSupportedException>(() => TableFilterTranslator.Translate<DatedRow>(x => x.When == date));
        Assert.Throws<NotSupportedException>(() => TableFilterTranslator.Translate<DatedRow>(x => x.At < offset));
    }

    [Fact]
    public void ADateAfter1601_IsComparedAsWritten()
    {
        var after = new DateTime(1601, 1, 1, 0, 0, 1, DateTimeKind.Utc);

        Assert.Equal("When ge datetime'1601-01-01T00:00:01Z'", TableFilterTranslator.Translate<DatedRow>(x => x.When >= after));
    }

    public static TheoryData<string> Predicates => new() { "when == default", "at == default", "when > default", "at >= default", "when < 2000" };

    private static Expression<Func<DatedRow, bool>> Predicate(string name) => name switch
    {
        "when == default" => x => x.When == default,
        "at == default" => x => x.At == default,
        "when > default" => x => x.When > default(DateTime),
        "at >= default" => x => x.At >= default(DateTimeOffset),
        _ => x => x.When < new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static async Task<string[]> MatchingAsync(IStorage<DatedRow> store, string predicate)
    {
        await store.CreateAsync(new DatedRow { Id = "p|unset" }, Ct);
        await store.CreateAsync(new DatedRow { Id = "p|set", When = Set, At = Set }, Ct);
        return (await store.QueryAsync(Predicate(predicate), "p", ct: Ct)).Select(r => r.Id).Order(StringComparer.Ordinal).ToArray();
    }

    [Theory]
    [MemberData(nameof(Predicates))]
    public async Task AComparisonWithTheDefault_FindsTheSameRows_LocallyAndOnAzure(string predicate)
    {
        Assert.SkipUnless(available, "Azurite is not running");
        var azure = new TableStorage<DatedRow>(service, new MemoryCache(new MemoryCacheOptions()), NullLogger<TableStorage<DatedRow>>.Instance,
            authorizer: null, options: new UnifiedTableStorageOptions { TableNameResolver = _ => table });

        var local = await MatchingAsync(new InMemoryStorage<DatedRow>(), predicate);
        var remote = await MatchingAsync(azure, predicate);

        Assert.Equal(local, remote);
        Assert.Equal(
            predicate switch { "when > default" => ["p|set"], "at >= default" => ["p|set", "p|unset"], _ => new[] { "p|unset" } },
            remote);
    }
}
