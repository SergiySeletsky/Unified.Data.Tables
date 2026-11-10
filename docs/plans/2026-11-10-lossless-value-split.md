# Lossless Value Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An opt-in `OversizedCellPolicy.Split` / `[TableValueSplit]` mode that stores a property value of any size
losslessly: chunk cells inside one entity, continuation rows written in one transaction, or (when that is not
enough) chunk rows across many transactions committed by a final commit row. Every read reassembles and checks
SHA-256. Ships as 0.9.0.

**Architecture:** Three layers, each a pure function of the one below. (1) `SplitCodec` turns one oversized value
into a *logical* column bag (`{Prop}__c0..cN` plus a manifest) and back. (2) The serializer emits and consumes those
cells for opted-in properties, so `ToTableEntity` / `FromTableEntity` round-trip an unbounded logical bag in memory.
(3) `SplitRowLayout` distributes a logical bag onto physical rows (primary + parts) and merges them back. `TableStorage<T>`
and `InMemoryStorage<T>` both store physical rows through `SplitRowLayout`, so part rows, hiding, generations and
sweep behave identically in the fake and in Azure. A type that opts into nothing never produces a split column or a
part row, so nothing changes for it.

**Tech Stack:** C# (`LangVersion=latest`), `net10.0` (Abstractions also `netstandard2.0`), Azure.Data.Tables 12.11.0,
xUnit v3 with plain `Assert`, NSubstitute, Azurite.

**Spec:** `D:\github\intellispec\docs\superpowers\specs\2026-11-10-llm-call-recorder-design.md`, §5.5 "Prerequisite:
lossless oversized values in Unified.Data.Tables", plus user decisions 9 and 10 (§2) and the payload sizes in §4.

**Branch / worktree:** library repo `D:\github\Unified.Data.Tables`, branch `feat/lossless-value-split` off `main`
(worktree, if used, at `D:\github\Unified.Data.Tables-lossless-value-split`; D: had 195 GB free on 2026-11-10).
Merging, tagging and running `.github/workflows/publish.yml` are the user's actions, not the executor's.

## Global Constraints

- The PR contains nothing specific to IntelliSpec: no recorder names, table names, vision ids or LLM terms in code,
  tests, README or CHANGELOG.
- No behaviour change for existing users: with the default policy (`OversizedCellPolicy.Throw`,
  `UnifiedTableStorageOptions.cs:22-24`, `TableEntitySerializer.cs:41`) and no `[TableValueSplit]`, every existing
  test passes unchanged and no row ever gains a split or `_Split*` column.
- Encodings: `SplitEncoding.Text` (readable UTF-16 string cells of at most **32,000 chars**, never splitting a
  surrogate pair) and `SplitEncoding.Gzip` (binary cells). `byte[]` is **always** raw binary cells, never base64
  strings. Binary cell maximum **65,536 bytes**.
- A value that fits one cell is stored as an ordinary column, exactly as today.
- Cell names: `{Prop}__c0..cN`, `{Prop}__chunks`, `{Prop}__sha256`, `{Prop}__enc`, plus `{Prop}__len`.
- Continuation rows (one transaction): `{RowKey}~0001`, `{RowKey}~0002`, … Chunk rows (multi-transaction):
  `{RowKey}~c{n:D6}~{generation}`; the commit row is `{RowKey}`, created last.
- One transaction = `BatchPlanner.DefaultMaxCount` (100) entities and `BatchPlanner.DefaultMaxBytes` (3,000,000 B,
  measured with `TableRowSize.Estimate`). One entity = at most 252 custom properties and 1,000,000 estimated bytes.
- Never trims, never refuses a size. A corrupt or missing chunk throws `TableValueIntegrityException`; partial data is
  never returned.
- Replace: new generation chunks → swap commit row with `If-Match` → delete old generation. Delete: commit row first,
  then chunks.
- `IStorage<T>.SweepUncommittedAsync(string partitionKey, TimeSpan olderThan, CancellationToken ct)` returns `Task<int>`.
- Version `0.9.0` in `Directory.Build.props:35`. No other package reference versions change.
- `TreatWarningsAsErrors`, analyzers `Recommended` (`Directory.Build.props:17-27`); XML docs on every public member.
- Tests that set `TableEntitySerializer.OversizedCellPolicy` join `[Collection("OversizedCellPolicy")]`
  (`tests/Unified.Data.Tables.Tests/Fixes053Tests.cs:21`) and restore the previous value in `Dispose`.
- Build: `dotnet build Unified.Data.Tables.slnx -c Release`. Focused test:
  `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj -c Release --filter "FullyQualifiedName~<Class>"`.
  Never pass `-o`.

## Review Focus

1. **A RowKey that already contains `~` in a split-aware table** would collide with another entity's part rows
   (`abc~0001`). Expected: the write is refused up front with `ArgumentException` naming the reserved character;
   nothing is written. Test in Task 4.
2. **Upsert that shrinks a split value back to one cell** (or to fewer parts) leaves the old part rows behind.
   Expected: reads ignore parts whose `_SplitGen` differs from the primary's, the value reads back exactly, and
   `SweepUncommittedAsync` later removes the stale parts. Tests in Task 4 and Task 7.
3. **A projection (`QueryOptions.Select`)** that names a split property must return the whole value, and one that
   omits it must not fetch parts or throw an integrity error. Test in Task 4.
4. **`Take` and paging over a partition containing part rows.** Expected: `Take = N` yields N logical entities; part
   rows are never counted, yielded or cached; `QueryPageAsync` may return a short page but its cursor still reaches
   every entity. Test in Task 4.
