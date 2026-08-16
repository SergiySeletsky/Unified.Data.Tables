using Microsoft.AspNetCore.Identity;
using Unified.Data.Tables;

namespace Unified.Data.Tables.Identity.Models
{
    /// <summary>Row in the <c>IdentityUserClaimModel</c> Azure table. Id is "{userId}|{md5(type-value)}".</summary>
    public sealed class IdentityUserClaimModel : Entity
    {
        /// <summary>Identity's own surrogate claim id. Named ClaimId because Entity already declares Id.</summary>
        public int ClaimId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string? ClaimType { get; set; }
        public string? ClaimValue { get; set; }

        public static IdentityUserClaimModel FromIdentity(IdentityUserClaim<string> claim)
        {
            ArgumentNullException.ThrowIfNull(claim);
            return new IdentityUserClaimModel
            {
                Id = IdentityKeys.UserClaim(claim.UserId, claim.ClaimType ?? string.Empty, claim.ClaimValue ?? string.Empty),
                ClaimId = claim.Id,
                UserId = claim.UserId,
                ClaimType = claim.ClaimType,
                ClaimValue = claim.ClaimValue
            };
        }

        public IdentityUserClaim<string> ToIdentity() => new()
        {
            Id = ClaimId,
            UserId = UserId,
            ClaimType = ClaimType,
            ClaimValue = ClaimValue
        };
    }
}
