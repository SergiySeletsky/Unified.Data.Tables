using System.Collections.Concurrent;

namespace Unified.Data.Tables;

/// <summary>
/// Stores <see cref="Type.AssemblyQualifiedName"/> — byte-identical to what
/// <c>ToTableEntity(persistType: true)</c> has always written, so an existing table is readable with
/// no migration and no configuration.
/// </summary>
/// <remarks>
/// This is the default so that upgrading never orphans data, not because it is the best choice.
/// Prefer <see cref="TypeDiscriminatorMap"/> for any new table: an assembly-qualified token breaks
/// on assembly rename and is large enough to measurably shrink the batch size a transaction can
/// carry. The type is named for what it stores rather than left as an unmarked default so that the
/// trade-off is visible at the call site.
/// </remarks>
public sealed class AssemblyQualifiedTypeDiscriminator : ITypeDiscriminator
{
    // Type.GetType parses and probes on every call; tokens repeat once per row.
    private static readonly ConcurrentDictionary<string, Type> ResolveCache = new(StringComparer.Ordinal);

    // Legacy namespace prefixes that were renamed after rows were written. Kept process-wide and
    // deliberately static: the tokens are baked into rows, so every resolver instance must see the
    // same map for reads to succeed regardless of which store instance serves them.
    private static readonly List<(string LegacyPrefix, string CurrentPrefix)> TypePrefixMappings = [];
    private static readonly object MappingLock = new();

    /// <summary>The shared instance. The type is stateless apart from its resolve cache.</summary>
    public static AssemblyQualifiedTypeDiscriminator Instance { get; } = new();

    /// <summary>The token to store for <paramref name="type"/>.</summary>
    /// <param name="type">The runtime type being written.</param>
    /// <returns>The discriminator token.</returns>
    /// <inheritdoc />
    public string ToDiscriminator(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // AssemblyQualifiedName is null only for open generic parameters, which cannot be persisted
        // anyway; falling back keeps the failure at the read rather than writing a null cell.
        return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    }

    /// <inheritdoc />
    public Type Resolve(string discriminator, Type baseType)
    {
        ArgumentNullException.ThrowIfNull(discriminator);

        // GetOrAdd's factory throwing leaves nothing cached, so a transient load failure is retried
        // rather than memoized.
        return ResolveCache.GetOrAdd(
            discriminator,
            token => ResolveTypeName(token)
                     ?? throw new TypeLoadException(PolymorphicMessages.Unresolvable(token)));
    }

    /// <summary>
    /// Registers a namespace rename for tokens written before the move: a stored
    /// assembly-qualified name whose TYPE portion starts with
    /// <paramref name="legacyNamespacePrefix"/> resolves by substituting
    /// <paramref name="currentNamespacePrefix"/>.
    /// </summary>
    /// <remarks>
    /// Rows store the assembly-qualified name verbatim, so renaming or moving a type orphans every
    /// row that carries it — the legacy serializer throws <c>TypeLoadException</c> and the affected
    /// processes can no longer read their own history. One registration covers every type moved in
    /// the same namespace move, and the same-namespace/assembly identity after the prefix is
    /// preserved, so the rewritten token names the current type. Writes are unaffected: new rows
    /// always carry the CURRENT assembly-qualified name.
    /// </remarks>
    /// <param name="legacyNamespacePrefix">
    /// The old type-namespace prefix, e.g. <c>"My.App.Notifications."</c>.
    /// </param>
    /// <param name="currentNamespacePrefix">
    /// The current type-namespace prefix, e.g. <c>"My.App.EmailNotifications."</c>.
    /// </param>
    public static void RegisterLegacyTypeNamespace(string legacyNamespacePrefix, string currentNamespacePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyNamespacePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentNamespacePrefix);

        lock (MappingLock)
        {
            TypePrefixMappings.Add((legacyNamespacePrefix, currentNamespacePrefix));

            // Tokens that failed to resolve before the registration must not stay memoized as
            // failures... they are not cached on failure, but a SUCCESSFUL old-namespace resolution
            // could not exist; still, clearing keeps the cache honest across registrations.
            ResolveCache.Clear();
        }
    }

    /// <summary>
    /// Resolves an assembly-qualified name, applying the registered legacy namespace renames when
    /// the direct resolution fails. Returns null when the name resolves to no loaded type.
    /// </summary>
    /// <param name="assemblyQualifiedName">The stored token.</param>
    /// <returns>The resolved type, or null.</returns>
    internal static Type? ResolveTypeName(string assemblyQualifiedName)
    {
        var direct = Type.GetType(assemblyQualifiedName);
        if (direct is not null)
        {
            return direct;
        }

        (string LegacyPrefix, string CurrentPrefix)[] mappings;
        lock (MappingLock)
        {
            mappings = TypePrefixMappings.ToArray();
        }

        foreach (var (legacy, current) in mappings)
        {
            if (!assemblyQualifiedName.StartsWith(legacy, StringComparison.Ordinal))
            {
                continue;
            }

            var rewritten = string.Concat(current, assemblyQualifiedName.AsSpan(legacy.Length));
            var mapped = Type.GetType(rewritten);
            if (mapped is not null)
            {
                return mapped;
            }
        }

        return null;
    }
}
