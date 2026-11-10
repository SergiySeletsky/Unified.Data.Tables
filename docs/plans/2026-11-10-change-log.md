# Change Log and Raw Snapshot Access Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An opt-in, fail-closed, append-only change log in the same storage account. Every library mutation of a
table row writes one log row **before** the operation. The release also adds a public log reader, a raw
(Edm-preserving) table snapshot reader and an NDJSON codec for raw rows, so a host can build a delete-aware
incremental table backup. Ships as 0.10.0.

**Architecture:** One internal writer, `TableChangeLog`, implements `IChangeLogWriter` and `IChangeLogReader` over
one table whose partition keys are hour buckets. `TableStorage<T>`, `PolymorphicTableStorage<TBase>` and their
InMemory counterparts call it once per logical entity, immediately before their first table mutation. A thrown
log write aborts the call. Raw access is separate and read-only:
- `ITableSnapshotReader` lists tables, streams physical rows and point-reads one logical entity (its primary row
  plus its 0.9.0 continuation rows) as untyped `TableEntity`, never through `T`.
- `TableEntityNdjson` writes and reads those rows losslessly.

`InMemoryTableAccount` gives the InMemory package a shared log and table registry for parity. With
`ChangeLog.Enabled == false` (the default), no new code path runs.

**Tech Stack:** C# (`LangVersion=latest`), `net10.0` (Abstractions also `netstandard2.0`), Azure.Data.Tables
12.11.0, Azure.Storage.Blobs (from 0.9.0), System.Text.Json (in the shared framework), xUnit v3 with plain `Assert`,
NSubstitute, Azurite (tables 10002, blobs 10000).

**Spec:** `D:\github\intellispec\docs\reports\2026-11-10-storage-backup-research.md` §2.3 items 1 and 4, §2.7.4 and
§4 items 9 and 10, plus the approved canon (user decisions 2026-11-10) quoted in Global Constraints. This plan builds
on `docs/plans/2026-11-10-lossless-value-split.md` (0.9.0). Its names (`SplitRowLayout`, `_SplitOf`, `_SplitParts`,
`{Prop}__blob`, `WriteLargeAsync`, `DeleteLargeAsync`, `ReadPriorAsync`, `InMemoryOffloadBlobStore`) are
used here as that plan defines them.

**Branch / worktree:**
- Repo `D:\github\Unified.Data.Tables`, branch `feat/change-log`. Branch from `main` once 0.9.0 has merged;
  until then, branch from `feat/lossless-value-split` and rebase after it merges.
- If a worktree is used, put it at `D:\github\Unified.Data.Tables-change-log`.
- Merging, tagging and running `publish.yml` are the user's actions.

## Global Constraints

- **Package scope.** Nothing IntelliSpec-specific goes in code, tests, README or CHANGELOG: no vision ids, no
  IntelliSpec table names, no backup container names.
- **No behaviour change unless `ChangeLog.Enabled`.**
  - With the default options, every store issues exactly the same SDK calls as 0.9.0.
  - It does not create a log table or call `GetTableClient` for one.
  - No new constructor or DI registration changes which services resolve, except the additions listed in Task 4
    and Task 7.
- **Options.** `UnifiedTableStorageOptions.ChangeLog : ChangeLogOptions` defaults to `new()`, where
  `ChangeLogOptions { bool Enabled = false; string TableName = "UnifiedChangeLog"; }`.
  - See Canon issue 1: `_UnifiedChangeLog` is not a valid Azure table name.
  - `TableName` must match `^[A-Za-z][A-Za-z0-9]{2,62}$`. It is checked when `Enabled`, at registration and at
    store construction, and fails with `ArgumentException`.
  - A `TableStorage<T>` or polymorphic store whose own table name equals `TableName` throws
    `InvalidOperationException` at construction. The log is never logged.
- **Log row shape.**
  - PK = `{yyyyMMddHH}` of `At` in UTC, invariant culture.
  - RK = `{table}|{pk}|{rk}|{At.UtcTicks:D19}|{guid:N}`. When that string would exceed 512 chars, RK =
    `{table}|h{sha256hex(pk + "\u0000" + rk)}|{ticks:D19}|{guid:N}`.
  - Properties: `Table`, `EntityPartitionKey`, `EntityRowKey` (strings, always the full stored keys), `Op` (`"Upsert"`
    or `"Delete"`) and `At` (DateTimeOffset, UTC).
  - Readers take keys from these properties, never by parsing the RK. A PK or RK can contain `|`.
- **Op mapping.** `Upsert` covers insert, upsert, replace, merge and transition. `Delete` covers delete and each row
  of a partition delete.
- **One logical entity = one log row**, keyed by its logical (primary) RowKey. Continuation rows `{rk}~0001..`,
  stale-part deletes inside a 0.9.0 transaction, blob uploads and blob deletes write no log row.
- **Ordering.** The log row is written after argument validation, id normalization, protected-property checks and
  (0.9.0) blob uploads, and **before the first table mutation** of the call.
  - It is written once per call. Upsert retry loops do not log again.
  - If the log write throws, the exception propagates unwrapped and no table mutation is attempted.
  - A failed mutation leaves its log row behind (a "phantom" entry). This is by design: readers must point-read
    current state.
- **Batching.**
  - `DeletePartitionAsync`: one log row per logical key. The log rows for each chunk are written as their own
    transaction(s) immediately before that chunk's delete transaction.
  - `CreateBatchAsync` and `UpsertBatchAsync` (unsplit types): the same per chunk.
  - Polymorphic `InsertBatchAsync`: the same per chunk.
  - Every log row of one `RecordAsync` call shares one `At`, so one bucket, so one partition. Log transactions are
    chunked with `BatchPlanner.Plan`.
- **Public names (canon).**
  - Change-log types: `enum ChangeOp { Upsert, Delete }`;
    `record ChangeLogEntry(string Table, string PartitionKey, string RowKey, ChangeOp Op, DateTimeOffset At)`;
    `IChangeLogReader { IAsyncEnumerable<ChangeLogEntry> ReadAsync(DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct); Task TrimBucketsBeforeAsync(DateTimeOffset before, CancellationToken ct); }`.
  - Raw access: `ITableSnapshotReader`, `TableEntityNdjson.Write` and `TableEntityNdjson.Read`.
  - Additions to the canon (Canon issues 2 and 3): `IChangeLogWriter`, `ITableSnapshotReader.GetLogicalAsync`,
    `SnapshotScope`.
