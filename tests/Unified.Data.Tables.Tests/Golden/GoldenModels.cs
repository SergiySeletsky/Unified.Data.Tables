namespace Unified.Data.Tables.Tests.Golden;

// The entity types of the golden corpus. They belong to the corpus alone: the checked-in rows are what
// 0.9.2 stores for exactly these shapes, so a member added, renamed or retyped here changes a golden
// file. Add a new type for a new case instead.

public enum GoldenColour
{
    Red,
    Green = 5,
    Blue = 9,
}

[Flags]
public enum GoldenAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    Admin = 4,
}

/// <summary>Every scalar the serializer stores, one property per type.</summary>
public sealed class GoldenScalars : Entity
{
    public string Text { get; set; } = "";
    public string EmptyText { get; set; } = "";
    public bool Flag { get; set; }
    public int Int32 { get; set; }
    public long Int64 { get; set; }
    public double Double { get; set; }
    public decimal Decimal { get; set; }
    public Guid Guid { get; set; }
    public DateTime DateTime { get; set; }
    public DateTimeOffset DateTimeOffset { get; set; }
    public DateTimeOffset DateTimeOffsetWithOffset { get; set; }
    public TimeSpan TimeSpan { get; set; }
    public byte[] Bytes { get; set; } = [];
    public GoldenColour Colour { get; set; }
    public GoldenAccess Access { get; set; }
    public uint UInt32 { get; set; }
    public ulong UInt64 { get; set; }
    public byte Byte { get; set; }
    public short Int16 { get; set; }
    public float Single { get; set; }
    public char Char { get; set; }
}

/// <summary>The nullable form of every scalar; an unset one stores no cell.</summary>
public sealed class GoldenNullableScalars : Entity
{
    public string? Text { get; set; }
    public bool? Flag { get; set; }
    public int? Int32 { get; set; }
    public long? Int64 { get; set; }
    public double? Double { get; set; }
    public decimal? Decimal { get; set; }
    public Guid? Guid { get; set; }
    public DateTime? DateTime { get; set; }
    public DateTimeOffset? DateTimeOffset { get; set; }
    public TimeSpan? TimeSpan { get; set; }
    public byte[]? Bytes { get; set; }
    public GoldenColour? Colour { get; set; }
    public uint? UInt32 { get; set; }
    public ulong? UInt64 { get; set; }
}

/// <summary>Values at the edge of their type, and the ones the serializer stores as something else.</summary>
public sealed class GoldenScalarEdges : Entity
{
    public int Int32Min { get; set; }
    public int Int32Max { get; set; }
    public long Int64Min { get; set; }
    public long Int64Max { get; set; }
    public uint UInt32Max { get; set; }
    public ulong UInt64Max { get; set; }
    public double DoubleTenth { get; set; }
    public double DoubleWhole { get; set; }
    public double DoubleNegativeZero { get; set; }
    public double DoubleLarge { get; set; }
    public double DoubleEpsilon { get; set; }
    public double DoubleNaN { get; set; }
    public decimal DecimalMoney { get; set; }
    public decimal DecimalCents { get; set; }
    public decimal DecimalTenth { get; set; }
    public decimal DecimalWhole { get; set; }
    public decimal DecimalNegative { get; set; }
    public DateTime DateTimeDefault { get; set; }
    public DateTime DateTimeUnspecified { get; set; }
    public DateTimeOffset DateTimeOffsetDefault { get; set; }
    public Guid GuidEmpty { get; set; }
    public byte[] BytesEmpty { get; set; } = [];
    public TimeSpan TimeSpanNegative { get; set; }
    public TimeSpan TimeSpanDays { get; set; }
    public string TextWhitespace { get; set; } = "";
    public string TextControl { get; set; } = "";
}

public sealed class GoldenStreet
{
    public string Name { get; set; } = "";
    public int Number { get; set; }
}

public sealed class GoldenAddress
{
    public string City { get; set; } = "";
    public string Country { get; set; } = "";
    public int? Floor { get; set; }
    public GoldenColour Door { get; set; }
    public GoldenStreet Street { get; set; } = new();
    public List<string> Tags { get; set; } = [];
}

