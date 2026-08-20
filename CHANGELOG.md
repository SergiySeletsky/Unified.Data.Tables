# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.8.1] — 2026-08-18

Three write/read asymmetries in `TableEntitySerializer`, all of the same shape: the write side
transformed a value and the read side had no inverse, so the round trip was lossy while every
single-direction test passed. Found by round-tripping a real application's object shapes rather than
the serializer's own fixtures.

### Fixed

- **A `byte` property was write-only.** A scalar `byte` is stored as a one-element `byte[]`
  (Edm.Binary has no narrower form), but the read path had no matching case, so it fell through to
  `Convert.ChangeType(byte[], typeof(byte))` and threw `Object must implement IConvertible`. Any type
  with a `byte` property could be written and then never read back — including by
  `IPolymorphicStorage<TBase>`, where one such property poisons the whole row. `byte?` is covered by
  the same inverse; a genuine `byte[]` is unaffected.
- **An unset date changed value on every save.** Azure Tables cannot store a date below
  `1601-01-01`, so `default(DateTime)` and `default(DateTimeOffset)` are written as that sentinel.
  Nothing mapped it back, so an unset date read as `1601-01-01` rather than `MinValue` — and after
  one round trip, "was this ever set?" became unanswerable. The inverse now runs on read. The cost is
  that `1601-01-01` cannot be stored as a genuine value, but the write side had already made that
  true: it cannot distinguish the sentinel it writes from a real one.
- **An immutable type with a private, foreign-annotated constructor could not be read at all.** The
  cell format this serializer preserves came from Newtonsoft-based Azure table serializers, whose
  idiomatic shape for a getters-only value object is a *private* constructor marked
  `[Newtonsoft.Json.JsonConstructor]`. System.Text.Json selects a constructor by its own attribute, a
  public parameterless one, or a single public parameterized one — none of which such a type has —
  and threw `NotSupportedException`. Rows holding those objects were therefore readable only by the
  serializer that wrote them, which made the format-compatibility guarantee false for exactly the
  types most likely to depend on it. The constructor is now matched by attribute NAME, so no
  dependency on Newtonsoft is taken and any equivalent annotation works. Types System.Text.Json can
  already construct are untouched, and the written JSON is unchanged.
- **A property name containing `_` silently lost its value.** `_` is the property-path delimiter, so
  a property named `Foo_Bar` writes the column `Foo_Bar`, which reads back as the path
  `["Foo", "Bar"]` — a nested property that does not exist — and the cell is dropped. No encoding
  disambiguates this after the fact, so the write now throws `SerializationException` naming the
  property instead of the read losing data quietly. **This is a behaviour change**: such a type
  previously appeared to save and came back with the property unset.

## [0.8.0] — 2026-08-17

Polymorphic storage: many concrete types in one table, discriminated by `_TypeName`, read back as a
common base with the true derived instance intact. A polymorphic *mode* on `IStorage<T>` was
evaluated and rejected in 0.6.0, and that conclusion stands — an abstract or interface base fails
`new()`, and a message or event base typically does not implement `IEntity` either, so the constraint
fails twice over. This ships instead as a **separate contract**, leaving `IStorage<T>` and every
existing behaviour untouched.

### Added

- **`IPolymorphicStorage<TBase>`** (`where TBase : class` — no `IEntity`, no `new()`) with
  `PolymorphicTableStorage<TBase>` and `InMemoryPolymorphicStorage<TBase>`. Insert (strict — a
  duplicate key throws `DuplicateKeyException`), upsert, heterogeneous transactional batch, blind
  sentinel-column merge, key/partition/table reads, streaming reads, delete and count. The store
  **owns its table**: there is no server-side type filter, so every enumerating operation sees every
  row in scope. `InsertBatchAsync` is strict the same way: a key that already exists (Azure 409) and
  a key repeated *within one batch* (Azure 400 `InvalidDuplicateRow`) both throw
  `DuplicateKeyException`, on both implementations. Merging a row that does not exist raises
  `Azure.RequestFailedException` with `Status == 404` — from the fake too, which is why that one
  provider type is documented on the contract.
