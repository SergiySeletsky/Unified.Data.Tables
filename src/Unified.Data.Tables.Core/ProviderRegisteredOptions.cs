using System.Runtime.CompilerServices;

namespace Unified.Data.Tables;

/// <summary>
/// What the DI registrations of the providers tell each other about the options they register
/// themselves. The providers do not reference one another, so the in-memory registration cannot name
/// the Azure or SQLite options types; it can only see, in the service collection, a registration of a
/// type derived from <see cref="StorageOptions"/>. This says whether such a registration is a
/// provider's own (registered beside options that were there first, and never meant for the in-memory
/// stores) or the host's (meant for every store, whatever order it was registered in).
/// </summary>
internal static class ProviderRegisteredOptions
{
    private static readonly object Marker = new();

    private static readonly ConditionalWeakTable<StorageOptions, object> Owned = new();

    private static readonly ConditionalWeakTable<object, StorageOptions> Fallbacks = new();

    /// <summary>Notes <paramref name="options"/> as an instance a provider's registration call created and registered.</summary>
    internal static void MarkProviderOwned(StorageOptions options) => Owned.AddOrUpdate(options, Marker);

    /// <summary>True when <paramref name="options"/> is an instance a provider's registration call created.</summary>
    internal static bool IsProviderOwned(object? options) => options is StorageOptions o && Owned.TryGetValue(o, out _);

    /// <summary>
    /// Notes that <paramref name="registration"/> (a service descriptor whose options are chosen when the
    /// container resolves them) resolves <paramref name="fallback"/> unless the host registered options of
    /// its own, so a later provider call can read them before the container is built, as it reads an
    /// instance registration.
    /// </summary>
    internal static void NoteFallback(object registration, StorageOptions fallback)
    {
        MarkProviderOwned(fallback);
        Fallbacks.AddOrUpdate(registration, fallback);
    }

    /// <summary>The options <paramref name="registration"/> resolves when the host registered none (see <see cref="NoteFallback"/>).</summary>
    internal static StorageOptions? FallbackOf(object? registration) =>
        registration is not null && Fallbacks.TryGetValue(registration, out var fallback) ? fallback : null;
}
