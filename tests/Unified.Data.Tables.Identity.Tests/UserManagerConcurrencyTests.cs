using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Unified.Data.Tables.InMemory;

namespace Unified.Data.Tables.Identity.Tests;

// AddUnifiedInMemoryStorage applies its options to the process-wide serializer policy; see
// AddUnifiedIdentityStoresTests.
[Collection("OversizedCellPolicy")]
public class UserManagerConcurrencyTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUnifiedInMemoryStorage();
        services.AddIdentityCore<IdentityUser>().AddRoles<IdentityRole>().AddUnifiedIdentityStores();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task UpdateSecurityStamp_OnAStaleCopy_ReturnsConcurrencyFailure()
    {
        // Arrange — two requests (or two instances) hold the same user; one revokes every session
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.True((await users.CreateAsync(new IdentityUser("alice") { Id = "u1" })).Succeeded);
        var revoker = (await users.FindByIdAsync("u1"))!;
        var stale = (await users.FindByIdAsync("u1"))!;
        Assert.True((await users.UpdateSecurityStampAsync(revoker)).Succeeded);
        var revokedStamp = revoker.SecurityStamp;

        // Act — before 0.9.2 either write replaced the row and put the pre-revocation security stamp
        // back, so the revoked sessions validated again
        var result = await users.SetLockoutEndDateAsync(stale, DateTimeOffset.UtcNow.AddMinutes(5));
        var second = await users.UpdateSecurityStampAsync(stale);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal("ConcurrencyFailure", Assert.Single(result.Errors).Code);
        Assert.False(second.Succeeded);
        Assert.Equal("ConcurrencyFailure", Assert.Single(second.Errors).Code);
        var stored = (await users.FindByIdAsync("u1"))!;
        Assert.Equal(revokedStamp, stored.SecurityStamp);
        Assert.Null(stored.LockoutEnd);
    }
}
