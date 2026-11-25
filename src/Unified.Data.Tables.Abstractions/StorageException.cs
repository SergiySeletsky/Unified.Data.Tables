namespace Unified.Data.Tables;

/// <summary>
/// The base of every failure a storage backend reports through <see cref="IStorage{T}"/>,
/// <see cref="IPolymorphicStorage{TBase}"/> or <see cref="IOrphanBlobSweeper"/>. Provider-agnostic: no
/// storage member throws a backend's own exception type; that exception (an Azure
/// <c>RequestFailedException</c>, say) is always preserved as <see cref="Exception.InnerException"/>.
/// </summary>
/// <remarks>
/// The derived types name the failures a caller can act on: <see cref="EntityNotFoundException"/>,
/// <see cref="TableNotFoundException"/>, <see cref="DuplicateKeyException"/>,
/// <see cref="ConcurrencyConflictException"/>, <see cref="StorageRequestRejectedException"/>,
/// <see cref="TransientStorageException"/>, <see cref="BatchPayloadTooLargeException"/> and
/// <see cref="BatchNotAtomicException"/>. A failure that is none of those (an authentication or
/// authorization error, an unexpected status, a failure of the blob store that holds offloaded values)
/// is thrown as this type itself.
/// A cancelled caller token still throws <see cref="OperationCanceledException"/>, and argument and
/// usage errors keep their own types (<see cref="ArgumentException"/>, <see cref="InvalidOperationException"/>).
/// </remarks>
public class StorageException : Exception
{
    /// <summary>
    /// The backend's own error code for the failure (on Azure Tables the service's error code, such as
    /// <c>EntityAlreadyExists</c> or <c>TableNotFound</c>), or null when it reported none. For diagnostics:
    /// the codes differ between backends, so branch on the exception type instead.
    /// </summary>
    public string? ProviderErrorCode { get; set; }

    /// <summary>Creates the exception with a generic message.</summary>
    public StorageException()
        : base("The storage request failed.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public StorageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public StorageException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with a custom message, the backend's error code and its exception.</summary>
    public StorageException(string message, string? providerErrorCode, Exception? innerException)
        : base(message, innerException)
    {
        ProviderErrorCode = providerErrorCode;
    }
}
