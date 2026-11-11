using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;

namespace Unified.Data.Tables.Providers;

/// <summary>
/// Storage-provider SPI, not for application code: how the protected-property check of a whole-entity write
/// compares and describes a property that is a large value, so that every <c>IStorage</c> implementation
/// (such as the in-memory store) applies the same rule as <c>TableStorage</c>. A property that is not a large
/// value keeps the comparison (<see cref="object.Equals(object, object)"/>) and the message of 0.8.3. Its shape
/// may change in any minor version.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ProtectedLargeValues
{
    private static readonly ConcurrentDictionary<(Type Entity, PropertyInfo Property), bool> Large = new();

    /// <summary>
    /// Storage-provider SPI, not for application code. True when <paramref name="property"/> of
    /// <paramref name="entityType"/> is a large value: declared as <see cref="TableBlobContent"/>, or opted in
    /// by <see cref="TableValueSplitAttribute"/> (its own, or the class-level one that covers it). Decided by
    /// reflection alone and remembered; false for a type that is not large-value aware, and nothing of the
    /// type is constructed or called.
    /// </summary>
    /// <param name="entityType">The entity type the store writes.</param>
    /// <param name="property">A top-level property of <paramref name="entityType"/>.</param>
    /// <returns>Whether the property is a large value.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool IsLargeValue(Type entityType, PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(property);
        return Large.GetOrAdd((entityType, property), static key =>
            TypeMetadataCache.IsLargeValueAware(key.Entity)
            && TypeMetadataCache.TryGetMember(key.Entity, key.Property.Name, out _, out var largeValue)
            && largeValue);
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. True when <paramref name="stored"/> or
    /// <paramref name="written"/>, the values of a protected property of <paramref name="entityType"/>, holds a
    /// large value below it: an object the serializer flattens into columns whose runtime type has, at any depth,
    /// a <see cref="TableBlobContent"/> or a member opted in by <see cref="TableValueSplitAttribute"/>. The refusal
    /// of a change to such a property then describes the values (<see cref="DescribeRefusedChange"/>) instead of
    /// quoting them: their text would hold the large value whole. False, with nothing of the values read, for a
    /// type that is not large-value aware, which keeps the message of 0.8.3; so does a value that holds no
    /// large-value member.
    /// </summary>
    /// <param name="entityType">The entity type the store writes.</param>
    /// <param name="stored">The value of the stored entity.</param>
    /// <param name="written">The value of the entity being written.</param>
    /// <returns>Whether either value holds a large-value member.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entityType"/> is null.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool HoldsLargeValue(Type entityType, object? stored, object? written)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        if (!TypeMetadataCache.IsLargeValueAware(entityType))
            return false;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return HoldsLargeValueBelow(stored, seen, 0) || HoldsLargeValueBelow(written, seen, 0);
    }

    // Walks where the serializer flattens (an object that is not a scalar, an enum or a JSON-stored value).
    private static bool HoldsLargeValueBelow(object? value, HashSet<object> seen, int depth)
    {
        if (value is TableBlobContent)
            return true;
        if (value is null or string or byte[] || depth > MaxDescribedDepth || value.GetType().IsEnum
            || TableEntityValue.IsPrimitive(value.GetType()) || !TableEntityValue.CanSerializeWithoutJson(value)
            || (!value.GetType().IsValueType && !seen.Add(value)))
            return false;

#pragma warning disable CA1031 // a value the check cannot inspect is described, never quoted
        try
        {
            var metadata = TypeMetadataCache.GetMetadata(value.GetType());
            foreach (var property in metadata.Properties)
            {
                if (property.PropertyType == typeof(TableBlobContent) || metadata.SplitOptions.ContainsKey(property.Name)
                    || HoldsLargeValueBelow(property.GetValue(value), seen, depth + 1))
                    return true;
            }
            return false;
        }
        catch (Exception)
        {
            return true;
        }
#pragma warning restore CA1031
    }

    // As deep as a column of the service's longest property name can lie.
    private const int MaxDescribedDepth = 128;

    /// <summary>
    /// Storage-provider SPI, not for application code. True when the value <paramref name="written"/> to a
    /// large-value property leaves the <paramref name="stored"/> one as it is: the same instance; two
    /// <see cref="TableBlobContent"/> of the same blob (the same SHA-256, length and content type, both known);
    /// two <c>byte[]</c> with the same bytes; otherwise <see cref="object.Equals(object, object)"/>. A content
    /// whose hash is not known yet (a stream that was never prepared) counts as changed: nothing is downloaded
    /// and no caller's stream is read to compare.
    /// </summary>
    /// <param name="stored">The value of the stored entity.</param>
    /// <param name="written">The value of the entity being written.</param>
    /// <returns>Whether the value is unchanged.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool IsUnchanged(object? stored, object? written)
    {
        if (ReferenceEquals(stored, written))
            return true;
        return (stored, written) switch
        {
            (TableBlobContent a, TableBlobContent b) => a.IsSealed && b.IsSealed
                && a.Length == b.Length
                && string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.ContentType, b.ContentType, StringComparison.Ordinal),
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            _ => Equals(stored, written),
        };
    }

    /// <summary>
    /// Storage-provider SPI, not for application code. The end of the refusal of a change to a protected
    /// large-value property, or to a protected property whose value holds one (<see cref="HoldsLargeValue"/>):
    /// the kind and the length of the stored and the new value, never the values themselves (a large value can
    /// be megabytes, and the stored one is protected).
    /// </summary>
    /// <param name="stored">The value of the stored entity.</param>
    /// <param name="written">The value of the entity being written.</param>
    /// <returns>A sentence that starts with "The current caller is not authorised to change it".</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static string DescribeRefusedChange(object? stored, object? written) =>
        $"The current caller is not authorised to change it from {Describe(stored)} to {Describe(written)} " +
        "(the values of a large-value property are not quoted).";

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => string.Create(CultureInfo.InvariantCulture, $"a string of {text.Length:N0} characters"),
        byte[] bytes => string.Create(CultureInfo.InvariantCulture, $"a byte[] of {bytes.Length:N0} bytes"),
        TableBlobContent { IsSealed: true } content => string.Create(CultureInfo.InvariantCulture, $"a TableBlobContent of {content.Length:N0} bytes"),
        TableBlobContent => "a TableBlobContent whose length is not known yet",
        _ => $"a value of type {value.GetType().Name}",
    };
}
