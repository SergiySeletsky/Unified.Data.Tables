using Microsoft.AspNetCore.Identity;
using Unified.Data.Tables;

namespace Unified.Data.Tables.Identity.Models
{
    /// <summary>Row in the <c>IdentityUserTokenModel</c> Azure table. Id is "{userId}|{md5(provider-name)}".</summary>
    public sealed class IdentityUserTokenModel : Entity
    {
        public string UserId { get; set; } = string.Empty;
        public string LoginProvider { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Value { get; set; }

        public static IdentityUserTokenModel FromIdentity(IdentityUserToken<string> token)
        {
            ArgumentNullException.ThrowIfNull(token);
            return new IdentityUserTokenModel
            {
                Id = IdentityKeys.UserToken(token.UserId, token.LoginProvider, token.Name),
                UserId = token.UserId,
                LoginProvider = token.LoginProvider,
                Name = token.Name,
                Value = token.Value
            };
        }

        public IdentityUserToken<string> ToIdentity() => new()
        {
            UserId = UserId,
            LoginProvider = LoginProvider,
            Name = Name,
            Value = Value
        };
    }
}
