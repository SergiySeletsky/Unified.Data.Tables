namespace Unified.Data.Tables;

// TableBlobContent and the read cache: a cached entity can outlive its blob (another process replaced
// the file, or this process's own write committed but threw and a sweep later removed the previous
// blob). Its lazy content then fails with "read the entity again", and that advice has to work: once a
// content this store materialized finds its blob missing, the store stops answering the row from its
// caches, so the next OneAsync or partition query reads the table (BLOBS-4 of the fix-wave-2 review).
public partial class TableStorage<T>
{
    // Called by the large-value layer (on the thread of the caller's OpenReadAsync) with the keys of the
    // row whose content found its blob missing. Evicts the entity's entry and the partition's cached
    // lists through the same helpers a write uses; the integrity failure is thrown by the caller either way.
    private void ForgetRowWithMissingBlob(string partitionKey, string rowKey)
    {
        if (cache is null)
            return;
        cache.Remove(EntityCacheKey($"{partitionKey}{Separator}{rowKey}"));
        InvalidateQueryCache(partitionKey);
    }
}
