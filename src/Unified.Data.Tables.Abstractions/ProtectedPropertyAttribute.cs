namespace Unified.Data.Tables;

/// <summary>
/// Marks a property as protected — only callers authorised for the specified roles may change its
/// value. The storage layer enforces this on write:
/// <list type="bullet">
///   <item><description>whole-entity <see cref="IStorage{T}.UpdateAsync(T, System.Threading.CancellationToken)"/> consults an <see cref="IProtectedPropertyAuthorizer"/>;</description></item>
///   <item><description>builder-based <see cref="IStorage{T}.UpdateAsync(string, System.Action{UpdateBuilder{T}}, System.Threading.CancellationToken)"/> rejects the property unless <see cref="UpdateBuilder{T}.AllowProtected"/> was called.</description></item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ProtectedPropertyAttribute : Attribute
{
    /// <summary>
    /// Comma-separated list of roles allowed to modify this property (e.g. <c>"admin,accountant"</c>).
    /// </summary>
    public string Roles { get; }

    /// <summary>Creates the attribute for the given comma-separated <paramref name="roles"/>.</summary>
    public ProtectedPropertyAttribute(string roles) => Roles = roles;
}
