# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.9.0] — 2026-11-10

Lossless storage of large values, opt-in per property.

### Upgrade notes

- **Roll forward only.** Rows written split or offloaded cannot be read correctly by 0.8.x or older: a
  0.8.3 reader returns the `{RowKey}~NNNN` continuation rows as entities, parses `Stages__c0` as the path
  `[Stages, c0]` so a split collection reads as empty, sees an offloaded property as null, and a later
  replace or upsert from that reader drops the value. Deploy the package bump first with no attributes
  (types that opt into nothing write exactly what 0.8.3 wrote), and add `[TableValueSplit]` or
  `TableBlobContent` in a later deploy, once no older reader (a swapped slot, a rollback target, another
  service) remains. Values that each fit one cell are not exempt: an entity whose opted-in values together
  exceed one entity is written with split cells and continuation rows too.
- Back up and restore an entity's main row, continuation rows and offload blobs together. A table copied
  raw shares the source's blobs (the references hold the source table's blob paths). Writes through the
  copy never delete them, but the source's own replaces, deletes, partition deletes and sweeps can: give
  the copy its own copy of them before the source changes, or enable blob versioning or soft delete on
  the container.
- The offload container is exclusive to the library. Orphan blobs are removed only by
  `SweepOrphanBlobsAsync`: schedule it, or register `AddUnifiedOrphanBlobSweep`.
- Reads of split-aware types (those with a `[TableValueSplit]` or `TableBlobContent` member) cost one
  extra range query per entity with continuation rows, plus a blob download per eager offloaded value,
  queries included.
- Once a type opts in, its `UpsertAsync`, `UpdateAsync(entity)`, `DeleteAsync` and `UpsertBatchAsync` read
  the row before they write, and `UpsertAsync`, a `LastWriterWins` `UpdateAsync`, `DeleteAsync` and
  `UpsertBatchAsync` can throw `ConcurrencyConflictException` under contention (see Changed). Code that
  assumed an upsert or a delete never conflicts needs a catch or a retry.

### Added

- **`[TableValueSplit(SplitEncoding)]`** (property or class, `Offload` = `BlobOffload.AboveThreshold`
  or `Never`) stores strings, `byte[]` and JSON-stored properties beyond the 64 KB cell and 1 MB row
  limits without trimming: split cells, continuation rows (`{RowKey}~0001..`) in the same entity-group
  transaction, or an Azure Blob, with a SHA-256 over the stored bytes verified on read.
  `SplitEncoding.Text` and `Gzip`; strings that are not well-formed UTF-16 are stored as `utf16le`.
  - On a class the attribute covers every `string`, `byte[]` and JSON-stored property the class declares
    or inherits, except the entity `Id`, which stays a key column that can be filtered. A derived class
    inherits the attribute of its base class; an attribute on a property wins over the one on its class;
    a property that overrides or hides (`new`) another keeps that one's attribute.
  - A member of a runtime subtype is supported: declare the property as an interface, an abstract class
    or a base class and initialise it with an instance of the type that has the member
    (`public IShape Shape { get; set; } = new Circle();`). A read keeps that instance and maps the
    columns through its type. A write of a value that needs split cells or a blob below an object whose
    type a read would not recreate throws `NotSupportedException` naming the column, and stores nothing.
  - When many opted-in values that each fit one cell exceed one entity together, the largest are stored
    in split form so that their chunks can go to continuation rows; a row that fits one entity is stored
    as before, every value in its own cell.
  - `[ColumnAlias]` on a renamed `[TableValueSplit]` or `TableBlobContent` property reads the value
    stored under the old column in every form: one cell, `__Json`, `__GZip`, split cells, continuation
    rows or a blob reference. The next save stores it under the new column and removes the old blob
    after the row has committed.
- **`BlobOffload`**, **`BlobOffloadOptions`** (`ContainerName`, `ThresholdBytes`, `CleanupGrace`,
  `ServiceClient`, `UseRegisteredServiceClient`) and `UnifiedTableStorageOptions.BlobOffload`.
  `CleanupGrace = TimeSpan.MaxValue` switches the cleanup after writes, deletes and partition deletes
  off and leaves every released blob to the sweep.