- **`TableKey`** — an explicit `(PartitionKey, RowKey)` pair, used **verbatim**. `IStorage<T>` derives
  keys from `Entity.Id`; a polymorphic table orders rows by things the object does not carry — an
  aggregate version, an inverted tick count, an ambient transaction id, a literal marker key — so the
  caller computes the pair and every scheme is expressible without a hook. `IdNormalization` is
  deliberately **not** applied: it lower-cases, and a case-sensitive key would be rewritten into a
  different row. `TableKey.FromId`/`ToId` bridge to the composite-id convention.
- **`ITypeDiscriminator`, `AssemblyQualifiedTypeDiscriminator`, `TypeDiscriminatorMap`** — the
  type-resolution seam, wired through `UnifiedTableStorageOptions.TypeDiscriminator`. The default
  stores an `AssemblyQualifiedName`, byte-identical to what `persistType: true` has always written, so
  an existing table is readable with no migration. Prefer a `TypeDiscriminatorMap` for anything new:
  an assembly-qualified name welds stored rows to assembly identity — a rename, strong-name change or
  namespace move orphans them — and costs a few hundred bytes on *every* row, charged against the
  transaction byte budget that caps batch size. `MapAssignableTo<TBase>(assembly)` bulk-registers a
  hierarchy and fails at registration on a token collision; `WithAssemblyQualifiedFallback()` keeps
  legacy rows readable while rewrites converge them, so the migration is in-place rather than a
  backfill.
- **A base-type gate that cannot be switched off.** Every polymorphic read verifies
  `typeof(TBase).IsAssignableFrom(resolved)` and throws otherwise — including when a custom resolver
  claims to have checked. Deserializing a type named by stored bytes is a gadget surface; this is the
  control that needs no configuration to be effective, and `TypeDiscriminatorMap` narrows it further.
- **`TableEntitySerializer.FromTableEntity<TBase>(entity, discriminator)` and
  `TryFromTableEntity<TBase>`.** `Try` returns `false` for a row with **no** discriminator — a
  deliberate typeless marker row, which previously threw and had to be skipped by RowKey before
  deserializing. A discriminator that is *present* but unresolvable or incompatible still throws:
  absent and broken are different failures and must not look alike. Neither overload rewrites the
  object's `Id` from the row keys, because a polymorphic key is unrelated to any property.
- **Raw system columns.** A write may carry `_`-prefixed cells alongside the serialized object, read
  back strictly (`Column<T>`, throws when absent) or tolerantly (`TryColumn<T>`), and patched with
  `MergeColumnsAsync` — a blind, unconditional server-side merge with no prior read and no
  `_TypeName` in the payload. This is the "mark as published / committed" primitive.
- **`AddUnifiedPolymorphicTable<TBase>(tableName, configure?)`** and its in-memory mirror
  `AddUnifiedInMemoryPolymorphicTable<TBase>(tableName, configure?)`, registering each store **keyed
  by table name**. Keyed rather than open-generic because one base type routinely addresses several
  tables, and an open-generic registration can only bind one. Resolve with
  `[FromKeyedServices("StateEventStore")] IPolymorphicStorage<IEvent>` — or, for a table name only
  known at runtime, `IServiceProvider.GetRequiredKeyedService<IPolymorphicStorage<IEvent>>(name)`,
  since an attribute argument must be a compile-time constant.

  The optional `configure` matters most in tests. Without it a store resolves the registered
  `UnifiedTableStorageOptions`, and a test host that registers *only*
  `AddUnifiedInMemoryPolymorphicTable` has none — so it would silently fall back to
  `AssemblyQualifiedTypeDiscriminator` while production used a `TypeDiscriminatorMap`, producing
  different tokens in the one place no assertion looks. Options passed this way apply to that table
  only and are never registered, so one table's type map cannot become the process-wide default.
  Note that stores which *do* share the registered options share **one `ITypeDiscriminator`, and so
  one global token namespace**: `MapAssignableTo<IEvent>(asm).MapAssignableTo<ICommand>(asm)` throws
  at startup on any simple name the two hierarchies share.

### Fixed

