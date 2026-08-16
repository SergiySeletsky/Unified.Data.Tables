using Microsoft.AspNetCore.Identity;

namespace Unified.Data.Tables.Identity.Tests
{
    public class IdentityUserStoreRoleTests : IDisposable
    {
        private readonly IdentityStoreFixture _fixture = new();

        // CA1001 — UserStore implements IUserStore<IdentityUser>, which extends IDisposable.
        public void Dispose()
        {
            _fixture.UserStore.Dispose();
            GC.SuppressFinalize(this);
        }

        private IUserRoleStore<IdentityUser> RoleStore => _fixture.UserRoleStore;

        [Fact]
        public async Task IsInRoleAsync_ReturnsFalse_WhenRoleMissing()
        {
            // Act — the old implementation threw InvalidOperationException("Role Not Found")
            var result = await RoleStore.IsInRoleAsync(new IdentityUser { Id = "u1" }, "NOSUCHROLE",
                                                       TestContext.Current.CancellationToken);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task GetUsersInRoleAsync_ReturnsEmpty_WhenRoleMissing()
        {
            // Act
            var users = await RoleStore.GetUsersInRoleAsync("NOSUCHROLE", TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(users);
        }

        [Fact]
        public async Task AddToRoleAsync_Throws_WhenRoleMissing()
        {
            // Act — a silent no-op here is indistinguishable from success: UserManager does not
            // validate role existence, so it returns IdentityResult.Success and
            // UsersController.AssignRole answers 200 OK having assigned nothing. EF Core's own
            // UserStore throws, and so must this one.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RoleStore.AddToRoleAsync(new IdentityUser { Id = "u1" }, "NOSUCHROLE",
                                         TestContext.Current.CancellationToken));

            // Assert — and nothing was written
            Assert.Contains("NOSUCHROLE", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, _fixture.UserRoleRows.Count);
        }

        [Fact]
        public async Task RemoveFromRoleAsync_IsNoOp_WhenRoleMissing()
        {
            // Arrange — coordinator-review addition: "must not throw" alone is vacuous, and so
            // would be `Assert.Equal(0, _fixture.UserRoleRows.Count)` against empty storage — deleting a
            // nonexistent row and never touching storage both leave the count at 0. Seed a real
            // assignment for a role that DOES exist, so a buggy implementation that reaches
            // storage despite a null roleEntity — or falls through to some broader delete — has
            // something to disturb.
            await _fixture.SeedRoleAsync("r1", "admin", TestContext.Current.CancellationToken);
            await RoleStore.AddToRoleAsync(new IdentityUser { Id = "u1" }, "ADMIN", TestContext.Current.CancellationToken);

            // Act
            await RoleStore.RemoveFromRoleAsync(new IdentityUser { Id = "u1" }, "NOSUCHROLE",
                                                TestContext.Current.CancellationToken);

            // Assert — the real ADMIN assignment must survive untouched
            Assert.Equal(1, _fixture.UserRoleRows.Count);
            Assert.True(await RoleStore.IsInRoleAsync(new IdentityUser { Id = "u1" }, "ADMIN",
                                                       TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task IsInRoleAsync_ReturnsTrue_WhenAssigned()
        {
            // Arrange
            await _fixture.SeedRoleAsync("r1", "admin", TestContext.Current.CancellationToken);
            await RoleStore.AddToRoleAsync(new IdentityUser { Id = "u1" }, "ADMIN", TestContext.Current.CancellationToken);

            // Act
            var result = await RoleStore.IsInRoleAsync(new IdentityUser { Id = "u1" }, "ADMIN",
                                                       TestContext.Current.CancellationToken);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task IsInRoleAsync_ReturnsFalse_WhenRoleExistsButUserNotAssigned()
        {
            // Arrange — self-review addition: without this, an implementation that returns
            // `roleEntity is not null` (i.e. "does the role exist" instead of "is this user
            // assigned to it") would still pass both IsInRoleAsync_ReturnsFalse_WhenRoleMissing
            // and IsInRoleAsync_ReturnsTrue_WhenAssigned above.
            await _fixture.SeedRoleAsync("r1", "admin", TestContext.Current.CancellationToken);

            // Act — u1 is never added to ADMIN
            var result = await RoleStore.IsInRoleAsync(new IdentityUser { Id = "u1" }, "ADMIN",
                                                       TestContext.Current.CancellationToken);

            // Assert
            Assert.False(result);
        }
    }
}
