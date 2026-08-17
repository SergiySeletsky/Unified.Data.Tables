# Polymorphic storage — design

**Date:** 2026-08-17
**Target release:** 0.8.0
**Status:** approved for implementation planning

## Problem

`IStorage<T>` stores one CLR type per table. A large class of Azure Table workloads does the
opposite: many concrete types share one table, discriminated by a stored type name, and are read
back as a common base. Event stores, command logs, outboxes and two-phase-commit tables all have
this shape.

The library already has the serializer half of this. `TableEntitySerializer.ToTableEntity(...,
persistType: true)` writes an assembly-qualified type name into `_TypeName`, and
`FromTableEntity(this TableEntity)` resolves it late-bound. What is missing is a *storage contract*
that surfaces it.

The driving consumer is IntelliGrowth's CQRS persistence layer, where five stores share this exact
pattern and are blocked on it: `CommandTableStore`, `StateEventTableStore`,
`IntegrationEventTableStore`, `TransactionTableStore` and `ProcessStore`.

## Why this cannot be `IStorage<T>`

`IStorage<T>` is `where T : class, IEntity, new()`. A polymorphic base fails that constraint twice:

1. **`new()`** — the bases in question are interfaces (`ICommand`, `IEvent`, `IIntegrationEvent`) or
   abstract classes. Neither can be `new()`-constrained.
2. **`IEntity`** — `IEntity` requires `Id`, `CreatedAt`, `UpdatedAt`, `ETag`, `Timestamp`. A message
   base such as `AbstractMessage` has `Id`, `Created` and `AggregateRootId` and none of the other
   four. Even a `new()`-free `IStorage<T>` would not admit it.

Relaxing `IStorage<T>` is therefore not a smaller change than adding a contract — it is a change that
still would not work. This ships as a **sibling contract**. `IStorage<T>` and every existing
behaviour are untouched; the PR is purely additive apart from one bug fix (see *Reserved column
namespace*).

A polymorphic *mode* on `IStorage<T>` was evaluated and rejected in 0.6.0 for these reasons. This
design does not revisit that conclusion; it changes the shape of the answer.

## The contract

`src/Unified.Data.Tables.Abstractions/IPolymorphicStorage.cs`

```csharp
public interface IPolymorphicStorage<TBase> where TBase : class
{
    Task EnsureCreatedAsync(CancellationToken ct = default);

    Task<PolymorphicEntry<TBase>> InsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default);
    Task<PolymorphicEntry<TBase>> UpsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default);
    Task<int> InsertBatchAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default);

    Task MergeColumnsAsync(TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct = default);

    Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default);
    Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(string? partition = null, int? take = null, CancellationToken ct = default);
    IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(string? partition = null, int? take = null, CancellationToken ct = default);

    Task DeleteAsync(TableKey key, CancellationToken ct = default);
    Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default);
    Task<int> CountAsync(string? partition = null, CancellationToken ct = default);
}
```

Constrained to `class` only. No Azure types in any signature, so it compiles on the Abstractions
package's `netstandard2.0` leg. No default interface methods — `netstandard2.0` cannot carry them;
convenience lives in `PolymorphicStorageExtensions`, following the existing `StorageExtensions` /
`AppendLogExtensions` precedent.

### Supporting types

```csharp
public readonly record struct TableKey(string PartitionKey, string RowKey)
{
    public static TableKey FromId(string id);   // splits on the first '|', bridging EntityId
    public string ToId();
}

public sealed record PolymorphicWrite<TBase>(
    TableKey Key,
    TBase? Item,                                       // null => typeless marker row
    IReadOnlyDictionary<string, object>? SystemColumns = null)
    where TBase : class
{
    public static PolymorphicWrite<TBase> Marker(TableKey key, IReadOnlyDictionary<string, object> systemColumns);
}

public sealed class PolymorphicEntry<TBase> where TBase : class
{
    public TableKey Key { get; }
    public TBase? Item { get; }                        // null exactly when the row has no _TypeName
    public TBase Value { get; }                        // Item, throwing when the row is a marker
    public string? Discriminator { get; }
    public string? ETag { get; }
    public DateTimeOffset? Timestamp { get; }
    public IReadOnlyDictionary<string, object> Columns { get; }   // raw cells, suffixes intact

    public TValue Column<TValue>(string name);         // strict: throws when absent
    public bool TryColumn<TValue>(string name, out TValue value);
}
```

