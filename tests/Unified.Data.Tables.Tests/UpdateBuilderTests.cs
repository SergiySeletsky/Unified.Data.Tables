using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// The fluent <see cref="UpdateBuilder{T}"/> rules and the builder-driven partial (Merge) update
/// path on <see cref="TableStorage{T}"/> — only declared columns are written, managed and protected
/// properties are guarded, and the in-memory cache is patched in place.
/// </summary>
public class UpdateBuilderTests
{
    // ── Builder rules ───────────────────────────────────────────────────────

    [Fact]
    public void SetProperty_RecordsDeclaredValues()
    {
        var builder = new UpdateBuilder<TestEntity>();

        builder.SetProperty(x => x.Name, "hello").SetProperty(x => x.Value, 42);

        Assert.Equal("hello", builder.Updates["Name"]);
        Assert.Equal(42, builder.Updates["Value"]);
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("CreatedAt")]
    [InlineData("UpdatedAt")]
    [InlineData("ETag")]
    [InlineData("Timestamp")]
    public void SetProperty_OnManagedProperty_Throws(string managed)
    {
        var builder = new UpdateBuilder<TestEntity>();

        var ex = Assert.Throws<InvalidOperationException>(() => managed switch
        {
            "Id" => builder.SetProperty(x => x.Id, "x"),
            "CreatedAt" => builder.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow),
            "UpdatedAt" => builder.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
            "Timestamp" => builder.SetProperty(x => x.Timestamp, DateTimeOffset.UtcNow),
            _ => builder.SetProperty(x => x.ETag!, "x")
        });

        Assert.Contains("managed by the storage layer", ex.Message);
    }

    [Fact]
    public void SetProperty_OnProtectedProperty_WithoutAllowProtected_Throws()
    {
        var builder = new UpdateBuilder<ProtectedEntity>();
        Assert.Throws<InvalidOperationException>(() => builder.SetProperty(x => x.Salary, 100m));
    }

    [Fact]
    public void SetProperty_OnProtectedProperty_WithAllowProtected_Succeeds()
    {
        var builder = new UpdateBuilder<ProtectedEntity>().AllowProtected();

        builder.SetProperty(x => x.Salary, 100m);

        Assert.True(builder.Updates.ContainsKey("Salary"));
    }

    [Fact]
    public void SetProperty_Duplicate_Throws()
    {
        var builder = new UpdateBuilder<TestEntity>();
        builder.SetProperty(x => x.Name, "a");

        Assert.Throws<InvalidOperationException>(() => builder.SetProperty(x => x.Name, "b"));
    }

    [Fact]
    public void SetProperty_NullValue_Throws()
    {
        var builder = new UpdateBuilder<TestEntity>();
        Assert.Throws<ArgumentNullException>(() => builder.SetProperty(x => x.Name, (string)null!));
    }

    [Fact]
    public void SetProperty_NonMemberExpression_Throws()
    {
        var builder = new UpdateBuilder<TestEntity>();
        Assert.Throws<ArgumentException>(() => builder.SetProperty(x => x.Value + 1, 5));
    }

    // ── Nested property paths ───────────────────────────────────────────────

    [Fact]
    public void SetProperty_NestedAccess_RecordsTheFlattenedColumnPath()
    {
        var builder = new UpdateBuilder<NestedEntity>();

        builder.SetProperty(x => x.Address.City, "Kyiv");

        // Pre-0.4 this silently recorded a wrong top-level 'City' column (orphaned on read).
        Assert.Equal("Kyiv", builder.Updates["Address_City"]);
        Assert.False(builder.Updates.ContainsKey("City"));
    }

    [Fact]
    public void SetProperty_NestedDuplicatePath_Throws()
    {
        var builder = new UpdateBuilder<NestedEntity>();
        builder.SetProperty(x => x.Address.City, "Kyiv");

        Assert.Throws<InvalidOperationException>(() => builder.SetProperty(x => x.Address.City, "Lviv"));
    }

    [Fact]
    public void SetProperty_NotRootedAtTheLambdaParameter_Throws()
    {
        var somebodyElse = new TestEntity { Name = "other" };
        var builder = new UpdateBuilder<TestEntity>();

        // A closure member access is NOT a column of the row being updated.
        Assert.Throws<ArgumentException>(() => builder.SetProperty(x => somebodyElse.Name, "x"));
    }

    [Fact]
    public async Task UpdateAsync_Builder_NestedPath_SendsTheFlattenedColumn()
    {
        using var h = new StorageHarness<NestedEntity>();
        h.SetupUpdate();

        await h.Store.UpdateAsync("pk|rk", b => b.SetProperty(x => x.Address.City, "Kyiv"));

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Is<TableEntity>(te => te.ContainsKey("Address_City") && !te.ContainsKey("City") && !te.ContainsKey("Address_Country")),
            ETag.All,
            TableUpdateMode.Merge,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InMemory_NestedMerge_ChangesOnlyTheLeaf_AndPreservesSiblings()
    {
        var store = new Unified.Data.Tables.InMemory.InMemoryStorage<NestedEntity>();
        await store.CreateAsync(new NestedEntity
        {
            Id = "p|r",
            Title = "HQ",
            Address = new AddressInfo { City = "Kyiv", Country = "Ukraine" },
        });

        await store.UpdateAsync("p|r", b => b.SetProperty(x => x.Address.City, "Lviv"));
        var loaded = await store.OneAsync("p|r");

        Assert.Equal("Lviv", loaded!.Address.City);
        Assert.Equal("Ukraine", loaded.Address.Country);   // sibling column untouched
        Assert.Equal("HQ", loaded.Title);                  // unrelated column untouched
    }

    // ── Partial update through storage (Merge) ──────────────────────────────

    [Fact]
    public async Task UpdateAsync_Builder_SendsOnlyDeclaredColumnsPlusUpdatedAt_ViaMerge()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupUpdate();

        await h.Store.UpdateAsync("pk|rk", b => b.SetProperty(x => x.Name, "updated"));

        await h.Table.Received(1).UpdateEntityAsync(
            Arg.Is<TableEntity>(te => te.ContainsKey("Name") && te.ContainsKey("UpdatedAt") && !te.ContainsKey("Value")),
            ETag.All,
            TableUpdateMode.Merge,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_Builder_EmptyBuilder_Throws()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.UpdateAsync("pk|rk", _ => { }));
    }

    [Fact]
    public async Task UpdateAsync_Builder_NullId_Throws()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => h.Store.UpdateAsync(null!, b => b.SetProperty(x => x.Name, "x")));
    }

    [Fact]
    public async Task UpdateAsync_Builder_NullAction_Throws()
    {
        using var h = new StorageHarness<TestEntity>();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => h.Store.UpdateAsync("pk|rk", (Action<UpdateBuilder<TestEntity>>)null!));
    }

    [Fact]
    public async Task UpdateAsync_Builder_PatchesCachedEntityInPlace()
    {
        using var h = new StorageHarness<TestEntity>();
        h.SetupAdd();
        await h.Store.CreateAsync(new TestEntity { Id = "pk|rk", Name = "old", Value = 5 });
        h.SetupUpdate();

        await h.Store.UpdateAsync("pk|rk", b => b.SetProperty(x => x.Name, "new"));
        var cached = await h.Store.OneAsync("pk|rk");

        Assert.Equal("new", cached!.Name);
        Assert.Equal(5, cached.Value);   // untouched column preserved in cache
    }
}
