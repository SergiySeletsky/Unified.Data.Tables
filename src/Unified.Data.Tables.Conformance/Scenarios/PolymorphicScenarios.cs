using static Unified.Data.Tables.Conformance.ConformanceLeniencies;

namespace Unified.Data.Tables.Conformance.Scenarios;

/// <summary>Tier 4: every <see cref="IPolymorphicStorage{TBase}"/> member, discriminators and marker rows.</summary>
internal static class PolymorphicScenarios
{
    private const string Published = "_Published";
    private const string Attempts = "_Attempts";

    public static void Add(ScenarioBuilder s)
    {
        s.Tier(ConformanceTier.Polymorphic);
        Writes(s);
        Reads(s);
        Markers(s);
        Deletes(s);
        Batches(s);
        Failures(s);
    }

    private static TableKey Key(ConformanceContext c, string rowKey) => new(c.Pk, rowKey);

    private static Dictionary<string, object> Columns(params (string Name, object Value)[] columns)
    {
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in columns)
            map[name] = value;
        return map;
    }

    private static List<TableKey> Keys(IEnumerable<PolymorphicEntry<ConformanceEvent>> entries) => entries.Select(e => e.Key).ToList();

    private static void Writes(ScenarioBuilder s)
    {
        s.Add("EnsureCreated.IsIdempotent_AndOptional",
            "EnsureCreatedAsync can be called any number of times, before or after the first write; a store that was never asked creates its table on first use.",
            async c =>
            {
                var store = await c.EventsAsync();
                await store.InsertAsync(Key(c, "r1"), Rows.Placed("first use creates the table"), c.Ct);
                await store.EnsureCreatedAsync(c.Ct);
                await store.EnsureCreatedAsync(c.Ct);
                Check.Equal(1, await store.CountAsync(null, c.Ct), "the row count after EnsureCreatedAsync");
            });

        s.Add("Insert.StoresTheRow_AsTheTypeWritten",
            "InsertAsync stores the item under its key and returns an entry with the derived instance, its discriminator, an ETag, and a null Timestamp (a write does not report one).",
            async c =>
            {
                var store = await c.EventsAsync();
                var entry = await store.InsertAsync(Key(c, "r1"), Rows.Placed("order", 3), c.Ct);

                Check.Equal(Key(c, "r1"), entry.Key, "the key of the inserted entry");
                var placed = Check.NotNull(entry.Item as ConformanceOrderPlaced, "the inserted entry's item, as the type written");
                Check.Equal(3, placed.Quantity, "a column of the derived type");
                Check.NotEmpty(entry.Discriminator, "the discriminator of the inserted entry");
                Check.NotEmpty(entry.ETag, "the ETag of the inserted entry");
                Check.Null(entry.Timestamp, "the Timestamp of an entry a write returns");

                var read = Check.NotNull(await store.GetAsync(Key(c, "r1"), c.Ct), "the row, read back");
                Check.Equal(entry.ETag, read.ETag, "the ETag read back, against the one InsertAsync returned");
                Check.Equal(entry.Discriminator, read.Discriminator, "the discriminator read back");
                Check.NotNull((object?)read.Timestamp, "the Timestamp of an entry a read returns");
            });

        s.Add("Insert.ExistingKey_ThrowsDuplicateKey_AndKeepsTheStoredRow",
            "InsertAsync of a stored key throws DuplicateKeyException (the neutral type, naming the key) and leaves the stored row: the insert is the de-duplication guarantee of an append-only log.",
            FailureTypesDiffer,
            async c =>
            {
                var store = await c.EventsAsync();
                await store.InsertAsync(Key(c, "r1"), Rows.Placed("original"), c.Ct);

                var duplicate = await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.InsertAsync(Key(c, "r1"), Rows.Shipped("intruder"), c.Ct), "an insert of a stored key");
                Check.Equal(Key(c, "r1").ToId(), duplicate.Id, "DuplicateKeyException.Id");

                Check.Equal("original", (await store.GetAsync(Key(c, "r1"), c.Ct))?.Item?.Subject, "Subject of the stored row after the refused insert");
            });

        s.Add("Upsert.InsertsOrReplaces_AndMayChangeTheType",
            "UpsertAsync creates a missing row and replaces a stored one unconditionally, columns and type: the row reads back as the last item written, with a new ETag.",
            async c =>
            {
                var store = await c.EventsAsync();
                var first = await store.UpsertAsync(Key(c, "r1"), Rows.Placed("placed", 2), c.Ct);
                Check.True(first.Item is ConformanceOrderPlaced, "the upserted entry carries the item written");

                var second = await store.UpsertAsync(Key(c, "r1"), Rows.Shipped("shipped"), c.Ct);

                Check.NotEqual(first.ETag, second.ETag, "the ETag after the second upsert");
                Check.Null(second.Timestamp, "the Timestamp of an entry a write returns");
                var read = Check.NotNull(await store.GetAsync(Key(c, "r1"), c.Ct), "the row");
                var shipped = Check.NotNull(read.Item as ConformanceOrderShipped, "the row, as the type of the last upsert");
                Check.Equal("carrier of shipped", shipped.Carrier, "a column of the new type");
                Check.True(!read.Columns.ContainsKey("Customer"), "a column of the replaced type is gone after the upsert");
                Check.Equal(1, await store.CountAsync(c.Pk, c.Ct), "the row count");
            });

        s.Add("Keys.AreStoredAsWritten",
            "A polymorphic store does not normalize keys: two keys that differ in case are two rows, and an empty partition key or row key is refused with an ArgumentException.",
            ArgumentValidationDiffers,
            async c =>
            {
                var store = await c.EventsAsync();
                await store.InsertAsync(new TableKey(c.Pk, "Row"), Rows.Placed("mixed"), c.Ct);
                await store.InsertAsync(new TableKey(c.Pk, "row"), Rows.Placed("lower"), c.Ct);

                Check.Equal("mixed", (await store.GetAsync(new TableKey(c.Pk, "Row"), c.Ct))?.Item?.Subject, "Subject of the mixed-case key");
                Check.Equal("lower", (await store.GetAsync(new TableKey(c.Pk, "row"), c.Ct))?.Item?.Subject, "Subject of the lower-case key");
                Check.Null(await store.GetAsync(new TableKey(c.Pk, "ROW"), c.Ct), "a read of a spelling that was never written");

                await Check.ThrowsAsync<ArgumentException>(() => store.InsertAsync(new TableKey("", "r"), Rows.Placed("x"), c.Ct), "an insert with an empty partition key");
                await Check.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(new TableKey(c.Pk, ""), Rows.Placed("x"), c.Ct), "an upsert with an empty row key");
                await Check.ThrowsAsync<ArgumentException>(() => store.GetAsync(new TableKey(c.Pk, ""), c.Ct), "a read with an empty row key");
                await Check.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(new TableKey("", "r"), c.Ct), "a delete with an empty partition key");
                Check.Equal(2, await store.CountAsync(null, c.Ct), "the row count");
            });
    }

    private static async Task<IPolymorphicStorage<ConformanceEvent>> SeededAsync(ConformanceContext c)
    {
        var store = await c.EventsAsync();
        await store.InsertAsync(Key(c, "b2"), Rows.Shipped("b2"), c.Ct);
        await store.InsertAsync(new TableKey(c.Pk + "x", "a1"), Rows.Placed("x-a1"), c.Ct);
        await store.InsertAsync(Key(c, "a1"), Rows.Placed("a1", 1), c.Ct);
        await store.InsertAsync(Key(c, "b1"), Rows.Placed("b1", 2), c.Ct);
        return store;
    }

    private static void Reads(ScenarioBuilder s)
    {
        s.Add("Get.ReturnsTheDerivedInstance_OrNull",
            "GetAsync returns the row as the derived type that was written, with every column of it, or null for a key no row has.",
            async c =>
            {
                var store = await SeededAsync(c);

                var placed = Check.NotNull((await store.GetAsync(Key(c, "b1"), c.Ct))?.Item as ConformanceOrderPlaced, "a row written as ConformanceOrderPlaced");
                Check.Equal("b1", placed.Subject, "a column of the base type");
                Check.Equal("customer of b1", placed.Customer, "a column of the derived type");
                Check.Equal(2, placed.Quantity, "a second column of the derived type");
                var shipped = Check.NotNull((await store.GetAsync(Key(c, "b2"), c.Ct))?.Item as ConformanceOrderShipped, "a row written as ConformanceOrderShipped");
                Check.Equal(new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero).UtcTicks, shipped.ShippedAt.UtcTicks, "a DateTime column of the derived type (ticks)");

                Check.Null(await store.GetAsync(Key(c, "never"), c.Ct), "a read of a key no row has");
                Check.Null(await store.GetAsync(new TableKey(c.Pk + "none", "a1"), c.Ct), "a read of a stored row key in another partition");
            });

        s.Add("Discriminator.TellsTheTypesApart",
            "Rows of different derived types carry different discriminators, each read back as its own type; ItemsOfType filters a result by type.",
            async c =>
            {
                var store = await SeededAsync(c);
                var rows = await store.QueryAsync(c.Pk, null, c.Ct);

                Check.Sequence(["a1", "b1", "b2"], rows.Select(r => r.Key.RowKey), "the row keys of the partition");
                Check.NotEqual(rows[0].Discriminator, rows[2].Discriminator, "the discriminator of another derived type");
                Check.Equal(rows[0].Discriminator, rows[1].Discriminator, "the discriminator of the same derived type");
                Check.Sequence(["a1", "b1"], rows.ItemsOfType<ConformanceEvent, ConformanceOrderPlaced>().Select(p => p.Subject), "the subjects of the ConformanceOrderPlaced rows");
                Check.Sequence(["b2"], rows.ItemsOfType<ConformanceEvent, ConformanceOrderShipped>().Select(p => p.Subject), "the subjects of the ConformanceOrderShipped rows");
                Check.Equal(rows[0].Discriminator, rows[0].Column<string>(SystemColumnNames.TypeName), "the _TypeName column, against the entry's Discriminator");
            });

        s.Add("Query.ReturnsRowsInKeyOrder_ScopedAndCapped",
            "QueryAsync returns the rows of a partition, or of the whole table, in ordinal (PartitionKey, RowKey) order, capped by take; a take of zero or less is empty.",
            KeyOrderDiffers,
            async c =>
            {
                var store = await SeededAsync(c);
                TableKey[] partition = [Key(c, "a1"), Key(c, "b1"), Key(c, "b2")];

                Check.Sequence(partition, Keys(await store.QueryAsync(c.Pk, null, c.Ct)), "the keys of the partition");
                Check.Sequence(partition.Concat([new TableKey(c.Pk + "x", "a1")]), Keys(await store.QueryAsync(null, null, c.Ct)), "the keys of the table");
                Check.Sequence(partition.Take(2), Keys(await store.QueryAsync(c.Pk, 2, c.Ct)), "the first two keys of the partition");
                Check.Sequence([], Keys(await store.QueryAsync(c.Pk, 0, c.Ct)), "the keys with take = 0");
                Check.Sequence([], Keys(await store.QueryAsync(c.Pk, -1, c.Ct)), "the keys with take = -1");
                Check.Sequence([], Keys(await store.QueryAsync(c.Pk + "none", null, c.Ct)), "the keys of an unknown partition");
                Check.True((await store.QueryAsync(c.Pk, null, c.Ct)).All(r => r.Timestamp is not null && !string.IsNullOrEmpty(r.ETag)),
                    "every entry a query returns carries an ETag and a Timestamp");
            });

        s.Add("Stream.YieldsWhatTheQueryReturns",
            "QueryStreamAsync yields the rows QueryAsync returns, in the same order, for a partition and for the whole table, capped by take.",
            KeyOrderDiffers,
            async c =>
            {
                var store = await c.EventsAsync();
                var writes = Enumerable.Range(0, 30)
                    .Select(i => new PolymorphicWrite<ConformanceEvent>(Key(c, "r" + i.ToString("D2")), i % 2 == 0 ? Rows.Placed("p" + i) : Rows.Shipped("s" + i)))
                    .Reverse().ToList();
                await store.InsertBatchAsync(writes, c.Ct);
                var expected = Enumerable.Range(0, 30).Select(i => Key(c, "r" + i.ToString("D2"))).ToList();

                Check.Sequence(expected, Keys(await Rows.ListAsync(store.QueryStreamAsync(c.Pk, null, c.Ct), c.Ct)), "the streamed keys of the partition");
                Check.Sequence(expected, Keys(await Rows.ListAsync(store.QueryStreamAsync(null, null, c.Ct), c.Ct)), "the streamed keys of the table");
                Check.Sequence(expected.Take(7), Keys(await Rows.ListAsync(store.QueryStreamAsync(c.Pk, 7, c.Ct), c.Ct)), "the first seven streamed keys");
                Check.Sequence([], Keys(await Rows.ListAsync(store.QueryStreamAsync(c.Pk, 0, c.Ct), c.Ct)), "the streamed keys with take = 0");
            });

        s.Add("Stream.TokenCancelledWhileReading_EndsWithOperationCanceled",
            "A stream whose token is cancelled between two rows ends with OperationCanceledException instead of yielding the rest.",
            CancellationNotObserved,
            async c =>
            {
                var store = await c.EventsAsync();
                await store.InsertBatchAsync(
                    Enumerable.Range(0, 20).Select(i => new PolymorphicWrite<ConformanceEvent>(Key(c, "r" + i.ToString("D2")), Rows.Placed("p" + i))).ToList(), c.Ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(c.Ct);

                var seen = 0;
                await Check.ThrowsAsync<OperationCanceledException>(
                    async () =>
                    {
                        await foreach (var _ in store.QueryStreamAsync(c.Pk, null, cts.Token))
                        {
                            if (++seen == 3)
                                cts.Cancel();
                        }
                    },
                    "a stream whose token is cancelled after its third row");
                Check.True(seen < 20, $"the stream stops before its last row once cancelled; it yielded {seen} of 20");
            });

        s.Add("Count.CountsEveryRowWhateverItsType",
            "CountAsync returns the number of rows of a partition, or of the whole table, markers and every type included.",
            async c =>
            {
                var store = await SeededAsync(c);
                await store.InsertMarkerAsync(Key(c, "marker"), Columns((Published, true)), c.Ct);
                Check.Equal(4, await store.CountAsync(c.Pk, c.Ct), "the count of the partition");
                Check.Equal(5, await store.CountAsync(null, c.Ct), "the count of the table");
                Check.Equal(0, await store.CountAsync(c.Pk + "none", c.Ct), "the count of an unknown partition");
            });
    }

    private static void Markers(ScenarioBuilder s)
    {
        s.Add("Marker.IsARowWithoutAnItem",
            "A marker row (system columns, no item) is stored and read back with a null Item, a null Discriminator and its columns; Value throws InvalidOperationException for it.",
            async c =>
            {
                var store = await c.EventsAsync();
                var written = await store.InsertMarkerAsync(Key(c, "m1"), Columns((Published, true), (Attempts, 3)), c.Ct);
                Check.Null(written.Item, "the item of a marker entry a write returns");

                var read = Check.NotNull(await store.GetAsync(Key(c, "m1"), c.Ct), "the marker row");
                Check.Null(read.Item, "the item of a marker row");
                Check.Null(read.Discriminator, "the discriminator of a marker row");
                Check.Equal(true, read.Column<bool>(Published), "a Boolean system column of the marker");
                Check.Equal(3, read.Column<int>(Attempts), "an Int32 system column of the marker");
                Check.True(read.TryColumn<bool>(Published, out _) && !read.TryColumn<string>(Published, out _) && !read.TryColumn<int>("_Missing", out _),
                    "TryColumn answers by name and type");
                try
                {
                    _ = read.Value;
                    throw new ConformanceAssertionException("expected Value of a marker row to throw InvalidOperationException, but it returned");
                }
                catch (InvalidOperationException)
                {
                }
                await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.InsertMarkerAsync(Key(c, "m1"), Columns((Published, false)), c.Ct), "an insert of a marker at a stored key");
            });

        s.Add("SystemColumns.RideAlongWithAnItem",
            "A write can carry system columns beside its item: they are stored with the row, returned in Columns, and do not disturb the item.",
            async c =>
            {
                var store = await c.EventsAsync();
                await store.InsertAsync(new PolymorphicWrite<ConformanceEvent>(Key(c, "r1"), Rows.Placed("with columns", 4), Columns((Published, false), (Attempts, 1))), c.Ct);

                var read = Check.NotNull(await store.GetAsync(Key(c, "r1"), c.Ct), "the row");
                Check.Equal(4, (read.Item as ConformanceOrderPlaced)?.Quantity, "a column of the item");
                Check.Equal(false, read.Column<bool>(Published), "a system column written beside the item");
                Check.Equal(1, read.Column<int>(Attempts), "a second system column written beside the item");
                Check.Equal("with columns", read.Column<string>("Subject"), "an item column, read through Columns");
            });

        s.Add("MergeColumns.SetsSystemColumns_AndKeepsTheRest",
            "MergeColumnsAsync sets the given system columns on an existing row without a read and keeps its item and its other columns; the row's ETag changes.",
            async c =>
            {
                var store = await c.EventsAsync();
                var inserted = await store.InsertAsync(new PolymorphicWrite<ConformanceEvent>(Key(c, "r1"), Rows.Placed("order", 4), Columns((Attempts, 1))), c.Ct);

                await store.MergeColumnsAsync(Key(c, "r1"), Columns((Published, true)), c.Ct);
                await store.MergeColumnsAsync(Key(c, "r1"), Columns((Attempts, 2)), c.Ct);

                var read = Check.NotNull(await store.GetAsync(Key(c, "r1"), c.Ct), "the merged row");
                Check.Equal(true, read.Column<bool>(Published), "a merged system column");
                Check.Equal(2, read.Column<int>(Attempts), "a system column merged over its old value");
                var placed = Check.NotNull(read.Item as ConformanceOrderPlaced, "the item of the merged row, as the type written");
                Check.Equal(4, placed.Quantity, "a column of the item after the merges");
                Check.NotEqual(inserted.ETag, read.ETag, "the ETag after the merges");
            });

        s.Add("MergeColumns.RefusesAnythingButSystemColumns",
            "MergeColumnsAsync throws ArgumentException for a column that is not a system column, for the discriminator column and for no columns at all, and changes nothing.",
            ArgumentValidationDiffers,
            async c =>
            {
                var store = await c.EventsAsync();
                await store.InsertAsync(Key(c, "r1"), Rows.Placed("order", 4), c.Ct);

                await Check.ThrowsAsync<ArgumentException>(
                    () => store.MergeColumnsAsync(Key(c, "r1"), Columns(("Quantity", 99)), c.Ct), "a merge of a data column");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.MergeColumnsAsync(Key(c, "r1"), Columns((SystemColumnNames.TypeName, "another type")), c.Ct), "a merge of the discriminator column");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.MergeColumnsAsync(Key(c, "r1"), Columns(), c.Ct), "a merge of no columns");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.InsertAsync(new PolymorphicWrite<ConformanceEvent>(Key(c, "r2"), Rows.Placed("x"), Columns(("NotSystem", 1))), c.Ct),
                    "an insert whose system columns hold a data column");

                var read = Check.NotNull(await store.GetAsync(Key(c, "r1"), c.Ct), "the row");
                Check.Equal(4, (read.Item as ConformanceOrderPlaced)?.Quantity, "Quantity after the refused merges");
                Check.Equal(1, await store.CountAsync(null, c.Ct), "the row count after the refused writes");
            });
    }

    private static void Deletes(ScenarioBuilder s)
    {
        s.Add("Delete.RemovesThatRowOnly_AndAMissingRowIsANoOp",
            "DeleteAsync removes the row at the key and no other; deleting a key no row has completes without an exception.",
            async c =>
            {
                var store = await SeededAsync(c);

                await store.DeleteAsync(Key(c, "a1"), c.Ct);
                await store.DeleteAsync(Key(c, "a1"), c.Ct);
                await store.DeleteAsync(Key(c, "never"), c.Ct);

                Check.Null(await store.GetAsync(Key(c, "a1"), c.Ct), "the deleted row");
                Check.Sequence([Key(c, "b1"), Key(c, "b2"), new TableKey(c.Pk + "x", "a1")], Keys(await store.QueryAsync(null, null, c.Ct)), "the keys after the delete");
                await store.InsertAsync(Key(c, "a1"), Rows.Shipped("second life"), c.Ct);
                Check.True((await store.GetAsync(Key(c, "a1"), c.Ct))?.Item is ConformanceOrderShipped, "the key can be inserted again after the delete");
            });

        s.Add("DeletePartition.RemovesEveryRowOfThePartition_WhateverItsType",
            "DeletePartitionAsync removes every row of the partition (every type, markers, more than one transaction holds), returns how many there were, and leaves other partitions; an empty partition argument throws ArgumentException.",
            async c =>
            {
                var store = await SeededAsync(c);
                await store.InsertMarkerAsync(Key(c, "marker"), Columns((Published, true)), c.Ct);
                await store.InsertBatchAsync(
                    Enumerable.Range(0, 120).Select(i => new PolymorphicWrite<ConformanceEvent>(Key(c, "z" + i.ToString("D3")), Rows.Placed("z" + i))).ToList(), c.Ct);

                Check.Equal(124, await store.DeletePartitionAsync(c.Pk, c.Ct), "the count DeletePartitionAsync returns");

                Check.Equal(0, await store.CountAsync(c.Pk, c.Ct), "the row count of the deleted partition");
                Check.Equal(1, await store.CountAsync(c.Pk + "x", c.Ct), "the row count of the other partition");
                Check.Equal(0, await store.DeletePartitionAsync(c.Pk, c.Ct), "the count for a partition that is already empty");
                await Check.ThrowsAsync<ArgumentException>(() => store.DeletePartitionAsync("", c.Ct), "a partition delete of an empty partition");
                await Check.ThrowsAsync<ArgumentException>(() => store.DeletePartitionAsync(null!, c.Ct), "a partition delete of null");
            });
    }

    private static void Batches(ScenarioBuilder s)
    {
        s.Add("InsertBatch.StoresRowsOfMixedTypes_AcrossPartitionsAndTransactions",
            "InsertBatchAsync stores a heterogeneous set (several derived types and markers), across partitions and beyond the 100 rows one transaction holds, and returns the number written; an empty batch returns 0.",
            async c =>
            {
                var store = await c.EventsAsync();
                Check.Equal(0, await store.InsertBatchAsync([], c.Ct), "the count of an empty batch");

                var writes = Enumerable.Range(0, 150)
                    .Select(i => new PolymorphicWrite<ConformanceEvent>(Key(c, "r" + i.ToString("D3")), i % 2 == 0 ? Rows.Placed("p" + i, i) : Rows.Shipped("s" + i)))
                    .ToList();
                writes.Add(PolymorphicWrite<ConformanceEvent>.Marker(Key(c, "marker"), Columns((Published, true))));
                writes.Add(new PolymorphicWrite<ConformanceEvent>(new TableKey(c.Pk + "x", "r000"), Rows.Placed("other partition")));

                Check.Equal(152, await store.InsertBatchAsync(writes, c.Ct), "the count InsertBatchAsync returns");

                Check.Equal(151, await store.CountAsync(c.Pk, c.Ct), "the row count of the first partition");
                Check.Equal(1, await store.CountAsync(c.Pk + "x", c.Ct), "the row count of the second partition");
                Check.Equal(148, ((await store.GetAsync(Key(c, "r148"), c.Ct))?.Item as ConformanceOrderPlaced)?.Quantity, "a column of an even row");
                Check.True((await store.GetAsync(Key(c, "r149"), c.Ct))?.Item is ConformanceOrderShipped, "an odd row reads back as ConformanceOrderShipped");
                Check.Null((await store.GetAsync(Key(c, "marker"), c.Ct))?.Item, "the item of the marker of the batch");
            });

        s.Add("InsertBatch.ExistingOrRepeatedKey_ThrowsDuplicateKey_AndStoresNothingOfThatTransaction",
            "InsertBatchAsync throws DuplicateKeyException for a key that is stored and for a key named twice in the batch, and stores no row of the failing transaction.",
            [BatchTransactionsDiffer, FailureTypesDiffer],
            async c =>
            {
                var store = await c.EventsAsync();
                await store.InsertAsync(Key(c, "r2"), Rows.Placed("original"), c.Ct);
                PolymorphicWrite<ConformanceEvent> Write(string rk) => new(Key(c, rk), Rows.Placed(rk));

                var stored = await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.InsertBatchAsync([Write("r1"), Write("r2"), Write("r3")], c.Ct), "an insert batch that names a stored key");
                Check.Equal(Key(c, "r2").ToId(), stored.Id, "DuplicateKeyException.Id (the key that exists)");
                await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.InsertBatchAsync([Write("r4"), Write("r5"), Write("r4")], c.Ct), "an insert batch that names a key twice");

                Check.Sequence([Key(c, "r2")], Keys(await store.QueryAsync(c.Pk, null, c.Ct)), "the keys after the refused batches");
                Check.Equal("original", (await store.GetAsync(Key(c, "r2"), c.Ct))?.Item?.Subject, "Subject of the stored row after the refused batches");
            });
    }

    private static void Failures(ScenarioBuilder s)
    {
        s.Add("MergeColumns.MissingRow_ThrowsEntityNotFound_AndCreatesNothing",
            "MergeColumnsAsync of a key no row has throws EntityNotFoundException (the neutral type, naming the key): a merge never creates a row.",
            FailureTypesDiffer,
            async c =>
            {
                var store = await c.EventsAsync();
                await store.InsertAsync(Key(c, "other"), Rows.Placed("other"), c.Ct);

                var missing = await Check.NeutralAsync<EntityNotFoundException>(
                    () => store.MergeColumnsAsync(Key(c, "never"), Columns((Published, true)), c.Ct), "a merge of a missing row");
                Check.Equal(Key(c, "never").ToId(), missing.Id, "EntityNotFoundException.Id");
                Check.Equal(1, await store.CountAsync(null, c.Ct), "the row count after the refused merge");
            });

        s.Add("Rejected.KeyCharacterTheServiceRefuses_IsAStorageRequestRejectedException",
            "An insert whose key holds '/', '\\', '#', '?' or a control character throws StorageRequestRejectedException and stores nothing.",
            [KeyRulesNotEnforced, FailureTypesDiffer],
            async c =>
            {
                var store = await c.EventsAsync();
                foreach (var rowKey in new[] { "a/b", "a\\b", "a#b", "a?b", "a\tb", "a\u007fb" })
                {
                    var shown = "U+" + ((int)rowKey[1]).ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
                    await Check.NeutralAsync<StorageRequestRejectedException>(
                        () => store.InsertAsync(Key(c, rowKey), Rows.Placed("x"), c.Ct), $"an insert whose row key holds {shown}");
                    await Check.NeutralAsync<StorageRequestRejectedException>(
                        () => store.InsertBatchAsync(
                            [new PolymorphicWrite<ConformanceEvent>(Key(c, "ok"), Rows.Placed("ok")), new PolymorphicWrite<ConformanceEvent>(Key(c, rowKey), Rows.Placed("x"))], c.Ct),
                        $"an insert batch whose second row key holds {shown}");
                }
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after the refused inserts");
            });

        s.Add("Cancellation.AWriteWithACancelledToken_ThrowsAndChangesNothing",
            "Every write of a polymorphic store given a token that is already cancelled throws OperationCanceledException and leaves storage as it was.",
            CancellationNotObserved,
            async c =>
            {
                var store = await c.EventsAsync();
                await store.InsertAsync(Key(c, "a"), Rows.Placed("original"), c.Ct);

                await Check.CancelledAsync(() => store.InsertAsync(Key(c, "b"), Rows.Placed("b"), Rows.Cancelled), "InsertAsync");
                await Check.CancelledAsync(() => store.UpsertAsync(Key(c, "a"), Rows.Placed("changed"), Rows.Cancelled), "UpsertAsync");
                await Check.CancelledAsync(
                    () => store.InsertBatchAsync([new PolymorphicWrite<ConformanceEvent>(Key(c, "c"), Rows.Placed("c"))], Rows.Cancelled), "InsertBatchAsync");
                await Check.CancelledAsync(() => store.MergeColumnsAsync(Key(c, "a"), Columns((Published, true)), Rows.Cancelled), "MergeColumnsAsync");
                await Check.CancelledAsync(() => store.DeleteAsync(Key(c, "a"), Rows.Cancelled), "DeleteAsync");
                await Check.CancelledAsync(() => store.DeletePartitionAsync(c.Pk, Rows.Cancelled), "DeletePartitionAsync");

                var rows = await store.QueryAsync(c.Pk, null, c.Ct);
                Check.Sequence([Key(c, "a")], Keys(rows), "the keys after the cancelled writes");
                Check.Equal("original", rows[0].Item?.Subject, "Subject after the cancelled writes");
                Check.True(!rows[0].Columns.ContainsKey(Published), "the cancelled merge set no column");
            });
    }
}