## Key design decisions

### Keys are explicit and verbatim

`TableKey` is supplied on every operation. It is **never** passed through `IdNormalization`.

The five consumer stores use six different key schemes, and two of them (`transactionId`,
`ProcessRequest.Id`) are ambient values with no relation to the stored object at all. A
`Func<TBase, TableKey>` strategy cannot see those without a context parameter — at which point it is
just an argument. Explicit keys serve all six with no hook:

| Scheme | Example |
| --- | --- |
| Aggregate-id partition | `AggregateRootId` |
| Ambient transaction id | `transactionId` |
| Zero-padded version | `Version.ToString("D9")` |
| Inverted ticks | `(DateTime.MaxValue.Ticks - dt.Ticks).ToString("D20")` |
| Case-sensitive id | a `UnifiedId` row key |
| Literal marker | `"FlagEntity"` |

Normalization must be off, not merely defaulted off: `EntityId.Normalize` lower-cases, and a
`UnifiedId` row key rewritten to lower case addresses a **different row**. Existing rows must keep
their exact keys to remain addressable.

### Type resolution behind a seam, with a gate that cannot be disabled

```csharp
public interface ITypeDiscriminator
{
    string ToDiscriminator(Type type);
    Type Resolve(string discriminator, Type baseType);
}
```

- `AssemblyQualifiedTypeDiscriminator` (default) writes `Type.AssemblyQualifiedName` — byte-identical
  to what `persistType: true` writes today, so existing tables are readable with **no migration and
  no configuration**.
- `TypeDiscriminatorMap` is an opt-in allow-list with `Map<T>(token)`,
  `MapAssignableTo<TBase>(assembly)` for bulk registration, and `WithAssemblyQualifiedFallback()` so
  legacy rows stay readable while rewrites converge the table in place.
- **Every** read verifies `typeof(TBase).IsAssignableFrom(resolved)` and throws otherwise, including
  when a custom resolver claims to have checked.

Deserializing a type named by stored bytes is a gadget surface. The assignability gate is the
control that needs no configuration to be effective, so security does not depend on the consumer
having read the docs. Making the allow-list *mandatory* was rejected: it would make every existing
row unreadable on upgrade, which is precisely the migration this feature exists to enable.

Assembly-qualified names also weld rows to assembly identity — a rename, namespace move or
strong-name change orphans them — and cost a few hundred bytes on every row, charged against the
transaction byte budget that caps batch size. The docs recommend a map for anything new.

### Reserved column namespace (and a latent bug fixed)

A leading `_` marks a **system column**: never produced from a property, never fed to a property
setter, on every read path including the two existing overloads.

This fixes a real bug. `TableEntityValue.Create` splits column names on `_` with
`RemoveEmptyEntries`, so `_TypeName` becomes path `["TypeName"]` and `_IsPublished` becomes
`["IsPublished"]`. A stored type declaring either property is silently clobbered today. The rule
codifies existing reality rather than inventing one — a property literally named `_Foo` already
writes to column `_Foo` and reads back into property `Foo`.

Nothing this library has ever written produces a `_`-prefixed column other than `_TypeName`, so no
row it authored changes meaning. A **hand-authored** `_X` column that relied on landing in property
`X` no longer will. That is the one behavioural change in the PR and it is called out in the
changelog.

### Mutation surface

Insert / Upsert / Delete whole rows, plus `MergeColumnsAsync` restricted to `_`-prefixed columns
with `_TypeName` rejected.

Rows in these tables are immutable facts with mutable workflow flags — exactly what `_IsPublished`
and `_IsCommitted` are, and all three consumer merge sites touch nothing else. `_TypeName` is
rejected because re-typing a row would strand the previous type's data columns. A polymorphic
`UpdateBuilder<TBase>` was rejected: it sources its blocklist from `typeof(IEntity).GetProperties()`,
which `TBase` does not implement.

