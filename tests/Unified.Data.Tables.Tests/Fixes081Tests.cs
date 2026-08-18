using System.Runtime.Serialization;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Regression tests for the 0.8.1 slate — three write/read asymmetries in
/// <see cref="TableEntitySerializer"/>, all found by round-tripping a real application's object
/// shapes rather than the serializer's own fixtures.
/// </summary>
/// <remarks>
/// Each is the same class of defect: the WRITE side transforms a value and the READ side has no
/// inverse, so the round trip is lossy while every single-direction test passes.
/// </remarks>
public class Fixes081Tests
{
    private const string PartitionKey = "p";
    private const string RowKey = "r";

    /// <summary>
    /// A scalar <see cref="byte"/> is stored as a one-element <c>byte[]</c> (Edm.Binary), so
    /// reading it back must take element zero. Without the inverse,
    /// <c>Convert.ChangeType(byte[], typeof(byte))</c> throws "Object must implement IConvertible"
    /// and every byte property is write-only.
    /// </summary>
    [Fact]
    public void ScalarByte_RoundTrips()
    {
        var source = new ByteModel { ByteProperty = 42 };

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        var restored = entity.FromTableEntity<ByteModel>();

        Assert.Equal(42, restored.ByteProperty);
    }

    /// <summary>A nullable byte with a value takes the same path as the scalar.</summary>
    [Fact]
    public void NullableByte_WithValue_RoundTrips()
    {
        var source = new ByteModel { NullableByteProperty = 7 };

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        var restored = entity.FromTableEntity<ByteModel>();

        Assert.Equal((byte)7, restored.NullableByteProperty);
    }

    /// <summary>A real byte array is still a byte array, not a scalar.</summary>
    [Fact]
    public void ByteArray_IsNotNarrowedToScalar()
    {
        var source = new ByteModel { ByteArrayProperty = [1, 2, 3] };

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        var restored = entity.FromTableEntity<ByteModel>();

        Assert.Equal([1, 2, 3], restored.ByteArrayProperty);
    }

    /// <summary>
    /// Azure Tables cannot store a date below 1601-01-01, so the write side maps
    /// <c>default(DateTime)</c> to that sentinel. The read side must map it back, or an unset date
    /// silently becomes 1601-01-01 and changes value on every round trip.
    /// </summary>
    [Fact]
    public void DefaultDateTime_RoundTripsBackToDefault()
    {
        var source = new DateModel();

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        var restored = entity.FromTableEntity<DateModel>();

        Assert.Equal(default, restored.DateTimeProperty);
        Assert.Equal(default, restored.DateTimeOffsetProperty);
    }

    /// <summary>The sentinel inverse must not disturb a date that was actually set.</summary>
    [Fact]
    public void SetDate_IsUnaffectedBySentinelInverse()
    {
        var moment = new DateTime(2026, 8, 18, 9, 30, 0, DateTimeKind.Utc);
        var source = new DateModel
        {
            DateTimeProperty = moment,
            DateTimeOffsetProperty = new DateTimeOffset(moment)
        };

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        var restored = entity.FromTableEntity<DateModel>();

        Assert.Equal(moment, restored.DateTimeProperty);
        Assert.Equal(new DateTimeOffset(moment), restored.DateTimeOffsetProperty);
    }

    /// <summary>
    /// <c>'_'</c> is the property-path delimiter, so a property named <c>Foo_Bar</c> writes the
    /// column <c>Foo_Bar</c> — which reads back as the path <c>["Foo", "Bar"]</c>, a nested
    /// property that does not exist. The value is silently dropped. Fail the write instead.
    /// </summary>
    [Fact]
    public void PropertyNameContainingDelimiter_Throws()
    {
        var source = new AmbiguousNameModel { Ambiguous_Name = "value" };

        var error = Assert.Throws<SerializationException>(
            () => source.ToTableEntity(PartitionKey, RowKey));

        Assert.Contains(nameof(AmbiguousNameModel.Ambiguous_Name), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard is about the delimiter INSIDE a name, not about reading rows that legitimately
    /// carry <c>'_'</c>-prefixed system columns.
    /// </summary>
    [Fact]
    public void SystemColumnsOnTheRow_AreStillIgnoredOnRead()
    {
        var source = new ByteModel { ByteProperty = 3 };

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        entity[SystemColumnNames.TypeName] = typeof(ByteModel).AssemblyQualifiedName;
        entity["_IsPublished"] = true;

        var restored = entity.FromTableEntity<ByteModel>();

        Assert.Equal(3, restored.ByteProperty);
    }

    private sealed class ByteModel
    {
        public byte ByteProperty { get; set; }

        public byte? NullableByteProperty { get; set; }

        public byte[]? ByteArrayProperty { get; set; }
    }

    private sealed class DateModel
    {
        public DateTime DateTimeProperty { get; set; }

        public DateTimeOffset DateTimeOffsetProperty { get; set; }
    }

    private sealed class AmbiguousNameModel
    {
        public string Ambiguous_Name { get; set; } = "";
    }
}