- **Version.** `0.10.0` in `Directory.Build.props:35`. No package reference changes.
- **Build rules.** `TreatWarningsAsErrors` and analyzers `Recommended` apply. Every public member needs XML docs.
- **Test layout.**
  - New tests live in `tests/Unified.Data.Tables.Tests/ChangeLog/`, namespace `Unified.Data.Tables.Tests.ChangeLog`.
  - Tests that call `AddUnifiedTableStorage` or `AddUnifiedInMemoryStorage` join `[Collection("OversizedCellPolicy")]`.
  - Build: `dotnet build Unified.Data.Tables.slnx -c Release`.
  - Focused test: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj -c Release --filter "FullyQualifiedName~<Class>"`.
    Never pass `-o`.

## Review Focus

1. **A partition key or row key containing `|`, or keys long enough to overflow the log RK.** Polymorphic `TableKey`
   allows `|` in the PK, and `IStorage<T>` row keys may contain `|`. Expected: the entry's `PartitionKey` and `RowKey`
   are exactly the stored keys, and a 1,000-char RK still logs (hashed RK form). Tests in Task 1 and Task 2.
2. **A per-table `configure` on `AddUnifiedPolymorphicTable` silently turning logging off.** A host that gives one
   polymorphic table its own discriminator options would otherwise lose change capture for that table without any
   error. Expected: the change log is process-wide; the store uses the registered options' `ChangeLog` whatever
   `configure` sets. Test in Task 4.
3. **A logical entity read while it is being replaced** (0.9.0 split rows: the primary is read, then a concurrent
   transaction replaces primary and parts, then the parts are read). Expected: `GetLogicalAsync` never returns a
   primary with another generation's parts. It re-checks the primary's ETag and retries, up to 3 attempts, then
   throws `InvalidOperationException`. Test in Task 6.
4. **Edm values the SDK or JSON could silently change.** Examples: an `Int64` above 2^53, a `Double` that is
   integral (`1.0`), `NaN`, `-0.0` or `±Infinity`, a `DateTime` with 7 fractional digits or a non-zero offset,
   `Guid.Empty`, an empty `byte[]`, and strings with `\n`, `"` and supplementary characters. Expected: the
   type and value round-trip exactly, with DateTime normalized to UTC. Tests in Task 5, and on Azurite in Task 9
   (including what CLR type the SDK hands back for an integral `Edm.Double`).