- **A system column is no longer written into a property that happens to share its name.** Column
  parsing strips the leading `_` (`_TypeName` splits to path `["TypeName"]`), so a type declaring a
  `TypeName` property silently received an assembly-qualified name as its value; the same held for
  any `_`-prefixed sentinel. A leading `_` now marks a **system column** on the read path: it is
  never fed to a property setter.

  **The rule is enforced on read only, and the write path is deliberately unchanged.** `Flatten`
  names columns after `PropertyInfo.Name` verbatim, so a public settable property literally called
  `_Foo` has always written — and still writes — a column called `_Foo`. What changes is the return
  trip. Concretely: a type declaring **both** `_Legacy` and `Legacy` round-tripped `_Legacy`'s cell
  into the `Legacy` property in ≤0.7.0; 0.8.0 skips that cell and `Legacy` keeps its default. A type
  declaring `_Foo` with no matching `Foo` loses nothing, because that cell was already landing in a
  property that does not exist. A **hand-authored** `_X` column that relied on being read into
  property `X` no longer will. Keep `_` out of your property names; `SystemColumnNames` documents why
  tightening the write path to match would be a larger break than the bug it tidies up after.

### Changed

- `TableStorage<T>`'s row-size estimation and its coalesced lazy table creation moved to internal
  helpers (`TableRowSize`, `TableInitializer`) shared with the polymorphic store, so both measure and
  initialise identically instead of drifting. No public surface, no semantics, no test changes.

### Changed — `TableEntitySerializer` row shape and diagnostics

Making a root object reconstructible by the polymorphic read path changed how the serializer treats a
**true root** (the object handed to `ToTableEntity`, not a nested property). Three behaviours differ
from 0.7.0 for the same input:

- **A root with no usable public parameterless constructor now flattens to real columns** instead of
  becoming one `__Json` cell. That covers a positional record, a constructor-injected type, and a type
  whose constructor carries `[JsonConstructor]`. **0.8.0 therefore writes a different row shape than
  0.7.0 for the same object** — the columns are the object's properties, not a single blob. Rows
  already written in the old shape are not migrated; see the third bullet for what reading one does.
- **A root that is itself a collection now throws** `InvalidOperationException("Cannot flatten object
  of type …")` where 0.7.0 wrote a `__Json` cell. A collection has no properties to flatten into, and
  the alternative was a raw `TargetParameterCountException` leaking out of `List<T>`'s indexer.
- **A column that is *exactly* a bare format suffix** — `__Json`, `__GZip` or `__Truncated`, with no
  property name in front of it — **now throws `SerializationException` on read** instead of being
  silently skipped. That column shape can only exist on a row an earlier version wrote under the
  first two bullets, and the skip (an accident of the bare suffix starting with `_`) handed back an
  empty, data-free object. Re-write such rows with the current serializer.

**Who this reaches.** All three are on the standalone public `TableEntitySerializer` API —
`ToTableEntity` / `FromTableEntity` called directly on an arbitrary object. `IStorage<T>` cannot
reach the first or the third at all: `where T : class, IEntity, new()` excludes a root with no
parameterless constructor, and a bare-suffix column can only come from a row written from one. The
single theoretical exception is an entity type that itself derives from a collection
(`class Basket : List<Item>, IEntity`) — legal under the constraint, and now a hard failure. It never
round-tripped either: 0.7.0 stored it as one `__Json` cell that read back as an empty object, so the
change converts silent data loss into a diagnosis.

### Known limitations

Deliberately out of scope, to keep this landable. **No caching** on the polymorphic store —
`TableStorage<T>` keys its cache on `typeof(T).FullName`, so two stores over one table would never
invalidate each other, and its snapshot round-trips through the base-typed read, silently downcasting
a derived instance and dropping its data. Rather than fix three coupled hazards, this store has none,
which also suits an append-only fact table. **No LINQ predicates and no `_TypeName` filtering** — the
filter translator only admits persisted columns, and a type filter would have to join
`PageCursor.Fingerprint` or resumed pages would silently change shape. **No `QueryPageAsync` cursors**
— `QueryStreamAsync` follows continuation tokens internally, which is what a scan actually needs.
**No `[ProtectedProperty]`, `UpdateBuilder` or `ConcurrencyMode`** — rows are immutable facts plus
mutable system columns. And note that batches are planned on payload bytes as well as entity count,
so a set that a count-only chunker sends as one transaction may split into two; batches remain atomic
per chunk only.

