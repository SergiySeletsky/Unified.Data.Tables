# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