- **`TableBlobContent`** for files: always offloaded, lazy `OpenReadAsync`, `FromBytes`/`FromStream`
  (never buffers in memory; leaves the caller's stream open; a non-seekable stream is copied to a
  delete-on-close temp file under `Path.GetTempPath()`, a seekable one is read twice and must not change
  until the write completes), `Length`/`Sha256` known after the write.
  - The stream verifies length and SHA-256 by the time it has delivered the content: the read that would
    return the last byte throws `TableValueIntegrityException` instead when the content does not match,
    so reading exactly `Length` bytes is verified too. It is not disturbed by the entity being saved
    again while it is read, and a blob deleted under it fails the read with
    `TableValueIntegrityException`, not with the blob service's own error.
  - A write that fails can be retried with the same content when it came from a seekable stream or from
    bytes. A content from a non-seekable stream whose write failed after the stream was read and before
    its blobs were uploaded must be created again: every later write of it throws
    `InvalidOperationException` before anything is read or uploaded.
  - One content can be assigned to several properties or entities, and a content read from one row can
    be written into another: each row gets its own blob, and the written property is given its own
    `TableBlobContent`, so the entity it came from is not affected.
- **`IOrphanBlobSweeper.SweepOrphanBlobsAsync(partitionKey, olderThan)`** and the table-wide
  **`SweepOrphanBlobsAsync(olderThan)`** delete unreferenced blobs, implemented by `TableStorage<T>` and
  `InMemoryStorage<T>`, with matching extension methods on `IStorage<T>` (they throw
  `NotSupportedException` for a store without the capability). Best-effort cleanup after a replace or
  delete only removes the row's own blobs (names under `{table}/{partitionKey}/{rowKey}/`), and only
  those untouched for `CleanupGrace`, so schedule the sweep; `olderThan` must exceed the longest write
  duration. The table-wide sweep lists partitions with the delimiter `/`, isolates a failing partition
  and throws the failures as one `AggregateException` at the end. Sweeps and partition deletes only
  delete names of the exact offload shape that no row of the partition references; the container is for
  offload alone.
- **`AddUnifiedOrphanBlobSweep(o => ...)`** and **`AddUnifiedOrphanBlobSweep<T>()`**: a hosted service
  (`IHostedService`) that runs the table-wide sweep of each registered type every
  `OrphanBlobSweepOptions.Interval` (default 1 hour) with `OlderThan` (default 1 hour), first after
  `InitialDelay` (default 1 minute). A failing type is logged at `Error` and retried next round without
  stopping the others; shutdown cancels the round; without `BlobOffload` it does nothing. Out-of-range
  options throw `ArgumentOutOfRangeException` at registration.
- **`TableValueIntegrityException`** and **`OversizedValueException`** (with `ColumnName`, `EncodedBytes`,
  `Reason` and `OffloadForbiddenByProperty`, which is true when offload is configured but the property
  sets `BlobOffload.Never`). `OversizedValueException` reports every entity that cannot be stored for
  its size: a value that needs blob offload and cannot have it, cells that cannot leave the main row and
  exceed one entity (252 properties, 1,000,000 estimated bytes), or a row that does not fit one
  transaction. It names a column of the entity, and its message ends with what to do. A second
  constructor, `OversizedValueException(columnName, encodedBytes, reason, remedy)`, takes that advice.
- **`BlobOffloadNotConfiguredException`** (an `InvalidOperationException` with `TableName` and
  `ColumnName`): a row holds a `{Prop}__blob` reference and the store has no `BlobOffload` configured.
- **`TableRequestSize`** for callers that build their own transactions: `EstimateEntityBytes`,
  `EstimateEntityWireBytes` and `EstimateActionWireBytes` (the planners' stored and escaped-wire estimates,
  for a `TableEntity` or any `ITableEntity` model), `FitsOneTransaction`, and the limits as constants
  (`MaxTransactionActions`, `MaxTransactionPayloadBytes`, `MaxTransactionWireBytes`,
  `MaxTransactionEntityBytes`, `MaxEntityBytes`, `MaxEntityProperties`).
- A six-parameter `TableStorage<T>` constructor taking the `BlobServiceClient`. In DI the offload client is
  chosen only when `BlobOffload` is set: `BlobOffloadOptions.ServiceClient`, else the host's registered
  `BlobServiceClient` when `UseRegisteredServiceClient` is true, else the one the connection-string or
  credential overload derives. The library keeps that client under its own key and never registers or
  takes the host's unkeyed `BlobServiceClient`. A store constructed directly uses
  `BlobOffloadOptions.ServiceClient` only when no client is passed to the constructor. When
  `AddUnifiedTableStorage` is called more than once, the first call's options are the registered ones; a
  later call supplies the offload client, by the same order, when the registered options set
  `BlobOffload` and no call has supplied one yet (a module registers the options, the host registers the
  connection string). A client an earlier call supplied, or its choice of the host's client, is never
  replaced, and a first call without `BlobOffload` still wins over a later one that sets it.