**One unreadable row fails the whole read.** There is no skip-and-report policy. If any row in scope
carries a discriminator that cannot be resolved, or one naming a type not assignable to `TBase`,
materialization throws: `QueryAsync` gives the caller nothing at all — including the rows that were
fine — and `QueryStreamAsync` throws mid-enumeration, after having yielded everything ahead of the
bad row. (`CountAsync` and `DeletePartitionAsync` project keys only and never materialize, so they
are unaffected.) That is the deliberate choice for now — absent and broken must not look alike, and
silently dropping rows from a fact table is worse than failing — but one bad row can take a partition
offline until it is fixed or deleted. A per-row skip-and-report callback is under consideration.

**Write responses report no `Timestamp`.** `InsertAsync`/`UpsertAsync` return an entry whose `ETag`
is the version the service reported and whose `Timestamp` is null — a write response carries no
service last-write time. Read the row back if you need it. The in-memory fake matches.

### Note on `RowKeys.InvertedTicks`

`InvertedTicks` formats `"D19"`. A common pre-existing convention formats the same arithmetic as
`"D20"`; since `DateTime.MaxValue.Ticks` is 19 digits, `D20` zero-pads to 20 characters and `D19` does
not. Each sorts correctly in isolation, but mixed in one partition the 20-character keys sort before
every 19-character key and interleave wrongly. Keep your own helper for such a table — the explicit
`TableKey` design imposes no key generation. A width-parameterised overload is under consideration.

## [0.7.0] — 2026-08-16

### Added

- **`Unified.Data.Tables.Identity`** — ASP.NET Core Identity stores persisted through `IStorage<T>`.
  Seven `Entity`-derived row models (one table each, named after the type), deterministic key
  composition, and `IUserStore`/`IRoleStore` implementations covering passwords, external logins,
  claims, roles, tokens, two-factor and lockout. It depends on **`Unified.Data.Tables.Abstractions`
  only**, so it runs against Azure Table Storage, the in-memory provider, or any other
  `IStorage<T>` — an entire Identity stack becomes unit-testable with no emulator. Register with
  `AddIdentityCore<IdentityUser>().AddRoles<IdentityRole>().AddUnifiedIdentityStores()`; the package
  deliberately does not register a storage provider, leaving that choice to the consumer.

  Two behaviours worth knowing before reading the source. **Login rows are written with
  `CreateAsync` and fail loud on a duplicate while the four other association tables upsert** — not
  an inconsistency: a login's key is `{provider}|{md5(providerKey)}` and the owning `UserId` lives in
  the row *value*, so an upsert would silently reassign ownership of an external identity to whoever
  wrote last. And **user rows should have caching disabled** on Azure
  (`o.CacheFor<IdentityUserModel>(CachePolicy.Disabled)`): they carry `SecurityStamp`, `PasswordHash`
  and `LockoutEnd`, and the default sliding cache can serve a revoked stamp indefinitely on a
  multi-instance host.

  Custom user and role types are not supported in this version — `AddUnifiedIdentityStores()` throws
  at startup for anything but `IdentityUser`/`IdentityRole`. Supporting them requires the consumer to
  supply their own row model, since table names derive from `typeof(T).Name`; that is an additive
  change for a later version.

  One dependency note for existing consumers: the package pins `Microsoft.Extensions.Identity.Stores`
  10.0.11, which transitively raises the floor on `Microsoft.Extensions.DependencyInjection` to
  **10.0.11** for anyone who references it. A restore pinned below that floor fails with NU1605
  rather than silently downgrading.

## [0.6.0] — 2026-07-14

The pre-announced breaking batch, plus the versioned-stream shape. Every change either tightens an
existing opinion or composes over `IStorage<T>` — no new abstractions. Scope was set by an
adversarial multi-agent evaluation of the full consumer wishlist; the rejected items (public
`TableGateway`, pluggable key strategy, polymorphic read surface, `FullName` table-name flip) are
documented in the evaluation notes.

### Changed — BREAKING

