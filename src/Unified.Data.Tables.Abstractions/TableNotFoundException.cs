namespace Unified.Data.Tables;

/// <summary>
/// Thrown when the table itself does not exist: it was deleted (or is still being deleted) after the
/// store created it, or the store may not create tables. Never raised for a missing row, which is
/// <see cref="EntityNotFoundException"/> on a write and an empty result on a read. Provider-agnostic: the
/// storage backend's own exception (e.g. an Azure 404 <c>TableNotFound</c>) is preserved as
/// <see cref="Exception.InnerException"/>.
/// </summary>
/// <remarks>
/// Reading the row again cannot help, so no retry loop of the library treats it as a lost race. A read
/// can throw it as well as a write.
/// </remarks>
public class TableNotFoundException : StorageException
{
    /// <summary>The name of the missing table, when known.</summary>
    public string? TableName { get; }

    /// <summary>Creates the exception with a generic message.</summary>
    public TableNotFoundException()
        : base("Table not found: the table does not exist.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public TableNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public TableNotFoundException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a specific table, preserving the provider exception.</summary>
    public TableNotFoundException(string message, string? tableName, Exception? innerException)
        : base(message, innerException)
    {
        TableName = tableName;
    }
}
