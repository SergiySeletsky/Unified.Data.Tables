namespace Unified.Data.Tables;

/// <summary>
/// How the storage layer treats ids and partition/row-key arguments before they hit the table.
/// One mode per store — mixing modes against the same table orphans rows.
/// </summary>
public enum IdNormalization
{
    /// <summary>
    /// Default (the historical behaviour): every id and partition/prefix argument is passed through
    /// <see cref="EntityId.Normalize"/> — trim, spaces to <c>'-'</c>, lower-case — so natural-form
    /// input always addresses the same row regardless of casing or stray whitespace.
    /// </summary>
    Normalized,

    /// <summary>
    /// Ids and key arguments are used exactly as supplied — for tables whose keys are
    /// case-sensitive payloads (Base64, hex, mixed-case natural keys) or pre-existing data written
    /// by another layer. The caller owns casing consistency: <c>"A|1"</c> and <c>"a|1"</c> are
    /// different rows. Note the append-log helpers (<c>AppendAsync</c>/<c>RecentAsync</c>)
    /// pre-normalize their inputs themselves and remain internally consistent in either mode.
    /// </summary>
    AsWritten,
}