- Storage-provider SPI in `Unified.Data.Tables.Providers` (`LargeValueRows`, `LargeValueTable`,
  `LargeValueWritePlan`, `IOffloadBlobStore`, `BlobState`, `BlobReference`), hidden from IntelliSense, for
  provider implementations such as the in-memory package. The in-memory store supports all of the above,
  with the same planning, retries, cleanup rules and exceptions.

### Changed

- `IStorage<T>` has no new member: third-party implementers, on `netstandard2.0` too, compile unchanged.
- New dependency **Azure.Storage.Blobs 12.29.2** (a floor: hosts that pin 12.29.2, 12.30.x or later need no
  change and get no NU1605 downgrade). It lifts `Azure.Core` from 1.44.1 to 1.55.0 transitively and pulls in
  `Azure.Storage.Common` 12.28.0; it does not pull `Apache.Arrow` (12.30.x does).
- New dependency **Microsoft.Extensions.Hosting.Abstractions 10.0.9** (abstractions only, for the hosted
  sweep's `BackgroundService`; any ASP.NET Core or generic host already has it).
- The connection-string overload derives an offload client only when the string has a blob endpoint
  (`BlobEndpoint`, `UseDevelopmentStorage=true`, or an `AccountName` with no `TableEndpoint` or an Azure
  Storage one) and throws `ArgumentException` at registration when that endpoint is malformed and the
  call is the one that has to supply the client (a call that is not asked for a client does not look at
  the blob endpoint). For a custom or emulator `TableEndpoint` with a key and no `BlobEndpoint`, or a
  Cosmos Table API host, set `BlobOffloadOptions.ServiceClient`.
- `Unified.Data.Tables` depends on exactly the same version of `Unified.Data.Tables.Abstractions`
  (`[0.9.0]`), and `Unified.Data.Tables.InMemory` on exactly the same version of `Unified.Data.Tables`: they
  share internals and the provider SPI, which may change in any minor version. Upgrade them together.
- For a split-aware type, `UpsertAsync`, `UpdateAsync(entity)`, `DeleteAsync` and `UpsertBatchAsync` are
  not single round trips: each reads the row first (one point read, to find the continuation rows and
  blobs it replaces) and writes on condition of the version it read. `UpsertAsync`, a `LastWriterWins`
  `UpdateAsync`, `DeleteAsync` and `UpsertBatchAsync` still mean "last writer wins", but a call that
  loses its condition to a concurrent writer reads again and retries after a short random pause (5 to
  10 ms before the first retry, doubling each time, at most 150 ms in all, honouring the cancellation
  token): five attempts in all, then `ConcurrencyConflictException`. Up to four concurrent writes of one
  row therefore all succeed; with more, a conflict is unlikely but possible. `Strict` updates, and `Auto`
  updates with an ETag, conflict at once, as for any type. `CreateAsync` and `CreateBatchAsync` read
  nothing first. The XML documentation of `UpsertAsync`, `UpdateAsync(entity, mode)`, `DeleteAsync` and
  `UpsertBatchAsync` on `IStorage<T>` says so.
- For split-aware types, writes and partial updates refuse, with `ArgumentException`, a RowKey ending
  with `~` and four digits (the continuation-row shape, e.g. `r~0001`) and a RowKey longer than 480
  characters; any other `~` is allowed, and deletes accept every key (deleting a continuation row's id
  is a no-op). They also refuse a partition or row key that is not well-formed UTF-16 (an unpaired
  surrogate escapes to the same blob name as U+FFFD), so two keys never share a blob and the table-wide
  sweep never reads the wrong partition.
- A split-aware write that needs a new continuation row at a key a foreign row already holds (one
  written before the type opted in, or by another type in the table) fails as a whole with
  `InvalidOperationException` naming that key, instead of overwriting the row: new continuation rows are
  inserted, and only the entity's own are replaced. A continuation row whose `_SplitOf` cell names the
  entity is its own even when no main row declares it. Such leftover rows (from a writer that does not
  know continuation rows, or a partition delete that raced an upsert) do not block the key: the write
  replaces the ones the new value needs and deletes the others in the same transaction, each on
  condition of the version it was read at, and `DeleteAsync` of an id that has no main row removes them.
  A write looks for leftovers only when one holds a key it needs; reads, queries and `CountAsync` never
  return them. The provider SPI gains `LargeValueWritePlan.StoredPartCount`/`IsNewPart`,
  `LargeValueTable.ValidateKeys`, `LargeValueTable.ContinuationKeyTaken` and
  `LargeValueTable.PlanOverOwnParts`.
- `QueryPageAsync` can return a page with no items and a non-null continuation token when it held only
  continuation rows; keep paging until the token is null.
- A projected `QueryPageAsync` no longer warms the entity cache for any type.
- A projected query (`QueryOptions.Select` on `QueryAsync(options)`, `QueryStreamAsync(options)` and
  `QueryPageAsync`) no longer sends `odata.etag` in `$select`, for every entity type. The row's version
  is not a column: the service returns it with every entity whatever `$select` names, so every projected
  entity still carries its `ETag`. A caller that names `odata.etag` in `QueryOptions.Select` has it
  dropped (a `Select` of that name alone is still a projection: the system columns and nothing else).
  This is the one deliberate difference in the requests a plain type makes compared with 0.8.3.
- The read cache no longer keeps a version that a write or delete has replaced, for every entity type.
  A read that overlapped a write or delete made through the same `IMemoryCache` could fetch the row,
  let the write commit, and then cache what it had fetched: later `OneAsync` calls returned the old
  version for the cache TTL (one hour, sliding, by default), `ExistsAsync` stayed true for a deleted
  entity, and a read-then-upsert caller wrote the old value back. The cached `QueryAsync(partition)` and
  `QueryAsync()` lists, and the entities that `QueryPageAsync` and the predicate queries warm, had the
  same defect, which 0.8.3 has too. A read now caches what it found only if no write or delete of that
  entity has completed since the read began, and a list only if nothing was written in its partition
  (anywhere in the table, for the whole-table list). After such an overlap the next read goes to the
  table; nothing else changes. Writes made by another process, or through another `IMemoryCache`, are
  still not seen until the entry expires, and two concurrent writes of one entity can still leave the
  earlier one's version cached.
- The entity cache holds one entry per row, however its id is spelled. `"a"` and `"a|a"` address the
  same row but had separate entries, so a write or delete under one spelling left the entry cached under
  the other, and `DeletePartitionAsync` did not evict an entity whose id has one segment: it stayed
  readable through `OneAsync`, and `ExistsAsync` stayed true, for the cache TTL (in 0.8.3 too).
- `DeletePartitionAsync` invalidates the caches also when one of its transactions fails or is cancelled:
  an entity that an earlier transaction deleted is no longer answered from the cache.
- When a store type is first used, an entity type with a property that can hold a nested object (a type
  that flattens into columns, an interface, an abstract class) is constructed once, with its public
  parameterless constructor, to find the large-value members of runtime subtypes. An exception from the
  constructor is ignored; a type without such a property is not constructed.
- Polymorphic stores (`PolymorphicTableStorage<TBase>`, `InMemoryPolymorphicStorage<TBase>`) now throw
  `InvalidOperationException` naming the column when they read a row holding an offloaded reference
  (`{Prop}__blob`), instead of returning the property as null; they cannot fetch blobs.
- Corrected the `UnifiedTableStorageOptions.OversizedCells` comment: the default is `Throw`, not trim.

### Fixed (before release)

- Blob names escape `.` as `%2E` (and write an empty key as `%`). With `Uri.EscapeDataString` alone, a
  PartitionKey or RowKey `.` or `..` became a dot segment that the Blobs SDK collapses: the blob was stored
  under another name (`..` even outside the table's prefix), the sweep then deleted it as unreferenced and
  reads threw `TableValueIntegrityException`. Both blob stores now refuse a name with a dot segment.

- Large-value transactions are planned on the size the SDK actually sends: JSON-escaped text (`\uXXXX`,
  six bytes per character, for non-ASCII and `< > & ' +` and the like) and base64 binary, within the
  4 MiB payload limit, with stale continuation-row deletes counted toward the byte budget. Split cells
  keep each row's request body within 1 MB. Escaped text that would overflow is offloaded (or throws
  `OversizedValueException`) instead of failing with HTTP 413 or `EntityTooLarge`. The plain batch
  planner is unchanged.
- Reading a row with an offloaded value through a path that cannot fetch it throws naming the column
  instead of leaving the property null, which a later write would have turned into data loss:
  `InvalidOperationException` from `FromTableEntity<T>()` or a store whose type has no `[TableValueSplit]`
  or `TableBlobContent` property, `BlobOffloadNotConfiguredException` from a store of such a type with no
  `BlobOffload` configured (it was `TableValueIntegrityException`, which means corruption and was retried
  as a torn read; uploading without offload threw a bare `InvalidOperationException`). A projection
  that omits the property still leaves it at its default. `ExistsAsync` on a plain type does not cache an
  entity from a row with large-value cells.
- `[TableValueSplit]` on a property of an interface or abstract (non-collection) type is refused with
  `NotSupportedException`: such a value may flatten into columns at runtime, where the attribute was
  silently ignored. Like every misused attribute (`InvalidOperationException` on a native scalar, on a
  type that flattens into columns and on a `TableBlobContent`) it is reported when an entity of the type
  is first written or a row of it is first read, not when the store is constructed.
- A create-only blob upload that conflicts with an orphan which a sweep deletes before the follow-up state
  read, or between that read and the touch, uploads again (three attempts in all) instead of failing the
  write with an integrity error.
- A read racing a concurrent replace or delete no longer fails with a false `TableValueIntegrityException`:
  when the continuation rows or a blob do not match, the main row is read again and, if its ETag changed,
  the read is retried from the new version (three attempts); a row deleted meanwhile reads as absent. The
  in-memory store does the same for blobs. A lazy `TableBlobContent` whose blob is gone says the row may
  have changed since it was read.
- Blob cleanup and sweep cutoffs are taken from the storage service's clock (the commit response's
  `Date`, or the blob service's time before listing) instead of the client clock, which could make a
  freshly touched blob look old when it ran ahead; the cleanup cutoff is taken from the commit. Without a
  service time the client clock is used less a further 5 minutes. The provider SPI `IOffloadBlobStore`
  gains `GetServiceTimeAsync`, and `LargeValueTable.DeleteReleasedBlobsAsync` takes the commit's service
  time.
- `CreateBatchAsync` and `UpsertBatchAsync` of a split-aware type are transactional again, with the plain
  batch contract: per partition, each entity's rows (main row, continuation rows, stale-part deletes) are
  one unit packed into entity-group transactions (at most 100 actions, within the byte and 4 MiB wire
  budgets), atomic per transaction; an entity's rows never straddle two transactions. Each transaction's
  blobs are uploaded just before it (so a blob waits for its row no longer than in a single write, and the
  sweep's `OlderThan` need only exceed one transaction) and released ones are deleted after it commits. An
  upsert transaction that loses a race (409, 412, or a stale part already gone) is re-read, planned again
  and retried after a short pause, five attempts in all. Two small rows in one partition are one
  transaction, not two separate writes. A row repeated in a create batch throws `DuplicateKeyException`
  before anything is written; in an upsert batch its last occurrence wins. The ids `"a"` and `"a|a"`
  address the same row and count as a repetition (they were sent as two rows of one transaction, which
  the service refuses with `InvalidDuplicateRow`). An existing key fails its create transaction as a
  whole; earlier transactions stay committed. The in-memory store packs and fails the same transactions.
  The provider SPI gains `LargeValueTable.PlanTransactions`, `MaxWriteAttempts` and
  `WaitBeforeRetryAsync`.
- A split-aware write or delete that has committed is no longer reported as failed when the caller's token
  is cancelled after the commit: the blob cleanup runs without the caller's token. Previously the cleanup
  was skipped too, leaving the old blobs to the sweep. The same holds for the blob cleanup after
  `DeletePartitionAsync`. Once the token is cancelled, the cleanup runs for at most 5 more seconds and
  then leaves the rest to the sweep, so a cancelled partition delete with many blobs does not hold a
  shutting-down host.
- With `BlobOffload` set and no blob client, every `IStorage<T>` failed to resolve; now only a type with
  `[TableValueSplit]` or `TableBlobContent` members does, as `UnifiedTableStorageOptions.BlobOffload`
  documents. The overloads no longer build a throwaway `BlobServiceClient` (swallowing every exception) to
  decide whether to register one.
- The escaped-wire estimate counts the `@odata.type` annotation Azure.Data.Tables 12.11.0 writes for every
  `double` (`"Edm.Double"`); a row of many doubles could be planned below its real request size.
- Fewer read-path allocations for plain types, with the same wire behaviour: recognising the large-value
  cell suffixes no longer allocates a substring for every `__Json`/`__GZip` (or other `__`) column, a
  projected query builds its read context once instead of copying the column list per row, and the cache
  clone and the public `ToTableEntity` no longer allocate an empty pending-blob list.
- `BlobOffloadOptions.ThresholdBytes` is validated: between 1 and `BlobOffloadOptions.MaxThresholdBytes`
  (4,194,304, one transaction's payload limit); anything else throws `ArgumentOutOfRangeException`.
- Types that opt into nothing behave as on 0.8.3. A type with a `System.Text.Json.Nodes.JsonNode` or
  `JsonValue` property (directly or inside a nested object), with a property whose declared type is an
  interface or abstract class that declares more than one indexer, or with a nested type that hides a
  base property with one of another type made `TableStorage<T>` and `InMemoryStorage<T>` throw
  `TypeInitializationException` on construction, on DI resolution and on every operation, and made a
  polymorphic insert of such an item throw `ArgumentException`. These stores construct, and write the
  same cells with the same SDK calls as 0.8.3. A builder partial update through an interface-typed
  property (`b.SetProperty(x => x.Lookup.Label, value)`) works as on 0.8.3, and a type with an invalid
  `[ColumnAlias]` declaration is reported by its first read or write (`InvalidOperationException`), not
  by `TypeInitializationException` when the store is constructed.
- Constructing a store no longer hangs for an entity whose nested types form a dense graph (many types
  that each have a property of every other one), and no longer overflows the stack, which ended the
  process, for a generic type whose property is a deeper instantiation of itself
  (`Node<T> { Node<Node<T>> Next }`).
- A class-level `[TableValueSplit]` covers the properties a class inherits, not only the ones it declares:
  an attribute on a derived class left the inherited `string` and `byte[]` properties out, and a derived
  class that declared the attribute again dropped the opt-in of its base class's properties. It no longer
  opts in the `Id` of a type that implements `IEntity` directly, so LINQ filters on `Id` work for such a
  type, and a property that hides (`new`) a base property marked `[TableValueSplit]` keeps that attribute.
- A `[TableValueSplit]` or `TableBlobContent` member of a runtime subtype is read back. When a property is
  declared as an interface, an abstract class or a base class and holds an object of a type that has such
  a member, a value too large for one cell was written as split cells (or a blob) and came back as `null`
  with no exception; the next save then dropped it. A read maps a large value the way it sets an ordinary
  cell: through the object that is already in the property. A builder partial update of such a member is
  refused with `NotSupportedException`.
- `[ColumnAlias]` on an opted-in property, and on a renamed `TableBlobContent` property, reads the value
  stored under the old column in every form. Only a value of one cell was read; a larger one loaded as
  `null`, and the next save dropped it and its blob.
- Many opted-in values that each fit one cell no longer make the entity unstorable. Twenty
  `[TableValueSplit]` strings of 30,000 characters were refused with an `OversizedValueException` that
  named the row key as the property and advised configuring `BlobOffload`, although the same entity with
  32,001 characters per property was stored.
- `OversizedValueException` names a column of the entity and gives an accurate reason. When the cells that
  cannot leave the main row exceed one entity, it names the largest of them and both limits; it no longer
  names the row key, and it does not advise configuring `BlobOffload` when ordinary cells are what does
  not fit.
- A large value that cannot be set on its property (the property's type changed since the row was written)
  is reported with at most 200 characters of the value and its total length, not with the whole value in
  the `SerializationException` message.
- After `DeletePartitionAsync` has deleted the rows it listed, it deletes a blob stored under the
  partition only if no row that is in the partition at that moment references it. A row another writer
  creates while the partition is being deleted survives the delete and keeps its blob, however long the
  delete took; every blob under the partition older than `CleanupGrace` used to be deleted, so such a row
  could become unreadable (`TableValueIntegrityException`, blob missing). When some blob is old enough,
  the cleanup reads the partition's rows once more; a blob younger than `CleanupGrace` is no longer sent
  a delete request at all. The provider SPI `LargeValueTable.DeletePartitionBlobsAsync` takes the entity
  type and a delegate that returns the rows that are in the partition now.
- For a split-aware type `DeletePartitionAsync` deletes a main row and its continuation rows in the same
  entity-group transaction, never across two (a transaction still holds at most 100 deletes), so a failure
  or a cancellation between two transactions leaves whole entities, never continuation rows without their
  main row. `InMemoryStorage<T>` deletes a partition of such a type in the same transactions and checks
  the cancellation token before each. The provider SPI gains `LargeValueTable.PlanPartitionDelete`.
- The cleanup that follows `UpsertAsync`, `UpdateAsync`, `UpsertBatchAsync` or `DeleteAsync` deletes a blob
  the row no longer references only when it is that row's own: a name of the offload shape under
  `{table}/{partitionKey}/{rowKey}/`. Saving or deleting a row through a table copied raw, or through a
  row re-keyed by copying its cells, deleted the source row's blob once it was older than `CleanupGrace`,
  and the source row then failed every read. The provider SPI `LargeValueTable.DeleteReleasedBlobsAsync`
  takes the keys of the row that was written or deleted and a callback for the paths that are not its own.
- A null `{Prop}__blob` cell means "no reference". The service returns a selected property the row does
  not have as null (the emulator omits it), and the point read before every large-value write selects
  each reference cell, so on a real account every large-value property that was not offloaded would have
  logged the warning "ignoring a malformed blob reference" on every write.
- A `CleanupGrace` longer than the time since year 1 (`TimeSpan.MaxValue`, say) no longer throws
  `ArgumentOutOfRangeException` after the row change has committed; with such a grace nothing is deleted.
- The version a write stamps on the entity is the write's own. After a write through an entity-group
  transaction (a value with continuation rows, or a shrink that deletes them) the row was read again to
  learn its new ETag; when another writer committed between the transaction and that read, the entity got
  the other writer's ETag over this writer's content, and its next `UpdateAsync` replaced the other
  writer's row without a `ConcurrencyConflictException`. The ETag now comes from the transaction's own
  response for the main row and the row is not read again (one round trip less). When that response
  carries no ETag, the entity's `ETag` is null after the write, as after a batch. The ETag of the
  transaction's response was compared with a point read on the storage emulator only; it was not checked
  against a real storage account.
- The point read before a split-aware `UpsertAsync`, `UpdateAsync(entity)`, `DeleteAsync` and
  `UpsertBatchAsync` no longer names `odata.etag` in its `$select`. The version is taken from the entity
  or from the response's `ETag` header; when neither carries one, the call throws
  `InvalidOperationException` and writes nothing, because a replace or delete without a version would be
  unconditional. With the projected queries (see Changed), the library names `odata.etag` in no `$select`.
- A missing table is not a concurrency conflict. When the table was deleted under a running store, a
  split-aware `UpsertAsync`, `LastWriterWins` `UpdateAsync` or `UpsertBatchAsync` retried and then threw
  `ConcurrencyConflictException`; it now throws the service's own error at once (`RequestFailedException`,
  status 404, error code `TableNotFound`; a `TableTransactionFailedException` when the write was a
  transaction). Only a missing row is a lost race.
- A damaged row can be deleted. A main row whose `_SplitParts` cell is not a count made `DeleteAsync`
  throw `TableValueIntegrityException`; `DeleteAsync` now deletes such a row with the continuation rows it
  finds, on condition of the row's version, and releases its blobs. Reading, upserting or updating such a
  row still throws `TableValueIntegrityException`: delete it, then create it again.
- A retried file write no longer stores a truncated file. When a write failed while a seekable stream was
  being hashed, a retry with the same `TableBlobContent` stored only the unread rest of the stream, with
  a matching length and SHA-256. Every attempt, and every upload, reads the stream from the position it
  had at the first attempt. A content that can no longer be written (a non-seekable stream partly read by
  a failed write, or one whose temporary copy a failed write released; a stream that cannot be put back
  where the content starts) is refused with `InvalidOperationException` before anything is read or
  uploaded, never with `ObjectDisposedException` and never by storing a partial or empty file.
- Reading a file while its entity is saved again no longer fails with `RequestFailedException` 412
  (`ConditionNotMet`), for the lazy stream of a `TableBlobContent` and for the eager download of an
  offloaded value. A blob deleted while it is read fails the read with `TableValueIntegrityException`
  ("was deleted while it was being read"), not with a raw `RequestFailedException`; an eager read then
  reads the row again and, when its version changed, is retried from the new version.
- A read of exactly `Length` bytes of a `TableBlobContent` stream is verified: the read that would return
  the last expected byte throws `TableValueIntegrityException` when the content does not match (such a
  caller used to get the bytes and no exception), and bytes beyond the expected length are never handed
  out.
- Writing a `TableBlobContent` taken from one row into another row no longer re-points the first row's
  entity, or its cached copy, at the second row's blob (it then failed with
  `TableValueIntegrityException` once the second row was deleted or replaced). One content from a
  non-seekable stream on two entities of one batch is stored in full for both; it threw
  `ObjectDisposedException` for the second.

### Tests and CI

- The Azurite integration tests skip only when the emulator is not reachable at all; any other probe
  failure is retried and then fails the run. `UNIFIED_TABLES_REQUIRE_AZURITE=1` turns a skip into a
  failure. Every workflow starts a pinned Azurite 3.35.0 and sets it.
- New workflow `.github/workflows/ci.yml` (`CI`, job `Build and test`): on every pull request and every
  push to `main` it builds the solution in Release and runs all tests on `ubuntu-latest`. It uses no
  secret. This is the check that gates a pull request on the tests: require `CI / Build and test` in
  branch protection.
- The SonarQube workflow no longer hides a failed build or a failed test. Its scanner `begin`,
  `dotnet build`, `dotnet test` and scanner `end` are separate steps, and a failure skips the steps after
  it, so the analysis of a commit whose tests fail is not uploaded. The four commands shared one
  PowerShell step, whose result is the exit code of the last command: a failing test passed the check
  whenever the scanner's `end` succeeded. The `SonarQube / Build and analyze` check needs a valid
  `SONAR_TOKEN` and fails at `Begin analysis` without one, whatever the tests do.
- The publish workflow fails before the push unless the packed `Unified.Data.Tables` depends on
  `Unified.Data.Tables.Abstractions` `[x.y.z]` and the packed `Unified.Data.Tables.InMemory` on
  `Unified.Data.Tables` `[x.y.z]`, where `x.y.z` is the version of the sibling package packed in the same
  run.
- `AzuriteTableStorageIntegrationTests` gives every test its own partitions (the name plus a GUID), so
  several test runs can share one Azurite.
- A test class that calls `AddUnifiedTableStorage` or `AddUnifiedInMemoryStorage`, or sets
  `TableEntitySerializer.OversizedCellPolicy`, must carry `[Collection("OversizedCellPolicy")]`: the
  policy is process-wide state, and one class outside the collection now and then failed the policy
  tests. `OversizedCellPolicyCollectionTests` fails the test run when a class of the main test project
  writes the policy outside that collection.

### Unchanged

- Types that opt into nothing produce the same columns and make the same SDK calls as 0.8.3, with the
  differences listed under Changed: the `$select` of a projected query, the cache corrections, and one
  construction of an entity type that has a nested-object property. `OversizedCellPolicy` gains no value.

## [0.8.3] — 2026-08-21

Another legacy-row read gap found by the production canary.

### Fixed

- **A getter-only wrapper whose single constructor parameter IS the stored JSON was unreadable.**
  Such a type (e.g. a collection wrapper with a private `[JsonConstructor]` taking the collection
  itself) is stored as a BARE JSON array. The constructor converter treated every root as an
  object, found no property matching the parameter name, and fed the parameter's default (null)
  into a constructor that does `ImmutableList.AddRange(items)` — `ArgumentNullException`. A
  single-parameter constructor now receives the whole document when the root is not an object.
  (Real-world shape: `JobLevelCollection` in `JobTrackCreatedStateEvent`; 21 rows in one
  production event store, written 2019.)

## [0.8.2] — 2026-08-21

Two more legacy-row read gaps found by running a read-only canary over a real staging storage
account (millions of rows across six accounts). Both are the same class of defect as 0.8.1: rows
written by the historical Newtonsoft-based serializers that the current read path could not
reconstruct.

### Fixed

- **A getter-only type with a single public parameterized constructor whose parameters do not
  exactly match the property types was unreadable.** System.Text.Json selects such a constructor
  but validates that each parameter name AND type match a property exactly, so a shape like an
  `IEnumerable<T>` parameter fed by an `ImmutableList<T>` property threw
  `InvalidOperationException` ("Each parameter in the deserialization constructor ... must bind to
  an object property or field"). Newtonsoft bound parameters by name and deserialized each argument
  to the parameter's declared type. The serializer's constructor converter now takes over this case
  and binds by name, deserializing each argument to the parameter type — the historical behaviour.
  (Real-world shape: `PaceQuestionGroup`; ~5,200 rows in one production event store.)
- **Rows whose `_TypeName` token names a type in a namespace that has since been renamed were
  unreadable.** `AssemblyQualifiedTypeDiscriminator.RegisterLegacyTypeNamespace(legacy, current)`
  registers a namespace rename; tokens whose type portion starts with the legacy prefix resolve by
  substituting the current prefix. The untyped `TableEntitySerializer.FromTableEntity` read path
  applies the same map, so a single registration covers every type moved in the same namespace
  move. Writes are unaffected — new rows always carry the current assembly-qualified name.
  (Real-world shape: `Notifications.Contracts` → `EmailNotifications.Contracts`; 42 of 45 rows in
  one process-manager store.)

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
