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

public class SplitGzipJsonEntity : Entity
{
    [TableValueSplit(SplitEncoding.Gzip)]
    public List<string>? Items { get; set; }
}

public class BlobEntity : Entity
{
    public TableBlobContent? Content { get; set; }

    public string? Title { get; set; }
}

public class NestedBlobHolder
{
    public TableBlobContent? Content { get; set; }
}

public class NestedBlobEntity : Entity
{
    public NestedBlobHolder? Inner { get; set; }
}

public class BlobListEntity : Entity
{
    public List<TableBlobContent>? Contents { get; set; }
}

public class TextAndBlobEntity : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    public TableBlobContent? Content { get; set; }
}

public class FileEntity : Entity
{
    public TableBlobContent? File { get; set; }

    public string? Title { get; set; }
}

public class TwoBlobEntity : Entity
{
    public TableBlobContent? First { get; set; }

    public TableBlobContent? Second { get; set; }
}

/// <summary>A blob content beside a split value that may never be offloaded (lets a write fail in Serialize).</summary>
public class NeverOffloadWithFileEntity : Entity
{
    [TableValueSplit(SplitEncoding.Text, Offload = BlobOffload.Never)]
    public string? Body { get; set; }

    public TableBlobContent? File { get; set; }
}

/// <summary>A shape decided only at runtime (flattened or JSON): the attribute cannot be honoured.</summary>
public interface ISplitShape
{
    string? Name { get; set; }
}

public abstract class SplitShapeBase
{
    public string? Name { get; set; }
}

/// <summary>Misuse: [TableValueSplit] on an interface-typed member that may flatten into columns.</summary>
public class SplitOnInterfaceEntity : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public ISplitShape? Shape { get; set; }
}

/// <summary>Misuse: [TableValueSplit] on an abstract-typed member that may flatten into columns.</summary>
public class SplitOnAbstractEntity : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public SplitShapeBase? Shape { get; set; }
}

/// <summary>An interface collection is always one JSON cell, so it can be split.</summary>
public class SplitOnInterfaceListEntity : Entity
{
    [TableValueSplit(SplitEncoding.Text)]
    public IList<string>? Items { get; set; }
}
