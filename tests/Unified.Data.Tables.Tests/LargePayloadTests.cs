using Azure.Data.Tables;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The three additive features that make this library usable for tables carrying large binary or
/// text payloads: transaction planning that respects the byte limit as well as the entity limit,
/// column projection, and a table-name override.
/// </summary>
public class BatchPlannerTests
{
    [Fact]
    public void Plan_CapsOnEntityCount_WhenPayloadsAreSmall()
    {
        var sizes = Enumerable.Repeat(1_000L, 250).ToArray();

        var plan = BatchPlanner.Plan(sizes);

        Assert.Equal(3, plan.Count);
        Assert.Equal(100, plan[0].Count);
        Assert.Equal(100, plan[1].Count);
        Assert.Equal(50, plan[2].Count);
    }

    [Fact]
    public void Plan_CapsOnBytes_LongBeforeTheEntityLimit()
    {
        // The bug this exists for: 100 x 61,440 B is a legal-looking 100-entity transaction that
        // the service rejects with 413, after earlier chunks have already committed.
        var sizes = Enumerable.Repeat(61_440L, 100).ToArray();

        var plan = BatchPlanner.Plan(sizes);

        Assert.True(plan.Count > 1, "a 6 MB run must not be planned as one transaction");
        Assert.All(plan, r => Assert.True(r.Bytes <= BatchPlanner.DefaultMaxBytes,
            $"a planned transaction carries {r.Bytes:N0} B, over the {BatchPlanner.DefaultMaxBytes:N0} B budget"));
        Assert.All(plan, r => Assert.True(r.Count <= BatchPlanner.DefaultMaxCount));
    }

    [Fact]
    public void Plan_CoversEveryEntityExactlyOnce_InOrder()
    {
        var sizes = Enumerable.Range(0, 137).Select(i => 40_000L + i).ToArray();

        var plan = BatchPlanner.Plan(sizes);

        // Contiguous, gapless, complete — a caller zips these ranges back onto its own list, so an
        // off-by-one here silently drops or duplicates rows.
        var next = 0;
        foreach (var range in plan)
        {
            Assert.Equal(next, range.Start);
            next += range.Count;
        }

        Assert.Equal(sizes.Length, next);
    }

    [Fact]
    public void Plan_ThrowsBeforeCommittingAnything_WhenOneEntityCannotFit()
    {
        // Raised during planning, not on the third flush with two transactions already committed.
        var sizes = new[] { 1_000L, BatchPlanner.DefaultMaxBytes + 1, 1_000L };

        var ex = Assert.Throws<BatchPayloadTooLargeException>(() => BatchPlanner.Plan(sizes));

        Assert.Equal(1, ex.Index);
        Assert.Equal(BatchPlanner.DefaultMaxBytes, ex.Limit);
    }

    [Fact]
    public void Plan_ReturnsNothing_ForAnEmptyRun() => Assert.Empty(BatchPlanner.Plan([]));

    [Fact]
    public void Plan_KeepsASingleOversizedButFittingEntityInItsOwnTransaction()
    {
        var sizes = new[] { 10L, BatchPlanner.DefaultMaxBytes, 10L };

        var plan = BatchPlanner.Plan(sizes);

        Assert.Equal(3, plan.Count);
        Assert.Equal(1, plan[1].Count);
    }
}

public class QueryProjectionTests
{
    [Fact]
    public void BuildSelect_ReturnsNull_WhenNoProjectionWasRequested()
    {
        // null means "every column" — the behaviour every prior version had unconditionally.
        Assert.Null(TableStorage<TestEntity>.BuildSelect(null));
        Assert.Null(TableStorage<TestEntity>.BuildSelect(new QueryOptions()));
        Assert.Null(TableStorage<TestEntity>.BuildSelect(new QueryOptions { Select = [] }));
    }

    [Fact]
    public void BuildSelect_AddsBackTheColumnsDeserializationCannotWorkWithout()
    {
        // The trap: SELECT Content is a valid Tables query that returns rows with no keys, no ETag
        // and no type discriminator, which deserialize into a silently wrong entity.
        var select = TableStorage<TestEntity>.BuildSelect(new QueryOptions { Select = ["Content"] })!.ToArray();

        Assert.Contains("Content", select);
        Assert.Contains("PartitionKey", select);
        Assert.Contains("RowKey", select);
        Assert.Contains("Timestamp", select);
        Assert.Contains("odata.etag", select);
        Assert.Contains("_TypeName", select);
    }

    [Fact]
    public void BuildSelect_DoesNotDuplicateAColumnTheCallerAlreadyNamed()
    {
        var select = TableStorage<TestEntity>.BuildSelect(
            new QueryOptions { Select = ["RowKey", "Content", "PartitionKey"] })!.ToArray();

        Assert.Equal(select.Length, select.Distinct(StringComparer.Ordinal).Count());
    }
}

public class TableNameResolverTests
{
    [Fact]
    public void ResolveTableName_DefaultsToTheTypeName()
        => Assert.Equal(nameof(TestEntity), TableStorage<TestEntity>.ResolveTableName(new UnifiedTableStorageOptions()));

    [Fact]
    public void ResolveTableName_UsesTheResolver_SoATablePerTenantIsExpressible()
    {
        var options = new UnifiedTableStorageOptions { TableNameResolver = t => $"tenantA{t.Name}" };

        Assert.Equal($"tenantA{nameof(TestEntity)}", TableStorage<TestEntity>.ResolveTableName(options));
    }

    [Fact]
    public void ResolveTableName_FallsBackWhenAResolverReturnsNothing()
    {
        // A silently wrong table name is far worse than ignoring a broken resolver: the store would
        // read and write a different table than the caller believes, with no error at any layer.
        var options = new UnifiedTableStorageOptions { TableNameResolver = _ => "   " };

        Assert.Equal(nameof(TestEntity), TableStorage<TestEntity>.ResolveTableName(options));
    }
}
