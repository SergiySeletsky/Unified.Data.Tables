using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Unified.Data.Tables;

/// <summary>The value types a table cell can hold: the eight property types of Azure Table storage.</summary>
[Experimental(RawTableDiagnostics.Id)]
public enum RawCellType
{
    /// <summary>A UTF-16 string (<c>Edm.String</c>).</summary>
    String,

    /// <summary>A byte array (<c>Edm.Binary</c>).</summary>
    Binary,

    /// <summary>A boolean (<c>Edm.Boolean</c>).</summary>
    Boolean,

    /// <summary>An instant in UTC (<c>Edm.DateTime</c>).</summary>
    DateTime,

    /// <summary>A 64-bit floating point number (<c>Edm.Double</c>).</summary>
    Double,

    /// <summary>A GUID (<c>Edm.Guid</c>).</summary>
    Guid,

    /// <summary>A 32-bit integer (<c>Edm.Int32</c>).</summary>
    Int32,

    /// <summary>A 64-bit integer (<c>Edm.Int64</c>).</summary>
    Int64,
}

/// <summary>
/// One cell of a physical row: a value and the type it is stored as. Provider-neutral: no backend type
/// appears in it. Immutable, except that a binary cell holds the caller's array without copying it.
/// </summary>
/// <remarks>
/// The type is part of the value: the integer 1 as <see cref="RawCellType.Int32"/> and as
/// <see cref="RawCellType.Int64"/> are different cells, and a copy that changed one into the other would
/// change what a typed store reads back. A cell never holds null; an absent column is absent from
/// <see cref="RawRow.Cells"/>.
/// </remarks>
[Experimental(RawTableDiagnostics.Id)]
public sealed class RawCell : IEquatable<RawCell>
{
    private RawCell(RawCellType type, object value)
    {
        Type = type;
        Value = value;
    }

    /// <summary>The type the value is stored as.</summary>
    public RawCellType Type { get; }

    /// <summary>
    /// The value, boxed: a <see cref="string"/>, <c>byte[]</c>, <see cref="bool"/>,
    /// <see cref="DateTimeOffset"/>, <see cref="double"/>, <see cref="System.Guid"/>, <see cref="int"/> or
    /// <see cref="long"/>, as <see cref="Type"/> says. Never null.
    /// </summary>
    public object Value { get; }

    /// <summary>A string cell.</summary>
    /// <param name="value">The text; not null.</param>
    /// <returns>The cell.</returns>
    public static RawCell FromString(string value)
    {
        Guard.NotNull(value, nameof(value));
        return new RawCell(RawCellType.String, value);
    }

    /// <summary>A binary cell. The array is not copied: do not change it afterwards.</summary>
    /// <param name="value">The bytes; not null.</param>
    /// <returns>The cell.</returns>
    public static RawCell FromBinary(byte[] value)
    {
        Guard.NotNull(value, nameof(value));
        return new RawCell(RawCellType.Binary, value);
    }

    /// <summary>A boolean cell.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The cell.</returns>
    public static RawCell FromBoolean(bool value) => new(RawCellType.Boolean, value);

    /// <summary>A date-time cell. The instant is stored in UTC; the offset is not kept.</summary>
    /// <param name="value">The instant.</param>
    /// <returns>The cell.</returns>
    public static RawCell FromDateTime(DateTimeOffset value) => new(RawCellType.DateTime, value.ToUniversalTime());

    /// <summary>A floating point cell.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The cell.</returns>
    public static RawCell FromDouble(double value) => new(RawCellType.Double, value);

    /// <summary>A GUID cell.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The cell.</returns>
    public static RawCell FromGuid(Guid value) => new(RawCellType.Guid, value);

    /// <summary>A 32-bit integer cell.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The cell.</returns>
    public static RawCell FromInt32(int value) => new(RawCellType.Int32, value);

    /// <summary>A 64-bit integer cell.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The cell.</returns>
    public static RawCell FromInt64(long value) => new(RawCellType.Int64, value);

    /// <summary>The text of a <see cref="RawCellType.String"/> cell.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The cell is of another type.</exception>
    public string AsString() => As<string>(RawCellType.String);

    /// <summary>The bytes of a <see cref="RawCellType.Binary"/> cell (the cell's own array, not a copy).</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The cell is of another type.</exception>
    public byte[] AsBinary() => As<byte[]>(RawCellType.Binary);

    /// <summary>The value of a <see cref="RawCellType.Boolean"/> cell.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The cell is of another type.</exception>
    public bool AsBoolean() => As<bool>(RawCellType.Boolean);

    /// <summary>The instant of a <see cref="RawCellType.DateTime"/> cell, in UTC.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The cell is of another type.</exception>
    public DateTimeOffset AsDateTime() => As<DateTimeOffset>(RawCellType.DateTime);

    /// <summary>The value of a <see cref="RawCellType.Double"/> cell.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The cell is of another type.</exception>
    public double AsDouble() => As<double>(RawCellType.Double);

