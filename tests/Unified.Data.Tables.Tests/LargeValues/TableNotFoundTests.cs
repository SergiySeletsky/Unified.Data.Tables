using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// A split-aware write retries when the row it read is gone by the time it writes (another writer
/// deleted it): that is a lost race. The service answers 404 for a missing table too
/// (<c>TableNotFound</c>). Reading again cannot cure that, and reporting it as
/// <see cref="ConcurrencyConflictException"/> after five attempts hid the cause: only a missing row is a
/// lost race, any other 404 surfaces as the service's own error.
/// </summary>
public class TableNotFoundTests
{
    private static readonly string TwoRowText = new('a', 600_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RequestFailedException NotFound(string? code) =>
        new(404, code == "TableNotFound" ? "The table specified does not exist." : "The specified resource does not exist.", code, null);

    private static TableTransactionFailedException TxNotFound(string? code, int index)
    {
        var inner = NotFound(code);
        inner.Data["FailedEntity"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new TableTransactionFailedException(inner);
    }

    private static TableEntity PriorPrimary(string rk, int parts, string etag)
    {
        var row = new TableEntity("p", rk) { ETag = new ETag(etag) };
        if (parts > 0) row[SystemColumnNames.SplitParts] = parts;
        return row;
    }

    private static List<TimeSpan> RecordWaits(StorageHarness<SplitTextEntity> h)
    {
        var waits = new List<TimeSpan>();
        h.Store.LargeValueRetryDelay = (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        };
        return waits;
    }

    // ── the table is gone ──────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_Insert_TableNotFound_SurfacesTheServicesError_AfterOneAttempt()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", null);
        var failure = NotFound("TableNotFound");
        h.Table.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>()).Returns<Response>(_ => throw failure);
        var waits = RecordWaits(h);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct));

        Assert.Same(failure, ex);
        Assert.Equal("TableNotFound", ex.ErrorCode);
        await h.Table.ReceivedWithAnyArgs(1).AddEntityAsync<TableEntity>(default!, default);
        Assert.Empty(waits);
    }

    [Theory]
    [InlineData("upsert")]
    [InlineData("update")]
    public async Task Replace_TableNotFound_SurfacesTheServicesError_AfterOneAttempt(string operation)
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"v1\""));
        var failure = NotFound("TableNotFound");
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw failure);
        var waits = RecordWaits(h);
        var entity = new SplitTextEntity { Id = "p|r", Body = "small" };

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => operation == "upsert"
            ? h.Store.UpsertAsync(entity, Ct)
            : h.Store.UpdateAsync(entity, ConcurrencyMode.LastWriterWins, Ct));

        Assert.Same(failure, ex);
        await h.Table.ReceivedWithAnyArgs(1).UpdateEntityAsync<TableEntity>(default!, default, default, default);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Upsert_NeedingATransaction_TableNotFound_SurfacesTheServicesError_AfterOneAttempt()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"v1\""));
        var failure = TxNotFound("TableNotFound", index: 0);
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw failure);

        var ex = await Assert.ThrowsAsync<TableTransactionFailedException>(
            () => h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = TwoRowText }, Ct));

        Assert.Same(failure, ex);
        await h.Table.ReceivedWithAnyArgs(1).SubmitTransactionAsync(default!, default);
    }

    [Fact]
    public async Task UpsertBatch_TableNotFound_SurfacesTheServicesError_AfterOneTransaction()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet(null);
        var failure = TxNotFound("TableNotFound", index: 0);
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw failure);
        var waits = RecordWaits(h);

        var ex = await Assert.ThrowsAsync<TableTransactionFailedException>(() => h.Store.UpsertBatchAsync(
        [
            new SplitTextEntity { Id = "p|r1", Body = "a" },
            new SplitTextEntity { Id = "p|r2", Body = "b" },
        ], Ct));

        Assert.Same(failure, ex);
        await h.Table.ReceivedWithAnyArgs(1).SubmitTransactionAsync(default!, default);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task A404ThatNamesSomethingElse_IsNotALostRaceEither()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("r", 0, "W/\"v1\""));
        var failure = NotFound("SomeOtherResourceNotFound");
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw failure);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct));

        Assert.Same(failure, ex);
        await h.Table.ReceivedWithAnyArgs(1).UpdateEntityAsync<TableEntity>(default!, default, default, default);
    }

    // ── the row is gone: still a lost race ─────────────────────────────────

    [Theory]
    [InlineData("ResourceNotFound")]
    [InlineData("EntityNotFound")]
    [InlineData(null)]
    public async Task Upsert_RowDeletedBetweenTheReadAndTheReplace_IsReadAgain_AndInserted(string? code)
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var stored = Mocks.Found(PriorPrimary("r", 0, "W/\"v1\""));
        var gone = Mocks.NotFound();
        h.Table.GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(stored, gone);
        var failure = NotFound(code);
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns<Response>(_ => throw failure);
        h.SetupAdd("W/\"inserted\"");
        var waits = RecordWaits(h);

        var written = await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct);

        Assert.Equal("W/\"inserted\"", written.ETag);
        await h.Table.ReceivedWithAnyArgs(1).AddEntityAsync<TableEntity>(default!, default);
        Assert.Single(waits);
    }

    [Fact]
    public async Task UpsertBatch_StaleContinuationRowAlreadyGone_IsStillPlannedAgain()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var withPart = Mocks.Found(PriorPrimary("r1", 1, "W/\"v1\""));
        var withoutParts = Mocks.Found(PriorPrimary("r1", 0, "W/\"v2\""));
        h.Table.GetEntityIfExistsAsync<TableEntity>("p", "r1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(withPart, withoutParts);
        h.SetupQueryByFilter(new TableEntity("p", "r1~0001") { ETag = new ETag("W/\"part\""), [SystemColumnNames.SplitOf] = "r1" });
        var failure = TxNotFound("ResourceNotFound", index: 1);
        var ok = Substitute.For<Response<IReadOnlyList<Response>>>();
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw failure, _ => ok);
        RecordWaits(h);

        await h.Store.UpsertBatchAsync([new SplitTextEntity { Id = "p|r1", Body = "small" }], Ct);

        await h.Table.ReceivedWithAnyArgs(2).SubmitTransactionAsync(default!, default);
    }

    // ── what the real SDK reports (scripted transport) ─────────────────────

    private static string Error(string code) => "{\"odata.error\":{\"code\":\"" + code + "\",\"message\":{\"lang\":\"en-US\",\"value\":\"" + code + "\"}}}";

    [Fact]
    public async Task OnTheWire_TableNotFound_IsOnePut_AndTheSdksOwnException()
    {
        var transport = new ScriptedTableTransport(sent => sent.Method switch
        {
            "GET" => new ScriptedTableTransport.Answer(200, "{\"odata.etag\":\"W/\\\"v1\\\"\"}"),
            _ => new ScriptedTableTransport.Answer(404, Error("TableNotFound"), ErrorCode: "TableNotFound"),
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TableStorage<SplitTextEntity>(transport.Service(), cache, NullLogger<TableStorage<SplitTextEntity>>.Instance);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct));

        Assert.Equal(404, ex.Status);
        Assert.Equal("TableNotFound", ex.ErrorCode);
        Assert.Single(transport.EntityRequests, r => r.Method == "PUT");
        Assert.Single(transport.EntityRequests, r => r.Method == "GET");
    }

    [Fact]
    public async Task OnTheWire_ResourceNotFound_IsReadAgain_AndInserted()
    {
        var reads = 0;
        var transport = new ScriptedTableTransport(sent => sent.Method switch
        {
            "GET" => ++reads == 1
                ? new ScriptedTableTransport.Answer(200, "{\"odata.etag\":\"W/\\\"v1\\\"\"}")
                : new ScriptedTableTransport.Answer(404, Error("ResourceNotFound"), ErrorCode: "ResourceNotFound"),
            "PUT" => new ScriptedTableTransport.Answer(404, Error("ResourceNotFound"), ErrorCode: "ResourceNotFound"),
            _ => new ScriptedTableTransport.Answer(204, ETag: "W/\"inserted\""),
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TableStorage<SplitTextEntity>(transport.Service(), cache, NullLogger<TableStorage<SplitTextEntity>>.Instance);
        store.LargeValueRetryDelay = (_, _) => Task.CompletedTask;

        var written = await store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct);

        Assert.Equal("W/\"inserted\"", written.ETag);
        Assert.Equal(2, reads);
    }
}

