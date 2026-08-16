using Unified.Data.Tables.Identity.Models;
using Unified.Data.Tables;

namespace Unified.Data.Tables.Identity
{
    internal sealed class UserOnlyStore : UserStoreBase
    {
        public UserOnlyStore(
            IStorage<IdentityUserModel> users,
            IStorage<IdentityUserLoginModel> userLogins,
            IStorage<IdentityUserClaimModel> userClaims,
            IStorage<IdentityUserRoleModel> userRoles,
            IStorage<IdentityUserTokenModel> userTokens)
            : base(users, userLogins, userClaims, userRoles, userTokens)
        {
        }

    }
}
