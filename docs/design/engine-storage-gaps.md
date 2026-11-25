# Storage gaps across four consumers: the 0.10.0 design

Status: accepted design, 2026-11-25. No code in this change. It replaces the first version of this note, which looked from CortadelDB only.

## Context

PR #22 is a design note written from one consumer (the CortadelDB engine). It proposes five additions: range scan (P1), neutral failures (P2), conditional delete (P3), a durability statement (P4) and a conformance kit (P5). The owner asked for those benefits to reach Cortadel, IntelliSpec, VSP and IntelliGrowth too.

A read of all five codebases (each map re-checked against the code by a second agent) changed the picture:

- Two needs are more widespread than any of the five: **own-write ambiguity** (a retried write that already landed comes back as a duplicate or conflict) and **raw table/row access** for backup, restore and copy. All four repos work around both.
- P2 is wider than the note says: a raw Azure 404 escapes at 10 provider sites plus `MutateAsync`, `MutateOrCreateAsync`, `TryMutateAsync` and `TryTransitionAsync`.
- P1 and P4 have the least cross-repo use; P5 has the most.
- A local provider has a prerequisite the note missed: the in-memory package depends on the Azure package and throws Azure types.

Owner decisions: one release, a trade-off per candidate (about 1,500 apps use the library), library plus one adoption PR per repo, a hard switch for P2, and a SQLite provider in the release.

Three independent designs were produced and attacked by three critics. This plan is the compatibility-first design with the critics' corrections applied.

## Design calls open to challenge in review

1. **The shared package keeps a dependency on `Azure.Data.Tables` (types only) in 0.10.0.** The serializer has 33 `TableEntity` references in 3,300 lines, and rewriting it risks the stored bytes of every app. So "InMemory and SQLite are Azure-free" becomes "no `Azure.Storage.Blobs`, no network, no Azure provider package". A `TableEntity`-free row model is left for 1.0. The alternative is to retarget the serializer now behind a byte-identical golden corpus; that becomes the critical path.
2. **No new member on `IStorage<T>` or `IPolymorphicStorage<TBase>`.** At least six hand-written implementers exist in the four repos alone. New operations are side interfaces.
3. **Own-write ambiguity ships as a signal only.** Automatic "it was my write" resolution by comparing rows is unsound: two writers inserting identical polymorphic rows would both succeed.
4. **SQLite uses the same read-cache default as Azure**, so a local run shows the same staleness production would.
5. **The upgrade analyzer ships at Info severity** and is not on the critical path, because every consumer builds with warnings as errors.

## Scope

| Candidate | Verdict | Trade-off |
|---|---|---|
| Neutral failures (P2) | In, all provider failures | A partial switch would need a second silent break later |
| Own-write ambiguity | In, signal only | Exact resolution needs a write-token column, a row-shape change |
| Conformance kit (P5) | In | Highest value in all four repos; pure addition |
| Conditional delete (P3) | In, side interface, status result | A `bool` cannot express "gone, maybe by my lost attempt" |
| Range scan (P1) | In, typed store only | Additive on a sealed record |
| Storage info (P4) | In, widened: durability, limits, cache flag, table name | A sealed record so later fields never break implementers |
| Raw rows and table lifecycle | In, marked `[Experimental]` | Three repos hand-wrote the same shape; still new, so it may change before 1.0 |
| All-or-nothing batch | In | Throws before any write instead of splitting silently |
| `TimeProvider`, client-options hook, key-rule validator | In | Additive options |
| Reject ETag `"*"` on whole-entity update | In, breaking | Today it is silent last-writer-wins on Azure and a conflict on the fake |
| Azure-free InMemory, shared core | In | Prerequisite of SQLite |
| SQLite provider | In | Contained by sharing one row-store engine with InMemory |
| Typed mixed or If-Match transaction | Out | Error model, large values and cache interplay undecided; raw `SubmitAsync` covers IntelliGrowth's uniqueness index |
| Change log, snapshot reader, NDJSON codec | Out | No adoption PR in this release depends on them |
| Polymorphic range, paging, conditional merge | Out, except atomic insert | Needs its own side-interface design |
| Explicit-key typed store, account-keyed DI | Out | One consumer; largest shape risk |
| Partition listing, "is unset" filter, metrics, per-row stream failures | Out | No Azure primitive, or a permanent contract not yet proven |
| `AtOrBeforeAsync` rewrite as a key seek | Out | Changes which rows return on older implementers |

## Release shape

Version **0.10.0**, not 1.0.0: the raw API and a second engine need a soak before the surface is frozen. Every break needed before 1.0 lands here.

Packages (one shared version, exact sibling pins):

