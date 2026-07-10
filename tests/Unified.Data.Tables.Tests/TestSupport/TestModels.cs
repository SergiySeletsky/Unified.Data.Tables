namespace Unified.Data.Tables.Tests.TestSupport;

// Shared entity / POCO types used across the test suite. Kept deliberately generic (no domain
// meaning) so the suite exercises the storage + serializer infrastructure itself, the way the
// package will be reused across projects.

/// <summary>Minimal entity with a couple of scalar columns — the workhorse for storage tests.</summary>
public sealed class TestEntity : Entity
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

/// <summary>A second, unrelated entity type — used to prove type-aware equality.</summary>
public sealed class OtherEntity : Entity
{
}

public sealed class SimpleEntity : Entity
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public bool Active { get; set; }
}

public sealed class NestedEntity : Entity
{
    public string Title { get; set; } = "";
    public AddressInfo Address { get; set; } = new();
}

public sealed class AddressInfo
{
    public string City { get; set; } = "";
    public string Country { get; set; } = "";
}

public sealed class EntityWithDateTime : Entity
{
    public DateTime EventDate { get; set; }
    public DateTime? OptionalDate { get; set; }
    public DateTimeOffset OffsetDate { get; set; }
}

public enum TestStatus
{
    Draft,
    Active,
    Completed
}

public sealed class EntityWithEnum : Entity
{
    public TestStatus Status { get; set; }
}

public sealed class EntityWithDecimal : Entity
{
    public decimal Amount { get; set; }
    public decimal? OptionalAmount { get; set; }
}

public sealed class EntityWithGuid : Entity
{
    public Guid TrackingId { get; set; }
}

public sealed class EntityWithCollections : Entity
{
    public List<string> Tags { get; set; } = [];
    public Dictionary<string, int> Scores { get; set; } = [];
}

public sealed class EntityWithNullable : Entity
{
    public int? OptionalInt { get; set; }
    public bool? OptionalBool { get; set; }
    public double? OptionalDouble { get; set; }
}

public sealed class EntityWithTimeSpan : Entity
{
    public TimeSpan Duration { get; set; }
}

public sealed class EntityWithByteArray : Entity
{
    public byte[] Data { get; set; } = [];
}

public sealed class EntityWithUnsignedInts : Entity
{
    public uint UInt32Value { get; set; }
    public ulong UInt64Value { get; set; }
}

/// <summary>Plain POCO (not an <see cref="Entity"/>) for cell-size serializer tests.</summary>
public sealed class EntityWithContent
{
    public string? Content { get; set; }
    public int Number { get; set; }
}

/// <summary>Plain POCO with a list property for oversized-list serializer tests.</summary>
public sealed class EntityWithSteps
{
    public List<string> Steps { get; set; } = [];
    public int Number { get; set; }
}

/// <summary>Entity with a role-gated property for <see cref="ProtectedPropertyAttribute"/> tests.</summary>
public sealed class ProtectedEntity : Entity
{
    public string Name { get; set; } = "";

    [ProtectedProperty("admin,accountant")]
    public decimal Salary { get; set; }
}

/// <summary>
/// Entity whose base-class timestamps were historically stored under legacy column names —
/// the class-level <see cref="ColumnAliasAttribute"/> shape for inherited properties.
/// </summary>
[ColumnAlias(nameof(Entity.CreatedAt), "Created")]
[ColumnAlias(nameof(Entity.UpdatedAt), "Modified")]
public class LegacyStampedEntity : Entity
{
    public string Name { get; set; } = "";
}

/// <summary>Inherits the class-level aliases from <see cref="LegacyStampedEntity"/>.</summary>
public sealed class InheritedAliasEntity : LegacyStampedEntity
{
}

/// <summary>Property-level alias — the property was renamed at some point.</summary>
public sealed class RenamedPropEntity : Entity
{
    [ColumnAlias("OldName")]
    public string NewName { get; set; } = "";
}

/// <summary>Alias on a JSON-serialized (collection) property, exercising the suffix variants.</summary>
public sealed class AliasedListEntity : Entity
{
    [ColumnAlias("OldTags")]
    public List<string> Tags { get; set; } = [];
}
