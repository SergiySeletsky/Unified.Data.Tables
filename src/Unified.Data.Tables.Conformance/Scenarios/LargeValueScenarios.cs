using static Unified.Data.Tables.Conformance.ConformanceLeniencies;

namespace Unified.Data.Tables.Conformance.Scenarios;

/// <summary>
/// Tier 5: values larger than one cell and larger than one row, and batches of them, as a caller sees
/// them. Where the factory can read raw rows, the continuation rows are checked too.
/// </summary>
internal static class LargeValueScenarios
{
    // One table cell holds 32,000 characters and one row about a megabyte: the sizes below are a value of
    // several cells in one row, of two rows, of three rows, and one that no number of rows holds.
    private const int SeveralCells = 100_000;
    private const int TwoRows = 600_000;
    private const int ThreeRows = 1_000_000;
    private const int AboveTheOffloadThreshold = 3_200_000;

    public static void Add(ScenarioBuilder s)
    {
        s.Tier(ConformanceTier.LargeValues);
        SingleWrites(s);
        Reads(s);
        Projection(s);
        Deletes(s);
        Batches(s);
        Offload(s);
        Concurrency(s);
    }

    private static async Task<(ConformanceStore<ConformanceLargeItem> Kit, IStorage<ConformanceLargeItem> Store)> LargeAsync(
        ConformanceContext c, ConformanceStoreSettings? settings = null)
    {
        var kit = await c.StoreAsync<ConformanceLargeItem>(settings);
        return (kit, kit.Storage);
    }

    /// <summary>The physical row keys of the scenario's partition, checked only where the factory can read them.</summary>
    private static async Task PhysicalAsync(ConformanceContext c, ConformanceStore<ConformanceLargeItem> kit, IEnumerable<string> expected, string when)
    {
        if (await Rows.RawKeysAsync(kit, c.Pk, c.Ct) is { } keys)
            Check.Sequence(expected, keys, "the physical row keys " + when);
    }

    private static void SingleWrites(ScenarioBuilder s)
    {
        s.Add("Value.LargerThanOneCell_RoundTrips",
            "A value marked [TableValueSplit] that needs several cells of one row is stored without loss and read back as written; the entity is still one row and one entity.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                var body = Rows.Text(SeveralCells);

                var created = await store.CreateAsync(Rows.Large(c.Id("r1"), body, "title"), c.Ct);

                Check.NotEmpty(created.ETag, "the ETag of the created entity");
                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the entity");
                Check.Text(body, read.Body, "the value read back");
                Check.Equal("title", read.Title, "a plain column beside the value");
                Check.Equal(1, await store.CountAsync(c.Pk, c.Ct), "the entity count");
                await PhysicalAsync(c, kit, ["r1"], "of a value of several cells");
            });

