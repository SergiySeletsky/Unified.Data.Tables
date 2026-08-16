using Unified.Data.Tables.Identity;
using Microsoft.AspNetCore.Identity;

namespace Unified.Data.Tables.Identity.Tests
{
    public class IdentityLoginAndTokenStoreTests : IDisposable
    {
        private readonly IdentityStoreFixture _fixture = new();

        private IUserLoginStore<IdentityUser> Logins => _fixture.UserStore;

        private IUserAuthenticationTokenStore<IdentityUser> Tokens => _fixture.UserStore;

        // CA1001 — IUserLoginStore<IdentityUser> extends IDisposable.
        public void Dispose()
        {
            _fixture.UserStore.Dispose();
            GC.SuppressFinalize(this);
        }

        private Task<IdentityResult> AddLogin(string userId, string provider, string providerKey, string? displayName = null) =>
            _fixture.UserStore.AddLoginCoreAsync(new IdentityUserLogin<string>
            {
                UserId = userId,
                LoginProvider = provider,
                ProviderKey = providerKey,
                ProviderDisplayName = displayName
            }, TestContext.Current.CancellationToken);

        [Fact]
        public async Task FindByLogin_ResolvesToTheOwningUser()
        {
            // Arrange — this is the external sign-in path every federated login depends on
            await _fixture.SeedUserAsync("u1", "alice", TestContext.Current.CancellationToken);
            await AddLogin("u1", "Google", "google-sub-123", "Google");

            // Act
            var user = await Logins.FindByLoginAsync("Google", "google-sub-123", TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(user);
            Assert.Equal("u1", user.Id);
        }

        [Fact]
        public async Task FindByLogin_DoesNotResolveToADifferentUser()
        {
            // Arrange — FindByLogin_ResolvesToTheOwningUser only ever seeds one user, so a bug that
            // ignores the resolved login's UserId and returns "whatever user exists" would pass it
            // for the wrong reason. With two users and two logins, that bug is distinguishable.
            await _fixture.SeedUserAsync("u1", "alice", TestContext.Current.CancellationToken);
            await _fixture.SeedUserAsync("u2", "bob", TestContext.Current.CancellationToken);
            await AddLogin("u1", "Google", "google-sub-alice");
            await AddLogin("u2", "Google", "google-sub-bob");

            // Act
            var resolvedForBob = await Logins.FindByLoginAsync("Google", "google-sub-bob", TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(resolvedForBob);
            Assert.Equal("u2", resolvedForBob.Id);
        }

        [Fact]
        public async Task AddLogin_Fails_WhenProviderKeyAlreadyLinkedToAnotherUser()
        {
            // Arrange — unlike the user-role, user-claim, role-claim and user-token writes, this one
            // must NOT upsert: the owning UserId lives in the row's VALUE, not its key
            // ({provider}|{hash(providerKey)}). An upsert would let u2 silently steal u1's Google
            // login, which is an account-takeover-shaped failure mode for any deployment
            // that uses external logins.
            await _fixture.SeedUserAsync("u1", "alice", TestContext.Current.CancellationToken);
            await AddLogin("u1", "Google", "sub-1");

            // Act
            var result = await AddLogin("u2", "Google", "sub-1");

            // Assert
            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Code == "DuplicateKey");

            // Load-bearing: proves ownership wasn't reassigned. Without this, the test would pass
            // even if the row had been silently overwritten and the failure were merely spurious.
            var owner = await Logins.FindByLoginAsync("Google", "sub-1", TestContext.Current.CancellationToken);
            Assert.NotNull(owner);
            Assert.Equal("u1", owner.Id);
        }

        [Fact]
        public async Task FindByLogin_ReturnsNull_ForUnknownProviderKey()
        {
            // Act
            var user = await Logins.FindByLoginAsync("Google", "does-not-exist", TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(user);
        }

        [Fact]
        public async Task RemoveLogin_RemovesLogin_WhenOwnedByRequestingUser()
        {
            // Arrange
            await AddLogin("u1", "Google", "k1");

            // Act
            var result = await _fixture.UserStore.RemoveLoginCoreAsync(
                new IdentityUser { Id = "u1" }, "Google", "k1", TestContext.Current.CancellationToken);

            // Assert — checked against the raw login row, not FindByLoginAsync, which also requires
            // an owning user row to resolve and would be null either way.
            Assert.True(result.Succeeded);
            Assert.Null(await _fixture.UserLoginRows.OneAsync(IdentityKeys.UserLogin("Google", "k1"), TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task RemoveLogin_Fails_WhenLoginBelongsToAnotherUser()
        {
            // Arrange — u2 must not be able to delete u1's Google login by guessing provider/key.
            await AddLogin("u1", "Google", "k1");

            // Act
            var result = await _fixture.UserStore.RemoveLoginCoreAsync(
                new IdentityUser { Id = "u2" }, "Google", "k1", TestContext.Current.CancellationToken);

            // Assert
            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Code == "LoginNotFound");
            Assert.NotNull(await _fixture.UserLoginRows.OneAsync(IdentityKeys.UserLogin("Google", "k1"), TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task DeletingAUser_RemovesOnlyThatUsersLogins()
        {
            // Arrange — logins are partitioned by provider, not by user, so the cascade has to find
            // them with a cross-partition predicate query on UserId.
            await _fixture.SeedUserAsync("u1", "alice", TestContext.Current.CancellationToken);
            await AddLogin("u1", "Google", "k1");
            await AddLogin("u2", "Google", "k2");

            // Act
            await _fixture.UserStore.DeleteAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(await Logins.GetLoginsAsync(new IdentityUser { Id = "u1" }, TestContext.Current.CancellationToken));
            Assert.Single(await Logins.GetLoginsAsync(new IdentityUser { Id = "u2" }, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task SetToken_Twice_Updates()
        {
            // Arrange — SetTokenAsync IS the update path, so an insert-only write made 2FA and
            // recovery codes silently never update after the first one.
            var user = new IdentityUser { Id = "u1" };
            await Tokens.SetTokenAsync(user, "[AspNetUserStore]", "RecoveryCodes", "first", TestContext.Current.CancellationToken);

            // Act
            await Tokens.SetTokenAsync(user, "[AspNetUserStore]", "RecoveryCodes", "second", TestContext.Current.CancellationToken);
            var found = await Tokens.GetTokenAsync(user, "[AspNetUserStore]", "RecoveryCodes", TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("second", found);
        }

        [Fact]
        public async Task DeletingAUser_RemovesEveryTokenForThatUser()
        {
            // Arrange
            var user = new IdentityUser { Id = "u1" };
            await _fixture.SeedUserAsync("u1", "alice", TestContext.Current.CancellationToken);
            await Tokens.SetTokenAsync(user, "p", "a", "1", TestContext.Current.CancellationToken);
            await Tokens.SetTokenAsync(user, "p", "b", "2", TestContext.Current.CancellationToken);

            // Act
            await _fixture.UserStore.DeleteAsync(user, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(0, _fixture.UserTokenRows.Count);
        }
    }
}
