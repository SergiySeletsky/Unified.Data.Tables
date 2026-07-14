using Azure;
using Azure.Data.Tables;
using NSubstitute;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Tests;

/// <summary>
/// Enforcement of <see cref="ProtectedPropertyAttribute"/> on the whole-entity update path via a
/// pluggable <see cref="IProtectedPropertyAuthorizer"/>. A changed protected value requires an
/// authorizer that allows it; an unchanged value (or a non-protected change) never does.
/// </summary>
public class ProtectedPropertyTests
{
    private static TableEntity StoredRow(decimal salary = 100m, string name = "old")
    {
        var row = new ProtectedEntity { Id = "all|e1", Name = name, Salary = salary }.ToTableEntity("all", "e1");
        row.ETag = new ETag("W/\"etag1\"");
        return row;
    }

    [Fact]
    public async Task UpdateAsync_ChangingProtectedProperty_WithoutAuthorizer_Throws()
    {
        using var h = new StorageHarness<ProtectedEntity>();   // no authorizer registered
        h.SetupGet("all", "e1", StoredRow(salary: 100m));

        var entity = new ProtectedEntity { Id = "all|e1", Name = "old", Salary = 200m };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Store.UpdateAsync(entity));
    }

    [Fact]
    public async Task UpdateAsync_ChangingProtectedProperty_WhenAuthorizerDenies_Throws()
    {
        var authorizer = Substitute.For<IProtectedPropertyAuthorizer>();
        authorizer.IsAllowed(Arg.Any<string>()).Returns(false);
        using var h = new StorageHarness<ProtectedEntity>(authorizer);
        h.SetupGet("all", "e1", StoredRow(salary: 100m));

        var entity = new ProtectedEntity { Id = "all|e1", Name = "old", Salary = 200m };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Store.UpdateAsync(entity));
    }

    [Fact]
    public async Task UpdateAsync_ChangingProtectedProperty_WhenAuthorizerAllows_Succeeds()
    {
        var authorizer = Substitute.For<IProtectedPropertyAuthorizer>();
        authorizer.IsAllowed("admin,accountant").Returns(true);
        using var h = new StorageHarness<ProtectedEntity>(authorizer);
        h.SetupGet("all", "e1", StoredRow(salary: 100m));
        h.SetupUpdate();

        // Round-trip the stored row's ETag — since 0.6.0, Auto without one throws instead of writing.
        var entity = new ProtectedEntity { Id = "all|e1", Name = "old", Salary = 200m, ETag = "W/\"etag1\"" };
        var result = await h.Store.UpdateAsync(entity);

        Assert.Equal(200m, result.Salary);
        authorizer.Received(1).IsAllowed("admin,accountant");
    }

    [Fact]
    public async Task UpdateAsync_UnchangedProtectedProperty_Succeeds_WithoutAuthorizer()
    {
        using var h = new StorageHarness<ProtectedEntity>();
        h.SetupGet("all", "e1", StoredRow(salary: 100m, name: "old"));
        h.SetupUpdate();

        // Salary unchanged (still 100) — the protected guard is not triggered. The ETag is
        // round-tripped because Auto without one throws since 0.6.0.
        var result = await h.Store.UpdateAsync(new ProtectedEntity { Id = "all|e1", Name = "renamed", Salary = 100m, ETag = "W/\"etag1\"" });

        Assert.Equal("renamed", result.Name);
    }

    [Fact]
    public async Task UpdateAsync_EntityWithNoStoredRow_SkipsProtectedCheck()
    {
        using var h = new StorageHarness<ProtectedEntity>();
        h.SetupGet(entity: null);   // nothing stored yet (first write)
        h.SetupUpdate();

        // A first write has no ETag to round-trip, so spell the unconditional replace explicitly —
        // Auto without an ETag throws since 0.6.0.
        var result = await h.Store.UpdateAsync(new ProtectedEntity { Id = "all|e1", Name = "x", Salary = 999m },
            ConcurrencyMode.LastWriterWins);

        Assert.Equal(999m, result.Salary);
    }
}
