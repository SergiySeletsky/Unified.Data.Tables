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
            IStorage<IdentityUserRoleModel> userRoles,
            IdentityErrorDescriber? describer = null)
        {
            this.roles = roles ?? throw new ArgumentNullException(nameof(roles));
            this.roleClaims = roleClaims ?? throw new ArgumentNullException(nameof(roleClaims));
            this.userRoles = userRoles ?? throw new ArgumentNullException(nameof(userRoles));
            ErrorDescriber = describer ?? new IdentityErrorDescriber();
        }

        /// <summary>
        /// Describes the errors this store reports, as the EF Core stores do: the describer Identity
        /// registers (so a localized one applies), or a default one.
        /// </summary>
        public IdentityErrorDescriber ErrorDescriber { get; }

        private IdentityResult ConcurrencyFailure() => IdentityResult.Failed(ErrorDescriber.ConcurrencyFailure());

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

        /// <summary>
        /// Deletes the role, its claims and its user links when the stored row still carries the caller's
        /// <see cref="IdentityRole{TKey}.ConcurrencyStamp"/>. When another writer changed the row since the
        /// caller read it, or the row is gone, nothing is deleted and the result is
        /// <see cref="IdentityErrorDescriber.ConcurrencyFailure"/>.
        /// </summary>
        /// <remarks>
        /// As for a user: <see cref="IStorage{T}"/> has no conditional delete, so before the cascade the row
        /// is rewritten, on condition of the ETag it was read with, with a fresh stamp no caller holds. A
        /// crash or a cancellation between that claim and the final row delete leaves the role with a new
        /// stamp (and possibly without some of its claims and links); a delete retried after re-reading the
        /// role finishes the job.
        /// </remarks>
        public async Task<IdentityResult> DeleteAsync(IdentityRole role, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(role);
            cancellationToken.ThrowIfCancellationRequested();

            var id = IdentityKeys.Role(role.Id);
            var current = await roles.OneAsync(id, cancellationToken);
            if (current is null || !ConcurrencyStamps.Matches(current.ConcurrencyStamp, role.ConcurrencyStamp))
                return ConcurrencyFailure();

            current.ConcurrencyStamp = ConcurrencyStamps.NewStamp();
            if (!await ConcurrencyStamps.TryReplaceAsync(roles, current, current.ETag, cancellationToken))
                return ConcurrencyFailure();

            // Cascade first, role row last.
            await Task.WhenAll(
                roleClaims.DeletePartitionAsync(role.Id, cancellationToken),
                DeleteRoleUsersAsync(role.Id, cancellationToken)
            );

            await roles.DeleteAsync(id, cancellationToken);
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

        /// <summary>
        /// Writes the role when the stored row still carries the caller's
        /// <see cref="IdentityRole{TKey}.ConcurrencyStamp"/>, and gives it a fresh stamp. When another writer
        /// changed the row since the caller read it, or the row is gone, nothing is written and the result is
        /// <see cref="IdentityErrorDescriber.ConcurrencyFailure"/>: re-read the role and repeat the change. On
        /// any failure <paramref name="role"/> keeps the stamp it came with.
        /// </summary>
        public async Task<IdentityResult> UpdateAsync(IdentityRole role, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(role);
            cancellationToken.ThrowIfCancellationRequested();

            var current = await roles.OneAsync(IdentityKeys.Role(role.Id), cancellationToken);
            if (current is null || !ConcurrencyStamps.Matches(current.ConcurrencyStamp, role.ConcurrencyStamp))
                return ConcurrencyFailure();

            var expected = role.ConcurrencyStamp;
            var written = false;
            role.ConcurrencyStamp = ConcurrencyStamps.NewStamp();
            try
            {
                // A full replace keyed by role id, so a rename updates the existing row instead of
                // minting a second one; conditional on the ETag of the read above.
                var model = IdentityRoleModel.FromIdentity(role);
                model.CreatedAt = current.CreatedAt;
                written = await ConcurrencyStamps.TryReplaceAsync(roles, model, current.ETag, cancellationToken);
                return written ? IdentityResult.Success : ConcurrencyFailure();
            }
            finally
            {
                if (!written)
                    role.ConcurrencyStamp = expected;
            }
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
