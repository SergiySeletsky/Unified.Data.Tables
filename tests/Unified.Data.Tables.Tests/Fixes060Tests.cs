using System.Linq.Expressions;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>Versioned snapshot model for the F2 pack tests.</summary>
public sealed class SnapshotModel : VersionedEntity
{
    public string State { get; set; } = "";
}

/// <summary>
/// Regression tests for the 0.6.0 slate: F5 (IEntity constraint), F7 (Auto-without-ETag throws),
/// F8 (Id-equality → key translation), F2 (versioned-stream pack). Each test names its feature so
/// a future breakage points at the contract it undid.
/// </summary>
public class Fixes060Tests
{
    // ── F5: interface-only entities (no Entity base) ────────────────────────

    [Fact]
    public async Task F5_InterfaceOnlyEntity_RoundTripsThroughTheFake()
    {
        var store = new InMemoryStorage<InterfaceOnlyEntity>();

        var created = await store.CreateAsync(new InterfaceOnlyEntity { Id = "p|r", Name = "x", Value = 7 });
        var read = await store.OneAsync("p|r");

        Assert.NotNull(created.ETag);
        Assert.NotNull(read);
        Assert.Equal("x", read!.Name);
        Assert.Equal(7, read.Value);
        Assert.Equal("p|r", read.Id);
        Assert.Single(await store.QueryAsync("p"));
    }

    [Fact]
    public async Task F5_InterfaceOnlyEntity_WorksAgainstTheRealStore()
    {
        using var h = new StorageHarness<InterfaceOnlyEntity>();
        var row = new TableEntity("p", "r") { ["Name"] = "x", ["Value"] = 7, ["Id"] = "p|r" };
        h.SetupGet(row);

        var read = await h.Store.OneAsync("p|r");

        Assert.NotNull(read);
        Assert.Equal("x", read!.Name);
    }

    // ── F7: Auto without an ETag throws ─────────────────────────────────────

