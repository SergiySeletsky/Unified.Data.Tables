using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The point read a split-aware upsert, update, delete and batch upsert make of the stored row. Its
/// <c>$select</c> never names <c>odata.etag</c>: that token is not a column (the service returns the
/// version with every entity, and a real account may refuse a select that names it). The version comes
/// from the entity, else from the response's <c>ETag</c> header; when neither has one the write is
/// refused, because a replace or delete without a version would be unconditional.
/// </summary>
public class PriorRowVersionTests
{
    private const string Stored = "W/\"stored\"";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Operations => ["upsert", "update", "delete", "batch"];

    private static SplitTextEntity Small(string? etag = null) => new() { Id = "p|r", Body = "small", ETag = etag };

    private static Task RunAsync(string operation, IStorage<SplitTextEntity> store) => operation switch
    {
        "upsert" => store.UpsertAsync(Small(), Ct),
        "update" => store.UpdateAsync(Small(), ConcurrencyMode.LastWriterWins, Ct),
        "delete" => store.DeleteAsync("p|r", Ct),
        "batch" => store.UpsertBatchAsync([Small()], Ct),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    // ── what the store asks the SDK for ────────────────────────────────────

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task PriorRowRead_DoesNotSelectTheEtagToken(string operation)
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var selects = new List<List<string>>();
        var stored = Mocks.Found(new TableEntity("p", "r") { ETag = new ETag(Stored) });
        h.Table.GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Do<IEnumerable<string>>(s => selects.Add([.. s])), Arg.Any<CancellationToken>())
            .Returns(stored);
        h.SetupUpdate();
        h.SetupDelete();
        h.SetupTransaction();

        await RunAsync(operation, h.Store);

        // The stored row's part count, whether it is a continuation row, and its blob references: nothing else.
        var select = Assert.Single(selects);
        Assert.Equal([SystemColumnNames.SplitParts, SystemColumnNames.SplitOf, "Body__blob"], select);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task StoredRowWithoutAVersion_IsNotWrittenUnconditionally(string operation)
    {
        using var h = new StorageHarness<SplitTextEntity>();
        // A row that carries no ETag, read through a response that has none either.
        h.SetupGet("p", "r", new TableEntity("p", "r"));
        h.SetupUpdate();
        h.SetupDelete();
        h.SetupTransaction();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(operation, h.Store));

        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p|r", ex.Message, StringComparison.Ordinal);
        await h.Table.DidNotReceiveWithAnyArgs().UpdateEntityAsync<TableEntity>(default!, default, default, default);
        await h.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
        await h.Table.DidNotReceiveWithAnyArgs().AddEntityAsync<TableEntity>(default!, default);
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
    }

    [Fact]
    public async Task StoredRowWithoutAVersion_ALargeValueIsNotWrittenInAnUnconditionalTransaction()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", new TableEntity("p", "r"));
        h.SetupTransaction();

        // Two rows: the write is a transaction, where the SDK sends a missing condition as "If-Match: *".
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = new string('a', 600_000) }, Ct));

        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
    }

    // ── what goes on the wire (the real SDK over a scripted transport) ─────

    private static TableStorage<SplitTextEntity> StoreOver(ScriptedTableTransport transport, MemoryCache cache) =>
        new(transport.Service(), cache, NullLogger<TableStorage<SplitTextEntity>>.Instance);

    private static string Row(string? etag) =>
        etag is null ? "{}" : "{\"odata.etag\":" + System.Text.Json.JsonSerializer.Serialize(etag) + "}";

    [Theory]
    [InlineData("upsert", "PUT")]
    [InlineData("update", "PUT")]
    [InlineData("delete", "DELETE")]
    public async Task OnTheWire_TheSelectHasNoEtagToken_AndTheWriteIsConditionalOnTheRowsVersion(string operation, string method)
    {
        var transport = new ScriptedTableTransport(sent => sent.Method == "GET"
            ? new ScriptedTableTransport.Answer(200, Row(Stored))
            : new ScriptedTableTransport.Answer(204, ETag: "W/\"new\""));
        using var cache = new MemoryCache(new MemoryCacheOptions());

        await RunAsync(operation, StoreOver(transport, cache));

        var read = Assert.Single(transport.EntityRequests, r => r.Method == "GET");
        Assert.Equal("_SplitParts,_SplitOf,Body__blob", read.Select);
        var write = Assert.Single(transport.EntityRequests, r => r.Method != "GET");
        Assert.Equal(method, write.Method);
        Assert.Equal(Stored, write.IfMatch);
    }

    [Theory]
    [InlineData("upsert")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task OnTheWire_PayloadWithoutAVersion_TheResponsesETagHeaderIsTheVersion(string operation)
    {
        var transport = new ScriptedTableTransport(sent => sent.Method == "GET"
            ? new ScriptedTableTransport.Answer(200, Row(null), ETag: "W/\"header\"")
            : new ScriptedTableTransport.Answer(204, ETag: "W/\"new\""));
        using var cache = new MemoryCache(new MemoryCacheOptions());

        await RunAsync(operation, StoreOver(transport, cache));

        var write = Assert.Single(transport.EntityRequests, r => r.Method != "GET");
        Assert.Equal("W/\"header\"", write.IfMatch);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task OnTheWire_NoVersionAnywhere_Throws_AndSendsNoWrite(string operation)
    {
        var transport = new ScriptedTableTransport(sent => sent.Method == "GET"
            ? new ScriptedTableTransport.Answer(200, Row(null))
            : new ScriptedTableTransport.Answer(204, ETag: "W/\"new\""));
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(operation, StoreOver(transport, cache)));

        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
        List<ScriptedTableTransport.Sent> sent;
        lock (transport.Requests) sent = [.. transport.Requests];
        // The table creation and the point read: no PUT, DELETE, insert or transaction.
        Assert.All(sent, r => Assert.True(r.Method == "GET" || r.Path.EndsWith("/Tables", StringComparison.Ordinal), r.Method + " " + r.Path));
    }

    [Fact]
    public async Task OnTheWire_AMissingRow_IsStillAnInsert()
    {
        var transport = new ScriptedTableTransport(sent => sent.Method == "GET"
            ? new ScriptedTableTransport.Answer(404, "{\"odata.error\":{\"code\":\"ResourceNotFound\"}}", ErrorCode: "ResourceNotFound")
            : new ScriptedTableTransport.Answer(204, ETag: "W/\"new\""));
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var written = await StoreOver(transport, cache).UpsertAsync(Small(), Ct);

        Assert.Equal("W/\"new\"", written.ETag);
        List<ScriptedTableTransport.Sent> sent;
        lock (transport.Requests) sent = [.. transport.Requests];
        var insert = Assert.Single(sent, r => r.Method == "POST" && !r.Path.EndsWith("/Tables", StringComparison.Ordinal));
        Assert.Null(insert.IfMatch);
    }
}

