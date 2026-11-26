using static Unified.Data.Tables.Conformance.ConformanceLeniencies;

namespace Unified.Data.Tables.Conformance.Scenarios;

/// <summary>
/// Tier 2: which provider-neutral exception each condition raises, and what a caller sees around a
/// transient failure and a lost response (through <see cref="FaultInjectingStorage{T}"/>).
/// </summary>
internal static class FailureScenarios
{
    public static void Add(ScenarioBuilder s)
    {
        s.Tier(ConformanceTier.Failures);
        NeutralTypes(s);
        Rejections(s);
        Faults(s);
        VerbsUnderFailure(s);
    }

    private static void NeutralTypes(ScenarioBuilder s)
    {
        s.Add("Duplicate.Create_IsTheNeutralDuplicateKeyException_NamingTheRow",
            "A create of a stored id throws DuplicateKeyException itself (never a provider's exception type) with the row's stored id in Id.",
            FailureTypesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct);

                var single = await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.CreateAsync(Rows.Item(c.Pk.ToUpperInvariant() + "|R1"), c.Ct), "a create of a stored id");
                Check.Equal(c.Id("r1"), single.Id, "DuplicateKeyException.Id (the stored form of the id)");
                Check.NotEmpty(single.EntityType, "DuplicateKeyException.EntityType");

