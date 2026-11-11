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
/// be represented natively fall back to JSON, and oversized JSON is GZip-compressed to fit the Azure
/// Tables 64&#160;KB per-cell limit; a payload that still does not fit throws by default
/// (<see cref="OversizedCellPolicy.Throw"/>) and is trimmed only under a trim policy. A property marked
/// <see cref="TableValueSplitAttribute"/> is stored whole instead.
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
    /// <remarks>
    /// When the runtime type of <paramref name="root"/> is not large-value aware (its declared types opt
    /// into nothing), a value met anywhere in the flattened graph in a member marked
    /// <see cref="TableValueSplitAttribute"/> (on the property or its class), or a
    /// <see cref="TableBlobContent"/> value (both are found only below a property whose runtime type differs
    /// from its declared type), is refused whatever its size: a store of such a type writes every entity as
    /// one plain row and can neither split, offload nor read back such a value. No row is returned.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The runtime type of <paramref name="root"/> is not large-value aware and its graph holds a value in a
    /// large-value member. Mark the type with <see cref="TableValueSplitAttribute"/>, or declare the
    /// property that holds the member with the type that carries it.
    /// </exception>
    public static TableEntity ToTableEntity(
        this object root,
        string partitionKey,
        string rowKey,
        bool persistType = false)
        => Build(root, partitionKey, rowKey, persistType, ValueWriteContext.Public, pending: null, plainEntry: true);

    /// <summary>
    /// Flatten <paramref name="root"/> under a storage write context: <paramref name="ctx"/> decides
    /// how large values are stored and <paramref name="pending"/> collects the blobs to upload first. A
    /// context without blob offload (<see cref="ValueWriteContext.ForClone"/>) never produces one, so its
    /// callers pass null rather than allocate an empty list per call.
    /// </summary>
    internal static TableEntity ToTableEntity(
        object root,
        string partitionKey,
        string rowKey,
        ValueWriteContext ctx,
        List<PendingBlob>? pending)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (pending is null && ctx.Offload is not null)
            throw new ArgumentNullException(nameof(pending), "A write with blob offload must collect its pending blobs.");
        return Build(root, partitionKey, rowKey, persistType: false, ctx, pending, plainEntry: false, ctx.ReadType);
    }

    /// <summary>
    /// The plain serializer as a store of <paramref name="storeType"/> writes <paramref name="root"/>: a
    /// read of the row creates a <paramref name="storeType"/>. When that type is not large-value aware,
    /// every large-value member met in the graph is refused, also one that only the runtime type of
    /// <paramref name="root"/> (a subclass of <paramref name="storeType"/>) declares.
    /// </summary>
    internal static TableEntity ToPlainTableEntity(object root, string partitionKey, string rowKey, Type storeType)
    {
        ArgumentNullException.ThrowIfNull(storeType);
        return Build(root, partitionKey, rowKey, persistType: false, ValueWriteContext.Public, pending: null, plainEntry: true, storeType);
    }

    // plainEntry: the public serializer, which every store of a type that is not large-value aware writes
    // through. For a root whose read type is not aware it refuses every large-value member it meets.
    // readType: the type a read of the row creates (the store's entity type); the runtime type when null.
    private static TableEntity Build(
        object root, string partitionKey, string rowKey, bool persistType, ValueWriteContext ctx, List<PendingBlob>? pending, bool plainEntry,
        Type? readType = null)
    {
        var entity = new TableEntity(partitionKey, rowKey);
        if (root == null) return entity;

        var rootType = root.GetType();
        readType ??= rootType;
        var plainRoot = plainEntry && !TypeMetadataCache.IsLargeValueAware(readType) ? readType : null;
        // A subclass written through a store of its base type: a read creates the base type.
        var subclass = readType != rootType ? rootType : null;
        var flat = Flatten(root, persistType, new FlattenState(ctx, pending, Partial: false, partitionKey, rowKey, readType, plainRoot, subclass));
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

    /// <summary>
    /// True when <paramref name="row"/> holds a large value the library wrote: a well-formed split manifest or
    /// a cell that parses as a blob reference. A column that only has the name of such a cell (another
    /// program's <c>Body__len</c>) is not one; a type that is not large-value aware reads it as 0.8.3 did.
    /// </summary>
    internal static bool HasGenuineLargeValueCells(TableEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return GenuineLargeValueColumns(row) is not null;
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

    /// <summary>
    /// The message for a large-value member that the plain serializer met below <paramref name="root"/>, a
    /// type that is not large-value aware: <paramref name="member"/> of the object at
    /// <paramref name="ownerPath"/> (empty: of the root itself), whose runtime type is <paramref name="ownerType"/>.
    /// </summary>
    internal static string PlainPathRefusal(Type root, IReadOnlyList<string> ownerPath, Type ownerType, string member)
    {
        var column = string.Join(TableEntityValue.PathDelimiter, ownerPath.Append(member));
        var holder = string.Join('.', ownerPath.SelectMany(p => p.Split(TableEntityValue.PathDelimiter)));
        var where = ownerPath.Count == 0
            ? $"{root.Name}.{member}"
            : $"{ownerType.Name}.{member} below {root.Name}.{holder}";
        var declare = ownerPath.Count == 0
            ? $"declare {root.Name}.{member} as TableBlobContent"
            : $"declare {root.Name}.{holder} with the type that carries the member ({ownerType.Name})";
        var refused = $"{where} (column '{column}') is a large value: it is marked [TableValueSplit] or holds a TableBlobContent. " +
                      $"{root.Name} is not large-value aware (its declared types opt into nothing), so its store writes every entity " +
                      "as one plain row and can neither split, offload nor read back such a value, whatever its size. ";
        // Below a nullable struct the class-level opt-in alone leads to the next refusal (a read cannot
        // create the struct of a nullable property); declaring the property with the struct itself works.
        if (NullableStructOnPath(root, ownerPath) is { } nullable)
            return refused +
                   $"A read cannot create the struct of the nullable property {nullable.Owner.Name}.{nullable.Property.Name} " +
                   $"({nullable.Struct.Name}?), so a large value below it cannot be stored as one (marking {root.Name} with " +
                   $"[TableValueSplit] is not enough on its own). Declare {nullable.Owner.Name}.{nullable.Property.Name} as " +
                   $"{nullable.Struct.Name}, not nullable, so that {root.Name} is large-value aware by its declared types and reads " +
                   "the value back. Nothing was serialized.";
        return refused +
               $"Mark {root.Name} with [TableValueSplit], or {declare}, so that {root.Name} is large-value aware by its declared types. " +
               "Nothing was serialized.";
    }

    // The first property along ownerPath (property names from root, by reflection only) declared as a
    // nullable struct, with the declared type that has it; null when there is none.
    private static (Type Owner, PropertyInfo Property, Type Struct)? NullableStructOnPath(Type root, IReadOnlyList<string> ownerPath)
    {
        var owner = root;
        foreach (var name in ownerPath.SelectMany(p => p.Split(TableEntityValue.PathDelimiter)))
        {
            var property = owner.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0);
            if (property is null)
                return null;
            if (Nullable.GetUnderlyingType(property.PropertyType) is { } underlying)
                return (owner, property, underlying);
            owner = property.PropertyType;
        }
        return null;
    }

    /// <summary>
    /// The message for a large value in <paramref name="column"/> of <paramref name="runtimeType"/>, an
    /// instance written through a store of its base type <paramref name="storeType"/>, whose reads create a
    /// <paramref name="storeType"/>. <paramref name="plainStore"/>: <paramref name="storeType"/> is not
    /// large-value aware (its store refuses every large-value form); otherwise the value needs split cells
    /// or a blob in a column a read of <paramref name="storeType"/> maps to no property.
    /// </summary>
    internal static string SubclassThroughBaseStoreRefusal(Type runtimeType, Type storeType, string column, bool plainStore)
    {
        var why = plainStore
            ? $"{storeType.Name} is not large-value aware (its declared types opt into nothing), so its store writes every entity as one " +
              "plain row and can neither split, offload nor read back such a value, whatever its size"
            : $"the value needs split cells or a blob, and a read through that store creates a {storeType.Name}, which maps no property " +
              $"to '{column}': the value would never be read back, and the next save of the entity would drop it";
        var remedy = TypeMetadataCache.IsLargeValueAware(runtimeType)
            ? $"Store {runtimeType.Name} entities through an IStorage<{runtimeType.Name}> (a store of the runtime type)"
            : $"Mark {runtimeType.Name} with [TableValueSplit] and store {runtimeType.Name} entities through an IStorage<{runtimeType.Name}> " +
              "(a store of the runtime type)";
        return $"'{column}' of {runtimeType.Name} is a large value: it is marked [TableValueSplit] or holds a TableBlobContent. This " +
               $"{runtimeType.Name} is written through an IStorage<{storeType.Name}>, and {why}. {remedy}. Nothing was stored.";
    }

    /// <summary>The message for a large value reached by a partial (Merge) update.</summary>
    internal static string PartialUpdateRefusal(string name) =>
        $"'{name}' is a large value; partial updates cannot write it — replace the entity instead.";

    /// <summary>
    /// The message for a partial update of <paramref name="column"/> of an aware <paramref name="root"/> whose
    /// read-side resolution threw <paramref name="failure"/>: whether the column is a large value is not known.
    /// </summary>
    internal static string UndecidablePartialUpdateRefusal(Type root, string column, Exception failure) =>
        $"The partial update of '{column}' of {root.Name} was refused: deciding whether the column is a large value needs a " +
        $"new {root.Name} as a read creates it, and that failed ({failure.GetType().Name}: {failure.Message}). The type's " +
        "constructor or a getter failed (or an object along the path cannot be created), so the library cannot tell whether " +
        $"'{column}' is a large value, which a partial update cannot write. Nothing was written. Send the update again once " +
        "the constructor and getters succeed, or replace the entity instead.";

    /// <summary>The message for a large value whose column a read of <paramref name="root"/> maps to no property.</summary>
    internal static string UnreadableLargeValue(Type root, string column) =>
        $"'{column}' of {root.Name} needs split cells or a blob, and a read of {root.Name} would not map the column " +
        $"'{column}' back to a property: the object that owns it has a runtime type a read does not recreate. A read " +
        "keeps the instance the constructor puts in a property and creates the declared type where there is none. " +
        "Initialise the owning property with an instance of that runtime type, or declare the property with that type.";

    /// <summary>
    /// The message for a large value whose column a read of <paramref name="root"/> cannot resolve: the read
    /// fails at an owner it cannot create (a struct held in a nullable property, an interface or an abstract
    /// type with no instance), so every read of a row holding the value would fail.
    /// </summary>
    internal static string UnresolvableLargeValue(Type root, string column) =>
        $"'{column}' of {root.Name} needs split cells or a blob, and a read of {root.Name} cannot map the column " +
        $"'{column}' back to a property: it cannot create the object that owns the value (a struct held in a nullable " +
        "property, or an interface or abstract type with no instance in the property), so no read of the row could " +
        "return it. Declare the owning property with a type a read can create (the struct itself rather than its " +
        "nullable form, or a class), or initialise the property with an instance in the constructor. Nothing was stored.";

    /// <summary>
    /// The message for a <see cref="TableBlobContent"/> held by <paramref name="property"/>, which a read
    /// does not give a <see cref="TableBlobContent"/> back to: it is not declared as one (it can only be
    /// <see cref="object"/>, since the content type is sealed).
    /// </summary>
    internal static string BlobContentInAnotherType(string column, PropertyInfo property)
    {
        var owner = (property.ReflectedType ?? property.DeclaringType)?.Name;
        var name = owner is null ? property.Name : $"{owner}.{property.Name}";
        return $"'{column}' holds a TableBlobContent, but {name} is declared as {property.PropertyType.Name}: a read gives a " +
               "TableBlobContent back only to a property declared as TableBlobContent, and would download this blob as a value " +
               $"that {name} cannot take, so no read of the row could return it. Declare {name} as TableBlobContent. Nothing was stored.";
    }

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

        // A type that opts into nothing reads a column that merely has the name of a large-value cell
        // ("Body__len" written by another program) as 0.8.3 read it. Only a group the library really
        // wrote is a large value there: a well-formed manifest, or a cell that parses as a blob reference.
        // For an aware type every such column is one (an incomplete group is damage, and throws).
        var aware = TypeMetadataCache.IsLargeValueAware(type);
        var genuine = aware ? null : GenuineLargeValueColumns(entity);

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
            if (SplitCodec.TryParseLargeValueColumn(kv.Key, out var baseColumn, out _, out _)
                && (aware || (genuine?.Contains(baseColumn) ?? false)))
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
            result = ApplyLargeValues(result, largeValues, ctx);

        result = ApplyColumnAliases(result, entity, meta, largeValues, ctx);

        // A polymorphic row's keys encode an aggregate version or a tick count, not an id — so
        // recomputing Id from them would overwrite the object's real id with "partition|row".
        return restoreId ? RestoreIdFromKeys(result, entity) : result;
    }

    // The base columns of the row whose large-value cells the library wrote: a complete, well-formed
    // manifest (__chunks a positive count, __enc a known encoding, __sha256 a SHA-256 in hex, __len a
    // non-negative length; its chunks may be missing, on continuation rows the read did not assemble), or a
    // __blob cell that parses as a blob reference. Null when there is none.
    private static HashSet<string>? GenuineLargeValueColumns(TableEntity row)
    {
        HashSet<string>? genuine = null;
        foreach (var (key, value) in row)
        {
            if (!SplitCodec.TryParseLargeValueColumn(key, out var column, out var kind, out _))
                continue;
            var isGenuine = kind switch
            {
                LargeValueCellKind.Count => IsWellFormedManifest(row, column),
                LargeValueCellKind.BlobReference => IsBlobReference(column, value),
                _ => false,
            };
            if (isGenuine)
                (genuine ??= new HashSet<string>(StringComparer.Ordinal)).Add(column);
        }
        return genuine;
    }

    private static bool IsWellFormedManifest(TableEntity row, string column) =>
        row.TryGetValue(column + SplitCodec.ChunksSuffix, out var chunks) && chunks is int and > 0 or long and > 0 and <= int.MaxValue
        && row.TryGetValue(column + "__enc", out var enc) && enc is "text" or "gzip" or "utf16le" or "binary"
        && row.TryGetValue(column + "__sha256", out var sha) && sha is string { Length: 64 } hex && hex.All(char.IsAsciiHexDigit)
        && row.TryGetValue(column + "__len", out var length) && length is long and >= 0 or int and >= 0;

    private static bool IsBlobReference(string column, object? cell)
    {
        if (cell is not string json)
            return false;
        try
        {
            _ = Providers.BlobReference.Parse(column, json);
            return true;
        }
        catch (TableValueIntegrityException)
        {
            return false;
        }
    }

    // Reassembles every split value (verifying its SHA-256) and sets it on its property. The property
    // is the one an ordinary cell of the same column would be set on: each owner along the column path
    // is the instance that is already there (a default instance keeps its runtime type, which may have
    // members its declared type lacks) or a new instance of the declared type. A value whose property
    // no longer exists is skipped unread, like any other unknown column. A group
    // holding a blob reference takes its value from what the read resolved outside the row: a
    // downloaded value, or a lazy TableBlobContent. A reference the read's projection excludes keeps
    // the property's default; any other unresolved one (a plain FromTableEntity, or a store whose type
    // is not large-value-aware) throws, because an entity with the property silently empty would drop
    // the reference on its next write and the orphan sweep would then delete the value.
    private static object ApplyLargeValues(
        object result, Dictionary<string, Dictionary<string, object>> largeValues, ValueReadContext ctx)
    {
        foreach (var (baseColumn, cells) in largeValues)
        {
            // A '_' base is a storage-layer column, never a property.
            if (SystemColumnNames.IsSystemColumn(baseColumn))
                continue;

            var path = TableEntityValue.Create(baseColumn, string.Empty).Path;
            if (path.IsEmpty || ReadTarget(result, path, baseColumn) is not { } target)
                continue;

            result = ApplyLargeValue(result, path, target, baseColumn, cells, ctx, renamedTo: null);
        }
        return result;
    }

    // Sets the large value stored in the cells of baseColumn on target, the property at path. For a
    // legacy column read through [ColumnAlias], renamedTo is the column of that property: the cells,
    // the resolved value and every failure are still named after the column the value is stored under.
    private static object ApplyLargeValue(
        object result, ImmutableList<string> path, PropertyInfo target, string baseColumn,
        Dictionary<string, object> cells, ValueReadContext ctx, string? renamedTo)
    {
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
                return SetProperty(result, new TableEntityValue(path, lazy(baseColumn, parsed)));
            }
            else if (ctx.Projection is { } projected && !ValueOffloadCoordinator.Projects(projected, baseColumn)
                     && (renamedTo is null || !ValueOffloadCoordinator.Projects(projected, renamedTo)))
            {
                return result;
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
        return SetProperty(result, new TableEntityValue(path, decoded, fmt) { IsLargeValue = true });
    }

    /// <summary>
    /// The property a read of <paramref name="row"/> as <paramref name="entityType"/> sets the large-value
    /// column <paramref name="column"/> on: its declared type and its own column. The property is
    /// resolved as the read resolves it (see
    /// <see cref="TypeMetadataCache.ReadTarget(object, IReadOnlyList{string})"/>); a legacy column is
    /// that of the property a <see cref="ColumnAliasAttribute"/> maps it to, unless the row also has the
    /// property's own column, which wins. Null when the read takes nothing from the column: no property
    /// is mapped to it, or the read fails at an owner it cannot create and reports that itself.
    /// </summary>
    internal static (Type Type, string Column)? LargeValueTarget(Type entityType, TableEntity row, string column)
    {
#pragma warning disable CA1031 // the read of the row reports what it cannot resolve; nothing is fetched for it
        try
        {
            if (TypeMetadataCache.ReadTarget(entityType, column) is { } target)
                return (target.PropertyType, column);
        }
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031

        return TypeMetadataCache.GetMetadata(entityType).AliasMap.TryGetValue(column, out var renamed)
               && !HasCanonicalColumn(row, renamed.Name)
            ? (renamed.PropertyType, renamed.Name)
            : null;
    }

    // The property a read sets the large-value column on (null: no property takes it). An owner the
    // read cannot create (an interface or abstract type with no default instance) fails as it fails
    // for the ordinary cells beside the value, never by leaving the value out.
    private static PropertyInfo? ReadTarget(object result, ImmutableList<string> path, string baseColumn)
    {
        try
        {
            return TypeMetadataCache.ReadTarget(result, path);
        }
        catch (Exception ex)
        {
            throw new SerializationException($"Failed to set '{baseColumn}'.", ex);
        }
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
    /// canonical data always wins and rewrites converge rows to the canonical schema. The legacy column
    /// is read in every form the canonical one can have: one cell (raw, <c>__Json</c>, <c>__GZip</c>),
    /// split cells with their manifest (continuation rows are already assembled into the row), or a
    /// blob reference.
    /// </summary>
    private static object ApplyColumnAliases(
        object result, TableEntity entity, TypeMetadata meta,
        Dictionary<string, Dictionary<string, object>>? largeValues, ValueReadContext ctx)
    {
        if (meta.AliasMap.Count == 0)
            return result;

        foreach (var kv in meta.AliasMap)
        {
            var aliasColumn = kv.Key;
            var property = kv.Value;

            if (HasCanonicalColumn(entity, property.Name))
                continue;

            // A legacy large value. It wins over a plain cell of the same legacy name, as the split cells
            // of a canonical column win over a plain cell beside them.
            if (largeValues is not null && largeValues.TryGetValue(aliasColumn, out var cells))
            {
                result = ApplyLargeValue(
                    result, ImmutableList.Create(property.Name), property, aliasColumn, cells, ctx, renamedTo: property.Name);
                continue;
            }

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

    // True when the row holds the property's own column in any form: one cell (raw, __Json, __GZip),
    // split cells (their manifest) or a blob reference. A legacy column is then ignored.
    private static bool HasCanonicalColumn(TableEntity row, string property)
    {
        foreach (var suffix in AliasSuffixes)
        {
            if (row.ContainsKey(property + suffix))
                return true;
        }
        return row.ContainsKey(property + SplitCodec.ChunksSuffix) || row.ContainsKey(property + SplitCodec.BlobSuffix);
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
    public static Dictionary<string, object> FlattenProperty(string propertyName, object value) =>
        FlattenProperty(propertyName, value, plainRoot: null);

    // plainRoot: the entity type of a partial update when it is not large-value aware; a large-value
    // member in the value is then refused with the message of the plain serializer.
    private static Dictionary<string, object> FlattenProperty(string propertyName, object value, Type? plainRoot)
    {
        var dict = new Dictionary<string, object>();
        if (value == null) return dict;

        if (plainRoot is not null && value is TableBlobContent)
            throw new NotSupportedException(PlainPathRefusal(plainRoot, [], plainRoot, propertyName));

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var node = new TableEntityValue(ImmutableList<string>.Empty.Add(propertyName), value);
        var state = new FlattenState(ValueWriteContext.Public, Pending: null, Partial: true, string.Empty, string.Empty, PlainRoot: plainRoot);
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
        // A type that opts into nothing by its declared types takes the path of 0.8.3: it builds no
        // metadata for the declared types along the column path (one may be a type the serializer cannot
        // build metadata for, such as an interface that declares two indexers) and constructs nothing. No
        // write of such a type stores a large value (the plain entry refuses one), so a path cannot reach
        // split cells; a value whose own graph holds a large-value member is refused by the flatten below.
        if (!TypeMetadataCache.IsLargeValueAware(ownerType))
            return FlattenProperty(columnPath, value, plainRoot: ownerType);

        // The entity type is one the serializer flattens: its metadata is built here as on any first
        // use, so a misused attribute is reported. The types further along the path are declared types,
        // which the serializer may never flatten, so they are read by reflection alone.
        _ = TypeMetadataCache.GetMetadata(ownerType);
        var owner = ownerType;
        foreach (var segment in columnPath.Split(TableEntityValue.PathDelimiter, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TypeMetadataCache.TryGetMember(owner, segment, out var memberType, out var largeValue))
                break;
            if (largeValue)
                throw new NotSupportedException(PartialUpdateRefusal(columnPath));
            owner = memberType;
        }

        // The same question for the object a read finds along the path: a default instance may be of a
        // runtime type that opts the member in although the declared type's member is ordinary. A plain
        // cell merged beside its split cells would read back the old value.
        if (TypeMetadataCache.IsLargeValueOnRead(ownerType, columnPath, out var unresolved))
            throw new NotSupportedException(PartialUpdateRefusal(columnPath));
        // A column the read-side resolution could not resolve may be a large value: refused, never merged.
        if (unresolved is not null)
            throw new NotSupportedException(UndecidablePartialUpdateRefusal(ownerType, columnPath, unresolved), unresolved);
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
            // The plain serializer over a type that is not large-value aware: a value in a large-value member
            // below a runtime subtype is refused whatever its size (its store could not plan, offload or read
            // it); a partial update refuses the member as it does for any type, with this message.
            if (state.PlainRoot is { } plainRoot && (childVal is not null || state.Partial)
                && (childSplit != null || prop.PropertyType == typeof(TableBlobContent) || childVal is TableBlobContent))
                throw new NotSupportedException(state.Subclass is { } subclass
                    ? SubclassThroughBaseStoreRefusal(subclass, plainRoot, string.Join(TableEntityValue.PathDelimiter, value.Path.Add(prop.Name)), plainStore: true)
                    : PlainPathRefusal(plainRoot, value.Path, value.Type, prop.Name));
            if (state.Partial && (childSplit != null || prop.PropertyType == typeof(TableBlobContent) || childVal is TableBlobContent))
                throw new NotSupportedException(
                    PartialUpdateRefusal(string.Join(TableEntityValue.PathDelimiter, value.Path.Add(prop.Name))));

            var childNode = new TableEntityValue(value.Path.Add(prop.Name), childVal!);
            // A write may have to put another content instance on this property (PendingBlob.Assign).
            if (childVal is TableBlobContent && !value.Type.IsValueType)
                childNode.BlobContentOwner = (value.Value, prop);
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
    /// The cached type check keeps every plain type off the reflection walk. A content below a runtime
    /// subtype of a type that is not large-value aware is not listed here; the plain serializer refuses it
    /// (NotSupportedException) before it returns a row.
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

    /// <summary>
    /// The <see cref="TableValueSplitAttribute"/> the flattened base column of <paramref name="root"/> was
    /// written under, or null: the attribute of the member of the object that holds the value, whose
    /// runtime type may differ from the declared type of the property it sits in.
    /// </summary>
    internal static TableValueSplitAttribute? SplitOptionFor(object root, string baseColumn)
    {
        var path = TableEntityValue.Create(baseColumn, string.Empty).Path;
        if (path.Count == 0) return null;
        var owner = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (!TypeMetadataCache.GetMetadata(owner.GetType()).PropertyMap.TryGetValue(path[i], out var prop)
                || prop.GetValue(owner) is not { } child)
                return null;
            owner = child;
        }
        return TypeMetadataCache.GetMetadata(owner.GetType()).SplitOptions.TryGetValue(path[^1], out var split) ? split : null;
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
/// update (which must refuse large values), the row keys blob names are built from, the type of
/// the object being flattened (the type a read of the row materializes; null for a partial update), and,
/// for the plain serializer over an entity type that is not large-value aware, that type
/// (<paramref name="PlainRoot"/>: every large-value member met in the graph is refused), and the runtime type
/// of the object being flattened when it is a subclass of <paramref name="RootType"/> written through a store
/// of that type (<paramref name="Subclass"/>: a large value in a column a read of <paramref name="RootType"/>
/// maps to no property is refused).
/// </summary>
internal readonly record struct FlattenState(
    ValueWriteContext Ctx, List<PendingBlob>? Pending, bool Partial, string PartitionKey, string RowKey, Type? RootType = null,
    Type? PlainRoot = null, Type? Subclass = null)
{
    /// <summary>
    /// Records a blob to upload before the entity write. Only a context with blob offload produces one,
    /// and its caller always collects them; a null list here is a bug, never silently dropped.
    /// </summary>
    public void AddPending(PendingBlob blob) =>
        (Pending ?? throw new InvalidOperationException("A write without blob offload produced a pending blob.")).Add(blob);
}

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
    /// True for a value reassembled from split cells or downloaded from a blob. It can be of any size, so
    /// a failure message quotes at most <see cref="MaxDescribedChars"/> characters of it and states its
    /// length; a value that came from one cell is named whole, as on 0.8.3.
    /// </summary>
    public bool IsLargeValue { get; init; }

    /// <summary>How many characters of a large value a failure message quotes.</summary>
    internal const int MaxDescribedChars = 200;

    /// <summary>
    /// Set by <see cref="TrySerialize"/> when the oversized-cell policy trimmed (or dropped) this
    /// value — human-readable "kept X of Y" text the caller writes into the sibling
    /// <see cref="TruncationMarkerColumn"/> cell. Null when nothing was lost.
    /// </summary>
    public string? TruncationNote { get; private set; }

    /// <summary>The marker column recording a truncation, e.g. <c>Tags__Truncated</c>.</summary>
    public string TruncationMarkerColumn => string.Join(Delim, Path) + TruncatedSuffix;

    /// <summary>
    /// For a <see cref="TableBlobContent"/> value: the object and the property it was read from, so that a
    /// write can put another content instance there. Null when that object is held by value.
    /// </summary>
    public (object Owner, PropertyInfo Property)? BlobContentOwner { get; set; }

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
    // text as __Json), otherwise split cells; a column the planner moved (ValueWriteContext.ForceSplit)
    // is split whatever its size. Never trims and never consults OversizedCellPolicy.
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

        // A value that fits one cell stays in its cell, unless the planner moved the column off a primary
        // row that was too full: then it takes the split form below, like a value too large for one cell.
        var moved = state is { Ctx: { } planned } && (planned.ForceSplit.Contains(columnName) || planned.ForceOffload.Contains(columnName));
        if (!moved && SplitCodec.FitsOneCell(payload, split.Encoding))
        {
            var column = columnName;
            if (isJson)
            {
                columnName += JsonSuffix;
                Format = TableEntityValueFormat.Json;
            }
            state?.Ctx.Inline?.Add(new InlineValue(column, columnName, split.Encoding));
            value = payload;
            return true;
        }

        RefuseUnreadable(state, columnName);

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
                st.AddPending(new PendingBlob(columnName, reference, stored.Enc == "gzip" ? "gzip" : null,
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

    // A large value is stored outside the cell of its property (split cells, continuation rows, a blob),
    // so a read must map its column back to that property: a value below an object whose runtime type a
    // read does not recreate would be stored and then left out of every read. A top-level column maps when
    // the object written is of the type a read creates (that type has the property); a subclass written
    // through a store of its base type may declare columns the base type does not read. A clone is read
    // back from the same object.
    private void RefuseUnreadable(FlattenState? state, string columnName)
    {
        if (state is not { RootType: { } root, Ctx.IsClone: false } st)
            return;
        if (st.Subclass is { } subclass && !TypeMetadataCache.ReadsBack(root, Path[0]))
            throw new NotSupportedException(TableEntitySerializer.SubclassThroughBaseStoreRefusal(subclass, root, columnName, plainStore: false));
        if (Path.Count < 2)
            return;
        if (!TypeMetadataCache.ReadsBack(root, columnName, out var unresolved))
            throw new NotSupportedException(unresolved
                ? TableEntitySerializer.UnresolvableLargeValue(root, columnName)
                : TableEntitySerializer.UnreadableLargeValue(root, columnName));
    }

    // The writer stores a TableBlobContent as a blob because of the value; a read decides by the declared
    // type of the property it maps the column to, and only a property declared TableBlobContent gets the
    // content back (any other has the blob downloaded as a value it cannot take). So a content is stored
    // only through such a property. (A column the read cannot resolve was refused by RefuseUnreadable.)
    private static void RefuseContentReadAsAnotherType(FlattenState? state, string columnName)
    {
        if (state is not { RootType: { } root, Ctx.IsClone: false })
            return;
        PropertyInfo? target;
#pragma warning disable CA1031 // RefuseUnreadable reports a column the read cannot resolve
        try
        {
            target = TypeMetadataCache.ReadTarget(root, columnName);
        }
        catch (Exception)
        {
            return;
        }
#pragma warning restore CA1031
        if (target is not null && target.PropertyType != typeof(TableBlobContent))
            throw new NotSupportedException(TableEntitySerializer.BlobContentInAnotherType(columnName, target));
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
        RefuseUnreadable(state, columnName);
        RefuseContentReadAsAnotherType(state, columnName);
        if (ctx.Offload is null)
            throw new OversizedValueException(columnName, blob.IsSealed ? blob.Length : 0,
                "a TableBlobContent is always stored in blob storage and blob offload is not configured");
        if (!blob.IsSealed)
            throw new InvalidOperationException(
                $"The TableBlobContent of '{columnName}' was not prepared for writing (its length and hash are unknown).");

        var st = state!.Value;
        var reference = new Providers.BlobReference(
            BlobName(ctx, st, columnName, blob.Sha256), blob.Sha256, blob.Length, blob.ContentType, "binary");
        // The content says what to upload from when the upload runs (its prepared source, or the blob it is bound to by then).
        st.AddPending(new PendingBlob(columnName, reference, null, c => blob.OpenUploadSourceAsync(columnName, c))
        {
            Content = blob,
            Assign = BlobContentOwner is { } owner ? content => owner.Property.SetValue(owner.Owner, content) : null,
        });
        largeCells = [new(columnName + SplitCodec.BlobSuffix, reference.ToJson())];
        return true;
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

    public override string ToString()
    {
        var name = string.Join(Delim, Path);
        if (IsLargeValue)
        {
            if (Value is string { Length: > MaxDescribedChars } text)
            {
                // Never end the excerpt on half a surrogate pair.
                var cut = char.IsHighSurrogate(text[MaxDescribedChars - 1]) ? MaxDescribedChars - 1 : MaxDescribedChars;
                return string.Create(CultureInfo.InvariantCulture, $"{name}={text.AsSpan(0, cut)}… ({text.Length:N0} characters)");
            }
            if (Value is byte[] bytes)
                return string.Create(CultureInfo.InvariantCulture, $"{name}={Value} ({bytes.Length:N0} bytes)");
        }
        return $"{name}={Value}";
    }
}

internal static class TypeMetadataCache
{
    private static readonly ConcurrentDictionary<Type, TypeMetadata> _cache = new();
    private static readonly ConcurrentDictionary<Type, bool> _aware = new();
    private static readonly ConcurrentDictionary<Type, LargeValueInfo> _largeValues = new();
    private static readonly ConcurrentDictionary<Type, LargeValueInfo> _declaredLargeValues = new();
    private static readonly ConcurrentDictionary<(Type Type, string Column), PropertyInfo?> _readTargets = new();
    private static readonly ConcurrentDictionary<(Type Type, string Column), bool> _largeOnRead = new();

    public static TypeMetadata GetMetadata(Type t)
        => _cache.GetOrAdd(t, CreateMetadata);

    /// <summary>True when flattening metadata was built for <paramref name="t"/> (for tests).</summary>
    internal static bool HasMetadata(Type t) => _cache.ContainsKey(t);

    /// <summary>
    /// The bound of the large-value walk: how many types it inspects below one entity type, and how many
    /// steps the listing of the columns may take. No real entity comes near it; it keeps a type graph
    /// with factorially many paths, or with no end (a generic type whose property is a deeper
    /// instantiation of itself), from hanging or overflowing the stack in a store's static initializer.
    /// </summary>
    internal const int MaxLargeValueWalkSteps = 10_000;

    /// <summary>The longest property name Azure Table Storage accepts: a longer path names no column.</summary>
    private const int MaxColumnNameLength = 255;

    /// <summary>
    /// How deep a column of at most <see cref="MaxColumnNameLength"/> characters can lie: every level
    /// adds a name of at least one character and the path delimiter.
    /// </summary>
    private const int MaxFlattenDepth = MaxColumnNameLength / 2 + 1;

    /// <summary>
    /// True when the declared types of <paramref name="t"/> opt into large values: <paramref name="t"/>,
    /// or a nested type it flattens into columns, has a <see cref="TableValueSplitAttribute"/> property, a
    /// <see cref="TableBlobContent"/> property or a class-level <see cref="TableValueSplitAttribute"/> that
    /// covers a property; or <paramref name="t"/> itself (or a base class) carries a class-level
    /// <see cref="TableValueSplitAttribute"/>, also when it covers none of its properties: that is how an
    /// entity opts in whose large values are below a property whose type is known only at run time (an
    /// interface, an abstract class, a base class). Such a type may produce split cells or blob references;
    /// any other type takes none of the large-value branches. Decided by reflection alone: nothing is
    /// constructed and no getter is called, so a store may ask this in its static initializer. Never
    /// throws, whatever the type, and the answer is the same in every process and at every moment.
    /// </summary>
    internal static bool IsLargeValueAware(Type t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return _aware.GetOrAdd(t, static type => DeclaredLargeValues(type).Aware || HasClassLevelSplit(type));
    }

    /// <summary>
    /// The flattened base columns of <paramref name="t"/> that can carry split cells or a blob reference
    /// (<c>"Body"</c>, <c>"Inner_Body"</c>, <c>"Content"</c>): one for every path of declared types that
    /// does not meet a type twice (a type that reaches itself ends the path) and whose name a column can
    /// hold, in declaration order. A type graph too dense to list every such path within
    /// <see cref="MaxLargeValueWalkSteps"/> lists the paths nearest the entity instead. For an entity
    /// type that is large-value aware (<see cref="IsLargeValueAware"/>) the columns of the runtime types of
    /// its default instances follow: the first call constructs one instance of the entity and reads the
    /// properties that may hold such an object. That is remembered once it succeeded; a constructor or
    /// getter that fails leaves the columns of the declared types, and a later call tries again. Never
    /// throws; empty, and nothing constructed, for a type that is not large-value aware.
    /// </summary>
    internal static IReadOnlyList<string> LargeValueColumns(Type t) => LargeValues(t, out _).Columns;

    /// <summary>
    /// <see cref="LargeValueColumns(Type)"/>, with <paramref name="remembered"/> true when the list is the one
    /// remembered for the type (the probe completed, or the type is not large-value aware) and false when a
    /// constructor or getter of the default instance failed: a caller that keeps something built from the
    /// list keeps it only when it is true.
    /// </summary>
    internal static IReadOnlyList<string> LargeValueColumns(Type t, out bool remembered) => LargeValues(t, out remembered).Columns;

    private static LargeValueInfo LargeValues(Type t, out bool remembered)
    {
        ArgumentNullException.ThrowIfNull(t);
        remembered = true;
        // A type that opts into nothing by its declared types is never constructed to answer this.
        if (!IsLargeValueAware(t))
            return DeclaredLargeValues(t);
        if (_largeValues.TryGetValue(t, out var known))
            return known;

        var info = WithDefaultInstances(t, DeclaredLargeValues(t), out var complete);
        remembered = complete;
        return complete ? _largeValues.GetOrAdd(t, info) : info;
    }

    // A class-level [TableValueSplit] on t or on a base class (read by reflection; never throws).
    private static bool HasClassLevelSplit(Type t)
    {
#pragma warning disable CA1031 // deciding whether a type is large-value aware must not throw, whatever reflection does
        try
        {
            return ClassLevelSplit(t) is not null;
        }
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }

    // What the declared types alone say (reflection only, nothing is constructed).
    private static LargeValueInfo DeclaredLargeValues(Type t) =>
        _declaredLargeValues.GetOrAdd(t, static type => WalkLargeValues(type));

    // Adds the columns a read takes a large value from that the declared walk cannot see. Every column
    // the declared walk listed stays where it is; the others follow.
    //  - The columns only a new instance of an entity type shows: the large-value members of the objects
    //    its constructor puts in its properties. A read keeps such a default instance and maps a column
    //    through its runtime type, which may have members its declared type (an interface, an abstract
    //    class, a base class) lacks.
    //  - The legacy columns of the type's own large-value properties ([ColumnAlias]): a row written
    //    before the rename holds the value, its split cells or its blob reference under the old name.
    // Only ever called for a type that is large-value aware by its declared types. complete is false when
    // the constructor or a getter of the default instance failed: then the answer is not remembered.
    private static LargeValueInfo WithDefaultInstances(Type root, LargeValueInfo declared, out bool complete)
    {
        var known = new HashSet<string>(declared.Columns, StringComparer.Ordinal);
        var found = new List<string>();
        complete = true;
#pragma warning disable CA1031 // listing the columns must not throw, whatever its constructor, getters or attributes do
        try
        {
            if (DefaultInstanceOf(root) is { } instance)
            {
                var steps = 0;
                complete = ListDefaultInstanceColumns(instance, string.Empty, [instance], isRoot: true, known, found, ref steps);
            }
        }
        catch (Exception)
        {
            // The declared types' columns stand for now; a later call tries again.
            complete = false;
        }

        try
        {
            AddLegacyColumns(root, known, found);
        }
        catch (Exception)
        {
            // An alias that cannot be read is reported by the serializer on first use of the type.
        }
#pragma warning restore CA1031
        return found.Count == 0 ? declared : new LargeValueInfo(true, [.. declared.Columns, .. found]);
    }

    // The legacy column names of root's own large-value properties: [ColumnAlias("Old")] on the property,
    // or [ColumnAlias("Property", "Old")] on the class (aliases apply to top-level columns only). Read by
    // reflection; a declaration the serializer would refuse is simply not listed here.
    private static void AddLegacyColumns(Type root, HashSet<string> known, List<string> found)
    {
        var large = LargeValueMembers(root).FindAll(m => m.Flattened is null);
        if (large.Count == 0)
            return;

        void Add(string? legacy)
        {
            if (!string.IsNullOrWhiteSpace(legacy) && legacy.Length <= MaxColumnNameLength && known.Add(legacy))
                found.Add(legacy);
        }

        foreach (var member in large)
        {
            foreach (var alias in member.Property.GetCustomAttributes<ColumnAliasAttribute>(inherit: true))
            {
                if (alias.PropertyName is null)
                    Add(alias.LegacyColumnName);
            }
        }

        foreach (var alias in root.GetCustomAttributes<ColumnAliasAttribute>(inherit: true))
        {
            if (alias.PropertyName is { } name && large.Exists(m => m.Name == name))
                Add(alias.LegacyColumnName);
        }
    }

    // A new instance of root, as a read starts from, when its default instances can matter. Only an
    // entity type that is large-value aware by its declared types is constructed (a store's T: a class
    // with a public parameterless constructor that implements IEntity), and only when it has a property
    // whose declared type flattens into columns; no other type is ever constructed to answer this.
    private static object? DefaultInstanceOf(Type root)
    {
        if (!root.IsClass || root.IsAbstract || root.ContainsGenericParameters
            || !typeof(IEntity).IsAssignableFrom(root)
            || root.GetConstructor(Type.EmptyTypes) is null
            || !LargeValueMembers(root).Exists(m => m.Flattened is not null))
            return null;

        return Activator.CreateInstance(root);
    }

    // The large-value columns below one object of a new entity's graph: its own large-value members,
    // those of the objects in its properties and, where a property is empty, those the declared walk
    // lists for the property's declared type (a read creates that type there). The entity's own empty
    // properties add nothing: the declared walk of the entity has listed them. Bounded like that walk.
    // False when a getter failed (what it would have shown is missing from the list).
    private static bool ListDefaultInstanceColumns(
        object instance, string prefix, List<object> owners, bool isRoot, HashSet<string> known, List<string> found, ref int steps)
    {
        if (++steps > MaxLargeValueWalkSteps)
            return true;

        var complete = true;

        void Add(string column)
        {
            if (column.Length <= MaxColumnNameLength && known.Count < MaxLargeValueWalkSteps && known.Add(column))
                found.Add(column);
        }

        foreach (var member in LargeValueMembers(instance.GetType()))
        {
            var column = prefix + member.Name;
            if (member.Flattened is not { } declared)
            {
                Add(column);
                continue;
            }

            if (column.Length >= MaxColumnNameLength)
                continue;

            object? value;
#pragma warning disable CA1031 // a getter that fails shows no default instance
            try
            {
                value = member.Property.GetValue(instance);
            }
            catch (Exception)
            {
                complete = false;
                continue;
            }
#pragma warning restore CA1031

            if (value is null)
            {
                if (!isRoot)
                {
                    foreach (var below in DeclaredLargeValues(declared).Columns)
                        Add(column + TableEntityValue.PathDelimiter + below);
                }
            }
            else if (FlattensAtRuntime(value) && owners.Count < MaxFlattenDepth && !owners.Exists(o => ReferenceEquals(o, value)))
            {
                owners.Add(value);
                complete &= ListDefaultInstanceColumns(value, column + TableEntityValue.PathDelimiter, owners, isRoot: false, known, found, ref steps);
                owners.RemoveAt(owners.Count - 1);
            }
        }
        return complete;
    }

    // True for an object the writer flattens into Owner_Child columns (and a read then drills into).
    private static bool FlattensAtRuntime(object value) =>
        value is not (string or TableBlobContent)
        && !TableEntityValue.IsPrimitive(value.GetType()) && !value.GetType().IsEnum
        && TableEntityValue.CanSerializeWithoutJson(value);

    /// <summary>What the large-value walk found below one type.</summary>
    private sealed class LargeValueInfo(bool aware, IReadOnlyList<string> columns)
    {
        public static readonly LargeValueInfo None = new(false, []);

        public bool Aware { get; } = aware;

        public IReadOnlyList<string> Columns { get; } = columns;
    }

    // Walks the property graph Flatten walks, by reflection alone: a large-value member is a column; a
    // property whose declared type flattens into Owner_Child columns is walked. It never builds
    // flattening metadata (GetMetadata): 0.8.3 built that only when a value of a type was first
    // serialized, and it throws for types the writer never flattens (JsonNode declares two indexers
    // named Item). Every type is inspected once, and a branch that leads to no large-value member is
    // never listed, so a type that opts into nothing costs one pass over its types however many paths
    // run through them.
    private static LargeValueInfo WalkLargeValues(Type root)
    {
        // 1. The types below the root, each inspected once, nearest first.
        var members = new Dictionary<Type, List<LargeValueMember>>();
        var found = new Queue<(Type Type, int Depth)>();
        found.Enqueue((root, 0));
        while (members.Count < MaxLargeValueWalkSteps && found.TryDequeue(out var next))
        {
            if (members.ContainsKey(next.Type))
                continue;

            var own = LargeValueMembers(next.Type);
            members.Add(next.Type, own);
            if (next.Depth >= MaxFlattenDepth)
                continue;

            foreach (var member in own)
            {
                if (member.Flattened is { } nested && !members.ContainsKey(nested))
                    found.Enqueue((nested, next.Depth + 1));
            }
        }

        // 2. The types that lead to a large-value member: those that have one, and every type above them.
        var owners = new Dictionary<Type, List<Type>>();
        var leads = new HashSet<Type>();
        var reached = new Queue<Type>();
        foreach (var (type, own) in members)
        {
            foreach (var member in own)
            {
                if (member.Flattened is not { } nested)
                {
                    if (leads.Add(type))
                        reached.Enqueue(type);
                    continue;
                }

                if (!owners.TryGetValue(nested, out var above))
                    owners[nested] = above = [];
                above.Add(type);
            }
        }
        while (reached.TryDequeue(out var type))
        {
            if (!owners.TryGetValue(type, out var above))
                continue;

            foreach (var owner in above)
            {
                if (leads.Add(owner))
                    reached.Enqueue(owner);
            }
        }
        if (!leads.Contains(root))
            return LargeValueInfo.None;

        // 3. The columns: every path, or the nearest ones when there are too many paths to list.
        var columns = new List<string>();
        var steps = 0;
        if (!TryListEveryPath(new WalkStep(root, string.Empty, null), members, leads, columns, ref steps))
        {
            columns.Clear();
            ListNearestPaths(root, members, leads, columns);
        }
        return new LargeValueInfo(true, columns.AsReadOnly());
    }

    // Every path through the types that lead to a large-value member, depth first in declaration
    // order. False when that takes more than MaxLargeValueWalkSteps. The column-name limit bounds the
    // depth, so an endless chain of types ends too.
    private static bool TryListEveryPath(
        WalkStep step, Dictionary<Type, List<LargeValueMember>> members, HashSet<Type> leads, List<string> columns, ref int steps)
    {
        if (++steps > MaxLargeValueWalkSteps)
            return false;

        foreach (var member in members[step.Type])
        {
            var column = step.Prefix + member.Name;
            if (column.Length > MaxColumnNameLength)
                continue;

            if (member.Flattened is not { } nested)
                columns.Add(column);
            else if (leads.Contains(nested) && !step.Meets(nested)
                     && !TryListEveryPath(new WalkStep(nested, column + TableEntityValue.PathDelimiter, step), members, leads, columns, ref steps))
                return false;
        }

        return true;
    }

    // The same paths breadth first, for a type graph with too many to list: the columns nearest the
    // root come first, and the listing stops after MaxLargeValueWalkSteps.
    private static void ListNearestPaths(
        Type root, Dictionary<Type, List<LargeValueMember>> members, HashSet<Type> leads, List<string> columns)
    {
        var queue = new Queue<WalkStep>();
        queue.Enqueue(new WalkStep(root, string.Empty, null));
        for (var steps = 1; queue.TryDequeue(out var step); steps++)
        {
            foreach (var member in members[step.Type])
            {
                var column = step.Prefix + member.Name;
                if (column.Length > MaxColumnNameLength)
                    continue;

                if (member.Flattened is not { } nested)
                    columns.Add(column);
                else if (leads.Contains(nested) && !step.Meets(nested) && steps + queue.Count < MaxLargeValueWalkSteps)
                    queue.Enqueue(new WalkStep(nested, column + TableEntityValue.PathDelimiter, step));
            }
        }
    }

    /// <summary>One type on a path from the root, with the column prefix that leads to it.</summary>
    private sealed class WalkStep(Type type, string prefix, WalkStep? owner)
    {
        public Type Type { get; } = type;

        public string Prefix { get; } = prefix;

        /// <summary>True when the path already passes through <paramref name="other"/>.</summary>
        public bool Meets(Type other)
        {
            for (var step = this; step is not null; step = step.Owner)
            {
                if (step.Type == other)
                    return true;
            }
            return false;
        }

        private WalkStep? Owner { get; } = owner;
    }

    /// <summary>
    /// One property of a type as the large-value walk sees it: a large-value column of its own
    /// (<see cref="Flattened"/> is null), or a property whose declared type flattens into columns.
    /// </summary>
    private readonly record struct LargeValueMember(string Name, Type? Flattened, PropertyInfo Property);

    // The properties of one type that are, or may lead to, large-value columns, in declaration order.
    // Reflection over an arbitrary type can fail (a member type that does not load, a dynamic type).
    // A property that cannot be read contributes nothing and the others still count; a type whose
    // properties cannot be listed contributes nothing. Neither costs data: the serializer reflects over
    // the same members before it writes a value of the type, and fails there.
    private static List<LargeValueMember> LargeValueMembers(Type type)
    {
        var members = new List<LargeValueMember>();
#pragma warning disable CA1031 // deciding whether a type is large-value aware must not throw, whatever reflection does
        try
        {
            var classLevel = ClassLevelSplit(type);
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    if (LargeValueMemberOf(type, prop, classLevel) is { } member)
                        members.Add(member);
                }
                catch (Exception)
                {
                    // This property is not a column the walk can describe.
                }
            }
        }
        catch (Exception)
        {
            // The properties of this type cannot be listed.
        }
#pragma warning restore CA1031
        return members;
    }

    private static LargeValueMember? LargeValueMemberOf(Type type, PropertyInfo prop, TableValueSplitAttribute? classLevel)
    {
        // An indexer is never a column: the writer cannot read it without an index.
        if (!IsSerialized(prop) || prop.GetIndexParameters().Length > 0)
            return null;

        var propertyType = prop.PropertyType;
        if (propertyType == typeof(TableBlobContent) || SplitFor(type, prop, classLevel, validate: false) is not null)
            return new LargeValueMember(prop.Name, Flattened: null, prop);
        return FlattensIntoColumns(propertyType) ? new LargeValueMember(prop.Name, propertyType, prop) : null;
    }

    /// <summary>
    /// The stored property of <paramref name="owner"/> named <paramref name="name"/>, read by reflection
    /// alone (no flattening metadata is built for <paramref name="owner"/>, which may be a declared type
    /// the serializer never flattens): its declared type, and whether it is a large-value member. False
    /// when the type stores no such property.
    /// </summary>
    internal static bool TryGetMember(Type owner, string name, out Type memberType, out bool largeValue)
    {
        foreach (var prop in SerializedProperties(owner))
        {
            if (prop.Name != name || prop.GetIndexParameters().Length > 0)
                continue;

            memberType = prop.PropertyType;
            largeValue = memberType == typeof(TableBlobContent)
                         || SplitFor(owner, prop, ClassLevelSplit(owner), validate: false) is not null;
            return true;
        }

        memberType = typeof(object);
        largeValue = false;
        return false;
    }

    /// <summary>
    /// The property a read of <paramref name="root"/> sets the flattened column path
    /// (<c>["Inner", "Body"]</c>) on, resolved as an ordinary cell is set: every owner along the path is
    /// the instance that is already there, so a default instance keeps its runtime type (which may have
    /// members its declared type lacks), or a new instance of the declared type where there is none.
    /// Null when no property takes the column. Nothing is changed on <paramref name="root"/>.
    /// </summary>
    /// <exception cref="Exception">
    /// Whatever creating a missing owner throws: an interface or abstract type has no instance to create,
    /// and an ordinary cell below it fails the same way.
    /// </exception>
    internal static PropertyInfo? ReadTarget(object root, IReadOnlyList<string> path)
    {
        var owner = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (!GetMetadata(owner.GetType()).PropertyMap.TryGetValue(path[i], out var property))
                return null;
            if (property.GetValue(owner) is not { } child)
                return NewInstanceReadTarget(property.PropertyType, string.Join(TableEntityValue.PathDelimiter, path.Skip(i + 1)));
            owner = child;
        }
        return GetMetadata(owner.GetType()).PropertyMap.TryGetValue(path[^1], out var leaf) ? leaf : null;
    }

    /// <summary>The most resolved columns <see cref="ReadTarget(Type, string)"/> remembers; beyond it they are resolved again.</summary>
    private const int MaxRememberedReadTargets = 10_000;

    /// <summary>
    /// The property a read of <paramref name="type"/> sets the flattened <paramref name="column"/> on, or
    /// null. For a type that is large-value aware (<see cref="IsLargeValueAware"/>) it is
    /// <see cref="ReadTarget(object, IReadOnlyList{string})"/> on a new instance of the type, as a read
    /// starts from: the answer is remembered per type and column; a failure is not, and is thrown again.
    /// For any other type nothing is constructed and no getter is called: the column is resolved through
    /// the declared property types alone (null where a declared type has no such property).
    /// </summary>
    internal static PropertyInfo? ReadTarget(Type type, string column)
    {
        ArgumentNullException.ThrowIfNull(type);
        return IsLargeValueAware(type) ? NewInstanceReadTarget(type, column) : DeclaredReadTarget(type, column);
    }

    // ReadTarget(object, path) on a new instance of type (the declared type of an empty property of an
    // object a read is building, or an aware entity type), remembered per type and column.
    private static PropertyInfo? NewInstanceReadTarget(Type type, string column)
    {
        if (_readTargets.TryGetValue((type, column), out var known))
            return known;

        var path = TableEntityValue.Create(column, string.Empty).Path;
        var target = path.IsEmpty ? null : ReadTarget(GetMetadata(type).Creator(), path);
        if (_readTargets.Count < MaxRememberedReadTargets)
            _readTargets.TryAdd((type, column), target);
        return target;
    }

    // The property the column names when every owner along it is of its declared type: reflection only,
    // no flattening metadata, nothing constructed.
    private static PropertyInfo? DeclaredReadTarget(Type type, string column)
    {
        var path = TableEntityValue.Create(column, string.Empty).Path;
        if (path.IsEmpty)
            return null;
        var owner = type;
        PropertyInfo? property = null;
        foreach (var segment in path)
        {
            if (property is not null)
                owner = property.PropertyType;
            property = SerializedProperties(owner).FirstOrDefault(p => p.Name == segment && p.GetIndexParameters().Length == 0);
            if (property is null)
                return null;
        }
        return property;
    }

    /// <summary>
    /// True when the property a read of <paramref name="type"/> sets <paramref name="column"/> on is a
    /// large-value member: a <see cref="TableBlobContent"/>, or one its owner's runtime type opts in.
    /// Never throws. The answer is remembered once the column was resolved; when resolving it throws (the
    /// entity's constructor or a getter failed, or an owner along the path cannot be created) the answer
    /// is false and is not remembered: <see cref="IsLargeValueOnRead(Type, string, out Exception?)"/> tells
    /// that case apart. False, with nothing constructed, for a type that is not large-value aware
    /// (<see cref="IsLargeValueAware"/>).
    /// </summary>
    internal static bool IsLargeValueOnRead(Type type, string column) => IsLargeValueOnRead(type, column, out _);

    /// <summary>
    /// <see cref="IsLargeValueOnRead(Type, string)"/>, with <paramref name="unresolved"/> set to the exception
    /// of the resolution when it threw: then the library cannot tell whether the column is a large value, and
    /// a write that depends on the answer refuses rather than take the false for "not one".
    /// </summary>
    internal static bool IsLargeValueOnRead(Type type, string column, out Exception? unresolved)
    {
        ArgumentNullException.ThrowIfNull(type);
        unresolved = null;
        if (!IsLargeValueAware(type))
            return false;
        if (_largeOnRead.TryGetValue((type, column), out var known))
            return known;

        bool large;
#pragma warning disable CA1031 // a path the check cannot resolve is reported to the caller, never remembered
        try
        {
            large = ReadTarget(type, column) is { ReflectedType: { } owner } leaf
                && (leaf.PropertyType == typeof(TableBlobContent) || GetMetadata(owner).SplitOptions.ContainsKey(leaf.Name));
        }
        catch (Exception ex)
        {
            // Not remembered: the constructor or a getter may succeed at the next call, and the answer
            // must not depend on whether it failed once.
            unresolved = ex;
            return false;
        }
#pragma warning restore CA1031
        if (_largeOnRead.Count < MaxRememberedReadTargets)
            _largeOnRead.TryAdd((type, column), large);
        return large;
    }

    /// <summary>
    /// True only when a read of <paramref name="root"/> maps <paramref name="column"/> to a property. False
    /// when it maps the column to no property (a value stored under it would never be read), and false, with
    /// <paramref name="unresolved"/> set, when resolving the column throws: the read fails at an owner it
    /// cannot create (a struct held in a nullable property, whose creator yields nothing; an interface or an
    /// abstract type with no instance), so every read of a row holding the value would fail. A check that
    /// cannot decide refuses. Resolved as <see cref="ReadTarget(Type, string)"/> resolves it: nothing is
    /// constructed for a type that is not large-value aware.
    /// </summary>
    internal static bool ReadsBack(Type root, string column, out bool unresolved)
    {
        unresolved = false;
#pragma warning disable CA1031 // a column the check cannot resolve is a column no read returns
        try
        {
            return ReadTarget(root, column) is not null;
        }
        catch (Exception)
        {
            unresolved = true;
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary><see cref="ReadsBack(Type, string, out bool)"/> without the reason.</summary>
    internal static bool ReadsBack(Type root, string column) => ReadsBack(root, column, out _);

    // A property type the serializer stores natively (no columns of its own to walk).
    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(string) || type == typeof(byte[]) || TableEntityValue.IsPrimitive(type) || underlying.IsEnum;
    }

    private static bool FlattensIntoColumns(Type type) =>
        !IsScalar(type) && TableEntitySerializer.FlattensToColumns(type);

    // The public instance properties the serializer stores as columns (the rule of 0.8.3, unchanged).
    private static IEnumerable<PropertyInfo> SerializedProperties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(IsSerialized);

    private static bool IsSerialized(PropertyInfo p) =>
        p.CanRead && p.CanWrite
        && p.GetCustomAttribute<IgnoreDataMemberAttribute>() == null
        && p.Name != nameof(TableEntity.ETag)
        && p.Name != nameof(TableEntity.Timestamp);

    private static TypeMetadata CreateMetadata(Type t)
    {
        var props = SerializedProperties(t).ToImmutableList();

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

    // [TableValueSplit] per property; see SplitFor. A misused attribute fails here, on first use of the type.
    private static Dictionary<string, TableValueSplitAttribute> BuildSplitOptions(Type t, ImmutableList<PropertyInfo> props)
    {
        RefuseInterfaceMemberSplit(t);
        var options = new Dictionary<string, TableValueSplitAttribute>(StringComparer.Ordinal);
        var classLevel = ClassLevelSplit(t);
        foreach (var prop in props)
        {
            if (SplitFor(t, prop, classLevel, validate: true) is { } split)
                options[prop.Name] = split;
        }

        return options;
    }

    // [TableValueSplit] on a member declared on an interface that t implements is a misused attribute: .NET
    // does not pass the attributes of an interface member to the implementing class, so the writer, which
    // reads the members of the runtime type, would store the value as an ordinary cell (and a trimming
    // OversizedCellPolicy would trim it). It opts nothing in (SplitFor ignores it) and fails here, on every
    // use of a type that implements the interface. Attributes are read by reflection alone; an interface
    // whose members cannot be read is skipped (the serializer never reads its attributes otherwise).
    private static void RefuseInterfaceMemberSplit(Type t)
    {
        if (t.IsInterface)
            return;

        foreach (var contract in t.GetInterfaces())
        {
            PropertyInfo? marked;
#pragma warning disable CA1031 // an interface the check cannot read has no attribute it could report
            try
            {
                marked = Array.Find(contract.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                    static p => p.IsDefined(typeof(TableValueSplitAttribute), inherit: false));
            }
            catch (Exception)
            {
                continue;
            }
#pragma warning restore CA1031
            if (marked is not null)
                throw new InvalidOperationException(
                    $"[TableValueSplit] on {contract.Name}.{marked.Name}, a member declared on an interface that {t.Name} implements: " +
                    ".NET does not pass the attributes of an interface member to the implementing class, so the attribute cannot be " +
                    $"honoured (the value would be stored as an ordinary cell, and could be trimmed). Put [TableValueSplit] on " +
                    $"{t.Name}.{marked.Name} (the property of the implementing class), or on the class {t.Name}, and remove it from " +
                    $"{contract.Name}. Where an entity holds a {t.Name} in a property declared as {contract.Name} (or as another type), " +
                    "mark the entity class with [TableValueSplit] as well, so that the entity is large-value aware.");
        }
    }

    // The class-level [TableValueSplit] that applies to t: the nearest one, on t itself or on a base
    // class (a derived class inherits it, and may declare its own).
    private static TableValueSplitAttribute? ClassLevelSplit(Type t)
    {
        for (var cur = t; cur is not null; cur = cur.BaseType)
        {
            if (cur.GetCustomAttribute<TableValueSplitAttribute>(inherit: false) is { } attribute)
                return attribute;
        }
        return null;
    }

    // The [TableValueSplit] that applies to one property of t, or null.
    //  - A property-level attribute wins: the property's own, or the one of the base property it
    //    re-declares (override or new), so a re-declaration keeps the opt-in.
    //  - Otherwise the class-level attribute of t covers every string, byte[] and JSON-stored property
    //    of t, declared or inherited - but never the entity id (it stays an ordinary, filterable key
    //    column) nor a member that is not stored as an ordinary column (an indexer; ETag, Timestamp and
    //    [IgnoreDataMember] members are not serialized properties at all).
    // A property-level attribute on a scalar or on a concrete type that flattens into columns has
    // nothing to split; one on an interface or abstract type that may flatten at runtime cannot be
    // honoured. With validate such a misuse throws (the serializer, on first use of the type); without,
    // the property is simply not opted in (the large-value walk, which must not throw).
    // A member declared on an interface (t is the interface) is never opted in: the writer reads the
    // members of the implementing class, where the attribute is not visible (RefuseInterfaceMemberSplit).
    private static TableValueSplitAttribute? SplitFor(Type t, PropertyInfo prop, TableValueSplitAttribute? classLevel, bool validate)
    {
        if (t.IsInterface)
            return null;
        // The property's type is resolved only for a property an attribute may cover: the metadata of
        // a type that opts into nothing is built from property names and attributes alone, as on 0.8.3.
        if (PropertyLevelSplit(prop) is not { } own)
            return classLevel is not null && IsOrdinaryColumn(t, prop) && IsSplittable(prop.PropertyType) ? classLevel : null;

        var type = prop.PropertyType;
        if (type == typeof(TableBlobContent))
            return !validate ? null : throw new InvalidOperationException(
                $"[TableValueSplit] on {t.Name}.{prop.Name}: a TableBlobContent is always stored in blob storage. Remove the attribute.");
        if (IsScalar(type) && type != typeof(string) && type != typeof(byte[]))
            return !validate ? null : throw new InvalidOperationException(
                $"[TableValueSplit] on {t.Name}.{prop.Name}: a {type.Name} is a native cell and cannot be split. " +
                "Only string, byte[] and JSON-stored properties can.");
        if (FlattensIntoColumns(type) && !type.IsInterface && !type.IsAbstract)
            return !validate ? null : throw new InvalidOperationException(
                $"[TableValueSplit] on {t.Name}.{prop.Name}: {type.Name} is flattened into columns. " +
                "Put [TableValueSplit] on its members instead.");
        // An interface or abstract type flattens or not depending on the runtime value; a
        // flattened value would silently ignore the attribute (and could then be trimmed).
        if (!IsSplittable(type))
            return !validate ? null : throw new NotSupportedException(
                $"[TableValueSplit] on {t.Name}.{prop.Name}: {type.Name} is an interface or abstract type, which may be " +
                "flattened into columns at runtime, so the attribute cannot be honoured. Declare the property as " +
                "string, byte[] or a concrete type stored as JSON, or put [TableValueSplit] on the members of its implementations.");
        return own;
    }

    // The property's own [TableValueSplit], or that of the base property it re-declares: an override
    // inherits it through reflection; a property that hides (new) a base property of the same name and
    // type does not, so the hidden declarations are looked up (by name first: a property that
    // re-declares nothing resolves no type).
    private static TableValueSplitAttribute? PropertyLevelSplit(PropertyInfo prop)
    {
        if (prop.GetCustomAttribute<TableValueSplitAttribute>(inherit: true) is { } own)
            return own;

        for (var cur = prop.DeclaringType?.BaseType; cur is not null; cur = cur.BaseType)
        {
            foreach (var hidden in cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (hidden.Name == prop.Name
                    && hidden.GetIndexParameters().Length == 0 && prop.GetIndexParameters().Length == 0
                    && hidden.PropertyType == prop.PropertyType
                    && hidden.GetCustomAttribute<TableValueSplitAttribute>(inherit: true) is { } inherited)
                    return inherited;
            }
        }
        return null;
    }

    // A property a class-level attribute may opt in: one the writer stores as an ordinary column (not
    // an indexer, nor a name with the path delimiter, which the writer refuses), and not the entity id.
    private static bool IsOrdinaryColumn(Type t, PropertyInfo prop) =>
        prop.GetIndexParameters().Length == 0
        && !TableEntityValue.ContainsPathDelimiter(prop.Name)
        && !(prop.Name == nameof(IEntity.Id) && typeof(IEntity).IsAssignableFrom(t));

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
