using Microsoft.AspNetCore.Identity;

namespace Unified.Data.Tables.Identity.Models
{
    /// <summary>Row in the <c>IdentityUserModel</c> Azure table. Id is "User|{userId}".</summary>
    public sealed class IdentityUserModel : Entity
    {
        public string UserId { get; set; } = string.Empty;
        public string? UserName { get; set; }
        public string? NormalizedUserName { get; set; }
        public string? Email { get; set; }
        public string? NormalizedEmail { get; set; }
        public bool EmailConfirmed { get; set; }
        public string? PasswordHash { get; set; }
        public string? SecurityStamp { get; set; }
        public string? ConcurrencyStamp { get; set; }
        public string? PhoneNumber { get; set; }
        public bool PhoneNumberConfirmed { get; set; }
        public bool TwoFactorEnabled { get; set; }
        public DateTimeOffset? LockoutEnd { get; set; }
        public bool LockoutEnabled { get; set; }
        public int AccessFailedCount { get; set; }

        public static IdentityUserModel FromIdentity(IdentityUser user)
        {
            ArgumentNullException.ThrowIfNull(user);
            return new IdentityUserModel
            {
                Id = IdentityKeys.User(user.Id),
                UserId = user.Id,
                UserName = user.UserName,
                NormalizedUserName = user.NormalizedUserName,
                Email = user.Email,
                NormalizedEmail = user.NormalizedEmail,
                EmailConfirmed = user.EmailConfirmed,
                PasswordHash = user.PasswordHash,
                SecurityStamp = user.SecurityStamp,
                ConcurrencyStamp = user.ConcurrencyStamp,
                PhoneNumber = user.PhoneNumber,
                PhoneNumberConfirmed = user.PhoneNumberConfirmed,
                TwoFactorEnabled = user.TwoFactorEnabled,
                LockoutEnd = user.LockoutEnd,
                LockoutEnabled = user.LockoutEnabled,
                AccessFailedCount = user.AccessFailedCount
            };
        }

        public IdentityUser ToIdentity() => new()
        {
            Id = UserId,
            UserName = UserName,
            NormalizedUserName = NormalizedUserName,
            Email = Email,
            NormalizedEmail = NormalizedEmail,
            EmailConfirmed = EmailConfirmed,
            PasswordHash = PasswordHash,
            SecurityStamp = SecurityStamp,
            ConcurrencyStamp = ConcurrencyStamp,
            PhoneNumber = PhoneNumber,
            PhoneNumberConfirmed = PhoneNumberConfirmed,
            TwoFactorEnabled = TwoFactorEnabled,
            LockoutEnd = LockoutEnd,
            LockoutEnabled = LockoutEnabled,
            AccessFailedCount = AccessFailedCount
        };
    }
}
