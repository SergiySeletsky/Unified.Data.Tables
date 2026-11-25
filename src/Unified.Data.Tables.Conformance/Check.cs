using System.Collections;
using System.Globalization;
using System.Text;

namespace Unified.Data.Tables.Conformance;

/// <summary>
/// The kit's assertions. Each takes the rule in words ("an update of a missing row") and throws
/// <see cref="ConformanceAssertionException"/> with what was expected and what the store did.
/// </summary>
internal static class Check
{
    public static void True(bool condition, string rule)
    {
        if (!condition)
            throw new ConformanceAssertionException($"expected that {rule}, but it does not hold");
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new ConformanceAssertionException($"expected {what} to be {Show(expected)}, but it is {Show(actual)}");
    }

    public static void NotEqual<T>(T unexpected, T actual, string what)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new ConformanceAssertionException($"expected {what} to differ from {Show(unexpected)}, but it is the same");
    }

    public static void Null(object? actual, string what)
    {
        if (actual is not null)
            throw new ConformanceAssertionException($"expected {what} to be null, but it is {Show(actual)}");
    }

    public static T NotNull<T>(T? actual, string what) where T : class =>
        actual ?? throw new ConformanceAssertionException($"expected {what}, but it is null");

    public static void NotEmpty(string? actual, string what)
    {
        if (string.IsNullOrEmpty(actual))
            throw new ConformanceAssertionException($"expected {what} to be set, but it is {Show(actual)}");
    }

    /// <summary>The same elements in the same order.</summary>
    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        var same = e.Count == a.Count;
        for (var i = 0; same && i < e.Count; i++)
            same = EqualityComparer<T>.Default.Equals(e[i], a[i]);
        if (!same)
            throw new ConformanceAssertionException($"expected {what} to be {Show(e)}, but it is {Show(a)}");
    }

    public static void Bytes(byte[]? expected, byte[]? actual, string what)
    {
        if (expected is null || actual is null)
        {
            if (!ReferenceEquals(expected, actual))
                throw new ConformanceAssertionException($"expected {what} to be {Show(expected)}, but it is {Show(actual)}");
            return;
        }
        if (expected.Length != actual.Length)
            throw new ConformanceAssertionException($"expected {what} to be {expected.Length} bytes, but it is {actual.Length} bytes");
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                throw new ConformanceAssertionException($"expected {what} to be the bytes written, but byte {i} differs");
        }
    }

    /// <summary>A long text, compared without quoting megabytes in the message.</summary>
    public static void Text(string? expected, string? actual, string what)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
            return;
        if (expected is null || actual is null)
            throw new ConformanceAssertionException($"expected {what} to be {Describe(expected)}, but it is {Describe(actual)}");
        var at = 0;
        while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
            at++;
        throw new ConformanceAssertionException(
            $"expected {what} to be the {expected.Length} characters written, but it is {actual.Length} characters and differs from character {at}");

        static string Describe(string? s) => s is null ? "null" : $"a text of {s.Length} characters";
    }

    /// <summary>
    /// <paramref name="act"/> throws <typeparamref name="TException"/> or a type derived from it. Anything
    /// else it throws, and nothing thrown at all, fails.
    /// </summary>
    public static async Task<TException> ThrowsAsync<TException>(Func<Task> act, string rule) where TException : Exception
    {
        Exception thrown;
        try
        {
            await act().ConfigureAwait(false);
            throw new ConformanceAssertionException(
                $"expected {rule} to throw {typeof(TException).Name}, but nothing was thrown");
        }
        catch (ConformanceAssertionException)
        {
            throw;
        }
#pragma warning disable CA1031 // the type is what is being checked
        catch (Exception ex)
#pragma warning restore CA1031
        {
            thrown = ex;
        }

        return thrown as TException
            ?? throw new ConformanceAssertionException(
                $"expected {rule} to throw {typeof(TException).Name}, but it threw {thrown.GetType().FullName}: {thrown.Message}", thrown);
    }

    /// <summary>
    /// As <see cref="ThrowsAsync{TException}"/>, for a provider-neutral failure: the thrown type must also
    /// be one the abstractions define, never a type of the provider (an Azure, SQLite or home-made
    /// exception derived from the neutral one counts as a provider type).
    /// </summary>
    public static async Task<TException> NeutralAsync<TException>(Func<Task> act, string rule) where TException : StorageException
    {
        var thrown = await ThrowsAsync<TException>(act, rule).ConfigureAwait(false);
        if (thrown.GetType().Assembly != typeof(StorageException).Assembly)
            throw new ConformanceAssertionException(
                $"expected {rule} to throw the provider-neutral {typeof(TException).Name}, but it threw the provider's own {thrown.GetType().FullName}", thrown);
        return thrown;
    }

    /// <summary>A call given a cancelled token throws <see cref="OperationCanceledException"/> (or a derived type).</summary>
    public static Task CancelledAsync(Func<Task> act, string rule) =>
        ThrowsAsync<OperationCanceledException>(act, rule + " with a cancelled token");

    public static string Show(object? value) => value switch
    {
        null => "null",
        string s => s.Length <= 120 ? "\"" + s + "\"" : "\"" + s.Substring(0, 117) + "...\" (" + s.Length.ToString(CultureInfo.InvariantCulture) + " characters)",
        byte[] bytes => bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes",
        DateTimeOffset at => at.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable items => ShowAll(items),
        _ => value.ToString() ?? value.GetType().Name,
    };

    private static string ShowAll(IEnumerable items)
    {
        var text = new StringBuilder("[");
        var count = 0;
        foreach (var item in items)
        {
            if (count == 40)
            {
                text.Append(", ...");
                break;
            }
            if (count++ > 0)
                text.Append(", ");
            text.Append(Show(item));
        }
        return text.Append(']').ToString();
    }
}