- `Unified.Data.Tables.Abstractions` (netstandard2.0; net10.0), stays Azure-free and browser-safe.
- `Unified.Data.Tables.Core` (new): serializer, split codec, filter validator, options base, cursor, row-store engine. Moved types are type-forwarded and keep their namespaces.
- `Unified.Data.Tables` (Azure provider).
- `Unified.Data.Tables.InMemory`: references Core, no longer the Azure provider.
- `Unified.Data.Tables.Sqlite` (new, `Microsoft.Data.Sqlite`).
- `Unified.Data.Tables.Identity`: unchanged graph.
- `Unified.Data.Tables.Conformance` (new): framework-neutral scenarios plus `CountingStorage<T>` and `FaultInjectingStorage<T>`.
- `Unified.Data.Tables.Conformance.Xunit` (new): xUnit v3 adapter. Cortadel and IntelliGrowth are on xUnit v2 and loop over the neutral scenarios.

## API shapes

**Exceptions** (Abstractions): `StorageException` base with `ProviderErrorCode`; `EntityNotFoundException`; `TableNotFoundException`; `StorageRequestRejectedException` (400/413); `TransientStorageException { MayHaveCommitted, RetryAfter }`; `BatchNotAtomicException`. `DuplicateKeyException`, `ConcurrencyConflictException` and `BatchPayloadTooLargeException` are reparented under the base (binary-safe; they derive from `Exception` today) and the first two gain `MayBeOwnWrite`.

- No storage member throws a provider type; the provider exception is always `InnerException`.
- `TableNotFound` never maps to row-not-found. A blob-side failure never maps to either.
- `MayBeOwnWrite` is true when an earlier attempt of the same HTTP request was observed, and true when attempts cannot be observed. False means only "no SDK retry seen in this call".
- `TryMutateAsync` and `TryTransitionAsync` return `NotFound` on a write-time miss; `MutateOrCreateAsync` creates; `MutateAsync` keeps its `InvalidOperationException`.

**Side interfaces**, reached through extension methods:

- `IConditionalDelete<T>` with `DeleteIfMatchAsync(id, etag)` and `DeleteIfExistsAsync(id)`, returning `DeleteStatus { Deleted, NotFound, Indeterminate }`. A mismatch throws `ConcurrencyConflictException`; `"*"`, null or empty throws `ArgumentException`.
- `IAtomicBatch<T>` with `CreateAtomicAsync` and `UpsertAtomicAsync`; `IAtomicInsert<TBase>` with `InsertAtomicAsync`.
- `IStorageInfo` returning a sealed `StorageInfo` record. `GetInfo()` never throws and returns `StorageInfo.Unknown` for stores that do not implement it. Azure reports `Replicated` only on a positive host match, otherwise `Unknown`; a declared value overrides. Limits are documented as what the library plans and refuses by.
- `ITableStore` and `IRawTable` (`[Experimental]`): list, exists, create-only, delete table; untyped rows with typed cells; single-partition all-or-nothing `SubmitAsync`, byte-planned. Rows are physical.
- `DelegatingStorage<T>` base class for decorators, plus one `TryGetCapability<TCap>` probe that decorators forward. Library code uses only the probe, so a decorator never claims a capability its inner store lacks.

**Range**: `QueryOptions.FromRowKey / FromInclusive / ToRowKey / ToInclusive`. Requires `Partition`; combining with `RowKeyPrefix` throws. Bounds follow the store's id normalization. The cursor fingerprint stays byte-identical when no bound is set and appends four parts otherwise. Projected scans always select `_SplitOf`, so continuation rows stop leaking.

**Options**: `StorageOptions` base in Core with `TimeProvider` and a neutral offload type. `UnifiedTableStorageOptions` keeps `BlobOffload`, keeps `CacheFor<T>` returning the derived type, and gains `ConfigureTableClient`.

## SQLite provider

- One database file per account. Per table: `(pk BLOB, rk BLOB, ver INTEGER, ts INTEGER, cells BLOB, PRIMARY KEY(pk, rk)) WITHOUT ROWID`.
- Keys are stored as UTF-16BE bytes, so byte order equals the ordinal order InMemory uses. The kit asserts order for BMP characters only; Azure's order for the rest is unverified.
- ETag is a database-wide monotonic counter bumped inside the write transaction.
- Azure limits are enforced (100 rows, 4 MB, one partition, 64 KB cell, 1 MB row, key rules), so a table that works locally works on Azure.
- Large values use the same split layout; offloaded blobs go to a `__blobs` table through `IOffloadBlobStore`.
- Filters: partition and key range go to SQL; column predicates run as the compiled predicate, exactly as InMemory does today.
- Durability is read back from `PRAGMA journal_mode` and `PRAGMA synchronous` on each connection: WAL with `FULL` reports `Fsynced`, `NORMAL` reports `OsBuffered`, `:memory:` reports `Volatile`.
- One writer, many readers; multi-process on one machine through `busy_timeout`. `SQLITE_BUSY` becomes `TransientStorageException(MayHaveCommitted: false)`. Network shares are unsupported.
- Not emulated: throttling, latency, SDK retries, short pages, credentials.

