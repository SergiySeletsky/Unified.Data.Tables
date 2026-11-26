using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Unified.Data.Tables.Identity.Models;
using Unified.Data.Tables.InMemory;
using Unified.Data.Tables.Sqlite;
using Unified.Data.Tables.Tests.TestSupport;

namespace Unified.Data.Tables.Identity.Tests
{
    /// <summary>The storage provider a run of the Identity suite puts under the stores.</summary>
    public enum IdentityBackend
    {
        /// <summary><see cref="InMemoryStorage{T}"/>.</summary>
        InMemory,

        /// <summary><see cref="SqliteStorage{T}"/> over a database file of the test's own, with the provider's default read cache.</summary>
        Sqlite,

        /// <summary><see cref="SqliteStorage{T}"/> over a <c>:memory:</c> database of the test's own, with the provider's default read cache.</summary>
        SqliteInMemory,

        /// <summary>
        /// <see cref="TableStorage{T}"/> over tables of the test's own on the local Azurite, with the
        /// provider's default read cache. A test on it skips itself when Azurite is not running.
        /// </summary>
        Azurite,
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
    /// The Azure provider under the Identity suite: the local Azurite, probed once per test run, and table
    /// names of one test's own. Only an emulator that is not there at all makes a test skip (see
    /// <see cref="AzuriteProbe"/>); with <c>UNIFIED_TABLES_REQUIRE_AZURITE=1</c> that fails the test instead.
    /// </summary>
    internal static class IdentityAzurite
    {
        private const string SkipReason = "Azurite (tables) is not reachable; set "
            + AzuriteConnection.Variable + " or start Azurite";

        /// <summary>The seven row models of the Identity stores, one table each.</summary>
        private static readonly Type[] Models =
        [
            typeof(IdentityUserModel), typeof(IdentityRoleModel), typeof(IdentityUserRoleModel),
            typeof(IdentityUserClaimModel), typeof(IdentityRoleClaimModel), typeof(IdentityUserLoginModel),
            typeof(IdentityUserTokenModel),
        ];

        // Identity keeps no large values, so the blob endpoint is not needed.
        private static readonly Lazy<Task<bool>> Probe =
            new(() => AzuriteProbe.ProbeAsync(AzuriteConnection.ConnectionString, blobs: false, AzuriteProbe.Required));

        // Built as AddUnifiedTableStorage(connectionString) builds its client: with the retry observation.
        private static readonly Lazy<TableServiceClient> Tables =
            new(() => new TableServiceClient(AzuriteConnection.ConnectionString, new TableClientOptions().AddRetryObservation()));

        internal static string ConnectionString => AzuriteConnection.ConnectionString;

        internal static TableServiceClient Client => Tables.Value;

        /// <summary>
        /// Skips the running test when Azurite is not there. Blocks: a test class is built by a constructor.
        /// The probe runs on the thread pool so that no continuation waits for the blocked thread.
        /// </summary>
        internal static void SkipUnlessAvailable() =>
            Assert.SkipUnless(Task.Run(() => Probe.Value).GetAwaiter().GetResult(), SkipReason);

        /// <summary>A table-name prefix no other test uses (a table name is a letter, then letters and digits).</summary>
        internal static string NewPrefix() => "id" + Guid.NewGuid().ToString("N");

        /// <summary>The table of <paramref name="model"/> under <paramref name="prefix"/>.</summary>
        internal static string TableName(string prefix, Type model) => prefix + model.Name;

        /// <summary>Deletes the tables of one test; a table the test never wrote to was never created.</summary>
        internal static void DropTables(string prefix) =>
            Task.Run(async () =>
            {
                foreach (var model in Models)
                {
                    try
                    {
                        await Client.DeleteTableAsync(TableName(prefix, model), CancellationToken.None);
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404)
                    {
                    }
                }
            }).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Builds <see cref="UserStore"/> and <see cref="RoleStore"/> over seven storages of one provider
    /// (<see cref="InMemoryStorage{T}"/>, <see cref="SqliteStorage{T}"/> over one database, or
    /// <see cref="TableStorage{T}"/> on Azurite) — the same seven <c>IStorage&lt;T&gt;</c> dependencies the
    /// DI container supplies in production. The raw storages stay exposed so a test can assert what was
    /// actually persisted rather than only what the store reports back.
    /// </summary>
    internal sealed class IdentityStoreFixture : IDisposable
    {
        private readonly SqliteStorageAccount? _account;
        private readonly string? _directory;
        private readonly string? _tablePrefix;
        private readonly MemoryCache? _cache;

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
            switch (backend)
            {
                case IdentityBackend.Sqlite:
                    // A file, never inside the source tree: what a development host runs on.
                    _directory = Path.Combine(Path.GetTempPath(), "udt-identity-tests", Guid.NewGuid().ToString("N"));
                    _account = new SqliteStorageAccount(Path.Combine(_directory, "identity.db"));
                    break;
                case IdentityBackend.SqliteInMemory:
                    _account = new SqliteStorageAccount(":memory:");
                    break;
                case IdentityBackend.Azurite:
                    IdentityAzurite.SkipUnlessAvailable();
                    _tablePrefix = IdentityAzurite.NewPrefix();
                    _cache = new MemoryCache(new MemoryCacheOptions());
                    break;
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
            if (_tablePrefix is { } prefix)
            {
                var options = new UnifiedTableStorageOptions { TableNameResolver = type => IdentityAzurite.TableName(prefix, type) };
                var azure = new TableStorage<T>(IdentityAzurite.Client, _cache!, NullLogger<TableStorage<T>>.Instance, authorizer: null, options);
                // On the thread pool, so that no continuation waits for the thread blocked here.
                return new IdentityRows<T>(azure, () => Task.Run(() => azure.CountAsync()).GetAwaiter().GetResult());
            }

            if (_account is null)
            {
                var memory = new InMemoryStorage<T>();
                return new IdentityRows<T>(memory, () => memory.Count);
            }

            var sqlite = new SqliteStorage<T>(_account);
            // SQLite does its work on the calling thread, so the count is there when the call returns.
            return new IdentityRows<T>(sqlite, () => sqlite.CountAsync().GetAwaiter().GetResult());
        }

        /// <summary>
        /// Closes the SQLite database of the fixture and removes its file, or deletes the fixture's tables
        /// on Azurite; nothing to do for the in-memory one.
        /// </summary>
        public void Dispose()
        {
            _account?.Dispose();
            if (_directory is not null && Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
            if (_tablePrefix is not null)
                IdentityAzurite.DropTables(_tablePrefix);
            _cache?.Dispose();
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
