#if NET
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Unified.Data.Tables;

/// <summary>
/// Preserves the reflection metadata the <c>Unified.Data.Tables</c> serializer needs in a
/// NativeAOT or trimmed application, and fails clearly when a type was not registered.
/// </summary>
/// <remarks>
/// <para>
/// The serializer reads an entity's properties, constructors, attributes and interfaces by
/// reflection, and it walks the types of the values those properties hold. The trimmer cannot see
/// that walk, so every type in the graph must be rooted before the store is used:
/// </para>
/// <code>
/// TableEntityMetadata.Register&lt;Customer&gt;();
/// TableEntityMetadata.Register&lt;Address&gt;();
/// </code>
/// <para>
/// A type is registered automatically by a provider's closed-generic registration
/// (for example <c>AddUnifiedInMemoryStorage&lt;Customer&gt;()</c>) and by
/// <c>AddUnifiedIdentityStores</c> for Identity's row models. Open-generic DI registrations cannot
/// carry the annotation, so an application that resolves <c>IStorage&lt;Customer&gt;</c> from an
/// open-generic registration registers its entity graph itself (a source generator can emit
/// these calls). Registering an entity type also preserves the members it inherits from
/// <see cref="Entity"/>; it does not preserve the members of its property types. Register every
/// nested type that is flattened into columns and every runtime subtype a polymorphic store can
/// materialize; a type stored as JSON is declared in the <c>JsonSerializerContext</c> instead
/// (see <c>TableJson.UseContext</c>).
/// </para>
/// <para>
/// Registration is a no-op at run time: it exists so the trimmer keeps the metadata. In a
/// NativeAOT application <see cref="RequireRegistration"/> is true by default, and the serializer
/// throws a clear <see cref="InvalidOperationException"/> when it meets an unregistered type
/// instead of silently reading partial metadata (which could store a nested object as a different
/// column shape). In a JIT application the check is off and behavior is unchanged.
/// </para>
/// </remarks>
public static class TableEntityMetadata
{
    /// <summary>
    /// The members the serializer reflects over: public properties (including inherited ones),
    /// public and non-public instance constructors, and interfaces. It deliberately excludes
    /// fields: the serializer never reads one, and preserving an enum's fields makes the trimmer
    /// call <c>Enum.GetValues(Type)</c>, which NativeAOT cannot do.
    /// </summary>
    public const DynamicallyAccessedMemberTypes SerializerMembers =
        DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.PublicConstructors
        | DynamicallyAccessedMemberTypes.NonPublicConstructors
        | DynamicallyAccessedMemberTypes.Interfaces;

    private static readonly ConcurrentDictionary<Type, byte> RegisteredTypes = new();

    /// <summary>
    /// Whether the serializer refuses a type that was not registered. Defaults to
    /// <see langword="true"/> when the runtime does not support dynamic code (NativeAOT), and to
    /// <see langword="false"/> otherwise. A trimmed JIT application that reflects over the
    /// serializer's graph can turn it on to get the same fail-fast check.
    /// </summary>
    public static bool RequireRegistration { get; set; } = !RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>
    /// Preserves the members of <typeparamref name="T"/> for the serializer and the metadata cache.
    /// Register the entity type, every nested type it flattens into columns, and every runtime
    /// subtype a polymorphic store can materialize, before the store is used.
    /// </summary>
    /// <typeparam name="T">A type the serializer will reflect over.</typeparam>
    public static void Register<[DynamicallyAccessedMembers(SerializerMembers)] T>()
        => RegisteredTypes.TryAdd(typeof(T), 0);

    /// <summary>
    /// Preserves the public methods of <typeparamref name="T"/> so a query predicate that compares
    /// values of <typeparamref name="T"/> can be built as an expression tree in NativeAOT. The C#
    /// compiler resolves a user-defined operator (for example <c>decimal</c>'s <c>&gt;</c>) through
    /// <see cref="System.Linq.Expressions.Expression"/>, which needs the operator method to survive
    /// trimming. Register every scalar type a filter compares: <c>decimal</c>,
    /// <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <see cref="TimeSpan"/>, and so on.
    /// </summary>
    /// <typeparam name="T">A value type used in a query comparison.</typeparam>
    public static void RegisterFilterOperators<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>()
    {
    }

    /// <summary>
    /// Preserves the members of <paramref name="type"/> for the serializer. The generic overload is
    /// preferred; this overload exists for source-generated registrations and for a type known only
    /// as a <see cref="Type"/> at a call site whose members are still statically known
    /// (<c>typeof(T)</c>).
    /// </summary>
    /// <param name="type">A type the serializer will reflect over.</param>
    public static void Register([DynamicallyAccessedMembers(SerializerMembers)] Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        RegisteredTypes.TryAdd(type, 0);
    }

    /// <summary>Whether <paramref name="type"/> was registered.</summary>
    internal static bool IsRegistered(Type type) => RegisteredTypes.ContainsKey(type);

    /// <summary>
    /// Throws when registration is required and <paramref name="type"/> was not registered. Called
    /// by the metadata cache before it reflects over a type.
    /// </summary>
    internal static void Require(Type type)
    {
        if (!RequireRegistration || RegisteredTypes.ContainsKey(type))
            return;

        throw new InvalidOperationException(
            $"The serializer has no preserved metadata for '{type.FullName}'. Register it with " +
            "TableEntityMetadata.Register<T>() (the type argument is the type named above) and every " +
            "nested or runtime subtype it reaches, before using the store. A source generator can " +
            "emit these calls; NativeAOT and trimmed builds cannot discover the types by reflection.");
    }
}
#endif
