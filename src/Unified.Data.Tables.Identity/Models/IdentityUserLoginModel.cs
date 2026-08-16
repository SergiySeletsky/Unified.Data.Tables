using Microsoft.AspNetCore.Identity;
using Unified.Data.Tables;

namespace Unified.Data.Tables.Identity.Models
{
    /// <summary>Row in the <c>IdentityUserLoginModel</c> Azure table. Id is "{loginProvider}|{md5(providerKey)}".</summary>
    public sealed class IdentityUserLoginModel : Entity
    {
        public string UserId { get; set; } = string.Empty;
        public string LoginProvider { get; set; } = string.Empty;
        public string ProviderKey { get; set; } = string.Empty;
        public string? ProviderDisplayName { get; set; }

        public static IdentityUserLoginModel FromIdentity(IdentityUserLogin<string> login)
        {
            ArgumentNullException.ThrowIfNull(login);
            return new IdentityUserLoginModel
            {
                Id = IdentityKeys.UserLogin(login.LoginProvider, login.ProviderKey),
                UserId = login.UserId,
                LoginProvider = login.LoginProvider,
                ProviderKey = login.ProviderKey,
                ProviderDisplayName = login.ProviderDisplayName
            };
        }

        public IdentityUserLogin<string> ToIdentity() => new()
        {
            UserId = UserId,
            LoginProvider = LoginProvider,
            ProviderKey = ProviderKey,
            ProviderDisplayName = ProviderDisplayName
        };
    }
}
