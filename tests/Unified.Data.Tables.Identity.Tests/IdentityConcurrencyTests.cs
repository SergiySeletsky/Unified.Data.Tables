using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Unified.Data.Tables.Identity.Models;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Identity.Tests
{
    /// <summary>
    /// ConcurrencyStamp is the optimistic token of Identity's store contract: an update or delete of a
    /// user or role applies only to the row the caller read. Before 0.9.2 both were unconditional, so on a
    /// multi-instance host the last writer silently won.
    /// </summary>
    public class IdentityConcurrencyTests : IDisposable
    {
        private const string ConcurrencyFailureCode = "ConcurrencyFailure";

        private readonly IdentityStoreFixture _fixture;

        public IdentityConcurrencyTests()
            : this(IdentityBackend.InMemory)
        {
        }

        /// <summary>The same tests over another storage provider (see SqliteIdentityTests.cs).</summary>
        protected IdentityConcurrencyTests(IdentityBackend backend) => _fixture = new IdentityStoreFixture(backend);

        private IUserStore<IdentityUser> Users => _fixture.UserStore;

        private IRoleStore<IdentityRole> Roles => _fixture.RoleStore;

        public void Dispose()
        {
            _fixture.UserStore.Dispose();
            _fixture.RoleStore.Dispose();
            _fixture.Dispose();
            GC.SuppressFinalize(this);
        }

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static IdentityUser NewUser(string id = "u1") => new()
        {
            Id = id,
            UserName = "alice",
            NormalizedUserName = "ALICE",
            Email = "alice@example.com",
            NormalizedEmail = "ALICE@EXAMPLE.COM",
            SecurityStamp = "stamp-0",
        };

        private async Task<IdentityUser> CreateAndReadAsync(IdentityUser user)
        {
            Assert.True((await Users.CreateAsync(user, Ct)).Succeeded);
            return (await Users.FindByIdAsync(user.Id, Ct))!;
        }

        private static void AssertConcurrencyFailure(IdentityResult result)
        {
            Assert.False(result.Succeeded);
            var error = Assert.Single(result.Errors);
            Assert.Equal(ConcurrencyFailureCode, error.Code);
            Assert.Equal(new IdentityErrorDescriber().ConcurrencyFailure().Description, error.Description);
        }

        // ---- user update -------------------------------------------------------------------------

        [Fact]
        public async Task UserUpdate_WithStaleStamp_FailsAndLeavesRowUnchanged()
        {
            // Arrange
            var user = await CreateAndReadAsync(NewUser());
            var storedStamp = user.ConcurrencyStamp;
            user.ConcurrencyStamp = "not-the-stored-stamp";
            user.SecurityStamp = "overwrite-attempt";

            // Act
            var result = await Users.UpdateAsync(user, Ct);

            // Assert
            AssertConcurrencyFailure(result);
            Assert.Equal("not-the-stored-stamp", user.ConcurrencyStamp);
            var row = await _fixture.UserRows.OneAsync(IdentityKeys.User("u1"), Ct);
            Assert.NotNull(row);
            Assert.Equal("stamp-0", row.SecurityStamp);
            Assert.Equal(storedStamp, row.ConcurrencyStamp);
        }

        [Fact]
        public async Task UserUpdate_TwoCopies_FirstWinsSecondFails()
        {
            // Arrange — two instances each read the same user
            await CreateAndReadAsync(NewUser());
            var first = (await Users.FindByIdAsync("u1", Ct))!;
            var second = (await Users.FindByIdAsync("u1", Ct))!;
            var secondStamp = second.ConcurrencyStamp;

            // Act
            first.SecurityStamp = "revoked-everywhere";
            var firstResult = await Users.UpdateAsync(first, Ct);
            second.PhoneNumber = "+100";
            var secondResult = await Users.UpdateAsync(second, Ct);

            // Assert — the security-stamp change survives; the stale writer is refused, keeps its stamp
            Assert.True(firstResult.Succeeded);
            AssertConcurrencyFailure(secondResult);
            Assert.Equal(secondStamp, second.ConcurrencyStamp);
            var stored = (await Users.FindByIdAsync("u1", Ct))!;
            Assert.Equal("revoked-everywhere", stored.SecurityStamp);
            Assert.Null(stored.PhoneNumber);
            Assert.Equal(first.ConcurrencyStamp, stored.ConcurrencyStamp);
        }

        [Fact]
        public async Task UserUpdate_StaleCopy_SucceedsAfterReRead()
        {
            // Arrange
            await CreateAndReadAsync(NewUser());
            var first = (await Users.FindByIdAsync("u1", Ct))!;
            var second = (await Users.FindByIdAsync("u1", Ct))!;
            Assert.True((await Users.UpdateAsync(first, Ct)).Succeeded);
            AssertConcurrencyFailure(await Users.UpdateAsync(second, Ct));

            // Act — the documented recovery: re-read, re-apply, retry
            var fresh = (await Users.FindByIdAsync("u1", Ct))!;
            fresh.PhoneNumber = "+100";
            var retry = await Users.UpdateAsync(fresh, Ct);

            // Assert
            Assert.True(retry.Succeeded);
            Assert.Equal("+100", (await Users.FindByIdAsync("u1", Ct))!.PhoneNumber);
        }

        [Fact]
        public async Task UserUpdate_Success_RotatesStampAndClearsNulls()
        {
            // Arrange
            var created = NewUser();
            created.LockoutEnd = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
            var user = await CreateAndReadAsync(created);
            var before = user.ConcurrencyStamp;

            // Act — unlock: the replace must clear the column, not merge around it
            user.LockoutEnd = null;
            var result = await Users.UpdateAsync(user, Ct);

            // Assert
            Assert.True(result.Succeeded);
            Assert.NotEqual(before, user.ConcurrencyStamp);
            Assert.True(Guid.TryParse(user.ConcurrencyStamp, out _));
            var row = (await _fixture.UserRows.OneAsync(IdentityKeys.User("u1"), Ct))!;
            Assert.Null(row.LockoutEnd);
            Assert.Equal(user.ConcurrencyStamp, row.ConcurrencyStamp);
        }

        [Fact]
        public async Task UserUpdate_KeepsCreatedAt()
        {
            // Arrange
            var user = await CreateAndReadAsync(NewUser());
            var createdAt = (await _fixture.UserRows.OneAsync(IdentityKeys.User("u1"), Ct))!.CreatedAt;

            // Act
            Assert.True((await Users.UpdateAsync(user, Ct)).Succeeded);

            // Assert
            Assert.Equal(createdAt, (await _fixture.UserRows.OneAsync(IdentityKeys.User("u1"), Ct))!.CreatedAt);
        }

        [Fact]
        public async Task UserUpdate_MissingRow_FailsAndCreatesNothing()
        {
            // Arrange — an update used to upsert, resurrecting a deleted user
            var user = NewUser("ghost");
            var stamp = user.ConcurrencyStamp;

            // Act
            var result = await Users.UpdateAsync(user, Ct);

            // Assert
            AssertConcurrencyFailure(result);
            Assert.Equal(stamp, user.ConcurrencyStamp);
            Assert.Equal(0, _fixture.UserRows.Count);
        }

        [Fact]
        public async Task UserUpdate_RowChangedBetweenReadAndWrite_FailsOnETag()
        {
            // Arrange — the stamp check passes (the store reads the row first), then another writer
            // replaces the row before the store writes: only the ETag condition can catch this.
            var inner = _fixture.UserRows;
            var racing = new InterleavingStorage<IdentityUserModel>(inner, async () =>
            {
                var row = (await inner.OneAsync(IdentityKeys.User("u1"), CancellationToken.None))!;
                row.SecurityStamp = "written-by-the-other-instance";
                await inner.UpsertAsync(row, CancellationToken.None);
            });
            var store = new UserStore(racing, _fixture.RoleRows, _fixture.UserLoginRows, _fixture.UserClaimRows,
                _fixture.UserRoleRows, _fixture.UserTokenRows);
            var user = await CreateAndReadAsync(NewUser());
            var stamp = user.ConcurrencyStamp;
            user.PhoneNumber = "+100";
            racing.Armed = true;

            // Act
            var result = await store.UpdateAsync(user, Ct);

            // Assert
            AssertConcurrencyFailure(result);
            Assert.Equal(stamp, user.ConcurrencyStamp);
            var row = (await inner.OneAsync(IdentityKeys.User("u1"), Ct))!;
            Assert.Equal("written-by-the-other-instance", row.SecurityStamp);
            Assert.Null(row.PhoneNumber);
        }

        [Fact]
        public async Task UserUpdate_RowDeletedBetweenReadAndWrite_Fails()
        {
            // Arrange — the conditional replace of a vanished row fails with the backend's not-found
            // error, which the store must report as a lost race rather than throw.
            var inner = _fixture.UserRows;
            var racing = new InterleavingStorage<IdentityUserModel>(inner,
                () => inner.DeleteAsync(IdentityKeys.User("u1"), CancellationToken.None));
            var store = new UserStore(racing, _fixture.RoleRows, _fixture.UserLoginRows, _fixture.UserClaimRows,
                _fixture.UserRoleRows, _fixture.UserTokenRows);
            var user = await CreateAndReadAsync(NewUser());
            racing.Armed = true;

            // Act
            var result = await store.UpdateAsync(user, Ct);

            // Assert
            AssertConcurrencyFailure(result);
            Assert.Equal(0, inner.Count);
        }

        [Fact]
        public async Task AConditionalReplaceOfADeletedRow_IsEntityNotFound_OnThisProvider()
        {
            // Arrange — what the test above rests on. The stores tell a row deleted after their read from
            // an outage by the type of the failure, and re-read to find out (ConcurrencyStamps) only over
            // a store that throws a type of its own. No provider of this library is such a store.
            await CreateAndReadAsync(NewUser());
            var row = (await _fixture.UserRows.OneAsync(IdentityKeys.User("u1"), Ct))!;
            await _fixture.UserRows.DeleteAsync(row.Id, Ct);

            // Act / Assert
            await Assert.ThrowsAsync<EntityNotFoundException>(
                () => _fixture.UserRows.UpdateAsync(row, ConcurrencyMode.Strict, Ct));
        }

        [Fact]
        public async Task UserStore_UsesTheDescriberItIsGiven()
        {
            // Arrange
            var store = new UserStore(_fixture.UserRows, _fixture.RoleRows, _fixture.UserLoginRows,
                _fixture.UserClaimRows, _fixture.UserRoleRows, _fixture.UserTokenRows, new CustomDescriber());

            // Act
            var result = await store.UpdateAsync(NewUser("ghost"), Ct);

            // Assert
            Assert.Equal("custom concurrency failure", Assert.Single(result.Errors).Description);
        }

        // ---- user delete -------------------------------------------------------------------------

        [Fact]
        public async Task UserDelete_WithStaleStamp_FailsAndDeletesNothing()
        {
            // Arrange — the user and every kind of child row
            var user = await CreateAndReadAsync(NewUser());
            await _fixture.SeedRoleAsync("r1", "admin", Ct);
            await _fixture.UserRoleStore.AddToRoleAsync(user, "ADMIN", Ct);
            await ((IUserClaimStore<IdentityUser>)Users).AddClaimsAsync(user, [new Claim("friendly-name", "Alice")], Ct);
            await _fixture.UserStore.AddLoginCoreAsync(
                new IdentityUserLogin<string> { UserId = "u1", LoginProvider = "Google", ProviderKey = "k1" }, Ct);
            await ((IUserAuthenticationTokenStore<IdentityUser>)Users).SetTokenAsync(user, "p", "n", "v", Ct);
            var stale = (await Users.FindByIdAsync("u1", Ct))!;
            var fresh = (await Users.FindByIdAsync("u1", Ct))!;
            fresh.SecurityStamp = "changed";
            Assert.True((await Users.UpdateAsync(fresh, Ct)).Succeeded);

            // Act
            var result = await Users.DeleteAsync(stale, Ct);

            // Assert — no cascade ran
            AssertConcurrencyFailure(result);
            Assert.NotNull(await Users.FindByIdAsync("u1", Ct));
            Assert.Equal(1, _fixture.UserClaimRows.Count);
            Assert.Equal(1, _fixture.UserLoginRows.Count);
            Assert.Equal(1, _fixture.UserRoleRows.Count);
            Assert.Equal(1, _fixture.UserTokenRows.Count);
        }

        [Fact]
        public async Task UserDelete_MissingRow_Fails()
        {
            // Act
            var result = await Users.DeleteAsync(NewUser("ghost"), Ct);

            // Assert
            AssertConcurrencyFailure(result);
        }

        [Fact]
        public async Task UserDelete_RowChangedBetweenReadAndClaim_FailsAndDeletesNothing()
        {
            // Arrange
            var inner = _fixture.UserRows;
            var racing = new InterleavingStorage<IdentityUserModel>(inner, async () =>
            {
                var row = (await inner.OneAsync(IdentityKeys.User("u1"), CancellationToken.None))!;
                row.SecurityStamp = "written-by-the-other-instance";
                await inner.UpsertAsync(row, CancellationToken.None);
            });
            var store = new UserStore(racing, _fixture.RoleRows, _fixture.UserLoginRows, _fixture.UserClaimRows,
                _fixture.UserRoleRows, _fixture.UserTokenRows);
            var user = await CreateAndReadAsync(NewUser());
            await ((IUserClaimStore<IdentityUser>)Users).AddClaimsAsync(user, [new Claim("friendly-name", "Alice")], Ct);
            racing.Armed = true;

            // Act
            var result = await store.DeleteAsync(user, Ct);

            // Assert
            AssertConcurrencyFailure(result);
            Assert.Equal("written-by-the-other-instance", (await inner.OneAsync(IdentityKeys.User("u1"), Ct))!.SecurityStamp);
            Assert.Equal(1, _fixture.UserClaimRows.Count);
        }

        [Fact]
        public async Task UserDelete_WithCurrentStamp_Cascades()
        {
            // Arrange
            var user = await CreateAndReadAsync(NewUser());
            await ((IUserClaimStore<IdentityUser>)Users).AddClaimsAsync(user, [new Claim("friendly-name", "Alice")], Ct);

            // Act
            var result = await Users.DeleteAsync(user, Ct);

            // Assert
            Assert.True(result.Succeeded);
            Assert.Equal(0, _fixture.UserRows.Count);
            Assert.Equal(0, _fixture.UserClaimRows.Count);
        }

        [Fact]
        public async Task UserDelete_StaleUpdateDuringCascade_FailsAndDoesNotResurrect()
        {
            // Arrange — a copy read before the delete; its update lands after the delete claimed the row
            // (rotated its stamp) but while the cascade is still running and the user row still exists.
            await CreateAndReadAsync(NewUser());
            var updater = (await Users.FindByIdAsync("u1", Ct))!;
            var deleter = (await Users.FindByIdAsync("u1", Ct))!;
            await ((IUserClaimStore<IdentityUser>)Users).AddClaimsAsync(deleter, [new Claim("friendly-name", "Alice")], Ct);
            IdentityResult? innerUpdate = null;
            bool rowPresentDuringCascade = false;
            var claims = new InterleavingStorage<IdentityUserClaimModel>(
                _fixture.UserClaimRows,
                () => Task.CompletedTask,
                beforeDeletePartition: async () =>
                {
                    rowPresentDuringCascade = await _fixture.UserRows.OneAsync(IdentityKeys.User("u1"), CancellationToken.None) is not null;
                    updater.SecurityStamp = "late";
                    innerUpdate = await Users.UpdateAsync(updater, CancellationToken.None);
                });
            var store = new UserStore(_fixture.UserRows, _fixture.RoleRows, _fixture.UserLoginRows, claims,
                _fixture.UserRoleRows, _fixture.UserTokenRows);

            // Act
            var result = await store.DeleteAsync(deleter, Ct);

            // Assert — the stale update was refused mid-cascade, the delete finished, nothing came back
            Assert.True(rowPresentDuringCascade);
            Assert.NotNull(innerUpdate);
            AssertConcurrencyFailure(innerUpdate);
            Assert.True(result.Succeeded);
            Assert.Equal(0, _fixture.UserRows.Count);
            Assert.Equal(0, _fixture.UserClaimRows.Count);
        }

        // ---- external logins ---------------------------------------------------------------------
        // The key of a login row is {provider}|{hash(providerKey)}: it does not name the owner. A login
        // unlinked and linked again by another user between a store's read and its delete is therefore
        // another user's row under the same key, which only a delete conditional on the version read
        // leaves alone.

        private const string LoginProvider = "Google";
        private const string ProviderKey = "k1";

        private static string LoginId => IdentityKeys.UserLogin(LoginProvider, ProviderKey);

        private static IdentityUserLoginModel LoginOf(string userId, string? displayName = null) =>
            IdentityUserLoginModel.FromIdentity(new IdentityUserLogin<string>
            {
                UserId = userId,
                LoginProvider = LoginProvider,
                ProviderKey = ProviderKey,
                ProviderDisplayName = displayName,
            });

        private UserStore StoreOverLogins(IStorage<IdentityUserLoginModel> logins) =>
            new(_fixture.UserRows, _fixture.RoleRows, logins, _fixture.UserClaimRows, _fixture.UserRoleRows, _fixture.UserTokenRows);

        /// <summary>The other instance: the first user unlinks the login and <paramref name="userId"/> links it.</summary>
        private Func<Task> RelinkTo(string userId) => async () =>
        {
            await _fixture.UserLoginRows.DeleteAsync(LoginId, CancellationToken.None);
            // Azurite's ETag is a timestamp in milliseconds: keep the new row out of the old one's.
            await Task.Delay(20, CancellationToken.None);
            await _fixture.UserLoginRows.CreateAsync(LoginOf(userId), CancellationToken.None);
        };

        [Fact]
        public async Task RemoveLogin_RelinkedToAnotherUserBetweenReadAndDelete_LeavesTheOtherUsersLogin()
        {
            // Arrange — the ownership check passes on the row as read, then the login changes hands
            var user = await CreateAndReadAsync(NewUser());
            await _fixture.UserLoginRows.CreateAsync(LoginOf("u1"), Ct);
            var racing = new RacingRows<IdentityUserLoginModel>(_fixture.UserLoginRows, RelinkTo("u2")) { Armed = true };

            // Act — before 0.10.0 the delete was unconditional and removed the other user's login
            var result = await StoreOverLogins(racing).RemoveLoginCoreAsync(user, LoginProvider, ProviderKey, Ct);

            // Assert
            Assert.False(result.Succeeded);
            Assert.Equal("LoginNotFound", Assert.Single(result.Errors).Code);
            var row = await _fixture.UserLoginRows.OneAsync(LoginId, Ct);
            Assert.NotNull(row);
            Assert.Equal("u2", row.UserId);
        }

        [Fact]
        public async Task RemoveLogin_RewrittenForTheSameUserBetweenReadAndDelete_IsStillRemoved()
        {
            // Arrange — the row is another version but still this user's: the store reads again and deletes
            var user = await CreateAndReadAsync(NewUser());
            await _fixture.UserLoginRows.CreateAsync(LoginOf("u1"), Ct);
            var racing = new RacingRows<IdentityUserLoginModel>(_fixture.UserLoginRows, async () =>
            {
                await Task.Delay(20, CancellationToken.None);
                await _fixture.UserLoginRows.UpsertAsync(LoginOf("u1", "renamed"), CancellationToken.None);
            })
            { Armed = true };

            // Act
            var result = await StoreOverLogins(racing).RemoveLoginCoreAsync(user, LoginProvider, ProviderKey, Ct);

            // Assert
            Assert.True(result.Succeeded);
            Assert.Equal(0, _fixture.UserLoginRows.Count);
        }

        [Fact]
        public async Task RemoveLogin_RemovedBetweenReadAndDelete_Succeeds()
        {
            // Arrange — someone else removed it first: the login is gone, which is what was asked for
            var user = await CreateAndReadAsync(NewUser());
            await _fixture.UserLoginRows.CreateAsync(LoginOf("u1"), Ct);
            var racing = new RacingRows<IdentityUserLoginModel>(_fixture.UserLoginRows,
                () => _fixture.UserLoginRows.DeleteAsync(LoginId, CancellationToken.None))
            { Armed = true };

            // Act
            var result = await StoreOverLogins(racing).RemoveLoginCoreAsync(user, LoginProvider, ProviderKey, Ct);

            // Assert
            Assert.True(result.Succeeded);
            Assert.Equal(0, _fixture.UserLoginRows.Count);
        }

        [Fact]
        public async Task UserDelete_LoginRelinkedToAnotherUserDuringCascade_LeavesTheOtherUsersLogin()
        {
            // Arrange — the cascade finds the user's logins by query, then the login changes hands
            var user = await CreateAndReadAsync(NewUser());
            await _fixture.UserLoginRows.CreateAsync(LoginOf("u1"), Ct);
            var racing = new RacingRows<IdentityUserLoginModel>(_fixture.UserLoginRows, RelinkTo("u2")) { Armed = true };

            // Act — before 0.10.0 the cascade removed whatever row had the key
            var result = await StoreOverLogins(racing).DeleteAsync(user, Ct);

            // Assert — the user is gone, the other user's login is not
            Assert.True(result.Succeeded);
            Assert.Equal(0, _fixture.UserRows.Count);
            var row = await _fixture.UserLoginRows.OneAsync(LoginId, Ct);
            Assert.NotNull(row);
            Assert.Equal("u2", row.UserId);
        }

        [Fact]
        public async Task UserDelete_RemovesEveryLoginOfTheUser_AndNoOtherUsers()
        {
            // Arrange
            var user = await CreateAndReadAsync(NewUser());
            await _fixture.UserStore.AddLoginCoreAsync(
                new IdentityUserLogin<string> { UserId = "u1", LoginProvider = "Google", ProviderKey = "a" }, Ct);
            await _fixture.UserStore.AddLoginCoreAsync(
                new IdentityUserLogin<string> { UserId = "u1", LoginProvider = "GitHub", ProviderKey = "b" }, Ct);
            await _fixture.UserStore.AddLoginCoreAsync(
                new IdentityUserLogin<string> { UserId = "u2", LoginProvider = "Google", ProviderKey = "c" }, Ct);

            // Act
            var result = await Users.DeleteAsync(user, Ct);

            // Assert
            Assert.True(result.Succeeded);
            Assert.Equal(1, _fixture.UserLoginRows.Count);
            Assert.Equal("u2", (await _fixture.UserLoginRows.OneAsync(IdentityKeys.UserLogin("Google", "c"), Ct))!.UserId);
        }

        // ---- roles -------------------------------------------------------------------------------

        private async Task<IdentityRole> CreateAndReadRoleAsync(string id = "r1", string name = "admin")
        {
            var role = new IdentityRole(name) { Id = id, NormalizedName = name.ToUpperInvariant() };
            Assert.True((await Roles.CreateAsync(role, Ct)).Succeeded);
            return (await Roles.FindByIdAsync(id, Ct))!;
        }

        [Fact]
        public async Task RoleUpdate_WithStaleStamp_FailsAndLeavesRowUnchanged()
        {
            // Arrange
            var role = await CreateAndReadRoleAsync();
            role.ConcurrencyStamp = "not-the-stored-stamp";
            role.Name = "renamed";

            // Act
            var result = await Roles.UpdateAsync(role, Ct);

            // Assert
            AssertConcurrencyFailure(result);
            Assert.Equal("not-the-stored-stamp", role.ConcurrencyStamp);
            Assert.Equal("admin", (await Roles.FindByIdAsync("r1", Ct))!.Name);
        }

        [Fact]
        public async Task RoleUpdate_TwoCopies_FirstWinsSecondFails()
        {
            // Arrange
            await CreateAndReadRoleAsync();
            var first = (await Roles.FindByIdAsync("r1", Ct))!;
            var second = (await Roles.FindByIdAsync("r1", Ct))!;
            var secondStamp = second.ConcurrencyStamp;

            // Act
            first.Name = "first";
            var firstResult = await Roles.UpdateAsync(first, Ct);
            second.Name = "second";
            var secondResult = await Roles.UpdateAsync(second, Ct);

            // Assert
            Assert.True(firstResult.Succeeded);
            AssertConcurrencyFailure(secondResult);
            Assert.Equal(secondStamp, second.ConcurrencyStamp);
            var stored = (await Roles.FindByIdAsync("r1", Ct))!;
            Assert.Equal("first", stored.Name);
            Assert.Equal(first.ConcurrencyStamp, stored.ConcurrencyStamp);
            Assert.Equal(1, _fixture.RoleRows.Count);
        }

        [Fact]
        public async Task RoleUpdate_Success_RotatesStamp()
        {
            // Arrange
            var role = await CreateAndReadRoleAsync();
            var before = role.ConcurrencyStamp;

            // Act
            role.Name = "renamed";
            var result = await Roles.UpdateAsync(role, Ct);

            // Assert
            Assert.True(result.Succeeded);
            Assert.NotEqual(before, role.ConcurrencyStamp);
            var stored = (await Roles.FindByIdAsync("r1", Ct))!;
            Assert.Equal("renamed", stored.Name);
            Assert.Equal(role.ConcurrencyStamp, stored.ConcurrencyStamp);
        }

        [Fact]
        public async Task RoleUpdate_MissingRow_FailsAndCreatesNothing()
        {
            // Act
            var result = await Roles.UpdateAsync(new IdentityRole("ghost") { Id = "ghost" }, Ct);

            // Assert
            AssertConcurrencyFailure(result);
            Assert.Equal(0, _fixture.RoleRows.Count);
        }

        [Fact]
        public async Task RoleDelete_WithStaleStamp_FailsAndDeletesNothing()
        {
            // Arrange
            var role = await CreateAndReadRoleAsync();
            await ((IRoleClaimStore<IdentityRole>)Roles).AddClaimAsync(role, new Claim("perm", "read"), Ct);
            await CreateAndReadAsync(NewUser());
            await _fixture.UserRoleStore.AddToRoleAsync((await Users.FindByIdAsync("u1", Ct))!, "ADMIN", Ct);
            var stale = (await Roles.FindByIdAsync("r1", Ct))!;
            var fresh = (await Roles.FindByIdAsync("r1", Ct))!;
            fresh.Name = "changed";
            Assert.True((await Roles.UpdateAsync(fresh, Ct)).Succeeded);

            // Act
            var result = await Roles.DeleteAsync(stale, Ct);

            // Assert
            AssertConcurrencyFailure(result);
            Assert.NotNull(await Roles.FindByIdAsync("r1", Ct));
            Assert.Equal(1, _fixture.RoleClaimRows.Count);
            Assert.Equal(1, _fixture.UserRoleRows.Count);
        }

        [Fact]
        public async Task RoleDelete_WithCurrentStamp_Cascades()
        {
            // Arrange
            var role = await CreateAndReadRoleAsync();
            await ((IRoleClaimStore<IdentityRole>)Roles).AddClaimAsync(role, new Claim("perm", "read"), Ct);

            // Act
            var result = await Roles.DeleteAsync(role, Ct);

            // Assert
            Assert.True(result.Succeeded);
            Assert.Equal(0, _fixture.RoleRows.Count);
            Assert.Equal(0, _fixture.RoleClaimRows.Count);
        }

        [Fact]
        public async Task RoleDelete_MissingRow_Fails()
        {
            // Act / Assert
            AssertConcurrencyFailure(await Roles.DeleteAsync(new IdentityRole("ghost") { Id = "ghost" }, Ct));
        }

        // ---- recovery codes ----------------------------------------------------------------------

        [Fact]
        public async Task RedeemCode_RemovesOnlyThatCode_AndRedeemsItOnce()
        {
            // Arrange
            var user = await CreateAndReadAsync(NewUser());
            var codes = (IUserTwoFactorRecoveryCodeStore<IdentityUser>)Users;
            await codes.ReplaceCodesAsync(user, ["a", "b", "c"], Ct);

            // Act
            var first = await codes.RedeemCodeAsync(user, "b", Ct);
            var again = await codes.RedeemCodeAsync(user, "b", Ct);

            // Assert
            Assert.True(first);
            Assert.False(again);
            Assert.Equal(2, await codes.CountCodesAsync(user, Ct));
            Assert.True(await codes.RedeemCodeAsync(user, "a", Ct));
            Assert.True(await codes.RedeemCodeAsync(user, "c", Ct));
        }

        [Fact]
        public async Task RedeemCode_ConcurrentRedemptions_NeitherResurrectsTheOther()
        {
            // Arrange — the old read-then-upsert let the later writer put back a code the earlier one
            // had just used. Here another redemption lands between this one's read and write.
            var user = await CreateAndReadAsync(NewUser());
            var inner = _fixture.UserTokenRows;
            var tokenId = IdentityKeys.UserToken("u1", "[AspNetUserStore]", "RecoveryCodes");
            var racing = new InterleavingStorage<IdentityUserTokenModel>(inner, async () =>
            {
                var row = (await inner.OneAsync(tokenId, CancellationToken.None))!;
                row.Value = string.Join(";", row.Value!.Split(';').Where(c => c != "a"));
                await inner.UpsertAsync(row, CancellationToken.None);
            });
            var store = new UserStore(_fixture.UserRows, _fixture.RoleRows, _fixture.UserLoginRows,
                _fixture.UserClaimRows, _fixture.UserRoleRows, racing);
            await store.ReplaceCodesAsync(user, ["a", "b", "c"], Ct);
            racing.Armed = true;

            // Act
            var redeemed = await store.RedeemCodeAsync(user, "b", Ct);

            // Assert — both redemptions hold: only "c" is left
            Assert.True(redeemed);
            Assert.Equal("c", (await inner.OneAsync(tokenId, Ct))!.Value);
        }

        // ---- helpers -----------------------------------------------------------------------------

        private sealed class CustomDescriber : IdentityErrorDescriber
        {
            public override IdentityError ConcurrencyFailure() =>
                new() { Code = nameof(ConcurrencyFailure), Description = "custom concurrency failure" };
        }

        /// <summary>
        /// A decorator that, once armed, runs <c>interleave</c> right after the next point read or predicate
        /// query returns, i.e. between a store's read and its write. Unlike <see cref="InterleavingStorage{T}"/>
        /// it forwards the capability probe, so the store above it sees the side interfaces of the provider
        /// underneath (a conditional delete among them).
        /// </summary>
        private sealed class RacingRows<T>(IStorage<T> inner, Func<Task> interleave) : DelegatingStorage<T>(inner)
            where T : class, IEntity, new()
        {
            public bool Armed { get; set; }

            private async Task FireAsync()
            {
                if (!Armed) return;
                Armed = false;
                await interleave();
            }

            public override async Task<T?> OneAsync(string id, CancellationToken ct = default)
            {
                var read = await base.OneAsync(id, ct);
                await FireAsync();
                return read;
            }

            public override async Task<IReadOnlyList<T>> QueryAsync(
                Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default)
            {
                var read = await base.QueryAsync(predicate, partition, take, ct);
                await FireAsync();
                return read;
            }
        }

        /// <summary>
        /// Delegates to an inner storage, and once armed runs <c>interleave</c> right after the next point
        /// read returns, i.e. between a store's read and its write — the window another instance's write
        /// can land in. <c>beforeDeletePartition</c>, when given, runs before every partition delete, i.e.
        /// inside a user delete's cascade. A plain <see cref="IStorage{T}"/> with no capability probe: over it
        /// the stores take the path they take over any third-party implementer.
        /// </summary>
        private sealed class InterleavingStorage<T>(IStorage<T> inner, Func<Task> interleave, Func<Task>? beforeDeletePartition = null) : IStorage<T>
            where T : class, IEntity, new()
        {
            public bool Armed { get; set; }

            public async Task<T?> OneAsync(string id, CancellationToken ct = default)
            {
                var read = await inner.OneAsync(id, ct);
                if (Armed)
                {
                    Armed = false;
                    await interleave();
                }
                return read;
            }

            public Task DeleteAsync(string id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
            public async Task<int> DeletePartitionAsync(string partition, CancellationToken ct = default)
            {
                if (beforeDeletePartition is not null)
                    await beforeDeletePartition();
                return await inner.DeletePartitionAsync(partition, ct);
            }
            public Task<T> CreateAsync(T entity, CancellationToken ct = default) => inner.CreateAsync(entity, ct);
            public Task<T> UpsertAsync(T entity, CancellationToken ct = default) => inner.UpsertAsync(entity, ct);
            public Task<T> UpdateAsync(T entity, CancellationToken ct = default) => inner.UpdateAsync(entity, ct);
            public Task<T> UpdateAsync(T entity, ConcurrencyMode mode, CancellationToken ct = default) => inner.UpdateAsync(entity, mode, ct);
            public Task<string> UpdateAsync(string id, Action<UpdateBuilder<T>> builderAction, CancellationToken ct = default) => inner.UpdateAsync(id, builderAction, ct);
            public Task<bool> ExistsAsync(string id, CancellationToken ct = default) => inner.ExistsAsync(id, ct);
            public Task<IEnumerable<T>> QueryAsync(string? partition = null, CancellationToken ct = default) => inner.QueryAsync(partition, ct);
            public Task<IReadOnlyList<T>> QueryAsync(QueryOptions options, CancellationToken ct = default) => inner.QueryAsync(options, ct);
            public Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) => inner.QueryAsync(predicate, partition, take, ct);
            public IAsyncEnumerable<T> QueryStreamAsync(QueryOptions? options = null, CancellationToken ct = default) => inner.QueryStreamAsync(options, ct);
            public IAsyncEnumerable<T> QueryStreamAsync(Expression<Func<T, bool>> predicate, string? partition = null, int? take = null, CancellationToken ct = default) => inner.QueryStreamAsync(predicate, partition, take, ct);
            public Task<EntityPage<T>> QueryPageAsync(QueryOptions options, CancellationToken ct = default) => inner.QueryPageAsync(options, ct);
            public Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, string? partition = null, CancellationToken ct = default) => inner.AnyAsync(predicate, partition, ct);
            public Task<int> CreateBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => inner.CreateBatchAsync(entities, ct);
            public Task<int> UpsertBatchAsync(IReadOnlyCollection<T> entities, CancellationToken ct = default) => inner.UpsertBatchAsync(entities, ct);
            public Task<int> CountAsync(string? partition = null, CancellationToken ct = default) => inner.CountAsync(partition, ct);
        }
    }
}
