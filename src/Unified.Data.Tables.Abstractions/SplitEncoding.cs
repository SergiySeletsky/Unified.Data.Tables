namespace Unified.Data.Tables;

/// <summary>How a property marked <see cref="TableValueSplitAttribute"/> is encoded when it does not fit one cell.</summary>
public enum SplitEncoding
{
    /// <summary>Readable string cells of at most 32,000 characters; a surrogate pair is never split.</summary>
    Text,

    /// <summary>The UTF-8 bytes of the string, gzip-compressed, stored in binary cells.</summary>
    Gzip,
}
