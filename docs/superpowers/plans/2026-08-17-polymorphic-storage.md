# Polymorphic Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `IPolymorphicStorage<TBase>` — a sibling contract to `IStorage<T>` that stores many concrete types in one Azure Table, discriminated by the existing `_TypeName` column, and reads them back as a common base with the true derived instance intact.

**Architecture:** Purely additive. A new contract plus supporting value types in `Unified.Data.Tables.Abstractions` (must compile on `netstandard2.0`), an Azure implementation and a type-resolution seam in `Unified.Data.Tables`, and a behavioural mirror in `Unified.Data.Tables.InMemory`. Keys are supplied explicitly as `TableKey(PartitionKey, RowKey)` and used verbatim. The store composes the existing serializer rather than modifying its write path: `ToTableEntity(persistType: false)` then set `_TypeName` from the discriminator. The only change to existing behaviour is reserving the `_` column prefix, which fixes a latent clobbering bug.

**Tech Stack:** .NET 10 (`Unified.Data.Tables`, `.InMemory`), `netstandard2.0;net10.0` (`.Abstractions`), `Azure.Data.Tables` 12.11.0, xunit.v3 3.2.2, NSubstitute 5.3.0.

**Spec:** `docs/superpowers/specs/2026-08-17-polymorphic-storage-design.md`

## Global Constraints

- Branch: `feat/polymorphic-storage`. Conventional commits (`feat:`, `fix:`, `test:`, `docs:`).
- `TreatWarningsAsErrors=true` and `GenerateDocumentationFile=true` repo-wide: **every public member needs an XML doc comment or the build fails.** Non-obvious decisions get a `//` comment stating the trap being closed.
- `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`, file-scoped namespaces.
- Anything in `src/Unified.Data.Tables.Abstractions/` **must compile on `netstandard2.0`**: use `Guard.NotNull(x, nameof(x))` — never `ArgumentNullException.ThrowIfNull`; no default interface methods; no `Index`/`Range`; no `Enumerable.Chunk`; no `Task.WaitAsync`; no `string.Split(string, StringSplitOptions)`. `IsExternalInit.cs` already exists, so `init`/`record` are fine.
- Code in `src/Unified.Data.Tables/` and `src/Unified.Data.Tables.InMemory/` is `net10.0`-only — modern BCL is fine there.
- Namespace is `Unified.Data.Tables` for both Abstractions and the Azure package. The fake uses `Unified.Data.Tables.InMemory`.
- Tests: namespace `Unified.Data.Tables.Tests`, no `using Xunit;` (global `<Using Include="Xunit" />`), plain `Assert.*` (no FluentAssertions), method names `Subject_Condition_Expectation`.
- Shared test models live in `tests/Unified.Data.Tables.Tests/TestSupport/TestModels.cs`; harnesses in `TestSupport/`.
- Do **not** add new `NoWarn` entries. Do **not** add `InternalsVisibleTo` to `Unified.Data.Tables.csproj`.
- Version lives in one place: `Directory.Build.props` `<Version>`. Bump 0.7.0 → 0.8.0 in the final task only.
- Build command: `dotnet build Unified.Data.Tables.slnx`. Test command: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj`.

---

## Task Overview

| # | Deliverable |
| --- | --- |
| 1 | `TableKey` value type (Abstractions) |
| 2 | `PolymorphicWrite<TBase>`, `PolymorphicEntry<TBase>`, `PolymorphicMessages` (Abstractions) |
| 3 | `IPolymorphicStorage<TBase>` + `PolymorphicStorageExtensions` (Abstractions) |
| 4 | Reserved `_` system-column rule in `TableEntitySerializer` (**the bug fix**) |
| 5 | `ITypeDiscriminator` + `AssemblyQualifiedTypeDiscriminator` |
| 6 | `TypeDiscriminatorMap` |
| 7 | `FromTableEntity<TBase>` / `TryFromTableEntity<TBase>` + `Materialize` extraction |
| 8 | `TableRowSize` + `TableInitializer` extractions from `TableStorage<T>` |
| 9 | `UnifiedTableStorageOptions.TypeDiscriminator` + `PolymorphicTableStorage<TBase>` construction, insert, upsert, get, delete |
| 10 | `PolymorphicTableStorage<TBase>` — heterogeneous batch + `MergeColumnsAsync` |
| 11 | `PolymorphicTableStorage<TBase>` — queries, streaming, count, delete-partition |
| 12 | `InMemoryPolymorphicStorage<TBase>` |
| 13 | Keyed DI registration (both packages) |
| 14 | README, CHANGELOG, version bump, PR |

---

### Task 1: `TableKey` value type

**Files:**
- Create: `src/Unified.Data.Tables.Abstractions/TableKey.cs`
- Test: `tests/Unified.Data.Tables.Tests/TableKeyTests.cs`

**Interfaces:**
- Consumes: `EntityId.Split`, `EntityId.Combine` (existing, `src/Unified.Data.Tables.Abstractions/EntityId.cs`).
- Produces: `readonly record struct TableKey(string PartitionKey, string RowKey)` with `static TableKey FromId(string id)`, `string ToId()`, `override string ToString()`. Every later task uses `TableKey` as the row address.

> **Note for the implementer:** `TableKey` deliberately does **not** validate. Nothing in this library validates ids today, and adding the first validator here would be inconsistent with `Entity.Id`. Null/empty rejection happens in `PolymorphicTableStorage` (Task 9). Do not add `Guard` calls to this type.

- [ ] **Step 1: Write the failing test**

Create `tests/Unified.Data.Tables.Tests/TableKeyTests.cs`:

```csharp
namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins <see cref="TableKey"/>'s bridge to the composite-id convention. The round-trip matters
/// because a polymorphic store addresses rows by key while the rest of the library addresses them
/// by id; the two spellings must agree or the same row gets two identities.
/// </summary>
public class TableKeyTests
{
    [Fact]
    public void FromId_CompositeId_SplitsOnFirstSeparator()
    {
        var key = TableKey.FromId("vision|execution|agent");

        Assert.Equal("vision", key.PartitionKey);
        Assert.Equal("execution|agent", key.RowKey);
    }

    [Fact]
    public void FromId_SingleSegmentId_UsesIdForBothKeys()
    {
        var key = TableKey.FromId("solo");

        Assert.Equal("solo", key.PartitionKey);
        Assert.Equal("solo", key.RowKey);
    }

    [Fact]
    public void ToId_EqualKeys_CollapsesToSingleSegment()
    {
        Assert.Equal("solo", new TableKey("solo", "solo").ToId());
    }

    [Fact]
    public void ToId_DistinctKeys_ProducesCompositeId()
    {
        Assert.Equal("agg-1|cmd-9", new TableKey("agg-1", "cmd-9").ToId());
    }

    [Theory]
    [InlineData("agg-1|cmd-9")]
    [InlineData("solo")]
    [InlineData("p|r|extra")]
    public void FromId_ThenToId_RoundTrips(string id)
    {
        Assert.Equal(id, TableKey.FromId(id).ToId());
    }

    [Fact]
    public void ToString_ReturnsTheComposeId()
    {
        Assert.Equal("agg-1|cmd-9", new TableKey("agg-1", "cmd-9").ToString());
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(new TableKey("a", "b"), new TableKey("a", "b"));
        Assert.NotEqual(new TableKey("a", "b"), new TableKey("a", "c"));
    }

