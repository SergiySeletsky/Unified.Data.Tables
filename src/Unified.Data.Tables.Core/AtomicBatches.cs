namespace Unified.Data.Tables;

/// <summary>
/// The checks every all-or-nothing write makes before it sends anything: what Azure Tables accepts as
/// one entity-group transaction.
/// </summary>
internal static class AtomicBatches
{
    /// <summary>
    /// Refuses more rows than one transaction holds, rows of more than one partition and a row named twice.
    /// </summary>
    /// <param name="keys">The key of each row, in order.</param>
    /// <param name="what">What is being written, for the message (<c>"batch"</c>, <c>"transaction"</c>).</param>
    /// <param name="row">What one entry is called, for the message (<c>"row"</c>, <c>"key"</c>).</param>
    /// <exception cref="BatchNotAtomicException">The rows cannot be one transaction.</exception>
    internal static void RequireOnePartitionEachRowOnce(IReadOnlyList<(string PartitionKey, string RowKey)> keys, string what, string row)
    {
        if (keys.Count > BatchPlanner.DefaultMaxCount)
            throw new BatchNotAtomicException(
                $"The {what} cannot be written as one transaction: it holds {keys.Count} {row}s and one transaction holds at most {BatchPlanner.DefaultMaxCount}. Nothing was written.");
        var seen = new HashSet<(string, string)>();
        foreach (var key in keys)
        {
            if (!string.Equals(key.PartitionKey, keys[0].PartitionKey, StringComparison.Ordinal))
                throw new BatchNotAtomicException(
                    $"The {what} cannot be written as one transaction: it spans more than one partition ('{keys[0].PartitionKey}' and '{key.PartitionKey}'). Nothing was written.");
            if (!seen.Add(key))
                throw new BatchNotAtomicException(
                    $"The {what} cannot be written as one transaction: it names the {row} '{key.PartitionKey}{EntityId.Separator}{key.RowKey}' twice. Nothing was written.");
        }
    }

    /// <summary>Refuses rows whose summed size exceeds the payload budget of one transaction (<see cref="BatchPlanner"/>).</summary>
    /// <param name="sizes">The size of each row, as the batch writes estimate it.</param>
    /// <param name="what">What is being written, for the message.</param>
    /// <exception cref="BatchNotAtomicException">The rows do not fit one transaction.</exception>
    internal static void RequireOneTransaction(IReadOnlyList<long> sizes, string what)
    {
        var bytes = sizes.Sum();
        if (bytes > BatchPlanner.DefaultMaxBytes)
            throw new BatchNotAtomicException(
                $"The {what} cannot be written as one transaction: its rows hold {bytes:N0} bytes and one transaction is planned for at most {BatchPlanner.DefaultMaxBytes:N0}. Nothing was written.");
    }
}
