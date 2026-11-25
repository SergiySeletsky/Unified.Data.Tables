namespace Unified.Data.Tables;

/// <summary>
/// Thrown by an all-or-nothing batch when its entities cannot be written as one transaction (they span
/// partitions, are more than one transaction holds, or exceed its payload budget). Raised before anything
/// is written, so the store is unchanged; the alternative, splitting silently, would commit a part of a
/// batch the caller asked to be atomic.
/// </summary>
public class BatchNotAtomicException : StorageException
{
    /// <summary>Creates the exception with a generic message.</summary>
    public BatchNotAtomicException()
        : base("The batch cannot be written as one transaction; nothing was written.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public BatchNotAtomicException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public BatchNotAtomicException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
