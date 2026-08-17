using System.Collections.Concurrent;
using System.Reflection;

namespace Unified.Data.Tables;

/// <summary>
/// An allow-list discriminator: only registered types can be written, and only registered tokens
/// can be read. The recommended choice for any new polymorphic table.
/// </summary>
/// <remarks>
/// Two problems this solves over <see cref="AssemblyQualifiedTypeDiscriminator"/>. A stable short
/// token survives assembly renames, namespace moves and strong-naming, none of which an
/// assembly-qualified name does. And it is small: an assembly-qualified name costs a few hundred
/// bytes on every row, charged against the 3&#160;MB transaction budget that caps how many rows a
/// batch can carry.
/// <para>
/// Registration is strict in both directions — a duplicate token or a re-registered type throws at
/// registration rather than at the first ambiguous read, because a mapping bug discovered against
/// production rows is a data problem rather than a configuration one.
/// </para>
/// <para>
/// For an existing table written with assembly-qualified names, call
/// <see cref="WithAssemblyQualifiedFallback"/>: reads accept both forms while writes always emit
/// the short token, so the table converges in place as rows are rewritten.
/// </para>
/// </remarks>
public sealed class TypeDiscriminatorMap : ITypeDiscriminator
{
    private readonly Dictionary<Type, string> toToken = [];
    private readonly Dictionary<string, Type> fromToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Type> fallbackCache = new(StringComparer.Ordinal);
    private bool assemblyQualifiedFallback;

    /// <summary>Registers <typeparamref name="T"/> under <paramref name="token"/>.</summary>
    /// <typeparam name="T">The concrete type to register.</typeparam>
    /// <param name="token">The stable token to store for it.</param>
    /// <returns>This map, for chaining.</returns>
    /// <exception cref="ArgumentException">The token or the type is already registered.</exception>
    public TypeDiscriminatorMap Map<T>(string token) => Map(typeof(T), token);

    /// <summary>Registers <paramref name="type"/> under <paramref name="token"/>.</summary>
    /// <param name="type">The concrete type to register.</param>
    /// <param name="token">The stable token to store for it.</param>
    /// <returns>This map, for chaining.</returns>
    /// <exception cref="ArgumentException">The token or the type is already registered.</exception>
    public TypeDiscriminatorMap Map(Type type, string token)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        if (fromToken.TryGetValue(token, out var existingType) && existingType != type)
        {
            throw new ArgumentException(
                $"Token '{token}' is already mapped to '{existingType.FullName}'. Two types sharing " +
                "one token would make every stored row of the pair ambiguous on read.",
                nameof(token));
        }

        if (toToken.TryGetValue(type, out var existingToken) && !string.Equals(existingToken, token, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Type '{type.FullName}' is already mapped to token '{existingToken}'. Remapping it " +
                "would strand every row already written under the old token.",
                nameof(type));
        }

        toToken[type] = token;
        fromToken[token] = type;
        return this;
    }

    /// <summary>
    /// Registers every concrete type in <paramref name="assembly"/> assignable to
    /// <typeparamref name="TBase"/>. Defaults to <see cref="MemberInfo.Name"/> as the token, which
    /// is short and stable but collides across namespaces — a collision throws here, at
    /// registration, rather than surfacing as an ambiguous read later.
    /// </summary>
    /// <typeparam name="TBase">The base type to scan for.</typeparam>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="naming">Token selector; defaults to the type's simple name.</param>
    /// <returns>This map, for chaining.</returns>
    /// <exception cref="ArgumentException">Two scanned types produce the same token.</exception>
    public TypeDiscriminatorMap MapAssignableTo<TBase>(Assembly assembly, Func<Type, string>? naming = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var name = naming ?? (t => t.Name);

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(TBase).IsAssignableFrom(type))
                continue;

            Map(type, name(type));
        }

        return this;
    }

    /// <summary>
    /// Also accept an assembly-qualified token on READ, for a table whose existing rows were written
    /// by <see cref="AssemblyQualifiedTypeDiscriminator"/>. Writes still emit the short token, so the
    /// table converges in place rather than needing a stop-the-world backfill.
    /// </summary>
    /// <returns>This map, for chaining.</returns>
    public TypeDiscriminatorMap WithAssemblyQualifiedFallback()
    {
        assemblyQualifiedFallback = true;
        return this;
    }

    /// <inheritdoc />
    public string ToDiscriminator(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (toToken.TryGetValue(type, out var token))
            return token;

        throw new InvalidOperationException(
            $"Type '{type.FullName}' is not registered on this {nameof(TypeDiscriminatorMap)}. Call " +
            $"{nameof(Map)}<{type.Name}>(\"token\") — or {nameof(MapAssignableTo)} to register a whole " +
            "hierarchy — before writing it. An allow-list that silently accepted unknown types would " +
            "not be one.");
    }

    /// <inheritdoc />
    public Type Resolve(string discriminator, Type baseType)
    {
        ArgumentNullException.ThrowIfNull(discriminator);
        if (fromToken.TryGetValue(discriminator, out var type))
            return type;

        if (assemblyQualifiedFallback)
        {
            return fallbackCache.GetOrAdd(
                discriminator,
                token => Type.GetType(token) ?? throw new TypeLoadException(PolymorphicMessages.Unresolvable(token)));
        }

        throw new TypeLoadException(
            $"Token '{discriminator}' is not registered on this {nameof(TypeDiscriminatorMap)}. If this " +
            "row was written before the map existed, call " +
            $"{nameof(WithAssemblyQualifiedFallback)}() to keep reading legacy rows while new writes " +
            "converge on short tokens.");
    }
}
