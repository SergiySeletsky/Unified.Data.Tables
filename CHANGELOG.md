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
- Do not remove `[TableValueSplit]` from a property while rows hold large values of it (split cells,
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
- Back up and restore an entity's main row, continuation rows and offload blobs together. A table copied
  raw shares the source's blobs (the references hold the source table's blob paths). Writes through the
  copy never delete them, but the source's own replaces, deletes, partition deletes and sweeps can: give
  the copy its own copy of them before the source changes, or enable blob versioning or soft delete on
  the container.
- The offload container is exclusive to the library. Orphan blobs are removed only by
  `SweepOrphanBlobsAsync`: schedule it, or register `AddUnifiedOrphanBlobSweep`.
- A type is large-value aware (split-aware) when its declared types opt in, read by reflection alone: a
  `[TableValueSplit]` or `TableBlobContent` property of the entity or of a type it flattens into columns,
  or `[TableValueSplit]` on the entity class. Every other type is plain and is stored as on 0.8.3. Reads
  of split-aware types cost one extra range query per entity with continuation rows, plus a blob download
  per eager offloaded value, queries included.
- Once a type opts in, its `UpsertAsync`, `UpdateAsync(entity)`, `DeleteAsync` and `UpsertBatchAsync` read
  the row before they write, and `UpsertAsync`, a `LastWriterWins` `UpdateAsync`, `DeleteAsync` and
  `UpsertBatchAsync` can throw `ConcurrencyConflictException` under contention (see Changed). Code that
  assumed an upsert or a delete never conflicts needs a catch or a retry.
- Reads of a split-aware type (a point read, a query, a stream, a page) can throw
  `ConcurrencyConflictException` too: when the row changed during each of a read's five attempts (another
  writer replaced it while its continuation rows or blobs were read). Nothing stored is damaged, and the
  read can be repeated as it is; there is nothing to re-apply. Its `InnerException` is the
  `TableValueIntegrityException` of the last attempt, which is thrown on its own only for a row whose
  version did not change. `MutateAsync`, `MutateOrCreateAsync`, `TryMutateAsync` and `TryTransitionAsync`
  count such a read as a lost race: it uses one of their `maxAttempts` attempts and is retried after the
  same backoff; when the attempts run out, `TryMutateAsync` and `TryTransitionAsync` return
  `MutationStatus.Conflicted` and the other two throw `ConcurrencyConflictException`. `GetOrCreateAsync`
  has no attempt budget, and a `ConcurrencyConflictException` from its read leaves it.
- Beside `UnifiedTableStorageOptions` that the host registered through a factory or a type, a
  connection-string or credential `AddUnifiedTableStorage` call registers a keyed `BlobServiceClient` even
  when those options use no blob offload. On Lamar the host's own unkeyed `BlobServiceClient` then
  resolves to the library's client unless the host registers it after the call; on the DryIoc adapter
  (6.2.0) it resolves to null. Register such options as an instance, or let
  `AddUnifiedTableStorage(configure)` register them, to keep 0.8.3's registrations.

### Added

