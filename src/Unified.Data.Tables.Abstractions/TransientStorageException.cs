namespace Unified.Data.Tables;

/// <summary>
/// Thrown when the storage backend could not serve a request for a reason that may pass: it throttled
/// the caller, was unavailable or timed out (HTTP 408, 429, 500, 502, 503 and 504 on Azure Tables), or
/// the connection failed or timed out, and the provider's own retries (the Azure SDK's retry policy)
/// were used up. The same call may succeed later. Provider-agnostic: the storage backend's own exception
/// is preserved as <see cref="Exception.InnerException"/>.
/// </summary>
/// <remarks>
/// A cancelled caller token is not a transient failure: it still throws
/// <see cref="OperationCanceledException"/>.
/// <para>
/// On the Azure provider every <see cref="OperationCanceledException"/> that leaves a storage call while
/// the caller's own token is not cancelled is reported as this exception, with the cancellation as its
/// <see cref="Exception.InnerException"/>: it is how the SDK reports its network timeout once its retries
/// are used up. The rule is by the token alone, not by who threw: a cancellation raised by code the call
/// runs on the caller's behalf (a stream handed to the store that is cancelled by a token of its own, an
/// authorizer, a cache callback) while the caller's token is live is reported the same way. The store
/// cannot soundly tell the two apart: it observes the attempts of its requests only on a client that has
/// the library's retry observation, and the lazy creation of a table is one request shared by every call
/// waiting for it.
/// </para>
/// </remarks>
public class TransientStorageException : StorageException
{
    /// <summary>
    /// Whether the failed call may have changed stored data although it threw. False for a read, and
    /// for a write only when the provider knows no attempt of it was applied. True otherwise (the
    /// conservative default): a request can be applied by the service and its response lost, so a
    /// write that timed out, or whose earlier attempt did, may have committed. Before repeating a write
    /// that is not idempotent, read the row.
    /// <para>
    /// The Azure provider knows it when it observed every attempt of every write request of the call (a
    /// client with the library's retry observation) and each was definitely rejected: a client error
    /// other than a request timeout (throttling, 429, among them) or the service's own 503
    /// <c>ServerBusy</c>. An attempt without a response, a server error (the service documents its 500
    /// timeout as "may or may not have succeeded") and a gateway's 502, 503 or 504 are not proof, and a
    /// batch write whose earlier transactions were accepted has committed those.
    /// </para>
    /// </summary>
    public bool MayHaveCommitted { get; set; } = true;

    /// <summary>
    /// How long the backend asked the caller to wait before trying again (the <c>Retry-After</c> response
    /// header on Azure), or null when it gave no hint.
    /// </summary>
    public TimeSpan? RetryAfter { get; set; }

    /// <summary>Creates the exception with a generic message.</summary>
    public TransientStorageException()
        : base("The storage backend could not serve the request; it may succeed when tried again.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public TransientStorageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public TransientStorageException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates the exception with a custom message, whether the call may have committed, the backend's
    /// retry hint and its exception.
    /// </summary>
    public TransientStorageException(string message, bool mayHaveCommitted, TimeSpan? retryAfter, Exception? innerException)
        : base(message, innerException)
    {
        MayHaveCommitted = mayHaveCommitted;
        RetryAfter = retryAfter;
    }
}
