using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// The version a split-aware write stamps on the entity is the one that write produced. A write that
/// goes through an entity-group transaction takes it from the transaction's own sub-response for the
/// primary row (action 0); it never reads the row again afterwards, because a later read can already
/// return another writer's version, and an entity carrying that version over this writer's content
/// would replace the other writer's row on its next optimistic update without a conflict.
/// </summary>
public class WriteVersionStampTests
{
    private const string Mine = "W/\"mine\"";
    private const string Theirs = "W/\"theirs\"";

    private static readonly string TwoRowText = new('a', 600_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>A found row, without NSubstitute (it is built inside another substitute's callback).</summary>
    private sealed class Found(TableEntity row) : NullableResponse<TableEntity>
    {
        public override bool HasValue => true;

        public override TableEntity Value => row;

        public override Response GetRawResponse() => new SubResponse(null);
    }

    /// <summary>One sub-response of a transaction: 204 with an <c>ETag</c> header, or with none.</summary>
    private sealed class SubResponse(string? etag) : Response
    {
        public override int Status => 204;

        public override string ReasonPhrase => "No Content";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => etag is not null && IsETag(name);

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => etag is null ? [] : [new HttpHeader("ETag", etag)];

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = IsETag(name) ? etag : null;
            return value is not null;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = IsETag(name) && etag is not null ? [etag] : null;
            return values is not null;
        }

        private static bool IsETag(string name) => string.Equals(name, "ETag", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A committed transaction whose sub-responses carry these ETags, in action order (null = no header).</summary>
    private static Response<IReadOnlyList<Response>> Committed(params string?[] etags) =>
        Response.FromValue<IReadOnlyList<Response>>([.. etags.Select(e => (Response)new SubResponse(e))], new SubResponse(null));

    private static TableEntity PriorPrimary(string rk, int parts, string etag)
    {
        var row = new TableEntity("p", rk) { ETag = new ETag(etag) };
        if (parts > 0) row[SystemColumnNames.SplitParts] = parts;
        return row;
    }

    private static TableEntity PartRow(string rk, int index) =>
        new("p", $"{rk}~{index:D4}") { ETag = new ETag("W/\"part\""), [SystemColumnNames.SplitOf] = rk, [SystemColumnNames.SplitIndex] = index };

    private static TableTransactionFailedException PrimaryConditionFailed()
    {
        var inner = new RequestFailedException(412, "failed", "UpdateConditionNotSatisfied", null);
        inner.Data["FailedEntity"] = "0";
        return new TableTransactionFailedException(inner);
    }

    /// <summary>
    /// A mocked table that holds the row <c>p|r</c> and enforces <c>If-Match</c> on it. Every transaction
    /// that passes its condition commits as <see cref="Mine"/>; then, before the caller sees the response,
    /// another writer replaces the row (<see cref="Theirs"/>), so any read after the commit returns that
    /// other writer's version.
    /// </summary>
    private sealed class ContestedRow
    {
        private readonly StorageHarness<SplitTextEntity> harness;

        public ContestedRow(StorageHarness<SplitTextEntity> harness, string? stored)
        {
            this.harness = harness;
            Stored = stored;
            harness.Table.GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Reads++;
                    return Stored is null ? new Missing() : new Found(PriorPrimary("r", Parts, Stored));
                });
            harness.Table.QueryAsync<TableEntity>(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(_ => Mocks.Pageable([.. Enumerable.Range(1, Parts).Select(i => PartRow("r", i))]));
            harness.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var actions = call.Arg<IEnumerable<TableTransactionAction>>().ToList();
                    var primary = actions[0];
                    var conditional = primary.ActionType == TableTransactionActionType.UpdateReplace;
                    if (conditional ? primary.ETag.ToString() != Stored : Stored is not null)
                        throw PrimaryConditionFailed();
                    Transactions++;
                    Parts = actions.Count(a => a.ActionType != TableTransactionActionType.Delete) - 1;
                    // This write is version "mine"; the other writer's commit follows at once.
                    Stored = Theirs;
                    return Committed([Mine, .. Enumerable.Repeat("W/\"part\"", actions.Count - 1)]);
                });
            harness.Table.UpdateEntityAsync(Arg.Any<TableEntity>(), Arg.Any<ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (call.Arg<ETag>().ToString() != Stored)
                        throw new RequestFailedException(412, "failed", "UpdateConditionNotSatisfied", null);
                    Replaces++;
                    Stored = "W/\"replaced\"";
                    return Mocks.EtagResponse(Stored);
                });
        }

        /// <summary>The version in the table now; null when the row does not exist.</summary>
        public string? Stored { get; private set; }

        public int Parts { get; private set; }

        public int Reads { get; private set; }

        public int Transactions { get; private set; }

        /// <summary>Single-call replaces the table accepted after the contested transaction.</summary>
        public int Replaces { get; private set; }

