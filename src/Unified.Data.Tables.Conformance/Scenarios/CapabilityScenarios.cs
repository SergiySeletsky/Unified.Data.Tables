using static Unified.Data.Tables.Conformance.ConformanceLeniencies;

namespace Unified.Data.Tables.Conformance.Scenarios;

/// <summary>
/// Tier 3: the side interfaces a store may expose through the capability probe. Each scenario asks the
/// probe first and ends as "not supported" when the store does not expose the capability; a store that
/// exposes it must pass.
/// </summary>
internal static class CapabilityScenarios
{
    public static void Add(ScenarioBuilder s)
    {
        s.Tier(ConformanceTier.Capabilities);
        ConditionalDelete(s);
        AtomicBatch(s);
        AtomicInsert(s);
        Info(s);
        Range(s);
        RawTable(s);
        TableStore(s);
    }

    private static void ConditionalDelete(ScenarioBuilder s)
    {
        s.Add("ConditionalDelete.IfExists_ReportsDeleted_ThenNotFound",
            "DeleteIfExistsAsync removes the row whatever its version and reports Deleted; for an id that is not stored it reports NotFound and changes nothing.",
            async c =>
            {
                var store = await c.ItemsAsync();
                c.Capability<ConformanceItem, IConditionalDelete<ConformanceItem>>(store);
                await store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct);
                await store.CreateAsync(Rows.Item(c.Id("r2")), c.Ct);

                Check.Equal(DeleteStatus.Deleted, await store.DeleteIfExistsAsync(c.Id("r1"), c.Ct), "the status for a stored row");
                Check.Equal(false, await store.ExistsAsync(c.Id("r1"), c.Ct), "Exists after the delete");
                Check.Equal(DeleteStatus.NotFound, await store.DeleteIfExistsAsync(c.Id("r1"), c.Ct), "the status for the row that was just deleted");
                Check.Equal(DeleteStatus.NotFound, await store.DeleteIfExistsAsync(c.Id("never"), c.Ct), "the status for a row that never existed");
                Check.Equal(DeleteStatus.Deleted, await store.DeleteIfExistsAsync(c.Pk.ToUpperInvariant() + "|R2", c.Ct), "the status for a stored row, by another spelling of its id");
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count");
            });

