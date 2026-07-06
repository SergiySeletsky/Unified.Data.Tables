namespace Unified.Data.Tables;

/// <summary>
/// Decides whether the current caller may modify a property gated by
/// <see cref="ProtectedPropertyAttribute"/>. Implement this in the host application (for example,
/// wrapping the current <c>ClaimsPrincipal</c> and checking its roles) and register it in DI so
/// <c>TableStorage&lt;T&gt;</c> can enforce protected properties without the package taking a
/// dependency on any specific authentication stack.
/// </summary>
/// <remarks>
/// When no implementation is registered, <c>TableStorage&lt;T&gt;</c> denies changes to protected
/// properties on the whole-entity update path (a changed protected value throws
/// <see cref="UnauthorizedAccessException"/>).
/// </remarks>
public interface IProtectedPropertyAuthorizer
{
    /// <summary>
    /// Returns <c>true</c> if the current caller is authorised for at least one of the
    /// comma-separated <paramref name="roles"/> declared on a <see cref="ProtectedPropertyAttribute"/>.
    /// </summary>
    /// <param name="roles">Comma-separated roles (e.g. <c>"admin,accountant"</c>).</param>
    bool IsAllowed(string roles);
}
