using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Unified.Data.Tables.Sqlite;

namespace Unified.Data.Tables.Identity.Tests;

// The Identity suite on the SQLite provider: every store test of this assembly once more, over
// SqliteStorage<T> on a database file and again on a :memory: database, both with the provider's default
// read cache (the Azure provider's). Each class below inherits the tests of the class it derives from and
// only names the provider; a test added to a base class runs on SQLite as well. (ConcurrencyStampsTests,
// IdentityConditionalDeleteTests, IdentityRowModelTests and the registration rules of
// AddUnifiedIdentityStoresTests touch no storage and are not repeated.) AzuriteIdentityTests.cs does the
// same for the Azure provider.

public sealed class SqliteIdentityClaimStoreTests() : IdentityClaimStoreTests(IdentityBackend.Sqlite);

public sealed class SqliteIdentityConcurrencyTests() : IdentityConcurrencyTests(IdentityBackend.Sqlite);

public sealed class SqliteIdentityLoginAndTokenStoreTests() : IdentityLoginAndTokenStoreTests(IdentityBackend.Sqlite);

public sealed class SqliteIdentityRoleStoreTests() : IdentityRoleStoreTests(IdentityBackend.Sqlite);

public sealed class SqliteIdentityUserRoleLinkTests() : IdentityUserRoleLinkTests(IdentityBackend.Sqlite);

public sealed class SqliteIdentityUserStoreRoleTests() : IdentityUserStoreRoleTests(IdentityBackend.Sqlite);

public sealed class SqliteIdentityUserStoreTests() : IdentityUserStoreTests(IdentityBackend.Sqlite);

public sealed class SqliteInMemoryIdentityClaimStoreTests() : IdentityClaimStoreTests(IdentityBackend.SqliteInMemory);

public sealed class SqliteInMemoryIdentityConcurrencyTests() : IdentityConcurrencyTests(IdentityBackend.SqliteInMemory);

public sealed class SqliteInMemoryIdentityLoginAndTokenStoreTests() : IdentityLoginAndTokenStoreTests(IdentityBackend.SqliteInMemory);

public sealed class SqliteInMemoryIdentityRoleStoreTests() : IdentityRoleStoreTests(IdentityBackend.SqliteInMemory);

public sealed class SqliteInMemoryIdentityUserRoleLinkTests() : IdentityUserRoleLinkTests(IdentityBackend.SqliteInMemory);

public sealed class SqliteInMemoryIdentityUserStoreRoleTests() : IdentityUserStoreRoleTests(IdentityBackend.SqliteInMemory);

public sealed class SqliteInMemoryIdentityUserStoreTests() : IdentityUserStoreTests(IdentityBackend.SqliteInMemory);

/// <summary>
/// UserManager over the stores as a host registers them on a SQLite <c>:memory:</c> database:
/// <c>AddUnifiedSqliteStorage(":memory:")</c>, then <c>AddUnifiedIdentityStores</c>.
/// </summary>
[Collection("OversizedCellPolicy")]
public sealed class SqliteInMemoryUserManagerConcurrencyTests : UserManagerConcurrencyTests
{
    protected override void AddStorage(IServiceCollection services) => services.AddUnifiedSqliteStorage(":memory:");
}

/// <summary>
/// UserManager over the stores as a host registers them on SQLite: <c>AddUnifiedSqliteStorage</c>, then
/// <c>AddUnifiedIdentityStores</c>.
/// </summary>
// AddUnifiedSqliteStorage applies its options to the process-wide serializer policy, as the in-memory
// registration does; see AddUnifiedIdentityStoresTests.
[Collection("OversizedCellPolicy")]
public sealed class SqliteUserManagerConcurrencyTests : UserManagerConcurrencyTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "udt-identity-tests", Guid.NewGuid().ToString("N"));

    protected override void AddStorage(IServiceCollection services) =>
        services.AddUnifiedSqliteStorage(Path.Combine(_directory, "identity.db"));

    public void Dispose()
    {
        // The container of each test owned the account and closed the database when it was disposed.
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task TheStoresResolve_AndAUserOutlivesTheHost()
    {
        // Arrange — a host that creates a user, and stops
        var first = new ServiceCollection();
        first.AddLogging();
        AddStorage(first);
        first.AddIdentityCore<IdentityUser>().AddRoles<IdentityRole>().AddUnifiedIdentityStores();
        await using (var provider = first.BuildServiceProvider())
        {
            using var scope = provider.CreateScope();
            Assert.IsType<UserStore>(scope.ServiceProvider.GetRequiredService<IUserStore<IdentityUser>>());
            Assert.IsType<RoleStore>(scope.ServiceProvider.GetRequiredService<IRoleStore<IdentityRole>>());
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True((await users.CreateAsync(new IdentityUser("alice") { Id = "u1", Email = "alice@example.test" })).Succeeded);
        }

        // Act — the next host over the same file
        var second = new ServiceCollection();
        second.AddLogging();
        AddStorage(second);
        second.AddIdentityCore<IdentityUser>().AddRoles<IdentityRole>().AddUnifiedIdentityStores();
        await using var restarted = second.BuildServiceProvider();
        using var next = restarted.CreateScope();
        var found = await next.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByNameAsync("alice");

        // Assert
        Assert.NotNull(found);
        Assert.Equal(("u1", "alice@example.test"), (found.Id, found.Email));
    }
}
