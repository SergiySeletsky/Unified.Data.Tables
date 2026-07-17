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
  complex graphs. Oversized cells are compressed — and, as a last resort, truncated — so a single large
  property can never blow the 64&nbsp;KB limit and lose the whole row.
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
- **Composite id convention** — `Id` is `"{PartitionKey}|{RowKey}"` (shared helpers in `EntityId`); row keys
  may themselves contain `|`.

---

## Installation

```bash
dotnet add package Unified.Data.Tables
```

This is shipped as three packages:

| Package | Contents | Use it in |
| --- | --- | --- |
| **Unified.Data.Tables** | `TableStorage<T>`, the serializer, cache policies, DI helpers (Azure dependencies) | server / host projects |
| **Unified.Data.Tables.Abstractions** | `Entity`, `IStorage<T>`, `QueryOptions`, `ConcurrencyMode`, `UpdateBuilder<T>`, `EntityId`, `[ColumnAlias]`, `[ProtectedProperty]` — no Azure/hosting deps | shared/domain & Blazor WebAssembly projects |
| **Unified.Data.Tables.InMemory** | `InMemoryStorage<T>` — serializer-faithful in-memory `IStorage<T>` | test projects, dev/offline mode |

`Unified.Data.Tables` references the abstractions transitively, so most apps just install it. In a **browser-safe** shared library that only defines entities and repository contracts, reference the abstractions alone.

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
| `CreateBatchAsync(entities)` | Transactional inserts, grouped by partition, 100 per transaction (`DuplicateKeyException` on an existing or repeated key). |
| `UpsertBatchAsync(entities)` | Transactional insert-or-replace, same chunking. |
| `CountAsync(partition?)` | Row count via keys-only projection (Tables has no server-side count). |
| `DeleteAsync(id)` | Delete one row (idempotent). |
| `DeletePartitionAsync(partition)` | Batch-delete a whole partition; returns the count. |

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
against Azure Tables.

---

## Building & testing

```bash
dotnet build Unified.Data.Tables.slnx -c Release
dotnet test  Unified.Data.Tables.slnx -c Release
```

The test suite mixes fast unit tests (Azure SDK mocked with NSubstitute) with integration tests that run
against a local **Azurite** emulator. The integration tests self-skip when Azurite is not reachable, so
`dotnet test` is safe to run anywhere.

---

## License

Licensed under the [MIT License](LICENSE). Copyright © Serhii Seletskyi.
