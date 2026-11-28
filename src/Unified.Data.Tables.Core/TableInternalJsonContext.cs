using System.Text.Json.Serialization;

namespace Unified.Data.Tables;

// The serializer writes a string through the JSON contract on the oversized-string compression path.
// A consumer's context usually declares string as part of a collection graph, but it need not: this
// context makes the library's own use of string independent of the consumer's declarations.
[JsonSerializable(typeof(string))]
internal partial class TableInternalJsonContext : JsonSerializerContext
{
}
