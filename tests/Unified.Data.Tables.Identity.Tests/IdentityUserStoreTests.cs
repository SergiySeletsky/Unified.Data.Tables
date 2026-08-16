using Unified.Data.Tables.Identity;
using Unified.Data.Tables.Identity.Models;
using Microsoft.AspNetCore.Identity;

namespace Unified.Data.Tables.Identity.Tests
{
    public class IdentityUserStoreTests : IDisposable
    {
        private readonly IdentityStoreFixture _fixture = new();

        private IUserStore<IdentityUser> Store => _fixture.UserStore;

        // CA1001 — IUserStore<IdentityUser> extends IDisposable.
        public void Dispose()
        {
            _fixture.UserStore.Dispose();
            GC.SuppressFinalize(this);
        }

        private static IdentityUser NewUser(string id = "u1") => new()
        {
            Id = id,
            UserName = "alice",
            NormalizedUserName = "ALICE",
            Email = "alice@example.com",
            NormalizedEmail = "ALICE@EXAMPLE.COM"
        };

        [Fact]
        public async Task CreateAsync_ThenFindByIdAsync_RoundTrips()
        {
            // Arrange / Act
            var added = await Store.CreateAsync(NewUser(), TestContext.Current.CancellationToken);
            var found = await Store.FindByIdAsync("u1", TestContext.Current.CancellationToken);

            // Assert
            Assert.True(added.Succeeded);
            Assert.NotNull(found);
            Assert.Equal("alice", found.UserName);
        }

        [Fact]
        public async Task CreateAsync_ReturnsFailedWithCode_OnDuplicate()
        {
            // Arrange — a create, not an upsert: a second registration for an existing user id must
            // not overwrite the live row.
            await Store.CreateAsync(NewUser(), TestContext.Current.CancellationToken);

            // Act
            var second = await Store.CreateAsync(NewUser(), TestContext.Current.CancellationToken);

            // Assert — the old code swallowed every exception with no Code at all
            Assert.False(second.Succeeded);
            Assert.Contains(second.Errors, e => e.Code == "DuplicateKey");
        }

        [Fact]
        public async Task FindByEmailAsync_FindsUser()
        {
            // Arrange
            await Store.CreateAsync(NewUser(), TestContext.Current.CancellationToken);

            // Act
            var found = await ((IUserEmailStore<IdentityUser>)Store)
                .FindByEmailAsync("ALICE@EXAMPLE.COM", TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(found);
            Assert.Equal("u1", found.Id);
        }

        [Fact]
        public async Task FindByNameAsync_FindsUser()
        {
            // Arrange
            await Store.CreateAsync(NewUser(), TestContext.Current.CancellationToken);

            // Act
            var found = await Store.FindByNameAsync("ALICE", TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(found);
            Assert.Equal("u1", found.Id);
        }

        [Fact]
        public async Task UpdateAsync_ClearsLockout_WhenSetBackToNull()
        {
            // Arrange — the old Merge-mode upsert could never clear a column, so an unlocked user
            // stayed locked forever.
            var user = NewUser();
            user.LockoutEnd = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
            await Store.CreateAsync(user, TestContext.Current.CancellationToken);

            // Act
            user.LockoutEnd = null;
            await Store.UpdateAsync(user, TestContext.Current.CancellationToken);
            var found = await Store.FindByIdAsync("u1", TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(found);
            Assert.Null(found.LockoutEnd);
        }

        [Fact]
        public async Task FindByIdAsync_ReturnsNull_WhenMissing()
        {
            // Act
            var found = await Store.FindByIdAsync("nope", TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(found);
        }

        [Fact]
        public async Task Users_ReturnsEveryUser()
        {
            // Arrange — IQueryableUserStore.Users is a synchronous property by force of the
            // interface; UsersController.Get() reads every user through it.
            await Store.CreateAsync(NewUser("u1"), TestContext.Current.CancellationToken);
            await Store.CreateAsync(NewUser("u2"), TestContext.Current.CancellationToken);

            // Act
            var all = ((IQueryableUserStore<IdentityUser>)Store).Users.ToList();

            // Assert
            Assert.Equal(2, all.Count);
            Assert.Contains(all, u => u.Id == "u1");
            Assert.Contains(all, u => u.Id == "u2");
        }

        [Fact]
        public async Task DeleteAsync_CascadesClaimsLoginsRolesAndTokens()
        {
            // Arrange — a user row deleted on its own would leave orphan logins that still resolve
            // a Google sign-in to a user that no longer exists.
            var ct = TestContext.Current.CancellationToken;
            var user = NewUser();
            await Store.CreateAsync(user, ct);
            await _fixture.SeedRoleAsync("r1", "admin", ct);
            await _fixture.UserRoleStore.AddToRoleAsync(user, "ADMIN", ct);
            await ((IUserClaimStore<IdentityUser>)Store).AddClaimsAsync(
                user, [new System.Security.Claims.Claim("friendly-name", "Alice")], ct);
            await _fixture.UserStore.AddLoginCoreAsync(
                new IdentityUserLogin<string> { UserId = "u1", LoginProvider = "Google", ProviderKey = "k1" }, ct);
            await ((IUserAuthenticationTokenStore<IdentityUser>)Store)
                .SetTokenAsync(user, "p", "n", "v", ct);

            // Act
            var result = await Store.DeleteAsync(user, ct);

            // Assert
            Assert.True(result.Succeeded);
            Assert.Null(await Store.FindByIdAsync("u1", ct));
            Assert.Equal(0, _fixture.UserClaimRows.Count);
            Assert.Equal(0, _fixture.UserLoginRows.Count);
            Assert.Equal(0, _fixture.UserRoleRows.Count);
            Assert.Equal(0, _fixture.UserTokenRows.Count);
        }
    }
}
