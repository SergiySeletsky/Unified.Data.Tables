using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Azure.Data.Tables;
using Unified.Data.Tables.Providers;

namespace Unified.Data.Tables.Sqlite;

/// <summary>
/// How a row is laid out in the database: its keys as the bytes SQLite orders the way the engine orders
/// keys, its cells as one self-describing blob that reads back the very values (and CLR types) the
/// serializer wrote. What is written here stays in files: a change of either layout is a new
/// <see cref="SqliteDatabase.SchemaVersion"/> with a migration, never an edit of this one.
/// </summary>
internal static class SqliteRowCodec
{
    /// <summary>The first byte of every cells blob: the layout below.</summary>
    internal const byte CellsFormat = 1;

    // The type tag of a cell. The numbers are stored: never renumber, only append.
    private const byte Null = 0;
    private const byte Text = 1;
    private const byte Bytes = 2;
    private const byte Binary = 3;
    private const byte Boolean = 4;
    private const byte DateAndOffset = 5;
    private const byte Date = 6;
    private const byte Double = 7;
    private const byte GuidValue = 8;
    private const byte Int32 = 9;
    private const byte Int64 = 10;

    // How a string is stored: UTF-8 when it is well-formed UTF-16 (nearly always), its UTF-16 code units
    // as they are otherwise, since UTF-8 has no encoding of an unpaired surrogate and would store U+FFFD.
    private const byte Utf8 = 0;
    private const byte Utf16 = 1;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// A key as SQLite stores and compares it: its UTF-16 code units, big-endian. SQLite orders blobs by
    /// their bytes (a shorter blob before a longer one it is the start of), which for these bytes is
    /// <see cref="string.CompareOrdinal(string, string)"/>, the order of the in-memory row store. (TEXT
    /// would be ordered by its UTF-8 bytes, which differs for supplementary characters, and cannot hold
    /// an unpaired surrogate.)
    /// </summary>
    internal static byte[] Key(string key)
    {
        var bytes = new byte[key.Length * 2];
        for (var i = 0; i < key.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2, 2), key[i]);
        return bytes;
    }

    /// <summary>The key a stored blob holds (see <see cref="Key(string)"/>).</summary>
    internal static string Key(ReadOnlySpan<byte> stored)
    {
        if ((stored.Length & 1) != 0)
            throw new FormatException("A stored key has an odd number of bytes; it is not UTF-16.");
        return string.Create(stored.Length / 2, stored, static (chars, bytes) =>
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i * 2, 2));
        });
    }

    /// <summary>The ETag of a row version: the weak-validator form the in-memory row store uses.</summary>
    internal static string ETag(long version) => $"W/\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// The cells of <paramref name="row"/> (everything but its two keys, which are columns of their own),
    /// in the order the row holds them: the format byte, the cell count, then per cell its name, its
    /// type tag and its value.
    /// </summary>
    /// <exception cref="NotSupportedException">A cell holds a value that is not one of the table cell types.</exception>
    internal static byte[] Cells(TableEntity row)
    {
        using var buffer = new MemoryStream(256);
        using var writer = new BinaryWriter(buffer);
        writer.Write(CellsFormat);
        var count = 0;
        foreach (var cell in row)
        {
            if (!IsKey(cell.Key))
                count++;
        }
        writer.Write7BitEncodedInt(count);
        foreach (var cell in row)
        {
            if (IsKey(cell.Key))
                continue;
            WriteString(writer, cell.Key);
            switch (cell.Value)
            {
                case null:
                    writer.Write(Null);
                    break;
                case string value:
                    writer.Write(Text);
                    WriteString(writer, value);
                    break;
                case byte[] value:
                    writer.Write(Bytes);
                    writer.Write7BitEncodedInt(value.Length);
                    writer.Write(value);
                    break;
                case BinaryData value:
                    writer.Write(Binary);
                    var memory = value.ToMemory();
                    writer.Write7BitEncodedInt(memory.Length);
                    writer.Write(memory.Span);
                    break;
                case bool value:
                    writer.Write(Boolean);
                    writer.Write(value);
                    break;
                case DateTimeOffset value:
                    writer.Write(DateAndOffset);
                    writer.Write(value.Ticks);
                    writer.Write((short)value.Offset.TotalMinutes);
                    break;
                case DateTime value:
                    writer.Write(Date);
                    writer.Write(value.ToBinary());
                    break;
                case double value:
                    writer.Write(Double);
                    writer.Write(BitConverter.DoubleToInt64Bits(value));
                    break;
                case Guid value:
                    writer.Write(GuidValue);
                    writer.Write(value.ToByteArray(bigEndian: true));
                    break;
                case int value:
                    writer.Write(Int32);
                    writer.Write(value);
                    break;
                case long value:
                    writer.Write(Int64);
                    writer.Write(value);
                    break;
                default:
                    throw new NotSupportedException(
                        $"The column '{cell.Key}' holds a {cell.Value.GetType().Name}, which is not one of the types of a table cell.");
            }
        }
        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>The row of the keys with the cells <see cref="Cells"/> stored, in their stored order.</summary>
    /// <exception cref="FormatException">The blob is not a cells blob of a format this version reads.</exception>
    internal static TableEntity Row(string partitionKey, string rowKey, byte[] cells)
    {
        var row = new TableEntity(partitionKey, rowKey);
        try
        {
            using var reader = new BinaryReader(new MemoryStream(cells, writable: false));
            var format = reader.ReadByte();
            if (format != CellsFormat)
                throw new FormatException($"The row's cells are in format {format}; this version reads format {CellsFormat}. Was the database written by a newer version?");
            var count = reader.Read7BitEncodedInt();
            for (var i = 0; i < count; i++)
            {
                var name = ReadString(reader);
                var tag = reader.ReadByte();
                row[name] = tag switch
                {
                    Null => null,
                    Text => ReadString(reader),
                    Bytes => Exactly(reader, reader.Read7BitEncodedInt()),
                    Binary => new BinaryData(Exactly(reader, reader.Read7BitEncodedInt())),
                    Boolean => reader.ReadBoolean(),
                    DateAndOffset => new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16())),
                    Date => DateTime.FromBinary(reader.ReadInt64()),
                    Double => BitConverter.Int64BitsToDouble(reader.ReadInt64()),
                    GuidValue => new Guid(Exactly(reader, 16), bigEndian: true),
                    Int32 => reader.ReadInt32(),
                    Int64 => reader.ReadInt64(),
                    _ => throw new FormatException($"The column '{name}' has the unknown type tag {tag}."),
                };
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentException or DecoderFallbackException)
        {
            throw new FormatException("The row's cells are truncated or damaged.", ex);
        }
        return row;
    }

    private static bool IsKey(string column) => column is "PartitionKey" or "RowKey";

    private static void WriteString(BinaryWriter writer, string value)
    {
        if (SplitCodec.IsWellFormedUtf16(value))
        {
            writer.Write(Utf8);
            var bytes = StrictUtf8.GetBytes(value);
            writer.Write7BitEncodedInt(bytes.Length);
            writer.Write(bytes);
        }
        else
        {
            writer.Write(Utf16);
            writer.Write7BitEncodedInt(value.Length);
            Span<byte> unit = stackalloc byte[2];
            foreach (var c in value)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(unit, c);
                writer.Write(unit);
            }
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        var encoding = reader.ReadByte();
        var length = reader.Read7BitEncodedInt();
        switch (encoding)
        {
            case Utf8:
                return StrictUtf8.GetString(Exactly(reader, length));
            case Utf16:
                return string.Create(length, Exactly(reader, checked(length * 2)), static (chars, bytes) =>
                {
                    for (var i = 0; i < chars.Length; i++)
                        chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
                });
            default:
                throw new FormatException($"A string has the unknown encoding {encoding}.");
        }
    }

    private static byte[] Exactly(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        return bytes.Length == count ? bytes : throw new EndOfStreamException();
    }
}
