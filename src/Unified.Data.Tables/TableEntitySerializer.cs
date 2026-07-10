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
    /// <summary>Column that stores the assembly-qualified type name when <c>persistType</c> is used.</summary>
    public const string TypeNameColumnName = "_TypeName";

    /// <summary>Flatten and serialize <paramref name="root"/> into a <see cref="TableEntity"/>.</summary>
    public static TableEntity ToTableEntity(
        this object root,
        string partitionKey,
        string rowKey,
        bool persistType = false)
    {
        var entity = new TableEntity(partitionKey, rowKey);
        if (root == null) return entity;

        var flat = Flatten(root, persistType);
        foreach (var kv in flat)
            entity[kv.Key] = kv.Value;

        return entity;
    }

    /// <summary>Deserialize into a new <typeparamref name="T"/> (requires a public parameterless ctor).</summary>
    public static T FromTableEntity<T>(this TableEntity entity)
        where T : new()
    {
        ArgumentNullException.ThrowIfNull(entity);
        var meta = TypeMetadataCache.GetMetadata(typeof(T));
        var result = (T)meta.Creator();

        foreach (var kv in entity)
        {
            var val = TableEntityValue.Create(kv.Key, kv.Value);
            result = (T)SetProperty(result, val);
        }

        return (T)ApplyColumnAliases(result!, entity, meta);
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
        var meta = TypeMetadataCache.GetMetadata(t);
        var result = meta.Creator();

        foreach (var kv in entity)
        {
            var val = TableEntityValue.Create(kv.Key, kv.Value);
            result = SetProperty(result, val);
        }

        return ApplyColumnAliases(result, entity, meta);
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
            if (hasCanonical)
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
    /// like the full <see cref="ToTableEntity"/> path. Used by partial-update (Merge) flows and by
    /// alternative <see cref="IStorage{T}"/> implementations (e.g. in-memory test doubles).
    /// </summary>
    public static Dictionary<string, object> FlattenProperty(string propertyName, object value)
    {
        var dict = new Dictionary<string, object>();
        if (value == null) return dict;

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var node = new TableEntityValue(ImmutableList<string>.Empty.Add(propertyName), value);
        if (!Flatten(dict, seen, node))
            throw new InvalidOperationException($"Cannot flatten property '{propertyName}' of type {value.GetType()}.");

        return dict;
    }

    //───────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, object> Flatten(object root, bool persistType)
    {
        var dict = new Dictionary<string, object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var top = new TableEntityValue(ImmutableList<string>.Empty, root);

        if (!Flatten(dict, seen, top))
            throw new InvalidOperationException($"Cannot flatten object of type {root.GetType()}.");

        if (persistType)
            dict[TypeNameColumnName] = root.GetType().AssemblyQualifiedName!;

        return dict;
    }

    private static bool Flatten(
        Dictionary<string, object> dict,
        ISet<object> seen,
        TableEntityValue value)
    {
        // nulls are fine
        if (value.Value == null) return true;

        // primitive/json/gzip cell (a null cell means the property was dropped because even its
        // compressed form exceeds the 64 KB cell cap — omit the column, keep the row)
        if (value.TrySerialize(out var col, out var cell))
        {
            if (cell != null)
                dict[col] = cell;
            return true;
        }

        // cycle detection
        if (!value.Type!.IsValueType && !seen.Add(value.Value))
            throw new SerializationException($"Circular reference at '{value}'.");

        // recurse into cached properties
        var meta = TypeMetadataCache.GetMetadata(value.Type);
        foreach (var prop in meta.Properties)
        {
            var childVal = prop.GetValue(value.Value);
            var childNode = new TableEntityValue(value.Path.Add(prop.Name), childVal!);
            if (!Flatten(dict, seen, childNode))
                return false;
        }

        if (!value.Type.IsValueType)
            seen.Remove(value.Value);

        return true;
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

internal enum TableEntityValueFormat { Raw, Json, GZip }

internal sealed class TableEntityValue
{
    private const int MaxCellBytes = 65536;
    private const string Delim = "_";
    private const string JsonSuffix = "__Json";
    private const string GZipSuffix = "__GZip";
    private static readonly DateTimeOffset MinDto =
        new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Keep camelCase + case-insensitive matching
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
                return new DateTimeOffset(dt, TimeSpan.Zero);
            }
        },
        { typeof(DateTime?), v =>
            {
                var dt = ((DateTime?)v) ?? MinDto.DateTime;
                return new DateTimeOffset(dt, TimeSpan.Zero);
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

    /// <summary>
    /// True if this value can be stored directly as a table cell. Otherwise it will be flattened by
    /// the caller. A <c>true</c> result with a <c>null</c> out-value means the property was dropped
    /// because even its compressed form exceeds the cell cap.
    /// </summary>
    public bool TrySerialize(out string columnName, out object? value)
    {
        columnName = string.Join(Delim, Path);
        value = null;
        if (Value == null) return false;

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
                    value = TruncateForCell(s);
                }
            }

            return true;
        }

        if (vt.IsEnum)
        {
            value = Value.ToString()!;
            return true;
        }

        if (CanSerializeWithoutJson(Value))
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
            // cell the service rejects loses the WHOLE row. Trim list payloads to the largest prefix
            // that still fits; as a last resort drop the property (null cell = column omitted) so
            // the rest of the row survives.
            if (Encoding.Unicode.GetByteCount(json) > MaxCellBytes)
                json = Value is IList list ? CompressLargestFittingPrefix(list) : null;
        }
        else
        {
            columnName += JsonSuffix;
            Format = TableEntityValueFormat.Json;
        }

        value = json;
        return true;
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

    private static bool CanSerializeWithoutJson(object val)
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
            return ConvertLong(t, l);

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

        if (t == typeof(decimal))
            return Convert.ToDecimal(val, CultureInfo.InvariantCulture);

        return Convert.ChangeType(val, t, CultureInfo.InvariantCulture);
    }

    private static object ConvertDateTimeOffset(Type t, DateTimeOffset dto)
    {
        if (t == typeof(DateTimeOffset)) return dto;
        if (t == typeof(DateTime)) return dto.DateTime;
        return Convert.ChangeType(dto, t, CultureInfo.InvariantCulture);
    }

    private static object ConvertDateTime(Type t, DateTime dt)
    {
        if (t == typeof(DateTime)) return dt;
        if (t == typeof(DateTimeOffset))
        {
            // Azure Tables persists dates in UTC; treat Unspecified as UTC and normalize Local to
            // UTC so the resulting offset is zero.
            var utc = dt.Kind switch
            {
                DateTimeKind.Utc => dt,
                DateTimeKind.Local => dt.ToUniversalTime(),
                _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            };
            return new DateTimeOffset(utc);
        }
        return Convert.ChangeType(dt, t, CultureInfo.InvariantCulture);
    }

