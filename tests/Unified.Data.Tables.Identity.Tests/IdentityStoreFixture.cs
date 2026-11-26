﻿using Unified.Data.Tables.Identity.Models;
using Microsoft.AspNetCore.Identity;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Sqlite;

namespace Unified.Data.Tables.Identity.Tests
{
    /// <summary>The storage provider a run of the Identity suite puts under the stores.</summary>
    public enum IdentityBackend
    {
        /// <summary><see cref="InMemoryStorage{T}"/>.</summary>
        InMemory,

        /// <summary><see cref="SqliteStorage{T}"/> over a database file of the test's own, with the provider's default read cache.</summary>
        Sqlite,
    }

    /// <summary>
    /// The rows of one Identity table as a test sees them: the <see cref="IStorage{T}"/> the stores under
    /// test are given, and how many rows it holds, whichever provider keeps them.
    /// </summary>
    internal sealed class IdentityRows<T>(IStorage<T> inner, Func<int> count) : DelegatingStorage<T>(inner)
        where T : class, IEntity, new()
    {
        /// <summary>Number of rows currently stored.</summary>
        public int Count => count();
    }

    /// <summary>
    /// Builds <see cref="UserStore"/> and <see cref="RoleStore"/> over seven storages of one provider
    /// (<see cref="InMemoryStorage{T}"/>, or <see cref="SqliteStorage{T}"/> over one database file) — the
    /// same seven <c>IStorage&lt;T&gt;</c> dependencies the DI container supplies in production. The raw
    /// storages stay exposed so a test can assert what was actually persisted rather than only what the
    /// store reports back.
    /// </summary>
    internal sealed class IdentityStoreFixture : IDisposable
    {
        private readonly SqliteStorageAccount? _account;
        private readonly string? _directory;

        public IdentityRows<IdentityUserModel> UserRows { get; }
        public IdentityRows<IdentityRoleModel> RoleRows { get; }
        public IdentityRows<IdentityUserRoleModel> UserRoleRows { get; }
        public IdentityRows<IdentityUserClaimModel> UserClaimRows { get; }
        public IdentityRows<IdentityRoleClaimModel> RoleClaimRows { get; }
        public IdentityRows<IdentityUserLoginModel> UserLoginRows { get; }
        public IdentityRows<IdentityUserTokenModel> UserTokenRows { get; }

        public UserStore UserStore { get; }

        public RoleStore RoleStore { get; }

        public IdentityStoreFixture(IdentityBackend backend = IdentityBackend.InMemory)
        {
            if (backend == IdentityBackend.Sqlite)
            {
                // A file, not :memory:, and never inside the source tree: what a development host runs on.
                _directory = Path.Combine(Path.GetTempPath(), "udt-identity-tests", Guid.NewGuid().ToString("N"));
                _account = new SqliteStorageAccount(Path.Combine(_directory, "identity.db"));
            }

            UserRows = Rows<IdentityUserModel>();
            RoleRows = Rows<IdentityRoleModel>();
            UserRoleRows = Rows<IdentityUserRoleModel>();
            UserClaimRows = Rows<IdentityUserClaimModel>();
            RoleClaimRows = Rows<IdentityRoleClaimModel>();
            UserLoginRows = Rows<IdentityUserLoginModel>();
            UserTokenRows = Rows<IdentityUserTokenModel>();
            UserStore = new UserStore(UserRows, RoleRows, UserLoginRows, UserClaimRows, UserRoleRows, UserTokenRows);
            RoleStore = new RoleStore(RoleRows, RoleClaimRows, UserRoleRows);
        }

        private IdentityRows<T> Rows<T>() where T : class, IEntity, new()
        {
            if (_account is null)
            {
                var memory = new InMemoryStorage<T>();
                return new IdentityRows<T>(memory, () => memory.Count);
            }

            var sqlite = new SqliteStorage<T>(_account);
            // SQLite does its work on the calling thread, so the count is there when the call returns.
            return new IdentityRows<T>(sqlite, () => sqlite.CountAsync().GetAwaiter().GetResult());
        }

        /// <summary>Closes the SQLite database of the fixture and removes its file; nothing to do for the in-memory one.</summary>
        public void Dispose()
        {
            _account?.Dispose();
            if (_directory is not null && Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
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
