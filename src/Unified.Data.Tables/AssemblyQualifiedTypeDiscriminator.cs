using System.Collections.Concurrent;

namespace Unified.Data.Tables;

/// <summary>
/// Stores <see cref="Type.AssemblyQualifiedName"/> — byte-identical to what
/// <c>ToTableEntity(persistType: true)</c> has always written, so an existing table is readable with
/// no migration and no configuration.
/// </summary>
/// <remarks>
/// This is the default so that upgrading never orphans data, not because it is the best choice.
/// Prefer <c>TypeDiscriminatorMap</c> for any new table: an assembly-qualified token breaks
/// on assembly rename and is large enough to measurably shrink the batch size a transaction can
/// carry. The type is named for what it stores rather than left as an unmarked default so that the
/// trade-off is visible at the call site.
/// </remarks>
public sealed class AssemblyQualifiedTypeDiscriminator : ITypeDiscriminator
{
    // Type.GetType parses and probes on every call; tokens repeat once per row.
    private static readonly ConcurrentDictionary<string, Type> ResolveCache = new(StringComparer.Ordinal);

    /// <summary>The shared instance. The type is stateless apart from its resolve cache.</summary>
    public static AssemblyQualifiedTypeDiscriminator Instance { get; } = new();

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
            token => Type.GetType(token)
                     ?? throw new TypeLoadException(PolymorphicMessages.Unresolvable(token)));
    }
}