## Breaking changes (complete list)

| # | Break | Who is hit | Migration |
|---|---|---|---|
| B1 | No provider exception leaves a storage member, including the documented 404 of `MergeColumnsAsync` | `catch (RequestFailedException)` around storage calls; silent at compile time | Catch the neutral type; the Azure exception is `InnerException` |
| B2 | `Try*` verbs return `NotFound`, `MutateOrCreateAsync` creates, where a 404 escaped | Callers relying on the escape | A row deleted mid-mutation is a result |
| B3 | InMemory throws neutral types | Tests asserting the Azure type from the fake | Assert the neutral type |
| B4 | InMemory no longer brings the Azure provider or `Azure.Storage.Blobs`; it takes `StorageOptions` | Test projects using those transitively; `o => o.BlobOffload = …` on the fake | Reference `Unified.Data.Tables` or use the neutral offload type |
| B5 | Whole-entity update with ETag `"*"` throws | Callers passing `"*"` | Use `ConcurrencyMode.LastWriterWins` |
| B6 | Projected scans no longer return continuation rows | Callers filtering `~` keys themselves | Remove the filter |

Not broken: `IStorage<T>` implementers and decorators, mock doubles, stored rows, in-flight cursors, existing overloads. One caveat for the CHANGELOG: an older third-party implementer ignores the new range bounds and returns the whole partition.

## Work packages (library)

Critical path, in order:

1. **WP0 Golden corpus** from 0.9.2: serialized rows, cursors, OData strings. Tests only (`tests/Unified.Data.Tables.Tests/TableEntitySerializerTests.cs`, `PagingTests.cs`, `TableFilterTranslatorTests.cs`).
2. **WP1 Package split, no logic change**: create Core; move serializer, split codec, cursor, `TableFilterTranslator` (as validator with a literal-format hook; `TableClient.CreateQueryFilter` stays in the Azure package); options base; type forwards; `InternalsVisibleTo`; fix the exact-pin check in `.github/workflows/publish.yml:142`.
3. **WP2 Abstractions and one error mapper**: new exceptions, side interfaces, `DelegatingStorage<T>`, `StorageInfo`, raw types, `QueryOptions`, `PageCursor`, verb fixes in `StorageExtensions.cs`; map errors at every escape site in `TableStorage.cs`, `TableStorage.LargeValues.cs` (`:183`, `:233`, `:474`), `PolymorphicTableStorage.cs`, `AzureOffloadBlobStore.cs`.
4. **WP3 Conformance kit**, written green against the current InMemory and Azurite before anything is rebased. Lift `LargeValues/LargeBatchContractTests.cs` (already an abstract contract with 45 tests), `InMemoryFidelityTests.cs` and `LargeValueStores.cs`. Tiers: logical, capabilities, polymorphic, large values (logical half), failures with fault injection.
5. **WP4 Azure features** (parallel with WP5): per-request retry observation, range, conditional delete (existence from the response status on the plain path, from a caught 404 on the transaction path; no 412 loop on the caller-ETag path), atomic batch, info, raw and table store, `TimeProvider`, the `"*"` rule.
6. **WP5 Row-store engine and InMemory rebase** in `src/Unified.Data.Tables.InMemory/*`.
7. **WP6 SQLite provider** in new `src/Unified.Data.Tables.Sqlite/`; the kit is its acceptance test. Start with a spike on connection pooling and lock waits.
8. **WP7 Identity**: drop the catch-all probe in `ConcurrencyStamps.cs:42-62`; use conditional delete in `UserStoreBase.cs:354-358` and `:382-387` through the capability probe, falling back to today's path on stores without it.
9. **WP8 Release plumbing**: `Directory.Build.props`, `CHANGELOG.md` with BREAKING labels and upgrade notes, `README.md`, `Unified.Data.Tables.slnx`, Info-level analyzer.

Step zero, before any code: replace the note in PR #22 (`docs/design/engine-storage-gaps.md` on `docs/engine-storage-gaps`) with this design, so the PR you review is the cross-consumer one.

## Adoption PRs

Each PR is built against a local package feed before the release tag, and opened as a PR for the owner to merge.

