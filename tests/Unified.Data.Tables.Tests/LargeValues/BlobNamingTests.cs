using Azure.Data.Tables;
using Azure.Storage.Blobs;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// Blob names of offloaded values: every segment is escaped so the Blobs SDK, which collapses
/// <c>.</c> and <c>..</c> path segments, addresses exactly the name a row references; sweeps and
/// partition deletes only ever delete names of the exact offload shape; the table-wide sweep.
/// </summary>
public class BlobNamingTests
{
    private static readonly string BigText = Ascii(40_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Keys that a naive escaping turns into dot segments, or that look already escaped.</summary>
    public static TheoryData<string> AwkwardKeys => new()
    {
        ".", "..", "a.", "...", ".a", "%2E", "%252E", "%", "a%2Fb", "ünï-çødé", "日本語", "a b", "",
    };

    private static UnifiedTableStorageOptions Options() => new()
    {
        IdNormalization = IdNormalization.AsWritten,
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000, CleanupGrace = TimeSpan.Zero },
    };

    private static string Hash(string token) => Sha(Utf8(token));

    private static void AssertAddressable(string name)
    {
        foreach (var segment in name.Split('/'))
        {
            Assert.NotEqual(string.Empty, segment);
            Assert.NotEqual(".", segment);
            Assert.NotEqual("..", segment);
            Assert.False(segment.EndsWith('.'), $"segment '{segment}' of '{name}' ends with '.'");
        }
    }

    // ── the encoding ──────────────────────────────────────────────────────

    [Fact]
    public void BlobName_EscapesDots()
    {
        var sha = Hash("x");

        Assert.Equal($"T/%2E/%2E%2E/a%2Eb/{sha}", ValueOffloadCoordinator.BlobName("T", ".", "..", "a.b", sha));
        Assert.Equal($"T/a%2E/%2E%2E%2E/Body/{sha}", ValueOffloadCoordinator.BlobName("T", "a.", "...", "Body", sha));
        Assert.Equal("T/%2E/", ValueOffloadCoordinator.PartitionPrefix("T", "."));
    }

    [Fact]
    public void BlobName_EmptyKey_IsANonEmptySegment()
    {
        var sha = Hash("x");

        Assert.Equal($"T/%/r/Body/{sha}", ValueOffloadCoordinator.BlobName("T", "", "r", "Body", sha));
        Assert.Equal($"T/p/%/Body/{sha}", ValueOffloadCoordinator.BlobName("T", "p", "", "Body", sha));
    }

    [Theory]
    [MemberData(nameof(AwkwardKeys))]
    public void Segment_IsAddressable_AndRoundTripsExactlyOnce(string key)
    {
        var segment = OffloadBlobNames.EscapeSegment(key);

        AssertAddressable(segment);
        Assert.True(OffloadBlobNames.TryUnescapeSegment(segment, out var back));
        Assert.Equal(key, back);
        // An escaped segment unescaped once is the key, never the key unescaped again.
        if (Uri.UnescapeDataString(key) != key)
            Assert.NotEqual(Uri.UnescapeDataString(key), back);
    }

    [Fact]
    public void Segment_EncodingIsInjective()
    {
        var keys = AwkwardKeys.Select(row => row.Data).Append("%2e").Append("A").Append("%41").ToList();
        var segments = keys.Select(OffloadBlobNames.EscapeSegment).ToList();

        Assert.Equal(keys.Count, segments.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("a.b")]   // a raw dot is never written
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("%2e")]   // lower-case escapes are never written
    [InlineData("%41")]   // an unreserved character is never escaped
    [InlineData("%zz")]
    [InlineData("a b")]   // a space is always escaped
    public void Segment_NotWrittenByThisLibrary_IsNotUnescaped(string segment)
    {
        Assert.False(OffloadBlobNames.TryUnescapeSegment(segment, out _));
    }

