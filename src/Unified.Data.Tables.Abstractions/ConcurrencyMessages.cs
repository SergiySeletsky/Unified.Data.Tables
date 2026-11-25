namespace Unified.Data.Tables;

/// <summary>
/// Shared error-message text for concurrency contract violations, so the real store and the
/// in-memory fake throw byte-identical messages — a test written against either implementation
/// documents the same contract.
/// </summary>
internal static class ConcurrencyMessages
{
    internal static string AutoRequiresETag(string typeName) =>
        $"UpdateAsync in Auto mode requires {typeName}.ETag — read the entity first and round-trip " +
        "its ETag (OneAsync/QueryAsync populate it), use MutateAsync for read-modify-write, or opt " +
        "into an unconditional replace explicitly with ConcurrencyMode.LastWriterWins. " +
        "(UnifiedTableStorageOptions.ImplicitLastWriterWins = true restores the pre-0.6.0 fallback.)";

    /// <summary>
    /// A whole-entity update whose ETag is the wildcard: <c>"*"</c> is not a version, so the update is
    /// refused instead of being sent as an unconditional replace.
    /// </summary>
    internal static string WildcardETagOnUpdate(string typeName) =>
        $"UpdateAsync was given {typeName}.ETag = \"*\". The wildcard is not a version: an update conditional on it " +
        "would replace whatever is stored. Round-trip the ETag a read returned, or ask for an unconditional replace " +
        "explicitly with ConcurrencyMode.LastWriterWins.";

    /// <summary>
    /// A large-value read whose row was replaced by another writer during each of its
    /// <paramref name="attempts"/> attempts (its continuation rows or blobs belonged to another version
    /// every time). Nothing stored is damaged; the read can be repeated.
    /// </summary>
    internal static string ReadKeptChanging(string typeName, string partitionKey, string rowKey, int attempts) =>
        $"Reading {typeName} '{partitionKey}|{rowKey}' did not complete: the row changed during each of its {attempts} " +
        "attempts (another writer replaced it while its continuation rows or blobs were being read). Nothing stored " +
        "is damaged; the read can be repeated.";
}
