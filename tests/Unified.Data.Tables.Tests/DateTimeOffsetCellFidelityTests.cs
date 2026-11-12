using System.Globalization;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The service stores a <see cref="DateTimeOffset"/> cell as an instant and returns it in UTC (offset zero),
/// whatever offset it was written with. <see cref="InMemoryStorage{T}"/> returns it the same way, and so does
/// a read that <see cref="TableStorage{T}"/>'s cache answers after a create, a replace or a builder update:
/// before, both returned the offset the value was written with (the instant was equal, its
/// <c>Offset</c>, <c>ToString()</c> and JSON were not).
/// </summary>
public sealed class DateTimeOffsetCellFidelityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Plus3 = new(2021, 5, 6, 7, 8, 9, TimeSpan.FromHours(3));

    private static readonly DateTimeOffset Minus5 = new(2022, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5));

    public sealed class DtoEntity : Entity
    {
        public DateTimeOffset At { get; set; }

        public DateTimeOffset? Maybe { get; set; }
    }

    private static string Show(DtoEntity? e) => e is null
        ? "null"
        : e.At.ToString("o", CultureInfo.InvariantCulture) + " " + e.Maybe?.ToString("o", CultureInfo.InvariantCulture);

    [Fact]
    public async Task OffsetCells_ReadBackInUtc_OnBothStores_CacheHitsIncluded()
    {
        var transcript = await FidelityScript.BothAsync(async s =>
        {
            var store = s.CachedStore<DtoEntity>();
            await s.StepAsync("create", () => store.CreateAsync(new DtoEntity { Id = s.Id("a"), At = Plus3, Maybe = Minus5 }, Ct));
            await s.StepAsync("one after create", async () => (object?)Show(await store.OneAsync(s.Id("a"), Ct)));
            await s.StepAsync("fresh one", async () => (object?)Show(await s.Store<DtoEntity>().OneAsync(s.Id("a"), Ct)));
            await s.StepAsync("builder update", () => store.UpdateAsync(s.Id("a"), b => b.SetProperty(x => x.At, Minus5).SetProperty(x => x.Maybe, Plus3), Ct));
            await s.StepAsync("one after builder update", async () => (object?)Show(await store.OneAsync(s.Id("a"), Ct)));
            await s.StepAsync("upsert", () => store.UpsertAsync(new DtoEntity { Id = s.Id("a"), At = Plus3, Maybe = null }, Ct));
            await s.StepAsync("one after upsert", async () => (object?)Show(await store.OneAsync(s.Id("a"), Ct)));
            await s.ListAsync<DtoEntity>(e => e.At.ToString("o", CultureInfo.InvariantCulture));
        });

        Assert.Contains("one after create: 2021-05-06T04:08:09.0000000+00:00 2022-01-02T08:04:05.0000000+00:00", transcript);
        Assert.Contains("one after builder update: 2022-01-02T08:04:05.0000000+00:00 2021-05-06T04:08:09.0000000+00:00", transcript);
        Assert.Contains("one after upsert: 2021-05-06T04:08:09.0000000+00:00 ", transcript);
    }
}
