using Microsoft.AspNetCore.Identity;

namespace Unified.Data.Tables.Identity.Models
{
    /// <summary>Row in the <c>IdentityRoleModel</c> Azure table. Id is "Role|{roleId}".</summary>
    public sealed class IdentityRoleModel : Entity
    {
        public string RoleId { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? NormalizedName { get; set; }
        public string? ConcurrencyStamp { get; set; }

        public static IdentityRoleModel FromIdentity(IdentityRole role)
        {
            ArgumentNullException.ThrowIfNull(role);
            return new IdentityRoleModel
            {
                Id = IdentityKeys.Role(role.Id),
                RoleId = role.Id,
                Name = role.Name,
                NormalizedName = role.NormalizedName,
                ConcurrencyStamp = role.ConcurrencyStamp
            };
        }

        public IdentityRole ToIdentity() => new()
        {
            Id = RoleId,
            Name = Name,
            NormalizedName = NormalizedName,
            ConcurrencyStamp = ConcurrencyStamp
        };
    }
}
