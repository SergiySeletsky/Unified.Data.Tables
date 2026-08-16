using Unified.Data.Tables.Identity.Models;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using Unified.Data.Tables;

namespace Unified.Data.Tables.Identity
{
    internal abstract class UserStoreBase :
        IUserLoginStore<IdentityUser>,
        IUserClaimStore<IdentityUser>,
        IUserPasswordStore<IdentityUser>,
        IUserSecurityStampStore<IdentityUser>,
        IUserEmailStore<IdentityUser>,
        IUserLockoutStore<IdentityUser>,
        IUserPhoneNumberStore<IdentityUser>,
        IQueryableUserStore<IdentityUser>,
        IUserTwoFactorStore<IdentityUser>,
        IUserAuthenticationTokenStore<IdentityUser>,
        IUserAuthenticatorKeyStore<IdentityUser>,
        IUserTwoFactorRecoveryCodeStore<IdentityUser>,
        IProtectedUserStore<IdentityUser>
    {
        private const string InternalLoginProvider = "[AspNetUserStore]";
        private const string AuthenticatorKeyTokenName = "AuthenticatorKey";
        private const string RecoveryCodeTokenName = "RecoveryCodes";

        private readonly IStorage<IdentityUserModel> users;
        private readonly IStorage<IdentityUserLoginModel> userLogins;
        private readonly IStorage<IdentityUserClaimModel> userClaims;
        private readonly IStorage<IdentityUserRoleModel> userRoles;
        private readonly IStorage<IdentityUserTokenModel> userTokens;

        protected UserStoreBase(
            IStorage<IdentityUserModel> users,
            IStorage<IdentityUserLoginModel> userLogins,
            IStorage<IdentityUserClaimModel> userClaims,
            IStorage<IdentityUserRoleModel> userRoles,
            IStorage<IdentityUserTokenModel> userTokens)
        {
            this.users = users ?? throw new ArgumentNullException(nameof(users));
            this.userLogins = userLogins ?? throw new ArgumentNullException(nameof(userLogins));
            this.userClaims = userClaims ?? throw new ArgumentNullException(nameof(userClaims));
            this.userRoles = userRoles ?? throw new ArgumentNullException(nameof(userRoles));
            this.userTokens = userTokens ?? throw new ArgumentNullException(nameof(userTokens));
        }

        #region IQueryableUserStore
        // Synchronous by force: IQueryableUserStore exposes a property, not a Task. The blocking
        // partition read is the only shape the interface allows.
        public IQueryable<IdentityUser> Users =>
            users.QueryAsync(IdentityKeys.UserPartition).GetAwaiter().GetResult()
                 .Select(u => u.ToIdentity()).ToList().AsQueryable();
        #endregion

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        #region IUserStore
        public async Task<IdentityResult> CreateAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            try
            {
                await users.CreateAsync(IdentityUserModel.FromIdentity(user), cancellationToken);
                return IdentityResult.Success;
            }
            catch (DuplicateKeyException ex)
            {
                return IdentityResult.Failed(new IdentityError { Code = "DuplicateKey", Description = ex.Message });
            }
        }

        public async Task<IdentityResult> DeleteAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            // Cascade first, user row last: a crash midway leaves orphan child rows rather than a
            // user that cannot be found but still holds logins, roles and claims.
            await Task.WhenAll(
                userClaims.DeletePartitionAsync(user.Id, cancellationToken),
                DeleteUserLoginsAsync(user.Id, cancellationToken),
                userRoles.DeletePartitionAsync(user.Id, cancellationToken),
                userTokens.DeletePartitionAsync(user.Id, cancellationToken)
            );

