using System.Text.Json;
using System.Text.Json.Nodes;

namespace Unified.Data.Tables.Tests.LargeValues;

// Plain entity types (no [TableValueSplit], no TableBlobContent anywhere) of the shapes the published
// 0.8.3 stored. PlainTypeRegressionTests pins their cells to what 0.8.3 writes.

/// <summary>A JSON DOM node: stored as one <c>Payload__Json</c> cell.</summary>
public class PlainJsonNodeDoc : Entity
{
    public JsonNode? Payload { get; set; }

    public string? Title { get; set; }
}

public class PlainJsonObjectDoc : Entity
{
    public JsonObject? Payload { get; set; }
}

public class PlainJsonArrayDoc : Entity
{
    public JsonArray? Payload { get; set; }
}

public class PlainJsonValueDoc : Entity
{
    public JsonValue? Payload { get; set; }
}

public class PlainJsonElementDoc : Entity
{
    public JsonElement Payload { get; set; }
}

public class PlainNullableJsonElementDoc : Entity
{
    public JsonElement? Payload { get; set; }
}

/// <summary>A nested object that flattens into <c>Inner_*</c> columns and holds a JSON DOM node.</summary>
public class PlainJsonHolder
{
    public string? Name { get; set; }

    public JsonNode? Data { get; set; }
}

public class PlainNestedJsonDoc : Entity
{
    public PlainJsonHolder? Inner { get; set; }
}

public class PlainJsonDictionaryDoc : Entity
{
    public Dictionary<string, JsonNode>? Map { get; set; }
}

public class PlainJsonListDoc : Entity
{
    public List<JsonNode>? Items { get; set; }
}

/// <summary>A flattened object with a get-only indexer (never a column).</summary>
public class PlainReadOnlyIndexed
{
    public string? Label { get; set; }

    public char this[int index] => (Label ?? string.Empty)[index];
}

public class PlainReadOnlyIndexerDoc : Entity
{
    public PlainReadOnlyIndexed? Bag { get; set; }
}

/// <summary>A flattened object with a read/write indexer.</summary>
public class PlainIndexed
{
    private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

    public string? Label { get; set; }

    public string? this[string key]
    {
        get => values.GetValueOrDefault(key);
        set => values[key] = value;
    }
}

public class PlainIndexerDoc : Entity
{
    public PlainIndexed? Bag { get; set; }
}

/// <summary>A flattened object with two read/write indexers (two properties named <c>Item</c>).</summary>
public class PlainTwoIndexed
{
    private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

    public string? Label { get; set; }

    public string? this[string key]
    {
        get => values.GetValueOrDefault(key);
        set => values[key] = value;
    }

    public string? this[int index]
    {
        get => values.GetValueOrDefault(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        set => values[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = value;
    }
}

public class PlainTwoIndexerDoc : Entity
{
    public PlainTwoIndexed? Bag { get; set; }
}

/// <summary>An interface that declares two indexers, as <see cref="JsonNode"/> does.</summary>
public interface IPlainLookup
{
    string? Label { get; set; }

    string? this[string key] { get; set; }

    string? this[int index] { get; set; }
}

/// <summary>Implements the indexers explicitly, so the class itself flattens into its <c>Label</c> column.</summary>
public class PlainLookup : IPlainLookup
{
    public string? Label { get; set; }

    string? IPlainLookup.this[string key]
    {
        get => Label;
        set => Label = value;
    }

    string? IPlainLookup.this[int index]
    {
        get => Label;
        set => Label = value;
    }
}

public class PlainLookupDoc : Entity
{
    public IPlainLookup? Lookup { get; set; } = new PlainLookup();
}

public class PlainHiddenBase
{
    public string? Tag { get; set; }

    public int Rank { get; set; }
}

/// <summary>Hides a base property with one of the same type: reflection reports one <c>Tag</c>.</summary>
public class PlainHiddenSameType : PlainHiddenBase
{
    public new string? Tag { get; set; }
}

/// <summary>Hides a base property with one of another type: reflection reports two properties named <c>Tag</c>.</summary>
public class PlainHiddenOtherType : PlainHiddenBase
{
    public new int Tag { get; set; }
}

public class PlainHiddenSameTypeDoc : Entity
{
    public PlainHiddenSameType? Thing { get; set; }
}

public class PlainHiddenOtherTypeDoc : Entity
{
    public PlainHiddenOtherType? Thing { get; set; }
}

public class PlainHidingEntityBase : Entity
{
    public string? Rank { get; set; }
}

/// <summary>An entity type that itself hides a base property with one of another type.</summary>
public class PlainHidingEntity : PlainHidingEntityBase
{
    public new int Rank { get; set; }
}

/// <summary>A nested type that refers to itself.</summary>
public class PlainTreeNode
{
    public string? Name { get; set; }

    public PlainTreeNode? Next { get; set; }
}

public class PlainTreeDoc : Entity
{
    public PlainTreeNode? Root { get; set; }
}

/// <summary>An entity type that refers to itself.</summary>
public class PlainLinkedDoc : Entity
{
    public string? Name { get; set; }

    public PlainLinkedDoc? Next { get; set; }
}

public class PlainObjectDoc : Entity
{
    public object? Value { get; set; }
}

public class PlainTypeDoc : Entity
{
    public Type? Kind { get; set; }

    public string? Title { get; set; }
}

public class PlainDelegateDoc : Entity
{
    public Func<int>? Compute { get; set; }

    public string? Title { get; set; }
}

/// <summary>The base of a polymorphic table's items.</summary>
public abstract class PlainEventBase
{
    public string? Name { get; set; }
}

public class PlainJsonEvent : PlainEventBase
{
    public JsonNode? Payload { get; set; }
}
