using Unified.Data.Tables.Identity.Models;
using Microsoft.AspNetCore.Identity;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Identity.Tests
{
    /// <summary>
    /// Builds <see cref="UserStore"/> and <see cref="RoleStore"/> over seven
    /// <see cref="InMemoryStorage{T}"/> instances — the same seven <c>IStorage&lt;T&gt;</c>
    /// dependencies the DI container supplies in production. The raw storages stay exposed so a
    /// test can assert what was actually persisted rather than only what the store reports back.
    /// </summary>
    internal sealed class IdentityStoreFixture
    {
        public InMemoryStorage<IdentityUserModel> UserRows { get; } = new();
        public InMemoryStorage<IdentityRoleModel> RoleRows { get; } = new();
        public InMemoryStorage<IdentityUserRoleModel> UserRoleRows { get; } = new();
        public InMemoryStorage<IdentityUserClaimModel> UserClaimRows { get; } = new();
        public InMemoryStorage<IdentityRoleClaimModel> RoleClaimRows { get; } = new();
        public InMemoryStorage<IdentityUserLoginModel> UserLoginRows { get; } = new();
        public InMemoryStorage<IdentityUserTokenModel> UserTokenRows { get; } = new();

        public UserStore UserStore { get; }

        public RoleStore RoleStore { get; }

        public IdentityStoreFixture()
        {
            UserStore = new UserStore(UserRows, RoleRows, UserLoginRows, UserClaimRows, UserRoleRows, UserTokenRows);
            RoleStore = new RoleStore(RoleRows, RoleClaimRows, UserRoleRows);
        }

        public IUserRoleStore<IdentityUser> UserRoleStore => UserStore;

        /// <summary>Seeds a role row directly, bypassing the store under test.</summary>
        public Task SeedRoleAsync(string roleId, string name, CancellationToken cancellationToken) =>
            RoleRows.UpsertAsync(
                IdentityRoleModel.FromIdentity(new IdentityRole(name) { Id = roleId, NormalizedName = name.ToUpperInvariant() }),
                cancellationToken);

        /// <summary>
        /// Reads a user through the store, as UserManager hands one to a delete or update: since 0.9.2 a
        /// hand-built object carries no stored ConcurrencyStamp and is refused.
        /// </summary>
        public async Task<IdentityUser> ReadUserAsync(string userId, CancellationToken cancellationToken) =>
            await UserStore.FindByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException($"No user {userId} was seeded.");

        /// <summary>Reads a role through the store; see <see cref="ReadUserAsync"/>.</summary>
        public async Task<IdentityRole> ReadRoleAsync(string roleId, CancellationToken cancellationToken) =>
            await RoleStore.FindByIdAsync(roleId, cancellationToken)
            ?? throw new InvalidOperationException($"No role {roleId} was seeded.");

        /// <summary>Seeds a user row directly, bypassing the store under test.</summary>
        public Task SeedUserAsync(string userId, string userName, CancellationToken cancellationToken) =>
            UserRows.UpsertAsync(
                IdentityUserModel.FromIdentity(new IdentityUser { Id = userId, UserName = userName }),
                cancellationToken);
    }
}
