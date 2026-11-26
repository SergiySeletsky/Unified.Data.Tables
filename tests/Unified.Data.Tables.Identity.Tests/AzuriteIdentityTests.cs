using Microsoft.Extensions.DependencyInjection;

namespace Unified.Data.Tables.Identity.Tests;

// The Identity suite on the Azure provider: every store test of this assembly once more, over
// TableStorage<T> on the local Azurite with the provider's default read cache, each test on tables of
// its own that are deleted when it ends. Each class below inherits the tests of the class it derives
// from and only names the provider; a test added to a base class runs on Azurite as well. When Azurite
// is not running every test here skips itself (with UNIFIED_TABLES_REQUIRE_AZURITE=1, as CI sets it,
// it fails instead); see AzuriteProbe.

public sealed class AzuriteIdentityClaimStoreTests() : IdentityClaimStoreTests(IdentityBackend.Azurite);

public sealed class AzuriteIdentityConcurrencyTests() : IdentityConcurrencyTests(IdentityBackend.Azurite);

public sealed class AzuriteIdentityLoginAndTokenStoreTests() : IdentityLoginAndTokenStoreTests(IdentityBackend.Azurite);

public sealed class AzuriteIdentityRoleStoreTests() : IdentityRoleStoreTests(IdentityBackend.Azurite);

public sealed class AzuriteIdentityUserRoleLinkTests() : IdentityUserRoleLinkTests(IdentityBackend.Azurite);

public sealed class AzuriteIdentityUserStoreRoleTests() : IdentityUserStoreRoleTests(IdentityBackend.Azurite);

public sealed class AzuriteIdentityUserStoreTests() : IdentityUserStoreTests(IdentityBackend.Azurite);

/// <summary>
/// UserManager over the stores as a host registers them on Azure Table Storage:
/// <c>AddUnifiedTableStorage(connectionString)</c>, then <c>AddUnifiedIdentityStores</c>.
/// </summary>
// AddUnifiedTableStorage applies its options to the process-wide serializer policy, as the in-memory
// registration does; see AddUnifiedIdentityStoresTests.
[Collection("OversizedCellPolicy")]
public sealed class AzuriteUserManagerConcurrencyTests : UserManagerConcurrencyTests, IDisposable
{
    private readonly string _tablePrefix = IdentityAzurite.NewPrefix();
    private bool _registered;

    protected override void AddStorage(IServiceCollection services)
    {
        IdentityAzurite.SkipUnlessAvailable();
        _registered = true;
        services.AddUnifiedTableStorage(
            IdentityAzurite.ConnectionString,
            options => options.TableNameResolver = type => IdentityAzurite.TableName(_tablePrefix, type));
    }

    public void Dispose()
    {
        if (_registered)
            IdentityAzurite.DropTables(_tablePrefix);
    }
}
