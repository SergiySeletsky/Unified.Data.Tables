using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Azure.Data.Tables;

namespace Unified.Data.Tables;

/// <summary>
/// Translates a strongly-typed LINQ predicate into a <b>server-side</b> Azure Tables OData
/// <c>$filter</c> string, mapping each property to its serialized column name and each value to its
/// stored representation — so the filter runs in the service, not after a full client-side scan.
/// </summary>
/// <remarks>
/// <para>Column/value mapping mirrors <see cref="TableEntitySerializer"/>: nested access
/// (<c>x =&gt; x.Address.City</c>) becomes the flattened column <c>Address_City</c>; an <c>enum</c> is
/// compared against its stored string name; a <c>decimal</c> against its stored <c>double</c>.</para>
/// <para>Supported: the comparison operators (<c>== != &lt; &lt;= &gt; &gt;=</c>), <c>&amp;&amp;</c>,
/// <c>||</c>, <c>!</c>, and a bare <c>bool</c> member. Supported leaf types: <c>string</c>, <c>bool</c>,
/// <c>int</c>, <c>uint</c>, <c>long</c>, <c>ulong</c>, <c>double</c>, <c>decimal</c>, <see cref="Guid"/>,
/// <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, and enums (plus their nullable forms).
/// Anything else — method calls (<c>StartsWith</c>, <c>Contains</c>), column-to-column comparisons,
/// null comparisons, or a leaf type stored as JSON (collections, <c>float</c>, <c>short</c>,
/// <see cref="TimeSpan"/>, <c>byte[]</c>) — throws <see cref="NotSupportedException"/>, so a filter is
/// never silently evaluated wrong.</para>
/// </remarks>
public static class TableFilterTranslator
{
    /// <summary>Translate <paramref name="predicate"/> into an OData <c>$filter</c> string.</summary>
    /// <exception cref="NotSupportedException">The predicate uses an unsupported construct or leaf type.</exception>
    public static string Translate<T>(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var parameter = predicate.Parameters[0];
        var args = new List<object>();
        var format = Visit(predicate.Body, parameter, args);
        return TableClient.CreateQueryFilter(FormattableStringFactory.Create(format, args.ToArray()));
    }