5. **A string containing an unpaired surrogate** (invalid UTF-16 that .NET strings allow). Expected: the Text split
   round-trips it code unit for code unit, and the SHA-256 (over raw UTF-16 code units, not `Encoding.UTF8`) matches.
   Test in Task 1.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Unified.Data.Tables.Abstractions/SplitEncoding.cs` (create) | `enum SplitEncoding { Text, Gzip }` |
| `src/Unified.Data.Tables.Abstractions/TableValueSplitAttribute.cs` (create) | Opt-in per property (or per class) |
| `src/Unified.Data.Tables.Abstractions/SystemColumnNames.cs` (modify) | `_SplitOf`, `_SplitGen`, `_SplitIndex`, `_SplitParts`, `_SplitMode` constants |
| `src/Unified.Data.Tables.Abstractions/IStorage.cs` (modify) | `SweepUncommittedAsync` member |
| `src/Unified.Data.Tables/OversizedCellPolicy.cs` (modify) | `Split` value |
| `src/Unified.Data.Tables.Abstractions/TableValueIntegrityException.cs` (create) | Read-side integrity failure (in Abstractions so consumers that reference only `.Abstractions` can catch it) |
| `src/Unified.Data.Tables/SplitCodec.cs` (create, internal) | One value ↔ chunk cells + manifest; surrogate-safe cut; SHA-256 |
| `src/Unified.Data.Tables/TableEntitySerializer.cs` (modify) | Emit/consume split cells for opted-in properties |
| `src/Unified.Data.Tables/SplitRowLayout.cs` (create, public) | Logical bag ↔ physical rows; generation; part detection |
| `src/Unified.Data.Tables/TableStorage.cs` (modify → `partial`) + `TableStorage.Split.cs` (create) | Split-aware write/read/delete/sweep paths |
| `src/Unified.Data.Tables/PolymorphicTableStorage.cs`, `TableFilterTranslator.cs` (modify) | Refuse split values / split members |
| `src/Unified.Data.Tables.InMemory/InMemoryStorage.cs`, `InMemoryPolymorphicStorage.cs`, `.csproj` (modify) | Parity |
| `tests/Unified.Data.Tables.Tests/Split/*.cs` (create) | All new tests; `Split/SplitModels.cs` holds fixtures |
| `README.md`, `CHANGELOG.md`, `Directory.Build.props` (modify) | Docs and 0.9.0 |

---

### Task 1: Public surface and `SplitCodec`

**Files:**
- Create: `src/Unified.Data.Tables.Abstractions/SplitEncoding.cs`, `src/Unified.Data.Tables.Abstractions/TableValueSplitAttribute.cs`
- Modify: `src/Unified.Data.Tables.Abstractions/SystemColumnNames.cs:24-39` (add constants), `src/Unified.Data.Tables/OversizedCellPolicy.cs:27` (add `Split`)
- Create: `src/Unified.Data.Tables.Abstractions/TableValueIntegrityException.cs`, `src/Unified.Data.Tables/SplitCodec.cs`
- Test: `tests/Unified.Data.Tables.Tests/Split/SplitCodecTests.cs`

**Interfaces:**
- Produces:
  - `public enum SplitEncoding { Text, Gzip }`
  - `[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, Inherited = true)] public sealed class TableValueSplitAttribute(SplitEncoding encoding) : Attribute { public SplitEncoding Encoding { get; } }`
  - `SystemColumnNames.SplitOf = "_SplitOf"`, `SplitGeneration = "_SplitGen"`, `SplitIndex = "_SplitIndex"`, `SplitParts = "_SplitParts"`, `SplitMode = "_SplitMode"`
  - `OversizedCellPolicy.Split` — XML doc: oversized values (after the existing single-cell GZip attempt) are split losslessly with `SplitEncoding.Gzip`; properties marked `[TableValueSplit]` use their own encoding whatever the policy.
  - `public sealed class TableValueIntegrityException : SerializationException` with ctor `(string columnName, string reason)` and properties `ColumnName`, `Reason`. Derives from `SerializationException` for the same reason as `OversizedCellException.cs:16`. It lives in **`Unified.Data.Tables.Abstractions`** (namespace `Unified.Data.Tables`; `System.Runtime.Serialization.SerializationException` exists on `netstandard2.0`), so a consumer layer that references only the Abstractions package can catch it.
  - `internal static class SplitCodec`:
    - `const int MaxTextCellChars = 32_000; const int MaxBinaryCellBytes = 65_536;`
    - `static bool NeedsSplit(object payload)` — string longer than 32,000 chars or `byte[]` longer than 65,536.
    - `static IReadOnlyList<KeyValuePair<string, object>> Encode(string column, object payload, SplitEncoding encoding)` — `payload` is `string` or `byte[]`; returns chunk cells then manifest cells (`__chunks` int, `__sha256` lower-hex string, `__enc` `"text"|"gzip"|"binary"`, `__len` long).
    - `static object Decode(string column, IReadOnlyDictionary<string, object> cells)` — returns `string` (`text`, `gzip`) or `byte[]` (`binary`); throws `TableValueIntegrityException` on a missing chunk, wrong count, wrong length or hash mismatch.
    - `static int SafeCut(string s, int start, int maxChars)` — end index; steps back one when the cut would separate a high surrogate from the low surrogate that follows it.
    - `static bool TryParseSplitColumn(string column, out string baseColumn, out SplitCellKind kind, out int index)` with `internal enum SplitCellKind { Chunk, Count, Sha256, Encoding, Length }`.
  - Hash definition (pins the algorithm): SHA-256 over the *stored* payload — for `text`, the raw UTF-16LE code units of the concatenated chunks (`MemoryMarshal.AsBytes(span)`, never `Encoding.Unicode.GetBytes`, which replaces lone surrogates); for `gzip`/`binary`, the concatenated chunk bytes. `gzip` compresses the UTF-8 of the string with `GZipStream` and stores the bytes as binary cells (no base64).

- [ ] **Step 1: Write the failing tests** in `SplitCodecTests`:
  - `Encode_Text_ProducesReadableStringCells_NoneLongerThan32000` — 70,001-char ASCII string → 3 chunk cells, all `string`, each `Length <= 32_000`, `__chunks == 3`, `__enc == "text"`, `__len == 70_001L`.
  - `Encode_Text_NeverSplitsASurrogatePair` — string whose char 31,999/32,000 is a pair `"\uD83D\uDE00"` → chunk 0 ends with no high surrogate; `Decode` equals input.
  - `Encode_Text_UnpairedSurrogate_RoundTripsExactly` (Review Focus 5) — lone `'\uD800'` at index 31,999 and at the end → `Decode(...) == input` with `string.Equals(..., StringComparison.Ordinal)`.
  - `Encode_Bytes_AlwaysBinaryCells` — 200,000 random bytes with either encoding → every chunk is `byte[]` with `Length <= 65_536`, `__enc == "binary"`.
  - `Encode_Gzip_StoresBinaryCells_NotBase64` — 300,000-char string, `SplitEncoding.Gzip` → chunks are `byte[]`, `__enc == "gzip"`, round-trips.
  - `Decode_CorruptChunk_ThrowsIntegrity` — flip one char of chunk 1 → `TableValueIntegrityException` with `ColumnName == "Body"`.
  - `Decode_MissingChunk_ThrowsIntegrity` — remove `Body__c1` → throws; never returns a partial string.
  - `Decode_WrongChunkCount_ThrowsIntegrity` — `__chunks` says 4, 3 present → throws.
  - `TryParseSplitColumn_RecognisesOnlySplitSuffixes` — `"Body__c12"` → Chunk/12; `"Parent_Child__sha256"` → base `"Parent_Child"`; `"Body__Json"`, `"Body__GZip"`, `"Body__Truncated"`, `"Body"` → false.

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SplitCodecTests"`. Expected: FAIL (types not defined).
- [ ] **Step 3: Implement** the types above. `SplitCodec` is pure (no I/O, no static state).
- [ ] **Step 4: Run** the same filter, then the full test project. Expected: new tests PASS; all existing tests PASS.
- [ ] **Step 5: Commit**

```bash
git add src/Unified.Data.Tables.Abstractions src/Unified.Data.Tables/OversizedCellPolicy.cs src/Unified.Data.Tables/SplitCodec.cs tests/Unified.Data.Tables.Tests/Split/SplitCodecTests.cs
git commit -m "feat(split): add SplitEncoding, TableValueSplit, Split policy and the lossless chunk codec"
```

---

### Task 2: Serializer emits and reassembles split cells

**Files:**
- Modify: `src/Unified.Data.Tables/TableEntitySerializer.cs` — `Materialize` (`:175-219`), `Flatten` (`:363-424`), `TableEntityValue.TrySerialize` (`:646-753`), `TypeMetadataCache.CreateMetadata` / `TypeMetadata` (`:973-993`, `:1042-1052`), `FlattenProperty` (`:303-314`)
- Modify: `src/Unified.Data.Tables/PolymorphicTableStorage.cs:306-315` (`ToRow`), `src/Unified.Data.Tables.InMemory/InMemoryPolymorphicStorage.cs` (same row build), `src/Unified.Data.Tables/TableFilterTranslator.cs` member resolution (`:59-120`), `src/Unified.Data.Tables/TableStorage.cs:681-687` (builder merge)
- Create: `tests/Unified.Data.Tables.Tests/Split/SplitModels.cs`
- Test: `tests/Unified.Data.Tables.Tests/Split/SplitSerializerTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SplitCodec`, `TableValueSplitAttribute`, `OversizedCellPolicy.Split`).
- Produces:
  - `TypeMetadata.SplitEncodings : IReadOnlyDictionary<string, SplitEncoding>` (property name → encoding; class-level attribute applies to every string/`byte[]`/JSON-fallback property that has no own attribute).
  - `internal static bool TypeMetadataCache.IsSplitAware(Type t)` — true when `t` or any flattened nested property type declares `[TableValueSplit]` (cycle-guarded), or `TableEntitySerializer.OversizedCellPolicy == OversizedCellPolicy.Split`.
  - `internal static bool TableEntitySerializer.HasSplitCells(TableEntity row)` — any column parses via `SplitCodec.TryParseSplitColumn`.
  - Fixtures in `SplitModels.cs`: `SplitTextEntity : Entity { [TableValueSplit(SplitEncoding.Text)] string? Body; string? Title }`, `SplitGzipEntity` (`Body` Gzip), `SplitBinaryEntity` (`[TableValueSplit(SplitEncoding.Text)] byte[]? Image`), `SplitJsonEntity` (`[TableValueSplit(SplitEncoding.Text)] List<string>? Items`), `SplitNestedEntity` (`Inner.Body` split), `PlainEntity` (no attribute, a `string? Body`).

Rules the implementer cannot infer:
- Opted-in property (attribute present): string/`byte[]`/JSON text that fits one cell → today's single column (string: raw column, never `__GZip`; JSON: `__Json`); oversized → `SplitCodec.Encode(column, payload, encoding)` with the payload being the string, the bytes, or the JSON text. `Text` never gzips.
- Global `OversizedCellPolicy.Split`, no attribute: today's path first (raw, `__Json`, single-cell `__GZip`); only where today's code would reach the Throw/Trim branch (`:671-679`, `:717-733`) split with `SplitEncoding.Gzip` instead. Also split a `byte[]` longer than 65,536 bytes (today unguarded).
- `Materialize` collects split groups before the column loop: skip every split column in the loop, then for each base column with a `__chunks` cell call `SplitCodec.Decode` and set the property via `TableEntityValue(path, decoded, fmt)` where `fmt` is `Raw` when the target property is `string`/`byte[]` and `Json` otherwise.
- `FlattenProperty` (merge path) and the builder merge at `TableStorage.cs:681-687` throw `NotSupportedException("… is a split value; partial updates cannot write it — replace the entity instead.")` when a split cell would be produced or the property is in `SplitEncodings`.
- `PolymorphicTableStorage.ToRow` and the in-memory polymorphic equivalent throw `NotSupportedException` when `HasSplitCells(row)`; `TableFilterTranslator` throws `NotSupportedException` for a member in `SplitEncodings`.

- [ ] **Step 1: Write the failing tests** in `SplitSerializerTests` (`[Collection("OversizedCellPolicy")]`, `IDisposable` restoring the policy):
  - `SmallValue_OnOptedInProperty_IsAnOrdinaryColumn` — `Body` of 100 chars → row has `Body` (string), no `Body__*` columns.
  - `TextValue_At32000_IsOneCell_At32001_IsTwoChunks` — boundary: 32,000 → column `Body`; 32,001 → `Body__c0`, `Body__c1`, `Body__chunks == 2`; both round-trip.
  - `TextSplit_NeverUsesGzip` — 254,000-char string → no `__GZip` column, all chunk cells `string`, 8 chunks.
  - `GzipSplit_RoundTrips` / `BinarySplit_RoundTrips` (1 MB) / `JsonFallbackSplit_RoundTrips` (`Items` with 5,000 × 50-char strings) / `NestedSplit_RoundTrips`.
  - `PlainEntity_DefaultPolicy_BehaviourUnchanged` — 100,000-char low-entropy string → `Body__GZip` exactly as 0.8.3; 100,000-char random string → `OversizedCellException` (default `Throw`).
  - `GlobalSplitPolicy_KeepsSingleCellGzip_WhenItFits_AndSplitsOnlyWhenItDoesNot` — low-entropy → `Body__GZip`; random 200,000 chars → `Body__c*` with `__enc == "gzip"`.
  - `Materialize_CorruptChunk_Throws` — tamper `Body__c1` → `TableValueIntegrityException`.
  - `PolymorphicWrite_OfSplitValue_IsRefused`, `LinqFilter_OnSplitProperty_IsRefused`, `FlattenProperty_OfSplitProperty_IsRefused` — `NotSupportedException`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SplitSerializerTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the rules above. `TrySerialize` gains `out IReadOnlyList<KeyValuePair<string, object>>? splitCells`; `Flatten` writes all of them into `dict`. `Flatten` passes the child's `SplitEncoding?` (from the owning `TypeMetadata.SplitEncodings`) into the child `TableEntityValue`.
- [ ] **Step 4: Run** the filter, then the whole test project. Expected: all PASS, including `Fixes053Tests`, `TableEntitySerializerSizeTests`, `LegacyCompatTests`, `ColumnAliasTests` unchanged.
- [ ] **Step 5: Commit** — `git commit -m "feat(split): serializer writes and reassembles split cells for opted-in properties"`

---

### Task 3: `SplitRowLayout` — logical bag ↔ physical rows

**Files:**
- Create: `src/Unified.Data.Tables/SplitRowLayout.cs`
- Test: `tests/Unified.Data.Tables.Tests/Split/SplitRowLayoutTests.cs`

**Interfaces:**
- Consumes: Task 1 constants and `SplitCodec.TryParseSplitColumn`; `TableRowSize.Estimate` (`TableRowSize.cs:16`); `BatchPlanner.Plan` (`BatchPlanner.cs:63`).
- Produces (public, so `Unified.Data.Tables.InMemory` and alternative `IStorage<T>` implementations can share it):
  - `public enum SplitWriteMode { SingleRow, SingleTransaction, MultiTransaction }`
  - `public sealed record SplitRowPlan(TableEntity Primary, IReadOnlyList<TableEntity> Parts, SplitWriteMode Mode, string? Generation)`
  - `public static class SplitRowLayout`
    - `const int MaxPropertiesPerRow = 252; const long MaxEntityBytes = 1_000_000; const int MaxRowKeyLength = 480;`
    - `static SplitRowPlan Plan(TableEntity logical)` — no split cells, or all cells fit the primary → `SingleRow`, no `_Split*` columns, `Parts` empty, primary is the input unchanged. Otherwise ordinary columns + manifest cells stay on the primary; chunk cells fill the primary (SingleTransaction only) and then parts in column order, each row ≤ 252 properties and ≤ 1,000,000 estimated bytes. If primary + parts fit `BatchPlanner.Plan` as **one** range → `SingleTransaction`, part keys `{RowKey}~{i:D4}`; else `MultiTransaction`, the primary carries no chunk cells and part keys are `{RowKey}~c{i:D6}~{generation}`. Primary gets `_SplitGen`, `_SplitParts` (int), `_SplitMode` (`"rows"` / `"txn"`); each part gets `_SplitOf` (base RowKey), `_SplitGen`, `_SplitIndex` (1-based).
    - `static string ComputeGeneration(TableEntity logical)` — first 16 lower-hex chars of SHA-256 over the `__sha256` cell values, ordinal column order. Same content → same generation (idempotent rewrite).
    - `static bool IsPartRow(TableEntity row)` — has `_SplitOf`.
    - `static bool HasParts(TableEntity primary)` — `_SplitParts` > 0.
    - `static string PartRangeLower(string rowKey) => rowKey + "~"; static string PartRangeUpper(string rowKey) => rowKey + "\u007f";`
    - `static TableEntity Assemble(TableEntity primary, IEnumerable<TableEntity> candidateParts)` — keeps parts whose `_SplitOf == primary.RowKey` and `_SplitGen == primary._SplitGen`; requires indices exactly `1.._SplitParts`, else `TableValueIntegrityException(column: "_SplitParts", reason: "part 3 of 5 missing")`; returns a new bag with the primary's keys/ETag/Timestamp, all its columns and every part's non-`_Split*` columns.
    - `static void ValidateRowKey(string rowKey)` — throws `ArgumentException` when the key contains `~` or exceeds `MaxRowKeyLength`.

- [ ] **Step 1: Write the failing tests** (build logical bags with `SplitCodec.Encode` directly):
  - `NoSplitCells_IsSingleRow_AndUnchanged` — reference-equal columns, no `_Split*`.
  - `EightTextCells_FitOneEntity` — 254,000 chars → `SingleRow`, 8 chunks on the primary (spec §4: "about 8 cells, inside one entity").
  - `TwoEntities_OneTransaction` — 600,000 chars → `SingleTransaction`, 1 part with key `pk/rk~0001`, `_SplitParts == 1`.
  - `PropertyCountLimit_ForcesAPart` — 300 tiny chunk cells → every row ≤ 252 properties.
  - `EightHundredKTokens_IsMultiTransaction` — 3,200,000 chars → `MultiTransaction`, about 7 parts, keys match `^rk~c\d{6}~[0-9a-f]{16}$`, primary has no `__c` cells, `BatchPlanner.Plan(sizes of parts)` returns more than 1 range.
  - `TwentyMegabyteBinary_IsMultiTransaction` and round-trips through `Assemble`.
  - `Generation_IsContentDerived` — same content → same generation; one char changed → different.
  - `Assemble_IgnoresStaleGenerationParts` — mix gen A and gen B parts, primary says B → value of B.
  - `Assemble_MissingPart_Throws` — drop part 2 → `TableValueIntegrityException`.
  - `ValidateRowKey_RejectsTilde_AndOverlongKeys`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SplitRowLayoutTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** `SplitRowLayout` (pure, no I/O).
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(split): plan split values onto continuation and chunk rows"`

---

### Task 4: `TableStorage<T>` — single-row and single-transaction writes, all reads

**Files:**
- Modify: `src/Unified.Data.Tables/TableStorage.cs:23` (make `partial`), `CreateAsync` `:111-158`, `UpdateAsync` `:766-835`, `UpsertAsync` `:837-876`, `WriteBatchAsync` `:542-622`, `OneAsync` `:218-247`, `ExistsAsync` `:250-278`, `QueryAsync(partition)` `:281-321`, `StreamAsync` `:416-432`, `QueryPageAsync` `:435-469`, `CountAsync` `:520-539`, `DeleteAsync` `:161-176`, `DeletePartitionAsync` `:179-215`, `BuildSelect`/`SystemColumns` `:399-412`
- Create: `src/Unified.Data.Tables/TableStorage.Split.cs`
- Test: `tests/Unified.Data.Tables.Tests/Split/TableStorageSplitTests.cs` (uses `StorageHarness<T>`, `tests/.../TestSupport/StorageHarness.cs`)

**Interfaces:**
- Consumes: Tasks 2-3.
- Produces (private to `TableStorage<T>`, used by Tasks 5 and 7):
  - `private bool SplitAware => TypeMetadataCache.IsSplitAware(typeof(T));` — every new branch is behind this flag, except the cheap `IsPartRow` skip on reads.
  - `private async Task<ETag> WriteSplitAsync(TableEntity logical, SplitWriteKind kind, ETag ifMatch, CancellationToken ct)` with `private enum SplitWriteKind { Create, Upsert, Replace }`.
  - `private async Task<TableEntity?> AssembleAsync(TableEntity primary, CancellationToken ct)` — no parts → `primary`; else one range query `PartitionKey eq pk and RowKey ge PartRangeLower and RowKey lt PartRangeUpper and _SplitOf eq rk and _SplitGen eq gen`, then `SplitRowLayout.Assemble`.
  - `private async Task DeleteSplitAsync(string pk, string rk, CancellationToken ct)`.

Write rules (SingleRow and SingleTransaction here; MultiTransaction is Task 5 and throws `NotImplementedException` from a single private switch arm until then):
- Split-aware writes call `SplitRowLayout.ValidateRowKey(rowKey)` first.
- `SingleRow` → the existing SDK call (`AddEntityAsync` / `UpsertEntityAsync` / `UpdateEntityAsync`), unchanged.
- `SingleTransaction` → one `SubmitTransactionAsync`: primary as `Add` (create) / `UpsertReplace` (upsert) / `UpdateReplace` with the caller's ETag (replace); parts as `UpsertReplace`. A transaction failure whose `FailedTransactionActionIndex` is the primary maps 409 → `DuplicateKeyException`, 412 → `ConcurrencyConflictException` (same messages as today's single-row paths). ETag for the caller: re-read the primary's ETag with a keys-only `GetEntityIfExistsAsync` (sub-responses are not correlated, as `WriteBatchAsync` already notes at `:561-565`).
- Batch writes: entities planning to `SingleRow` keep the existing planner path; the rest are written one by one through `WriteSplitAsync` afterwards. Document that this keeps "atomic per chunk only".
- Delete: split-aware `DeleteAsync` reads the primary's `_Split*` columns; `SingleTransaction` deletes primary + parts in one transaction; no parts → today's call.
- `DeletePartitionAsync` still deletes every row; its return value counts only rows without `_SplitOf` when split-aware (select adds `_SplitOf`).
- Reads: every path skips rows where `IsPartRow`, before counting toward `take` and before caching; split-aware paths `await AssembleAsync` before `FromTableEntity<T>`. `OneAsync`/`ExistsAsync` on a part row's id return `null`/`false`.
- Projection: `SystemColumns` gains the five `_Split*` names. If `QueryOptions.Select` names a property in `SplitEncodings`, `BuildSelect` returns `null` (full row). Rows read with a projection that omits every split property are not assembled.
- `CountAsync` selects `PartitionKey` and `_SplitOf` when split-aware and counts rows without `_SplitOf`.

- [ ] **Step 1: Write the failing tests** (mock `Table.SubmitTransactionAsync`, `Table.QueryAsync<TableEntity>` with `Mocks.Pageable`, capture actions):
  - `Create_SmallValue_UsesAddEntity_NoTransaction` — `AddEntityAsync` received once, `SubmitTransactionAsync` never.
  - `Create_TwoEntityValue_IsOneTransaction_PrimaryAddPartsUpsert` — captured actions: index 0 `Add` with RowKey `rk`, index 1 `UpsertReplace` with `rk~0001`.
  - `Create_ExistingKey_InTransaction_ThrowsDuplicateKey` — transaction failure index 0, status 409.
  - `Update_WithStaleETag_InTransaction_ThrowsConcurrencyConflict` — index 0, status 412.
  - `Create_RowKeyWithTilde_IsRefusedBeforeAnyCall` (Review Focus 1) — `ArgumentException`; `Table.ReceivedCalls()` contains no write.
  - `OneAsync_AssemblesParts_AndVerifiesHash` — primary + part rows from the mock → `Body` equals original.
  - `OneAsync_OnPartRowId_ReturnsNull`.
  - `OneAsync_SecondCallFromCache_ReturnsSameValue` — no second SDK read; values equal.
  - `Upsert_ShrinkToOneCell_StalePartsIgnored` (Review Focus 2) — primary without `_SplitParts` plus a leftover `rk~0001` of an older gen in the partition → read returns the short value; `QueryAsync(partition)` yields exactly 1 entity.
  - `Query_Take_CountsLogicalEntitiesOnly` (Review Focus 4) — partition rows `a, a~0001, b, b~0001, c`, `Take = 2` → ids `a`, `b`.
  - `QueryPage_SkipsPartRows_AndCursorReachesAll` — two server pages split across a part row → union of pages = `a, b, c`.
  - `Projection_NamingSplitProperty_FetchesWholeRow` and `Projection_OmittingSplitProperty_DoesNotFetchParts` (Review Focus 3) — select passed to the SDK is `null` in the first; no part range query in the second.
  - `Count_ExcludesPartRows` and `DeletePartition_ReturnsLogicalCount`.
  - `Delete_SingleTransactionValue_DeletesPrimaryAndPartsTogether` — one transaction, `Delete` actions for `rk` and `rk~0001`.
  - `PlainEntity_ReadAndWritePaths_AreUnchanged` — the SDK calls received for a `TestEntity` create/read/count match today's (no extra query, no `_SplitOf` in select).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TableStorageSplitTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the rules above in `TableStorage.Split.cs`, with minimal call-site branches in `TableStorage.cs`.
- [ ] **Step 4: Run** the filter, then the whole test project. Expected: PASS; `TableStorageTests`, `TableStorageQueryTests`, `PagingTests`, `ConcurrencyTests` unchanged and green.
- [ ] **Step 5: Commit** — `git commit -m "feat(split): TableStorage writes split values in one transaction and hides part rows on read"`

---

### Task 5: Multi-transaction commit-row mode

**Files:**
- Modify: `src/Unified.Data.Tables/TableStorage.Split.cs`
- Test: `tests/Unified.Data.Tables.Tests/Split/TableStorageMultiTransactionTests.cs`

**Interfaces:**
- Consumes: `WriteSplitAsync`, `DeleteSplitAsync` from Task 4; `SplitRowPlan.Generation`.
- Produces: the `MultiTransaction` arm of `WriteSplitAsync` and `DeleteSplitAsync`; `private async Task DeleteGenerationAsync(string pk, string rk, string generation, CancellationToken ct)` (range query filtered by `_SplitOf eq rk and _SplitGen eq generation`, deleted in `BatchPlanner`-sized transactions; reused by Task 7).

Algorithm (fixed by the spec, §5.5):
1. Chunk rows: `UpsertReplace` in transactions planned by `BatchPlanner.Plan(parts.Select(TableRowSize.Estimate))`, in index order. Cancellation between transactions propagates; nothing is committed.
2. Create: `AddEntityAsync(primary)` last. On 409: read the existing primary's `_SplitGen`; if it differs from ours, best-effort `DeleteGenerationAsync(ours)`; throw `DuplicateKeyException`.
3. Replace / Upsert: read the current primary (`_SplitGen`, `_SplitMode`, `_SplitParts`, ETag) **before** step 1 to learn `oldGen`. Swap with `UpdateEntityAsync(primary, ifMatch, TableUpdateMode.Replace)` — `ifMatch` is the caller's ETag for `Replace`, the ETag just read for `Upsert` (missing primary → `AddEntityAsync`). On success and `oldGen != newGen`, `DeleteGenerationAsync(oldGen)` (old SingleTransaction parts are deleted by `_SplitOf` range the same way). On 412: re-read the primary; if its `_SplitGen != newGen`, best-effort `DeleteGenerationAsync(newGen)`; throw `ConcurrencyConflictException`. Upsert treats its own 412 the same (a concurrent writer won; the last-writer-wins contract is met by the winner).
4. Delete: delete the primary first (`DeleteEntityAsync`), then `DeleteGenerationAsync(gen)`. A crash in between leaves only orphan chunks, which reads never see and the sweep removes.

- [ ] **Step 1: Write the failing tests** (mocked SDK, capture call order with `Received.InOrder`):
  - `Create_MultiTransaction_WritesChunksFirst_CommitRowLast` — ≥ 2 `SubmitTransactionAsync` (all `UpsertReplace`, keys `~c…~gen`) then exactly one `AddEntityAsync` for `rk`.
  - `Create_CancelledBetweenTransactions_NeverWritesCommitRow` — token cancelled after the first transaction → `OperationCanceledException`; `AddEntityAsync` not received.
  - `Create_SameContentTwice_ReusesChunkKeys` — the two calls' captured chunk RowKeys are equal (content-derived generation); second throws `DuplicateKeyException` and issues no chunk delete.
  - `Replace_SwapsWithIfMatch_ThenDeletesOldGeneration` — order: new-gen upserts → `UpdateEntityAsync(…, callerETag, Replace)` → deletes only of old-gen keys.
  - `Replace_LosesRace_CleansOwnGeneration_AndThrowsConflict` — `UpdateEntityAsync` throws 412; re-read shows another gen → deletes of our gen; `ConcurrencyConflictException`.
  - `Replace_LosesRaceToIdenticalContent_DoesNotDeleteWinnersChunks` — re-read shows our gen → no deletes.
  - `Delete_MultiTransaction_DeletesCommitRowBeforeChunks`.
  - `Read_CommitRowMissing_ChunksInvisible` — partition with only `~c…` rows → `QueryAsync` empty, `OneAsync(rk)` null.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TableStorageMultiTransactionTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the algorithm.
- [ ] **Step 4: Run** the filter and Task 4's filter. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(split): commit-row mode for values larger than one transaction"`

---

### Task 6: `InMemoryStorage<T>` parity

**Files:**
- Modify: `src/Unified.Data.Tables.InMemory/InMemoryStorage.cs` — `Store` `:616-624`, `Materialize` `:626-634`, `Count` `:79`, `Snapshot` `:100`, `OneAsync` `:265`, `ExistsAsync` `:281`, query paths `:297-437`, `CountAsync` `:489-499`, `DeleteAsync` `:502-518`, `DeletePartitionAsync` `:521-535`, `WriteBatch` `:538`, `UpdateAsync` `:168-215`
- Modify: `src/Unified.Data.Tables.InMemory/Unified.Data.Tables.InMemory.csproj` (add `<InternalsVisibleTo Include="Unified.Data.Tables.Tests" />`)
- Test: `tests/Unified.Data.Tables.Tests/Split/InMemorySplitParityTests.cs`

**Interfaces:**
- Consumes: `SplitRowLayout` (public, Task 3).
- Produces: `internal void SeedPhysicalRow(TableEntity row, DateTimeOffset timestamp)` on `InMemoryStorage<T>` — test-only, inserts a raw row (used to model crashed writers in Task 7).

Rules: `Store` runs `SplitRowLayout.Plan` on the serialized row and stores primary + parts under `gate`, removing the primary's previous-generation parts in the same lock (the fake's writes are atomic, which is the post-condition Azure reaches after a successful write). Reads skip part rows and assemble through `SplitRowLayout.Assemble`. `Count`, `Snapshot`, `CountAsync`, `DeletePartitionAsync`'s return value count logical rows. `ValidateRowKey` applies to split-aware types. Concurrency (`ConcurrencyConflictException`, `DuplicateKeyException`) behaves as today, keyed on the primary.

- [ ] **Step 1: Write the failing tests** — one `[Theory]` per behaviour over sizes `{ 100, 32_001, 600_000, 3_200_000 }` chars (Text) and `{ 70_000, 20_000_000 }` bytes:
  - `RoundTrip_Create_One_Query` — equal values; `QueryAsync(partition)` count 1; `Count == 1`.
  - `Upsert_Shrink_LeavesNoVisibleParts` — `Snapshot().Count == 1`, value equals the short one.
  - `ConcurrentReplace_OneWins_ValueIntact` — two `UpdateAsync` with the same ETag → one `ConcurrencyConflictException`; read equals the winner.
  - `Delete_RemovesPrimaryAndParts` — `Count == 0` and no raw rows remain.
  - `RowKeyWithTilde_IsRefused`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~InMemorySplitParityTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter and `InMemoryStorageTests`. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(split): InMemoryStorage stores split values as physical rows for parity"`

---

### Task 7: `SweepUncommittedAsync`

**Files:**
- Modify: `src/Unified.Data.Tables.Abstractions/IStorage.cs:190` (append member), `src/Unified.Data.Tables/TableStorage.Split.cs`, `src/Unified.Data.Tables.InMemory/InMemoryStorage.cs`
- Test: `tests/Unified.Data.Tables.Tests/Split/SweepUncommittedTests.cs`

**Interfaces:**
- Consumes: `DeleteGenerationAsync` (Task 5), `SeedPhysicalRow` (Task 6).
- Produces:

```csharp
/// Deletes part rows (continuation or chunk rows) in <paramref name="partitionKey"/> that no
/// committed row references — the primary is missing or carries another generation — and whose
/// Timestamp is older than <paramref name="olderThan"/>. Returns the number of rows deleted.
Task<int> SweepUncommittedAsync(string partitionKey, TimeSpan olderThan, CancellationToken ct = default);
```

  On `net10.0` the member has a default implementation that throws `NotSupportedException` (wrap in `#if NET`), so
  existing `net10.0` implementers keep compiling; `netstandard2.0` cannot express a default implementation and
  implementers there must add it (CHANGELOG "Changed").

Rules: argument checks (`partitionKey` blank → `ArgumentNullException`, `olderThan < TimeSpan.Zero` →
`ArgumentOutOfRangeException`); normalize the partition like `DeletePartitionAsync`. One query
`PartitionKey eq pk and _SplitOf ge ''` selecting `RowKey`, `_SplitOf`, `_SplitGen`, `Timestamp`; group by
`(_SplitOf, _SplitGen)`; for each group read the primary's `_SplitGen` once; delete the group only when it is
unreferenced **and every row in it** is older than `UtcNow - olderThan`. Never touches primaries. Invalidates the
query cache for the partition when it deletes anything.

- [ ] **Step 1: Write the failing tests** (TableStorage with mocks, and InMemory with `SeedPhysicalRow`):
  - `Sweep_CrashBeforeCommit_DeletesOrphanChunks_AfterGrace` — chunk rows aged 10 min, no primary, `olderThan` 5 min → returns their count; rows gone.
  - `Sweep_InFlightWrite_WithinGrace_IsKept` — chunks aged 10 s → returns 0.
  - `Sweep_KeepsCommittedGeneration_DeletesStaleOne` — primary gen B; parts of gen A (old) and B → only A deleted.
  - `Sweep_NeverDeletesPrimaries_OrUnrelatedRows` — a plain entity whose RowKey sorts inside the range survives.
  - `Sweep_NegativeOlderThan_Throws`, `Sweep_BlankPartition_Throws`.
  - `Sweep_PlainImplementer_DefaultThrowsNotSupported` — a minimal `IStorage<T>` test double without the member → `NotSupportedException`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SweepUncommittedTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** in both stores.
- [ ] **Step 4: Run** the filter and the whole test project. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(split): SweepUncommittedAsync removes chunk rows no commit row references"`

---

### Task 8: Azurite integration

**Files:**
- Test: `tests/Unified.Data.Tables.Tests/Split/AzuriteSplitIntegrationTests.cs` (same probe/skip/cleanup pattern as `AzuriteTableStorageIntegrationTests.cs:20-62`; each test uses its own partition `split-{Guid:N}` and cleans it with `DeletePartitionAsync`)

**Interfaces:** Consumes everything above through `TableStorage<T>` against `UseDevelopmentStorage=true`.

- [ ] **Step 1: Write the tests** — each `Assert.SkipUnless(_available, …)`:
  - `[Theory]` `TextBoundaries_RoundTrip` over `{ 32_000, 32_001, 254_000, 600_000, 3_200_000 }` chars (1 cell, 2 cells, 1 entity, 2 entities / 1 transaction, 800K tokens / multi-transaction) — equal by ordinal comparison; raw `TableClient` query shows the expected number of physical rows and that chunk cells are `string`.
  - `TextWithSurrogatePairsAtEveryBoundary_RoundTrips` — emoji placed at each `k * 32_000 - 1`.
  - `TwentyMegabyteBinary_RoundTrips` — `byte[]` 20,000,000 random bytes, raw cells are `byte[]`.
  - `GzipEncoding_RoundTrips` — 1,000,000 random chars.
  - `CorruptChunk_Throws` — overwrite one chunk cell via raw `TableClient.UpdateEntityAsync` → `OneAsync` throws `TableValueIntegrityException`.
  - `MissingChunk_Throws` — delete one chunk row via raw client → throws.
  - `CrashBeforeCommit_ThenSweep` — write chunk rows of a 3,200,000-char value with a token cancelled before commit (use a `CancellationTokenSource` cancelled from a `TableClient` policy hook, or write the planned parts directly with `SplitRowLayout.Plan` + raw `SubmitTransactionAsync`); `QueryAsync` empty; `SweepUncommittedAsync(pk, TimeSpan.Zero)` deletes all; a raw query finds nothing.
  - `ConcurrentReplace_OneWins_NoOrphansAfterSweep` — two `UpdateAsync` with the same ETag via `Task.WhenAll` → exactly one `ConcurrencyConflictException`; read equals winner; `SweepUncommittedAsync(pk, TimeSpan.Zero)` then a raw query shows only the winner's rows.
  - `SweepFilter_SplitOfGeEmpty_MatchesOnlyPartRows` — confirms Azurite evaluates the property-existence filter as Azure does (a plain row without `_SplitOf` is not returned).
- [ ] **Step 2: Start Azurite** (`npx azurite --silent --location <scratch dir>`, table endpoint 10002) and **run** `--filter "FullyQualifiedName~AzuriteSplitIntegrationTests"`. Expected: PASS, none skipped. Then run once with Azurite stopped. Expected: all skipped, none failed.
- [ ] **Step 3: Fix** any provider difference found in the store code, not by weakening the test. Re-run Tasks 4-7 filters.
- [ ] **Step 4: Commit** — `git commit -m "test(split): Azurite round-trips at every split boundary, integrity and sweep"`

---

### Task 9: Documentation and 0.9.0

**Files:**
- Modify: `README.md` — Features list (`:20`), new section `## Large values: lossless split` after `## Serialization` (`:476-505`), `IStorage<T>` table (`:165`) gains `SweepUncommittedAsync`
- Modify: `CHANGELOG.md` — new `## [0.9.0] — <release date>` above `:7`
- Modify: `Directory.Build.props:35` — `<Version>0.9.0</Version>`
- Modify: `src/Unified.Data.Tables/UnifiedTableStorageOptions.cs:19-24` — the `OversizedCells` doc comment names `Split`

- [ ] **Step 1: Write the README section**, covering: opt-in (`[TableValueSplit(SplitEncoding.Text)]` per property or class, or `OversizedCells = OversizedCellPolicy.Split` globally); Text vs Gzip (Text is readable in Storage Explorer, Gzip is smaller; `byte[]` is always raw binary); the three physical layouts and their row-key shapes; the generation/commit-row guarantees and the replace/delete ordering; `SweepUncommittedAsync` and why it needs a grace period; that `~` is reserved in RowKeys of split-aware tables; limitations (no LINQ filter on a split property, no partial update of one, not supported by `IPolymorphicStorage`, batch writes of split entities are per entity); and consider `CacheFor<T>(…)` to disable caching for types holding very large values.
- [ ] **Step 2: Write the CHANGELOG entry**: **Added** (policy, attribute, encodings, layouts, `SweepUncommittedAsync`, `TableValueIntegrityException`, `SplitRowLayout`); **Changed** (`IStorage<T>` gains a member — default implementation on `net10.0`, required for `netstandard2.0` implementers); **Unchanged** (types that do not opt in: same columns, same SDK calls).
- [ ] **Step 3: Run** `dotnet build Unified.Data.Tables.slnx -c Release` and `dotnet test Unified.Data.Tables.slnx -c Release` (Azurite running). Expected: 0 warnings, 0 failures; `dotnet pack Unified.Data.Tables.slnx -c Release -o <scratch dir>` produces `*.0.9.0.nupkg` for all four packages.
- [ ] **Step 4: Commit** — `git commit -m "docs: lossless value split; release 0.9.0"`
- [ ] **Step 5: Hand-off** — push `feat/lossless-value-split` and open a PR to `main` only when the user asks; merge, release (`publish.yml`, version `0.9.0`) and the IntelliSpec upgrade (P1) are separate, user-authorised steps.

---

## Canon issues

1. **`IStorage<T>.SweepUncommittedAsync` breaks existing implementers.** IntelliSpec has its own `IStorage<T>` test
   doubles: `dotnet-agents.Tests/TestSupport/InMemoryStorage.cs:15`, `dotnet-agents.Tests/TestSupport/InterceptingStorage.cs:11`,
   `dotnet-agents.Tests/Services/Learning/InstinctStoreBareIdResolutionTests.cs:127`,
   `dotnet-agents.Tests/Agents/Orchestration/PipelineOrchestrator/ReviewOnlyFollowUpChecks.cs:426`. The `net10.0`
   default implementation keeps them compiling, but `InterceptingStorage` will *not* forward the call to its inner
   store unless P1 adds the member there.
2. **`TableValueIntegrityException` lives in `Unified.Data.Tables.Abstractions`** (resolved by the completeness
   review). IntelliSpec's `IntelliSpec.Data` project, where the P6 export reader catches it, references only the
   Abstractions package (through `IntelliSpec.Abstractions`), and adding the main package there would be a new package
   reference. The exception is still thrown only by the main package and the in-memory provider.
3. **Chunk row key shape.** The spec gives `{RowKey}~c{n:D6}`; the canon requires a generation suffix. This plan uses
   `{RowKey}~c{n:D6}~{generation}` (16 hex chars, content-derived).
4. **`~` is reserved in RowKeys of split-aware tables.** P1's `LlmRecordKeys` RowKey `{keyKind}:{keyValue}:{ulid}`
   must escape `~` in `keyValue` (and any other RowKey-forbidden characters) or that table must not be split-aware.
   Resolved in P1: `LlmRecordKeyEntity` has no split property, and P1's `LlmRecordIds.EscapeKeyValue` percent-encodes
   `~`, `:`, `/`, `\`, `#`, `?`, `|`, `%`, space and control characters anyway.
5. **Doc mismatch, not changed by this plan.** Spec §4 and the doc comment at `UnifiedTableStorageOptions.cs:19-21`
   say the default policy trims; the code default is `Throw` (`UnifiedTableStorageOptions.cs:24`,
   `TableEntitySerializer.cs:41`). Task 9 corrects the doc comment only.

## Open risks

- Read cost: assembling a split entity is one extra range query per entity (N+1 on large queries). Acceptable for
  record-at-a-time readers; README says so.
- Azurite's handling of the `_SplitOf ge ''` existence filter and of 20 MB multi-transaction writes may differ from
  Azure; Task 8 checks both, but production behaviour is only proven after release.
- Concurrent multi-transaction **upserts** (no caller ETag) both write chunks before one wins; the loser's chunks are
  orphaned until a sweep runs. Callers using upsert on split types must schedule the sweep.
- The entity size budget (1,000,000 estimated bytes from `TableRowSize.Estimate`) relies on that estimate staying
  conservative for UTF-8 wire size; Task 8's 600,000-char and 20 MB cases exercise it against Azurite only.