/// <summary>
/// On a local Azurite: a store whose table was deleted under it (the store creates its table once).
/// The table is this test's own, created and deleted here.
/// </summary>
public sealed class AzuriteTableNotFoundTests : AzuriteCleanupTestBase
{
    private readonly string tableName = "B2wGone" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task TableDeletedUnderTheStore_AnUpsertReportsTableNotFound_NotAConcurrencyConflict()
    {
        SkipUnlessAvailable();
        var store = Store<SplitTextEntity>(table: tableName);
        await store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = "one" }, Ct);
        await TableService.DeleteTableAsync(tableName, Ct);

        try
        {
            var small = await Assert.ThrowsAsync<RequestFailedException>(
                () => store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = "two" }, Ct));
            Assert.Equal(404, small.Status);
            Assert.Equal("TableNotFound", small.ErrorCode);

            // A value with a continuation row goes through a transaction: the same answer.
            var large = await Assert.ThrowsAnyAsync<RequestFailedException>(
                () => store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = new string('a', 600_000) }, Ct));
            Assert.Equal(404, large.Status);
            Assert.Equal("TableNotFound", large.ErrorCode);

            var batch = await Assert.ThrowsAnyAsync<RequestFailedException>(
                () => store.UpsertBatchAsync([new SplitTextEntity { Id = Id("r"), Body = "three" }], Ct));
            Assert.Equal("TableNotFound", batch.ErrorCode);

            // A delete of a row in a table that is not there is a no-op, as for a type without large values.
            await store.DeleteAsync(Id("r"), Ct);
        }
        finally
        {
            try
            {
                await TableService.DeleteTableAsync(tableName, CancellationToken.None);
            }
            catch (RequestFailedException)
            {
                // Already deleted above.
            }
        }
    }
}
