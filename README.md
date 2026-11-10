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
  blobs. Opt-in; types that use neither are unchanged (roll forward only once a type opts in).
- **Configurable caching** — reads are served from `IMemoryCache` per a registration-time `CachePolicy`
  (`Sliding`, `Absolute`, or `Disabled` — per entity type or globally); writes keep the entity cache
  coherent and invalidate the relevant query caches automatically.
- **Optimistic concurrency (ETag)** — a caller-supplied `ETag` enforces strict concurrency; a lost
  race throws the provider-agnostic `ConcurrencyConflictException` (map to HTTP 409). Explicit
  `ConcurrencyMode.Strict` / `LastWriterWins` overloads make intent greppable, and
  `MutateAsync(id, e => e.Count++)` packages the read → mutate → strict-write → retry loop that makes
  derived values (counters, unions) correct under concurrency.
- **Upsert & batches** — `UpsertAsync` (single round-trip insert-or-replace), `CreateBatchAsync` /
  `UpsertBatchAsync` (partition-grouped 100-row transactions), `CountAsync` (keys-only projection).
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
| `UpsertAsync(entity)` | Insert-or-replace in one round trip. Unconditional by design (last writer wins); preserves a caller-supplied `CreatedAt`. |
| `OneAsync(id)` | Fetch one row by composite id, or `null`. |
| `ExistsAsync(id)` | Whether a row exists (cache-aware). |
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
| `UpdateAsync(entity, mode)` | Full replace with explicit `ConcurrencyMode`. |
| `UpdateAsync(id, builder)` | Partial `Merge` — writes only the declared columns (nested paths supported, optional `WithETag`); returns the new ETag. |
| `MutateAsync(id, e => …)` | Extension: read → mutate → `Strict` write, re-reading and re-applying on conflict (CAS). |
| `GetOrCreateAsync(id, factory)` | Extension: read-or-insert; a lost create race converges on the winner's row. |
| `MutateOrCreateAsync(id, create, mutate)` | Extension: insert-or-mutate CAS — the delta applies uniformly on first insert and on updates. |
| `TryMutateAsync(id, e => …)` | Extension: outcome-returning CAS — `Updated` / `NotFound` / `Conflicted`, nothing thrown for expected branches. |
| `TryTransitionAsync(id, when, apply)` | Extension: exactly-once transition as a result — the race-loser gets `PreconditionFailed` with the winner's row. |
| `CreateBatchAsync(entities)` | Transactional inserts, grouped by partition, 100 per transaction, atomic per transaction (`DuplicateKeyException` on an existing or repeated key). Split-aware types too: an entity's continuation rows ride in its transaction. |
| `UpsertBatchAsync(entities)` | Transactional insert-or-replace, same chunking. |
| `CountAsync(partition?)` | Row count via keys-only projection (Tables has no server-side count). |
| `DeleteAsync(id)` | Delete one row (idempotent). |
| `DeletePartitionAsync(partition)` | Batch-delete a whole partition; returns the count. |
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
along as `InnerException`), and the conflicting row's cache entry is evicted so the next read is
fresh.

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
variants and are validated eagerly (collisions and unknown targets throw on first use of the type).

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
  the same held for any `_`-prefixed sentinel. The rule is enforced on **read** only: flattening still
  names columns after the property verbatim, so a property called `_Foo` does write a column `_Foo`
  (which is then skipped on the way back). Keep `_` out of property names.
- **`persistType: true` writes `_TypeName`** with `Type.AssemblyQualifiedName`. `FromTableEntity<TBase>(discriminator)`
  reads it back constrained to a base type, and `TryFromTableEntity<TBase>` additionally tolerates a
  row that carries no discriminator at all.
- A base-constrained read does **not** recompute `Id` from the row keys, because a polymorphic key
  (an aggregate version, an inverted tick count) is unrelated to any property.

---

## Large values: split and blob offload

A table cell holds 64&nbsp;KB and a row 1&nbsp;MB. By default an oversized value throws
(`OversizedCellPolicy.Throw`); nothing is ever trimmed. Opt a property in and it is stored
losslessly instead. Types with no `[TableValueSplit]` and no `TableBlobContent` property produce the
same columns and make the same SDK calls as before.

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