        public TableStorage<SplitTextEntity> Store => harness.Store;
    }

    private sealed class Missing : NullableResponse<TableEntity>
    {
        public override bool HasValue => false;

        public override TableEntity Value => throw new InvalidOperationException("no value");

        public override Response GetRawResponse() => new SubResponse(null);
    }

    private static StorageHarness<SplitTextEntity> Harness() =>
        new(options: new UnifiedTableStorageOptions { Cache = CachePolicy.Disabled });

    // ── the finding ────────────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_AnotherWriterCommitsRightAfterTheTransaction_TheNextUpdateConflicts()
    {
        using var h = Harness();
        var row = new ContestedRow(h, stored: "W/\"v0\"");
        var mine = new SplitTextEntity { Id = "p|r", Body = TwoRowText };

        await row.Store.UpsertAsync(mine, Ct);
        Assert.Equal(Theirs, row.Stored);

        // The entity holds this writer's content: its version must not be the other writer's.
        mine.Body = "changed";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => row.Store.UpdateAsync(mine, Ct));

        Assert.Equal(Theirs, row.Stored);
        Assert.Equal(1, row.Transactions);
        Assert.Equal(0, row.Replaces);
    }

    [Fact]
    public async Task Create_AnotherWriterCommitsRightAfterTheTransaction_TheNextUpdateConflicts()
    {
        using var h = Harness();
        var row = new ContestedRow(h, stored: null);
        var mine = new SplitTextEntity { Id = "p|r", Body = TwoRowText };

        await row.Store.CreateAsync(mine, Ct);

        mine.Body = "changed";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => row.Store.UpdateAsync(mine, ConcurrencyMode.Strict, Ct));
        Assert.Equal(Theirs, row.Stored);
        Assert.Equal(0, row.Replaces);
    }

    [Fact]
    public async Task Update_AnotherWriterCommitsRightAfterTheTransaction_TheNextUpdateConflicts()
    {
        using var h = Harness();
        var row = new ContestedRow(h, stored: "W/\"v0\"");
        var mine = new SplitTextEntity { Id = "p|r", Body = TwoRowText, ETag = "W/\"v0\"" };

        await row.Store.UpdateAsync(mine, Ct);

        mine.Body = "changed";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => row.Store.UpdateAsync(mine, Ct));
        Assert.Equal(Theirs, row.Stored);
        Assert.Equal(0, row.Replaces);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("upsert")]
    [InlineData("update")]
    public async Task TransactionWrite_IsStampedWithThePrimarysOwnSubResponse(string operation)
    {
        using var h = Harness();
        var row = new ContestedRow(h, stored: operation == "create" ? null : "W/\"v0\"");
        var mine = new SplitTextEntity { Id = "p|r", Body = TwoRowText, ETag = operation == "update" ? "W/\"v0\"" : null };

        var written = operation switch
        {
            "create" => await row.Store.CreateAsync(mine, Ct),
            "upsert" => await row.Store.UpsertAsync(mine, Ct),
            _ => await row.Store.UpdateAsync(mine, Ct),
        };

        // Action 0 is the primary: its sub-response's ETag, not the continuation row's and not a later read's.
        Assert.Equal(Mine, written.ETag);
    }

    [Theory]
    [InlineData("create", 0)]
    [InlineData("upsert", 1)]
    [InlineData("update", 1)]
    public async Task TransactionWrite_ReadsNothingAfterTheCommit(string operation, int reads)
    {
        using var h = Harness();
        var row = new ContestedRow(h, stored: operation == "create" ? null : "W/\"v0\"");
        var mine = new SplitTextEntity { Id = "p|r", Body = TwoRowText, ETag = operation == "update" ? "W/\"v0\"" : null };

        _ = operation switch
        {
            "create" => await row.Store.CreateAsync(mine, Ct),
            "upsert" => await row.Store.UpsertAsync(mine, Ct),
            _ => await row.Store.UpdateAsync(mine, Ct),
        };

        // The point read before the write (none for a create), and no read after it.
        Assert.Equal(reads, row.Reads);
        Assert.Equal(1, row.Transactions);
    }

    [Fact]
    public async Task TransactionResponseWithoutAVersion_StampsNull_AndDoesNotCacheTheEntity()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        var stored = PriorPrimary("r", 0, "W/\"v0\"");
        var reads = 0;
        h.Table.GetEntityIfExistsAsync<TableEntity>("p", "r", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                reads++;
                return new Found(stored);
            });
        // The primary's sub-response carries no ETag header (a service or a double that sends none).
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(Committed(null, "W/\"part\""));

        var written = await h.Store.UpsertAsync(new SplitTextEntity { Id = "p|r", Body = TwoRowText }, Ct);

        // Unknown, as after a batch: re-read before an optimistic update. Never another read's version.
        Assert.Null(written.ETag);
        Assert.Equal(1, reads);
        Assert.NotNull(await h.Store.OneAsync("p|r", Ct));
        Assert.Equal(2, reads); // not served from the cache
    }

    [Fact]
    public async Task TransactionResponseWithNoSubResponses_StampsNull()
    {
        using var h = new StorageHarness<SplitTextEntity>();
        h.Table.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(Committed());

        var written = await h.Store.CreateAsync(new SplitTextEntity { Id = "p|r", Body = TwoRowText }, Ct);

        Assert.Null(written.ETag);
        await h.Table.DidNotReceiveWithAnyArgs().GetEntityIfExistsAsync<TableEntity>(default!, default!, default, default);
    }
}

