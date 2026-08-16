using Unified.Data.Tables.Identity.Models;
using Microsoft.AspNetCore.Identity;

namespace Unified.Data.Tables.Identity
{
    internal sealed class UserStore : UserStoreBase,
        IUserRoleStore<IdentityUser>
    {
        private readonly IStorage<IdentityUserModel> users;
        private readonly IStorage<IdentityRoleModel> roles;
        private readonly IStorage<IdentityUserRoleModel> userRoles;

        public UserStore(
            IStorage<IdentityUserModel> users,
            IStorage<IdentityRoleModel> roles,
            IStorage<IdentityUserLoginModel> userLogins,
            IStorage<IdentityUserClaimModel> userClaims,
            IStorage<IdentityUserRoleModel> userRoles,
            IStorage<IdentityUserTokenModel> userTokens)
            : base(users, userLogins, userClaims, userRoles, userTokens)
        {
            this.users = users ?? throw new ArgumentNullException(nameof(users));
            this.roles = roles ?? throw new ArgumentNullException(nameof(roles));
            this.userRoles = userRoles ?? throw new ArgumentNullException(nameof(userRoles));
        }

        private async Task<IdentityRoleModel?> FindRoleByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken)
        {
            var matches = await roles.QueryAsync(
                r => r.NormalizedName == normalizedName, IdentityKeys.RolePartition, 1, cancellationToken);
            return matches.Count > 0 ? matches[0] : null;
        }

        #region IUserRoleStore
        public async Task AddToRoleAsync(IdentityUser user, string roleName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            var role = await FindRoleByNormalizedNameAsync(roleName, cancellationToken);
            // Unlike the three query-shaped members below, this is a mutation that CANNOT be
            // satisfied. UserManager.AddToRoleAsync does not validate role existence itself, so a
            // silent return here surfaces as IdentityResult.Success and a controller assigning roles
            // answers 200 OK having assigned nothing. EF Core's own UserStore throws here; match it.
            if (role is null)
                throw new InvalidOperationException($"Role {roleName} does not exist.");

            // Upsert, not create: assigning a role a user already has must be idempotent. The
            // owner is part of the key ({userId}|{roleId}), so nothing can be reassigned.
            await userRoles.UpsertAsync(
                IdentityUserRoleModel.FromIdentity(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.RoleId }),
                cancellationToken);
        }

        public async Task RemoveFromRoleAsync(IdentityUser user, string roleName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            var role = await FindRoleByNormalizedNameAsync(roleName, cancellationToken);
            if (role is null) return;

            await userRoles.DeleteAsync(IdentityKeys.UserRole(user.Id, role.RoleId), cancellationToken);
        }

        public async Task<IList<string>> GetRolesAsync(IdentityUser user, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            // UserId is the partition of the user-role row, so this is a partition read.
            var roleIds = (await userRoles.QueryAsync(user.Id, cancellationToken)).Select(i => i.RoleId).ToList();
            if (roleIds.Count > 0)
            {
                var all = await roles.QueryAsync(IdentityKeys.RolePartition, cancellationToken);
                return all.Join(roleIds, role => role.RoleId, id => id, (role, id) => role.Name!).ToList();
            }
            else
                return [];
        }

        public async Task<bool> IsInRoleAsync(IdentityUser user, string roleName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(user);

            var role = await FindRoleByNormalizedNameAsync(roleName, cancellationToken);
            if (role is null) return false;

            return (await userRoles.QueryAsync(user.Id, cancellationToken)).Any(i => i.RoleId == role.RoleId);
        }

        public async Task<IList<IdentityUser>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken)
        {
            var role = await FindRoleByNormalizedNameAsync(roleName, cancellationToken);
            if (role is null) return [];

            // RoleId is the row key, not the partition, so this is a cross-partition server-side
            // predicate query.
            var roleId = role.RoleId;
            var links = await userRoles.QueryAsync(r => r.RoleId == roleId, null, null, cancellationToken);

            List<IdentityUser> found = [];
            foreach (var link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var model = await users.OneAsync(IdentityKeys.User(link.UserId), cancellationToken);
                if (model is not null) found.Add(model.ToIdentity());
            }
            return found;
        }
        #endregion
    }
}
