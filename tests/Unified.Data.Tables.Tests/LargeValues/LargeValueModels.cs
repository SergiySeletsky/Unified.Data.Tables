namespace Unified.Data.Tables.Tests.LargeValues;

public class SplitTextEntity : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    public string? Title { get; set; }
}

public class SplitGzipEntity : Entity
{
    [TableValueSplit(SplitEncoding.Gzip)]
    public string? Body { get; set; }
}

public class SplitBinaryEntity : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public byte[]? Image { get; set; }
}

public class SplitJsonEntity : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public List<string>? Items { get; set; }
}

public class SplitInner
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    public int Count { get; set; }
}

public class SplitNestedEntity : Entity
{
    public SplitInner? Inner { get; set; }
}

public class NeverOffloadEntity : Entity
{
    [TableValueSplit(SplitEncoding.Text, Offload = BlobOffload.Never)]
    public string? Body { get; set; }
}

public class PlainEntity : Entity
{
    public string? Body { get; set; }
}

/// <summary>A class-level opt-in: every string, byte[] and JSON-stored property is split-aware.</summary>
[TableValueSplit(SplitEncoding.Text)]
public class ClassLevelSplitEntity : Entity
{
    public string? Body { get; set; }

    public int Number { get; set; }

    public List<string>? Items { get; set; }
}

/// <summary>Misuse: a native scalar has nothing to split.</summary>
public class SplitOnScalarEntity : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public int Number { get; set; }
}
