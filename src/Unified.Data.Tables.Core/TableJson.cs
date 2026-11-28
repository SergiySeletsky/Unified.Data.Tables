using System.Text.Json.Serialization;

namespace Unified.Data.Tables;

/// <summary>
/// JSON configuration for the values <see cref="TableEntitySerializer"/> stores in a JSON or GZip
/// cell (collections, records with a parameterized constructor, and JSON-stored
/// <see cref="TableValueSplitAttribute"/> values).
/// </summary>
/// <remarks>
/// A JIT application needs no configuration: the serializer uses its reflection-based contract,
/// exactly as before. A NativeAOT or trimmed application must call <see cref="UseContext"/> before
/// the first storage operation with a source-generated <see cref="JsonSerializerContext"/> that
/// declares every type the store keeps as JSON, for example:
/// <code>
/// [JsonSourceGenerationOptions(
///     PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
///     PropertyNameCaseInsensitive = true,
///     UseStringEnumConverter = true,
///     DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
/// [JsonSerializable(typeof(List&lt;Address&gt;))]
/// [JsonSerializable(typeof(MyRecord))]
/// partial class TablesJsonContext : JsonSerializerContext;
///
/// TableJson.UseContext(TablesJsonContext.Default);
/// </code>
/// The context must use the serializer's naming, enum and null settings so rows are byte-compatible
/// with the reflection contract; the AOT smoke test compares the two.
/// </remarks>
public static class TableJson
{
    /// <summary>
    /// Uses <paramref name="context"/> for every value the serializer stores as JSON. Call it once,
    /// at startup, before the first storage operation.
    /// </summary>
    /// <param name="context">The source-generated JSON contract.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The serializer already used its JSON contract.</exception>
    public static void UseContext(JsonSerializerContext context) =>
        TableEntitySerializer.UseJsonContext(context);
}