- **`UpdateAsync(entity)` in `Auto` mode with no ETag now throws `InvalidOperationException`**
  instead of silently writing last-writer-wins (announced in the 0.5.2 README). There is no version
  to check without an ETag: round-trip it (`OneAsync`/`QueryAsync` populate it), use `MutateAsync`
  for read-modify-write, or spell the intent with `ConcurrencyMode.LastWriterWins`. The cached-ETag
  fallback (and its 412 refetch-and-retry path — itself a lost-update vector) is **deleted**, making
  `Auto` deterministic: the outcome no longer depends on cache state, and the in-memory fake (which
  never had an ETag cache) now matches the real store byte-for-byte, including the exception
  message. Migration cushion: `UnifiedTableStorageOptions.ImplicitLastWriterWins = true` restores
  the pre-0.6.0 unconditional fallback (with a warning log) while call sites are converted.
- **A LINQ predicate on `Id` now translates equality to the key pair** —
  `x => x.Id == "p|r"` becomes `(PartitionKey eq 'p' and RowKey eq 'r')` (announced in the 0.5.3
  changelog). Keys are the authoritative identity (B9), so legacy rows *without* an `Id` column are
  now matched, and rows are matched by the row a value addresses rather than its spelling
  (`"a"` == `"a|a"`); the in-memory fake canonicalizes identically. Every operator other than `==`
  on `Id` is rejected with `NotSupportedException` (the `Id` column is not guaranteed to exist).

### Changed

- **The storage constraint is loosened from `where T : Entity, new()` to
  `where T : class, IEntity, new()`** across `IStorage<T>`, both implementations, and all extension
  packs. Models whose single base-class slot is taken (interface-first domain types) can now
  implement `IEntity` directly instead of deriving `Entity`. Non-breaking for existing code —
  `Entity` implements `IEntity` and remains the recommended default.

### Added

- **Versioned streams** — append-only, per-stream versioned snapshots ("state as of version N") for
  event-sourced read models: `VersionedEntity` / `IVersionedEntity` (adds `int Version`),
  `RowKeys.VersionKey(version)` (inverted zero-padded key; a stable wire format byte-compatible
  with the common hand-rolled `int.MaxValue - version` scheme), and `VersionedStreamExtensions` —
  `AppendVersionAsync` (immutable; duplicate version throws `DuplicateKeyException`),
  `AtVersionAsync`, `LatestAsync`, `AtOrBeforeAsync` (server-side `Version <= v`, one bounded
  read), `HistoryAsync` (newest-first), plus throwing `Get*` variants. Like the append-log helpers,
  these are thin compositions over `IStorage<T>` — no new interface, no separate backend — so
  caching, outcome verbs, and the in-memory fake work unchanged. Stream ids are validated (no `|`,
  trimmed). Adoption note for pre-existing inverted-key tables: the key-addressed reads work over
  legacy rows as-is; `AtOrBeforeAsync` filters on the `Version` column (present on every row the
  pack writes) — backfill it on foreign rows before relying on "state as of" there.

## [0.5.3] — 2026-07-13

Migration-safety patch driven by a consumer review (IntelliGrowth / Intellias.CQRS migration).
No breaking API changes; one new marker column by default (see below).

### Fixed

- **`Entity.Id` is now derived from the row's `PartitionKey`/`RowKey` on every read** instead of
  trusting a stored `"Id"` column. A legacy row written by another serializer (no `Id` column) read
  back with `Id = ""`, and a legacy single-segment id split as the wrong keys on the next write —
  writes could land on a different row than the one read. Keys are the authoritative identity; a
  stored id that already addresses the row's exact keys is kept verbatim (so explicit forms like
  `"a|a"` round-trip byte-identically), and serializer-only round-trips without keys keep the
  stored column. *Known limitations:* a legacy `PartitionKey` containing `'|'` cannot be expressed
  in the single-separator id grammar, and server-side predicates on `Id` still target the stored
  `Id` **column** — legacy rows without one are invisible to `QueryAsync(x => x.Id == …)` until
  backfilled (key-based predicate translation is tracked for 0.6.0). (B9)