    /// <summary>The value of a <see cref="RawCellType.Guid"/> cell.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The cell is of another type.</exception>
    public Guid AsGuid() => As<Guid>(RawCellType.Guid);

    /// <summary>The value of a <see cref="RawCellType.Int32"/> cell.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The cell is of another type.</exception>
    public int AsInt32() => As<int>(RawCellType.Int32);

    /// <summary>The value of a <see cref="RawCellType.Int64"/> cell. An <see cref="RawCellType.Int32"/> cell is not widened.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The cell is of another type.</exception>
    public long AsInt64() => As<long>(RawCellType.Int64);

    private TValue As<TValue>(RawCellType expected) =>
        Type == expected
            ? (TValue)Value
            : throw new InvalidOperationException($"The cell holds {Type}, not {expected}.");

    /// <summary>Whether <paramref name="other"/> is the same type and value; binary cells compare byte for byte.</summary>
    /// <param name="other">The cell to compare with.</param>
    /// <returns>True when both cells would be stored identically.</returns>
    public bool Equals(RawCell? other)
    {
        if (other is null || other.Type != Type)
            return false;
        return Type == RawCellType.Binary
            ? ((byte[])Value).AsSpanEquals((byte[])other.Value)
            : Value.Equals(other.Value);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RawCell);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        if (Type != RawCellType.Binary)
            return ((int)Type * 397) ^ Value.GetHashCode();

        // Length and the first bytes: enough to spread, and cheap for a megabyte cell.
        var bytes = (byte[])Value;
        var hash = bytes.Length;
        for (var i = 0; i < bytes.Length && i < 16; i++)
            hash = unchecked((hash * 31) + bytes[i]);
        return hash;
    }

    /// <summary>The type and value for diagnostics; a binary cell prints its length only.</summary>
    /// <returns>For example <c>Int32:42</c>.</returns>
    public override string ToString() =>
        Type == RawCellType.Binary
            ? string.Format(CultureInfo.InvariantCulture, "Binary:{0} bytes", ((byte[])Value).Length)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1}", Type, Value);
}

/// <summary>
/// One physical row of a table: its two keys, its cells, and the version and write time the store
/// reported. Physical means as stored: a continuation row of a split value, a marker row and a row another
/// layer wrote are rows like any other, and nothing is assembled, renamed or hidden.
/// </summary>
[Experimental(RawTableDiagnostics.Id)]
public sealed class RawRow
{
    private static readonly IReadOnlyDictionary<string, RawCell> NoCells = new Dictionary<string, RawCell>();

    /// <summary>Creates a row.</summary>
    /// <param name="partitionKey">The partition key, exactly as stored (no id normalization is applied).</param>
    /// <param name="rowKey">The row key, exactly as stored.</param>
    /// <param name="cells">
    /// The row's columns by name, without the keys, the timestamp and the ETag; null for none. The
    /// dictionary is not copied.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="partitionKey"/> or <paramref name="rowKey"/> is null.</exception>
    public RawRow(string partitionKey, string rowKey, IReadOnlyDictionary<string, RawCell>? cells = null)
    {
        Guard.NotNull(partitionKey, nameof(partitionKey));
        Guard.NotNull(rowKey, nameof(rowKey));
        PartitionKey = partitionKey;
        RowKey = rowKey;
        Cells = cells ?? NoCells;
    }

    /// <summary>The partition key, exactly as stored.</summary>
    public string PartitionKey { get; }

    /// <summary>The row key, exactly as stored.</summary>
    public string RowKey { get; }

    /// <summary>The row's columns by name (ordinal, case-sensitive), without the keys, the timestamp and the ETag.</summary>
    public IReadOnlyDictionary<string, RawCell> Cells { get; }

    /// <summary>
    /// The row's version as the store reported it on a read; null on a row that was not read. Ignored on
    /// a write: a condition is given on the <see cref="RawOperation"/>.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>The store's last-write time as reported on a read; null on a row that was not read. Ignored on a write.</summary>
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>A row-key range in one partition, for <see cref="IRawTable.QueryAsync"/>.</summary>
/// <remarks>
/// Keys are compared as stored, in the store's key order (ordinal UTF-16 on the local providers; the
/// same for the characters of the Basic Multilingual Plane on Azure Tables, unverified beyond it). No id
/// normalization is applied: this is the physical view.
/// </remarks>
[Experimental(RawTableDiagnostics.Id)]
public sealed record RawQuery
{
    /// <summary>Creates a query over one partition.</summary>
    /// <param name="partitionKey">The partition to read, exactly as stored.</param>
    /// <exception cref="ArgumentNullException"><paramref name="partitionKey"/> is null.</exception>
    public RawQuery(string partitionKey)
    {
        Guard.NotNull(partitionKey, nameof(partitionKey));
        PartitionKey = partitionKey;
    }

    /// <summary>The partition to read, exactly as stored.</summary>
    public string PartitionKey { get; }

