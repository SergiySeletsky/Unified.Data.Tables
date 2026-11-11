namespace Unified.Data.Tables;

/// <summary>
/// Opts a property (or the properties of a class) into lossless storage of values of
/// any size: the value is kept inline when it fits one cell, otherwise split across cells and rows,
/// and finally offloaded to blob storage. Nothing is ever trimmed.
/// </summary>
/// <remarks>
/// On a class the attribute covers every <c>string</c>, <c>byte[]</c> and JSON-stored property of the
/// class, declared or inherited, except the entity id (<see cref="IEntity.Id"/>), which stays an
/// ordinary key column. A derived class inherits the attribute of its base class and may declare its
/// own. An attribute on a property wins over the one on its class, and a property that a derived class
/// overrides or hides (<c>new</c>) keeps the attribute of the property it re-declares.
/// </remarks>
/// <param name="encoding">How the value is encoded once it has to be split.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, Inherited = true)]
public sealed class TableValueSplitAttribute(SplitEncoding encoding) : Attribute
{
    /// <summary>How the value is encoded once it has to be split.</summary>
    public SplitEncoding Encoding { get; } = encoding;

    /// <summary>Whether the value may be offloaded to blob storage. Defaults to <see cref="BlobOffload.AboveThreshold"/>.</summary>
    public BlobOffload Offload { get; set; }
}
