using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A <see cref="BlobOffloadOptions.CleanupGrace"/> that reaches back beyond the start of the calendar
/// (<see cref="TimeSpan.MaxValue"/>: "never clean up after a write") must not overflow the cutoff
/// subtraction. The cleanup runs after the row change has committed, so an exception there reported a
/// committed write as failed. With such a grace no blob is old enough: nothing is deleted, no delete is
/// even attempted, and nothing throws.
/// </summary>
public class CleanupGraceOverflowTests
{
    private const string Pk = "p";

    private static readonly string Text = Ascii(40_000, seed: 71);
    private static readonly string NewText = Ascii(40_000, seed: 72);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Graces longer than the time since year 1: <see cref="TimeSpan.MaxValue"/> and ten thousand years.</summary>
    public static TheoryData<long> HugeGraceTicks => new() { TimeSpan.MaxValue.Ticks, TimeSpan.FromDays(3_650_000).Ticks };

    private static UnifiedTableStorageOptions Options(long graceTicks) => new()
    {
        BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000, CleanupGrace = TimeSpan.FromTicks(graceTicks) },
        Cache = CachePolicy.Disabled,
    };

    private static SplitTextEntity Row(string id, string body) => new() { Id = id, Body = body, Title = "t" };

    // ── InMemoryStorage ───────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(HugeGraceTicks))]
    public async Task InMemory_Upsert_CommitsAndKeepsTheReplacedBlob(long graceTicks)
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options(graceTicks));
        await store.CreateAsync(Row("p|r", Text), Ct);
        var first = Assert.Single(store.OffloadedBlobNames());

        await store.UpsertAsync(Row("p|r", NewText), Ct);

        Assert.Equal(NewText, (await store.OneAsync("p|r", Ct))!.Body, StringComparer.Ordinal);
        Assert.Contains(first, store.OffloadedBlobNames());
        Assert.Equal(2, store.OffloadedBlobNames().Count);
    }

    [Theory]
    [MemberData(nameof(HugeGraceTicks))]
    public async Task InMemory_UpsertBatch_CommitsAndKeepsTheReplacedBlob(long graceTicks)
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options(graceTicks));
        await store.CreateAsync(Row("p|r", Text), Ct);
        var first = Assert.Single(store.OffloadedBlobNames());

        Assert.Equal(2, await store.UpsertBatchAsync([Row("p|r", "small"), Row("p|s", "b")], Ct));

        Assert.Equal("small", (await store.OneAsync("p|r", Ct))!.Body);
        Assert.Equal([first], store.OffloadedBlobNames());
    }

    [Theory]
    [MemberData(nameof(HugeGraceTicks))]
    public async Task InMemory_Delete_CommitsAndKeepsTheBlob(long graceTicks)
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options(graceTicks));
        await store.CreateAsync(Row("p|r", Text), Ct);
        var blob = Assert.Single(store.OffloadedBlobNames());

        await store.DeleteAsync("p|r", Ct);

        Assert.Null(await store.OneAsync("p|r", Ct));
        Assert.Equal([blob], store.OffloadedBlobNames());
    }

    [Theory]
    [MemberData(nameof(HugeGraceTicks))]
    public async Task InMemory_DeletePartition_CommitsAndKeepsTheBlobs(long graceTicks)
    {
        var store = new InMemoryStorage<SplitTextEntity>(null, Options(graceTicks));
        await store.CreateAsync(Row("p|r", Text), Ct);
        await store.CreateAsync(Row("p|s", NewText), Ct);
        var blobs = store.OffloadedBlobNames();
        Assert.Equal(2, blobs.Count);

        Assert.Equal(2, await store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(0, store.Count);
        Assert.Equal(blobs, store.OffloadedBlobNames());
    }

    // ── TableStorage (mocked table) ────────────────────────────────────────

    private sealed class Rig : IDisposable
    {
        public Rig(long graceTicks, InMemoryOffloadBlobStore inner)
        {
            Blobs = new RecordingBlobStore([], inner);
            H = new StorageHarness<SplitTextEntity>(options: Options(graceTicks), blobs: Blobs);
            H.SetupUpdate("W/\"v2\"");
            H.SetupDelete();
            H.SetupTransaction();
        }

        public RecordingBlobStore Blobs { get; }

        public StorageHarness<SplitTextEntity> H { get; }

        public TableStorage<SplitTextEntity> Store => H.Store;

        public void Dispose() => H.Dispose();
    }

    /// <summary>The row a create writes, its blob left in <paramref name="inner"/> and aged by an hour.</summary>
    private static async Task<TableEntity> StoredRowAsync(InMemoryOffloadBlobStore inner, long graceTicks)
    {
        var written = new List<TableEntity>();
        using var h = new StorageHarness<SplitTextEntity>(options: Options(graceTicks), blobs: inner);
        h.Table.When(t => t.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>()))
            .Do(c => written.Add(c.Arg<TableEntity>()));
        h.SetupAdd("W/\"v1\"");
        await h.Store.CreateAsync(Row("p|r", Text), Ct);
        var row = Assert.Single(written);
        row.ETag = new ETag("W/\"v1\"");
        var name = Assert.Single(inner.Names);
        await using var stream = await inner.OpenReadAsync("Body", name, CancellationToken.None);
        var bytes = await ReadAllAsync(stream);
        inner.Seed(name, bytes, Sha(bytes), DateTimeOffset.UtcNow.AddHours(-1));
        return row;
    }

    [Theory]
    [MemberData(nameof(HugeGraceTicks))]
    public async Task TableStorage_Upsert_CommitsAndAttemptsNoBlobDelete(long graceTicks)
    {
        var inner = new InMemoryOffloadBlobStore();
        var prior = await StoredRowAsync(inner, graceTicks);
        using var rig = new Rig(graceTicks, inner);
        rig.H.SetupGet(Pk, "r", prior);

        var written = await rig.Store.UpsertAsync(Row("p|r", NewText), Ct);

        Assert.Equal("W/\"v2\"", written.ETag);
        Assert.Equal(0, rig.Blobs.Count("delete"));
        Assert.Equal(2, inner.Names.Count);
    }

    [Theory]
    [MemberData(nameof(HugeGraceTicks))]
    public async Task TableStorage_Upsert_WithACommitDate_CommitsAndAttemptsNoBlobDelete(long graceTicks)
    {
        // The cutoff is taken from the commit response's Date when it has one: the same subtraction.
        var inner = new InMemoryOffloadBlobStore();
        var prior = await StoredRowAsync(inner, graceTicks);
        using var rig = new Rig(graceTicks, inner);
        rig.H.SetupGet(Pk, "r", prior);
        rig.H.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns(new DatedResponse(DateTimeOffset.UtcNow));

        await rig.Store.UpsertAsync(Row("p|r", NewText), Ct);

        Assert.Equal(0, rig.Blobs.Count("delete"));
        Assert.Equal(0, rig.Blobs.ServiceTimeCalls);
        Assert.Equal(2, inner.Names.Count);
    }

    [Theory]
    [MemberData(nameof(HugeGraceTicks))]
    public async Task TableStorage_UpsertBatch_CommitsAndAttemptsNoBlobDelete(long graceTicks)
    {
        var inner = new InMemoryOffloadBlobStore();
        var prior = await StoredRowAsync(inner, graceTicks);
        using var rig = new Rig(graceTicks, inner);
        rig.H.SetupGet(Pk, "r", prior);

        Assert.Equal(1, await rig.Store.UpsertBatchAsync([Row("p|r", "small")], Ct));

        Assert.Equal(0, rig.Blobs.Count("delete"));
        Assert.Single(inner.Names);
    }

    [Theory]
    [MemberData(nameof(HugeGraceTicks))]
    public async Task TableStorage_Delete_CommitsAndAttemptsNoBlobDelete(long graceTicks)
    {
        var inner = new InMemoryOffloadBlobStore();
        var prior = await StoredRowAsync(inner, graceTicks);
        using var rig = new Rig(graceTicks, inner);
        rig.H.SetupGet(Pk, "r", prior);

        await rig.Store.DeleteAsync("p|r", Ct);

        await rig.H.Table.Received(1).DeleteEntityAsync(Pk, "r", Arg.Any<ETag>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, rig.Blobs.Count("delete"));
        Assert.Single(inner.Names);
    }

    [Theory]
    [MemberData(nameof(HugeGraceTicks))]
    public async Task TableStorage_DeletePartition_CommitsAndAttemptsNoBlobDelete(long graceTicks)
    {
        var inner = new InMemoryOffloadBlobStore();
        var prior = await StoredRowAsync(inner, graceTicks);
        using var rig = new Rig(graceTicks, inner);
        rig.H.SetupQueryByPartition(prior);

        Assert.Equal(1, await rig.Store.DeletePartitionAsync(Pk, Ct));

        Assert.Equal(0, rig.Blobs.Count("delete"));
        Assert.Single(inner.Names);
    }

    // ── the shared core, with an explicit service time ─────────────────────

    [Theory]
    [MemberData(nameof(HugeGraceTicks))]
    public async Task DeleteReleasedBlobs_WithAServiceTime_DeletesNothing(long graceTicks)
    {
        var blobs = new RecordingBlobStore([]);
        var table = new LargeValueTable("T", new BlobOffloadOptions { CleanupGrace = TimeSpan.FromTicks(graceTicks) }, blobs);
        var name = ValueOffloadCoordinator.BlobName("T", "p", "r", "Body", new string('a', 64));
        blobs.Inner.Seed(name, [1], Sha([1]), DateTimeOffset.UtcNow.AddYears(-50));
        var failures = new List<string>();

        await table.DeleteReleasedBlobsAsync("p", "r", [name], DateTimeOffset.UtcNow, onForeign: null, (path, _) => failures.Add(path), Ct);
        await table.DeleteReleasedBlobsAsync("p", "r", [name], serviceNow: null, onForeign: null, (path, _) => failures.Add(path), Ct);

        Assert.Equal([name], blobs.Inner.Names);
        Assert.Equal(0, blobs.Count("delete"));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ALongGraceThatDoesNotOverflow_StillDeletesWhatIsOlder()
    {
        // A century is a long grace, not an overflowing one: a blob older than that still goes.
        var blobs = new RecordingBlobStore([]);
        var table = new LargeValueTable("T", new BlobOffloadOptions { CleanupGrace = TimeSpan.FromDays(36_500) }, blobs);
        var ancient = ValueOffloadCoordinator.BlobName("T", "p", "r", "Body", new string('a', 64));
        var recent = ValueOffloadCoordinator.BlobName("T", "p", "r", "Other", new string('b', 64));
        blobs.Inner.Seed(ancient, [1], Sha([1]), DateTimeOffset.UtcNow.AddYears(-150));
        blobs.Inner.Seed(recent, [2], Sha([2]), DateTimeOffset.UtcNow.AddYears(-50));

        await table.DeleteReleasedBlobsAsync("p", "r", [ancient, recent], DateTimeOffset.UtcNow, onForeign: null, onFailure: null, Ct);

        Assert.Equal([recent], blobs.Inner.Names);
    }

    /// <summary>A response with a <c>Date</c> (and an <c>ETag</c>) header.</summary>
    private sealed class DatedResponse(DateTimeOffset date, string etag = "W/\"v2\"") : Response
    {
        public override int Status => 204;

        public override string ReasonPhrase => "No Content";

        public override Stream? ContentStream { get => null; set { } }

        public override string ClientRequestId { get => string.Empty; set { } }

        public override void Dispose()
        {
        }

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = name.ToUpperInvariant() switch
            {
                "DATE" => date.ToString("R", CultureInfo.InvariantCulture),
                "ETAG" => etag,
                _ => null,
            };
            return value is not null;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = TryGetHeader(name, out var value) ? [value] : null;
            return values is not null;
        }

        protected override bool ContainsHeader(string name) => TryGetHeader(name, out _);

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
    }
}

