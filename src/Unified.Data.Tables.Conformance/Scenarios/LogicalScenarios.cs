using static Unified.Data.Tables.Conformance.ConformanceLeniencies;

namespace Unified.Data.Tables.Conformance.Scenarios;

/// <summary>
/// Tier 1: what every <see cref="IStorage{T}"/> member and every <see cref="StorageExtensions"/> verb
/// does to rows, as a caller sees it.
/// </summary>
internal static class LogicalScenarios
{
    public static void Add(ScenarioBuilder s)
    {
        s.Tier(ConformanceTier.Logical);
        Create(s);
        Read(s);
        Upsert(s);
        Update(s);
        Merge(s);
        Delete(s);
        Query(s);
        Streams(s);
        Paging(s);
        OrderAndIds(s);
        ETagsAndTimestamps(s);
        Batches(s);
        Cancellation(s);
        Verbs(s);
    }

    private static void Create(ScenarioBuilder s)
    {
        s.Add("Create.StoresTheRow_AndReturnsItWithAnETag",
            "CreateAsync stores a new row and returns the entity with its ETag; the row is then read back as written.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var created = await store.CreateAsync(Rows.Item(c.Id("r1"), "first", 7), c.Ct);

                Check.Equal(c.Id("r1"), created.Id, "the id of the created entity");
                Check.NotEmpty(created.ETag, "the ETag of the created entity");
                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the created row, read back");
                Check.Equal("first", read.Name, "Name read back");
                Check.Equal(7, read.Number, "Number read back");
                Check.Equal(created.ETag, read.ETag, "the ETag read back, against the one CreateAsync returned");
            });

