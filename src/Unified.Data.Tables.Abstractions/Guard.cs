namespace Unified.Data.Tables;

// netstandard2.0 has no ArgumentNullException.ThrowIfNull; net10.0's analyzers demand it (CA1510).
// One conditional helper keeps every multi-targeted call site clean.
internal static class Guard
{
    public static void NotNull(object? argument, string paramName)
    {
#if NETSTANDARD2_0
        if (argument is null) throw new ArgumentNullException(paramName);
#else
        ArgumentNullException.ThrowIfNull(argument, paramName);
#endif
    }
}
