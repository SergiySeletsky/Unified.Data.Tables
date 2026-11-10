# Lossless Large Values (Split + Blob Offload) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An opt-in `[TableValueSplit]` attribute and a `TableBlobContent` value type that store a property value of any
size losslessly: an inline cell, split cells plus continuation rows in **one** entity-group transaction, or (above a
threshold or beyond one transaction) a blob in the same storage account that the row references. Callers only ever
use `IStorage<T>`. Ships as 0.9.0.

**Architecture:** Four pure layers and one I/O seam. (1) `SplitCodec` turns one value into chunk cells plus a manifest
and back. (2) The serializer, given an internal `ValueWriteContext`, picks the tier per opted-in property: inline,
split cells, or a `{Prop}__blob` reference cell plus a `PendingBlob`. (3) `SplitRowLayout` spreads a logical bag over
a primary row plus continuation rows and reports whether it fits one transaction; `ValueOffloadCoordinator`
re-serializes with the largest split groups forced to blobs until it does. (4) `IOffloadBlobStore` is the only blob
seam: `AzureOffloadBlobStore` (Azure.Storage.Blobs) in the main package and `InMemoryOffloadBlobStore` in the
InMemory package. `TableStorage<T>` and `InMemoryStorage<T>` share the coordinator, so tiers, ordering, integrity and
sweep match. A type with no `[TableValueSplit]` and no `TableBlobContent` property takes none of the new branches.

**Tech Stack:** C# (`LangVersion=latest`), `net10.0` (Abstractions also `netstandard2.0`), Azure.Data.Tables 12.11.0,
**Azure.Storage.Blobs (new reference)**, xUnit v3 with plain `Assert`, NSubstitute, Azurite (tables 10002, blobs 10000).

**Spec:** `D:\github\intellispec\docs\superpowers\specs\2026-11-10-llm-call-recorder-design.md`, §5.5 "Prerequisite:
lossless oversized values in Unified.Data.Tables 0.9.0", user decisions 9, 10 and 11 (§2), and §5.11 (uploaded files).

**Branch / worktree:** library repo `D:\github\Unified.Data.Tables`, branch `feat/lossless-value-split` off `main`
(worktree, if used, at `D:\github\Unified.Data.Tables-lossless-value-split`; D: had 195 GB free on 2026-11-10).
Merging, tagging and running `.github/workflows/publish.yml` are the user's actions, not the executor's.

## Global Constraints

- The PR contains nothing specific to IntelliSpec: no recorder names, table names, vision ids or LLM terms in code,
  tests, README or CHANGELOG.
- No behaviour change for existing users. A type with no `[TableValueSplit]` and no `TableBlobContent` property
  produces the same columns and the same SDK calls as 0.8.3, with `BlobOffload` set or not. The global
  `OversizedCellPolicy` is unchanged and gains no value.
- Tiers per opted-in property, in order: (1) inline cell if it fits; (2) split cells `{Prop}__c0..cN` and continuation
  rows `{RowKey}~0001..` written in **one** entity-group transaction; (3) blob offload when the encoded value exceeds
  `BlobOffloadOptions.ThresholdBytes` (default `3_145_728`) or the row would not fit one transaction. Without offload
  configured (or with `Offload = BlobOffload.Never`), a value that needs tier 3 throws `OversizedValueException`.
  Nothing ever trims.
- `TableBlobContent` is always offloaded, whatever its size. Reading an entity never downloads it.
- Encodings: `SplitEncoding.Text` (string cells of at most **32,000 chars**, never splitting a surrogate pair) and
  `SplitEncoding.Gzip` (binary cells). `byte[]` is always raw binary cells (at most **65,536 bytes** each), never
  base64 strings.
- Cell names: `{Prop}__c0..cN`, `{Prop}__chunks` (int), `{Prop}__sha256` (lower hex), `{Prop}__enc`, `{Prop}__len`
  (long), `{Prop}__blob` (reference JSON). `__enc` values: `text`, `gzip`, `binary`, `utf16le`.
- Hash: SHA-256, lower hex, over the **stored bytes** — UTF-8 of the string for `text`, the gzip bytes for `gzip`,
  the raw bytes for `binary`, UTF-16LE for `utf16le`. A split value and the same value offloaded have the same hash.
- Blob name: `{table}/{partitionKey}/{rowKey}/{property}/{sha256}`, each segment `Uri.EscapeDataString`-escaped;
  `{table}` is the store's resolved table name; `{property}` is the flattened column base name (`Inner_Body`). Text is
  stored as UTF-8 with `Content-Type: text/plain; charset=utf-8`; `Gzip` blobs hold gzip bytes with
  `Content-Encoding: gzip`; binary is stored as is. Blob metadata: `sha256`, `length`. Reference cell JSON:
  `{"path","sha256","length","contentType","encoding"}` (`length` = stored bytes).
- Write order: blob upload create-only **first** (an existing blob with the same name and matching `sha256` is
  success), then the entity write. Replace: new blob, entity swap with ETag `If-Match`, then best-effort delete of old
  blobs the new row no longer references. Delete: entity first, then its blobs. **A failed entity write never deletes
  a blob**; `SweepOrphanBlobsAsync` does.
- Reads: `string`/`byte[]`/JSON-fallback values are read eagerly (reassemble or fetch, then verify SHA-256).
  `TableBlobContent` is lazy and verifies when its stream reaches the end. A mismatch, missing chunk, missing part or
  missing blob throws `TableValueIntegrityException`; partial data is never returned.
- One transaction = `BatchPlanner.DefaultMaxCount` (100) actions and `BatchPlanner.DefaultMaxBytes` (3,000,000 B,
  measured with `TableRowSize.Estimate`, `src/Unified.Data.Tables/TableRowSize.cs:16`). One entity = at most 252
  custom properties and 1,000,000 estimated bytes.
- `IStorage<T>.SweepOrphanBlobsAsync(string partitionKey, TimeSpan olderThan, CancellationToken ct = default)` returns
  `Task<int>`. There is no `SweepUncommittedAsync`, no commit row, no chunk rows and no generation column.
- Version `0.9.0` in `Directory.Build.props:35`. The only package change is the new `Azure.Storage.Blobs` reference in
  `src/Unified.Data.Tables/Unified.Data.Tables.csproj`; no existing reference version changes.
- `TreatWarningsAsErrors`, analyzers `Recommended` (`Directory.Build.props:17-27`); XML docs on every public member.
- Tests that call `AddUnifiedTableStorage`/`AddUnifiedInMemoryStorage` or set `TableEntitySerializer.OversizedCellPolicy`
  join `[Collection("OversizedCellPolicy")]` (`tests/Unified.Data.Tables.Tests/Fixes053Tests.cs:21`).
  New tests live in `tests/Unified.Data.Tables.Tests/LargeValues/`, namespace `Unified.Data.Tables.Tests.LargeValues`.
- Build: `dotnet build Unified.Data.Tables.slnx -c Release`. Focused test:
  `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj -c Release --filter "FullyQualifiedName~<Class>"`.
  Never pass `-o`.

## Review Focus

1. **A string that is not well-formed UTF-16** (an unpaired surrogate, which .NET strings allow). UTF-8 would replace
   it with U+FFFD and the Tables wire JSON rejects it, so it would silently change or fail. Expected: such a value
   ignores the requested encoding and is stored as `utf16le` binary cells or a `text/plain; charset=utf-16le` blob,
   and reads back code unit for code unit. Tests in Task 1 and Task 9.
2. **The same content written again to the same row** (a retried create, an idempotent upsert, a replace with an
   unchanged file) maps to the same blob name. Expected: no re-upload of the bytes, no integrity error, and the old
   blob is **not** deleted after the swap because the new row references it. Tests in Task 6.
3. **An orphan sweep racing a write that reuses an old orphan blob.** Expected: the write refreshes the blob's
   `Last-Modified` (metadata touch) before the entity write, and the sweep deletes with `If-Unmodified-Since` set to the
   listed time, so a newly referenced blob is never deleted. Tests in Task 6 and Task 8.
4. **Upsert or replace that shrinks a split value** (fewer continuation rows, or back to one cell, or from split to a
   blob). Expected: the stale continuation rows are deleted in the same transaction; reads and `CountAsync` never see
   them. Test in Task 4.
