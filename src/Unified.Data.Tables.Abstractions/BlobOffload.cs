namespace Unified.Data.Tables;

/// <summary>Whether a property marked <see cref="TableValueSplitAttribute"/> may be moved to blob storage.</summary>
public enum BlobOffload
{
    /// <summary>The value moves to blob storage when it is too large to keep in table rows (the default).</summary>
    AboveThreshold = 0,

    /// <summary>The value is never offloaded; a value too large for table rows throws <see cref="OversizedValueException"/>.</summary>
    Never = 1,
}
