namespace Unified.Data.Tables.Tests;

/// <summary>
/// A plain type's <c>byte[]</c> cell is never compressed, and under the default <c>Throw</c> policy one above the
/// 64 KB cell limit is sent as it is and refused by the service (a 400 <c>RequestFailedException</c>), as on
/// 0.8.3; <c>OversizedCellException</c> is thrown only for a string or JSON-stored cell. The in-memory store
/// fails the same way.
/// </summary>
public sealed class OversizedBinaryCellFidelityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class BinaryEntity : Entity
    {
        public byte[]? Bytes { get; set; }
    }

    [Fact]
    public async Task PlainByteArrayAboveTheCellLimit_IsRefusedTheSameWay()
    {
        var large = Enumerable.Range(0, 70_000).Select(i => (byte)(i % 7)).ToArray();
        var fits = new byte[65_536];

        var transcript = await FidelityScript.BothAsync(async s =>
        {
            await s.StepAsync("create 64 KB", () => s.Store<BinaryEntity>().CreateAsync(new BinaryEntity { Id = s.Id("fits"), Bytes = fits }, Ct));
            await s.StepAsync("create 70,000 bytes", () => s.Store<BinaryEntity>().CreateAsync(new BinaryEntity { Id = s.Id("large"), Bytes = large }, Ct));
            await s.StepAsync("upsert 70,000 bytes", () => s.Store<BinaryEntity>().UpsertAsync(new BinaryEntity { Id = s.Id("fits"), Bytes = large }, Ct));
            await s.StepAsync("builder update 70,000 bytes", () => s.Store<BinaryEntity>().UpdateAsync(s.Id("fits"), b => b.SetProperty(x => x.Bytes, large), Ct));
            await s.StepAsync("create batch 70,000 bytes", () => s.Store<BinaryEntity>().CreateBatchAsync([new BinaryEntity { Id = s.Id("batch"), Bytes = large }], Ct));
            await s.ListAsync<BinaryEntity>(e => e.Bytes?.Length);
        });

        Assert.Contains($"list {nameof(BinaryEntity)} pa: [pa|fits=65536]", transcript);
    }
}
