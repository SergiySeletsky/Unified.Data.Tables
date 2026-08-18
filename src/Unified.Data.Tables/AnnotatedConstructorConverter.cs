using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unified.Data.Tables;

/// <summary>
/// Lets System.Text.Json construct an immutable type whose only constructor is non-public and
/// marked with a <c>JsonConstructorAttribute</c> that is NOT
/// <see cref="System.Text.Json.Serialization.JsonConstructorAttribute"/> — in practice
/// <c>Newtonsoft.Json.JsonConstructorAttribute</c>.
/// </summary>
/// <remarks>
/// This is a WIRE-COMPATIBILITY concern, not a convenience. <see cref="TableEntitySerializer"/>
/// deliberately preserves the cell format of the Newtonsoft-based Azure table serializers it
/// replaces — the same <c>__Json</c> and <c>__GZip</c> suffixes over the same JSON. Rows written by
/// those serializers routinely hold immutable value objects built through a private annotated
/// constructor, which is the idiomatic Newtonsoft shape for a type with only getters. System.Text.Json
/// picks a constructor by its OWN attribute, a public parameterless one, or a single public
/// parameterized one; none of those exist on such a type, so it throws:
/// <para>
/// <c>NotSupportedException: Deserialization of types without a parameterless constructor, a singular
/// parameterized constructor, or a parameterized constructor annotated with 'JsonConstructorAttribute'
/// is not supported.</c>
/// </para>
/// Without this converter those rows are readable only by the serializer that wrote them, which
/// makes the format-compatibility guarantee false for exactly the types most likely to rely on it.
/// <para>
/// The attribute is matched by NAME rather than by type so this assembly takes no dependency on
/// Newtonsoft — and so any other library's equivalent annotation works too. A type that System.Text.Json
/// can already construct is never touched: <see cref="CanConvert"/> declines it, and the default
/// behaviour (including source-generated contracts for other types) is left intact.
/// </para>
/// </remarks>
internal sealed class AnnotatedConstructorConverterFactory : JsonConverterFactory
{
    private static readonly ConcurrentDictionary<Type, ConstructorInfo?> Constructors = new();

    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => FindConstructor(typeToConvert) is not null;

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(AnnotatedConstructorConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType, FindConstructor(typeToConvert)!)!;
    }

    internal static ConstructorInfo? FindConstructor(Type type) =>
        Constructors.GetOrAdd(type, static t =>
        {
            // Leave everything System.Text.Json already handles — and everything it handles
            // specially — strictly alone.
            if (t.IsAbstract || t.IsInterface || t.IsPrimitive || t.IsEnum || t.IsArray
                || t == typeof(string) || t.IsGenericTypeDefinition
                || Nullable.GetUnderlyingType(t) is not null)
            {
                return null;
            }

            var all = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            // A public parameterless constructor, System.Text.Json's own attribute, or a single
            // public parameterized constructor all mean System.Text.Json can cope on its own.
            if (all.Any(c => c.IsPublic && c.GetParameters().Length == 0)
                || all.Any(c => c.GetCustomAttribute<JsonConstructorAttribute>() is not null))
            {
                return null;
            }

            var publicParameterized = all.Where(c => c.IsPublic && c.GetParameters().Length > 0).ToArray();
            if (publicParameterized.Length == 1)
            {
                return null;
            }

            var annotated = all
                .Where(c => c.GetCustomAttributes()
                    .Any(a => a.GetType().Name == nameof(JsonConstructorAttribute)))
                .ToArray();

            // Exactly one annotated constructor, or the intent is ambiguous and guessing would be
            // worse than the framework's own error.
            return annotated.Length == 1 && annotated[0].GetParameters().Length > 0 ? annotated[0] : null;
        });
}

/// <summary>
/// Reads <typeparamref name="T"/> by matching JSON properties to the parameters of a constructor
/// System.Text.Json would not have selected. See <see cref="AnnotatedConstructorConverterFactory"/>
/// for why this exists.
/// </summary>
/// <typeparam name="T">The type being constructed.</typeparam>
internal sealed class AnnotatedConstructorConverter<T> : JsonConverter<T>
{
    private static readonly ConcurrentDictionary<JsonSerializerOptions, JsonSerializerOptions> Delegating = new();

    private readonly ConstructorInfo constructor;
    private readonly ParameterInfo[] parameters;

    /// <summary>Creates the converter for one constructor.</summary>
    /// <param name="constructor">The constructor to invoke.</param>
    public AnnotatedConstructorConverter(ConstructorInfo constructor)
    {
        this.constructor = constructor;
        parameters = constructor.GetParameters();
    }

    /// <inheritdoc />
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return default;

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            // Match on the parameter name as written and as the naming policy would render it, both
            // case-insensitively — the same latitude System.Text.Json gives its own constructor
            // binding, and what a camelCase-on-write / case-insensitive-on-read policy requires.
            args[i] = TryGetProperty(root, parameter.Name!, options, out var value)
                ? value.Deserialize(parameter.ParameterType, options)
                : DefaultOf(parameter);
        }

        return (T)constructor.Invoke(args);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        // Writing never needed help — only construction did. Delegate to the default contract by
        // serializing through options with this factory removed, so the bytes are byte-for-byte what
        // they were before this converter existed. Without the copy this would recurse forever.
        JsonSerializer.Serialize(writer, value, WithoutFactory(options));
    }

    private static bool TryGetProperty(
        JsonElement root, string name, JsonSerializerOptions options, out JsonElement value)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var candidate in Names(name, options))
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static IEnumerable<string> Names(string name, JsonSerializerOptions options)
    {
        yield return name;

        var converted = options.PropertyNamingPolicy?.ConvertName(name);
        if (converted is not null && !string.Equals(converted, name, StringComparison.Ordinal))
            yield return converted;
    }

    private static object? DefaultOf(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue)
            return parameter.DefaultValue;

        return parameter.ParameterType.IsValueType
            ? Activator.CreateInstance(parameter.ParameterType)
            : null;
    }

    // Cached by reference: building a JsonSerializerOptions rebuilds its whole contract cache, so
    // doing it per Write call would cost far more than the write itself.
    private static JsonSerializerOptions WithoutFactory(JsonSerializerOptions options) =>
        Delegating.GetOrAdd(options, static o =>
        {
            var copy = new JsonSerializerOptions(o);
            for (var i = copy.Converters.Count - 1; i >= 0; i--)
            {
                if (copy.Converters[i] is AnnotatedConstructorConverterFactory)
                    copy.Converters.RemoveAt(i);
            }

            return copy;
        });
}
