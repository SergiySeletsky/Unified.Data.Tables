using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>Written by the wider of two entity types that share one table.</summary>
public class ParityTwoTexts : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    [TableValueSplit(SplitEncoding.Text)]
    public string? Extra { get; set; }
}

/// <summary>The narrower type over the same table: it does not declare <c>Extra</c>.</summary>
public class ParityOneText : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }
}

/// <summary>A node that can hold another node of its own type: the type repeats below itself.</summary>
public class ParityLink
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    public ParityLink? Next { get; set; }
}

/// <summary>An entity whose large value can sit below a repeated type (<c>Head_Next_Body</c>), a column its list does not name.</summary>
public class ParityChain : Entity
{
    public ParityLink? Head { get; set; }
}

/// <summary>
/// The same chain on a type with so many large-value columns that the point read before a write reads the
/// row whole: then every reference of the row is seen, on both stores.
/// </summary>
public class ParityWideChain : Entity
{
    public ParityLink? Head { get; set; }
    public PriorRowReadBoundTests.ReportSection? Executive { get; set; }
    public PriorRowReadBoundTests.ReportSection? Technical { get; set; }
    public PriorRowReadBoundTests.ReportSection? Financial { get; set; }
    public PriorRowReadBoundTests.ReportSection? Legal { get; set; }
    public PriorRowReadBoundTests.ReportSection? Operations { get; set; }
}

