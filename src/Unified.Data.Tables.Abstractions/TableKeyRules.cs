using System.Globalization;

namespace Unified.Data.Tables;

/// <summary>
/// The rules Azure Table storage applies to a <c>PartitionKey</c> and a <c>RowKey</c>, as a check that
/// runs without a request: for validating an id at an API boundary, before a write the service would
/// refuse with <see cref="StorageRequestRejectedException"/>, and for a provider that must accept only
/// the keys Azure accepts.
/// </summary>
/// <remarks>
/// A key is refused when it is null, when it holds a forward slash (<c>/</c>), a backslash (<c>\</c>), a
/// number sign (<c>#</c>), a question mark (<c>?</c>) or a control character (U+0000 to U+001F, which
/// includes tab, line feed and carriage return, and U+007F to U+009F), or when it is longer than
/// <see cref="MaxKeyLength"/> characters. The empty string is a key the service accepts (the typed
/// stores of this library refuse an empty id themselves).
/// <para>
/// The check is on the key as stored. A typed store normalizes an id before it stores it (see
/// <see cref="EntityId.Normalize"/> and <see cref="IdNormalization"/>), which removes nothing these rules
/// refuse, so an id that fails here fails after normalization too. A type with large values reserves
/// further row keys for its continuation rows; that rule is the store's own and is not checked here.
/// </para>
/// </remarks>
public static class TableKeyRules
{
    /// <summary>
    /// The longest key the check accepts, in UTF-16 characters. The service documents a limit of 1 KiB
    /// per key and stores strings as UTF-16, so the limit is counted as 512 characters: the conservative
    /// reading, under which a key that passes here is never refused by the service for its size.
    /// <para>
    /// It is a conservative bound, not the service's measured limit: the documentation gives the size in
    /// bytes without naming the encoding, and the service may accept a longer key (up to 1,024
    /// characters, if it counts one byte a character). A key of 513 to 1,024 characters is therefore
    /// refused here although a storage account might take it. The Azure provider itself does not apply
    /// this check to the ids it stores; the service decides there.
    /// </para>
    /// </summary>
    public const int MaxKeyLength = 512;

    /// <summary>Whether the service accepts <paramref name="key"/> as a <c>PartitionKey</c> or a <c>RowKey</c>.</summary>
    /// <param name="key">The key, as it would be stored.</param>
    /// <returns>True when the key breaks none of the rules.</returns>
    public static bool IsValid(string? key) => FindViolation(key) is null;

    /// <summary>What is wrong with <paramref name="key"/>, in words, or null when the service accepts it.</summary>
    /// <param name="key">The key, as it would be stored.</param>
    /// <returns>A description of the first rule the key breaks, or null.</returns>
    public static string? FindViolation(string? key)
    {
        if (key is null)
            return "it is null";
        if (key.Length > MaxKeyLength)
            return string.Format(CultureInfo.InvariantCulture,
                "it is {0} characters long and a key holds at most {1} (1 KiB of UTF-16)", key.Length, MaxKeyLength);
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c == '/' || c == '\\' || c == '#' || c == '?')
                return string.Format(CultureInfo.InvariantCulture, "it holds '{0}' at index {1}, which a key cannot contain", c, i);
            if (c <= '\u001f' || (c >= '\u007f' && c <= '\u009f'))
                return string.Format(CultureInfo.InvariantCulture,
                    "it holds the control character U+{0:X4} at index {1}, which a key cannot contain", (int)c, i);
        }
        return null;
    }

    /// <summary>Throws when the service would refuse either key.</summary>
    /// <param name="partitionKey">The partition key, as it would be stored.</param>
    /// <param name="rowKey">The row key, as it would be stored.</param>
    /// <exception cref="ArgumentException">A key breaks a rule; the message says which key and which rule.</exception>
    public static void Validate(string partitionKey, string rowKey)
    {
        if (FindViolation(partitionKey) is { } partitionViolation)
            throw new ArgumentException($"The PartitionKey is not a key Azure Table storage accepts: {partitionViolation}.", nameof(partitionKey));
        if (FindViolation(rowKey) is { } rowViolation)
            throw new ArgumentException($"The RowKey is not a key Azure Table storage accepts: {rowViolation}.", nameof(rowKey));
    }

    /// <summary>Throws when the service would refuse either key of <paramref name="key"/>.</summary>
    /// <param name="key">The row address.</param>
    /// <exception cref="ArgumentException">A key breaks a rule; the message says which key and which rule.</exception>
    public static void Validate(TableKey key) => Validate(key.PartitionKey, key.RowKey);
}
