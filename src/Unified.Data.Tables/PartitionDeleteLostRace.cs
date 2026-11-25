using Azure;
using Azure.Data.Tables;

namespace Unified.Data.Tables;

/// <summary>
/// What a partition delete does with a delete transaction the service refused because one of its rows is
/// missing (a transaction of which nothing was applied): the rows are deleted one by one, and the ones
/// this call did not remove are counted, so the call can leave them out of what it reports as deleted.
/// </summary>
internal static class PartitionDeleteLostRace
{
    /// <summary>
    /// Deletes <paramref name="rows"/> one by one, whatever their version, and returns how many of them
    /// were already gone by another writer's doing.
    /// </summary>
    /// <remarks>
    /// A row that answers 404 was not deleted by its request. Whether this call deleted it all the same is
    /// read from what the call saw of its own requests (<see cref="RequestAttempts"/>):
    /// <list type="bullet">
    ///   <item><description>The row's own delete was sent again after an attempt that may have been
    ///   applied: that attempt removed it, and the row counts as deleted by this call.</description></item>
    ///   <item><description>The transaction (sent since <paramref name="transactionSent"/>) was itself sent
    ///   again after an attempt that may have been applied, and every one of its rows is gone: that attempt
    ///   was applied, its response was lost, and the 404 of the retry was this call meeting its own
    ///   delete. All the rows count as deleted by this call. (Another writer deleting exactly these rows,
    ///   all of them, in the same moment would look the same and is counted here too.)</description></item>
    ///   <item><description>Otherwise the row was deleted by another writer after the listing: a lost race.
    ///   On a client without the retry observation nothing is seen, and every such row is read this
    ///   way, so after a transaction whose response was lost the count is a lower bound there.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="client">The table.</param>
    /// <param name="rows">The rows of the refused transaction.</param>
    /// <param name="transactionSent">The position in the call's log just before the transaction was sent.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of rows this call did not delete.</returns>
    internal static async Task<int> DeleteOneByOneAsync(
        TableClient client, IReadOnlyCollection<TableEntity> rows, int transactionSent, CancellationToken ct)
    {
        var transactionRetriedInDoubt = RequestAttempts.RetriedInDoubtSince(transactionSent);
        var goneMeanwhile = 0;
        foreach (var row in rows)
        {
            var sent = RequestAttempts.MarkCurrent();
            // The SDK returns the 404 of a row that is gone instead of throwing it.
            var response = await client.DeleteEntityAsync(row.PartitionKey, row.RowKey, ETag.All, ct).ConfigureAwait(false);
            if (IsNotFound(response) && !RequestAttempts.RetriedInDoubtSince(sent))
                goneMeanwhile++;
        }
        return transactionRetriedInDoubt && goneMeanwhile == rows.Count ? 0 : goneMeanwhile;
    }

    // True for a 404 the SDK returned instead of throwing; tolerates the responses of test doubles.
    private static bool IsNotFound(Response? response)
    {
#pragma warning disable CA1031 // a response that cannot be read (a test double) is not a 404
        try
        {
            return response is not null && response.Status == 404;
        }
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }
}