    /// <summary>The lower row-key bound, or null to start at the first row of the partition.</summary>
    public string? FromRowKey { get; init; }

    /// <summary>Whether a row whose key equals <see cref="FromRowKey"/> is returned. Default true.</summary>
    public bool FromInclusive { get; init; } = true;

    /// <summary>The upper row-key bound, or null to read to the end of the partition.</summary>
    public string? ToRowKey { get; init; }

    /// <summary>Whether a row whose key equals <see cref="ToRowKey"/> is returned. Default false.</summary>
    public bool ToInclusive { get; init; }

    /// <summary>Stop after this many rows; null for all of them. Must be positive when set.</summary>
    public int? Take { get; init; }
}

/// <summary>What a <see cref="RawOperation"/> does to its row.</summary>
[Experimental(RawTableDiagnostics.Id)]
public enum RawOperationKind
{
    /// <summary>Store a new row; fails when a row has the key.</summary>
    Insert,

    /// <summary>Replace every cell of an existing row; fails when no row has the key.</summary>
    Replace,

    /// <summary>Set the given cells of an existing row and keep its other cells; fails when no row has the key.</summary>
    Merge,

    /// <summary>Remove an existing row; fails when no row has the key.</summary>
    Delete,
}

/// <summary>One write of an <see cref="IRawTable.SubmitAsync"/> transaction.</summary>
[Experimental(RawTableDiagnostics.Id)]
public sealed class RawOperation
{
    private RawOperation(RawOperationKind kind, RawRow row, string? ifMatch)
    {
        Kind = kind;
        Row = row;
        IfMatch = ifMatch;
    }

    /// <summary>What the operation does.</summary>
    public RawOperationKind Kind { get; }

    /// <summary>The row to write; for a <see cref="RawOperationKind.Delete"/> only its keys are used.</summary>
    public RawRow Row { get; }

    /// <summary>
    /// The version the stored row must have for the operation to apply, or null for no condition. Never
    /// set on an <see cref="RawOperationKind.Insert"/>.
    /// </summary>
    public string? IfMatch { get; }

    /// <summary>Store a new row.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The operation.</returns>
    public static RawOperation Insert(RawRow row) => Create(RawOperationKind.Insert, row, null);

    /// <summary>Replace every cell of an existing row.</summary>
    /// <param name="row">The row as it should be afterwards.</param>
    /// <param name="ifMatch">The version the stored row must have, or null to replace whatever the version.</param>
    /// <returns>The operation.</returns>
    /// <exception cref="ArgumentException"><paramref name="ifMatch"/> is empty or <c>"*"</c> (pass null for no condition).</exception>
    public static RawOperation Replace(RawRow row, string? ifMatch = null) => Create(RawOperationKind.Replace, row, ifMatch);

    /// <summary>Set the given cells of an existing row and keep its other cells.</summary>
    /// <param name="row">The keys and the cells to set.</param>
    /// <param name="ifMatch">The version the stored row must have, or null to merge whatever the version.</param>
    /// <returns>The operation.</returns>
    /// <exception cref="ArgumentException"><paramref name="ifMatch"/> is empty or <c>"*"</c> (pass null for no condition).</exception>
    public static RawOperation Merge(RawRow row, string? ifMatch = null) => Create(RawOperationKind.Merge, row, ifMatch);

    /// <summary>Remove an existing row.</summary>
    /// <param name="partitionKey">The partition key, exactly as stored.</param>
    /// <param name="rowKey">The row key, exactly as stored.</param>
    /// <param name="ifMatch">The version the stored row must have, or null to delete whatever the version.</param>
    /// <returns>The operation.</returns>
    /// <exception cref="ArgumentException"><paramref name="ifMatch"/> is empty or <c>"*"</c> (pass null for no condition).</exception>
    public static RawOperation Delete(string partitionKey, string rowKey, string? ifMatch = null) =>
        Create(RawOperationKind.Delete, new RawRow(partitionKey, rowKey), ifMatch);

    private static RawOperation Create(RawOperationKind kind, RawRow row, string? ifMatch)
    {
        Guard.NotNull(row, nameof(row));
        // One spelling of "no condition" (null), so the two providers cannot read "*" or "" differently.
        if (ifMatch is not null && (ifMatch.Length == 0 || ifMatch == "*"))
            throw new ArgumentException(
                "An ETag condition names one version; pass null for an unconditional operation.", nameof(ifMatch));
        return new RawOperation(kind, row, ifMatch);
    }
}

/// <summary>The diagnostic the experimental raw-table API is reported under.</summary>
internal static class RawTableDiagnostics
{
    /// <summary>Suppress this id to use <see cref="ITableStore"/> and <see cref="IRawTable"/>.</summary>
    public const string Id = "UDT0001";
}

internal static class ByteArrayComparison
{
    public static bool AsSpanEquals(this byte[] left, byte[] right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }
        return true;
    }
}
