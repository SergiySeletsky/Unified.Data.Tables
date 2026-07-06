using Azure.Data.Tables;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Round-trip and representation-tolerance tests for <see cref="TableEntitySerializer"/> — the
/// reflection-based object-graph flattener. These pin the contract for every supported cell shape
/// (scalars, nested types, enums, collections, JSON/GZip fallback) and guard the "legacy cell"
/// tolerance that keeps reads from throwing when the SDK surfaces a date as a <see cref="DateTime"/>
/// or a string against a <see cref="DateTimeOffset"/> property.
/// </summary>
public class TableEntitySerializerTests
{
    private static T RoundTrip<T>(T entity) where T : Entity, new()
    {
        var te = entity.ToTableEntity("pk", "rk");
        return te.FromTableEntity<T>();
    }

    // ── Scalar + structural round-trips ─────────────────────────────────────

    [Fact]
    public void SimpleEntity_RoundTrip_PreservesAllFields()
    {
        var original = new SimpleEntity { Id = "test-1", Name = "Hello", Count = 42, Active = true };

        var restored = RoundTrip(original);

        Assert.Equal("Hello", restored.Name);
        Assert.Equal(42, restored.Count);
        Assert.True(restored.Active);
        Assert.Equal("test-1", restored.Id);
    }

    [Fact]
    public void NestedEntity_RoundTrip_FlattensThenRestores()
    {
        var original = new NestedEntity
        {
            Id = "nested-1",
            Title = "HQ",
            Address = new AddressInfo { City = "Kyiv", Country = "Ukraine" }
        };

        var tableEntity = original.ToTableEntity("pk", "rk");

        // Nested complex types fan out to Parent_Child columns.
        Assert.Contains("Address_City", tableEntity.Keys);
        Assert.Contains("Address_Country", tableEntity.Keys);

        var restored = tableEntity.FromTableEntity<NestedEntity>();
        Assert.Equal("HQ", restored.Title);
        Assert.Equal("Kyiv", restored.Address.City);
        Assert.Equal("Ukraine", restored.Address.Country);
    }

    [Fact]
    public void CreatedAndModified_RoundTrip_AsColumns()
    {
        var created = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var modified = new DateTimeOffset(2024, 2, 2, 12, 30, 0, TimeSpan.Zero);
        var original = new SimpleEntity { Id = "ts", Created = created, Modified = modified };

        var restored = RoundTrip(original);

        Assert.Equal(created, restored.Created);
        Assert.Equal(modified, restored.Modified);
    }

    [Fact]
    public void DateTime_RoundTrip_PreservesValues()
    {
        var now = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var original = new EntityWithDateTime { Id = "dt-1", EventDate = now, OptionalDate = now.AddDays(1) };

        var restored = RoundTrip(original);

        Assert.Equal(now, restored.EventDate);
        Assert.Equal(now.AddDays(1), restored.OptionalDate);
    }

    [Fact]
    public void DateTimeOffset_RoundTrip_PreservesValue()
    {
        var value = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var original = new EntityWithDateTime { Id = "dto-1", OffsetDate = value };

        var restored = RoundTrip(original);

        Assert.Equal(value, restored.OffsetDate);
    }

    [Fact]
    public void Enum_RoundTrip_SerializesAsString()
    {
        var original = new EntityWithEnum { Id = "enum-1", Status = TestStatus.Active };

        var tableEntity = original.ToTableEntity("pk", "rk");
        Assert.Equal("Active", tableEntity["Status"]);

        var restored = tableEntity.FromTableEntity<EntityWithEnum>();
        Assert.Equal(TestStatus.Active, restored.Status);
    }

    [Fact]
    public void Decimal_RoundTrip_ConvertsThroughDouble()
    {
        var original = new EntityWithDecimal { Id = "dec-1", Amount = 123.45m, OptionalAmount = 678.90m };

        var tableEntity = original.ToTableEntity("pk", "rk");
        Assert.IsType<double>(tableEntity["Amount"]);

        var restored = tableEntity.FromTableEntity<EntityWithDecimal>();
        Assert.Equal(123.45m, restored.Amount);
        Assert.Equal(678.90m, restored.OptionalAmount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(1234.56)]
    [InlineData(99999.99)]
    public void Decimal_TypicalMoneyValues_SurviveDoubleStorage(double raw)
    {
        var amount = (decimal)raw;
        var restored = RoundTrip(new EntityWithDecimal { Id = "m", Amount = amount });
        Assert.Equal(amount, restored.Amount);
    }

    [Fact]
    public void Guid_RoundTrip_Preserves()
    {
        var guid = Guid.NewGuid();
        var restored = RoundTrip(new EntityWithGuid { Id = "guid-1", TrackingId = guid });
        Assert.Equal(guid, restored.TrackingId);
    }

    [Fact]
    public void Collections_RoundTrip_SerializeAsJson()
    {
        var original = new EntityWithCollections
        {
            Id = "col-1",
            Tags = ["alpha", "beta", "gamma"],
            Scores = new Dictionary<string, int> { ["Math"] = 95, ["Science"] = 88 }
        };

        var tableEntity = original.ToTableEntity("pk", "rk");
        Assert.Contains(tableEntity.Keys, k => k.Contains("Json", StringComparison.Ordinal));

        var restored = tableEntity.FromTableEntity<EntityWithCollections>();
        Assert.Equal(["alpha", "beta", "gamma"], restored.Tags);
        Assert.Equal(95, restored.Scores["Math"]);
        Assert.Equal(88, restored.Scores["Science"]);
    }

    [Fact]
    public void LargeCollection_CompressedAsGZip_WhenJsonExceeds64KB()
    {
        var largeTags = Enumerable.Range(0, 5000)
            .Select(i => $"tag-{i}-with-some-padding-text-to-increase-size")
            .ToList();
        var original = new EntityWithCollections { Id = "big-1", Tags = largeTags };

        var tableEntity = original.ToTableEntity("pk", "rk");
        Assert.Contains(tableEntity.Keys, k => k.Contains("GZip", StringComparison.Ordinal));

        var restored = tableEntity.FromTableEntity<EntityWithCollections>();
        Assert.Equal(5000, restored.Tags.Count);
        Assert.Equal("tag-0-with-some-padding-text-to-increase-size", restored.Tags[0]);
    }

    [Fact]
    public void NullableFields_WithoutValues_RoundTrip()
    {
        var restored = RoundTrip(new EntityWithNullable { Id = "null-1" });

        Assert.Null(restored.OptionalInt);
        Assert.Null(restored.OptionalBool);
        Assert.Null(restored.OptionalDouble);
    }

    [Fact]
    public void NullableFields_WithValues_RoundTrip()
    {
        var original = new EntityWithNullable { Id = "null-2", OptionalInt = 42, OptionalBool = true, OptionalDouble = 3.14 };

        var restored = RoundTrip(original);

        Assert.Equal(42, restored.OptionalInt);
        Assert.True(restored.OptionalBool);
        Assert.Equal(3.14, restored.OptionalDouble);
    }

    [Fact]
    public void TimeSpan_RoundTrip_SerializesAsString()
    {
        var duration = TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(30));
        var original = new EntityWithTimeSpan { Id = "ts-1", Duration = duration };

        var tableEntity = original.ToTableEntity("pk", "rk");
        Assert.IsType<string>(tableEntity["Duration"]);

        var restored = tableEntity.FromTableEntity<EntityWithTimeSpan>();
        Assert.Equal(duration, restored.Duration);
    }

