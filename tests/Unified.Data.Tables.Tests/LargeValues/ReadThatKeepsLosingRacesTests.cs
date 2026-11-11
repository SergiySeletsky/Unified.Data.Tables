using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Providers;
using Unified.Data.Tables.Tests.TestSupport;
using static Unified.Data.Tables.Tests.LargeValues.OffloadTestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A split-aware read whose row is replaced by a writer during each of its attempts (CACHE-5, the same
/// defect as COMPLETENESS-3; ruling R5). The read gets as many attempts as a write
/// (<see cref="LargeValueTable.MaxWriteAttempts"/>) with the writers' pause before each retry, and then
/// throws <see cref="ConcurrencyConflictException"/>: an intact row is never reported as
/// <see cref="TableValueIntegrityException"/>, which only a row whose version did not change during the
/// failed attempt gets, on the last attempt too.
/// </summary>
public class ReadThatKeepsLosingRacesTests
{
    private const string Id = "p|a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableStorage<RaceDoc> NewStore(RacingTableClient table, IMemoryCache cache, List<TimeSpan>? pauses = null)
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(typeof(RaceDoc).Name).Returns(table);
        var store = new TableStorage<RaceDoc>(service, cache, NullLogger<TableStorage<RaceDoc>>.Instance);
        if (pauses is not null)
        {
            store.LargeValueRetryDelay = (pause, _) =>
            {
                lock (pauses)
                    pauses.Add(pause);
                return Task.CompletedTask;
            };
        }
        return store;
    }

    private static RaceDoc RowsDoc(int version) => new() { Id = Id, Body = new string((char)('a' + version), 600_000), Version = version };

    private static RaceDoc BlobDoc(int version) => new() { Id = Id, Body = Ascii(40_000, seed: version), Version = version };

    private static void AssertTheRowKeptChanging(Exception? failure, string partitionKey = "p", string rowKey = "a")
    {
        var conflict = Assert.IsType<ConcurrencyConflictException>(failure);
        Assert.Equal(ConcurrencyMessages.ReadKeptChanging(nameof(RaceDoc), partitionKey, rowKey, LargeValueTable.MaxWriteAttempts), conflict.Message);
        Assert.Contains("changed during each of its 5 attempts", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("can be repeated", conflict.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("updating", conflict.Message, StringComparison.Ordinal);
        Assert.IsType<TableValueIntegrityException>(conflict.InnerException);
    }

    // CACHE-5 (reproduction of the review, renamed). A write of a new version commits between the reader's
    // fetch of the main row and its query of the continuation rows, for the first `racedReads` whole-row
    // reads made for the reader (its own fetch and each re-read after a failed assembly). The row is whole
    // the entire time. Three lost races: the read succeeds on its fourth attempt. Twelve: every one of the
    // five attempts is overtaken, and the read says so with a ConcurrencyConflictException.
    [Theory]
    [InlineData("one", 3)]
    [InlineData("query", 3)]
    [InlineData("page", 3)]
    [InlineData("stream", 3)]
    [InlineData("one", 12)]
    [InlineData("query", 12)]
    [InlineData("page", 12)]
    [InlineData("stream", 12)]
    public async Task SplitRead_ThatKeepsLosingTheRaceToAWriter_IsRetriedWithPauses_AndNeverReportedAsCorrupt(string read, int racedReads)
    {
        var table = new RacingTableClient();
        using var readerCache = new MemoryCache(new MemoryCacheOptions());
        using var writerCache = new MemoryCache(new MemoryCacheOptions());
        var pauses = new List<TimeSpan>();
        var reader = NewStore(table, readerCache, pauses);
        var writer = NewStore(table, writerCache);
        await writer.UpsertAsync(RowsDoc(1), Ct);

        var version = 1;
        var writing = false;
        table.ResponseInFlight = async request =>
        {
            if (writing || version > racedReads || !(request.IsWholeRowPointRead || request.IsEntityQuery))
                return;
            writing = true;
            try
            {
                await writer.UpsertAsync(RowsDoc(++version), Ct);
            }
            finally
            {
                writing = false;
            }
        };

        int? returned = null;
        var failure = await Record.ExceptionAsync(async () =>
        {
            switch (read)
            {
                case "one":
                    returned = (await reader.OneAsync(Id, Ct))?.Version;
                    break;
                case "query":
                    returned = (await reader.QueryAsync("p", Ct)).SingleOrDefault()?.Version;
                    break;
                case "page":
                    returned = (await reader.QueryPageAsync(new QueryOptions { Partition = "p" }, Ct)).Items.SingleOrDefault()?.Version;
                    break;
                default:
                    await foreach (var entity in reader.QueryStreamAsync(x => x.Version > 0, "p", take: null, Ct))
                        returned = entity.Version;
                    break;
            }
        });
        table.ResponseInFlight = null;

        // Nothing is corrupt: the row reads back whole, as the last version written.
        Assert.Equal(version, (await reader.OneAsync(Id, Ct))!.Version);
        Assert.False(failure is TableValueIntegrityException, $"an intact row was reported as corrupt: {failure?.Message}");
        if (racedReads == 3)
        {
            // Three lost races, then the fourth attempt reads the fourth version whole.
            Assert.Null(failure);
            Assert.Equal(4, version);
            Assert.Equal(4, returned);
            Assert.Equal(3, pauses.Count);
        }
        else
        {
            // Five attempts and their re-reads (six whole-row reads, each overtaken), four pauses, then the
            // read gives up: bounded, and with an error that says the row kept changing.
            AssertTheRowKeptChanging(failure);
            Assert.Equal(1 + LargeValueTable.MaxWriteAttempts + 1, version);
            Assert.Equal(LargeValueTable.MaxWriteAttempts - 1, pauses.Count);
            Assert.Null(returned);
        }
        Assert.All(pauses, pause => Assert.InRange(pause, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(250)));
    }

    // CACHE-5 on the in-memory store (rule 3): a replace commits while the reader is about to open the
    // blob of the version it holds, and its cleanup deletes that blob. The same attempts, the same pause,
    // the same exception.
    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    public async Task InMemory_BlobRead_ThatKeepsLosingTheRaceToAWriter_IsRetriedWithPauses_AndNeverReportedAsCorrupt(int racedReads)
    {
        var store = new InMemoryStorage<RaceDoc>(options: new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 },
        });
        var pauses = new List<TimeSpan>();
        store.LargeValueRules.RetryDelay = (pause, _) =>
        {
            pauses.Add(pause);
            return Task.CompletedTask;
        };
        await store.CreateAsync(BlobDoc(1), Ct);

        var version = 1;
        var writing = false;
        store.Blobs.BeforeOpenRead = async (name, ct) =>
        {
            if (writing || version > racedReads)
                return;
            writing = true;
            try
            {
                await store.UpsertAsync(BlobDoc(++version), ct);
                store.Blobs.Remove(name);
            }
            finally
            {
                writing = false;
            }
        };

        int? returned = null;
        var failure = await Record.ExceptionAsync(async () => returned = (await store.OneAsync(Id, Ct))?.Version);
        store.Blobs.BeforeOpenRead = null;

        Assert.Equal(version, (await store.OneAsync(Id, Ct))!.Version);
        Assert.False(failure is TableValueIntegrityException, $"an intact row was reported as corrupt: {failure?.Message}");
        if (racedReads == 3)
        {
            Assert.Null(failure);
            Assert.Equal(4, returned);
            Assert.Equal(3, pauses.Count);
        }
        else
        {
            AssertTheRowKeptChanging(failure);
            Assert.Equal(1 + LargeValueTable.MaxWriteAttempts, version);
            Assert.Equal(LargeValueTable.MaxWriteAttempts - 1, pauses.Count);
        }
    }

    // ── the version check is made on the last attempt too (scripted primary reads) ──────────────────────

    private const string Pk = "p";
    private const string Rk = "r";

    private static List<TableEntity> SplitRows(char fill, string etag)
    {
        var entity = new SplitTextEntity { Id = Pk + "|" + Rk, Body = new string(fill, 600_000), Title = etag };
        var logical = TableEntitySerializer.ToTableEntity(entity, Pk, Rk, ValueWriteContext.Public, []);
        var plan = SplitRowLayout.Plan(logical);
        Assert.NotEmpty(plan.Parts);
        plan.Primary.ETag = new ETag(etag);
        var rows = new List<TableEntity> { plan.Primary };
        rows.AddRange(plan.Parts);
        return rows;
    }

    private static StorageHarness<SplitTextEntity> Harness(List<TableEntity?> primaryReads, IEnumerable<TableEntity> parts, Func<TimeSpan, CancellationToken, Task>? pause = null)
    {
        var h = new StorageHarness<SplitTextEntity>();
        var responses = primaryReads.Select(v => v is null ? Mocks.NotFound() : Mocks.Found(v)).ToArray();
        h.Table.GetEntityIfExistsAsync<TableEntity>(Pk, Rk, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(responses[0], responses[1..]);
        var partRows = parts.ToList();
        h.Table.QueryAsync<TableEntity>(
                Arg.Is<string?>(f => f != null && f.Contains("_SplitOf eq", StringComparison.Ordinal)),
                Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable([.. partRows]));
        h.Store.LargeValueRetryDelay = pause ?? ((_, _) => Task.CompletedTask);
        return h;
    }

    private static List<TableEntity?> Primaries(params string[] versions) =>
        [.. versions.Select((v, i) => (TableEntity?)SplitRows((char)('a' + i), v)[0])];

    [Fact]
    public async Task VersionThatStopsChangingBeforeTheLastAttempt_IsReportedAsAnIntegrityFailure()
    {
        // The parts never match the primary. The version changes after attempts 1 to 4; after attempt 5 the
        // re-read finds the version that attempt read: a genuine integrity failure, not a lost race.
        var reads = Primaries("W/\"v1\"", "W/\"v2\"", "W/\"v3\"", "W/\"v4\"", "W/\"v5\"");
        reads.Add(reads[^1]);
        using var h = Harness(reads, SplitRows('z', "W/\"other\"").Skip(1));

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => h.Store.OneAsync(Pk + "|" + Rk, Ct));
        await h.Table.Received(6).GetEntityIfExistsAsync<TableEntity>(Pk, Rk, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VersionThatChangesAfterEveryAttempt_IsAConcurrencyConflict_AfterFiveAttemptsAndFourPauses()
    {
        var reads = Primaries("W/\"v1\"", "W/\"v2\"", "W/\"v3\"", "W/\"v4\"", "W/\"v5\"", "W/\"v6\"");
        var pauses = new List<TimeSpan>();
        using var h = Harness(reads, SplitRows('z', "W/\"other\"").Skip(1), (pause, _) =>
        {
            pauses.Add(pause);
            return Task.CompletedTask;
        });

        var failure = await Record.ExceptionAsync(() => h.Store.OneAsync(Pk + "|" + Rk, Ct));

        var conflict = Assert.IsType<ConcurrencyConflictException>(failure);
        Assert.Equal(ConcurrencyMessages.ReadKeptChanging(nameof(SplitTextEntity), Pk, Rk, 5), conflict.Message);
        Assert.IsType<TableValueIntegrityException>(conflict.InnerException);
        await h.Table.Received(6).GetEntityIfExistsAsync<TableEntity>(Pk, Rk, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        Assert.Equal(4, pauses.Count);
    }

    [Fact]
    public async Task RowDeletedDuringTheLastAttempt_ReadsAsAbsent()
    {
        var reads = Primaries("W/\"v1\"", "W/\"v2\"", "W/\"v3\"", "W/\"v4\"", "W/\"v5\"");
        reads.Add(null);
        using var h = Harness(reads, SplitRows('z', "W/\"other\"").Skip(1));

        Assert.Null(await h.Store.OneAsync(Pk + "|" + Rk, Ct));
    }

    [Fact]
    public async Task TheWritersPause_HonoursTheCallersCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reads = Primaries("W/\"v1\"", "W/\"v2\"", "W/\"v3\"");
        using var h = Harness(reads, SplitRows('z', "W/\"other\"").Skip(1), (pause, ct) =>
        {
            // The pause is given the caller's token: cancelling it ends the wait, and the read.
            cts.Cancel();
            return Task.Delay(pause, ct);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Store.OneAsync(Pk + "|" + Rk, cts.Token));
        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>(Pk, Rk, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InMemory_TheWritersPause_HonoursTheCallersCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var store = new InMemoryStorage<RaceDoc>(options: new UnifiedTableStorageOptions
        {
            BlobOffload = new BlobOffloadOptions { ThresholdBytes = 1_000 },
        });
        store.LargeValueRules.RetryDelay = (pause, ct) =>
        {
            cts.Cancel();
            return Task.Delay(pause, ct);
        };
        await store.CreateAsync(BlobDoc(1), Ct);
        var version = 1;
        store.Blobs.BeforeOpenRead = async (name, ct) =>
        {
            if (version > 1)
                return;
            await store.UpsertAsync(BlobDoc(++version), Ct);
            store.Blobs.Remove(name);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OneAsync(Id, cts.Token));
        store.Blobs.BeforeOpenRead = null;
        Assert.Equal(2, (await store.OneAsync(Id, Ct))!.Version);
    }
}
