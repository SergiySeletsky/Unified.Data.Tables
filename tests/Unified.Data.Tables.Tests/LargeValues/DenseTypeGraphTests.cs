using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Deciding whether a type is large-value aware, and listing its large-value columns, stays fast on a
/// dense type graph (14 types that each have a property of every other one), where listing every path
/// through the graph takes factorial time; and the listing of ordinary graphs is what it was.
/// </summary>
public class DenseTypeGraphTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    // Runs the work on another thread so a walk that never ends fails the test instead of hanging the run.
    private static T Within<T>(Func<T> work, string what)
    {
        var task = Task.Run(work);
        Assert.True(task.Wait(Bound), $"{what} did not complete within {Bound.TotalSeconds:0} seconds.");
        return task.Result;
    }

    private static string Text(int length) => new('x', length);

    // ── a dense graph that opts into nothing ─────────────────────────────────

    [Fact]
    public void PlainDenseGraph_IsNotLargeValueAware_WithinTheBound()
    {
        Assert.False(Within(() => TypeMetadataCache.IsLargeValueAware(typeof(DenseDoc<DensePlainLeaf>)), "IsLargeValueAware"));
        Assert.Empty(Within(() => TypeMetadataCache.LargeValueColumns(typeof(DenseDoc<DensePlainLeaf>)), "LargeValueColumns"));
        Assert.False(Within(() => LargeValueTable.IsLargeValueAware(typeof(Dense07<DensePlainLeaf>)), "IsLargeValueAware"));
    }

    [Fact]
    public async Task PlainDenseGraph_StoresConstructAndRoundTrip_WithinTheBound()
    {
        DenseDoc<DensePlainLeaf> Make() => new()
        {
            Id = "p|r",
            Root = new Dense01<DensePlainLeaf>
            {
                Name = "one",
                Leaf = new DensePlainLeaf { Name = "leaf" },
                P02 = new Dense02<DensePlainLeaf> { Name = "two", P14 = new Dense14<DensePlainLeaf> { Name = "fourteen" } },
            },
        };

        using var harness = Within(() => new StorageHarness<DenseDoc<DensePlainLeaf>>(), "TableStorage<T> construction");
        harness.SetupAdd();
        await harness.Store.CreateAsync(Make());
        // A plain type: one insert, no transaction and no point read.
        Assert.Equal(["CreateIfNotExistsAsync", "AddEntityAsync"], harness.Table.ReceivedCalls().Select(c => c.GetMethodInfo().Name));

        var memory = Within(() => new InMemoryStorage<DenseDoc<DensePlainLeaf>>(), "InMemoryStorage<T> construction");
        await memory.CreateAsync(Make());
        var row = memory.PhysicalRow("p", "r")!;
        Assert.Equal("one", row["Root_Name"]);
        Assert.Equal("leaf", row["Root_Leaf_Name"]);
        Assert.Equal("two", row["Root_P02_Name"]);
        Assert.Equal("fourteen", row["Root_P02_P14_Name"]);
        var read = (await memory.OneAsync("p|r"))!;
        Assert.Equal("fourteen", read.Root!.P02!.P14!.Name);
        Assert.Equal("leaf", read.Root.Leaf!.Name);
    }

    // ── the same graph with a large-value member reachable from every type ───

    [Fact]
    public void DenseGraphWithALargeValueMember_IsAware_AndListsTheNearestColumns_WithinTheBound()
    {
        Assert.True(Within(() => TypeMetadataCache.IsLargeValueAware(typeof(DenseDoc<DenseSplitLeaf>)), "IsLargeValueAware"));
        var columns = Within(() => TypeMetadataCache.LargeValueColumns(typeof(DenseDoc<DenseSplitLeaf>)), "LargeValueColumns");

        // The graph has more paths than can be listed (13! and more): the columns nearest the entity are.
        Assert.Contains("Root_Leaf_Body", columns);
        for (var i = 2; i <= 14; i++)
            Assert.Contains($"Root_P{i:00}_Leaf_Body", columns);
        Assert.Contains("Root_P02_P03_Leaf_Body", columns);
        Assert.Contains("Root_P14_P13_Leaf_Body", columns);
        Assert.All(columns, c => Assert.EndsWith("_Leaf_Body", c, StringComparison.Ordinal));
        Assert.Equal(columns.Count, columns.Distinct(StringComparer.Ordinal).Count());
        Assert.InRange(columns.Count, 14 + 13 * 12, TypeMetadataCache.MaxLargeValueWalkSteps);
    }

    [Fact]
    public async Task DenseGraphWithALargeValueMember_StoresConstructAndRoundTrip_WithinTheBound()
    {
        var near = Text(40_000);
        var far = Text(50_000);
        DenseDoc<DenseSplitLeaf> Make() => new()
        {
            Id = "p|r",
            Root = new Dense01<DenseSplitLeaf>
            {
                Name = "one",
                Leaf = new DenseSplitLeaf { Body = near },
                P02 = new Dense02<DenseSplitLeaf> { P03 = new Dense03<DenseSplitLeaf> { Leaf = new DenseSplitLeaf { Body = far } } },
            },
        };

        using var harness = Within(() => new StorageHarness<DenseDoc<DenseSplitLeaf>>(), "TableStorage<T> construction");
        Assert.NotNull(harness.Store);

        var memory = Within(() => new InMemoryStorage<DenseDoc<DenseSplitLeaf>>(), "InMemoryStorage<T> construction");
        await memory.CreateAsync(Make());
        var row = memory.PhysicalRow("p", "r")!;
        Assert.Equal(2, row["Root_Leaf_Body__chunks"]);
        Assert.Equal(2, row["Root_P02_P03_Leaf_Body__chunks"]);
        var read = (await memory.OneAsync("p|r"))!;
        Assert.Equal(near, read.Root!.Leaf!.Body);
        Assert.Equal(far, read.Root.P02!.P03!.Leaf!.Body);
    }

    // ── an endless chain of declared types ───────────────────────────────────

    [Fact]
    public async Task EndlessTypeChain_ThatOptsIntoNothing_IsNotAware_AndIsStoredAs083()
    {
        Assert.False(Within(() => TypeMetadataCache.IsLargeValueAware(typeof(EndlessChainDoc)), "IsLargeValueAware"));
        Assert.Empty(TypeMetadataCache.LargeValueColumns(typeof(EndlessChainDoc)));

        EndlessChainDoc Make() => new()
        {
            Id = "p|r",
            Chain = new EndlessChain<int> { Name = "a", Next = new EndlessChain<EndlessChain<int>> { Name = "b" } },
        };

        // The cells the published 0.8.3 writes for it.
        var row = Make().ToTableEntity("p", "r");
        Assert.Equal(
            ["Chain_Name", "Chain_Next_Name", "CreatedAt", "Id", "PartitionKey", "RowKey", "UpdatedAt"],
            row.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("a", row["Chain_Name"]);
        Assert.Equal("b", row["Chain_Next_Name"]);

        using var harness = Within(() => new StorageHarness<EndlessChainDoc>(), "TableStorage<T> construction");
        harness.SetupAdd();
        await harness.Store.CreateAsync(Make());
        Assert.Equal(["CreateIfNotExistsAsync", "AddEntityAsync"], harness.Table.ReceivedCalls().Select(c => c.GetMethodInfo().Name));

        var memory = Within(() => new InMemoryStorage<EndlessChainDoc>(), "InMemoryStorage<T> construction");
        await memory.CreateAsync(Make());
        var read = (await memory.OneAsync("p|r"))!;
        Assert.Equal("a", read.Chain!.Name);
        Assert.Equal("b", read.Chain.Next!.Name);
        Assert.Null(read.Chain.Next.Next);
    }

    [Fact]
    public async Task EndlessTypeChain_WithALargeValueMember_ListsTheColumnsANameCanHold()
    {
        Assert.True(Within(() => TypeMetadataCache.IsLargeValueAware(typeof(EndlessSplitChainDoc)), "IsLargeValueAware"));
        var columns = TypeMetadataCache.LargeValueColumns(typeof(EndlessSplitChainDoc));

        // Chain_Body, Chain_Next_Body, Chain_Next_Next_Body, ...: a column name holds at most 255
        // characters, so the chain of columns ends there.
        Assert.Equal(50, columns.Count);
        Assert.Equal("Chain_Body", columns[0]);
        Assert.Equal("Chain_Next_Body", columns[1]);
        Assert.Equal("Chain_" + string.Concat(Enumerable.Repeat("Next_", 49)) + "Body", columns[^1]);
        Assert.Equal(255, columns[^1].Length);

        var body = Text(40_000);
        var memory = Within(() => new InMemoryStorage<EndlessSplitChainDoc>(), "InMemoryStorage<T> construction");
        await memory.CreateAsync(new EndlessSplitChainDoc
        {
            Id = "p|r",
            Chain = new EndlessSplitChain<int> { Next = new EndlessSplitChain<EndlessSplitChain<int>> { Body = body } },
        });
        Assert.Equal(2, memory.PhysicalRow("p", "r")!["Chain_Next_Body__chunks"]);
        Assert.Equal(body, (await memory.OneAsync("p|r"))!.Chain!.Next!.Body);
    }

    /// <summary>An endless graph that also branches: every level doubles the number of new types.</summary>
    public class EndlessFan<T>
    {
        public string? Name { get; set; }

        public EndlessFan<KeyValuePair<T, int>>? Left { get; set; }

        public EndlessFan<KeyValuePair<T, long>>? Right { get; set; }
    }

    public class EndlessFanDoc : Entity
    {
        public EndlessFan<int>? Fan { get; set; }
    }

    [Fact]
    public async Task EndlessBranchingTypes_ThatOptIntoNothing_AreDecidedWithinTheBound()
    {
        Assert.False(Within(() => TypeMetadataCache.IsLargeValueAware(typeof(EndlessFanDoc)), "IsLargeValueAware"));

        var memory = Within(() => new InMemoryStorage<EndlessFanDoc>(), "InMemoryStorage<T> construction");
        await memory.CreateAsync(new EndlessFanDoc
        {
            Id = "p|r",
            Fan = new EndlessFan<int> { Name = "a", Right = new EndlessFan<KeyValuePair<int, long>> { Name = "b" } },
        });
        var row = memory.PhysicalRow("p", "r")!;
        Assert.Equal("a", row["Fan_Name"]);
        Assert.Equal("b", row["Fan_Right_Name"]);
        Assert.Equal("b", (await memory.OneAsync("p|r"))!.Fan!.Right!.Name);
    }

    // ── the listing of ordinary graphs ───────────────────────────────────────

    private static string[] Columns(Type type) =>
        TypeMetadataCache.LargeValueColumns(type).OrderBy(c => c, StringComparer.Ordinal).ToArray();

    private sealed class WalkLeaf
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }

        public int Count { get; set; }
    }

    private sealed class WalkWrapper
    {
        public WalkLeaf? Inner { get; set; }

        public TableBlobContent? File { get; set; }
    }

    // The same type under two properties, and once more behind a wrapper: one column per path.
    private sealed class WalkDiamond : Entity
    {
        public WalkLeaf? First { get; set; }

        public WalkLeaf? Second { get; set; }

        public WalkWrapper? Wrapped { get; set; }

        [TableValueSplit(SplitEncoding.Gzip)]
        public string? Own { get; set; }
    }

    [Fact]
    public void TypeReachedByTwoPaths_IsListedUnderBoth()
    {
        Assert.Equal(["First_Body", "Own", "Second_Body", "Wrapped_File", "Wrapped_Inner_Body"], Columns(typeof(WalkDiamond)));
        Assert.True(TypeMetadataCache.IsLargeValueAware(typeof(WalkDiamond)));
    }

    private sealed class WalkNode
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? Body { get; set; }

        public WalkNode? Next { get; set; }
    }

    private sealed class WalkList : Entity
    {
        public WalkNode? Head { get; set; }

        public WalkNode? Tail { get; set; }
    }

    [Fact]
    public void TypeThatReachesItself_IsListedOncePerPath()
    {
        // The path ends where a type would repeat: Head_Next_Body is not listed, as before.
        Assert.Equal(["Head_Body", "Tail_Body"], Columns(typeof(WalkList)));
        Assert.Equal(["Body"], Columns(typeof(WalkNode)));
    }

    [Fact]
    public async Task ValueBelowARepeatedType_IsNotListed_AndStillRoundTripsOnBothStores()
    {
        // The list of columns is what the stores read ahead and project by, not what may be stored: a
        // large value deeper than the list reaches is still written split and read back whole.
        var deep = OffloadTestSupport.Ascii(40_000);
        WalkList Make() => new() { Id = "p|r", Head = new WalkNode { Body = "head", Next = new WalkNode { Body = deep } } };
        Assert.DoesNotContain("Head_Next_Body", TypeMetadataCache.LargeValueColumns(typeof(WalkList)));

        var memory = new InMemoryStorage<WalkList>();
        await memory.CreateAsync(Make());
        Assert.Equal(2, memory.PhysicalRow("p", "r")!["Head_Next_Body__chunks"]);
        var read = (await memory.OneAsync("p|r"))!;
        Assert.Equal("head", read.Head!.Body);
        Assert.Equal(deep, read.Head.Next!.Body);

        Azure.Data.Tables.TableEntity? written = null;
        using (var writer = new StorageHarness<WalkList>())
        {
            writer.Table.AddEntityAsync(Arg.Do<Azure.Data.Tables.TableEntity>(e => written = e), Arg.Any<CancellationToken>()).Returns(Mocks.EtagResponse());
            await writer.Store.CreateAsync(Make());
        }
        Assert.NotNull(written);
        Assert.Equal(2, written["Head_Next_Body__chunks"]);
        written.ETag = new Azure.ETag("W/\"etag1\"");
        using var reader = new StorageHarness<WalkList>();
        reader.SetupGet(written);
        Assert.Equal(deep, (await reader.Store.OneAsync("p|r"))!.Head!.Next!.Body);
    }

    private sealed class WalkLeft
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? LeftBody { get; set; }

        public WalkRight? Right { get; set; }
    }

    private sealed class WalkRight
    {
        [TableValueSplit(SplitEncoding.Text)]
        public string? RightBody { get; set; }

        public WalkLeft? Left { get; set; }
    }

    private sealed class WalkPair : Entity
    {
        public WalkLeft? Left { get; set; }

        public WalkRight? Right { get; set; }
    }

    [Fact]
    public void TypesThatReachEachOther_AreListedAlongEveryPathThatDoesNotRepeatAType()
    {
        Assert.Equal(
            ["Left_LeftBody", "Left_Right_RightBody", "Right_Left_LeftBody", "Right_RightBody"],
            Columns(typeof(WalkPair)));
    }

    private sealed class WalkPlainBranch
    {
        public string? Name { get; set; }

        public WalkPlainBranch? Again { get; set; }
    }

    // A large-value member beside a branch that leads to none: the branch adds nothing.
    private sealed class WalkMixed : Entity
    {
        public WalkPlainBranch? Plain { get; set; }

        public WalkLeaf? Leaf { get; set; }
    }

    [Fact]
    public void BranchThatLeadsToNoLargeValue_AddsNothing()
    {
        Assert.Equal(["Leaf_Body"], Columns(typeof(WalkMixed)));
        Assert.False(TypeMetadataCache.IsLargeValueAware(typeof(WalkPlainBranch)));
    }
}
