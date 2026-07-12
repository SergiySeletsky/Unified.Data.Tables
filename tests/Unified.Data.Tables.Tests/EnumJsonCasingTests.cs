using Azure.Data.Tables;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Pins the casing of enum values written INSIDE a JSON/GZip fallback cell (i.e. an enum nested in
/// a collection or complex property). They are written using the enum's declared member name
/// (PascalCase) — the default of both System.Text.Json and Newtonsoft.Json — so stored tokens stay
/// stable and byte-compatible with data written by name-as-declared serializers. Reads stay
/// case-insensitive so lowercase/camelCase tokens written by earlier versions still round-trip.
/// (Top-level/flattened enum cells are written via <c>ToString()</c> and were already declared-case;
/// see <see cref="TableEntitySerializerTests"/>.)
/// </summary>
public class EnumJsonCasingTests
{
    private sealed class EntityWithEnumList : Entity
    {
        public List<TestStatus> Statuses { get; set; } = [];
    }

    [Fact]
    public void EnumInsideJsonBlob_IsWrittenAsDeclared_NotCamelCased()
    {
        var original = new EntityWithEnumList { Id = "pk|rk", Statuses = [TestStatus.Active, TestStatus.Completed] };

        var entity = original.ToTableEntity("pk", "rk");

        Assert.True(entity.ContainsKey("Statuses__Json"));
        Assert.Equal("[\"Active\",\"Completed\"]", (string)entity["Statuses__Json"]);
    }

    [Fact]
    public void EnumInsideJsonBlob_RoundTrips()
    {
        var original = new EntityWithEnumList { Id = "pk|rk", Statuses = [TestStatus.Draft, TestStatus.Active] };

        var restored = original.ToTableEntity("pk", "rk").FromTableEntity<EntityWithEnumList>();

        Assert.Equal([TestStatus.Draft, TestStatus.Active], restored.Statuses);
    }

    [Theory]
    [InlineData("Active")] // as-declared (the new default, and the Newtonsoft form legacy data used)
    [InlineData("active")] // legacy camelCase form written by <= 0.5.0 — must still read back
    [InlineData("ACTIVE")] // arbitrary casing — reads are case-insensitive
    public void EnumInsideJsonBlob_ReadsAnyCasing(string stored)
    {
        var entity = new TableEntity("pk", "rk") { ["Statuses__Json"] = $"[\"{stored}\"]" };

        var restored = entity.FromTableEntity<EntityWithEnumList>();

        Assert.Equal([TestStatus.Active], restored.Statuses);
    }
}
