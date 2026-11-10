namespace Unified.Data.Tables;

/// <summary>
/// A stored value lives in blob storage (its row holds a <c>{Prop}__blob</c> reference), but the store
/// reading or writing it has no blob offload configured. This is a configuration error, not corruption:
/// configure <c>UnifiedTableStorageOptions.BlobOffload</c> with the container the value was written to.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/>, not from <see cref="TableValueIntegrityException"/>:
/// the stored data is intact and retrying the read cannot help.
/// </remarks>
public sealed class BlobOffloadNotConfiguredException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="tableName">The table of the store that needs blob offload.</param>
    /// <param name="columnName">The column whose value is stored in blob storage.</param>
    public BlobOffloadNotConfiguredException(string tableName, string columnName)
        : base($"Column '{columnName}' of table '{tableName}' is stored in blob storage, but blob offload is not configured. "
            + "Set UnifiedTableStorageOptions.BlobOffload (with the container the value was written to) to read or write it.")
    {
        TableName = tableName;
        ColumnName = columnName;
    }

    /// <summary>The table of the store that needs blob offload.</summary>
    public string TableName { get; }

    /// <summary>The column whose value is stored in blob storage.</summary>
    public string ColumnName { get; }
}
