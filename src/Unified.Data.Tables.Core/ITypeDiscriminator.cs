namespace Unified.Data.Tables;

/// <summary>
/// Maps a CLR type to the token stored in <see cref="SystemColumnNames.TypeName"/> and back.
/// </summary>
/// <remarks>
/// This is a seam rather than a fixed rule because the obvious implementation — an assembly-qualified
/// name — welds stored rows to assembly identity: a rename, namespace move or strong-name change
/// orphans every row that carries it. It also costs a few hundred bytes on <em>every</em> row,
/// charged against the transaction byte budget that caps batch size. See
/// <see cref="TypeDiscriminatorMap"/> for the recommended alternative.
/// <para>
/// A resolver is NOT a security boundary. Every polymorphic read independently verifies that the
/// resolved type is assignable to the store's base type, and no configuration can disable that
/// check — see <c>TableEntitySerializer.TryFromTableEntity</c>.
/// </para>
/// </remarks>
public interface ITypeDiscriminator
{
    /// <summary>The token to store for <paramref name="type"/>.</summary>
    /// <param name="type">The runtime type being written.</param>
    /// <returns>The discriminator token.</returns>
    string ToDiscriminator(Type type);

    /// <summary>Resolves a stored token back to a CLR type.</summary>
    /// <param name="discriminator">The stored token.</param>
    /// <param name="baseType">
    /// The store's base type, for diagnostics and for implementations that scope their lookup. The
    /// caller still enforces assignability, so an implementation must not rely on doing so itself.
    /// </param>
    /// <returns>The resolved type.</returns>
    /// <exception cref="TypeLoadException">The token does not name a type this resolver knows.</exception>
    Type Resolve(string discriminator, Type baseType);
}