**Cortadel** (engine and SaaS in one PR; their pins must move together).
- Engine: rewrite the Azure-type pins in `UnifiedDataTablesContractTests.cs:202-215`; `AzureTransientClassifier.cs` becomes one type test; `AzureTableSubstrate.cs:350` tells a missing row from an outage; `AzureEngineStorageProbe.cs` moves to `ITableStore`.
- SaaS: `CountingStorage` and `GatedStorage` become kit decorators; `ControlStoreTests` and `ControlStoreLiveTests` share one base; `StoredTimestamps` reflection goes; `ControlBatch.cs` becomes `CreateAtomicAsync`; the middleware maps `TransientStorageException` to 503.
- Later, not this PR: backup and restore onto `IRawTable`; a local-provider option.

**IntelliSpec.**
- `TestSupport/InMemoryStorage.cs` becomes a wrapper over the library fake with named leniency switches; the kit's failures on it are the retirement list.
- `InterceptingStorage.cs` derives `DelegatingStorage<T>`.
- `AzureTableSpecChangeJournal.cs` moves to `IRawTable` with `ToRowKey` (keeps its stored row shape).
- `InstinctStore.cs:362` uses `DeleteIfMatchAsync`; the seven existence-only sites use `DeleteIfExistsAsync`.
- Limits in `LlmRecordTableWriter.cs:57` come from `StorageInfo`.

**VSP** (two PRs; `main` deploys straight to production).
- PR A: 0.7.0 to 0.9.2 only. Deploy and soak.
- PR B: 0.10.0. The 21 catch sites and `RequestFailedExceptionHandler.cs` become one neutral handler; the direct `Azure.Data.Tables` reference goes; Development moves to SQLite. `ConfigureVspStorage` is retyped to the options base. SQLite must pass the Identity suite first.

**IntelliGrowth** (Azure DevOps).
- Bump the two pins in `cqrs/src/Intellias.CQRS.Persistence.AzureStorage/*.csproj`; mirror the new package ids in the private feed.
- `ProcessStore`, `TransactionTableStore` and `StateEventTableStore` use `InsertAtomicAsync`.
- Fakes throw neutral types; the polymorphic kit tier runs on `InMemoryPolymorphicStorage`.
- Later: `UniqueConstraintService.cs:101` and the backup tools onto `IRawTable`.

## Consumer-only defects found (listed, not fixed here)

- **VSP**: Development and Production settings carry the same storage connection string; balances are updated by unconditional merges of earlier reads; lesson side effects run before the row write.
- **IntelliSpec**: delta-proposal approvals and rejections fail on a missing ETag and a catch-all swallows it, so they never persist; 15 whole-table scans by a non-key column.
- **IntelliGrowth**: ten tracked `local.settings.json` files point at shared dev accounts, one checked holds live keys; the wire-format tests run in no pipeline; restore parses numbers with the current culture.
- **Cortadel**: a `|` inside `TableColdColumnStore` partition names misroutes ids; the live Azure gate has never run.

## Risks

| Risk | Containment |
|---|---|
| B1 is silent at compile time across 1,500 apps | First CHANGELOG item, `InnerException` always set, Info analyzer, a documented search recipe |
| The package split or InMemory rebase changes behaviour under everyone's tests | WP0 corpus stays byte-identical; the kit is green before the rebase and after |
| Rolling deploys break cursors | Tokens issued by 0.9.2 decode in 0.10.0 when no bound is set |
| `MayBeOwnWrite` read as proof of a foreign writer | Conservative default of true; documented meaning; lost-response tests for create, update and delete |
| SQLite delays the release | It is the last package and only a row-store implementation |
| The raw API shape is wrong | `[Experimental]`, changeable before 1.0 |

## Verification

- Kit tiers green on InMemory, SQLite (file and `:memory:`) and Azurite; logical, capability and polymorphic tiers run once against a real Standard storage account.
- The Identity suite passes on all three providers.
- Every existing `RequestFailedException` assertion is converted one by one, not in bulk.
- An API comparison against 0.9.2 shows only B1 to B6.
- An `IStorage<T>` implementer compiled against 0.9.2 Abstractions loads and runs on 0.10.0.
- The InMemory and Sqlite nuspecs contain no `Azure.Storage.Blobs` and no Azure provider dependency.
- A table written by 0.9.2 is read and rewritten by 0.10.0 with identical bytes.
- SQLite: a two-process write test and a kill-during-commit test.
- All four adoption PRs build and pass their tests against a local feed before the tag.

## Not measured

- Nothing was built or run for this note; it rests on a read of the five codebases at 0.9.2 (VSP at 0.7.0 on its `main`).
- No benchmark compares SQLite, Azurite and Azure under the same workload.
- Whether Azure Tables orders non-BMP key characters like ordinal UTF-16 is unverified.