5. **A log write failure under a successful-looking API.** For example, the log table cannot be created, or
   its transaction returns 403 or 500. Expected: the public call throws the SDK's exception and the entity table
   receives no call. Tests in Task 3 (every mutator) and Task 7 (InMemory).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Unified.Data.Tables.Abstractions/ChangeOp.cs`, `ChangeLogEntry.cs`, `IChangeLogReader.cs`, `IChangeLogWriter.cs` (create) | Public log contract; no Azure types (`netstandard2.0`-safe) |
| `src/Unified.Data.Tables.Abstractions/ChangeLogKeys.cs` (create, internal) | Bucket and RowKey composition, shared by both providers |
| `src/Unified.Data.Tables/ChangeLogOptions.cs` (create), `UnifiedTableStorageOptions.cs` (modify) | Configuration and table-name validation |
| `src/Unified.Data.Tables/TableChangeLog.cs` (create, internal) | Azure writer, reader and trim over the log table |
| `src/Unified.Data.Tables/TableStorage.cs`, `TableStorage.LargeValues.cs` (modify) | Log before every mutation |
| `src/Unified.Data.Tables/PolymorphicTableStorage.cs` (modify) | Log before every mutation |
| `src/Unified.Data.Tables/ServiceCollectionExtensions.cs` (modify) | Register log reader/writer and the snapshot reader |
| `src/Unified.Data.Tables/TableEntityNdjson.cs` (create) | Edm-preserving raw row ↔ JSON object / NDJSON line |
| `src/Unified.Data.Tables/ITableSnapshotReader.cs`, `SnapshotScope.cs`, `TableSnapshotReader.cs` (create) | Raw table enumeration and reads |
| `src/Unified.Data.Tables.InMemory/InMemoryTableAccount.cs`, `InMemoryChangeLog.cs`, `InMemorySnapshotReader.cs` (create); `InMemoryStorage.cs`, `InMemoryPolymorphicStorage.cs`, `InMemoryServiceCollectionExtensions.cs` (modify) | Parity |
| `tests/Unified.Data.Tables.Tests/ChangeLog/*.cs` (create) | All new tests |
| `README.md`, `CHANGELOG.md`, `Directory.Build.props` (modify) | Docs and 0.10.0 |

---

### Task 1: Public contract, options and key composition

**Files:**
- Create the Abstractions files listed in File Structure.
- Create: `src/Unified.Data.Tables/ChangeLogOptions.cs`.
- Modify: `src/Unified.Data.Tables/UnifiedTableStorageOptions.cs`. The new property goes after `TableNameResolver`.
- Test: `tests/Unified.Data.Tables.Tests/ChangeLog/ChangeLogKeysTests.cs`.

**Interfaces:**
- Produces:
  - Public enum, record and reader interface exactly as in Global Constraints. All are in namespace
    `Unified.Data.Tables`, in Abstractions. The default parameter is `CancellationToken ct = default`.
  - `public interface IChangeLogWriter { Task RecordAsync(string table, IReadOnlyCollection<TableKey> keys, ChangeOp op, CancellationToken ct = default); }`
    - It writes one row per key, all with one `At`.
    - An empty `keys` collection is a no-op with no I/O.
    - A blank `table` throws `ArgumentException`.
  - `public sealed class ChangeLogOptions` with members `bool Enabled`, `string TableName = "UnifiedChangeLog"` and
    `internal TimeProvider TimeProvider = TimeProvider.System` (tests set it through `InternalsVisibleTo`).
  - `internal void ChangeLogOptions.Validate()`. It throws `ArgumentException` naming `TableName` when `Enabled` and
    the name fails the regex.
  - `public ChangeLogOptions UnifiedTableStorageOptions.ChangeLog { get; set; } = new();`
  - `internal static class ChangeLogKeys` (Abstractions; already visible to both implementation assemblies):
    - `const int MaxInlineRowKeyLength = 512;`
    - `static string Bucket(DateTimeOffset at)` returns `at.UtcDateTime.ToString("yyyyMMddHH", CultureInfo.InvariantCulture)`.
    - `static string RowKey(string table, string pk, string rk, DateTimeOffset at, Guid id)` builds the inline or
      hashed form from Global Constraints.
    - `static IEnumerable<string> BucketsCovering(DateTimeOffset fromInclusive, DateTimeOffset toExclusive)` returns
      buckets in ascending order. It is empty when `to <= from`.
    - Column-name constants `Table`, `EntityPartitionKey`, `EntityRowKey`, `Op` and `At`.

- [ ] **Step 1: Write the failing tests** in `ChangeLogKeysTests`:
  - `Bucket_IsUtcHour_InvariantCulture`: `2026-11-10T23:30:00+02:00` gives `"2026111021"`.
  - `RowKey_InlineForm`: `("Orders","p","a|b", at, guid)` gives `"Orders|p|a|b|" + at.UtcTicks.ToString("D19") + "|" + guid.ToString("N")`.
  - `RowKey_OverLimit_UsesHashedForm` (Review Focus 1):
    - Use a 1,000-char `rk`. The result is at most 512 chars, contains `"|h"` followed by 64 lower-hex characters,
      and starts with `"Orders|"`.
    - Two different `(pk, rk)` pairs with the same concatenation (`("a|","b")` vs `("a","|b")`) hash differently.
  - `RowKey_SortsByTableThenKeys`.
  - `BucketsCovering_SpansHoursInclusiveExclusive`: `10:59` to `12:00` gives `["…10", "…11"]`, and `to <= from`
    gives an empty result.
  - `Options_DefaultDisabled_DefaultName`: `new UnifiedTableStorageOptions().ChangeLog` has `Enabled == false` and
    `TableName == "UnifiedChangeLog"`.
  - `Validate_RejectsInvalidNames_WhenEnabled`:
    - `"_UnifiedChangeLog"`, `"1Log"`, `"ab"` and a 64-char name each throw `ArgumentException`.
    - The same names do not throw when `Enabled == false`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ChangeLogKeysTests"`. Expected: FAIL (types not defined).
- [ ] **Step 3: Implement.** Hash with `SHA256.HashData` over UTF-8 and convert to lower hex. On `netstandard2.0`,
  use `SHA256.Create()`, guarded with `#if NET`.
- [ ] **Step 4: Run** the filter, then the whole project. Expected: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(changelog): public change-log contract, options and key composition"`

---

### Task 2: `TableChangeLog`, the Azure writer, reader and trim

**Files:**
- Create: `src/Unified.Data.Tables/TableChangeLog.cs`.
- Test: `tests/Unified.Data.Tables.Tests/ChangeLog/TableChangeLogTests.cs`. Use NSubstitute `TableServiceClient`
  and `TableClient`, as `StorageHarness` does (`tests/Unified.Data.Tables.Tests/TestSupport/StorageHarness.cs:73-86`).

**Interfaces:**
- Consumes: Task 1, `TableInitializer` (`src/Unified.Data.Tables/TableInitializer.cs`), `BatchPlanner.Plan` and
  `TableRowSize.Estimate`.
- Produces: `internal sealed class TableChangeLog : IChangeLogWriter, IChangeLogReader`, with constructor
  `TableChangeLog(TableServiceClient service, ChangeLogOptions options)`.
  - The constructor calls `options.Validate()`, and does no I/O.
  - The log table is created lazily through its own `TableInitializer`.
  - `RecordAsync`:
    1. Take one `At = options.TimeProvider.GetUtcNow()`.
    2. Build the rows with PK `Bucket(At)`, RK from `ChangeLogKeys.RowKey` with `Guid.NewGuid()`, and the five
       properties.
    3. A single row goes through `AddEntityAsync`. Otherwise call `SubmitTransactionAsync` per `BatchPlanner.Plan`
       range, with action type `Add`.
  - `ReadAsync`:
    1. Make one paged query with filter `PartitionKey ge '{Bucket(from)}' and PartitionKey le '{Bucket(to - 1 tick)}'
       and At ge datetime'{from:o}' and At lt datetime'{to:o}'`. Build the filter with
       `TableClient.CreateQueryFilter` and select only the five properties.
    2. Map each row to `ChangeLogEntry`.
    3. A missing log table (404) yields nothing.
    4. Order: buckets ascending; no order within a bucket (documented).
  - `TrimBucketsBeforeAsync(before)`: query `PartitionKey lt '{Bucket(before)}'`, keys only. Group rows by PK and
    delete them in `BatchPlanner` transactions with `ETag.All`, treating a 404 as done. Returns once every listed row
    is deleted. Idempotent.

- [ ] **Step 1: Write the failing tests:**
  - `Record_OneKey_SingleAdd_NoTransaction`.
  - `Record_ManyKeys_OneBucket_ChunkedTransactions`:
    - Record 250 keys. Expect 3 `SubmitTransactionAsync` calls (100, 100, 50).
    - Every action is `Add` with the same PK.
    - The RKs are unique.
  - `Record_EmptyKeys_NoIo`: no `CreateIfNotExistsAsync` call and no write.
  - `Record_LongKeys_StoresFullKeysInProperties` (Review Focus 1): PK `"a|b"` and a 1,000-char RK. The properties
    hold both keys exactly, and the RK is at most 512 chars.
  - `Record_FixedClock_UsesBucketAndTicks`: use a `FakeTimeProvider`-style test double deriving from `TimeProvider`.
  - `Record_TransactionFails_Throws_Unwrapped`: `TableTransactionFailedException` propagates as is.
  - `Read_FilterAndMapping`: the captured filter equals the expected string, and the rows map to entries with
    `Op` parsed.
  - `Read_MissingTable_YieldsNothing`.
  - `Trim_DeletesOnlyOlderBuckets_ByPartition`:
    - Rows exist in buckets `…09`, `…10` and `…11`, and `before` is `11:15`.
    - Rows of `…09` and `…10` are deleted, one transaction per bucket.
    - The captured filter is `PartitionKey lt '…11'`.
  - `Ctor_InvalidTableName_Throws_WithoutIo`.
  - `Record_TableWithNoStore_LogsLikeAnyOther` (Canon issue 3): `RecordAsync("RawJournal", [new("p", "r")], Upsert)`
    with no `TableStorage<T>` for `RawJournal` writes one row whose `Table == "RawJournal"`; the writer never checks
    that a store exists.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TableChangeLogTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(changelog): TableChangeLog writes, reads and trims hour-bucketed log rows"`

---

### Task 3: `TableStorage<T>` logs before every mutation

**Files:**
- Modify: `src/Unified.Data.Tables/TableStorage.cs`:
  - `CreateAsync` `:111-158`
  - `DeleteAsync` `:161-176`
  - `DeletePartitionAsync` `:179-215`
  - `WriteBatchAsync` `:542-622`
  - `UpdateAsync(id, builder)` `:661-724`
  - `UpdateAsync(T, mode)` `:766-835`
  - `UpsertAsync` `:837-876`
  - the constructor `:74-87`
  - Line numbers are as of 0.8.3. Re-locate them after the 0.9.0 rebase.
- Modify: `src/Unified.Data.Tables/TableStorage.LargeValues.cs` (0.9.0): `WriteLargeAsync`, `DeleteLargeAsync`, and
  the per-entity batch path.
- Test: `tests/Unified.Data.Tables.Tests/ChangeLog/TableStorageChangeLogTests.cs`.

**Interfaces:**
- Consumes: `TableChangeLog` (Task 2).
- Produces:
  - `private readonly IChangeLogWriter? changeLog;` is non-null only when `opts.ChangeLog.Enabled`.
  - `private readonly string tableName;` holds the resolved table name (`ResolveTableName(opts)`).
  - `private Task LogAsync(IReadOnlyCollection<TableKey> keys, ChangeOp op, CancellationToken ct)` returns
    `Task.CompletedTask` when `changeLog` is null.
  - Constructor: when enabled, it builds `new TableChangeLog(serviceClient, opts.ChangeLog)`. When `tableName`
    equals `opts.ChangeLog.TableName` (ordinal, ignore case), it throws `InvalidOperationException`.

**Call-site rules:**
- **Create / Upsert / Update(T) / Update(id, builder).** Call `LogAsync([new(pk, rk)], ChangeOp.Upsert)`
  immediately before `AddEntityAsync`, `UpsertEntityAsync` or `UpdateEntityAsync`. The call sits outside those
  `try` blocks, so a log 409 is never mapped to `DuplicateKeyException`.
- **Delete.** Call `LogAsync(..., Delete)` before `DeleteEntityAsync`.
- **DeletePartitionAsync.**
  - Per chunk, log that chunk's keys before its delete transaction.
  - For split-aware `T`, skip rows carrying `_SplitOf`, and add `_SplitOf` to the select, as 0.9.0 Task 4 rule 9 does.
  - No rows means no log.
- **WriteBatchAsync** (unsplit `T`): per `BatchPlanner` range, log that range's keys with `Upsert` before its
  `SubmitTransactionAsync`.
- **0.9.0 split-aware `WriteLargeAsync`:** log once after `UploadAsync` and before the single SDK call or
  transaction. Retries do not log again.
- **0.9.0 split-aware `DeleteLargeAsync`:** log before the first table delete. A missing prior still logs once,
  because every call logs.
- **Unchanged:** `EnforceProtectedPropertiesAsync` still runs before logging, and caching is untouched.

- [ ] **Step 1: Write the failing tests.** Build the harness with `options.ChangeLog.Enabled = true` and mock
  `Service.GetTableClient("UnifiedChangeLog")` to a second `TableClient`. Both mocks append to one shared ordered
  `List<string>` through `When(...).Do(...)`.
  - `[Theory]` `EveryMutator_LogsBeforeEntityCall`. Run it over Create, Upsert, Update(T, Strict),
    Update(T, LastWriterWins), Update(id, builder) and Delete.
    - The call log is `["log:add", "table:<verb>"]`.
    - The log row's `Table == "TestEntity"`, its keys equal the normalized id, and its `Op` is as mapped.
  - `[Theory]` `EveryMutator_LogFailure_AbortsWithoutEntityCall` (Review Focus 5). Run it over the same
    cases, plus `CreateBatchAsync` and `DeletePartitionAsync`. The log mock throws `RequestFailedException(500)`.
    The same exception propagates, and the entity `TableClient` receives no mutating call.
  - `Create_LogSucceeds_EntityConflicts_StillDuplicateKey_PhantomEntryRemains`.
  - `DeletePartition_250Rows_LogsPerChunkBeforeEachDelete`: the order is `log-tx(100)`, `del-tx(100)`, `log-tx(100)`,
    `del-tx(100)`, `log-tx(50)`, `del-tx(50)`.
  - `DeletePartition_SplitAware_LogsLogicalRowsOnly`: rows `a`, `a~0001` and `b` produce log keys `a` and `b`.
  - `CreateBatch_LogsPerTransactionRange`.
  - `SplitAware_Create_LogsAfterBlobUpload_BeforeTransaction`:
    - Use 0.9.0's `RecordingBlobStore` and `SplitTextEntity`.
    - The order is `blob:get`, `blob:upload`, `log:add`, `table:tx`.
    - The transaction has 2 actions, and the log has 1 row keyed `rk`.
  - `SplitAware_UpsertRetry_LogsOnce`.
  - `Disabled_NoLogClient_SameCallsAs090`: the default options produce no `GetTableClient("UnifiedChangeLog")` call
    and the same mutating calls as today, for every mutator.
  - `TableNameResolver_LogsResolvedName`.
  - `StoreOverLogTable_Throws`: `TableNameResolver` returns `"UnifiedChangeLog"`, which gives
    `InvalidOperationException`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TableStorageChangeLogTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter, then the whole project. Expected: PASS. In particular, `TableStorageTests`,
  `ConcurrencyTests`, `OutcomeVerbsTests` and the 0.9.0 `LargeValues` suites are unchanged.
- [ ] **Step 5: Commit**: `git commit -m "feat(changelog): TableStorage writes a change-log row before every mutation, fail-closed"`

---

### Task 4: Polymorphic store and DI registration

**Files:**
- Modify: `src/Unified.Data.Tables/PolymorphicTableStorage.cs`:
  - the constructor `:47-61`
  - `InsertAsync` `:67`
  - `UpsertAsync` `:89`
  - `InsertBatchAsync` `:101-159`
  - `MergeColumnsAsync` `:162-187`
  - `DeleteAsync` `:239-253`
  - `DeletePartitionAsync` `:256-285`
- Modify: `src/Unified.Data.Tables/ServiceCollectionExtensions.cs`:
  - `AddUnifiedTableStorage` `:20-38`
  - `AddUnifiedPolymorphicTable` `:111-136`
- Test: `tests/Unified.Data.Tables.Tests/ChangeLog/PolymorphicChangeLogTests.cs` and `ChangeLogRegistrationTests.cs`
  (`[Collection("OversizedCellPolicy")]`).

**Interfaces:**
- Consumes: Task 2 and Task 3's rules.
- Produces:
  - `internal PolymorphicTableStorage(TableServiceClient, string tableName, ILogger<…>, UnifiedTableStorageOptions? options, ChangeLogOptions? changeLog)`.
    - The public constructor forwards `options?.ChangeLog`.
    - Logging uses the explicit `tableName`.
    - Calls log as follows:
      - `InsertAsync`, `UpsertAsync` and `MergeColumnsAsync` log `Upsert`.
      - `InsertBatchAsync` logs `Upsert` per range, before each transaction.
      - `DeleteAsync` logs `Delete` before the delete. A 404 no-op still logs.
      - `DeletePartitionAsync` logs `Delete` per range, before each transaction.
  - In `AddUnifiedTableStorage`:
    - Call `options.ChangeLog.Validate()` at registration.
    - When `Enabled`, `TryAddSingleton<TableChangeLog>(sp => new(sp.GetRequiredService<TableServiceClient>(), options.ChangeLog))`.
      Also `TryAddSingleton<IChangeLogReader>(sp => sp.GetRequiredService<TableChangeLog>())` and the same for
      `IChangeLogWriter`.
    - Always `TryAddSingleton<ITableSnapshotReader>(sp => new TableSnapshotReader(sp.GetRequiredService<TableServiceClient>()))`.
      This registration lands in Task 6; add the line there.
  - `AddUnifiedPolymorphicTable`: the factory passes
    `changeLog: sp.GetService<UnifiedTableStorageOptions>()?.ChangeLog ?? local?.ChangeLog`. The registered,
    process-wide setting wins over a per-table `configure` (Review Focus 2).

- [ ] **Step 1: Write the failing tests:**
  - `PolymorphicChangeLogTests` (mocked clients, ordered call log as in Task 3):
    - `[Theory]` `EveryPolymorphicMutator_LogsBeforeEntityCall`.
    - `[Theory]` `EveryPolymorphicMutator_LogFailure_Aborts`.
    - `Key_WithPipeInPartition_LogsExactKeys` (Review Focus 1): `TableKey("a|b", "c")`.
    - `Delete_Missing_StillLogsOnce`.
    - `Disabled_NoLogCalls`.
  - `ChangeLogRegistrationTests`:
    - `Enabled_RegistersReaderAndWriter_AsOneInstance`.
    - `Disabled_RegistersNoReaderOrWriter`.
    - `InvalidTableName_ThrowsAtRegistration`.
    - `PerTableConfigure_DoesNotDisableLogging` (Review Focus 2): with the global options enabled and
      `configure: o => o.TypeDiscriminator = map`, an insert logs.
    - `PlainRegistration_ResolvesStoresAsBefore`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~PolymorphicChangeLogTests|FullyQualifiedName~ChangeLogRegistrationTests"`.
  Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter, then the whole project (including `PolymorphicServiceCollectionTests` and
  `ServiceCollectionExtensionsTests`). Expected: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(changelog): polymorphic store logging and DI registration of the change log"`

---

### Task 5: `TableEntityNdjson`, the Edm-preserving raw codec

**Files:**
- Create: `src/Unified.Data.Tables/TableEntityNdjson.cs`.
- Test: `tests/Unified.Data.Tables.Tests/ChangeLog/TableEntityNdjsonTests.cs`.

**Interfaces:**
- Produces `public static class TableEntityNdjson` with these members:
  - `void WriteObject(Utf8JsonWriter writer, TableEntity entity)` writes one JSON object for embedding in a host's
    envelope (no newline).
  - `TableEntity ReadObject(JsonElement element)`.
  - `void Write(Stream utf8Destination, TableEntity entity)` writes one line: the object plus `'\n'`.
  - `TableEntity Read(ReadOnlySpan<byte> utf8Line)`.
  - `IAsyncEnumerable<TableEntity> ReadLinesAsync(Stream utf8Source, CancellationToken ct = default)` streams line
    by line, never buffers the whole stream, and skips blank lines.

**Wire format**, version 1. The line is exactly
`{"v":1,"pk":"…","rk":"…","ts":"<o>"|null,"p":{"<name>":{"t":"<Edm>","v":<value>},…}}`:
- Properties are sorted ordinally, so equal rows give byte-identical lines.
- `PartitionKey`, `RowKey`, `Timestamp` and `odata.etag` never appear in `p`. The ETag is not exported.
- Null-valued properties are skipped, because the service never stores nulls.

| `t` | Accepted CLR value on write | `v` | CLR value on read |
|---|---|---|---|
| `Edm.String` | `string` | JSON string | `string` |
| `Edm.Binary` | `byte[]`, `BinaryData` | base64 string | `byte[]` |
| `Edm.Boolean` | `bool` | `true`/`false` | `bool` |
| `Edm.DateTime` | `DateTimeOffset`, `DateTime` (`Unspecified` treated as UTC) | `"o"` string of the UTC instant | `DateTimeOffset` (offset 0) |
| `Edm.Double` | `double` | string, `ToString("R")`, plus `"NaN"`, `"Infinity"`, `"-Infinity"` and `"-0"` | `double` |
| `Edm.Guid` | `Guid` | `"D"` string | `Guid` |
| `Edm.Int32` | `int` | JSON number | `int` |
| `Edm.Int64` | `long` | decimal string | `long` |

**Rules:**
- Use `Utf8JsonWriter` with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`.
- Any other CLR type on write throws `NotSupportedException` naming the property.
- A string containing an unpaired surrogate throws `ArgumentException` naming the property. The service cannot hold
  one.
- On read, an unknown `v`, an unknown `t` or a malformed value throws `FormatException` naming the property.

- [ ] **Step 1: Write the failing tests:**
  - `[Theory]` `EveryEdmType_RoundTrips_TypeAndValue` (Review Focus 4). Cover:
    - `long.MinValue`, `long.MaxValue`, `(1L << 53) + 1`
    - `int.MinValue`
    - `double.NaN`, `±Infinity`, `-0.0` (assert with `BitConverter.DoubleToInt64Bits`), `double.Epsilon`, `0.1`, `1.0`
      (stays `double`)
    - `DateTimeOffset` with 7 fractional digits and `+05:30`, which reads back equal to the same instant at offset 0;
      `new DateTimeOffset(1601,1,1,0,0,0,TimeSpan.Zero)`
    - `DateTime` of kind Utc, Local and Unspecified
    - `Guid.Empty`
    - `byte[0]`, a 65,536-byte array, and `BinaryData`
    - `""`, `"line1\nline2\t\"q\""`, `"😀𠀀"`, `true` and `false`
  - `Write_IsOneLine_Deterministic_SortedProperties`: the output contains exactly one `\n`, at the end, and two
    entities with insertion-ordered properties give equal bytes.
  - `SystemColumns_NotInPayload_TimestampCarried_ETagDropped`.
  - `SplitRows_And_BlobReferenceCells_RoundTripVerbatim`:
    - The primary carries `_SplitParts`, `Body__c0`, `Body__chunks`, `Body__sha256`, `Body__enc` and `Body__len`.
    - The part `rk~0001` carries `_SplitOf` and `_SplitIndex`.
    - `File__blob` holds the 0.9.0 reference JSON.
    - All of these round-trip with identical types.
  - `UnsupportedClrType_Throws` (`decimal`, `float`) and `UnpairedSurrogate_Throws`.
  - `Read_UnknownVersionOrType_ThrowsFormatException`.
  - `ReadLinesAsync_StreamsLargeFile_WithoutBuffering`: 10,000 lines of about 200 KB, read through a non-seekable
    stream. The allocated-bytes delta per line stays bounded (under 4 MB).
  - `WriteObject_EmbedsInEnvelope`: the host-style `{"op":"upsert","entity":<object>}` round-trips through
    `ReadObject`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TableEntityNdjsonTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(snapshot): TableEntityNdjson — lossless Edm-typed raw row codec"`

---

### Task 6: `ITableSnapshotReader` over Azure

**Files:**
- Create: `src/Unified.Data.Tables/ITableSnapshotReader.cs`, `SnapshotScope.cs` and `TableSnapshotReader.cs`.
- Modify: `src/Unified.Data.Tables/ServiceCollectionExtensions.cs`. This adds the always-on registration from
  Task 4.
- Test: `tests/Unified.Data.Tables.Tests/ChangeLog/TableSnapshotReaderTests.cs`.

**Interfaces:**
- Consumes: 0.9.0 `SplitRowLayout.PartRangeLower`, `PartRangeUpper` and `PartCount`, and `SystemColumnNames.SplitOf`.
- Produces:
  - `public sealed record SnapshotScope(string? PartitionKey = null, DateTimeOffset? ModifiedSince = null);`
  - `public interface ITableSnapshotReader`:
    - `IAsyncEnumerable<string> ListTablesAsync(CancellationToken ct = default);` returns every table in the
      account, including the change-log table. The host decides what to skip.
    - `IAsyncEnumerable<TableEntity> ReadAllAsync(string table, SnapshotScope? scope = null, CancellationToken ct = default);`
      - It streams every **physical** row, continuation rows included.
      - The `PartitionKey` scope becomes `PartitionKey eq`, and the `ModifiedSince` scope becomes
        `Timestamp ge datetime'…'`.
      - A missing table yields nothing.
    - `Task<TableEntity?> GetAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default);`
      is one raw point read. A missing row or table returns `null`.
    - `Task<IReadOnlyList<TableEntity>> GetLogicalAsync(string table, string partitionKey, string rowKey, CancellationToken ct = default);`
      - An absent entity returns `[]`.
      - An unsplit entity returns `[primary]`.
      - A split entity returns `[primary, part 1..N]`. N is the primary's `_SplitParts`, and a part counts only when
        `_SplitOf == rowKey` and `_SplitIndex` is in 1..N.
      - Consistency (Review Focus 3): after reading the parts, re-read the primary keys-only. If its ETag changed,
        retry. After 3 attempts, throw `InvalidOperationException`.
  - `public sealed class TableSnapshotReader(TableServiceClient service) : ITableSnapshotReader`. It never normalizes
    keys, never creates tables, and never deserializes through `T`.

- [ ] **Step 1: Write the failing tests** (mocked `TableServiceClient` and `TableClient`):
  - `ListTables_ReturnsAllNames`.
  - `ReadAll_NoScope_NoFilter_AllPhysicalRows`.
  - `ReadAll_Scope_BuildsPartitionAndTimestampFilter`, with the exact filter string.
  - `ReadAll_MissingTable_YieldsNothing`.
  - `Get_Missing_ReturnsNull`.
  - `GetLogical_Unsplit_OneRow_NoRangeQuery`.
  - `GetLogical_Split_PrimaryThenPartsInIndexOrder_IgnoresForeignAndStale`. The candidates are `rk~0001`,
    `rk~0002`, `rk~0003` (stale, index > `_SplitParts`) and `rkx~0001`.
  - `GetLogical_PrimaryChangedDuringRead_Retries_ThenSucceeds`.
  - `GetLogical_PrimaryKeepsChanging_ThrowsAfter3`.
  - `Registration_AlwaysResolvesSnapshotReader` (`[Collection("OversizedCellPolicy")]`).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TableSnapshotReaderTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(snapshot): ITableSnapshotReader — raw table listing, streaming and logical point reads"`

---

### Task 7: InMemory parity

**Files:**
- Create: `src/Unified.Data.Tables.InMemory/InMemoryTableAccount.cs`, `InMemoryChangeLog.cs` and
  `InMemorySnapshotReader.cs`.
- Modify `src/Unified.Data.Tables.InMemory/InMemoryStorage.cs`:
  - the constructors `:43-70`
  - `CreateAsync` `:111`
  - `UpsertAsync` `:139`
  - `UpdateAsync` `:168-262`
  - `DeleteAsync` `:502`
  - `DeletePartitionAsync` `:521`
  - `WriteBatch` `:538`
  - 0.9.0's split paths
- Modify `InMemoryPolymorphicStorage.cs` (constructor `:60` and every mutator).
- Modify `InMemoryServiceCollectionExtensions.cs`.
- Test: `tests/Unified.Data.Tables.Tests/ChangeLog/InMemoryChangeLogParityTests.cs`.

**Interfaces:**
- Produces:
  - `public sealed class InMemoryTableAccount`:
    - `public InMemoryChangeLog ChangeLog { get; }` and `public ITableSnapshotReader Snapshots { get; }`.
    - `internal void Register(string table, Func<IReadOnlyList<(TableEntity Row, long Version, DateTimeOffset Timestamp)>> snapshot)`.
      Registering the same name from two stores throws `InvalidOperationException`.
  - `public sealed class InMemoryChangeLog : IChangeLogWriter, IChangeLogReader`:
    - It uses the same `ChangeLogKeys`, the same `At` per call and the same trim semantics.
    - It reads its `TimeProvider` from `ChangeLogOptions`.
    - Test hooks: `internal Func<Exception?>? FailNext` and `public IReadOnlyList<ChangeLogEntry> Entries()`.
  - `InMemorySnapshotReader` behaves as in Task 6, over the registered snapshots. Rows are copies whose `ETag` is
    `W/"{version}"` and whose `Timestamp` is the stored one.
  - New constructor `InMemoryStorage(IProtectedPropertyAuthorizer? authorizer = null, UnifiedTableStorageOptions? options = null, InMemoryTableAccount? account = null)`.
    - The existing two-parameter constructor **loses its default values**. This is binary-compatible, and source
      callers bind to the new one; without the change the calls would be ambiguous (CS0121).
    - A null `account` gets a private one, exposed as `public InMemoryTableAccount Account { get; }`.
    - The table name is `options.TableNameResolver?.Invoke(typeof(T))`, falling back to `typeof(T).Name`.
  - New constructor `InMemoryPolymorphicStorage(string tableName, UnifiedTableStorageOptions? options, InMemoryTableAccount? account)`,
    with the same default-value move.
  - `AddUnifiedInMemoryStorage`:
    - `TryAddSingleton<InMemoryTableAccount>()` and `TryAddSingleton<ITableSnapshotReader>(sp => sp.GetRequiredService<InMemoryTableAccount>().Snapshots)`.
    - When enabled, also register `IChangeLogReader` and `IChangeLogWriter` as `account.ChangeLog`.
    - `AddUnifiedInMemoryPolymorphicTable` passes `sp.GetService<InMemoryTableAccount>()` and the registered
      `ChangeLog`, as in Task 4.

**Rules:**
- Logging happens before the `gate` lock and before the mutation, and uses the same keys and op mapping as Task 3.
- A `FailNext` exception propagates, and the row store stays unchanged.
- `DeletePartitionAsync` logs logical keys only, and logs nothing when the partition is empty.

- [ ] **Step 1: Write the failing tests:**
  - `[Theory]` `SameScenario_SameEntries_AsTableStorage`. Each mutator runs on InMemory, and the produced
    `(Table, PartitionKey, RowKey, Op)` set equals the set captured from the mocked `TableStorage` in Task 3's
    harness for the same scenario.
  - `LogFailure_RowStoreUnchanged` (Review Focus 5), for every mutator.
  - `SplitAware_OneEntryPerLogicalEntity_SnapshotHasParts`:
    - A 600,000-char `SplitTextEntity` gives one log entry.
    - `GetLogicalAsync` returns 2 rows, and `ReadAllAsync` returns 2 physical rows.
  - `SharedAccount_TwoStores_OneLog_TwoTables`.
  - `ReadAsync_Range_And_Trim`, with a fixed clock: entries in three buckets are trimmed to one.
  - `DirectConstruction_PrivateAccount_Works`.
  - `Disabled_NoEntries`.
  - `NamedOptionsArgument_StillCompiles`: `new InMemoryStorage<TestEntity>(options: o)` compiles without ambiguity.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~InMemoryChangeLogParityTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter, `InMemoryStorageTests` and `InMemoryPolymorphicStorageTests`. Expected: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(changelog): InMemory change log, table account and snapshot reader for parity"`

---

### Task 8: Contract test over every public mutator

**Files:**
- Test: `tests/Unified.Data.Tables.Tests/ChangeLog/ChangeLogContractTests.cs`.

**Interfaces:**
- Consumes: everything above, through InMemory (shared account) and through Task 3's mocked `TableStorage<T>`
  harness.

**Rules:**
- The inventory is computed by reflection over public members of four kinds:
  - instance methods of `IStorage<>` and `IPolymorphicStorage<>`;
  - public static methods of `StorageExtensions`, `AppendLogExtensions`, `VersionedStreamExtensions` and
    `PolymorphicStorageExtensions`.
- Each member is keyed by name plus parameter types. The test holds one explicit classification:
  - `Mutates(op)` for:
    - `IStorage`: `CreateAsync`, `UpsertAsync`, the three `UpdateAsync` overloads, `DeleteAsync`,
      `DeletePartitionAsync`, `CreateBatchAsync` and `UpsertBatchAsync`;
    - `IPolymorphicStorage`: `InsertAsync`, `UpsertAsync`, `InsertBatchAsync`, `MergeColumnsAsync`, `DeleteAsync`
      and `DeletePartitionAsync`;
    - extensions: `MutateAsync`, `GetOrCreateAsync`, `MutateOrCreateAsync`, `TryMutateAsync`,
      `TryTransitionAsync`, `AppendAsync`, `AppendVersionAsync`, and the polymorphic `InsertAsync`, `UpsertAsync`
      and `InsertMarkerAsync`.
  - `ReadOnly` for everything else, with `SweepOrphanBlobsAsync` marked `BlobOnly`.

- [ ] **Step 1: Write the tests:**
  - `Inventory_EveryPublicMemberIsClassified`. An unclassified or vanished member fails with its signature in the
    message, so a mutator added later cannot ship without a log case.
  - `[Theory]` `EveryMutator_WritesExactlyOneEntryPerLogicalEntity`. The `MemberData` is built from the
    `Mutates` set. Each case:
    - runs the member once on InMemory and once on mocked `TableStorage`, asserting the entries' keys and ops;
    - runs once with `FailNext` set, asserting the store is unchanged and the exception propagates.
    - Batch cases use 3 entities, giving 3 entries.
    - `GetOrCreateAsync` on an existing id writes 0 entries, because no mutation happens. The case asserts that.
  - `[Theory]` `EveryReadOnlyMember_WritesNoEntry`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ChangeLogContractTests"`. Expected: PASS. A failure means a
  provider gap: fix the provider, not the classification.
- [ ] **Step 3: Commit**: `git commit -m "test(changelog): contract test — every public mutator writes a log row, every reader none"`

---

### Task 9: Azurite integration

**Files:**
- Test: `tests/Unified.Data.Tables.Tests/ChangeLog/AzuriteChangeLogIntegrationTests.cs`.
  - Use the probe/skip/cleanup pattern of `AzuriteTableStorageIntegrationTests.cs:20-62`, probing tables and blobs.
  - Each test uses unique names: entity tables `cl{Guid:N}`[..20], the log table `cllog{Guid:N}`[..20], and a blob
    container `cl-{Guid:N}`.
  - `DisposeAsync` deletes all three.

- [ ] **Step 1: Write the tests.** Each one starts with `Assert.SkipUnless(_available, …)`.
  - `EveryMutator_LogRowLands_BeforeEntity`. Run Create, Upsert, Update and Delete through a real
    `TableStorage<TestEntity>`. `ReadAsync(start, now + 1 s)` returns 4 entries with the right ops, and each
    entry's `At` is at or before the entity row's service `Timestamp`, within the measured skew.
  - `DeletePartition_250Rows_250DeleteEntries`.
  - `Polymorphic_Mutators_Log`.
  - `LongAndPipeKeys_Log` (Review Focus 1). Use the RowKey `"a|b"` plus 600 `x` characters.
  - `RawEdm_RoundTrip_ThroughService` (Review Focus 4):
    1. Write a raw `TableEntity` holding every Edm edge value from Task 5 with `TableClient`.
    2. Read it with `TableSnapshotReader.GetAsync`, write it with `TableEntityNdjson.Write`, then `Read` it.
    3. Write it into a second table and read it raw.
    4. Every property must have equal type and value.
    5. Record in a comment, and assert, the CLR type the SDK returns for the integral `Edm.Double` `1.0` and for
       `Edm.Binary`. If the SDK returns `int` for `1.0`, the test asserts the writer still emits `Edm.Double`
       through the type annotation it read. If that information is lost, fix it in `TableSnapshotReader`, for
       example with `TableEntity.GetDouble`, not by weakening the test.
  - `SplitEntity_Snapshot_Restore_ReadsBack`:
    1. Write a 600,000-char `SplitTextEntity` through `TableStorage`. The log has 1 entry.
    2. `GetLogicalAsync` returns 2 rows.
    3. Round-trip them through NDJSON, upsert the raw rows into a fresh table, and open a `TableStorage` over it
       (`TableNameResolver`).
    4. `OneAsync` returns the original value.
  - `OffloadedEntity_Snapshot_CarriesBlobPath_RestoreReadsSameBlob`:
    1. Write a 3,200,000-char value with `BlobOffload`.
    2. The raw row's `Body__blob` holds the path.
    3. After a restore into a differently named table, `OneAsync` reads the value through the same blob path. This
       confirms 0.9.0 reads the stored reference path and does not recompute it.
  - `ReadAll_ModifiedSince_FiltersByTimestamp`.
  - `Trim_RemovesOlderBuckets_Only`. Use an injected clock to write to two past buckets and the current bucket.
  - `LogTableMissing_FirstWriteCreatesIt`.
- [ ] **Step 2: Start Azurite** (`npx azurite --silent --location <scratch dir>`) and **run**
  `--filter "FullyQualifiedName~AzuriteChangeLogIntegrationTests"`. Expected: PASS, with none skipped.
  Then run once with Azurite stopped. Expected: every test skipped, none failed.
- [ ] **Step 3: Fix** provider differences in the source, not by weakening a test. Then re-run the Task 3-8 filters.
- [ ] **Step 4: Commit**: `git commit -m "test(changelog): Azurite — log rows, Edm round-trip, split and offloaded snapshot restore"`

---

### Task 10: Documentation and 0.10.0

**Files:**
- Modify: `README.md`. Add a Features bullet, and add a new section `## Change log and raw snapshots` after
  `## Polymorphic storage`. In `## Testing with Unified.Data.Tables.InMemory`, describe `InMemoryTableAccount`.
- Modify: `CHANGELOG.md`. Add `## [0.10.0] — <release date>` above the 0.9.0 entry.
- Modify: `Directory.Build.props:35`. Set `<Version>0.10.0</Version>`.

- [ ] **Step 1: Write the README section.** It covers:
  - enabling the log and the table-name rule;
  - the row shape;
  - the op mapping;
  - "before the operation, fail-closed": phantom entries, why readers must point-read current state, and why they
    must overlap one bucket because `At` is the writer's clock before commit;
  - one entry per logical entity;
  - what is not logged: raw `TableClient` writers (use `IChangeLogWriter`), blob operations, and the log itself;
  - `IChangeLogReader` range and order semantics, and `TrimBucketsBeforeAsync`;
  - `ITableSnapshotReader`, including `GetLogicalAsync` consistency;
  - the NDJSON format table from Task 5;
  - the cost (one extra transaction per mutation call);
  - the hot-partition limit (one bucket partition per hour for the whole account).
- [ ] **Step 2: Write the CHANGELOG entry.**
  - **Added:** every new public type and member; `UnifiedTableStorageOptions.ChangeLog`; the DI registrations.
  - **Changed:** the `InMemoryStorage` and `InMemoryPolymorphicStorage` two-parameter constructors lose their
    defaults (binary-compatible); new three-parameter constructors are added.
  - **Unchanged:** with `ChangeLog.Enabled == false`, the same SDK calls as 0.9.0.
- [ ] **Step 3: Run** `dotnet build Unified.Data.Tables.slnx -c Release` and `dotnet test Unified.Data.Tables.slnx -c Release`,
  with Azurite running. Expected: 0 warnings and 0 failures.
  Then run `dotnet pack Unified.Data.Tables.slnx -c Release -o <scratch dir>`. Expected: `*.0.10.0.nupkg` for all
  four packages.
- [ ] **Step 4: Commit**: `git commit -m "docs: change log and raw snapshot access; release 0.10.0"`
- [ ] **Step 5: Hand-off.** Push and open the PR only when the user asks. Merge, release and the IntelliSpec upgrade
  are separate steps the user authorizes.

---

## Canon issues

1. **`_UnifiedChangeLog` is not a valid Azure table name.** Table names are alphanumeric and start with a letter
   (`^[A-Za-z][A-Za-z0-9]{2,62}$`). The service, and Azurite, reject the underscore. The plan uses `UnifiedChangeLog`
   as the default and validates any override. The IntelliSpec backup plan and runbook must use `UnifiedChangeLog`,
   or read `ChangeLogOptions.TableName`, when they exclude the log from table enumeration.
2. **Point read returns the logical entity.** The canon's "point-read raw TableEntity" is kept as `GetAsync`.
   `GetLogicalAsync` is added because a 0.9.0 split entity is a primary row plus `~0001..` continuation rows, and an
   incremental backup must capture all of them consistently.
3. **`IChangeLogWriter` is new.** It is the library's own write seam. It is also how a host logs rows that it writes
   raw: a `SelectiveLive` restore, or the append-only journal table that IntelliSpec writes through `TableClient`.
   Without it, restored or raw-written rows are invisible to increments until the next weekly baseline. The
   IntelliSpec backup plan (`docs/superpowers/plans/2026-11-10-storage-backup.md` Task B7) calls it for every key its
   `SelectiveLive` restore upserts or deletes. The user decided (2026-11-10) that IntelliSpec's raw journal table is
   logged too: its writer calls `RecordAsync` before each append, fail-closed, so the journal has the same hourly RPO
   as library tables (backup plan Task B1a). Nothing in this library changes for that; `RecordAsync` must simply stay
   usable by a host for a table no `TableStorage<T>` owns.
4. **The log RK is not parseable**, because a PK or RK may contain `|`. Entries are built from the five property
   columns. Keys longer than 512 chars use a hashed RK; the full keys stay in the properties.
5. **The polymorphic store logs too**, and its change-log setting is process-wide (Review Focus 2). The research only
   named `IStorage<T>`.
6. **`SweepOrphanBlobsAsync` (0.9.0) writes no log row.** It never touches rows. The blobs it removes are
   unreferenced by definition.
7. **Phantom entries and clock basis.** `At` is the writer's clock, taken before the commit, and entries exist for
   operations that then fail. The canon's "watermark − 1 bucket" overlap in `TableBackupService` covers commit
   lag and cross-instance skew of under an hour. The host must not narrow it.

## Open risks

- **Write latency and cost.** Every mutation call gains one extra round trip (or one transaction per batch chunk)
  to the same account. Fail-closed means a log-table outage blocks writes. Both writes are in the same account, so
  the added failure probability is small but not zero.
- **Hot partition.** All mutations in the account during one hour land in one log partition. Azure's per-partition
  target is about 2,000 entities/s. That is ample for the intended hosts, but a high-throughput user would need a
  sharded bucket (`{yyyyMMddHH}-{n}`), which is a later, compatible addition. The README states the limit.
- **SDK type fidelity for `Edm.Double` and `Edm.Binary`.** It is unverified which CLR types the SDK surfaces for these
  in a raw `TableEntity`. Task 9 pins this against Azurite only; production is not covered.
- **Writers outside the library**, such as raw `TableClient` users, other processes, older package versions or
  portal edits, are not logged. The host's weekly key-diff reconciliation is the only safety net, and every
  writer must run 0.10.0 with `Enabled` before the first baseline (spec §2.7.4).
- **Full scans are not snapshot-isolated.** `ReadAllAsync` can see a split entity mid-replace. The next incremental
  run repairs it, because the log names the key. Restores from a baseline alone can briefly carry a mixed generation
  that the library's integrity check then reports.
- **The InMemory private account.** A directly constructed `InMemoryStorage` without a shared account logs into its
  own account, so cross-store assertions need the DI registration or an explicitly shared account.