    [Fact]
    public async Task F7_Auto_NoETag_Throws_RealStore()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Store.UpdateAsync(new TestEntity { Id = "p|r", Name = "x" }));

        Assert.Contains("Auto mode requires", ex.Message);
        // Deterministic: no write, no refetch — the contract violation surfaces before any SDK call.
        await h.Table.DidNotReceive().UpdateEntityAsync(
            Arg.Any<TableEntity>(), Arg.Any<Azure.ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task F7_Auto_NoETag_Throws_Fake_WithIdenticalMessage()
    {
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "x" });

        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate();

        var fakeEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateAsync(new TestEntity { Id = "p|r", Name = "y" }));
        var realEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Store.UpdateAsync(new TestEntity { Id = "p|r", Name = "y" }));

        // Byte-identical messages: a test written against either implementation documents the same contract.
        Assert.Equal(realEx.Message, fakeEx.Message);
    }

    [Fact]
    public async Task F7_Auto_NoETag_ThrowsEvenWithWarmCache()
    {
        // The cached ETag is deliberately NOT consulted for writes anymore — a write conditional on
        // cache state was itself a lost-update vector. Deterministic: cold or warm, same outcome.
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet(Mocks.Row("p", "r"));
        h.SetupUpdate();

        _ = await h.Store.OneAsync("p|r");   // warms the entity cache

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Store.UpdateAsync(new TestEntity { Id = "p|r", Name = "x" }));
    }

    [Fact]
    public async Task F7_ImplicitLastWriterWins_OptIn_WritesUnconditionally()
    {
        using var h = new StorageHarness<TestEntity>(
            options: new UnifiedTableStorageOptions { ImplicitLastWriterWins = true });
        h.SetupUpdate();

        var updated = await h.Store.UpdateAsync(new TestEntity { Id = "p|r", Name = "x" });

        Assert.NotNull(updated);
        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), Azure.ETag.All, TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task F7_ImplicitLastWriterWins_OptIn_FakeConvergesLww()
    {
        var store = new InMemoryStorage<TestEntity>(
            options: new UnifiedTableStorageOptions { ImplicitLastWriterWins = true });
        await store.CreateAsync(new TestEntity { Id = "p|r", Name = "first" });

        var updated = await store.UpdateAsync(new TestEntity { Id = "p|r", Name = "second" });

        Assert.NotNull(updated.ETag);
        Assert.Equal("second", (await store.OneAsync("p|r"))!.Name);
    }

    // ── F8: Id equality translates to the key pair ──────────────────────────

    [Fact]
    public void F8_IdEquality_TranslatesToKeyFilter()
    {
        Expression<Func<TestEntity, bool>> predicate = x => x.Id == "p|r";

        var filter = TableFilterTranslator.Translate(predicate);

        Assert.Equal("(PartitionKey eq 'p' and RowKey eq 'r')", filter);
    }

    [Fact]
    public void F8_SingleSegmentId_AddressesThePkEqualsRkRow()
    {
        Expression<Func<TestEntity, bool>> predicate = x => x.Id == "solo";

        Assert.Equal("(PartitionKey eq 'solo' and RowKey eq 'solo')", TableFilterTranslator.Translate(predicate));
    }

    [Fact]
    public void F8_IdEquality_ComposesWithOtherClauses()
    {
        Expression<Func<TestEntity, bool>> predicate = x => x.Id == "p|r" && x.Value > 1;

        var filter = TableFilterTranslator.Translate(predicate);

        Assert.Contains("PartitionKey eq 'p'", filter);
        Assert.Contains("Value gt 1", filter);
    }

    [Fact]
    public void F8_NonEqualityOnId_IsRejected()
    {
        // The Id COLUMN is not guaranteed to exist (legacy rows) — only equality can be expressed
        // over the key pair.
        Expression<Func<TestEntity, bool>> predicate = x => x.Id != "p|r";

        var ex = Assert.Throws<NotSupportedException>(() => TableFilterTranslator.Translate(predicate));
        Assert.Contains("equality", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task F8_Fake_MatchesIdByAddressedRow_NotSpelling()
    {
        // "a" and "a|a" address the same row (pk == rk). The real store's key translation matches it
        // under either spelling; the fake's canonicalizing rewrite must agree.
        var store = new InMemoryStorage<TestEntity>();
        await store.CreateAsync(new TestEntity { Id = "a", Name = "x" });

        Assert.Single(await store.QueryAsync(x => x.Id == "a"));
        Assert.Single(await store.QueryAsync(x => x.Id == "a|a"));
        Assert.Empty(await store.QueryAsync(x => x.Id == "a|b"));
    }

    [Fact]
    public async Task F8_RealStore_EmitsKeyFilter_ForIdPredicate()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupQueryByFilter(Mocks.Row("p", "r"));

        await h.Store.QueryAsync(x => x.Id == "p|r");

        Assert.NotNull(h.LastQueryFilter);
        Assert.Contains("PartitionKey eq 'p'", h.LastQueryFilter);
        Assert.Contains("RowKey eq 'r'", h.LastQueryFilter);
        Assert.DoesNotContain("Id eq", h.LastQueryFilter);
    }

    // ── F2: versioned-stream extension pack ─────────────────────────────────

    [Fact]
    public void F2_VersionKey_IsTheWireContractByteFormat()
    {
        // (int.MaxValue - version) zero-padded to 20 digits — byte-compatible with pre-existing
        // hand-rolled inverted-version stores. Do not change.
        Assert.Equal("00000000002147483647", RowKeys.VersionKey(0));
        Assert.Equal("00000000002147483644", RowKeys.VersionKey(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => RowKeys.VersionKey(-1));
    }

    [Fact]
    public async Task F2_AppendAndReadBack_AtVersionAndLatest()
    {
        var store = new InMemoryStorage<SnapshotModel>();

        await store.AppendVersionAsync("stream-1", new SnapshotModel { Version = 1, State = "v1" });
        await store.AppendVersionAsync("stream-1", new SnapshotModel { Version = 2, State = "v2" });
        await store.AppendVersionAsync("stream-2", new SnapshotModel { Version = 9, State = "other" });

        Assert.Equal("v1", (await store.AtVersionAsync("stream-1", 1))!.State);
        Assert.Equal("v2", (await store.LatestAsync("stream-1"))!.State);
        Assert.Null(await store.AtVersionAsync("stream-1", 5));
        Assert.Null(await store.LatestAsync("stream-3"));
    }

    [Fact]
    public async Task F2_AppendingAnExistingVersion_ThrowsDuplicateKey()
    {
        var store = new InMemoryStorage<SnapshotModel>();
        await store.AppendVersionAsync("s", new SnapshotModel { Version = 1, State = "first" });

        await Assert.ThrowsAsync<DuplicateKeyException>(
            () => store.AppendVersionAsync("s", new SnapshotModel { Version = 1, State = "second" }));
    }

    [Fact]
    public async Task F2_AtOrBefore_PicksTheHighestQualifyingVersion()
    {
        var store = new InMemoryStorage<SnapshotModel>();
        await store.AppendVersionAsync("s", new SnapshotModel { Version = 1, State = "v1" });
        await store.AppendVersionAsync("s", new SnapshotModel { Version = 3, State = "v3" });
        await store.AppendVersionAsync("s", new SnapshotModel { Version = 7, State = "v7" });

        Assert.Equal("v3", (await store.AtOrBeforeAsync("s", 5))!.State);   // gap: 5 → 3
        Assert.Equal("v7", (await store.AtOrBeforeAsync("s", 7))!.State);   // inclusive
        Assert.Equal("v7", (await store.AtOrBeforeAsync("s", 100))!.State);
        Assert.Null(await store.AtOrBeforeAsync("s", 0));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetAtOrBeforeAsync("s", 0));
    }

    [Fact]
    public async Task F2_History_StreamsNewestFirst()
    {
        var store = new InMemoryStorage<SnapshotModel>();
        await store.AppendVersionAsync("s", new SnapshotModel { Version = 1, State = "v1" });
        await store.AppendVersionAsync("s", new SnapshotModel { Version = 2, State = "v2" });
        await store.AppendVersionAsync("s", new SnapshotModel { Version = 3, State = "v3" });

        var seen = new List<int>();
        await foreach (var snap in store.HistoryAsync("s", take: 2))
            seen.Add(snap.Version);

        Assert.Equal([3, 2], seen);
    }

    [Fact]
    public async Task F2_RealStore_AtOrBefore_IsAServerSideBoundedRead()
    {
        using var h = new StorageHarness<SnapshotModel>();
        h.SetupQueryByFilter(Mocks.Row("s", RowKeys.VersionKey(3)));

        _ = await h.Store.AtOrBeforeAsync("s", 5);

        Assert.NotNull(h.LastQueryFilter);
        Assert.Contains("PartitionKey eq 's'", h.LastQueryFilter);
        Assert.Contains("Version le 5", h.LastQueryFilter);
    }

    [Fact]
    public async Task F2_ThrowingVariants_NameTheStream()
    {
        var store = new InMemoryStorage<SnapshotModel>();

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetLatestAsync("ghost"));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public async Task F2_StreamIdWithSeparator_IsRejectedByEveryVerb()
    {
        // The stream id IS the partition key. A '|' would make Append store the row under the FIRST
        // segment while the ordered reads query the full stream id — silently invisible rows.
        var store = new InMemoryStorage<SnapshotModel>();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendVersionAsync("tenant|order-42", new SnapshotModel { Version = 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.LatestAsync("tenant|order-42"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AtVersionAsync("tenant|order-42", 1));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AtOrBeforeAsync("tenant|order-42", 1));
    }

    [Fact]
    public async Task F2_StreamIdEdgeWhitespace_IsTrimmed_WriteAndReadAgree()
    {
        // A trailing space would otherwise become '-' on write (whole-id normalization) but be
        // trimmed on read (partition-arg normalization) — different partitions. Trimming in the
        // pack keeps both sides consistent in either normalization mode.
        var store = new InMemoryStorage<SnapshotModel>();

        await store.AppendVersionAsync("stream-x ", new SnapshotModel { Version = 1, State = "v1" });

        Assert.NotNull(await store.LatestAsync("stream-x"));
        Assert.NotNull(await store.LatestAsync("stream-x "));   // same stream, either spelling
    }

    // ── Review fixes: fake/real parity on insert audit stamps ───────────────

    [Fact]
    public async Task Fake_CreateAsync_StampsUpdatedAt_LikeTheRealStore()
    {
        // TableStorage stamps UpdatedAt = CreatedAt on insert ("an insert is a write"). Entity's
        // property initializer masked a missing stamp in the fake; an interface-only IEntity model
        // has no initializer, so without the stamp it would read back the 1601 sentinel.
        var store = new InMemoryStorage<InterfaceOnlyEntity>();

        await store.CreateAsync(new InterfaceOnlyEntity { Id = "p|r", Name = "x" });
        var read = await store.OneAsync("p|r");

        Assert.NotNull(read);
        Assert.Equal(read!.CreatedAt, read.UpdatedAt);
        Assert.True(read.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }
}