- **Types without a public parameterless constructor can be read late-bound again.** Legacy
  FormatterServices-based serializers persisted such events/commands; `FromTableEntity()` now falls
  back to `RuntimeHelpers.GetUninitializedObject` when no public parameterless ctor exists, instead
  of throwing `MissingMethodException`. Types with a ctor keep ctor semantics (initializers run).

### Added

- **`OversizedCellPolicy`** (`UnifiedTableStorageOptions.OversizedCells` /
  `TableEntitySerializer.OversizedCellPolicy`) — what happens when a payload exceeds the 64 KB cell
  cap even compressed. The new default, `TrimWithMarker`, records the loss in a sibling
  `{Column}__Truncated` cell (e.g. `"kept 125 of 2000 items"`) — previously the serializer kept the
  largest fitting list prefix (or dropped the property) with **no trace**. `Throw` fails the write
  loudly for data where loss is never acceptable; `TrimSilently` restores the pre-0.5.3 behaviour.
  The marker column is ignored on read.
- **`IdNormalization.AsWritten`** (`UnifiedTableStorageOptions.IdNormalization`) — opt out of id
  normalization (trim → spaces to `-` → lower-case) for tables whose keys are case-sensitive
  payloads (Base64, hex, mixed-case natural keys) or pre-existing data written by another layer.
  Applied uniformly to ids, partition scopes, and `RowKeyPrefix` in both `TableStorage` and the
  in-memory fake (parity preserved). Default remains `Normalized`.
- **`AddUnifiedInMemoryStorage(configure)`** — the fake's DI registration now accepts the same
  options delegate as `AddUnifiedTableStorage`, so a DI-wired fake honours the configured
  `IdNormalization`/`OversizedCells` instead of silently running defaults. The static serializer
  policy follows first-registration-wins semantics (mirroring `TryAddSingleton`), so a later bare
  registration can never reset an explicitly configured policy.

### Changed

- **`ConcurrencyMode.Auto` now logs a warning when it degrades to an unconditional write.** With no
  caller ETag and no cached ETag (cold start, cache eviction, scale-out), Auto silently wrote
  last-writer-wins; the fall-through is now visible in logs, steering intentional LWW to the
  explicit `ConcurrencyMode.LastWriterWins`.
- **Query-cache entries are sized by row count** (entity entries remain size 1), so a
  `SizeLimit`-bounded `IMemoryCache` actually accounts for large or whole-table cached results.

## [0.5.2] — 2026-07-13

A correctness patch — no API changes. Fixes write/query bugs and hardens the cache.

### Fixed

- **`DateTime` properties whose `Kind` is `Local` (or `Unspecified`) no longer crash writes on a
  non-UTC host.** The serializer built `new DateTimeOffset(value, TimeSpan.Zero)`, which throws
  `ArgumentException` whenever the value's `Kind` is `Local` and the machine's offset is non-zero —
  so a perfectly ordinary `DateTime.Now`-derived value failed to persist on most developer and
  server machines. Values are now normalized to their UTC instant first (`Local` → `ToUniversalTime`,
  `Unspecified` → assumed UTC), matching the existing read path. The stored instant is unchanged on
  UTC hosts. (B1)
- **A LINQ filter that reaches into a JSON-serialized nested value now throws
  `NotSupportedException` instead of silently matching nothing.** Types stored as a single JSON cell
  (positional records, collections, ctor-only types) have no flattened `Owner_Member` columns, so a
  predicate like `x => x.Location.Lat > 5` had no column to target and translated to a filter that
  could never match. It is now rejected up front, consistent with how every other
  un-pushdownable predicate is handled. Flattened nested owners (`x.Address.City`) — including ones
  declared through an interface or abstract base but holding a flattenable concrete value — are
  unaffected. (B2)
- **Partition scope and `RowKeyPrefix` now normalize to the stored form** the same way writes
  normalize ids. `QueryAsync(partition)`, `QueryAsync(QueryOptions)`, `QueryStreamAsync`,
  `QueryPageAsync`, `CountAsync`, and `DeletePartitionAsync` passed the caller's raw partition (and
  raw `RowKeyPrefix`) straight into the filter, so a natural-form value (`"My Vision"`, `"Task A"`)
  never matched a stored, normalized key (`my-vision`, `task-a`) — a silent empty result or no-op
  delete. The in-memory fake normalizes identically, preserving fake/Azure parity. (G3)

