using System.Globalization;
using System.Text;

namespace Unified.Data.Tables.Tests.Golden;

/// <summary>
/// Fixed inputs of the golden corpus. Nothing here may depend on the clock, the machine or the runtime:
/// the generators are spelled out (no <see cref="Random"/>) so the same call yields the same value on
/// every target framework for as long as the corpus lives.
/// </summary>
internal static class GoldenData
{
    /// <summary>A fixed instant with all seven fractional digits in use.</summary>
    internal static readonly DateTimeOffset Instant = new DateTimeOffset(2025, 3, 14, 15, 9, 26, TimeSpan.Zero).AddTicks(5358979);

    /// <summary>A fixed instant one day and a little after <see cref="Instant"/>.</summary>
    internal static readonly DateTimeOffset Later = new DateTimeOffset(2025, 3, 15, 16, 10, 27, TimeSpan.Zero).AddTicks(1234567);

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 éßЖя中文";

    /// <summary>
    /// Text that does not compress well, <paramref name="length"/> UTF-16 chars long, with characters of
    /// one, two and three UTF-8 bytes. A value longer than one text cell (32,000 chars) holds a surrogate
    /// pair across that boundary, where a chunk must not be cut.
    /// </summary>
    internal static string Text(int length, uint seed)
    {
        var chars = new char[length];
        var state = seed;
        for (var i = 0; i < length; i++)
        {
            state = Next(state);
            chars[i] = Alphabet[(int)((state >> 8) % (uint)Alphabet.Length)];
        }
        if (length > 32_001)
        {
            // U+1F600: its high surrogate is the last char a first chunk could take.
            chars[31_999] = '\uD83D';
            chars[32_000] = '\uDE00';
        }
        return new string(chars);
    }

    /// <summary><paramref name="length"/> bytes that do not compress.</summary>
    internal static byte[] Bytes(int length, uint seed)
    {
        var bytes = new byte[length];
        var state = seed;
        for (var i = 0; i < length; i++)
        {
            state = Next(state);
            bytes[i] = (byte)(state >> 16);
        }
        return bytes;
    }

    /// <summary>Text of <paramref name="length"/> chars that compresses to a small fraction of its size.</summary>
    internal static string Compressible(int length)
    {
        var text = new StringBuilder(length + 64);
        for (var line = 0; text.Length < length; line++)
            text.Append(CultureInfo.InvariantCulture, $"{line:D6} The quick brown fox jumps over the lazy dog.\n");
        return text.ToString(0, length);
    }

    /// <summary><paramref name="count"/> short strings that compress well as a list.</summary>
    internal static List<string> Items(int count) =>
        [.. Enumerable.Range(0, count).Select(i => string.Create(CultureInfo.InvariantCulture, $"item-{i:D5} lorem ipsum dolor sit amet"))];

    /// <summary><paramref name="count"/> strings of <paramref name="length"/> chars that do not compress.</summary>
    internal static List<string> Items(int count, int length, uint seed) =>
        [.. Enumerable.Range(0, count).Select(i => Text(length, seed + (uint)i))];

    // The linear congruential generator of Numerical Recipes: fixed here, so no runtime can change it.
    private static uint Next(uint state) => unchecked(state * 1664525u + 1013904223u);
}