        s.Add("Create.ExistingId_ThrowsDuplicateKey_AndKeepsTheStoredRow",
            "CreateAsync of an id that is stored throws DuplicateKeyException and leaves the stored row as it was.",
            FailureTypesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "original"), c.Ct);

                await Check.ThrowsAsync<DuplicateKeyException>(
                    () => store.CreateAsync(Rows.Item(c.Id("r1"), "intruder"), c.Ct), "a create of a stored id");

                Check.Equal("original", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name of the stored row after the refused create");
                Check.Equal(1, await store.CountAsync(c.Pk, c.Ct), "the row count after the refused create");
            });

        s.Add("Create.StampsCreatedAtAndUpdatedAt",
            "CreateAsync stamps CreatedAt and UpdatedAt with the time of the write (the same instant), whatever the caller put there.",
            TimestampsDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var entity = Rows.Item(c.Id("r1"));
                entity.CreatedAt = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
                entity.UpdatedAt = new DateTimeOffset(2002, 3, 4, 5, 6, 7, TimeSpan.Zero);

                var before = DateTimeOffset.UtcNow;
                var created = await store.CreateAsync(entity, c.Ct);
                var after = DateTimeOffset.UtcNow;

                Rows.StampedBetween(before, after, created.CreatedAt, "CreatedAt of a created entity");
                Check.Equal(created.CreatedAt, created.UpdatedAt, "UpdatedAt of a created entity (the instant of CreatedAt)");
                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the created row");
                Check.Equal(created.CreatedAt.UtcTicks, read.CreatedAt.UtcTicks, "CreatedAt read back (ticks)");
                Check.Equal(created.UpdatedAt.UtcTicks, read.UpdatedAt.UtcTicks, "UpdatedAt read back (ticks)");
            });

        s.Add("Create.NullEntityOrBlankId_ThrowsArgumentException",
            "CreateAsync refuses a null entity and an entity whose id is null or blank with an ArgumentException, and stores nothing.",
            ArgumentValidationDiffers,
            async c =>
            {
                var store = await c.ItemsAsync();
                await Check.ThrowsAsync<ArgumentException>(() => store.CreateAsync(null!, c.Ct), "a create of null");
                await Check.ThrowsAsync<ArgumentException>(() => store.CreateAsync(Rows.Item(""), c.Ct), "a create with an empty id");
                await Check.ThrowsAsync<ArgumentException>(() => store.CreateAsync(Rows.Item("   "), c.Ct), "a create with a blank id");
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after the refused creates");
            });
    }

    private static void Read(ScenarioBuilder s)
    {
        s.Add("One.MissingRow_ReturnsNull",
            "OneAsync returns null for an id that is not stored; a missing row is never an exception on a read.",
            async c =>
            {
                var store = await c.ItemsAsync();
                Check.Null(await store.OneAsync(c.Id("nothing"), c.Ct), "a read of a missing row in an empty table");
                await store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct);
                Check.Null(await store.OneAsync(c.Id("r2"), c.Ct), "a read of a missing row beside a stored one");
                Check.Null(await store.OneAsync(c.Pk + "x|r1", c.Ct), "a read of a stored row key in another partition");
            });

        s.Add("One.RoundTripsEveryCellKind",
            "A row is read back with every value as written: string, Int32, Int64, Double, Boolean, DateTime (the same instant), Guid, an enum, a byte array and a null.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var when = new DateTimeOffset(2024, 2, 29, 13, 14, 15, 678, TimeSpan.FromHours(2));
                var token = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
                await store.CreateAsync(new ConformanceItem
                {
                    Id = c.Id("r1"),
                    Name = "Näme 中文 ✓",
                    Note = null,
                    Number = -42,
                    Big = 9_000_000_000L,
                    Ratio = 0.125,
                    Flag = true,
                    When = when,
                    Token = token,
                    Color = ConformanceColor.Blue,
                    Data = [0, 1, 2, 254, 255],
                }, c.Ct);

                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row");
                Check.Equal("Näme 中文 ✓", read.Name, "a string cell");
                Check.Null(read.Note, "a null string");
                Check.Equal(-42, read.Number, "an Int32 cell");
                Check.Equal(9_000_000_000L, read.Big, "an Int64 cell");
                Check.Equal(0.125, read.Ratio, "a Double cell");
                Check.Equal(true, read.Flag, "a Boolean cell");
                Check.Equal(when.UtcTicks, read.When?.UtcTicks, "a DateTime cell (UTC ticks)");
                Check.Equal(token, read.Token, "a Guid cell");
                Check.Equal(ConformanceColor.Blue, read.Color, "an enum");
                Check.Bytes([0, 1, 2, 254, 255], read.Data, "a Binary cell");
            });

        s.Add("One.ReturnsACopy_TheCallerCannotChangeTheStoredRow",
            "What a read returns and what a write was given are the caller's own objects: changing them, or a byte array in them, does not change the stored row.",
            RowsNotIsolated,
            async c =>
            {
                var store = await c.ItemsAsync();
                var written = Rows.Item(c.Id("r1"), "stored");
                written.Data = [1, 2, 3];
                await store.CreateAsync(written, c.Ct);
                written.Name = "changed after the write";
                written.Data[0] = 99;

                var first = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row");
                Check.Equal("stored", first.Name, "Name after the writer changed its own object");
                Check.Bytes([1, 2, 3], first.Data, "Data after the writer changed its own array");
                first.Name = "changed after the read";
                first.Data![1] = 98;

                var second = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row, read again");
                Check.Equal("stored", second.Name, "Name after a reader changed the object it was given");
                Check.Bytes([1, 2, 3], second.Data, "Data after a reader changed the array it was given");
                var listed = (await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct)).Single();
                Check.Equal("stored", listed.Name, "Name in a query after a reader changed the object it was given");
            });

        s.Add("Exists.TrueForAStoredRow_FalseOtherwise",
            "ExistsAsync is true for a stored row and false for a missing one, before the row is written and after it is deleted.",
            async c =>
            {
                var store = await c.ItemsAsync();
                Check.Equal(false, await store.ExistsAsync(c.Id("r1"), c.Ct), "Exists before the row is written");
                await store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct);
                Check.Equal(true, await store.ExistsAsync(c.Id("r1"), c.Ct), "Exists of the stored row");
                Check.Equal(false, await store.ExistsAsync(c.Id("r2"), c.Ct), "Exists of another row key");
                await store.DeleteAsync(c.Id("r1"), c.Ct);
                Check.Equal(false, await store.ExistsAsync(c.Id("r1"), c.Ct), "Exists after the delete");
            });

        s.Add("Read.BlankId_ThrowsArgumentException",
            "OneAsync, ExistsAsync and DeleteAsync refuse a null or blank id with an ArgumentException.",
            ArgumentValidationDiffers,
            async c =>
            {
                var store = await c.ItemsAsync();
                await Check.ThrowsAsync<ArgumentException>(() => store.OneAsync(" ", c.Ct), "OneAsync with a blank id");
                await Check.ThrowsAsync<ArgumentException>(() => store.ExistsAsync("", c.Ct), "ExistsAsync with an empty id");
                await Check.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(null!, c.Ct), "DeleteAsync with a null id");
            });
    }

    private static void Upsert(ScenarioBuilder s)
    {
        s.Add("Upsert.MissingRow_CreatesIt",
            "UpsertAsync of an id that is not stored creates the row and returns the entity with its ETag.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var upserted = await store.UpsertAsync(Rows.Item(c.Id("r1"), "new", 3), c.Ct);

                Check.NotEmpty(upserted.ETag, "the ETag of the upserted entity");
                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the upserted row");
                Check.Equal("new", read.Name, "Name read back");
                Check.Equal(upserted.ETag, read.ETag, "the ETag read back, against the one UpsertAsync returned");
            });

        s.Add("Upsert.StoredRow_ReplacesItWhole_WhateverTheETag",
            "UpsertAsync replaces every column of a stored row (a value left null is removed) and ignores the entity's ETag: the last writer wins.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var first = Rows.Item(c.Id("r1"), "first", 1);
                first.Note = "only the first version has a note";
                var created = await store.CreateAsync(first, c.Ct);

                var replacement = Rows.Item(c.Id("r1"), "second", 2);
                replacement.ETag = "W/\"this-version-never-existed\"";
                var upserted = await store.UpsertAsync(replacement, c.Ct);

                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the upserted row");
                Check.Equal("second", read.Name, "Name after the upsert");
                Check.Equal(2, read.Number, "Number after the upsert");
                Check.Null(read.Note, "a column the replacement leaves null");
                Check.NotEqual(created.ETag, upserted.ETag, "the ETag after the upsert");
                Check.Equal(1, await store.CountAsync(c.Pk, c.Ct), "the row count after the upsert");
            });

        s.Add("Upsert.KeepsASuppliedCreatedAt_AndStampsUpdatedAt",
            "UpsertAsync keeps the CreatedAt the caller supplies and stamps UpdatedAt with the time of the write.",
            TimestampsDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var createdAt = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
                var entity = Rows.Item(c.Id("r1"));
                entity.CreatedAt = createdAt;
                entity.UpdatedAt = createdAt;

                var before = DateTimeOffset.UtcNow;
                await store.UpsertAsync(entity, c.Ct);
                var after = DateTimeOffset.UtcNow;

                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the upserted row");
                Check.Equal(createdAt.UtcTicks, read.CreatedAt.UtcTicks, "the supplied CreatedAt (ticks)");
                Rows.StampedBetween(before, after, read.UpdatedAt, "UpdatedAt of an upserted row");
            });
    }

    private static void Update(ScenarioBuilder s)
    {
        s.Add("Update.CurrentETag_ReplacesTheRow_AndReturnsANewETag",
            "UpdateAsync with the ETag that was read replaces the row in full and returns the entity with a new ETag.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var first = Rows.Item(c.Id("r1"), "first", 1);
                first.Note = "note";
                await store.CreateAsync(first, c.Ct);
                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row");
                var readETag = read.ETag;

                read.Name = "second";
                read.Note = null;
                var updated = await store.UpdateAsync(read, c.Ct);

                Check.NotEmpty(updated.ETag, "the ETag UpdateAsync returns");
                Check.NotEqual(readETag, updated.ETag, "the ETag after the update");
                var after = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the updated row");
                Check.Equal("second", after.Name, "Name after the update");
                Check.Null(after.Note, "a column the update set to null");
                Check.Equal(1, after.Number, "a column the update left as read");
                Check.Equal(updated.ETag, after.ETag, "the ETag read back, against the one UpdateAsync returned");
            });

        s.Add("Update.StaleETag_ThrowsConflict_AndKeepsTheRow",
            "UpdateAsync with an ETag another write has replaced throws ConcurrencyConflictException in Auto and Strict mode, and the row keeps the other write.",
            [ETagRulesDiffer, FailureTypesDiffer],
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "first"), c.Ct);
                var stale = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row");
                var winner = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row");
                winner.Name = "winner";
                await store.UpdateAsync(winner, c.Ct);

                stale.Name = "loser";
                await Check.ThrowsAsync<ConcurrencyConflictException>(() => store.UpdateAsync(stale, c.Ct), "an update with a stale ETag (Auto)");
                await Check.ThrowsAsync<ConcurrencyConflictException>(
                    () => store.UpdateAsync(stale, ConcurrencyMode.Strict, c.Ct), "an update with a stale ETag (Strict)");

                Check.Equal("winner", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after the refused updates");
            });

        s.Add("Update.NoETag_ThrowsInvalidOperation_InAutoAndStrictMode",
            "UpdateAsync of an entity without an ETag throws InvalidOperationException in Auto mode (the default) and in Strict mode: no version to check is not permission to overwrite.",
            ETagRulesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "first"), c.Ct);

                await Check.ThrowsAsync<InvalidOperationException>(
                    () => store.UpdateAsync(Rows.Item(c.Id("r1"), "blind"), c.Ct), "an update without an ETag (Auto)");
                await Check.ThrowsAsync<InvalidOperationException>(
                    () => store.UpdateAsync(Rows.Item(c.Id("r1"), "blind"), ConcurrencyMode.Strict, c.Ct), "an update without an ETag (Strict)");

                Check.Equal("first", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after the refused updates");
            });

        s.Add("Update.LastWriterWins_ReplacesTheRow_WhateverTheETag",
            "UpdateAsync in LastWriterWins mode replaces a stored row with a stale ETag and with none.",
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "first"), c.Ct);
                var stale = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row");
                await store.UpsertAsync(Rows.Item(c.Id("r1"), "moved on"), c.Ct);

                stale.Name = "stale wins";
                await store.UpdateAsync(stale, ConcurrencyMode.LastWriterWins, c.Ct);
                Check.Equal("stale wins", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after a last-writer-wins update with a stale ETag");

                var updated = await store.UpdateAsync(Rows.Item(c.Id("r1"), "blind wins"), ConcurrencyMode.LastWriterWins, c.Ct);
                Check.NotEmpty(updated.ETag, "the ETag a last-writer-wins update returns");
                Check.Equal("blind wins", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after a last-writer-wins update without an ETag");
            });

        s.Add("Update.MissingRow_ThrowsEntityNotFound_InEveryMode_AndCreatesNothing",
            "UpdateAsync of an id that is not stored throws EntityNotFoundException in every ConcurrencyMode, LastWriterWins included: an update never creates a row.",
            FailureTypesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var created = await store.CreateAsync(Rows.Item(c.Id("gone"), "to be deleted"), c.Ct);
                await store.DeleteAsync(c.Id("gone"), c.Ct);

                // Deleted since it was read: the ETag is one the row had.
                await Check.ThrowsAsync<EntityNotFoundException>(() => store.UpdateAsync(created, c.Ct), "an update of a deleted row (Auto)");
                await Check.ThrowsAsync<EntityNotFoundException>(
                    () => store.UpdateAsync(created, ConcurrencyMode.Strict, c.Ct), "an update of a deleted row (Strict)");
                await Check.ThrowsAsync<EntityNotFoundException>(
                    () => store.UpdateAsync(Rows.Item(c.Id("never"), "x"), ConcurrencyMode.LastWriterWins, c.Ct),
                    "an update of a row that never existed (LastWriterWins)");

                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after the refused updates");
            });

        s.Add("Update.WildcardETag_IsRefused",
            "A whole-entity UpdateAsync whose ETag is the wildcard \"*\" throws ArgumentException and leaves the row: \"*\" is not a version, and an unconditional replace is asked for with ConcurrencyMode.LastWriterWins.",
            WildcardETagOnUpdate,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "first"), c.Ct);
                var wildcard = Rows.Item(c.Id("r1"), "through the wildcard");
                wildcard.ETag = "*";

                await Check.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(wildcard, c.Ct), "an update with the ETag \"*\" (Auto)");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.UpdateAsync(wildcard, ConcurrencyMode.Strict, c.Ct), "an update with the ETag \"*\" (Strict)");

                Check.Equal("first", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after the refused updates");
            });

        s.Add("Update.StampsUpdatedAt_AndKeepsCreatedAt",
            "UpdateAsync stamps UpdatedAt with the time of the write and stores the CreatedAt the entity carries.",
            TimestampsDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "first"), c.Ct);
                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row");
                var createdAt = read.CreatedAt;
                read.UpdatedAt = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

                var before = DateTimeOffset.UtcNow;
                await store.UpdateAsync(read, c.Ct);
                var after = DateTimeOffset.UtcNow;

                var updated = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the updated row");
                Check.Equal(createdAt.UtcTicks, updated.CreatedAt.UtcTicks, "CreatedAt after an update (ticks)");
                Rows.StampedBetween(before, after, updated.UpdatedAt, "UpdatedAt of an updated row");
            });
    }

    private static void Merge(ScenarioBuilder s)
    {
        s.Add("Merge.SetsOnlyTheNamedColumns",
            "The builder UpdateAsync writes the named columns and keeps every other column of the row, without a read.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var entity = Rows.Item(c.Id("r1"), "name", 1);
                entity.Note = "note";
                await store.CreateAsync(entity, c.Ct);

                await store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Name, "merged").SetProperty(x => x.Flag, true), c.Ct);

                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the merged row");
                Check.Equal("merged", read.Name, "a merged column");
                Check.Equal(true, read.Flag, "a second merged column");
                Check.Equal("note", read.Note, "a column the merge did not name");
                Check.Equal(1, read.Number, "another column the merge did not name");
            });

        s.Add("Merge.TwoMergesOfDifferentColumns_BothSurvive",
            "Two builder updates of different columns, neither having read the other's write, both survive: disjoint merges never conflict.",
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "name", 1), c.Ct);

                await store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Name, "from the first writer"), c.Ct);
                await store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Number, 2), c.Ct);

                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row");
                Check.Equal("from the first writer", read.Name, "the first writer's column");
                Check.Equal(2, read.Number, "the second writer's column");
            });

        s.Add("Merge.ReturnsTheNewETag",
            "The builder UpdateAsync changes the row's ETag; the ETag it returns, when it returns one, is the one a read of the table then reports.",
            ETagRulesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var created = await store.CreateAsync(Rows.Item(c.Id("r1"), "name"), c.Ct);

                var returned = await store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Name, "merged"), c.Ct);

                var read = Check.NotNull(await Rows.StoredAsync(store, c.Id("r1"), c.Ct), "the merged row");
                Check.NotEqual(created.ETag, read.ETag, "the ETag after a merge");
                Check.NotNull(returned, "the ETag a merge returns (empty, not null, when the backend reported none)");
                if (returned.Length > 0)
                    Check.Equal(read.ETag, returned, "the ETag the merge returned, against the one read back");
            });

        s.Add("Merge.WithETag_AppliesToThatVersionOnly",
            "A builder update that carries WithETag applies when the row is that version, and throws ConcurrencyConflictException and changes nothing when it is not.",
            [ETagRulesDiffer, FailureTypesDiffer],
            async c =>
            {
                var store = await c.ItemsAsync();
                var created = await store.CreateAsync(Rows.Item(c.Id("r1"), "first", 1), c.Ct);

                await store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Name, "second").WithETag(created.ETag!), c.Ct);
                Check.Equal("second", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after a merge on the current ETag");

                await Check.ThrowsAsync<ConcurrencyConflictException>(
                    () => store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Name, "third").WithETag(created.ETag!), c.Ct),
                    "a merge on an ETag that has been replaced");
                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row");
                Check.Equal("second", read.Name, "Name after the refused merge");
            });

        s.Add("Merge.MissingRow_ThrowsEntityNotFound_AndCreatesNothing",
            "The builder UpdateAsync of an id that is not stored throws EntityNotFoundException, with or without an ETag: a merge never creates a row.",
            FailureTypesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();

                await Check.ThrowsAsync<EntityNotFoundException>(
                    () => store.UpdateAsync(c.Id("never"), b => b.SetProperty(x => x.Name, "x"), c.Ct), "a merge of a missing row");
                await Check.ThrowsAsync<EntityNotFoundException>(
                    () => store.UpdateAsync(c.Id("never"), b => b.SetProperty(x => x.Name, "x").WithETag("W/\"1\""), c.Ct),
                    "a conditional merge of a missing row");

                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after the refused merges");
            });

        s.Add("Merge.NoColumns_ThrowsInvalidOperation",
            "A builder update that names no column throws InvalidOperationException.",
            ArgumentValidationDiffers,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "name"), c.Ct);
                await Check.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(c.Id("r1"), _ => { }, c.Ct), "a merge of no columns");
            });

        s.Add("Merge.StampsUpdatedAt",
            "The builder UpdateAsync stamps UpdatedAt with the time of the write and keeps CreatedAt.",
            TimestampsDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var entity = Rows.Item(c.Id("r1"), "name");
                entity.CreatedAt = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
                await store.UpsertAsync(entity, c.Ct);
                await store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Number, 1), c.Ct);
                var firstMerge = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row").UpdatedAt;

                var before = DateTimeOffset.UtcNow;
                await store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Number, 2), c.Ct);
                var after = DateTimeOffset.UtcNow;

                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the merged row");
                Rows.StampedBetween(before, after, read.UpdatedAt, "UpdatedAt of a merged row");
                Check.True(read.UpdatedAt >= firstMerge, "UpdatedAt does not go back between two merges");
                Check.Equal(entity.CreatedAt.UtcTicks, read.CreatedAt.UtcTicks, "CreatedAt after a merge (ticks)");
            });
    }

    private static void Delete(ScenarioBuilder s)
    {
        s.Add("Delete.RemovesThatRowOnly",
            "DeleteAsync removes the row with the id and no other row.",
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct);
                await store.CreateAsync(Rows.Item(c.Id("r2")), c.Ct);
                await store.CreateAsync(Rows.Item(c.Pk + "x|r1"), c.Ct);

                await store.DeleteAsync(c.Id("r1"), c.Ct);

                Check.Null(await store.OneAsync(c.Id("r1"), c.Ct), "the deleted row");
                Check.Equal(true, await store.ExistsAsync(c.Id("r2"), c.Ct), "Exists of the neighbour in the partition");
                Check.Equal(true, await store.ExistsAsync(c.Pk + "x|r1", c.Ct), "Exists of the same row key in another partition");
                Check.Equal(2, await store.CountAsync(null, c.Ct), "the row count after the delete");
            });

        s.Add("Delete.MissingRow_IsANoOp",
            "DeleteAsync of an id that is not stored completes without an exception, on an empty table and beside stored rows, and twice in a row.",
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.DeleteAsync(c.Id("never"), c.Ct);
                await store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct);
                await store.DeleteAsync(c.Id("r1"), c.Ct);
                await store.DeleteAsync(c.Id("r1"), c.Ct);
                await store.DeleteAsync(c.Id("r2"), c.Ct);
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count");
            });

        s.Add("Delete.TheIdCanBeCreatedAgain",
            "After DeleteAsync the id is free: CreateAsync stores a new row at it.",
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "first life"), c.Ct);
                await store.DeleteAsync(c.Id("r1"), c.Ct);
                await store.CreateAsync(Rows.Item(c.Id("r1"), "second life"), c.Ct);
                Check.Equal("second life", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name of the row created after the delete");
            });

        s.Add("DeletePartition.RemovesThatPartitionOnly_AndReturnsTheCount",
            "DeletePartitionAsync removes every row of the partition, returns how many there were, and leaves other partitions alone; an empty partition returns 0.",
            async c =>
            {
                var store = await c.ItemsAsync();
                for (var i = 0; i < 5; i++)
                    await store.CreateAsync(Rows.Item(c.Id("r" + i)), c.Ct);
                await store.CreateAsync(Rows.Item(c.Pk + "x|r0"), c.Ct);

                Check.Equal(5, await store.DeletePartitionAsync(c.Pk, c.Ct), "the count DeletePartitionAsync returns");

                Check.Equal(0, await store.CountAsync(c.Pk, c.Ct), "the row count of the deleted partition");
                Check.Equal(1, await store.CountAsync(c.Pk + "x", c.Ct), "the row count of the other partition");
                Check.Equal(0, await store.DeletePartitionAsync(c.Pk, c.Ct), "the count for a partition that is already empty");
            });

        s.Add("DeletePartition.MoreRowsThanOneTransactionHolds",
            "DeletePartitionAsync deletes a partition of more than 100 rows completely and returns the full count.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var batch = Enumerable.Range(0, 130).Select(i => Rows.Item(c.Id("r" + i.ToString("D3")))).ToList();
                await store.CreateBatchAsync(batch, c.Ct);

                Check.Equal(130, await store.DeletePartitionAsync(c.Pk, c.Ct), "the count DeletePartitionAsync returns");
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after the partition delete");
            });

        s.Add("DeletePartition.BlankPartition_ThrowsArgumentException",
            "DeletePartitionAsync refuses a null or blank partition with an ArgumentException: there is no \"delete the whole table\" by omission.",
            ArgumentValidationDiffers,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct);
                await Check.ThrowsAsync<ArgumentException>(() => store.DeletePartitionAsync(null!, c.Ct), "a partition delete of null");
                await Check.ThrowsAsync<ArgumentException>(() => store.DeletePartitionAsync("  ", c.Ct), "a partition delete of a blank partition");
                Check.Equal(1, await store.CountAsync(null, c.Ct), "the row count after the refused deletes");
            });
    }

    private static async Task<IStorage<ConformanceItem>> SeededAsync(ConformanceContext c)
    {
        // Two partitions; written out of order, so an order in the results is the store's.
        var store = await c.ItemsAsync();
        await store.CreateAsync(Rows.Item(c.Id("b2"), "two", 2), c.Ct);
        await store.CreateAsync(Rows.Item(c.Pk + "x|a1", "other", 10), c.Ct);
        await store.CreateAsync(Rows.Item(c.Id("b1"), "one", 1), c.Ct);
        await store.CreateAsync(Rows.Item(c.Id("a3"), "three", 3), c.Ct);
        await store.CreateAsync(Rows.Item(c.Id("a1"), "four", 4), c.Ct);
        return store;
    }

    private static void Query(ScenarioBuilder s)
    {
        s.Add("Query.Partition_ReturnsItsRows_InRowKeyOrder",
            "QueryAsync(partition) returns exactly the rows of that partition, in row-key order; an unknown partition is empty.",
            KeyOrderDiffers,
            async c =>
            {
                var store = await SeededAsync(c);
                Check.Sequence([c.Id("a1"), c.Id("a3"), c.Id("b1"), c.Id("b2")], Rows.Ids(await store.QueryAsync(c.Pk, c.Ct)), "the ids of the partition");
                Check.Sequence([], Rows.Ids(await store.QueryAsync(c.Pk + "none", c.Ct)), "the ids of an unknown partition");
            });

        s.Add("Query.NoPartition_ReturnsTheWholeTable_InKeyOrder",
            "QueryAsync() without a partition (null or blank) returns every row, ordered by partition key and then row key.",
            KeyOrderDiffers,
            async c =>
            {
                var store = await SeededAsync(c);
                string[] all = [c.Id("a1"), c.Id("a3"), c.Id("b1"), c.Id("b2"), c.Pk + "x|a1"];
                Check.Sequence(all, Rows.Ids(await store.QueryAsync((string?)null, c.Ct)), "the ids of the table");
                Check.Sequence(all, Rows.Ids(await store.QueryAsync(" ", c.Ct)), "the ids of the table (blank partition)");
            });

        s.Add("Query.SeesEveryWriteMadeThroughTheStore",
            "QueryAsync(partition) returns what the store's own writes left: a row created, changed and deleted after an earlier query is seen created, changed and gone.",
            async c =>
            {
                var store = await SeededAsync(c);
                _ = await store.QueryAsync(c.Pk, c.Ct);

                await store.CreateAsync(Rows.Item(c.Id("a2"), "late"), c.Ct);
                await store.UpdateAsync(c.Id("b1"), b => b.SetProperty(x => x.Name, "changed"), c.Ct);
                await store.DeleteAsync(c.Id("b2"), c.Ct);

                var rows = (await store.QueryAsync(c.Pk, c.Ct)).ToList();
                Check.Sequence([c.Id("a1"), c.Id("a2"), c.Id("a3"), c.Id("b1")], Rows.Ids(rows), "the ids of the partition after the writes");
                Check.Equal("changed", rows[3].Name, "Name of the row merged after the first query");
            });

        s.Add("Query.Options_PartitionPrefixAndTake",
            "QueryAsync(QueryOptions) scopes to the partition, to the rows whose row key starts with RowKeyPrefix, and to the first Take rows, in row-key order.",
            KeyOrderDiffers,
            async c =>
            {
                var store = await SeededAsync(c);
                Check.Sequence([c.Id("a1"), c.Id("a3"), c.Id("b1"), c.Id("b2")],
                    Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct)), "the ids of the partition");
                Check.Sequence([c.Id("b1"), c.Id("b2")],
                    Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk, RowKeyPrefix = "b" }, c.Ct)), "the ids under the prefix 'b'");
                Check.Sequence([c.Id("a1"), c.Id("a3")],
                    Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk, Take = 2 }, c.Ct)), "the first two ids of the partition");
                Check.Sequence([c.Id("b1")],
                    Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk, RowKeyPrefix = "b", Take = 1 }, c.Ct)), "the first id under the prefix 'b'");
                Check.Sequence([],
                    Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk, RowKeyPrefix = "zz" }, c.Ct)), "the ids under a prefix nothing has");
                Check.Equal(5, (await store.QueryAsync(new QueryOptions(), c.Ct)).Count, "the row count of a query without bounds");
            });

        s.Add("Query.Options_InvalidBounds_ThrowArgumentException",
            "QueryAsync(QueryOptions) refuses a RowKeyPrefix without a Partition, a Take that is not positive, and a ContinuationToken (only QueryPageAsync resumes), each with an ArgumentException.",
            ArgumentValidationDiffers,
            async c =>
            {
                var store = await SeededAsync(c);
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryAsync(new QueryOptions { RowKeyPrefix = "a" }, c.Ct), "a prefix without a partition");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryAsync(new QueryOptions { Partition = c.Pk, Take = 0 }, c.Ct), "Take = 0");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryAsync(new QueryOptions { Partition = c.Pk, Take = -1 }, c.Ct), "Take = -1");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryAsync(new QueryOptions { Partition = c.Pk, ContinuationToken = "anything" }, c.Ct),
                    "a ContinuationToken on a query that is not paged");
                await Check.ThrowsAsync<ArgumentException>(() => store.QueryAsync((QueryOptions)null!, c.Ct), "null options");
            });

        s.Add("Query.Options_Select_ReturnsOnlyTheNamedColumns",
            "A query with QueryOptions.Select returns entities that carry their id and the selected properties; every other property is left at its default.",
            ProjectionIgnored,
            async c =>
            {
                var store = await SeededAsync(c);
                var rows = await store.QueryAsync(new QueryOptions { Partition = c.Pk, RowKeyPrefix = "b", Select = ["Name"] }, c.Ct);

                Check.Sequence([c.Id("b1"), c.Id("b2")], Rows.Ids(rows), "the ids of a projected query");
                Check.Sequence(["one", "two"], rows.Select(r => r.Name), "the selected column");
                Check.Sequence([0, 0], rows.Select(r => r.Number), "a column that was not selected");

                var options = new QueryOptions { Partition = c.Pk, Select = ["Number"] };
                var streamed = await Rows.ListAsync(store.QueryStreamAsync(options, c.Ct), c.Ct);
                Check.Sequence([4, 3, 1, 2], streamed.Select(r => r.Number), "the selected column of a projected stream");
                Check.Sequence([null, null, null, null], streamed.Select(r => r.Name), "a column a projected stream did not select");
                var paged = await Rows.PageAllAsync(store, options with { Take = 3 }, c.Ct);
                Check.Sequence([c.Id("a1"), c.Id("a3"), c.Id("b1"), c.Id("b2")], Rows.Ids(paged), "the ids of a projected paged query");
                Check.Sequence([null, null, null, null], paged.Select(r => r.Name), "a column a projected page did not select");

                // The projection does not stay behind: a whole read of the same rows returns whole rows.
                Check.Sequence(["four", "three", "one", "two"], (await store.QueryAsync(c.Pk, c.Ct)).Select(r => r.Name), "Name in a whole read after the projected ones");
                Check.Equal("one", (await store.OneAsync(c.Id("b1"), c.Ct))?.Name, "Name read by OneAsync after the projected reads");
            });

        s.Add("Query.Predicate_FiltersByColumn_WithPartitionAndTake",
            "QueryAsync(predicate) returns the rows that match, in key order, scoped to the partition and capped by take when given.",
            async c =>
            {
                var store = await SeededAsync(c);
                Check.Sequence([c.Id("a1"), c.Id("a3"), c.Pk + "x|a1"],
                    Rows.Ids(await store.QueryAsync(x => x.Number > 2, null, null, c.Ct)), "the ids with Number > 2");
                Check.Sequence([c.Id("a1"), c.Id("a3")],
                    Rows.Ids(await store.QueryAsync(x => x.Number > 2, c.Pk, null, c.Ct)), "the ids with Number > 2 in the partition");
                Check.Sequence([c.Id("a1")],
                    Rows.Ids(await store.QueryAsync(x => x.Number > 2, c.Pk, 1, c.Ct)), "the first id with Number > 2 in the partition");
                Check.Sequence([c.Id("b1")],
                    Rows.Ids(await store.QueryAsync(x => x.Name == "one" && x.Number < 5, null, null, c.Ct)), "the ids with Name 'one' and Number < 5");
                Check.Sequence([],
                    Rows.Ids(await store.QueryAsync(x => x.Name == "nobody", null, null, c.Ct)), "the ids no row matches");
            });

        s.Add("Query.Predicate_InvalidArguments_Throw",
            "QueryAsync(predicate) refuses a null predicate and a take that is not positive with an ArgumentException.",
            ArgumentValidationDiffers,
            async c =>
            {
                var store = await SeededAsync(c);
                await Check.ThrowsAsync<ArgumentException>(() => store.QueryAsync(null!, null, null, c.Ct), "a null predicate");
                await Check.ThrowsAsync<ArgumentException>(() => store.QueryAsync(x => x.Number > 0, null, 0, c.Ct), "take = 0");
            });

        s.Add("Query.Predicate_ThatAzureTablesCannotFilterBy_ThrowsNotSupported",
            "A predicate Azure Tables cannot evaluate (a method call on a column) throws NotSupportedException on every provider, so a query that works locally works on Azure.",
            UntranslatablePredicateAccepted,
            async c =>
            {
                var store = await SeededAsync(c);
                await Check.ThrowsAsync<NotSupportedException>(
                    () => store.QueryAsync(x => x.Name!.ToUpperInvariant() == "ONE", null, null, c.Ct), "a predicate that calls ToUpperInvariant on a column");
                await Check.ThrowsAsync<NotSupportedException>(
                    () => store.AnyAsync(x => x.Name!.ToUpperInvariant() == "ONE", null, c.Ct), "AnyAsync with the same predicate");
            });

        s.Add("Any.TrueWhenARowMatches",
            "AnyAsync is true when a row matches the predicate in the scope, false otherwise.",
            async c =>
            {
                var store = await SeededAsync(c);
                Check.Equal(true, await store.AnyAsync(x => x.Number == 10, null, c.Ct), "Any over the table");
                Check.Equal(false, await store.AnyAsync(x => x.Number == 10, c.Pk, c.Ct), "Any in a partition the match is not in");
                Check.Equal(true, await store.AnyAsync(x => x.Number == 10, c.Pk + "x", c.Ct), "Any in the partition of the match");
                Check.Equal(false, await store.AnyAsync(x => x.Number == 99, null, c.Ct), "Any for a value no row has");
            });

        s.Add("Count.CountsTheTableAndAPartition",
            "CountAsync returns the number of rows of the table, or of one partition; zero for an unknown partition and an empty table.",
            async c =>
            {
                var empty = await c.ItemsAsync();
                Check.Equal(0, await empty.CountAsync(null, c.Ct), "the count of an empty table");
                var store = await SeededAsync(c);
                Check.Equal(5, await store.CountAsync(null, c.Ct), "the count of the table");
                Check.Equal(4, await store.CountAsync(c.Pk, c.Ct), "the count of the partition");
                Check.Equal(0, await store.CountAsync(c.Pk + "none", c.Ct), "the count of an unknown partition");
            });
    }

    private static void Streams(ScenarioBuilder s)
    {
        s.Add("Stream.Options_YieldsWhatTheQueryReturns",
            "QueryStreamAsync(QueryOptions) yields the rows QueryAsync(QueryOptions) returns, in the same order; null options stream the whole table.",
            KeyOrderDiffers,
            async c =>
            {
                var store = await SeededAsync(c);
                Check.Sequence([c.Id("a1"), c.Id("a3"), c.Id("b1"), c.Id("b2"), c.Pk + "x|a1"],
                    Rows.Ids(await Rows.ListAsync(store.QueryStreamAsync((QueryOptions?)null, c.Ct), c.Ct)), "the ids of a stream without options");
                Check.Sequence([c.Id("b1"), c.Id("b2")],
                    Rows.Ids(await Rows.ListAsync(store.QueryStreamAsync(new QueryOptions { Partition = c.Pk, RowKeyPrefix = "b" }, c.Ct), c.Ct)),
                    "the ids of a stream under the prefix 'b'");
                Check.Sequence([c.Id("a1"), c.Id("a3"), c.Id("b1")],
                    Rows.Ids(await Rows.ListAsync(store.QueryStreamAsync(new QueryOptions { Partition = c.Pk, Take = 3 }, c.Ct), c.Ct)),
                    "the first three ids of a stream of the partition");
            });

        s.Add("Stream.Predicate_YieldsTheMatches",
            "QueryStreamAsync(predicate) yields the rows QueryAsync(predicate) returns, in the same order.",
            async c =>
            {
                var store = await SeededAsync(c);
                Check.Sequence([c.Id("a1"), c.Id("a3")],
                    Rows.Ids(await Rows.ListAsync(store.QueryStreamAsync(x => x.Number > 2, c.Pk, null, c.Ct), c.Ct)),
                    "the streamed ids with Number > 2 in the partition");
                Check.Sequence([c.Id("a1")],
                    Rows.Ids(await Rows.ListAsync(store.QueryStreamAsync(x => x.Number > 2, null, 1, c.Ct), c.Ct)),
                    "the first streamed id with Number > 2");
            });

        s.Add("Stream.InvalidOptions_FailTheEnumeration",
            "A stream with invalid bounds (a prefix without a partition, a Take that is not positive) throws ArgumentException when it is called or when it is first read; it never yields rows.",
            ArgumentValidationDiffers,
            async c =>
            {
                var store = await SeededAsync(c);
                await Check.ThrowsAsync<ArgumentException>(
                    () => Rows.ListAsync(store.QueryStreamAsync(new QueryOptions { RowKeyPrefix = "a" }, c.Ct), c.Ct), "a stream with a prefix and no partition");
                await Check.ThrowsAsync<ArgumentException>(
                    () => Rows.ListAsync(store.QueryStreamAsync(new QueryOptions { Partition = c.Pk, Take = 0 }, c.Ct), c.Ct), "a stream with Take = 0");
                await Check.ThrowsAsync<ArgumentException>(
                    () => Rows.ListAsync(store.QueryStreamAsync(x => x.Number > 0, null, 0, c.Ct), c.Ct), "a predicate stream with take = 0");
            });

        s.Add("Stream.TokenCancelledWhileReading_EndsWithOperationCanceled",
            "A stream whose token is cancelled between two rows ends with OperationCanceledException instead of yielding the rest.",
            CancellationNotObserved,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateBatchAsync(Enumerable.Range(0, 20).Select(i => Rows.Item(c.Id("r" + i.ToString("D2")))).ToList(), c.Ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(c.Ct);

                var seen = 0;
                await Check.ThrowsAsync<OperationCanceledException>(
                    async () =>
                    {
                        await foreach (var _ in store.QueryStreamAsync(new QueryOptions { Partition = c.Pk }, cts.Token))
                        {
                            if (++seen == 3)
                                cts.Cancel();
                        }
                    },
                    "a stream whose token is cancelled after its third row");
                Check.True(seen < 20, $"the stream stops before its last row once cancelled; it yielded {seen} of 20");
            });
    }

    private static void Paging(ScenarioBuilder s)
    {
        s.Add("Page.WalksEveryRowOnce_InKeyOrder",
            "QueryPageAsync followed from token to token returns every row of the query exactly once, in key order, in pages of at most Take rows, and ends with a null token.",
            KeyOrderDiffers,
            async c =>
            {
                var store = await c.ItemsAsync();
                var ids = Enumerable.Range(0, 25).Select(i => c.Id("r" + i.ToString("D2"))).ToList();
                await store.CreateBatchAsync(ids.AsEnumerable().Reverse().Select(id => Rows.Item(id)).ToList(), c.Ct);
                await store.CreateAsync(Rows.Item(c.Pk + "x|r00"), c.Ct);

                var first = await store.QueryPageAsync(new QueryOptions { Partition = c.Pk, Take = 10 }, c.Ct);
                Check.Sequence(ids.Take(10), Rows.Ids(first.Items), "the first page of 10");
                Check.NotNull(first.ContinuationToken, "a continuation token while rows remain");

                var paged = await Rows.PageAllAsync(store, new QueryOptions { Partition = c.Pk, Take = 10 }, c.Ct);
                Check.Sequence(ids, Rows.Ids(paged), "the ids of the partition, paged by 10");

                var table = await Rows.PageAllAsync(store, new QueryOptions { Take = 7 }, c.Ct);
                Check.Sequence(ids.Concat([c.Pk + "x|r00"]), Rows.Ids(table), "the ids of the table, paged by 7");
            });

        s.Add("Page.EmptyAndExactlyFullResults_EndWithoutAToken",
            "QueryPageAsync over no rows returns an empty page and no token; a result that fits the page returns no token.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var empty = await store.QueryPageAsync(new QueryOptions { Partition = c.Pk, Take = 5 }, c.Ct);
                Check.Equal(0, empty.Items.Count, "the row count of a page of an empty partition");
                Check.Null(empty.ContinuationToken, "the token of a page of an empty partition");

                await store.CreateBatchAsync(Enumerable.Range(0, 3).Select(i => Rows.Item(c.Id("r" + i))).ToList(), c.Ct);
                var all = await Rows.PageAllAsync(store, new QueryOptions { Partition = c.Pk, Take = 5 }, c.Ct);
                Check.Equal(3, all.Count, "the row count of a partition of three, paged by five");
                var whole = await store.QueryPageAsync(new QueryOptions { Partition = c.Pk, Take = 5 }, c.Ct);
                Check.Null(whole.ContinuationToken, "the token of a page that holds every row and is not full");
            });

        s.Add("Page.Prefix_PagesOnlyThePrefix",
            "QueryPageAsync with a RowKeyPrefix pages the rows under the prefix and no others.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var rows = Enumerable.Range(0, 6).Select(i => Rows.Item(c.Id("a" + i)))
                    .Concat(Enumerable.Range(0, 6).Select(i => Rows.Item(c.Id("b" + i)))).ToList();
                await store.CreateBatchAsync(rows, c.Ct);

                var paged = await Rows.PageAllAsync(store, new QueryOptions { Partition = c.Pk, RowKeyPrefix = "b", Take = 4 }, c.Ct);
                Check.Sequence(Enumerable.Range(0, 6).Select(i => c.Id("b" + i)), Rows.Ids(paged), "the ids under the prefix 'b', paged by 4");
            });

        s.Add("Page.TokenIsBoundToItsQuery",
            "A continuation token replayed with another partition, another prefix or another page size throws ArgumentException instead of returning rows of the wrong query.",
            ContinuationTokenNotBound,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateBatchAsync(Enumerable.Range(0, 12).Select(i => Rows.Item(c.Id("r" + i.ToString("D2")))).ToList(), c.Ct);
                await store.CreateBatchAsync(Enumerable.Range(0, 12).Select(i => Rows.Item(c.Pk + "x|r" + i.ToString("D2"))).ToList(), c.Ct);
                var page = await store.QueryPageAsync(new QueryOptions { Partition = c.Pk, Take = 5 }, c.Ct);
                var token = Check.NotNull(page.ContinuationToken, "a continuation token while rows remain");

                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(new QueryOptions { Partition = c.Pk + "x", Take = 5, ContinuationToken = token }, c.Ct),
                    "a token replayed with another partition");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(new QueryOptions { Partition = c.Pk, Take = 6, ContinuationToken = token }, c.Ct),
                    "a token replayed with another page size");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(new QueryOptions { Partition = c.Pk, RowKeyPrefix = "r", Take = 5, ContinuationToken = token }, c.Ct),
                    "a token replayed with a prefix");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(new QueryOptions { Take = 5, ContinuationToken = token }, c.Ct),
                    "a token replayed without the partition");

                var next = await store.QueryPageAsync(new QueryOptions { Partition = c.Pk, Take = 5, ContinuationToken = token }, c.Ct);
                Check.Sequence(Enumerable.Range(5, 5).Select(i => c.Id("r" + i.ToString("D2"))), Rows.Ids(next.Items), "the second page of its own query");
            });

        s.Add("Page.MalformedTokenAndInvalidBounds_ThrowArgumentException",
            "QueryPageAsync refuses a token it did not issue, a Take that is not positive and a prefix without a partition, each with an ArgumentException.",
            [ContinuationTokenNotBound, ArgumentValidationDiffers],
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct);
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(new QueryOptions { Partition = c.Pk, ContinuationToken = "not a token" }, c.Ct), "a malformed token");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(new QueryOptions { Partition = c.Pk, Take = 0 }, c.Ct), "Take = 0");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.QueryPageAsync(new QueryOptions { RowKeyPrefix = "r" }, c.Ct), "a prefix without a partition");
                await Check.ThrowsAsync<ArgumentException>(() => store.QueryPageAsync(null!, c.Ct), "null options");
            });
    }

    private static void OrderAndIds(ScenarioBuilder s)
    {
        s.Add("Order.RowKeys_AreOrdinal_ForCharactersOfTheBasicMultilingualPlane",
            "Rows of a partition are returned in ordinal (UTF-16 code unit) row-key order: digits, upper case, lower case, then non-ASCII letters, a shorter key before a longer one it starts.",
            KeyOrderDiffers,
            async c =>
            {
                var store = await c.ItemsAsync(new ConformanceStoreSettings { IdNormalization = IdNormalization.AsWritten });
                string[] keys = ["a", "B", "é", "0", "aa", "Z", "9", "a-", "_", "A", "я", "b", "a0", "中", "~", "z", "AB", "￦"];
                foreach (var key in keys)
                    await store.CreateAsync(Rows.Item(c.Id(key)), c.Ct);

                var expected = keys.OrderBy(k => k, StringComparer.Ordinal).Select(c.Id).ToList();
                Check.Sequence(expected, Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct)), "the ids of a bounded query");
                Check.Sequence(expected, Rows.Ids(await store.QueryAsync(c.Pk, c.Ct)), "the ids of a partition query");
                Check.Sequence(expected, Rows.Ids(await Rows.PageAllAsync(store, new QueryOptions { Partition = c.Pk, Take = 5 }, c.Ct)), "the ids of a paged query");
                Check.Sequence(expected, Rows.Ids(await Rows.ListAsync(store.QueryStreamAsync(new QueryOptions { Partition = c.Pk }, c.Ct), c.Ct)), "the ids of a stream");
            });

        s.Add("Order.PartitionKeys_AreOrdinal",
            "A read of the whole table returns partitions in ordinal partition-key order, and the rows of each in row-key order.",
            KeyOrderDiffers,
            async c =>
            {
                var store = await c.ItemsAsync(new ConformanceStoreSettings { IdNormalization = IdNormalization.AsWritten });
                string[] partitions = [c.Pk + "b", c.Pk + "B", c.Pk + "a", c.Pk, c.Pk + "-", c.Pk + "A1", c.Pk + "é"];
                foreach (var partition in partitions)
                {
                    await store.CreateAsync(Rows.Item(partition + "|r2"), c.Ct);
                    await store.CreateAsync(Rows.Item(partition + "|r1"), c.Ct);
                }

                var expected = partitions.OrderBy(p => p, StringComparer.Ordinal).SelectMany(p => new[] { p + "|r1", p + "|r2" }).ToList();
                Check.Sequence(expected, Rows.Ids(await store.QueryAsync(new QueryOptions(), c.Ct)), "the ids of the table");
                Check.Sequence(expected, Rows.Ids(await Rows.PageAllAsync(store, new QueryOptions { Take = 3 }, c.Ct)), "the ids of the table, paged by 3");
            });

        s.Add("Id.Normalized_TrimsLowerCasesAndDashesSpaces",
            "Under IdNormalization.Normalized (the default) an id is trimmed, lower-cased and its spaces become '-' on every write and read, and the entity carries the normalized id after a write.",
            IdNormalizationDiffers,
            async c =>
            {
                var store = await c.ItemsAsync();
                var natural = "  " + c.Pk.ToUpperInvariant() + "|Row One ";
                var stored = c.Pk + "|row-one";

                var created = await store.CreateAsync(Rows.Item(natural, "name"), c.Ct);
                Check.Equal(stored, created.Id, "the id of the created entity");
                Check.Equal(stored, (await store.OneAsync(stored, c.Ct))?.Id, "the id read by its stored form");
                Check.Equal(stored, (await store.OneAsync(natural, c.Ct))?.Id, "the id read by its natural form");
                Check.Equal(true, await store.ExistsAsync(c.Pk + "|ROW ONE", c.Ct), "Exists by another spelling");
                await Check.ThrowsAsync<DuplicateKeyException>(
                    () => store.CreateAsync(Rows.Item(c.Pk + "|ROW-ONE"), c.Ct), "a create of another spelling of a stored id");
                Check.Sequence([stored], Rows.Ids(await store.QueryAsync(c.Pk.ToUpperInvariant(), c.Ct)), "the ids of the partition, asked for in upper case");
                Check.Sequence([stored],
                    Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = " " + c.Pk.ToUpperInvariant(), RowKeyPrefix = "Row O" }, c.Ct)),
                    "the ids under a prefix given in natural form");
                Check.Equal(1, await store.CountAsync(c.Pk.ToUpperInvariant(), c.Ct), "the count of the partition, asked for in upper case");

                await store.UpdateAsync(c.Pk + "|Row One", b => b.SetProperty(x => x.Name, "merged"), c.Ct);
                Check.Equal("merged", (await store.OneAsync(stored, c.Ct))?.Name, "Name after a merge by the natural id");
                await store.DeleteAsync(natural, c.Ct);
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after a delete by the natural id");
            });

        s.Add("Id.AsWritten_KeepsCaseAndSpaces",
            "Under IdNormalization.AsWritten ids are stored as given: two ids that differ in case are two rows.",
            IdNormalizationDiffers,
            async c =>
            {
                var store = await c.ItemsAsync(new ConformanceStoreSettings { IdNormalization = IdNormalization.AsWritten });
                var created = await store.CreateAsync(Rows.Item(c.Pk + "|Row One", "mixed"), c.Ct);
                await store.CreateAsync(Rows.Item(c.Pk + "|row one", "lower"), c.Ct);

                Check.Equal(c.Pk + "|Row One", created.Id, "the id of the created entity");
                Check.Equal("mixed", (await store.OneAsync(c.Pk + "|Row One", c.Ct))?.Name, "Name of the mixed-case row");
                Check.Equal("lower", (await store.OneAsync(c.Pk + "|row one", c.Ct))?.Name, "Name of the lower-case row");
                Check.Null(await store.OneAsync(c.Pk + "|ROW ONE", c.Ct), "a read by a spelling that was never written");
                Check.Equal(2, await store.CountAsync(c.Pk, c.Ct), "the row count");
            });

        s.Add("Id.SplitsAtTheFirstSeparator_AndAOneSegmentIdIsBothKeys",
            "An id is partition key and row key separated by the first '|': further separators belong to the row key, and an id without one is partition key and row key at once.",
            IdNormalizationDiffers,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Pk + "|a|b", "three segments"), c.Ct);
                await store.CreateAsync(Rows.Item(c.Pk, "one segment"), c.Ct);

                var rows = (await store.QueryAsync(c.Pk, c.Ct)).ToList();
                Check.Equal(2, rows.Count, "the rows of the partition both ids belong to");
                Check.Equal("three segments", (await store.OneAsync(c.Pk + "|a|b", c.Ct))?.Name, "Name of the row whose row key holds a separator");
                Check.Equal("one segment", (await store.OneAsync(c.Pk, c.Ct))?.Name, "Name read by the one-segment id");
                Check.Equal("one segment", (await store.OneAsync(c.Pk + "|" + c.Pk, c.Ct))?.Name, "Name read by the two-segment spelling of the same row");
                await Check.ThrowsAsync<DuplicateKeyException>(
                    () => store.CreateAsync(Rows.Item(c.Pk + "|" + c.Pk), c.Ct), "a create of the two-segment spelling of a stored one-segment id");
            });
    }

    private static void ETagsAndTimestamps(ScenarioBuilder s)
    {
        s.Add("ETag.EveryWriteGivesTheRowANewOne",
            "Each write of a row (create, update, merge, upsert) gives it an ETag it did not have before.",
            ETagRulesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var seen = new List<string>();
                async Task RecordAsync(string after)
                {
                    var etag = (await Rows.StoredAsync(store, c.Id("r1"), c.Ct))?.ETag;
                    Check.NotEmpty(etag, "the ETag read after " + after);
                    Check.True(!seen.Contains(etag!), $"the ETag after {after} is one the row did not have before");
                    seen.Add(etag!);
                }

                await store.CreateAsync(Rows.Item(c.Id("r1"), "v1"), c.Ct);
                await RecordAsync("the create");
                var read = (await store.OneAsync(c.Id("r1"), c.Ct))!;
                read.Name = "v2";
                await store.UpdateAsync(read, c.Ct);
                await RecordAsync("the update");
                await store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Name, "v3"), c.Ct);
                await RecordAsync("the merge");
                await store.UpsertAsync(Rows.Item(c.Id("r1"), "v4"), c.Ct);
                await RecordAsync("the upsert");
            });

        s.Add("ETag.AWriteReturnsTheETagForTheNextUpdate",
            "The entity a write returns carries the row's current ETag: it can be updated again without a read in between.",
            ETagRulesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var entity = await store.CreateAsync(Rows.Item(c.Id("r1"), "v1"), c.Ct);
                entity.Name = "v2";
                entity = await store.UpdateAsync(entity, ConcurrencyMode.Strict, c.Ct);
                entity.Name = "v3";
                entity = await store.UpdateAsync(entity, ConcurrencyMode.Strict, c.Ct);
                entity = await store.UpsertAsync(entity, c.Ct);
                entity.Name = "v4";
                await store.UpdateAsync(entity, ConcurrencyMode.Strict, c.Ct);
                Check.Equal("v4", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after a chain of updates on returned ETags");
            });

        s.Add("ETag.OneAsync_AfterTheStoresOwnWrites_NeverCarriesAVersionNewerThanItsContent",
            "OneAsync may answer from the store's read cache. After the store's own writes it returns the content as last written, and an ETag that is the row's current one or one the row had before: a conditional write with it succeeds or conflicts, and never overwrites a version the caller has not seen.",
            ETagRulesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var versions = new List<string?>();
                async Task<ConformanceItem> ReadAsync(string expectedName, string after)
                {
                    versions.Add((await Rows.StoredAsync(store, c.Id("r1"), c.Ct))?.ETag);
                    var one = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row read by OneAsync after " + after);
                    Check.Equal(expectedName, one.Name, "Name read by OneAsync after " + after);
                    Check.True(versions.Contains(one.ETag), $"the ETag OneAsync reports after {after} is the row's current one or an earlier one of it");
                    return one;
                }

                await store.CreateAsync(Rows.Item(c.Id("r1"), "v1"), c.Ct);
                await ReadAsync("v1", "the create");
                await store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Name, "v2"), c.Ct);
                var merged = await ReadAsync("v2", "an unconditional merge");

                // Whatever version the read carried: the update on it lands on the content it read, or conflicts.
                merged.Name = "v3";
                try
                {
                    await store.UpdateAsync(merged, ConcurrencyMode.Strict, c.Ct);
                }
                catch (ConcurrencyConflictException)
                {
                    // The cached version was the one before the merge: the conflict makes the store read the row.
                    var fresh = await ReadAsync("v2", "a conflict");
                    Check.Equal(versions[versions.Count - 1], fresh.ETag, "the ETag OneAsync reports after a conflict (the row's current one)");
                    fresh.Name = "v3";
                    await store.UpdateAsync(fresh, ConcurrencyMode.Strict, c.Ct);
                }
                await ReadAsync("v3", "the update");
            });

        s.Add("Timestamp.NullAfterAWrite_SetOnARead",
            "IEntity.Timestamp is null on the entity a write returns (a write does not report the store's last-write time) and set on every entity a read of the table returns.",
            TimestampsDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var entity = Rows.Item(c.Id("r1"), "v1");
                entity.Timestamp = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
                Check.Null((await store.CreateAsync(entity, c.Ct)).Timestamp, "Timestamp after CreateAsync");
                var read = Check.NotNull(await Rows.StoredAsync(store, c.Id("r1"), c.Ct), "the row");
                Check.NotNull((object?)read.Timestamp, "Timestamp of a row read from the table");
                Check.Null((await store.UpdateAsync(read, c.Ct)).Timestamp, "Timestamp after UpdateAsync");
                read = Check.NotNull(await Rows.StoredAsync(store, c.Id("r1"), c.Ct), "the row");
                Check.Null((await store.UpsertAsync(read, c.Ct)).Timestamp, "Timestamp after UpsertAsync");
                Check.NotNull((object?)(await Rows.ListAsync(store.QueryStreamAsync(new QueryOptions { Partition = c.Pk }, c.Ct), c.Ct)).Single().Timestamp,
                    "Timestamp of a row read by a stream");
                Check.NotNull((object?)(await store.QueryPageAsync(new QueryOptions { Partition = c.Pk }, c.Ct)).Items.Single().Timestamp,
                    "Timestamp of a row read by a page");

                var listed = (await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct)).Single();
                Check.NotNull((object?)listed.Timestamp, "Timestamp of a row read by a query");
                var before = DateTimeOffset.UtcNow.AddMinutes(-1);
                Check.True(listed.Timestamp >= before, $"Timestamp is the store's time of the last write; it is {Check.Show(listed.Timestamp)}");
            });
    }

    private static void Batches(ScenarioBuilder s)
    {
        s.Add("CreateBatch.WritesEveryRow_AcrossPartitionsAndTransactions",
            "CreateBatchAsync inserts every entity and returns their number, across partitions and beyond the 100 rows one transaction holds; an empty batch writes nothing and returns 0.",
            async c =>
            {
                var store = await c.ItemsAsync();
                Check.Equal(0, await store.CreateBatchAsync([], c.Ct), "the count of an empty batch");

                var batch = Enumerable.Range(0, 150).Select(i => Rows.Item(c.Id("r" + i.ToString("D3")), "n" + i, i))
                    .Concat(Enumerable.Range(0, 3).Select(i => Rows.Item(c.Pk + "x|r" + i, "x" + i, i))).ToList();
                Check.Equal(153, await store.CreateBatchAsync(batch, c.Ct), "the count CreateBatchAsync returns");

                Check.Equal(150, await store.CountAsync(c.Pk, c.Ct), "the row count of the first partition");
                Check.Equal(3, await store.CountAsync(c.Pk + "x", c.Ct), "the row count of the second partition");
                Check.Equal("n149", (await store.OneAsync(c.Id("r149"), c.Ct))?.Name, "Name of the last row of the batch");
            });

        s.Add("CreateBatch.ExistingId_ThrowsDuplicateKey_AndWritesNothingOfThatTransaction",
            "CreateBatchAsync of a batch that fits one transaction and names a stored id throws DuplicateKeyException and stores none of its rows.",
            [BatchTransactionsDiffer, FailureTypesDiffer],
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r2"), "original"), c.Ct);

                await Check.ThrowsAsync<DuplicateKeyException>(
                    () => store.CreateBatchAsync([Rows.Item(c.Id("r1"), "new"), Rows.Item(c.Id("r2"), "clash"), Rows.Item(c.Id("r3"), "new")], c.Ct),
                    "a create batch that names a stored id");

                Check.Sequence([c.Id("r2")], Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct)), "the ids after the refused batch");
                Check.Equal("original", (await store.OneAsync(c.Id("r2"), c.Ct))?.Name, "Name of the stored row after the refused batch");
            });

        s.Add("CreateBatch.FailingTransaction_KeepsTheEarlierOnes",
            "A batch is atomic per transaction only: when the second transaction of a CreateBatchAsync fails, the first hundred rows stay stored and none of the second is.",
            BatchTransactionsDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r120"), "original"), c.Ct);
                var batch = Enumerable.Range(0, 150).Select(i => Rows.Item(c.Id("r" + i.ToString("D3")), "n" + i)).ToList();

                await Check.ThrowsAsync<DuplicateKeyException>(() => store.CreateBatchAsync(batch, c.Ct), "a create batch whose second transaction names a stored id");

                Check.Sequence(Enumerable.Range(0, 100).Select(i => c.Id("r" + i.ToString("D3"))).Concat([c.Id("r120")]),
                    Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct)), "the ids after the batch failed in its second transaction");
            });

        s.Add("UpsertBatch.InsertsAndReplaces",
            "UpsertBatchAsync inserts the rows that are missing, replaces those that are stored, and returns the number of entities passed.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var old = Rows.Item(c.Id("r1"), "old", 1);
                old.Note = "the old row has a note";
                await store.CreateAsync(old, c.Ct);
                Check.Equal(0, await store.UpsertBatchAsync([], c.Ct), "the count of an empty batch");

                Check.Equal(3, await store.UpsertBatchAsync(
                    [Rows.Item(c.Id("r1"), "replaced", 10), Rows.Item(c.Id("r2"), "inserted", 20), Rows.Item(c.Pk + "x|r1", "other partition", 30)], c.Ct),
                    "the count UpsertBatchAsync returns");

                var replaced = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the replaced row");
                Check.Equal("replaced", replaced.Name, "Name of the replaced row");
                Check.Null(replaced.Note, "a column the replacement leaves null");
                Check.Equal("inserted", (await store.OneAsync(c.Id("r2"), c.Ct))?.Name, "Name of the inserted row");
                Check.Equal(3, await store.CountAsync(null, c.Ct), "the row count after the batch");
            });

        s.Add("Batch.ResetsTheETagsOfItsEntities",
            "A batch does not report the rows' new ETags: CreateBatchAsync and UpsertBatchAsync set the ETag of every entity passed to null.",
            ETagRulesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var created = new[] { Rows.Item(c.Id("r1")), Rows.Item(c.Id("r2")) };
                created[0].ETag = "W/\"left over\"";
                await store.CreateBatchAsync(created, c.Ct);
                Check.Sequence([null, null], created.Select(e => e.ETag), "the ETags after CreateBatchAsync");

                var read = (await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct)).ToList();
                await store.UpsertBatchAsync(read, c.Ct);
                Check.Sequence([null, null], read.Select(e => e.ETag), "the ETags after UpsertBatchAsync");
            });

        s.Add("Batch.NullEntityOrBlankId_ThrowsArgumentException_AndWritesNothing",
            "A batch that holds a null entity or an entity without an id throws ArgumentException before anything is written.",
            ArgumentValidationDiffers,
            async c =>
            {
                var store = await c.ItemsAsync();
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.CreateBatchAsync([Rows.Item(c.Id("r1")), null!], c.Ct), "a create batch with a null entity");
                await Check.ThrowsAsync<ArgumentException>(
                    () => store.UpsertBatchAsync([Rows.Item(c.Id("r1")), Rows.Item(" ")], c.Ct), "an upsert batch with a blank id");
                await Check.ThrowsAsync<ArgumentException>(() => store.CreateBatchAsync(null!, c.Ct), "a null batch");
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after the refused batches");
            });
    }

    private static void Cancellation(ScenarioBuilder s)
    {
        s.Add("Cancellation.AWriteWithACancelledToken_ThrowsAndChangesNothing",
            "Every write given a token that is already cancelled throws OperationCanceledException and leaves storage as it was.",
            CancellationNotObserved,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("a"), "original"), c.Ct);
                var read = Check.NotNull(await store.OneAsync(c.Id("a"), c.Ct), "the row");
                read.Name = "changed";

                await Check.CancelledAsync(() => store.CreateAsync(Rows.Item(c.Id("b")), Rows.Cancelled), "CreateAsync");
                await Check.CancelledAsync(() => store.UpsertAsync(Rows.Item(c.Id("a"), "changed"), Rows.Cancelled), "UpsertAsync");
                await Check.CancelledAsync(() => store.UpdateAsync(read, Rows.Cancelled), "UpdateAsync");
                await Check.CancelledAsync(() => store.UpdateAsync(read, ConcurrencyMode.LastWriterWins, Rows.Cancelled), "UpdateAsync (LastWriterWins)");
                await Check.CancelledAsync(() => store.UpdateAsync(c.Id("a"), b => b.SetProperty(x => x.Name, "changed"), Rows.Cancelled), "the builder UpdateAsync");
                await Check.CancelledAsync(() => store.CreateBatchAsync([Rows.Item(c.Id("c"))], Rows.Cancelled), "CreateBatchAsync");
                await Check.CancelledAsync(() => store.UpsertBatchAsync([Rows.Item(c.Id("a"), "changed")], Rows.Cancelled), "UpsertBatchAsync");
                await Check.CancelledAsync(() => store.DeleteAsync(c.Id("a"), Rows.Cancelled), "DeleteAsync");
                await Check.CancelledAsync(() => store.DeletePartitionAsync(c.Pk, Rows.Cancelled), "DeletePartitionAsync");

                var rows = await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct);
                Check.Sequence([c.Id("a")], Rows.Ids(rows), "the ids after the cancelled writes");
                Check.Equal("original", rows[0].Name, "Name after the cancelled writes");
            });

        s.Add("Cancellation.AReadWithACancelledToken_ThrowsOrAnswersCorrectly",
            "A read given a token that is already cancelled either throws OperationCanceledException or returns the correct answer (one it had at hand); it never returns a wrong answer or another exception.",
            CancellationNotObserved,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("a"), "stored", 1), c.Ct);

                async Task ReadAsync<TResult>(Func<Task<TResult>> read, Func<TResult, bool> correct, string what)
                {
                    TResult result;
                    try
                    {
                        result = await read().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    Check.True(correct(result), $"{what} with a cancelled token, when it answers, answers correctly");
                }

                await ReadAsync(() => store.OneAsync(c.Id("a"), Rows.Cancelled), r => r?.Name == "stored", "OneAsync");
                await ReadAsync(() => store.ExistsAsync(c.Id("a"), Rows.Cancelled), r => r, "ExistsAsync");
                await ReadAsync(() => store.CountAsync(c.Pk, Rows.Cancelled), r => r == 1, "CountAsync");
                await ReadAsync(() => store.QueryAsync(c.Pk, Rows.Cancelled), r => r.Count() == 1, "QueryAsync(partition)");
                await ReadAsync(() => store.QueryAsync(new QueryOptions { Partition = c.Pk }, Rows.Cancelled), r => r.Count == 1, "QueryAsync(options)");
                await ReadAsync(() => store.QueryAsync(x => x.Number == 1, c.Pk, null, Rows.Cancelled), r => r.Count == 1, "QueryAsync(predicate)");
                await ReadAsync(() => store.AnyAsync(x => x.Number == 1, c.Pk, Rows.Cancelled), r => r, "AnyAsync");
                await ReadAsync(() => store.QueryPageAsync(new QueryOptions { Partition = c.Pk }, Rows.Cancelled), r => r.Items.Count == 1, "QueryPageAsync");
                await ReadAsync(() => Rows.ListAsync(store.QueryStreamAsync(new QueryOptions { Partition = c.Pk }, Rows.Cancelled), Rows.Cancelled),
                    r => r.Count == 1, "QueryStreamAsync");
            });
    }

    private static void Verbs(ScenarioBuilder s)
    {
        s.Add("Verbs.GetOrCreate_ReturnsTheStoredRow_OrCreatesIt",
            "GetOrCreateAsync creates the row from the factory when it is missing (with the id asked for) and returns the stored row, without calling the factory, when it exists.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var created = await store.GetOrCreateAsync(c.Id("r1"), () => Rows.Item("ignored", "made by the factory"), c.Ct);
                Check.Equal(c.Id("r1"), created.Id, "the id of the created entity");
                Check.NotEmpty(created.ETag, "the ETag of the created entity");

                var factoryCalls = 0;
                var existing = await store.GetOrCreateAsync(c.Id("r1"), () =>
                {
                    factoryCalls++;
                    return Rows.Item("ignored", "second factory");
                }, c.Ct);
                Check.Equal("made by the factory", existing.Name, "Name of the row GetOrCreateAsync found");
                Check.Equal(0, factoryCalls, "the factory calls for a row that exists");
                Check.Equal(1, await store.CountAsync(null, c.Ct), "the row count");
            });

        s.Add("Verbs.Mutate_ReadsAppliesAndWrites_AndRefusesAMissingRow",
            "MutateAsync reads the row, applies the change and writes it on the version it read; a row that does not exist throws InvalidOperationException.",
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "name", 1), c.Ct);

                var mutated = await store.MutateAsync(c.Id("r1"), e => e.Number += 41, ct: c.Ct);

                Check.Equal(42, mutated.Number, "Number of the entity MutateAsync returns");
                Check.Equal(42, (await store.OneAsync(c.Id("r1"), c.Ct))?.Number, "Number read back");
                await Check.ThrowsAsync<InvalidOperationException>(
                    () => store.MutateAsync(c.Id("never"), e => e.Number++, ct: c.Ct), "MutateAsync of a missing row");
                Check.Equal(1, await store.CountAsync(null, c.Ct), "the row count after the refused mutation");
            });

        s.Add("Verbs.MutateOrCreate_CreatesThenMutates",
            "MutateOrCreateAsync creates the row from the factory and applies the change to it when it is missing, and applies the change to the stored row when it exists.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var first = await store.MutateOrCreateAsync(c.Id("r1"), () => Rows.Item("ignored", "fresh", 100), e => e.Number++, ct: c.Ct);
                Check.Equal(101, first.Number, "Number after the create-and-mutate");
                Check.Equal(c.Id("r1"), first.Id, "the id of the created entity");

                var second = await store.MutateOrCreateAsync(c.Id("r1"), () => Rows.Item("ignored", "never used", -1), e => e.Number++, ct: c.Ct);
                Check.Equal(102, second.Number, "Number after the mutation of the stored row");
                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row");
                Check.Equal(102, read.Number, "Number read back");
                Check.Equal("fresh", read.Name, "Name read back");
            });

        s.Add("Verbs.TryMutate_ReportsUpdatedOrNotFound",
            "TryMutateAsync returns Updated with the persisted entity for a stored row and NotFound, without an exception, for a missing one.",
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "name", 1), c.Ct);

                var updated = await store.TryMutateAsync(c.Id("r1"), e => e.Name = "mutated", ct: c.Ct);
                Check.Equal(MutationStatus.Updated, updated.Status, "the status for a stored row");
                Check.Equal(true, updated.Succeeded, "Succeeded for a stored row");
                Check.Equal("mutated", updated.Entity?.Name, "Name of the persisted entity");
                Check.Equal("mutated", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name read back");

                var missing = await store.TryMutateAsync(c.Id("never"), e => e.Name = "x", ct: c.Ct);
                Check.Equal(MutationStatus.NotFound, missing.Status, "the status for a missing row");
                Check.Null(missing.Entity, "the entity for a missing row");
                Check.Equal(1, await store.CountAsync(null, c.Ct), "the row count");
            });

        s.Add("Verbs.TryTransition_AppliesOnlyWhenThePreconditionHolds",
            "TryTransitionAsync applies the change and returns Updated when the precondition holds, returns PreconditionFailed with the row as it is when it does not, and NotFound for a missing row.",
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "open", 1), c.Ct);

                var done = await store.TryTransitionAsync(c.Id("r1"), e => e.Name == "open", e => e.Name = "closed", ct: c.Ct);
                Check.Equal(MutationStatus.Updated, done.Status, "the status of the first transition");
                Check.Equal("closed", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after the transition");

                var again = await store.TryTransitionAsync(c.Id("r1"), e => e.Name == "open", e => e.Name = "closed twice", ct: c.Ct);
                Check.Equal(MutationStatus.PreconditionFailed, again.Status, "the status of a transition whose precondition no longer holds");
                Check.Equal("closed", again.Entity?.Name, "Name of the row handed back with PreconditionFailed");
                Check.Equal("closed", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after the refused transition");

                var missing = await store.TryTransitionAsync(c.Id("never"), _ => true, e => e.Name = "x", ct: c.Ct);
                Check.Equal(MutationStatus.NotFound, missing.Status, "the status for a missing row");
            });
    }
}