### No caching

`TableStorage<T>` keys its cache on `typeof(T).FullName`, so two stores over one table never
invalidate each other; its snapshot round-trips through `FromTableEntity<T>()`, which downcasts a
derived instance to the base and drops the derived data. Rather than mitigate three coupled hazards,
this store has no cache at all — which also suits an append-only fact table.

### The store owns its table

No `_TypeName` filter anywhere; enumerating operations see every row in scope. Server-side type
filtering would need `TableFilterTranslator` to admit a non-property column, a `QueryOptions` member,
and inclusion in `PageCursor.Fingerprint` (a type filter changes result shape, so resumed pages would
otherwise silently change meaning). That is a separate PR, and unnecessary here: all five consumer
stores own their tables outright.

Pointing two stores at one table means they see each other's rows. Documented, not prevented.

### Batch chunking

Reuses `BatchPlanner`, capping on entity count (100) **and** payload bytes (3 MB). Count-only
chunking sends a legal-looking 100-entity batch that the service rejects with 413 once rows carry
binary or large JSON columns — after earlier chunks have already committed.

The cost is a genuine semantic difference: a set the consumer sends as one transaction may split into
two, so a trailing commit-marker row could land in a later chunk than the rows it guards. That window
already exists above 100 rows under count-only chunking; the byte cap widens it rather than
introducing a new failure class. Called out in the changelog.

## Dependency injection — keyed services

`StateEventTableStore` and `TransactionTableStore` are both `IPolymorphicStorage<IEvent>` over
*different* tables, so a plain open-generic registration cannot serve both.

```csharp
services.AddUnifiedPolymorphicTable<IEvent>("StateEventStore");
services.AddUnifiedPolymorphicTable<IEvent>("TransactionStore");
services.AddUnifiedPolymorphicTable<ICommand>("CommandStore");
```

registers each keyed by table name. Consumers resolve with:

```csharp
public StateEventStore([FromKeyedServices("StateEventStore")] IPolymorphicStorage<IEvent> storage)
```

Keyed DI is native to `Microsoft.Extensions.DependencyInjection` 8+, needs no new types, and the key
doubles as the table name. `InMemoryServiceCollectionExtensions` mirrors the shape exactly
(`AddUnifiedInMemoryPolymorphicTable<TBase>`) so a test host swaps one line.

## Mapping to the driving consumer

| Store | `TBase` | Partition key | Row key | Contract members used |
| --- | --- | --- | --- | --- |
| `CommandTableStore` | `ICommand` | `AggregateRootId` | `command.Id` | `InsertAsync`, `QueryAsync()` |
| `StateEventTableStore` | `IEvent` | `AggregateRootId` | `Version:D9` | `InsertBatchAsync`, `QueryAsync(partition)` |
| `IntegrationEventTableStore` | `IIntegrationEvent` | `AggregateRootId` | inverted ticks `D20` | `InsertAsync` + `_IsPublished`, `MergeColumnsAsync`, `QueryAsync()` |
| `TransactionTableStore` | `IEvent` | `transactionId` | inverted ticks `D20`, `"FlagEntity"` | heterogeneous `InsertBatchAsync` incl. marker row, `MergeColumnsAsync`, `QueryAsync()` |
| `ProcessStore<THandler>` | `AbstractMessage` | process `id` (ambient) | `message.Id` | `InsertBatchAsync` + `_IsPublished`, `MergeColumnsAsync`, `QueryStreamAsync` |

Two capabilities exist specifically because of this table set: the **typeless marker row** (`Item` is
`null`) so `FlagEntity` can share one Entity Group Transaction with the events it guards, and
**system columns on a write** so `_IsPublished` rides alongside the serialized object.

Two consumer defects the contract removes by construction:

- `ProcessStore.GetMessagesAsync` calls `ExecuteQuerySegmentedAsync(query, null)` and reads only the
  first segment — it silently truncates at one page. `QueryStreamAsync` follows continuation tokens
  internally, so the caller cannot omit the loop.