        s.Add("Value.LargerThanOneRow_RoundTrips_AndCountsAsOneEntity",
            "A value that needs more than one row is stored across continuation rows and read back as written; counts and queries see one entity, never a continuation row.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                var two = Rows.Text(TwoRows);
                var three = Rows.Text(ThreeRows, 3);

                await store.CreateAsync(Rows.Large(c.Id("r1"), two, "two rows"), c.Ct);
                await store.UpsertAsync(Rows.Large(c.Id("r2"), three, "three rows"), c.Ct);

                Check.Text(two, (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the two-row value read back");
                Check.Text(three, (await store.OneAsync(c.Id("r2"), c.Ct))?.Body, "the three-row value read back");
                Check.Equal(2, await store.CountAsync(c.Pk, c.Ct), "the entity count");
                Check.Equal(2, await store.CountAsync(null, c.Ct), "the entity count of the table");
                Check.Sequence([c.Id("r1"), c.Id("r2")], Rows.Ids(await store.QueryAsync(c.Pk, c.Ct)), "the ids of the partition");
                Check.Equal(true, await store.ExistsAsync(c.Id("r1"), c.Ct), "Exists of an entity with continuation rows");
                await PhysicalAsync(c, kit, ["r1", "r1~0001", "r2", "r2~0001", "r2~0002"], "of a two-row and a three-row value");
            });

        s.Add("Value.Binary_LargerThanOneRow_RoundTrips",
            "A byte array marked [TableValueSplit] that needs more than one row is read back byte for byte, beside a text value on the same entity.",
            async c =>
            {
                var (_, store) = await LargeAsync(c);
                var payload = Rows.Bytes(1_200_000, 7);
                var entity = Rows.Large(c.Id("r1"), Rows.Text(SeveralCells), "both");
                entity.Payload = payload;

                await store.CreateAsync(entity, c.Ct);

                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the entity");
                Check.Bytes(payload, read.Payload, "the binary value read back");
                Check.Text(Rows.Text(SeveralCells), read.Body, "the text value beside it");
            });

        s.Add("Value.GrowsAndShrinks_LeavingNoStrayRows",
            "Replacing a value by a larger and then a smaller one (upsert, update, last-writer-wins update) always reads back the last value, and leaves exactly the continuation rows that value needs.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                await store.CreateAsync(Rows.Large(c.Id("r1"), "small"), c.Ct);
                await PhysicalAsync(c, kit, ["r1"], "of a small value");

                await store.UpsertAsync(Rows.Large(c.Id("r1"), Rows.Text(ThreeRows)), c.Ct);
                Check.Text(Rows.Text(ThreeRows), (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value after it grew to three rows");
                await PhysicalAsync(c, kit, ["r1", "r1~0001", "r1~0002"], "after the value grew to three rows");

                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the entity");
                read.Body = Rows.Text(TwoRows, 1);
                var updated = await store.UpdateAsync(read, ConcurrencyMode.Strict, c.Ct);
                Check.NotEmpty(updated.ETag, "the ETag after the update");
                Check.Text(Rows.Text(TwoRows, 1), (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value after it shrank to two rows");
                await PhysicalAsync(c, kit, ["r1", "r1~0001"], "after the value shrank to two rows");

                await store.UpdateAsync(Rows.Large(c.Id("r1"), "small again"), ConcurrencyMode.LastWriterWins, c.Ct);
                Check.Equal("small again", (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value after it shrank to one cell");
                await PhysicalAsync(c, kit, ["r1"], "after the value shrank to one cell");
                Check.Equal(1, await store.CountAsync(c.Pk, c.Ct), "the entity count");
            });

        s.Add("Value.Update_KeepsTheETagRules",
            "An entity with a large value keeps the update rules of a plain one: a stale ETag is a ConcurrencyConflictException that leaves the stored value whole, a missing row an EntityNotFoundException, and a create of a stored id a DuplicateKeyException.",
            [ETagRulesDiffer, FailureTypesDiffer],
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                await store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(TwoRows)), c.Ct);
                var stale = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the entity");
                await store.UpsertAsync(Rows.Large(c.Id("r1"), Rows.Text(TwoRows, 2)), c.Ct);

                stale.Body = Rows.Text(ThreeRows, 4);
                await Check.NeutralAsync<ConcurrencyConflictException>(() => store.UpdateAsync(stale, c.Ct), "an update of a large value on a stale ETag");
                await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(ThreeRows, 5)), c.Ct), "a create of a stored id with a large value");
                await Check.NeutralAsync<EntityNotFoundException>(
                    () => store.UpdateAsync(Rows.Large(c.Id("never"), Rows.Text(TwoRows)), ConcurrencyMode.LastWriterWins, c.Ct),
                    "an update of a missing row with a large value");

                Check.Text(Rows.Text(TwoRows, 2), (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the stored value after the refused writes");
                await PhysicalAsync(c, kit, ["r1", "r1~0001"], "after the refused writes");
            });

        s.Add("Value.MergeOfAPlainColumn_KeepsTheLargeValue",
            "A builder update of a plain column of an entity with a large value keeps the value whole.",
            async c =>
            {
                var (_, store) = await LargeAsync(c);
                await store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(TwoRows), "old title"), c.Ct);

                await store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Title, "new title"), c.Ct);

                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the entity");
                Check.Equal("new title", read.Title, "the merged column");
                Check.Text(Rows.Text(TwoRows), read.Body, "the large value after the merge");
            });
    }

    private static void Reads(ScenarioBuilder s)
    {
        s.Add("Read.AContinuationRowIsNotAnEntity",
            "The key of a continuation row addresses no entity: a read of it is null, Exists is false, and a delete of it removes nothing of the entity it belongs to.",
            async c =>
            {
                var (_, store) = await LargeAsync(c);
                await store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(TwoRows)), c.Ct);

                Check.Null(await store.OneAsync(c.Id("r1~0001"), c.Ct), "a read of a continuation row's key");
                Check.Equal(false, await store.ExistsAsync(c.Id("r1~0001"), c.Ct), "Exists of a continuation row's key");
                await store.DeleteAsync(c.Id("r1~0001"), c.Ct);

                Check.Text(Rows.Text(TwoRows), (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value after a delete of its continuation row's key");
            });

        s.Add("Read.QueriesStreamsAndPages_ReturnWholeEntities_NeverContinuationRows",
            "Every query form returns each entity once with its value assembled, under a prefix too; a page may hold fewer entities than its size (a continuation row takes a place), and paging to the null token returns them all.",
            async c =>
            {
                var (_, store) = await LargeAsync(c);
                var bodies = new Dictionary<string, string>
                {
                    [c.Id("a1")] = Rows.Text(TwoRows, 1),
                    [c.Id("a1x")] = "small",
                    [c.Id("a2")] = Rows.Text(ThreeRows, 2),
                    [c.Id("b1")] = Rows.Text(SeveralCells, 3),
                    [c.Id("b2")] = Rows.Text(TwoRows, 4),
                };
                foreach (var body in bodies)
                    await store.CreateAsync(Rows.Large(body.Key, body.Value, "listed"), c.Ct);
                var ids = bodies.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

                void Whole(IEnumerable<ConformanceLargeItem> entities, IEnumerable<string> expected, string what)
                {
                    var list = entities.ToList();
                    Check.Sequence(expected, Rows.Ids(list), "the ids of " + what);
                    foreach (var entity in list)
                        Check.Text(bodies[entity.Id], entity.Body, $"the value of '{entity.Id}' in {what}");
                }

                Whole(await store.QueryAsync(c.Pk, c.Ct), ids, "a partition query");
                Whole(await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct), ids, "a bounded query");
                Whole(await store.QueryAsync(new QueryOptions { Partition = c.Pk, RowKeyPrefix = "a1" }, c.Ct), [c.Id("a1"), c.Id("a1x")], "a query under the prefix 'a1'");
                Whole(await store.QueryAsync(new QueryOptions { Partition = c.Pk, Take = 2 }, c.Ct), ids.Take(2), "a query with Take = 2");
                Whole(await store.QueryAsync(x => x.Title == "listed", c.Pk, null, c.Ct), ids, "a predicate query");
                Whole(await Rows.ListAsync(store.QueryStreamAsync(new QueryOptions { Partition = c.Pk }, c.Ct), c.Ct), ids, "a stream");
                Whole(await Rows.PageAllAsync(store, new QueryOptions { Partition = c.Pk, Take = 2 }, c.Ct), ids, "a paged query (page size 2)");
                Whole(await Rows.PageAllAsync(store, new QueryOptions { Partition = c.Pk, RowKeyPrefix = "a", Take = 3 }, c.Ct),
                    [c.Id("a1"), c.Id("a1x"), c.Id("a2")], "a paged query under the prefix 'a'");
            });
    }

    private static void Projection(ScenarioBuilder s)
    {
        s.Add("Read.Select_ReturnsThePlainColumnsItNames_WithoutTheLargeValue",
            "A query that selects plain columns of an entity with a large value returns each entity once with those columns, and leaves the value it did not select at null.",
            ProjectionIgnored,
            async c =>
            {
                var (_, store) = await LargeAsync(c);
                await store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(TwoRows), "large"), c.Ct);
                await store.CreateAsync(Rows.Large(c.Id("r2"), "small body", "small"), c.Ct);
                var options = new QueryOptions { Partition = c.Pk, Select = ["Title"] };

                foreach (var (rows, what) in new[]
                {
                    (await store.QueryAsync(options, c.Ct), "a projected query"),
                    (await Rows.ListAsync(store.QueryStreamAsync(options, c.Ct), c.Ct), "a projected stream"),
                    (await Rows.PageAllAsync(store, options with { Take = 1 }, c.Ct), "a projected paged query"),
                })
                {
                    Check.Sequence([c.Id("r1"), c.Id("r2")], Rows.Ids(rows), "the ids of " + what);
                    Check.Sequence(["large", "small"], rows.Select(r => r.Title), "the selected column of " + what);
                    Check.Sequence([null, null], rows.Select(r => r.Body), "the large value " + what + " did not select");
                }
                Check.Text(Rows.Text(TwoRows), (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value in a whole read after the projected ones");
            });
    }

    private static void Deletes(ScenarioBuilder s)
    {
        s.Add("Delete.RemovesTheEntityWithItsContinuationRows",
            "DeleteAsync of an entity with a value of several rows removes all of them and nothing of its neighbours; the id can be created again.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                await store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(ThreeRows)), c.Ct);
                await store.CreateAsync(Rows.Large(c.Id("r1x"), "small"), c.Ct);
                await store.CreateAsync(Rows.Large(c.Id("r2"), Rows.Text(TwoRows, 2)), c.Ct);

                await store.DeleteAsync(c.Id("r1"), c.Ct);
                await store.DeleteAsync(c.Id("r1"), c.Ct);

                Check.Null(await store.OneAsync(c.Id("r1"), c.Ct), "the deleted entity");
                Check.Equal(2, await store.CountAsync(c.Pk, c.Ct), "the entity count after the delete");
                await PhysicalAsync(c, kit, ["r1x", "r2", "r2~0001"], "after the delete");
                Check.Text(Rows.Text(TwoRows, 2), (await store.OneAsync(c.Id("r2"), c.Ct))?.Body, "the neighbour's value after the delete");

                await store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(TwoRows, 9)), c.Ct);
                Check.Text(Rows.Text(TwoRows, 9), (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value of an entity created at the deleted id");
            });

        s.Add("DeletePartition.CountsEntities_AndLeavesNoRow",
            "DeletePartitionAsync of a partition of entities with large values returns the number of entities (continuation rows are not counted) and leaves no row of the partition.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                await store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(ThreeRows)), c.Ct);
                await store.CreateAsync(Rows.Large(c.Id("r2"), "small"), c.Ct);
                await store.CreateAsync(Rows.Large(c.Id("r3"), Rows.Text(TwoRows)), c.Ct);
                await store.CreateAsync(Rows.Large(c.Pk + "x|r1", Rows.Text(TwoRows)), c.Ct);

                Check.Equal(3, await store.DeletePartitionAsync(c.Pk, c.Ct), "the count DeletePartitionAsync returns");

                Check.Equal(0, await store.CountAsync(c.Pk, c.Ct), "the entity count of the deleted partition");
                await PhysicalAsync(c, kit, [], "after the partition delete");
                Check.Text(Rows.Text(TwoRows), (await store.OneAsync(c.Pk + "x|r1", c.Ct))?.Body, "the value in the other partition");
            });
    }

    private static void Batches(ScenarioBuilder s)
    {
        s.Add("CreateBatch.MixedSizes_StoresEveryEntity",
            "CreateBatchAsync of small entities and entities with values of several rows stores every one and returns their number; every ETag is reset to null.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                var batch = new[] { Rows.Large(c.Id("r1"), "small"), Rows.Large(c.Id("r2"), Rows.Text(TwoRows)), Rows.Large(c.Id("r3"), Rows.Text(SeveralCells, 3)) };

                Check.Equal(3, await store.CreateBatchAsync(batch, c.Ct), "the count CreateBatchAsync returns");

                Check.Sequence([null, null, null], batch.Select(e => e.ETag), "the ETags after the batch");
                Check.Equal("small", (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the small value");
                Check.Text(Rows.Text(TwoRows), (await store.OneAsync(c.Id("r2"), c.Ct))?.Body, "the two-row value");
                Check.Text(Rows.Text(SeveralCells, 3), (await store.OneAsync(c.Id("r3"), c.Ct))?.Body, "the value of several cells");
                Check.Equal(3, await store.CountAsync(c.Pk, c.Ct), "the entity count");
                await PhysicalAsync(c, kit, ["r1", "r2", "r2~0001", "r3"], "after the batch");
            });

        s.Add("CreateBatch.NearTheTransactionLimits_KeepsEveryEntityWhole",
            "A create batch whose rows exceed one transaction's payload, and one of more than 100 entities, are split between transactions without ever splitting an entity: every value reads back whole.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                // Six values of two rows (about 1.2 MB each): two fit one transaction's budget, three do not.
                var heavy = Enumerable.Range(0, 6).Select(i => Rows.Large(c.Id("h" + i), Rows.Text(TwoRows, i))).ToList();
                Check.Equal(6, await store.CreateBatchAsync(heavy, c.Ct), "the count of a batch of six two-row values");
                for (var i = 0; i < 6; i++)
                    Check.Text(Rows.Text(TwoRows, i), (await store.OneAsync(c.Id("h" + i), c.Ct))?.Body, $"the value of 'h{i}'");
                await PhysicalAsync(c, kit, Enumerable.Range(0, 6).SelectMany(i => new[] { "h" + i, "h" + i + "~0001" }), "after the batch of six two-row values");

                var many = Enumerable.Range(0, 150).Select(i => Rows.Large(c.Id("m" + i.ToString("D3")), "v" + i)).ToList();
                Check.Equal(150, await store.CreateBatchAsync(many, c.Ct), "the count of a batch of 150 entities");
                Check.Equal(156, await store.CountAsync(c.Pk, c.Ct), "the entity count");
                Check.Equal("v149", (await store.OneAsync(c.Id("m149"), c.Ct))?.Body, "the value of the last entity");
            });

        s.Add("CreateBatch.ExistingId_FailsItsTransactionWhole",
            "A create batch that names a stored id throws DuplicateKeyException naming it, and nothing of that transaction is stored: no entity and no continuation row of a neighbour in it.",
            [BatchTransactionsDiffer, FailureTypesDiffer],
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                await store.CreateAsync(Rows.Large(c.Id("r2"), "original"), c.Ct);

                var duplicate = await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.CreateBatchAsync([Rows.Large(c.Id("r1"), Rows.Text(TwoRows)), Rows.Large(c.Id("r2"), "clash"), Rows.Large(c.Id("r3"), "c")], c.Ct),
                    "a create batch that names a stored id");

                Check.Equal(c.Id("r2"), duplicate.Id, "DuplicateKeyException.Id (the row that exists)");
                Check.Sequence([c.Id("r2")], Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct)), "the ids after the refused batch");
                Check.Equal("original", (await store.OneAsync(c.Id("r2"), c.Ct))?.Body, "the stored value after the refused batch");
                await PhysicalAsync(c, kit, ["r2"], "after the refused batch");
            });

        s.Add("CreateBatch.FailingTransaction_KeepsTheEarlierOnesWhole",
            "When the second transaction of a create batch fails, the entities of the first stay stored with all their rows, and nothing of the second or of a later one is stored.",
            BatchTransactionsDiffer,
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                await store.CreateAsync(Rows.Large(c.Id("r3"), "original"), c.Ct);
                var batch = Enumerable.Range(0, 6).Select(i => Rows.Large(c.Id("r" + i), Rows.Text(TwoRows, i))).ToList();

                var duplicate = await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.CreateBatchAsync(batch, c.Ct), "a create batch whose second transaction names a stored id");

                Check.Equal(c.Id("r3"), duplicate.Id, "DuplicateKeyException.Id");
                Check.Sequence([c.Id("r0"), c.Id("r1"), c.Id("r3")],
                    Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct)), "the ids after the batch failed in its second transaction");
                Check.Text(Rows.Text(TwoRows, 1), (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value of an entity of the first transaction");
                Check.Equal("original", (await store.OneAsync(c.Id("r3"), c.Ct))?.Body, "the stored value");
                await PhysicalAsync(c, kit, ["r0", "r0~0001", "r1", "r1~0001", "r3"], "after the batch failed in its second transaction");
            });

        s.Add("Batch.ARowNamedTwice_IsRefusedByACreate_AndWrittenOnceByAnUpsert",
            "For a type with large values a create batch that names a row twice throws DuplicateKeyException before anything is written, and an upsert batch writes the row once, with its last occurrence, counting every entity passed; the two spellings of a one-segment id are one row.",
            BatchTransactionsDiffer,
            async c =>
            {
                var (kit, store) = await LargeAsync(c);

                var repeated = await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.CreateBatchAsync([Rows.Large(c.Id("r1"), "a"), Rows.Large(c.Id("r2"), "b"), Rows.Large(c.Id("r1"), "c")], c.Ct),
                    "a create batch that names a row twice");
                Check.Equal(c.Id("r1"), repeated.Id, "DuplicateKeyException.Id of a repeated row");
                await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.CreateBatchAsync([Rows.Large(c.Pk, "one segment"), Rows.Large(c.Id("r1"), "x"), Rows.Large(c.Pk + "|" + c.Pk, "two segments")], c.Ct),
                    "a create batch that names a row by both spellings of its id");
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the entity count after the refused batches");
                await PhysicalAsync(c, kit, [], "after the refused batches");

                Check.Equal(3, await store.UpsertBatchAsync(
                    [Rows.Large(c.Id("r1"), Rows.Text(TwoRows)), Rows.Large(c.Id("r2"), "x"), Rows.Large(c.Id("r1"), "last")], c.Ct),
                    "the count of an upsert batch that names a row twice");
                Check.Equal("last", (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value of the row named twice");
                Check.Equal(3, await store.UpsertBatchAsync(
                    [Rows.Large(c.Pk, "first"), Rows.Large(c.Id("r3"), "x"), Rows.Large(c.Pk + "|" + c.Pk, "last")], c.Ct),
                    "the count of an upsert batch that names a row by both spellings");
                Check.Equal("last", (await store.OneAsync(c.Pk, c.Ct))?.Body, "the value of the row named by both spellings");
                Check.Equal(4, await store.CountAsync(c.Pk, c.Ct), "the entity count");
                await PhysicalAsync(c, kit, new[] { c.Pk, "r1", "r2", "r3" }.OrderBy(k => k, StringComparer.Ordinal), "after the upsert batches");
            });

        s.Add("UpsertBatch.GrowsAndShrinksStoredValues",
            "An upsert batch replaces stored values by larger and smaller ones and inserts new entities; each reads back as last written with exactly the rows it needs, and every ETag is reset to null.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                await store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(ThreeRows)), c.Ct);
                await store.CreateAsync(Rows.Large(c.Id("r2"), "small"), c.Ct);
                var batch = new[] { Rows.Large(c.Id("r1"), "now small"), Rows.Large(c.Id("r2"), Rows.Text(TwoRows, 2)), Rows.Large(c.Id("r3"), Rows.Text(TwoRows, 3)) };

                Check.Equal(3, await store.UpsertBatchAsync(batch, c.Ct), "the count UpsertBatchAsync returns");

                Check.Sequence([null, null, null], batch.Select(e => e.ETag), "the ETags after the batch");
                Check.Equal("now small", (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value that shrank");
                Check.Text(Rows.Text(TwoRows, 2), (await store.OneAsync(c.Id("r2"), c.Ct))?.Body, "the value that grew");
                Check.Text(Rows.Text(TwoRows, 3), (await store.OneAsync(c.Id("r3"), c.Ct))?.Body, "the value that was inserted");
                await PhysicalAsync(c, kit, ["r1", "r2", "r2~0001", "r3", "r3~0001"], "after the upsert batch");
            });

        s.Add("Batch.AcrossPartitions_StoresEveryEntity",
            "A batch of entities with large values in two partitions stores all of them.",
            async c =>
            {
                var (_, store) = await LargeAsync(c);
                await store.CreateBatchAsync([Rows.Large(c.Id("r1"), Rows.Text(TwoRows)), Rows.Large(c.Pk + "x|r1", Rows.Text(TwoRows, 1))], c.Ct);

                Check.Text(Rows.Text(TwoRows), (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value in the first partition");
                Check.Text(Rows.Text(TwoRows, 1), (await store.OneAsync(c.Pk + "x|r1", c.Ct))?.Body, "the value in the second partition");
                Check.Equal(2, await store.CountAsync(null, c.Ct), "the entity count of the table");
            });

        s.Add("Cancellation.AWriteWithACancelledToken_ThrowsAndChangesNothing",
            "Every write of an entity with a large value given a token that is already cancelled throws OperationCanceledException and leaves the stored value whole.",
            CancellationNotObserved,
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                await store.CreateAsync(Rows.Large(c.Id("a"), Rows.Text(TwoRows)), c.Ct);
                var read = Check.NotNull(await store.OneAsync(c.Id("a"), c.Ct), "the entity");
                read.Body = "changed";

                await Check.CancelledAsync(() => store.CreateAsync(Rows.Large(c.Id("b"), Rows.Text(TwoRows)), Rows.Cancelled), "CreateAsync");
                await Check.CancelledAsync(() => store.UpsertAsync(Rows.Large(c.Id("a"), "changed"), Rows.Cancelled), "UpsertAsync");
                await Check.CancelledAsync(() => store.UpdateAsync(read, Rows.Cancelled), "UpdateAsync");
                await Check.CancelledAsync(() => store.CreateBatchAsync([Rows.Large(c.Id("c"), "x")], Rows.Cancelled), "CreateBatchAsync");
                await Check.CancelledAsync(() => store.UpsertBatchAsync([Rows.Large(c.Id("a"), "changed")], Rows.Cancelled), "UpsertBatchAsync");
                await Check.CancelledAsync(() => store.DeleteAsync(c.Id("a"), Rows.Cancelled), "DeleteAsync");
                await Check.CancelledAsync(() => store.DeletePartitionAsync(c.Pk, Rows.Cancelled), "DeletePartitionAsync");

                Check.Text(Rows.Text(TwoRows), (await store.OneAsync(c.Id("a"), c.Ct))?.Body, "the stored value after the cancelled writes");
                Check.Equal(1, await store.CountAsync(null, c.Ct), "the entity count after the cancelled writes");
                await PhysicalAsync(c, kit, ["a", "a~0001"], "after the cancelled writes");
            });
    }

    private static void Offload(ScenarioBuilder s)
    {
        s.Add("Offload.ValueTooLargeForRows_WithoutBlobOffload_IsRefused",
            "A store without blob offload refuses a value too large for table rows with OversizedValueException (it names the value and its size) and stores nothing; nothing is ever trimmed.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c);

                var refused = await Check.ThrowsAsync<OversizedValueException>(
                    () => store.CreateAsync(Rows.Large(c.Id("r1"), Rows.Text(AboveTheOffloadThreshold)), c.Ct),
                    "a create of a value too large for table rows, in a store without blob offload");
                Check.Equal(nameof(ConformanceLargeItem.Body), refused.ColumnName, "OversizedValueException.ColumnName");
                await Check.ThrowsAsync<OversizedValueException>(
                    () => store.UpsertBatchAsync([Rows.Large(c.Id("r2"), "small"), Rows.Large(c.Id("r3"), Rows.Text(AboveTheOffloadThreshold))], c.Ct),
                    "an upsert batch with such a value");

                Check.Equal(0, await store.CountAsync(null, c.Ct), "the entity count after the refused writes");
                await PhysicalAsync(c, kit, [], "after the refused writes");
            });

        s.Add("Offload.ValueTooLargeForRows_RoundTripsThroughABlob",
            "A store with blob offload stores a value too large for table rows without loss: it reads back as written from every read form, is replaced by a smaller and a larger value, and is gone after a delete.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c, new ConformanceStoreSettings { BlobOffload = true });
                var huge = Rows.Text(AboveTheOffloadThreshold);

                await store.CreateAsync(Rows.Large(c.Id("r1"), huge, "offloaded"), c.Ct);
                await store.CreateBatchAsync([Rows.Large(c.Id("r2"), "small"), Rows.Large(c.Id("r3"), Rows.Text(AboveTheOffloadThreshold, 3))], c.Ct);

                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the entity");
                Check.Text(huge, read.Body, "the offloaded value read back");
                Check.Equal("offloaded", read.Title, "a plain column beside the offloaded value");
                Check.Text(Rows.Text(AboveTheOffloadThreshold, 3), (await store.OneAsync(c.Id("r3"), c.Ct))?.Body, "the offloaded value of a batch");
                Check.Text(huge, (await store.QueryAsync(new QueryOptions { Partition = c.Pk, RowKeyPrefix = "r1" }, c.Ct)).Single().Body, "the offloaded value in a query");
                Check.Equal(3, await store.CountAsync(c.Pk, c.Ct), "the entity count");
                // The value is in a blob: the entity is one row.
                await PhysicalAsync(c, kit, ["r1", "r2", "r3"], "of offloaded values");

                read.Body = Rows.Text(TwoRows);
                await store.UpdateAsync(read, c.Ct);
                Check.Text(Rows.Text(TwoRows), (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value after it shrank into table rows");
                await store.UpsertAsync(Rows.Large(c.Id("r1"), Rows.Text(AboveTheOffloadThreshold, 5)), c.Ct);
                Check.Text(Rows.Text(AboveTheOffloadThreshold, 5), (await store.OneAsync(c.Id("r1"), c.Ct))?.Body, "the value after it grew into a blob again");

                await store.DeleteAsync(c.Id("r1"), c.Ct);
                Check.Null(await store.OneAsync(c.Id("r1"), c.Ct), "the deleted entity");
                Check.Equal(2, await store.DeletePartitionAsync(c.Pk, c.Ct), "the count DeletePartitionAsync returns");
                await PhysicalAsync(c, kit, [], "after the deletes");
            });
    }

    private static void Concurrency(ScenarioBuilder s)
    {
        s.Add("Concurrency.UpsertsOfOneEntity_AllSucceed_AndLeaveOneWholeValue",
            "Four concurrent upserts of one entity whose value grows and shrinks all succeed (an upsert that loses a race reads again and retries), and the entity is left with exactly one of their values, whole.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                string[] bodies = ["small", Rows.Text(TwoRows, 1), Rows.Text(ThreeRows, 2), Rows.Text(TwoRows, 3)];

                for (var round = 0; round < 3; round++)
                {
                    await Task.WhenAll(bodies.Select(body => Task.Run(() => store.UpsertAsync(Rows.Large(c.Id("r1"), body), c.Ct), c.Ct)));

                    // Read from the table, not from a cache two writers may have raced to fill.
                    var stored = (await store.QueryAsync(new QueryOptions { Partition = c.Pk, RowKeyPrefix = "r1" }, c.Ct)).Single().Body;
                    var winner = Array.FindIndex(bodies, body => string.Equals(body, stored, StringComparison.Ordinal));
                    Check.True(winner >= 0, $"the stored value is one of the four that were written (round {round})");
                    var parts = winner switch { 0 => 0, 2 => 2, _ => 1 };
                    await PhysicalAsync(c, kit, new[] { "r1" }.Concat(Enumerable.Range(1, parts).Select(i => "r1~" + i.ToString("D4"))),
                        $"of the value that won round {round}");
                }
                Check.Equal(1, await store.CountAsync(c.Pk, c.Ct), "the entity count");
            });

        s.Add("Concurrency.UpsertsAndDeletesOfOneEntity_LeaveItWholeOrGone",
            "Concurrent upserts and deletes of one entity all succeed, and leave either no row at all or one whole value: never a continuation row without its entity.",
            async c =>
            {
                var (kit, store) = await LargeAsync(c);
                var large = Rows.Text(TwoRows);

                for (var round = 0; round < 3; round++)
                {
                    await Task.WhenAll(
                        Task.Run(() => store.UpsertAsync(Rows.Large(c.Id("r1"), large), c.Ct), c.Ct),
                        Task.Run(() => store.DeleteAsync(c.Id("r1"), c.Ct), c.Ct),
                        Task.Run(() => store.UpsertAsync(Rows.Large(c.Id("r1"), "small"), c.Ct), c.Ct),
                        Task.Run(() => store.DeleteAsync(c.Id("r1"), c.Ct), c.Ct));

                    var stored = (await store.QueryAsync(new QueryOptions { Partition = c.Pk, RowKeyPrefix = "r1" }, c.Ct)).SingleOrDefault();
                    if (stored is null)
                        await PhysicalAsync(c, kit, [], $"when the entity is gone (round {round})");
                    else if (stored.Body == "small")
                        await PhysicalAsync(c, kit, ["r1"], $"when the small value won (round {round})");
                    else
                    {
                        Check.Text(large, stored.Body, $"the stored value (round {round})");
                        await PhysicalAsync(c, kit, ["r1", "r1~0001"], $"when the large value won (round {round})");
                    }
                }
            });
    }
}