#pragma warning disable CA1859 // Method intentionally returns different boxed value types
    private static object ConvertLong(Type t, long l)
#pragma warning restore CA1859
    {
        if (t == typeof(int)) return (int)l;
        if (t == typeof(uint)) return (uint)l;
        if (t == typeof(ulong)) return (ulong)l;
        return l;
    }

    // Leaves generous headroom under the 32K-char (64 KB UTF-16) cell limit for the marker.
    private const int TruncatedCellChars = 30_000;

    private static string TruncateForCell(string s)
    {
        // Never cut between a surrogate pair — a lone surrogate is invalid UTF-16 and corrupts (or
        // fails) the SDK's wire encoding.
        var cut = TruncatedCellChars;
        if (char.IsHighSurrogate(s[cut - 1]))
            cut--;
        return $"{s[..cut]}\n…[truncated {s.Length - cut} chars to fit table storage]";
    }

    private static string? CompressLargestFittingPrefix(IList list)
    {
        for (var n = list.Count / 2; ; n /= 2)
        {
            var prefix = new List<object?>(n);
            for (var i = 0; i < n; i++)
                prefix.Add(list[i]);
            var candidate = Compress(JsonSerializer.Serialize(prefix, JsonOptions));
            if (Encoding.Unicode.GetByteCount(candidate) <= MaxCellBytes)
                return candidate;
            if (n == 0)
                return null; // even an empty list doesn't fit — cannot happen in practice
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

    public static TypeMetadata GetMetadata(Type t)
        => _cache.GetOrAdd(t, CreateMetadata);

    private static TypeMetadata CreateMetadata(Type t)
    {
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.CanWrite
                              && p.GetCustomAttribute<IgnoreDataMemberAttribute>() == null
                              && p.Name != nameof(TableEntity.ETag)
                              && p.Name != nameof(TableEntity.Timestamp))
                     .ToImmutableList();

        var map = props.ToDictionary(p => p.Name, p => p);
        Func<object> creator = () => Activator.CreateInstance(t)!;

        return new TypeMetadata(props, map, BuildAliasMap(t, map), creator);
    }

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
    Func<object> creator)
{
    public ImmutableList<PropertyInfo> Properties { get; } = props;
    public Dictionary<string, PropertyInfo> PropertyMap { get; } = map;
    public Dictionary<string, PropertyInfo> AliasMap { get; } = aliasMap;
    public Func<object> Creator { get; } = creator;
}
