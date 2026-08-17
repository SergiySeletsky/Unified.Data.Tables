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

/// <summary>
/// Entity whose nested value is a POSITIONAL RECORD — the serializer stores it as a single JSON
/// cell (no flattened <c>Location_Lat</c> columns), so a member-access filter into it cannot be
/// translated to OData and must be rejected (B2).
/// </summary>
public sealed class EntityWithJsonNested : Entity
{
    public string Title { get; set; } = "";
    public GeoPoint Location { get; set; } = new(0, 0);
}

public sealed record GeoPoint(double Lat, double Lng);

/// <summary>
/// Entity whose nested value is declared as an ABSTRACT base but holds a flattenable concrete
/// instance — the serializer flattens by runtime type (Circle → Shape_Radius columns), so a filter
/// into it is valid and must NOT be rejected by the B2 guard (which sees only the static type).
/// </summary>
public sealed class EntityWithAbstractNested : Entity
{
    public ShapeBase Shape { get; set; } = new Circle();
}

public abstract class ShapeBase
{
    public double Radius { get; set; }
}

public sealed class Circle : ShapeBase
{
    public string Label { get; set; } = "";
}

/// <summary>
/// Nested owner typed as an interface that is ALSO <see cref="System.Collections.IEnumerable"/> — the
/// serializer always stores an enumerable value as one JSON cell, so a filter into it must be rejected
/// even though the owner is an interface (B2: the IEnumerable signal must win over the interface
/// "can't determine" fallback).
/// </summary>
public sealed class EntityWithEnumerableInterfaceNested : Entity
{
    public IScalarStream Stream { get; set; } = null!;
}

public interface IScalarStream : IEnumerable<int>
{
    double Score { get; set; }
}

/// <summary>A concrete <see cref="IScalarStream"/> that serializes to a JSON array (dropping
/// <see cref="Score"/>) and cannot be deserialized back into the interface — i.e. not round-trippable.</summary>
public sealed class ScalarStreamImpl : List<int>, IScalarStream
{
    public double Score { get; set; }
}

/// <summary>
/// Entity with a NON-LIST payload (a dictionary can't be prefix-trimmed) — exercises the
/// oversized-cell DROP branch: the property is omitted and only a __Truncated marker remains.
/// </summary>
public sealed class EntityWithBigBlob : Entity
{
    public Dictionary<string, string>? Blob { get; set; }
}

/// <summary>Same drop-branch shape, but INTERFACE-typed — the worst case for phantom
/// materialization on read (an interface cannot be constructed).</summary>
public sealed class EntityWithInterfaceBlob : Entity
{
    public IDictionary<string, string>? Blob { get; set; }
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

/// <summary>Entity whose NESTED object carries a role-gated property — nested-path protection tests.</summary>
public sealed class NestedProtectedEntity : Entity
{
    public string Name { get; set; } = "";
    public PayrollInfo Payroll { get; set; } = new();
}

public sealed class PayrollInfo
{
    public string Bank { get; set; } = "";

    [ProtectedProperty("admin")]
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

/// <summary>A domain base class unrelated to storage — occupies the single-inheritance slot.</summary>
public abstract class DomainAggregate
{
    public string AggregateKind => GetType().Name;
}

/// <summary>
/// INTERFACE-ONLY entity: implements <see cref="IEntity"/> directly (its base-class slot is taken
/// by a domain type), proving the 0.6.0 <c>where T : class, IEntity, new()</c> constraint — models
/// that cannot derive <see cref="Entity"/> still work end to end.
/// </summary>
public sealed class InterfaceOnlyEntity : DomainAggregate, IEntity
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

/// <summary>
/// Declares a property whose name collides with the discriminator column after the leading '_' is
/// stripped. Pins that a system column is never written into a same-named property.
/// </summary>
public sealed class MessageWithTypeNameProperty
{
    /// <summary>Payload, to prove the rest of the row still deserializes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Collides with "_TypeName" once the prefix is stripped.</summary>
    public string TypeName { get; set; } = "untouched";
}

/// <summary>
/// Declares a property colliding with a sentinel column name. Pins the same rule for consumer-owned
/// system columns, not just the discriminator.
/// </summary>
public sealed class MessageWithIsPublishedProperty
{
    /// <summary>Payload, to prove the rest of the row still deserializes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Collides with "_IsPublished" once the prefix is stripped.</summary>
    public bool IsPublished { get; set; }
}

/// <summary>
/// A non-<see cref="IEntity"/> message base shaped like a real CQRS message: an <c>Id</c>, a
/// protected-setter timestamp, and no CreatedAt/UpdatedAt/ETag/Timestamp. Exists to prove the
/// polymorphic contract admits a base that <see cref="IStorage{T}"/> cannot.
/// </summary>
public abstract class TestMessage
{
    /// <summary>The message id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Creation time. A protected setter, to pin that reflection still round-trips it.</summary>
    public DateTimeOffset Created { get; protected set; } = DateTimeOffset.UnixEpoch;

    /// <summary>Sets <see cref="Created"/> from a test.</summary>
    /// <param name="value">The value to set.</param>
    public void SetCreated(DateTimeOffset value) => Created = value;
}

/// <summary>Marks a message as an integration event, for runtime-subtype filtering tests.</summary>
public interface ITestIntegrationEvent;

/// <summary>A command in the shared hierarchy.</summary>
public sealed class TestCommand : TestMessage
{
    /// <summary>Command-only payload, to prove derived data survives a base-typed read.</summary>
    public string Operation { get; set; } = string.Empty;
}

/// <summary>A domain event in the shared hierarchy.</summary>
public sealed class TestCreatedEvent : TestMessage
{
    /// <summary>Event-only payload, to prove derived data survives a base-typed read.</summary>
    public int Version { get; set; }
}

/// <summary>A second domain event, so a partition can hold more than one derived type.</summary>
public sealed class TestArchivedEvent : TestMessage
{
    /// <summary>Event-only payload.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>An integration event, to exercise runtime-interface filtering on read.</summary>
public sealed class TestIntegrationEvent : TestMessage, ITestIntegrationEvent
{
    /// <summary>Event-only payload.</summary>
    public string Topic { get; set; } = string.Empty;
}

/// <summary>
/// A derived type with no public parameterless constructor. Pins that the polymorphic read path
/// falls back to uninitialized-object construction rather than throwing.
/// </summary>
public sealed class TestCtorlessEvent : TestMessage
{
    /// <summary>Creates the event.</summary>
    /// <param name="payload">The required payload.</param>
    public TestCtorlessEvent(string payload) => Payload = payload;

    /// <summary>The payload.</summary>
    public string Payload { get; set; }
}

/// <summary>Not part of the <see cref="TestMessage"/> hierarchy. Pins the assignability gate.</summary>
public sealed class UnrelatedType
{
    /// <summary>Payload.</summary>
    public string Name { get; set; } = string.Empty;
}