    [Theory]
    [InlineData("T/p/r/Body/{sha}", true)]
    [InlineData("T/%2E/%2E%2E/Inner_Body/{sha}", true)]
    [InlineData("T/p/%/Body/{sha}", true)]
    [InlineData("T/p/r/Body/aa", false)]                 // not a SHA-256
    [InlineData("T/p/r/Body/{SHA}", false)]              // upper-case hex is never written
    [InlineData("T/p/r/Body/{sha}/x", false)]            // too deep
    [InlineData("T/p/r/{sha}", false)]                   // too shallow
    [InlineData("T/p/stray", false)]
    [InlineData("T/p/./Body/{sha}", false)]              // not an escaped segment
    [InlineData("T/p/r.x/Body/{sha}", false)]
    [InlineData("T/p//Body/{sha}", false)]
    [InlineData("T/p/r//{sha}", false)]
    [InlineData("T/q/r/Body/{sha}", false)]              // another partition
    public void IsOffloadBlobName_AcceptsOnlyTheExactShape(string template, bool expected)
    {
        var sha = Hash("x");
        var name = template.Replace("{sha}", sha, StringComparison.Ordinal).Replace("{SHA}", sha.ToUpperInvariant(), StringComparison.Ordinal);
        var prefix = ValueOffloadCoordinator.PartitionPrefix("T", name.Contains("/%2E/", StringComparison.Ordinal) ? "." : "p");

        Assert.Equal(expected, OffloadBlobNames.IsOffloadBlobName(name, prefix));
    }

    // ── the stores refuse names the SDK would not address as given ─────────

    [Theory]
    [InlineData("T/./r/Body/x")]
    [InlineData("T/../r/Body/x")]
    [InlineData("T/p/r/Body/..")]
    public async Task InMemoryStore_RefusesDotSegmentNames_LikeTheSdkWouldRewrite(string name)
    {
        var store = new InMemoryOffloadBlobStore();
        var reference = new BlobReference(name, Hash("x"), 1, "application/octet-stream", "binary");

        await Assert.ThrowsAsync<ArgumentException>(() => store.UploadCreateOnlyAsync(name, new MemoryStream([1]), reference, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetStateAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TouchAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync("Body", name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteIfUnmodifiedAsync(name, null, Ct));
    }

    [Theory]
    [InlineData("T/./r/Body/x")]
    [InlineData("T/../r/Body/x")]
    public async Task AzureStore_RefusesDotSegmentNames_BeforeAnyRequest(string name)
    {
        // No request is sent: the container URI points nowhere.
        var store = new AzureOffloadBlobStore(new BlobContainerClient(new Uri("http://127.0.0.1:1/account/container")));
        var reference = new BlobReference(name, Hash("x"), 1, "application/octet-stream", "binary");

        await Assert.ThrowsAsync<ArgumentException>(() => store.UploadCreateOnlyAsync(name, new MemoryStream([1]), reference, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetStateAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TouchAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync("Body", name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteIfUnmodifiedAsync(name, null, Ct));
    }

    // ── InMemoryStorage: awkward keys end to end ──────────────────────────

    private static async Task AgeAllAsync(InMemoryStorage<SplitTextEntity> store, TimeSpan age)
    {
        foreach (var name in store.OffloadedBlobNames())
        {
            await using var stream = await store.Blobs.OpenReadAsync("Body", name, CancellationToken.None);
            var bytes = await ReadAllAsync(stream);
            store.Blobs.Seed(name, bytes, Sha(bytes), DateTimeOffset.UtcNow - age);
        }
    }

    [Theory]
    [MemberData(nameof(AwkwardKeys))]
    public async Task InMemory_AwkwardKeys_RoundTrip_SweepKeeps_DeleteRemoves(string key)
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options());
        var asPartition = new SplitTextEntity { Id = key + "|r", Body = BigText };
        var asRow = new SplitTextEntity { Id = "p|" + key, Body = Ascii(40_000, seed: 3) };
        await store.CreateAsync(asPartition, Ct);
        await store.CreateAsync(asRow, Ct);

        var names = store.OffloadedBlobNames();
        Assert.Equal(2, names.Count);
        Assert.Contains(names, n => n.StartsWith($"SplitTextEntity/{OffloadBlobNames.EscapeSegment(key)}/r/Body/", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith($"SplitTextEntity/p/{OffloadBlobNames.EscapeSegment(key)}/Body/", StringComparison.Ordinal));
        Assert.All(names, AssertAddressable);

        await AgeAllAsync(store, TimeSpan.FromHours(1));
        if (!string.IsNullOrWhiteSpace(key))
            Assert.Equal(0, await store.SweepOrphanBlobsAsync(key, TimeSpan.FromMinutes(5), Ct));
        Assert.Equal(0, await store.SweepOrphanBlobsAsync("p", TimeSpan.FromMinutes(5), Ct));
        Assert.Equal(0, await store.SweepOrphanBlobsAsync(TimeSpan.FromMinutes(5), Ct));
        Assert.Equal(names, store.OffloadedBlobNames());

        Assert.Equal(BigText, (await store.OneAsync(asPartition.Id, Ct))!.Body);
        Assert.Equal(asRow.Body, (await store.OneAsync(asRow.Id, Ct))!.Body);

        await store.DeleteAsync(asPartition.Id, Ct);
        await store.DeleteAsync(asRow.Id, Ct);
        Assert.Empty(store.OffloadedBlobNames());
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("%2E")]
    public async Task InMemory_DeletePartition_OfAnAwkwardKey_RemovesItsBlobsOnly(string key)
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options());
        await store.CreateAsync(new SplitTextEntity { Id = key + "|r", Body = BigText }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = BigText }, Ct);
        await AgeAllAsync(store, TimeSpan.FromHours(1));

        Assert.Equal(1, await store.DeletePartitionAsync(key, Ct));

        Assert.StartsWith("SplitTextEntity/p/r/Body/", Assert.Single(store.OffloadedBlobNames()), StringComparison.Ordinal);
        Assert.Equal(BigText, (await store.OneAsync("p|r", Ct))!.Body);
    }

