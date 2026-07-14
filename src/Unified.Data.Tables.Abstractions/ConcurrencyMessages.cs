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
}
