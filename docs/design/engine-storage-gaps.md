# Storage gaps seen from CortadelDB

Status: proposal, 2026-11-25. No code in this change.

CortadelDB's durable engine stores its write-ahead log on Azure Table through this library. Its table substrate (`AzureTableSubstrate` in the Cortadel repository, `cortadel-db/src/Cortadel.Db.Core/Persistence/`) compiles against `Unified.Data.Tables.Abstractions` only, and its tests run it on `InMemoryStorage<T>`. This note lists what the engine has to work around today, and proposes five additions. References to Unified are to `main` at 0.9.2; references to the engine are to Cortadel `main` on 2026-11-25.

## Why this matters for other providers

The question behind this note: could a file or SQLite provider (`Unified.Data.Tables.Filesystem`, `.Sqlite`) give the engine one interchangeable storage interface, in place of its own native file substrate?

Not as the abstractions stand. Three things stop it:

- A caller cannot tell whether a returned write is durable. The engine treats every table insert as durable on return; a local provider that buffers writes would break that silently.
- "Row not found" reaches callers as `Azure.RequestFailedException`, so a non-Azure provider must either throw an Azure type or be indistinguishable from any other failure.
- There is no test kit a new provider can run to prove it behaves like the Azure one.

Proposals 2, 4 and 5 below remove these. Whether a local provider would then be fast enough for the engine is a separate, unmeasured question: on local disk the table path costs several row writes where the native file substrate does one append and one fsync.

## Proposals

Ordered by what the engine would use first.

### 1. Range scan from a row key

**Today.** `QueryOptions` offers `Partition` and `RowKeyPrefix` only (`QueryOptions.cs:15,23`). The engine reads its log from a given position onward, so it scans the partition and drops the rows below its lower bound on the client (`AzureTableSubstrate.cs:107-109`, which names a future `QueryOptions.FromRowKey`).

**Proposal.** Add to `QueryOptions`:

```csharp
public string? FromRowKey { get; init; }
public bool FromInclusive { get; init; } = true;
public string? ToRowKey { get; init; }
public bool ToInclusive { get; init; }
```

They require `Partition` and cannot be combined with `RowKeyPrefix`. The Azure provider maps them to `RowKey ge|gt … and RowKey lt|le …`. They become part of the `PageCursor` fingerprint.

**Breaking:** no. New optional properties.

**Note.** The engine keeps its client-side check for correctness; the gain is transfer, not safety.

### 2. Provider-neutral "not found" and transient failures

**Today.** `DuplicateKeyException` and `ConcurrencyConflictException` are neutral, but a missing row on `UpdateAsync` surfaces as `Azure.RequestFailedException` 404. The engine pins that in a contract test (`UnifiedDataTablesContractTests.cs:202-215`), and `InMemoryStorage` throws the Azure type to match (`InMemoryPolymorphicStorage.cs:212`, `InMemoryStorage.LargeValues.cs:127`). Because the engine's core references Abstractions only, it cannot catch that type; its pointer compare-and-swap treats any exception as "fence the writer" (`AzureTableSubstrate.cs:351`).

**Proposal.** In Abstractions:

- `EntityNotFoundException`, thrown where a 404 escapes today, with the provider's exception as `InnerException`.
- `TransientStorageException` with `bool MayHaveCommitted`, for timeouts and throttling after the provider's own retries.

`MutationStatus.NotFound` and `TryMutateAsync` already give a non-throwing path (`MutationResult.cs:15`); the exceptions cover the throwing members. The two existing exceptions derive from `Exception` directly (`DuplicateKeyException.cs:13`, `ConcurrencyConflictException.cs:26`), so a common `StorageException` base would be new and is optional.

**Breaking:** yes, for callers that catch `Azure.RequestFailedException` from `UpdateAsync` or `MergeColumnsAsync`. Suggested path: one minor version where the new exception is thrown with the Azure exception inside and the change is announced, then document it as the contract in 1.0.

### 3. Conditional delete

**Today.** `DeleteAsync(string id, CancellationToken)` is unconditional (`IStorage.cs:34`). A caller that read a row and wants to delete exactly that version cannot.

**Proposal.**

```csharp
Task<bool> DeleteAsync(string id, string etag, CancellationToken ct = default);
```

Returns `false` when the row is gone, throws `ConcurrencyConflictException` when the ETag does not match.

**Breaking:** not for callers. For implementers it is a new member; `netstandard2.0` has no default interface members, so it goes on `IStorage<T>` in a minor version with both in-tree providers updated, or on a small separate interface.

### 4. A durability statement

**Today.** Nothing in the abstractions says whether a completed write survives a crash. No member mentions durability, flush or capabilities (the one "capability" in the source is the orphan blob sweeper's, `IOrphanBlobSweeper.cs:64`).

**Proposal.** A small interface a provider may implement, resolvable beside `IStorage<T>`:

```csharp
public enum DurabilityLevel { Volatile, OsBuffered, Fsynced, Replicated }

public interface IStorageCapabilities
{
    DurabilityLevel Durability { get; }
}
```

Azure reports `Replicated`, InMemory `Volatile`. The engine would refuse to open a durable store on anything below `Fsynced`. Batch limits and scan abilities could join this interface later; the engine has no use for them today, so they are left out.

**Breaking:** no.

### 5. A conformance kit for providers

**Today.** The fidelity tests live in this repository's test projects and compare InMemory with Azurite. The engine keeps its own contract tests that pin the behaviour it depends on (`UnifiedDataTablesContractTests.cs`). A third provider has nothing to run.

**Proposal.** A package `Unified.Data.Tables.Conformance`: an abstract xUnit class a provider's test project derives from, given a factory for `IStorage<T>`. It covers create and duplicate, both concurrency modes, a missing-row update, prefix and range scans, paging cursors, projection, batch atomicity per partition, and key rules. The engine's pins move into it.

**Breaking:** no. A new package.

## Considered and left out

- **A raw-row API** (one opaque payload per row, chunked by the provider). The engine declares eleven `byte[]` columns by hand to fit Azure's cell limit. Real, but a larger design, and the large-value support added in 0.9.0 may already cover part of it.
- **A transaction scope returning new ETags.** Batches are atomic per partition chunk and return counts (`IStorage.cs:228,247`). The engine's seam needs only single-row insert and single-pointer compare-and-swap, so it has no use for this today.
- **Public clock and metrics hooks.** `TimeProvider` is used internally (`LargeValueTable.cs:33`, `TableStorage.cs:65`) and there is no `Meter`. The engine wraps `IStorage<T>` in its own counting decorator for tests. Useful, not blocking.
- **A separate append-log package.** `AppendLogExtensions`, `VersionedStreamExtensions` and `RowKeys` already exist in Abstractions; any log abstraction should extend those.

## Not measured

- No benchmark compares the engine's native file substrate with its table substrate on any provider.
- No local provider exists, so its cost under the table substrate is unknown.
