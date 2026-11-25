namespace Unified.Data.Tables;

/// <summary>
/// Thrown when the storage backend refuses a request as invalid or too large (HTTP 400 and 413 on Azure
/// Tables): a key with a character the service does not accept, a property value over the service's
/// size limit, a transaction that names one row twice, a request body over the limit. Sending the same
/// request again fails the same way. Provider-agnostic: the storage backend's own exception is preserved
/// as <see cref="Exception.InnerException"/> and its error code as
/// <see cref="StorageException.ProviderErrorCode"/>.
/// </summary>
/// <remarks>
/// The refused request wrote nothing. A batch is sent as several transactions, so the transactions
/// before the refused one stay committed.
/// </remarks>
public class StorageRequestRejectedException : StorageException
{
    /// <summary>Creates the exception with a generic message.</summary>
    public StorageRequestRejectedException()
        : base("The storage backend rejected the request as invalid.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public StorageRequestRejectedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public StorageRequestRejectedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with a custom message, the backend's error code and its exception.</summary>
    public StorageRequestRejectedException(string message, string? providerErrorCode, Exception? innerException)
        : base(message, providerErrorCode, innerException)
    {
    }
}