/// <summary><see cref="CleanupGraceOverflowTests"/> against a local Azurite and the real blob client.</summary>
public sealed class AzuriteCleanupGraceOverflowTests : AzuriteCleanupTestBase
{
    private static readonly string Text = Ascii(40_000, seed: 73);
    private static readonly string NewText = Ascii(40_000, seed: 74);

    [Fact]
    public async Task HugeGrace_WritesAndDeletesCommit_NoBlobIsDeleted_AndNoDeleteFails()
    {
        SkipUnlessAvailable();
        var logger = new ListLogger();
        var store = Store<SplitTextEntity>(TimeSpan.MaxValue, logger: logger);
        await store.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = Text }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = Id("s"), Body = Text }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = Id("t"), Body = Text }, Ct);
        await BlobsOlderThanAsync(TimeSpan.Zero);

        await store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = NewText }, Ct);
        await store.DeleteAsync(Id("s"), Ct);
        Assert.Equal(2, await store.DeletePartitionAsync(Pk, Ct));

        Assert.Empty(await RawRowsAsync(nameof(SplitTextEntity)));
        // The three created blobs and the replacement: nothing was deleted, and no delete was refused.
        Assert.Equal(4, (await BlobNamesAsync()).Count);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    private sealed class ListLogger : ILogger<TableStorage<SplitTextEntity>>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
