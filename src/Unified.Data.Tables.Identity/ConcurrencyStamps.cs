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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A conditional replace of a row that was deleted after the read fails with the backend's
                // own not-found error (a 404 on Azure Tables), which the abstractions do not map. Tell that
                // case from a real failure by looking again: a row that is gone lost the race like any other
                // concurrent writer would have made it.
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
