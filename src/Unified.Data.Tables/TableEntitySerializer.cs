using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Azure.Data.Tables;

namespace Unified.Data.Tables;

/// <summary>
/// Reflection-based (de)serializer that flattens an arbitrary object graph into a flat
/// <see cref="TableEntity"/> and reconstructs it on read. Scalars map to native cells; nested
/// complex types fan out to <c>Parent_Child</c> columns; collections and other shapes that cannot
/// be represented natively fall back to JSON, and oversized JSON is GZip-compressed (and, as a last
/// resort, trimmed) to fit the Azure Tables 64&#160;KB per-cell limit.
/// </summary>
public static class TableEntitySerializer
{
    /// <summary>
    /// Column that stores the type discriminator when <c>persistType</c> is used. An alias for
    /// <see cref="SystemColumnNames.TypeName"/>, which is the single definition — Abstractions
    /// cannot reference this package, so the constant lives there and this is the historical name
    /// for it.
    /// </summary>
    public const string TypeNameColumnName = SystemColumnNames.TypeName;

    /// <summary>
    /// Process-wide policy for payloads that exceed the 64 KB cell cap even compressed — see
    /// <see cref="Unified.Data.Tables.OversizedCellPolicy"/>. The serializer is static, so this is
    /// static too; <c>AddUnifiedTableStorage</c> applies
    /// <see cref="UnifiedTableStorageOptions.OversizedCells"/> here.
    /// </summary>
    public static OversizedCellPolicy OversizedCellPolicy { get; set; } = OversizedCellPolicy.Throw;

    /// <summary>Flatten and serialize <paramref name="root"/> into a <see cref="TableEntity"/>.</summary>
    public static TableEntity ToTableEntity(
        this object root,
        string partitionKey,
        string rowKey,
        bool persistType = false)
        => Build(root, partitionKey, rowKey, persistType, ValueWriteContext.Public, []);

