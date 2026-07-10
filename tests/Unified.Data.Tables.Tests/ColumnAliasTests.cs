using Azure.Data.Tables;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Read-time legacy-column fallback via <see cref="ColumnAliasAttribute"/>: alias columns hydrate
/// a property only when its canonical column is absent, writes always emit canonical names, and
/// invalid alias declarations fail eagerly on first use of the type.
/// </summary>
public class ColumnAliasTests
{
    // ── Class-level aliases (inherited base-class properties) ────────────────

    [Fact]
    public void LegacyRow_WithOnlyAliasColumns_HydratesCanonicalProperties()
    {
        var created = new DateTimeOffset(2024, 3, 1, 8, 0, 0, TimeSpan.Zero);
        var updated = new DateTimeOffset(2024, 4, 2, 9, 30, 0, TimeSpan.Zero);
        var row = new TableEntity("p", "r")
        {
            ["Id"] = "p|r",
            ["Name"] = "legacy",
            ["Created"] = created,
            ["Modified"] = updated,
        };

        var entity = row.FromTableEntity<LegacyStampedEntity>();

        Assert.Equal(created, entity.CreatedAt);
        Assert.Equal(updated, entity.UpdatedAt);
        Assert.Equal("legacy", entity.Name);
    }

    [Fact]
    public void CanonicalColumn_WinsOverAlias_WhenBothPresent()
    {
        var canonical = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var legacy = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var row = new TableEntity("p", "r")
        {
            ["Id"] = "p|r",
            ["CreatedAt"] = canonical,
            ["Created"] = legacy,
        };

        var entity = row.FromTableEntity<LegacyStampedEntity>();

        Assert.Equal(canonical, entity.CreatedAt);
    }

    [Fact]
    public void ClassLevelAliases_AreInherited_ByDerivedTypes()
    {
        var created = new DateTimeOffset(2023, 5, 5, 5, 5, 5, TimeSpan.Zero);
        var row = new TableEntity("p", "r") { ["Id"] = "p|r", ["Created"] = created };

        var entity = row.FromTableEntity<InheritedAliasEntity>();

        Assert.Equal(created, entity.CreatedAt);
    }

    // ── Property-level aliases ───────────────────────────────────────────────

    [Fact]
    public void PropertyLevelAlias_HydratesRenamedProperty()
    {
        var row = new TableEntity("p", "r") { ["Id"] = "p|r", ["OldName"] = "carried over" };

        var entity = row.FromTableEntity<RenamedPropEntity>();

        Assert.Equal("carried over", entity.NewName);
    }

    [Fact]
    public void Alias_SupportsSuffixedCellFormats()
    {
        var row = new TableEntity("p", "r") { ["Id"] = "p|r", ["OldTags__Json"] = "[\"a\",\"b\"]" };

        var entity = row.FromTableEntity<AliasedListEntity>();

        Assert.Equal(new List<string> { "a", "b" }, entity.Tags);
    }

    // ── Writes stay canonical ────────────────────────────────────────────────

    [Fact]
    public void Writes_NeverEmitAliasColumns()
    {
        var entity = new LegacyStampedEntity { Id = "p|r", Name = "x" };

        var row = entity.ToTableEntity("p", "r");

        Assert.Contains("CreatedAt", row.Keys);
        Assert.Contains("UpdatedAt", row.Keys);
        Assert.DoesNotContain("Created", row.Keys);
        Assert.DoesNotContain("Modified", row.Keys);
    }

    // ── Eager validation ─────────────────────────────────────────────────────

    private sealed class CollidingAliasEntity : Entity
    {
        [ColumnAlias("Other")]
        public string Name { get; set; } = "";

        public string Other { get; set; } = "";
    }

    [Fact]
    public void Alias_CollidingWithRealPropertyName_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new TableEntity("p", "r").FromTableEntity<CollidingAliasEntity>());
        Assert.Contains("collides", ex.Message);
    }

    [ColumnAlias("NoSuchProperty", "Legacy")]
    private sealed class UnknownTargetEntity : Entity
    {
    }

    [Fact]
    public void ClassLevelAlias_ReferencingUnknownProperty_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new TableEntity("p", "r").FromTableEntity<UnknownTargetEntity>());
        Assert.Contains("unknown", ex.Message);
    }

    [ColumnAlias("JustOneArgument")]
    private sealed class ClassLevelSingleArgEntity : Entity
    {
    }

    [Fact]
    public void ClassLevelAlias_WithSingleArgumentConstructor_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new TableEntity("p", "r").FromTableEntity<ClassLevelSingleArgEntity>());
        Assert.Contains("propertyName", ex.Message);
    }
}
