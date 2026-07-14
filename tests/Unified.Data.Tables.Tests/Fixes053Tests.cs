using System.Linq.Expressions;
using System.Runtime.Serialization;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Regression tests for the 0.5.3 slate. Each test names the finding it guards (B9, ctor fallback,
/// IdNormalization, Auto ETag contract, query-cache sizing) so a future breakage points at the fix it undid.
/// </summary>
public class Fixes053Tests
{
    // ── B9: Id is derived from PartitionKey/RowKey on read ──────────────────

    [Fact]
    public void B9_LegacyRow_WithoutIdColumn_GetsIdFromKeys()
    {
        // A row written by another serializer has no "Id" column — pre-0.5.3 it read back Id = "".
        var te = new TableEntity("legacy-p", "legacy-r") { ["Name"] = "x" };

        var e = te.FromTableEntity<TestEntity>();

        Assert.Equal("legacy-p|legacy-r", e.Id);
    }

    [Fact]
    public void B9_LegacyRow_SameKeyBothSides_ReadsSingleSegmentId()
    {
        // EntityId.Split("x") uses the whole id as both keys, so pk == rk round-trips to "x", not "x|x".
        var te = new TableEntity("solo", "solo") { ["Name"] = "x" };

        Assert.Equal("solo", te.FromTableEntity<TestEntity>().Id);
    }

    [Fact]
    public void B9_DivergentIdColumn_KeysWin()
    {
        // The keys are the row's authoritative identity — a stale/wrong "Id" column must not win,
        // or the next write lands on a different row than the one that was read.
        var te = new TableEntity("real-p", "real-r") { ["Id"] = "stale|value", ["Name"] = "x" };

        Assert.Equal("real-p|real-r", te.FromTableEntity<TestEntity>().Id);
    }

    [Fact]
    public void B9_KeylessDictionary_PreservesIdColumn()
    {
        // Serializer-only round-trips (no table keys) keep whatever the Id column said.
        var te = new TableEntity { ["Id"] = "kept-as-is", ["Name"] = "x" };

        Assert.Equal("kept-as-is", te.FromTableEntity<TestEntity>().Id);
    }

    [Fact]
    public void B9_StoredIdAddressingTheSameKeys_IsKeptVerbatim()
    {
        // "a|a" Splits to ("a","a") — the exact keys of this row — so the explicit composite form
        // must round-trip byte-identically instead of being canonicalized to "a".
        var te = new TableEntity("a", "a") { ["Id"] = "a|a", ["Name"] = "x" };

        Assert.Equal("a|a", te.FromTableEntity<TestEntity>().Id);
    }

    // ── Uninitialized-object fallback for ctor-less types ───────────────────

    public sealed class LegacyCtorlessModel
    {
        private LegacyCtorlessModel()
        {
        }

        public LegacyCtorlessModel(string name) => Name = name;

        public string Name { get; set; } = "";
    }

    [Fact]
    public void CtorlessType_LateBoundRead_UsesUninitializedObjectFallback()
    {
        // Legacy FormatterServices-based serializers persisted types with no public parameterless
        // ctor; the late-bound read must construct them uninitialized instead of throwing.
        var te = new TableEntity("p", "r")
        {
            [TableEntitySerializer.TypeNameColumnName] = typeof(LegacyCtorlessModel).AssemblyQualifiedName!,
            ["Name"] = "restored",
        };

        var obj = te.FromTableEntity();

        var model = Assert.IsType<LegacyCtorlessModel>(obj);
        Assert.Equal("restored", model.Name);
    }

    // ── IdNormalization.AsWritten ────────────────────────────────────────────

    [Fact]
    public async Task AsWritten_Fake_PreservesCaseSensitiveIds()
    {
        var opts = new UnifiedTableStorageOptions { IdNormalization = IdNormalization.AsWritten };
        var store = new InMemoryStorage<TestEntity>(options: opts);

        await store.CreateAsync(new TestEntity { Id = "MyStream|CaseSENSITIVE==", Name = "x" });

        Assert.NotNull(await store.OneAsync("MyStream|CaseSENSITIVE=="));
        Assert.Null(await store.OneAsync("mystream|casesensitive=="));   // different row in this mode
        Assert.Single(await store.QueryAsync("MyStream"));
        Assert.Empty(await store.QueryAsync("mystream"));
        Assert.Equal(1, await store.DeletePartitionAsync("MyStream"));
    }

    [Fact]
    public async Task AsWritten_RealStore_PassesKeysVerbatim()
    {
        using var h = new StorageHarness<TestEntity>(
            options: new UnifiedTableStorageOptions { IdNormalization = IdNormalization.AsWritten });
        h.SetupGet("My Part", "My Row", Mocks.Row("My Part", "My Row"));   // exact-key match

        var e = await h.Store.OneAsync("My Part|My Row");   // would request ("my-part","my-row") if normalized

        Assert.NotNull(e);
        Assert.Equal("My Part|My Row", e!.Id);
    }

    [Fact]
    public async Task AsWritten_DIWiredFake_HonoursConfiguredOptions()
    {
        // AddUnifiedInMemoryStorage(configure) applies the same options the production registration
        // uses, so a DI-resolved fake behaves like the configured TableStorage.
        var services = new ServiceCollection();
        services.AddUnifiedInMemoryStorage(o => o.IdNormalization = IdNormalization.AsWritten);
        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IStorage<TestEntity>>();
        await store.CreateAsync(new TestEntity { Id = "My X|Case==", Name = "x" });

        Assert.NotNull(await store.OneAsync("My X|Case=="));
        Assert.Null(await store.OneAsync("my-x|case=="));
    }

    // ── Auto-mode without an ETag: throws by default (0.6.0), warns under the opt-in ────────

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Auto_NoETag_ThrowsByDefault()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate();

        // Since 0.6.0, Auto with no ETag has no version to check — that's a caller bug, not a
        // license to silently clobber. Nothing may reach the table.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Store.UpdateAsync(new TestEntity { Id = "p|r", Name = "x" }));   // no ETag, cold cache

        Assert.Contains("Auto mode requires", ex.Message);
        await h.Table.DidNotReceive().UpdateEntityAsync(
            Arg.Any<TableEntity>(), Arg.Any<Azure.ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Auto_NoETag_ImplicitLastWriterWinsOptIn_WritesUnconditionally_AndWarns()
    {
        var logger = new ListLogger<TableStorage<TestEntity>>();
        using var h = new StorageHarness<TestEntity>(
            options: new UnifiedTableStorageOptions { ImplicitLastWriterWins = true }, logger: logger);
        h.SetupUpdate();

        await h.Store.UpdateAsync(new TestEntity { Id = "p|r", Name = "x" });   // no ETag, opted-in fallback

        // The migration cushion restores the pre-0.6.0 unconditional replace (ETag.All) — but
        // still says so out loud: deliberate LWW should be spelled ConcurrencyMode.LastWriterWins.
        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Any<TableEntity>(), Azure.ETag.All, Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("last-writer-wins"));
    }

    [Fact]
    public async Task ExplicitLastWriterWins_DoesNotWarn()
    {
        var logger = new ListLogger<TableStorage<TestEntity>>();
        using var h = new StorageHarness<TestEntity>(logger: logger);
        h.SetupUpdate();

        await h.Store.UpdateAsync(new TestEntity { Id = "p|r", Name = "x" }, ConcurrencyMode.LastWriterWins);

        // Deliberate LWW is the caller's explicit choice — no nagging.
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Auto_NoCallerETag_ThrowsEvenWithWarmCache()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupGet(Mocks.Row("p", "r"));
        h.SetupUpdate();

        _ = await h.Store.OneAsync("p|r");   // warms the entity cache (ETag and all)

        // The cached ETag is never consulted for writes since 0.6.0 — a write conditional on cache
        // state was itself a lost-update vector — so Auto without a caller-round-tripped ETag
        // throws regardless of cache warmth, with no refetch and no write.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Store.UpdateAsync(new TestEntity { Id = "p|r", Name = "x" }));

        Assert.Contains("Auto mode requires", ex.Message);
        await h.Table.Received(1).GetEntityIfExistsAsync<TableEntity>(   // the OneAsync warm-up only — no refetch
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await h.Table.DidNotReceive().UpdateEntityAsync(
            Arg.Any<TableEntity>(), Arg.Any<Azure.ETag>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>());
    }

    // ── Query-cache entries are sized by row count ──────────────────────────

    [Fact]
    public async Task QueryCache_ListEntry_IsSizedByRowCount()
    {
        // SizeLimit 4: the 3 warmed entity entries (size 1 each) fit; the result list, now sized by
        // its row count (3), no longer does — so the second query re-fetches. Under the old Size=1
        // the list would have been cached (4 <= 4) and the second query served from cache.
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 4 });
        using var h = new StorageHarness<TestEntity>(cache: cache);
        h.SetupQueryByPartition(Mocks.Row("p", "r1"), Mocks.Row("p", "r2"), Mocks.Row("p", "r3"));

        await h.Store.QueryAsync("p");
        await h.Store.QueryAsync("p");

        h.Table.Received(2).QueryAsync<TableEntity>(
            Arg.Any<Expression<Func<TableEntity, bool>>>(), Arg.Any<int?>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }
}

/// <summary>
/// Oversized-cell policy tests. <see cref="TableEntitySerializer.OversizedCellPolicy"/> is static
/// process state, so every class that exercises (or resets) it shares this collection and runs
/// sequentially relative to the others.
/// </summary>
[Collection("OversizedCellPolicy")]
public sealed class OversizedCellPolicyTests : IDisposable
{
    public void Dispose() => TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimWithMarker;

    // High-entropy payloads (fixed seed) that blow the 64 KB cap even gzip-compressed.
    private static EntityWithSteps HugeList()
    {
        var rng = new Random(11);
        string RandomBlob()
        {
            var c = new char[4000];
            for (var i = 0; i < c.Length; i++)
                c[i] = (char)rng.Next(0x21, 0x2FA0);
            return new string(c);
        }

        return new EntityWithSteps { Steps = [.. Enumerable.Range(0, 200).Select(_ => RandomBlob())], Number = 3 };
    }

    private static EntityWithContent HugeString()
    {
        var rng = new Random(42);
        var chars = new char[600_000];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (char)rng.Next(0x21, 0x2FA0);
        return new EntityWithContent { Content = new string(chars), Number = 7 };
    }

    [Fact]
    public void Default_TrimWithMarker_List_WritesTruncatedMarker()
    {
        var te = HugeList().ToTableEntity("pk", "rk");

        Assert.True(te.ContainsKey("Steps__Truncated"), "a trimmed list must leave a __Truncated marker");
        Assert.Matches(@"^kept \d+ of 200 items$", (string)te["Steps__Truncated"]);

        // The marker is an extra column with no matching property — ignored on read.
        var back = te.FromTableEntity<EntityWithSteps>();
        Assert.True(back.Steps.Count < 200);
        Assert.Equal(3, back.Number);
    }

    [Fact]
    public void Default_TrimWithMarker_String_WritesTruncatedMarker()
    {
        var te = HugeString().ToTableEntity("pk", "rk");

        Assert.True(te.ContainsKey("Content__Truncated"));
        Assert.Matches(@"^kept \d+ of 600000 chars$", (string)te["Content__Truncated"]);
        Assert.Equal(7, te.FromTableEntity<EntityWithContent>().Number);
    }

    [Fact]
    public void Throw_List_FailsTheWriteLoudly()
    {
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.Throw;

        var ex = Assert.Throws<SerializationException>(() => HugeList().ToTableEntity("pk", "rk"));
        Assert.Contains("64 KB", ex.Message);
    }

    [Fact]
    public void Throw_String_FailsTheWriteLoudly()
    {
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.Throw;

        Assert.Throws<SerializationException>(() => HugeString().ToTableEntity("pk", "rk"));
    }

    [Fact]
    public void TrimSilently_RestoresPre053Behaviour_NoMarker()
    {
        TableEntitySerializer.OversizedCellPolicy = OversizedCellPolicy.TrimSilently;

        var te = HugeList().ToTableEntity("pk", "rk");

        Assert.False(te.ContainsKey("Steps__Truncated"));
        Assert.True(te.FromTableEntity<EntityWithSteps>().Steps.Count < 200);
    }

    [Fact]
    public void FittingPayload_NeverGetsAMarker()
    {
        var te = new EntityWithSteps { Steps = ["a", "b"], Number = 1 }.ToTableEntity("pk", "rk");

        Assert.DoesNotContain(te.Keys, k => k.EndsWith("__Truncated", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> HugeBlob()
    {
        var rng = new Random(23);
        string RandomBlob()
        {
            var c = new char[4000];
            for (var i = 0; i < c.Length; i++)
                c[i] = (char)rng.Next(0x21, 0x2FA0);
            return new string(c);
        }

        return Enumerable.Range(0, 100).ToDictionary(i => $"k{i}", _ => RandomBlob());
    }

    [Fact]
    public void DroppedNonListPayload_LeavesMarkerOnly_AndRowStaysReadable()
    {
        // A dictionary can't be prefix-trimmed — the DROP branch omits the cell and writes only the
        // marker. The marker must be SKIPPED on read: pre-fix it drilled into the property and
        // resurrected it as a phantom empty instance.
        var te = new EntityWithBigBlob { Id = "p|r", Blob = HugeBlob() }.ToTableEntity("p", "r");

        Assert.True(te.ContainsKey("Blob__Truncated"));
        Assert.False(te.ContainsKey("Blob__Json"));
        Assert.False(te.ContainsKey("Blob__GZip"));

        var back = te.FromTableEntity<EntityWithBigBlob>();
        Assert.Null(back.Blob);   // dropped means dropped — not an empty phantom
    }

    [Fact]
    public void DroppedInterfaceTypedPayload_RowStaysReadable()
    {
        // Worst case: the dropped property is interface-typed, which cannot be constructed at all —
        // pre-fix, reading the marker made the whole row permanently unreadable.
        var te = new EntityWithInterfaceBlob { Id = "p|r", Blob = HugeBlob() }.ToTableEntity("p", "r");

        Assert.True(te.ContainsKey("Blob__Truncated"));

        var back = te.FromTableEntity<EntityWithInterfaceBlob>();   // must not throw
        Assert.Null(back.Blob);
    }

    [Fact]
    public void SecondRegistration_DoesNotResetConfiguredPolicy()
    {
        // First-registration-wins, mirroring TryAddSingleton: a bare AddUnifiedTableStorage() from
        // another module must not silently reset an explicitly configured process-wide policy.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        ServiceCollectionExtensions.AddUnifiedTableStorage(services,
            o => o.OversizedCells = OversizedCellPolicy.Throw);
        Assert.Equal(OversizedCellPolicy.Throw, TableEntitySerializer.OversizedCellPolicy);

        ServiceCollectionExtensions.AddUnifiedTableStorage(services);

        Assert.Equal(OversizedCellPolicy.Throw, TableEntitySerializer.OversizedCellPolicy);
    }
}
