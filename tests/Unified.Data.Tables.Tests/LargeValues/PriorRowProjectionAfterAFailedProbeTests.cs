namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The projection of the point read before a replace or delete is kept per entity type only once it was built
/// from a column list the default-instance probe completed. A constructor that failed during the first
/// operation of a type does not decide, for the process, which blobs later replaces release (SERIALIZER-8,
/// ROWS-3). The projection and the column list are remembered for the process, so every test and every store
/// case has its own entity types.
/// </summary>
public class PriorRowProjectionAfterAFailedProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores() => [LargeValueStores.Memory, LargeValueStores.Azurite];

    public interface IProbedShape
    {
    }

    public sealed class ProbedCircle : IProbedShape
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Notes { get; set; }
    }

    // A type whose constructor fails while Failing is set (a dependency not ready yet), one per test and store.
    public abstract class FlakyHolderBase : Entity
    {
        public IProbedShape Shape { get; set; } = new ProbedCircle();

        public int Rank { get; set; }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class ReplaceMemoryFlaky : FlakyHolderBase
    {
        private static volatile bool failing;

        public static bool Failing { get => failing; set => failing = value; }

        public ReplaceMemoryFlaky()
        {
            if (failing) throw new InvalidOperationException("not ready yet");
        }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class ReplaceAzuriteFlaky : FlakyHolderBase
    {
        private static volatile bool failing;

        public static bool Failing { get => failing; set => failing = value; }

        public ReplaceAzuriteFlaky()
        {
            if (failing) throw new InvalidOperationException("not ready yet");
        }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class CountMemoryFlaky : FlakyHolderBase
    {
        private static volatile bool failing;

        public static bool Failing { get => failing; set => failing = value; }

        public CountMemoryFlaky()
        {
            if (failing) throw new InvalidOperationException("not ready yet");
        }
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class CountAzuriteFlaky : FlakyHolderBase
    {
        private static volatile bool failing;

        public static bool Failing { get => failing; set => failing = value; }

        public CountAzuriteFlaky()
        {
            if (failing) throw new InvalidOperationException("not ready yet");
        }
    }

    // The same type, whose constructor never fails (the control).
    [TableValueSplit(SplitEncoding.Text)]
    public sealed class SteadyHolder : FlakyHolderBase
    {
    }

    private static string Ascii(int length, int seed)
    {
        var rng = new Random(seed);
        return string.Create(length, rng, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)r.Next(0x21, 0x7F);
        });
    }

    // The blobs left under the type's rows after a create and a replace of one id, the first operation of the
    // type in the process (a delete of an absent id) running while the constructor fails.
    private static async Task<int> BlobsLeftAfterAReplaceAsync<T>(LargeValueStores stores, Action<bool> failing, string row)
        where T : FlakyHolderBase, new()
    {
        var store = stores.Store<T>();
        failing(true);
        try
        {
            await store.DeleteAsync(stores.Id("absent-" + row), Ct);
        }
        finally
        {
            failing(false);
        }
        await store.CreateAsync(new T { Id = stores.Id(row), Shape = new ProbedCircle { Notes = Ascii(40_000, 1) } }, Ct);
        await store.UpsertAsync(new T { Id = stores.Id(row), Shape = new ProbedCircle { Notes = Ascii(40_000, 2) } }, Ct);
        await stores.PastTheNewestBlobAsync<T>();
        Assert.Equal(Ascii(40_000, 2), ((ProbedCircle)(await store.OneAsync(stores.Id(row), Ct))!.Shape).Notes);
        return (await stores.BlobNamesAsync<T>()).Count;
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AReplaceReleasesTheRuntimeMembersBlob_AlsoAfterAFirstOperationWhoseProbeFailed(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind, cleanupGrace: TimeSpan.Zero, thresholdBytes: 1_000);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);

        var flaky = kind == LargeValueStores.Memory
            ? await BlobsLeftAfterAReplaceAsync<ReplaceMemoryFlaky>(stores, f => ReplaceMemoryFlaky.Failing = f, "f")
            : await BlobsLeftAfterAReplaceAsync<ReplaceAzuriteFlaky>(stores, f => ReplaceAzuriteFlaky.Failing = f, "f");

        // Only the blob of the current value is left: the replace released the one it superseded.
        Assert.Equal(1, flaky);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ConstructorThatFailedOnce_DoesNotChangeWhichBlobsAReplaceReleases(string kind)
    {
        await using var stores = await LargeValueStores.OpenAsync(kind, cleanupGrace: TimeSpan.Zero, thresholdBytes: 1_000);
        Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);

        var stable = await BlobsLeftAfterAReplaceAsync<SteadyHolder>(stores, _ => { }, "s-" + kind);
        var flakyType = kind == LargeValueStores.Memory ? typeof(CountMemoryFlaky) : typeof(CountAzuriteFlaky);
        var flaky = kind == LargeValueStores.Memory
            ? await BlobsLeftAfterAReplaceAsync<CountMemoryFlaky>(stores, f => CountMemoryFlaky.Failing = f, "f")
            : await BlobsLeftAfterAReplaceAsync<CountAzuriteFlaky>(stores, f => CountAzuriteFlaky.Failing = f, "f");

        var flakySelect = string.Join(",", Providers.LargeValueTable.PriorRowSelect(flakyType) ?? []);
        var stableSelect = string.Join(",", Providers.LargeValueTable.PriorRowSelect(typeof(SteadyHolder)) ?? []);
        Assert.True(flaky == stable && flakySelect == stableSelect,
            $"blobs left after a replace: stable {stable}, flaky {flaky}; prior-row select: flaky {flakySelect} vs stable {stableSelect}");
        // A type whose probe succeeds sends the request it always sent.
        Assert.Equal("_SplitParts,_SplitOf,Shape_Notes__blob", stableSelect);
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class SelectFlaky : FlakyHolderBase
    {
        private static volatile bool failing;

        public static bool Failing { get => failing; set => failing = value; }

        public SelectFlaky()
        {
            if (failing) throw new InvalidOperationException("not ready yet");
        }
    }

    // While the probe fails the projection names the declared columns only; it is not kept, so the first
    // call after the constructor works again names the runtime member's reference cell.
    [Fact]
    public void ProjectionBuiltWhileTheProbeFails_IsNotKept()
    {
        SelectFlaky.Failing = true;
        IReadOnlyList<string>? whileFailing;
        try
        {
            whileFailing = Providers.LargeValueTable.PriorRowSelect(typeof(SelectFlaky));
        }
        finally
        {
            SelectFlaky.Failing = false;
        }

        Assert.Equal("_SplitParts,_SplitOf", string.Join(",", whileFailing ?? []));
        Assert.Equal("_SplitParts,_SplitOf,Shape_Notes__blob", string.Join(",", Providers.LargeValueTable.PriorRowSelect(typeof(SelectFlaky)) ?? []));
    }

    [TableValueSplit(SplitEncoding.Text)]
    public sealed class DeleteMemoryFlaky : FlakyHolderBase
    {
        private static volatile bool failing;

        public static bool Failing { get => failing; set => failing = value; }

        public DeleteMemoryFlaky()
        {
            if (failing) throw new InvalidOperationException("not ready yet");
        }
    }

    // The in-memory store consults the projection only for a row that exists: here its first use is a delete
    // of a stored row while the constructor fails. That delete may leave the row's blob to the orphan sweep
    // (the documented consequence), but a later replace releases the blob it supersedes.
    [Fact]
    public async Task InMemory_AReplaceReleasesTheRuntimeMembersBlob_AlsoAfterADeleteWhoseProbeFailed()
    {
        await using var stores = await LargeValueStores.OpenAsync(LargeValueStores.Memory, cleanupGrace: TimeSpan.Zero, thresholdBytes: 1_000);
        var store = stores!.Store<DeleteMemoryFlaky>();
        await store.CreateAsync(new DeleteMemoryFlaky { Id = stores.Id("x"), Shape = new ProbedCircle { Notes = Ascii(40_000, 1) } }, Ct);
        DeleteMemoryFlaky.Failing = true;
        try
        {
            await store.DeleteAsync(stores.Id("x"), Ct);
        }
        finally
        {
            DeleteMemoryFlaky.Failing = false;
        }

        await store.CreateAsync(new DeleteMemoryFlaky { Id = stores.Id("y"), Shape = new ProbedCircle { Notes = Ascii(40_000, 2) } }, Ct);
        await store.UpsertAsync(new DeleteMemoryFlaky { Id = stores.Id("y"), Shape = new ProbedCircle { Notes = Ascii(40_000, 3) } }, Ct);

        var ofY = (await stores.BlobNamesAsync<DeleteMemoryFlaky>()).Where(n => n.Contains("/y/", StringComparison.Ordinal)).ToList();
        Assert.True(ofY.Count == 1, $"blobs of the replaced row: {string.Join(", ", ofY)}");
        Assert.Equal(Ascii(40_000, 3), ((ProbedCircle)(await store.OneAsync(stores.Id("y"), Ct))!.Shape).Notes);
    }
}