- **`[TableValueSplit(SplitEncoding)]`** (property or class, `Offload` = `BlobOffload.AboveThreshold`
  or `Never`) stores strings, `byte[]` and JSON-stored properties beyond the 64 KB cell and 1 MB row
  limits without trimming: split cells, continuation rows (`{RowKey}~0001..`) in the same entity-group
  transaction, or an Azure Blob, with a SHA-256 over the stored bytes verified on read.
  `SplitEncoding.Text` and `Gzip`; strings that are not well-formed UTF-16 are stored as `utf16le`.
  - On a class the attribute covers every `string`, `byte[]` and JSON-stored property the class declares
    or inherits, except the entity `Id`, which stays a key column that can be filtered, and except a
    scalar stored as JSON only for want of a native cell (`float`, `short`, `ushort`, `sbyte`, `char`,
    `DateOnly`, `TimeOnly`, `Half`, `Int128`, `UInt128`, nullable or not), which can never need
    splitting and keeps a plain type's builder updates. A derived class
    inherits the attribute of its base class; an attribute on a property wins over the one on its class;
    a property that overrides or hides (`new`) another keeps that one's attribute.
  - A member of a runtime subtype is supported: declare the property as an interface, an abstract class
    or a base class, initialise it with an instance of the type that has the member
    (`public IShape Shape { get; set; } = new Circle();`), and opt the entity in. Such a member does not
    make the entity aware, because its declared types do not show it: mark the entity class
    `[TableValueSplit]` (which also makes its own `string`, `byte[]` and JSON-stored properties large
    values: no LINQ filter and no builder partial update on them), or mark one of its own properties, or
    declare the property with the type that carries the member. A read keeps the instance and maps the
    columns through its type.
  - A large value is stored only where a read returns it into a large-value member. These writes throw
    `NotSupportedException` naming the column or property before anything is uploaded or stored: through a
    store of a plain type, any value in a `[TableValueSplit]` member or a `TableBlobContent` below a
    runtime subtype, whatever its size (a plain store stores no large-value form; a null member is not
    refused), also through a builder update that sets an object holding it; a property marked
    `[TableValueSplit]` holding an object the serializer stores as columns (a subclass of its declared type,
    such as a positional record, with a public parameterless constructor, or, in a property declared as a
    nullable struct, a struct that declares a public parameterless constructor of its own), at every size (the runtime form of the misuse on a property
    whose declared type flattens; the message names one remedy: declare the property as the runtime type
    and mark that type's members); a value that needs split cells or a blob below an object whose
    type a read would not recreate, when the object a read creates or keeps there (the declared type of a
    property that is empty on a new entity, or the default instance) has no member of that name or one that
    is not a large-value member (`public MemoBase? Doc { get; set; }` written holding a `MemoMarked` that
    marks its `Notes`, which `MemoBase` does not), or that a read cannot create at all (a struct in a
    nullable property, an interface- or abstract-typed property with no instance); a `TableBlobContent` in
    a property declared as `object`; and a subclass written through an `IStorage<TBase>` whose large value
    a read of `TBase` would not return into a large-value member (any value of a large-value member when
    `TBase` is plain; when `TBase` is aware, a value that would leave its cell in a column only the
    subclass declares, before the caller's stream is read, or in a member the subclass marks and `TBase`
    declares without marking it). A value of one cell in such places is an ordinary cell, as on 0.8.3, and
    is never moved to split form to make a crowded row fit. In `CreateBatchAsync` and `UpsertBatchAsync`
    the refusal comes before anything of the refused entity's partition is uploaded or written; the
    transactions of partitions the batch wrote before it stay committed, as for any failing transaction of
    a batch. A write refused because a read could not map a file's column back to a property (a
    `TableBlobContent` below an object whose runtime type a read does not recreate) may have read the
    file's source first; a content created from a non-seekable stream must then be created again from its
    source.
  - A builder update is refused with `NotSupportedException` before anything is sent, for every entity
    type, when the declared types lead its column path to a marked member or a `TableBlobContent` through
    any member of each name (get-only, `[IgnoreDataMember]`, `new`, overridden, inherited), also through a
    property declared as a nullable struct, and also under a legacy `[ColumnAlias]` name of such a property
    in any form a read maps (`Old`, `Old__Json`, `Old__GZip`, split cells, `Old__blob`). An ordinary member
    of a nullable struct, and the legacy name of an ordinary property, are written as before. On a plain
    type, an `UpdateBuilder.Updates` key that names a member only a runtime subtype declares writes an
    ordinary cell, as on 0.8.3 (a trimming `OversizedCellPolicy` can trim it; a `TableBlobContent` value is
    refused).
  - The refusal messages name only remedies that work for the shape: a builder update that would write a
    large value says that a partial update cannot write one on any store and to replace the entity; a
    `TableBlobContent` in an `object` property (refused on every store, aware or not) names the
    declaration as `TableBlobContent`; a value below a nullable struct names the declarations that work
    (the struct property not nullable, and the holder property with the type that has it), not a
    class-level `[TableValueSplit]`, which does not help there; a value a read would bring into a member
    that is not a large-value member names the column, the type a read creates there and the runtime type,
    and the declarations that work (the property initialised with an instance of the runtime type in the
    constructor of its owner, the property declared as the runtime type, or `[TableValueSplit]` on the
    member of the declared type; for a subclass written through a store of its base type, an `IStorage` of
    the subclass, or the attribute on the base type's member). No refusal names a declaration that is
    already in place: where the declared types already give the member's owner its type, the message of a
    plain store names the properties on the way that hold an object of another type
    (`declare Order.Doc as DocImpl`), and a plain type's builder update of a `TableBlobContent` names the
    declaration of the member the declared types reach.
  - When many opted-in values that each fit one cell exceed one entity together, the largest are stored
    in split form so that their chunks can go to continuation rows (never a value a read would not return
    into a large-value member); a row that fits one entity is stored as before, every value in its own
    cell.
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
  - The library puts a seekable stream back where the content started whenever it is done reading it
    (after hashing, after each upload, after a failed, cancelled or successful write), so a retry with the
    same content, or with a new content over the same stream (a `MutateAsync` delegate that runs again),
    stores the same bytes. A stream that can no longer be put back fails the write with
    `InvalidOperationException` before anything is uploaded.
  - A forward-only stream is read once. The library remembers the stream objects a write has read: the
    stream given to `FromStream` and the streams below it that `BufferedStream`, `GZipStream`,
    `DeflateStream`, `ZLibStream` and `BrotliStream` expose, followed recursively. A stream a write read
    to its end is remembered for as long as the stream object lives, whether that write committed or
    failed and whatever became of any other write. A new content over it is stored whole when the stream
    delivers at least one byte (a new payload: a server hands every request of a connection the same
    request-body object), and refused with `InvalidOperationException` before anything is uploaded when it
    delivers nothing, whether the earlier write committed or not (the retry that would store an empty
    file). A new, empty payload on a reused stream object is therefore refused too: store an empty file
    with `TableBlobContent.FromBytes` and an empty array, or from a seekable stream. A write that read the
    stream to its end and then lost its temporary copy (it failed or was cancelled while finishing it)
    counts the stream as read to its end; a write that cannot create its temporary copy throws before it
    reads anything and leaves the stream as it was. Two contents over one such stream in one write are
    refused the same way. A stream a failed or cancelled write read only part of (a write cancelled once
    it began to read counts, also before the stream delivered a byte, since the library cannot tell
    whether the stream took bytes it did not deliver), and a seekable stream a write could not put back,
    are refused for every later content, also below a new framework wrapper; so a server that reuses a
    request-body object which a failed write read part-way refuses every later content over it (buffer the
    body into a seekable stream, or close the connection after such a failure). A content whose write
    failed after it began to read the stream and before its upload is refused for every later write. (A
    forward-only content whose blob was uploaded before another value of the same write failed is not
    affected: it is bound to that blob and a retry reuses it.) A
    new wrapper of another kind (a user-written or decryption stream) over a consumed stream is not
    recognised and stores whatever that stream still delivers, possibly nothing. Create the content once,
    outside a delegate that may run again, and assign that instance on every attempt; use a seekable
    stream or `FromBytes`; or open the source again for each attempt (a new source of the bytes, not a new
    wrapper).
  - Once an attempt has uploaded the content's blob, the content streams that blob, also when the write
    then failed on another value's upload or was cancelled after that upload: a retry with the same
    content reuses it, before the blob is collected by a sweep or a partition delete's cleanup; after
    that the write throws `TableValueIntegrityException` and the content must be created again. The
    written property is given its new content as soon as the blob is uploaded, before the row is written;
    a write that then fails leaves the property streaming the blob uploaded for that row. Retry before an
    orphan sweep collects that blob, or assign the original content again (or read its row again) before
    retrying.
  - One content can be assigned to several properties or entities, written at the same time through one
    store or several: the writes take turns on it (a waiting write can be cancelled), and every write that
    succeeds stores the whole content. A content read from one row can be written into another, or into
    the same row through another store: each row gets its own blob, and the written property is given its
    own `TableBlobContent` that streams the blob of its own row in its own store, so the entity it came
    from, and its cached copy, are not affected. A property on a struct owner keeps its content object.
  - `ContentType` is the type recorded in the row; the blob's `Content-Type` header is the one of its
    first upload.
  - When `OpenReadAsync` finds the blob gone, it throws `TableValueIntegrityException` and the
    `TableStorage<T>` that returned the entity stops answering that row from its cache, so reading the
    entity again returns the current row.
- **`IOrphanBlobSweeper.SweepOrphanBlobsAsync(partitionKey, olderThan)`** and the table-wide
  **`SweepOrphanBlobsAsync(olderThan)`** delete unreferenced blobs, implemented by `TableStorage<T>` and
  `InMemoryStorage<T>`, with matching extension methods on `IStorage<T>` (they throw
  `NotSupportedException` for a store without the capability). Best-effort cleanup after a replace or
  delete only removes the row's own blobs (names under `{table}/{partitionKey}/{rowKey}/`), and only
  those untouched for `CleanupGrace`, so schedule the sweep; `olderThan` must exceed the longest write
  duration. The table-wide sweep lists partitions with the delimiter `/` and isolates a failing
  partition. Neither sweep stops at an orphan it cannot delete: the other orphans are still tried, and
  the failures are thrown as one `AggregateException` at the end, whose message says how many blobs were
  deleted. The partition key may be empty or whitespace (only `null` throws) and is normalized as ids
  are on write. Sweeps and partition deletes only
  delete names of the exact offload shape that no row of the partition references; the container is for
  offload alone.
- **`AddUnifiedOrphanBlobSweep(o => ...)`** and **`AddUnifiedOrphanBlobSweep<T>()`**: a hosted service
  (`IHostedService`) that runs the table-wide sweep of each registered type every
  `OrphanBlobSweepOptions.Interval` (default 1 hour) with `OlderThan` (default 1 hour), first after
  `InitialDelay` (default 1 minute). A failing type is logged at `Error` and retried next round without
  stopping the others; shutdown cancels the round; each round sweeps the store `IStorage<T>` resolves
  to, whatever options are registered (a store built with its own options and blob client is swept),
  and skips a library store without blob offload with one `Information` log. Out-of-range
  options throw `ArgumentOutOfRangeException` at registration.
- **`TableValueIntegrityException`** and **`OversizedValueException`** (with `ColumnName`, `EncodedBytes`,
  `Reason` and `OffloadForbiddenByProperty`, which is true when offload is configured but the property
  sets `BlobOffload.Never`). `OversizedValueException` reports every entity that cannot be stored for
  its size: a value that needs blob offload and cannot have it, cells that cannot leave the main row and
  exceed one entity (252 properties, 1,000,000 estimated bytes), or a row that does not fit one
  transaction. It names a column of the entity, and its message ends with what to do. A split value
  keeps four cells on the main row and a row with split values its part count, so an entity with only
  the properties of `Entity` holds 62 split values; beyond that the exception names the largest split
  value with its own encoded size, says how many properties its cells need and how many the rest of the
  row leaves, and advises fewer properties or a lower `BlobOffloadOptions.ThresholdBytes`. A second
  constructor, `OversizedValueException(columnName, encodedBytes, reason, remedy)`, takes that advice.
- **`BlobOffloadNotConfiguredException`** (an `InvalidOperationException` with `TableName` and
  `ColumnName`): a row holds a `{Prop}__blob` reference and the store has no `BlobOffload` configured.
- **`TableRequestSize`** for callers that build their own transactions: `EstimateEntityBytes`,
  `EstimateEntityWireBytes` and `EstimateActionWireBytes` (the planners' stored and escaped-wire estimates,
  for a `TableEntity` or any `ITableEntity` model), `FitsOneTransaction`, and the limits as constants
  (`MaxTransactionActions`, `MaxTransactionPayloadBytes`, `MaxTransactionWireBytes`,
  `MaxTransactionEntityBytes`, `MaxEntityBytes`, `MaxEntityProperties`). `FitsOneTransaction` returns
  false when two actions name the same row (the service refuses such a transaction with 400
  `InvalidDuplicateRow`; row keys compare ordinally).
- A six-parameter `TableStorage<T>` constructor taking the `BlobServiceClient`. In DI the offload client is
  chosen only when `BlobOffload` is set, and follows from the registered (unkeyed)
  `UnifiedTableStorageOptions`, whoever registered them (a library call, or the host as an instance, a
  factory or a type): their `BlobOffloadOptions.ServiceClient`; else, when they set
  `UseRegisteredServiceClient`, the host's registered `BlobServiceClient` and no other one; only when they
  choose neither, the client a call supplied (its own `ServiceClient`, the host's client when it sets
  `UseRegisteredServiceClient`, or the one its connection string or endpoint derives). The library keeps
  that client under its own key (`[FromKeyedServices]`) and never registers or takes the host's unkeyed
  `BlobServiceClient`. A store constructed directly uses `BlobOffloadOptions.ServiceClient` only when no
  client is passed to the constructor. When `AddUnifiedTableStorage` is called more than once, the first
  call's options are the registered ones; a later call supplies the offload client when the registered
  options set `BlobOffload`, choose no client and no call has supplied one yet (a module registers the
  options, the host registers the connection string). The first client a call supplies is never
  replaced, and a first call without `BlobOffload` still wins over a later one that sets it. Options
  registered through a factory or a type are read when the container builds the client: beside them a
  later call offers a client only when it has one. Keyed services are needed: on Lamar register the
  host's own `BlobServiceClient` after `AddUnifiedTableStorage`; DryIoc's adapter is not supported for
  offload in DI (see the README). With `UseRegisteredServiceClient`, register the host's
  `BlobServiceClient` as a singleton: every `TableStorage<T>` is a singleton that takes the offload client
  when it is constructed, so with a scoped registration every store, a plain one too, fails to resolve
  under `ValidateScopes`, and without `ValidateScopes` the stores keep the root scope's instance.
- Storage-provider SPI in `Unified.Data.Tables.Providers` (`LargeValueRows`, `LargeValueTable`,
  `LargeValueWritePlan`, `IOffloadBlobStore`, `BlobState`, `BlobReference`), hidden from IntelliSense, for
  provider implementations such as the in-memory package. The in-memory store supports all of the above,
  with the same planning, retries, cleanup rules and exceptions.
- `Unified.Data.Tables.Providers.ProjectedRows`, a provider SPI marked `[EditorBrowsable(Never)]` and not
  for application code. It lets another `IStorage` implementation read a projected row or a filter view
  the way `TableStorage<T>` does. Its shape may change in any minor version.

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
  A write looks for leftovers only when one holds a key it needs; the reads and queries of every type never
  return them, nor does `CountAsync` of the split-aware type (a plain type on the same table counts them,
  as on 0.8.3). A leftover that another caller removes between the write's failed transaction and its look
  at the key is a lost race (retried within the five attempts); `InvalidOperationException` is thrown only
  when a row that is not the entity's own holds the key at that moment. A row another writer puts at a
  continuation key the entity already declares is treated as the entity's own and is replaced or deleted
  by its next write or delete. The provider SPI gains `LargeValueWritePlan.StoredPartCount`/`IsNewPart`,
  `LargeValueTable.ValidateKeys`, `LargeValueTable.ContinuationKeyTaken` and
  `LargeValueTable.PlanOverOwnParts`.
- `QueryPageAsync` of a split-aware type can return a page with fewer items than `Take`, or none, and a
  non-null continuation token: a continuation row takes a place in the page. Keep paging until the token
  is null. `InMemoryStorage.QueryPageAsync` pages such a type the same way.
- A projected `QueryPageAsync` no longer warms the entity cache for any type.
- A projected query (`QueryOptions.Select` on `QueryAsync(options)`, `QueryStreamAsync(options)` and
  `QueryPageAsync`) no longer sends `odata.etag` in `$select`, for every entity type. The row's version
  is not a column: the service returns it with every entity whatever `$select` names, so every projected
  entity still carries its `ETag`. A caller that names `odata.etag` in `QueryOptions.Select` has it
  dropped (a `Select` of that name alone is still a projection: the system columns and nothing else).
  This is the one deliberate difference in what a plain type's requests contain compared with 0.8.3; the
  cache corrections below can add a point read where 0.8.3 answered from its cache.
- The read cache no longer keeps a version that a write or delete has replaced, for every entity type. A
  read that overlapped a write or delete made through the same `IMemoryCache` could fetch the row, let the
  write commit, and then cache what it had fetched: later `OneAsync` calls returned the old version for
  the cache TTL (one hour, sliding, by default), `ExistsAsync` stayed true for a deleted entity, and a
  read-then-upsert caller wrote the old value back. The cached `QueryAsync(partition)` and `QueryAsync()`
  lists, and the entities that `QueryPageAsync` and the predicate queries warm, had the same defect, which
  0.8.3 has too. A read now caches what it found only if no write or delete of that entity has completed
  since the read began, and a list only if nothing was written in its partition (anywhere in the table,
  for the whole-table list). When two writes of one entity overlap, or a write and a delete do, neither
  keeps the entity cached once both have returned (before, the write whose call returned last stayed
  cached, also when the table held a later version or no row). While an earlier write of an entity is
  still running, a read made after a later write or delete of that entity has returned can, for a moment,
  be answered with the earlier write's version; once both calls have returned, the store answers what the
  table holds. Use ETags (`ConcurrencyMode.Strict`, `MutateAsync`) for an entity that several callers
  write at once. A create, update, upsert, builder update or delete that throws (a timeout or a cancelled
  token included) drops the entity's entry and its partition's cached lists, because it may have committed
  (before, only a 412 did); a cache that throws while it does (a disposed `MemoryCache` during shutdown)
  is logged as a warning and never replaces the call's own exception (on 0.8.3 a `Strict` update that met
  a 412 over a disposed cache threw `ObjectDisposedException`; it now throws
  `ConcurrencyConflictException`). A call refused by its own checks before anything is sent (a builder
  update of a large-value member, for one) drops nothing; for a type with large values, a create, update
  or upsert that fails while its values are prepared (a
  stream that fails while it is read, a value too large to store, a cancelled token) also drops the entry,
  although nothing was written. After `UpdateAsync(id, builder)` the cached entity is kept warm as on
  0.8.3 only when the entry cached when the update was sent is still the cached entry (no read or other
  write has put another since), every path names a top-level property, and every value is a non-null
  scalar of the property's type that the update writes as one cell in its own column and that a read
  converts back to an equal value (by the type's own equality, as on 0.8.3): a string the
  `OversizedCellPolicy` did not trim or compress, a `bool`, `int`, `uint`, `long`, `ulong`, `byte`,
  `double`, `Guid`, `TimeSpan` or enum (nullable forms included), a `decimal` that its stored `double`
  converts back to, a `DateTimeOffset` other than the instant 1601-01-01T00:00Z, or a `DateTime` that is
  neither local nor that instant. Then the store copies the cached entity once, constructing it once as
  0.8.3 did, and sets the values on the copy; the copy takes the update's ETag only when the update was
  conditional on exactly the cached entity's version and the response carried one, and otherwise keeps
  the ETag it had, as on 0.8.3, so an optimistic write with it meets a conflict and reads the row. Every
  other builder update (a nested path, an object, a collection or other JSON-stored value, `float`,
  `short`, `char`, `sbyte` and `ushort` included, a `byte[]`, a trimmed or compressed string, a null, a
  `decimal` that its stored `double` does not convert back to, the 1601-01-01 instant, which is stored as
  an unset date, a local `DateTime`) evicts the entry, and the next read fetches the row; on 0.8.3 any
  update with top-level paths patched the cached entity. If the copy or the cache put throws, the entry is
  evicted before the exception leaves the call, which has stored the update. Each of these can cost one
  more point read. None changes a cell or the request of a write; what a read returns changes only from a
  stale version to the current one. Writes made by another process, or through another `IMemoryCache`,
  are still not seen until the entry expires; a store with `CachePolicy.Disabled` over a shared cache
  still does not evict on a successful create, update or upsert; and a write of an entity that cannot be
  copied for the cache still leaves the cached copy of the version it replaced.
- The entity cache holds one entry per row, however its id is spelled. `"a"` and `"a|a"` address the
  same row but had separate entries, so a write or delete under one spelling left the entry cached under
  the other, and `DeletePartitionAsync` did not evict an entity whose id has one segment: it stayed
  readable through `OneAsync`, and `ExistsAsync` stayed true, for the cache TTL (in 0.8.3 too).
- `DeletePartitionAsync` invalidates the caches also when one of its transactions fails or is cancelled:
  an entity that an earlier transaction deleted is no longer answered from the cache.
- The library runs none of a plain type's code (constructor, static constructor, getters) that 0.8.3 does
  not run: constructing a store, `DeleteAsync`, `ExistsAsync` of a missing row, `CountAsync` and
  `DeletePartitionAsync` construct nothing, and neither does `UpdateAsync(id, builder)`, except that when
  it keeps a cached entity warm it copies it once, constructing it once as 0.8.3 did. A large-value aware
  type is constructed once, with its public parameterless constructor, when its store first needs the list
  of its large-value columns, to learn the columns of the objects the constructor puts in its properties;
  an exception from the constructor or a getter is ignored (the declared columns stand for that operation)
  and a later use tries again, after which replaces and deletes release the blobs of members of runtime
  subtypes again. A write of such a type may construct it once for each column of a `TableBlobContent`,
  and of a `[TableValueSplit]` value below a nested object or of a subclass written through a store of its
  base type, that is not resolved yet, to learn whether a read returns that column into a large-value
  member; while the constructor or a getter throws, such a write of a value below a nested object, or of
  such a subclass, that needs split cells or a blob is refused with `NotSupportedException` before
  anything is uploaded or stored (a value of one cell is written, and so is every top-level value of an
  entity of the store's own type, a file included). A builder partial update of such a type may
  construct it once per column it first
  names; while the constructor or a getter throws (or an object along the path cannot be created), a
  builder update that names a column not resolved before (a top-level one too) is refused with
  `NotSupportedException` before anything is written, because whether the column is a large value cannot
  be decided. Its inner exception is the failure (a `TargetInvocationException` whose inner exception is
  the constructor's); a misused `[TableValueSplit]` met there is reported as itself, as a whole-entity
  write reports it. The failure is not remembered: replace the entity instead, or send the update again
  once the constructor succeeds.
- The reads of `TableStorage<T>` (`OneAsync`, `ExistsAsync`, the queries, streams and pages) skip every
  row that carries a `_SplitOf` cell, for every entity type, so a plain type on a table shared with a
  large-value aware type does not return that type's continuation rows; `CountAsync` of a plain type still
  counts them, as on 0.8.3. A projected `QueryAsync` of a plain type (one with `QueryOptions.Select`)
  returns them too, because `_SplitOf` is not selected. No plain type writes that cell, so a plain type
  on a table of its own is unaffected. `InMemoryStorage<T>` now does the same (see InMemory fidelity).
- No `byte[]` (at any depth, of a plain or a large-value aware type) is shared between what a caller
  writes or reads and what the read cache of `TableStorage<T>` or the rows of `InMemoryStorage<T>` and
  `InMemoryPolymorphicStorage<TBase>` hold: an array the store returns or was given is never the cached or
  stored one, on every put and every hit. Reusing a buffer you wrote, or changing an array you read, no
  longer changes what the store answers or what an optimistic write sends (on 0.8.3 it could store bytes
  that were never written). A `[ProtectedProperty]` `byte[]` is therefore compared by content on both
  stores: an array with the same bytes is not a change (on 0.8.3 an array read from the table on a cache
  miss counted as one). A `TableBlobContent` is not copied: the cached entity holds the content object
  that was written or read, and a `TableBlobContent.FromBytes` content keeps reading the caller's array,
  which therefore must not be changed after the write.
- A projection (`QueryOptions.Select`) of a split-aware type is narrowed only when every name is a system
  column or resolves to an ordinary property; otherwise the whole row is fetched and whole entities are
  returned. A name of a large value in any form (its column, an object that owns it, one of its cells)
  returns the whole value, never a part of it; the reference cell `{Prop}__blob` named alone is the
  exception and leaves the property at its default. A plain type projects as on 0.8.3.
- Before a write or delete of an existing row, a split-aware store reads the stored row projected onto
  its part-count and continuation-marker cells and one `__blob` cell per large-value column; above 200
  such columns, or a `$select` longer than 2,000 characters, it reads the whole row instead (up to 1 MB
  per write or delete). That read decides which blobs the write or delete releases at once (subject to
  `CleanupGrace`): those of the type's own large-value columns, or every reference cell of a row read
  whole. A blob referenced from another cell (another type's column, a value below a repeated type, a
  removed property) is left to the orphan sweep, on both stores. The provider SPI gains
  `LargeValueTable.PriorRowSelect` and `LargeValueTable.PriorRowBlobPaths`.
- `DeletePartitionAsync` of a split-aware type leaves an entity written while it runs whole or gone.
  Each main row is deleted on condition of the version it was listed at. A write of the entity that
  commits after the listing (one that adds or removes continuation rows) makes that transaction fail with
  412 or 404, and its entities are then deleted one by one as they are now: the main row and every
  continuation row that names it. So no continuation row is left without its main row, and a concurrent
  shrink no longer fails the call with a raw 404 `TableTransactionFailedException`. Continuation rows
  listed without their main row are deleted last, only when no main row exists for them after the
  listing and each row is still the version listed; a row that changed is left alone and the call does
  not fail. Each such entity costs one point read and one transaction more. `InMemoryStorage<T>` applies
  the same condition. A partition with no concurrent writer costs the same requests as before. The count
  returned is of entities. A plain type and a polymorphic store delete a partition as 0.8.3 does, row
  by row: on a table a split-aware type also writes, such a delete can remove continuation rows of an
  entity written at the same time, so delete such a partition through the split-aware type.
- `InMemoryStorage<T>` of a plain type serializes every entity of a batch before it stores the first, as
  `TableStorage<T>` does: a batch in which one entity cannot be serialized stores nothing (a transaction
  refused by the store itself still keeps the transactions before it, see InMemory fidelity). On 0.8.3
  the in-memory store kept the entities before it, which a test against the fake could not see on Azure.
- Keyed `UnifiedTableStorageOptions` that a host registers for a purpose of its own no longer count as the
  library's options: the first `AddUnifiedTableStorage` or `AddUnifiedInMemoryStorage` call beside them
  registers its options and applies their `OversizedCells` policy. On 0.8.3 such a call silently did not
  apply its policy (the serializer kept the previous one).
- Polymorphic stores (`PolymorphicTableStorage<TBase>`, `InMemoryPolymorphicStorage<TBase>`) now throw
  `InvalidOperationException` naming the column when they read a row holding an offloaded reference
  (`{Prop}__blob`), instead of returning the property as null; they cannot fetch blobs.
- **A type read through its constructor lost every member that is not a constructor parameter.** Since
  0.8.2 the serializer's constructor converter has taken over types System.Text.Json reads through a
  constructor: a positional record, a class whose only public constructor takes arguments, or a private
  constructor marked with a foreign `[JsonConstructor]`. It invoked the constructor and dropped every
  other JSON member. A positional record's `init`/`set` members
  (`record Order(string Number) { List<string> Lines { get; init; } }`) therefore read back empty, as one
  `__Json` cell and as a `[TableValueSplit]` value at every size, and the next write stored the loss.
  Each JSON property no constructor parameter takes is now assigned to the settable or `init` member that
  System.Text.Json's own contract names for it, as both System.Text.Json and Newtonsoft do.
  `[JsonIgnore]`, `[JsonPropertyName]`, `[JsonInclude]` and a member's `[JsonConverter]` mean what they
  mean to System.Text.Json, and a member the JSON does not name keeps its initialiser. Writing is
  unchanged, so rows written by 0.8.x already carry these members and now read them. The change affects
  reads of plain types too: a stored member whose JSON no longer deserializes to the member's current
  type now fails the read, where 0.8.x ignored it. Up to 0.9.0 the converter matched Newtonsoft only in
  how it bound constructor parameters (the 0.8.2 entry's "the historical behaviour"). Newtonsoft also
  assigned the remaining members, which the converter did not do until now.
- Two stores of one entity type that share an `IMemoryCache` no longer serve each other's rows.
  Read-cache keys now include the table, meaning the account endpoint and the table name, compared
  ignoring case. Before, a store over another table (another `TableNameResolver` result) or the same
  table name in another account answered `OneAsync`, `ExistsAsync` and `QueryAsync` from the other
  store's entries (in 0.8.3 too). Stores of the same table still share entries. The cache key format is
  internal and changed: code that removed entries by a hand-built key (`{Type.FullName}:entity:{id}`)
  must stop doing so.
- A failed `CreateAsync` (409, `DuplicateKeyException`) evicts the entity's cache entry and its
  partition's cached lists, so the next read of them goes to the table (one extra request after a
  duplicate create). A 409 can be the SDK's retry meeting this call's own committed insert. 0.8.3 kept
  the partition list cached and answered it without the new row. `GetOrCreateAsync` is unaffected.
- Corrected the `UnifiedTableStorageOptions.OversizedCells` comment: the default is `Throw`, not trim.

### Changed: InMemory fidelity (behaviour changes for tests written against the fake)

`InMemoryStorage<T>` and `InMemoryPolymorphicStorage<TBase>` now succeed and fail where
`TableStorage<T>` and `PolymorphicTableStorage<TBase>` do over the service. A test suite that pinned the
fake's old behaviour can see these changes:

- `InMemoryStorage<T>` evaluates a predicate query (`QueryAsync(predicate)`, `QueryStreamAsync(predicate)`)
  on the stored primary row before it assembles or downloads anything. A row whose large value cannot be
  read (a missing continuation row or blob, a failed integrity check) fails the query only when the row
  matches, as on Azure. On 0.8.3 and the earlier 0.9.0 builds every row was assembled first, so one
  damaged row failed every predicate query of its partition.
- `QueryOptions.Select` is honoured by `InMemoryStorage<T>.QueryAsync`, `QueryStreamAsync` and
  `QueryPageAsync`. Only the selected cells and the system columns are read, and unselected members keep
  their defaults, as on Azure. A projection that names no large value of a large-value aware type
  assembles and downloads nothing. A projection that names a large value reads whole rows, as
  `TableStorage<T>` does. On 0.8.3 the fake ignored `Select` and returned whole entities.
- An already-cancelled `CancellationToken` makes every in-memory write and delete throw
  `OperationCanceledException` before anything commits: `CreateAsync`, `UpsertAsync`, `UpdateAsync`
  (entity and builder), `CreateBatchAsync`, `UpsertBatchAsync`, `DeleteAsync` and `DeletePartitionAsync`,
  on plain and large-value types and on `InMemoryPolymorphicStorage<TBase>`. A plain batch checks the
  token before each transaction. On 0.8.3 the fake committed despite the token.
- A `PartitionKey` or `RowKey` containing `/`, `\`, `#`, `?` or a control character (U+0000-U+001F,
  U+007F-U+009F) is refused with `RequestFailedException` 400 `InvalidInput`, wrapped in
  `TableTransactionFailedException` inside a batch, as the service refuses it. On 0.8.3 the fake stored
  such keys.
- A plain `UpsertBatchAsync` that names one key twice in one transaction fails with
  `TableTransactionFailedException` (400 `InvalidDuplicateRow`), as on Azure, and earlier transactions
  stay committed. On 0.8.3 the fake kept the last occurrence. `TableStorage<T>` is unchanged. Large-value
  types still write the last occurrence on both stores.
- Plain `CreateBatchAsync` and `UpsertBatchAsync` of `InMemoryStorage<T>`, and the batches of
  `InMemoryPolymorphicStorage<TBase>`, are applied per entity-group transaction. Grouping is per
  partition in first-appearance order, at most 100 rows and the request budget each, as `TableStorage<T>`
  sends them. A failing transaction applies nothing, and the partitions and transactions before it stay
  committed. On 0.8.3 the fake applied a whole batch or nothing. Every entity is still serialized before
  the first is stored.
- In-memory failures carry the service's `Status` and `ErrorCode`: 409 `EntityAlreadyExists` (inside
  `DuplicateKeyException`), 404 `ResourceNotFound`, 412 `UpdateConditionNotSatisfied` (inside
  `ConcurrencyConflictException`), 400 `InvalidDuplicateRow` and 400 `InvalidInput`. A failure inside a
  batch is wrapped in `TableTransactionFailedException`, as the SDK wraps it. On 0.8.3 the inner
  `RequestFailedException` had no `ErrorCode` and batch failures were not wrapped. (The large-value
  create batch's own up-front repeated-key check keeps an inner `RequestFailedException` 400 with no
  `ErrorCode`, as `TableStorage<T>` raises it.)
- Reads of a plain type on `InMemoryStorage<T>` (`OneAsync`, `ExistsAsync`, queries, streams, pages) skip
  rows that carry a `_SplitOf` cell for every `T`, and `CountAsync` counts them, as `TableStorage<T>`
  does. Before, the fake hid such rows only for large-value aware types and also left them out of the
  count of those types.

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
  entity from a row with a large value written by a large-value aware type (a well-formed split manifest
  or a blob reference); a row whose columns only have the names of such cells is cached as on 0.8.3.
- `[TableValueSplit]` on a property of an interface or abstract (non-collection) type is refused with
  `NotSupportedException`: such a value may flatten into columns at runtime, where the attribute was
  silently ignored. Like every misused attribute (`InvalidOperationException` on a native scalar, on a
  type that flattens into columns and on a `TableBlobContent`) it is reported when an entity of the type
  is written or a row of it is read (from the first such use, and on every later one), not when the store
  is constructed.
- `[TableValueSplit]` on a property declared on an interface is a misused attribute: .NET does not pass
  the attributes of an interface member to the implementing class, so the value was stored as an
  ordinary cell, and a trimming `OversizedCellPolicy` could trim it. It makes no type large-value aware,
  and every write or read that flattens a class implementing the interface into columns (the entity, or a
  property that flattens) throws `InvalidOperationException` naming the interface member, the
  implementing class and the remedy (put the attribute on the property of the implementing class, or on
  that class; for a struct, only the property of the struct, since C# allows no class-level attribute on
  a struct), before anything is stored; such a value is never trimmed. For an implementing class or
  struct stored as one JSON value (a class whose public constructors all take parameters, or a struct that
  declares no public parameterless constructor of its own), an attribute on its members has no effect: put
  `[TableValueSplit]` on the property that holds it, declared as the implementing type. A builder partial update, on both
  stores, is refused with the same exception before anything is written when the entity type, or the
  declared type of a property along the updated path, is or implements such an interface, also through a
  property declared as a nullable struct (the message then also names declaring that property as the
  struct), at every size and under every policy (`InMemoryStorage<T>` looks at the row first and throws
  the 404 of a missing row, or the conflict of a `WithETag` that names another version, as for every
  builder update;
  `TableStorage<T>` reports the misuse in both cases). Two cases are not checked: an instance held in a
  JSON-stored property (a collection, or a type stored as one JSON cell) is written as JSON like every
  other value of that cell, and a trimming `OversizedCellPolicy` can trim that cell; and a builder update
  through a property declared as a base class or `object` whose value at run time is such a class writes
  an ordinary cell (the check reads declared types), unless the entity type is large-value aware and its
  constructor puts an instance of the implementing class in that property. When the constructor of a
  plain type puts one there, every later read of the row reports the misuse.
- A create-only blob upload that conflicts with an orphan which a sweep deletes before the follow-up state
  read, or between that read and the touch, uploads again (three attempts in all) instead of failing the
  write with an integrity error.
- A read racing a concurrent replace or delete no longer fails with a false `TableValueIntegrityException`:
  when the continuation rows or a blob do not match, the main row is read again and, if its ETag changed,
  the read is retried from the new version after the writers' pause (honouring the cancellation token),
  five attempts in all, then `ConcurrencyConflictException` (see Upgrade notes); a row deleted meanwhile
  reads as absent. Only a row whose version did not change is reported as `TableValueIntegrityException`.
  The in-memory store does the same for blobs, with the same attempts, pause and message. A lazy
  `TableBlobContent` whose blob is gone says the row may have changed since it was read.
- Blob cleanup and sweep cutoffs are taken from the storage service's clock (the commit response's
  `Date`, or the blob service's time before listing) instead of the client clock, which could make a
  freshly touched blob look old when it ran ahead; the cleanup cutoff is taken from the commit. Without a
  service time the client clock is used less a further 5 minutes. That includes a service-time request
  cancelled without the caller cancelling (an SDK or network timeout), so a committed delete or partition
  delete is never reported as failed because of it. The provider SPI `IOffloadBlobStore`
  gains `GetServiceTimeAsync`, and `LargeValueTable.DeleteReleasedBlobsAsync` takes the commit's service
  time.
- `CreateBatchAsync` and `UpsertBatchAsync` of a split-aware type are transactional again, with the plain
  batch contract: per partition, each entity's rows (main row, continuation rows, stale-part deletes) are
  one unit packed into entity-group transactions (at most 100 actions, within the byte and 4 MiB wire
  budgets), atomic per transaction; an entity's rows never straddle two transactions. Each transaction's
  blobs are uploaded just before it (so a blob waits for its row no longer than in a single write, and the
  sweep's `OlderThan` need only exceed one transaction). The blobs the replaced rows released are deleted
  once, after the last transaction has committed, so one batch can store a content read from a row it
  replaces (a history entity keeping a document's previous file, two documents exchanging files) in any
  order and over any number of transactions; a batch that fails or is cancelled part way deletes none of
  them (the sweep does), and the same batch can be sent again. A batch refused for a reserved row key
  leaves the caller's entities unchanged (`ETag`, `CreatedAt`, `UpdatedAt`). An
  upsert transaction that loses a race (409, 412, or a stale part already gone) is re-read, planned again
  and retried after a short pause, five attempts in all. Two small rows in one partition are one
  transaction, not two separate writes. A row repeated in a create batch throws `DuplicateKeyException`
  before anything is written; in an upsert batch its last occurrence wins. The ids `"a"` and `"a|a"`
  address the same row and count as a repetition (they were sent as two rows of one transaction, which
  the service refuses with `InvalidDuplicateRow`). An existing key fails its create transaction as a
  whole; earlier transactions stay committed. The in-memory store packs and fails the same transactions.
  The provider SPI gains `LargeValueTable.PlanTransactions`, `MaxWriteAttempts`,
  `WaitBeforeRetryAsync` and `CommitTimeAsync`.
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
  cell: through the object that is already in the property. A builder partial update that sets an object
  holding such a member is refused with `NotSupportedException`, and so is one that names the member's
  column on a large-value aware type whose new entity holds such an object in that property.
- `[ColumnAlias]` on an opted-in property, and on a renamed `TableBlobContent` property, reads the value
  stored under the old column in every form. Only a value of one cell was read; a larger one loaded as
  `null`, and the next save dropped it and its blob.
- Many opted-in values that each fit one cell no longer make the entity unstorable. Twenty
  `[TableValueSplit]` strings of 30,000 characters were refused with an `OversizedValueException` that
  named the row key as the property and advised configuring `BlobOffload`, although the same entity with
  32,001 characters per property was stored.
- `OversizedValueException` names a column of the entity and gives an accurate reason. When the bytes of
  the cells that cannot leave the main row exceed one entity, it names the largest of them and both
  limits; when their number does (no property left for the cells of the split values), it names the
  largest split value with its own encoded size. It no longer names the row key or the size of a hash
  cell, and it does not advise configuring `BlobOffload` when ordinary cells are what does not fit, nor
  marking `[TableValueSplit]` a property that already is, except for a value that a read would not return
  into a large-value member, which stays on the main row: when it is the largest cell there, the message
  names it and advises marking the large properties (or the class), although that value is opted in
  already (a remedy that its refusal names lets it move, see Added).
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
  had at the first attempt, and the stream is put back there after every read. A content that can no
  longer be written (a non-seekable stream partly read by a failed write, or one whose temporary copy a
  failed write released; a stream that cannot be put back where the content starts; a new content over a
  forward-only stream that an earlier write read to its end and that delivers nothing now) is refused with
  `InvalidOperationException` before anything is uploaded, never with `ObjectDisposedException` and never
  by storing a partial or empty file (see Added for which streams are recognised). A
  retry with a content whose uploaded blob was collected meanwhile throws `TableValueIntegrityException`
  saying so, and writes nothing.
- A plain type reads a row whose extra columns merely have the name of a large-value cell (`Body__len`,
  `Body__c0`, written by another program) as 0.8.3 does: the column is ignored, or mapped as 0.8.3 maps it.
  Such a row, and every query of its partition, threw `TableValueIntegrityException` or
  `InvalidOperationException`. A group counts as the library's only when its manifest is complete and
  well formed or its `__blob` cell parses as a blob reference. Such a group is verified on read, for a
  plain type too: a bad hash throws `TableValueIntegrityException`, which fails the whole query that
  returns the row.
- A large value no read could return is refused at write (see Added): a `[TableValueSplit]` member of a
  struct in a nullable property, a value below an interface- or abstract-typed property with no instance,
  and a `TableBlobContent` in an `object`-typed property were stored, and every read of the row, and every
  query of its partition, then threw `SerializationException`.
- A whole-entity `UpdateAsync` or `UpsertAsync` that leaves a protected `TableBlobContent` or a protected
  opted-in `byte[]` as it was read is no longer refused as a change: a file counts as unchanged when it is
  the same blob (same SHA-256, length and content type), a `byte[]` when it has the same bytes (the
  in-memory store always refused it, `TableStorage` whenever its cache was cold). The refusal of a change
  to a protected large-value property gives the kind and length of both values instead of quoting them,
  and so does the refusal of a change to a protected property whose object holds, at any depth, a
  `TableBlobContent` or a `[TableValueSplit]` member ("a value of type Note"). A protected property whose
  values hold no such member, and every property of a type that is not large-value aware, keep the 0.8.3
  message. The provider SPI gains `ProtectedLargeValues` (with `HoldsLargeValue`), so other stores apply
  the same rule.
- One `TableBlobContent` written by several writes at the same time is stored whole by each, and two
  contents over one forward-only stream in one write are refused instead of storing an empty file in the
  second. A retry that creates a new content over the same seekable stream stores the same bytes, also
  after a failure in or after the upload.
- A content keeps streaming the blob of the store it was read through. Saving an entity through a second
  store of the same type whose blob has the same name (another account, the same table and keys)
  re-pointed the first store's entity, and its cached copy, at the second store's blob.
- After `OpenReadAsync` finds a content's blob gone, the store that returned the entity no longer answers
  it from its cache; reading the entity again returned the same cached entity, whose file could not be
  opened, for as long as the entry lived.
- A connection-string or credential `AddUnifiedTableStorage` call that follows options the host
  registered itself no longer replaces the client those options name (`ServiceClient`) or ask for
  (`UseRegisteredServiceClient`). Every large-value store then used the client derived from the later
  call's string, possibly another storage account.
- A type with several hundred large-value columns (or a few dozen with long flattened names) could be
  created and read but not written again or deleted by id: the read before the write named every such
  column (HTTP 431 on the emulator from about 450 columns, beyond the service's 255 projected properties
  from 254).
- `TableStorage.ExistsAsync` answers `true` for a row whose `_SplitParts` cell is not a count, or whose
  value stored in the row's own cells fails its integrity check, as `InMemoryStorage` does, and does not
  cache it. `OneAsync` and the queries still throw `TableValueIntegrityException` for such a row, and
  `DeleteAsync` removes it.
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
- A forward-only `TableBlobContent` whose blob was uploaded before another value of the same write failed
  (its upload threw, or the write was cancelled after this upload) is bound to that blob, so a retry with
  the same entity reuses it: no second upload and no orphan. Before, the content gave back its temporary
  copy and every later write refused it.
- Reading an offloaded split value no longer allocates the length its row's blob reference claims before
  reading the blob; the buffer grows with what the blob delivers (integrity checks unchanged).
- A read-cache hit copies large values in process, without the split codec (no gzip, hash or chunking).
  Before, each hit of a multi-megabyte Gzip value compressed and decompressed it, which made a hit slower
  than an uncached read (an 8M-char Gzip hit took 500-780 ms; it now takes under 1 ms). Entity cache
  entries still count as size 1 whatever their byte size.
- A whole-entity `UpdateAsync`/`UpsertAsync` that carries a protected `[TableValueSplit]` value stored as
  JSON (a list, a dictionary, an object stored as one value) unchanged from the read is no longer
  refused. Such values are compared by the JSON they store. A changed value is still refused without the
  role. This applies to both `TableStorage<T>` and `InMemoryStorage<T>`.
- A `[TableValueSplit]` value stored as JSON that holds a string with an unpaired surrogate is refused.
  System.Text.Json writes U+FFFD in its place, so the value read back altered with no error. The write
  now throws `NotSupportedException` naming the column, before anything is sent. This covers a string
  value, a string dictionary key and a `char`. A marked `string` member is unaffected and still stored
  losslessly as `utf16le`. Types that opt into nothing are unchanged.
- A class-level `[TableValueSplit]` no longer covers a scalar stored as JSON only for want of a native
  cell (`float`, `short`, `ushort`, `sbyte`, `char`, `DateOnly`, `TimeOnly`, `Half`, `Int128`, `UInt128`,
  nullable or not): a builder update of one was refused as a large value, where a plain type updates it.
  It is stored in the same `__Json` column as before, and a LINQ filter on it gives the plain type's
  refusal ("Filtering on type 'Single' is not supported"). A property-level attribute on one is still
  honoured.
- An orphan that cannot be deleted (an immutability policy, a permission, a service error) no longer
  stops the sweep of its partition: every orphan is tried, then the failures are thrown together (see
  Added). A blank partition key can be swept (only `null` is refused). The hosted sweep no longer skips a
  store built with its own blob offload when the registered options have none.

### Known limitations

- `MutateAsync`, `TryMutateAsync` and `MutateOrCreateAsync` can apply a mutation twice when the service
  applied the strict update but its response was lost (a timeout or a dropped connection). The SDK
  retries the request and gets 412, and the mutation is applied again to the re-read row. This is
  unchanged since 0.8.3. Make such mutations idempotent, or set `TableClientOptions.Retry.MaxRetries = 0`.

### Tests and CI

- The Azurite integration tests skip only when the emulator is not reachable at all; any other probe
  failure is retried and then fails the run. `UNIFIED_TABLES_REQUIRE_AZURITE=1` turns a skip into a
  failure. Every workflow starts a pinned Azurite 3.35.0 and sets it. The "Start Azurite" step of
  `ci.yml` and `publish.yml` waits until the blob (10000) and table (10002) endpoints each answer an HTTP
  request, and fails the job after 60 s with a message and the container's log.
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
- Every Azurite test takes its connection string from one helper: `UNIFIED_TABLES_TEST_STORAGE` when it
  is set, `UseDevelopmentStorage=true` otherwise.
- `InMemoryFidelityTests` runs each scenario on `InMemoryStorage<T>` and on `TableStorage<T>` over Azurite
  and compares the transcripts step by step.
- `publish.yml` has a 45-minute job timeout and fails before anything is built when the release tag (or
  an explicit `workflow_dispatch` version), without its leading `v`, differs from the `<Version>` of
  `Directory.Build.props`.
- A test class that calls `AddUnifiedTableStorage` or `AddUnifiedInMemoryStorage`, or sets
  `TableEntitySerializer.OversizedCellPolicy`, must carry `[Collection("OversizedCellPolicy")]`: the
  policy is process-wide state, and one class outside the collection now and then failed the policy
  tests. `OversizedCellPolicyCollectionTests` fails the test run when a class of the main test project
  writes the policy outside that collection.

### Unchanged

- Types that opt into nothing produce the same columns and make the same SDK calls as 0.8.3, and the
  library runs none of their code that 0.8.3 does not run, with the differences listed under Changed: the
  `$select` of a projected query, the cache corrections, the `byte[]` values no longer shared with the
  cache and the in-memory rows, the rows with a `_SplitOf` cell that reads skip, the policy of a first
  call beside keyed host options, the cache keys that include the table, the eviction after a duplicate
  create, the constructor converter's reads of non-constructor members, and the InMemory fidelity
  changes. `OversizedCellPolicy` gains no value.

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