The attribute goes on a property, or on a class to cover every `string`/`byte[]` property of it. On a
property it must be a `string`, a `byte[]` or a type stored as one JSON cell (a collection, or a concrete
type that does not flatten into columns); on an interface or abstract type, which may flatten at runtime,
it throws `NotSupportedException` on first use of the entity type, as it does on a native scalar or a type
that flattens.
`Offload = BlobOffload.Never` forbids tier 3 below; a value that would need it throws
`OversizedValueException` (a `SerializationException`). Its `ColumnName`, `EncodedBytes` and `Reason`
describe the value, and `OffloadForbiddenByProperty` tells you which fix applies: `true` means blob offload
is configured but this property forbids it (set its `Offload` to `BlobOffload.AboveThreshold`), `false`
means no offload is configured (set `UnifiedTableStorageOptions.BlobOffload`). The message names that fix.

### Tiers

For each opted-in property the smallest tier that fits is used:

1. **Inline** - one cell, exactly as before, when the value fits.
2. **Split** - cells `{Prop}__c0..cN` (strings in chunks of at most 32,000 chars, never splitting a
   surrogate pair; `byte[]` as raw binary cells of at most 65,536 bytes), plus `{Prop}__chunks`,
   `{Prop}__sha256`, `{Prop}__enc` and `{Prop}__len`. When the cells do not fit one row, the rest go to
   continuation rows with RowKey `{RowKey}~0001`, `~0002`, ... in the same partition, written in **one**
   entity-group transaction with the main row.
3. **Blob offload** - when the encoded value exceeds `BlobOffloadOptions.ThresholdBytes` (default
   3,145,728) or the row would not fit one transaction, the row holds only `{Prop}__blob` (reference
   JSON: path, sha256, length, content type, encoding) and the bytes live in an Azure Blob.

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

The blob client is chosen only when `BlobOffload` is set, in this order, and the library never registers
or replaces the host's own `BlobServiceClient` service:

1. `BlobOffloadOptions.ServiceClient`, when set (a `TableStorage<T>` constructed directly uses it only
   when no `BlobServiceClient` is passed to the constructor: an explicit constructor argument wins);
2. the `BlobServiceClient` the host registers in DI, only when `BlobOffloadOptions.UseRegisteredServiceClient`
   is `true` (resolved lazily, so it may be registered before or after `AddUnifiedTableStorage`);
3. one the overload derives, kept to the library: the connection-string overload builds it from the same
   string when it has a blob endpoint (`BlobEndpoint`, `UseDevelopmentStorage=true`, or an `AccountName`
   with no `TableEndpoint` or an Azure Storage one); the credential overload derives
   `{account}.blob.core.{suffix}` only for `*.table.core.*` hosts (not Cosmos `.table.cosmos.azure.com`).

```csharp
services.AddUnifiedTableStorage(o => o.BlobOffload = new BlobOffloadOptions
{
    ServiceClient = new BlobServiceClient(blobUri, credential),   // or UseRegisteredServiceClient = true
});
```

With no client found, only types that have `[TableValueSplit]` or `TableBlobContent` members fail to resolve
(`InvalidOperationException` naming the fix); every other `IStorage<T>` resolves as before. Without offload
configured, tiers 1 and 2 still work and a value that needs tier 3 throws `OversizedValueException`.

The container belongs to offload alone: do not store other blobs in it. Sweeps and partition deletes
only ever delete names of the exact offload shape (see below) and skip anything else, but they list
the whole `{table}/` prefix.

### Files: `TableBlobContent`

A `TableBlobContent` property is always offloaded, whatever its size, and reading an entity never
downloads it. `TableBlobContent.FromBytes(bytes, contentType)` and `FromStream(stream, contentType)` create
one; `FromStream` never buffers the content in memory and leaves your stream open. A non-seekable stream
is copied to a delete-on-close temporary file under `Path.GetTempPath()` (so the host needs temp disk space
and access); a seekable stream is read twice (hash, then upload) and must not change until the write completes. `Length` and `Sha256`
are known after the write (immediately for `FromBytes`). On a loaded entity, `await content.OpenReadAsync()`
streams the blob lazily and verifies the SHA-256 when the stream reaches its end.

### Ordering and integrity

- **Write** - the blob is uploaded create-only first (an existing blob with the same name and matching
  `sha256` counts as success, and its `Last-Modified` is touched), then the entity is written.
