using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Identity.Tests;

public class AddUnifiedIdentityStoresTests
{
    private static IServiceCollection Base()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUnifiedInMemoryStorage();
        return services;
    }

    [Fact]
    public void RegistersBothStores()
    {
        // Arrange
        var services = Base();

        // Act
        services.AddIdentityCore<IdentityUser>().AddRoles<IdentityRole>().AddUnifiedIdentityStores();
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.NotNull(provider.GetRequiredService<IUserStore<IdentityUser>>());
        Assert.NotNull(provider.GetRequiredService<IRoleStore<IdentityRole>>());
    }

    [Fact]
    public void DoesNotRegisterStorageItself()
    {
        // Arrange — no storage provider registered at all
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddIdentityCore<IdentityUser>().AddRoles<IdentityRole>().AddUnifiedIdentityStores();
        using var provider = services.BuildServiceProvider();

        // Assert — resolving a store must fail, proving the package leaves the provider choice open
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IUserStore<IdentityUser>>());
    }

    [Fact]
    public void ThrowsForACustomUserType()
    {
        // Arrange
        var services = Base();

        // Act / Assert — the documented limitation, surfaced at startup rather than at runtime
        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddIdentityCore<CustomUser>().AddUnifiedIdentityStores());
        Assert.Contains("IdentityUser", ex.Message, StringComparison.Ordinal);
    }

    private sealed class CustomUser : IdentityUser;
}