    private static string Visit(Expression node, ParameterExpression p, List<object> args)
    {
        switch (node)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } b:
                return $"({Visit(b.Left, p, args)} and {Visit(b.Right, p, args)})";
            case BinaryExpression { NodeType: ExpressionType.OrElse } b:
                return $"({Visit(b.Left, p, args)} or {Visit(b.Right, p, args)})";
            case BinaryExpression b when IsComparison(b.NodeType):
                return VisitComparison(b, p, args);
            case UnaryExpression { NodeType: ExpressionType.Not } u:
                RejectNegatedNullableComparison(u.Operand, p);
                return $"not ({Visit(u.Operand, p, args)})";
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u:
                return Visit(u.Operand, p, args);
            case MemberExpression m:
                return VisitBoolMember(m, p, args);
            default:
                throw new NotSupportedException(
                    $"Unsupported filter expression '{node}' ({node.NodeType}). See {nameof(TableFilterTranslator)} for the supported grammar.");
        }
    }

    private static string VisitBoolMember(MemberExpression m, ParameterExpression p, List<object> args)
    {
        var column = ResolveColumn(m, p, out var leafType);
        if ((Nullable.GetUnderlyingType(leafType) ?? leafType) != typeof(bool))
            throw new NotSupportedException($"A bare member is only valid for a bool column: '{m}'.");
        args.Add(true);
        return $"{column} eq {{{args.Count - 1}}}";
    }

    private static string VisitComparison(BinaryExpression b, ParameterExpression p, List<object> args)
    {
        var leftRooted = IsParameterRooted(b.Left, p);
        var rightRooted = IsParameterRooted(b.Right, p);
        if (leftRooted && rightRooted)
            throw new NotSupportedException($"Column-to-column comparisons are not supported: '{b}'.");
        if (!leftRooted && !rightRooted)
            throw new NotSupportedException($"At least one side of a comparison must reference the entity: '{b}'.");

        Expression memberSide;
        Expression valueSide;
        ExpressionType op;
        if (leftRooted) { memberSide = b.Left; valueSide = b.Right; op = b.NodeType; }
        else { memberSide = b.Right; valueSide = b.Left; op = Flip(b.NodeType); }

        var column = ResolveColumn(memberSide, p, out var leafType);

        // Reject ordering on leaf types whose STORED form does not preserve CLR ordering — an enum is
        // stored by name (lexical, not numeric), a ulong wraps to a signed Int64 — so a server-side
        // ordering compare would silently disagree with the in-memory (compiled-predicate) fake.
        var underlying = Nullable.GetUnderlyingType(leafType) ?? leafType;
        if (IsOrdering(op) && (underlying.IsEnum || underlying == typeof(ulong)))
            throw new NotSupportedException(
                $"Ordering comparisons (<, <=, >, >=) are not supported on the {(underlying.IsEnum ? "enum" : "ulong")} column " +
                $"'{column}' — its stored form does not preserve CLR ordering. Use == or != instead.");

        // Inequality on a column that can be UNSET (nullable value or reference type) is rejected: Azure
        // Tables' handling of an absent column under `ne` is version-dependent and diverges from the
        // in-memory CLR predicate (and from Azurite), so allowing it would break the "green fake ⇒ holds
        // on Azure" guarantee. Non-nullable value columns are always present, so their `!=` is safe.
        if (op == ExpressionType.NotEqual && IsNullableOrReference(leafType))
            throw new NotSupportedException(
                $"'!=' on the nullable/reference column '{column}' is not supported — absent-column semantics " +
                "differ between Azure Tables and in-memory evaluation. Filter positively (==) instead.");

        object value;
        try
        {
            value = MapValue(leafType, Evaluate(valueSide));
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Narrowing overflow, a value side that still references the entity, a failed parse, etc. —
            // surface the translator's documented exception type, not a raw runtime one.
            throw new NotSupportedException($"Cannot translate the value in comparison '{b}': {ex.Message}", ex);
        }

        args.Add(value);
        return $"{column} {ODataOperator(op)} {{{args.Count - 1}}}";
    }

    private static bool IsNullableOrReference(Type leafType) =>
        !leafType.IsValueType || Nullable.GetUnderlyingType(leafType) is not null;

    private static Expression? MemberSideOrNull(BinaryExpression b, ParameterExpression p)
    {
        if (IsParameterRooted(b.Left, p)) return b.Left;
        if (IsParameterRooted(b.Right, p)) return b.Right;
        return null;
    }

    // Negating ANY comparison on a column that can be UNSET (nullable value or reference) is rejected for
    // the same reason as atomic '!=': Azure's treatment of an absent column under negation is
    // version-dependent and not equal to the in-memory CLR result, and rejecting negated-equality while
    // allowing negated-ordering would be internally inconsistent (over an absent column Azure evaluates
    // every atomic comparison as false, so not(eq) and not(ge) behave identically). Non-nullable value
    // columns are always present, so negated comparisons on them are safe.
    private static void RejectNegatedNullableComparison(Expression operand, ParameterExpression p)
    {
        if (operand is not BinaryExpression b || !IsComparison(b.NodeType))
            return;
        var memberSide = MemberSideOrNull(b, p);
        if (memberSide is null)
            return;
        ResolveColumn(memberSide, p, out var leafType);
        if (IsNullableOrReference(leafType))
            throw new NotSupportedException(
                "Negating a comparison on a nullable/reference column ('!(x == v)', '!(x >= v)', …) is not " +
                "supported — absent-column semantics differ between Azure Tables and in-memory evaluation. " +
                "Rewrite the filter positively instead.");
    }

    private static bool IsOrdering(ExpressionType t) => t is
        ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
        ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

    private static bool IsComparison(ExpressionType t) => t is
        ExpressionType.Equal or ExpressionType.NotEqual or
        ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
        ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

    private static ExpressionType Flip(ExpressionType t) => t switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => t,
    };

    private static string ODataOperator(ExpressionType t) => t switch
    {
        ExpressionType.Equal => "eq",
        ExpressionType.NotEqual => "ne",
        ExpressionType.GreaterThan => "gt",
        ExpressionType.GreaterThanOrEqual => "ge",
        ExpressionType.LessThan => "lt",
        ExpressionType.LessThanOrEqual => "le",
        _ => throw new NotSupportedException($"Operator '{t}' is not supported."),
    };

    private static bool IsParameterRooted(Expression e, ParameterExpression p)
    {
        e = StripConvert(e);
        return e switch
        {
            ParameterExpression pe => pe == p,
            MemberExpression m => m.Expression is not null && IsParameterRooted(m.Expression, p),
            _ => false,
        };
    }

    private static Expression StripConvert(Expression e)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
            e = u.Operand;
        return e;
    }

    private static string ResolveColumn(Expression memberExpr, ParameterExpression p, out Type leafType)
    {
        var names = new List<string>();
        leafType = typeof(object);
        var first = true;

        var current = StripConvert(memberExpr);
        while (current is MemberExpression m)
        {
            if (m.Member is not PropertyInfo pi)
                throw new NotSupportedException($"Only property access is supported in filters: '{memberExpr}'.");

            var ownerType = StripConvert(m.Expression!).Type;

            // x.Foo.Value on a Nullable<T> would map to a non-existent "Foo_Value" column, and the
            // fake's compiled predicate throws when Foo is null — steer to comparing the member directly.
            if (pi.Name == "Value" && Nullable.GetUnderlyingType(ownerType) is not null)
                throw new NotSupportedException(
                    $"Compare the nullable member directly (x.Foo == v), not x.Foo.Value: '{memberExpr}'.");

            // Only PERSISTED columns can be filtered server-side. A computed / get-only /
            // [IgnoreDataMember] property (and ETag/Timestamp) has no stored cell — the server would
            // match nothing while the fake recomputes it from backing columns, so reject the divergence.
            if (!TypeMetadataCache.GetMetadata(ownerType).PropertyMap.ContainsKey(pi.Name))
                throw new NotSupportedException(
                    $"'{pi.Name}' on {ownerType.Name} is not a persisted column (it is computed, get-only, " +
                    "[IgnoreDataMember], or ETag/Timestamp) and cannot be filtered server-side.");

            // A NESTED owner (x.Owner.Child) only yields an "Owner_Child" column when the serializer
            // FLATTENS it. A JSON-blobbed owner (positional record, collection, [JsonConstructor]) is
            // stored as one cell, so no such column exists — the filter would match nothing on Azure
            // while the in-memory fake evaluates it. Reject the divergence rather than translate it wrong.
            if (StripConvert(m.Expression!) is MemberExpression && !TableEntitySerializer.FlattensToColumns(ownerType))
                throw new NotSupportedException(
                    $"'{ownerType.Name}' is stored as a single JSON cell (not flattened columns), so its nested " +
                    $"member cannot be filtered server-side: '{memberExpr}'. Filter a top-level or flattened property.");

            if (first) { leafType = pi.PropertyType; first = false; }
            names.Add(pi.Name);
            current = StripConvert(m.Expression!);
        }

        if (first)
            throw new NotSupportedException($"Expected a property access: '{memberExpr}'.");
        if (current is not ParameterExpression pe || pe != p)
            throw new NotSupportedException($"A filter member must be rooted at the entity parameter: '{memberExpr}'.");

        names.Reverse();
        return string.Join("_", names);
    }

    private static object Evaluate(Expression valueSide)
    {
        if (valueSide is ConstantExpression c)
            return c.Value!;
        // A closed sub-expression (captured local, literal math, method result) — compile and run it.
        var lambda = Expression.Lambda(Expression.Convert(valueSide, typeof(object)));
        return lambda.Compile().DynamicInvoke()!;
    }

    private static object MapValue(Type leafType, object? value)
    {
        var t = Nullable.GetUnderlyingType(leafType) ?? leafType;
        if (value is null)
            throw new NotSupportedException(
                "Null comparisons are not supported — Azure Tables stores no null cells (an unset property is an absent column).");

        if (t.IsEnum) return MapEnumValue(t, value);
        if (t == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        if (t == typeof(bool)) return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        if (t == typeof(int)) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (t == typeof(uint) || t == typeof(long)) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (t == typeof(ulong)) return unchecked((long)Convert.ToUInt64(value, CultureInfo.InvariantCulture));
        if (t == typeof(double)) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (t == typeof(decimal)) return (double)Convert.ToDecimal(value, CultureInfo.InvariantCulture); // stored as double
        if (t == typeof(Guid)) return AsGuid(value);
        if (t == typeof(DateTimeOffset)) return AsDateTimeOffset(value);
        if (t == typeof(DateTime)) return AsDateTimeOffsetFromDateTime(value);

        throw new NotSupportedException(
            $"Filtering on type '{t.Name}' is not supported. Supported leaf types: string, bool, int, uint, " +
            "long, ulong, double, decimal, Guid, DateTime, DateTimeOffset, and enums (and their nullable forms).");
    }

    private static object MapEnumValue(Type enumType, object value)
    {
        if (enumType.IsInstanceOfType(value))
            return value.ToString()!; // stored as the enum name
        try
        {
            return Enum.ToObject(enumType, Convert.ToInt64(value, CultureInfo.InvariantCulture)).ToString()!;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new NotSupportedException($"Cannot compare enum '{enumType.Name}' with value '{value}'.");
        }
    }

    private static Guid AsGuid(object value) =>
        value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);

    private static DateTimeOffset AsDateTimeOffset(object value) =>
        value is DateTimeOffset dto ? dto : new DateTimeOffset(Convert.ToDateTime(value, CultureInfo.InvariantCulture), TimeSpan.Zero);

    private static DateTimeOffset AsDateTimeOffsetFromDateTime(object value) =>
        value is DateTime dt
            ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))
            : new DateTimeOffset(Convert.ToDateTime(value, CultureInfo.InvariantCulture), TimeSpan.Zero);
}