    /// <summary>
    /// Flatten <paramref name="root"/> under a storage write context: <paramref name="ctx"/> decides
    /// how large values are stored and <paramref name="pending"/> collects the blobs to upload first.
    /// </summary>
    internal static TableEntity ToTableEntity(
        object root,
        string partitionKey,
        string rowKey,
        ValueWriteContext ctx,
        List<PendingBlob> pending)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(pending);
        return Build(root, partitionKey, rowKey, persistType: false, ctx, pending);
    }

    private static TableEntity Build(
        object root, string partitionKey, string rowKey, bool persistType, ValueWriteContext ctx, List<PendingBlob> pending)
    {
        var entity = new TableEntity(partitionKey, rowKey);
        if (root == null) return entity;

        var flat = Flatten(root, persistType, new FlattenState(ctx, pending, Partial: false, partitionKey, rowKey));
        foreach (var kv in flat)
            entity[kv.Key] = kv.Value;

        return entity;
    }

    /// <summary>Deserialize into a new <typeparamref name="T"/> (requires a public parameterless ctor).</summary>
    public static T FromTableEntity<T>(this TableEntity entity)
        where T : new()
        => FromTableEntity<T>(entity, ValueReadContext.None);

    /// <summary>Deserialize into a new <typeparamref name="T"/> with values a read resolved outside the row.</summary>
    internal static T FromTableEntity<T>(TableEntity entity, ValueReadContext ctx)
        where T : new()
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(ctx);
        return (T)Materialize(entity, typeof(T), restoreId: true, ctx);
    }

    /// <summary>
    /// True when <paramref name="row"/> holds any cell of a split or offloaded value
    /// (<c>{Prop}__c{n}</c>, <c>__chunks</c>, <c>__sha256</c>, <c>__enc</c>, <c>__len</c>, <c>__blob</c>).
    /// </summary>
    internal static bool HasLargeValueCells(TableEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        foreach (var column in row.Keys)
        {
            if (SplitCodec.TryParseLargeValueColumn(column, out _, out _, out _))
                return true;
        }
        return false;
    }

    /// <summary>The message for a read of an offloaded value that nothing resolved.</summary>
    internal static string UnresolvedOffloadMessage(string column) =>
        $"Column '{column}' holds a value offloaded to blob storage ('{column}{SplitCodec.BlobSuffix}'), and this read " +
        "cannot fetch it. Read the row through an IStorage<T> whose entity type marks the property " +
        "[TableValueSplit] (or declares it as TableBlobContent), with UnifiedTableStorageOptions.BlobOffload " +
        "configured. Returning the property empty would let the next write drop the reference.";

    /// <summary>The message for a polymorphic write of an item that needs large-value cells.</summary>
    internal static string PolymorphicWriteRefusal(Type itemType) =>
        $"{itemType.Name} carries a value too large for one cell or a TableBlobContent. Polymorphic stores cannot write " +
        "split or offloaded values; store this type through IStorage<T> instead.";

    /// <summary>The message for a large value reached by a partial (Merge) update.</summary>
    internal static string PartialUpdateRefusal(string name) =>
        $"'{name}' is a large value; partial updates cannot write it — replace the entity instead.";

    /// <summary>Late-bound deserialize; requires the row to have been written with <c>persistType: true</c>.</summary>
    /// <remarks>
    /// This overload performs <b>no base-type check</b>: it resolves whatever assembly-qualified name
    /// the row's <c>_TypeName</c> cell holds and materializes it, so the stored bytes choose the type
    /// to construct. Prefer
    /// <see cref="FromTableEntity{TBase}(TableEntity, ITypeDiscriminator)"/>, which resolves through
    /// an <see cref="ITypeDiscriminator"/> and enforces assignability to <c>TBase</c> — a gate no
    /// configuration can disable — or <see cref="TryFromTableEntity{TBase}"/> when the row may be a
    /// typeless marker. Kept as-is for rows and call sites that predate the discriminator seam.
    /// </remarks>
    public static object FromTableEntity(this TableEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.TryGetValue(TypeNameColumnName, out var tn)
            || tn is not string asmQName)
        {
            throw new InvalidOperationException($"Missing '{TypeNameColumnName}' column.");
        }

        // Resolve through the discriminator's resolver rather than a bare Type.GetType so the
        // registered legacy namespace renames apply here too: a row whose type moved keeps reading
        // through the same configuration the polymorphic stores use.
        var t = AssemblyQualifiedTypeDiscriminator.ResolveTypeName(asmQName)
                ?? throw new TypeLoadException($"Type '{asmQName}' not found.");

        return Materialize(entity, t, restoreId: true, ValueReadContext.None);
    }

    /// <summary>
    /// Late-bound deserialize constrained to a base type: resolves the stored discriminator through
    /// <paramref name="discriminator"/> and returns the TRUE derived instance typed as
    /// <typeparamref name="TBase"/>.
    /// </summary>
    /// <typeparam name="TBase">The base type the row must materialize as.</typeparam>
    /// <param name="entity">The row to read.</param>
    /// <param name="discriminator">The resolver for the stored token.</param>
    /// <returns>The materialized object.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="discriminator"/> is null.
    /// </exception>
    /// <exception cref="TypeLoadException">The stored token could not be resolved.</exception>
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
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="discriminator"/> is null.
    /// </exception>
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

        result = (TBase)Materialize(entity, type, restoreId: false, ValueReadContext.None);
        return true;
    }

    // The shared read loop behind all four FromTableEntity overloads (TryFromTableEntity<TBase> and
    // the FromTableEntity<TBase> that delegates to it share ONE call here, not a loop each). Extracted
    // rather than copied a third time: the truncation-marker skip, the system-column skip and the
    // alias pass must stay in lockstep, and the two PRE-EXISTING copies had already drifted once (one
    // loop's explanatory comment on the truncation-marker check was missing from the other, until an
    // earlier fix resynced them).
    private static object Materialize(TableEntity entity, Type type, bool restoreId, ValueReadContext ctx)
    {
        var meta = TypeMetadataCache.GetMetadata(type);
        var result = meta.Creator();

        // Cells of split values, grouped by their flattened base column ("Inner_Body").
        Dictionary<string, Dictionary<string, object>>? largeValues = null;

        foreach (var kv in entity)
        {
            // A column that is EXACTLY "__Json"/"__GZip"/"__Truncated" (no property name in front of
            // it) can only be a legacy artifact: a version older than the write-side Path.IsEmpty
            // guard blobbed (or trimmed/dropped) a whole root object with no usable ctor into a single
            // cell. Neither this nor any prior skip below may treat it as ordinary metadata — that
            // would silently hand back an empty, no-data object where every earlier release threw.
            // Checked before IsTruncationMarker specifically so a bare "__Truncated" is caught here
            // too, not silently skipped as if it were an ordinary per-property truncation note.
            if (TableEntityValue.IsBareFormatColumn(kv.Key))
            {
                throw new SerializationException(
                    $"Column '{kv.Key}' cannot be read: the row was written by a version that " +
                    "serialized the whole object as a single cell instead of columns, and this " +
                    "version has no properties to put that data back into. Re-write the row with the " +
                    "current serializer.");
            }

            // A __Truncated marker (on a real property, e.g. "Tags__Truncated") is metadata about a
            // trimmed/dropped cell, not data — feeding it through SetProperty would drill into (and
            // materialize) the property it describes.
            if (TableEntityValue.IsTruncationMarker(kv.Key))
                continue;

            // A split value's cells ("Body__c0", "Body__chunks", ...) are parts of one value, not
            // properties: collect them and reassemble each value once the row has been read.
            if (SplitCodec.TryParseLargeValueColumn(kv.Key, out var baseColumn, out _, out _))
            {
                largeValues ??= new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                if (!largeValues.TryGetValue(baseColumn, out var cells))
                    largeValues[baseColumn] = cells = new Dictionary<string, object>(StringComparer.Ordinal);
                cells[kv.Key] = kv.Value;
                continue;
            }

            // A leading '_' marks a column the storage layer owns. It must never reach a property
            // setter: TableEntityValue.Create strips the prefix, so "_TypeName" resolves to path
            // ["TypeName"] and "_IsPublished" to ["IsPublished"] — a stored type declaring either
            // property was silently receiving the storage layer's value.
            if (SystemColumnNames.IsSystemColumn(kv.Key))
                continue;

            result = SetProperty(result, TableEntityValue.Create(kv.Key, kv.Value));
        }

        if (largeValues is not null)
            result = ApplyLargeValues(result, type, largeValues, ctx);

        result = ApplyColumnAliases(result, entity, meta);

        // A polymorphic row's keys encode an aggregate version or a tick count, not an id — so
        // recomputing Id from them would overwrite the object's real id with "partition|row".
        return restoreId ? RestoreIdFromKeys(result, entity) : result;
    }

    // Reassembles every split value (verifying its SHA-256) and sets it on its property. A value whose
    // property no longer exists on the type is skipped unread, like any other unknown column. A group
    // holding a blob reference takes its value from what the read resolved outside the row: a
    // downloaded value, or a lazy TableBlobContent. A reference the read's projection excludes keeps
    // the property's default; any other unresolved one (a plain FromTableEntity, or a store whose type
    // is not large-value-aware) throws, because an entity with the property silently empty would drop
    // the reference on its next write and the orphan sweep would then delete the value.
    private static object ApplyLargeValues(
        object result, Type type, Dictionary<string, Dictionary<string, object>> largeValues, ValueReadContext ctx)
    {
        foreach (var (baseColumn, cells) in largeValues)
        {
            // A '_' base is a storage-layer column, never a property.
            if (SystemColumnNames.IsSystemColumn(baseColumn))
                continue;

            var path = TableEntityValue.Create(baseColumn, string.Empty).Path;
            if (TypeMetadataCache.ResolvePath(type, path) is not { } target)
                continue;

            object decoded;
            if (cells.TryGetValue(baseColumn + SplitCodec.BlobSuffix, out var reference))
            {
                if (ctx.Resolved.TryGetValue(baseColumn, out var resolved))
                {
                    decoded = resolved;
                }
                else if (target.PropertyType == typeof(TableBlobContent) && ctx.LazyContent is { } lazy)
                {
                    var parsed = Providers.BlobReference.Parse(baseColumn,
                        reference as string ?? throw new TableValueIntegrityException(baseColumn, "the blob reference is not a string."));
                    result = SetProperty(result, new TableEntityValue(path, lazy(baseColumn, parsed)));
                    continue;
                }
                else if (ctx.Projection is { } projected && !ValueOffloadCoordinator.Projects(projected, baseColumn))
                {
                    continue;
                }
                else
                {
                    throw new InvalidOperationException(UnresolvedOffloadMessage(baseColumn));
                }
            }
            else
            {
                decoded = SplitCodec.Decode(baseColumn, cells);
            }

            var fmt = target.PropertyType == typeof(string) || target.PropertyType == typeof(byte[])
                ? TableEntityValueFormat.Raw
                : TableEntityValueFormat.Json;
            result = SetProperty(result, new TableEntityValue(path, decoded, fmt));
        }
        return result;
    }

    // The row's PartitionKey/RowKey are the authoritative identity. A legacy row written by another
    // serializer has no "Id" column (Id would read back empty), and a legacy single-segment id would
    // split as the wrong keys on the next write — so whenever the row carries real keys and the
    // stored column does not already address them, recompute the composite id from the keys. A
    // stored id that Splits to these exact keys is kept verbatim, so explicit forms like "a|a"
    // round-trip byte-identically. Rows serialized without keys (plain dictionary round-trips) keep
    // whatever the "Id" column said. (A PartitionKey that itself contains '|' cannot be expressed
    // in the single-separator id grammar; the stored column — if consistent — is the only handle.)
    private static object RestoreIdFromKeys(object result, TableEntity entity)
    {
        if (result is not IEntity e
            || string.IsNullOrEmpty(entity.PartitionKey)
            || string.IsNullOrEmpty(entity.RowKey))
        {
            return result;
        }

        if (!string.IsNullOrEmpty(e.Id)
            && EntityId.Split(e.Id) == (entity.PartitionKey, entity.RowKey))
        {
            return result; // the stored id already addresses this exact row — keep it verbatim
        }

        e.Id = entity.PartitionKey == entity.RowKey
            ? entity.PartitionKey
            : EntityId.Combine(entity.PartitionKey, entity.RowKey);
        return result;
    }

    // Mirrors TableEntityValue's private cell-format suffixes — an alias column may carry any of
    // the three representations a canonical column can.
    private static readonly string[] AliasSuffixes = ["", "__Json", "__GZip"];

    /// <summary>
    /// Second pass for <see cref="ColumnAliasAttribute"/>: a legacy (alias) column deserializes
    /// into its property only when no canonical column for that property exists on the row, so
    /// canonical data always wins and rewrites converge rows to the canonical schema.
    /// </summary>
    private static object ApplyColumnAliases(object result, TableEntity entity, TypeMetadata meta)
    {
        if (meta.AliasMap.Count == 0)
            return result;

        foreach (var kv in meta.AliasMap)
        {
            var aliasColumn = kv.Key;
            var property = kv.Value;

            var hasCanonical = false;
            foreach (var suffix in AliasSuffixes)
            {
                if (entity.ContainsKey(property.Name + suffix))
                {
                    hasCanonical = true;
                    break;
                }
            }
            if (hasCanonical
                || entity.ContainsKey(property.Name + SplitCodec.ChunksSuffix)
                || entity.ContainsKey(property.Name + SplitCodec.BlobSuffix))
                continue;

            foreach (var suffix in AliasSuffixes)
            {
                if (entity.TryGetValue(aliasColumn + suffix, out var raw) && raw is not null)
                {
                    // Re-key the cell to the canonical name (+ its format suffix) and reuse the
                    // normal deserialization path.
                    var val = TableEntityValue.Create(property.Name + suffix, raw);
                    result = SetProperty(result, val);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Flatten a single named property/value into the column(s) it occupies on a row. Top-level
    /// scalars produce one entry; nested complex types fan out to <c>Parent_Child</c> columns just
    /// like the full <see cref="ToTableEntity(object, string, string, bool)"/> path. Used by partial-update (Merge) flows and by
    /// alternative <see cref="IStorage{T}"/> implementations (e.g. in-memory test doubles).
    /// </summary>
    /// <remarks>
    /// A large value cannot be written by a partial update: its split cells and continuation rows
    /// must be replaced together. A nested object that carries a <see cref="TableValueSplitAttribute"/>
    /// member throws <see cref="NotSupportedException"/>.
    /// </remarks>
    /// <exception cref="NotSupportedException"><paramref name="value"/> carries a large-value member.</exception>
    public static Dictionary<string, object> FlattenProperty(string propertyName, object value)
    {
        var dict = new Dictionary<string, object>();
        if (value == null) return dict;

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var node = new TableEntityValue(ImmutableList<string>.Empty.Add(propertyName), value);
        var state = new FlattenState(ValueWriteContext.Public, [], Partial: true, string.Empty, string.Empty);
        if (!Flatten(dict, seen, node, state, split: null))
            throw new InvalidOperationException($"Cannot flatten property '{propertyName}' of type {value.GetType()}.");

        return dict;
    }

    /// <summary>
    /// <see cref="FlattenProperty(string, object)"/> for a column path of <paramref name="ownerType"/>
    /// (<c>"Inner_Body"</c>): refuses a path that reaches a large-value member, which the untyped
    /// overload cannot see.
    /// </summary>
    /// <exception cref="NotSupportedException">The path names a large-value member.</exception>
    internal static Dictionary<string, object> FlattenProperty(Type ownerType, string columnPath, object value)
    {
        ArgumentNullException.ThrowIfNull(ownerType);
        var owner = ownerType;
        foreach (var segment in columnPath.Split(TableEntityValue.PathDelimiter, StringSplitOptions.RemoveEmptyEntries))
        {
            var meta = TypeMetadataCache.GetMetadata(owner);
            if (meta.SplitOptions.ContainsKey(segment))
                throw new NotSupportedException(PartialUpdateRefusal(columnPath));
            if (!meta.PropertyMap.TryGetValue(segment, out var prop))
                break;
            if (prop.PropertyType == typeof(TableBlobContent))
                throw new NotSupportedException(PartialUpdateRefusal(columnPath));
            owner = prop.PropertyType;
        }
        return FlattenProperty(columnPath, value);
    }

    /// <summary>
    /// Whether a complex type is FLATTENED into <c>Parent_Child</c> columns (the serializer recurses
    /// into it) rather than stored as a single JSON/GZip cell. Mirrors the serializer's own decision so
    /// callers (e.g. the LINQ filter translator) can tell whether an <c>Owner_Child</c> column exists at
    /// all for a nested member access.
    /// </summary>
    internal static bool FlattensToColumns(Type type)
    {
        // An IEnumerable (other than string) is ALWAYS stored as a single JSON cell, whatever the
        // runtime type — so this is statically decidable and must be checked first, even for an
        // interface/abstract collection type (e.g. IList<T>), before the can't-determine fallback below.
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
            return false; // collections serialize to JSON, not columns
        // Non-enumerable interfaces and abstract types never appear as a runtime VALUE type — the
        // serializer decides flatten-vs-JSON from the concrete instance at write time, which we cannot
        // see here. Their (public) ctor list is empty, which would otherwise read as "JSON". Don't claim
        // JSON on that artifact: a polymorphic-owner filter (e.g. IShape Shape = new Circle()) whose
        // runtime type really does flatten to Owner_Child columns must not be rejected. Assume flattenable.
        if (type.IsInterface || type.IsAbstract)
            return true;
        var ctors = type.GetConstructors();
        if (ctors.Length == 0)
            return false;
        if (ctors.All(c => c.GetParameters().Length > 0))
            return false; // positional records / parameterized-only DTOs -> JSON
        if (ctors.Any(c => c.GetCustomAttributes(typeof(JsonConstructorAttribute), true).Length > 0))
            return false;
        return true;
    }

    //───────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, object> Flatten(object root, bool persistType, FlattenState state)
    {
        var dict = new Dictionary<string, object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var top = new TableEntityValue(ImmutableList<string>.Empty, root);

        if (!Flatten(dict, seen, top, state, split: null))
            throw new InvalidOperationException($"Cannot flatten object of type {root.GetType()}.");

        if (persistType)
            dict[TypeNameColumnName] = root.GetType().AssemblyQualifiedName!;

        return dict;
    }

    private static bool Flatten(
        Dictionary<string, object> dict,
        ISet<object> seen,
        TableEntityValue value,
        FlattenState state,
        TableValueSplitAttribute? split)
    {
        // nulls are fine
        if (value.Value == null) return true;

        // primitive/json/gzip cell (a null cell means the property was dropped because even its
        // compressed form exceeds the 64 KB cell cap — omit the column, keep the row). An opted-in
        // value too large for one cell comes back as its split cells instead.
        if (value.TrySerialize(out var col, out var cell, out var largeCells, split, state))
        {
            if (largeCells != null)
            {
                foreach (var kv in largeCells)
                    dict[kv.Key] = kv.Value;
            }
            else if (cell != null)
                dict[col] = cell;
            // Under OversizedCellPolicy.TrimWithMarker, a trimmed/dropped payload leaves a visible
            // trace in the row itself; FromTableEntity explicitly skips __Truncated columns on read.
            if (value.TruncationNote is { } note)
                dict[value.TruncationMarkerColumn] = note;
            return true;
        }

        // A collection at the TRUE root (empty Path) has no properties to flatten into. TrySerialize
        // above declined to JSON-blob it (Path.IsEmpty), so without this check we'd fall through to
        // "recurse into cached properties" below and hit List<T>'s Item INDEXER: it passes the
        // CanRead/CanWrite filter in TypeMetadataCache, but PropertyInfo.GetValue with no index args
        // throws TargetParameterCountException — a raw reflection leak, not a serializer diagnostic. A
        // NESTED collection never reaches this line: its Path is non-empty, so TrySerialize's own
        // IEnumerable branch already routed it to one JSON cell before this point. Fail the same way
        // FlattenProperty already does for an unflattenable value — the caller (Flatten(object, bool))
        // turns this into "Cannot flatten object of type ...".
        if (value.Path.IsEmpty && value.Type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(value.Type!))
            return false;

        // cycle detection
        if (!value.Type!.IsValueType && !seen.Add(value.Value))
            throw new SerializationException($"Circular reference at '{value}'.");

        // recurse into cached properties
        var meta = TypeMetadataCache.GetMetadata(value.Type);
        foreach (var prop in meta.Properties)
        {
            // '_' is the property-path delimiter: "Foo_Bar" writes the column "Foo_Bar", which reads
            // back as the path ["Foo", "Bar"] — a nested property that does not exist — so the cell is
            // silently dropped on the way in. There is no encoding that would disambiguate it after
            // the fact, so the write fails loudly instead of the read losing data quietly.
            if (TableEntityValue.ContainsPathDelimiter(prop.Name))
                throw new SerializationException(
                    $"Property '{value.Type.Name}.{prop.Name}' contains the property-path delimiter " +
                    $"'{TableEntityValue.PathDelimiter}', which would make its column ambiguous with a " +
                    "nested property. Rename the property or map it to another column name.");

            meta.SplitOptions.TryGetValue(prop.Name, out var childSplit);
            var childVal = prop.GetValue(value.Value);
            if (state.Partial && (childSplit != null || prop.PropertyType == typeof(TableBlobContent) || childVal is TableBlobContent))
                throw new NotSupportedException(
                    PartialUpdateRefusal(string.Join(TableEntityValue.PathDelimiter, value.Path.Add(prop.Name))));

            var childNode = new TableEntityValue(value.Path.Add(prop.Name), childVal!);
            if (!Flatten(dict, seen, childNode, state, childSplit))
                return false;
        }

        if (!value.Type.IsValueType)
            seen.Remove(value.Value);

        return true;
    }

    /// <summary>
    /// Every <see cref="TableBlobContent"/> reachable through the columns <paramref name="root"/>
    /// flattens into, with its flattened base column (<c>Inner_Content</c>). Contents inside
    /// collections or JSON-stored objects are not listed: the write refuses those.
    /// </summary>
    internal static List<(string Column, TableBlobContent Content)> FindBlobContents(object root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var found = new List<(string, TableBlobContent)>();
        CollectBlobContents(root, string.Empty, new HashSet<object>(ReferenceEqualityComparer.Instance), found);
        return found;
    }

    /// <summary>True when <paramref name="item"/> holds a <see cref="TableBlobContent"/> in a flattened column.</summary>
    /// <remarks>
    /// The cached type check keeps every plain type off the reflection walk. A content reachable only
    /// through a runtime subtype is missed here, but the write still refuses it (OversizedValueException).
    /// </remarks>
    internal static bool CarriesBlobContent(object item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return TypeMetadataCache.IsLargeValueAware(item.GetType()) && FindBlobContents(item).Count > 0;
    }

    // Mirrors Flatten: recurse exactly where Flatten recurses (a non-primitive, non-enum object that is
    // not JSON-stored); a TableBlobContent is a leaf.
    private static void CollectBlobContents(object owner, string prefix, HashSet<object> seen, List<(string, TableBlobContent)> found)
    {
        var isReference = !owner.GetType().IsValueType;
        if (isReference && !seen.Add(owner))
            return;
        foreach (var prop in TypeMetadataCache.GetMetadata(owner.GetType()).Properties)
        {
            var value = prop.GetValue(owner);
            if (value is null) continue;
            var column = prefix + prop.Name;
            if (value is TableBlobContent content)
                found.Add((column, content));
            else if (value is not string && !TableEntityValue.IsPrimitive(value.GetType()) && !value.GetType().IsEnum
                     && TableEntityValue.CanSerializeWithoutJson(value))
                CollectBlobContents(value, column + TableEntityValue.PathDelimiter, seen, found);
        }
        if (isReference)
            seen.Remove(owner);
    }

    /// <summary>The <see cref="TableValueSplitAttribute"/> of a flattened base column of <paramref name="type"/>, or null.</summary>
    internal static TableValueSplitAttribute? SplitOptionFor(Type type, string baseColumn)
    {
        var path = TableEntityValue.Create(baseColumn, string.Empty).Path;
        if (path.Count == 0) return null;
        var owner = type;
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (!TypeMetadataCache.GetMetadata(owner).PropertyMap.TryGetValue(path[i], out var prop))
                return null;
            owner = prop.PropertyType;
        }
        return TypeMetadataCache.GetMetadata(owner).SplitOptions.TryGetValue(path[^1], out var split) ? split : null;
    }

    private static object SetProperty(object root, TableEntityValue val)
    {
        try
        {
            object? cur = root;
            var path = val.Path;
            var parents = new Stack<(PropertyInfo Prop, object Owner)>();

            // drill into nested structs/objects
            for (int i = 0; i < path.Count - 1; i++)
            {
                var ownerMeta = TypeMetadataCache.GetMetadata(cur!.GetType());
                if (!ownerMeta.PropertyMap.TryGetValue(path[i], out var pi))
                { cur = null; break; }

                var child = pi.GetValue(cur)
                          ?? TypeMetadataCache.GetMetadata(pi.PropertyType).Creator();

                // queue struct owners for re-assign
                if (pi.PropertyType.IsValueType)
                    parents.Push((pi, cur));

                pi.SetValue(cur, child);
                cur = child;
            }

            // final leaf
            if (cur != null)
            {
                var leafMeta = TypeMetadataCache.GetMetadata(cur.GetType());
                if (leafMeta.PropertyMap.TryGetValue(path[^1], out var leafProp))
                {
                    val.SetTo(leafProp, cur);
                }
            }

            // reassign structs up the chain
            while (parents.Count > 0)
            {
                var (pi, owner) = parents.Pop();
                // `cur` now holds the updated child
                pi.SetValue(owner, cur);
                cur = owner;
            }

            return root;
        }
        catch (Exception ex)
        {
            throw new SerializationException($"Failed to set '{val}'.", ex);
        }
    }

    // simple reference comparer for cycle detection
    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
        int IEqualityComparer<object>.GetHashCode(object obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>
/// One flatten pass: the write context, the blobs it collects, whether it serves a partial (Merge)
/// update (which must refuse large values), and the row keys blob names are built from.
/// </summary>
internal readonly record struct FlattenState(
    ValueWriteContext Ctx, List<PendingBlob> Pending, bool Partial, string PartitionKey, string RowKey);

internal enum TableEntityValueFormat { Raw, Json, GZip }

internal sealed class TableEntityValue
{
    private const int MaxCellBytes = 65536;
    private const string Delim = "_";

    /// <summary>The property-path delimiter, for callers that must reject it inside a name.</summary>
    internal static string PathDelimiter => Delim;

    /// <summary>True when <paramref name="name"/> would be ambiguous with a nested property path.</summary>
    internal static bool ContainsPathDelimiter(string name) => name.Contains(Delim, StringComparison.Ordinal);
    private const string JsonSuffix = "__Json";
    private const string GZipSuffix = "__GZip";
    private const string TruncatedSuffix = "__Truncated";
    private static readonly DateTimeOffset MinDto =
        new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // camelCase property names; reads stay case-insensitive.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Enum values are written using the declared member name (PascalCase) — the default of
        // both System.Text.Json and Newtonsoft.Json — so stored tokens stay stable and
        // byte-compatible with name-as-declared serializers. Reads remain case-insensitive, so
        // lowercase/camelCase tokens written by <= 0.5.0 still round-trip.
        // JsonStringEnumConverter: see the comment above. ParameterizedConstructorConverterFactory
        // restores construction of immutable types whose constructor System.Text.Json would not
        // select (a private foreign-annotated one) or would bind too strictly (parameters that do
        // not exactly match property names and types) — both idiomatic historical shapes, and ones
        // that appear throughout rows written by the serializers this format is compatible with.
        Converters = { new RefuseBlobContentConverter(), new JsonStringEnumConverter(), new ParameterizedConstructorConverterFactory() },
        // Allow full Unicode so Cyrillic (and other) characters aren't \uXXXX escaped in stored JSON
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly Dictionary<Type, Func<object, object?>> Primitives = new()
    {
        { typeof(string), v => (string)v },
        { typeof(byte[]), v => (byte[])v },
        { typeof(byte), v => new byte[]{(byte)v} },
        { typeof(bool), v => (bool)v },
        { typeof(bool?), v => (bool?)v },
        { typeof(DateTime), v =>
            {
                var dt = (DateTime)v;
                if (dt == default) dt = MinDto.DateTime;
                return new DateTimeOffset(NormalizeToUtc(dt), TimeSpan.Zero);
            }
        },
        { typeof(DateTime?), v =>
            {
                var dt = ((DateTime?)v) ?? MinDto.DateTime;
                return new DateTimeOffset(NormalizeToUtc(dt), TimeSpan.Zero);
            }
        },
        { typeof(DateTimeOffset), v =>
            {
                var dto = (DateTimeOffset)v;
                return dto == default ? MinDto : dto;
            }
        },
        { typeof(DateTimeOffset?), v =>
            {
                var dto = ((DateTimeOffset?)v) ?? MinDto;
                return dto;
            }
        },
        { typeof(double), v => (double)v },
        { typeof(double?), v => (double?)v },
        { typeof(decimal),  v => (double)(decimal)v },
        { typeof(decimal?), v => ((decimal?)v) is decimal d ? (double?)d : null },
        { typeof(Guid), v => (Guid)v },
        { typeof(Guid?), v => (Guid?)v },
        { typeof(int), v => (int)v },
        { typeof(int?), v => (int?)v },
        { typeof(uint), v => (long)(uint)v },
        { typeof(uint?), v => (long?)((uint?)v) },
        { typeof(long), v => (long)v },
        { typeof(long?), v => (long?)v },
        { typeof(ulong), v => (long)Convert.ToUInt64(v, CultureInfo.InvariantCulture) },
        { typeof(ulong?), v => (long?)Convert.ToUInt64(((ulong?)v)!.Value, CultureInfo.InvariantCulture) },
        { typeof(TimeSpan), v => ((TimeSpan)v).ToString() },
        { typeof(TimeSpan?), v => ((TimeSpan?)v)?.ToString() }
    };

    public ImmutableList<string> Path { get; }
    public object? Value { get; }
    public Type? Type { get; }
    public TableEntityValueFormat Format { get; private set; }

    /// <summary>
    /// Set by <see cref="TrySerialize"/> when the oversized-cell policy trimmed (or dropped) this
    /// value — human-readable "kept X of Y" text the caller writes into the sibling
    /// <see cref="TruncationMarkerColumn"/> cell. Null when nothing was lost.
    /// </summary>
    public string? TruncationNote { get; private set; }

    /// <summary>The marker column recording a truncation, e.g. <c>Tags__Truncated</c>.</summary>
    public string TruncationMarkerColumn => string.Join(Delim, Path) + TruncatedSuffix;

    /// <summary>Whether a column is a truncation marker — metadata that must be skipped on read.</summary>
    internal static bool IsTruncationMarker(string column) =>
        column.EndsWith(TruncatedSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Whether a column name is EXACTLY a cell-format/truncation suffix, with no property name in
    /// front of it. TrySerialize never produces this shape for a real property — a column is always
    /// "PropertyName" plus, optionally, one of these suffixes — so the only way it reaches a row is a
    /// pre-write-side-fix version that blobbed a whole root object (no usable ctor, or a bare
    /// collection) into one cell. That row can never be read back into properties: Materialize must
    /// diagnose it rather than let it collide with the (unrelated) '_' system-column prefix.
    /// </summary>
    internal static bool IsBareFormatColumn(string column) =>
        column is JsonSuffix or GZipSuffix or TruncatedSuffix;

    public TableEntityValue(
        ImmutableList<string> path,
        object? value,
        TableEntityValueFormat fmt)
    {
        Path = path;
        Value = value;
        Type = value?.GetType();
        Format = fmt;
    }

    public TableEntityValue(
        ImmutableList<string> path,
        object? value)
        : this(path, value, TableEntityValueFormat.Raw) { }

    public static TableEntityValue Create(string col, object val)
    {
        var fmt = TableEntityValueFormat.Raw;
        if (col.EndsWith(JsonSuffix, StringComparison.Ordinal))
        {
            col = col[..^JsonSuffix.Length];
            fmt = TableEntityValueFormat.Json;
        }
        else if (col.EndsWith(GZipSuffix, StringComparison.Ordinal))
        {
            col = col[..^GZipSuffix.Length];
            fmt = TableEntityValueFormat.GZip;
        }
        var segs = col.Split(Delim, StringSplitOptions.RemoveEmptyEntries);
        var path = ImmutableList<string>.Empty.AddRange(segs);
        return new TableEntityValue(path, val, fmt);
    }

    /// <summary>Whether <paramref name="type"/> is stored natively by the primitive map.</summary>
    internal static bool IsPrimitive(Type type) => Primitives.ContainsKey(type);

    /// <summary>
    /// True if this value can be stored directly as a table cell. Otherwise it will be flattened by
    /// the caller. A <c>true</c> result with a <c>null</c> out-value means the property was dropped
    /// because even its compressed form exceeds the cell cap.
    /// </summary>
    /// <param name="columnName">The column the value occupies.</param>
    /// <param name="value">The single cell, or null when the value was dropped or split.</param>
    /// <param name="largeCells">
    /// For an opted-in value too large for one cell: its split cells and manifest, which replace the
    /// single cell. Null otherwise.
    /// </param>
    /// <param name="split">The property's <see cref="TableValueSplitAttribute"/>; null takes today's path.</param>
    /// <param name="state">The flatten pass (write context and pending blobs); null behaves as the public serializer.</param>
    public bool TrySerialize(
        out string columnName,
        out object? value,
        out IReadOnlyList<KeyValuePair<string, object>>? largeCells,
        TableValueSplitAttribute? split = null,
        FlattenState? state = null)
    {
        columnName = string.Join(Delim, Path);
        value = null;
        largeCells = null;
        if (Value == null) return false;

        if (Value is TableBlobContent blob)
            return SerializeBlobContent(blob, state, columnName, out value, out largeCells);

        if (split is not null && TrySerializeLarge(split, state, ref columnName, out value, out largeCells))
            return true;

        var vt = Value.GetType();
        if (Primitives.TryGetValue(vt, out var fac))
        {
            value = fac(Value);

            // Azure Tables caps a string cell at 64 KB (UTF-16); an oversized property fails the
            // WHOLE entity insert (PropertyValueTooLarge) and the row is lost. Compress oversized
            // strings losslessly (the __GZip read path round-trips them), truncating only when even
            // the compressed form cannot fit — a capped cell beats a lost entity.
            if (value is string s && Encoding.Unicode.GetByteCount(s) > MaxCellBytes)
            {
                var compressed = Compress(JsonSerializer.Serialize(s, JsonOptions));
                if (Encoding.Unicode.GetByteCount(compressed) <= MaxCellBytes)
                {
                    columnName += GZipSuffix;
                    Format = TableEntityValueFormat.GZip;
                    value = compressed;
                }
                else
                {
                    // Even compressed it exceeds the cap — the oversized-cell policy decides.
                    if (TableEntitySerializer.OversizedCellPolicy == OversizedCellPolicy.Throw)
                        throw new OversizedCellException(
                            columnName, Encoding.Unicode.GetByteCount(compressed), MaxCellBytes);
                    value = TruncateForCell(s, out var keptChars);
                    if (TableEntitySerializer.OversizedCellPolicy == OversizedCellPolicy.TrimWithMarker)
                        TruncationNote = $"kept {keptChars} of {s.Length} chars";
                }
            }

            return true;
        }

        if (vt.IsEnum)
        {
            value = Value.ToString()!;
            return true;
        }

        // A root value (empty Path) that reaches this point is neither a primitive nor an enum (both
        // returned true above) — so it must never fall to the JSON branch below and become "the whole
        // row is one cell", even when the root type has no public parameterless ctor (the exact shape
        // a polymorphic base-constrained read reconstructs via the uninitialized-object fallback).
        // Without this, such a root would produce a column named by its bare "__Json"/"__GZip" suffix
        // — a name that starts with the reserved '_' system-column prefix by sheer coincidence of
        // string concatenation, so Materialize's system-column skip would silently swallow the whole
        // object. A root that is itself an IEnumerable (a collection) also lands here and is handled
        // one level up, in Flatten: it cannot be flattened into properties either (see the comment
        // there), so it must fail loudly rather than let this method's caller try.
        if (Path.IsEmpty || CanSerializeWithoutJson(Value))
            return false;

        // fallback to JSON / GZip
        var json = JsonSerializer.Serialize(Value, JsonOptions);
        var size = Encoding.Unicode.GetByteCount(json);
        if (size > MaxCellBytes)
        {
            json = Compress(json);
            columnName += GZipSuffix;
            Format = TableEntityValueFormat.GZip;

            // Even compressed, a huge high-entropy payload can exceed the 64 KB cell cap — and a
            // cell the service rejects loses the WHOLE row. The oversized-cell policy decides:
            // throw, or trim list payloads to the largest prefix that fits (as a last resort drop
            // the property — null cell = column omitted) with or without a __Truncated marker.
            if (Encoding.Unicode.GetByteCount(json) > MaxCellBytes)
            {
                if (TableEntitySerializer.OversizedCellPolicy == OversizedCellPolicy.Throw)
                    throw new OversizedCellException(
                        columnName, Encoding.Unicode.GetByteCount(json), MaxCellBytes);
                if (Value is IList list)
                {
                    json = CompressLargestFittingPrefix(list, out var keptItems);
                    if (TableEntitySerializer.OversizedCellPolicy == OversizedCellPolicy.TrimWithMarker)
                        TruncationNote = $"kept {keptItems} of {list.Count} items";
                }
                else
                {
                    json = null;
                    if (TableEntitySerializer.OversizedCellPolicy == OversizedCellPolicy.TrimWithMarker)
                        TruncationNote = "property dropped: compressed payload exceeds the 64 KB cell cap";
                }
            }
        }
        else
        {
            columnName += JsonSuffix;
            Format = TableEntityValueFormat.Json;
        }

        value = json;
        return true;
    }

    // An opted-in value: one ordinary cell when it fits (a string or byte[] raw, never __GZip; JSON
    // text as __Json), otherwise split cells. Never trims and never consults OversizedCellPolicy.
    // False for a value that is not string, byte[] or JSON-stored (a scalar, or an object that
    // flattens into columns) — it takes the ordinary path.
    private bool TrySerializeLarge(
        TableValueSplitAttribute split,
        FlattenState? state,
        ref string columnName,
        out object? value,
        out IReadOnlyList<KeyValuePair<string, object>>? largeCells)
    {
        value = null;
        largeCells = null;

        object payload;
        var isJson = false;
        switch (Value)
        {
            case string or byte[]:
                payload = Value;
                break;
            case not null when IsPrimitive(Value.GetType()) || Value.GetType().IsEnum:
                return false;
            case not null when Path.IsEmpty || CanSerializeWithoutJson(Value):
                return false;
            default:
                payload = JsonSerializer.Serialize(Value, JsonOptions);
                isJson = true;
                break;
        }

        if (SplitCodec.FitsOneCell(payload, split.Encoding))
        {
            if (isJson)
            {
                columnName += JsonSuffix;
                Format = TableEntityValueFormat.Json;
            }
            value = payload;
            return true;
        }

        // Tier 3 (blob offload): only for a storage write, never for a clone or the public serializer.
        if (state is { Ctx: { IsClone: false } ctx } st && (ctx.Offload is not null || ctx.ForceOffload.Count > 0))
        {
            var stored = SplitCodec.ToStored(payload, split.Encoding);
            var forced = ctx.ForceOffload.Contains(columnName);
            if (forced && split.Offload == BlobOffload.Never)
                throw new OversizedValueException(columnName, stored.Bytes.Length,
                    "the row does not fit one entity-group transaction and the property does not allow blob offload",
                    offloadForbiddenByProperty: ctx.Offload is not null);
            if (forced && ctx.Offload is null)
                throw new OversizedValueException(columnName, stored.Bytes.Length,
                    "the row does not fit one entity-group transaction and blob offload is not configured");
            if (split.Offload != BlobOffload.Never && (forced || (ctx.Offload is not null && stored.Bytes.Length > ctx.Offload.ThresholdBytes)))
            {
                var contentType = stored.Enc switch
                {
                    "text" or "gzip" => "text/plain; charset=utf-8",
                    "utf16le" => "text/plain; charset=utf-16le",
                    _ => "application/octet-stream",
                };
                var bytes = stored.Bytes;
                var reference = new Providers.BlobReference(
                    BlobName(ctx, st, columnName, stored.Sha256), stored.Sha256, bytes.Length, contentType, stored.Enc);
                st.Pending.Add(new PendingBlob(columnName, reference, stored.Enc == "gzip" ? "gzip" : null,
                    _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))));
                largeCells = [new(columnName + SplitCodec.BlobSuffix, reference.ToJson())];
                return true;
            }
            largeCells = SplitCodec.Encode(columnName, payload, stored);
            return true;
        }

        largeCells = SplitCodec.Encode(columnName, payload, split.Encoding);
        return true;
    }

    private static string BlobName(ValueWriteContext ctx, FlattenState state, string columnName, string sha256) =>
        ValueOffloadCoordinator.BlobName(
            ctx.TableName ?? throw new InvalidOperationException("A blob offload write needs the table name."),
            state.PartitionKey, state.RowKey, columnName, sha256);

    // A TableBlobContent is always one blob reference cell. A clone keeps the instance itself; a write
    // without blob offload, a partial update, or content that is not prepared cannot store it.
    private bool SerializeBlobContent(
        TableBlobContent blob,
        FlattenState? state,
        string columnName,
        out object? value,
        out IReadOnlyList<KeyValuePair<string, object>>? largeCells)
    {
        value = null;
        largeCells = null;
        if (Path.IsEmpty)
            throw new NotSupportedException("A TableBlobContent cannot be serialized as a whole entity.");
        if (state is { Partial: true })
            throw new NotSupportedException(TableEntitySerializer.PartialUpdateRefusal(columnName));
        var ctx = state?.Ctx ?? ValueWriteContext.Public;
        if (ctx.IsClone)
        {
            value = blob;
            return true;
        }
        if (ctx.Offload is null)
            throw new OversizedValueException(columnName, blob.IsSealed ? blob.Length : 0,
                "a TableBlobContent is always stored in blob storage and blob offload is not configured");
        if (!blob.IsSealed)
            throw new InvalidOperationException(
                $"The TableBlobContent of '{columnName}' was not prepared for writing (its length and hash are unknown).");

        var st = state!.Value;
        var reference = new Providers.BlobReference(
            BlobName(ctx, st, columnName, blob.Sha256), blob.Sha256, blob.Length, blob.ContentType, "binary");
        st.Pending.Add(new PendingBlob(columnName, reference, null, OpenSource(blob)) { Content = blob });
        largeCells = [new(columnName + SplitCodec.BlobSuffix, reference.ToJson())];
        return true;
    }

    // The bytes of in-memory content, the (rewound) prepared stream, or the blob the content is bound to.
    private static Func<CancellationToken, Task<Stream>> OpenSource(TableBlobContent blob)
    {
        if (blob.Bytes is { } bytes)
            return _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        if (blob.Source is { } source)
        {
            var origin = blob.SourceOrigin;
            return _ =>
            {
                if (source.CanSeek) source.Position = origin;
                // The caller (or the content's spool) owns the stream: hand out a non-closing view.
                return Task.FromResult<Stream>(new HashingReadStream(source, null, null, r => new InvalidOperationException(r), leaveOpen: true));
            };
        }
        return blob.OpenReadAsync;
    }

    // A TableBlobContent is stored as its own blob, never inside a collection or a JSON-stored object.
    private sealed class RefuseBlobContentConverter : JsonConverter<TableBlobContent>
    {
        private const string Message =
            "A TableBlobContent cannot be stored inside a collection or a JSON-stored object; make it a property of the entity or of a flattened object.";

        public override TableBlobContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException(Message);

        public override void Write(Utf8JsonWriter writer, TableBlobContent value, JsonSerializerOptions options) =>
            throw new NotSupportedException(Message);
    }

    public void SetTo(PropertyInfo prop, object owner)
    {
        if (prop == null) return;
        object? conv;

        if (Format == TableEntityValueFormat.Json)
        {
            conv = JsonSerializer.Deserialize((string)Value!, prop.PropertyType, JsonOptions);
        }
        else if (Format == TableEntityValueFormat.GZip)
        {
            var txt = Decompress((string)Value!);
            conv = JsonSerializer.Deserialize(txt, prop.PropertyType, JsonOptions);
        }
        else
        {
            conv = ConvertTo(prop.PropertyType, Value);
        }

        prop.SetValue(owner, conv);
    }

    internal static bool CanSerializeWithoutJson(object val)
    {
        if (val is IEnumerable) return false;
        var ctors = val.GetType().GetConstructors();
        if (ctors.Length == 0) return false;
        if (ctors.All(c => c.GetParameters().Length > 0)) return false;
        if (ctors.Any(c => c.GetCustomAttributes(typeof(JsonConstructorAttribute), true).Length > 0))
            return false;
        return true;
    }

    private static object? ConvertTo(Type tgt, object? val)
    {
        if (val == null) return null;
        // An in-process clone carries a TableBlobContent cell by reference.
        if (val is TableBlobContent) return val;
        var t = Nullable.GetUnderlyingType(tgt) ?? tgt;

        if (t.IsEnum)
            return Enum.Parse(t, val.ToString()!);

        if (val is DateTimeOffset dto)
            return ConvertDateTimeOffset(t, dto);

        // Azure Tables may surface a stored date as a boxed DateTime (legacy rows / SDK behavior).
        // DateTimeOffset does not implement IConvertible, so the generic Convert.ChangeType fallback
        // below throws for a DateTimeOffset target — handle DateTime explicitly so reads never fail
        // on either date target.
        if (val is DateTime dt)
            return ConvertDateTime(t, dt);

        if (val is long l)
        {
            // Fall through to Convert.ChangeType for non-int64 targets (double, decimal, short…)
            // — a long cell can face a property whose type drifted across versions.
            var converted = ConvertLong(t, l);
            if (converted is not null)
                return converted;
        }

        // String cells targeting a date type: parse explicitly. Convert.ChangeType can never produce
        // a DateTimeOffset, and this also round-trips dates written by older serializers.
        if (val is string ds)
        {
            if (t == typeof(DateTimeOffset))
                return DateTimeOffset.Parse(ds, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t == typeof(DateTime))
                return DateTime.Parse(ds, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (t == typeof(TimeSpan))
            return TimeSpan.Parse(val.ToString()!, CultureInfo.InvariantCulture);

        if (t == typeof(Guid))
            return Guid.Parse(val.ToString()!);

        if (t == typeof(byte[]))
            return (byte[])val;

        // A scalar byte is STORED as a one-element byte[] (Edm.Binary has no narrower form) — see
        // the Primitives map. Without this inverse the write is one-way: the Convert.ChangeType
        // fallback below throws "Object must implement IConvertible" for a byte[] source, so every
        // byte property is writable and unreadable. `t` is already the underlying type, so this
        // covers byte? too.
        if (t == typeof(byte) && val is byte[] { Length: > 0 } singleByte)
            return singleByte[0];

        if (t == typeof(decimal))
            return Convert.ToDecimal(val, CultureInfo.InvariantCulture);

        return Convert.ChangeType(val, t, CultureInfo.InvariantCulture);
    }

    // Azure Tables cannot store a date below 1601-01-01, so the write side maps an unset date to
    // that sentinel (see the DateTime/DateTimeOffset entries in the Primitives map). This is the
    // inverse. Without it an unset date round-trips as 1601-01-01 and CHANGES VALUE on every save,
    // which also makes "was this ever set?" unanswerable one write later.
    //
    // The cost is that 1601-01-01 cannot be stored as a genuine value — but the write side already
    // made that true, since it has no way to distinguish the sentinel it wrote from a real one.
    private static bool TryRestoreDefaultDate(Type t, out object restored)
    {
        if (t == typeof(DateTime))
        {
            restored = default(DateTime);
            return true;
        }

        if (t == typeof(DateTimeOffset))
        {
            restored = default(DateTimeOffset);
            return true;
        }

        restored = null!;
        return false;
    }

    private static object ConvertDateTimeOffset(Type t, DateTimeOffset dto)
    {
        if (dto == MinDto && TryRestoreDefaultDate(t, out var restored)) return restored;
        if (t == typeof(DateTimeOffset)) return dto;
        if (t == typeof(DateTime)) return dto.DateTime;
        return Convert.ChangeType(dto, t, CultureInfo.InvariantCulture);
    }

    // Convert a DateTime to a UTC-kind DateTime: a Local value becomes its UTC instant, an
    // Unspecified value is assumed to be UTC. Used by BOTH the write (flatten) and read
    // (deserialize) paths so that `new DateTimeOffset(x, TimeSpan.Zero)` never throws on a
    // Local-kind value on a non-UTC host, and both directions agree on the stored instant.
    internal static DateTime NormalizeToUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };

    private static object ConvertDateTime(Type t, DateTime dt)
    {
        // DateTime equality ignores Kind, so this matches the sentinel however the backend surfaced it.
        if (dt == MinDto.DateTime && TryRestoreDefaultDate(t, out var restored)) return restored;
        if (t == typeof(DateTime)) return dt;
        if (t == typeof(DateTimeOffset))
        {
            // Azure Tables persists dates in UTC; treat Unspecified as UTC and normalize Local to
            // UTC so the resulting offset is zero.
            var utc = NormalizeToUtc(dt);
            return new DateTimeOffset(utc);
        }
        return Convert.ChangeType(dt, t, CultureInfo.InvariantCulture);
    }

#pragma warning disable CA1859 // Method intentionally returns different boxed value types
    private static object? ConvertLong(Type t, long l)
#pragma warning restore CA1859
    {
        if (t == typeof(int)) return (int)l;
        if (t == typeof(uint)) return (uint)l;
        if (t == typeof(ulong)) return (ulong)l;
        if (t == typeof(long)) return l;
        return null; // caller falls through to Convert.ChangeType
    }

    // Leaves generous headroom under the 32K-char (64 KB UTF-16) cell limit for the marker.
    private const int TruncatedCellChars = 30_000;

    private static string TruncateForCell(string s, out int keptChars)
    {
        // Never cut between a surrogate pair — a lone surrogate is invalid UTF-16 and corrupts (or
        // fails) the SDK's wire encoding.
        var cut = TruncatedCellChars;
        if (char.IsHighSurrogate(s[cut - 1]))
            cut--;
        keptChars = cut;
        return $"{s[..cut]}\n…[truncated {s.Length - cut} chars to fit table storage]";
    }

    private static string? CompressLargestFittingPrefix(IList list, out int keptItems)
    {
        for (var n = list.Count / 2; ; n /= 2)
        {
            var prefix = new List<object?>(n);
            for (var i = 0; i < n; i++)
                prefix.Add(list[i]);
            var candidate = Compress(JsonSerializer.Serialize(prefix, JsonOptions));
            if (Encoding.Unicode.GetByteCount(candidate) <= MaxCellBytes)
            {
                keptItems = n;
                return candidate;
            }
            if (n == 0)
            {
                keptItems = 0;
                return null; // even an empty list doesn't fit — cannot happen in practice
            }
        }
    }

    private static string Compress(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        using var inMs = new MemoryStream(bytes);
        using var outMs = new MemoryStream();
        using (var gz = new GZipStream(outMs, CompressionMode.Compress))
            inMs.CopyTo(gz);
        return Convert.ToBase64String(outMs.ToArray());
    }

    private static string Decompress(string b64)
    {
        using var inMs = new MemoryStream(Convert.FromBase64String(b64));
        using var outMs = new MemoryStream();
        using var gz = new GZipStream(inMs, CompressionMode.Decompress);
        gz.CopyTo(outMs);
        return Encoding.UTF8.GetString(outMs.ToArray());
    }

    public override string ToString() => $"{string.Join(Delim, Path)}={Value}";
}

internal static class TypeMetadataCache
{
    private static readonly ConcurrentDictionary<Type, TypeMetadata> _cache = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<string>> _largeValueColumns = new();

    public static TypeMetadata GetMetadata(Type t)
        => _cache.GetOrAdd(t, CreateMetadata);

    /// <summary>
    /// True when <paramref name="t"/>, or any nested type it flattens into columns, has a
    /// <see cref="TableValueSplitAttribute"/> or <see cref="TableBlobContent"/> property. Such a type may
    /// produce split cells or blob references; any other type takes none of the large-value branches.
    /// </summary>
    internal static bool IsLargeValueAware(Type t) => LargeValueColumns(t).Count > 0;

    /// <summary>
    /// Every flattened base column of <paramref name="t"/> that can carry split cells or a blob
    /// reference (<c>"Body"</c>, <c>"Inner_Body"</c>, <c>"Content"</c>).
    /// </summary>
    internal static IReadOnlyList<string> LargeValueColumns(Type t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return _largeValueColumns.GetOrAdd(t, static type =>
        {
            var columns = new List<string>();
            CollectLargeValueColumns(type, string.Empty, new HashSet<Type>(), columns);
            return columns.AsReadOnly();
        });
    }

    // Walks the same property graph Flatten does: a property in SplitOptions is a large-value column;
    // a property whose type flattens into Owner_Child columns is walked (once per path, so a type that
    // reaches itself does not recurse forever).
    private static void CollectLargeValueColumns(Type type, string prefix, HashSet<Type> visiting, List<string> columns)
    {
        if (!visiting.Add(type))
            return;

        var meta = GetMetadata(type);
        foreach (var prop in meta.Properties)
        {
            if (meta.SplitOptions.ContainsKey(prop.Name) || prop.PropertyType == typeof(TableBlobContent))
                columns.Add(prefix + prop.Name);
            else if (FlattensIntoColumns(prop.PropertyType))
                CollectLargeValueColumns(prop.PropertyType, prefix + prop.Name + TableEntityValue.PathDelimiter, visiting, columns);
        }

        visiting.Remove(type);
    }

    /// <summary>Resolves a flattened column path (<c>["Inner", "Body"]</c>) to its leaf property, or null.</summary>
    internal static PropertyInfo? ResolvePath(Type type, IReadOnlyList<string> path)
    {
        PropertyInfo? prop = null;
        var owner = type;
        foreach (var segment in path)
        {
            if (!GetMetadata(owner).PropertyMap.TryGetValue(segment, out prop))
                return null;
            owner = prop.PropertyType;
        }
        return prop;
    }

    // A property type the serializer stores natively (no columns of its own to walk).
    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(string) || type == typeof(byte[]) || TableEntityValue.IsPrimitive(type) || underlying.IsEnum;
    }

    private static bool FlattensIntoColumns(Type type) =>
        !IsScalar(type) && TableEntitySerializer.FlattensToColumns(type);

    private static TypeMetadata CreateMetadata(Type t)
    {
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.CanWrite
                              && p.GetCustomAttribute<IgnoreDataMemberAttribute>() == null
                              && p.Name != nameof(TableEntity.ETag)
                              && p.Name != nameof(TableEntity.Timestamp))
                     .ToImmutableList();

        var map = props.ToDictionary(p => p.Name, p => p);
        // Prefer the real ctor (runs field initializers). For types WITHOUT a public parameterless
        // ctor — common for events/commands read late-bound from legacy tables — fall back to an
        // uninitialized instance, the same construction the legacy FormatterServices-based
        // serializers used, so those rows stay readable instead of throwing MissingMethodException.
        Func<object> creator = t.IsValueType || t.GetConstructor(Type.EmptyTypes) is not null
            ? () => Activator.CreateInstance(t)!
            : () => RuntimeHelpers.GetUninitializedObject(t);

        return new TypeMetadata(props, map, BuildAliasMap(t, map), BuildSplitOptions(t, props), creator);
    }

    // [TableValueSplit] per property. A property-level attribute wins. A class-level attribute covers
    // every string, byte[] and JSON-stored property declared by the annotated class or a class
    // derived from it - not members inherited from its bases (Entity.Id stays an ordinary, filterable
    // key column). A property-level attribute on a scalar or on a concrete type that flattens into
    // columns has nothing to split and fails on first use of the type.
    private static Dictionary<string, TableValueSplitAttribute> BuildSplitOptions(Type t, ImmutableList<PropertyInfo> props)
    {
        var options = new Dictionary<string, TableValueSplitAttribute>(StringComparer.Ordinal);

        Type? annotated = null;
        for (var cur = t; cur is not null; cur = cur.BaseType)
        {
            if (cur.IsDefined(typeof(TableValueSplitAttribute), inherit: false))
            {
                annotated = cur;
                break;
            }
        }
        var classLevel = annotated?.GetCustomAttribute<TableValueSplitAttribute>(inherit: false);

        foreach (var prop in props)
        {
            var type = prop.PropertyType;
            if (prop.GetCustomAttribute<TableValueSplitAttribute>(inherit: true) is { } own)
            {
                if (type == typeof(TableBlobContent))
                    throw new InvalidOperationException(
                        $"[TableValueSplit] on {t.Name}.{prop.Name}: a TableBlobContent is always stored in blob storage. Remove the attribute.");
                if (IsScalar(type) && type != typeof(string) && type != typeof(byte[]))
                    throw new InvalidOperationException(
                        $"[TableValueSplit] on {t.Name}.{prop.Name}: a {type.Name} is a native cell and cannot be split. " +
                        "Only string, byte[] and JSON-stored properties can.");
                if (FlattensIntoColumns(type) && !type.IsInterface && !type.IsAbstract)
                    throw new InvalidOperationException(
                        $"[TableValueSplit] on {t.Name}.{prop.Name}: {type.Name} is flattened into columns. " +
                        "Put [TableValueSplit] on its members instead.");
                options[prop.Name] = own;
            }
            else if (classLevel is not null
                     && prop.DeclaringType is { } declaring && annotated!.IsAssignableFrom(declaring)
                     && IsSplittable(type))
            {
                options[prop.Name] = classLevel;
            }
        }

        return options;
    }

    // string, byte[], or a type the serializer stores as one JSON cell.
    private static bool IsSplittable(Type type) =>
        type != typeof(TableBlobContent) && (type == typeof(string) || type == typeof(byte[]) || (!IsScalar(type) && !TableEntitySerializer.FlattensToColumns(type)));

    // Collect [ColumnAlias] declarations — property-level for owned properties, class-level
    // (inherited) for properties on base types the annotating class doesn't own — and validate
    // them eagerly so a bad alias fails on first use of the type, not on some later row shape.
    private static Dictionary<string, PropertyInfo> BuildAliasMap(Type t, Dictionary<string, PropertyInfo> map)
    {
        var aliases = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        foreach (var prop in map.Values)
        {
            foreach (var attr in prop.GetCustomAttributes<ColumnAliasAttribute>(inherit: true))
            {
                if (attr.PropertyName is not null)
                    throw new InvalidOperationException(
                        $"Property-level [ColumnAlias] on {t.Name}.{prop.Name} must use the single-argument constructor.");
                AddAlias(aliases, map, attr.LegacyColumnName, prop, t);
            }
        }

        foreach (var attr in t.GetCustomAttributes<ColumnAliasAttribute>(inherit: true))
        {
            if (attr.PropertyName is null)
                throw new InvalidOperationException(
                    $"Class-level [ColumnAlias] on {t.Name} must use the (propertyName, legacyColumnName) constructor.");
            if (!map.TryGetValue(attr.PropertyName, out var target))
                throw new InvalidOperationException(
                    $"[ColumnAlias] on {t.Name} references unknown or non-serialized property '{attr.PropertyName}'.");
            AddAlias(aliases, map, attr.LegacyColumnName, target, t);
        }

        return aliases;
    }

    private static void AddAlias(
        Dictionary<string, PropertyInfo> aliases, Dictionary<string, PropertyInfo> map,
        string alias, PropertyInfo target, Type t)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new InvalidOperationException($"[ColumnAlias] on {t.Name} declares an empty legacy column name.");
        if (map.ContainsKey(alias))
            throw new InvalidOperationException(
                $"[ColumnAlias] '{alias}' on {t.Name} collides with a real property name.");
        if (aliases.TryGetValue(alias, out var existing) && !ReferenceEquals(existing, target))
            throw new InvalidOperationException(
                $"[ColumnAlias] '{alias}' on {t.Name} is declared for both '{existing.Name}' and '{target.Name}'.");
        aliases[alias] = target;
    }
}

internal sealed class TypeMetadata(
    ImmutableList<PropertyInfo> props,
    Dictionary<string, PropertyInfo> map,
    Dictionary<string, PropertyInfo> aliasMap,
    IReadOnlyDictionary<string, TableValueSplitAttribute> splitOptions,
    Func<object> creator)
{
    public ImmutableList<PropertyInfo> Properties { get; } = props;
    public Dictionary<string, PropertyInfo> PropertyMap { get; } = map;
    public Dictionary<string, PropertyInfo> AliasMap { get; } = aliasMap;

    /// <summary>Property name to its <see cref="TableValueSplitAttribute"/>, for the opted-in properties.</summary>
    public IReadOnlyDictionary<string, TableValueSplitAttribute> SplitOptions { get; } = splitOptions;
    public Func<object> Creator { get; } = creator;
}
