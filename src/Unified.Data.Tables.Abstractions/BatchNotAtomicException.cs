namespace Unified.Data.Tables;

/// <summary>
/// Thrown by the all-or-nothing writes (<see cref="IAtomicBatch{T}.CreateAtomicAsync"/>,
/// <see cref="IAtomicBatch{T}.UpsertAtomicAsync"/>, <see cref="IAtomicInsert{TBase}.InsertAtomicAsync"/>
/// and the experimental raw <c>IRawTable.SubmitAsync</c>) when what they were given cannot be written as
/// one transaction: the rows span partitions, are more than one transaction holds, exceed its payload
/// budget, or name a row twice. Raised before anything is written, so the store is unchanged; the
/// alternative, splitting silently, would commit a part of a batch the caller asked to be atomic.
/// </summary>
/// <remarks>
/// The batch members of the store interfaces (<see cref="IStorage{T}.CreateBatchAsync"/>,
/// <see cref="IStorage{T}.UpsertBatchAsync"/>, <see cref="IPolymorphicStorage{TBase}.InsertBatchAsync"/>)
/// never throw it: they split a batch into several transactions, as before.
/// </remarks>
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