### Changed

- **Every read and write now hands back an entity isolated from the cache.** The per-entity cache
  stored the very instance it also returned — from `OneAsync`/`QueryAsync`, from the predicate,
  streaming and paged read paths (`QueryPageAsync` / predicate `QueryStreamAsync`), and from the
  write methods (`CreateAsync`/`UpdateAsync`/`UpsertAsync`) — so a caller mutating a result (or a
  create-then-mutate) corrupted the cache for every subsequent reader. The cache now holds a private
  snapshot at the single point every entity enters it, so no returned or written instance is ever
  aliased to a cached one. (G1)
- **Cache entries are keyed by the entity type's full (namespace-qualified) name.** Keying by the
  simple type name let two same-named types in different namespaces collide in a shared
  `IMemoryCache`. (Note: table names are still derived from the simple name — a separate change
  deferred to 0.6.0.) (G5)
- **Query-cache invalidation on write is now scoped to the written partition plus the table-wide
  entry**, instead of walking an unbounded, process-wide set of every partition ever queried. A write
  to one partition no longer evicts unrelated partitions' cached queries, and the per-instance
  tracking set (a latent memory leak) is gone. A whole-table `QueryAsync` (null or whitespace
  partition) now consistently caches under, and is evicted through, the table-wide key. (G4)
- **Cache entries now declare a `Size`**, so the library works with a `MemoryCache` configured with a
  `SizeLimit` (previously every `Set` threw). (G6)

## [0.5.1] — 2026-07-12

### Changed

- **Enum values inside JSON/GZip fallback cells are now written using the declared member name
  (PascalCase)** instead of camelCase, matching the default of both System.Text.Json and
  Newtonsoft.Json. This keeps stored enum tokens byte-stable and compatible with data written by
  name-as-declared serializers (e.g. a Newtonsoft `StringEnumConverter`), which matters for tables
  migrated onto this library. Reads remain case-insensitive, so lowercase/camelCase tokens written
  by `<= 0.5.0` still round-trip. Top-level (flattened) enum columns are unaffected — they were
  already written via `ToString()`. Only the `JsonStringEnumConverter` naming policy changed
  (`TableEntityValue.JsonOptions`); property names remain camelCase.

## [0.5.0] — 2026-07-12

### Added

- **Server-side LINQ filters** — `QueryAsync(Expression<Func<T, bool>>)`,
  `QueryStreamAsync(Expression<Func<T, bool>>)`, and `AnyAsync(Expression<Func<T, bool>>)` on
  `IStorage<T>`. A predicate is translated to an Azure Tables OData `$filter` by
  `TableFilterTranslator` (mapping to the stored form: enum→name, `decimal`→`double`, nested
  `x.A.B`→`A_B`), so the filter runs in the service rather than as a client-side scan. Anything that
  cannot be pushed down faithfully throws `NotSupportedException`; the in-memory fake validates every
  predicate through the same translator, so a green fake test also holds against Azure.
- **Resumable paging** — `QueryPageAsync(QueryOptions)` returns `EntityPage<T>` with an opaque,
  query-bound continuation cursor; `QueryOptions.ContinuationToken` resumes it. The canonical grid /
  infinite-scroll primitive, with no load-the-whole-partition-then-slice and no second scan for a count.
- **Append-log helpers** — `RowKeys.AppendKey` / `SubStreamPrefix` / `TryParseAppendKey`, and the
  `AppendAsync` / `RecentAsync` extensions, for the "append an event, read the newest N in order" shape
  (inverted-ticks RowKeys, optional per-sub-stream isolation).

### Changed

- **BREAKING:** `IStorage<T>` gains four members — `QueryPageAsync`, `QueryAsync(Expression)`,
  `QueryStreamAsync(Expression)`, and `AnyAsync(Expression)`. Calling code is unaffected (the additions
  are purely additive and unambiguous), but any external hand-rolled `IStorage<T>` implementation (e.g. a
  custom test double) must add the four members. `netstandard2.0` rules out default interface methods, so
  the members are declared directly on the interface.