    [Fact]
    public void Equality_IsCaseSensitive_BecauseKeysAreUsedVerbatim()
    {
        Assert.NotEqual(new TableKey("A", "b"), new TableKey("a", "b"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~TableKeyTests"`
Expected: FAIL — compile error, `The type or namespace name 'TableKey' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Unified.Data.Tables.Abstractions/TableKey.cs`:

```csharp
namespace Unified.Data.Tables;

/// <summary>
/// An explicit Azure Tables row address. <see cref="IStorage{T}"/> derives its keys from
/// <see cref="IEntity.Id"/>; a polymorphic table cannot, because its rows are ordered by things the
/// stored object does not carry — an aggregate version, an inverted tick count, an ambient
/// transaction id, or a literal marker key. Passing the pair explicitly IS the key strategy: the
/// caller computes it, so every scheme is expressible and none needs a hook.
/// </summary>
/// <remarks>
/// Keys are used <b>verbatim</b>. <see cref="IdNormalization"/> is never applied to a
/// <see cref="TableKey"/>, and that is deliberate rather than an oversight: a polymorphic row key is
/// frequently a case-sensitive payload (a base-32 id, a zero-padded tick count), and
/// <see cref="EntityId.Normalize"/> would lower-case it into a <em>different row</em>, silently
/// orphaning existing data.
/// <para>
/// This type does not validate. Nothing in this library validates ids, and making this the one
/// exception would put the in-memory fake and <see cref="Entity"/> under different rules. The
/// storage implementations reject null and empty keys at the boundary instead.
/// </para>
/// </remarks>
/// <param name="PartitionKey">The row's partition key, verbatim.</param>
/// <param name="RowKey">The row's row key, verbatim.</param>
public readonly record struct TableKey(string PartitionKey, string RowKey)
{
    /// <summary>
    /// Bridges the <see cref="IStorage{T}"/> composite-id convention: splits on the FIRST
    /// <c>'|'</c>, so a row key may itself contain <c>'|'</c>.
    /// </summary>
    /// <param name="id">A composite <c>"{PartitionKey}|{RowKey}"</c> id, or a single-segment id.</param>
    /// <returns>The address that id refers to.</returns>
    public static TableKey FromId(string id)
    {
        var split = EntityId.Split(id);
        return new TableKey(split.PartitionKey, split.RowKey);
    }

    /// <summary>
    /// The composite id addressing this row, in <see cref="EntityId"/>'s canonical spelling —
    /// the single-segment form when both keys are equal, so <c>"a|a"</c> and <c>"a"</c> agree.
    /// </summary>
    /// <returns>The composite id.</returns>
    public string ToId() =>
        string.Equals(PartitionKey, RowKey, StringComparison.Ordinal)
            ? PartitionKey
            : EntityId.Combine(PartitionKey, RowKey);

    /// <inheritdoc />
    public override string ToString() => ToId();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~TableKeyTests"`
Expected: PASS, 8 tests.

- [ ] **Step 5: Verify the netstandard2.0 leg still builds**

Run: `dotnet build src/Unified.Data.Tables.Abstractions/Unified.Data.Tables.Abstractions.csproj -f netstandard2.0`
Expected: `Build succeeded`, 0 warnings. (`StringComparison.Ordinal` and `record struct` are both fine there; `IsExternalInit.cs` already supplies the `init` polyfill.)

- [ ] **Step 6: Commit**

```bash
git add src/Unified.Data.Tables.Abstractions/TableKey.cs tests/Unified.Data.Tables.Tests/TableKeyTests.cs
git commit -m "feat(abstractions): add TableKey explicit row address"
```

---

### Task 2: `PolymorphicWrite`, `PolymorphicEntry`, `PolymorphicMessages`

**Files:**
- Create: `src/Unified.Data.Tables.Abstractions/PolymorphicWrite.cs`
- Create: `src/Unified.Data.Tables.Abstractions/PolymorphicEntry.cs`
- Create: `src/Unified.Data.Tables.Abstractions/PolymorphicMessages.cs`
- Test: `tests/Unified.Data.Tables.Tests/PolymorphicEntryTests.cs`

**Interfaces:**
- Consumes: `TableKey` (Task 1), `Guard.NotNull` (existing).
- Produces:
  - `sealed record PolymorphicWrite<TBase>(TableKey Key, TBase? Item, IReadOnlyDictionary<string, object>? SystemColumns = null) where TBase : class`, plus `PolymorphicWrite(TableKey, TBase)` and `static PolymorphicWrite<TBase> Marker(TableKey, IReadOnlyDictionary<string, object>)`.
  - `sealed class PolymorphicEntry<TBase> where TBase : class` with `Key`, `Item`, `Value`, `Discriminator`, `ETag`, `Timestamp`, `Columns`, `TValue Column<TValue>(string name)`, `bool TryColumn<TValue>(string name, out TValue value)`.
  - `internal static class PolymorphicMessages` with `MarkerHasNoValue(TableKey)`, `NotAssignable(string, Type)`, `NotSystemColumn(string)`, `TypeNameNotMergeable()`, `EmptyKey(string)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Unified.Data.Tables.Tests/PolymorphicEntryTests.cs`:

```csharp
namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the read-result shape. The distinction that matters: <c>Item</c> is null ONLY for a
/// deliberate typeless marker row, and <c>Value</c> is the accessor that refuses to pretend a
/// marker row carries an object.
/// </summary>
public class PolymorphicEntryTests
{
    private static PolymorphicEntry<object> Entry(object? item, params (string Name, object Value)[] columns)
    {
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var c in columns)
            dict[c.Name] = c.Value;

        return new PolymorphicEntry<object>(
            new TableKey("p", "r"), item, item is null ? null : "token",
            "W/\"1\"", DateTimeOffset.UnixEpoch, dict);
    }

    [Fact]
    public void Value_TypedRow_ReturnsItem()
    {
        var payload = new object();

        Assert.Same(payload, Entry(payload).Value);
    }

    [Fact]
    public void Value_MarkerRow_Throws()
    {
        var entry = Entry(null, ("_IsCommitted", true));

        var ex = Assert.Throws<InvalidOperationException>(() => entry.Value);
        Assert.Contains("marker row", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Item_MarkerRow_IsNull_AndColumnsSurvive()
    {
        var entry = Entry(null, ("_IsCommitted", true));

        Assert.Null(entry.Item);
        Assert.Null(entry.Discriminator);
        Assert.True(entry.Column<bool>("_IsCommitted"));
    }

    [Fact]
    public void Column_MissingColumn_Throws()
    {
        var entry = Entry(new object());

        Assert.Throws<KeyNotFoundException>(() => entry.Column<bool>("_Nope"));
    }

    [Fact]
    public void TryColumn_MissingColumn_ReturnsFalseAndDefault()
    {
        var entry = Entry(new object());

        Assert.False(entry.TryColumn<bool>("_Nope", out var value));
        Assert.False(value);
    }

    [Fact]
    public void TryColumn_PresentColumn_ReturnsTrueAndValue()
    {
        var entry = Entry(new object(), ("_IsPublished", true));

        Assert.True(entry.TryColumn<bool>("_IsPublished", out var value));
        Assert.True(value);
    }

    [Fact]
    public void Column_WrongType_ThrowsInvalidCast()
    {
        var entry = Entry(new object(), ("_IsPublished", true));

        Assert.Throws<InvalidCastException>(() => entry.Column<int>("_IsPublished"));
    }

    [Fact]
    public void Marker_Factory_ProducesNullItemAndTheGivenColumns()
    {
        var write = PolymorphicWrite<object>.Marker(
            new TableKey("t1", "FlagEntity"),
            new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsCommitted"] = false });

        Assert.Null(write.Item);
        Assert.Equal("FlagEntity", write.Key.RowKey);
        Assert.False((bool)write.SystemColumns!["_IsCommitted"]);
    }

    [Fact]
    public void TypedWrite_Convenience_HasNoSystemColumns()
    {
        var write = new PolymorphicWrite<object>(new TableKey("p", "r"), new object());

        Assert.NotNull(write.Item);
        Assert.Null(write.SystemColumns);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~PolymorphicEntryTests"`
Expected: FAIL — `The type or namespace name 'PolymorphicEntry<>' could not be found`.

- [ ] **Step 3: Write `PolymorphicMessages`**

Create `src/Unified.Data.Tables.Abstractions/PolymorphicMessages.cs`:

```csharp
namespace Unified.Data.Tables;

/// <summary>
/// Shared error text for the polymorphic contract, so the Azure store and the in-memory fake throw
/// byte-identical messages — a test written against either implementation documents the same
/// contract. Mirrors <see cref="ConcurrencyMessages"/>.
/// </summary>
internal static class PolymorphicMessages
{
    internal static string MarkerHasNoValue(TableKey key) =>
        $"Row '{key}' is a typeless marker row — it carries system columns only and no " +
        $"'{SystemColumnNames.TypeName}', so it has no object to return. Read Item (which is null " +
        "for a marker) or the raw Columns instead of Value.";

    internal static string NotAssignable(string discriminator, Type baseType) =>
        $"Stored type '{discriminator}' is not assignable to '{baseType.FullName}'. A polymorphic " +
        "read never materializes a type outside its base — this is the gate that stops a stored " +
        "type name from becoming an arbitrary-type deserialization. Point the store at the right " +
        "base type, or register the type on a TypeDiscriminatorMap for the base you intend.";

    internal static string Unresolvable(string discriminator) =>
        $"Stored type '{discriminator}' could not be resolved. With the default " +
        "AssemblyQualifiedTypeDiscriminator this usually means the assembly was renamed, moved or " +
        "strong-named since the row was written — which is exactly why assembly-qualified " +
        "discriminators are discouraged for new tables. Register a TypeDiscriminatorMap with a " +
        "stable token and call WithAssemblyQualifiedFallback() to keep reading legacy rows.";

    internal static string NotSystemColumn(string columnName) =>
        $"Column '{columnName}' is not a system column. A raw column written alongside a " +
        $"serialized object must start with '{SystemColumnNames.Prefix}': the serializer owns the " +
        "un-prefixed column namespace, so an un-prefixed sentinel would collide with a real " +
        "property and be silently overwritten on the next write.";

    internal static string TypeNameNotMergeable() =>
        $"'{SystemColumnNames.TypeName}' cannot be merged. Re-typing an existing row would leave " +
        "the previous type's data columns stranded on it, readable by nothing. Delete the row and " +
        "insert the new shape instead.";

    internal static string EmptyKey(string part) =>
        $"{part} must be a non-empty string. Azure Tables has no concept of an absent key, and an " +
        "empty one addresses a real (and almost certainly unintended) row.";
}
```

- [ ] **Step 4: Write `SystemColumnNames`**

Still in `src/Unified.Data.Tables.Abstractions/PolymorphicMessages.cs`, append:

```csharp
/// <summary>
/// The reserved column namespace. A leading <see cref="Prefix"/> marks a <em>system column</em>:
/// never produced from a property, never fed to a property setter.
/// </summary>
/// <remarks>
/// This lives in Abstractions rather than beside the serializer because both the Azure store and
/// the in-memory fake need the predicate, and because a consumer writing sentinel columns has to be
/// able to ask the question too.
/// </remarks>
public static class SystemColumnNames
{
    /// <summary>The character that marks a column as belonging to the storage layer, not the object.</summary>
    public const char Prefix = '_';

    /// <summary>Column holding the stored type discriminator.</summary>
    public const string TypeName = "_TypeName";

    /// <summary>
    /// True when <paramref name="columnName"/> is a system column. Used on every read path to keep
    /// a system column out of a same-named property, and on every write path to validate a raw
    /// column bag.
    /// </summary>
    /// <param name="columnName">The column name to test.</param>
    /// <returns>True when the name starts with <see cref="Prefix"/>.</returns>
    public static bool IsSystemColumn(string columnName) =>
        !string.IsNullOrEmpty(columnName) && columnName[0] == Prefix;
}
```

> **Why `SystemColumnNames.TypeName` duplicates `TableEntitySerializer.TypeNameColumnName`:** the serializer lives in the Azure package and Abstractions cannot reference it. Task 4 makes `TableEntitySerializer.TypeNameColumnName` delegate to this constant so there is exactly one definition of the string at runtime.

- [ ] **Step 5: Write `PolymorphicWrite`**

Create `src/Unified.Data.Tables.Abstractions/PolymorphicWrite.cs`:

```csharp
namespace Unified.Data.Tables;

/// <summary>
/// One row to write: where it goes, what object it holds, and any system columns riding alongside
/// the serialized object.
/// </summary>
/// <remarks>
/// <c>Item</c> is nullable on purpose. A null item writes a TYPELESS MARKER ROW — system columns
/// only, no discriminator, no data columns. That is what lets a commit-flag row share one Entity
/// Group Transaction with the typed rows it guards, and it reads back as an entry whose
/// <see cref="PolymorphicEntry{TBase}.Item"/> is null rather than throwing.
/// <para>
/// Every key in <c>SystemColumns</c> must satisfy
/// <see cref="SystemColumnNames.IsSystemColumn"/> and must not be
/// <see cref="SystemColumnNames.TypeName"/>. The prefix is a wire rule, not decoration — see
/// <see cref="SystemColumnNames"/>.
/// </para>
/// </remarks>
/// <typeparam name="TBase">The common base type this row's object is written as.</typeparam>
/// <param name="Key">The row address.</param>
/// <param name="Item">The object to serialize, or null for a typeless marker row.</param>
/// <param name="SystemColumns">Optional <c>'_'</c>-prefixed cells written alongside the object.</param>
public sealed record PolymorphicWrite<TBase>(
    TableKey Key,
    TBase? Item,
    IReadOnlyDictionary<string, object>? SystemColumns = null)
    where TBase : class
{
    /// <summary>A typed row with no system columns — the common case.</summary>
    /// <param name="key">The row address.</param>
    /// <param name="item">The object to serialize.</param>
    public PolymorphicWrite(TableKey key, TBase item)
        : this(key, item, null)
    {
    }

    /// <summary>
    /// A typeless marker row carrying only system columns — the two-phase-commit flag primitive.
    /// </summary>
    /// <param name="key">The row address.</param>
    /// <param name="systemColumns">The <c>'_'</c>-prefixed cells to write.</param>
    /// <returns>A write with no object.</returns>
    public static PolymorphicWrite<TBase> Marker(
        TableKey key, IReadOnlyDictionary<string, object> systemColumns)
    {
        Guard.NotNull(systemColumns, nameof(systemColumns));
        return new PolymorphicWrite<TBase>(key, null, systemColumns);
    }
}
```

- [ ] **Step 6: Write `PolymorphicEntry`**

Create `src/Unified.Data.Tables.Abstractions/PolymorphicEntry.cs`:

```csharp
namespace Unified.Data.Tables;

/// <summary>
/// One row read back: its address, the materialized object (typed as <typeparamref name="TBase"/>
/// but the TRUE derived instance), its discriminator, and its raw cells.
/// </summary>
/// <remarks>
/// <see cref="Item"/> is null exactly when the row carries no discriminator — a deliberate marker
/// row. A discriminator that is PRESENT but unresolvable or not assignable to
/// <typeparamref name="TBase"/> is an error and throws during materialization; it is never quietly
/// downgraded to null. "No type was ever written" and "the wrong type was written" are different
/// failures and must not look alike.
/// </remarks>
/// <typeparam name="TBase">The common base type every row materializes as.</typeparam>
public sealed class PolymorphicEntry<TBase>
    where TBase : class
{
    /// <summary>Creates an entry. Implementations construct these; callers read them.</summary>
    /// <param name="key">The row address.</param>
    /// <param name="item">The materialized object, or null for a marker row.</param>
    /// <param name="discriminator">The stored discriminator, or null for a marker row.</param>
    /// <param name="etag">The row's ETag, when the backend reported one.</param>
    /// <param name="timestamp">The service's last-write time.</param>
    /// <param name="columns">The row's raw cells.</param>
    public PolymorphicEntry(
        TableKey key,
        TBase? item,
        string? discriminator,
        string? etag,
        DateTimeOffset? timestamp,
        IReadOnlyDictionary<string, object> columns)
    {
        Guard.NotNull(columns, nameof(columns));
        Key = key;
        Item = item;
        Discriminator = discriminator;
        ETag = etag;
        Timestamp = timestamp;
        Columns = columns;
    }

    /// <summary>The row's address.</summary>
    public TableKey Key { get; }

    /// <summary>The materialized object, or null for a typeless marker row.</summary>
    public TBase? Item { get; }

    /// <summary>The stored discriminator value, or null for a typeless marker row.</summary>
    public string? Discriminator { get; }

    /// <summary>The row's ETag, or null when the backend did not report one.</summary>
    public string? ETag { get; }

    /// <summary>The service's last-write time, or null when the backend did not report one.</summary>
    public DateTimeOffset? Timestamp { get; }

    /// <summary>
    /// Every cell on the row except <c>PartitionKey</c>, <c>RowKey</c>, <c>Timestamp</c> and
    /// <c>odata.etag</c>, exactly as stored — including format suffixes (<c>Tags__Json</c>) and
    /// system columns. This is the raw property bag, not a property view.
    /// </summary>
    public IReadOnlyDictionary<string, object> Columns { get; }

    /// <summary>
    /// <see cref="Item"/>, throwing when the row is a marker. Use this when the call site knows the
    /// row is typed; a marker slipping through as null is a bug worth an exception, not a
    /// <see cref="NullReferenceException"/> three frames later.
    /// </summary>
    /// <exception cref="InvalidOperationException">The row is a typeless marker row.</exception>
    public TBase Value =>
        Item ?? throw new InvalidOperationException(PolymorphicMessages.MarkerHasNoValue(Key));

    /// <summary>Reads a raw column, strictly.</summary>
    /// <typeparam name="TValue">The stored cell type.</typeparam>
    /// <param name="name">The column name.</param>
    /// <returns>The cell value.</returns>
    /// <exception cref="KeyNotFoundException">The column is absent.</exception>
    /// <exception cref="InvalidCastException">The cell is not a <typeparamref name="TValue"/>.</exception>
    public TValue Column<TValue>(string name)
    {
        Guard.NotNull(name, nameof(name));
        if (!Columns.TryGetValue(name, out var raw))
            throw new KeyNotFoundException($"Row '{Key}' has no column '{name}'.");

        return (TValue)raw;
    }

    /// <summary>Reads a raw column, tolerantly — the right accessor for an optional sentinel.</summary>
    /// <typeparam name="TValue">The stored cell type.</typeparam>
    /// <param name="name">The column name.</param>
    /// <param name="value">The cell value, or default when absent or of another type.</param>
    /// <returns>True when the column exists and is a <typeparamref name="TValue"/>.</returns>
    public bool TryColumn<TValue>(string name, out TValue value)
    {
        Guard.NotNull(name, nameof(name));
        if (Columns.TryGetValue(name, out var raw) && raw is TValue typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~PolymorphicEntryTests"`
Expected: PASS, 9 tests.

- [ ] **Step 8: Verify the netstandard2.0 leg**

Run: `dotnet build src/Unified.Data.Tables.Abstractions/Unified.Data.Tables.Abstractions.csproj -f netstandard2.0`
Expected: `Build succeeded`, 0 warnings.

- [ ] **Step 9: Commit**

```bash
git add src/Unified.Data.Tables.Abstractions/PolymorphicWrite.cs \
        src/Unified.Data.Tables.Abstractions/PolymorphicEntry.cs \
        src/Unified.Data.Tables.Abstractions/PolymorphicMessages.cs \
        tests/Unified.Data.Tables.Tests/PolymorphicEntryTests.cs
git commit -m "feat(abstractions): add polymorphic write/entry types and reserved column namespace"
```

---

### Task 3: `IPolymorphicStorage<TBase>` and `PolymorphicStorageExtensions`

**Files:**
- Create: `src/Unified.Data.Tables.Abstractions/IPolymorphicStorage.cs`
- Create: `src/Unified.Data.Tables.Abstractions/PolymorphicStorageExtensions.cs`
- Test: `tests/Unified.Data.Tables.Tests/PolymorphicStorageExtensionsTests.cs`

**Interfaces:**
- Consumes: `TableKey`, `PolymorphicWrite<TBase>`, `PolymorphicEntry<TBase>` (Tasks 1–2).
- Produces: the 11-member `IPolymorphicStorage<TBase>` interface (exact signatures in Step 3 below) — Tasks 9–13 implement it. Plus extension methods `InsertAsync(key, item, ct)`, `UpsertAsync(key, item, ct)`, `InsertMarkerAsync(key, columns, ct)`, `ItemsOfType<TBase, TDerived>(entries)`.

> **netstandard2.0 reminder:** no default interface methods. All convenience goes in the static extension class. `IAsyncEnumerable<T>` is available via the already-referenced `Microsoft.Bcl.AsyncInterfaces`.

- [ ] **Step 1: Write the failing test**

Create `tests/Unified.Data.Tables.Tests/PolymorphicStorageExtensionsTests.cs`:

```csharp
using NSubstitute;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the convenience layer. These exist so the interface stays thin (both implementations must
/// mirror it exactly), and so a caller never hand-builds a <see cref="PolymorphicWrite{TBase}"/>
/// for the two common cases.
/// </summary>
public class PolymorphicStorageExtensionsTests
{
    private abstract class Msg;

    private sealed class Created : Msg;

    private sealed class Archived : Msg;

    [Fact]
    public async Task InsertAsync_KeyAndItem_ForwardsATypedWrite()
    {
        var store = Substitute.For<IPolymorphicStorage<Msg>>();
        var item = new Created();

        await store.InsertAsync(new TableKey("p", "r"), item, TestContext.Current.CancellationToken);

        await store.Received(1).InsertAsync(
            Arg.Is<PolymorphicWrite<Msg>>(w =>
                w.Key == new TableKey("p", "r") && ReferenceEquals(w.Item, item) && w.SystemColumns == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InsertMarkerAsync_ForwardsATypelessWrite()
    {
        var store = Substitute.For<IPolymorphicStorage<Msg>>();
        var columns = new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsCommitted"] = false };

        await store.InsertMarkerAsync(
            new TableKey("t1", "FlagEntity"), columns, TestContext.Current.CancellationToken);

        await store.Received(1).InsertAsync(
            Arg.Is<PolymorphicWrite<Msg>>(w => w.Item == null && w.SystemColumns != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ItemsOfType_FiltersByRuntimeType_AndSkipsMarkers()
    {
        var entries = new[]
        {
            Entry(new Created()),
            Entry(new Archived()),
            Entry(new Created()),
            Entry(null),
        };

        var created = entries.ItemsOfType<Msg, Created>().ToList();

        Assert.Equal(2, created.Count);
        Assert.All(created, c => Assert.IsType<Created>(c));
    }

    private static PolymorphicEntry<Msg> Entry(Msg? item) =>
        new(new TableKey("p", Guid.NewGuid().ToString()), item, item is null ? null : "t",
            null, null, new Dictionary<string, object>(StringComparer.Ordinal));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~PolymorphicStorageExtensionsTests"`
Expected: FAIL — `The type or namespace name 'IPolymorphicStorage<>' could not be found`.

- [ ] **Step 3: Write the interface**

Create `src/Unified.Data.Tables.Abstractions/IPolymorphicStorage.cs`:

```csharp
namespace Unified.Data.Tables;

/// <summary>
/// Persistence contract for a table holding MANY concrete types under one common base,
/// discriminated by the <see cref="SystemColumnNames.TypeName"/> column and read back as
/// <typeparamref name="TBase"/> with the true derived instance intact.
/// </summary>
/// <remarks>
/// A sibling of <see cref="IStorage{T}"/>, not a replacement. <see cref="IStorage{T}"/>'s
/// <c>where T : class, IEntity, new()</c> cannot express this and relaxing it would not help: an
/// abstract or interface base fails <c>new()</c>, and a message or event base typically does not
/// implement <see cref="IEntity"/> at all. <typeparamref name="TBase"/> is therefore constrained
/// only to <c>class</c>.
/// <para>
/// <b>The store owns its table.</b> There is no server-side type filter, so every enumerating
/// operation returns, counts or deletes every row in scope regardless of discriminator. Point two
/// stores at one table and each will see the other's rows.
/// </para>
/// <para>
/// Rows are immutable facts plus mutable system columns. There is no whole-row update, no
/// <see cref="ConcurrencyMode"/>, no <see cref="ProtectedPropertyAttribute"/> enforcement and no
/// caching — see <see cref="MergeColumnsAsync"/> for the one supported mutation.
/// </para>
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
public interface IPolymorphicStorage<TBase>
    where TBase : class
{
    /// <summary>
    /// Eagerly create the underlying table. It is otherwise created lazily on first use; call this
    /// from host startup for fail-fast semantics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the table exists.</returns>
    Task EnsureCreatedAsync(CancellationToken ct = default);

    /// <summary>
    /// Insert one row. Strict by design — an existing key throws
    /// <see cref="DuplicateKeyException"/>, because a polymorphic table is normally an append-only
    /// log whose de-duplication guarantee IS the insert.
    /// </summary>
    /// <param name="write">The row to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The written row, read back as an entry.</returns>
    /// <exception cref="DuplicateKeyException">A row already exists at that key.</exception>
    Task<PolymorphicEntry<TBase>> InsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default);

    /// <summary>Insert-or-replace one row, unconditionally.</summary>
    /// <param name="write">The row to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The written row, read back as an entry.</returns>
    Task<PolymorphicEntry<TBase>> UpsertAsync(PolymorphicWrite<TBase> write, CancellationToken ct = default);

    /// <summary>
    /// Transactional insert of a HETEROGENEOUS set — each write may carry a different concrete type
    /// (and so a different discriminator), or no type at all. Grouped by partition and chunked by
    /// <see cref="BatchPlanner"/> on both entity count and payload bytes, so a batch is atomic
    /// <em>per chunk</em> only.
    /// </summary>
    /// <param name="writes">The rows to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many rows were written.</returns>
    /// <exception cref="BatchPayloadTooLargeException">One row is too large to batch at any size.</exception>
    Task<int> InsertBatchAsync(IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default);

    /// <summary>
    /// Blind, unconditional server-side merge of system columns onto an existing row — no prior
    /// read, no ETag. The "mark as published / committed" primitive.
    /// </summary>
    /// <remarks>
    /// Every column name must satisfy <see cref="SystemColumnNames.IsSystemColumn"/>, and
    /// <see cref="SystemColumnNames.TypeName"/> is rejected: re-typing a row would strand the
    /// previous type's data columns on it.
    /// </remarks>
    /// <param name="key">The row to patch.</param>
    /// <param name="columns">The system columns to set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the merge is applied.</returns>
    /// <exception cref="ArgumentException">A column name is not a system column, or is the discriminator.</exception>
    Task MergeColumnsAsync(TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct = default);

    /// <summary>Read one row, or null when it does not exist.</summary>
    /// <param name="key">The row address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The entry, or null.</returns>
    Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default);

    /// <summary>Buffered read of a partition, or of the whole table when no partition is given.</summary>
    /// <param name="partition">The partition to read, or null for the whole table.</param>
    /// <param name="take">Maximum rows to return, or null for all of them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rows, in lexical (PartitionKey, RowKey) order.</returns>
    Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(
        string? partition = null, int? take = null, CancellationToken ct = default);

    /// <summary>
    /// Streaming read. Follows continuation tokens internally, so a caller never writes the
    /// <c>do { } while (continuationToken != null)</c> loop — nor omits it and silently truncates
    /// at one segment.
    /// </summary>
    /// <param name="partition">The partition to read, or null for the whole table.</param>
    /// <param name="take">Maximum rows to yield, or null for all of them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rows, in lexical (PartitionKey, RowKey) order.</returns>
    IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(
        string? partition = null, int? take = null, CancellationToken ct = default);

    /// <summary>Delete one row. Deleting a missing row is a no-op.</summary>
    /// <param name="key">The row address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the row is gone.</returns>
    Task DeleteAsync(TableKey key, CancellationToken ct = default);

    /// <summary>Delete every row in a partition, whatever its type.</summary>
    /// <param name="partition">The partition to clear.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many rows were deleted.</returns>
    Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default);

    /// <summary>
    /// Count rows in a partition, or in the whole table. Azure Tables has no server-side count, so
    /// this is a keys-only scan — O(n) round trips.
    /// </summary>
    /// <param name="partition">The partition to count, or null for the whole table.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The row count.</returns>
    Task<int> CountAsync(string? partition = null, CancellationToken ct = default);
}
```

- [ ] **Step 4: Write the extensions**

Create `src/Unified.Data.Tables.Abstractions/PolymorphicStorageExtensions.cs`:

```csharp
namespace Unified.Data.Tables;

/// <summary>
/// Convenience over <see cref="IPolymorphicStorage{TBase}"/>. Kept outside the interface — as with
/// <c>StorageExtensions</c> and <c>AppendLogExtensions</c> — so both implementations stay thin and
/// neither can drift from the other on sugar.
/// </summary>
public static class PolymorphicStorageExtensions
{
    /// <summary>Insert a typed row without hand-building a <see cref="PolymorphicWrite{TBase}"/>.</summary>
    /// <typeparam name="TBase">The store's base type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="key">The row address.</param>
    /// <param name="item">The object to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The written row, read back as an entry.</returns>
    public static Task<PolymorphicEntry<TBase>> InsertAsync<TBase>(
        this IPolymorphicStorage<TBase> storage, TableKey key, TBase item, CancellationToken ct = default)
        where TBase : class
    {
        Guard.NotNull(storage, nameof(storage));
        return storage.InsertAsync(new PolymorphicWrite<TBase>(key, item), ct);
    }

    /// <summary>Upsert a typed row without hand-building a <see cref="PolymorphicWrite{TBase}"/>.</summary>
    /// <typeparam name="TBase">The store's base type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="key">The row address.</param>
    /// <param name="item">The object to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The written row, read back as an entry.</returns>
    public static Task<PolymorphicEntry<TBase>> UpsertAsync<TBase>(
        this IPolymorphicStorage<TBase> storage, TableKey key, TBase item, CancellationToken ct = default)
        where TBase : class
    {
        Guard.NotNull(storage, nameof(storage));
        return storage.UpsertAsync(new PolymorphicWrite<TBase>(key, item), ct);
    }

    /// <summary>Insert a typeless marker row carrying only system columns.</summary>
    /// <typeparam name="TBase">The store's base type.</typeparam>
    /// <param name="storage">The store.</param>
    /// <param name="key">The row address.</param>
    /// <param name="columns">The system columns to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The written row, read back as an entry with a null <c>Item</c>.</returns>
    public static Task<PolymorphicEntry<TBase>> InsertMarkerAsync<TBase>(
        this IPolymorphicStorage<TBase> storage,
        TableKey key,
        IReadOnlyDictionary<string, object> columns,
        CancellationToken ct = default)
        where TBase : class
    {
        Guard.NotNull(storage, nameof(storage));
        return storage.InsertAsync(PolymorphicWrite<TBase>.Marker(key, columns), ct);
    }

    /// <summary>
    /// The derived instances of one concrete type from a mixed read, skipping marker rows. The
    /// point of a polymorphic store is that the runtime type survives the round trip; this is how a
    /// caller uses it.
    /// </summary>
    /// <typeparam name="TBase">The store's base type.</typeparam>
    /// <typeparam name="TDerived">The concrete type to select.</typeparam>
    /// <param name="entries">Entries from a read.</param>
    /// <returns>The matching derived instances.</returns>
    public static IEnumerable<TDerived> ItemsOfType<TBase, TDerived>(
        this IEnumerable<PolymorphicEntry<TBase>> entries)
        where TBase : class
        where TDerived : class, TBase
    {
        Guard.NotNull(entries, nameof(entries));
        foreach (var entry in entries)
        {
            if (entry.Item is TDerived derived)
                yield return derived;
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~PolymorphicStorageExtensionsTests"`
Expected: PASS, 3 tests.

- [ ] **Step 6: Verify the netstandard2.0 leg**

Run: `dotnet build src/Unified.Data.Tables.Abstractions/Unified.Data.Tables.Abstractions.csproj -f netstandard2.0`
Expected: `Build succeeded`, 0 warnings. If `IAsyncEnumerable<T>` fails to resolve, confirm `Microsoft.Bcl.AsyncInterfaces` is in the `netstandard2.0` conditional `ItemGroup` — it already should be.

- [ ] **Step 7: Commit**

```bash
git add src/Unified.Data.Tables.Abstractions/IPolymorphicStorage.cs \
        src/Unified.Data.Tables.Abstractions/PolymorphicStorageExtensions.cs \
        tests/Unified.Data.Tables.Tests/PolymorphicStorageExtensionsTests.cs
git commit -m "feat(abstractions): add IPolymorphicStorage contract and convenience extensions"
```

---

### Task 4: Reserve the `_` column prefix in `TableEntitySerializer` (bug fix)

**Files:**
- Modify: `src/Unified.Data.Tables/TableEntitySerializer.cs` (`TypeNameColumnName` at ~line 28; the read loops in `FromTableEntity<T>` at ~line 60 and `FromTableEntity` at ~line 89)
- Modify: `tests/Unified.Data.Tables.Tests/TestSupport/TestModels.cs`
- Test: `tests/Unified.Data.Tables.Tests/TableEntitySerializerTests.cs` (extend)

**Interfaces:**
- Consumes: `SystemColumnNames.IsSystemColumn`, `SystemColumnNames.TypeName` (Task 2).
- Produces: no new public API. `TableEntitySerializer.TypeNameColumnName` now delegates to `SystemColumnNames.TypeName`. Every read path skips system columns. Task 7 relies on this skip.

> **The bug being fixed:** `TableEntityValue.Create` splits a column name on `'_'` with `RemoveEmptyEntries`, so `_TypeName` becomes property path `["TypeName"]` and `_IsPublished` becomes `["IsPublished"]`. A stored type declaring either property silently receives the storage layer's value. Harmless only because no current model declares one.

- [ ] **Step 1: Add the test models**

Append to `tests/Unified.Data.Tables.Tests/TestSupport/TestModels.cs`:

```csharp
/// <summary>
/// Declares a property whose name collides with the discriminator column after the leading '_' is
/// stripped. Pins that a system column is never written into a same-named property.
/// </summary>
public sealed class MessageWithTypeNameProperty
{
    /// <summary>Payload, to prove the rest of the row still deserializes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Collides with "_TypeName" once the prefix is stripped.</summary>
    public string TypeName { get; set; } = "untouched";
}

/// <summary>
/// Declares a property colliding with a sentinel column name. Pins the same rule for consumer-owned
/// system columns, not just the discriminator.
/// </summary>
public sealed class MessageWithIsPublishedProperty
{
    /// <summary>Payload, to prove the rest of the row still deserializes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Collides with "_IsPublished" once the prefix is stripped.</summary>
    public bool IsPublished { get; set; }
}
```

- [ ] **Step 2: Write the failing test**

Append to `tests/Unified.Data.Tables.Tests/TableEntitySerializerTests.cs` (inside the existing test class, and add `using Unified.Data.Tables.Tests.TestSupport;` at the top if not already present):

```csharp
[Fact]
public void FromTableEntity_SystemColumn_IsNotWrittenIntoMatchingProperty()
{
    var row = new TableEntity("p", "r")
    {
        ["Name"] = "kept",
        [SystemColumnNames.TypeName] = "Some.Assembly.Qualified.Name, Some.Assembly",
    };

    var result = row.FromTableEntity<MessageWithTypeNameProperty>();

    Assert.Equal("kept", result.Name);
    Assert.Equal("untouched", result.TypeName);
}

[Fact]
public void FromTableEntity_SentinelColumn_IsNotWrittenIntoMatchingProperty()
{
    var row = new TableEntity("p", "r")
    {
        ["Name"] = "kept",
        ["_IsPublished"] = true,
    };

    var result = row.FromTableEntity<MessageWithIsPublishedProperty>();

    Assert.Equal("kept", result.Name);
    Assert.False(result.IsPublished);
}

[Fact]
public void IsSystemColumn_LeadingUnderscore_IsReserved()
{
    Assert.True(SystemColumnNames.IsSystemColumn("_TypeName"));
    Assert.True(SystemColumnNames.IsSystemColumn("_IsCommitted"));
    Assert.False(SystemColumnNames.IsSystemColumn("TypeName"));
    Assert.False(SystemColumnNames.IsSystemColumn("Tags__Json"));
    Assert.False(SystemColumnNames.IsSystemColumn(string.Empty));
}

[Fact]
public void TypeNameColumnName_HasExactlyOneDefinition()
{
    Assert.Equal(SystemColumnNames.TypeName, TableEntitySerializer.TypeNameColumnName);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~TableEntitySerializerTests"`
Expected: FAIL — `FromTableEntity_SystemColumn_IsNotWrittenIntoMatchingProperty` asserts `"untouched"` but gets the assembly-qualified name; `FromTableEntity_SentinelColumn_...` asserts `False` but gets `True`.

- [ ] **Step 4: Point `TypeNameColumnName` at the single definition**

In `src/Unified.Data.Tables/TableEntitySerializer.cs`, replace:

```csharp
    /// <summary>Column that stores the assembly-qualified type name when <c>persistType</c> is used.</summary>
    public const string TypeNameColumnName = "_TypeName";
```

with:

```csharp
    /// <summary>
    /// Column that stores the type discriminator when <c>persistType</c> is used. An alias for
    /// <see cref="SystemColumnNames.TypeName"/>, which is the single definition — Abstractions
    /// cannot reference this package, so the constant lives there and this is the historical name
    /// for it.
    /// </summary>
    public const string TypeNameColumnName = SystemColumnNames.TypeName;
```

- [ ] **Step 5: Skip system columns on both existing read paths**

In `FromTableEntity<T>`, replace the loop body's guard:

```csharp
        foreach (var kv in entity)
        {
            // A __Truncated marker is metadata about a trimmed/dropped cell, not data — feeding it
            // through SetProperty would drill into (and materialize) the property it describes.
            if (TableEntityValue.IsTruncationMarker(kv.Key))
                continue;
            var val = TableEntityValue.Create(kv.Key, kv.Value);
            result = (T)SetProperty(result, val);
        }
```

with:

```csharp
        foreach (var kv in entity)
        {
            // A __Truncated marker is metadata about a trimmed/dropped cell, not data — feeding it
            // through SetProperty would drill into (and materialize) the property it describes.
            if (TableEntityValue.IsTruncationMarker(kv.Key))
                continue;

            // A leading '_' marks a column the storage layer owns. It must never reach a property
            // setter: TableEntityValue.Create strips the prefix, so "_TypeName" resolves to path
            // ["TypeName"] and "_IsPublished" to ["IsPublished"] — a stored type declaring either
            // property was silently receiving the storage layer's value.
            if (SystemColumnNames.IsSystemColumn(kv.Key))
                continue;

            var val = TableEntityValue.Create(kv.Key, kv.Value);
            result = (T)SetProperty(result, val);
        }
```

Apply the identical guard to the loop in the untyped `FromTableEntity(this TableEntity entity)` overload (note it uses `result = SetProperty(result, val);` without the `(T)` cast — keep that as-is).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj`
Expected: PASS. If an existing test fails, it is asserting the old clobbering behaviour — read it carefully before changing it, and confirm against the spec's *Reserved column namespace* section that the new behaviour is the intended one.

- [ ] **Step 7: Commit**

```bash
git add src/Unified.Data.Tables/TableEntitySerializer.cs \
        tests/Unified.Data.Tables.Tests/TestSupport/TestModels.cs \
        tests/Unified.Data.Tables.Tests/TableEntitySerializerTests.cs
git commit -m "fix(serializer): never write a '_'-prefixed system column into a same-named property"
```

---

### Task 5: `ITypeDiscriminator` and `AssemblyQualifiedTypeDiscriminator`

**Files:**
- Create: `src/Unified.Data.Tables/ITypeDiscriminator.cs`
- Create: `src/Unified.Data.Tables/AssemblyQualifiedTypeDiscriminator.cs`
- Test: `tests/Unified.Data.Tables.Tests/TypeDiscriminatorTests.cs`

**Interfaces:**
- Consumes: `PolymorphicMessages.Unresolvable` (Task 2).
- Produces:
  - `public interface ITypeDiscriminator { string ToDiscriminator(Type type); Type Resolve(string discriminator, Type baseType); }`
  - `public sealed class AssemblyQualifiedTypeDiscriminator : ITypeDiscriminator` with `static AssemblyQualifiedTypeDiscriminator Instance { get; }`.

Tasks 6, 7, 9 and 14 consume `ITypeDiscriminator`.

> **Placement:** these live in `src/Unified.Data.Tables/` (net10.0), **not** Abstractions. Nothing in `IPolymorphicStorage<TBase>`'s signatures mentions a discriminator, so keeping the reflection off the `netstandard2.0` leg costs nothing. `Unified.Data.Tables.InMemory` references this package, so the fake can reach it.

- [ ] **Step 1: Write the failing test**

Create `tests/Unified.Data.Tables.Tests/TypeDiscriminatorTests.cs`:

```csharp
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins type resolution — the seam that decides what a stored type name is allowed to become.
/// </summary>
public class TypeDiscriminatorTests
{
    [Fact]
    public void AssemblyQualified_RoundTrips()
    {
        var sut = AssemblyQualifiedTypeDiscriminator.Instance;

        var token = sut.ToDiscriminator(typeof(TestCreatedEvent));

        Assert.Equal(typeof(TestCreatedEvent), sut.Resolve(token, typeof(TestMessage)));
    }

    [Fact]
    public void AssemblyQualified_Token_IsTheAssemblyQualifiedName()
    {
        Assert.Equal(
            typeof(TestCreatedEvent).AssemblyQualifiedName,
            AssemblyQualifiedTypeDiscriminator.Instance.ToDiscriminator(typeof(TestCreatedEvent)));
    }

    [Fact]
    public void AssemblyQualified_UnknownToken_ThrowsTypeLoadWithGuidance()
    {
        var ex = Assert.Throws<TypeLoadException>(() =>
            AssemblyQualifiedTypeDiscriminator.Instance.Resolve("No.Such.Type, No.Such.Asm", typeof(TestMessage)));

        Assert.Contains("TypeDiscriminatorMap", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssemblyQualified_ResolveIsCached_SameInstanceReturned()
    {
        var sut = AssemblyQualifiedTypeDiscriminator.Instance;
        var token = sut.ToDiscriminator(typeof(TestArchivedEvent));

        Assert.Same(sut.Resolve(token, typeof(TestMessage)), sut.Resolve(token, typeof(TestMessage)));
    }
}
```

- [ ] **Step 2: Add the shared polymorphic test models**

Append to `tests/Unified.Data.Tables.Tests/TestSupport/TestModels.cs`:

```csharp
/// <summary>
/// A non-<see cref="IEntity"/> message base shaped like a real CQRS message: an <c>Id</c>, a
/// protected-setter timestamp, and no CreatedAt/UpdatedAt/ETag/Timestamp. Exists to prove the
/// polymorphic contract admits a base that <see cref="IStorage{T}"/> cannot.
/// </summary>
public abstract class TestMessage
{
    /// <summary>The message id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Creation time. A protected setter, to pin that reflection still round-trips it.</summary>
    public DateTimeOffset Created { get; protected set; } = DateTimeOffset.UnixEpoch;

    /// <summary>Sets <see cref="Created"/> from a test.</summary>
    /// <param name="value">The value to set.</param>
    public void SetCreated(DateTimeOffset value) => Created = value;
}

/// <summary>Marks a message as an integration event, for runtime-subtype filtering tests.</summary>
public interface ITestIntegrationEvent;

/// <summary>A command in the shared hierarchy.</summary>
public sealed class TestCommand : TestMessage
{
    /// <summary>Command-only payload, to prove derived data survives a base-typed read.</summary>
    public string Operation { get; set; } = string.Empty;
}

/// <summary>A domain event in the shared hierarchy.</summary>
public sealed class TestCreatedEvent : TestMessage
{
    /// <summary>Event-only payload, to prove derived data survives a base-typed read.</summary>
    public int Version { get; set; }
}

/// <summary>A second domain event, so a partition can hold more than one derived type.</summary>
public sealed class TestArchivedEvent : TestMessage
{
    /// <summary>Event-only payload.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>An integration event, to exercise runtime-interface filtering on read.</summary>
public sealed class TestIntegrationEvent : TestMessage, ITestIntegrationEvent
{
    /// <summary>Event-only payload.</summary>
    public string Topic { get; set; } = string.Empty;
}

/// <summary>
/// A derived type with no public parameterless constructor. Pins that the polymorphic read path
/// falls back to uninitialized-object construction rather than throwing.
/// </summary>
public sealed class TestCtorlessEvent : TestMessage
{
    /// <summary>Creates the event.</summary>
    /// <param name="payload">The required payload.</param>
    public TestCtorlessEvent(string payload) => Payload = payload;

    /// <summary>The payload.</summary>
    public string Payload { get; set; }
}

/// <summary>Not part of the <see cref="TestMessage"/> hierarchy. Pins the assignability gate.</summary>
public sealed class UnrelatedType
{
    /// <summary>Payload.</summary>
    public string Name { get; set; } = string.Empty;
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~TypeDiscriminatorTests"`
Expected: FAIL — `The type or namespace name 'AssemblyQualifiedTypeDiscriminator' could not be found`.

- [ ] **Step 4: Write the interface**

Create `src/Unified.Data.Tables/ITypeDiscriminator.cs`:

```csharp
namespace Unified.Data.Tables;

/// <summary>
/// Maps a CLR type to the token stored in <see cref="SystemColumnNames.TypeName"/> and back.
/// </summary>
/// <remarks>
/// This is a seam rather than a fixed rule because the obvious implementation — an assembly-qualified
/// name — welds stored rows to assembly identity: a rename, namespace move or strong-name change
/// orphans every row that carries it. It also costs a few hundred bytes on <em>every</em> row,
/// charged against the transaction byte budget that caps batch size. See
/// <see cref="TypeDiscriminatorMap"/> for the recommended alternative.
/// <para>
/// A resolver is NOT a security boundary. Every polymorphic read independently verifies that the
/// resolved type is assignable to the store's base type, and no configuration can disable that
/// check — see <c>TableEntitySerializer.TryFromTableEntity</c>.
/// </para>
/// </remarks>
public interface ITypeDiscriminator
{
    /// <summary>The token to store for <paramref name="type"/>.</summary>
    /// <param name="type">The runtime type being written.</param>
    /// <returns>The discriminator token.</returns>
    string ToDiscriminator(Type type);

    /// <summary>Resolves a stored token back to a CLR type.</summary>
    /// <param name="discriminator">The stored token.</param>
    /// <param name="baseType">
    /// The store's base type, for diagnostics and for implementations that scope their lookup. The
    /// caller still enforces assignability, so an implementation must not rely on doing so itself.
    /// </param>
    /// <returns>The resolved type.</returns>
    /// <exception cref="TypeLoadException">The token does not name a type this resolver knows.</exception>
    Type Resolve(string discriminator, Type baseType);
}
```

- [ ] **Step 5: Write the default implementation**

Create `src/Unified.Data.Tables/AssemblyQualifiedTypeDiscriminator.cs`:

```csharp
using System.Collections.Concurrent;

namespace Unified.Data.Tables;

/// <summary>
/// Stores <see cref="Type.AssemblyQualifiedName"/> — byte-identical to what
/// <c>ToTableEntity(persistType: true)</c> has always written, so an existing table is readable with
/// no migration and no configuration.
/// </summary>
/// <remarks>
/// This is the default so that upgrading never orphans data, not because it is the best choice.
/// Prefer <see cref="TypeDiscriminatorMap"/> for any new table: an assembly-qualified token breaks
/// on assembly rename and is large enough to measurably shrink the batch size a transaction can
/// carry. The type is named for what it stores rather than left as an unmarked default so that the
/// trade-off is visible at the call site.
/// </remarks>
public sealed class AssemblyQualifiedTypeDiscriminator : ITypeDiscriminator
{
    // Type.GetType parses and probes on every call; tokens repeat once per row.
    private static readonly ConcurrentDictionary<string, Type> ResolveCache = new(StringComparer.Ordinal);

    /// <summary>The shared instance. The type is stateless apart from its resolve cache.</summary>
    public static AssemblyQualifiedTypeDiscriminator Instance { get; } = new();

    /// <inheritdoc />
    public string ToDiscriminator(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // AssemblyQualifiedName is null only for open generic parameters, which cannot be persisted
        // anyway; falling back keeps the failure at the read rather than writing a null cell.
        return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    }

    /// <inheritdoc />
    public Type Resolve(string discriminator, Type baseType)
    {
        ArgumentNullException.ThrowIfNull(discriminator);

        // GetOrAdd's factory throwing leaves nothing cached, so a transient load failure is retried
        // rather than memoized.
        return ResolveCache.GetOrAdd(
            discriminator,
            token => Type.GetType(token)
                     ?? throw new TypeLoadException(PolymorphicMessages.Unresolvable(token)));
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~TypeDiscriminatorTests"`
Expected: PASS, 4 tests.

- [ ] **Step 7: Commit**

```bash
git add src/Unified.Data.Tables/ITypeDiscriminator.cs \
        src/Unified.Data.Tables/AssemblyQualifiedTypeDiscriminator.cs \
        tests/Unified.Data.Tables.Tests/TestSupport/TestModels.cs \
        tests/Unified.Data.Tables.Tests/TypeDiscriminatorTests.cs
git commit -m "feat: add ITypeDiscriminator seam with assembly-qualified default"
```

---

*Tasks 6–14 continue in this document. Each follows the same five-beat rhythm: write the failing test, run it and see it fail, write the minimal implementation, run it and see it pass, commit.*

### Task 6: `TypeDiscriminatorMap`

**Files:**
- Create: `src/Unified.Data.Tables/TypeDiscriminatorMap.cs`
- Test: `tests/Unified.Data.Tables.Tests/TypeDiscriminatorTests.cs` (extend)

**Interfaces:**
- Consumes: `ITypeDiscriminator` (Task 5), `PolymorphicMessages.Unresolvable` (Task 2).
- Produces: `public sealed class TypeDiscriminatorMap : ITypeDiscriminator` with `Map<T>(string token)`, `Map(Type type, string token)`, `MapAssignableTo<TBase>(Assembly assembly, Func<Type, string>? naming = null)`, `WithAssemblyQualifiedFallback()`. All builder methods return `this` for chaining. Task 14 wires it through options.

- [ ] **Step 1: Write the failing test**

Append to `tests/Unified.Data.Tables.Tests/TypeDiscriminatorTests.cs`:

```csharp
[Fact]
public void Map_ShortToken_RoundTripsAndOmitsAssemblyIdentity()
{
    var sut = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created");

    Assert.Equal("created", sut.ToDiscriminator(typeof(TestCreatedEvent)));
    Assert.Equal(typeof(TestCreatedEvent), sut.Resolve("created", typeof(TestMessage)));
    Assert.DoesNotContain(",", sut.ToDiscriminator(typeof(TestCreatedEvent)), StringComparison.Ordinal);
}

[Fact]
public void Map_UnregisteredType_ThrowsOnWriteWithGuidance()
{
    var sut = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created");

    var ex = Assert.Throws<InvalidOperationException>(() => sut.ToDiscriminator(typeof(TestArchivedEvent)));
    Assert.Contains(nameof(TypeDiscriminatorMap.Map), ex.Message, StringComparison.Ordinal);
}

[Fact]
public void Map_UnknownToken_ThrowsTypeLoad()
{
    var sut = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created");

    Assert.Throws<TypeLoadException>(() => sut.Resolve("nope", typeof(TestMessage)));
}

[Fact]
public void Map_DuplicateToken_ThrowsAtRegistration()
{
    var sut = new TypeDiscriminatorMap().Map<TestCreatedEvent>("dup");

    Assert.Throws<ArgumentException>(() => sut.Map<TestArchivedEvent>("dup"));
}

[Fact]
public void Map_SameTypeTwice_ThrowsAtRegistration()
{
    var sut = new TypeDiscriminatorMap().Map<TestCreatedEvent>("a");

    Assert.Throws<ArgumentException>(() => sut.Map<TestCreatedEvent>("b"));
}

[Fact]
public void MapAssignableTo_BulkRegisters_TheHierarchy()
{
    var sut = new TypeDiscriminatorMap()
        .MapAssignableTo<TestMessage>(typeof(TestCreatedEvent).Assembly);

    Assert.Equal(nameof(TestCreatedEvent), sut.ToDiscriminator(typeof(TestCreatedEvent)));
    Assert.Equal(typeof(TestArchivedEvent), sut.Resolve(nameof(TestArchivedEvent), typeof(TestMessage)));
}

[Fact]
public void AssemblyQualifiedFallback_DisabledByDefault_ThenEnabled()
{
    var token = typeof(TestCreatedEvent).AssemblyQualifiedName!;
    var strict = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created");

    Assert.Throws<TypeLoadException>(() => strict.Resolve(token, typeof(TestMessage)));

    var lenient = new TypeDiscriminatorMap().Map<TestCreatedEvent>("created").WithAssemblyQualifiedFallback();

    Assert.Equal(typeof(TestCreatedEvent), lenient.Resolve(token, typeof(TestMessage)));
    Assert.Equal("created", lenient.ToDiscriminator(typeof(TestCreatedEvent)));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~TypeDiscriminatorTests"`
Expected: FAIL — `The type or namespace name 'TypeDiscriminatorMap' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Unified.Data.Tables/TypeDiscriminatorMap.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reflection;

namespace Unified.Data.Tables;

/// <summary>
/// An allow-list discriminator: only registered types can be written, and only registered tokens
/// can be read. The recommended choice for any new polymorphic table.
/// </summary>
/// <remarks>
/// Two problems this solves over <see cref="AssemblyQualifiedTypeDiscriminator"/>. A stable short
/// token survives assembly renames, namespace moves and strong-naming, none of which an
/// assembly-qualified name does. And it is small: an assembly-qualified name costs a few hundred
/// bytes on every row, charged against the 3&#160;MB transaction budget that caps how many rows a
/// batch can carry.
/// <para>
/// Registration is strict in both directions — a duplicate token or a re-registered type throws at
/// registration rather than at the first ambiguous read, because a mapping bug discovered against
/// production rows is a data problem rather than a configuration one.
/// </para>
/// <para>
/// For an existing table written with assembly-qualified names, call
/// <see cref="WithAssemblyQualifiedFallback"/>: reads accept both forms while writes always emit
/// the short token, so the table converges in place as rows are rewritten.
/// </para>
/// </remarks>
public sealed class TypeDiscriminatorMap : ITypeDiscriminator
{
    private readonly Dictionary<Type, string> toToken = [];
    private readonly Dictionary<string, Type> fromToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Type> fallbackCache = new(StringComparer.Ordinal);
    private bool assemblyQualifiedFallback;

    /// <summary>Registers <typeparamref name="T"/> under <paramref name="token"/>.</summary>
    /// <typeparam name="T">The concrete type to register.</typeparam>
    /// <param name="token">The stable token to store for it.</param>
    /// <returns>This map, for chaining.</returns>
    /// <exception cref="ArgumentException">The token or the type is already registered.</exception>
    public TypeDiscriminatorMap Map<T>(string token) => Map(typeof(T), token);

    /// <summary>Registers <paramref name="type"/> under <paramref name="token"/>.</summary>
    /// <param name="type">The concrete type to register.</param>
    /// <param name="token">The stable token to store for it.</param>
    /// <returns>This map, for chaining.</returns>
    /// <exception cref="ArgumentException">The token or the type is already registered.</exception>
    public TypeDiscriminatorMap Map(Type type, string token)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        if (fromToken.TryGetValue(token, out var existingType) && existingType != type)
        {
            throw new ArgumentException(
                $"Token '{token}' is already mapped to '{existingType.FullName}'. Two types sharing " +
                "one token would make every stored row of the pair ambiguous on read.",
                nameof(token));
        }

        if (toToken.TryGetValue(type, out var existingToken) && !string.Equals(existingToken, token, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Type '{type.FullName}' is already mapped to token '{existingToken}'. Remapping it " +
                "would strand every row already written under the old token.",
                nameof(type));
        }

        toToken[type] = token;
        fromToken[token] = type;
        return this;
    }

    /// <summary>
    /// Registers every concrete type in <paramref name="assembly"/> assignable to
    /// <typeparamref name="TBase"/>. Defaults to <see cref="MemberInfo.Name"/> as the token, which
    /// is short and stable but collides across namespaces — a collision throws here, at
    /// registration, rather than surfacing as an ambiguous read later.
    /// </summary>
    /// <typeparam name="TBase">The base type to scan for.</typeparam>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="naming">Token selector; defaults to the type's simple name.</param>
    /// <returns>This map, for chaining.</returns>
    /// <exception cref="ArgumentException">Two scanned types produce the same token.</exception>
    public TypeDiscriminatorMap MapAssignableTo<TBase>(Assembly assembly, Func<Type, string>? naming = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var name = naming ?? (t => t.Name);

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(TBase).IsAssignableFrom(type))
                continue;

            Map(type, name(type));
        }

        return this;
    }

    /// <summary>
    /// Also accept an assembly-qualified token on READ, for a table whose existing rows were written
    /// by <see cref="AssemblyQualifiedTypeDiscriminator"/>. Writes still emit the short token, so the
    /// table converges in place rather than needing a stop-the-world backfill.
    /// </summary>
    /// <returns>This map, for chaining.</returns>
    public TypeDiscriminatorMap WithAssemblyQualifiedFallback()
    {
        assemblyQualifiedFallback = true;
        return this;
    }

    /// <inheritdoc />
    public string ToDiscriminator(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (toToken.TryGetValue(type, out var token))
            return token;

        throw new InvalidOperationException(
            $"Type '{type.FullName}' is not registered on this {nameof(TypeDiscriminatorMap)}. Call " +
            $"{nameof(Map)}<{type.Name}>(\"token\") — or {nameof(MapAssignableTo)} to register a whole " +
            "hierarchy — before writing it. An allow-list that silently accepted unknown types would " +
            "not be one.");
    }

    /// <inheritdoc />
    public Type Resolve(string discriminator, Type baseType)
    {
        ArgumentNullException.ThrowIfNull(discriminator);
        if (fromToken.TryGetValue(discriminator, out var type))
            return type;

        if (assemblyQualifiedFallback)
        {
            return fallbackCache.GetOrAdd(
                discriminator,
                token => Type.GetType(token) ?? throw new TypeLoadException(PolymorphicMessages.Unresolvable(token)));
        }

        throw new TypeLoadException(
            $"Token '{discriminator}' is not registered on this {nameof(TypeDiscriminatorMap)}. If this " +
            "row was written before the map existed, call " +
            $"{nameof(WithAssemblyQualifiedFallback)}() to keep reading legacy rows while new writes " +
            "converge on short tokens.");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~TypeDiscriminatorTests"`
Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Unified.Data.Tables/TypeDiscriminatorMap.cs tests/Unified.Data.Tables.Tests/TypeDiscriminatorTests.cs
git commit -m "feat: add TypeDiscriminatorMap allow-list with legacy read fallback"
```

---

### Task 7: `FromTableEntity<TBase>` / `TryFromTableEntity<TBase>` and the `Materialize` extraction

**Files:**
- Modify: `src/Unified.Data.Tables/TableEntitySerializer.cs`
- Test: `tests/Unified.Data.Tables.Tests/TableEntitySerializerTests.cs` (extend)

**Interfaces:**
- Consumes: `ITypeDiscriminator` (Task 5), `SystemColumnNames` (Task 2), `PolymorphicMessages.NotAssignable` (Task 2), the existing private `TypeMetadataCache`, `SetProperty`, `ApplyColumnAliases`, `RestoreIdFromKeys`, `TableEntityValue`.
- Produces:
  - `public static TBase FromTableEntity<TBase>(this TableEntity entity, ITypeDiscriminator discriminator) where TBase : class`
  - `public static bool TryFromTableEntity<TBase>(this TableEntity entity, ITypeDiscriminator discriminator, out TBase? result) where TBase : class`
  - `private static object Materialize(TableEntity entity, Type type, bool restoreId)` — the shared read loop, now used by all four overloads.

Tasks 9–12 consume both public methods.

> **Two behaviours specific to the base-constrained path:** (1) `restoreId: false`, because a polymorphic key is unrelated to any property — recomputing `Id` from `(PartitionKey, RowKey)` would overwrite a command's real id with `"aggregateId|commandId"`; (2) an **absent** discriminator returns `false` (a deliberate marker row), while a **present but broken** one throws.

- [ ] **Step 1: Write the failing test**

Append to `tests/Unified.Data.Tables.Tests/TableEntitySerializerTests.cs`:

```csharp
[Fact]
public void FromTableEntity_BaseConstrained_ReturnsTrueDerivedInstance()
{
    var discriminator = AssemblyQualifiedTypeDiscriminator.Instance;
    var row = new TestCreatedEvent { Id = "e1", Version = 7 }.ToTableEntity("agg-1", "000000001");
    row[SystemColumnNames.TypeName] = discriminator.ToDiscriminator(typeof(TestCreatedEvent));

    var result = row.FromTableEntity<TestMessage>(discriminator);

    var typed = Assert.IsType<TestCreatedEvent>(result);
    Assert.Equal(7, typed.Version);
    Assert.Equal("e1", typed.Id);
}

[Fact]
public void FromTableEntity_BaseConstrained_DoesNotRewriteIdFromKeys()
{
    var discriminator = AssemblyQualifiedTypeDiscriminator.Instance;
    var row = new TestCommand { Id = "cmd-9", Operation = "op" }.ToTableEntity("agg-1", "cmd-9");
    row[SystemColumnNames.TypeName] = discriminator.ToDiscriminator(typeof(TestCommand));

    var result = row.FromTableEntity<TestMessage>(discriminator);

    Assert.Equal("cmd-9", result.Id);
}

[Fact]
public void TryFromTableEntity_NoDiscriminator_ReturnsFalse()
{
    var row = new TableEntity("t1", "FlagEntity") { ["_IsCommitted"] = true };

    Assert.False(row.TryFromTableEntity<TestMessage>(
        AssemblyQualifiedTypeDiscriminator.Instance, out var result));
    Assert.Null(result);
}

[Fact]
public void TryFromTableEntity_UnresolvableDiscriminator_Throws()
{
    var row = new TableEntity("p", "r") { [SystemColumnNames.TypeName] = "No.Such, No.Asm" };

    Assert.Throws<TypeLoadException>(() => row.TryFromTableEntity<TestMessage>(
        AssemblyQualifiedTypeDiscriminator.Instance, out _));
}

[Fact]
public void TryFromTableEntity_TypeNotAssignableToBase_Throws()
{
    var discriminator = AssemblyQualifiedTypeDiscriminator.Instance;
    var row = new TableEntity("p", "r")
    {
        [SystemColumnNames.TypeName] = discriminator.ToDiscriminator(typeof(UnrelatedType)),
    };

    var ex = Assert.Throws<InvalidOperationException>(() =>
        row.TryFromTableEntity<TestMessage>(discriminator, out _));
    Assert.Contains("not assignable", ex.Message, StringComparison.Ordinal);
}

[Fact]
public void FromTableEntity_BaseConstrained_ProtectedSetterRoundTrips()
{
    var discriminator = AssemblyQualifiedTypeDiscriminator.Instance;
    var stamp = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    var source = new TestCreatedEvent { Id = "e1", Version = 1 };
    source.SetCreated(stamp);

    var row = source.ToTableEntity("p", "r");
    row[SystemColumnNames.TypeName] = discriminator.ToDiscriminator(typeof(TestCreatedEvent));

    var result = row.FromTableEntity<TestMessage>(discriminator);

    Assert.Equal(stamp, result.Created);
}

[Fact]
public void FromTableEntity_BaseConstrained_CtorlessDerivedType_Materializes()
{
    var discriminator = AssemblyQualifiedTypeDiscriminator.Instance;
    var row = new TestCtorlessEvent("payload") { Id = "e1" }.ToTableEntity("p", "r");
    row[SystemColumnNames.TypeName] = discriminator.ToDiscriminator(typeof(TestCtorlessEvent));

    var result = row.FromTableEntity<TestMessage>(discriminator);

    Assert.Equal("payload", Assert.IsType<TestCtorlessEvent>(result).Payload);
}

[Fact]
public void FromTableEntity_BaseConstrained_MissingDiscriminator_Throws()
{
    var row = new TableEntity("p", "r") { ["Id"] = "x" };

    Assert.Throws<InvalidOperationException>(() =>
        row.FromTableEntity<TestMessage>(AssemblyQualifiedTypeDiscriminator.Instance));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~TableEntitySerializerTests"`
Expected: FAIL — no overload of `FromTableEntity` takes an `ITypeDiscriminator`.

- [ ] **Step 3: Extract `Materialize`**

In `src/Unified.Data.Tables/TableEntitySerializer.cs`, add this private helper next to the existing read methods:

```csharp
    // The shared read loop behind all four FromTableEntity overloads. Extracted rather than copied a
    // fourth time: the truncation-marker skip, the system-column skip and the alias pass must stay
    // in lockstep, and three hand-maintained copies had already drifted once.
    private static object Materialize(TableEntity entity, Type type, bool restoreId)
    {
        var meta = TypeMetadataCache.GetMetadata(type);
        var result = meta.Creator();

        foreach (var kv in entity)
        {
            if (TableEntityValue.IsTruncationMarker(kv.Key))
                continue;
            if (SystemColumnNames.IsSystemColumn(kv.Key))
                continue;

            result = SetProperty(result, TableEntityValue.Create(kv.Key, kv.Value));
        }

        result = ApplyColumnAliases(result, entity, meta);

        // A polymorphic row's keys encode an aggregate version or a tick count, not an id — so
        // recomputing Id from them would overwrite the object's real id with "partition|row".
        return restoreId ? RestoreIdFromKeys(result, entity) : result;
    }
```

Then rewrite the two existing overloads to delegate:

```csharp
    /// <summary>Deserialize into a new <typeparamref name="T"/> (requires a public parameterless ctor).</summary>
    public static T FromTableEntity<T>(this TableEntity entity)
        where T : new()
    {
        ArgumentNullException.ThrowIfNull(entity);
        return (T)Materialize(entity, typeof(T), restoreId: true);
    }

    /// <summary>Late-bound deserialize; requires the row to have been written with <c>persistType: true</c>.</summary>
    public static object FromTableEntity(this TableEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.TryGetValue(TypeNameColumnName, out var tn)
            || tn is not string asmQName)
        {
            throw new InvalidOperationException($"Missing '{TypeNameColumnName}' column.");
        }

        var t = Type.GetType(asmQName)
                ?? throw new TypeLoadException($"Type '{asmQName}' not found.");

        return Materialize(entity, t, restoreId: true);
    }
```

> **Check:** `Materialize` uses `meta.Creator()` exactly as the originals did, so the ctor-less fallback is unchanged. If `ApplyColumnAliases`'s first parameter is declared non-nullable, the original `result!` suppression is no longer needed because `meta.Creator()` returns non-null.

- [ ] **Step 4: Add the base-constrained overloads**

Still in `TableEntitySerializer.cs`:

```csharp
    /// <summary>
    /// Late-bound deserialize constrained to a base type: resolves the stored discriminator through
    /// <paramref name="discriminator"/> and returns the TRUE derived instance typed as
    /// <typeparamref name="TBase"/>.
    /// </summary>
    /// <typeparam name="TBase">The base type the row must materialize as.</typeparam>
    /// <param name="entity">The row to read.</param>
    /// <param name="discriminator">The resolver for the stored token.</param>
    /// <returns>The materialized object.</returns>
    /// <exception cref="InvalidOperationException">
    /// The row carries no discriminator, or names a type not assignable to <typeparamref name="TBase"/>.
    /// </exception>
    public static TBase FromTableEntity<TBase>(this TableEntity entity, ITypeDiscriminator discriminator)
        where TBase : class
    {
        if (!entity.TryFromTableEntity<TBase>(discriminator, out var result))
            throw new InvalidOperationException($"Missing '{TypeNameColumnName}' column.");

        return result!;
    }

    /// <summary>
    /// Base-constrained deserialize that tolerates a TYPELESS row.
    /// </summary>
    /// <remarks>
    /// Returns false when the row carries NO discriminator — a deliberate marker row, such as a
    /// two-phase-commit flag carrying only system columns. A discriminator that is PRESENT but
    /// unresolvable or not assignable to <typeparamref name="TBase"/> still throws: "no type was
    /// ever written" and "the wrong type was written" are different failures and must not look alike.
    /// </remarks>
    /// <typeparam name="TBase">The base type the row must materialize as.</typeparam>
    /// <param name="entity">The row to read.</param>
    /// <param name="discriminator">The resolver for the stored token.</param>
    /// <param name="result">The materialized object, or null for a marker row.</param>
    /// <returns>True when the row carried a discriminator and materialized.</returns>
    /// <exception cref="TypeLoadException">The stored token could not be resolved.</exception>
    /// <exception cref="InvalidOperationException">
    /// The resolved type is not assignable to <typeparamref name="TBase"/>.
    /// </exception>
    public static bool TryFromTableEntity<TBase>(
        this TableEntity entity, ITypeDiscriminator discriminator, out TBase? result)
        where TBase : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(discriminator);

        result = null;
        if (!entity.TryGetValue(TypeNameColumnName, out var raw)
            || raw is not string token
            || string.IsNullOrEmpty(token))
        {
            return false;
        }

        var type = discriminator.Resolve(token, typeof(TBase));

        // The gate no configuration can disable. A resolver is not a security boundary: deserializing
        // a type named by stored bytes is a gadget surface, and this is the check that holds even when
        // a custom resolver claims to have made it.
        if (!typeof(TBase).IsAssignableFrom(type))
            throw new InvalidOperationException(PolymorphicMessages.NotAssignable(token, typeof(TBase)));

        result = (TBase)Materialize(entity, type, restoreId: false);
        return true;
    }
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj`
Expected: PASS — including the pre-existing `Fixes053Tests` ctor-less cases and `LargePayloadTests`, which now run through `Materialize`.

- [ ] **Step 6: Commit**

```bash
git add src/Unified.Data.Tables/TableEntitySerializer.cs tests/Unified.Data.Tables.Tests/TableEntitySerializerTests.cs
git commit -m "feat(serializer): add base-constrained polymorphic read with an always-on assignability gate"
```

---

### Task 8: `TableRowSize` and `TableInitializer` extractions

**Files:**
- Create: `src/Unified.Data.Tables/TableRowSize.cs`
- Create: `src/Unified.Data.Tables/TableInitializer.cs`
- Modify: `src/Unified.Data.Tables/TableStorage.cs` (remove `EstimateSize` ~line 585; replace the `tableInit`/`initLock`/`EnsureTableAsync`/`EnsureTableSlowAsync` block ~lines 50–135)
- Test: `tests/Unified.Data.Tables.Tests/TableRowSizeTests.cs`

**Interfaces:**
- Produces: `internal static class TableRowSize` with `internal static long Estimate(TableEntity row)`; `internal sealed class TableInitializer` with `TableInitializer(TableClient client)` and `Task EnsureAsync(CancellationToken ct)`. Tasks 9–11 use both.

> **This task must not change behaviour.** Both members move verbatim. The existing `PagingTests`, `LargePayloadTests` and batch tests are the regression net — if any of them change result, the extraction is wrong.
>
> `InternalsVisibleTo` for `Unified.Data.Tables.Tests` already exists on this project, so `internal` members are testable. Do **not** add `InternalsVisibleTo` for `Unified.Data.Tables.InMemory`.

- [ ] **Step 1: Write the failing test**

Create `tests/Unified.Data.Tables.Tests/TableRowSizeTests.cs`:

```csharp
using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the row-size estimate now that two batch planners share it. The numbers are Azure's own
/// accounting (88 B per entity, 8 B per property) and are load-bearing: they decide how many rows a
/// transaction carries.
/// </summary>
public class TableRowSizeTests
{
    [Fact]
    public void Estimate_EmptyRow_IsEntityOverheadOnly()
    {
        Assert.Equal(88L, TableRowSize.Estimate(new TableEntity()));
    }

    [Fact]
    public void Estimate_StringColumn_CountsTwoBytesPerChar_ForNameAndValue()
    {
        var row = new TableEntity { ["Ab"] = "cde" };

        // 88 entity + 8 per-property + (2 name chars * 2) + (3 value chars * 2) = 88 + 8 + 4 + 6
        Assert.Equal(106L, TableRowSize.Estimate(row));
    }

    [Fact]
    public void Estimate_BinaryColumn_CountsRawLength()
    {
        var row = new TableEntity { ["B"] = new byte[10] };

        // 88 + 8 + (1 name char * 2) + 10 bytes
        Assert.Equal(108L, TableRowSize.Estimate(row));
    }

    [Fact]
    public void Estimate_ScalarColumn_CountsEightBytes()
    {
        var row = new TableEntity { ["N"] = 42 };

        // 88 + 8 + 2 + 8
        Assert.Equal(106L, TableRowSize.Estimate(row));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~TableRowSizeTests"`
Expected: FAIL — `The type or namespace name 'TableRowSize' could not be found`.

- [ ] **Step 3: Create `TableRowSize`**

Create `src/Unified.Data.Tables/TableRowSize.cs`, moving the body of `TableStorage<T>.EstimateSize` verbatim:

```csharp
using Azure.Data.Tables;

namespace Unified.Data.Tables;

// Shared by TableStorage<T> and PolymorphicTableStorage<TBase>. Extracted rather than duplicated
// because the two batch planners must measure identically: a divergence would let one store send a
// transaction the other considers oversized, and the failure surfaces as an HTTP 413 partway
// through a bulk write that has already committed earlier chunks.
internal static class TableRowSize
{
    /// <summary>
    /// Serialized size of one row, for transaction planning. Binary and string columns dominate; the
    /// fixed per-property and per-entity overhead is approximated rather than computed exactly,
    /// because the budget already sits well under the service limit to absorb it.
    /// </summary>
    internal static long Estimate(TableEntity row)
    {
        // Azure's own accounting: ~88 B of entity overhead plus 8 B per property, before values.
        var bytes = 88L + (row.Count * 8L);
        foreach (var key in row.Keys)
        {
            bytes += key.Length * 2L;
            bytes += row[key] switch
            {
                byte[] binary => binary.Length,
                BinaryData binary => binary.ToMemory().Length,
                string text => text.Length * 2L,
                _ => 8L,
            };
        }

        return bytes;
    }
}
```

- [ ] **Step 4: Create `TableInitializer`**

Create `src/Unified.Data.Tables/TableInitializer.cs`, moving the coalesced-lazy-create logic verbatim out of `TableStorage<T>`:

```csharp
using Azure.Data.Tables;

namespace Unified.Data.Tables;

// Coalesced lazy CreateIfNotExists, shared by TableStorage<T> and PolymorphicTableStorage<TBase>.
// Three properties make this subtle enough that two hand-maintained copies would drift: no network
// I/O at construction/DI-resolve time; ONE create per store shared by all concurrent callers; and a
// FAILED attempt is forgotten so the next call retries instead of poisoning the store for the
// process lifetime.
internal sealed class TableInitializer(TableClient client)
{
    private readonly object initLock = new();
    private Task? tableInit;

    internal Task EnsureAsync(CancellationToken ct)
    {
        var existing = Volatile.Read(ref tableInit);
        return existing is { IsCompletedSuccessfully: true } ? Task.CompletedTask : EnsureSlowAsync(ct);
    }

    private async Task EnsureSlowAsync(CancellationToken ct)
    {
        Task pending;
        lock (initLock)
        {
            // Reuse an in-flight or succeeded attempt; start fresh after a failed/canceled one.
            pending = tableInit is { IsFaulted: false, IsCanceled: false }
                ? tableInit
                // The shared operation deliberately ignores the first caller's token — a canceled
                // caller must not cancel (and thereby poison) everyone else's init.
                : tableInit = client.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);
        }

        try
        {
            await pending.WaitAsync(ct);
        }
        catch
        {
            lock (initLock)
            {
                if (ReferenceEquals(tableInit, pending) && pending is { IsCompletedSuccessfully: false })
                    tableInit = null;
            }

            throw;
        }
    }
}
```

> **Important:** copy the `catch` block from the existing `EnsureTableSlowAsync` in `TableStorage.cs` verbatim rather than trusting the sketch above — read lines ~117–140 and reproduce the real forget-on-failure logic exactly.

- [ ] **Step 5: Rewire `TableStorage<T>`**

In `src/Unified.Data.Tables/TableStorage.cs`:
1. Delete the `tableInit` field, the `initLock` field and the comment block above them; add `private readonly TableInitializer tableInitializer;`.
2. In the primary constructor, after `client = serviceClient.GetTableClient(ResolveTableName(opts));`, add `tableInitializer = new TableInitializer(client);`.
3. Replace `private Task EnsureTableAsync(CancellationToken ct) => ...` and the whole `EnsureTableSlowAsync` method with `private Task EnsureTableAsync(CancellationToken ct) => tableInitializer.EnsureAsync(ct);`.
4. Delete `private static long EstimateSize(TableEntity row)` and replace its two call sites (`EstimateSize(r.Row)` around line 644, plus any other) with `TableRowSize.Estimate(r.Row)`.

Find every call site first: `grep -n "EstimateSize" src/Unified.Data.Tables/TableStorage.cs`.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj`
Expected: PASS with no change in count. This is a pure refactor — any behavioural difference is a bug in the extraction.

- [ ] **Step 7: Commit**

```bash
git add src/Unified.Data.Tables/TableRowSize.cs src/Unified.Data.Tables/TableInitializer.cs \
        src/Unified.Data.Tables/TableStorage.cs tests/Unified.Data.Tables.Tests/TableRowSizeTests.cs
git commit -m "refactor: extract TableRowSize and TableInitializer for sharing with the polymorphic store"
```

---

### Task 9: `PolymorphicTableStorage<TBase>` — construction, insert, upsert, get, delete

**Files:**
- Create: `src/Unified.Data.Tables/PolymorphicTableStorage.cs`
- Modify: `src/Unified.Data.Tables/UnifiedTableStorageOptions.cs`
- Create: `tests/Unified.Data.Tables.Tests/TestSupport/PolymorphicHarness.cs`
- Create: `tests/Unified.Data.Tables.Tests/PolymorphicStorageTests.cs`

**Interfaces:**
- Consumes: `IPolymorphicStorage<TBase>`, `PolymorphicWrite<TBase>`, `PolymorphicEntry<TBase>`, `TableKey`, `SystemColumnNames`, `PolymorphicMessages` (Tasks 1–3); `ITypeDiscriminator`, `AssemblyQualifiedTypeDiscriminator` (Task 5); `TryFromTableEntity<TBase>` (Task 7); `TableInitializer` (Task 8).
- Produces:
  - `public sealed class PolymorphicTableStorage<TBase> : IPolymorphicStorage<TBase> where TBase : class`, constructor `(TableServiceClient serviceClient, string tableName, ILogger<PolymorphicTableStorage<TBase>> logger, UnifiedTableStorageOptions? options = null)`.
  - Private helpers `TableEntity ToRow(PolymorphicWrite<TBase>)`, `PolymorphicEntry<TBase> ToEntry(TableEntity)`, `static void ValidateKey(TableKey)`, `static void ValidateSystemColumn(string)` — Tasks 10 and 11 use all four.
  - `UnifiedTableStorageOptions.TypeDiscriminator { get; set; }` and `internal ITypeDiscriminator ResolveTypeDiscriminator()`.
  - `PolymorphicHarness<TBase>` with `Service`, `Table`, `Store`, `LastWrittenEntity`, `LastQueryFilter`, and `SetupAdd/SetupUpsert/SetupGet/SetupNotFound/SetupDelete`.

> **Table name is explicit**, not `typeof(TBase).Name`: a base type says nothing about which of several tables holds it, and `IEvent` addresses two different tables in the driving consumer.

- [ ] **Step 1: Write the failing test**

Create `tests/Unified.Data.Tables.Tests/PolymorphicStorageTests.cs`:

```csharp
using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the Azure-side polymorphic contract: the discriminator records the RUNTIME type, the read
/// gives back the true derived instance, and keys are used exactly as supplied.
/// </summary>
public class PolymorphicStorageTests
{
    [Fact]
    public async Task InsertAsync_DerivedInstance_WritesDiscriminatorForRuntimeType()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        await harness.Store.InsertAsync(
            new TableKey("agg-1", "000000001"),
            new TestCreatedEvent { Id = "e1", Version = 7 },
            TestContext.Current.CancellationToken);

        var written = harness.LastWrittenEntity!;
        Assert.Equal(
            AssemblyQualifiedTypeDiscriminator.Instance.ToDiscriminator(typeof(TestCreatedEvent)),
            written[SystemColumnNames.TypeName]);
    }

    [Fact]
    public async Task InsertAsync_ReturnsEntry_WithTrueDerivedInstance()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        var entry = await harness.Store.InsertAsync(
            new TableKey("agg-1", "000000001"),
            new TestCreatedEvent { Id = "e1", Version = 7 },
            TestContext.Current.CancellationToken);

        Assert.Equal(7, Assert.IsType<TestCreatedEvent>(entry.Item).Version);
    }

    [Fact]
    public async Task InsertAsync_Keys_AreUsedVerbatim_UnderDefaultNormalization()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        await harness.Store.InsertAsync(
            new TableKey("Agg 1", "MiXeD Case"),
            new TestCommand { Id = "c1" },
            TestContext.Current.CancellationToken);

        var written = harness.LastWrittenEntity!;
        Assert.Equal("Agg 1", written.PartitionKey);
        Assert.Equal("MiXeD Case", written.RowKey);
    }

    [Fact]
    public async Task InsertAsync_ExistingKey_ThrowsDuplicateKeyException()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.Table
            .AddEntityAsync(Arg.Any<TableEntity>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new RequestFailedException(409, "conflict"));

        await Assert.ThrowsAsync<DuplicateKeyException>(() => harness.Store.InsertAsync(
            new TableKey("p", "r"), new TestCommand { Id = "c1" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InsertAsync_MarkerRow_WritesNoDiscriminator()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        await harness.Store.InsertMarkerAsync(
            new TableKey("t1", "FlagEntity"),
            new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsCommitted"] = false },
            TestContext.Current.CancellationToken);

        var written = harness.LastWrittenEntity!;
        Assert.False(written.ContainsKey(SystemColumnNames.TypeName));
        Assert.False((bool)written["_IsCommitted"]);
    }

    [Fact]
    public async Task InsertAsync_UnprefixedSystemColumn_Throws()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        var write = new PolymorphicWrite<TestMessage>(
            new TableKey("p", "r"),
            new TestCommand { Id = "c1" },
            new Dictionary<string, object>(StringComparer.Ordinal) { ["IsPublished"] = true });

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Store.InsertAsync(write, TestContext.Current.CancellationToken));
        Assert.Contains("not a system column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsertAsync_TypeNameAsSystemColumn_Throws()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        var write = new PolymorphicWrite<TestMessage>(
            new TableKey("p", "r"),
            new TestCommand { Id = "c1" },
            new Dictionary<string, object>(StringComparer.Ordinal) { [SystemColumnNames.TypeName] = "x" });

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Store.InsertAsync(write, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("", "r")]
    [InlineData("p", "")]
    public async Task InsertAsync_EmptyKey_Throws(string partition, string row)
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupAdd();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.InsertAsync(
            new TableKey(partition, row), new TestCommand { Id = "c1" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsync_MissingRow_ReturnsNull()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupNotFound();

        Assert.Null(await harness.Store.GetAsync(new TableKey("p", "r"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsync_DerivedRow_ReturnsTrueDerivedInstance()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        var row = new TestArchivedEvent { Id = "e2", Reason = "obsolete" }.ToTableEntity("agg-1", "000000002");
        row[SystemColumnNames.TypeName] =
            AssemblyQualifiedTypeDiscriminator.Instance.ToDiscriminator(typeof(TestArchivedEvent));
        harness.SetupGet(row);

        var entry = await harness.Store.GetAsync(
            new TableKey("agg-1", "000000002"), TestContext.Current.CancellationToken);

        Assert.Equal("obsolete", Assert.IsType<TestArchivedEvent>(entry!.Item).Reason);
    }

    [Fact]
    public async Task GetAsync_MarkerRow_ReturnsEntryWithNullItemAndColumnsIntact()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupGet(new TableEntity("t1", "FlagEntity") { ["_IsCommitted"] = true });

        var entry = await harness.Store.GetAsync(
            new TableKey("t1", "FlagEntity"), TestContext.Current.CancellationToken);

        Assert.Null(entry!.Item);
        Assert.Null(entry.Discriminator);
        Assert.True(entry.Column<bool>("_IsCommitted"));
    }

    [Fact]
    public async Task UpsertAsync_SendsReplaceMode()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupUpsert();

        await harness.Store.UpsertAsync(
            new TableKey("p", "r"), new TestCommand { Id = "c1" }, TestContext.Current.CancellationToken);

        await harness.Table.Received(1).UpsertEntityAsync(
            Arg.Any<TableEntity>(), TableUpdateMode.Replace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_MissingRow_IsANoOp()
    {
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.Table
            .DeleteEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new RequestFailedException(404, "not found"));

        await harness.Store.DeleteAsync(new TableKey("p", "r"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Constructor_ResolvesTheGivenTableName()
    {
        using var harness = new PolymorphicHarness<TestMessage>(tableName: "StateEventStore");

        harness.Service.Received(1).GetTableClient("StateEventStore");
    }
}
```

Add `using NSubstitute.ExceptionExtensions;` at the top for `ThrowsAsyncForAnyArgs`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~PolymorphicStorageTests"`
Expected: FAIL — `PolymorphicHarness<>` and `PolymorphicTableStorage<>` do not exist.

- [ ] **Step 3: Add the options seam**

In `src/Unified.Data.Tables/UnifiedTableStorageOptions.cs`, add (mirroring the existing `ResolveCachePolicy` shape):

```csharp
    /// <summary>
    /// How <see cref="IPolymorphicStorage{TBase}"/> maps types to the stored discriminator. Null
    /// selects <see cref="AssemblyQualifiedTypeDiscriminator"/>, which writes exactly what
    /// <c>persistType: true</c> has always written — so an existing table is readable with no
    /// migration. Prefer a <see cref="TypeDiscriminatorMap"/> for new tables; see
    /// <see cref="ITypeDiscriminator"/> for why.
    /// </summary>
    public ITypeDiscriminator? TypeDiscriminator { get; set; }

    // Null means "the legacy-compatible default", not "no discriminator" — a polymorphic store
    // without one could not read or write a type at all.
    internal ITypeDiscriminator ResolveTypeDiscriminator() =>
        TypeDiscriminator ?? AssemblyQualifiedTypeDiscriminator.Instance;
```

- [ ] **Step 4: Write the harness**

Create `tests/Unified.Data.Tables.Tests/TestSupport/PolymorphicHarness.cs`:

```csharp
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Unified.Data.Tables.Tests.TestSupport;

/// <summary>
/// Builds a <see cref="PolymorphicTableStorage{TBase}"/> over NSubstitute mocks of the Azure Tables
/// SDK. Cannot reuse <see cref="StorageHarness{T}"/>: its <c>where T : class, IEntity, new()</c> is
/// the exact constraint the polymorphic contract exists to escape.
/// </summary>
public sealed class PolymorphicHarness<TBase> : IDisposable
    where TBase : class
{
    public TableServiceClient Service { get; }

    public TableClient Table { get; }

    public PolymorphicTableStorage<TBase> Store { get; }

    /// <summary>The last entity handed to Add/Upsert/Update — the write-side assertion hook.</summary>
    public TableEntity? LastWrittenEntity { get; private set; }

    /// <summary>The last OData filter the store emitted — the read-side assertion hook.</summary>
    public string? LastQueryFilter { get; private set; }

    public PolymorphicHarness(string tableName = "TestTable", UnifiedTableStorageOptions? options = null)
    {
        Service = Substitute.For<TableServiceClient>();
        Table = Substitute.For<TableClient>();
        Service.GetTableClient(tableName).Returns(Table);

        // Table creation is lazy (the first operation awaits it); an unmocked substitute returns a
        // null Task and NREs on await, so every harness store gets a completed init by default.
        Table.CreateIfNotExistsAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response<Azure.Data.Tables.Models.TableItem>>(null!));

        Store = new PolymorphicTableStorage<TBase>(
            Service, tableName, NullLogger<PolymorphicTableStorage<TBase>>.Instance, options);
    }

    public void SetupAdd() =>
        Table.AddEntityAsync(Arg.Do<TableEntity>(e => LastWrittenEntity = e), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response>(new FakeResponse()));

    public void SetupUpsert() =>
        Table.UpsertEntityAsync(
                 Arg.Do<TableEntity>(e => LastWrittenEntity = e),
                 Arg.Any<TableUpdateMode>(),
                 Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response>(new FakeResponse()));

    public void SetupMerge() =>
        Table.UpdateEntityAsync(
                 Arg.Do<TableEntity>(e => LastWrittenEntity = e),
                 Arg.Any<ETag>(),
                 Arg.Any<TableUpdateMode>(),
                 Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response>(new FakeResponse()));

    public void SetupGet(TableEntity row) =>
        Table.GetEntityIfExistsAsync<TableEntity>(
                 row.PartitionKey, row.RowKey, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(Mocks.Found(row)));

    public void SetupNotFound() =>
        Table.GetEntityIfExistsAsync<TableEntity>(
                 Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(Mocks.NotFound()));

    public void SetupDelete() =>
        Table.DeleteEntityAsync(
                 Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response>(new FakeResponse()));

    /// <summary>Arranges a single-page query result and captures the emitted filter.</summary>
    public void SetupQuery(params TableEntity[] rows) =>
        Table.QueryAsync<TableEntity>(
                 Arg.Do<string>(f => LastQueryFilter = f),
                 Arg.Any<int?>(),
                 Arg.Any<IEnumerable<string>>(),
                 Arg.Any<CancellationToken>())
             .Returns(Mocks.Pageable(rows));

    /// <summary>Arranges a MULTI-page query result, to prove continuation tokens are followed.</summary>
    public void SetupPagedQuery(TableEntity[] firstPage, TableEntity[] secondPage)
    {
        var pages = new[]
        {
            Page<TableEntity>.FromValues(firstPage, continuationToken: "next", new FakeResponse()),
            Page<TableEntity>.FromValues(secondPage, continuationToken: null, new FakeResponse()),
        };

        Table.QueryAsync<TableEntity>(
                 Arg.Do<string>(f => LastQueryFilter = f),
                 Arg.Any<int?>(),
                 Arg.Any<IEnumerable<string>>(),
                 Arg.Any<CancellationToken>())
             .Returns(AsyncPageable<TableEntity>.FromPages(pages));
    }

    public void Dispose()
    {
        // Nothing owned; the method exists so tests can use `using var` uniformly with StorageHarness.
    }
}
```

- [ ] **Step 5: Write the store**

Create `src/Unified.Data.Tables/PolymorphicTableStorage.cs`:

```csharp
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace Unified.Data.Tables;

/// <summary>
/// Azure Table Storage implementation of <see cref="IPolymorphicStorage{TBase}"/>: many concrete
/// types in one table, discriminated by <see cref="SystemColumnNames.TypeName"/>.
/// </summary>
/// <remarks>
/// The table name is supplied explicitly rather than derived from <typeparamref name="TBase"/>: a
/// base type says nothing about which of several tables holds it, and one base commonly addresses
/// more than one table.
/// <para>
/// There is no cache. <c>TableStorage&lt;T&gt;</c> keys its cache on <c>typeof(T).FullName</c>, so
/// two stores over one table would never invalidate each other, and its snapshot round-trips
/// through the base-typed read — silently downcasting a derived instance and dropping its data.
/// Rather than mitigate three coupled hazards, this store has none, which also suits the
/// append-only fact tables it is designed for.
/// </para>
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
public sealed class PolymorphicTableStorage<TBase> : IPolymorphicStorage<TBase>
    where TBase : class
{
    private static readonly string[] ReservedCells = ["PartitionKey", "RowKey", "Timestamp", "odata.etag"];

    private readonly TableClient client;
    private readonly TableInitializer initializer;
    private readonly ITypeDiscriminator discriminator;
    private readonly ILogger logger;
    private readonly string tableName;

    /// <summary>Creates a store over one named table.</summary>
    /// <param name="serviceClient">The Azure Tables service client.</param>
    /// <param name="tableName">The table this store owns.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Options; null selects the defaults.</param>
    public PolymorphicTableStorage(
        TableServiceClient serviceClient,
        string tableName,
        ILogger<PolymorphicTableStorage<TBase>> logger,
        UnifiedTableStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        this.tableName = tableName;
        this.logger = logger;
        discriminator = (options ?? new UnifiedTableStorageOptions()).ResolveTypeDiscriminator();
        client = serviceClient.GetTableClient(tableName);
        initializer = new TableInitializer(client);
    }

    /// <inheritdoc />
    public Task EnsureCreatedAsync(CancellationToken ct = default) => initializer.EnsureAsync(ct);

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>> InsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = ToRow(write);
        await initializer.EnsureAsync(ct);

        try
        {
            await client.AddEntityAsync(row, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw new DuplicateKeyException(tableName, write.Key.ToId(), ex);
        }

        return ToEntry(row);
    }

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>> UpsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = ToRow(write);
        await initializer.EnsureAsync(ct);
        await client.UpsertEntityAsync(row, TableUpdateMode.Replace, ct);
        return ToEntry(row);
    }

    /// <inheritdoc />
    public async Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default)
    {
        ValidateKey(key);
        await initializer.EnsureAsync(ct);

        var response = await client.GetEntityIfExistsAsync<TableEntity>(
            key.PartitionKey, key.RowKey, cancellationToken: ct);

        return response.HasValue ? ToEntry(response.Value!) : null;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(TableKey key, CancellationToken ct = default)
    {
        ValidateKey(key);
        await initializer.EnsureAsync(ct);

        try
        {
            await client.DeleteEntityAsync(key.PartitionKey, key.RowKey, ETag.All, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Idempotent by contract: deleting what is already gone is the caller's desired state.
            logger.LogDebug("Delete of missing row {Key} in {Table} treated as a no-op.", key, tableName);
        }
    }

    // Build the row: serialize the object with persistType FALSE, then stamp the discriminator
    // ourselves. Composing this way rather than extending ToTableEntity is what gives the
    // ITypeDiscriminator seam for free, with zero change to the existing write path.
    private TableEntity ToRow(PolymorphicWrite<TBase> write)
    {
        ValidateKey(write.Key);

        var row = write.Item is null
            ? new TableEntity(write.Key.PartitionKey, write.Key.RowKey)
            : write.Item.ToTableEntity(write.Key.PartitionKey, write.Key.RowKey);

        if (write.Item is not null)
            row[SystemColumnNames.TypeName] = discriminator.ToDiscriminator(write.Item.GetType());

        if (write.SystemColumns is null)
            return row;

        foreach (var column in write.SystemColumns)
        {
            ValidateSystemColumn(column.Key);
            row[column.Key] = column.Value;
        }

        return row;
    }

    private PolymorphicEntry<TBase> ToEntry(TableEntity row)
    {
        string? storedDiscriminator = null;
        if (row.TryGetValue(SystemColumnNames.TypeName, out var raw)
            && raw is string token
            && token.Length > 0)
        {
            storedDiscriminator = token;
        }

        // Throws for a discriminator that is present but broken; returns false only for a row that
        // never carried one.
        var item = row.TryFromTableEntity<TBase>(discriminator, out var materialized) ? materialized : null;

        var columns = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var cell in row)
        {
            if (Array.IndexOf(ReservedCells, cell.Key) >= 0)
                continue;

            columns[cell.Key] = cell.Value;
        }

        return new PolymorphicEntry<TBase>(
            new TableKey(row.PartitionKey, row.RowKey),
            item,
            storedDiscriminator,
            row.ETag == default ? null : row.ETag.ToString(),
            row.Timestamp,
            columns);
    }

    private static void ValidateKey(TableKey key)
    {
        if (string.IsNullOrEmpty(key.PartitionKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.PartitionKey)), nameof(key));
        if (string.IsNullOrEmpty(key.RowKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.RowKey)), nameof(key));
    }

    private static void ValidateSystemColumn(string columnName)
    {
        if (!SystemColumnNames.IsSystemColumn(columnName))
            throw new ArgumentException(PolymorphicMessages.NotSystemColumn(columnName), nameof(columnName));

        if (string.Equals(columnName, SystemColumnNames.TypeName, StringComparison.Ordinal))
            throw new ArgumentException(PolymorphicMessages.TypeNameNotMergeable(), nameof(columnName));
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~PolymorphicStorageTests"`
Expected: PASS, 15 tests. `InsertBatchAsync`, `MergeColumnsAsync`, `QueryAsync`, `QueryStreamAsync`, `DeletePartitionAsync` and `CountAsync` are still unimplemented — leave them as `throw new NotImplementedException();` stubs so the class satisfies the interface, and delete each stub as Tasks 10–11 fill it in.

- [ ] **Step 7: Commit**

```bash
git add src/Unified.Data.Tables/PolymorphicTableStorage.cs \
        src/Unified.Data.Tables/UnifiedTableStorageOptions.cs \
        tests/Unified.Data.Tables.Tests/TestSupport/PolymorphicHarness.cs \
        tests/Unified.Data.Tables.Tests/PolymorphicStorageTests.cs
git commit -m "feat: add PolymorphicTableStorage single-row operations"
```

---

### Task 10: `InsertBatchAsync` and `MergeColumnsAsync`

**Files:**
- Modify: `src/Unified.Data.Tables/PolymorphicTableStorage.cs`
- Test: `tests/Unified.Data.Tables.Tests/PolymorphicStorageTests.cs` (extend)

**Interfaces:**
- Consumes: `ToRow`, `ValidateKey`, `ValidateSystemColumn` (Task 9); `BatchPlanner.Plan`, `BatchRange` (existing); `TableRowSize.Estimate` (Task 8).
- Produces: the two method bodies. No new public API.

> **The capability that matters:** a batch may mix concrete types *and* a typeless marker row in one Entity Group Transaction — that is how a two-phase-commit flag stays atomic with the events it guards. Azure requires an EGT to be single-partition, so writes are grouped by partition first, then chunked by `BatchPlanner` on count **and** bytes.

- [ ] **Step 1: Write the failing test**

Append to `tests/Unified.Data.Tables.Tests/PolymorphicStorageTests.cs`:

```csharp
[Fact]
public async Task InsertBatchAsync_HeterogeneousTypesPlusMarker_OneTransactionPerPartition()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupTransaction();

    var written = await harness.Store.InsertBatchAsync(
        [
            new PolymorphicWrite<TestMessage>(new TableKey("t1", "001"), new TestCreatedEvent { Id = "e1" }),
            new PolymorphicWrite<TestMessage>(new TableKey("t1", "002"), new TestArchivedEvent { Id = "e2" }),
            new PolymorphicWrite<TestMessage>(new TableKey("t1", "003"), new TestIntegrationEvent { Id = "e3" }),
            PolymorphicWrite<TestMessage>.Marker(
                new TableKey("t1", "FlagEntity"),
                new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsCommitted"] = false }),
        ],
        TestContext.Current.CancellationToken);

    Assert.Equal(4, written);
    Assert.Single(harness.Transactions);

    var actions = harness.Transactions[0];
    Assert.Equal(4, actions.Count);
    Assert.Equal(3, actions.Count(a => a.Entity.ContainsKey(SystemColumnNames.TypeName)));
    Assert.Single(actions, a => !a.Entity.ContainsKey(SystemColumnNames.TypeName));
    Assert.All(actions, a => Assert.Equal(TableTransactionActionType.Add, a.ActionType));
}

[Fact]
public async Task InsertBatchAsync_MultiplePartitions_GroupsIntoSeparateTransactions()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupTransaction();

    await harness.Store.InsertBatchAsync(
        [
            new PolymorphicWrite<TestMessage>(new TableKey("agg-1", "001"), new TestCreatedEvent { Id = "e1" }),
            new PolymorphicWrite<TestMessage>(new TableKey("agg-2", "001"), new TestCreatedEvent { Id = "e2" }),
        ],
        TestContext.Current.CancellationToken);

    Assert.Equal(2, harness.Transactions.Count);
}

[Fact]
public async Task InsertBatchAsync_EmptyCollection_WritesNothing()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupTransaction();

    Assert.Equal(0, await harness.Store.InsertBatchAsync([], TestContext.Current.CancellationToken));
    Assert.Empty(harness.Transactions);
}

[Fact]
public async Task InsertBatchAsync_OverHundredRows_SplitsOnTheCountCap()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupTransaction();

    var writes = Enumerable.Range(0, 150)
        .Select(i => new PolymorphicWrite<TestMessage>(
            new TableKey("t1", i.ToString("D5", CultureInfo.InvariantCulture)),
            new TestCreatedEvent { Id = $"e{i}" }))
        .ToArray();

    Assert.Equal(150, await harness.Store.InsertBatchAsync(writes, TestContext.Current.CancellationToken));
    Assert.Equal(2, harness.Transactions.Count);
    Assert.Equal(100, harness.Transactions[0].Count);
    Assert.Equal(50, harness.Transactions[1].Count);
}

[Fact]
public async Task MergeColumnsAsync_SentinelOnly_SendsMergeWithWildcardETagAndNoPriorRead()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupMerge();

    await harness.Store.MergeColumnsAsync(
        new TableKey("agg-1", "000000001"),
        new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsPublished"] = true },
        TestContext.Current.CancellationToken);

    await harness.Table.Received(1).UpdateEntityAsync(
        Arg.Any<TableEntity>(), ETag.All, TableUpdateMode.Merge, Arg.Any<CancellationToken>());
    await harness.Table.DidNotReceiveWithAnyArgs()
        .GetEntityIfExistsAsync<TableEntity>(default!, default!, default, default);

    var sent = harness.LastWrittenEntity!;
    Assert.True((bool)sent["_IsPublished"]);
    Assert.False(sent.ContainsKey(SystemColumnNames.TypeName));
}

[Fact]
public async Task MergeColumnsAsync_TypeNameColumn_Throws()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupMerge();

    await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.MergeColumnsAsync(
        new TableKey("p", "r"),
        new Dictionary<string, object>(StringComparer.Ordinal) { [SystemColumnNames.TypeName] = "x" },
        TestContext.Current.CancellationToken));
}

[Fact]
public async Task MergeColumnsAsync_UnprefixedColumn_Throws()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupMerge();

    await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.MergeColumnsAsync(
        new TableKey("p", "r"),
        new Dictionary<string, object>(StringComparer.Ordinal) { ["IsPublished"] = true },
        TestContext.Current.CancellationToken));
}

[Fact]
public async Task MergeColumnsAsync_EmptyColumns_Throws()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupMerge();

    await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.MergeColumnsAsync(
        new TableKey("p", "r"),
        new Dictionary<string, object>(StringComparer.Ordinal),
        TestContext.Current.CancellationToken));
}
```

Add `using System.Globalization;` at the top of the test file.

- [ ] **Step 2: Add the transaction hook to the harness**

Append to `PolymorphicHarness<TBase>` in `tests/Unified.Data.Tables.Tests/TestSupport/PolymorphicHarness.cs`:

```csharp
    /// <summary>Every transaction the store submitted, in order — the batch assertion hook.</summary>
    public List<IReadOnlyList<TableTransactionAction>> Transactions { get; } = [];

    public void SetupTransaction() =>
        Table.SubmitTransactionAsync(
                 Arg.Do<IEnumerable<TableTransactionAction>>(a => Transactions.Add(a.ToList())),
                 Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<Response<IReadOnlyList<Response>>>(null!));
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~PolymorphicStorageTests"`
Expected: FAIL with `NotImplementedException` from the Task 9 stubs.

- [ ] **Step 4: Implement both methods**

Replace the `InsertBatchAsync` and `MergeColumnsAsync` stubs in `src/Unified.Data.Tables/PolymorphicTableStorage.cs`:

```csharp
    /// <inheritdoc />
    public async Task<int> InsertBatchAsync(
        IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return 0;

        // Build every row first, so a validation failure costs nothing rather than surfacing after
        // earlier partitions have already committed.
        var rows = new List<TableEntity>(writes.Count);
        foreach (var write in writes)
            rows.Add(ToRow(write));

        await initializer.EnsureAsync(ct);

        var written = 0;

        // Azure requires an Entity Group Transaction to be single-partition, so partition first and
        // chunk within each group.
        foreach (var group in rows.GroupBy(r => r.PartitionKey, StringComparer.Ordinal))
        {
            var groupRows = group.ToList();
            var plan = BatchPlanner.Plan([.. groupRows.Select(TableRowSize.Estimate)]);

            foreach (var range in plan)
            {
                var actions = new List<TableTransactionAction>(range.Count);
                for (var i = range.Start; i < range.Start + range.Count; i++)
                    actions.Add(new TableTransactionAction(TableTransactionActionType.Add, groupRows[i]));

                await client.SubmitTransactionAsync(actions, ct);
                written += actions.Count;
            }
        }

        return written;
    }

    /// <inheritdoc />
    public async Task MergeColumnsAsync(
        TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "A merge with no columns would be a network round trip that changes nothing.",
                nameof(columns));
        }

        var patch = new TableEntity(key.PartitionKey, key.RowKey);
        foreach (var column in columns)
        {
            ValidateSystemColumn(column.Key);
            patch[column.Key] = column.Value;
        }

        await initializer.EnsureAsync(ct);

        // Wildcard ETag and Merge mode: blind, unconditional, and no prior read. A sentinel flip is
        // idempotent and order-independent, so optimistic concurrency here would only manufacture
        // conflicts the caller would have to retry through.
        await client.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct);
    }
```

Add `using System.Linq;` if `ImplicitUsings` does not already cover it (it does — `Microsoft.NET.Sdk` implicit usings include `System.Linq`).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~PolymorphicStorageTests"`
Expected: PASS, 23 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Unified.Data.Tables/PolymorphicTableStorage.cs \
        tests/Unified.Data.Tables.Tests/TestSupport/PolymorphicHarness.cs \
        tests/Unified.Data.Tables.Tests/PolymorphicStorageTests.cs
git commit -m "feat: add heterogeneous transactional batch and blind sentinel merge"
```

---

### Task 11: Queries, streaming, count, delete-partition

**Files:**
- Modify: `src/Unified.Data.Tables/PolymorphicTableStorage.cs`
- Test: `tests/Unified.Data.Tables.Tests/PolymorphicStorageTests.cs` (extend)

**Interfaces:**
- Consumes: `ToEntry`, `ValidateKey` (Task 9); `BatchPlanner.Plan`, `TableRowSize.Estimate` (Task 8).
- Produces: the four remaining method bodies, plus private `static string? PartitionFilter(string?)` and `static readonly string[] KeysOnly`. Task 12 mirrors this behaviour in the fake.

> **The defect this closes:** a hand-written `ExecuteQuerySegmentedAsync(query, null)` reads one segment and silently truncates. `QueryStreamAsync` iterates `AsyncPageable<T>`, which follows continuation tokens internally, so the caller cannot omit the loop.

- [ ] **Step 1: Write the failing test**

Append to `tests/Unified.Data.Tables.Tests/PolymorphicStorageTests.cs`:

```csharp
[Fact]
public async Task QueryStreamAsync_MultipleServerPages_YieldsEveryRow()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupPagedQuery(
        [TypedRow("agg-1", "001", new TestCreatedEvent { Id = "e1" })],
        [TypedRow("agg-1", "002", new TestArchivedEvent { Id = "e2" })]);

    var seen = new List<PolymorphicEntry<TestMessage>>();
    await foreach (var entry in harness.Store.QueryStreamAsync(
                       "agg-1", ct: TestContext.Current.CancellationToken))
    {
        seen.Add(entry);
    }

    Assert.Equal(2, seen.Count);
}

[Fact]
public async Task QueryAsync_Partition_EmitsAPartitionKeyFilter()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupQuery(TypedRow("agg-1", "001", new TestCreatedEvent { Id = "e1" }));

    await harness.Store.QueryAsync("agg-1", ct: TestContext.Current.CancellationToken);

    Assert.Equal("PartitionKey eq 'agg-1'", harness.LastQueryFilter);
}

[Fact]
public async Task QueryAsync_NoPartition_EmitsNoFilter()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupQuery(TypedRow("agg-1", "001", new TestCreatedEvent { Id = "e1" }));

    await harness.Store.QueryAsync(ct: TestContext.Current.CancellationToken);

    Assert.Null(harness.LastQueryFilter);
}

[Fact]
public async Task QueryAsync_PartitionWithApostrophe_IsEscaped()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupQuery();

    await harness.Store.QueryAsync("O'Brien", ct: TestContext.Current.CancellationToken);

    Assert.Equal("PartitionKey eq 'O''Brien'", harness.LastQueryFilter);
}

[Fact]
public async Task QueryAsync_MixedTypePartition_RuntimeSubtypeFilteringWorks()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupQuery(
        TypedRow("t1", "001", new TestCreatedEvent { Id = "e1" }),
        TypedRow("t1", "002", new TestIntegrationEvent { Id = "e2", Topic = "t" }),
        TypedRow("t1", "003", new TestArchivedEvent { Id = "e3" }));

    var entries = await harness.Store.QueryAsync("t1", ct: TestContext.Current.CancellationToken);

    Assert.Equal(3, entries.Count);
    Assert.Single(entries, e => e.Item is ITestIntegrationEvent);
    Assert.Single(entries.ItemsOfType<TestMessage, TestCreatedEvent>());
}

[Fact]
public async Task QueryAsync_Take_LimitsResults()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupQuery(
        TypedRow("t1", "001", new TestCreatedEvent { Id = "e1" }),
        TypedRow("t1", "002", new TestCreatedEvent { Id = "e2" }),
        TypedRow("t1", "003", new TestCreatedEvent { Id = "e3" }));

    var entries = await harness.Store.QueryAsync("t1", take: 2, ct: TestContext.Current.CancellationToken);

    Assert.Equal(2, entries.Count);
}

[Fact]
public async Task QueryAsync_MarkerRowAmongTypedRows_MaterializesWithNullItem()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupQuery(
        TypedRow("t1", "001", new TestCreatedEvent { Id = "e1" }),
        new TableEntity("t1", "FlagEntity") { ["_IsCommitted"] = true });

    var entries = await harness.Store.QueryAsync("t1", ct: TestContext.Current.CancellationToken);

    var marker = Assert.Single(entries, e => e.Item is null);
    Assert.True(marker.Column<bool>("_IsCommitted"));
}

[Fact]
public async Task CountAsync_ProjectsKeysOnly()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupQuery(
        TypedRow("t1", "001", new TestCreatedEvent { Id = "e1" }),
        TypedRow("t1", "002", new TestCreatedEvent { Id = "e2" }));

    Assert.Equal(2, await harness.Store.CountAsync("t1", TestContext.Current.CancellationToken));

    await harness.Table.Received(1).QueryAsync<TableEntity>(
        Arg.Any<string>(),
        Arg.Any<int?>(),
        Arg.Is<IEnumerable<string>>(s => s != null && s.Contains("PartitionKey") && s.Contains("RowKey")),
        Arg.Any<CancellationToken>());
}

[Fact]
public async Task DeletePartitionAsync_DeletesEveryRow_WhateverItsType()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupQuery(
        TypedRow("t1", "001", new TestCreatedEvent { Id = "e1" }),
        TypedRow("t1", "002", new TestArchivedEvent { Id = "e2" }),
        new TableEntity("t1", "FlagEntity") { ["_IsCommitted"] = true });
    harness.SetupTransaction();

    Assert.Equal(3, await harness.Store.DeletePartitionAsync("t1", TestContext.Current.CancellationToken));

    var actions = Assert.Single(harness.Transactions);
    Assert.All(actions, a => Assert.Equal(TableTransactionActionType.Delete, a.ActionType));
}

[Fact]
public async Task DeletePartitionAsync_EmptyPartition_ReturnsZeroWithoutATransaction()
{
    using var harness = new PolymorphicHarness<TestMessage>();
    harness.SetupQuery();
    harness.SetupTransaction();

    Assert.Equal(0, await harness.Store.DeletePartitionAsync("t1", TestContext.Current.CancellationToken));
    Assert.Empty(harness.Transactions);
}

private static TableEntity TypedRow(string partition, string row, TestMessage item)
{
    var entity = item.ToTableEntity(partition, row);
    entity[SystemColumnNames.TypeName] =
        AssemblyQualifiedTypeDiscriminator.Instance.ToDiscriminator(item.GetType());
    return entity;
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~PolymorphicStorageTests"`
Expected: FAIL with `NotImplementedException` from the remaining Task 9 stubs.

- [ ] **Step 3: Implement the four methods**

Replace the remaining stubs in `src/Unified.Data.Tables/PolymorphicTableStorage.cs`. Add `using System.Runtime.CompilerServices;` at the top for `[EnumeratorCancellation]`, and add the field alongside `ReservedCells`:

```csharp
    // Counting and partition-deletion never need the payload; projecting keys only turns a full-row
    // scan into a keys scan, which matters because Azure Tables has no server-side count.
    private static readonly string[] KeysOnly = ["PartitionKey", "RowKey"];
```

Then:

```csharp
    /// <inheritdoc />
    public async Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(
        string? partition = null, int? take = null, CancellationToken ct = default)
    {
        var results = new List<PolymorphicEntry<TBase>>();
        await foreach (var entry in QueryStreamAsync(partition, take, ct))
            results.Add(entry);

        return results;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(
        string? partition = null,
        int? take = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (take is <= 0)
            yield break;

        await initializer.EnsureAsync(ct);

        var yielded = 0;

        // AsyncPageable follows continuation tokens internally. That is the whole reason streaming is
        // the primitive here: a hand-rolled single-segment read silently truncates at one page, and
        // the truncation looks exactly like an empty tail.
        await foreach (var row in client.QueryAsync<TableEntity>(
                           PartitionFilter(partition), maxPerPage: null, select: null, ct))
        {
            yield return ToEntry(row);

            if (take is { } limit && ++yielded >= limit)
                yield break;
        }
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(string? partition = null, CancellationToken ct = default)
    {
        await initializer.EnsureAsync(ct);

        var count = 0;
        await foreach (var _ in client.QueryAsync<TableEntity>(
                           PartitionFilter(partition), maxPerPage: null, KeysOnly, ct))
        {
            count++;
        }

        return count;
    }

    /// <inheritdoc />
    public async Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(partition);
        await initializer.EnsureAsync(ct);

        var rows = new List<TableEntity>();
        await foreach (var row in client.QueryAsync<TableEntity>(
                           PartitionFilter(partition), maxPerPage: null, KeysOnly, ct))
        {
            rows.Add(row);
        }

        if (rows.Count == 0)
            return 0;

        var deleted = 0;

        // Already single-partition by construction, so one plan covers it.
        foreach (var range in BatchPlanner.Plan([.. rows.Select(TableRowSize.Estimate)]))
        {
            var actions = new List<TableTransactionAction>(range.Count);
            for (var i = range.Start; i < range.Start + range.Count; i++)
                actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, rows[i], ETag.All));

            await client.SubmitTransactionAsync(actions, ct);
            deleted += actions.Count;
        }

        return deleted;
    }

    // OData string literals escape an apostrophe by doubling it. Without this a partition key like
    // "O'Brien" produces a malformed filter that the service rejects — or, worse, one that parses
    // into a different query.
    private static string? PartitionFilter(string? partition) =>
        partition is null
            ? null
            : $"PartitionKey eq '{partition.Replace("'", "''", StringComparison.Ordinal)}'";
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj`
Expected: PASS. `PolymorphicTableStorage<TBase>` now has no stubs left — confirm with `grep -n "NotImplementedException" src/Unified.Data.Tables/PolymorphicTableStorage.cs`, which must print nothing.

- [ ] **Step 5: Commit**

```bash
git add src/Unified.Data.Tables/PolymorphicTableStorage.cs tests/Unified.Data.Tables.Tests/PolymorphicStorageTests.cs
git commit -m "feat: add polymorphic queries, streaming reads, count and partition delete"
```

---

### Task 12: `InMemoryPolymorphicStorage<TBase>`

**Files:**
- Create: `src/Unified.Data.Tables.InMemory/InMemoryPolymorphicStorage.cs`
- Create: `tests/Unified.Data.Tables.Tests/InMemoryPolymorphicStorageTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–7 and 11. The InMemory project references `Unified.Data.Tables`, so `ITypeDiscriminator` and `TableEntitySerializer` are reachable.
- Produces: `public sealed class InMemoryPolymorphicStorage<TBase> : IPolymorphicStorage<TBase> where TBase : class`, constructors `()` and `(UnifiedTableStorageOptions? options)`, plus `int Count`, `void Clear()`.

> **Read `src/Unified.Data.Tables.InMemory/InMemoryStorage.cs` first and mirror its structure**: a `Dictionary<(string, string), StoredRow>` under one `gate`, a monotonic `versionCounter` producing `W/"n"` ETags, ordinal (PartitionKey, RowKey) ordering, and the same 409/404 `RequestFailedException` shapes. Rows are stored as serialized `TableEntity`s and round-trip through the **real** serializer, so a test exercises production serialization rather than object identity — that is the fake's entire value proposition.
>
> **Message parity is enforced.** The repo's doctrine is that the fake and the real store throw byte-identical messages for the same contract violation (see `ConcurrencyMessages`). `PolymorphicMessages` exists for this; use it in both.

- [ ] **Step 1: Write the failing test**

Create `tests/Unified.Data.Tables.Tests/InMemoryPolymorphicStorageTests.cs`:

```csharp
using Azure;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The polymorphic contract against the fake. The repo's guarantee is that a green test here holds
/// against Azure Tables, so this file deliberately re-runs the same behaviours as
/// <see cref="PolymorphicStorageTests"/> rather than testing the fake's internals.
/// </summary>
public class InMemoryPolymorphicStorageTests
{
    private static InMemoryPolymorphicStorage<TestMessage> NewStore() => new();

    [Fact]
    public async Task InsertAsync_ThenGetAsync_ReturnsTrueDerivedInstance()
    {
        var store = NewStore();
        var key = new TableKey("agg-1", "000000001");

        await store.InsertAsync(key, new TestCreatedEvent { Id = "e1", Version = 7 },
            TestContext.Current.CancellationToken);

        var entry = await store.GetAsync(key, TestContext.Current.CancellationToken);
        Assert.Equal(7, Assert.IsType<TestCreatedEvent>(entry!.Item).Version);
    }

    [Fact]
    public async Task InsertAsync_DuplicateKey_ThrowsDuplicateKeyException()
    {
        var store = NewStore();
        var key = new TableKey("p", "r");
        await store.InsertAsync(key, new TestCommand { Id = "c1" }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DuplicateKeyException>(() =>
            store.InsertAsync(key, new TestCommand { Id = "c1" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpsertAsync_ExistingKey_Replaces()
    {
        var store = NewStore();
        var key = new TableKey("p", "r");
        await store.InsertAsync(key, new TestCreatedEvent { Id = "e1", Version = 1 },
            TestContext.Current.CancellationToken);

        await store.UpsertAsync(key, new TestCreatedEvent { Id = "e1", Version = 2 },
            TestContext.Current.CancellationToken);

        var entry = await store.GetAsync(key, TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.IsType<TestCreatedEvent>(entry!.Item).Version);
    }

    [Fact]
    public async Task InsertBatchAsync_HeterogeneousPlusMarker_AllReadBack()
    {
        var store = NewStore();

        var written = await store.InsertBatchAsync(
            [
                new PolymorphicWrite<TestMessage>(new TableKey("t1", "001"), new TestCreatedEvent { Id = "e1" }),
                new PolymorphicWrite<TestMessage>(new TableKey("t1", "002"), new TestArchivedEvent { Id = "e2" }),
                PolymorphicWrite<TestMessage>.Marker(
                    new TableKey("t1", "FlagEntity"),
                    new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsCommitted"] = false }),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(3, written);

        var entries = await store.QueryAsync("t1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, entries.Count);
        Assert.Single(entries, e => e.Item is null);
        Assert.Single(entries.ItemsOfType<TestMessage, TestArchivedEvent>());
    }

    [Fact]
    public async Task MergeColumnsAsync_FlipsASentinel_WithoutTouchingThePayload()
    {
        var store = NewStore();
        var key = new TableKey("agg-1", "000000001");
        await store.InsertAsync(
            new PolymorphicWrite<TestMessage>(
                key,
                new TestIntegrationEvent { Id = "e1", Topic = "orders" },
                new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsPublished"] = false }),
            TestContext.Current.CancellationToken);

        await store.MergeColumnsAsync(
            key,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsPublished"] = true },
            TestContext.Current.CancellationToken);

        var entry = await store.GetAsync(key, TestContext.Current.CancellationToken);
        Assert.True(entry!.Column<bool>("_IsPublished"));
        Assert.Equal("orders", Assert.IsType<TestIntegrationEvent>(entry.Item).Topic);
    }

    [Fact]
    public async Task MergeColumnsAsync_MissingRow_Throws404LikeAzure()
    {
        var store = NewStore();

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => store.MergeColumnsAsync(
            new TableKey("p", "r"),
            new Dictionary<string, object>(StringComparer.Ordinal) { ["_IsPublished"] = true },
            TestContext.Current.CancellationToken));

        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public async Task QueryAsync_OrdersLexicallyByPartitionThenRow()
    {
        var store = NewStore();
        await store.InsertAsync(new TableKey("b", "2"), new TestCommand { Id = "3" },
            TestContext.Current.CancellationToken);
        await store.InsertAsync(new TableKey("a", "2"), new TestCommand { Id = "2" },
            TestContext.Current.CancellationToken);
        await store.InsertAsync(new TableKey("a", "1"), new TestCommand { Id = "1" },
            TestContext.Current.CancellationToken);

        var entries = await store.QueryAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(["1", "2", "3"], entries.Select(e => e.Item!.Id));
    }

    [Fact]
    public async Task Keys_AreUsedVerbatim_AndAreCaseSensitive()
    {
        var store = NewStore();
        await store.InsertAsync(new TableKey("Agg 1", "MiXeD"), new TestCommand { Id = "c1" },
            TestContext.Current.CancellationToken);

        Assert.NotNull(await store.GetAsync(new TableKey("Agg 1", "MiXeD"), TestContext.Current.CancellationToken));
        Assert.Null(await store.GetAsync(new TableKey("agg-1", "mixed"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_MissingRow_IsANoOp()
    {
        var store = NewStore();

        await store.DeleteAsync(new TableKey("p", "r"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeletePartitionAsync_RemovesOnlyThatPartition()
    {
        var store = NewStore();
        await store.InsertAsync(new TableKey("a", "1"), new TestCommand { Id = "1" },
            TestContext.Current.CancellationToken);
        await store.InsertAsync(new TableKey("b", "1"), new TestCommand { Id = "2" },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, await store.DeletePartitionAsync("a", TestContext.Current.CancellationToken));
        Assert.Equal(1, await store.CountAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RowsRoundTripThroughTheRealSerializer()
    {
        var store = NewStore();
        var key = new TableKey("p", "r");
        var payload = new TestCtorlessEvent("large-" + new string('x', 200)) { Id = "e1" };

        await store.InsertAsync(key, payload, TestContext.Current.CancellationToken);

        var entry = await store.GetAsync(key, TestContext.Current.CancellationToken);
        Assert.StartsWith("large-", Assert.IsType<TestCtorlessEvent>(entry!.Item).Payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("IsPublished")]
    [InlineData("_TypeName")]
    public async Task Fake_And_Real_ThrowByteIdenticalMessages_ForBadSystemColumns(string columnName)
    {
        var fake = NewStore();
        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupMerge();

        var columns = new Dictionary<string, object>(StringComparer.Ordinal) { [columnName] = true };
        var key = new TableKey("p", "r");

        var fromFake = await Assert.ThrowsAsync<ArgumentException>(
            () => fake.MergeColumnsAsync(key, columns, TestContext.Current.CancellationToken));
        var fromReal = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Store.MergeColumnsAsync(key, columns, TestContext.Current.CancellationToken));

        Assert.Equal(fromReal.Message, fromFake.Message);
    }

    [Fact]
    public async Task Fake_And_Real_ThrowByteIdenticalMessages_ForUnassignableType()
    {
        var discriminator = AssemblyQualifiedTypeDiscriminator.Instance;
        var row = new Azure.Data.Tables.TableEntity("p", "r")
        {
            [SystemColumnNames.TypeName] = discriminator.ToDiscriminator(typeof(UnrelatedType)),
        };

        using var harness = new PolymorphicHarness<TestMessage>();
        harness.SetupGet(row);

        var fromReal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Store.GetAsync(new TableKey("p", "r"), TestContext.Current.CancellationToken));

        Assert.Equal(PolymorphicMessages.NotAssignable(
            discriminator.ToDiscriminator(typeof(UnrelatedType)), typeof(TestMessage)), fromReal.Message);
    }
}
```

> `PolymorphicMessages` is `internal` in Abstractions, which grants `InternalsVisibleTo` to the two implementation assemblies but **not** to the test project. Before writing the last test, add `<InternalsVisibleTo Include="Unified.Data.Tables.Tests" />` to the existing `ItemGroup` in `src/Unified.Data.Tables.Abstractions/Unified.Data.Tables.Abstractions.csproj` — this mirrors what `Unified.Data.Tables.csproj` already does for its own internals.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~InMemoryPolymorphicStorageTests"`
Expected: FAIL — `The type or namespace name 'InMemoryPolymorphicStorage<>' could not be found`.

- [ ] **Step 3: Write the fake**

Create `src/Unified.Data.Tables.InMemory/InMemoryPolymorphicStorage.cs`:

```csharp
using System.Globalization;
using Azure;
using Azure.Data.Tables;

namespace Unified.Data.Tables.InMemory;

/// <summary>
/// Faithful in-memory <see cref="IPolymorphicStorage{TBase}"/> for tests, dev mode and offline
/// runtime. Rows are stored as serialized <see cref="TableEntity"/>s and round-trip through the REAL
/// <see cref="TableEntitySerializer"/> on every read and write, so code under test exercises
/// production serialization rather than object identity.
/// </summary>
/// <remarks>
/// Semantics mirror <see cref="PolymorphicTableStorage{TBase}"/>: keys used verbatim and
/// case-sensitively, 409 on duplicate insert surfaced as <see cref="DuplicateKeyException"/>, 404 on
/// merging a missing row, idempotent delete, lexical (PartitionKey, RowKey) ordering, and
/// byte-identical validation messages via <c>PolymorphicMessages</c>.
/// </remarks>
/// <typeparam name="TBase">The common base type every stored row materializes as.</typeparam>
public sealed class InMemoryPolymorphicStorage<TBase> : IPolymorphicStorage<TBase>
    where TBase : class
{
    private static readonly string[] ReservedCells = ["PartitionKey", "RowKey", "Timestamp", "odata.etag"];

    private readonly Dictionary<(string PartitionKey, string RowKey), StoredRow> rows = [];
    private readonly object gate = new();
    private readonly ITypeDiscriminator discriminator;
    private long versionCounter;

    /// <summary>Creates a store with the default options.</summary>
    public InMemoryPolymorphicStorage()
        : this(null)
    {
    }

    /// <summary>Creates a store with explicit options.</summary>
    /// <param name="options">Options; null selects the defaults.</param>
    public InMemoryPolymorphicStorage(UnifiedTableStorageOptions? options) =>
        discriminator = (options ?? new UnifiedTableStorageOptions()).ResolveTypeDiscriminator();

    /// <summary>How many rows the store holds. For assertions.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return rows.Count;
            }
        }
    }

    /// <summary>Removes every row. For test isolation.</summary>
    public void Clear()
    {
        lock (gate)
        {
            rows.Clear();
        }
    }

    /// <inheritdoc />
    public Task EnsureCreatedAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>> InsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        Guard.NotNull(write, nameof(write));
        var row = ToRow(write);

        lock (gate)
        {
            var key = (write.Key.PartitionKey, write.Key.RowKey);
            if (rows.ContainsKey(key))
            {
                throw new DuplicateKeyException(
                    typeof(TBase).Name,
                    write.Key.ToId(),
                    new RequestFailedException(409, "The specified entity already exists."));
            }

            rows[key] = Store(row);
            return Task.FromResult(ToEntry(rows[key]));
        }
    }

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>> UpsertAsync(
        PolymorphicWrite<TBase> write, CancellationToken ct = default)
    {
        Guard.NotNull(write, nameof(write));
        var row = ToRow(write);

        lock (gate)
        {
            var key = (write.Key.PartitionKey, write.Key.RowKey);
            rows[key] = Store(row);
            return Task.FromResult(ToEntry(rows[key]));
        }
    }

    /// <inheritdoc />
    public Task<int> InsertBatchAsync(
        IReadOnlyCollection<PolymorphicWrite<TBase>> writes, CancellationToken ct = default)
    {
        Guard.NotNull(writes, nameof(writes));
        if (writes.Count == 0)
            return Task.FromResult(0);

        // Build every row before taking the lock, so validation failures cost nothing.
        var built = writes.Select(w => (w.Key, Row: ToRow(w))).ToList();

        lock (gate)
        {
            foreach (var (key, _) in built)
            {
                if (rows.ContainsKey((key.PartitionKey, key.RowKey)))
                {
                    throw new DuplicateKeyException(
                        typeof(TBase).Name,
                        key.ToId(),
                        new RequestFailedException(409, "The specified entity already exists."));
                }
            }

            foreach (var (key, row) in built)
                rows[(key.PartitionKey, key.RowKey)] = Store(row);
        }

        return Task.FromResult(built.Count);
    }

    /// <inheritdoc />
    public Task MergeColumnsAsync(
        TableKey key, IReadOnlyDictionary<string, object> columns, CancellationToken ct = default)
    {
        ValidateKey(key);
        Guard.NotNull(columns, nameof(columns));
        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "A merge with no columns would be a network round trip that changes nothing.",
                nameof(columns));
        }

        foreach (var column in columns)
            ValidateSystemColumn(column.Key);

        lock (gate)
        {
            if (!rows.TryGetValue((key.PartitionKey, key.RowKey), out var existing))
                throw new RequestFailedException(404, "The specified resource does not exist.");

            // Server-side Merge overlays the supplied cells onto a copy and leaves the rest alone.
            var merged = CopyOf(existing.Data);
            foreach (var column in columns)
                merged[column.Key] = column.Value;

            rows[(key.PartitionKey, key.RowKey)] = Store(merged);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PolymorphicEntry<TBase>?> GetAsync(TableKey key, CancellationToken ct = default)
    {
        ValidateKey(key);

        lock (gate)
        {
            return Task.FromResult(
                rows.TryGetValue((key.PartitionKey, key.RowKey), out var stored) ? ToEntry(stored) : null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PolymorphicEntry<TBase>>> QueryAsync(
        string? partition = null, int? take = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PolymorphicEntry<TBase>>>(Snapshot(partition, take));

    /// <inheritdoc />
    public async IAsyncEnumerable<PolymorphicEntry<TBase>> QueryStreamAsync(
        string? partition = null,
        int? take = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in Snapshot(partition, take))
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(TableKey key, CancellationToken ct = default)
    {
        ValidateKey(key);

        lock (gate)
        {
            rows.Remove((key.PartitionKey, key.RowKey));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default)
    {
        Guard.NotNull(partition, nameof(partition));

        lock (gate)
        {
            var doomed = rows.Keys
                .Where(k => string.Equals(k.PartitionKey, partition, StringComparison.Ordinal))
                .ToList();

            foreach (var key in doomed)
                rows.Remove(key);

            return Task.FromResult(doomed.Count);
        }
    }

    /// <inheritdoc />
    public Task<int> CountAsync(string? partition = null, CancellationToken ct = default)
    {
        lock (gate)
        {
            return Task.FromResult(partition is null
                ? rows.Count
                : rows.Keys.Count(k => string.Equals(k.PartitionKey, partition, StringComparison.Ordinal)));
        }
    }

    private List<PolymorphicEntry<TBase>> Snapshot(string? partition, int? take)
    {
        if (take is <= 0)
            return [];

        lock (gate)
        {
            var query = rows
                .Where(kv => partition is null
                             || string.Equals(kv.Key.PartitionKey, partition, StringComparison.Ordinal))
                .OrderBy(kv => kv.Key.PartitionKey, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.RowKey, StringComparer.Ordinal)
                .Select(kv => ToEntry(kv.Value));

            if (take is { } limit)
                query = query.Take(limit);

            return query.ToList();
        }
    }

    private StoredRow Store(TableEntity row) =>
        new(CopyOf(row), ++versionCounter, DateTimeOffset.UtcNow);

    private TableEntity ToRow(PolymorphicWrite<TBase> write)
    {
        ValidateKey(write.Key);

        var row = write.Item is null
            ? new TableEntity(write.Key.PartitionKey, write.Key.RowKey)
            : write.Item.ToTableEntity(write.Key.PartitionKey, write.Key.RowKey);

        if (write.Item is not null)
            row[SystemColumnNames.TypeName] = discriminator.ToDiscriminator(write.Item.GetType());

        if (write.SystemColumns is null)
            return row;

        foreach (var column in write.SystemColumns)
        {
            ValidateSystemColumn(column.Key);
            row[column.Key] = column.Value;
        }

        return row;
    }

    private PolymorphicEntry<TBase> ToEntry(StoredRow stored)
    {
        var row = CopyOf(stored.Data);
        row.Timestamp = stored.Timestamp;

        string? storedDiscriminator = null;
        if (row.TryGetValue(SystemColumnNames.TypeName, out var raw)
            && raw is string token
            && token.Length > 0)
        {
            storedDiscriminator = token;
        }

        var item = row.TryFromTableEntity<TBase>(discriminator, out var materialized) ? materialized : null;

        var columns = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var cell in row)
        {
            if (Array.IndexOf(ReservedCells, cell.Key) >= 0)
                continue;

            columns[cell.Key] = cell.Value;
        }

        return new PolymorphicEntry<TBase>(
            new TableKey(row.PartitionKey, row.RowKey),
            item,
            storedDiscriminator,
            stored.ETagString(),
            stored.Timestamp,
            columns);
    }

    // Isolation: a caller mutating a returned row must not reach into the store, and a stored row
    // must not change under a caller who is still holding the object they wrote.
    private static TableEntity CopyOf(TableEntity source)
    {
        var copy = new TableEntity(source.PartitionKey, source.RowKey);
        foreach (var cell in source)
        {
            if (Array.IndexOf(ReservedCells, cell.Key) >= 0)
                continue;

            copy[cell.Key] = cell.Value;
        }

        return copy;
    }

    private static void ValidateKey(TableKey key)
    {
        if (string.IsNullOrEmpty(key.PartitionKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.PartitionKey)), nameof(key));
        if (string.IsNullOrEmpty(key.RowKey))
            throw new ArgumentException(PolymorphicMessages.EmptyKey(nameof(TableKey.RowKey)), nameof(key));
    }

    private static void ValidateSystemColumn(string columnName)
    {
        if (!SystemColumnNames.IsSystemColumn(columnName))
            throw new ArgumentException(PolymorphicMessages.NotSystemColumn(columnName), nameof(columnName));

        if (string.Equals(columnName, SystemColumnNames.TypeName, StringComparison.Ordinal))
            throw new ArgumentException(PolymorphicMessages.TypeNameNotMergeable(), nameof(columnName));
    }

    private sealed record StoredRow(TableEntity Data, long Version, DateTimeOffset Timestamp)
    {
        public string ETagString() => $"W/\"{Version.ToString(CultureInfo.InvariantCulture)}\"";
    }
}
```

> **Note on the `paramName` mismatch:** `ValidateSystemColumn` passes `nameof(columnName)` in both implementations, so the messages match. If a test compares `ex.ParamName` as well as `ex.Message`, both must agree — keep the two copies identical, which is why they are deliberately duplicated rather than shared through an internal helper the fake cannot see.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~InMemoryPolymorphicStorageTests"`
Expected: PASS, 14 tests.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj`
Expected: PASS, everything.

- [ ] **Step 6: Commit**

```bash
git add src/Unified.Data.Tables.InMemory/InMemoryPolymorphicStorage.cs \
        src/Unified.Data.Tables.Abstractions/Unified.Data.Tables.Abstractions.csproj \
        tests/Unified.Data.Tables.Tests/InMemoryPolymorphicStorageTests.cs
git commit -m "feat(inmemory): add InMemoryPolymorphicStorage mirroring the Azure contract"
```

---

### Task 13: Keyed DI registration

**Files:**
- Modify: `src/Unified.Data.Tables/ServiceCollectionExtensions.cs`
- Modify: `src/Unified.Data.Tables.InMemory/InMemoryServiceCollectionExtensions.cs`
- Create: `tests/Unified.Data.Tables.Tests/PolymorphicServiceCollectionTests.cs`

**Interfaces:**
- Consumes: `PolymorphicTableStorage<TBase>` (Tasks 9–11), `InMemoryPolymorphicStorage<TBase>` (Task 12).
- Produces:
  - `ServiceCollectionExtensions.AddUnifiedPolymorphicTable<TBase>(this IServiceCollection services, string tableName) where TBase : class`
  - `InMemoryServiceCollectionExtensions.AddUnifiedInMemoryPolymorphicTable<TBase>(this IServiceCollection services, string tableName) where TBase : class`

> **Why keyed, not open-generic:** one base type commonly addresses several tables — in the driving consumer `IEvent` is both the state-event store and the transaction store. `TryAddSingleton(typeof(IPolymorphicStorage<>), ...)` can only bind one. Keyed registration uses the table name as the key, which is native to `Microsoft.Extensions.DependencyInjection` 8+, needs no phantom type parameter, and makes the consumer's `[FromKeyedServices("StateEventStore")]` self-documenting. The two registrations must mirror each other exactly so a test host swaps one line.

- [ ] **Step 1: Write the failing test**

Create `tests/Unified.Data.Tables.Tests/PolymorphicServiceCollectionTests.cs`:

```csharp
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the registration shape. The case that drives it: two stores over the SAME base type and
/// different tables, which an open-generic registration cannot express.
/// </summary>
public class PolymorphicServiceCollectionTests
{
    [Fact]
    public void AddUnifiedPolymorphicTable_TwoTablesOneBaseType_ResolveIndependently()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<TableServiceClient>());
        services.AddLogging();
        services.AddUnifiedTableStorage();
        services.AddUnifiedPolymorphicTable<TestMessage>("StateEventStore");
        services.AddUnifiedPolymorphicTable<TestMessage>("TransactionStore");

        using var provider = services.BuildServiceProvider();

        var stateEvents = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("StateEventStore");
        var transactions = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("TransactionStore");

        Assert.NotSame(stateEvents, transactions);
    }

    [Fact]
    public void AddUnifiedPolymorphicTable_ResolvesToTheAzureImplementation()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<TableServiceClient>());
        services.AddLogging();
        services.AddUnifiedTableStorage();
        services.AddUnifiedPolymorphicTable<TestMessage>("CommandStore");

        using var provider = services.BuildServiceProvider();

        Assert.IsType<PolymorphicTableStorage<TestMessage>>(
            provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("CommandStore"));
    }

    [Fact]
    public void AddUnifiedPolymorphicTable_IsASingletonPerKey()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<TableServiceClient>());
        services.AddLogging();
        services.AddUnifiedTableStorage();
        services.AddUnifiedPolymorphicTable<TestMessage>("CommandStore");

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("CommandStore"),
            provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("CommandStore"));
    }

    [Fact]
    public void AddUnifiedInMemoryPolymorphicTable_MirrorsTheAzureRegistration()
    {
        var services = new ServiceCollection();
        services.AddUnifiedInMemoryStorage();
        services.AddUnifiedInMemoryPolymorphicTable<TestMessage>("StateEventStore");
        services.AddUnifiedInMemoryPolymorphicTable<TestMessage>("TransactionStore");

        using var provider = services.BuildServiceProvider();

        var stateEvents = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("StateEventStore");
        var transactions = provider.GetRequiredKeyedService<IPolymorphicStorage<TestMessage>>("TransactionStore");

        Assert.IsType<InMemoryPolymorphicStorage<TestMessage>>(stateEvents);
        Assert.NotSame(stateEvents, transactions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddUnifiedPolymorphicTable_BlankTableName_Throws(string? tableName)
    {
        var services = new ServiceCollection();

        Assert.ThrowsAny<ArgumentException>(
            () => services.AddUnifiedPolymorphicTable<TestMessage>(tableName!));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~PolymorphicServiceCollectionTests"`
Expected: FAIL — `AddUnifiedPolymorphicTable` does not exist.

- [ ] **Step 3: Add the Azure registration**

Append to `ServiceCollectionExtensions` in `src/Unified.Data.Tables/ServiceCollectionExtensions.cs`:

```csharp
    /// <summary>
    /// Registers one <see cref="IPolymorphicStorage{TBase}"/> over <paramref name="tableName"/>,
    /// KEYED by that table name. Requires a registered <see cref="TableServiceClient"/> — call an
    /// <c>AddUnifiedTableStorage</c> overload first.
    /// </summary>
    /// <remarks>
    /// Keyed rather than open-generic because one base type routinely addresses several tables (an
    /// event base is both the state-event store and the transaction store), and an open-generic
    /// registration can only bind one. Resolve with
    /// <c>[FromKeyedServices("TableName")] IPolymorphicStorage&lt;TBase&gt;</c>.
    /// </remarks>
    /// <typeparam name="TBase">The common base type the table's rows materialize as.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="tableName">The table this store owns; also the DI key.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddUnifiedPolymorphicTable<TBase>(
        this IServiceCollection services, string tableName)
        where TBase : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        services.TryAddKeyedSingleton<IPolymorphicStorage<TBase>>(tableName, (sp, _) =>
            new PolymorphicTableStorage<TBase>(
                sp.GetRequiredService<TableServiceClient>(),
                tableName,
                sp.GetRequiredService<ILogger<PolymorphicTableStorage<TBase>>>(),
                sp.GetService<UnifiedTableStorageOptions>()));

        return services;
    }
```

Add `using Microsoft.Extensions.Logging;` at the top of the file.

- [ ] **Step 4: Add the in-memory registration**

Append to `InMemoryServiceCollectionExtensions` in `src/Unified.Data.Tables.InMemory/InMemoryServiceCollectionExtensions.cs`:

```csharp
    /// <summary>
    /// Registers one <see cref="IPolymorphicStorage{TBase}"/> backed by
    /// <see cref="InMemoryPolymorphicStorage{TBase}"/>, KEYED by <paramref name="tableName"/> — the
    /// drop-in replacement for <c>AddUnifiedPolymorphicTable</c>. The key is still the table name so
    /// a host swaps the registration line and nothing at the injection sites changes.
    /// </summary>
    /// <typeparam name="TBase">The common base type the table's rows materialize as.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="tableName">The logical table name; also the DI key.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddUnifiedInMemoryPolymorphicTable<TBase>(
        this IServiceCollection services, string tableName)
        where TBase : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        services.TryAddKeyedSingleton<IPolymorphicStorage<TBase>>(tableName, (sp, _) =>
            new InMemoryPolymorphicStorage<TBase>(sp.GetService<UnifiedTableStorageOptions>()));

        return services;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj --filter "FullyQualifiedName~PolymorphicServiceCollectionTests"`
Expected: PASS, 7 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Unified.Data.Tables/ServiceCollectionExtensions.cs \
        src/Unified.Data.Tables.InMemory/InMemoryServiceCollectionExtensions.cs \
        tests/Unified.Data.Tables.Tests/PolymorphicServiceCollectionTests.cs
git commit -m "feat(di): register polymorphic stores keyed by table name"
```

---

### Task 14: Documentation and release

**Files:**
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `Directory.Build.props`

**Interfaces:**
- Consumes: everything. Produces no code.

> **The README is packed into all four nupkgs** (`PackageReadmeFile`), so shipping without this section publishes package docs that contradict the code. This is a deliverable, not a chore.

- [ ] **Step 1: Add the README section**

Insert a `## Polymorphic storage` section immediately after the existing `## Serialization` section in `README.md`:

````markdown
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
        return [.. entries.Select(e => e.Value)];
    }
}
```

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
````

- [ ] **Step 2: Extend the `## Serialization` section**

Append these bullets to the existing `## Serialization` list in `README.md`:

```markdown
- **A leading `_` is reserved.** `_`-prefixed columns belong to the storage layer: they are never
  produced from a property and never written into one. Before 0.8.0, `_TypeName` was parsed as
  property path `["TypeName"]`, so a type declaring a `TypeName` property silently received the
  assembly-qualified name as its value — the same held for any `_`-prefixed sentinel.
- **`persistType: true` writes `_TypeName`** with `Type.AssemblyQualifiedName`. `FromTableEntity<TBase>(discriminator)`
  reads it back constrained to a base type, and `TryFromTableEntity<TBase>` additionally tolerates a
  row that carries no discriminator at all.
- A base-constrained read does **not** recompute `Id` from the row keys, because a polymorphic key
  (an aggregate version, an inverted tick count) is unrelated to any property.
```

- [ ] **Step 3: Add the CHANGELOG entry**

Insert at the top of the release list in `CHANGELOG.md`:

```markdown
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
  row in scope.
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
- **`AddUnifiedPolymorphicTable<TBase>(tableName)`** and its in-memory mirror
  `AddUnifiedInMemoryPolymorphicTable<TBase>(tableName)`, registering each store **keyed by table
  name**. Keyed rather than open-generic because one base type routinely addresses several tables, and
  an open-generic registration can only bind one. Resolve with
  `[FromKeyedServices("StateEventStore")] IPolymorphicStorage<IEvent>`.

### Fixed

- **A system column is no longer written into a property that happens to share its name.** Column
  parsing strips the leading `_` (`_TypeName` splits to path `["TypeName"]`), so a type declaring a
  `TypeName` property silently received an assembly-qualified name as its value; the same held for
  any `_`-prefixed sentinel. A leading `_` now marks a **system column** on every read path: never
  produced from a property, never fed to a property setter. This codifies existing reality rather
  than inventing a rule — a property literally named `_Foo` already wrote to column `_Foo` and read
  back into property `Foo`. Nothing this library has ever written produces a `_`-prefixed column
  other than `_TypeName`, so no row it authored changes meaning; a **hand-authored** `_X` column that
  relied on landing in property `X` no longer will.

### Changed

- `TableStorage<T>`'s row-size estimation and its coalesced lazy table creation moved to internal
  helpers (`TableRowSize`, `TableInitializer`) shared with the polymorphic store, so both measure and
  initialise identically instead of drifting. No public surface, no semantics, no test changes.

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

### Note on `RowKeys.InvertedTicks`

`InvertedTicks` formats `"D19"`. A common pre-existing convention formats the same arithmetic as
`"D20"`; since `DateTime.MaxValue.Ticks` is 19 digits, `D20` zero-pads to 20 characters and `D19` does
not. Each sorts correctly in isolation, but mixed in one partition the 20-character keys sort before
every 19-character key and interleave wrongly. Keep your own helper for such a table — the explicit
`TableKey` design imposes no key generation. A width-parameterised overload is under consideration.
```

- [ ] **Step 4: Bump the version**

In `Directory.Build.props`, change `<Version>0.7.0</Version>` to `<Version>0.8.0</Version>`.

- [ ] **Step 5: Final verification**

```bash
dotnet build Unified.Data.Tables.slnx
dotnet build src/Unified.Data.Tables.Abstractions/Unified.Data.Tables.Abstractions.csproj -f netstandard2.0
dotnet test tests/Unified.Data.Tables.Tests/Unified.Data.Tables.Tests.csproj
dotnet test tests/Unified.Data.Tables.Identity.Tests/Unified.Data.Tables.Identity.Tests.csproj
dotnet pack Unified.Data.Tables.slnx -c Release -o ./artifacts
```

Expected: all builds succeed with **0 warnings** (`TreatWarningsAsErrors=true` means any warning is already a failure), all tests pass, and four `.nupkg` + four `.snupkg` files appear at version `0.8.0`.

- [ ] **Step 6: Commit**

```bash
git add README.md CHANGELOG.md Directory.Build.props
git commit -m "docs: document polymorphic storage and release 0.8.0"
```

- [ ] **Step 7: Open the PR**

```bash
git push -u origin feat/polymorphic-storage
gh pr create --title "feat: polymorphic storage (0.8.0)" --body-file docs/superpowers/specs/2026-08-17-polymorphic-storage-design.md
```

---

## Self-Review

**Spec coverage** — every section of `2026-08-17-polymorphic-storage-design.md` maps to a task:

| Spec section | Task(s) |
| --- | --- |
| Why this cannot be `IStorage<T>` | 3 (contract shape + XML doc) |
| The contract | 3 |
| Supporting types | 1, 2 |
| Keys are explicit and verbatim | 1, 9 (`ValidateKey`), 11 (verbatim-key test) |
| Type resolution behind a seam | 5, 6, 9 (options) |
| Always-on assignability gate | 7 |
| Reserved column namespace + bug fix | 2 (`SystemColumnNames`), 4 |
| Mutation surface | 10 (`MergeColumnsAsync`) |
| No caching | 9 (no `IMemoryCache` in ctor) |
| The store owns its table | 11 (no type filter) |
| Batch chunking | 10 |
| DI — keyed services | 13 |
| Mapping to the driving consumer | 10, 11, 12 (marker rows, heterogeneous batch, sentinels, streaming) |
| Wire-format compatibility | 7 (no `Id` rewrite, protected setters, ctor-less), 14 (`RowKeys` note) |
| Accepted JSON risk | 14 (unchanged behaviour; no task needed) |
| Out of scope | 14 (Known limitations) |
| Testing | every task |
| File plan | matches, with two deviations recorded below |

**Deviations from the spec, deliberate:**
1. **No `ITypeDiscriminator` write overload on `ToTableEntity`.** The store composes `ToTableEntity(persistType: false)` and stamps `_TypeName` itself, which delivers the same seam with a strictly smaller change to existing code. Task 9, `ToRow`.
2. **`SystemColumnNames` lives in Abstractions, not beside the serializer.** Both implementations and consumers need the predicate, and Abstractions cannot reference the Azure package. `TableEntitySerializer.TypeNameColumnName` is now an alias so there is one definition at runtime. Task 2/4.
3. **`UnifiedTableStorageOptions.TypeDiscriminator` moved from Task 14 into Task 9**, because the store cannot construct without it. Task 14 is docs and release only. The overview table reflects this.
4. **`PolymorphicMessages` needs `InternalsVisibleTo` for the test project** to assert message parity — added in Task 12, mirroring what `Unified.Data.Tables.csproj` already does for its own internals.

**Placeholder scan:** none. Every code step carries compile-shaped C#; every run step names an exact command and expected outcome.

**Type consistency:** `TableKey`, `PolymorphicWrite<TBase>`, `PolymorphicEntry<TBase>`, `IPolymorphicStorage<TBase>`, `ITypeDiscriminator`, `SystemColumnNames`, `PolymorphicMessages`, `TableRowSize.Estimate`, `TableInitializer.EnsureAsync`, `ToRow`, `ToEntry`, `ValidateKey`, `ValidateSystemColumn`, `PartitionFilter`, `KeysOnly` are spelled identically at every definition and use site. `PolymorphicTableStorage<TBase>` and `InMemoryPolymorphicStorage<TBase>` implement the same 11 members in the same order as the interface.

**Known gap deferred by design:** `PolymorphicTableStorage<TBase>` carries `NotImplementedException` stubs between Tasks 9 and 11. This is intentional — it keeps each task independently compilable and reviewable — and Task 11 Step 4 verifies with `grep` that none survive.
