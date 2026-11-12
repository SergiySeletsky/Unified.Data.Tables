using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A selected property the row does not have comes back from the service as null (the storage emulator
/// omits it instead). The point read before every large-value write selects each <c>{column}__blob</c>
/// cell, so on the service a value that is not offloaded has a null reference cell: that is "no
/// reference", never a malformed one. A cell that holds something and does not parse is still malformed.
/// </summary>
public class NullBlobReferenceCellTests
{
    private const string Pk = "p";
    private const string Rk = "r";
    private const string Id = "p|r";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UnifiedTableStorageOptions Offload() => new()
    {
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 },
        Cache = CachePolicy.Disabled,
    };

    /// <summary>The stored row of a value that is not offloaded, as the service returns it for the prior-row projection.</summary>
    private static TableEntity PriorRowAsTheServiceReturnsIt() => new(Pk, Rk)
    {
        ETag = new ETag("W/\"v1\""),
        [SystemColumnNames.SplitParts] = null,
        [SystemColumnNames.SplitOf] = null,
        ["Body__blob"] = null,
    };

    // ── the shared core ───────────────────────────────────────────────────

    [Fact]
    public void BlobPaths_NullReferenceCell_IsNoReference_NotAMalformedOne()
    {
        var row = new TableEntity(Pk, Rk) { ["Body__blob"] = null, ["Title"] = "t" };
        var malformed = new List<string>();

        var paths = LargeValueTable.BlobPaths(row, (column, _) => malformed.Add(column));

        Assert.Empty(paths);
        Assert.Empty(malformed);
    }

    [Fact]
    public void BlobPaths_NullCellBesideARealReference_YieldsOnlyTheRealPath()
    {
        var reference = new BlobReference($"T/p/r/File/{new string('a', 64)}", new string('a', 64), 3, "image/png", "binary");
        var row = new TableEntity(Pk, Rk) { ["Body__blob"] = null, ["File__blob"] = reference.ToJson() };
        var malformed = new List<string>();

        var paths = LargeValueTable.BlobPaths(row, (column, _) => malformed.Add(column));

        Assert.Equal([reference.Path], paths);
        Assert.Empty(malformed);
    }

    public static TheoryData<object> UnreadableCells => new() { string.Empty, "{not json", """{"path":"x"}""", 42 };

    [Theory]
    [MemberData(nameof(UnreadableCells))]
    public void BlobPaths_NonNullCellThatDoesNotParse_IsStillMalformed(object cell)
    {
        var row = new TableEntity(Pk, Rk) { ["Body__blob"] = cell };
        var malformed = new List<(string Column, TableValueIntegrityException Error)>();

        var paths = LargeValueTable.BlobPaths(row, (column, ex) => malformed.Add((column, ex)));

        Assert.Empty(paths);
        var (column, error) = Assert.Single(malformed);
        Assert.Equal("Body", column);
        Assert.NotNull(error);
    }

    [Fact]
    public void References_NullReferenceCell_IsSkipped()
    {
        var reference = new BlobReference($"T/p/r/File/{new string('b', 64)}", new string('b', 64), 3, "image/png", "binary");
        var row = new TableEntity(Pk, Rk) { ["Body__blob"] = null, ["File__blob"] = reference.ToJson() };

        Assert.Equal([reference], ValueOffloadCoordinator.References(row));
        Assert.Empty(ValueOffloadCoordinator.References(new TableEntity(Pk, Rk) { ["Body__blob"] = null }));
    }

    [Theory]
    [MemberData(nameof(UnreadableCells))]
    public void References_NonNullCellThatDoesNotParse_StillThrows(object cell)
    {
        var row = new TableEntity(Pk, Rk) { ["Body__blob"] = cell };

        Assert.Throws<TableValueIntegrityException>(() => ValueOffloadCoordinator.References(row));
    }

    // ── TableStorage: no warning on a write over a row that is not offloaded ──

    [Fact]
    public async Task Upsert_OverARowWhoseReferenceCellsComeBackNull_LogsNoWarning()
    {
        var logger = new ListLogger<TableStorage<SplitTextEntity>>();
        using var h = new StorageHarness<SplitTextEntity>(options: Offload(), logger: logger, blobs: new InMemoryOffloadBlobStore());
        h.SetupGet(Pk, Rk, PriorRowAsTheServiceReturnsIt());
        h.SetupUpdate("W/\"v2\"");

        await h.Store.UpsertAsync(new SplitTextEntity { Id = Id, Body = "small", Title = "t" }, Ct);

        await h.Table.Received(1).UpdateEntityAsync(Arg.Any<TableEntity>(), new ETag("W/\"v1\""), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task UpdateDeleteAndBatch_OverSuchARow_LogNoWarning()
    {
        var logger = new ListLogger<TableStorage<SplitTextEntity>>();
        using var h = new StorageHarness<SplitTextEntity>(options: Offload(), logger: logger, blobs: new InMemoryOffloadBlobStore());
        h.SetupGet(Pk, Rk, PriorRowAsTheServiceReturnsIt());
        h.SetupUpdate("W/\"v2\"");
        h.SetupDelete();
        h.SetupTransaction();

        await h.Store.UpdateAsync(new SplitTextEntity { Id = Id, Body = "small", ETag = "W/\"v1\"" }, Ct);
        await h.Store.UpsertBatchAsync([new SplitTextEntity { Id = Id, Body = "other" }], Ct);
        await h.Store.DeleteAsync(Id, Ct);

        await h.Table.Received(1).DeleteEntityAsync(Pk, Rk, new ETag("W/\"v1\""), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Upsert_OverARowWithAnUnreadableReferenceCell_StillWarns_AndStillWrites()
    {
        var logger = new ListLogger<TableStorage<SplitTextEntity>>();
        using var h = new StorageHarness<SplitTextEntity>(options: Offload(), logger: logger, blobs: new InMemoryOffloadBlobStore());
        var prior = PriorRowAsTheServiceReturnsIt();
        prior["Body__blob"] = "{not json";
        h.SetupGet(Pk, Rk, prior);
        h.SetupUpdate("W/\"v2\"");

        await h.Store.UpsertAsync(new SplitTextEntity { Id = Id, Body = "small" }, Ct);

        await h.Table.Received(1).UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("malformed blob reference", warning.Message, StringComparison.Ordinal);
        Assert.IsType<TableValueIntegrityException>(warning.Error);
    }

    // ── InMemoryStorage: the same row is written over without an error ─────

    [Fact]
    public async Task InMemory_Upsert_OverARowWithANullReferenceCell_ReplacesIt()
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Offload());
        store.SeedPhysicalRow(new TableEntity(Pk, Rk) { ["Body__blob"] = null, ["Title"] = "old" });
        var text = Ascii(40_000);

        await store.UpsertAsync(new SplitTextEntity { Id = Id, Body = text, Title = "new" }, Ct);

        var read = await store.OneAsync(Id, Ct);
        Assert.Equal(text, read!.Body, StringComparer.Ordinal);
        Assert.Equal("new", read.Title);
        Assert.Single(store.OffloadedBlobNames());
    }

    /// <summary>Records what the store logs.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