/// <summary>A positional record: stored as one JSON cell, not as columns.</summary>
public sealed record GoldenPoint(double Lat, double Lng);

/// <summary>Nested objects: flattened to <c>Parent_Child</c> columns, or one JSON cell.</summary>
public sealed class GoldenNested : Entity
{
    public string Title { get; set; } = "";
    public GoldenAddress Home { get; set; } = new();
    public GoldenAddress? Work { get; set; }
    public GoldenPoint Position { get; set; } = new(0, 0);
    public GoldenPoint? Previous { get; set; }
}

/// <summary>Collections: stored as one JSON cell each.</summary>
public sealed class GoldenCollections : Entity
{
    public List<string> Tags { get; set; } = [];
    public List<string> NoTags { get; set; } = [];
    public List<string>? NullTags { get; set; }
    public int[] Numbers { get; set; } = [];
    public Dictionary<string, int> Scores { get; set; } = [];
    public List<GoldenStreet> Streets { get; set; } = [];
    public Dictionary<string, GoldenPoint> Places { get; set; } = [];
    public HashSet<GoldenColour> Colours { get; set; } = [];
    public IDictionary<string, string>? Labels { get; set; }
}

/// <summary>Values too large for one plain cell on a type that opted into nothing: the JSON and GZip fallbacks.</summary>
public sealed class GoldenFallbacks : Entity
{
    public List<string> SmallList { get; set; } = [];
    public List<string> LargeList { get; set; } = [];
    public string ShortText { get; set; } = "";
    public string LongText { get; set; } = "";
}

/// <summary>Renamed members: a write uses the current names, a read also takes the old ones.</summary>
[ColumnAlias(nameof(Label), "Caption")]
public sealed class GoldenAliased : Entity
{
    [ColumnAlias("OldName")]
    public string NewName { get; set; } = "";

    [ColumnAlias("OldTags")]
    public List<string> Tags { get; set; } = [];

    public string Label { get; set; } = "";
}

/// <summary>An entity that implements <see cref="IEntity"/> without deriving <see cref="Entity"/>.</summary>
public sealed class GoldenInterfaceEntity : IEntity
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string Name { get; set; } = "";
    public string Computed => Name + "!";
}

/// <summary>Large values that opted into being stored whole: split over cells, and over continuation rows.</summary>
public sealed class GoldenSplit : Entity
{
    public string Title { get; set; } = "";

    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    [TableValueSplit(SplitEncoding.Text)]
    public byte[]? Image { get; set; }

    [TableValueSplit(SplitEncoding.Text)]
    public List<string>? Items { get; set; }

    [TableValueSplit(SplitEncoding.Gzip)]
    public string? Zipped { get; set; }
}

/// <summary>The base of a polymorphic table: not an <see cref="IEntity"/>.</summary>
public abstract class GoldenMessage
{
    public string Id { get; set; } = "";
    public DateTimeOffset Created { get; set; }
}

public sealed class GoldenOrderPlaced : GoldenMessage
{
    public string Customer { get; set; } = "";
    public decimal Total { get; set; }
    public List<string> Lines { get; set; } = [];
    public GoldenAddress ShipTo { get; set; } = new();
}

public sealed class GoldenOrderCancelled : GoldenMessage
{
    public string Reason { get; set; } = "";
    public GoldenColour Severity { get; set; }
}

/// <summary>A model for the filters of the corpus: one member per leaf type a filter can compare.</summary>
public sealed class GoldenFilterEntity : Entity
{
    public string Name { get; set; } = "";
    public string? Note { get; set; }
    public bool Active { get; set; }
    public bool? Verified { get; set; }
    public int Count { get; set; }
    public int? Rank { get; set; }
    public uint Small { get; set; }
    public long Big { get; set; }
    public ulong Huge { get; set; }
    public double Ratio { get; set; }
    public decimal Price { get; set; }
    public Guid Token { get; set; }
    public DateTime When { get; set; }
    public DateTimeOffset Seen { get; set; }
    public GoldenColour Colour { get; set; }
    public GoldenAddress Home { get; set; } = new();
}