        s.Add("ConditionalDelete.IfMatch_DeletesThatVersionOnly",
            "DeleteIfMatchAsync removes the row and reports Deleted when it is the version the ETag names; for another version it throws ConcurrencyConflictException and leaves the row; for a missing row it reports NotFound.",
            async c =>
            {
                var store = await c.ItemsAsync();
                c.Capability<ConformanceItem, IConditionalDelete<ConformanceItem>>(store);
                var created = await store.CreateAsync(Rows.Item(c.Id("r1"), "first"), c.Ct);
                var staleETag = created.ETag!;
                var current = await store.UpsertAsync(Rows.Item(c.Id("r1"), "second"), c.Ct);

                await Check.NeutralAsync<ConcurrencyConflictException>(
                    () => store.DeleteIfMatchAsync(c.Id("r1"), staleETag, c.Ct), "a conditional delete on a stale ETag");
                Check.Equal("second", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after the refused delete");

                Check.Equal(DeleteStatus.Deleted, await store.DeleteIfMatchAsync(c.Id("r1"), current.ETag!, c.Ct), "the status on the current ETag");
                Check.Equal(false, await store.ExistsAsync(c.Id("r1"), c.Ct), "Exists after the delete");
                Check.Equal(DeleteStatus.NotFound, await store.DeleteIfMatchAsync(c.Id("r1"), current.ETag!, c.Ct), "the status for a row that is gone");
            });

        s.Add("ConditionalDelete.IfMatch_WithoutAVersion_ThrowsArgumentException",
            "DeleteIfMatchAsync refuses a null, empty or wildcard (\"*\") ETag with an ArgumentException, on the extension method and on the store's own implementation, and deletes nothing.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var own = c.Capability<ConformanceItem, IConditionalDelete<ConformanceItem>>(store);
                await store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct);

                foreach (var etag in new[] { "*", "", null })
                {
                    await Check.ThrowsAsync<ArgumentException>(
                        () => store.DeleteIfMatchAsync(c.Id("r1"), etag!, c.Ct), $"the extension method with the ETag {Check.Show(etag)}");
                    await Check.ThrowsAsync<ArgumentException>(
                        () => own.DeleteIfMatchAsync(c.Id("r1"), etag!, c.Ct), $"the store's own DeleteIfMatchAsync with the ETag {Check.Show(etag)}");
                }
                Check.Equal(true, await store.ExistsAsync(c.Id("r1"), c.Ct), "Exists after the refused deletes");
            });

        s.Add("ConditionalDelete.CancelledToken_DeletesNothing",
            "A conditional delete given a token that is already cancelled throws OperationCanceledException and leaves the row.",
            CancellationNotObserved,
            async c =>
            {
                var store = await c.ItemsAsync();
                c.Capability<ConformanceItem, IConditionalDelete<ConformanceItem>>(store);
                var created = await store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct);

                await Check.CancelledAsync(() => store.DeleteIfExistsAsync(c.Id("r1"), Rows.Cancelled), "DeleteIfExistsAsync");
                await Check.CancelledAsync(() => store.DeleteIfMatchAsync(c.Id("r1"), created.ETag!, Rows.Cancelled), "DeleteIfMatchAsync");
                Check.Equal(true, await store.ExistsAsync(c.Id("r1"), c.Ct), "Exists after the cancelled deletes");
            });

        s.Add("ConditionalDelete.LargeValue_TakesItsContinuationRowsAlong",
            "A conditional delete of an entity with a value larger than one row removes its continuation rows with it, as DeleteAsync does.",
            async c =>
            {
                var large = await c.StoreAsync<ConformanceLargeItem>();
                var store = large.Storage;
                c.Capability<ConformanceLargeItem, IConditionalDelete<ConformanceLargeItem>>(store);
                await store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(600_000)), c.Ct);
                var second = await store.CreateAsync(Rows.Large(c.Id("r2"), Rows.Text(600_000, 5)), c.Ct);

                Check.Equal(DeleteStatus.Deleted, await store.DeleteIfExistsAsync(c.Id("r1"), c.Ct), "the status of DeleteIfExistsAsync");
                Check.Equal(DeleteStatus.Deleted, await store.DeleteIfMatchAsync(c.Id("r2"), second.ETag!, c.Ct), "the status of DeleteIfMatchAsync");

                Check.Equal(0, await store.CountAsync(c.Pk, c.Ct), "the entity count after the deletes");
                if (await Rows.RawKeysAsync(large, c.Pk, c.Ct) is { } keys)
                    Check.Sequence([], keys, "the physical row keys after the deletes");
                await store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(600_000, 9)), c.Ct);
                Check.Text(Rows.Text(600_000, 9), (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value of an entity created at the deleted id");
            });
    }

    private static List<ConformanceItem> Items(ConformanceContext c, int count, string prefix = "r") =>
        Enumerable.Range(0, count).Select(i => Rows.Item(c.Id(prefix + i.ToString("D3")), "n" + i, i)).ToList();

    private static void AtomicBatch(ScenarioBuilder s)
    {
        s.Add("AtomicBatch.Create_StoresEveryRowOrNone",
            "CreateAtomicAsync stores every entity of a one-partition batch (up to 100); when one id is stored already it throws DuplicateKeyException and stores none of them. An empty batch writes nothing.",
            async c =>
            {
                var store = await c.ItemsAsync();
                c.Capability<ConformanceItem, IAtomicBatch<ConformanceItem>>(store);

                await store.CreateAtomicAsync([], c.Ct);
                await store.CreateAtomicAsync(Items(c, 100), c.Ct);
                Check.Equal(100, await store.CountAsync(c.Pk, c.Ct), "the row count after an atomic create of 100");
                Check.Equal("n99", (await store.OneAsync(c.Id("r099"), c.Ct))?.Name, "Name of the last row");

                var clash = Items(c, 3, "s");
                clash.Add(Rows.Item(c.Id("r050"), "clash"));
                await Check.NeutralAsync<DuplicateKeyException>(() => store.CreateAtomicAsync(clash, c.Ct), "an atomic create that names a stored id");
                Check.Equal(100, await store.CountAsync(c.Pk, c.Ct), "the row count after the refused atomic create");
                Check.Equal("n50", (await store.OneAsync(c.Id("r050"), c.Ct))?.Name, "Name of the stored row after the refused atomic create");
            });

        s.Add("AtomicBatch.Upsert_InsertsAndReplacesInOneTransaction",
            "UpsertAtomicAsync inserts the missing rows and replaces the stored ones of a one-partition batch; the last writer wins per row.",
            async c =>
            {
                var store = await c.ItemsAsync();
                c.Capability<ConformanceItem, IAtomicBatch<ConformanceItem>>(store);
                var old = Rows.Item(c.Id("r001"), "old");
                old.Note = "the old row has a note";
                await store.CreateAsync(old, c.Ct);

                await store.UpsertAtomicAsync([], c.Ct);
                await store.UpsertAtomicAsync(Items(c, 3), c.Ct);

                Check.Equal(3, await store.CountAsync(c.Pk, c.Ct), "the row count after the atomic upsert");
                var replaced = Check.NotNull(await store.OneAsync(c.Id("r001"), c.Ct), "the replaced row");
                Check.Equal("n1", replaced.Name, "Name of the replaced row");
                Check.Null(replaced.Note, "a column the replacement leaves null");
            });

        s.Add("AtomicBatch.BatchThatCannotBeOneTransaction_ThrowsBatchNotAtomic_AndStoresNothing",
            "A batch that spans two partitions, holds more than 100 rows, names a row twice or exceeds the payload budget throws BatchNotAtomicException before anything is written; it is never split.",
            async c =>
            {
                var store = await c.ItemsAsync();
                c.Capability<ConformanceItem, IAtomicBatch<ConformanceItem>>(store);

                async Task RefusedAsync(List<ConformanceItem> batch, string why)
                {
                    await Check.NeutralAsync<BatchNotAtomicException>(() => store.CreateAtomicAsync(batch, c.Ct), "an atomic create " + why);
                    await Check.NeutralAsync<BatchNotAtomicException>(() => store.UpsertAtomicAsync(batch, c.Ct), "an atomic upsert " + why);
                    Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after a batch " + why);
                }

                var twoPartitions = Items(c, 2);
                twoPartitions.Add(Rows.Item(c.Pk + "x|r000"));
                await RefusedAsync(twoPartitions, "over two partitions");
                await RefusedAsync(Items(c, 101), "of 101 rows");
                var twice = Items(c, 2);
                twice.Add(Rows.Item(c.Id("r000"), "again"));
                await RefusedAsync(twice, "that names a row twice");
                var heavy = Items(c, 60);
                foreach (var item in heavy)
                    item.Data = Rows.Bytes(60_000);
                await RefusedAsync(heavy, "of 60 rows of 60,000 bytes (above the 3,000,000-byte budget)");
            });
    }

    private static void AtomicInsert(ScenarioBuilder s)
    {
        s.Add("AtomicInsert.StoresEveryRowOrNone",
            "InsertAtomicAsync stores every row of a one-partition batch of mixed types; when one key is stored already it throws DuplicateKeyException and stores none of them. An empty batch writes nothing.",
            async c =>
            {
                var store = await c.EventsAsync();
                c.Capability<ConformanceEvent, IAtomicInsert<ConformanceEvent>>(store);

                await store.InsertAtomicAsync([], c.Ct);
                await store.InsertAtomicAsync(
                [
                    new PolymorphicWrite<ConformanceEvent>(new TableKey(c.Pk, "r1"), Rows.Placed("a")),
                    new PolymorphicWrite<ConformanceEvent>(new TableKey(c.Pk, "r2"), Rows.Shipped("b")),
                ], c.Ct);
                Check.Equal(2, await store.CountAsync(c.Pk, c.Ct), "the row count after the atomic insert");
                Check.True((await store.GetAsync(new TableKey(c.Pk, "r2"), c.Ct))?.Item is ConformanceOrderShipped, "the second row reads back as the type written");

                await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.InsertAtomicAsync(
                    [
                        new PolymorphicWrite<ConformanceEvent>(new TableKey(c.Pk, "r3"), Rows.Placed("c")),
                        new PolymorphicWrite<ConformanceEvent>(new TableKey(c.Pk, "r1"), Rows.Placed("clash")),
                    ], c.Ct), "an atomic insert that names a stored key");
                Check.Equal(2, await store.CountAsync(c.Pk, c.Ct), "the row count after the refused atomic insert");
            });

        s.Add("AtomicInsert.BatchThatCannotBeOneTransaction_ThrowsBatchNotAtomic_AndStoresNothing",
            "An insert batch that spans two partitions, holds more than 100 rows or names a key twice throws BatchNotAtomicException before anything is written.",
            async c =>
            {
                var store = await c.EventsAsync();
                c.Capability<ConformanceEvent, IAtomicInsert<ConformanceEvent>>(store);
                PolymorphicWrite<ConformanceEvent> Write(string pk, string rk) => new(new TableKey(pk, rk), Rows.Placed(rk));

                await Check.NeutralAsync<BatchNotAtomicException>(
                    () => store.InsertAtomicAsync([Write(c.Pk, "r1"), Write(c.Pk + "x", "r1")], c.Ct), "an atomic insert over two partitions");
                await Check.NeutralAsync<BatchNotAtomicException>(
                    () => store.InsertAtomicAsync(Enumerable.Range(0, 101).Select(i => Write(c.Pk, "r" + i)).ToList(), c.Ct), "an atomic insert of 101 rows");
                await Check.NeutralAsync<BatchNotAtomicException>(
                    () => store.InsertAtomicAsync([Write(c.Pk, "r1"), Write(c.Pk, "r2"), Write(c.Pk, "r1")], c.Ct), "an atomic insert that names a key twice");
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after the refused atomic inserts");
            });
    }

    private static void Info(ScenarioBuilder s)
    {
        s.Add("StorageInfo.GetInfo_NeverThrows",
            "GetInfo() returns a StorageInfo for every store, with or without IStorageInfo: StorageInfo.Unknown when the store states nothing.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var info = Check.NotNull(store.GetInfo(), "the info of a store");
                if (!store.TryGetCapability<ConformanceItem, IStorageInfo>(out _))
                    Check.Equal(StorageInfo.Unknown, info, "the info of a store without IStorageInfo");
            });

        s.Add("StorageInfo.StatesTheProvider_AndSaneLimits",
            "A store with IStorageInfo names its provider, states only positive limits, and answers the same on every call without reaching the backend.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var own = c.Capability<ConformanceItem, IStorageInfo>(store);

                var info = Check.NotNull(own.GetInfo(), "the info IStorageInfo returns");
                Check.NotEmpty(info.ProviderName, "StorageInfo.ProviderName");
                Check.True(info.TableName is null || info.TableName.Length > 0, "StorageInfo.TableName is a name or null, not an empty string");
                Check.True(info.MaxBatchRows is null or > 0, "StorageInfo.MaxBatchRows is positive when stated");
                Check.True(info.MaxBatchBytes is null or > 0, "StorageInfo.MaxBatchBytes is positive when stated");
                Check.True(info.MaxCellBytes is null or > 0, "StorageInfo.MaxCellBytes is positive when stated");
                Check.True(info.MaxRowBytes is null or > 0, "StorageInfo.MaxRowBytes is positive when stated");
                Check.True(info.MaxCellBytes is null || info.MaxRowBytes is null || info.MaxCellBytes <= info.MaxRowBytes, "a cell is no larger than a row");
                Check.Equal(info, own.GetInfo(), "the info on a second call");
                Check.Equal(info, store.GetInfo(), "the info through the GetInfo() extension");
            });

        s.Add("StorageInfo.AgreesWithTheAtomicBatch",
            "A store that exposes IAtomicBatch does not state that it has no single-partition transactions, and refuses an atomic batch of one row more than the MaxBatchRows it states.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var info = c.Capability<ConformanceItem, IStorageInfo>(store).GetInfo();
                c.Capability<ConformanceItem, IAtomicBatch<ConformanceItem>>(store);

                Check.True(info.SinglePartitionTransactions != false, "a store with IAtomicBatch does not state SinglePartitionTransactions = false");
                if (info.MaxBatchRows is int rows and <= 1000)
                {
                    await store.CreateAtomicAsync(Items(c, rows), c.Ct);
                    await Check.NeutralAsync<BatchNotAtomicException>(
                        () => store.CreateAtomicAsync(Items(c, rows + 1, "s"), c.Ct), $"an atomic create of MaxBatchRows + 1 ({rows + 1}) rows");
                    Check.Equal(rows, await store.CountAsync(null, c.Ct), "the row count");
                }
            });

        s.Add("StorageInfo.Polymorphic_GetInfo_NeverThrows",
            "GetInfo() on a polymorphic store returns a StorageInfo, StorageInfo.Unknown when the store states nothing.",
            async c =>
            {
                var store = await c.EventsAsync();
                var info = Check.NotNull(store.GetInfo(), "the info of a polymorphic store");
                if (!store.TryGetCapability<ConformanceEvent, IStorageInfo>(out _))
                    Check.Equal(StorageInfo.Unknown, info, "the info of a polymorphic store without IStorageInfo");
            });
    }

    /// <summary>
    /// Ten rows k00..k09 in the partition and two in another, in a store that answers a bounded query. A
    /// store that refuses one with NotSupportedException ends the scenario as "not supported".
    /// </summary>
    private static async Task<IStorage<ConformanceItem>> RangeStoreAsync(ConformanceContext c)
    {
        var store = await c.ItemsAsync();
        try
        {
            await store.QueryAsync(new QueryOptions { Partition = c.Pk, FromRowKey = "k03" }, c.Ct);
        }
        catch (NotSupportedException ex)
        {
            throw c.NotSupported("the store refuses a query with RowKey bounds: " + ex.Message);
        }
        var rows = Enumerable.Range(0, 10).Select(i => Rows.Item(c.Id("k0" + i))).ToList();
        rows.Reverse();
        await store.CreateBatchAsync(rows, c.Ct);
        await store.CreateBatchAsync([Rows.Item(c.Pk + "x|k04"), Rows.Item(c.Pk + "x|k05")], c.Ct);
        return store;
    }

    private static IEnumerable<string> K(ConformanceContext c, int from, int to) =>
        Enumerable.Range(from, to - from + 1).Select(i => c.Id("k0" + i));

    private static void Range(ScenarioBuilder s)
    {
        s.Add("Range.Bounds_SelectTheRowKeysBetweenThem",
            "FromRowKey and ToRowKey return the rows of the partition between them in row-key order: the lower bound inclusive and the upper exclusive by default, either changed by its flag, either left open, and a bound need not be a stored key.",
            RowKeyRangeIgnored,
            async c =>
            {
                var store = await RangeStoreAsync(c);
                async Task<List<string>> IdsAsync(QueryOptions options) => Rows.Ids(await store.QueryAsync(options with { Partition = c.Pk }, c.Ct));

                Check.Sequence(K(c, 3, 5), await IdsAsync(new QueryOptions { FromRowKey = "k03", ToRowKey = "k06" }), "the ids in [k03, k06)");
                Check.Sequence(K(c, 4, 6), await IdsAsync(new QueryOptions { FromRowKey = "k03", FromInclusive = false, ToRowKey = "k06", ToInclusive = true }), "the ids in (k03, k06]");
                Check.Sequence(K(c, 7, 9), await IdsAsync(new QueryOptions { FromRowKey = "k07" }), "the ids from k07");
                Check.Sequence(K(c, 0, 1), await IdsAsync(new QueryOptions { ToRowKey = "k02" }), "the ids before k02");
                Check.Sequence(K(c, 0, 2), await IdsAsync(new QueryOptions { ToRowKey = "k02", ToInclusive = true }), "the ids up to k02");
                Check.Sequence(K(c, 4, 6), await IdsAsync(new QueryOptions { FromRowKey = "k035", ToRowKey = "k065" }), "the ids in [k035, k065)");
                Check.Sequence(K(c, 0, 9), await IdsAsync(new QueryOptions { FromRowKey = "" }), "the ids from the empty string (a bound, not \"no bound\")");
                Check.Sequence([], await IdsAsync(new QueryOptions { FromRowKey = "k06", ToRowKey = "k03" }), "the ids of a range whose lower bound is above its upper bound");
                Check.Sequence([], await IdsAsync(new QueryOptions { FromRowKey = "k03", ToRowKey = "k03" }), "the ids in [k03, k03)");
                Check.Sequence(K(c, 3, 3), await IdsAsync(new QueryOptions { FromRowKey = "k03", ToRowKey = "k03", ToInclusive = true }), "the ids in [k03, k03]");
                Check.Sequence(K(c, 3, 4), await IdsAsync(new QueryOptions { FromRowKey = "k03", ToRowKey = "k08", Take = 2 }), "the first two ids in [k03, k08)");
            });

        s.Add("Range.StreamAndPages_HonourTheBounds",
            "QueryStreamAsync and QueryPageAsync return the same bounded rows as QueryAsync; paging walks the range and nothing outside it.",
            RowKeyRangeIgnored,
            async c =>
            {
                var store = await RangeStoreAsync(c);
                var options = new QueryOptions { Partition = c.Pk, FromRowKey = "k02", ToRowKey = "k08" };

                Check.Sequence(K(c, 2, 7), Rows.Ids(await Rows.ListAsync(store.QueryStreamAsync(options, c.Ct), c.Ct)), "the streamed ids in [k02, k08)");
                Check.Sequence(K(c, 2, 7), Rows.Ids(await Rows.PageAllAsync(store, options with { Take = 4 }, c.Ct)), "the ids in [k02, k08), paged by 4");
            });

        s.Add("Range.PageToken_IsBoundToTheBounds",
            "A continuation token of a bounded query replayed with another bound, another inclusivity or no bound throws ArgumentException.",
            [RowKeyRangeIgnored, ContinuationTokenNotBound],
            async c =>
            {
                var store = await RangeStoreAsync(c);
                var options = new QueryOptions { Partition = c.Pk, FromRowKey = "k02", ToRowKey = "k08", Take = 2 };
                var token = Check.NotNull((await store.QueryPageAsync(options, c.Ct)).ContinuationToken, "a continuation token while rows of the range remain");

                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(options with { ToRowKey = "k09", ContinuationToken = token }, c.Ct), "a token replayed with another upper bound");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(options with { FromRowKey = "k01", ContinuationToken = token }, c.Ct), "a token replayed with another lower bound");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(options with { FromInclusive = false, ContinuationToken = token }, c.Ct), "a token replayed with another lower inclusivity");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(options with { ToInclusive = true, ContinuationToken = token }, c.Ct), "a token replayed with another upper inclusivity");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(new QueryOptions { Partition = c.Pk, Take = 2, ContinuationToken = token }, c.Ct), "a token replayed without the bounds");
                Check.Sequence(K(c, 4, 5), Rows.Ids((await store.QueryPageAsync(options with { ContinuationToken = token }, c.Ct)).Items), "the second page of its own query");
            });

        s.Add("Range.NeedsAPartition_AndExcludesAPrefix",
            "A RowKey bound without a Partition, and one combined with RowKeyPrefix, throw ArgumentException on every query member.",
            [RowKeyRangeIgnored, ArgumentValidationDiffers],
            async c =>
            {
                var store = await RangeStoreAsync(c);
                var noPartition = new QueryOptions { FromRowKey = "k03" };
                var withPrefix = new QueryOptions { Partition = c.Pk, RowKeyPrefix = "k", ToRowKey = "k05" };

                foreach (var (options, why) in new[] { (noPartition, "a bound without a partition"), (withPrefix, "a bound with a prefix") })
                {
                    await Check.ThrowsAsync<ArgumentException>(() => store.QueryAsync(options, c.Ct), "QueryAsync with " + why);
                    await Check.ThrowsAsync<ArgumentException>(() => store.QueryPageAsync(options, c.Ct), "QueryPageAsync with " + why);
                    await Check.ThrowsAsync<ArgumentException>(
                        () => Rows.ListAsync(store.QueryStreamAsync(options, c.Ct), c.Ct), "QueryStreamAsync with " + why);
                }
            });

        s.Add("Range.Bounds_AreNormalizedLikeIds",
            "Under IdNormalization.Normalized a bound is normalized the way ids are (trimmed, lower-cased, spaces to '-') before it is compared with the stored row keys.",
            [RowKeyRangeIgnored, IdNormalizationDiffers],
            async c =>
            {
                var store = await RangeStoreAsync(c);
                Check.Sequence(K(c, 3, 5),
                    Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk.ToUpperInvariant(), FromRowKey = " K03", ToRowKey = "K06 " }, c.Ct)),
                    "the ids in [\" K03\", \"K06 \")");
            });
    }

    private static RawRow Raw(string pk, string rk, params (string Name, RawCell Cell)[] cells)
    {
        var map = new Dictionary<string, RawCell>(StringComparer.Ordinal);
        foreach (var (name, cell) in cells)
            map[name] = cell;
        return new RawRow(pk, rk, map);
    }

    private static async Task<List<string>> RawKeysAsync(IRawTable table, RawQuery query, CancellationToken ct)
    {
        var keys = new List<string>();
        await foreach (var row in table.QueryAsync(query, ct).ConfigureAwait(false))
            keys.Add(row.RowKey);
        return keys;
    }

    private static void RawTable(ScenarioBuilder s)
    {
        s.Add("RawTable.Get_ReturnsThePhysicalRowTheTypedStoreWrote",
            "IRawTable.GetAsync returns a row the typed store wrote, as stored: its keys, its columns as typed cells, its ETag and timestamp; null for keys no row has.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var raw = c.Capability<ConformanceItem, IRawTable>(store);
                var created = await store.CreateAsync(Rows.Item(c.Id("r1"), "name", 5), c.Ct);

                Check.NotEmpty(raw.TableName, "IRawTable.TableName");
                var row = Check.NotNull(await raw.GetAsync(c.Pk, "r1", c.Ct), "the raw row");
                Check.Equal(c.Pk, row.PartitionKey, "the partition key of the raw row");
                Check.Equal("r1", row.RowKey, "the row key of the raw row");
                Check.Equal("name", row.Cells["Name"].AsString(), "the Name cell");
                Check.Equal(RawCellType.Int32, row.Cells["Number"].Type, "the type of the Number cell");
                Check.Equal(5, row.Cells["Number"].AsInt32(), "the Number cell");
                Check.Equal(created.ETag, row.ETag, "the ETag of the raw row, against the one the typed create returned");
                Check.NotNull((object?)row.Timestamp, "the timestamp of the raw row");
                Check.True(!row.Cells.ContainsKey("PartitionKey") && !row.Cells.ContainsKey("RowKey") && !row.Cells.ContainsKey("Timestamp"),
                    "the cells hold neither the keys nor the timestamp");
                Check.Null(await raw.GetAsync(c.Pk, "r2", c.Ct), "a raw read of keys no row has");
                Check.Null(await raw.GetAsync(c.Pk.ToUpperInvariant(), "R1", c.Ct), "a raw read by keys in another case (keys are not normalized)");
            });

        s.Add("RawTable.Cells_RoundTripEveryType",
            "A raw row written with a cell of each type (String, Binary, Boolean, DateTime, Double, Guid, Int32, Int64) is read back with the same types and values.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var raw = c.Capability<ConformanceItem, IRawTable>(store);
                var when = new DateTimeOffset(2024, 2, 29, 13, 14, 15, 678, TimeSpan.Zero);
                var guid = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
                var written = Raw(c.Pk, "Raw Row",
                    ("S", RawCell.FromString("text")), ("B", RawCell.FromBinary([1, 2, 3])), ("F", RawCell.FromBoolean(true)),
                    ("D", RawCell.FromDateTime(when)), ("R", RawCell.FromDouble(1.5)), ("G", RawCell.FromGuid(guid)),
                    ("I", RawCell.FromInt32(-7)), ("L", RawCell.FromInt64(9_000_000_000L)));

                await raw.SubmitAsync([RawOperation.Insert(written)], c.Ct);

                var read = Check.NotNull(await raw.GetAsync(c.Pk, "Raw Row", c.Ct), "the raw row (its row key is stored as written, not normalized)");
                Check.Equal(written.Cells.Count, read.Cells.Count, "the cell count");
                foreach (var cell in written.Cells)
                    Check.True(read.Cells.TryGetValue(cell.Key, out var stored) && stored.Equals(cell.Value),
                        $"the cell '{cell.Key}' reads back as {cell.Value}");
            });

        s.Add("RawTable.Query_ReturnsARowKeyRangeOfOnePartition_InOrder",
            "IRawTable.QueryAsync streams the rows of one partition whose row key lies in the range, in row-key order, capped by Take; a Take that is not positive throws ArgumentOutOfRangeException.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var raw = c.Capability<ConformanceItem, IRawTable>(store);
                await raw.SubmitAsync(Enumerable.Range(0, 6).Reverse().Select(i => RawOperation.Insert(Raw(c.Pk, "k" + i, ("N", RawCell.FromInt32(i))))).ToList(), c.Ct);
                await raw.SubmitAsync([RawOperation.Insert(Raw(c.Pk + "x", "k2"))], c.Ct);

                Check.Sequence(["k0", "k1", "k2", "k3", "k4", "k5"], await RawKeysAsync(raw, new RawQuery(c.Pk), c.Ct), "the row keys of the partition");
                Check.Sequence(["k1", "k2", "k3"], await RawKeysAsync(raw, new RawQuery(c.Pk) { FromRowKey = "k1", ToRowKey = "k4" }, c.Ct), "the row keys in [k1, k4)");
                Check.Sequence(["k2", "k3", "k4"],
                    await RawKeysAsync(raw, new RawQuery(c.Pk) { FromRowKey = "k1", FromInclusive = false, ToRowKey = "k4", ToInclusive = true }, c.Ct),
                    "the row keys in (k1, k4]");
                Check.Sequence(["k0", "k1"], await RawKeysAsync(raw, new RawQuery(c.Pk) { Take = 2 }, c.Ct), "the first two row keys");
                Check.Sequence([], await RawKeysAsync(raw, new RawQuery(c.Pk) { FromRowKey = "k4", ToRowKey = "k1" }, c.Ct), "the row keys of a range whose lower bound is above its upper bound");
                Check.Sequence([], await RawKeysAsync(raw, new RawQuery(c.Pk + "none"), c.Ct), "the row keys of an unknown partition");
                await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => RawKeysAsync(raw, new RawQuery(c.Pk) { Take = 0 }, c.Ct), "a raw query with Take = 0");
            });

        s.Add("RawTable.Submit_AppliesInsertReplaceMergeAndDelete",
            "SubmitAsync applies its operations in one transaction and returns one entry each: a replace swaps every cell, a merge keeps the cells it does not name, a delete removes the row; an empty list writes nothing.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var raw = c.Capability<ConformanceItem, IRawTable>(store);
                Check.Equal(0, (await raw.SubmitAsync([], c.Ct)).Count, "the entries of an empty transaction");
                var inserted = await raw.SubmitAsync(
                [
                    RawOperation.Insert(Raw(c.Pk, "r1", ("A", RawCell.FromString("a1")), ("B", RawCell.FromString("b1")))),
                    RawOperation.Insert(Raw(c.Pk, "r2", ("A", RawCell.FromString("a2")), ("B", RawCell.FromString("b2")))),
                    RawOperation.Insert(Raw(c.Pk, "r3", ("A", RawCell.FromString("a3")))),
                ], c.Ct);
                Check.Equal(3, inserted.Count, "the entries of a transaction of three inserts");

                var results = await raw.SubmitAsync(
                [
                    RawOperation.Replace(Raw(c.Pk, "r1", ("A", RawCell.FromString("replaced")))),
                    RawOperation.Merge(Raw(c.Pk, "r2", ("A", RawCell.FromString("merged")))),
                    RawOperation.Delete(c.Pk, "r3"),
                    RawOperation.Insert(Raw(c.Pk, "r4", ("A", RawCell.FromString("a4")))),
                ], c.Ct);

                Check.Equal(4, results.Count, "the entries of a transaction of four operations");
                Check.Null(results[2], "the entry of a delete");
                var r1 = Check.NotNull(await raw.GetAsync(c.Pk, "r1", c.Ct), "the replaced row");
                Check.Equal("replaced", r1.Cells["A"].AsString(), "the replaced cell");
                Check.True(!r1.Cells.ContainsKey("B"), "a replace removes the cells it does not name");
                if (results[0] is { } replacedETag)
                    Check.Equal(replacedETag, r1.ETag, "the ETag of the replaced row, against the entry SubmitAsync returned");
                var r2 = Check.NotNull(await raw.GetAsync(c.Pk, "r2", c.Ct), "the merged row");
                Check.Equal("merged", r2.Cells["A"].AsString(), "the merged cell");
                Check.Equal("b2", r2.Cells["B"].AsString(), "a cell the merge does not name");
                Check.Sequence(["r1", "r2", "r4"], await RawKeysAsync(raw, new RawQuery(c.Pk), c.Ct), "the row keys after the transaction");
            });

        s.Add("RawTable.Submit_IsAllOrNothing_AndNamesTheFailure",
            "When one operation is refused nothing of the transaction is applied, and the failure of that operation is thrown: DuplicateKeyException for an insert of a stored row, EntityNotFoundException for a replace, merge or delete of a missing one, ConcurrencyConflictException for a condition on another version.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var raw = c.Capability<ConformanceItem, IRawTable>(store);
                await raw.SubmitAsync([RawOperation.Insert(Raw(c.Pk, "stored", ("A", RawCell.FromString("v1"))))], c.Ct);
                var staleETag = Check.NotNull((await raw.GetAsync(c.Pk, "stored", c.Ct))?.ETag, "the ETag of a raw row");
                await raw.SubmitAsync([RawOperation.Replace(Raw(c.Pk, "stored", ("A", RawCell.FromString("v2"))))], c.Ct);
                var fresh = RawOperation.Insert(Raw(c.Pk, "fresh", ("A", RawCell.FromString("new"))));

                await Check.NeutralAsync<DuplicateKeyException>(
                    () => raw.SubmitAsync([fresh, RawOperation.Insert(Raw(c.Pk, "stored"))], c.Ct), "a transaction with an insert of a stored row");
                await Check.NeutralAsync<EntityNotFoundException>(
                    () => raw.SubmitAsync([fresh, RawOperation.Replace(Raw(c.Pk, "missing"))], c.Ct), "a transaction with a replace of a missing row");
                await Check.NeutralAsync<EntityNotFoundException>(
                    () => raw.SubmitAsync([fresh, RawOperation.Merge(Raw(c.Pk, "missing", ("A", RawCell.FromString("x"))))], c.Ct), "a transaction with a merge of a missing row");
                await Check.NeutralAsync<EntityNotFoundException>(
                    () => raw.SubmitAsync([fresh, RawOperation.Delete(c.Pk, "missing")], c.Ct), "a transaction with a delete of a missing row");
                await Check.NeutralAsync<ConcurrencyConflictException>(
                    () => raw.SubmitAsync([fresh, RawOperation.Replace(Raw(c.Pk, "stored", ("A", RawCell.FromString("v3"))), staleETag)], c.Ct),
                    "a transaction with a replace on a stale ETag");
                await Check.NeutralAsync<ConcurrencyConflictException>(
                    () => raw.SubmitAsync([fresh, RawOperation.Delete(c.Pk, "stored", staleETag)], c.Ct), "a transaction with a delete on a stale ETag");

                Check.Sequence(["stored"], await RawKeysAsync(raw, new RawQuery(c.Pk), c.Ct), "the row keys after the refused transactions");
                Check.Equal("v2", (await raw.GetAsync(c.Pk, "stored", c.Ct))?.Cells["A"].AsString(), "the cell of the stored row after the refused transactions");

                var current = (await raw.GetAsync(c.Pk, "stored", c.Ct))!.ETag!;
                await raw.SubmitAsync([RawOperation.Merge(Raw(c.Pk, "stored", ("B", RawCell.FromInt32(1))), current)], c.Ct);
                current = (await raw.GetAsync(c.Pk, "stored", c.Ct))!.ETag!;
                await raw.SubmitAsync([RawOperation.Delete(c.Pk, "stored", current), fresh], c.Ct);
                Check.Sequence(["fresh"], await RawKeysAsync(raw, new RawQuery(c.Pk), c.Ct), "the row keys after two transactions on current ETags");
            });

        s.Add("RawTable.Submit_ThatCannotBeOneTransaction_ThrowsBatchNotAtomic",
            "A transaction over two partitions, of more than 100 operations, or that names a row twice throws BatchNotAtomicException and writes nothing; it is never split.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var raw = c.Capability<ConformanceItem, IRawTable>(store);

                await Check.NeutralAsync<BatchNotAtomicException>(
                    () => raw.SubmitAsync([RawOperation.Insert(Raw(c.Pk, "r1")), RawOperation.Insert(Raw(c.Pk + "x", "r1"))], c.Ct), "a transaction over two partitions");
                await Check.NeutralAsync<BatchNotAtomicException>(
                    () => raw.SubmitAsync(Enumerable.Range(0, 101).Select(i => RawOperation.Insert(Raw(c.Pk, "r" + i))).ToList(), c.Ct), "a transaction of 101 operations");
                await Check.NeutralAsync<BatchNotAtomicException>(
                    () => raw.SubmitAsync([RawOperation.Insert(Raw(c.Pk, "r1")), RawOperation.Merge(Raw(c.Pk, "r1"))], c.Ct), "a transaction that names a row twice");

                Check.Sequence([], await RawKeysAsync(raw, new RawQuery(c.Pk), c.Ct), "the row keys after the refused transactions");
                await raw.SubmitAsync(Enumerable.Range(0, 100).Select(i => RawOperation.Insert(Raw(c.Pk, "r" + i.ToString("D3")))).ToList(), c.Ct);
                Check.Equal(100, (await RawKeysAsync(raw, new RawQuery(c.Pk), c.Ct)).Count, "the row count after a transaction of exactly 100 operations");
            });
    }

    private static void TableStore(ScenarioBuilder s)
    {
        s.Add("TableStore.CreatesFindsListsAndDeletesATable",
            "ITableStore creates a table once (true, then false), finds and lists it, opens it as raw rows, and deletes it once (true, then false).",
            async c =>
            {
                var store = await c.ItemsAsync();
                var tables = c.Capability<ConformanceItem, ITableStore>(store);
                var name = "ck" + Guid.NewGuid().ToString("N");
                try
                {
                    Check.Equal(false, await tables.TableExistsAsync(name, c.Ct), "TableExists before the table is created");
                    Check.Equal(true, await tables.CreateTableAsync(name, c.Ct), "the first CreateTable");
                    Check.Equal(false, await tables.CreateTableAsync(name, c.Ct), "the second CreateTable");
                    Check.Equal(true, await tables.TableExistsAsync(name, c.Ct), "TableExists after the table is created");
                    Check.True((await Rows.ListAsync(tables.ListTablesAsync(c.Ct), c.Ct)).Contains(name), "ListTables holds the created table");

                    var table = tables.GetTable(name);
                    Check.Equal(name, table.TableName, "IRawTable.TableName");
                    await table.SubmitAsync([RawOperation.Insert(Raw("p", "r", ("A", RawCell.FromString("a"))))], c.Ct);
                    Check.Equal(false, await tables.CreateTableAsync(name, c.Ct), "CreateTable of a table with rows");
                    Check.Equal("a", (await table.GetAsync("p", "r", c.Ct))?.Cells["A"].AsString(), "a cell of the table after a second CreateTable (create-only)");
                }
                finally
                {
                    Check.Equal(true, await tables.DeleteTableAsync(name, CancellationToken.None), "the first DeleteTable");
                }
                Check.Equal(false, await tables.DeleteTableAsync(name, c.Ct), "the second DeleteTable");
                Check.Equal(false, await tables.TableExistsAsync(name, c.Ct), "TableExists after the table is deleted");
            });

        s.Add("TableStore.ATableThatDoesNotExist_FailsWithTableNotFound",
            "GetTable creates nothing: every operation on a table that does not exist throws TableNotFoundException, never the row-level EntityNotFoundException and never a null.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var tables = c.Capability<ConformanceItem, ITableStore>(store);
                var name = "ck" + Guid.NewGuid().ToString("N");
                var table = tables.GetTable(name);

                await Check.NeutralAsync<TableNotFoundException>(() => table.GetAsync("p", "r", c.Ct), "a raw read of a missing table");
                await Check.NeutralAsync<TableNotFoundException>(() => RawKeysAsync(table, new RawQuery("p"), c.Ct), "a raw query of a missing table");
                await Check.NeutralAsync<TableNotFoundException>(
                    () => table.SubmitAsync([RawOperation.Insert(Raw("p", "r"))], c.Ct), "a raw transaction on a missing table");
                Check.Equal(false, await tables.TableExistsAsync(name, c.Ct), "TableExists after the refused operations");
            });
    }
}
