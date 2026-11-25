namespace Unified.Data.Tables.Conformance;

/// <summary>Argument checks that compile on both target frameworks.</summary>
internal static class Arg
{
    public static void NotNull(object? value, string name)
    {
#if NETSTANDARD2_0
        if (value is null)
            throw new ArgumentNullException(name);
#else
        ArgumentNullException.ThrowIfNull(value, name);
#endif
    }
}