- `ProcessStore.PersistMessagesAsync` throws `NotSupportedException` above 100 messages rather than
  chunking. `InsertBatchAsync` plans batches via `BatchPlanner`.

`ProcessStore<THandler>` is generic over the handler and derives its table name from
`typeof(THandler).Name`, so it needs one keyed registration per handler — the same shape as the two
`IEvent` stores. It additionally depends on `TableNamePrefix`, which is a separate PR; until that
lands it must pass the fully-qualified table name.

## Wire-format compatibility

Verified:

- **Discriminator** — column name and value unchanged. A `PolymorphicTableStorage<IEvent>` pointed at
  an existing `StateEventStore` table reads it as-is.
- **`RowKeys.VersionKey(v)`** is byte-identical to the consumer's `(int.MaxValue - v).ToString("D20")`.
  Confirmed numerically.
- **`RowKeys.InvertedTicks` is NOT interchangeable** with the consumer's inverted-tick helper. Both
  compute `MaxValue.Ticks - ticks`, but the library formats `"D19"` and the consumer `"D20"`.
  `DateTime.MaxValue.Ticks` is 19 digits, so `D20` zero-pads to 20 characters and `D19` does not.
  Each sorts correctly in isolation; **mixed in one partition the 20-character keys sort before every
  19-character key and interleave wrongly.** The explicit-key design means consumers keep their own
  helper, so this PR is unaffected — but a width-parameterised `RowKeys` overload is worth a
  follow-up.
- **Marker rows become readable** where they previously threw. `TryFromTableEntity<TBase>` returns
  `false` for an *absent* discriminator. A discriminator that is *present* but unresolvable or not
  assignable still throws — absent and broken must not look alike.
- **No `Id` rewriting.** `RestoreIdFromKeys` is suppressed unconditionally on the polymorphic path
  rather than relying on `TBase` not being `IEntity`.
- **Nested flattening, enums, dates, decimals and the 64 KB path are untouched** — the same `Flatten`
  and the same `TableEntityValue` dispatch.

### Accepted risk: JSON cell payloads

`__Json` / `__GZip` cells are read with `System.Text.Json` under `PropertyNamingPolicy = CamelCase`
and `PropertyNameCaseInsensitive = true`. The driving consumer's existing blobs were written by
Newtonsoft with `CamelCasePropertyNamesContractResolver` + `StringEnumConverter`.

Case-insensitive matching means ordinary camelCase/PascalCase members round-trip. A member **renamed**
via a Newtonsoft `[JsonProperty("x")]` attribute inside a JSON blob will **not** bind, because
System.Text.Json ignores that attribute. Newtonsoft `$type` payloads and Newtonsoft's default
`DateTime` format would likewise not round-trip.

**Decision:** accepted for this PR. The design keeps the door open — the serializer options are
currently `private static readonly` inside `TableEntityValue`; exposing an `IJsonCellSerializer` seam
later is additive and does not change `IPolymorphicStorage<TBase>`. Consumers should sample real rows
before cutting over a table whose types use `[JsonProperty]` renames.

## Out of scope (deliberate)

Kept out to keep the PR landable and reviewable:

- Caching on the polymorphic store.
- LINQ predicates and server-side `_TypeName` filtering.
- `QueryPageAsync` cursors — `QueryStreamAsync` follows continuation tokens internally, which is what
  a scan actually needs.
- `[ProtectedProperty]`, `UpdateBuilder`, `ConcurrencyMode`.
- The separate upstream gaps the consumer also needs: `TableNamePrefix`, a partition-key resolver,
  `ManyAsync` batch point-lookup, and the `RowKeys` D20 overload. Each is its own PR.

## Testing

Both implementations are tested to the same contract, and the repo's existing doctrine of
byte-identical exception messages between the Azure store and the fake is enforced for the new
surface.