            await users.DeleteAsync(IdentityKeys.User(user.Id), cancellationToken);
            return IdentityResult.Success;
        }

        public async Task<IdentityUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
            (await users.OneAsync(IdentityKeys.User(userId), cancellationToken))?.ToIdentity();

        public async Task<IdentityUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
        {
            var matches = await users.QueryAsync(
                u => u.NormalizedUserName == normalizedUserName, IdentityKeys.UserPartition, 1, cancellationToken);
            return matches.Count > 0 ? matches[0].ToIdentity() : null;
        }

        public Task<string?> GetNormalizedUserNameAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.NormalizedUserName);

        public Task<string> GetUserIdAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id);

        public Task<string?> GetUserNameAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.UserName);

        public Task SetNormalizedUserNameAsync(IdentityUser user, string? normalizedName, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).NormalizedUserName = normalizedName;
            return Task.CompletedTask;
        }

        public Task SetUserNameAsync(IdentityUser user, string? userName, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).UserName = userName;
            return Task.CompletedTask;
        }

        public async Task<IdentityResult> UpdateAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            // Upsert, i.e. a full replace rather than a merge: only a replace can clear a column
            // that was set back to null, which is what lets an unlocked user actually unlock.
            await users.UpsertAsync(IdentityUserModel.FromIdentity(user), cancellationToken);
            return IdentityResult.Success;
        }
        #endregion

        #region IUserPasswordStore
        public Task SetPasswordHashAsync(IdentityUser user, string? passwordHash, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).PasswordHash = passwordHash;
            return Task.CompletedTask;
        }

        public Task<string?> GetPasswordHashAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash);

        public Task<bool> HasPasswordAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
        #endregion

        #region IUserEmailStore
        public Task SetEmailAsync(IdentityUser user, string? email, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).Email = email;
            return Task.CompletedTask;
        }

        public Task<string?> GetEmailAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.Email);

        public Task<bool> GetEmailConfirmedAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.EmailConfirmed);

        public Task SetEmailConfirmedAsync(IdentityUser user, bool confirmed, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).EmailConfirmed = confirmed;
            return Task.CompletedTask;
        }

        public async Task<IdentityUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        {
            var matches = await users.QueryAsync(
                u => u.NormalizedEmail == normalizedEmail, IdentityKeys.UserPartition, 1, cancellationToken);
            return matches.Count > 0 ? matches[0].ToIdentity() : null;
        }

        public Task<string?> GetNormalizedEmailAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.NormalizedEmail);

        public Task SetNormalizedEmailAsync(IdentityUser user, string? normalizedEmail, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).NormalizedEmail = normalizedEmail;
            return Task.CompletedTask;
        }
        #endregion

        #region IUserPhoneNumberStore
        public Task SetPhoneNumberAsync(IdentityUser user, string? phoneNumber, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).PhoneNumber = phoneNumber;
            return Task.CompletedTask;
        }

        public Task<string?> GetPhoneNumberAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.PhoneNumber);

        public Task<bool> GetPhoneNumberConfirmedAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.PhoneNumberConfirmed);

        public Task SetPhoneNumberConfirmedAsync(IdentityUser user, bool confirmed, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).PhoneNumberConfirmed = confirmed;
            return Task.CompletedTask;
        }
        #endregion

        #region IUserSecurityStampStore
        public Task SetSecurityStampAsync(IdentityUser user, string stamp, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).SecurityStamp = stamp;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecurityStampAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.SecurityStamp);
        #endregion

        #region IUserTwoFactorStore
        public Task SetTwoFactorEnabledAsync(IdentityUser user, bool enabled, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).TwoFactorEnabled = enabled;
            return Task.CompletedTask;
        }

        public Task<bool> GetTwoFactorEnabledAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.TwoFactorEnabled);
        #endregion

        #region IUserLockoutStore
        public Task<DateTimeOffset?> GetLockoutEndDateAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnd);

        public Task SetLockoutEndDateAsync(IdentityUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).LockoutEnd = lockoutEnd;
            return Task.CompletedTask;
        }

        public Task<int> IncrementAccessFailedCountAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(++(user ?? throw new ArgumentNullException(nameof(user))).AccessFailedCount);

        public Task ResetAccessFailedCountAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).AccessFailedCount = 0;
            return Task.CompletedTask;
        }

        public Task<int> GetAccessFailedCountAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.AccessFailedCount);

        public Task<bool> GetLockoutEnabledAsync(IdentityUser user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnabled);

        public Task SetLockoutEnabledAsync(IdentityUser user, bool enabled, CancellationToken cancellationToken)
        {
            (user ?? throw new ArgumentNullException(nameof(user))).LockoutEnabled = enabled;
            return Task.CompletedTask;
        }
        #endregion

        #region IUserLoginStore
        public Task AddLoginAsync(IdentityUser user, UserLoginInfo login, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            ArgumentNullException.ThrowIfNull(login);

            IdentityUserLogin<string> userLogin = new();
            userLogin.UserId = user.Id;
            userLogin.LoginProvider = login.LoginProvider;
            userLogin.ProviderKey = login.ProviderKey;
            userLogin.ProviderDisplayName = login.ProviderDisplayName;
            return AddLoginCoreAsync(userLogin, cancellationToken);
        }

        /// <summary>
        /// The write behind <see cref="AddLoginAsync"/>, surfacing the <see cref="IdentityResult"/>
        /// that <see cref="IUserLoginStore{TUser}"/>'s <c>Task</c>-returning signature discards.
        /// </summary>
        internal async Task<IdentityResult> AddLoginCoreAsync(IdentityUserLogin<string> userLogin, CancellationToken cancellationToken)
        {
            try
            {
                await userLogins.CreateAsync(IdentityUserLoginModel.FromIdentity(userLogin), cancellationToken);
                return IdentityResult.Success;
            }
            catch (DuplicateKeyException ex)
            {
                // Unlike the user-role, user-claim, role-claim and user-token rows, a login must
                // NOT be upserted: the owning UserId lives in the row's VALUE, not its key
                // ({provider}|{hash(providerKey)}). An upsert here would silently reassign
                // ownership of an external identity to whoever writes last, and every subsequent
                // sign-in with that provider key would resolve to them.
                return IdentityResult.Failed(new IdentityError { Code = "DuplicateKey", Description = ex.Message });
            }
        }

        public Task RemoveLoginAsync(IdentityUser user, string loginProvider, string providerKey, CancellationToken cancellationToken) =>
            RemoveLoginCoreAsync(user ?? throw new ArgumentNullException(nameof(user)), loginProvider, providerKey, cancellationToken);

        /// <summary>
        /// The delete behind <see cref="RemoveLoginAsync"/>, surfacing the <see cref="IdentityResult"/>
        /// that <see cref="IUserLoginStore{TUser}"/>'s <c>Task</c>-returning signature discards.
        /// </summary>
        internal async Task<IdentityResult> RemoveLoginCoreAsync(IdentityUser user, string loginProvider, string providerKey, CancellationToken cancellationToken)
        {
            // Verify ownership before deleting: the provider key alone is not proof that this
            // login belongs to the requesting user.
            var id = IdentityKeys.UserLogin(loginProvider, providerKey);
            var existing = await userLogins.OneAsync(id, cancellationToken);
            if (existing is null || existing.UserId != user.Id)
                return IdentityResult.Failed(new IdentityError { Code = "LoginNotFound", Description = "Login not found for this user." });

            await userLogins.DeleteAsync(id, cancellationToken);
            return IdentityResult.Success;
        }

        public async Task<IList<UserLoginInfo>> GetLoginsAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            // The login row's partition is the provider, not the user, so this is a cross-partition
            // server-side predicate query rather than a partition read.
            var key = user.Id;
            var rows = await userLogins.QueryAsync(l => l.UserId == key, null, null, cancellationToken);
            return rows.Select(i => new UserLoginInfo(i.LoginProvider, i.ProviderKey, i.ProviderDisplayName)).ToList();
        }

        public async Task<IdentityUser?> FindByLoginAsync(string loginProvider, string providerKey, CancellationToken cancellationToken)
        {
            var login = await userLogins.OneAsync(IdentityKeys.UserLogin(loginProvider, providerKey), cancellationToken);
            if (login is null) return null;

            var model = await users.OneAsync(IdentityKeys.User(login.UserId), cancellationToken);
            return model?.ToIdentity();
        }

        private async Task DeleteUserLoginsAsync(string userId, CancellationToken cancellationToken)
        {
            var rows = await userLogins.QueryAsync(l => l.UserId == userId, null, null, cancellationToken);
            foreach (var row in rows)
                await userLogins.DeleteAsync(row.Id, cancellationToken);
        }
        #endregion

        #region IUserClaimStore
        public async Task<IList<Claim>> GetClaimsAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            var rows = await userClaims.QueryAsync(user.Id, cancellationToken);
            return rows.Select(r => new Claim(r.ClaimType ?? string.Empty, r.ClaimValue ?? string.Empty)).ToList();
        }

        public Task AddClaimsAsync(IdentityUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(claims);

            return Task.WhenAll(claims.Select(i =>
            {
                IdentityUserClaim<string> userClaim = new();
                userClaim.UserId = user.Id;
                userClaim.InitializeFromClaim(i);
                // Upsert: the owner is part of the key, so re-adding the same claim is idempotent.
                return userClaims.UpsertAsync(IdentityUserClaimModel.FromIdentity(userClaim), cancellationToken);
            }));
        }

        public async Task ReplaceClaimAsync(IdentityUser user, Claim claim, Claim newClaim, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            IdentityUserClaim<string> userClaim = new();
            userClaim.UserId = user.Id;
            userClaim.InitializeFromClaim(claim);
            await userClaims.DeleteAsync(IdentityUserClaimModel.FromIdentity(userClaim).Id, cancellationToken);

            userClaim.InitializeFromClaim(newClaim);
            await userClaims.UpsertAsync(IdentityUserClaimModel.FromIdentity(userClaim), cancellationToken);
        }

        public Task RemoveClaimsAsync(IdentityUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(claims);

            return Task.WhenAll(claims.Select(i =>
            {
                IdentityUserClaim<string> userClaim = new();
                userClaim.UserId = user.Id;
                userClaim.InitializeFromClaim(i);
                return userClaims.DeleteAsync(IdentityUserClaimModel.FromIdentity(userClaim).Id, cancellationToken);
            }));
        }

        public async Task<IList<IdentityUser>> GetUsersForClaimAsync(Claim claim, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(claim);

            var type = claim.Type;
            var value = claim.Value;
            var rows = await userClaims.QueryAsync(c => c.ClaimType == type && c.ClaimValue == value, null, null, cancellationToken);

            List<IdentityUser> found = [];
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var model = await users.OneAsync(IdentityKeys.User(row.UserId), cancellationToken);
                if (model is null) continue;

                found.Add(model.ToIdentity());
            }
            return found;
        }
        #endregion

        #region IUserAuthenticationTokenStore
        public async Task SetTokenAsync(IdentityUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            IdentityUserToken<string> userToken = new();
            userToken.UserId = user.Id;
            userToken.LoginProvider = loginProvider;
            userToken.Name = name;
            userToken.Value = value;
            // Upsert: this IS the update path for 2FA and recovery codes. An insert-only write made
            // them silently never update after the first one.
            await userTokens.UpsertAsync(IdentityUserTokenModel.FromIdentity(userToken), cancellationToken);
        }

        public Task RemoveTokenAsync(IdentityUser user, string loginProvider, string name, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return userTokens.DeleteAsync(IdentityKeys.UserToken(user.Id, loginProvider, name), cancellationToken);
        }

        public async Task<string?> GetTokenAsync(IdentityUser user, string loginProvider, string name, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);
            return (await userTokens.OneAsync(IdentityKeys.UserToken(user.Id, loginProvider, name), cancellationToken))?.Value;
        }
        #endregion

        #region IUserAuthenticatorKeyStore
        public Task SetAuthenticatorKeyAsync(IdentityUser user, string key, CancellationToken cancellationToken) => SetTokenAsync(user, InternalLoginProvider, AuthenticatorKeyTokenName, key, cancellationToken);

        public Task<string?> GetAuthenticatorKeyAsync(IdentityUser user, CancellationToken cancellationToken) => GetTokenAsync(user, InternalLoginProvider, AuthenticatorKeyTokenName, cancellationToken);
        #endregion

        #region IUserTwoFactorRecoveryCodeStore
        public Task ReplaceCodesAsync(IdentityUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
        {
            var mergedCodes = string.Join(";", recoveryCodes);
            return SetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, mergedCodes, cancellationToken);
        }

        public async Task<bool> RedeemCodeAsync(IdentityUser user, string code, CancellationToken cancellationToken)
        {
            var mergedCodes = await GetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, cancellationToken) ?? "";
            var splitCodes = mergedCodes.Split(';');
            if (splitCodes.Contains(code))
            {
                var updatedCodes = new List<string>(splitCodes.Where(s => s != code));
                await ReplaceCodesAsync(user, updatedCodes, cancellationToken);
                return true;
            }
            return false;
        }

        public async Task<int> CountCodesAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            var mergedCodes = await GetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, cancellationToken) ?? "";
            if (mergedCodes.Length > 0)
            {
                return mergedCodes.Split(';').Length;
            }
            return 0;
        }
        #endregion
    }
}
