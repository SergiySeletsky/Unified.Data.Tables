using Microsoft.AspNetCore.Identity;
using Unified.Data.Tables.Identity.Models;

namespace Unified.Data.Tables.Identity.Tests
{
    /// <summary>
    /// Two hosts over one store, each with a read cache of its own: what one host has cached is not
    /// evicted when the other writes. A login this host still holds in its cache as the user's may have
    /// been unlinked and linked by another user through the other host; the delete conditional on the
    /// cached version must then leave the other user's login alone. Run on the providers that keep rows
    /// outside the process and cache reads (a SQLite file, Azurite); see the sealed classes in
    /// SqliteIdentityTests.cs and AzuriteIdentityTests.cs.
    /// </summary>
    public abstract class IdentitySecondHostTests : IDisposable
    {
        private const string LoginProvider = "Google";
        private const string ProviderKey = "k1";

        private readonly IdentityStoreFixture _fixture;

        protected IdentitySecondHostTests(IdentityBackend backend) => _fixture = new IdentityStoreFixture(backend);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string LoginId => IdentityKeys.UserLogin(LoginProvider, ProviderKey);

        public void Dispose()
        {
            _fixture.UserStore.Dispose();
            _fixture.RoleStore.Dispose();
            _fixture.Dispose();
            GC.SuppressFinalize(this);
        }

        private static IdentityUserLoginModel LoginOf(string userId) =>
            IdentityUserLoginModel.FromIdentity(new IdentityUserLogin<string>
            {
                UserId = userId,
                LoginProvider = LoginProvider,
                ProviderKey = ProviderKey,
            });

        private async Task<IdentityUser> CreateAndReadAsync(string id)
        {
            var user = new IdentityUser { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), SecurityStamp = "stamp-0" };
            Assert.True((await _fixture.UserStore.CreateAsync(user, Ct)).Succeeded);
            return (await _fixture.UserStore.FindByIdAsync(id, Ct))!;
        }

        /// <summary>
        /// This host links the login to u1 and reads it (so its cache holds u1's row), then the other host
        /// unlinks it and links it to u2. Returns the other host's view of the login table.
        /// </summary>
        private async Task<IStorage<IdentityUserLoginModel>> RelinkThroughTheOtherHostAsync()
        {
            await _fixture.UserLoginRows.CreateAsync(LoginOf("u1"), Ct);
            Assert.Equal("u1", (await _fixture.UserLoginRows.OneAsync(LoginId, Ct))!.UserId);
            Assert.Single(await _fixture.UserLoginRows.QueryAsync(l => l.UserId == "u1", null, null, Ct));

            var other = _fixture.SecondHostRows<IdentityUserLoginModel>();
            await other.DeleteAsync(LoginId, Ct);
            // Azurite's ETag is a timestamp in milliseconds: keep the new row out of the old one's.
            await Task.Delay(20, Ct);
            await other.CreateAsync(LoginOf("u2"), Ct);
            // The premise of these tests: this host has not noticed, and still answers with u1's row.
            Assert.Equal("u1", (await _fixture.UserLoginRows.OneAsync(LoginId, Ct))!.UserId);
            return other;
        }

        [Fact]
        public async Task RemoveLogin_RelinkedThroughAnotherHost_WhileThisHostStillCachesIt_LeavesTheOtherUsersLogin()
        {
            // Arrange
            var user = await CreateAndReadAsync("u1");
            var other = await RelinkThroughTheOtherHostAsync();

            // Act — this host's cache still answers with u1's row and its version
            var result = await _fixture.UserStore.RemoveLoginCoreAsync(user, LoginProvider, ProviderKey, Ct);

            // Assert — read through the other host: this one's cache proves nothing about what is stored
            Assert.False(result.Succeeded);
            Assert.Equal("LoginNotFound", Assert.Single(result.Errors).Code);
            var row = await other.OneAsync(LoginId, Ct);
            Assert.NotNull(row);
            Assert.Equal("u2", row.UserId);
        }

        [Fact]
        public async Task UserDelete_LoginRelinkedThroughAnotherHost_WhileThisHostStillCachesIt_LeavesTheOtherUsersLogin()
        {
            // Arrange
            var user = await CreateAndReadAsync("u1");
            var other = await RelinkThroughTheOtherHostAsync();

            // Act — the cascade finds u1's login in this host's cache
            var result = await _fixture.UserStore.DeleteAsync(user, Ct);

            // Assert
            Assert.True(result.Succeeded);
            var row = await other.OneAsync(LoginId, Ct);
            Assert.NotNull(row);
            Assert.Equal("u2", row.UserId);
        }
    }
}
