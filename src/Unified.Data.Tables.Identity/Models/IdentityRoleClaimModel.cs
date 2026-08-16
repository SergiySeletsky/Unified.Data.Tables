using Microsoft.AspNetCore.Identity;
using Unified.Data.Tables;

namespace Unified.Data.Tables.Identity.Models
{
    /// <summary>Row in the <c>IdentityRoleClaimModel</c> Azure table. Id is "{roleId}|{md5(type-value)}".</summary>
    public sealed class IdentityRoleClaimModel : Entity
    {
        public int ClaimId { get; set; }
        public string RoleId { get; set; } = string.Empty;
        public string? ClaimType { get; set; }
        public string? ClaimValue { get; set; }

        public static IdentityRoleClaimModel FromIdentity(IdentityRoleClaim<string> claim)
        {
            ArgumentNullException.ThrowIfNull(claim);
            return new IdentityRoleClaimModel
            {
                Id = IdentityKeys.RoleClaim(claim.RoleId, claim.ClaimType ?? string.Empty, claim.ClaimValue ?? string.Empty),
                ClaimId = claim.Id,
                RoleId = claim.RoleId,
                ClaimType = claim.ClaimType,
                ClaimValue = claim.ClaimValue
            };
        }

        public IdentityRoleClaim<string> ToIdentity() => new()
        {
            Id = ClaimId,
            RoleId = RoleId,
            ClaimType = ClaimType,
            ClaimValue = ClaimValue
        };
    }
}