                var batch = await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.CreateBatchAsync([Rows.Item(c.Id("r0")), Rows.Item(c.Id("r1"))], c.Ct), "a create batch that names a stored id");
                Check.Equal(c.Id("r1"), batch.Id, "DuplicateKeyException.Id of a batch (the row that exists)");
            });

        s.Add("MissingRow.Update_IsTheNeutralEntityNotFoundException_NamingTheRow",
            "A whole-entity update and a builder update of a missing row throw EntityNotFoundException itself, with the row's stored id in Id.",
            FailureTypesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();

                var update = await Check.NeutralAsync<EntityNotFoundException>(
                    () => store.UpdateAsync(Rows.Item(c.Pk.ToUpperInvariant() + "|Never"), ConcurrencyMode.LastWriterWins, c.Ct),
                    "an update of a missing row");
                Check.Equal(c.Id("never"), update.Id, "EntityNotFoundException.Id of an update (the stored form of the id)");

                var merge = await Check.NeutralAsync<EntityNotFoundException>(
                    () => store.UpdateAsync(c.Pk.ToUpperInvariant() + "|Never", b => b.SetProperty(x => x.Name, "x"), c.Ct),
                    "a merge of a missing row");
                Check.Equal(c.Id("never"), merge.Id, "EntityNotFoundException.Id of a merge (the stored form of the id)");
            });

        s.Add("MissingRow.EveryOtherMember_TreatsItAsAbsent",
            "Only an update needs the row: every read of a missing row answers null, false, zero or empty, and a delete of it is a no-op; none of them throws.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var id = c.Id("never");

                Check.Null(await store.OneAsync(id, c.Ct), "OneAsync");
                Check.Equal(false, await store.ExistsAsync(id, c.Ct), "ExistsAsync");
                Check.Equal(0, (await store.QueryAsync(c.Pk, c.Ct)).Count(), "the row count of QueryAsync(partition)");
                Check.Equal(0, (await store.QueryAsync(new QueryOptions { Partition = c.Pk, RowKeyPrefix = "never" }, c.Ct)).Count, "the row count of QueryAsync(options)");
                Check.Equal(0, (await store.QueryAsync(x => x.Number == 0, c.Pk, null, c.Ct)).Count, "the row count of QueryAsync(predicate)");
                Check.Equal(0, (await Rows.ListAsync(store.QueryStreamAsync(new QueryOptions { Partition = c.Pk }, c.Ct), c.Ct)).Count, "the row count of a stream");
                Check.Equal(0, (await store.QueryPageAsync(new QueryOptions { Partition = c.Pk }, c.Ct)).Items.Count, "the row count of a page");
                Check.Equal(false, await store.AnyAsync(x => x.Number == 0, c.Pk, c.Ct), "AnyAsync");
                Check.Equal(0, await store.CountAsync(c.Pk, c.Ct), "CountAsync");
                await store.DeleteAsync(id, c.Ct);
                Check.Equal(0, await store.DeletePartitionAsync(c.Pk, c.Ct), "DeletePartitionAsync");
            });

        s.Add("Conflict.Update_IsTheNeutralConcurrencyConflictException_NamingTheRow",
            "A whole-entity update and a conditional builder update on a stale ETag throw ConcurrencyConflictException itself, with the row's id in Id.",
            [FailureTypesDiffer, ETagRulesDiffer],
            async c =>
            {
                var store = await c.ItemsAsync();
                var created = await store.CreateAsync(Rows.Item(c.Id("r1"), "first"), c.Ct);
                var staleETag = created.ETag!;
                await store.UpsertAsync(Rows.Item(c.Id("r1"), "second"), c.Ct);

                var stale = Rows.Item(c.Id("r1"), "third");
                stale.ETag = staleETag;
                var update = await Check.NeutralAsync<ConcurrencyConflictException>(() => store.UpdateAsync(stale, c.Ct), "an update on a stale ETag");
                Check.Equal(c.Id("r1"), update.Id, "ConcurrencyConflictException.Id of an update");

                await Check.NeutralAsync<ConcurrencyConflictException>(
                    () => store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Name, "third").WithETag(staleETag), c.Ct), "a merge on a stale ETag");
                Check.Equal("second", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after the refused writes");
            });

        s.Add("Failures.AreStorageExceptions_ThatCarryNoRowOfAnotherCondition",
            "The neutral failures are distinct conditions under one base: a duplicate is not a conflict, a missing row is neither, and each is a StorageException.",
            FailureTypesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct);

                var duplicate = await Check.ThrowsAsync<StorageException>(() => store.CreateAsync(Rows.Item(c.Id("r1")), c.Ct), "a create of a stored id");
                Check.True(duplicate is DuplicateKeyException, "a duplicate is a DuplicateKeyException");
                var missing = await Check.ThrowsAsync<StorageException>(
                    () => store.UpdateAsync(Rows.Item(c.Id("never")), ConcurrencyMode.LastWriterWins, c.Ct), "an update of a missing row");
                Check.True(missing is EntityNotFoundException && missing.GetType() != typeof(TableNotFoundException), "a missing row is an EntityNotFoundException, never a TableNotFoundException");
                Check.True(missing is not TransientStorageException, "a missing row is not a transient failure");
            });
    }

    private static void Rejections(ScenarioBuilder s)
    {
        s.Add("Rejected.CellAbove64KiB_IsAStorageRequestRejectedException",
            "A write whose binary cell is larger than the 64 KiB a table cell holds throws StorageRequestRejectedException (sending it again fails the same way) and stores nothing.",
            [OversizedCellAccepted, FailureTypesDiffer],
            async c =>
            {
                var store = await c.ItemsAsync();
                ConformanceItem Oversized(string rowKey)
                {
                    var item = Rows.Item(c.Id(rowKey), "too large");
                    item.Data = Rows.Bytes(70_000);
                    return item;
                }

                await Check.NeutralAsync<StorageRequestRejectedException>(() => store.CreateAsync(Oversized("r1"), c.Ct), "a create with a 70,000-byte cell");
                await Check.NeutralAsync<StorageRequestRejectedException>(() => store.UpsertAsync(Oversized("r2"), c.Ct), "an upsert with a 70,000-byte cell");
                await Check.NeutralAsync<StorageRequestRejectedException>(
                    () => store.CreateBatchAsync([Rows.Item(c.Id("r3")), Oversized("r4")], c.Ct), "a create batch with a 70,000-byte cell");
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after the refused writes");

                var fits = Rows.Item(c.Id("r5"), "fits");
                fits.Data = Rows.Bytes(60_000);
                await store.CreateAsync(fits, c.Ct);
                Check.Bytes(Rows.Bytes(60_000), (await store.OneAsync(c.Id("r5"), c.Ct))?.Data, "a 60,000-byte cell read back");
            });

        s.Add("Rejected.KeyCharacterTheServiceRefuses_IsAStorageRequestRejectedException",
            "A write whose partition key or row key holds '/', '\\', '#', '?' or a control character throws StorageRequestRejectedException and stores nothing, so a key that works locally works on Azure.",
            [KeyRulesNotEnforced, FailureTypesDiffer],
            async c =>
            {
                var store = await c.ItemsAsync();
                string[] rowKeys = ["a/b", "a\\b", "a#b", "a?b", "a\tb", "a\u0001b", "a\u007fb", "a\u009fb"];
                foreach (var rowKey in rowKeys)
                {
                    var shown = "U+" + ((int)rowKey[1]).ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
                    await Check.NeutralAsync<StorageRequestRejectedException>(
                        () => store.CreateAsync(Rows.Item(c.Id(rowKey)), c.Ct), $"a create whose row key holds {shown}");
                    await Check.NeutralAsync<StorageRequestRejectedException>(
                        () => store.CreateBatchAsync([Rows.Item(c.Id("ok")), Rows.Item(c.Id(rowKey))], c.Ct), $"a create batch whose second row key holds {shown}");
                    await Check.NeutralAsync<StorageRequestRejectedException>(
                        () => store.UpsertBatchAsync([Rows.Item(c.Id(rowKey))], c.Ct), $"an upsert batch whose row key holds {shown}");
                }
                string[] partitionKeys = [c.Pk + "/x", c.Pk + "#x", c.Pk + "\u0002x"];
                foreach (var partitionKey in partitionKeys)
                {
                    await Check.NeutralAsync<StorageRequestRejectedException>(
                        () => store.CreateAsync(Rows.Item(partitionKey + "|r"), c.Ct), "a create whose partition key holds a refused character");
                }
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after the refused writes");
            });

        s.Add("Rejected.KeyLongerThanTheServiceHolds_IsAStorageRequestRejectedException",
            "A write whose partition key or row key is longer than 1 KiB (here 1,025 characters) throws StorageRequestRejectedException and stores nothing, so a key that works locally works on Azure.",
            [KeyRulesNotEnforced, FailureTypesDiffer],
            async c =>
            {
                var store = await c.ItemsAsync();
                var longKey = new string('k', 1_025);
                await Check.NeutralAsync<StorageRequestRejectedException>(
                    () => store.CreateAsync(Rows.Item(c.Id(longKey)), c.Ct), "a create whose row key is 1,025 characters long");
                await Check.NeutralAsync<StorageRequestRejectedException>(
                    () => store.UpsertAsync(Rows.Item(c.Id(longKey)), c.Ct), "an upsert whose row key is 1,025 characters long");
                await Check.NeutralAsync<StorageRequestRejectedException>(
                    () => store.CreateAsync(Rows.Item(c.Pk + longKey + "|r"), c.Ct), "a create whose partition key is longer than 1,025 characters");
                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after the refused writes");

                var longest = new string('k', TableKeyRules.MaxKeyLength);
                await store.CreateAsync(Rows.Item(c.Id(longest)), c.Ct);
                Check.True(await store.ExistsAsync(c.Id(longest), c.Ct), $"a row whose row key is {TableKeyRules.MaxKeyLength} characters long is stored");
            });

        s.Add("Rejected.BatchThatNamesARowTwice",
            "A plain create batch that names a row twice throws DuplicateKeyException, a plain upsert batch that does throws StorageRequestRejectedException, and neither stores a row of that transaction.",
            [BatchTransactionsDiffer, FailureTypesDiffer],
            async c =>
            {
                var store = await c.ItemsAsync();

                await Check.NeutralAsync<DuplicateKeyException>(
                    () => store.CreateBatchAsync([Rows.Item(c.Id("r1"), "a"), Rows.Item(c.Id("r2"), "b"), Rows.Item(c.Id("r1"), "c")], c.Ct),
                    "a create batch that names a row twice");
                await Check.NeutralAsync<StorageRequestRejectedException>(
                    () => store.UpsertBatchAsync([Rows.Item(c.Id("r1"), "a"), Rows.Item(c.Id("r2"), "b"), Rows.Item(c.Id("r1"), "c")], c.Ct),
                    "an upsert batch that names a row twice");

                Check.Equal(0, await store.CountAsync(null, c.Ct), "the row count after the refused batches");
            });
    }

    private static void Faults(ScenarioBuilder s)
    {
        s.Add("Transient.RequestThatNeverArrived_WritesNothing_AndARetrySucceeds",
            "A write that fails before it reaches the store (TransientStorageException, MayHaveCommitted false) leaves storage as it was, and the same write sent again succeeds.",
            async c =>
            {
                var store = await c.ItemsAsync();
                var faulty = new FaultInjectingStorage<ConformanceItem>(store).FailNext(StorageMember.Create, FaultTiming.Before);

                var failure = await Check.ThrowsAsync<TransientStorageException>(
                    () => faulty.CreateAsync(Rows.Item(c.Id("r1"), "mine"), c.Ct), "a create that is failed before the store");
                Check.Equal(false, failure.MayHaveCommitted, "MayHaveCommitted of a request that never arrived");
                Check.Equal(false, await store.ExistsAsync(c.Id("r1"), c.Ct), "Exists of the row after the failed create");

                var created = await faulty.CreateAsync(Rows.Item(c.Id("r1"), "mine"), c.Ct);
                Check.NotEmpty(created.ETag, "the ETag of the retried create");
                Check.Equal("mine", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after the retry");
            });

        s.Add("LostResponse.Create_TheRetryMeetsItsOwnRowAsADuplicate",
            "A create whose response is lost has landed (TransientStorageException, MayHaveCommitted true): the retry throws DuplicateKeyException for the caller's own row, and a read tells what is stored.",
            FailureTypesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var faulty = new FaultInjectingStorage<ConformanceItem>(store).FailNext(StorageMember.Create, FaultTiming.After);

                var lost = await Check.ThrowsAsync<TransientStorageException>(
                    () => faulty.CreateAsync(Rows.Item(c.Id("r1"), "mine"), c.Ct), "a create whose response is lost");
                Check.Equal(true, lost.MayHaveCommitted, "MayHaveCommitted of a lost response");

                await Check.NeutralAsync<DuplicateKeyException>(
                    () => faulty.CreateAsync(Rows.Item(c.Id("r1"), "mine"), c.Ct), "the retry of a create that had landed");
                Check.Equal("mine", (await faulty.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name of the row the first attempt stored");
                Check.Equal(1, await store.CountAsync(null, c.Ct), "the row count");
            });

        s.Add("LostResponse.Update_TheRetryMeetsItsOwnWriteAsAConflict",
            "An update whose response is lost has landed: the retry on the ETag that was read throws ConcurrencyConflictException although no other writer touched the row.",
            [FailureTypesDiffer, ETagRulesDiffer],
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "first"), c.Ct);
                var faulty = new FaultInjectingStorage<ConformanceItem>(store).FailNext(StorageMember.Update, FaultTiming.After);
                var read = Check.NotNull(await faulty.OneAsync(c.Id("r1"), c.Ct), "the row");
                var readETag = read.ETag;

                read.Name = "mine";
                var lost = await Check.ThrowsAsync<TransientStorageException>(() => faulty.UpdateAsync(read, c.Ct), "an update whose response is lost");
                Check.Equal(true, lost.MayHaveCommitted, "MayHaveCommitted of a lost response");
                Check.Equal("mine", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after the update whose response was lost");

                // What the caller still holds: the entity as it was sent, on the ETag it was read with.
                var retry = Rows.Item(c.Id("r1"), "mine");
                retry.ETag = readETag;
                await Check.NeutralAsync<ConcurrencyConflictException>(() => faulty.UpdateAsync(retry, c.Ct), "the retry of an update that had landed");
                Check.Equal("mine", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name after the refused retry");
            });

        s.Add("LostResponse.DeleteAndUpsert_AreSafeToRepeat",
            "A delete and an upsert whose response is lost have landed, and sending them again converges on the same state without an exception.",
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "to delete"), c.Ct);
                var faulty = new FaultInjectingStorage<ConformanceItem>(store)
                    .FailNext(StorageMember.Delete, FaultTiming.After)
                    .FailNext(StorageMember.Upsert, FaultTiming.After);

                await Check.ThrowsAsync<TransientStorageException>(() => faulty.DeleteAsync(c.Id("r1"), c.Ct), "a delete whose response is lost");
                Check.Equal(false, await store.ExistsAsync(c.Id("r1"), c.Ct), "Exists after the delete whose response was lost");
                await faulty.DeleteAsync(c.Id("r1"), c.Ct);

                await Check.ThrowsAsync<TransientStorageException>(
                    () => faulty.UpsertAsync(Rows.Item(c.Id("r2"), "mine"), c.Ct), "an upsert whose response is lost");
                await faulty.UpsertAsync(Rows.Item(c.Id("r2"), "mine"), c.Ct);
                Check.Sequence([c.Id("r2")], Rows.Ids(await store.QueryAsync(new QueryOptions { Partition = c.Pk }, c.Ct)), "the ids after the repeated writes");
            });
    }

    private static void VerbsUnderFailure(ScenarioBuilder s)
    {
        s.Add("Verbs.AConflict_IsRetriedFromAFreshRead",
            "MutateAsync and TryMutateAsync answer a ConcurrencyConflictException by reading the row again and applying the change to what they read; the result is the change on top of the other writer's.",
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "name", 1), c.Ct);
                var racing = new WriteBeforeNextUpdate(store, () => store.UpdateAsync(c.Id("r1"), b => b.SetProperty(x => x.Note, "other writer"), c.Ct));
                var counted = new CountingStorage<ConformanceItem>(racing);

                var mutated = await counted.MutateAsync(c.Id("r1"), e => e.Number += 10, ct: c.Ct);

                Check.Equal(11, mutated.Number, "Number after the mutation");
                Check.Equal(2, counted.Count(StorageMember.Update), "the update attempts (one lost to the other writer, one that won)");
                Check.Equal(2, counted.Count(StorageMember.One), "the reads (one per attempt)");
                var read = Check.NotNull(await store.OneAsync(c.Id("r1"), c.Ct), "the row");
                Check.Equal("other writer", read.Note, "the other writer's column after the mutation");
                Check.Equal(11, read.Number, "Number read back");

                racing.Arm();
                var result = await counted.TryMutateAsync(c.Id("r1"), e => e.Number += 10, ct: c.Ct);
                Check.Equal(MutationStatus.Updated, result.Status, "the TryMutateAsync status after one lost race");
                Check.Equal(21, (await store.OneAsync(c.Id("r1"), c.Ct))?.Number, "Number after the second mutation");
            });

        s.Add("Verbs.AConflictOnEveryAttempt_EndsAsConflicted",
            "When every attempt loses its race, TryMutateAsync and TryTransitionAsync return Conflicted and MutateAsync throws ConcurrencyConflictException, after exactly maxAttempts updates.",
            async c =>
            {
                var store = await c.ItemsAsync();
                await store.CreateAsync(Rows.Item(c.Id("r1"), "name", 1), c.Ct);
                Exception Conflict() => new ConcurrencyConflictException(nameof(ConformanceItem), c.Id("r1"), null);
                var faulty = new FaultInjectingStorage<ConformanceItem>(store);
                var counted = new CountingStorage<ConformanceItem>(faulty);

                faulty.FailNext(StorageMember.Update, FaultTiming.Before, 3, Conflict);
                Check.Equal(MutationStatus.Conflicted, (await counted.TryMutateAsync(c.Id("r1"), e => e.Number++, 3, c.Ct)).Status, "the TryMutateAsync status");
                Check.Equal(3, counted.Count(StorageMember.Update), "the update attempts of TryMutateAsync");

                faulty.FailNext(StorageMember.Update, FaultTiming.Before, 2, Conflict);
                Check.Equal(MutationStatus.Conflicted,
                    (await counted.TryTransitionAsync(c.Id("r1"), _ => true, e => e.Number++, 2, c.Ct)).Status, "the TryTransitionAsync status");

                faulty.FailNext(StorageMember.Update, FaultTiming.Before, 3, Conflict);
                await Check.ThrowsAsync<ConcurrencyConflictException>(() => counted.MutateAsync(c.Id("r1"), e => e.Number++, 3, c.Ct), "MutateAsync after three lost races");
                Check.Equal(1, (await store.OneAsync(c.Id("r1"), c.Ct))?.Number, "Number after the lost races");
            });

        s.Add("Verbs.RowDeletedBetweenReadAndWrite_IsAnOutcome",
            "When the row is deleted after a verb read it and before it wrote: TryMutateAsync and TryTransitionAsync return NotFound, MutateAsync throws InvalidOperationException, and MutateOrCreateAsync creates the row again. No storage exception escapes.",
            FailureTypesDiffer,
            async c =>
            {
                var store = await c.ItemsAsync();
                var vanishing = new WriteBeforeNextUpdate(store, () => store.DeleteAsync(c.Id("r1"), c.Ct));

                await store.CreateAsync(Rows.Item(c.Id("r1"), "name", 1), c.Ct);
                Check.Equal(MutationStatus.NotFound, (await vanishing.TryMutateAsync(c.Id("r1"), e => e.Number++, ct: c.Ct)).Status, "the TryMutateAsync status");

                await store.CreateAsync(Rows.Item(c.Id("r1"), "name", 1), c.Ct);
                vanishing.Arm();
                Check.Equal(MutationStatus.NotFound,
                    (await vanishing.TryTransitionAsync(c.Id("r1"), _ => true, e => e.Number++, ct: c.Ct)).Status, "the TryTransitionAsync status");

                await store.CreateAsync(Rows.Item(c.Id("r1"), "name", 1), c.Ct);
                vanishing.Arm();
                await Check.ThrowsAsync<InvalidOperationException>(() => vanishing.MutateAsync(c.Id("r1"), e => e.Number++, ct: c.Ct), "MutateAsync of a row deleted under it");
                Check.Equal(false, await store.ExistsAsync(c.Id("r1"), c.Ct), "Exists after the three verbs (none of them creates)");

                await store.CreateAsync(Rows.Item(c.Id("r1"), "name", 1), c.Ct);
                vanishing.Arm();
                var recreated = await vanishing.MutateOrCreateAsync(c.Id("r1"), () => Rows.Item("ignored", "created again", 100), e => e.Number++, ct: c.Ct);
                Check.Equal(101, recreated.Number, "Number of the row MutateOrCreateAsync created again");
                Check.Equal("created again", (await store.OneAsync(c.Id("r1"), c.Ct))?.Name, "Name read back");
            });
    }

    /// <summary>
    /// Runs another writer's write once, right before the next whole-entity update reaches the store: the
    /// race a read-modify-write loses, without threads.
    /// </summary>
    private sealed class WriteBeforeNextUpdate(IStorage<ConformanceItem> inner, Func<Task> otherWriter) : InterceptingStorage<ConformanceItem>(inner)
    {
        private int armed = 1;

        public void Arm() => Interlocked.Exchange(ref armed, 1);

        protected override async ValueTask OnCallingAsync(StorageMember member, CancellationToken ct)
        {
            if (member == StorageMember.Update && Interlocked.Exchange(ref armed, 0) == 1)
                await otherWriter().ConfigureAwait(false);
        }
    }
}