5. **A projection (`QueryOptions.Select`)** that names a split or offloaded property returns the whole value; one that
   omits it neither reads continuation rows nor touches the blob store. Test in Task 4 and Task 6.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Unified.Data.Tables.Abstractions/SplitEncoding.cs` (create) | `enum SplitEncoding { Text, Gzip }` |
| `src/Unified.Data.Tables.Abstractions/BlobOffload.cs` (create) | `enum BlobOffload { AboveThreshold, Never }` |
| `src/Unified.Data.Tables.Abstractions/TableValueSplitAttribute.cs` (create) | Opt-in per property or class |
| `src/Unified.Data.Tables.Abstractions/TableBlobContent.cs` (create) | Always-offloaded file value; lazy read |
| `src/Unified.Data.Tables.Abstractions/TableValueIntegrityException.cs`, `OversizedValueException.cs` (create) | Read and write failures, catchable from Abstractions-only consumers |
| `src/Unified.Data.Tables.Abstractions/SystemColumnNames.cs` (modify `:24-39`) | `_SplitOf`, `_SplitIndex`, `_SplitParts` |
| `src/Unified.Data.Tables.Abstractions/IStorage.cs` (modify `:190`) | `SweepOrphanBlobsAsync` |
| `src/Unified.Data.Tables/SplitCodec.cs` (create, internal) | One value ↔ chunk cells + manifest; well-formedness; hash |
| `src/Unified.Data.Tables/TableEntitySerializer.cs` (modify) | Tier choice, split cells, reference cells, read contexts |
| `src/Unified.Data.Tables/ValueContexts.cs` (create, internal) | `ValueWriteContext`, `ValueReadContext`, `PendingBlob`, `BlobReference` |
| `src/Unified.Data.Tables/SplitRowLayout.cs` (create, internal) | Logical bag ↔ primary + continuation rows; transaction fit |
| `src/Unified.Data.Tables/BlobOffloadOptions.cs` (create), `UnifiedTableStorageOptions.cs` (modify) | Offload configuration |
| `src/Unified.Data.Tables/IOffloadBlobStore.cs`, `AzureOffloadBlobStore.cs` (create, internal) | Blob seam and its Azure implementation |
| `src/Unified.Data.Tables/ValueOffloadCoordinator.cs` (create, internal) | Prepare streams, serialize with tier fallback, upload, resolve, sweep core |
| `src/Unified.Data.Tables/TableStorage.cs` (modify → `partial`) + `TableStorage.LargeValues.cs` (create) | Split-aware write/read/delete/sweep |
| `src/Unified.Data.Tables/TableInitializer.cs` (modify) | Reused for lazy container creation |
| `src/Unified.Data.Tables/ServiceCollectionExtensions.cs` (modify) | `BlobServiceClient` registration |
| `src/Unified.Data.Tables/PolymorphicTableStorage.cs`, `TableFilterTranslator.cs` (modify) | Refuse split and offloaded members |
| `src/Unified.Data.Tables/Unified.Data.Tables.csproj` (modify) | `Azure.Storage.Blobs`; `InternalsVisibleTo Unified.Data.Tables.InMemory` |
| `src/Unified.Data.Tables.InMemory/InMemoryOffloadBlobStore.cs` (create), `InMemoryStorage.cs`, `InMemoryPolymorphicStorage.cs`, `.csproj` (modify) | Parity |
| `tests/Unified.Data.Tables.Tests/LargeValues/*.cs` (create) | All new tests; `LargeValueModels.cs` holds fixtures, `RecordingBlobStore.cs` the ordered call log |
| `README.md`, `CHANGELOG.md`, `Directory.Build.props` (modify) | Docs and 0.9.0 |

---

### Task 1: Public surface and `SplitCodec`

**Files:**
- Create: `src/Unified.Data.Tables.Abstractions/SplitEncoding.cs`, `BlobOffload.cs`, `TableValueSplitAttribute.cs`, `TableValueIntegrityException.cs`, `OversizedValueException.cs`
- Modify: `src/Unified.Data.Tables.Abstractions/SystemColumnNames.cs:24-39`
- Create: `src/Unified.Data.Tables/SplitCodec.cs`
- Test: `tests/Unified.Data.Tables.Tests/LargeValues/SplitCodecTests.cs`

**Interfaces:**
- Produces:
  - `public enum SplitEncoding { Text, Gzip }`; `public enum BlobOffload { AboveThreshold, Never }` (`AboveThreshold` = 0, the default).
  - `[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, Inherited = true)] public sealed class TableValueSplitAttribute(SplitEncoding encoding) : Attribute { public SplitEncoding Encoding { get; } public BlobOffload Offload { get; set; } }`
  - `public sealed class TableValueIntegrityException : SerializationException` — ctor `(string columnName, string reason)`, properties `ColumnName`, `Reason`.
  - `public sealed class OversizedValueException : SerializationException` — ctor `(string columnName, long encodedBytes, string reason)`, properties `ColumnName`, `EncodedBytes`, `Reason`; message names `UnifiedTableStorageOptions.BlobOffload` as the fix (or `BlobOffload.AboveThreshold` when the property said `Never`).
    Both exceptions live in **Abstractions** (namespace `Unified.Data.Tables`; `SerializationException` exists on
    `netstandard2.0`) and derive from `SerializationException` for the reason given at `OversizedCellException.cs:9-15`.
  - `SystemColumnNames.SplitOf = "_SplitOf"`, `SplitIndex = "_SplitIndex"`, `SplitParts = "_SplitParts"`.
  - `internal static class SplitCodec`:
    - `const int MaxTextCellChars = 32_000; const int MaxBinaryCellBytes = 65_536;`
    - `static bool IsWellFormedUtf16(string s)` — false on any unpaired surrogate.
    - `static StoredPayload ToStored(object payload, SplitEncoding encoding)` with `internal readonly record struct StoredPayload(byte[] Bytes, string Enc, string Sha256)` — `string` → `text` (UTF-8) or `gzip` (`GZipStream` over UTF-8); ill-formed `string` → `utf16le` whatever the encoding; `byte[]` → `binary` whatever the encoding.
    - `static object FromStored(string column, ReadOnlySpan<byte> bytes, string enc, string expectedSha256)` — verifies the hash, then returns `string` (`text`, `gzip`, `utf16le`) or `byte[]` (`binary`); throws `TableValueIntegrityException` on mismatch or unknown `enc`.
    - `static bool FitsOneCell(object payload, SplitEncoding encoding)` — well-formed `string` ≤ 32,000 chars under `Text`; `byte[]` ≤ 65,536 B.
    - `static IReadOnlyList<KeyValuePair<string, object>> Encode(string column, object payload, SplitEncoding encoding)` — chunk cells then `__chunks`, `__sha256`, `__enc`, `__len` (stored byte count). `text` chunks are `string` cells cut with `SafeCut`; every other `enc` uses `byte[]` cells of ≤ 65,536 B.
    - `static object Decode(string column, IReadOnlyDictionary<string, object> cells)` — throws `TableValueIntegrityException` on a missing chunk, wrong count, wrong length or hash mismatch.
    - `static int SafeCut(string s, int start, int maxChars)` — steps back one when the cut would separate a high surrogate from its low surrogate.
    - `static bool TryParseLargeValueColumn(string column, out string baseColumn, out LargeValueCellKind kind, out int index)` with `internal enum LargeValueCellKind { Chunk, Count, Sha256, Encoding, Length, BlobReference }`.

- [ ] **Step 1: Write the failing tests** in `SplitCodecTests`:
  - `Encode_Text_ProducesReadableStringCells_NoneLongerThan32000` — 70,001-char ASCII → 3 `string` chunks, each `Length <= 32_000`; `__chunks == 3`, `__enc == "text"`, `__len == 70_001L`.
  - `Encode_Text_NeverSplitsASurrogatePair` — `"\uD83D\uDE00"` at chars 31,999-32,000 → chunk 0 does not end with a high surrogate; `Decode` equals input (ordinal).
  - `IllFormedString_IsStoredAsUtf16le_AndRoundTripsExactly` (Review Focus 1) — lone `'\uD800'` at index 31,999 and at the end, both encodings → `__enc == "utf16le"`, chunks are `byte[]`, `Decode` equals input ordinally.
  - `Encode_Bytes_AlwaysBinaryCells` — 200,000 random bytes with either encoding → every chunk `byte[]` ≤ 65,536, `__enc == "binary"`.
  - `Encode_Gzip_StoresBinaryCells_NotBase64` — 300,000-char string → chunks `byte[]`, `__enc == "gzip"`, round-trips.
  - `Sha256_IsOverStoredBytes_SameForSplitAndStored` — `Encode(...)["Body__sha256"] == ToStored(...).Sha256` and equals `Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant()` for `text`.
  - `Decode_CorruptChunk_Throws`, `Decode_MissingChunk_Throws`, `Decode_WrongChunkCount_Throws` — `TableValueIntegrityException`, `ColumnName == "Body"`, never a partial string.
  - `FromStored_HashMismatch_Throws`.
  - `TryParseLargeValueColumn_RecognisesOnlyItsSuffixes` — `"Body__c12"` → Chunk/12; `"Parent_Child__sha256"` → base `"Parent_Child"`; `"Body__blob"` → BlobReference; `"Body__Json"`, `"Body__GZip"`, `"Body__Truncated"`, `"Body"` → false.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SplitCodecTests"`. Expected: FAIL (types not defined).
- [ ] **Step 3: Implement** the types above. `SplitCodec` is pure (no I/O, no static state).
- [ ] **Step 4: Run** the filter, then the full test project. Expected: new tests PASS; all existing tests PASS.
- [ ] **Step 5: Commit**

```bash
git add src/Unified.Data.Tables.Abstractions src/Unified.Data.Tables/SplitCodec.cs tests/Unified.Data.Tables.Tests/LargeValues/SplitCodecTests.cs
git commit -m "feat(values): add TableValueSplit, encodings, value exceptions and the lossless chunk codec"
```

---

### Task 2: Serializer writes and reassembles split cells

**Files:**
- Modify: `src/Unified.Data.Tables/TableEntitySerializer.cs` — `ToTableEntity` (`:44-58`), `Materialize` (`:175-219`), `FlattenProperty` (`:303-314`), `Flatten` (`:348-424`), `TableEntityValue.TrySerialize` (`:646-744`), `TypeMetadataCache.CreateMetadata` / `TypeMetadata` (`:973-995`, `:1042-1052`)
- Create: `src/Unified.Data.Tables/ValueContexts.cs`
- Modify: `src/Unified.Data.Tables/PolymorphicTableStorage.cs:305-315` (`ToRow`), `src/Unified.Data.Tables.InMemory/InMemoryPolymorphicStorage.cs:339-345` (`ToRow`), `src/Unified.Data.Tables/TableFilterTranslator.cs:248` (`ResolveColumn`), `src/Unified.Data.Tables/TableStorage.cs:681-687` (builder flatten)
- Create: `tests/Unified.Data.Tables.Tests/LargeValues/LargeValueModels.cs`
- Test: `tests/Unified.Data.Tables.Tests/LargeValues/SplitSerializerTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `TypeMetadata.SplitOptions : IReadOnlyDictionary<string, TableValueSplitAttribute>` (property name → attribute; a class-level attribute applies to every `string`/`byte[]`/JSON-fallback property without its own).
  - `internal static bool TypeMetadataCache.IsLargeValueAware(Type t)` — `t` or any flattened nested property type has a `[TableValueSplit]` or a `TableBlobContent` property (cycle-guarded). The global policy plays no part.
  - `internal static IReadOnlyList<string> TypeMetadataCache.LargeValueColumns(Type t)` — every flattened base column that can carry split or reference cells (used by Task 8's projection).
  - In `ValueContexts.cs`: `internal sealed record ValueWriteContext(BlobOffloadOptions? Offload, string? TableName, IReadOnlySet<string> ForceOffload, bool Clone)` with `static ValueWriteContext Public` (no offload, not clone) and `static ValueWriteContext ForClone` (Task 5 fills the offload fields); `internal sealed class ValueReadContext` (Task 5 fills it; `static ValueReadContext None`).
  - `internal static TableEntity TableEntitySerializer.ToTableEntity(object root, string pk, string rk, ValueWriteContext ctx, List<PendingBlob> pending)`; `internal static T FromTableEntity<T>(TableEntity row, ValueReadContext ctx)`. The public overloads call these with `Public` / `None`.
  - `internal static bool TableEntitySerializer.HasLargeValueCells(TableEntity row)`.
  - Fixtures: `SplitTextEntity : Entity { [TableValueSplit(SplitEncoding.Text)] string? Body; string? Title }`, `SplitGzipEntity`, `SplitBinaryEntity` (`[TableValueSplit(SplitEncoding.Text)] byte[]? Image`), `SplitJsonEntity` (`[TableValueSplit(SplitEncoding.Text)] List<string>? Items`), `SplitNestedEntity` (`Inner.Body` split), `NeverOffloadEntity` (`[TableValueSplit(SplitEncoding.Text, Offload = BlobOffload.Never)] string? Body`), `PlainEntity` (`string? Body`, no attribute).

Rules the implementer cannot infer:
- Opted-in property: a value that `FitsOneCell` → today's single column (`string`/`byte[]` raw, never `__GZip`; JSON text that fits → `__Json`). Otherwise → `SplitCodec.Encode`, payload = the string, the bytes, or the JSON text. `Text` never gzips. (Tier 3 is Task 5; with `ValueWriteContext.Public` there is no tier 3 and the bag is unbounded.)
- A property without the attribute takes today's path unchanged, including `OversizedCellPolicy`.
- `Materialize` skips every large-value column in its loop (before the `IsSystemColumn` check at `:205`), then per base column with `__chunks` calls `SplitCodec.Decode` and sets the property through `TableEntityValue(path, decoded, fmt)`, `fmt` = `Raw` for `string`/`byte[]` targets, `Json` otherwise.
- `FlattenProperty` and the builder flatten at `TableStorage.cs:681-687` throw `NotSupportedException("'{name}' is a large value; partial updates cannot write it — replace the entity instead.")` for a property in `SplitOptions` or of type `TableBlobContent`.
- Both polymorphic `ToRow` methods throw `NotSupportedException` when `HasLargeValueCells(row)`; `ResolveColumn` throws `NotSupportedException` for a member in `SplitOptions` or of type `TableBlobContent`.

- [ ] **Step 1: Write the failing tests** in `SplitSerializerTests`:
  - `SmallValue_OnOptedInProperty_IsAnOrdinaryColumn` — 100 chars → `Body` (string), no `Body__*`.
  - `TextValue_At32000_IsOneCell_At32001_IsTwoChunks` — both round-trip; 32,001 → `Body__c0`, `Body__c1`, `Body__chunks == 2`.
  - `TextSplit_NeverUsesGzip` — 254,000 chars → no `__GZip`, 8 `string` chunks.
  - `GzipSplit_RoundTrips`, `BinarySplit_RoundTrips` (1 MB), `JsonFallbackSplit_RoundTrips` (`Items` = 5,000 × 50-char strings), `NestedSplit_RoundTrips`.
  - `PlainEntity_BehaviourUnchanged` — 100,000-char low-entropy `Body` → `Body__GZip` exactly as 0.8.3; 100,000 random chars → `OversizedCellException` (default `Throw`).
  - `Materialize_CorruptChunk_Throws` — tamper `Body__c1` → `TableValueIntegrityException`.
  - `PolymorphicWrite_OfSplitValue_IsRefused`, `LinqFilter_OnSplitProperty_IsRefused`, `FlattenProperty_OfSplitProperty_IsRefused` — `NotSupportedException`.
  - `IsLargeValueAware_TrueForAttributeOrNestedAttribute_FalseForPlain`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SplitSerializerTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** `TrySerialize` gains `out IReadOnlyList<KeyValuePair<string, object>>? largeCells` and an optional `TableValueSplitAttribute?` the owning `Flatten` passes from `TypeMetadata.SplitOptions`.
- [ ] **Step 4: Run** the filter, then the whole project. Expected: PASS, with `Fixes053Tests`, `TableEntitySerializerSizeTests`, `LegacyCompatTests`, `ColumnAliasTests`, `LargePayloadTests` unchanged.
- [ ] **Step 5: Commit** — `git commit -m "feat(values): serializer writes and reassembles split cells for opted-in properties"`

---

### Task 3: `SplitRowLayout` — logical bag ↔ primary and continuation rows

**Files:**
- Create: `src/Unified.Data.Tables/SplitRowLayout.cs`
- Test: `tests/Unified.Data.Tables.Tests/LargeValues/SplitRowLayoutTests.cs`

**Interfaces:**
- Consumes: Task 1 constants and `SplitCodec.TryParseLargeValueColumn`; `TableRowSize.Estimate`; `BatchPlanner.Plan` (`src/Unified.Data.Tables.Abstractions/BatchPlanner.cs:63`).
- Produces (`internal`; the InMemory package sees it through the `InternalsVisibleTo` Task 5 adds):
  - `internal sealed record SplitRowPlan(TableEntity Primary, IReadOnlyList<TableEntity> Parts)`
  - `internal static class SplitRowLayout`
    - `const int MaxPropertiesPerRow = 252; const long MaxEntityBytes = 1_000_000; const int MaxRowKeyLength = 480;`
    - `static SplitRowPlan Plan(TableEntity logical)` — everything fits the primary → primary is the input unchanged, `Parts` empty, no `_Split*` columns. Otherwise ordinary, manifest and `__blob` cells stay on the primary; chunk cells fill the primary, then parts in column order, each ≤ 252 properties and ≤ 1,000,000 estimated bytes. Part keys `{RowKey}~{i:D4}` (1-based); the primary gets `_SplitParts` (int); each part gets `_SplitOf` (base RowKey) and `_SplitIndex`.
    - `static bool FitsOneTransaction(SplitRowPlan plan, int extraActions)` — `1 + Parts.Count + extraActions <= 100` and `BatchPlanner.Plan` over primary + parts returns exactly one range.
    - `static bool IsPartRow(TableEntity row)`; `static int PartCount(TableEntity primary)`.
    - `static string PartRangeLower(string rowKey) => rowKey + "~"; static string PartRangeUpper(string rowKey) => rowKey + "\u007f";`
    - `static TableEntity Assemble(TableEntity primary, IEnumerable<TableEntity> candidateParts)` — keeps parts with `_SplitOf == primary.RowKey` and `_SplitIndex` in `1.._SplitParts` (ignores higher indices); requires every index, else `TableValueIntegrityException("_SplitParts", "part 3 of 5 missing")`; returns a bag with the primary's keys/ETag/Timestamp, its columns, and every part's non-`_Split*` columns.
    - `static void ValidateRowKey(string rowKey)` — `ArgumentException` when the key contains `~` or exceeds `MaxRowKeyLength`.

- [ ] **Step 1: Write the failing tests** (bags built with `SplitCodec.Encode`):
  - `NoSplitCells_IsSingleRow_AndUnchanged` — reference-equal, no `_Split*`.
  - `EightTextCells_FitOneEntity` — 254,000 chars → no parts.
  - `TwoEntities_OneTransaction` — 600,000 chars → 1 part `rk~0001`, `_SplitParts == 1`, `FitsOneTransaction(plan, 0)`.
  - `PropertyCountLimit_ForcesAPart` — 300 tiny chunk cells → every row ≤ 252 properties.
  - `TwoMillionChars_DoesNotFitOneTransaction` — 2,000,000-char Text → `FitsOneTransaction(plan, 0) == false`.
  - `FitsOneTransaction_CountsExtraActions` — a plan with 3 parts and `extraActions = 97` → false.
  - `Assemble_IgnoresPartsBeyondCount_AndOtherRows` — primary `_SplitParts == 1`, candidates `rk~0001`, `rk~0002`, `rkx~0001` → value of part 1 only.
  - `Assemble_MissingPart_Throws`.
  - `ValidateRowKey_RejectsTilde_AndOverlongKeys`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SplitRowLayoutTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** `SplitRowLayout` (pure).
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(values): plan split values onto continuation rows within one transaction"`

---

### Task 4: `TableStorage<T>` — split values in one transaction, every read path

**Files:**
- Modify: `src/Unified.Data.Tables/TableStorage.cs:23` (`partial`), `CreateAsync` `:111-158`, `DeleteAsync` `:161-176`, `DeletePartitionAsync` `:179-215`, `OneAsync` `:218-247`, `ExistsAsync` `:250-278`, `QueryAsync(partition)` `:281-321`, `BuildSelect`/`SystemColumns` `:399-412`, `StreamAsync` `:416-432`, `QueryPageAsync` `:435-469`, `Deserialize`/`Materialize`/`Clone` `:479-505`, `CountAsync` `:520-539`, `WriteBatchAsync` `:542-622`, `UpdateAsync(T, mode)` `:766-835`, `UpsertAsync` `:837-876`
- Create: `src/Unified.Data.Tables/TableStorage.LargeValues.cs`
- Test: `tests/Unified.Data.Tables.Tests/LargeValues/TableStorageSplitTests.cs` (uses `StorageHarness<T>`, `tests/Unified.Data.Tables.Tests/TestSupport/StorageHarness.cs:58`)

**Interfaces:**
- Consumes: Tasks 2-3.
- Produces (private to `TableStorage<T>`; Tasks 6 and 8 extend them):
  - `private static readonly bool LargeValueAware = TypeMetadataCache.IsLargeValueAware(typeof(T));` — every new branch sits behind it, except the cheap `IsPartRow` skip on reads.
  - `private sealed record PriorRow(ETag ETag, int Parts, IReadOnlyList<BlobReference> Blobs)` and `private Task<PriorRow?> ReadPriorAsync(string pk, string rk, CancellationToken ct)` — one point read selecting `_SplitParts`, `odata.etag` and every `LargeValueColumns(typeof(T))` `__blob` column.
  - `private enum LargeWriteKind { Create, Upsert, Replace }` and `private async Task<ETag> WriteLargeAsync(T entity, LargeWriteKind kind, ETag ifMatch, CancellationToken ct)`.
  - `private async Task<TableEntity> AssembleAsync(TableEntity primary, CancellationToken ct)` — no parts → `primary`; else one range query `PartitionKey eq pk and RowKey ge PartRangeLower and RowKey lt PartRangeUpper and _SplitOf eq rk`, then `SplitRowLayout.Assemble`.
  - `private async Task DeleteLargeAsync(string pk, string rk, CancellationToken ct)`.

Write rules (split-aware `T` only):
1. `ValidateRowKey(rowKey)` before anything else.
2. `Create` skips the prior read. `Replace` and `Upsert` call `ReadPriorAsync`. `Replace` with a caller ETag different from the prior's → `ConcurrencyConflictException` before any write; missing prior → `new RequestFailedException(404, $"The specified resource does not exist. Id: {id}")` (the message `InMemoryStorage.cs:197` uses).
3. Serialize with `ToTableEntity(..., ctx, pending)`; `plan = SplitRowLayout.Plan(row)`; `stale` = prior part keys with index > `plan.Parts.Count`.
4. No parts and no stale rows → the existing single SDK call: `AddEntityAsync` (Create), `UpdateEntityAsync(primary, ifMatch, Replace)` (Replace, and Upsert with a prior), `AddEntityAsync` (Upsert without a prior). Otherwise one `SubmitTransactionAsync`: primary `Add` / `UpdateReplace` with the same ETag rule, parts `UpsertReplace`, stale rows `Delete` with `ETag.All`.
5. Upsert that loses its condition (412, or 409 on the add) re-reads and retries from step 2, at most 3 attempts, then throws `ConcurrencyConflictException`. Create 409 → `DuplicateKeyException`; Replace 412 → `ConcurrencyConflictException` (same messages as today). A transaction failure maps through `FailedTransactionActionIndex == 0`.
6. ETag: single call → response header, as today; transaction → keys-only `GetEntityIfExistsAsync` re-read (sub-responses are not correlated, `TableStorage.cs:561-565`).
7. `CreateBatchAsync`/`UpsertBatchAsync` for split-aware `T` write entity by entity through `WriteLargeAsync` (document: no per-chunk atomicity for these types).
8. Delete: `ReadPriorAsync`; missing → return (idempotent). No parts → `DeleteEntityAsync(pk, rk, prior.ETag)`; else one transaction deleting primary (If-Match prior ETag) and parts. 412 → re-read and retry, at most 3 attempts.
9. `DeletePartitionAsync` still deletes every row; when split-aware, its select adds `_SplitOf` and its return value counts rows without it.

Read rules: every path skips `IsPartRow` rows before counting toward `take` and before caching; split-aware paths `await AssembleAsync` before deserializing (`QueryPageAsync`'s `page.Values.Select(Materialize)` becomes an awaited loop). `OneAsync`/`ExistsAsync` on a part row's id return `null`/`false`. `SystemColumns` gains the three `_Split*` names. `BuildSelect` returns `null` when `Select` names a property in `LargeValueColumns`; a projection that omits them is not assembled. `CountAsync` selects `PartitionKey` and `_SplitOf` when split-aware and counts rows without `_SplitOf`. `Clone` uses `ValueWriteContext.ForClone` so a cached split entity is never re-encoded through the public path.

- [ ] **Step 1: Write the failing tests** (mock `SubmitTransactionAsync`, `QueryAsync<TableEntity>` with `Mocks.Pageable`, capture actions):
  - `Create_SmallValue_UsesAddEntity_NoTransaction_NoPriorRead`.
  - `Create_TwoEntityValue_IsOneTransaction_PrimaryAddPartUpsert` — action 0 `Add` `rk`, action 1 `UpsertReplace` `rk~0001`.
  - `Create_ExistingKey_InTransaction_ThrowsDuplicateKey` (index 0, 409); `Replace_StaleETag_ThrowsConcurrencyConflict_BeforeAnyWrite` (prior ETag differs → no `SubmitTransactionAsync`, no `UpdateEntityAsync`).
  - `Replace_Missing_Throws404`.
  - `Create_RowKeyWithTilde_IsRefusedBeforeAnyCall` — `ArgumentException`; no write received.
  - `Upsert_Shrink_DeletesStalePartsInSameTransaction` (Review Focus 4) — prior `_SplitParts == 2`, new value fits one row → one transaction: `UpdateReplace rk` (prior ETag), `Delete rk~0001`, `Delete rk~0002`.
  - `Upsert_LosesRaceThreeTimes_ThrowsConcurrencyConflict`.
  - `OneAsync_AssemblesParts_AndVerifiesHash`; `OneAsync_OnPartRowId_ReturnsNull`; `OneAsync_SecondCallFromCache_ReturnsSameValue`.
  - `Query_Take_CountsLogicalEntitiesOnly` — rows `a, a~0001, b, b~0001, c`, `Take = 2` → `a`, `b`.
  - `QueryPage_SkipsPartRows_AndCursorReachesAll`.
  - `Projection_NamingSplitProperty_FetchesWholeRow` / `Projection_OmittingSplitProperty_DoesNotFetchParts` (Review Focus 5).
  - `Count_ExcludesPartRows`; `DeletePartition_ReturnsLogicalCount`.
  - `Delete_SplitValue_DeletesPrimaryAndPartsInOneTransaction`; `Delete_Missing_IsNoOp`.
  - `PlainEntity_ReadAndWritePaths_AreUnchanged` — a `TestEntity` create/read/count/delete issues exactly today's SDK calls (no prior read, no `_SplitOf` in any select).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TableStorageSplitTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** in `TableStorage.LargeValues.cs`, with one-line branches at each call site in `TableStorage.cs`.
- [ ] **Step 4: Run** the filter, then the whole project. Expected: PASS; `TableStorageTests`, `TableStorageQueryTests`, `PagingTests`, `ConcurrencyTests`, `CachePolicyTests` unchanged.
- [ ] **Step 5: Commit** — `git commit -m "feat(values): TableStorage writes split values in one transaction and hides continuation rows"`

---

### Task 5: Blob offload core — options, `TableBlobContent`, tier 3, blob seam

**Files:**
- Create: `src/Unified.Data.Tables.Abstractions/TableBlobContent.cs`
- Create: `src/Unified.Data.Tables/BlobOffloadOptions.cs`, `IOffloadBlobStore.cs`, `ValueOffloadCoordinator.cs`; modify `ValueContexts.cs`, `TableEntitySerializer.cs` (`TrySerialize`, `Materialize`), `UnifiedTableStorageOptions.cs` (after `:161`)
- Modify: `src/Unified.Data.Tables/Unified.Data.Tables.csproj` (add `<PackageReference Include="Azure.Storage.Blobs" Version="…" />` and `<InternalsVisibleTo Include="Unified.Data.Tables.InMemory" />`)
- Create: `src/Unified.Data.Tables.InMemory/InMemoryOffloadBlobStore.cs`
- Test: `tests/Unified.Data.Tables.Tests/LargeValues/OffloadSerializerTests.cs`, `TableBlobContentTests.cs`, `ValueOffloadCoordinatorTests.cs`

**Interfaces:**
- Consumes: Tasks 1-3.
- Produces:
  - `public sealed class BlobOffloadOptions { public string ContainerName { get; set; } = "unified-tables-values"; public long ThresholdBytes { get; set; } = 3_145_728; }`
  - `UnifiedTableStorageOptions.BlobOffload : BlobOffloadOptions?` (default `null` = offload off).
  - `public sealed class TableBlobContent` (Abstractions, `netstandard2.0`-safe):
    - `long Length`, `string Sha256`, `string ContentType` — known at once for `FromBytes` and for content read from a row. For `FromStream`, `Length`/`Sha256` throw `InvalidOperationException("…known once the entity has been written")` until a store writes it.
    - `static TableBlobContent FromStream(Stream content, string contentType)` (keeps the caller's stream, does not read or dispose it); `static TableBlobContent FromBytes(byte[] content, string contentType)`.
    - `Task<Stream> OpenReadAsync(CancellationToken ct = default)` — `FromBytes`: a read-only `MemoryStream`; written or read from a row: the blob, lazily, through a stream that throws `TableValueIntegrityException` at end-of-stream on a length or SHA-256 mismatch; unwritten `FromStream`: `InvalidOperationException`.
    - internal: `Seal(long length, string sha256)`, `Bind(string path, Func<CancellationToken, Task<Stream>> open)`, `static Bound(long length, string sha256, string contentType, string path, Func<CancellationToken, Task<Stream>> open)`, `Stream? Source`, `string? BoundPath`.
  - `internal sealed record BlobReference(string Path, string Sha256, long Length, string ContentType, string Encoding)` with `string ToJson()` (camelCase keys `path, sha256, length, contentType, encoding`) and `static BlobReference Parse(string column, string json)` (malformed → `TableValueIntegrityException`).
  - `internal sealed record PendingBlob(string Column, BlobReference Reference, string? ContentEncoding, Func<CancellationToken, Task<Stream>> OpenSource)`.
  - `ValueReadContext`: `IReadOnlyDictionary<string, object> Resolved` (base column → decoded `string`/`byte[]`) and `Func<string, BlobReference, TableBlobContent> LazyContent`.
  - `internal interface IOffloadBlobStore`:
    - `Task<BlobState?> GetStateAsync(string name, CancellationToken ct)` with `internal sealed record BlobState(string Sha256, long Length, DateTimeOffset LastModified)`.
    - `Task UploadCreateOnlyAsync(string name, Stream content, BlobReference reference, string? contentEncoding, CancellationToken ct)` — returns normally when a blob of that name already exists with the same `sha256` metadata (and refreshes its `Last-Modified`); throws `TableValueIntegrityException` when it exists with a different hash.
    - `Task TouchAsync(string name, CancellationToken ct)` — refreshes `Last-Modified` (metadata re-set).
    - `Task<Stream> OpenReadAsync(string column, string name, CancellationToken ct)` — missing → `TableValueIntegrityException(column, "blob missing")`.
    - `Task<bool> DeleteIfUnmodifiedAsync(string name, DateTimeOffset? unmodifiedSince, CancellationToken ct)` — false on 404 or 412.
    - `IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListAsync(string prefix, CancellationToken ct)`.
  - `internal sealed class ValueOffloadCoordinator(IOffloadBlobStore? blobs, BlobOffloadOptions? options, string tableName)`:
    - `static string BlobName(string table, string pk, string rk, string column, string sha256)` — escapes each segment; `ArgumentException` when the name exceeds 1,024 chars.
    - `Task PrepareAsync(object entity, CancellationToken ct)` — seals every unsealed `TableBlobContent` reachable through flattened properties: a seekable source is hashed in one pass and rewound; a non-seekable one is copied while hashing to a temp file (`FileOptions.DeleteOnClose | FileOptions.Asynchronous`) that becomes the upload source. Never buffers the content in memory.
    - `(TableEntity Row, SplitRowPlan Plan, IReadOnlyList<PendingBlob> Blobs) Serialize(object entity, string pk, string rk, int extraActions)` — serializes; while `!FitsOneTransaction(plan, extraActions)`, re-serializes with the largest split group (by `__len`) whose property allows offload added to `ForceOffload`; no group left → `OversizedValueException`.
    - `Task UploadAsync(IReadOnlyList<PendingBlob> blobs, CancellationToken ct)` — per blob: `GetStateAsync`; same hash → `TouchAsync` and skip the bytes; absent → `UploadCreateOnlyAsync` through a hashing read-through wrapper that throws `InvalidOperationException` when the bytes sent do not match the sealed hash (the source changed after preparing). Then `Bind`s each `TableBlobContent` to its blob.
    - `Task<ValueReadContext> ResolveAsync(TableEntity row, IReadOnlyCollection<string>? projectedColumns, CancellationToken ct)` — for each `__blob` reference in scope: a `TableBlobContent` target → `LazyContent`; any other target → download, `SplitCodec.FromStored` (verifies), into `Resolved`.
    - `static IReadOnlyList<BlobReference> References(TableEntity row)`.

Tier-3 rules inside `TrySerialize` (`ctx.Offload` non-null):
- Opted-in value: `stored = SplitCodec.ToStored(payload, encoding)`; offload when `stored.Bytes.Length > ThresholdBytes` or the column is in `ForceOffload`, unless the property says `BlobOffload.Never` (then a forced column → `OversizedValueException`). Offload writes `{col}__blob` = reference JSON (`encoding` = `stored.Enc`, `contentType` `text/plain; charset=utf-8` for `text`/`gzip`, `text/plain; charset=utf-16le` for `utf16le`, `application/octet-stream` for `binary`; `gzip` adds `ContentEncoding = "gzip"`) and appends a `PendingBlob` over the stored bytes.
- `TableBlobContent` value: always `{col}__blob` (`encoding` `binary`, the content's `ContentType`); its `PendingBlob` source is the prepared stream, the bytes, or (for content bound to another path) the source blob's stream.
- `ctx.Offload` null: a value needing tier 3, or any `TableBlobContent`, throws `OversizedValueException`. `ctx.Clone`: no thresholds, values stay as an unbounded logical bag, `TableBlobContent` is copied by reference.
- `TableBlobContent` inside a collection or a JSON-fallback graph → `NotSupportedException` at write.
- `Materialize` with a `ValueReadContext`: `__blob` base columns take their value from `Resolved` or `LazyContent`; a reference with no resolution (projection omitted it) leaves the property at its default.

- [ ] **Step 1: Write the failing tests:**
  - `OffloadSerializerTests` (ctx with an `InMemoryOffloadBlobStore`, `ThresholdBytes = 100_000` unless stated):
    - `JustBelowThreshold_IsSplit_JustAboveThreshold_IsOffloaded` — Text 99,999 vs 100,001 ASCII chars.
    - `DefaultThreshold_EightHundredKTokens_IsOffloaded` — default options, 3,200,000 chars → only `Body__blob` and ordinary columns; reference `encoding == "text"`, `contentType == "text/plain; charset=utf-8"`, `length == 3_200_000`.
    - `GzipOffload_HasContentEncodingGzip`; `BinaryOffload_IsOctetStream`; `IllFormedText_OffloadsAsUtf16le` (Review Focus 1).
    - `BelowThreshold_ButNotOneTransaction_IsForcedToBlob` — default threshold, 2,000,000-char Text (2 MB UTF-8 < 3 MiB, 4 MB as cells) → offloaded.
    - `NeverOffload_TooLargeForOneTransaction_Throws` — `NeverOffloadEntity`, 2,000,000 chars → `OversizedValueException`.
    - `NoOffloadConfigured_ValueNeedingTier3_Throws` and `NoOffloadConfigured_TableBlobContent_Throws` — `OversizedValueException`.
    - `BlobName_IsEscapedAndContentAddressed` — `("T", "p k", "r/k", "Inner_Body", sha)` → `"T/p%20k/r%2Fk/Inner_Body/" + sha`.
    - `TableBlobContent_InList_IsRefused` — `NotSupportedException`.
  - `TableBlobContentTests`: `FromBytes_KnowsLengthAndSha`; `FromStream_LengthBeforeWrite_Throws`; `PrepareAsync_SeekableStream_HashesWithoutBuffering` (a 100 MB generated seekable stream; `GC.GetAllocatedBytesForCurrentThread()` delta < 16 MB; position back at 0); `PrepareAsync_NonSeekableStream_SpoolsToTempFile`; `OpenRead_TamperedBlob_ThrowsAtEndOfStream`.
  - `ValueOffloadCoordinatorTests`: `Upload_SameNameSameHash_TouchesAndSkipsBytes` (Review Focus 2); `Upload_SameNameDifferentHash_ThrowsIntegrity`; `Upload_SourceChangedAfterPrepare_Throws`; `Resolve_MissingBlob_ThrowsIntegrity`; `Resolve_ProjectionWithoutProperty_TouchesNoBlob`; `Resolve_TableBlobContent_DoesNotDownload` (the store records no `OpenReadAsync` until the caller opens it).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~OffloadSerializerTests|FullyQualifiedName~TableBlobContentTests|FullyQualifiedName~ValueOffloadCoordinatorTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Pick the newest stable `Azure.Storage.Blobs` on nuget.org that is at least two weeks old, restores without `NU1605`, and does not change `Azure.Data.Tables` 12.11.0; record the version in the CHANGELOG. `InMemoryOffloadBlobStore` keeps `ConcurrentDictionary<string, (byte[] Bytes, BlobReference Ref, string? ContentEncoding, DateTimeOffset LastModified)>` and exposes internal `Seed(name, bytes, sha256, lastModified)`, `Tamper(name, bytes)`, `Remove(name)` and `Names` for tests.
- [ ] **Step 4: Run** the filter, then the whole project. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(values): blob offload tier, TableBlobContent and the offload blob seam"`

---

### Task 6: `TableStorage<T>` — offload wiring, ordering, DI and the Azure blob store

**Files:**
- Create: `src/Unified.Data.Tables/AzureOffloadBlobStore.cs`
- Modify: `src/Unified.Data.Tables/TableStorage.cs` (constructors `:53-87`), `TableStorage.LargeValues.cs`, `src/Unified.Data.Tables/TableInitializer.cs:10` (replace the primary constructor with a `Func<Task> create` constructor plus a `TableClient` constructor that delegates to it, so both callers keep the coalesced, retry-on-failure init), `src/Unified.Data.Tables/ServiceCollectionExtensions.cs:46-76`
- Create: `tests/Unified.Data.Tables.Tests/LargeValues/RecordingBlobStore.cs`
- Test: `tests/Unified.Data.Tables.Tests/LargeValues/TableStorageOffloadTests.cs`, `AzureOffloadBlobStoreTests.cs`, `OffloadRegistrationTests.cs`

**Interfaces:**
- Consumes: Tasks 4-5.
- Produces:
  - New public constructor `TableStorage(TableServiceClient serviceClient, IMemoryCache cache, ILogger<TableStorage<T>> logger, IProtectedPropertyAuthorizer? authorizer = null, UnifiedTableStorageOptions? options = null, BlobServiceClient? blobServiceClient = null)`. The existing five-parameter constructor stays (binary compatibility) and forwards with `null`. Microsoft DI picks the longest satisfiable constructor, so the six-parameter one resolves a registered `BlobServiceClient` and gets `null` otherwise. `options.BlobOffload` set with no client → `InvalidOperationException` naming `BlobServiceClient` at construction (no I/O).
  - `internal TableStorage(TableServiceClient, IMemoryCache, ILogger<TableStorage<T>>, IProtectedPropertyAuthorizer?, UnifiedTableStorageOptions?, IOffloadBlobStore?)` for tests.
  - `internal sealed class AzureOffloadBlobStore(BlobContainerClient container) : IOffloadBlobStore` — creates the container lazily through `new TableInitializer(() => container.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None))`; uploads with `BlobUploadOptions { Conditions = new() { IfNoneMatch = ETag.All }, HttpHeaders = { ContentType, ContentEncoding }, Metadata = { ["sha256"], ["length"] }, TransferOptions = { MaximumTransferSize = 4 MiB, MaximumConcurrency = 4 } }`; a 409 reads properties and compares `sha256`; `DeleteIfUnmodifiedAsync` passes `BlobRequestConditions { IfUnmodifiedSince }`.
  - `ServiceCollectionExtensions`: the connection-string overload also `TryAddSingleton(_ => new BlobServiceClient(connectionString))`; the endpoint/credential overload `TryAdd`s `new BlobServiceClient(blobEndpoint, credential)` only when the table host contains `.table.` (replaced by `.blob.`), otherwise the caller registers it. The bare overload registers nothing new; XML docs say a host with `BlobOffload` registers a `BlobServiceClient` the way it registers its `TableServiceClient`.

Write and read rules (extends Task 4's `WriteLargeAsync`):
1. `PrepareAsync`, then `Serialize(entity, pk, rk, extraActions: stale count)`, then `UploadAsync` — all before the first table call.
2. The entity write as in Task 4.
3. After success: for each prior reference whose path is not referenced by the new row, `DeleteIfUnmodifiedAsync(path, null)` best-effort (log at Warning on failure, never throw).
4. Any failed entity write leaves uploaded blobs in place.
5. Delete: after the row (and parts) are deleted, delete each prior reference best-effort. `DeletePartitionAsync`: after the rows, delete every blob listed under `{table}/{pk}/`.
6. Reads: after `AssembleAsync`, `ResolveAsync(row, projection)`, then `FromTableEntity<T>(row, ctx)`.

- [ ] **Step 1: Write the failing tests** (`RecordingBlobStore` wraps `InMemoryOffloadBlobStore` and appends to a shared `List<string>` that the table mock's `When(...).Do(...)` also appends to):
  - `Create_Offloaded_UploadsBlobBeforeEntityWrite` — log `["blob:get", "blob:upload", "table:add"]`.
  - `Create_TableBlobContent_100MB_StreamsAndRowCarriesOnlyReference` — the row sent has `File__blob` and no chunk cells; `Length == 100_000_000`.
  - `Read_TableBlobContent_NeverDownloadsUntilOpened`.
  - `Replace_NewContent_UploadThenIfMatchSwapThenDeleteOld` — `blob:upload(new)` → `table:update(If-Match callerETag)` → `blob:delete(old)`.
  - `Replace_SameContent_DoesNotDeleteItsOwnBlob` (Review Focus 2) — no `blob:delete`; `blob:touch` before `table:update`.
  - `Replace_LosesRace_DeletesNoBlob` — `UpdateEntityAsync` throws 412 → `ConcurrencyConflictException`; no `blob:delete`.
  - `Delete_EntityFirst_ThenBlobs`.
  - `Delete_BlobDeleteFails_EntityStillDeleted_NoThrow`.
  - `Read_MissingBlob_ThrowsIntegrity`; `Read_TamperedTextBlob_ThrowsIntegrity`.
  - `Projection_OmittingOffloadedProperty_TouchesNoBlob` (Review Focus 5).
  - `Cache_ClonesOffloadedEntity_WithoutReencoding` — second `OneAsync` served from cache, no second blob read.
  - `AzureOffloadBlobStoreTests` (NSubstitute `BlobContainerClient`/`BlobClient`): `Upload_IsCreateOnly_WithMetadataAndHeaders`; `Upload_409_SameSha_IsSuccess`; `Upload_409_DifferentSha_ThrowsIntegrity`; `OpenRead_404_ThrowsIntegrity`; `Delete_PassesIfUnmodifiedSince`.
  - `OffloadRegistrationTests` (`[Collection("OversizedCellPolicy")]`): `ConnectionStringOverload_RegistersBlobServiceClient`; `CredentialOverload_DerivesBlobEndpoint`; `CustomHost_RegistersNoBlobClient`; `DiResolvesSixParameterCtor_WithBlobClient`; `BlobOffloadWithoutClient_ThrowsAtResolve`; `PlainHost_NoBlobOffload_ResolvesAsBefore`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~TableStorageOffloadTests|FullyQualifiedName~AzureOffloadBlobStoreTests|FullyQualifiedName~OffloadRegistrationTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter, Task 4's filter, then the whole project (including `ServiceCollectionExtensionsTests`). Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(values): TableStorage offloads to blobs with create-only, If-Match swap and ordered cleanup"`

---

### Task 7: `InMemoryStorage<T>` parity

**Files:**
- Modify: `src/Unified.Data.Tables.InMemory/InMemoryStorage.cs` — constructor `:64-70`, `Count` `:79`, `Snapshot` `:100`, `CreateAsync` `:111`, `UpsertAsync` `:139`, `UpdateAsync` `:168-215`, `UpdateAsync(id, builder)` `:217-262`, `OneAsync` `:265`, `ExistsAsync` `:281`, query paths `:297-437`, `CountAsync` `:489-499`, `DeleteAsync` `:502-518`, `DeletePartitionAsync` `:521-535`, `WriteBatch` `:538`, `Store` `:616-624`, `Materialize` `:626-634`
- Modify: `src/Unified.Data.Tables.InMemory/Unified.Data.Tables.InMemory.csproj` (add `<InternalsVisibleTo Include="Unified.Data.Tables.Tests" />`)
- Test: `tests/Unified.Data.Tables.Tests/LargeValues/InMemoryLargeValueParityTests.cs`

**Interfaces:**
- Consumes: `SplitRowLayout`, `ValueOffloadCoordinator`, `InMemoryOffloadBlobStore` (Tasks 3, 5).
- Produces: `public IReadOnlyList<string> OffloadedBlobNames()` (test convenience beside `Count`/`Snapshot`), `internal InMemoryOffloadBlobStore Blobs`, `internal int PhysicalRowCount`, `internal void SeedPhysicalRow(TableEntity row)`.

Rules: `options.BlobOffload` non-null gives the store its own `InMemoryOffloadBlobStore` (no `BlobServiceClient`); the table segment is `options.TableNameResolver?.Invoke(typeof(T))` or `typeof(T).Name`, exactly as `TableStorage<T>.ResolveTableName` (`TableStorage.cs:96`). For split-aware `T`: `PrepareAsync`, `Serialize` and `UploadAsync` run outside `gate`; the prior check, primary + parts + stale-row removal happen atomically under `gate`; old-blob deletes run after it. Failed writes delete no blob. Reads skip part rows, assemble, and resolve through the coordinator. `Count`, `Snapshot`, `CountAsync` and `DeletePartitionAsync`'s return value count logical rows. Exceptions match Task 4 (`DuplicateKeyException`, `ConcurrencyConflictException`, `RequestFailedException(404)`, `ArgumentException` for `~`, `OversizedValueException`).

- [ ] **Step 1: Write the failing tests** — one `[Theory]` per behaviour over Text sizes `{ 100, 32_001, 600_000, 2_000_000, 3_200_000 }` chars and `byte[]` sizes `{ 70_000, 3_145_729 }`, with `BlobOffload = new()`:
  - `RoundTrip_Create_One_Query` — equal values; `QueryAsync(partition)` yields 1; `Count == 1`; `OffloadedBlobNames()` has one entry exactly when the size is offloaded.
  - `Upsert_Shrink_LeavesNoPartsOrBlobs` — `PhysicalRowCount == 1`, `OffloadedBlobNames()` empty.
  - `ConcurrentReplace_OneWins_ValueIntact` — two `UpdateAsync` with the same ETag → one `ConcurrencyConflictException`; read equals the winner.
  - `Delete_RemovesRowPartsAndBlobs`.
  - `TableBlobContent_RoundTrip_IsLazy` — 10 MB `FromStream`; read entity, then `OpenReadAsync` content equals.
  - `TamperedBlob_ThrowsIntegrity`; `RemovedBlob_ThrowsIntegrity`.
  - `NoOffloadConfigured_Throws_LikeTableStorage` — `OversizedValueException` for 3,200,000 chars.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~InMemoryLargeValueParityTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter and `InMemoryStorageTests`. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(values): InMemoryStorage stores split rows and offloaded blobs for parity"`

---

### Task 8: `SweepOrphanBlobsAsync`

**Files:**
- Modify: `src/Unified.Data.Tables.Abstractions/IStorage.cs:190` (append member), `src/Unified.Data.Tables/ValueOffloadCoordinator.cs`, `TableStorage.LargeValues.cs`, `src/Unified.Data.Tables.InMemory/InMemoryStorage.cs`
- Test: `tests/Unified.Data.Tables.Tests/LargeValues/SweepOrphanBlobsTests.cs`

**Interfaces:**
- Consumes: `IOffloadBlobStore.ListAsync`/`DeleteIfUnmodifiedAsync`, `ValueOffloadCoordinator.References`, `TypeMetadataCache.LargeValueColumns`, `InMemoryOffloadBlobStore.Seed`.
- Produces:

```csharp
/// Deletes blobs offloaded under {table}/{partitionKey}/ that no row of the partition references and
/// whose Last-Modified is older than <paramref name="olderThan"/>. Returns the number deleted.
/// Returns 0 when blob offload is not configured.
Task<int> SweepOrphanBlobsAsync(string partitionKey, TimeSpan olderThan, CancellationToken ct = default);
```

  On `net10.0` the member has a default implementation that throws `NotSupportedException` (inside `#if NET`), so
  existing `net10.0` implementers keep compiling; `netstandard2.0` implementers must add it (CHANGELOG "Changed").
- `Task<int> ValueOffloadCoordinator.SweepAsync(string pk, DateTimeOffset cutoff, Func<CancellationToken, IAsyncEnumerable<TableEntity>> partitionRows, CancellationToken ct)`.

Rules: `partitionKey` blank → `ArgumentNullException`; `olderThan < TimeSpan.Zero` → `ArgumentOutOfRangeException`;
normalize the partition like `DeletePartitionAsync`. Order: (1) list blobs under the escaped prefix and keep those with
`LastModified < UtcNow - olderThan`; none → return 0 with no table read. (2) Read the partition's referenced paths
with one query selecting `PartitionKey`, `RowKey`, `_SplitOf` and every `{column}__blob` from `LargeValueColumns`
(a type whose flattened members include an interface or abstract type selects every column instead). (3) Delete
each unreferenced candidate with `DeleteIfUnmodifiedAsync(name, listed LastModified)`; count only `true`. Never
touches rows.

- [ ] **Step 1: Write the failing tests** (TableStorage with mocked table + `RecordingBlobStore`, and InMemory with `Blobs.Seed`):
  - `Sweep_CrashBeforeEntityWrite_DeletesOrphan_AfterGrace` — a seeded blob aged 10 min, no row, `olderThan` 5 min → 1; gone.
  - `Sweep_InFlightWrite_WithinGrace_IsKept` — aged 10 s → 0.
  - `Sweep_KeepsReferencedBlob_DeletesReplacedOne` — row references blob B; old blob A, both aged → only A deleted.
  - `Sweep_BlobTouchedAfterListing_IsKept` (Review Focus 3) — the store's `DeleteIfUnmodifiedAsync` sees a newer `LastModified` → returns false; count 0; blob present.
  - `Sweep_OtherPartitionsBlobs_AreUntouched`.
  - `Sweep_NoCandidates_DoesNotReadTheTable`.
  - `Sweep_NoOffloadConfigured_ReturnsZero`; `Sweep_NegativeOlderThan_Throws`; `Sweep_BlankPartition_Throws`.
  - `Sweep_PlainImplementer_DefaultThrowsNotSupported` — a minimal `IStorage<T>` test double without the member.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SweepOrphanBlobsTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** in both stores.
- [ ] **Step 4: Run** the filter and the whole project. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(values): SweepOrphanBlobsAsync removes offloaded blobs no row references"`

---

### Task 9: Azurite integration

**Files:**
- Test: `tests/Unified.Data.Tables.Tests/LargeValues/AzuriteLargeValueIntegrationTests.cs` (probe/skip/cleanup pattern of `AzuriteTableStorageIntegrationTests.cs:20-62`, probing both the table and the blob endpoint; each test uses partition `lv-{Guid:N}`, a container `lv-{Guid:N}` set through `BlobOffloadOptions.ContainerName`, and deletes both in `DisposeAsync`)

**Interfaces:** Consumes everything above through `TableStorage<T>` built with `new TableServiceClient("UseDevelopmentStorage=true")` and `new BlobServiceClient("UseDevelopmentStorage=true")`.

- [ ] **Step 1: Write the tests** — each `Assert.SkipUnless(_available, …)`:
  - `[Theory]` `TextBoundaries_RoundTrip` over `{ 32_000, 32_001, 254_000, 600_000, 3_200_000 }` chars (1 cell, 2 cells, 1 entity, 2 entities / 1 transaction, 800K tokens / offloaded) — ordinal equality; a raw `TableClient` query shows the expected physical rows and `string` chunk cells; the 3,200,000 case shows one blob with `Content-Type: text/plain; charset=utf-8`, metadata `sha256`/`length`.
  - `FullTransaction_StaysInTables_OneMoreCharIsOffloaded` — the test binary-searches the largest Text size for which `SplitRowLayout.FitsOneTransaction` holds; that size has 0 blobs and the next size has 1.
  - `JustAboveThreshold_Bytes_IsOffloaded` — `byte[]` of 3,145,729 random bytes.
  - `TextWithSurrogatePairsAtEveryBoundary_RoundTrips` — emoji at each `k * 32_000 - 1`; `IllFormedText_RoundTrips` (Review Focus 1).
  - `GzipEncoding_RoundTrips_BlobHasContentEncodingGzip` — 4,000,000 random chars; also confirms the SDK download returns the stored gzip bytes (no transport decompression).
  - `TableBlobContent_100MB_StreamsBothWays` — `FromStream` over a generated, non-buffered 100,000,000-byte stream; `OneAsync` does no blob read; `OpenReadAsync` hashes to the same SHA-256.
  - `CorruptChunk_Throws`, `MissingPart_Throws`, `MissingBlob_Throws`, `TamperedBlob_Throws` — via raw clients → `TableValueIntegrityException`.
  - `Replace_DeletesOldBlob_AfterSwap`; `Delete_RemovesRowThenBlobs`.
  - `CrashBeforeEntityWrite_ThenSweep` — upload a planned blob directly with `AzureOffloadBlobStore` and write no row; `SweepOrphanBlobsAsync(pk, TimeSpan.Zero)` returns 1; the container is empty under the prefix.
  - `ConcurrentReplace_OneWins_SweepLeavesOnlyWinnersBlob` — two `UpdateAsync` with the same ETag → one `ConcurrencyConflictException`; after the sweep only the winner's blob remains.
- [ ] **Step 2: Start Azurite** (`npx azurite --silent --location <scratch dir>`, blob 10000, table 10002) and **run** `--filter "FullyQualifiedName~AzuriteLargeValueIntegrationTests"`. Expected: PASS, none skipped. Then run once with Azurite stopped. Expected: all skipped, none failed.
- [ ] **Step 3: Fix** any provider difference in the store code, not by weakening a test. Re-run Tasks 4-8 filters.
- [ ] **Step 4: Commit** — `git commit -m "test(values): Azurite round-trips at every tier boundary, 100 MB streaming, integrity and sweep"`

---

### Task 10: Documentation and 0.9.0

**Files:**
- Modify: `README.md` — Features list (`:20`), new section `## Large values: split and blob offload` after `## Serialization` (`:476-505`), the `IStorage<T>` table (`:165-194`) gains `SweepOrphanBlobsAsync`, `## Testing with Unified.Data.Tables.InMemory` (`:602`) notes the in-memory blob dictionary
- Modify: `CHANGELOG.md` — new `## [0.9.0] — <release date>` above `:7`
- Modify: `Directory.Build.props:35` — `<Version>0.9.0</Version>`
- Modify: `src/Unified.Data.Tables/UnifiedTableStorageOptions.cs:156-161` — the `OversizedCells` comment says the default is `Throw` (it says "trim") and points to `[TableValueSplit]` and `BlobOffload` for lossless storage

- [ ] **Step 1: Write the README section:** opt-in (`[TableValueSplit(SplitEncoding.Text)]` per property or class; `Offload = BlobOffload.Never`); `TableBlobContent` for files (always offloaded, lazy `OpenReadAsync`, `FromStream` never buffers and leaves the caller's stream open; `Length`/`Sha256` known after the write); the three tiers and their row and blob shapes; registering `BlobServiceClient` and `BlobOffload`; Text vs Gzip; ill-formed strings stored as UTF-16LE; write, replace and delete ordering; `SweepOrphanBlobsAsync` and why it needs a grace period; `~` reserved in RowKeys of split-aware tables; limitations (no LINQ filter or partial update on a large property, not supported by `IPolymorphicStorage`, batch writes of these types are per entity, split-aware writes pay one point read, eager offloaded values download on every read); and `CacheFor<T>(CachePolicy.Disabled)` for types holding very large eager values.
- [ ] **Step 2: Write the CHANGELOG entry:** **Added** (`[TableValueSplit]`, `SplitEncoding`, `BlobOffload`, `BlobOffloadOptions`, `UnifiedTableStorageOptions.BlobOffload`, `TableBlobContent`, `SweepOrphanBlobsAsync`, `TableValueIntegrityException`, `OversizedValueException`, the six-parameter `TableStorage<T>` constructor, the `BlobServiceClient` registration in the connection-string and credential overloads); **Changed** (`IStorage<T>` gains a member: default implementation on `net10.0`, required for `netstandard2.0` implementers; new `Azure.Storage.Blobs` dependency with its exact version); **Unchanged** (types that opt into nothing: same columns, same SDK calls).
- [ ] **Step 3: Run** `dotnet build Unified.Data.Tables.slnx -c Release` and `dotnet test Unified.Data.Tables.slnx -c Release` (Azurite running). Expected: 0 warnings, 0 failures; `dotnet pack Unified.Data.Tables.slnx -c Release -o <scratch dir>` produces `*.0.9.0.nupkg` for all four packages, and the main package's nuspec lists `Azure.Storage.Blobs`.
- [ ] **Step 4: Commit** — `git commit -m "docs: lossless large values and blob offload; release 0.9.0"`
- [ ] **Step 5: Hand-off** — push `feat/lossless-value-split` and open a PR to `main` only when the user asks; merge, release (`publish.yml`, version `0.9.0`) and the IntelliSpec upgrade (P1) are separate, user-authorised steps.

---

## Canon issues

1. **`IStorage<T>.SweepOrphanBlobsAsync` and existing implementers.** IntelliSpec's own `IStorage<T>` test doubles
   (`dotnet-agents.Tests/TestSupport/InMemoryStorage.cs:15`, `dotnet-agents.Tests/TestSupport/InterceptingStorage.cs:11`,
   `dotnet-agents.Tests/Services/Learning/InstinctStoreBareIdResolutionTests.cs:127`,
   `dotnet-agents.Tests/Agents/Orchestration/PipelineOrchestrator/ReviewOnlyFollowUpChecks.cs:426`) keep compiling
   through the `net10.0` default implementation, but `InterceptingStorage` will not forward the call unless P1 adds the
   member. IntelliSpec's P1 plan (`docs/superpowers/plans/2026-11-10-llm-recorder-p1-core.md`, Task 1) now uses this
   plan's names (`SweepOrphanBlobsAsync`, `BlobOffload`, `BlobOffloadOptions`) and adds the member to its test doubles.
2. **`OversizedCellPolicy.Split` is dropped.** The spec makes the capability opt-in per property only; a global policy
   would route every type's oversized cell through blob offload and change behaviour for users who did not opt in.
3. **Hash definition changed** from "UTF-16 code units of the string" (the previous plan) to "SHA-256 of the stored
   bytes", so a split value and the same value offloaded agree, and the blob name is the hash of the blob's bytes.
4. **Ill-formed UTF-16 is stored as `utf16le`**, an `__enc`/`encoding` value and a `text/plain; charset=utf-16le`
   content type the spec does not list. UTF-8 cannot represent an unpaired surrogate and the Tables JSON wire rejects
   it, so this is the only lossless option.
5. **`BlobServiceClient` resolution.** Chosen approach: resolve it from DI like `TableServiceClient` (new optional
   constructor parameter); the connection-string overload `TryAdd`s one from the same string; the credential overload
   derives the blob endpoint only for `*.table.*` hosts. Hosts with custom domains, or that register their own
   `TableServiceClient`, register a `BlobServiceClient` themselves; IntelliSpec already does (spec §5.5).
6. **`OversizedValueException` lives in Abstractions**, next to `TableValueIntegrityException`, so IntelliSpec's
   Abstractions-only `IntelliSpec.Data` can catch both without a new package reference. The spec does not fix its
   assembly.
7. **`~` is reserved in RowKeys of split-aware tables**, and the escaped blob name must fit 1,024 chars. P1 already
   percent-encodes `~` in `LlmRecordIds.EscapeKeyValue`, and `LlmRecordKeyEntity` has no large property.
8. **Doc mismatch fixed in passing.** `UnifiedTableStorageOptions.cs:157-158` says the default policy trims; the code
   default is `Throw` (`:161`, `TableEntitySerializer.cs:41`). Task 10 corrects the comment only.

## Open risks

- **Azure.Storage.Blobs pulls a newer `Azure.Core`** transitively than Azure.Data.Tables 12.11.0 alone; Task 5 checks
  restore, but consumers pinning `Azure.Core` lower will see a resolution change.
- **Read cost.** A split value costs one extra range query per entity, and an eagerly read offloaded value one blob
  download per entity per read (N+1 on large queries; the entity cache holds the full value). Split-aware writes pay
  one point read for stale-part and old-blob detection. Acceptable for record-at-a-time readers; README says so.
- **`Content-Encoding: gzip` blobs** could be transparently decompressed by a transport configured for automatic
  decompression, which would fail the hash check. Task 9 pins the default SDK behaviour; a custom transport is the
  caller's risk and is documented.
- **Orphans accumulate** without a scheduled `SweepOrphanBlobsAsync` (lost races, crashes, failed best-effort deletes).
  The library does not schedule it.
- **The entity budget** (1,000,000 estimated bytes from `TableRowSize.Estimate`) relies on that estimate staying
  conservative for the wire size; Task 9's 600,000-char and full-transaction cases exercise it against Azurite only,
  and the 100 MB streaming case proves memory use only against Azurite, not production throughput or timeouts.