/// <summary>
/// The same on a local Azurite: every split-aware path that reads the stored row first still works
/// (upsert twice, update, delete, batch upsert, with and without continuation rows), and none of its
/// requests names <c>odata.etag</c>.
/// </summary>
public sealed class AzuritePriorRowVersionTests : AzuriteCleanupTestBase
{
    private static readonly string TwoRows = new('a', 600_000);

    private readonly List<string> urls = [];

    private TableStorage<SplitTextEntity> RecordedStore() =>
        SplitOnlyStore<SplitTextEntity>(TableServiceWith(new UrlRecorder(urls)));

    private void AssertNoEtagTokenWasSent()
    {
        List<string> sent;
        lock (urls) sent = [.. urls];
        Assert.Contains(sent, u => u.Contains("$select=", StringComparison.Ordinal) && u.Contains(SystemColumnNames.SplitParts, StringComparison.Ordinal));
        Assert.DoesNotContain(sent, u => Uri.UnescapeDataString(u).Contains("odata.etag", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpsertTwice_Update_Delete_StillWork_WithoutTheEtagToken()
    {
        SkipUnlessAvailable();
        var store = RecordedStore();

        var first = await store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = "one", Title = "t1" }, Ct);
        var firstVersion = first.ETag;
        var second = await store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = "two", Title = "t2" }, Ct);
        var secondVersion = second.ETag;
        Assert.NotEqual(firstVersion, secondVersion);
        Assert.Equal("two", (await store.OneAsync(Id("r"), Ct))!.Body);

        // The round-tripped version is the stored one: an optimistic update passes, a stale one conflicts.
        second.Body = "three";
        var third = await store.UpdateAsync(second, Ct);
        Assert.NotEqual(secondVersion, third.ETag);
        Assert.Equal("three", (await store.OneAsync(Id("r"), Ct))!.Body);
        var stale = new SplitTextEntity { Id = Id("r"), Body = "stale", ETag = firstVersion };
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpdateAsync(stale, Ct));
        Assert.Equal("three", (await store.OneAsync(Id("r"), Ct))!.Body);

        await store.DeleteAsync(Id("r"), Ct);
        Assert.Empty(await RawRowsAsync(nameof(SplitTextEntity)));
        AssertNoEtagTokenWasSent();
    }

    [Fact]
    public async Task ValuesWithContinuationRows_UpsertTwice_Update_Delete_StillWork_WithoutTheEtagToken()
    {
        SkipUnlessAvailable();
        var store = RecordedStore();
        var other = new string('b', 600_000);

        await store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = TwoRows }, Ct);
        await store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = other }, Ct);
        var read = (await store.OneAsync(Id("r"), Ct))!;
        Assert.Equal(other, read.Body, StringComparer.Ordinal);

        read.Body = "small again";
        await store.UpdateAsync(read, Ct);
        Assert.Equal(["r"], (await RawRowsAsync(nameof(SplitTextEntity))).Select(r => r.RowKey));

        await store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = TwoRows }, Ct);
        await store.DeleteAsync(Id("r"), Ct);
        Assert.Empty(await RawRowsAsync(nameof(SplitTextEntity)));
        AssertNoEtagTokenWasSent();
    }

    [Fact]
    public async Task UpsertBatch_OverExistingRows_StillWorks_WithoutTheEtagToken()
    {
        SkipUnlessAvailable();
        var store = RecordedStore();
        await store.CreateAsync(new SplitTextEntity { Id = Id("a"), Body = TwoRows }, Ct);
        await store.CreateAsync(new SplitTextEntity { Id = Id("b"), Body = "small" }, Ct);

        await store.UpsertBatchAsync(
        [
            new SplitTextEntity { Id = Id("a"), Body = "now small" },
            new SplitTextEntity { Id = Id("b"), Body = TwoRows },
            new SplitTextEntity { Id = Id("c"), Body = "new" },
        ], Ct);

        Assert.Equal(["a", "b", "b~0001", "c"], (await RawRowsAsync(nameof(SplitTextEntity))).Select(r => r.RowKey));
        Assert.Equal("now small", (await store.OneAsync(Id("a"), Ct))!.Body);
        Assert.Equal(TwoRows, (await store.OneAsync(Id("b"), Ct))!.Body, StringComparer.Ordinal);
        AssertNoEtagTokenWasSent();
    }

    /// <summary>Records the URL of every request, in order.</summary>
    private sealed class UrlRecorder(List<string> urls) : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            lock (urls) urls.Add(message.Request.Uri.ToString());
        }
    }
}
