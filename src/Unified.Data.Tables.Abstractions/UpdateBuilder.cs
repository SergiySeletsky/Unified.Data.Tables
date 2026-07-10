using System.Linq.Expressions;
using System.Reflection;

namespace Unified.Data.Tables;

/// <summary>
/// Fluent, type-safe collector of the columns a builder-based partial update should write.
/// Passed to <see cref="IStorage{T}.UpdateAsync(string, System.Action{UpdateBuilder{T}}, System.Threading.CancellationToken)"/>.
/// </summary>
/// <typeparam name="T">The entity type being updated.</typeparam>
public class UpdateBuilder<T>
{
    // Properties declared on the Entity base (Id, CreatedAt, UpdatedAt, ETag, Timestamp) are
    // managed by the storage layer — Id is part of the partition+row key, CreatedAt is set on
    // insert, UpdatedAt is bumped automatically on every Merge, ETag is the row version, and
    // Timestamp is service-owned. Letting callers patch these via SetProperty would either
    // silently corrupt the row's identity or be overridden anyway, so we reject them up front.
    private static readonly HashSet<string> ManagedPropertyNames = new(
        typeof(Entity)
            .GetProperties()
            .Select(p => p.Name),
        StringComparer.Ordinal);

    // Properties decorated with [ProtectedProperty] are role-gated and must go through dedicated
    // endpoints, never through generic builder-based updates — unless the caller has already
    // verified authorisation and opted in via AllowProtected().
    private static readonly HashSet<string> ProtectedPropertyNames = new(
        typeof(T)
            .GetProperties()
            .Where(p => p.GetCustomAttribute<ProtectedPropertyAttribute>() is not null)
            .Select(p => p.Name),
        StringComparer.Ordinal);

    /// <summary>The set of column names to value mappings collected so far.</summary>
    public Dictionary<string, object> Updates { get; } = new();

    private bool _protectedAllowed;

    /// <summary>
    /// Unlocks <see cref="ProtectedPropertyAttribute"/>-decorated properties for this builder
    /// instance. Call this only from code paths that have already verified the caller is authorised
    /// (e.g. a role-gated controller endpoint or a trusted server-side service).
    /// </summary>
    public UpdateBuilder<T> AllowProtected()
    {
        _protectedAllowed = true;
        return this;
    }

    /// <summary>
    /// Records that the property selected by <paramref name="propertyPicker"/> should be set to
    /// <paramref name="value"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The property is managed by the storage layer, is a protected property that has not been
    /// unlocked via <see cref="AllowProtected"/>, or was already set in this call.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <c>null</c>.</exception>
    public UpdateBuilder<T> SetProperty<TProp>(
        Expression<Func<T, TProp>> propertyPicker,
        TProp value)
    {
        string propertyName = GetPropertyName(propertyPicker);

        if (ManagedPropertyNames.Contains(propertyName))
            throw new InvalidOperationException(
                $"'{propertyName}' is managed by the storage layer and cannot be set via SetProperty.");

        if (ProtectedPropertyNames.Contains(propertyName) && !_protectedAllowed)
            throw new InvalidOperationException(
                $"'{propertyName}' is a [ProtectedProperty] and cannot be set via the generic UpdateBuilder. Use a dedicated endpoint or call AllowProtected().");

        if (Updates.ContainsKey(propertyName))
            throw new InvalidOperationException(
                $"Property '{propertyName}' was already set in this UpdateAsync call.");

        if (value is null)
            throw new ArgumentNullException(nameof(value), $"Value for property '{propertyName}' cannot be null.");

        Updates[propertyName] = value;
        return this;
    }

    private static string GetPropertyName<TProp>(Expression<Func<T, TProp>> expression)
    {
        if (expression.Body is MemberExpression member)
        {
            return member.Member.Name;
        }
        throw new ArgumentException("Expression must be a simple property access (e.g., x => x.Name).");
    }
}
