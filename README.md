# Unified.Data.Tables

[![Build & Analyze](https://github.com/SergiySeletsky/Unified.Data.Tables/actions/workflows/sonarqube.yml/badge.svg)](https://github.com/SergiySeletsky/Unified.Data.Tables/actions/workflows/sonarqube.yml)
[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=testasm_unified-data-tables&metric=alert_status)](https://sonarcloud.io/summary/overall?id=testasm_unified-data-tables)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=testasm_unified-data-tables&metric=coverage)](https://sonarcloud.io/summary/overall?id=testasm_unified-data-tables)
[![Unified.Data.Tables on NuGet](https://img.shields.io/nuget/v/Unified.Data.Tables.svg?label=Unified.Data.Tables)](https://www.nuget.org/packages/Unified.Data.Tables)
[![Downloads](https://img.shields.io/nuget/dt/Unified.Data.Tables.svg)](https://www.nuget.org/packages/Unified.Data.Tables)

A small, reusable **Azure Table Storage** data layer for .NET: a generic `IStorage<T>` repository with
configurable in-memory caching, optimistic concurrency, upserts and batch transactions, bounded
prefix queries, builder-driven partial updates, role-gated properties, legacy-column aliases, and a
reflection-based object-graph serializer that transparently handles nested types and the 64&nbsp;KB
per-cell limit — plus a semantically faithful in-memory backend for tests.

It exists so the same battle-tested storage primitives can be shared across projects instead of being
copy-pasted into each one.

---

## Features

- **Generic repository** — `IStorage<T>` over Azure Tables, one table per entity type (`typeof(T).Name`),
  created lazily on first use (`EnsureCreatedAsync()` for fail-fast hosts).
- **Reflection-based serializer** — flattens nested objects into `Parent_Child` columns, stores enums as
  strings and money-style `decimal`s as doubles, and falls back to JSON (then GZip) for collections and
  complex graphs. Oversized cells are compressed, and an oversized payload throws rather than being
  trimmed. Opt a property into `[TableValueSplit]` to store arbitrarily large values losslessly.
- **Large values & blob offload** — `[TableValueSplit]` stores strings and byte arrays far beyond the
  64&nbsp;KB cell limit as split cells, continuation rows or an Azure Blob, SHA-256 verified on read;
  `TableBlobContent` stores files as blobs with a lazy stream; an optional hosted sweep removes orphan
  blobs. Opt-in; types that use neither are stored exactly as before (roll forward only once a type
  opts in).
- **Configurable caching** — reads are served from `IMemoryCache` per a registration-time `CachePolicy`
  (`Sliding`, `Absolute`, or `Disabled` — per entity type or globally); writes keep the entity cache
  coherent and invalidate the relevant query caches automatically.
- **Optimistic concurrency (ETag)** — a caller-supplied `ETag` enforces strict concurrency; a lost
  race throws the provider-agnostic `ConcurrencyConflictException` (map to HTTP 409). Explicit
  `ConcurrencyMode.Strict` / `LastWriterWins` overloads make intent greppable, and
  `MutateAsync(id, e => e.Count++)` packages the read → mutate → strict-write → retry loop that makes
  derived values (counters, unions) correct under concurrency.
- **Upsert & batches** — `UpsertAsync` (single round-trip insert-or-replace; a point read and a
  conditional write for a type with large values), `CreateBatchAsync` / `UpsertBatchAsync`
  (partition-grouped 100-row transactions), `CountAsync` (keys-only projection).
- **Bounded queries** — `QueryAsync(QueryOptions)` and streaming `QueryStreamAsync` with partition scope,
  canonical RowKey-prefix ranges, and `Take` — never cached, never a disguised full scan.
- **Server-side LINQ, paging & append logs** — `QueryAsync(x => x.Status == Open)` translates to an OData
  `$filter` (server-side, not a scan); `QueryPageAsync` returns a page plus a query-bound continuation
  cursor for grids and infinite scroll; `AppendAsync`/`RecentAsync` give the newest-N event-stream shape
  for free. The in-memory fake validates predicates through the same translator, so green tests hold on Azure.
- **Partial (Merge) updates** — `UpdateAsync(id, builder)` writes only the columns you declare
  (including nested paths: `x => x.Address.City` → `Address_City`), leaving the rest of the row
  untouched with no read required — concurrent writers touching disjoint columns never conflict.
  `builder.WithETag(...)` makes the merge conditional for column-level compare-and-swap.
- **Legacy column aliases** — `[ColumnAlias]` reads old column names (e.g. after a property rename) when
  the canonical column is absent; writes stay canonical, so rows converge without a migration job.
- **Protected properties** — mark a property `[ProtectedProperty("admin,...")]` and role-gate writes through
  a pluggable `IProtectedPropertyAuthorizer` (the package itself has **no** ASP.NET Core dependency).
- **Faithful in-memory backend** — `Unified.Data.Tables.InMemory` round-trips rows through the REAL
  serializer with 409/412/404 and ETag semantics, so tests exercise production behaviour.
- **ASP.NET Core Identity stores** — `Unified.Data.Tables.Identity` implements Identity's
  `IUserStore`/`IRoleStore` family (users, roles, claims, logins, tokens, two-factor and lockout) on top
  of `IStorage<T>`, registered with one `AddUnifiedIdentityStores()` call — and, because it is just
  `IStorage<T>`, the whole Identity stack runs against the in-memory backend in unit tests.
- **Composite id convention** — `Id` is `"{PartitionKey}|{RowKey}"` (shared helpers in `EntityId`); row keys
  may themselves contain `|`.

---

## Installation

```bash
dotnet add package Unified.Data.Tables
```

This is shipped as four packages:

| Package | Contents | Use it in |
| --- | --- | --- |
| **Unified.Data.Tables** | `TableStorage<T>`, the serializer, cache policies, DI helpers (Azure dependencies) | server / host projects |
| **Unified.Data.Tables.Abstractions** | `Entity`, `IStorage<T>`, `QueryOptions`, `ConcurrencyMode`, `UpdateBuilder<T>`, `EntityId`, `[ColumnAlias]`, `[ProtectedProperty]` — no Azure/hosting deps | shared/domain & Blazor WebAssembly projects |
| **Unified.Data.Tables.InMemory** | `InMemoryStorage<T>` — serializer-faithful in-memory `IStorage<T>` | test projects, dev/offline mode |
| **Unified.Data.Tables.Identity** | ASP.NET Core Identity `IUserStore`/`IRoleStore` persisted through `IStorage<T>` | hosts using ASP.NET Core Identity |

`Unified.Data.Tables` references the abstractions transitively, so most apps just install it. The
implementation packages pin each other exactly: `Unified.Data.Tables` depends on
`Unified.Data.Tables.Abstractions` `[x.y.z]` and `Unified.Data.Tables.InMemory` on `Unified.Data.Tables`
`[x.y.z]` (they share internals), so upgrade them together. Blob offload needs `Azure.Storage.Blobs`
12.29.2 or later; the optional hosted sweep uses `Microsoft.Extensions.Hosting.Abstractions`. In a **browser-safe** shared library that only defines entities and repository contracts, reference the abstractions alone.

Requires **.NET 10** and an Azure Storage account (or the local [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) emulator).

---

## Quick start

### 1. Define an entity

Derive from `Entity`. `Id` is the composite `"{PartitionKey}|{RowKey}"`; `CreatedAt`, `UpdatedAt`,
`ETag` and the service-managed `Timestamp` are maintained for you.

```csharp
using Unified.Data.Tables;

public sealed class Customer : Entity
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public decimal Balance { get; set; }
}
```

### 2. Register it

```csharp
using Azure.Data.Tables;
using Unified.Data.Tables;

// Option A — from a connection string:
builder.Services.AddUnifiedTableStorage(connectionString);

// Option B — managed identity:
builder.Services.AddUnifiedTableStorage(
    new Uri("https://myaccount.table.core.windows.net"), new DefaultAzureCredential());

// Option C — you already register a TableServiceClient yourself:
builder.Services.AddSingleton(_ => new TableServiceClient(connectionString));
builder.Services.AddUnifiedTableStorage();

// Cache policy is a DEPLOYMENT concern — configure it per host:
builder.Services.AddUnifiedTableStorage(connectionString, o =>
{
    o.Cache = CachePolicy.Absolute(TimeSpan.FromSeconds(30));   // bound cross-process staleness
    o.CacheFor<ChatMessage>(CachePolicy.Disabled);              // huge partitions — don't cache
});
```

All overloads register `IMemoryCache`, the configured options, and the open-generic
`IStorage<T>` → `TableStorage<T>` mapping as singletons.

> **Sharing tables across processes?** A second process (worker, subprocess, sidecar) writing to the
> same tables makes `Sliding` caching unbounded-stale in the readers. Use `CachePolicy.Disabled` in
> secondary processes and `Absolute` with a short TTL in the primary.

Within one process the cache follows the writes made through it: once a write or delete this process
made has returned, the cache does not answer with an older version, except for the moment described
below while an earlier, overlapping write of the same entity is still running. This holds for every
entity type, across all stores of one entity type that share an `IMemoryCache` (the DI registration
shares one), and for the cached `QueryAsync(partition)` lists:

- a read that is in flight while a write or delete of the same entity completes may still return the
  version it fetched, but it does not leave that version in the cache;
- when two writes of one entity overlap in time (create, update, upsert or `UpdateAsync(id, builder)`),
  or a write and a delete do, neither keeps the entity cached: once both have returned, the next
  `OneAsync` or `ExistsAsync` reads the table. While an earlier write of an entity is still running, a
  read made after a later write or delete of that entity has returned can, for a moment, be answered with
  the earlier write's version; once both calls have returned, the store answers what the table holds. Use
  ETags (`ConcurrencyMode.Strict`, `MutateAsync`) for an entity that several callers write at once. Writes
  that do not overlap behave as before: the entity a write returns is cached with the version the service
  returned;
- when a create, update, upsert, builder update or delete throws (a timeout or a cancelled token
  included), the store cannot know whether the row was written, so it drops the entity's entry and its
  partition's cached lists. If the cache itself throws while it does (a disposed `MemoryCache` during host
  shutdown, say), that failure is logged as a warning, and the exception the call throws is still the one
  the storage call produced, or the library's translation of it (`DuplicateKeyException`,
  `ConcurrencyConflictException`). A call refused by its own checks before anything is sent (an argument,
  a missing ETag in `Strict` mode, a protected property, a value of a plain type that cannot be
  serialized, a builder update of a large-value member) drops nothing. For a type with large values, a
  create, update or upsert that fails while its values are prepared (a stream that fails while it is
  read, a value too large to store, a cancelled token) also drops the entry, although nothing was
  written; the next read goes to the table;
- after `UpdateAsync(id, builder)` the store keeps the entity cached with the update applied, as 0.8.3
  did, only when all of this holds: the entity was cached when the update was sent, and no read or other
  write has put another version of it into the cache since; every path of the builder names a top-level
  property; and every value is a non-null scalar of the property's own type that the update writes as one
  cell in the property's own column and that a read converts back to an equal value: a `string` that the
  `OversizedCellPolicy` did not trim or compress, a `bool`, `int`, `uint`, `long`, `ulong`, `byte`,
  `double`, `Guid`, `TimeSpan` or enum (or the nullable form of one), a `decimal` that its stored `double`
  converts back to, a `DateTimeOffset` other than the instant 1601-01-01T00:00Z, or a `DateTime` that is
  neither local nor that instant. Equal means equal by the type's own equality (a `decimal` whatever its
  scale, a `DateTime` whatever its `Kind`, a `DateTimeOffset` by its instant). A read the cache answers
  returns each value as a read of the row converts it: a `decimal` with the scale a read gives (`7.5` for
  `7.50`), a `DateTime` with `Kind` `Unspecified`. A `DateTimeOffset` keeps the offset it was set with,
  where the table service returns the instant in UTC. 0.8.3 does the same. Then the store copies the
  cached entity once (its constructor and getters run once, as on 0.8.3) and sets the values on the copy.
  In every other case the store drops
  the entry without copying the entity, and the next read fetches the row: a nested path or a name that
  maps to no property, a null put into `Updates`, an object, a collection or another JSON-stored value
  (a `float`, `short`, `char`, `sbyte` or `ushort` is stored as JSON), a `byte[]`, a trimmed or compressed
  string, a `decimal` that its stored `double` does not convert back to (`0.1234567890123456789m`), the
  instant 1601-01-01T00:00Z (stored as an unset date, which a read returns as the default value), a
  local `DateTime` (stored as its UTC instant), or a value of another type than the property's. If
  the constructor or a getter throws while the copy is made, or the cache throws while the entry is put,
  the update has been stored and the entry is dropped before the exception leaves the call, as on 0.8.3 (a
  `TargetInvocationException` around the constructor's exception, or the cache's own); the next read
  returns the stored row. The entity kept cached takes the ETag the update returned only when the update
  was conditional on exactly the cached entity's version (`WithETag` with the ETag of the entity this
  store holds). In every other case (no `WithETag`, a `WithETag` naming another version, a response
  without an ETag) it keeps the ETag it had, as on 0.8.3. That ETag is older than the row's: a `Strict`
  update or `MutateAsync` with it meets a conflict, which drops the entry, and the retry reads the row. So
  after an unconditional builder update, `OneAsync` can return the updated entity with its previous ETag;
  to hold the current ETag, use the string the builder update returns, or make the update conditional on
  the version you read. A cached entity never carries an ETag newer than its content, so an optimistic
  write of a cached entity never overwrites a change it has not seen;
- `"a"` and `"a|a"` address the same row and share one entry, and `DeletePartitionAsync` evicts every row
  it listed, also when it fails part-way.

Each of these can cost one more point read. None changes a cell or the request of a write; what a read
returns changes only from a stale version to the current one, and a failed write whose cache eviction
itself throws now throws the storage call's exception (on 0.8.3 a `Strict` update that met a 412 over a
disposed cache threw `ObjectDisposedException`). What the cache does not cover:

- a write made by another process, or through a store over another `IMemoryCache`: it is not seen until
  the cache entry expires, hence the note above. A store built with `CachePolicy.Disabled` over the cache
  of a caching store does not evict that store's entry on a successful create, update or upsert;
- a write of an entity that does not round-trip through the serializer (it could not be read back
  either): it caches nothing and leaves the cached copy of the version it replaced.

### 3. Use it

```csharp
public sealed class CustomerService(IStorage<Customer> storage)
{
    public Task<Customer> Add(Customer c)          => storage.CreateAsync(c);
    public Task<Customer> Save(Customer c)         => storage.UpsertAsync(c);
    public Task<Customer?> Get(string id)          => storage.OneAsync(id);
    public Task<bool> Exists(string id)            => storage.ExistsAsync(id);
    public Task<IEnumerable<Customer>> InRegion(string region) => storage.QueryAsync(region);
    public Task Remove(string id)                  => storage.DeleteAsync(id);
}
```

```csharp
// PartitionKey = "eu", RowKey = "alice@example.com"
var customer = new Customer
{
    Id = "eu|alice@example.com",
    Name = "Alice",
    Email = "alice@example.com",
    Balance = 100m
};

await storage.CreateAsync(customer);           // ids are normalized: trim → spaces to '-' → lower-case
var loaded  = await storage.OneAsync("eu|alice@example.com");
var euOnes  = await storage.QueryAsync("eu");  // scope to a partition (omit for the whole table)
```

---

## `IStorage<T>`

| Method | Description |
| --- | --- |
| `CreateAsync(entity)` | Insert a new row (`DuplicateKeyException` when it exists); returns it with its populated `ETag`. |
| `UpsertAsync(entity)` | Insert-or-replace in one round trip. Unconditional by design (last writer wins); preserves a caller-supplied `CreatedAt`. For a type with [large values](#large-values-split-and-blob-offload) it is a point read and a conditional write, retried on a lost race; `ConcurrencyConflictException` after five. |
| `OneAsync(id)` | Fetch one row by composite id, or `null`. |
| `ExistsAsync(id)` | Whether a row exists (cache-aware). It never assembles or downloads a large value: a row with continuation rows or an offloaded value, or one whose part count or in-row value is damaged, answers `true` and is not cached by this call. |
| `QueryAsync(partition?)` | All rows, or just one partition (the cached read path). |
| `QueryAsync(options)` | Bounded query: partition + RowKey prefix + Take. Never cached. |
| `QueryStreamAsync(options?)` | Streaming variant — never caches, never buffers. |
| `QueryPageAsync(options)` | One server page + an opaque, query-bound cursor for the next (`Take` = page size, default 100). Resumable grid / infinite-scroll paging. |
| `QueryAsync(predicate, partition?, take?)` | **Server-side** LINQ filter translated to an OData `$filter` — not a client-side scan. |
| `QueryStreamAsync(predicate, partition?, take?)` | Streaming variant of the LINQ filter. |
| `AnyAsync(predicate, partition?)` | `Take(1)` existence check for a server-side predicate. |
| `AppendAsync(partition, entity, subStream?)` | Extension: append a time-ordered event (inverted-ticks RowKey); the `Id` is assigned for you. |
| `RecentAsync(partition, count, subStream?)` | Extension: read the newest N events, newest-first — one bounded partition scan. |
| `UpdateAsync(entity)` | Full replace with adaptive (`Auto`) ETag concurrency. |
| `UpdateAsync(entity, mode)` | Full replace with explicit `ConcurrencyMode`. `LastWriterWins` on a type with large values can throw `ConcurrencyConflictException` after five lost races. |
| `UpdateAsync(id, builder)` | Partial `Merge` — writes only the declared columns (nested paths supported, optional `WithETag`); returns the new ETag. |
| `MutateAsync(id, e => …)` | Extension: read → mutate → `Strict` write, re-reading and re-applying on conflict (CAS). |
| `GetOrCreateAsync(id, factory)` | Extension: read-or-insert; a lost create race converges on the winner's row. |
| `MutateOrCreateAsync(id, create, mutate)` | Extension: insert-or-mutate CAS — the delta applies uniformly on first insert and on updates. |
| `TryMutateAsync(id, e => …)` | Extension: outcome-returning CAS — `Updated` / `NotFound` / `Conflicted`, nothing thrown for expected branches. |
| `TryTransitionAsync(id, when, apply)` | Extension: exactly-once transition as a result — the race-loser gets `PreconditionFailed` with the winner's row. |
| `CreateBatchAsync(entities)` | Transactional inserts, grouped by partition, 100 per transaction, atomic per transaction (`DuplicateKeyException` on an existing or repeated key). Types with large values too: an entity's continuation rows ride in its transaction. |
| `UpsertBatchAsync(entities)` | Transactional insert-or-replace, same chunking; returns the number of entities passed (a row named twice counts twice and is written once). A type with large values reads each row first and replaces it conditionally; `ConcurrencyConflictException` after five lost races. |
| `CountAsync(partition?)` | Row count via keys-only projection (Tables has no server-side count). |
| `DeleteAsync(id)` | Delete one row (idempotent). For a type with large values it reads the row first and deletes it, with its continuation rows and blobs, on condition of the version read; `ConcurrencyConflictException` after five lost races. |
| `DeletePartitionAsync(partition)` | Batch-delete a whole partition in transactions of up to 100 rows; returns the number of entities deleted (continuation rows are not counted). Atomic per transaction only: call it again to finish one that failed. |
| `SweepOrphanBlobsAsync(partition, olderThan)` | Extension (`IOrphanBlobSweeper`): delete offloaded blobs of the partition that no row references and that are older than `olderThan`; returns the count. See [Large values](#large-values-split-and-blob-offload). |
| `SweepOrphanBlobsAsync(olderThan)` | Extension (`IOrphanBlobSweeper`): the same for every partition of the table. |

### Bounded queries

```csharp
// Chat messages for one vision, bounded by RowKey prefix:
var messages = await storage.QueryAsync(new QueryOptions
{
    Partition = visionId,
    RowKeyPrefix = "msg_",
    Take = 100,
});

// Stream a huge partition without buffering:
await foreach (var run in storage.QueryStreamAsync(new QueryOptions { Partition = visionId }))
    Process(run);
```

Results arrive in lexical (PartitionKey, RowKey) order — encode any other order into your RowKeys;
`RowKeys.InvertedTicks(now)` makes later timestamps sort FIRST, so "most recent N" is just
`QueryAsync(new QueryOptions { Partition = p, Take = n })` with no client-side sorting.
`RowKeyPrefix` requires `Partition` (a cross-partition RowKey range would be a full table scan).

### Server-side LINQ filters

`QueryAsync(predicate)` translates a strongly-typed predicate into a **server-side** Azure Tables
OData `$filter` — the service does the filtering, not a client-side scan of the whole partition. The
translation maps to the *stored* representation: an `enum` compares against its string name, a
`decimal` against its stored `double`, and a nested `x.Address.City` against the flattened
`Address_City` column.

```csharp
var open = await storage.QueryAsync(x => x.Status == Status.Open && x.Amount >= 100m);
var mine = await storage.QueryAsync(x => x.Owner == userId, partition: tenantId, take: 50);
if (await storage.AnyAsync(x => x.Email == email)) { /* ... */ }
```

Supported: `== != < <= > >=`, `&&`, `||`, `!`, and a bare `bool` member, over `string`, `bool`,
`int`/`uint`/`long`/`ulong`, `double`, `decimal`, `Guid`, `DateTime(Offset)`, and enums. Anything the
service can't filter — method calls (`StartsWith`, `Contains`), column-to-column comparisons, null
comparisons, or a JSON-backed property — throws `NotSupportedException` rather than silently degrading
to a scan. The in-memory fake validates the predicate through the *same* translator, so a green test
means the filter also runs on Azure.

To keep that "green fake ⇒ works on Azure" guarantee airtight, the translator also **rejects** the
handful of shapes where a server-side OData filter would disagree with in-memory evaluation: ordering
(`<`/`>`) on an `enum` or `ulong` (stored form isn't order-preserving), inequality or a negated comparison
(`!=`, `!(x == v)`, `!(x >= v)`) on a nullable-or-reference column (absent-column semantics differ between
Azure and in-memory evaluation), computed / get-only / `[IgnoreDataMember]` properties (no stored column),
and `x.Nullable.Value` (compare the member directly). Two remaining caveats are documented rather than blocked: `decimal` is stored and
compared as `double`, so equality can differ beyond ~15 significant digits (prefer range comparisons);
and a server-side filter on a property that still carries only a legacy `[ColumnAlias]` column won't match
until that row is rewritten. The `partition` argument is matched against stored (already-normalized) keys.

### Resumable paging

`QueryPageAsync` returns one server page plus an opaque cursor bound to the query — the canonical
grid / infinite-scroll primitive, with no load-the-whole-partition-then-slice:

```csharp
string? cursor = null;
do
{
    var page = await storage.QueryPageAsync(new QueryOptions
    {
        Partition = visionId, Take = 50, ContinuationToken = cursor,
    });
    Render(page.Items);
    cursor = page.ContinuationToken;   // null when exhausted
}
while (cursor is not null);             // loop on HasMore, not on Items.Count
```

The cursor is bound to its exact bounds (partition, RowKey prefix, page size) — replaying it against a
different query throws. There is deliberately no total count (Azure Tables has none; a total would
force a second full scan) — drive the UI off `HasMore` and use `CountAsync` only when you truly need a
number.

### Append logs

For the "append an event, read the newest N in order" shape — events, chat, agent runs, audit — the
append helpers key rows with inverted ticks so the newest sort first, making `RecentAsync` a single
bounded partition scan:

```csharp
await storage.AppendAsync("vision-42", new ChatMessage { Text = "hi" }, subStream: sessionId);
var latest = await storage.RecentAsync("vision-42", 20, subStream: sessionId); // newest first
```

The optional `subStream` lets one partition hold several independent streams (e.g. per session) that
`RecentAsync` isolates by RowKey prefix.

### Versioned streams

For append-only, per-stream versioned snapshots — event-sourced read models, "state as of version N"
— derive from `VersionedEntity` (or implement `IVersionedEntity`) and use the versioned-stream
extensions. The stream id is the partition; the version becomes the RowKey via
`RowKeys.VersionKey(version)` — inverted and zero-padded (a stable wire format, byte-compatible with
the common hand-rolled `int.MaxValue - version` scheme), so the **newest version sorts first** and
every read below is a single bounded, server-side operation:

```csharp
public sealed class OrderSnapshot : VersionedEntity { public string State { get; set; } = ""; }

await storage.AppendVersionAsync("order-42", new OrderSnapshot { Version = 3, State = "packed" });
// versions are immutable: appending an existing version throws DuplicateKeyException

var latest = await storage.LatestAsync("order-42");          // newest snapshot, 1 bounded read
var exact  = await storage.AtVersionAsync("order-42", 2);    // or null
var asOf   = await storage.AtOrBeforeAsync("order-42", 5);   // highest version <= 5 ("state as of")
await foreach (var s in storage.HistoryAsync("order-42", take: 10)) { /* newest first */ }
```

Like the append-log helpers, these are thin compositions over `IStorage<T>` — no new interface, no
separate backend — so caching, the outcome verbs, and the in-memory fake work unchanged. Throwing
variants (`GetLatestAsync`, `GetAtVersionAsync`, `GetAtOrBeforeAsync`) raise `KeyNotFoundException`.
For case-sensitive stream ids, configure `IdNormalization.AsWritten` (the all-digit version segment
is unaffected either way). Adopting a pre-existing inverted-key table: the key-addressed reads work
over legacy rows as-is, but `AtOrBeforeAsync` filters on the `Version` column (present on every row
the pack writes) — backfill it on foreign rows before relying on "state as of" there.

### Partial updates

Only the properties you set are written; everything else on the row is preserved, and no read is
needed. Nested access writes the flattened column (`Address_City`), leaving sibling columns of the
nested object untouched.

```csharp
await storage.UpdateAsync("eu|alice@example.com", b => b
    .SetProperty(x => x.Balance, 250m)
    .SetProperty(x => x.Address.City, "Lviv"));

// Conditional merge — column-level compare-and-swap:
var read = await storage.OneAsync(id);
await storage.UpdateAsync(id, b => b
    .WithETag(read!.ETag!)                 // ConcurrencyConflictException if the row moved on
    .SetProperty(x => x.Name, "Alice A."));
```

### Optimistic concurrency

`OneAsync`/`QueryAsync` populate `Entity.ETag`. Pass it back on `UpdateAsync(entity)` for a strict
check — a concurrent modification throws `ConcurrencyConflictException` (the provider's 412 rides
along as `InnerException`; a type with large values reads the row first and, when that read already
shows another version, has no provider exception to attach), and the conflicting row's cache entry is
evicted so the next read is fresh.

```csharp
var c = await storage.OneAsync(id);
c!.Balance += 100;
await storage.UpdateAsync(c);   // ConcurrencyConflictException if the row changed since it was read
```

> **⚠️ Since 0.6.0, `UpdateAsync(entity)` with no ETag throws `InvalidOperationException`.** `Auto`
> mode has no version to check against without one, and silently writing unconditionally was
> lost-update territory — so the contract violation now surfaces loudly instead. If you read, mutate
> a field, and write it back, round-trip the ETag (`OneAsync`/`QueryAsync` populate it) — or use
> `MutateAsync`, which owns the read→mutate→strict-write loop for you. When you genuinely mean "make
> the row look like this object regardless of its current state", say so explicitly with
> `UpdateAsync(entity, ConcurrencyMode.LastWriterWins)`. Migrating a large codebase? Set
> `UnifiedTableStorageOptions.ImplicitLastWriterWins = true` to temporarily restore the pre-0.6.0
> fallback (unconditional replace + a warning log) while you convert call sites.

For values *derived* from the current row (counters, unions, merges), use the packaged
compare-and-swap loop — it re-reads and re-applies on conflict, so no increment is ever lost:

```csharp
await storage.MutateAsync(id, e => e.OccurrenceCount++);   // read → mutate → Strict write, ≤3 attempts with jittered backoff
```

### Outcome verbs — expected situations as return values

The expected branches of concurrent programs — *already exists*, *gone*, *someone got there
first* — come back as return values instead of exceptions, so a forgotten catch can never turn an
expected race into a 500:

```csharp
// Idempotent create — a lost create race converges on the winner's row:
var member = await storage.GetOrCreateAsync(id, () => new MemberEntity { /* ... */ });

// Insert-or-mutate CAS — the delta behaves identically on first insert and on updates:
await storage.MutateOrCreateAsync(id,
    create: () => new FeedbackEntity { OccurrenceCount = 0 },
    mutate: e => e.OccurrenceCount++);

// Exactly-once transitions — the race-loser is an EXPECTED branch, not an exception:
var result = await storage.TryTransitionAsync(gateId,
    when:  g => g.Status == "open",
    apply: g => { g.Status = "resolved"; g.ResolvedBy = userId; });
// result.Status: Updated | PreconditionFailed (carries the winner's fresh row) | NotFound | Conflicted
```

Losing the race to the *same* transition reports `PreconditionFailed` (the retry re-reads, sees
the precondition no longer holds, and returns the winner's row) — `Conflicted` is reserved for a
genuinely hot row whose precondition still held every attempt.

For a type with [large values](#large-values-split-and-blob-offload), a read whose row kept changing
throws `ConcurrencyConflictException` (see [Ordering and integrity](#ordering-and-integrity)).
`MutateAsync`, `MutateOrCreateAsync`, `TryMutateAsync` and `TryTransitionAsync` count such a read as a
lost race, exactly like a conflict of their write: it uses one of the `maxAttempts` attempts and is
retried after the same backoff. When the attempts run out, `TryMutateAsync` and `TryTransitionAsync`
return `MutationStatus.Conflicted` (nothing thrown), and `MutateAsync` and `MutateOrCreateAsync` throw
`ConcurrencyConflictException`. Any other failure of the read (`TableValueIntegrityException`, a
cancelled token) leaves the helper at once. `GetOrCreateAsync` has no attempt budget: a
`ConcurrencyConflictException` from its read leaves it, as it leaves `OneAsync`.

### Choosing a write strategy (concurrency cookbook)

| Your write looks like… | Use | Why |
| --- | --- | --- |
| Writers touch **different fields** of the same row (PATCH endpoints, per-subsystem status fields, progress columns vs. approval flags) | `UpdateAsync(id, builder)` | Merge is atomic per request; disjoint columns from concurrent writers both land — no ETags, no retries, no clobber |
| The new value is **computed from the current one** (counters, evidence unions, weighted merges) | `MutateAsync(id, e => …)` | Merge would persist a value computed from a stale read; CAS re-reads and re-applies until it wins |
| Mutating **inside a JSON-serialized column** (an item in a `List<>` property) | Remodel as row-per-item (RowKey prefix + `QueryOptions`), or `MutateAsync` | The column is the unit of atomicity — two writers editing different items of one list still clobber |
| A transition that must happen **exactly once** (approve, resolve, finalize) | `TryTransitionAsync(id, when, apply)` | The loser is an EXPECTED branch: it gets `PreconditionFailed` carrying the winner's row — three switch arms, no catch. (Raw `UpdateAsync(entity, ConcurrencyMode.Strict)` remains the primitive when you want the loser to throw.) |
| **Create-if-absent** (member registration, idempotent provisioning) | `GetOrCreateAsync(id, factory)` | A lost create race converges on the winner's row instead of throwing `DuplicateKeyException` |
| **Insert-or-apply-delta** (occurrence counters, feedback dedupe) | `MutateOrCreateAsync(id, create, mutate)` | The delta applies exactly once per attempt, uniformly on first insert and on every later call |
| Deliberate unconditional overwrite (convergent upserts, supersede sweeps) | `UpdateAsync(entity, ConcurrencyMode.LastWriterWins)` | Explicit and greppable. Nulling the ETag to "turn off" concurrency throws since 0.6.0 — LWW must be spelled out |

`Entity.Timestamp` mirrors the service-managed last-write time: populated on every read, reset to
`null` on writes (write responses don't carry it), never stored as a column. Unlike `UpdatedAt` it is
bumped by ANY storage write — including migrations — and cannot be set by clients.

---

## Legacy column aliases

Renamed a property (or adopted this package with pre-existing rows)? Declare the old column name and
reads fall back to it whenever the canonical column is absent — writes always use the property name,
so rows converge to the canonical schema as they are rewritten. No migration job.

```csharp
// Property you own:
public sealed class Customer : Entity
{
    [ColumnAlias("FullName")]                       // rows written before the rename
    public string Name { get; set; } = "";
}

// Inherited property — the class-level form targets properties on base types:
[ColumnAlias(nameof(Entity.CreatedAt), "LegacyStamp")]
public sealed class LegacyCustomer : Entity { }
```

The `Entity` base class itself ships with `Created` → `CreatedAt` and `Modified` → `UpdatedAt`
aliases, so rows written by pre-0.3.0 serializers deserialize correctly out of the box — no
per-project opt-in needed.

The canonical column wins unconditionally when both exist. Aliases cover the `__Json`/`__GZip` cell
variants and, for a `[TableValueSplit]` or `TableBlobContent` property, the split cells, continuation
rows and blob reference stored under the old name (see
[Large values](#large-values-split-and-blob-offload)). They apply to top-level columns only. A
declaration is checked the first time the type is written or a row of it is read, not when the store is
constructed: a collision or an unknown target then throws `InvalidOperationException`.

---

## Protected properties

Gate sensitive columns behind roles. Enforcement runs on the whole-entity update/upsert paths and
needs an `IProtectedPropertyAuthorizer`; if none is registered, changing a protected value is denied.

```csharp
public sealed class Employee : Entity
{
    public string Name { get; set; } = "";

    [ProtectedProperty("admin,accountant")]
    public decimal Salary { get; set; }
}
```

Provide an authorizer from your host (this is where the `ClaimsPrincipal` lives — kept out of the package
so it stays dependency-light):

```csharp
public sealed class RoleAuthorizer(IHttpContextAccessor accessor) : IProtectedPropertyAuthorizer
{
    public bool IsAllowed(string roles) =>
        roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
             .Any(role => accessor.HttpContext?.User.IsInRole(role) == true);
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IProtectedPropertyAuthorizer, RoleAuthorizer>();
```

For the builder path, protected properties are rejected unless you opt in after verifying authorization:

```csharp
await storage.UpdateAsync(id, b => b.AllowProtected().SetProperty(x => x.Salary, 5000m));
```

Batch writes bypass protected-property enforcement (per-row reads would defeat the batch) — treat
them as trusted server-side paths.

A protected property counts as changed when its new value does not `Equals` the stored one, and the
refusal quotes both values; a `byte[]` counts as changed only when its bytes differ, on both stores and
for every type. A protected [large value](#large-values-split-and-blob-offload) (a
`TableBlobContent`, or a value opted in with `[TableValueSplit]`) is compared by content instead: a file
is unchanged when it is the same blob (same SHA-256, length and content type), a `byte[]` when it has the
same bytes, a string as always. So saving an entity as it was read, with only other properties changed,
is not refused. A `TableBlobContent` made from a stream that has not been written yet has no known hash
and counts as changed (the stream is not read and no blob is downloaded to compare); so does removing
the file or setting another one. The refusal of such a change gives the kind and length of both values
("from a string of 700,000 characters to a string of 650,000 characters"), never the values.

---

## Serialization

`TableStorage<T>` uses `TableEntitySerializer` under the hood, but you can call it directly:

```csharp
TableEntity row = customer.ToTableEntity(partitionKey, rowKey);
Customer     back = row.FromTableEntity<Customer>();
```

- **Scalars** map to native cells; **enums** become strings; **`decimal`** is stored as `double`.
- **Nested objects** fan out to `Parent_Child` columns.
- **Collections / complex graphs** serialize to JSON (`__Json` column suffix), GZip-compressed
  (`__GZip`) when they exceed the cell limit.
- **Legacy tolerance** — a stored date surfaced by the SDK as a `DateTime` or a `string` still
  deserializes into a `DateTimeOffset` (or `DateTime`) property without throwing.
- Set `persistType: true` to embed the type name and use the late-bound `FromTableEntity()` overload.
- `TableEntitySerializer.FlattenProperty` is public for alternative `IStorage<T>` implementations.
- **A leading `_` is reserved.** `_`-prefixed columns belong to the storage layer and are never
  written into a property. Before 0.8.0, `_TypeName` was parsed as property path `["TypeName"]`, so a
  type declaring a `TypeName` property silently received the assembly-qualified name as its value —
  the same held for any `_`-prefixed sentinel. A property cannot produce such a column either: `_` is
  also the column-path delimiter, so since 0.8.1 the writer refuses any property whose name contains
  `_` (`_Foo`, `Foo_Bar`) with `SerializationException` naming the property. Keep `_` out of property
  names.
- **`persistType: true` writes `_TypeName`** with `Type.AssemblyQualifiedName`. `FromTableEntity<TBase>(discriminator)`
  reads it back constrained to a base type, and `TryFromTableEntity<TBase>` additionally tolerates a
  row that carries no discriminator at all.
- A base-constrained read does **not** recompute `Id` from the row keys, because a polymorphic key
  (an aggregate version, an inverted tick count) is unrelated to any property.

---

## Large values: split and blob offload

A table cell holds 64&nbsp;KB and a row 1&nbsp;MB. By default an oversized value throws
(`OversizedCellPolicy.Throw`); nothing is ever trimmed. Opt a property in and it is stored
losslessly instead. A store plans large values (split cells, continuation rows, blobs) only for an
entity type that is *large-value aware*, called *split-aware* below; every other type is a *plain* type.
Whether a type is aware is decided from its declared types alone, by reflection (below). Plain types produce the same
columns and make the same SDK calls as in 0.8.3, with deliberate differences that apply to every type: a
projected query no longer names `odata.etag` in its `$select` (the entities still carry their `ETag`),
the read cache corrections described under [Register it](#2-register-it), the reads of `TableStorage<T>`
skipping rows that carry a `_SplitOf` cell (see [Limitations](#limitations)), and no `byte[]` shared
between a caller and the read cache or the rows of the in-memory store (the read cache does share a
`TableBlobContent`, and with it the array of a `FromBytes` content, see [Files](#files-tableblobcontent)).

> **Roll forward only.** Rows written split or offloaded cannot be read correctly by 0.8.x or older.
> Deploy the 0.9.0 package bump first with no attributes, and add `[TableValueSplit]` or
> `TableBlobContent` in a later deploy, once no older reader (a staging slot, a rollback target, another
> service) can reach the table. See [Upgrading, backups and restores](#upgrading-backups-and-restores).

```csharp
public sealed class Document : Entity
{
    [TableValueSplit(SplitEncoding.Text)]                 // large text
    public string Body { get; set; } = "";

    [TableValueSplit(SplitEncoding.Gzip, Offload = BlobOffload.Never)]  // never leaves the table
    public string Notes { get; set; } = "";

    public TableBlobContent? Attachment { get; set; }     // a file: always a blob
}
```

The attribute goes on a property or on a class. On a property it must be a `string`, a `byte[]` or a
type stored as one JSON cell (a collection, or a concrete type that does not flatten into columns).
Anywhere else it is a misuse, reported when an entity of the type is written or a row of it is read
(from the first such use, and on every later one until the attribute is fixed), not when the store is
constructed or resolved from DI: `InvalidOperationException` on a native scalar, on a concrete type that
flattens into columns and on a `TableBlobContent`; `NotSupportedException` on an interface or abstract
type, which may flatten at runtime. The message names the member.

The attribute on a property declared on an interface is a misuse too (`InvalidOperationException`): .NET
does not pass the attributes of an interface member to the class that implements it, so the library
cannot honour it. It makes no type large-value aware and lists no large-value column. It is reported
whenever the serializer flattens a class that implements the interface into columns: a write of such an
instance as the entity or in a property that flattens into columns (at any size, under every
`OversizedCellPolicy`; the value is never trimmed), or a read that creates or fills one, before anything
is stored. An instance held in a JSON-stored property (a collection, or a type stored as one JSON cell)
is not checked: it is written as JSON like every other value of that cell, and a trimming
`OversizedCellPolicy` can trim that cell. The message names the interface member (`INote.Notes`), the
implementing class and the remedy: put `[TableValueSplit]` on the property of the implementing class (or
on that class) and remove it from the interface; where an entity holds the implementation in a property
declared as the interface (or as another type), also mark the entity class `[TableValueSplit]` so that
the entity is large-value aware. A value that needs split cells or a blob is then stored only when a read
returns it into a large-value member: give that property an instance of the implementing class on a new
entity (the refused writes below list the other declarations that work). On a read below the entity
itself, the misuse arrives as the inner exception of a `SerializationException` ("Failed to set ..."), as
for every misused attribute of a nested type. An attribute on an abstract property, or on a base-class
property that a derived class overrides or hides, is honoured (below).

A builder partial update (`UpdateAsync(id, builder)`, on `TableStorage<T>` and `InMemoryStorage<T>`) meets
the misuse through the declared types: when the entity type, or the declared type of a property along the
updated path, is or implements an interface that declares a member marked `[TableValueSplit]`, the update is
refused with the same `InvalidOperationException` before anything is written, under every
`OversizedCellPolicy` and at every size, also for a path to another member of that type; the message names
the interface member and the remedy. `InMemoryStorage<T>` looks at the row first: when the row does not
exist it throws `RequestFailedException` (404), and when `WithETag` names another version
`ConcurrencyConflictException`, as for every builder update; `TableStorage<T>` reports the misuse in both
cases.

This check reads declared types and attributes by reflection only, so for a plain entity type it does not
see a property declared as a base class or `object` whose value at run time is such a class: a path
through it (`x => x.Doc.Notes`) writes an ordinary cell, which a trimming `OversizedCellPolicy` can trim
(the library constructs nothing for such a type). When the entity's constructor puts an instance of the
implementing class in that property, every later read of the row then reports the misuse (a
`SerializationException` whose inner exception is the `InvalidOperationException`), so the row cannot be
read until the attribute is fixed; otherwise a read creates the declared type and returns the value as an
ordinary cell, with no report. Marking the entity class `[TableValueSplit]` makes such a property seen
only when the entity's constructor puts an instance of the implementing class in it: a property that is
empty on a new entity is still not seen. Declare the property with the implementing class, or mark the
entity class and initialise the property with such an instance, and fix the attribute as the message says.

On a class the attribute covers every `string`, `byte[]` and JSON-stored property of the class, whether
the class declares the property or inherits it, except the entity `Id`, which stays an ordinary key
column that can be filtered. A derived class inherits the attribute of its base class; when it declares
its own, that one applies to all its properties. An attribute on a property wins over the one on its
class (encoding and `Offload`), and a property that a derived class overrides or hides (`new`) keeps the
attribute of the property it re-declares. Every covered property is a large property, which cannot be
used in a LINQ filter or in a builder partial update (see [Limitations](#limitations)): to keep a
`string` property filterable, put the attribute on the properties that need it instead of on the class.

An entity type is large-value aware when it, or a type it flattens into columns (a nested object
property), has a `[TableValueSplit]` property, a `TableBlobContent` property or a class-level
`[TableValueSplit]` that covers one of its properties; or when the entity class itself (or a base class)
carries `[TableValueSplit]`, also when that covers none of its own properties. The answer is read from the
declared types by reflection, so it is the same in every process and at every moment.

The library runs none of a plain type's code (constructor, static constructor, property getters, of the
entity type or of the types of its properties) except where 0.8.3 runs it: when a read materializes a row,
when an entity you passed in is serialized, and when `UpdateAsync(id, builder)` keeps a cached entity warm
(see [Register it](#2-register-it)), which copies that entity once and so constructs it once, as 0.8.3
did. Constructing a store (also when a DI container resolves it), `DeleteAsync`, `ExistsAsync` of a
missing row, `CountAsync` and `DeletePartitionAsync` construct nothing, and neither does a builder update
that does not keep a cached entity warm, so whatever the entity's constructor does cannot change, break or
delay those calls. For a large-value aware type the store constructs the entity once, with its public
parameterless constructor, when it first needs the list of its large-value columns, and reads the
properties that may hold a nested object, to learn the columns of the objects the constructor puts there.
If the constructor or a getter throws then, the exception is ignored, the columns of the declared types
stand for that operation, and a later use tries again; it never makes the store treat the type as plain.
Once the constructor succeeds, replaces and deletes release the blobs of members of runtime subtypes again
(a blob that an operation during the failure did not release is left to the orphan sweep). A write of
such a type may construct the entity too (and the declared type of an empty nested property), once for
each column of a `TableBlobContent`, and of a `[TableValueSplit]` value below a nested object (or of a
subclass written through a store of its base type), that has not been resolved before, to learn whether a
read returns that column into a large-value member (see the refused writes below). While that
constructor or a getter throws, such a write of a value below a nested object, or of such a subclass,
that needs split cells or a blob (every `TableBlobContent` does) is refused with `NotSupportedException`
naming the column, before anything is uploaded or stored. It carries no inner exception; for an entity of
the store's own type its message says that a read cannot create the object that owns the value. A value
of one cell is written, and so is every top-level value of an entity of the store's own type, a file
included. The failure is not remembered: write the entity again once the constructor succeeds. A
builder partial update of such a type may construct
the entity (and the declared type of an empty nested property on the updated path) once per column it
names for the first time, to learn whether the column is a large value as a read would see it, and copies
a cached entity it keeps warm once, as for a plain type. While that constructor or a getter throws, or an
object along the path cannot be created (an empty property declared as an interface or an abstract type),
a builder update that names a column the store has not resolved before (a top-level one too) is refused
with `NotSupportedException` before anything is written. Its inner exception is the failure (for a
throwing constructor, a `TargetInvocationException` whose inner exception is the constructor's): the
library cannot tell whether the column is a large value, which a partial update cannot write. The failure
is not remembered: replace the entity instead, or send the update again once the constructor succeeds. A
column resolved once is remembered and needs no constructor again. A misused `[TableValueSplit]` that this
resolution meets is not wrapped: the update throws the exception a whole-entity write of the type reports.
Such a constructor should have no side effects.

A member of a runtime subtype can be a large value too. When a property is declared as an interface, an
abstract class or a base class, its `[TableValueSplit]` and `TableBlobContent` members are those of the
object it holds. The declared types do not show such a member, so it does not make the entity aware: opt
the entity in with a class-level `[TableValueSplit]`. A read does not know the held object's type either:
it keeps the instance the entity's constructor puts in the property, and creates the declared type where
there is none. So initialise such a property with an instance of the type that has the members (without
one, a value of such a member that needs split cells or a blob is refused, see the refused writes below):

```csharp
public interface IShape { }

public sealed class Circle : IShape
{
    [TableValueSplit(SplitEncoding.Text)]
    public string Notes { get; set; } = "";
}

[TableValueSplit(SplitEncoding.Text)]                   // opts Drawing in
public sealed class Drawing : Entity
{
    public IShape Shape { get; set; } = new Circle();   // the instance a read keeps
}
```

`Shape_Notes` is then stored and read back at every size, and a projection that names `Shape_Notes` or
`Shape` returns it. The class-level attribute has its usual cost: it also covers the entity's own
`string`, `byte[]` and JSON-stored properties, which become large values (a value that fits one cell is
still one ordinary cell, but no LINQ filter and no builder partial update on them). To avoid that, mark
one of the entity's own properties instead (a `string`, `byte[]` or JSON-stored property you do not filter
on, or a `TableBlobContent` property): the entity is then aware by its declared types, the value below the
runtime subtype is stored and read back at every size, and every other property stays an ordinary column.
Or declare the property with the type that carries the member (`public Circle Shape { get; set; }`).
`[TableValueSplit]` on the property that holds the subtype is not an opt-in: on an interface, an abstract
class or a type that flattens it is the misuse described above.

A value too large for one cell is stored only where a read returns it into a large-value member, and a
store of a plain type stores no large-value form at all. These writes are refused with
`NotSupportedException` naming the column or the property, before anything is uploaded or stored:

- Through a store of a plain type, a value in a `[TableValueSplit]` member or a `TableBlobContent` value
  below a runtime subtype, whatever its size: a 10-character value is refused as a 700,000-character one
  is, so such a model fails on its first write of the member, not on its first large value. A member that
  holds null is not refused. A builder update that sets such a value
  (`SetProperty(x => x.Shape, new Circle { Notes = "..." })`) is refused the same way. The message names
  the member, where it sits and the remedies above.
- Below an object whose type a read would not recreate, a value that needs split cells or a blob (every
  `TableBlobContent` does). A read keeps the instance the constructor puts in the property and creates the
  declared type where there is none, and the value is stored only when that object carries it as a
  large-value member: where the object has no member of that name, a read would drop the value, and where
  its member of that name is not a large-value member, a read would bring the value back into a member
  that cannot carry it (the store's own copy of the entity for its cache, and the next save of the entity
  that was read, would trim it or fail). The usual shape is an entity opted in with a class-level
  `[TableValueSplit]` whose property is declared as a base class and empty on a new entity
  (`public MemoBase? Doc { get; set; }`), written holding a subclass that marks the member
  (`MemoMarked : MemoBase` with `[TableValueSplit] override string? Notes`). The same holds when the
  property's default instance is of the base class and for such a property deeper in the graph, for a
  `string`, `byte[]` or JSON-stored member, and under every `OversizedCellPolicy`. For such a value the
  message names the declarations that work: initialise the property with an instance of the runtime type
  in the constructor of its owner (`public MemoBase Doc { get; set; } = new MemoMarked();`), or declare the
  property as the runtime type (`public MemoMarked? Doc { get; set; }`); where the type a read creates has
  the member, it also names both types and a third remedy, `[TableValueSplit]` on that member of the
  declared type. A value of the same member that fits one cell is an ordinary cell, as it always was (a
  read drops the members the created type does not have), and is not moved to split form to make a
  crowded row fit (see [Tiers](#tiers)).
- Below an object a read cannot create at all, a value that needs split cells or a blob: a struct held in a
  nullable property (`public Attachment? Extra { get; set; }`), or an interface- or abstract-typed property
  that holds no instance on a new entity. A value of one cell there is an ordinary cell, as on 0.8.3 (a
  nullable struct then reads back null; a property with no instance fails the read with
  `SerializationException`, as for its ordinary cells). A struct in a nullable property does not make the
  entity aware (`Attachment?` is not `Attachment`), so a plain entity refuses a value in that member at
  every size: declare the property as `Attachment`, not nullable (that makes the entity aware and
  round-trips the value). Marking the entity `[TableValueSplit]` does not help: a value below a nullable
  struct that needs split cells or a blob is still refused, and one of one cell reads back null. When the
  struct is reached through a property declared as an interface, an abstract or a base class, the message
  names both declarations that work: the struct property as the struct itself, not nullable, and the
  holder property with the type that has it.
- A `TableBlobContent` in a property declared as `object` (the only other type that can hold one, since
  `TableBlobContent` is sealed), on every store, aware or not; marking the class `[TableValueSplit]` does
  not change that. The message says to declare the property as `TableBlobContent`, the only type through
  which a content is stored. For a subclass written through a store of its base type it names the
  declaring class: when the base declares the property, declaring it there is enough; when only the
  subclass declares it, store the subclass through an `IStorage` of its own type as well. When the
  refusal concerns a `TableBlobContent` below an object held in a property declared as `object` (or
  another type that is not the holder's), declare that property with the holder's type as well: a read
  creates an object only for a property whose declared type it can construct.
- A subclass written through a store of its base type (`IStorage<TBase>`): a read creates a `TBase`, so a
  store never stores a value of a subclass that it could not read back. When `TBase` is plain, a value in
  any large-value member of the subclass is refused whatever its size; a subclass that opts into nothing is
  written as on 0.8.3 (its extra members are cells that a read through the base store drops). When `TBase`
  is aware, a value that would leave its cell (split cells, continuation rows, a blob; every
  `TableBlobContent` is one) is refused in a column only the subclass declares, before the caller's stream
  is read, and in a member the subclass marks `[TableValueSplit]` and `TBase` declares without marking it
  (a read would bring it into `TBase`'s ordinary member); a value of one cell is an ordinary cell (a read
  drops one of a column only the subclass declares). Store such entities through an `IStorage<TDerived>`,
  after marking `TDerived` `[TableValueSplit]` when it is not aware itself; for a member `TBase` declares,
  marking it on `TBase` works too. This holds for `CreateAsync`, `UpsertAsync`, `UpdateAsync`,
  `CreateBatchAsync` and `UpsertBatchAsync` on both stores.

In `CreateBatchAsync` and `UpsertBatchAsync` the refusal comes before anything of the refused entity's
partition is uploaded or written; the transactions of partitions the batch wrote before it stay committed,
as for any failing transaction of a batch. A file refused because a read could not map its column may
have had its source read first (see [Files](#files-tableblobcontent)).

`[ColumnAlias]` works with large values. `[ColumnAlias("Old")]` on a renamed `[TableValueSplit]` or
`TableBlobContent` property, or the class-level `[ColumnAlias("Property", "Old")]`, reads a value stored
under the old column in every form: one cell, `Old__Json` or `Old__GZip` (written by a type that had not
opted in), split cells, continuation rows, or a blob reference `Old__blob`. A projection that names the
renamed property returns it. A read moves nothing. The next save through the new type stores the value
under the new column and drops the old cells and continuation rows with the replaced row; an offloaded
value is uploaded again under the new column's blob name (a `TableBlobContent` is copied from the old
blob), and the old blob is deleted after the new row has committed, subject to `CleanupGrace`.

`Offload = BlobOffload.Never` forbids tier 3 below; a value that would need it throws
`OversizedValueException` (a `SerializationException`). The same exception reports every entity that is
too large to store: also one whose ordinary cells, which cannot leave the main row, exceed one entity.
Its `ColumnName` (a column of the entity), `EncodedBytes` and `Reason` describe what does not fit, and
its message ends with what to do. `OffloadForbiddenByProperty` is `true` when blob offload is configured
and the property forbids it (set its `Offload` to `BlobOffload.AboveThreshold`). When it is `false`,
follow the message: configure `UnifiedTableStorageOptions.BlobOffload` when a value needs a blob and no
offload is configured; mark the large properties (or the class) `[TableValueSplit]`, or store less in
one entity, when the bytes of the cells that must stay on the main row are what does not fit (for a value
that a read would not return into a large-value member, see [Tiers](#tiers)). When their
bytes fit but the main row has no property left for the cells of its split values (see
[Tiers](#tiers)), `ColumnName` is the largest split value, `EncodedBytes` its own encoded size, and the
message says how many properties its cells need and how many of the 252 the other properties leave. The
remedy then is fewer properties in one entity or, with offload configured and the property's `Offload` not
`BlobOffload.Never`, a `BlobOffloadOptions.ThresholdBytes` below the value's encoded size, so that it is
offloaded (one cell; every other split value above the new threshold is offloaded too).

### Tiers

For each opted-in property the smallest tier that fits is used:

1. **Inline** - one cell, exactly as before, when the value fits.
2. **Split** - cells `{Prop}__c0..cN` (strings in chunks of at most 32,000 chars, never splitting a
   surrogate pair; `byte[]` as raw binary cells of at most 65,536 bytes), plus the four manifest cells
   `{Prop}__chunks`, `{Prop}__sha256`, `{Prop}__enc` and `{Prop}__len`. When the cells do not fit one
   row, the rest go to continuation rows with RowKey `{RowKey}~0001`, `~0002`, ... in the same partition,
   written in **one** entity-group transaction with the main row.
3. **Blob offload** - when the encoded value exceeds `BlobOffloadOptions.ThresholdBytes` (default
   3,145,728) or the row would not fit one transaction, the row holds only `{Prop}__blob` (reference
   JSON: path, sha256, length, content type, encoding) and the bytes live in an Azure Blob.

Many opted-in values that each fit one cell can exceed one entity together (twenty strings of 30,000
characters are 1.2&nbsp;MB). When the cells that must stay on the main row, which is every cell but the
chunks of split values, exceed one entity (1,000,000 estimated bytes), the largest of the values that
sit in one cell are stored in split form instead (one chunk and the manifest), only as many as it takes,
so that their chunks can go to continuation rows; tier 3 then applies to them as to any split value. A
row that fits one entity is stored exactly as before: every value in its own cell, one row, no manifest.
A moved value keeps four cells on the main row where it had one, so the limit of 252 properties can be
what stops it; the `OversizedValueException` then says so. A value that a read would not return into a
large-value member (see [above](#large-values-split-and-blob-offload)) is never moved: it stays in its
cell, the other values are moved instead, and when moving them is not enough the write throws
`OversizedValueException` before anything is stored. When the bytes of the cells that must stay on the
main row are what does not fit, the exception names the largest of those cells, and its message advises
marking the large properties (or the class) `[TableValueSplit]`, or storing less in one entity. When that
cell is such a value, the value is opted in already, and marking the entity class does not reach a value
below a nested object: to let that value move, use a remedy that the refusal of such a value names
(above; below a nested object, an instance of the runtime type in the property that holds it, put there
by the constructor, that property declared with the runtime type, or `[TableValueSplit]` on the member of
the declared type). Marking the other large properties, or the class, still lets the other values move,
which can be enough.

One entity holds 252 properties besides `PartitionKey`, `RowKey` and `Timestamp`. A split value keeps its
four manifest cells on the main row besides its chunks, a row with split values keeps its part count
(`_SplitParts`) there too, and an offloaded value keeps one cell (`{Prop}__blob`). So an entity with P
other properties holds S split values while P + 4 × S + 1 ≤ 252: one with only the properties of `Entity`
(`Id`, `CreatedAt`, `UpdatedAt`) holds 62 split values and refuses 63. The planner does not fall back to a
blob below the offload threshold when the properties do not fit; a value above `ThresholdBytes` is a blob
reference from the start.

"Fits one transaction" is measured as the SDK sends it, not only as the service stores it: the SDK
JSON-escapes every non-ASCII character and `"`, `\`, `<`, `>`, `&`, `'`, `+` and `` ` `` as `\uXXXX`, so
such text is up to six bytes per character on the wire (and base64 binary four bytes per three). A
transaction is planned within the 4&nbsp;MiB payload limit (less 64&nbsp;KiB for framing), and the deletes
of stale continuation rows count toward it; split cells are placed so that each row's request body also
stays within 1&nbsp;MB, which the storage emulator enforces. So 700,000 characters of Cyrillic text
(1.4&nbsp;MB as UTF-8, 4.2&nbsp;MB on the wire) is offloaded, or throws `OversizedValueException` when
offload is not configured, instead of failing with HTTP 413.

`__enc` is `text`, `gzip`, `binary` or `utf16le`. `SplitEncoding.Text` stores UTF-8; `SplitEncoding.Gzip`
gzips first and stores binary cells (a blob carries `Content-Encoding: gzip`), which suits compressible
text. A string that is not well-formed UTF-16 (an unpaired surrogate) cannot survive UTF-8, so it ignores
the requested encoding and is stored as `utf16le`, code unit for code unit. The SHA-256 is over the stored
bytes; a split value and the same value offloaded have the same hash.

### Registering blob offload

```csharp
services.AddUnifiedTableStorage(o =>
{
    o.BlobOffload = new BlobOffloadOptions
    {
        ContainerName = "unified-tables-values",   // default
        ThresholdBytes = 3_145_728,                // default; 1 to 4,194,304
        CleanupGrace = TimeSpan.FromMinutes(5),    // default
    };
});
```

The blob client is chosen only when `BlobOffload` is set, and the library never registers or replaces
the host's own `BlobServiceClient` service. It follows from the registered `UnifiedTableStorageOptions`
(the unkeyed registration every store receives), whoever registered them: an `AddUnifiedTableStorage` or
`AddUnifiedInMemoryStorage` call, or the host itself, as an instance, a factory or a type. In this order:

1. their `BlobOffloadOptions.ServiceClient`, when set (a `TableStorage<T>` constructed directly uses it
   only when no `BlobServiceClient` is passed to the constructor: an explicit constructor argument wins);
2. else, when they set `BlobOffloadOptions.UseRegisteredServiceClient`, the `BlobServiceClient` the host
   registers in DI, and no other one (resolved lazily, so it may be registered before or after
   `AddUnifiedTableStorage`); without a host client a store of a type with large values fails to resolve.
   Register the host's `BlobServiceClient` as a singleton. Every `TableStorage<T>` is a singleton that
   takes the offload client when it is constructed, so with a scoped registration every store, a plain
   one too, fails to resolve under `ValidateScopes` ('Cannot resolve scoped service ... from root
   provider'), and without `ValidateScopes` the stores keep the root scope's instance;
3. only when they choose neither, the client a call supplied: that call's own `ServiceClient`, the host's
   client when that call sets `UseRegisteredServiceClient`, or one the overload derives, kept to the
   library. The connection-string overload builds it from the same string when it has a blob endpoint
   (`BlobEndpoint`, `UseDevelopmentStorage=true`, or an `AccountName` with no `TableEndpoint` or an Azure
   Storage one); the credential overload derives `{account}.blob.core.{suffix}` only for `*.table.core.*`
   hosts (not Cosmos `.table.cosmos.azure.com`). The first client a call supplies is kept; a later call
   never replaces it.

```csharp
services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions
{
    ServiceClient = new BlobServiceClient(blobUri, credential),   // or UseRegisteredServiceClient = true
});
```

With no client found, only stores of large-value aware types fail to resolve
(`InvalidOperationException` listing the ways a store gets a client); every other `IStorage<T>` resolves as
before. Without offload configured, tiers 1 and 2 still work and a value that needs tier 3 throws
`OversizedValueException`.

`AddUnifiedTableStorage` may be called more than once, by a library module and by the host, say. The
first call's options are the registered ones (unless the host registered its own before it), and the
options of a later call are not used. A later call does one thing: when the registered options set
`BlobOffload`, choose no client, and no call has supplied one yet, it supplies it, by step 3. So
`AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions())` followed by
`AddUnifiedTableStorage(connectionString)` resolves large-value stores. The other order does not turn
offload on (the first call's options win), and a later connection-string or credential call never
replaces the client the registered options name or ask for. Only an unkeyed registration of
`UnifiedTableStorageOptions` counts as registered options: options the host registers under a key, for a
purpose of its own, are never what a store receives, so the first library call beside them behaves as a
first call (its options are registered, its `OversizedCells` policy is applied, and it supplies their
client).

Options registered through a factory or a type cannot be read at registration. Beside them a later call
offers its client only when it has one (a connection-string or credential overload, or a call whose own
`BlobOffload` sets `ServiceClient` or `UseRegisteredServiceClient`), and the choice above is made when the
container builds the client. So such options that set `UseRegisteredServiceClient`, beside only
`AddUnifiedTableStorage()` calls, get no client, and a large-value store fails to resolve; a
connection-string call whose blob endpoint the Blobs SDK rejects does not fail at registration there
either. Give options registered that way a `ServiceClient`, register them as an instance, or let
`AddUnifiedTableStorage(configure)` register them. A connection-string call that is the one to supply the
client otherwise throws `ArgumentException` at registration when the string's blob endpoint is malformed.

Call one registration or the other, not both: after `AddUnifiedInMemoryStorage` registered options with
`BlobOffload` set (and no client chosen), a later `AddUnifiedTableStorage(connectionString)` or
`(endpoint, credential)` call is the one to supply the offload client, so it validates the blob endpoint
of its string (and throws `ArgumentException` for a malformed one, a placeholder `AccountKey` say), and
otherwise registers a client that no store uses.

The offload client reaches `TableStorage<T>` through a keyed `BlobServiceClient` registration
(`[FromKeyedServices]`), so blob offload in DI relies on Microsoft's keyed-service rules (a plain
resolution never sees a keyed registration). A host that uses no offload is unaffected on every
container: nothing keyed is registered when the registered options are known to set no `BlobOffload`
(beside options registered through a factory or a type, a connection-string or credential call registers
the keyed client anyway). Microsoft.Extensions.DependencyInjection behaves as described. Other containers
were measured:

- Autofac (Autofac.Extensions.DependencyInjection 11.0.2): as described, except that with
  `UseRegisteredServiceClient` and no `BlobServiceClient` registered by the host every store fails to
  resolve, plain ones too.
- Lamar (16.0.0): an unkeyed request resolves to the last registration of the type, keyed or not. Register
  the host's own `BlobServiceClient` after `AddUnifiedTableStorage` (or pass the client as
  `BlobOffloadOptions.ServiceClient`), otherwise the host's own code is given the library's offload
  client. Use `UseRegisteredServiceClient` only when the host's client is registered after the call;
  otherwise resolution recurses until the stack overflows and the process exits, for plain stores too.
- DryIoc (DryIoc.Microsoft.DependencyInjection 6.2.0) is not supported for blob offload in DI: its adapter
  does not implement keyed services.

Beside `UnifiedTableStorageOptions` that the host registered through a factory or a type, a
connection-string or credential `AddUnifiedTableStorage` call registers a keyed `BlobServiceClient` even
when those options use no blob offload. On Lamar the host's own unkeyed `BlobServiceClient` then resolves
to the library's client unless the host registers it after the call; on the DryIoc adapter (6.2.0) it
resolves to null. Register such options as an instance, or let `AddUnifiedTableStorage(configure)`
register them, to keep 0.8.3's registrations.

The container belongs to offload alone: do not store other blobs in it. Sweeps and partition deletes
only ever delete names of the exact offload shape (see below) and skip anything else, but they list
the whole `{table}/` prefix.

### Files: `TableBlobContent`

A `TableBlobContent` property is always offloaded, whatever its size, and reading an entity never
downloads it. A content is stored only through a property declared as `TableBlobContent` (see above for
an `object`-typed one). `TableBlobContent.FromBytes(bytes, contentType)` and
`FromStream(stream, contentType)` create one; `FromStream` never buffers the content in memory and leaves
your stream open. A non-seekable stream is copied to a delete-on-close temporary file under
`Path.GetTempPath()` (so the host needs temp disk space and access); a seekable stream is read from its
current position, twice (hash, then upload), and must not change until the write completes. `Length` and
`Sha256` are known after the write (immediately for `FromBytes`). `ContentType` is the type recorded in
the row. The blob's own `Content-Type` header is set when its bytes are first uploaded and is not changed
when the same bytes are saved again into the same property with another type: the library's reads report
the row's type, while anything that serves the blob directly (a SAS link, a CDN) sends the header of the
first upload.

On a loaded entity, `await content.OpenReadAsync()` streams the blob lazily and has verified it by the
time it has delivered it: the read that would return the last byte of the expected length compares the
SHA-256 first, and throws `TableValueIntegrityException` instead of returning that data when it does
not match. A caller that reads exactly `Length` bytes therefore never gets changed content. Bytes beyond
the expected length are never handed out, a blob that ends early fails at the end of the stream, and
once a check has failed every later read of that stream throws the same exception. Nothing is verified
before the last byte: a caller that processes the content while reading it has processed all but the
last read when a mismatch is reported, so buffer first when that matters. Saving the entity again while
one of its files is being read does not disturb the read. A blob that is deleted under an open stream
(the row was replaced with other content or deleted, and cleanup or a sweep removed the blob) fails the
read with `TableValueIntegrityException`, unless the stream already holds the rest of its bytes. When
`OpenReadAsync` finds the blob gone (the row was replaced or deleted since the entity was read, by another
process too), it throws `TableValueIntegrityException`, and the `TableStorage<T>` that returned the entity
stops answering that row from its cache (its entry and its partition's cached lists are evicted): read
the entity again and open the current content.

The library puts a seekable stream back at the position the content started from whenever it is done
reading it: after hashing, after each upload, after a write that failed or was cancelled, and after a
successful write. So a write that fails before its upload finished can be retried with the same content,
and a retry that creates a new content over the same stream (a `MutateAsync`, `TryMutateAsync`,
`TryTransitionAsync` or `MutateOrCreateAsync` delegate that runs again after a conflict, or a retry loop
of your own) stores the same bytes as the first attempt. A seekable stream that can no longer be put back
(seeking throws, or it was disposed) makes the next write of that content, and any write of a new content
over that stream, fail with `InvalidOperationException` before anything is uploaded.

A forward-only (non-seekable) stream can be read once. The library remembers the stream objects a write
of a `TableBlobContent` has read, for as long as the stream object lives (being remembered does not keep
a stream alive), and recognises a stream by its object: the stream given to `FromStream`, and the
streams below it that the framework wrappers expose, followed recursively:
`BufferedStream` (its underlying stream), `GZipStream`, `DeflateStream`, `ZLibStream` and `BrotliStream`
(their base stream).

- A non-seekable stream that a write read to its end stays remembered whether that write stored its row or
  failed (a conflict, a duplicate key, any exception), and whatever became of any other write (another
  transaction of the same batch, another write that shares the content): no commit makes the library
  forget it. A new content over that stream object is stored whole when the stream delivers at least one
  byte: the object was given a new payload. So `TableBlobContent.FromStream(Request.Body, ...)` stores
  every non-empty request of a keep-alive connection, although the server hands all of them the same
  request-body object.
- When the stream delivers nothing, the new content is refused with `InvalidOperationException` before
  anything is uploaded, after a write that committed and after one that failed alike: that is the retry
  that would otherwise store an empty file and report success. Every row keeps the file it holds (a row
  that a batch committed before a later transaction of the batch failed keeps the committed file). A new,
  empty payload on a reused stream object is therefore refused too; the message says so. Store an empty
  file with `TableBlobContent.FromBytes([], contentType)` (an empty array), or from a seekable stream.
- A write that read a non-seekable stream to its end and then failed or was cancelled while it finished
  its temporary copy counts the stream as read to its end, not as partly read: a new payload on it is
  stored, an empty one refused. That write's own content has no copy left and is refused for every later
  write ("its temporary copy was released by a failed write"): create the content again.
- When the temporary copy of a non-seekable stream cannot be created (no usable temporary directory), the
  write throws before it reads anything and leaves the stream as it was: the same content, or a new
  content over the stream, can be written once a temporary copy can be made.
- Two contents over one non-seekable stream written by one write (two properties of one entity, two
  entities of one batch) are refused with `InvalidOperationException` before anything is written.
- A stream that a failed or cancelled write read only part of, and a seekable stream that a write could not
  put back where the content started, are refused with `InvalidOperationException` for every later content
  over them, also when they are wrapped in one of the framework wrappers above. A write cancelled once it
  has begun to read a non-seekable stream counts the stream as read part-way, also when the stream had
  delivered no byte yet: the library cannot tell whether the stream took bytes from its own source that it
  did not deliver. A content whose write failed after it began to read a non-seekable stream and before
  its blob was uploaded has lost its bytes (the stream is partly read, or its temporary copy was deleted),
  and every later write of it is refused the same way.
- A retry that wraps the stream an earlier attempt consumed in a new `BufferedStream`, `GZipStream`,
  `DeflateStream`, `ZLibStream` or `BrotliStream` (also several deep) is recognised: it is refused before
  anything is uploaded when the inner stream delivers nothing, and stored when the caller put the inner
  stream back (a seekable inner stream rewound) or gave it a new payload. A seekable stream below such a
  wrapper that the first attempt moved counts as consumed.
- Wrappers of any other kind are not recognised (a user-written stream, a decryption stream such as
  `CryptoStream`, a substream): a new such wrapper over a stream an earlier attempt consumed stores
  whatever that stream still delivers, possibly nothing, and the write reports success.

So when a write may be retried (`MutateAsync`, `TryMutateAsync`, `TryTransitionAsync`,
`MutateOrCreateAsync`, or a retry loop of your own), create the content once, outside the delegate that
may run again, and assign that instance on every attempt (it is copied once and reused); or use a seekable
stream or `FromBytes`; or open the source again for each attempt, where "open again" means a new source of
the bytes (the file opened again, a new request), never a new wrapper over the stream an earlier attempt
read. The refusal messages say which case applies and give these remedies.

A server author must know two consequences. A web server hands every request of a connection the same
request-body object, so after a write that read the body only part-way (the read failed or was
cancelled), every later content over that object is refused for as long as the server reuses it, also on
the next requests of that connection. Buffer the body into a seekable stream (a `MemoryStream`, or a file)
and create the content over that, or close the connection after such a failure. And once a write has read
that object to its end, a later request with an empty body over it is refused (see above): store an
empty upload with `TableBlobContent.FromBytes([], contentType)`.

A write refused because a read could not map a file's column back to a property (a `TableBlobContent`
below an object whose runtime type a read does not recreate) may have read the file's source first; a
content created from a non-seekable stream must then be created again from its source.

Once an attempt has uploaded the content's blob (the row write then failed, say), the content streams
that blob and no longer reads its stream: a retry with the same content reuses the blob. That retry has
to come before the blob is collected (by an orphan sweep whose age has passed, or a partition delete's
cleanup). After that the write throws `TableValueIntegrityException` ("the blob this content was uploaded
to or read from ... is gone"), nothing is written, and the content must be created again (over the same
seekable stream, which stands where the content started, or from the source). A content from `FromBytes`
is uploaded again from its bytes.

One content may be assigned to several properties or entities, and those entities may be written at the
same time (with `Task.WhenAll`, say), through one store or several. The writes take turns on the content:
one hashes its source (copying a non-seekable one to a temporary file), and the others use that result.
Every write that succeeds stores the whole content, a seekable or a forward-only one; none stores a part
of it. A write waiting for its turn can be cancelled with its token, and it waits only for writes that use
one of its own contents. A source whose read never completes keeps the other writes of that content
waiting until the write that reads it is cancelled.

A content taken from a loaded entity may be written into another one: every row gets a blob of its own. A
content that already streams a blob keeps streaming that blob, through the store it was read or written
through, and the property that was written is given a new `TableBlobContent` for the blob of its own row
in its own store. The written property is given its new content as soon as the blob is uploaded, before
the row is written; a write that then fails leaves the property streaming the blob uploaded for that row.
Retry before an orphan sweep collects that blob, or assign the original content again (or read its row
again) before retrying. With `a` read from the table, after `b.Attachment = a.Attachment; await
storage.CreateAsync(b);` the two are therefore not the same object: both return the same bytes, each from
its own blob, and replacing or deleting one row does not affect the other entity or its cached copy. The
same holds for an entity saved through a second store of its type whose blob has the same name (a dual
write to a primary and a secondary account, a migration): the written property gets a content that
streams the second store's blob, and the entity the first store returned, and its cached copy, keep
reading the first. Two store instances count as different stores even over the same container (the
written property then gets a new instance bound to the same blob). A content read by a store without
blob offload is the exception: saved to its own row through a store with offload, it stays on the
property and becomes readable through that store. Content created with `FromBytes` reads its own bytes and
is never replaced. Its array is not copied, so do not change it afterwards: every entity that holds the
content would read the change, the read cache's copy of the written entity included, while the stored
blob keeps the bytes that were written and a fresh read returns them (so does every read of
`InMemoryStorage<T>`, which has no cache). After concurrent writes of one content, exactly one of the
written entities keeps the shared instance.

A property whose owner the entity holds by value (a struct flattened into columns) cannot be given a new
content object. When such an entity is written into another row with a content taken from another row,
the row written references a blob of its own, but the written entity, and the store's cached copy of it,
go on streaming the blob of the row the content came from until the entity is read from the table (a
query; `OneAsync` returns the cached copy while its entry lives). If that source blob is deleted, those
copies fail to open their file, and that failure evicts the source row's cache entry, not this one.

### Ordering and integrity

- **Write** - the blob is uploaded create-only first (an existing blob with the same name and matching
  `sha256` counts as success, and its `Last-Modified` is touched), then the entity is written.
- **Replace** - new blob, entity swap guarded by the ETag, then best-effort delete of old blobs the new
  row no longer references. Only the row's own blobs are deleted, those named
  `{table}/{partitionKey}/{rowKey}/...`: a reference to any other blob, as a row copied raw from another
  table or key carries, is left alone.
- **Delete** - the entity first, then the blobs of its large-value columns; a blob referenced from another
  column is left for the orphan sweep (see [Limitations](#limitations) for which blobs a write or delete
  releases at once).
- **Partition delete** - the rows first, in transactions of at most 100 deletes that never divide a main
  row from its continuation rows, so a failure or a cancelled token between two transactions leaves whole
  entities (the call throws; call it again to finish). The count returned is of entities, not rows. An
  entity written while the delete runs is, after it, either whole or gone: continuation rows listed
  without their main row (a leftover, or the rows of an entity created between two listing pages) are
  deleted last, and only when no main row exists for them after the listing (one point read per such
  entity) and each row is still the version that was listed; a row that changed or is gone is left alone,
  and the call does not fail because of it. With no concurrent writer the partition is left empty. A
  partition in which every continuation row is listed with its main row costs the same requests as before;
  each entity whose continuation rows were listed alone costs one point read and one transaction more. Then
  the blobs under the partition that are older than `CleanupGrace` and that no row of the partition
  references at that moment: a row another writer created meanwhile keeps its blobs.
- A failed entity write never deletes a blob. Blobs are named
  `{table}/{partitionKey}/{rowKey}/{property}/{sha256}`, so rewriting identical content reuses the blob.
  Each segment is percent-escaped (`Uri.EscapeDataString`) with `.` also escaped as `%2E`, and an empty
  key is written as `%`: no segment is ever `.` or `..`, which the Blobs SDK would collapse out of the
  URI (a key `.` or `..` would otherwise address another blob, even outside the table's prefix).
- Reads reassemble or download eagerly (`string`, `byte[]`) and verify the SHA-256. A mismatch, a missing
  chunk, continuation row or blob throws `TableValueIntegrityException`; partial data is never returned.
  A transport that transparently decompresses `Content-Encoding: gzip` would break the hash check.
  A damaged row stays deletable: `DeleteAsync` removes it with the continuation rows it finds, also when
  its part count (`_SplitParts`) cannot be read. A row in that last state cannot be upserted or updated
  either, so delete it and create it again (`ExistsAsync` answers `true` for it). A row whose value is
  stored in the row's own cells and fails its integrity check (cells damaged outside the library) answers
  `true` to `ExistsAsync` on both stores and is not cached; `OneAsync` and the queries throw
  `TableValueIntegrityException` for it, and `DeleteAsync` removes it.
- A read that races a replace or delete (a point read, a query, a stream or a page; the main row is read
  before its continuation rows and blobs) can see parts or a missing blob of another version, or have a
  blob deleted while it downloads it (a blob that is only touched, by a write that reuses its content,
  does not disturb the download). On such a failure the main row is read again: if its ETag is unchanged,
  the stored value is damaged and the read throws `TableValueIntegrityException`; a row deleted meanwhile
  reads as absent (`OneAsync` returns null, a query skips it); if the ETag changed, the whole read is
  retried from the new version after the pause a write that lost a race takes (it honours the
  cancellation token). A read is attempted five times in all, as a write is. When the row changed during
  each of the five, the read throws `ConcurrencyConflictException` ("... did not complete: the row changed
  during each of its 5 attempts ... Nothing stored is damaged; the read can be repeated."), whose
  `InnerException` is the `TableValueIntegrityException` of the last attempt; nothing is damaged, so
  repeat the read. In a query, a stream or a page it ends the whole call. Data of two versions is never
  combined. The in-memory store does the same for blobs a concurrent replace releases.
- An offloaded value is never returned empty: writing such an entity back would drop the reference and let
  the sweep delete the value. Reading a row that holds a `{Prop}__blob` reference through something that
  cannot fetch it throws, naming the column:
  - the public `FromTableEntity<T>()`, or a store of a plain type, throws `InvalidOperationException`;
  - a store whose type has such properties but no `BlobOffload` configured throws
    `BlobOffloadNotConfiguredException` (an `InvalidOperationException` with `TableName` and `ColumnName`; a
    configuration error, not corruption, and not retried) from the read. A `TableBlobContent` property is
    still read lazily, and throws that exception when it is opened.

  Only a projection (`QueryOptions.Select`) that omits the property leaves it at its default. Split cells are
  reassembled by any reader; a plain reader that sees a split row without its continuation rows throws
  `TableValueIntegrityException`. A plain reader takes a group of cells for the library's only when its
  manifest is complete and well formed (`__chunks` a positive count, `__enc` one of `text`, `gzip`,
  `utf16le`, `binary`, `__sha256` 64 hexadecimal digits, `__len` a non-negative length) or its `__blob`
  cell parses as a blob reference. A column that merely has such a name (`Body__len`, `Body__c0`, written
  by another program) is read as 0.8.3 reads it: ignored, or mapped to a nested member (`Stats__len` to
  `Stats.len`). For a split-aware type an incomplete group is damage and throws.

### Sweeping orphans

Lost races, crashes and failed deletes leave unreferenced blobs. Best-effort cleanup after a replace,
delete or partition delete only removes blobs untouched for `BlobOffloadOptions.CleanupGrace` (default
5 minutes), and after a replace or delete only the row's own; the others are left for the sweep, which
must be scheduled: call it yourself, or register the [hosted sweep](#hosted-sweep). A committed write or
delete is never reported as failed because its token was cancelled: its cleanup runs on for at most 5
more seconds, then stops and leaves the rest of the blobs (a large partition's, say, during a host
shutdown) to the sweep.

`CleanupGrace` is not a delay after the change: a replace or delete deletes a released blob at once when
it was last written or touched longer ago than the grace, counted back from the storage service's time of
the commit (below), and leaves a blob touched within the grace for the sweep. Keep `CleanupGrace` well
above the time a write takes from its blob upload to its row commit. A blob's `Last-Modified` has
one-second resolution, so a grace of zero, or below one second, protects nothing: a blob that another
writer touched in the same second as the commit, and is about to reference, can be deleted under it. Use
such a grace only in tests.
`CleanupGrace = TimeSpan.MaxValue` switches this cleanup off: nothing is deleted after a write, a delete
or a partition delete, and every released blob is left for the sweep.

Both ages are measured on the storage service's clock, the one that stamps a blob's `Last-Modified`: the
cleanup after a write or delete uses the `Date` of the commit response, and the sweep and the cleanup after
a partition delete ask the blob service for its time (one container properties request) before listing.
A client clock that runs ahead therefore cannot make a freshly touched blob look old. Only when no service
time is available is the client clock used, moved back by a further 5 minutes
(`LargeValueTable.ClientClockMargin`). The service clock has one-second resolution, so a blob written in
the current second is not yet older than `TimeSpan.Zero`. Like the sweep, the cleanup after a partition
delete reads the partition's rows once (keys and reference cells) to see which blobs are still
referenced, and only when some blob is old enough.

```csharp
int deleted = await storage.SweepOrphanBlobsAsync("tenant-a", olderThan: TimeSpan.FromHours(1));
int all = await storage.SweepOrphanBlobsAsync(olderThan: TimeSpan.FromHours(1)); // every partition
```

`olderThan` must exceed the longest write (blob upload to entity commit): a younger blob may belong to
a write still in flight. In a batch write that is one transaction's duration, not the whole batch's: each
transaction's blobs are uploaded just before it. Each delete is conditional on the blob being unmodified since it was listed, so a
write that reuses an old orphan is never undercut. Only names of the exact offload shape
`{table}/{partitionKey}/{rowKey}/{property}/{64 lower-hex sha256}` are ever deleted.

The sweep is the capability interface `IOrphanBlobSweeper`, implemented by `TableStorage<T>` and
`InMemoryStorage<T>`; on an `IStorage<T>` the same calls are extension methods that throw
`NotSupportedException` for a store without it. The table-wide overload lists the partitions from the
container (`{table}/` with the delimiter `/`), takes one cutoff from the service clock, and sweeps each
partition in turn. A failing partition does not stop the others: afterwards the failures are thrown
together as an `AggregateException` whose message says how many blobs were deleted.

### Hosted sweep

`AddUnifiedOrphanBlobSweep` registers an `IHostedService` that runs the table-wide sweep of each
registered type on a schedule:

```csharp
services.AddUnifiedTableStorage(connectionString, o => o.BlobOffload = new BlobOffloadOptions());
services.AddUnifiedOrphanBlobSweep(o =>
{
    o.Interval = TimeSpan.FromHours(1);      // default; pause between rounds
    o.OlderThan = TimeSpan.FromHours(1);     // default; must exceed the longest write
    o.InitialDelay = TimeSpan.FromMinutes(1); // default; first round after startup
});
services.AddUnifiedOrphanBlobSweep<Document>();   // once per type with large-value members
```

Each round resolves `IStorage<T>` for every registered type (in a fresh scope) and calls
`SweepOrphanBlobsAsync(OlderThan)`; the next round starts `Interval` after the previous one ended. A type
that throws (a partition failure, a store that cannot be built, a timeout) is logged through
`ILogger<>` at `Error` and retried next round; the other types still run. A store that does not
implement `IOrphanBlobSweeper` is skipped with a warning. Shutdown cancels the round in progress. Without
`UnifiedTableStorageOptions.BlobOffload`, or with no type registered, the service logs that once and does
nothing. The first `AddUnifiedOrphanBlobSweep(configure)` call's options win; out-of-range options throw
`ArgumentOutOfRangeException` at registration. Works the same over `AddUnifiedInMemoryStorage`. Every
instance that registers it sweeps: concurrent sweeps are safe (each delete is conditional) but redundant,
so a scaled-out app may register it in one instance only.

### Upgrading, backups and restores

- **Roll forward only.** A 0.8.x (or older) reader does not know the 0.9.0 layout: it returns
  continuation rows (`{RowKey}~0001`, ...) as entities of their own, reads `Stages__c0` as the nested path
  `[Stages, c0]` so a split collection reads as empty, and sees an offloaded property as null. A later
  replace or upsert from that reader writes the empty value back and the data is lost (its blob is then
  an orphan the sweep deletes). Values that each fit one cell are not exempt: an entity whose opted-in
  values together exceed one entity is written with split cells and continuation rows too. Deploy in two
  steps: first the package bump with no `[TableValueSplit]` or `TableBlobContent`, which writes exactly
  what 0.8.3 wrote; then, once no older reader remains (swapped slots drained, rollback no longer to a
  pre-0.9.0 build, every service that reads the table upgraded), the attributes. Rolling back past the
  second deploy needs the data rewritten first.
- **Do not remove `[TableValueSplit]` from a property while rows hold large values of it** (split cells,
  continuation rows or a blob). A read brings such a value into a member that is no longer a large value,
  and `TableStorage<T>` copies the entity it read as ordinary cells: the copy it caches, and every entity
  that `QueryAsync(partition)` and `QueryAsync()` return (with caching off too). When the value is a
  string or a JSON-stored value that does not fit one cell even compressed, a trimming
  `OversizedCellPolicy` trims it in those copies (so a later `OneAsync` that the cache answers returns it
  trimmed) and in the next save of the entity, which stores it trimmed (a JSON-stored value that is not a
  list is dropped instead); `OneAsync` when it reads the table, and the other queries, return it whole.
  Under `Throw`, `QueryAsync(partition)`, `QueryAsync()` and the save throw `OversizedCellException`,
  while `OneAsync` and the other queries return the value whole and the entity is not cached. Once the
  type opts into nothing at all, a read of a row that holds such a value in a blob or on continuation
  rows fails.
- **Back up and restore an entity as one unit**: its main row, its continuation rows (same partition,
  RowKeys `{RowKey}~NNNN`) and its blobs in the offload container, taken together. A row restored without
  its parts or blobs (or with those of another version) reads as `TableValueIntegrityException`.
- **A table copied raw shares the source's blobs**: each `{Prop}__blob` reference holds the blob path
  under the source table's name, so the copy reads the source's blobs (a row copied raw to another key
  likewise reads the blobs of the row it was copied from). Writes through the copy never delete them:
  cleanup removes only a row's own blobs, so loading a copied entity and saving it through the library
  uploads a blob of the copy's own and leaves the source's untouched. The other direction is not
  protected: once the source replaces or deletes those rows, its cleanup, partition delete and sweep
  delete blobs the copy still reads. Give the copy its own copy of the blobs before the source changes
  (rewrite the entities through the library, or copy the container and point the copy's `ContainerName`
  at it). Blob versioning or soft delete on the container makes such a mistake recoverable.
- **The offload container is the library's.** Do not put other blobs in it, and do not share it between
  applications whose tables have the same names.
- **Schedule the sweep** (above): nothing else removes orphans younger than `CleanupGrace` or left by a
  crash.

### Limitations

- In RowKeys of split-aware types, only the continuation-row shape is reserved: a key ending with `~`
  and exactly four digits (`r~0001`, `a~b~0042`). Writes, batch writes and partial updates refuse it with
  `ArgumentException`, as they refuse a row key longer than 480 characters (which leaves room for the
  continuation-row suffix); every other `~` is allowed (`a~b`, `x~12`, `a~0001x`, append-log keys). A
  batch checks every key before it stamps any entity, so a batch refused this way leaves the entities you
  passed unchanged (`ETag`, `CreatedAt`, `UpdatedAt`). Deletes accept any key, so a row written before the
  type opted in stays deletable; deleting a continuation row's id is a no-op. A write never overwrites a
  foreign row at the key of a NEW continuation row: a row keyed `{RowKey}~0001` that is not a continuation
  row of that entity (one from before the opt-in, or of another type sharing the table) makes a write of
  `{RowKey}` that needs that key fail as a whole with `InvalidOperationException` naming the key, stores
  nothing and is left as it is. A row another writer puts at a continuation key the entity already
  declares is treated as the entity's own: its next write replaces it, or its next shrink or delete
  deletes it, and until then a read of the entity throws `TableValueIntegrityException`. Do not write rows
  with the continuation-row keys of a split entity through anything else. The escaped blob name must fit
  1,024 characters.
- A continuation row whose `_SplitOf` cell names an entity belongs to that entity even when no main row
  declares it. Such leftover rows appear when a writer that does not know continuation rows (a type
  without `[TableValueSplit]` on the same table, a 0.8.x instance, the raw SDK) deletes or replaces the
  main row or stops halfway through a partition delete, and when a partition delete races an upsert that
  adds continuation rows. The reads and queries of every type never return them, nor does `CountAsync` of
  the split-aware type (a plain type on the same table counts them, as on 0.8.3; see the next bullet),
  and they do not block the key: a write of the entity that needs a leftover's key replaces it and deletes
  the entity's other leftovers in the same transaction (at the cost of one failed transaction and one
  range query), and `DeleteAsync` of an id that has no main row removes them (so a `DeleteAsync` that
  finds no row costs a split-aware type one range query more). If another caller (a delete of the same
  id, a shrinking upsert, a partition delete) removes that leftover between the write's failed
  transaction and its look at the key, the write counts a lost race and retries within its five
  attempts. A write looks for leftovers only when one holds a key it needs, so the others stay, unseen,
  until the entity grows onto them, shrinks or is deleted while its main row declares continuation rows,
  `DeleteAsync` finds no main row at its id, or the partition is deleted.
- A table shared between a split-aware type and a plain type or a polymorphic store: the reads of
  `TableStorage<T>` (`OneAsync`, `ExistsAsync`, the queries, streams and pages) skip every row that
  carries a `_SplitOf` cell, for every type, so the plain type does not return the split-aware type's
  continuation rows; `CountAsync` of the plain type still counts them, as on 0.8.3. A plain type and a
  polymorphic store delete a partition as 0.8.3 does, every listed row by key, in chunks of 100. Such a
  delete can remove continuation rows of a split entity that is written while it runs, and leave that
  entity unreadable (`TableValueIntegrityException`): delete such a partition through the split-aware
  type, or do not write into it while the delete runs. A polymorphic store refuses an item whose type is
  not large-value aware and holds a value in a large-value member below a runtime subtype with the plain
  store's `NotSupportedException` (whose remedies are for `IStorage<T>`); polymorphic stores write no
  split or offloaded value at all, so store such an item through an `IStorage<T>` of a large-value aware
  type.
- Keys of split-aware types must be well-formed UTF-16: a partition or row key with an unpaired surrogate
  (`"a\uD800"`) would name the same blobs as one with U+FFFD in its place, so writes refuse it with
  `ArgumentException`. Plain types are unaffected.
- No LINQ filter and no partial update (`UpdateAsync(id, builder)`) on a large property (both throw
  `NotSupportedException`); not supported by `IPolymorphicStorage`. That includes every property a
  class-level attribute covers. One filter is not refused: on a member that an interface or base class
  declares without the attribute and the runtime type opts in. It translates, and does not match rows whose
  value is stored split or offloaded.
- Batch writes of these types keep the batch contract: per partition, each entity's rows (main row,
  continuation rows, deletes of stale ones) are one unit, units are packed in order into entity-group
  transactions (at most 100 actions and the same byte and 4&nbsp;MiB wire budgets as a single write), and
  each transaction is atomic; an entity's rows never straddle two. Atomicity is per transaction only: a
  failing transaction leaves earlier ones committed. Each transaction's blobs are uploaded just before
  that transaction. The blobs the replaced rows no longer reference are deleted once, after the last
  transaction of the call has committed (each subject to `CleanupGrace`, counted from the commit that
  released it). So one batch can store a content read from a row that the same batch replaces (keep a
  document's previous file in a history entity while replacing it, exchange the files of two documents,
  move a file to another document), whatever the order of the entities and however many transactions
  the batch needs. A batch that fails part way, or is cancelled between two transactions, deletes none of
  the blobs its committed transactions released (the orphan sweep removes them); every entity it did not
  write keeps a content that can still be read, and sending the same batch again stores it. A row
  repeated in `CreateBatchAsync` throws `DuplicateKeyException` before anything is written; in
  `UpsertBatchAsync` its last occurrence is written (the ids `"a"` and `"a|a"` address the same row).
  Upserts do one point read per entity first, and each replace is conditional on the version read: a
  transaction that loses a race is planned again and retried as a single write is (next bullet), and
  after five lost races `UpsertBatchAsync` throws `ConcurrencyConflictException` naming the entity
  (earlier transactions stay committed).
- For a split-aware type `UpsertAsync`, `UpdateAsync(entity)`, `DeleteAsync` and `UpsertBatchAsync` are
  not single round trips: each reads the row first (one point read, to find the continuation rows and
  blobs it replaces) and writes on condition of the version it read. For a write the order is table get,
  blob get, blob upload, table update. A `Strict` update, or an `Auto` update with an ETag, that finds
  another version is a conflict at once, as for any type. `UpsertAsync`, a `LastWriterWins` `UpdateAsync`,
  `DeleteAsync` and `UpsertBatchAsync` still mean "last writer wins", but a call that loses its condition
  to a concurrent writer reads again and retries: five attempts in all, then
  `ConcurrencyConflictException`. Before each retry the call waits a random time (5 to 10 ms before the
  first retry, doubling each time, at most 150 ms in all; the wait honours the cancellation token). Each
  lost attempt is caused by another writer's commit, so up to four concurrent writes of one row all
  succeed. With more, a conflict is unlikely but possible: catch `ConcurrencyConflictException` and retry,
  or serialize the writes of that row. `CreateAsync` and `CreateBatchAsync` read nothing first (an
  existing key is `DuplicateKeyException`). They are retried only when they take back leftover
  continuation rows (above), and end in `ConcurrencyConflictException` only if those rows keep changing
  or vanishing under them. So for a split-aware type `ConcurrencyConflictException` comes from a `Strict`
  or `Auto` update with an ETag and a builder update with `WithETag` (as for any type), from the writes
  and deletes above after five lost races, and from a read whose row changed during each of its five
  attempts (see [Ordering and integrity](#ordering-and-integrity)).
- The read before a write projects the stored row onto the part-count and continuation-marker cells and
  one `{column}__blob` cell per large-value column of the type. When that would name more than 200
  large-value columns, or be longer than 2,000 characters as a URL-encoded `$select`, the store reads the
  whole row instead, to stay within the 255 properties the service projects and the request-URL limit of
  the service and the emulator. A type above that bound (several hundred opted-in columns, such as
  sections of blocks under a class-level `[TableValueSplit]`, or a few dozen columns with long flattened
  names) pays one whole-row read, up to 1&nbsp;MB, per write or delete instead of a few cells.
- A replace, upsert, update, batch upsert or delete deletes at once (subject to `CleanupGrace`) the blobs
  the stored row referenced in the cells that read returns: the type's own large-value columns, including
  legacy `[ColumnAlias]` columns and the columns of the runtime subtypes of its default instances, or every
  reference cell of the row when the row is read whole. A blob referenced in any other cell (a column
  another entity type wrote on a shared table, a value below a type that repeats below itself, a property
  removed from the type) is left until the orphan sweep removes it. `InMemoryStorage<T>` follows the same
  rule.
- After a write that needed an entity-group transaction (continuation rows were written or removed), the
  entity's `ETag` is the version the transaction's response reports for the main row; the row is not read
  again, and the entity is not put in the entity cache. If that response carries no version, `ETag` is
  null after the write, as after a batch: read the entity again before an optimistic update.
- A `QueryPageAsync` page of a split-aware type can hold fewer entities than `Take`, or none, with a
  non-null continuation token: a continuation row, declared or left over, takes a place in the page. Keep
  paging until the token is null; a short page is not the last page. `InMemoryStorage<T>` pages the same
  way.
- Reads and scans cost more for these types: an entity whose value is split across continuation rows
  takes one extra range query for its parts (on every read, queries included), and an eager offloaded
  value (`string`, `byte[]`) is downloaded from blob storage on every read, one blob per entity, so a
  query over N such entities makes N extra requests. `TableBlobContent` is never downloaded by a read.
  The entity cache holds the full value.
  For types with very large eager values use `CacheFor<T>(CachePolicy.Disabled)`.
- A `TableBlobContent` on an entity you hold (or one the entity cache returns) names the blob of the version
  it was read from. Once a replace or delete has deleted that blob (at once when it was older than
  `CleanupGrace`, or later by a sweep), `OpenReadAsync` throws `TableValueIntegrityException` saying the
  row may have changed, and the store that returned the entity stops answering that row from its cache:
  read the entity again. It never returns other bytes: blobs are named by their hash.
- A projection (`QueryOptions.Select`) of a split-aware type is narrowed to the named columns only when
  every name is known to be an ordinary column: a system column (`PartitionKey`, `RowKey`, `Timestamp`,
  `_TypeName`, `_SplitOf`, `_SplitIndex`, `_SplitParts`, `odata.etag`) or a name that resolves to an
  ordinary property. Otherwise the whole row is fetched and the entity is returned whole. A name that
  names a large value in any way returns the whole value: the property's column (`Body`, `Inner_Body`), an
  object that owns one (`Inner`), one of its cells (`Body__c0`, `Body__chunks`, `Body__sha256`,
  `Body__enc`, `Body__len`, the `__Json` or `__GZip` cell of an opted-in property), also for a value below
  a repeated type or a runtime subtype; a projection never returns part of a value. The one exception is
  the reference cell `{Prop}__blob` named alone, which is fetched alone and leaves the property at its
  default. A name that resolves to no property (a misspelling, `ETag`, a stored alias name, a nested
  object's name) is not known to be ordinary either, so it fetches whole entities: more data than asked
  for, never less. Name the property, not its cells. A plain type projects exactly as in 0.8.3 (a
  JSON-stored property by its stored cell's name, `Tags__Json`).
- A projection of a plain type (one that does not opt the property in) that names a property another type
  stored split or offloaded reads it as null: the plain store neither fetches nor recognises the value's
  cells. Read such rows through the opted-in type.
- A type that maps a column another type stored split or offloaded to a member that is not a large-value
  member (a plain type sharing the table, for one) reads the value into that member where it can read it
  at all (a plain type reassembles split cells on the main row and fails to read a value on continuation
  rows or in a blob, see [Ordering and integrity](#ordering-and-integrity)). `TableStorage<T>` then copies
  the entity it read as that member would be stored: the copy it caches, and every entity that
  `QueryAsync(partition)` and `QueryAsync()` return (with caching off too). Under `TrimSilently` or
  `TrimWithMarker` a string or JSON-stored value that does not fit one cell even compressed is trimmed in
  those copies (so a later `OneAsync` that the cache answers and those two queries return it trimmed) and
  in the next save of such an entity, which stores it trimmed (a JSON-stored value that is not a list is
  dropped instead); under `Throw` those two queries and the save throw `OversizedCellException`, and the
  entity is not cached. `OneAsync` when it reads the table, and the other queries, return the value whole.
  Read such rows through the opted-in type.
- The store learns a type's large-value columns from its declared property types (along every path that
  does not pass through the same type twice) and from the objects a new entity holds. Three shapes fall
  outside that: a large value below a repeated type (`Head.Next.Body` when the node type refers to
  itself); a runtime subtype that only the constructor of a nested object creates, below a property that
  is null on a new entity (`Order.Payment` is null by default and `Payment`'s constructor sets
  `Method = new Card()`); and a type graph with more paths to large-value members than can be listed in
  10,000 steps, where only the columns nearest the entity are known. In a split-aware type such a value is
  still written split or offloaded and read back whole, a projection that names it returns it, and a
  partial update of it is refused, but the store does not track its column: the blob a replaced or deleted
  row referenced there is left to the orphan sweep (unless the row is read whole, above). A type whose only
  large values are of the second kind is not split-aware, and its store refuses such a value (see
  [above](#large-values-split-and-blob-offload)); mark the entity class `[TableValueSplit]`. Give the outer
  property a default instance where you can.
- Limits of one split-aware entity, and what a caller gets beyond them:
  - a row key of at most 480 characters and an escaped blob name of at most 1,024 (`ArgumentException`);
  - at most 252 properties and 1,000,000 estimated bytes for the cells that cannot leave the main row:
    everything but the chunks of split values (`OversizedValueException` naming the largest of them, or,
    when the properties are what does not fit, the largest split value); 62 split values on a bare
    `Entity` (see [Tiers](#tiers));
  - a main row and at most 99 continuation rows, written in one transaction of at most 100 actions,
    3,000,000 estimated bytes and 4&nbsp;MiB less 64&nbsp;KiB on the wire, the deletes of stale
    continuation rows included. A value that does not fit is offloaded; when nothing more can be offloaded
    the write throws `OversizedValueException` and stores nothing.
- The storage emulator limits an entity by its request body, the service by its stored size. Split
  cells are placed so that each row's request body stays within 1&nbsp;MB, and a main row whose values
  had to be moved is brought within it as far as moving opted-in values can. Cells that are not split
  count by their stored size only (binary is base64 on the wire, escaped text up to six bytes per
  character), for plain types too. Azurite accepts an insert of a row whose request body exceeds
  1&nbsp;MiB and refuses to replace, merge or upsert it (`EntityTooLarge`): fourteen ordinary binary cells
  of 60,000 bytes can be created there and not updated.
- Storage-provider SPI types (namespace `Unified.Data.Tables.Providers`) are public only so other
  providers, such as the in-memory package, can share the implementation. They are hidden from
  IntelliSense and not for application code.

### Sizing your own transactions: `TableRequestSize`

Code that builds its own `TableTransactionAction` lists can measure them as the library's planners do:

```csharp
long stored = TableRequestSize.EstimateEntityBytes(entity);      // vs MaxEntityBytes (1,000,000)
long wire = TableRequestSize.EstimateEntityWireBytes(entity);    // upper bound of the JSON body the SDK sends
bool fits = TableRequestSize.FitsOneTransaction(actions);        // one partition, <= 100 actions,
                                                                 // <= 3,000,000 B stored, <= 4 MiB - 64 KiB on the wire
```

The wire estimate counts JSON escaping (six bytes for every non-ASCII character and for
`"`, `\`, `<`, `>`, `&`, `'`, `+` and `` ` ``), base64 binary and an `@odata.type` annotation for every value other than a string,
`bool` or `int` (doubles included). A `TableEntity` is measured as is; another `ITableEntity` as the SDK
serializes a model (public properties, `[IgnoreDataMember]` skipped, `[DataMember(Name)]` honoured).
`MaxTransactionActions`, `MaxTransactionPayloadBytes`, `MaxTransactionWireBytes`, `MaxTransactionEntityBytes`,
`MaxEntityBytes` and `MaxEntityProperties` are public constants.

---

## Polymorphic storage

`IStorage<T>` is one CLR type per table. When many types share one table — an event store, a command
log, an outbox — use `IPolymorphicStorage<TBase>` instead. Rows carry a `_TypeName` discriminator and
read back as `TBase` with the true derived instance intact.

```csharp
services.AddUnifiedTableStorage(connectionString);
services.AddUnifiedPolymorphicTable<IEvent>("StateEventStore");
services.AddUnifiedPolymorphicTable<IEvent>("TransactionStore");
```

```csharp
public sealed class StateEventStore(
    [FromKeyedServices("StateEventStore")] IPolymorphicStorage<IEvent> storage)
{
    public Task SaveAsync(string aggregateId, IReadOnlyCollection<IEvent> events) =>
        storage.InsertBatchAsync(
            [.. events.Select(e => new PolymorphicWrite<IEvent>(
                new TableKey(aggregateId, e.Version.ToString("D9")), e))]);

    public async Task<IReadOnlyList<IEvent>> GetAsync(string aggregateId)
    {
        var entries = await storage.QueryAsync(aggregateId);
        // Item, not Value: a partition may hold marker rows (see below), and Value throws on one.
        return [.. entries.Where(e => e.Item is not null).Select(e => e.Item!)];
    }
}
```

`[FromKeyedServices]` takes an attribute argument, so the table name has to be a compile-time
constant; a name computed at runtime must be resolved imperatively with
`serviceProvider.GetRequiredKeyedService<IPolymorphicStorage<IEvent>>(tableName)` instead.

**Keys are explicit and verbatim.** `TableKey(PartitionKey, RowKey)` is passed on every operation and
is never normalized — a polymorphic row key is usually a case-sensitive payload or a zero-padded
counter, and lower-casing it would address a different row.

**Marker rows.** A write whose `Item` is `null` stores system columns only, with no discriminator.
That lets a commit flag share one transaction with the rows it guards; it reads back as an entry
whose `Item` is `null` and whose `Columns` are intact.

```csharp
await storage.InsertBatchAsync([
    ..events.Select(e => new PolymorphicWrite<IEvent>(new TableKey(txId, RowKey(e)), e)),
    PolymorphicWrite<IEvent>.Marker(new TableKey(txId, "FlagEntity"),
        new Dictionary<string, object> { ["_IsCommitted"] = false }),
]);

await storage.MergeColumnsAsync(new TableKey(txId, "FlagEntity"),
    new Dictionary<string, object> { ["_IsCommitted"] = true });
```

**Type discriminators.** The default `AssemblyQualifiedTypeDiscriminator` stores
`Type.AssemblyQualifiedName`, byte-identical to what `persistType: true` has always written — so an
existing table reads with no migration. Prefer a map for anything new: an assembly-qualified name
breaks on rename and costs a few hundred bytes on every row, charged against the transaction budget
that caps batch size.

```csharp
services.AddUnifiedTableStorage(cs, o => o.TypeDiscriminator =
    new TypeDiscriminatorMap()
        .MapAssignableTo<IEvent>(typeof(OrderPlaced).Assembly)
        .WithAssemblyQualifiedFallback());   // keep reading legacy rows while writes converge
```

Every read verifies the resolved type is assignable to `TBase` and throws otherwise. **No
configuration disables that check** — deserializing a type named by stored bytes is a gadget surface,
and a resolver is not a security boundary.

**The store owns its table.** There is no server-side type filter, so every enumerating operation
sees every row in scope. Point two stores at one table and each sees the other's rows.

**Not supported here:** caching, LINQ predicates, `QueryPageAsync` cursors, `UpdateBuilder`,
`ConcurrencyMode`, and `[ProtectedProperty]`. Rows are immutable facts plus mutable `_`-prefixed
system columns; `MergeColumnsAsync` is the one mutation.

---

## Id convention

`Entity.Id` is a composite string split on the **first** `|` (helpers: `EntityId.Normalize`,
`EntityId.Split`, `EntityId.Combine`):

| `Id` | PartitionKey | RowKey |
| --- | --- | --- |
| `"eu|alice"` | `eu` | `alice` |
| `"vision|exec|agent"` | `vision` | `exec|agent` |
| `"single"` | `single` | `single` |

Ids are normalized on write (`trim → replace spaces with '-' → ToLowerInvariant`). `Id` is also stored as
a data column, so the full composite id is preserved on read.

---

## Testing with Unified.Data.Tables.InMemory

```csharp
// In tests (or a dev/offline host):
services.AddUnifiedInMemoryStorage();          // open-generic IStorage<> → InMemoryStorage<>

var store = new InMemoryStorage<Customer>();   // or construct directly
await store.CreateAsync(new Customer { Id = "eu|alice" });
Assert.Equal(1, store.Count);                  // + Clear(), Snapshot() conveniences
```

The fake is deliberately faithful: rows round-trip through the REAL serializer (decimal-as-double,
enum-as-string, flattening, `__Json`/`__GZip`, 64&nbsp;KB handling), duplicate `CreateAsync` throws
`DuplicateKeyException`, updating a missing row throws 404, stale ETags throw
`ConcurrencyConflictException` per `ConcurrencyMode`, deletes are idempotent,
and results arrive in lexical key order — so a green test against the fake means the same code holds
against Azure Tables. `[TableValueSplit]` and `TableBlobContent` work too: the fake keeps offloaded blobs in
an in-memory dictionary, with the same integrity checks and `SweepOrphanBlobsAsync` support (it needs no
`BlobServiceClient` and ignores `BlobOffloadOptions.ServiceClient`). It plans the same rows and
transactions, refuses the same keys and shapes, takes back leftover continuation rows, cleans up only a
row's own blobs and releases the same blobs at once, retries a write that lost a race with the same five
attempts and pauses (and a read whose blobs a concurrent replace released, with the same
`ConcurrencyConflictException` after five), and pages a split-aware type as the table does (a continuation
row takes a place in a page). Like `TableStorage<T>`, it serializes every entity of a plain type's batch
before it stores the first, so a batch in which one entity cannot be serialized stores nothing (0.8.3's
in-memory store kept the entities before it). Three differences are worth knowing: it has no read cache;
a stream opened on a `TableBlobContent` holds the bytes it was opened with, so a blob deleted meanwhile
does not fail that read; and a builder partial update checks that the row exists and that its
`WithETag` matches before it looks at its paths, so for a missing row or a stale ETag it throws the 404
or the conflict where `TableStorage<T>` throws the refusal of the path (a large property, a member
marked on an interface).

The polymorphic store has the same mirror, keyed the same way:

```csharp
services.AddUnifiedInMemoryPolymorphicTable<IEvent>("StateEventStore",
    o => o.TypeDiscriminator = new TypeDiscriminatorMap().MapAssignableTo<IEvent>(asm));
```

Pass the **same discriminator configuration production uses**. Without it the fake resolves whatever
`UnifiedTableStorageOptions` the container happens to hold — and a test host that registers only this
line holds none, so it would quietly fall back to assembly-qualified tokens while production writes
short ones. Nothing fails; the tokens simply differ, in the one place no assertion looks. The table
name is passed through to the store too, not just used as the DI key, so `DuplicateKeyException`
names the same table the Azure store would.

---

## ASP.NET Core Identity

`Unified.Data.Tables.Identity` ports ASP.NET Core Identity's `IUserStore`/`IRoleStore` contracts onto
`IStorage<T>` — passwords, external logins, claims, roles, tokens, two-factor and lockout, all as
ordinary rows through the same storage abstraction as your domain entities.

```bash
dotnet add package Unified.Data.Tables.Identity
```

### Register it

The package depends on `Unified.Data.Tables.Abstractions` only, so it deliberately does **not**
register a storage provider — pick one yourself, then wire the stores on top:

```csharp
using Unified.Data.Tables.Identity;

builder.Services.AddUnifiedTableStorage(connectionString);   // or AddUnifiedInMemoryStorage() in tests

builder.Services
    .AddIdentityCore<IdentityUser>()
    .AddRoles<IdentityRole>()
    .AddUnifiedIdentityStores();
```

Because it only touches `IStorage<T>`, the same registration works unmodified against
`Unified.Data.Tables.InMemory` — an entire Identity stack becomes unit-testable with no Azurite
emulator, no test container, nothing but the fake.

### Tables

Seven `Entity`-derived row models, one Azure table each (named after the type, per the package's
usual `typeof(T).Name` convention): `IdentityUserModel`, `IdentityRoleModel`,
`IdentityUserRoleModel`, `IdentityUserClaimModel`, `IdentityRoleClaimModel`,
`IdentityUserLoginModel`, `IdentityUserTokenModel`. Keys are composed deterministically by
`IdentityKeys` — GUID and constant components are used verbatim, unbounded user- or
provider-supplied text (claim values, login provider keys) is MD5-hashed, because Azure Table
Storage rejects `/`, `\`, `#`, `?` and control characters in `PartitionKey`/`RowKey`.

### Disable caching for user rows

User rows carry `SecurityStamp`, `PasswordHash` and `LockoutEnd`. On Azure, the default sliding
cache can serve a revoked security stamp indefinitely on a multi-instance host — signing a user out
everywhere, or locking them out, wouldn't reliably take effect. Turn caching off for that one type:

```csharp
builder.Services.AddUnifiedTableStorage(connectionString, o =>
    o.CacheFor<IdentityUserModel>(CachePolicy.Disabled));
```

### Login rows use `CreateAsync`, not upsert

Every other association table (roles, claims, tokens) upserts. Login rows don't, and that's
intentional rather than an inconsistency: a login's key is `{provider}|{md5(providerKey)}`, and the
owning `UserId` lives in the row's *value*, not its key. An upsert would silently reassign
ownership of an external identity to whoever wrote last; `CreateAsync` fails loud on a duplicate
instead, so a second account colliding on the same external identity surfaces as an error rather
than a silent takeover.

### `AddToRoleAsync` throws when the role does not exist

The other role members stay quiet about a missing role — `IsInRoleAsync` returns `false`,
`RemoveFromRoleAsync` is a no-op and `GetUsersInRoleAsync` returns empty. `AddToRoleAsync` is the
exception: it throws `InvalidOperationException`, matching EF Core's own store. It is a mutation
that cannot be satisfied, and `UserManager.AddToRoleAsync` does not validate role existence itself,
so a silent return would surface to the caller as `IdentityResult.Success` having assigned nothing.
Code wrapping `UserManager.AddToRoleAsync` should expect the throw and translate it (typically to a
400) rather than assume every failure arrives as a failed `IdentityResult`.

### Custom user and role types are not supported yet

`AddUnifiedIdentityStores()` inspects the `IdentityBuilder` and throws `InvalidOperationException`
at startup for anything but exactly `IdentityUser` and (if roles are enabled) exactly
`IdentityRole` — a subclass like `class AppUser : IdentityUser` is rejected, not silently coerced.
Table names are derived from `typeof(T).Name`, so supporting custom types means the consumer
supplying their own row model; that's an additive change reserved for a later version, not
something this version does partially.

---

## Building & testing

```bash
dotnet build Unified.Data.Tables.slnx -c Release
dotnet test  Unified.Data.Tables.slnx -c Release
```

The test suite mixes fast unit tests (Azure SDK mocked with NSubstitute) with integration tests that run
against a local **Azurite** emulator (tables on port 10002, blobs on 10000). The integration tests skip
only when nothing is listening there (connection refused, host unreachable), so `dotnet test` is safe to
run anywhere; an emulator that answers but fails (a timeout, an error) fails them after three attempts.
Set `UNIFIED_TABLES_REQUIRE_AZURITE=1` to make an absent emulator a failure too. Each integration test
uses partitions of its own (a name plus a GUID), so several test runs can share one emulator.

Three GitHub workflows run the suite with that variable set and a pinned Azurite (3.35.0,
`--loose --skipApiVersionCheck`), so nothing is gated by skipped integration tests:

- **CI** (`.github/workflows/ci.yml`, job `Build and test`) runs on every pull request and every push to
  `main`: it builds the solution in Release and runs all tests on `ubuntu-latest`. It needs no secret.
  This is the check that gates a change on the tests; require `CI / Build and test` in branch protection.
- **SonarQube** (`sonarqube.yml`, job `Build and analyze`) runs the scanner's begin, the build, the tests
  and the scanner's end as separate steps, so a failed build or test fails the job and its analysis is
  not uploaded. It needs a valid `SONAR_TOKEN` and fails at `Begin analysis` without one, whatever the
  tests do.
- **Publish** (`publish.yml`) runs the tests, packs, and stops before the push unless the packed
  `Unified.Data.Tables` depends on `Unified.Data.Tables.Abstractions` `[x.y.z]` and
  `Unified.Data.Tables.InMemory` on `Unified.Data.Tables` `[x.y.z]`, the versions packed in the same run.

`TableEntitySerializer.OversizedCellPolicy` is process-wide state, so a test class that sets it, or
calls `AddUnifiedTableStorage` or `AddUnifiedInMemoryStorage` (which set it), must carry
`[Collection("OversizedCellPolicy")]`; a test of the main test project fails when one does not.

---

## License

Licensed under the [MIT License](LICENSE). Copyright © Serhii Seletskyi.
