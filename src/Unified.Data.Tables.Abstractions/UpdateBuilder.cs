using System.Linq.Expressions;
using System.Reflection;

namespace Unified.Data.Tables;

/// <summary>
/// Fluent, type-safe collector of the columns a builder-based partial update should write.
/// Passed to <see cref="IStorage{T}.UpdateAsync(string, System.Action{UpdateBuilder{T}}, System.Threading.CancellationToken)"/>.
/// Supports nested property paths (<c>x =&gt; x.Address.City</c> → column <c>Address_City</c>) and
/// optional optimistic concurrency via <see cref="WithETag"/>.
/// </summary>
/// <typeparam name="T">The entity type being updated.</typeparam>
public class UpdateBuilder<T>
{
    private const char PathSeparator = '_';

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

    /// <summary>
    /// The property paths to write, keyed by the flattened column path (<c>"Name"</c>,
    /// <c>"Address_City"</c>) — the same convention the serializer uses for nested columns.
    /// </summary>
    public Dictionary<string, object> Updates { get; } = new();

    /// <summary>
    /// The ETag to enforce on the merge, or <c>null</c> for an unconditional merge. See
    /// <see cref="WithETag"/>.
    /// </summary>
    public string? ETag { get; private set; }

    private bool _protectedAllowed;

    /// <summary>
    /// Makes the partial update conditional: the merge only applies when the row still carries
    /// <paramref name="etag"/> (as read via <see cref="Entity.ETag"/>); otherwise the update fails
    /// with <see cref="ConcurrencyConflictException"/>. Combine with a re-read loop for
    /// compare-and-swap on specific columns.
    /// </summary>
    public UpdateBuilder<T> WithETag(string etag)
    {
        if (string.IsNullOrEmpty(etag))
            throw new ArgumentException("ETag must be a non-empty row version read from the entity.", nameof(etag));
        if (etag == "*")
            throw new ArgumentException(
                "ETag \"*\" matches any row version, which would silently make the merge unconditional — omit WithETag for an unconditional merge.",
                nameof(etag));
        ETag = etag;
        return this;
    }

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
    /// <paramref name="value"/>. Nested access (<c>x =&gt; x.Address.City</c>) writes the flattened
    /// <c>Address_City</c> column, leaving sibling columns of the nested object untouched.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The property is managed by the storage layer, any segment of the path is a protected
    /// property that has not been unlocked via <see cref="AllowProtected"/>, or the path was
    /// already set (or overlaps a path already set) in this call.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The expression is not a property access rooted at the lambda parameter.</exception>
    public UpdateBuilder<T> SetProperty<TProp>(
        Expression<Func<T, TProp>> propertyPicker,
        TProp value)
    {
        var path = GetPropertyPath(propertyPicker);
        var rootProperty = path[0].Name;
        var columnPath = string.Join(PathSeparator.ToString(), path.Select(p => p.Name));

        if (ManagedPropertyNames.Contains(rootProperty))
            throw new InvalidOperationException(
                $"'{rootProperty}' is managed by the storage layer and cannot be set via SetProperty.");

        // [ProtectedProperty] is enforced on EVERY segment, not just the root — a nested path
        // (x => x.Payroll.Salary) reaches the same role-gated column as a root-level one would.
        var protectedSegment = path.FirstOrDefault(p => p.GetCustomAttribute<ProtectedPropertyAttribute>() is not null);
        if (protectedSegment is not null && !_protectedAllowed)
            throw new InvalidOperationException(
                $"'{protectedSegment.Name}' is a [ProtectedProperty] and cannot be set via the generic UpdateBuilder. Use a dedicated endpoint or call AllowProtected().");

        if (Updates.ContainsKey(columnPath))
            throw new InvalidOperationException(
                $"Property '{columnPath}' was already set in this UpdateAsync call.");

        // A whole-object write flattens into the same columns its nested paths would — writes
        // along one ancestry collide with an unspecified winner, so reject the overlap up front.
        foreach (var existing in Updates.Keys)
        {
            if (existing.StartsWith(columnPath + PathSeparator, StringComparison.Ordinal)
                || columnPath.StartsWith(existing + PathSeparator, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Property path '{columnPath}' overlaps '{existing}', which was already set in this UpdateAsync call — both would write the same columns.");
        }

        if (value is null)
            throw new ArgumentNullException(nameof(value), $"Value for property '{columnPath}' cannot be null.");

        Updates[columnPath] = value;
        return this;
    }

    // Walks the member chain back to the lambda parameter so nested access produces the full
    // flattened path. Anything not rooted at the parameter (closures, method calls, casts of
    // non-members) is rejected — silently accepting it is how columns end up orphaned.
    private static IReadOnlyList<PropertyInfo> GetPropertyPath<TProp>(Expression<Func<T, TProp>> expression)
    {
        var segments = new Stack<PropertyInfo>();
        var node = expression.Body;

        // Unwrap a boxing/implicit conversion (e.g. value-type property picked as object).
        if (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            node = unary.Operand;

        while (node is MemberExpression { Member: PropertyInfo property } member)
        {
            segments.Push(property);
            node = member.Expression!;
        }

        if (node is not ParameterExpression || segments.Count == 0)
            throw new ArgumentException(
                "Expression must be a property access rooted at the lambda parameter (e.g., x => x.Name or x => x.Address.City).");

        return segments.ToArray();
    }
}