- **Replace** - new blob, entity swap guarded by the ETag, then best-effort delete of old blobs the new
  row no longer references.
- **Delete** - the entity first, then its blobs.
- A failed entity write never deletes a blob. Blobs are named
  `{table}/{partitionKey}/{rowKey}/{property}/{sha256}`, so rewriting identical content reuses the blob.
  Each segment is percent-escaped (`Uri.EscapeDataString`) with `.` also escaped as `%2E`, and an empty
  key is written as `%`: no segment is ever `.` or `..`, which the Blobs SDK would collapse out of the
  URI (a key `.` or `..` would otherwise address another blob, even outside the table's prefix).
- Reads reassemble or download eagerly (`string`, `byte[]`) and verify the SHA-256. A mismatch, a missing
  chunk, continuation row or blob throws `TableValueIntegrityException`; partial data is never returned.
  A transport that transparently decompresses `Content-Encoding: gzip` would break the hash check.
- A read that races a replace or delete (the main row is read before its continuation rows and blobs) can
  see parts or a missing blob of another version. On such a failure the main row is read again: if its
  ETag changed, the whole read is retried from the new version (three attempts in all); a row deleted
  meanwhile reads as absent (`OneAsync` returns null, a query skips it); only an unchanged ETag is reported
  as `TableValueIntegrityException`. Data of two versions is never combined.
- An offloaded value is never returned empty: writing such an entity back would drop the reference and let
  the sweep delete the value. Reading a row that holds a `{Prop}__blob` reference through something that
  cannot fetch it throws, naming the column:
  - the public `FromTableEntity<T>()`, or a store whose type has no `[TableValueSplit]` or
    `TableBlobContent` property, throws `InvalidOperationException`;
  - a store whose type has such properties but no `BlobOffload` configured throws
    `BlobOffloadNotConfiguredException` (an `InvalidOperationException` with `TableName` and `ColumnName`; a
    configuration error, not corruption, and not retried) from the read. A `TableBlobContent` property is
    still read lazily, and throws that exception when it is opened.

  Only a projection (`QueryOptions.Select`) that omits the property leaves it at its default. Split cells are
  reassembled by any reader; a plain reader that sees a split row without its continuation rows throws
  `TableValueIntegrityException`.

### Sweeping orphans

Lost races, crashes and failed deletes leave unreferenced blobs. Best-effort cleanup after a replace or
delete only removes blobs untouched for `BlobOffloadOptions.CleanupGrace` (default 5 minutes); younger ones
are left for the sweep, which must be scheduled: call it yourself, or register the
[hosted sweep](#hosted-sweep). A committed write or delete is never reported as failed because its token
was cancelled: its cleanup runs on for at most 5 more seconds, then stops and leaves the rest of the blobs
(a large partition's, say, during a host shutdown) to the sweep.

Both ages are measured on the storage service's clock, the one that stamps a blob's `Last-Modified`: the
cleanup after a write or delete uses the `Date` of the commit response, and the sweep and the cleanup after
a partition delete ask the blob service for its time (one container properties request) before listing.
A client clock that runs ahead therefore cannot make a freshly touched blob look old. Only when no service
time is available is the client clock used, moved back by a further 5 minutes
(`LargeValueTable.ClientClockMargin`). The service clock has one-second resolution, so a blob written in
the current second is not yet older than `TimeSpan.Zero`.

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
  an orphan the sweep deletes). Deploy in two steps: first the package bump with no `[TableValueSplit]` or
  `TableBlobContent`, which writes exactly what 0.8.3 wrote; then, once no older reader remains (swapped
  slots drained, rollback no longer to a pre-0.9.0 build, every service that reads the table upgraded), the
  attributes. Rolling back past the second deploy needs the data rewritten first.
- **Back up and restore an entity as one unit**: its main row, its continuation rows (same partition,
  RowKeys `{RowKey}~NNNN`) and its blobs in the offload container, taken together. A row restored without
  its parts or blobs (or with those of another version) reads as `TableValueIntegrityException`.
- **A table copied raw shares the source's blobs**: each `{Prop}__blob` reference holds the blob path
  under the source table's name, so the copy reads the source's blobs, and once the source replaces or
  deletes those rows its cleanup and sweep delete blobs the copy still reads. Give the copy its own copy of
  the blobs (rewrite the entities through the library, or copy the container and point the copy's
  `ContainerName` at it). Blob versioning or soft delete on the container makes such a mistake recoverable.
- **The offload container is the library's.** Do not put other blobs in it, and do not share it between
  applications whose tables have the same names.
- **Schedule the sweep** (above): nothing else removes orphans younger than `CleanupGrace` or left by a
  crash.

### Limitations

- In RowKeys of split-aware types, only the continuation-row shape is reserved: a key ending with `~`
  and exactly four digits (`r~0001`, `a~b~0042`). Writes, batch writes and partial updates refuse it with
  `ArgumentException`; every other `~` is allowed (`a~b`, `x~12`, `a~0001x`, append-log keys). Deletes
  accept any key, so a row written before the type opted in stays deletable; deleting a continuation row's
  id is a no-op. A row from before the opt-in (or of another type sharing the table) keyed
  `{RowKey}~0001` is never overwritten: a write of `{RowKey}` that needs that key for a new continuation
  row fails as a whole with `InvalidOperationException` naming the key, and stores nothing. The escaped
  blob name must fit 1,024 characters.
- Keys of split-aware types must be well-formed UTF-16: a partition or row key with an unpaired surrogate
  (`"a\uD800"`) would name the same blobs as one with U+FFFD in its place, so writes refuse it with
  `ArgumentException`. Plain types are unaffected.
- No LINQ filter and no partial update (`UpdateAsync(id, builder)`) on a large property; not supported by
  `IPolymorphicStorage`.
- Batch writes of these types keep the batch contract: per partition, each entity's rows (main row,
  continuation rows, deletes of stale ones) are one unit, units are packed in order into entity-group
  transactions (at most 100 actions and the same byte and 4&nbsp;MiB wire budgets as a single write), and
  each transaction is atomic; an entity's rows never straddle two. Atomicity is per transaction only: a
  failing transaction leaves earlier ones committed. Each transaction's blobs are uploaded just before it
  and released ones deleted after it commits. A key repeated in `CreateBatchAsync` throws
  `DuplicateKeyException` before anything is written; in `UpsertBatchAsync` its last occurrence is
  written. Upserts do one point read per entity first, and each replace is conditional on the version
  read: a transaction that loses a race is planned again and retried, and after three lost races
  `UpsertBatchAsync` throws `ConcurrencyConflictException` naming the entity (earlier transactions stay
  committed).
- Split-aware writes do one point read first (to find stale parts and old blobs): order is table get,
  blob get, blob upload, table update. A `LastWriterWins` `UpdateAsync` can therefore throw
  `ConcurrencyConflictException` after three lost races.
- A `QueryPageAsync` page can hold zero items with a non-null continuation token when it contained only
  continuation rows; keep paging until the token is null.
- Reads and scans cost more for these types: an entity whose value is split across continuation rows
  takes one extra range query for its parts (on every read, queries included), and an eager offloaded
  value (`string`, `byte[]`) is downloaded from blob storage on every read, one blob per entity, so a
  query over N such entities makes N extra requests. `TableBlobContent` is never downloaded by a read.
  The entity cache holds the full value.
  For types with very large eager values use `CacheFor<T>(CachePolicy.Disabled)`.
- A `TableBlobContent` on an entity you hold (or one the entity cache returns) names the blob of the version
  it was read from. Once a replace or delete has removed that blob (after `CleanupGrace`), `OpenReadAsync`
  throws `TableValueIntegrityException` saying the row may have changed; read the entity again. It never
  returns other bytes: blobs are named by their hash.
- A projection of a plain type (one that does not opt the property in) that names a property another type
  stored split or offloaded reads it as null: the plain store neither fetches nor recognises the value's
  cells. Read such rows through the opted-in type.
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
`BlobServiceClient` and ignores `BlobOffloadOptions.ServiceClient`).

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
Set `UNIFIED_TABLES_REQUIRE_AZURITE=1` to make an absent emulator a failure too: CI does, and starts a
pinned Azurite (3.35.0, `--loose --skipApiVersionCheck`) before the tests, so a release or pull request
is never gated by skipped integration tests.

---

## License

Licensed under the [MIT License](LICENSE). Copyright © Serhii Seletskyi.
