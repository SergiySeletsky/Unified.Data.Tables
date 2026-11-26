namespace Unified.Data.Tables.Identity
{
    /// <summary>
    /// The optimistic-concurrency steps the user and role stores share. ASP.NET Core Identity treats
    /// <c>ConcurrencyStamp</c> as the caller's version token: an update or delete applies only to the row
    /// the caller read, and an update gives the row a fresh stamp. The stamp alone cannot close the gap
    /// between the store's own read and its write, so the write is also conditional on the ETag of that
    /// read.
    /// </summary>
    internal static class ConcurrencyStamps
    {
        /// <summary>A new stamp, in the format Identity's own stores use.</summary>
        internal static string NewStamp() => Guid.NewGuid().ToString();

        /// <summary>
        /// Whether the stored stamp is the one the caller holds. Ordinal: a stamp is an opaque token, and a
        /// row and an object that both carry none (rows written before stamps were set) match.
        /// </summary>
        internal static bool Matches(string? stored, string? expected) =>
            string.Equals(stored, expected, StringComparison.Ordinal);

        /// <summary>
        /// Replaces the row in full, on condition that it is still the version <paramref name="etag"/> names.
        /// Returns false when another writer changed the row since that read or removed it; any other
        /// failure is thrown.
        /// </summary>
        internal static async Task<bool> TryReplaceAsync<TModel>(
            IStorage<TModel> storage, TModel model, string? etag, CancellationToken cancellationToken)
            where TModel : class, IEntity, new()
        {
            var id = model.Id;
            model.ETag = etag;
            try
            {
                await storage.UpdateAsync(model, ConcurrencyMode.Strict, cancellationToken);
                return true;
            }
            catch (ConcurrencyConflictException)
            {
                return false;
            }
            catch (EntityNotFoundException)
            {
                // The row was deleted after the read: it lost the race like any other concurrent writer
                // would have made it.
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not StorageException)
            {
                // A store of this library (Azure, in-memory, SQLite) reports a row deleted after the read
                // as EntityNotFoundException (above), and every other failure of it as a StorageException,
                // which is thrown as it is: none of them ever gets here. A third-party IStorage<T> may
                // still fail a conditional replace of a missing row with an exception of its own, and
                // before 0.10.0 that was a lost race, not a throw. Kept for such a store: tell that case
                // from a real failure by looking again; a row that is gone lost the race.
                bool gone;
                try
                {
                    gone = !await storage.ExistsAsync(id, cancellationToken);
                }
#pragma warning disable CA1031 // The original failure, not the probe's, is the one to report.
                catch (Exception)
#pragma warning restore CA1031
                {
                    gone = false;
                }

                if (gone)
                    return false;
                throw;
            }
        }
    }
}
