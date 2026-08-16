using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace Unified.Data.Tables.Identity.Tests
{
    public class IdentityClaimStoreTests : IDisposable
    {
        private readonly IdentityStoreFixture _fixture = new();

        private IUserClaimStore<IdentityUser> UserClaims => _fixture.UserStore;

        private IRoleClaimStore<IdentityRole> RoleClaims => _fixture.RoleStore;

        // CA1001 — both stores extend IDisposable.
        public void Dispose()
        {
            _fixture.UserStore.Dispose();
            _fixture.RoleStore.Dispose();
            GC.SuppressFinalize(this);
        }

        private Task AddUserClaim(string userId, string type, string value) =>
            UserClaims.AddClaimsAsync(new IdentityUser { Id = userId }, [new Claim(type, value)],
                                      TestContext.Current.CancellationToken);

        private Task AddRoleClaim(string roleId, string type, string value) =>
            RoleClaims.AddClaimAsync(new IdentityRole { Id = roleId }, new Claim(type, value),
                                     TestContext.Current.CancellationToken);

        [Fact]
        public async Task GetClaimsAsync_ReturnsThatUsersClaims()
        {
            // Arrange
            await AddUserClaim("u1", "friendly-name", "Alice");
            await AddUserClaim("u2", "friendly-name", "Bob");

            // Act
            var claims = await UserClaims.GetClaimsAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken);

            // Assert
            Assert.Single(claims);
            Assert.Equal("Alice", claims.Single().Value);
        }

        [Fact]
        public async Task GetUsersForClaimAsync_ReturnsMatchingUsers()
        {
            // Arrange — a cross-partition server-side predicate query on ClaimType + ClaimValue
            await _fixture.SeedUserAsync("u1", "alice", TestContext.Current.CancellationToken);
            await AddUserClaim("u1", "friendly-name", "Alice");

            // Act
            var users = await UserClaims.GetUsersForClaimAsync(new Claim("friendly-name", "Alice"), TestContext.Current.CancellationToken);

            // Assert
            Assert.Single(users);
            Assert.Equal("u1", users.Single().Id);
        }

        [Fact]
        public async Task DeletingAUser_RemovesOnlyThatUsersClaims()
        {
            // Arrange — the claims cascade is a partition delete keyed on the user id
            await _fixture.SeedUserAsync("u1", "alice", TestContext.Current.CancellationToken);
            await AddUserClaim("u1", "friendly-name", "Alice");
            await AddUserClaim("u2", "friendly-name", "Bob");

            // Act
            await _fixture.UserStore.DeleteAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(await UserClaims.GetClaimsAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken));
            Assert.Single(await UserClaims.GetClaimsAsync(new IdentityUser { Id = "u2" }, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task BulkClaimRead_ReturnsEveryClaim_PreservingEveryField()
        {
            // Arrange — UsersController reads every user claim in one unpartitioned query, so this
            // exercises IStorage<IdentityUserClaimModel> the way the controller does. Asserting the
            // fields as well as the count catches a transposed ClaimType/ClaimValue or a dropped
            // UserId, which a bare count would not.
            await AddUserClaim("u1", "friendly-name", "Alice");
            await AddUserClaim("u2", "friendly-name", "Bob");

            // Act
            var all = (await _fixture.UserClaimRows.QueryAsync((string?)null, TestContext.Current.CancellationToken)).ToList();

            // Assert
            Assert.Equal(2, all.Count);
            var alice = Assert.Single(all, c => c.UserId == "u1");
            Assert.Equal("friendly-name", alice.ClaimType);
            Assert.Equal("Alice", alice.ClaimValue);
        }

        [Fact]
        public async Task RoleClaims_AddThenGet_RoundTrips()
        {
            // Arrange
            var role = new IdentityRole("admin") { Id = "r1" };

            // Act
            await AddRoleClaim("r1", "perm", "read");
            var claims = await RoleClaims.GetClaimsAsync(role, TestContext.Current.CancellationToken);

            // Assert
            Assert.Single(claims);
            Assert.Equal("read", claims.Single().Value);
        }

        [Fact]
        public async Task DeletingARole_RemovesOnlyThatRolesClaims()
        {
            // Arrange — the role-claims cascade is a partition delete keyed on the role id
            await _fixture.SeedRoleAsync("r1", "admin", TestContext.Current.CancellationToken);
            await AddRoleClaim("r1", "perm", "read");
            await AddRoleClaim("r2", "perm", "write");

            // Act
            await _fixture.RoleStore.DeleteAsync(new IdentityRole("admin") { Id = "r1" }, TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(await RoleClaims.GetClaimsAsync(new IdentityRole("admin") { Id = "r1" }, TestContext.Current.CancellationToken));
            Assert.Single(await RoleClaims.GetClaimsAsync(new IdentityRole("editor") { Id = "r2" }, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task RemoveClaimsAsync_RemovesOnlyThatUserClaim()
        {
            // Arrange
            await AddUserClaim("u1", "friendly-name", "Alice");
            await AddUserClaim("u1", "perm", "read");

            // Act
            await UserClaims.RemoveClaimsAsync(new IdentityUser { Id = "u1" }, [new Claim("friendly-name", "Alice")],
                                               TestContext.Current.CancellationToken);

            // Assert
            var remaining = await UserClaims.GetClaimsAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken);
            Assert.Single(remaining);
            Assert.Equal("perm", remaining.Single().Type);
        }

        [Fact]
        public async Task RemoveClaimAsync_RemovesOnlyThatRoleClaim()
        {
            // Arrange
            var role = new IdentityRole("admin") { Id = "r1" };
            await AddRoleClaim("r1", "perm", "read");
            await AddRoleClaim("r1", "perm", "write");

            // Act
            await RoleClaims.RemoveClaimAsync(role, new Claim("perm", "read"), TestContext.Current.CancellationToken);

            // Assert
            var remaining = await RoleClaims.GetClaimsAsync(role, TestContext.Current.CancellationToken);
            Assert.Single(remaining);
            Assert.Equal("write", remaining.Single().Value);
        }

        [Fact]
        public async Task ReplaceClaimAsync_SwapsTheValue()
        {
            // Arrange — UsersController.Update renames a user through ReplaceClaimAsync, and the
            // claim key is derived from type+value, so the old row must be deleted, not merged.
            await AddUserClaim("u1", "friendly-name", "Alice");

            // Act
            await UserClaims.ReplaceClaimAsync(new IdentityUser { Id = "u1" },
                new Claim("friendly-name", "Alice"), new Claim("friendly-name", "Alicia"),
                TestContext.Current.CancellationToken);

            // Assert
            var claims = await UserClaims.GetClaimsAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken);
            Assert.Equal("Alicia", Assert.Single(claims).Value);
        }
    }
}
