using Microsoft.AspNetCore.Identity;

namespace Unified.Data.Tables.Identity.Tests
{
    /// <summary>
    /// The user-role link rows, exercised through <c>UserStore</c>'s <see cref="IUserRoleStore{TUser}"/>
    /// members. Role-existence edge cases live in <see cref="IdentityUserStoreRoleTests"/>.
    /// </summary>
    public class IdentityUserRoleLinkTests : IDisposable
    {
        private readonly IdentityStoreFixture _fixture = new();

        private IUserRoleStore<IdentityUser> Store => _fixture.UserRoleStore;

        // CA1001 — IUserRoleStore<IdentityUser> extends IDisposable.
        public void Dispose()
        {
            _fixture.UserStore.Dispose();
            GC.SuppressFinalize(this);
        }

        private async Task SeedRoles(params string[] names)
        {
            var i = 0;
            foreach (var name in names)
                await _fixture.SeedRoleAsync($"r{++i}", name, TestContext.Current.CancellationToken);
        }

        private Task Add(string userId, string roleName) =>
            Store.AddToRoleAsync(new IdentityUser { Id = userId }, roleName, TestContext.Current.CancellationToken);

        [Fact]
        public async Task GetRolesAsync_ReturnsOnlyThatUsersRoles()
        {
            // Arrange
            await SeedRoles("admin", "teacher");
            await Add("u1", "ADMIN");
            await Add("u1", "TEACHER");
            await Add("u2", "ADMIN");

            // Act
            var roles = await Store.GetRolesAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(2, roles.Count);
            Assert.Contains("admin", roles);
            Assert.Contains("teacher", roles);
        }

        [Fact]
        public async Task GetUsersInRoleAsync_ReturnsOnlyThatRolesUsers()
        {
            // Arrange — RoleId is the row key, not the partition, so this is a cross-partition
            // predicate query followed by a per-user row read.
            await SeedRoles("admin", "teacher");
            await _fixture.SeedUserAsync("u1", "alice", TestContext.Current.CancellationToken);
            await _fixture.SeedUserAsync("u2", "bob", TestContext.Current.CancellationToken);
            await _fixture.SeedUserAsync("u3", "carol", TestContext.Current.CancellationToken);
            await Add("u1", "ADMIN");
            await Add("u2", "ADMIN");
            await Add("u3", "TEACHER");

            // Act
            var users = await Store.GetUsersInRoleAsync("ADMIN", TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(2, users.Count);
            Assert.Contains(users, u => u.Id == "u1");
            Assert.Contains(users, u => u.Id == "u2");
        }

        [Fact]
        public async Task RemoveFromRoleAsync_RemovesExactPair()
        {
            // Arrange
            await SeedRoles("admin", "teacher");
            await Add("u1", "ADMIN");
            await Add("u1", "TEACHER");

            // Act
            await Store.RemoveFromRoleAsync(new IdentityUser { Id = "u1" }, "ADMIN", TestContext.Current.CancellationToken);

            // Assert
            var remaining = await Store.GetRolesAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken);
            Assert.Equal("teacher", Assert.Single(remaining));
        }

        [Fact]
        public async Task DeletingAUser_RemovesEveryRoleLinkForThatUser()
        {
            // Arrange — the user-role row is partitioned by user id, so the cascade is a partition
            // delete.
            await SeedRoles("admin", "teacher");
            await _fixture.SeedUserAsync("u1", "alice", TestContext.Current.CancellationToken);
            await Add("u1", "ADMIN");
            await Add("u1", "TEACHER");
            await Add("u2", "ADMIN");

            // Act
            await _fixture.UserStore.DeleteAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(await Store.GetRolesAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken));
            Assert.Single(await Store.GetRolesAsync(new IdentityUser { Id = "u2" }, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task BulkUserRoleRead_ReturnsEveryPair()
        {
            // Arrange — a controller listing users reads every user-role link in one unpartitioned query, so
            // this exercises IStorage<IdentityUserRoleModel> the way the controller does.
            await SeedRoles("admin", "teacher");
            await Add("u1", "ADMIN");
            await Add("u2", "TEACHER");

            // Act
            var all = (await _fixture.UserRoleRows.QueryAsync((string?)null, TestContext.Current.CancellationToken)).ToList();

            // Assert
            Assert.Equal(2, all.Count);
            Assert.Contains(all, r => r.UserId == "u1" && r.RoleId == "r1");
            Assert.Contains(all, r => r.UserId == "u2" && r.RoleId == "r2");
        }

        [Fact]
        public async Task AddToRoleAsync_ReassigningSameRole_IsIdempotent()
        {
            // Arrange — unlike a login, the owner is part of the user-role key, so an upsert here
            // cannot reassign anything and re-adding must not throw.
            await SeedRoles("admin");
            await Add("u1", "ADMIN");

            // Act
            await Add("u1", "ADMIN");

            // Assert
            var roles = await Store.GetRolesAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken);
            Assert.Equal("admin", Assert.Single(roles));
        }
    }
}
