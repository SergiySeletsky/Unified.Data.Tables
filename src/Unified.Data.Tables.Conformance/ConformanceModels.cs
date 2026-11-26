namespace Unified.Data.Tables.Conformance;

/// <summary>A value of <see cref="ConformanceItem.Color"/>: an enum is stored by name.</summary>
public enum ConformanceColor
{
    /// <summary>The default value.</summary>
    Red,

    /// <summary>A second value.</summary>
    Green,

    /// <summary>A third value.</summary>
    Blue,
}

/// <summary>
/// The plain entity the logical, failure and capability tiers store: one property of each kind a table
/// cell holds, and no large value.
/// </summary>
public class ConformanceItem : Entity
{
    /// <summary>A string cell.</summary>
    public string? Name { get; set; }

    /// <summary>A second string cell, for the scenarios that change one column and watch another.</summary>
    public string? Note { get; set; }

    /// <summary>An Int32 cell.</summary>
    public int Number { get; set; }

    /// <summary>An Int64 cell.</summary>
    public long Big { get; set; }

    /// <summary>A Double cell.</summary>
    public double Ratio { get; set; }

    /// <summary>A Boolean cell.</summary>
    public bool Flag { get; set; }

    /// <summary>A DateTime cell.</summary>
    public DateTimeOffset? When { get; set; }

    /// <summary>A Guid cell.</summary>
    public Guid Token { get; set; }

    /// <summary>An enum, stored by name.</summary>
    public ConformanceColor Color { get; set; }

    /// <summary>A Binary cell: stored as is, so one above 64 KiB is refused.</summary>
#pragma warning disable CA1819 // a byte[] property is the cell type under test
    public byte[]? Data { get; set; }
#pragma warning restore CA1819
}

/// <summary>
/// The entity the large-value tier stores: a text and a binary value that may be of any size
/// (<see cref="TableValueSplitAttribute"/>), and a plain column beside them.
/// </summary>
public class ConformanceLargeItem : Entity
{
    /// <summary>A text of any size: one cell, several cells, several rows, or a blob.</summary>
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    /// <summary>A plain column.</summary>
    public string? Title { get; set; }

    /// <summary>A binary value of any size.</summary>
    [TableValueSplit(SplitEncoding.Text)]
#pragma warning disable CA1819 // a byte[] property is the value type under test
    public byte[]? Payload { get; set; }
#pragma warning restore CA1819
}

/// <summary>
/// The base type of the polymorphic tier: not an <see cref="IEntity"/>, abstract, read back as the
/// derived type that was written.
/// </summary>
public abstract class ConformanceEvent
{
    /// <summary>A column every derived type has.</summary>
    public string? Subject { get; set; }

    /// <summary>A second shared column.</summary>
    public int Sequence { get; set; }
}

/// <summary>One derived type of <see cref="ConformanceEvent"/>.</summary>
public sealed class ConformanceOrderPlaced : ConformanceEvent
{
    /// <summary>A column only this type has.</summary>
    public string? Customer { get; set; }

    /// <summary>A second column only this type has.</summary>
    public int Quantity { get; set; }
}

/// <summary>Another derived type of <see cref="ConformanceEvent"/>.</summary>
public sealed class ConformanceOrderShipped : ConformanceEvent
{
    /// <summary>A column only this type has.</summary>
    public string? Carrier { get; set; }

    /// <summary>A second column only this type has.</summary>
    public DateTimeOffset ShippedAt { get; set; }
}
