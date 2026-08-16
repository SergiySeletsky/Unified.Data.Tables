using Microsoft.AspNetCore.Identity;
using Unified.Data.Tables;

namespace Unified.Data.Tables.Identity.Models
{
    /// <summary>Row in the <c>IdentityUserRoleModel</c> Azure table. Id is "{userId}|{roleId}".</summary>
    public sealed class IdentityUserRoleModel : Entity
    {
        public string UserId { get; set; } = string.Empty;
        public string RoleId { get; set; } = string.Empty;

        public static IdentityUserRoleModel FromIdentity(IdentityUserRole<string> userRole)
        {
            ArgumentNullException.ThrowIfNull(userRole);
            return new IdentityUserRoleModel
            {
                Id = IdentityKeys.UserRole(userRole.UserId, userRole.RoleId),
                UserId = userRole.UserId,
                RoleId = userRole.RoleId
            };
        }

        public IdentityUserRole<string> ToIdentity() => new() { UserId = UserId, RoleId = RoleId };
    }
}