    [Fact]
    public async Task InMemory_SweepAndDeletePartition_NeverDeleteNamesOfAnotherShape()
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options());
        var old = DateTimeOffset.UtcNow.AddHours(-1);
        string[] foreign =
        [
            "SplitTextEntity/p/stray",
            "SplitTextEntity/p/r/Body/not-a-hash",
            $"SplitTextEntity/p/r/Body/{Hash("x").ToUpperInvariant()}",
            $"SplitTextEntity/p/r/Body/{Hash("x")}/deeper",
            $"SplitTextEntity/p/r x/Body/{Hash("x")}",
            "SplitTextEntity/p/readme.txt",
        ];
        foreach (var name in foreign)
            store.Blobs.Seed(name, [1], Sha([1]), old);
        var orphan = ValueOffloadCoordinator.BlobName("SplitTextEntity", "p", "r", "Body", Hash("orphan"));
        store.Blobs.Seed(orphan, [2], Sha([2]), old);

        Assert.Equal(1, await store.SweepOrphanBlobsAsync("p", TimeSpan.FromMinutes(5), Ct));
        Assert.Equal(0, await store.SweepOrphanBlobsAsync(TimeSpan.FromMinutes(5), Ct));
        await store.DeletePartitionAsync("p", Ct);

        Assert.Equal(foreign.Order(StringComparer.Ordinal), store.OffloadedBlobNames());
    }

    // ── table-wide sweep ──────────────────────────────────────────────────

    [Fact]
    public async Task InMemory_TableWideSweep_DeletesOrphansOfEveryPartition_KeepsLiveBlobs()
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options());
        await store.CreateAsync(new SplitTextEntity { Id = ".|r", Body = BigText }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = BigText }, Ct);
        var live = store.OffloadedBlobNames();
        var old = DateTimeOffset.UtcNow.AddHours(-1);
        var orphans = new[] { ".", "p", "q", "..", "" }
            .Select(pk => ValueOffloadCoordinator.BlobName("SplitTextEntity", pk, "gone", "Body", Hash(pk)))
            .ToList();
        foreach (var name in orphans)
            store.Blobs.Seed(name, [1], Sha([1]), old);
        var young = ValueOffloadCoordinator.BlobName("SplitTextEntity", "q", "new", "Body", Hash("young"));
        store.Blobs.Seed(young, [1], Sha([1]), DateTimeOffset.UtcNow);
        var otherTable = ValueOffloadCoordinator.BlobName("OtherTable", "p", "gone", "Body", Hash("o"));
        store.Blobs.Seed(otherTable, [1], Sha([1]), old);
        // A partition segment this library never writes (a raw dot): not a partition of the table.
        var unescaped = $"SplitTextEntity/a.b/gone/Body/{Hash("a.b")}";
        store.Blobs.Seed(unescaped, [1], Sha([1]), old);
        await AgeAllLiveAsync();

        Assert.Equal(orphans.Count, await store.SweepOrphanBlobsAsync(TimeSpan.FromMinutes(5), Ct));

        Assert.Equal(live.Append(young).Append(otherTable).Append(unescaped).Order(StringComparer.Ordinal), store.OffloadedBlobNames());
        Assert.Equal(BigText, (await store.OneAsync(".|r", Ct))!.Body);

        async Task AgeAllLiveAsync()
        {
            foreach (var name in live)
            {
                await using var stream = await store.Blobs.OpenReadAsync("Body", name, CancellationToken.None);
                var bytes = await ReadAllAsync(stream);
                store.Blobs.Seed(name, bytes, Sha(bytes), old);
            }
        }
    }

    [Fact]
    public async Task InMemory_TableWideSweep_Validates_AndWithoutOffloadReturnsZero()
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SweepOrphanBlobsAsync(TimeSpan.FromTicks(-1), Ct));

        var plain = new InMemoryStorage<SplitTextEntity>(null, new UnifiedTableStorageOptions());
        Assert.Equal(0, await plain.SweepOrphanBlobsAsync(TimeSpan.Zero, Ct));
    }

    private static (StorageHarness<SplitTextEntity> H, RecordingBlobStore Blobs) TableRig()
    {
        var blobs = new RecordingBlobStore([]);
        var h = new StorageHarness<SplitTextEntity>(options: Options(), blobs: blobs);
        h.SetupQueryByFilter();
        return (h, blobs);
    }

    [Fact]
    public async Task TableWideSweep_ListsPartitionsWithADelimiter_AndSweepsEach()
    {
        var (h, blobs) = TableRig();
        using var _ = h;
        var old = DateTimeOffset.UtcNow.AddHours(-1);
        var a = ValueOffloadCoordinator.BlobName("SplitTextEntity", ".", "r", "Body", Hash("a"));
        var b = ValueOffloadCoordinator.BlobName("SplitTextEntity", "p", "r", "Body", Hash("b"));
        blobs.Inner.Seed(a, [1], Sha([1]), old);
        blobs.Inner.Seed(b, [1], Sha([1]), old);

        Assert.Equal(2, await h.Store.SweepOrphanBlobsAsync(TimeSpan.FromMinutes(5), Ct));

        Assert.Empty(blobs.Inner.Names);
        Assert.Contains("prefixes SplitTextEntity/", blobs.Calls);
        Assert.Contains("list SplitTextEntity/%2E/", blobs.Calls);
        Assert.Contains("list SplitTextEntity/p/", blobs.Calls);
        // One service-clock read for the whole table: every partition shares the cutoff.
        Assert.Equal(1, blobs.ServiceTimeCalls);
    }

    [Fact]
    public async Task TableWideSweep_OnePartitionFails_OthersAreSwept_ThenAggregateException()
    {
        var (h, blobs) = TableRig();
        using var _ = h;
        var old = DateTimeOffset.UtcNow.AddHours(-1);
        var bad = ValueOffloadCoordinator.BlobName("SplitTextEntity", "bad", "r", "Body", Hash("bad"));
        var good1 = ValueOffloadCoordinator.BlobName("SplitTextEntity", "a", "r", "Body", Hash("a"));
        var good2 = ValueOffloadCoordinator.BlobName("SplitTextEntity", "z", "r", "Body", Hash("z"));
        foreach (var name in new[] { bad, good1, good2 })
            blobs.Inner.Seed(name, [1], Sha([1]), old);
        var failure = new InvalidOperationException("boom");
        blobs.BeforeDelete = name => name == bad ? Task.FromException(failure) : Task.CompletedTask;

        var ex = await Assert.ThrowsAsync<AggregateException>(() => h.Store.SweepOrphanBlobsAsync(TimeSpan.FromMinutes(5), Ct));

        // One inner exception per failed partition: its orphans that could not be deleted, together.
        var partition = Assert.IsType<AggregateException>(Assert.Single(ex.InnerExceptions));
        Assert.Same(failure, Assert.Single(partition.InnerExceptions));
        Assert.Contains("2", ex.Message, StringComparison.Ordinal); // the blobs deleted before giving up
        Assert.Equal([bad], blobs.Inner.Names);
    }

    [Fact]
    public async Task TableWideSweep_Cancellation_IsNotAggregated()
    {
        var (h, blobs) = TableRig();
        using var _ = h;
        blobs.Inner.Seed(ValueOffloadCoordinator.BlobName("SplitTextEntity", "p", "r", "Body", Hash("p")), [1], Sha([1]), DateTimeOffset.UtcNow.AddHours(-1));
        using var cts = new CancellationTokenSource();
        blobs.BeforeDelete = _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Store.SweepOrphanBlobsAsync(TimeSpan.FromMinutes(5), cts.Token));
    }

    // ── the capability interface ──────────────────────────────────────────

    [Fact]
    public void Stores_ImplementTheSweeperCapability_AndIStorageHasNoSweepMember()
    {
        Assert.True(typeof(IOrphanBlobSweeper).IsAssignableFrom(typeof(TableStorage<SplitTextEntity>)));
        Assert.True(typeof(IOrphanBlobSweeper).IsAssignableFrom(typeof(InMemoryStorage<SplitTextEntity>)));
        Assert.DoesNotContain(typeof(IStorage<SplitTextEntity>).GetMethods(), m => m.Name == "SweepOrphanBlobsAsync");
    }

    [Fact]
    public async Task Extension_OnIStorage_ForwardsToTheSweeper()
    {
        IStorage<SplitTextEntity> storage = new InMemoryStorage<SplitTextEntity>(null, Options());
        var inMemory = (InMemoryStorage<SplitTextEntity>)storage;
        var orphan = ValueOffloadCoordinator.BlobName("SplitTextEntity", "p", "r", "Body", Hash("p"));
        inMemory.Blobs.Seed(orphan, [1], Sha([1]), DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(0, await storage.SweepOrphanBlobsAsync("p", TimeSpan.FromDays(1), Ct));
        Assert.Equal(1, await storage.SweepOrphanBlobsAsync(TimeSpan.FromMinutes(5), Ct));
    }

    [Fact]
    public async Task Extension_OnAPlainIStorage_ThrowsNotSupported_ForBothOverloads()
    {
        var storage = Substitute.For<IStorage<SplitTextEntity>>();

        await Assert.ThrowsAsync<NotSupportedException>(() => storage.SweepOrphanBlobsAsync("p", TimeSpan.Zero, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => storage.SweepOrphanBlobsAsync(TimeSpan.Zero, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IStorage<SplitTextEntity>)null!).SweepOrphanBlobsAsync(TimeSpan.Zero, Ct));
    }
}
