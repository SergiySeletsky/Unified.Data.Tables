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

    /// <summary>
    /// A nested immutable value object whose ONLY constructor is private and annotated with a
    /// foreign <c>JsonConstructorAttribute</c> — the idiomatic Newtonsoft shape, and the shape found
    /// throughout rows written by the serializers this cell format is compatible with. System.Text.Json
    /// refuses to construct it on its own.
    /// </summary>
    [Fact]
    public void PrivateAnnotatedConstructor_RoundTrips()
    {
        var source = new HolderModel
        {
            Name = "holder",
            Location = ForeignAnnotatedModel.Create("Lviv", 7)
        };

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        var restored = entity.FromTableEntity<HolderModel>();

        Assert.Equal("holder", restored.Name);
        Assert.NotNull(restored.Location);
        Assert.Equal("Lviv", restored.Location!.City);
        Assert.Equal(7, restored.Location.Floor);
    }

    /// <summary>A collection of such objects goes through the same cell as one JSON blob.</summary>
    [Fact]
    public void CollectionOfPrivateAnnotatedConstructorObjects_RoundTrips()
    {
        var source = new HolderModel
        {
            Name = "holder",
            Locations = [ForeignAnnotatedModel.Create("Kyiv", 1), ForeignAnnotatedModel.Create("Lviv", 2)]
        };

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        var restored = entity.FromTableEntity<HolderModel>();

        Assert.Equal(2, restored.Locations!.Count);
        Assert.Equal("Kyiv", restored.Locations[0].City);
        Assert.Equal(2, restored.Locations[1].Floor);
    }

    /// <summary>
    /// The converter must not take over a type System.Text.Json already handles: the written JSON has
    /// to stay byte-for-byte what it was, or the cell format silently changes for everyone.
    /// </summary>
    [Fact]
    public void OrdinaryTypes_AreUntouchedByTheConstructorConverter()
    {
        var source = new HolderModel { Name = "holder", Plain = new PlainModel { Value = "v" } };

        var entity = source.ToTableEntity(PartitionKey, RowKey);
        var restored = entity.FromTableEntity<HolderModel>();

        Assert.Equal("v", restored.Plain!.Value);
    }

    private sealed class HolderModel
    {
        public string Name { get; set; } = "";

        public ForeignAnnotatedModel? Location { get; set; }

        public List<ForeignAnnotatedModel>? Locations { get; set; }

        public PlainModel? Plain { get; set; }
    }

    private sealed class PlainModel
    {
        public string Value { get; set; } = "";
    }

    private sealed class ForeignAnnotatedModel
    {
        [Foreign.JsonConstructor]
        private ForeignAnnotatedModel(string city, int floor)
        {
            City = city;
            Floor = floor;
        }

        public string City { get; }

        public int Floor { get; }

        public static ForeignAnnotatedModel Create(string city, int floor) => new(city, floor);
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
