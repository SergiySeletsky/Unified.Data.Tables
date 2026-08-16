using System.Security.Cryptography;
using System.Text;

namespace Unified.Data.Tables.Identity
{
    /// <summary>
    /// Composes <see cref="Unified.Data.Tables.Entity.Id"/> values for the identity row models.
    /// Ids are "{PartitionKey}|{RowKey}", split on the FIRST '|'.
    ///
    /// Components that are GUIDs or known constants are used verbatim. Components carrying
    /// unbounded user- or provider-supplied text are MD5-hashed, because Azure Table Storage
    /// rejects '/', '\', '#', '?' and control characters in PartitionKey/RowKey.
    /// </summary>
    public static class IdentityKeys
    {
        /// <summary>Partition holding user rows in <see cref="Models.IdentityUserModel"/>.</summary>
        public const string UserPartition = "User";

        /// <summary>Partition holding role rows in <see cref="Models.IdentityRoleModel"/>.</summary>
        public const string RolePartition = "Role";

        public static string User(string userId) => $"{UserPartition}|{userId}";

        public static string Role(string roleId) => $"{RolePartition}|{roleId}";

        public static string UserRole(string userId, string roleId) => $"{userId}|{roleId}";

        public static string UserClaim(string userId, string claimType, string claimValue) =>
            $"{userId}|{Hash($"{claimType}-{claimValue}")}";

        public static string RoleClaim(string roleId, string claimType, string claimValue) =>
            $"{roleId}|{Hash($"{claimType}-{claimValue}")}";

        public static string UserLogin(string loginProvider, string providerKey) =>
            $"{loginProvider}|{Hash(providerKey)}";

        public static string UserToken(string userId, string loginProvider, string name) =>
            $"{userId}|{Hash($"{loginProvider}-{name}")}";

        /// <summary>
        /// Lowercase hex MD5. Key derivation only — not a security control. Independent of the
        /// legacy uppercase-hex hash format used by some earlier Azure Table Storage identity layers.
        /// </summary>
        public static string Hash(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            byte[] inputBytes = Encoding.UTF8.GetBytes(value);
#pragma warning disable CA5351, S4790 // MD5 is used for non-security hash keys, not cryptographic purposes
            byte[] hashBytes = MD5.HashData(inputBytes);
#pragma warning restore CA5351, S4790
            return string.Join(string.Empty, hashBytes.Select(i => i.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