- `PolymorphicStorageTests` — discriminator written for the *runtime* type; derived identity survives
  the read; heterogeneous batch produces one transaction per partition; oversized rows split on the
  byte budget; duplicate key throws `DuplicateKeyException`; merge sends `TableUpdateMode.Merge` with
  `ETag.All` and no prior read; `_TypeName` in a merge payload throws; unprefixed system column
  throws; marker row returns `Item == null` with columns intact; keys are used verbatim under default
  `IdNormalization`; multi-page `QueryStreamAsync` yields every row.
- `TypeDiscriminatorTests` — map round-trip; unknown token throws with guidance; duplicate simple name
  throws *at registration*; type not assignable to `TBase` throws; AQN fallback off by default then on.
- `TableEntitySerializerTests` additions — system column not written into a matching property;
  base-constrained read skips `Id` restoration; `TryFromTableEntity` distinguishes absent from broken;
  protected setter round-trips; ctor-less derived type uses `GetUninitializedObject`.
- `InMemoryPolymorphicStorageTests` — the full slate against the fake, plus message-parity assertions
  and a round-trip proving the fake exercises production serialization rather than object identity.

A new `PolymorphicHarness<TBase>` is required; `StorageHarness<T>` cannot be reused because its
`where T : class, IEntity, new()` is the exact constraint being escaped.

## File plan

**New — Abstractions:** `TableKey.cs`, `IPolymorphicStorage.cs`, `PolymorphicWrite.cs`,
`PolymorphicEntry.cs`, `PolymorphicStorageExtensions.cs`, `PolymorphicMessages.cs` (internal shared
exception text).

**New — Azure:** `ITypeDiscriminator.cs`, `AssemblyQualifiedTypeDiscriminator.cs`,
`TypeDiscriminatorMap.cs`, `PolymorphicTableStorage.cs`, `TableRowSize.cs`, `TableInitializer.cs`.

**New — InMemory:** `InMemoryPolymorphicStorage.cs`.

**Modified:** `TableEntitySerializer.cs` (system-column rule, `ITypeDiscriminator` overload,
`FromTableEntity<TBase>` / `TryFromTableEntity<TBase>`, `restoreId` flag);
`TableStorage.cs` (two behaviour-preserving extractions only — `EstimateSize` delegates to
`TableRowSize`, lazy-create block becomes a `TableInitializer` field);
`UnifiedTableStorageOptions.cs` (`TypeDiscriminator` + `ResolveTypeDiscriminator()`);
`ServiceCollectionExtensions.cs` and `InMemoryServiceCollectionExtensions.cs` (keyed registration);
`CHANGELOG.md`; `README.md` (a `## Polymorphic storage` section — the README is packed into all
nupkgs, so omitting it ships package docs that contradict the code);
`Directory.Build.props` (`<Version>` 0.7.0 → 0.8.0).

**Tests:** `TestSupport/PolymorphicHarness.cs` (new), `TestSupport/TestModels.cs` (extended),
`PolymorphicStorageTests.cs`, `InMemoryPolymorphicStorageTests.cs`, `TypeDiscriminatorTests.cs` (new),
`TableEntitySerializerTests.cs` (extended).

## Effort

~12 new source files, ~6 modified. Roughly 1,500–1,700 lines of source and ~900 of tests.
4–6 focused days.

The line count is driven by house style, not complexity: 30–40% comment density, and
`GenerateDocumentationFile=true` with `TreatWarningsAsErrors=true` means every public member needs
XML docs before it compiles. The genuinely novel logic is small — the serializer already resolves
types late-bound, `TypeMetadataCache` is already `Type`-keyed and handles ctor-less types,
`BatchPlanner` exists, and `_TypeName` is already projection-safe with a test asserting it.

Two things carry most of the real effort: `InMemoryPolymorphicStorage` is a full behavioural mirror
rather than a stub, and the README/CHANGELOG are load-bearing deliverables.

Risk is low and well-bounded. The PR is additive; the only touches to `TableStorage.cs` are covered
by existing tests; and the sole change with blast radius is the system-column skip, which alters
reads only for rows carrying a `_`-prefixed column this library has never written.
