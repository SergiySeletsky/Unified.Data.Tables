using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace Unified.Data.Tables.Identity.Tests
{
    public class IdentityRoleStoreTests : IDisposable
    {
        private readonly IdentityStoreFixture _fixture = new();

        private IRoleStore<IdentityRole> Store => _fixture.RoleStore;

        // CA1001 — IRoleStore<IdentityRole> extends IDisposable.
        public void Dispose()
        {
            _fixture.RoleStore.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public async Task CreateAsync_KeysByIdNotName_SoFindByIdSucceeds()
        {
            // Arrange — an earlier role store wrote RowKey = role.Name but read by role.Id, so an
            // existing 'accountant' row could not be found by id.
            var role = new IdentityRole("accountant") { Id = "role-guid-1", NormalizedName = "ACCOUNTANT" };

            // Act
            await Store.CreateAsync(role, TestContext.Current.CancellationToken);
            var byId = await Store.FindByIdAsync("role-guid-1", TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(byId);
            Assert.Equal("accountant", byId.Name);
        }

        [Fact]
        public async Task CreateAsync_ReturnsFailedWithCode_OnDuplicate()
        {
            // Arrange — a create, not an upsert
            var role = new IdentityRole("admin") { Id = "role-guid-0", NormalizedName = "ADMIN" };
            await Store.CreateAsync(role, TestContext.Current.CancellationToken);

            // Act
            var second = await Store.CreateAsync(role, TestContext.Current.CancellationToken);

            // Assert
            Assert.False(second.Succeeded);
            Assert.Contains(second.Errors, e => e.Code == "DuplicateKey");
        }

        [Fact]
        public async Task UpdateAsync_DoesNotCreateDuplicateRow()
        {
            // Arrange — RoleManager.UpdateAsync used to mint a second row, which made
            // a caller keying users or roles by id throw on ToDictionary.
            var role = new IdentityRole("teacher") { Id = "role-guid-2", NormalizedName = "TEACHER" };
            await Store.CreateAsync(role, TestContext.Current.CancellationToken);

            // Act
            role.Name = "Teacher";
            role.NormalizedName = "TEACHER";
            await Store.UpdateAsync(role, TestContext.Current.CancellationToken);
            var all = ((IQueryableRoleStore<IdentityRole>)Store).Roles.ToList();

            // Assert
            Assert.Single(all);
            Assert.Equal("Teacher", all[0].Name);
        }

        [Fact]
        public async Task FindByNameAsync_FindsRole()
        {
            // Arrange
            await Store.CreateAsync(new IdentityRole("admin") { Id = "role-guid-3", NormalizedName = "ADMIN" }, TestContext.Current.CancellationToken);

            // Act
            var found = await Store.FindByNameAsync("ADMIN", TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(found);
            Assert.Equal("role-guid-3", found.Id);
        }

        [Fact]
        public async Task DeleteAsync_RemovesOnlyThatRole()
        {
            // Arrange
            await Store.CreateAsync(new IdentityRole("admin") { Id = "r1", NormalizedName = "ADMIN" }, TestContext.Current.CancellationToken);
            await Store.CreateAsync(new IdentityRole("teacher") { Id = "r2", NormalizedName = "TEACHER" }, TestContext.Current.CancellationToken);

            // Act
            await Store.DeleteAsync(new IdentityRole("admin") { Id = "r1" }, TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(await Store.FindByIdAsync("r1", TestContext.Current.CancellationToken));
            Assert.NotNull(await Store.FindByIdAsync("r2", TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Roles_ReturnsEveryRole()
        {
            // Arrange
            await Store.CreateAsync(new IdentityRole("admin") { Id = "r1", NormalizedName = "ADMIN" }, TestContext.Current.CancellationToken);
            await Store.CreateAsync(new IdentityRole("teacher") { Id = "r2", NormalizedName = "TEACHER" }, TestContext.Current.CancellationToken);

            // Act
            var all = ((IQueryableRoleStore<IdentityRole>)Store).Roles.ToList();

            // Assert
            Assert.Equal(2, all.Count);
            Assert.Contains(all, r => r.Id == "r1" && r.Name == "admin");
            Assert.Contains(all, r => r.Id == "r2" && r.Name == "teacher");
        }

        [Fact]
        public async Task DeleteAsync_CascadesRoleClaimsAndUserRoleLinks()
        {
            // Arrange — a deleted role must not leave user-role links behind: those links are the
            // authorization decision, and a recycled role id would silently re-grant them.
            var ct = TestContext.Current.CancellationToken;
            var admin = new IdentityRole("admin") { Id = "r1", NormalizedName = "ADMIN" };
            var teacher = new IdentityRole("teacher") { Id = "r2", NormalizedName = "TEACHER" };
            await Store.CreateAsync(admin, ct);
            await Store.CreateAsync(teacher, ct);
            await ((IRoleClaimStore<IdentityRole>)Store).AddClaimAsync(admin, new Claim("perm", "read"), ct);
            await ((IRoleClaimStore<IdentityRole>)Store).AddClaimAsync(teacher, new Claim("perm", "write"), ct);
            await _fixture.UserRoleStore.AddToRoleAsync(new IdentityUser { Id = "u1" }, "ADMIN", ct);
            await _fixture.UserRoleStore.AddToRoleAsync(new IdentityUser { Id = "u2" }, "ADMIN", ct);
            await _fixture.UserRoleStore.AddToRoleAsync(new IdentityUser { Id = "u1" }, "TEACHER", ct);

            // Act
            await Store.DeleteAsync(admin, ct);

            // Assert — only the deleted role's dependants go
            Assert.Empty(await ((IRoleClaimStore<IdentityRole>)Store).GetClaimsAsync(admin, ct));
            Assert.Single(await ((IRoleClaimStore<IdentityRole>)Store).GetClaimsAsync(teacher, ct));
            var remainingLinks = (await _fixture.UserRoleRows.QueryAsync((string?)null, ct)).ToList();
            Assert.Equal("r2", Assert.Single(remainingLinks).RoleId);
        }
    }
}
