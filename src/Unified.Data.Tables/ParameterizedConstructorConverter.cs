using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unified.Data.Tables;

/// <summary>
/// Lets System.Text.Json construct an immutable type through a constructor it would not have
/// selected, or whose parameter binding it would have rejected. Two shapes are covered:
/// </summary>
/// <remarks>
/// This is a WIRE-COMPATIBILITY concern, not a convenience. <see cref="TableEntitySerializer"/>
/// deliberately preserves the cell format of the Newtonsoft-based Azure table serializers it
/// replaces — the same <c>__Json</c> and <c>__GZip</c> suffixes over the same JSON. Rows written by
/// those serializers routinely hold immutable value objects that System.Text.Json cannot
/// reconstruct on its own:
/// <list type="bullet">
/// <item>
/// A type whose ONLY constructor is non-public and marked with a <c>JsonConstructorAttribute</c>
/// that is NOT <see cref="System.Text.Json.Serialization.JsonConstructorAttribute"/> — in practice
/// <c>Newtonsoft.Json.JsonConstructorAttribute</c>. System.Text.Json throws
/// <c>NotSupportedException</c> for such a type. The attribute is matched by NAME rather than by
/// type so this assembly takes no dependency on Newtonsoft — and so any other library's equivalent
/// annotation works too.
/// </item>
/// <item>
/// A type whose single public parameterized constructor System.Text.Json WOULD select, but whose
/// parameters do not exactly match the property names AND types on the object. Newtonsoft bound
/// constructor parameters by name and deserialized each argument to the parameter's declared type,
/// so shapes like an <c>IEnumerable&lt;T&gt;</c> parameter fed by an <c>ImmutableList&lt;T&gt;</c>
/// property round-tripped fine. System.Text.Json validates that each parameter name and type match
/// a property exactly and throws <c>InvalidOperationException</c> ("Each parameter in the
/// deserialization constructor ... must bind to an object property or field") otherwise. Binding by
/// name here, deserializing each argument to the parameter type, restores the historical behaviour.
/// </item>
/// </list>
/// Without this converter those rows are readable only by the serializer that wrote them, which
/// makes the format-compatibility guarantee false for exactly the types most likely to rely on it.
/// <para>
/// A type that System.Text.Json can already construct without a constructor decision is never
/// touched: <see cref="CanConvert"/> declines it, and the default behaviour (including
/// source-generated contracts for other types) is left intact.
/// </para>
/// </remarks>
internal sealed class ParameterizedConstructorConverterFactory : JsonConverterFactory
{
    private static readonly ConcurrentDictionary<Type, ConstructorInfo?> Constructors = new();

    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => FindConstructor(typeToConvert) is not null;

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(ParameterizedConstructorConverter<>).MakeGenericType(typeToConvert);
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

            // A public parameterless constructor or System.Text.Json's own attribute mean
            // System.Text.Json can cope on its own.
            if (all.Any(c => c.IsPublic && c.GetParameters().Length == 0)
                || all.Any(c => c.GetCustomAttribute<JsonConstructorAttribute>() is not null))
            {
                return null;
            }

            var annotated = all
                .Where(c => c.GetCustomAttributes()
                    .Any(a => a.GetType().Name == nameof(JsonConstructorAttribute)))
                .ToArray();

            // Exactly one annotated constructor, or the intent is ambiguous and guessing would be
            // worse than the framework's own error.
            if (annotated.Length == 1 && annotated[0].GetParameters().Length > 0)
            {
                return annotated[0];
            }

            // A single public parameterized constructor: System.Text.Json would select it, but only
            // under its strict name+type parameter/property matching, which rejects the historical
            // Newtonsoft shapes this converter exists for (see the type summary). Take the
            // constructor over and bind by name instead.
            var publicParameterized = all.Where(c => c.IsPublic && c.GetParameters().Length > 0).ToArray();
            return publicParameterized.Length == 1 ? publicParameterized[0] : null;
        });
}

/// <summary>
/// Reads <typeparamref name="T"/> by matching JSON properties to the parameters of a constructor
/// System.Text.Json would not have selected, or would have bound too strictly. See
/// <see cref="ParameterizedConstructorConverterFactory"/> for why this exists.
/// </summary>
/// <typeparam name="T">The type being constructed.</typeparam>
internal sealed class ParameterizedConstructorConverter<T> : JsonConverter<T>
{
    private static readonly ConcurrentDictionary<JsonSerializerOptions, JsonSerializerOptions> Delegating = new();

    private readonly ConstructorInfo constructor;
    private readonly ParameterInfo[] parameters;

    /// <summary>Creates the converter for one constructor.</summary>
    /// <param name="constructor">The constructor to invoke.</param>
    public ParameterizedConstructorConverter(ConstructorInfo constructor)
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
            // Each argument deserializes to the PARAMETER's declared type, so a parameter typed
            // IEnumerable<T> accepts JSON that was written from an ImmutableList<T> property.
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
                if (copy.Converters[i] is ParameterizedConstructorConverterFactory)
                    copy.Converters.RemoveAt(i);
            }

            return copy;
        });
}