/// <summary>
/// A write or delete releases the blobs the stored row references in the cells the point read before it
/// returns: the type's large-value columns, or every cell when the row is read whole. A blob referenced
/// in any other cell (a value below a type that repeats below itself, another type's column on a shared
/// table) is left for the orphan sweep. <see cref="InMemoryStorage{T}"/> follows the same rule as
/// <see cref="TableStorage{T}"/>, so after the same calls both hold the same blobs and a sweep deletes the
/// same number (BLOBS-7).
/// </summary>
public class ReleasedBlobParityTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // An existing table of the suite: only rows of this test's own partition are written, and removed at the end.
    private const string Table = nameof(SplitTextEntity);

    private static readonly string[] Kinds = [LargeValueStores.Memory, LargeValueStores.Azurite];

    // What is left under the row: the column and hash of each blob (the partition differs between the stores).
    private static List<string> UnderTheRow(IReadOnlyList<string> names) =>
        names.Select(name => name[(name.IndexOf("/r1/", StringComparison.Ordinal) + 4)..]).ToList();

    [Fact]
    public async Task DeleteThroughTheNarrowerType_LeavesTheSameBlobsOnBothStores_AndTheSweepDeletesTheSameNumber()
    {
        var left = new Dictionary<string, List<string>>();
        var swept = new Dictionary<string, int>();
        foreach (var kind in Kinds)
        {
            await using var stores = await LargeValueStores.OpenAsync(kind, cleanupGrace: TimeSpan.Zero, thresholdBytes: 1_000);
            Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
            var id = stores!.Id("r1");
            await stores.Store<ParityTwoTexts>(Table).CreateAsync(
                new ParityTwoTexts { Id = id, Body = new string('b', 40_000), Extra = new string('e', 40_000) }, Ct);
            await stores.ShareTableAsync<ParityTwoTexts, ParityOneText>(Table);
            Assert.Equal(2, (await stores.BlobNamesAsync<ParityOneText>(Table)).Count);
            await stores.PastTheNewestBlobAsync<ParityOneText>(Table);

            var narrow = stores.Store<ParityOneText>(Table);
            await narrow.DeleteAsync(id, Ct);

            Assert.Empty(await stores.RawRowsAsync<ParityOneText>(Table));
            left[kind] = UnderTheRow(await stores.BlobNamesAsync<ParityOneText>(Table));
            await stores.PastTheNewestBlobAsync<ParityOneText>(Table);
            swept[kind] = await narrow.SweepOrphanBlobsAsync(stores.Pk, TimeSpan.Zero, Ct);
            output.WriteLine($"{kind}: blobs left after the delete: [{string.Join(", ", left[kind])}]; the sweep deleted {swept[kind]}");
        }

        Assert.Equal(left[LargeValueStores.Azurite], left[LargeValueStores.Memory]);
        Assert.Equal(swept[LargeValueStores.Azurite], swept[LargeValueStores.Memory]);
        // The undeclared column's blob was left for the sweep.
        Assert.Single(left[LargeValueStores.Memory], n => n.StartsWith("Extra/", StringComparison.Ordinal));
        Assert.Equal(1, swept[LargeValueStores.Memory]);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("replace")]
    public async Task AValueBelowARepeatedType_DeletedOrReplaced_LeavesTheSameBlobsOnBothStores(string change)
    {
        var left = new Dictionary<string, List<string>>();
        var swept = new Dictionary<string, int>();
        foreach (var kind in Kinds)
        {
            await using var stores = await LargeValueStores.OpenAsync(kind, cleanupGrace: TimeSpan.Zero, thresholdBytes: 1_000);
            Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
            var id = stores!.Id("r1");
            await stores.Store<ParityChain>(Table).CreateAsync(
                new ParityChain { Id = id, Head = new ParityLink { Body = "head", Next = new ParityLink { Body = new string('d', 40_000) } } }, Ct);
            var read = await stores.Store<ParityChain>(Table).OneAsync(id, Ct);
            Assert.Equal(40_000, read!.Head!.Next!.Body!.Length);
            Assert.Single(await stores.BlobNamesAsync<ParityChain>(Table));
            await stores.PastTheNewestBlobAsync<ParityChain>(Table);

            var store = stores.Store<ParityChain>(Table);
            if (change == "delete")
                await store.DeleteAsync(id, Ct);
            else
                await store.UpsertAsync(new ParityChain { Id = id, Head = new ParityLink { Body = "head" } }, Ct);

            left[kind] = UnderTheRow(await stores.BlobNamesAsync<ParityChain>(Table));
            await stores.PastTheNewestBlobAsync<ParityChain>(Table);
            swept[kind] = await store.SweepOrphanBlobsAsync(stores.Pk, TimeSpan.Zero, Ct);
            output.WriteLine($"{kind}, {change}: blobs left [{string.Join(", ", left[kind])}]; the sweep deleted {swept[kind]}");
        }

        Assert.Equal(left[LargeValueStores.Azurite], left[LargeValueStores.Memory]);
        Assert.Equal(swept[LargeValueStores.Azurite], swept[LargeValueStores.Memory]);
        Assert.Single(left[LargeValueStores.Memory]);
        Assert.Equal(1, swept[LargeValueStores.Memory]);
    }

    // Above the bound the row is read whole, so TableStorage sees the reference below the repeated type
    // and releases it at once; the in-memory store agrees with that too.
    [Theory]
    [InlineData("delete")]
    [InlineData("replace")]
    public async Task AValueBelowARepeatedType_OfATypeWhoseRowIsReadWhole_IsReleasedAtOnce_OnBothStores(string change)
    {
        Assert.Null(LargeValueTable.PriorRowSelect(typeof(ParityWideChain)));
        var left = new Dictionary<string, int>();
        foreach (var kind in Kinds)
        {
            await using var stores = await LargeValueStores.OpenAsync(kind, cleanupGrace: TimeSpan.Zero, thresholdBytes: 1_000);
            Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
            var id = stores!.Id("r1");
            await stores.Store<ParityWideChain>(Table).CreateAsync(
                new ParityWideChain { Id = id, Head = new ParityLink { Body = "head", Next = new ParityLink { Body = new string('d', 40_000) } } }, Ct);
            Assert.Single(await stores.BlobNamesAsync<ParityWideChain>(Table));
            await stores.PastTheNewestBlobAsync<ParityWideChain>(Table);

            var store = stores.Store<ParityWideChain>(Table);
            if (change == "delete")
                await store.DeleteAsync(id, Ct);
            else
                await store.UpsertAsync(new ParityWideChain { Id = id, Head = new ParityLink { Body = "head" } }, Ct);

            left[kind] = (await stores.BlobNamesAsync<ParityWideChain>(Table)).Count;
        }

        Assert.Equal(0, left[LargeValueStores.Azurite]);
        Assert.Equal(0, left[LargeValueStores.Memory]);
    }

    // A large value of a runtime subtype is on the type's list of columns: released at once on both stores.
    [Fact]
    public async Task ReplacingAnOffloadedValueOfARuntimeMember_LeavesTheSameBlobsOnBothStores()
    {
        var left = new Dictionary<string, int>();
        foreach (var kind in Kinds)
        {
            await using var stores = await LargeValueStores.OpenAsync(kind, cleanupGrace: TimeSpan.Zero, thresholdBytes: 1_000);
            Assert.SkipWhen(stores is null, LargeValueStores.SkipReason);
            var id = stores!.Id("r1");
            await stores.Store<RuntimeSubtypeSplitTests.InterfaceHolder>().CreateAsync(new RuntimeSubtypeSplitTests.InterfaceHolder
            {
                Id = id,
                Shape = new RuntimeSubtypeSplitTests.RuntimeCircle { Notes = new string('a', 40_000) },
            }, Ct);
            Assert.Single(await stores.BlobNamesAsync<RuntimeSubtypeSplitTests.InterfaceHolder>());
            await stores.PastTheNewestBlobAsync<RuntimeSubtypeSplitTests.InterfaceHolder>();

            await stores.Store<RuntimeSubtypeSplitTests.InterfaceHolder>().UpsertAsync(new RuntimeSubtypeSplitTests.InterfaceHolder
            {
                Id = id,
                Shape = new RuntimeSubtypeSplitTests.RuntimeCircle { Notes = new string('b', 40_000) },
            }, Ct);

            var read = await stores.Store<RuntimeSubtypeSplitTests.InterfaceHolder>().OneAsync(id, Ct);
            Assert.Equal(new string('b', 40_000), read!.Large.Notes);
            left[kind] = (await stores.BlobNamesAsync<RuntimeSubtypeSplitTests.InterfaceHolder>()).Count;
        }

        Assert.Equal(left[LargeValueStores.Azurite], left[LargeValueStores.Memory]);
        Assert.Equal(1, left[LargeValueStores.Memory]);
    }
}
