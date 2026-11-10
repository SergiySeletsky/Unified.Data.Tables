using Azure;
using Azure.Data.Tables;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// <see cref="TableStorage{T}"/> over split values: a value too big for one row is written with its
/// continuation rows in ONE entity-group transaction, and every read path hides those rows, counts
/// logical entities only and reassembles (and verifies) the value before deserializing.
/// </summary>
public class TableStorageSplitTests
{
    private const string Big = "big";
    private static readonly string TwoRowText = new('a', 600_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>The stored rows (primary first) of a split-aware entity, exactly as the store would write them.</summary>
    private static List<TableEntity> StoredRows(string pk, string rk, string body, string etag = "W/\"stored\"")
    {
        var entity = new SplitTextEntity { Id = $"{pk}|{rk}", Body = body, Title = "t-" + rk };
        var logical = TableEntitySerializer.ToTableEntity(entity, pk, rk, ValueWriteContext.Public, []);
        var plan = SplitRowLayout.Plan(logical);
        plan.Primary.ETag = new ETag(etag);
        var rows = new List<TableEntity> { plan.Primary };
        foreach (var part in plan.Parts)
        {
            part.ETag = new ETag(etag);
            rows.Add(part);
        }
        return rows;
    }

    private static TableEntity PriorPrimary(string pk, string rk, int parts, string etag)
    {
        var row = new TableEntity(pk, rk) { ETag = new ETag(etag) };
        if (parts > 0) row[SystemColumnNames.SplitParts] = parts;
        return row;
    }

    private static TableEntity PartRow(string pk, string rk, int index) =>
        new(pk, $"{rk}~{index:D4}") { ETag = new ETag("W/\"part\""), [SystemColumnNames.SplitOf] = rk, [SystemColumnNames.SplitIndex] = index };

    private static void CaptureTransactions(StorageHarness<SplitTextEntity> h, List<List<TableTransactionAction>> sink)
    {
        var resp = Substitute.For<Response<IReadOnlyList<Response>>>();
        h.Table.SubmitTransactionAsync(Arg.Do<IEnumerable<TableTransactionAction>>(a => sink.Add(a.ToList())), Arg.Any<CancellationToken>())
            .Returns(resp);
    }

    /// <summary>A transaction failure at <paramref name="index"/>, as the SDK reports it (the index rides on the inner exception's Data).</summary>
    private static TableTransactionFailedException TxFailed(int status, int index = 0)
    {
        var inner = new RequestFailedException(status, "failed", status == 409 ? "EntityAlreadyExists" : "UpdateConditionNotSatisfied", null);
        inner.Data["FailedEntity"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ex = new TableTransactionFailedException(inner);
        Assert.Equal(index, ex.FailedTransactionActionIndex);
        return ex;
    }

    private static bool IsPartRangeFilter(string? filter) => filter is not null && filter.Contains("_SplitOf eq", StringComparison.Ordinal);

    /// <summary>A pageable that honours continuation tokens ("1", "2", …) across the given pages.</summary>
    private sealed class TokenPageable(IReadOnlyList<TableEntity[]> pages) : AsyncPageable<TableEntity>
    {
        public override async IAsyncEnumerable<Page<TableEntity>> AsPages(string? continuationToken = null, int? pageSizeHint = null)
        {
            var start = continuationToken is null ? 0 : int.Parse(continuationToken, System.Globalization.CultureInfo.InvariantCulture);
            for (var i = start; i < pages.Count; i++)
            {
                await Task.Yield();
                var next = i + 1 < pages.Count ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                yield return Page<TableEntity>.FromValues(pages[i], next, new FakeResponse());
            }
        }
    }

    // ── writes ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_SmallValue_UsesAddEntity_NoTransaction_NoPriorRead()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupAdd("W/\"new\"");

        var created = await h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct);

        await h.Table.Received(1).AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>());
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
        await h.Table.DidNotReceiveWithAnyArgs().GetEntityIfExistsAsync<TableEntity>(default!, default!, default, default);
        Assert.Equal("W/\"new\"", created.ETag);
    }

    [Fact]
    public async Task Create_TwoEntityValue_IsOneTransaction_PrimaryAddPartUpsert()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var tx = new List<List<TableTransactionAction>>();
        CaptureTransactions(h, tx);
        h.SetupGet("p", "r", new TableEntity("p", "r") { ETag = new ETag("W/\"after\"") });

        var created = await h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = TwoRowText }, Ct);

        var actions = Assert.Single(tx);
        Assert.Equal(2, actions.Count);
        Assert.Equal(TableTransactionActionType.Add, actions[0].ActionType);
        Assert.Equal("r", actions[0].Entity.RowKey);
        Assert.Equal(TableTransactionActionType.UpsertReplace, actions[1].ActionType);
        Assert.Equal("r~0001", actions[1].Entity.RowKey);
        await h.Table.DidNotReceiveWithAnyArgs().AddEntityAsync<TableEntity>(default!, default);
        // The transaction's sub-responses are not correlated, so the ETag comes from a keys-only re-read.
        Assert.Equal("W/\"after\"", created.ETag);
    }

    [Fact]
    public async Task Create_ExistingKey_InTransaction_ThrowsDuplicateKey()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var failure = TxFailed(409);
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw failure);

        var ex = await Assert.ThrowsAsync<DuplicateKeyException>(
            () => h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = TwoRowText }, Ct));

        Assert.Equal("p|r", ex.Id);
        Assert.Same(failure, ex.InnerException);
    }

    [Fact]
    public async Task Replace_StaleETag_ThrowsConcurrencyConflict_BeforeAnyWrite()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("p", "r", 1, "W/\"current\""));
        h.SetupUpdate();
        h.SetupTransaction();

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => h.Store.UpdateAsync(
            new SplitTextEntity { Id = "p|r", Body = TwoRowText, ETag = "W/\"old\"" }, ConcurrencyMode.Strict, Ct));

        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
        await h.Table.DidNotReceiveWithAnyArgs().UpdateEntityAsync<TableEntity>(default!, default, default, default);
    }

    [Fact]
    public async Task Replace_Missing_Throws404()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", null);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => h.Store.UpdateAsync(
            new SplitTextEntity { Id = "p|r", Body = "x", ETag = "W/\"e\"" }, ConcurrencyMode.Strict, Ct));

        Assert.Equal(404, ex.Status);
        Assert.Contains("The specified resource does not exist.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RowKeyWithTilde_IsRefusedBeforeAnyCall()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupAdd();
        h.SetupTransaction();

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Store.CreateAsync(new SplitTextEntity { Id = "p|r~0001", Body = "x" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r~0001", Body = "x" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.UpdateAsync(
            new SplitTextEntity { Id = "p|r~0001", Body = "x", ETag = "W/\"e\"" }, ConcurrencyMode.Strict, Ct));

        await h.Table.DidNotReceiveWithAnyArgs().CreateIfNotExistsAsync(default);
        await h.Table.DidNotReceiveWithAnyArgs().GetEntityIfExistsAsync<TableEntity>(default!, default!, default, default);
        await h.Table.DidNotReceiveWithAnyArgs().AddEntityAsync<TableEntity>(default!, default);
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
    }

    [Fact]
    public async Task Upsert_Shrink_DeletesStalePartsInSameTransaction()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("p", "r", 2, "W/\"prior\""));
        h.SetupQueryByFilter(PartRow("p", "r", 1), PartRow("p", "r", 2));
        var tx = new List<List<TableTransactionAction>>();
        CaptureTransactions(h, tx);

        await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "now small" }, Ct);

        var actions = Assert.Single(tx);
        Assert.Equal(3, actions.Count);
        Assert.Equal(TableTransactionActionType.UpdateReplace, actions[0].ActionType);
        Assert.Equal("r", actions[0].Entity.RowKey);
        Assert.Equal(new ETag("W/\"prior\""), actions[0].ETag);
        Assert.False(((TableEntity)actions[0].Entity).ContainsKey(SystemColumnNames.SplitParts));
        Assert.Equal(TableTransactionActionType.Delete, actions[1].ActionType);
        Assert.Equal("r~0001", actions[1].Entity.RowKey);
        Assert.Equal(ETag.All, actions[1].ETag);
        Assert.Equal(TableTransactionActionType.Delete, actions[2].ActionType);
        Assert.Equal("r~0002", actions[2].Entity.RowKey);
        await h.Table.DidNotReceiveWithAnyArgs().UpsertEntityAsync<TableEntity>(default!, default, default);
        await h.Table.DidNotReceiveWithAnyArgs().UpdateEntityAsync<TableEntity>(default!, default, default, default);
    }

    [Fact]
    public async Task Upsert_WithoutPrior_SmallValue_IsOneAdd()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", null);
        h.SetupAdd("W/\"a\"");

        var written = await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct);

        await h.Table.Received(1).AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>());
        await h.Table.DidNotReceiveWithAnyArgs().UpsertEntityAsync<TableEntity>(default!, default, default);
        Assert.Equal("W/\"a\"", written.ETag);
    }

    [Fact]
    public async Task Upsert_LosesRaceThreeTimes_ThrowsConcurrencyConflict()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("p", "r", 0, "W/\"prior\""));
        h.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RequestFailedException(412, "UpdateConditionNotSatisfied"));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct));

        await h.Table.Received(3).UpdateEntityAsync(Arg.Any<TableEntity>(), new ETag("W/\"prior\""), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
        await h.Table.Received(3).GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upsert_LosesRaceOnce_ThenSucceeds()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", null);
        h.Table.AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new RequestFailedException(409, "EntityAlreadyExists"),
                _ => Mocks.EtagResponse("W/\"second\""));

        var written = await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct);

        Assert.Equal("W/\"second\"", written.ETag);
        await h.Table.Received(2).AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upsert_StalePartAlreadyGone_RereadsAndRetries()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        // First read: two parts. The shrink transaction then fails at action 1 (a stale part another
        // writer already removed); the retry re-reads a row with none and writes it in one call.
        var withParts = Mocks.Found(PriorPrimary("p", "r", 2, "W/\"v1\""));
        var withoutParts = Mocks.Found(PriorPrimary("p", "r", 0, "W/\"v2\""));
        h.Table.GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(withParts, withoutParts);
        h.SetupQueryByFilter(PartRow("p", "r", 1), PartRow("p", "r", 2));
        var failure = TxFailed(404, index: 1);
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw failure);
        h.SetupUpdate("W/\"v3\"");

        var written = await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "small" }, Ct);

        Assert.Equal("W/\"v3\"", written.ETag);
        await h.Table.Received(1).UpdateEntityAsync(Arg.Any<TableEntity>(), new ETag("W/\"v2\""), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_PartFailure_IsNotADuplicateKey()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var failure = TxFailed(409, index: 1);
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns<Response<IReadOnlyList<Response>>>(_ => throw failure);

        var ex = await Assert.ThrowsAsync<TableTransactionFailedException>(
            () => h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = TwoRowText }, Ct));

        Assert.Same(failure, ex);
    }

    [Fact]
    public async Task CreateBatch_RowKeyWithTilde_IsRefusedBeforeAnyWrite()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupAdd();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.CreateBatchAsync(
        [
            new SplitTextEntity { Id = "p|ok", Body = "x" },
            new SplitTextEntity { Id = "p|bad~1", Body = "x" },
        ], Ct));

        await h.Table.DidNotReceiveWithAnyArgs().AddEntityAsync<TableEntity>(default!, default);
    }

    [Fact]
    public async Task CreateBatch_SplitAware_WritesEntityByEntity()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var tx = new List<List<TableTransactionAction>>();
        CaptureTransactions(h, tx);
        h.SetupAdd();
        h.SetupGet(new TableEntity("p", "r2") { ETag = new ETag("W/\"x\"") });

        var n = await h.Store.CreateBatchAsync(
        [
            new SplitTextEntity { Id = "p|r1", Body = "small" },
            new SplitTextEntity { Id = "p|r2", Body = TwoRowText },
        ], Ct);

        Assert.Equal(2, n);
        await h.Table.Received(1).AddEntityAsync(Arg.Is<TableEntity>(e => e.RowKey == "r1"), Arg.Any<CancellationToken>());
        var actions = Assert.Single(tx);
        Assert.Equal(["r2", "r2~0001"], actions.Select(a => a.Entity.RowKey));
    }

    // ── point reads ────────────────────────────────────────────────────────

    [Fact]
    public async Task OneAsync_AssemblesParts_AndVerifiesHash()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var rows = StoredRows("p", "r", TwoRowText);
        Assert.Equal(2, rows.Count);
        h.SetupGet("p", "r", rows[0]);
        h.SetupQueryByFilter([.. rows]);

        var read = await h.Store.OneAsync("p|r", Ct);

        Assert.NotNull(read);
        Assert.Equal(TwoRowText, read!.Body);
        Assert.Equal("t-r", read.Title);
        Assert.True(IsPartRangeFilter(h.LastQueryFilter));
        Assert.Contains("RowKey ge 'r~'", h.LastQueryFilter!, StringComparison.Ordinal);

        // A tampered chunk is caught, never returned.
        using var h2 = new StorageHarness<SplitTextEntity>();
        var tampered = StoredRows("p", "r", TwoRowText);
        var chunk = tampered[1].Keys.First(k => k.StartsWith("Body__c", StringComparison.Ordinal) && k != "Body__chunks");
        tampered[1][chunk] = new string('b', ((string)tampered[1][chunk]).Length);
        h2.SetupGet("p", "r", tampered[0]);
        h2.SetupQueryByFilter([.. tampered]);
        await Assert.ThrowsAsync<TableValueIntegrityException>(() => h2.Store.OneAsync("p|r", Ct));
    }

    [Fact]
    public async Task OneAsync_MissingPart_ThrowsIntegrity()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var rows = StoredRows("p", "r", TwoRowText);
        h.SetupGet("p", "r", rows[0]);
        h.SetupQueryByFilter();

        await Assert.ThrowsAsync<TableValueIntegrityException>(() => h.Store.OneAsync("p|r", Ct));
    }

    [Fact]
    public async Task OneAsync_OnPartRowId_ReturnsNull()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var rows = StoredRows("p", "r", TwoRowText);
        h.SetupGet("p", "r~0001", rows[1]);

        Assert.Null(await h.Store.OneAsync("p|r~0001", Ct));
        Assert.False(await h.Store.ExistsAsync("p|r~0001", Ct));
    }

    [Fact]
    public async Task OneAsync_SecondCallFromCache_ReturnsSameValue()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var rows = StoredRows("p", "r", TwoRowText);
        h.SetupGet("p", "r", rows[0]);
        h.SetupQueryByFilter([.. rows]);

        var first = await h.Store.OneAsync("p|r", Ct);
        var second = await h.Store.OneAsync("p|r", Ct);

        Assert.Equal(TwoRowText, second!.Body);
        Assert.NotSame(first, second);
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ── queries ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Query_Take_CountsLogicalEntitiesOnly()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var a = StoredRows("p", "a", TwoRowText);
        var b = StoredRows("p", "b", TwoRowText);
        var c = StoredRows("p", "c", "small");
        h.SetupQueryByFilter([.. a, .. b, .. c]);

        var result = await h.Store.QueryAsync(new QueryOptions { Partition = "p", Take = 2 }, Ct);

        Assert.Equal(["p|a", "p|b"], result.Select(e => e.Id));
        Assert.All(result, e => Assert.Equal(TwoRowText, e.Body));
    }

    [Fact]
    public async Task QueryByPartition_SkipsPartRows_AndAssembles()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var a = StoredRows("p", "a", TwoRowText);
        var c = StoredRows("p", "c", "small");
        h.SetupQueryByPartition([.. a, .. c]);
        h.SetupQueryByFilter([.. a, .. c]);

        var result = (await h.Store.QueryAsync("p", Ct)).ToList();

        Assert.Equal(["p|a", "p|c"], result.Select(e => e.Id));
        Assert.Equal(TwoRowText, result[0].Body);
    }

    [Fact]
    public async Task QueryPage_SkipsPartRows_AndCursorReachesAll()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var a = StoredRows("p", "a", TwoRowText);
        var b = StoredRows("p", "b", TwoRowText);
        var c = StoredRows("p", "c", "small");
        var pageable = new TokenPageable([[a[0], a[1], b[0]], [b[1], c[0]]]);
        h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(pageable);

        var seen = new List<SplitTextEntity>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await h.Store.QueryPageAsync(new QueryOptions { Partition = "p", Take = 3, ContinuationToken = cursor }, Ct);
            seen.AddRange(page.Items);
            cursor = page.ContinuationToken;
            Assert.True(++pages <= 5);
        }
        while (cursor is not null);

        Assert.Equal(["p|a", "p|b", "p|c"], seen.Select(e => e.Id));
        Assert.Equal(TwoRowText, seen[1].Body);
    }

    [Fact]
    public async Task Projection_NamingSplitProperty_FetchesWholeRow()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var a = StoredRows("p", "a", TwoRowText);
        var selects = new List<IEnumerable<string>?>();
        h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Do<IEnumerable<string>?>(s => selects.Add(s)), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable([.. a]));

        var result = await h.Store.QueryAsync(new QueryOptions { Partition = "p", Select = ["Body"] }, Ct);

        Assert.Equal(TwoRowText, Assert.Single(result).Body);
        Assert.Null(selects[0]);
        Assert.Null(TableStorage<SplitTextEntity>.BuildSelect(new QueryOptions { Select = ["Title", "Body"] }));
        Assert.Null(TableStorage<SplitNestedEntity>.BuildSelect(new QueryOptions { Select = ["Inner"] }));
        Assert.Null(TableStorage<SplitNestedEntity>.BuildSelect(new QueryOptions { Select = ["Inner_Body"] }));
    }

    [Fact]
    public async Task Projection_OmittingSplitProperty_DoesNotFetchParts()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        // What the service returns for Select = ["Title"]: the projected columns only.
        var primary = new TableEntity("p", "a") { ETag = new ETag("W/\"e\""), ["Title"] = "t-a", [SystemColumnNames.SplitParts] = 1 };
        var part = new TableEntity("p", "a~0001") { ETag = new ETag("W/\"e\""), [SystemColumnNames.SplitOf] = "a" };
        var filters = new List<string?>();
        var selects = new List<IEnumerable<string>?>();
        h.Table.QueryAsync<TableEntity>(Arg.Do<string?>(f => filters.Add(f)), Arg.Any<int?>(), Arg.Do<IEnumerable<string>?>(s => selects.Add(s)), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable(primary, part));

        var result = await h.Store.QueryAsync(new QueryOptions { Partition = "p", Select = ["Title"] }, Ct);

        var only = Assert.Single(result);
        Assert.Equal("t-a", only.Title);
        Assert.Null(only.Body);
        Assert.Single(filters);
        Assert.DoesNotContain(filters, IsPartRangeFilter);
        Assert.Contains(SystemColumnNames.SplitOf, selects[0]!);
        Assert.Contains(SystemColumnNames.SplitParts, selects[0]!);
        Assert.Contains(SystemColumnNames.SplitIndex, selects[0]!);
    }

    [Fact]
    public async Task Count_ExcludesPartRows()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var a = StoredRows("p", "a", TwoRowText);
        var b = StoredRows("p", "b", "small");
        var selects = new List<IEnumerable<string>?>();
        h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Do<IEnumerable<string>?>(s => selects.Add(s)), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable([.. a, .. b]));

        Assert.Equal(2, await h.Store.CountAsync("p", Ct));
        Assert.Equal(["PartitionKey", SystemColumnNames.SplitOf], selects[0]!);
    }

    [Fact]
    public async Task DeletePartition_ReturnsLogicalCount()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var a = StoredRows("p", "a", TwoRowText);
        var b = StoredRows("p", "b", "small");
        var selects = new List<IEnumerable<string>?>();
        h.Table.QueryAsync<TableEntity>(
                Arg.Any<System.Linq.Expressions.Expression<Func<TableEntity, bool>>>(), Arg.Any<int?>(),
                Arg.Do<IEnumerable<string>?>(s => selects.Add(s)), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable([.. a, .. b]));
        var tx = new List<List<TableTransactionAction>>();
        var resp = Substitute.For<Response<IReadOnlyList<Response>>>();
        h.Table.SubmitTransactionAsync(Arg.Do<IEnumerable<TableTransactionAction>>(x => tx.Add(x.ToList())), Arg.Any<CancellationToken>()).Returns(resp);

        Assert.Equal(2, await h.Store.DeletePartitionAsync("p", Ct));
        Assert.Equal(3, tx.Sum(t => t.Count));
        Assert.Contains(SystemColumnNames.SplitOf, selects[0]!);
    }

    // ── deletes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_SplitValue_DeletesPrimaryAndPartsInOneTransaction()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("p", "r", 2, "W/\"prior\""));
        h.SetupQueryByFilter(PartRow("p", "r", 1), PartRow("p", "r", 2));
        var tx = new List<List<TableTransactionAction>>();
        CaptureTransactions(h, tx);
        h.SetupDelete();

        await h.Store.DeleteAsync("p|r", Ct);

        var actions = Assert.Single(tx);
        Assert.Equal(["r", "r~0001", "r~0002"], actions.Select(a => a.Entity.RowKey));
        Assert.All(actions, a => Assert.Equal(TableTransactionActionType.Delete, a.ActionType));
        Assert.Equal(new ETag("W/\"prior\""), actions[0].ETag);
        await h.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Delete_SingleRowValue_UsesConditionalDeleteEntity()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("p", "r", 0, "W/\"prior\""));
        h.SetupDelete();
        h.SetupTransaction();

        await h.Store.DeleteAsync("p|r", Ct);

        await h.Table.Received(1).DeleteEntityAsync("p", "r", new ETag("W/\"prior\""), Arg.Any<CancellationToken>());
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
    }

    [Fact]
    public async Task Delete_Missing_IsNoOp()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", null);
        h.SetupDelete();
        h.SetupTransaction();

        await h.Store.DeleteAsync("p|r", Ct);

        await h.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
    }

    // ── review fix round 1 ─────────────────────────────────────────────────

    [Fact]
    public async Task Delete_PartRowId_IsRefused_PrimaryIntact()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r~0001", PartRow("p", "r", 1));
        h.SetupDelete();
        h.SetupTransaction();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.DeleteAsync("p|r~0001", Ct));

        await h.Table.DidNotReceiveWithAnyArgs().CreateIfNotExistsAsync(default);
        await h.Table.DidNotReceiveWithAnyArgs().GetEntityIfExistsAsync<TableEntity>(default!, default!, default, default);
        await h.Table.DidNotReceiveWithAnyArgs().DeleteEntityAsync(default!, default!, default, default);
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
    }

    [Fact]
    public async Task PartialUpdate_PartRowId_IsRefused_PrimaryIntact()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r~0001", PartRow("p", "r", 1));
        h.SetupUpdate();

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Store.UpdateAsync("p|r~0001", b => b.SetProperty(x => x.Title, "injected"), Ct));

        // Nothing reaches the table: no table creation, no merge into the continuation row.
        await h.Table.DidNotReceiveWithAnyArgs().CreateIfNotExistsAsync(default);
        await h.Table.DidNotReceiveWithAnyArgs().UpdateEntityAsync<TableEntity>(default!, default, default, default);
    }

    [Fact]
    public async Task PartialUpdate_PlainType_TildeId_IsUnchanged()
    {
        using var h = new StorageHarness<PlainEntity>();
        h.SetupUpdate();

        await h.Store.UpdateAsync("p|r~0001", b => b.SetProperty(x => x.Body, "b"), Ct);

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Is<TableEntity>(e => e.RowKey == "r~0001"), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_PrimaryWithMissingPart_Succeeds()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        // The primary declares two parts, but only the first exists: delete what exists, once.
        h.SetupGet("p", "r", PriorPrimary("p", "r", 2, "W/\"prior\""));
        h.SetupQueryByFilter(PartRow("p", "r", 1));
        var tx = new List<List<TableTransactionAction>>();
        CaptureTransactions(h, tx);

        await h.Store.DeleteAsync("p|r", Ct);

        var actions = Assert.Single(tx);
        Assert.Equal(["r", "r~0001"], actions.Select(a => a.Entity.RowKey));
    }

    [Fact]
    public async Task Upsert_ShrinkWithMissingPart_DeletesOnlyExistingParts()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.SetupGet("p", "r", PriorPrimary("p", "r", 2, "W/\"prior\""));
        h.SetupQueryByFilter(PartRow("p", "r", 2));
        var tx = new List<List<TableTransactionAction>>();
        CaptureTransactions(h, tx);

        await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = "now small" }, Ct);

        var actions = Assert.Single(tx);
        Assert.Equal(["r", "r~0002"], actions.Select(a => a.Entity.RowKey));
    }

    [Fact]
    public async Task Transaction_Write_EvictsCache_NextReadHitsStore()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var rows = StoredRows("p", "r", TwoRowText);
        h.SetupGet("p", "r", rows[0]);
        h.SetupQueryByFilter([.. rows]);
        CaptureTransactions(h, []);

        Assert.NotNull(await h.Store.OneAsync("p|r", Ct));          // warms the cache
        var written = await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = TwoRowText + "b" }, Ct);
        Assert.Equal("W/\"stored\"", written.ETag);               // the re-read version is still reported

        Assert.NotNull(await h.Store.OneAsync("p|r", Ct));          // not served from the cache
        await h.Table.Received(2).GetEntityIfExistsAsync<TableEntity>("p", "r", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryPage_Projection_DoesNotCacheIncompleteEntity()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var projected = new TableEntity("p", "a") { ETag = new ETag("W/\"e\""), ["Title"] = "t-a", [SystemColumnNames.SplitParts] = 1 };
        h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable(projected));

        var page = await h.Store.QueryPageAsync(new QueryOptions { Partition = "p", Select = ["Title"] }, Ct);
        Assert.Null(Assert.Single(page.Items).Body);

        var full = StoredRows("p", "a", TwoRowText);
        h.SetupGet("p", "a", full[0]);
        h.SetupQueryByFilter([.. full]);
        var read = await h.Store.OneAsync("p|a", Ct);

        Assert.Equal(TwoRowText, read!.Body);
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "a", null, Arg.Any<CancellationToken>());
    }

    // ── plain types are untouched ──────────────────────────────────────────

    [Fact]
    public async Task PlainEntity_ReadAndWritePaths_AreUnchanged()
    {
        using var h = new StorageHarness<TestEntity>(options: new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled });
        h.SetupAdd();
        h.SetupDelete();
        h.SetupGet("p", "r", Mocks.Row("p", "r"));
        var selects = new List<IEnumerable<string>?>();
        h.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Do<IEnumerable<string>?>(s => selects.Add(s)), Arg.Any<CancellationToken>())
            .Returns(_ => Mocks.Pageable(Mocks.Row("p", "r")));

        await h.Store.CreateAsync(new TestEntity { Id = "p|r", Name = "n" }, Ct);
        Assert.NotNull(await h.Store.OneAsync("p|r", Ct));
        Assert.Equal(1, await h.Store.CountAsync("p", Ct));
        await h.Store.DeleteAsync("p|r", Ct);

        await h.Table.Received(1).AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>());
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>("p", "r", null, Arg.Any<CancellationToken>());
        await h.Table.Received(1).DeleteEntityAsync("p", "r", default, Arg.Any<CancellationToken>());
        await h.Table.DidNotReceiveWithAnyArgs().SubmitTransactionAsync(default!, default);
        Assert.Equal(["PartitionKey"], Assert.Single(selects)!);
        Assert.Equal(
            ["PartitionKey", "RowKey", "Timestamp", "odata.etag", "_TypeName", "Name"],
            TableStorage<TestEntity>.BuildSelect(new QueryOptions { Select = ["Name"] })!);
    }
}
