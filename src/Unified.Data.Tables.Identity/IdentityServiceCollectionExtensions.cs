using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Unified.Data.Tables.Identity.Models;

namespace Unified.Data.Tables.Identity;

/// <summary>
/// Registers ASP.NET Core Identity stores backed by <see cref="Unified.Data.Tables.IStorage{T}"/>.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    private const string NotIdentityUser =
        "AddUnifiedIdentityStores requires IdentityUser as the user type, not a subclass or a different type.";
    private const string NotIdentityRole =
        "AddUnifiedIdentityStores requires IdentityRole as the role type, not a subclass or a different type.";

    /// <summary>
    /// Adds <see cref="IUserStore{TUser}"/> and <see cref="IRoleStore{TRole}"/> implementations that
    /// persist through <see cref="Unified.Data.Tables.IStorage{T}"/>.
    /// </summary>
    /// <param name="builder">The identity builder to add the stores to.</param>
    /// <returns>The same <paramref name="builder"/>, for chaining.</returns>
    /// <remarks>
    /// This does NOT register <see cref="Unified.Data.Tables.IStorage{T}"/> — register a provider
    /// yourself, for example <c>AddUnifiedTableStorage(connectionString)</c> or
    /// <c>AddUnifiedInMemoryStorage()</c>.
    /// <para>
    /// Recommended for Azure Table Storage: disable caching for user rows. They carry
    /// <c>SecurityStamp</c>, <c>PasswordHash</c> and <c>LockoutEnd</c>, and the default sliding cache
    /// can serve a revoked security stamp indefinitely on a multi-instance host:
    /// <code>
    /// services.AddUnifiedTableStorage(cs, o => o.CacheFor&lt;IdentityUserModel&gt;(CachePolicy.Disabled));
    /// </code>
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The builder's user type is not exactly <see cref="IdentityUser"/>, or its role type is set and
    /// is not exactly <see cref="IdentityRole"/>. Custom user and role types are not supported.
    /// </exception>
    public static IdentityBuilder AddUnifiedIdentityStores(this IdentityBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.UserType != typeof(IdentityUser))
            throw new InvalidOperationException(NotIdentityUser);

        if (builder.RoleType is null)
        {
            builder.Services.TryAddScoped<IUserStore<IdentityUser>, UserOnlyStore>();
            return builder;
        }

        if (builder.RoleType != typeof(IdentityRole))
            throw new InvalidOperationException(NotIdentityRole);

        builder.Services.TryAddScoped<IUserStore<IdentityUser>, UserStore>();
        builder.Services.TryAddScoped<IRoleStore<IdentityRole>, RoleStore>();
        return builder;
    }
}