/// <summary>
/// The same against a local Azurite: the ETag of a transaction's own sub-response for the primary row is
/// the row's version (what a point read then returns when nobody else writes), and a second writer that
/// commits right after the transaction no longer lends its version to the first writer's entity.
/// </summary>
public sealed class AzuriteWriteVersionStampTests : AzuriteCleanupTestBase
{
    private static readonly string TwoRows = new('a', 600_000);

    private async Task<string> StoredVersionAsync(string rk) =>
        (await Table(nameof(SplitTextEntity)).GetEntityAsync<TableEntity>(Pk, rk, cancellationToken: Ct)).Value.ETag.ToString();

    [Fact]
    public async Task AfterAWriteThatNeedsContinuationRows_TheEntitysETagIsTheStoredRowsVersion()
    {
        SkipUnlessAvailable();
        var store = SplitOnlyStore<SplitTextEntity>();

        // Insert: primary and one continuation row.
        var entity = await store.CreateAsync(new SplitTextEntity { Id = Id("r"), Body = TwoRows }, Ct);
        Assert.Equal(await StoredVersionAsync("r"), entity.ETag);

        // Replace through a transaction, conditional on the version just stamped.
        entity.Body = new string('b', 1_000_000);
        await store.UpdateAsync(entity, Ct);
        Assert.Equal(["r", "r~0001", "r~0002"], (await RawRowsAsync(nameof(SplitTextEntity))).Select(r => r.RowKey));
        Assert.Equal(await StoredVersionAsync("r"), entity.ETag);

        // Shrink: the transaction replaces the primary and deletes both continuation rows.
        entity.Body = "small";
        await store.UpdateAsync(entity, Ct);
        Assert.Equal(["r"], (await RawRowsAsync(nameof(SplitTextEntity))).Select(r => r.RowKey));
        Assert.Equal(await StoredVersionAsync("r"), entity.ETag);

        // Upsert over the stored row, growing again.
        var upserted = await store.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = TwoRows }, Ct);
        Assert.Equal(await StoredVersionAsync("r"), upserted.ETag);
        upserted.Title = "still mine";
        await store.UpdateAsync(upserted, ConcurrencyMode.Strict, Ct);
        Assert.Equal("still mine", (await store.OneAsync(Id("r"), Ct))!.Title);
    }

    [Fact]
    public async Task AnotherWriterCommitsRightAfterTheTransaction_TheFirstWritersNextUpdateConflicts()
    {
        SkipUnlessAvailable();
        var other = SplitOnlyStore<SplitTextEntity>();
        // Right after the first writer's transaction has committed, and before that writer gets its
        // response, a second writer replaces the row.
        var interleaved = new AfterFirstBatch(() => other.UpsertAsync(new SplitTextEntity { Id = Id("r"), Body = "theirs" }, CancellationToken.None));
        var store = SplitOnlyStore<SplitTextEntity>(TableServiceWith(interleaved));
        var mine = new SplitTextEntity { Id = Id("r"), Body = TwoRows };

        await store.CreateAsync(mine, Ct);

        Assert.Equal(1, interleaved.Fired);
        Assert.Equal("theirs", (await other.OneAsync(Id("r"), Ct))!.Body);
        Assert.NotEqual(await StoredVersionAsync("r"), mine.ETag);

        mine.Body = "mine again";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpdateAsync(mine, Ct));
        Assert.Equal("theirs", (await other.OneAsync(Id("r"), Ct))!.Body);
    }

    /// <summary>Runs <paramref name="action"/> once, after the first entity-group transaction has been answered.</summary>
    private sealed class AfterFirstBatch(Func<Task> action) : HttpPipelinePolicy
    {
        private int fired;

        public int Fired => Volatile.Read(ref fired);

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            await ProcessNextAsync(message, pipeline);
            if (IsAnsweredBatch(message) && Interlocked.Exchange(ref fired, 1) == 0)
                await action();
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            ProcessNext(message, pipeline);
            if (IsAnsweredBatch(message) && Interlocked.Exchange(ref fired, 1) == 0)
                action().GetAwaiter().GetResult();
        }

        private static bool IsAnsweredBatch(HttpMessage message) =>
            message.Request.Method == RequestMethod.Post
            && message.Request.Uri.Path.EndsWith("$batch", StringComparison.Ordinal)
            && message.Response.Status is >= 200 and < 300;
    }
}