    [Fact]
    public void ByteArray_RoundTrip_PreservesData()
    {
        var data = new byte[] { 0x01, 0x02, 0xFF, 0x00 };
        var restored = RoundTrip(new EntityWithByteArray { Id = "bytes-1", Data = data });
        Assert.Equal(data, restored.Data);
    }

    [Fact]
    public void UnsignedInts_RoundTrip_ConvertsThroughLong()
    {
        var original = new EntityWithUnsignedInts { Id = "uint-1", UInt32Value = uint.MaxValue, UInt64Value = 1234567890123 };

        var restored = RoundTrip(original);

        Assert.Equal(uint.MaxValue, restored.UInt32Value);
        Assert.Equal(1234567890123UL, restored.UInt64Value);
    }

    [Fact]
    public void PersistType_LateBoundDeserialize_ReturnsConcreteType()
    {
        var original = new SimpleEntity { Id = "pt-1", Name = "Typed", Count = 7 };

        var tableEntity = original.ToTableEntity("pk", "rk", persistType: true);
        Assert.Contains(TableEntitySerializer.TypeNameColumnName, tableEntity.Keys);

        var restored = tableEntity.FromTableEntity();
        var typed = Assert.IsType<SimpleEntity>(restored);
        Assert.Equal("Typed", typed.Name);
        Assert.Equal(7, typed.Count);
    }

    [Fact]
    public void FromTableEntity_LateBound_MissingTypeColumn_Throws()
    {
        var te = new TableEntity("pk", "rk") { { "Name", "x" } };
        Assert.Throws<InvalidOperationException>(() => te.FromTableEntity());
    }

    // ── Legacy / SDK-representation tolerance ────────────────────────────────

    [Fact]
    public void FromTableEntity_DateOffsetTarget_AcceptsUtcDateTimeCell()
    {
        var te = new TableEntity("pk", "rk")
        {
            { "Id", "pk|rk" },
            { "OffsetDate", new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc) }
        };

        var restored = te.FromTableEntity<EntityWithDateTime>();

        Assert.Equal(new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero), restored.OffsetDate);
    }

    [Fact]
    public void FromTableEntity_DateOffsetTarget_TreatsUnspecifiedDateTimeAsUtc()
    {
        var te = new TableEntity("pk", "rk")
        {
            { "Id", "pk|rk" },
            { "OffsetDate", new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Unspecified) }
        };

        var restored = te.FromTableEntity<EntityWithDateTime>();

        Assert.Equal(new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero), restored.OffsetDate);
    }

    [Fact]
    public void FromTableEntity_DateOffsetTarget_AcceptsStringCell()
    {
        var te = new TableEntity("pk", "rk")
        {
            { "Id", "pk|rk" },
            { "OffsetDate", "2024-06-15T10:00:00+00:00" }
        };

        var restored = te.FromTableEntity<EntityWithDateTime>();

        Assert.Equal(new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero), restored.OffsetDate);
    }

    [Fact]
    public void FromTableEntity_DateTimeTarget_AcceptsDateTimeCell()
    {
        var te = new TableEntity("pk", "rk")
        {
            { "Id", "pk|rk" },
            { "EventDate", new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc) }
        };

        var restored = te.FromTableEntity<EntityWithDateTime>();

        Assert.Equal(new DateTime(2024, 6, 15, 9, 0, 0), restored.EventDate);
    }

    [Fact]
    public void FromTableEntity_DateTimeTarget_AcceptsStringCell()
    {
        var te = new TableEntity("pk", "rk")
        {
            { "Id", "pk|rk" },
            { "OptionalDate", "2024-06-15T09:00:00" }
        };

        var restored = te.FromTableEntity<EntityWithDateTime>();

        Assert.NotNull(restored.OptionalDate);
        Assert.Equal(new DateTime(2024, 6, 15, 9, 0, 0), restored.OptionalDate!.Value);
    }
}
