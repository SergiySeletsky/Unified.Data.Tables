# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
