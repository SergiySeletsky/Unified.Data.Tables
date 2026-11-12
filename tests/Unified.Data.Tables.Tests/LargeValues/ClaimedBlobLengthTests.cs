using Azure.Data.Tables;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A read of an offloaded split value sizes its buffer by what the blob delivers, not by the length the
/// row's reference claims: a corrupt or hostile row that claims a huge length for a small blob fails
/// verification without the read allocating that length first. The integrity checks are unchanged.
/// </summary>
public class ClaimedBlobLengthTests
{
    private const string Pk = "p";
    private const string Rk = "r";

    private static ValueOffloadCoordinator Coordinator(InMemoryOffloadBlobStore store) =>
        new(store, new BlobOffloadOptions { ThresholdBytes = 100_000 }, "T");

    private static async Task<TableEntity> RowClaimingAsync(ValueOffloadCoordinator coordinator, string body, Func<long, long> claim)
    {
        var (row, _, blobs) = coordinator.Serialize(new SplitTextEntity { Body = body }, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);
        const string cell = "Body" + SplitCodec.BlobSuffix;
        var reference = BlobReference.Parse("Body", (string)row[cell]);
        row[cell] = (reference with { Length = claim(reference.Length) }).ToJson();
        return row;
    }

    [Fact]
    public async Task AHugeClaimedLength_FailsVerification_WithoutAllocatingIt()
    {
        var coordinator = Coordinator(new InMemoryOffloadBlobStore());
        var row = await RowClaimingAsync(coordinator, Ascii(200_000), _ => 1_500_000_000);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(
            () => coordinator.ResolveAsync(row, typeof(SplitTextEntity), null, CancellationToken.None));
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal("Body", ex.ColumnName);
        Assert.Contains("holds 200000 bytes, expected 1500000000", ex.Message, StringComparison.Ordinal);
        // Far below the claimed 1.5 GB (other tests may allocate meanwhile; this bounds what the read did).
        Assert.True(allocated < 256L * 1024 * 1024, $"the read allocated {allocated:N0} bytes");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task AClaimedLengthOffByOne_StillFails(int delta)
    {
        var coordinator = Coordinator(new InMemoryOffloadBlobStore());
        var row = await RowClaimingAsync(coordinator, Ascii(200_000), length => length + delta);

        var ex = await Assert.ThrowsAsync<TableValueIntegrityException>(
            () => coordinator.ResolveAsync(row, typeof(SplitTextEntity), null, CancellationToken.None));
        Assert.Equal("Body", ex.ColumnName);
    }

    [Theory]
    [InlineData(150_000)]
    [InlineData(3_000_000)]
    [InlineData(9_000_001)]
    public async Task AValueOfAnySize_StillReadsBack(int length)
    {
        var coordinator = Coordinator(new InMemoryOffloadBlobStore());
        var body = Ascii(length, seed: 5);
        var (row, _, blobs) = coordinator.Serialize(new SplitTextEntity { Body = body }, Pk, Rk, 0);
        await coordinator.UploadAsync(blobs, CancellationToken.None);

        var ctx = await coordinator.ResolveAsync(row, typeof(SplitTextEntity), null, CancellationToken.None);

        Assert.Equal(body, TableEntitySerializer.FromTableEntity<SplitTextEntity>(row, ctx).Body);
    }
}
