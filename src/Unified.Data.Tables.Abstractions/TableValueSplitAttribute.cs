namespace Unified.Data.Tables;

/// <summary>
/// Opts a property (or the properties of a class) into lossless storage of values of
/// any size: the value is kept inline when it fits one cell and the entity fits one row, otherwise split
/// across cells and rows, and finally offloaded to blob storage. Nothing is ever trimmed.
/// </summary>
/// <remarks>
/// On a class the attribute covers every <c>string</c>, <c>byte[]</c> and JSON-stored property of the
/// class, declared or inherited, except the entity id (<see cref="IEntity.Id"/>), which stays an
/// ordinary key column. A derived class inherits the attribute of its base class and may declare its
/// own. An attribute on a property wins over the one on its class, and a property that a derived class
/// overrides or hides (<c>new</c>) keeps the attribute of the property it re-declares.
/// <para>
/// On a property declared on an interface the attribute is a misuse: .NET does not pass the attributes of
/// an interface member to the class that implements it, so it cannot be honoured. It makes no type
/// large-value aware and lists no large-value column. Every write or read that flattens a class
/// implementing the interface into columns (the entity, or a property that flattens into columns) throws
/// <see cref="System.InvalidOperationException"/> before anything is stored (a value of such a class is
/// never trimmed), naming the interface member and the remedy: put the attribute on the property of the
/// implementing class, or on that class (and, where an entity holds the implementation in a property
/// declared as the interface, on the entity class too). A builder partial update is refused with the same
/// exception before anything is written when the entity type, or the declared type of a property along
/// the updated path, is or implements such an interface. An instance held in a JSON-stored property (a
/// collection, or a type stored as one JSON cell) is not checked: it is written as JSON like every other
/// value of that cell, and a trimming <c>OversizedCellPolicy</c> can trim that cell.
/// </para>
/// <para>
/// A store treats its entity type as large-value aware (it plans split cells, continuation rows and blobs)
/// only when the declared types say so, read by reflection alone: the attribute on a property of the entity
/// or of a type the entity flattens into columns, a <see cref="TableBlobContent"/> property there, or the
/// attribute on the entity class itself. The last also applies when it covers none of the entity's own
/// properties: it is how an entity opts in whose large values are members of an object whose type is known
/// only at run time (an interface-, abstract- or base-class-typed property that holds a subtype with
/// <c>[TableValueSplit]</c> or <see cref="TableBlobContent"/> members); it still covers the entity's own
/// string, <c>byte[]</c> and JSON-stored properties as above. The store of such an entity
/// constructs it once, when it first needs the columns of the objects its constructor puts in those
/// properties; it never constructs a type that is not large-value aware. A type that is not large-value
/// aware refuses to store a value in such a member below a runtime subtype, whatever its size, with
/// <see cref="System.NotSupportedException"/>.
/// </para>
/// <para>
/// A value too large for one cell is stored only where a read returns it. Below an object a read cannot
/// create (a struct held in a nullable property, an interface- or abstract-typed property with no instance)
/// the write is refused with <see cref="System.NotSupportedException"/> naming the column, before anything
/// is stored; a value that fits one cell there stays an ordinary cell. A split value keeps four cells on
/// the entity's main row besides its chunks, and a row with split values its part count, within the 252
/// properties one entity holds besides its keys and timestamp: an entity with no other properties than
/// those of <see cref="Entity"/> holds 62 split values, a wider one fewer. Beyond that the write throws
/// <see cref="OversizedValueException"/>, which names the value and the properties its cells need.
/// </para>
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
