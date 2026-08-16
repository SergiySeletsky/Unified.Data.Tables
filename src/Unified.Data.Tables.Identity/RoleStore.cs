using Unified.Data.Tables.Identity.Models;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace Unified.Data.Tables.Identity
{
    internal sealed class RoleStore :
        IRoleClaimStore<IdentityRole>,
        IQueryableRoleStore<IdentityRole>
    {
        private readonly IStorage<IdentityRoleModel> roles;
        private readonly IStorage<IdentityRoleClaimModel> roleClaims;
        private readonly IStorage<IdentityUserRoleModel> userRoles;

        public RoleStore(
            IStorage<IdentityRoleModel> roles,
            IStorage<IdentityRoleClaimModel> roleClaims,
            IStorage<IdentityUserRoleModel> userRoles)
        {
            this.roles = roles ?? throw new ArgumentNullException(nameof(roles));
            this.roleClaims = roleClaims ?? throw new ArgumentNullException(nameof(roleClaims));
            this.userRoles = userRoles ?? throw new ArgumentNullException(nameof(userRoles));
        }

        #region IQueryableRoleStore
        // Synchronous by force: IQueryableRoleStore exposes a property, not a Task.
        public IQueryable<IdentityRole> Roles =>
            roles.QueryAsync(IdentityKeys.RolePartition).GetAwaiter().GetResult()
                 .Select(r => r.ToIdentity()).ToList().AsQueryable();
        #endregion

        #region IRoleStore
        public async Task<IdentityResult> CreateAsync(IdentityRole role, CancellationToken cancellationToken)
        {
            try
            {
                await roles.CreateAsync(IdentityRoleModel.FromIdentity(role), cancellationToken);
                return IdentityResult.Success;
            }
            catch (DuplicateKeyException ex)
            {
                return IdentityResult.Failed(new IdentityError { Code = "DuplicateKey", Description = ex.Message });
            }
        }

        public async Task<IdentityResult> DeleteAsync(IdentityRole role, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(role);

            // Cascade first, role row last.
            await Task.WhenAll(
                roleClaims.DeletePartitionAsync(role.Id, cancellationToken),
                DeleteRoleUsersAsync(role.Id, cancellationToken)
            );

            await roles.DeleteAsync(IdentityKeys.Role(role.Id), cancellationToken);
            return IdentityResult.Success;
        }

        private async Task DeleteRoleUsersAsync(string roleId, CancellationToken cancellationToken)
        {
            // RoleId is the row key, not the partition, so the links must be found by query.
            var rows = await userRoles.QueryAsync(r => r.RoleId == roleId, null, null, cancellationToken);
            foreach (var row in rows)
                await userRoles.DeleteAsync(row.Id, cancellationToken);
        }

        public void Dispose() { }

        public async Task<IdentityRole?> FindByIdAsync(string roleId, CancellationToken cancellationToken) =>
            (await roles.OneAsync(IdentityKeys.Role(roleId), cancellationToken))?.ToIdentity();

        public async Task<IdentityRole?> FindByNameAsync(string normalizedRoleName, CancellationToken cancellationToken)
        {
            var matches = await roles.QueryAsync(
                r => r.NormalizedName == normalizedRoleName, IdentityKeys.RolePartition, 1, cancellationToken);
            return matches.Count > 0 ? matches[0].ToIdentity() : null;
        }

        public Task<string?> GetNormalizedRoleNameAsync(IdentityRole role, CancellationToken cancellationToken) => Task.FromResult(role.NormalizedName);

        public Task<string> GetRoleIdAsync(IdentityRole role, CancellationToken cancellationToken) => Task.FromResult(role.Id);

        public Task<string?> GetRoleNameAsync(IdentityRole role, CancellationToken cancellationToken) => Task.FromResult(role.Name);

        public Task SetNormalizedRoleNameAsync(IdentityRole role, string? normalizedName, CancellationToken cancellationToken)
        {
            (role ?? throw new ArgumentNullException(nameof(role))).NormalizedName = normalizedName;
            return Task.CompletedTask;
        }

        public Task SetRoleNameAsync(IdentityRole role, string? roleName, CancellationToken cancellationToken)
        {
            (role ?? throw new ArgumentNullException(nameof(role))).Name = roleName;
            return Task.CompletedTask;
        }

        public async Task<IdentityResult> UpdateAsync(IdentityRole role, CancellationToken cancellationToken)
        {
            // Upsert, i.e. a full replace rather than a merge, and keyed by role id, so a rename
            // updates the existing row instead of minting a second one.
            await roles.UpsertAsync(IdentityRoleModel.FromIdentity(role), cancellationToken);
            return IdentityResult.Success;
        }
        #endregion

        #region IRoleClaimStore
        public Task AddClaimAsync(IdentityRole role, Claim claim, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(role);

            IdentityRoleClaim<string> roleClaim = new() { RoleId = role.Id };
            roleClaim.InitializeFromClaim(claim);
            // Upsert: the owner is part of the key, so re-adding the same claim is idempotent.
            return roleClaims.UpsertAsync(IdentityRoleClaimModel.FromIdentity(roleClaim), cancellationToken);
        }

        public async Task<IList<Claim>> GetClaimsAsync(IdentityRole role, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(role);

            var rows = await roleClaims.QueryAsync(role.Id, cancellationToken);
            return rows.Select(r => new Claim(r.ClaimType ?? string.Empty, r.ClaimValue ?? string.Empty)).ToList();
        }

        public Task RemoveClaimAsync(IdentityRole role, Claim claim, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(role);

            IdentityRoleClaim<string> roleClaim = new() { RoleId = role.Id };
            roleClaim.InitializeFromClaim(claim);
            return roleClaims.DeleteAsync(IdentityRoleClaimModel.FromIdentity(roleClaim).Id, cancellationToken);
        }
        #endregion
    }
}
