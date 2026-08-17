using Azure.Data.Tables;

namespace Unified.Data.Tables;

// Shared by TableStorage<T> and PolymorphicTableStorage<TBase>. Extracted rather than duplicated
// because the two batch planners must measure identically: a divergence would let one store send a
// transaction the other considers oversized, and the failure surfaces as an HTTP 413 partway
// through a bulk write that has already committed earlier chunks.
internal static class TableRowSize
{
    /// <summary>
    /// Serialized size of one row, for transaction planning. Binary and string columns dominate; the
    /// fixed per-property and per-entity overhead is approximated rather than computed exactly,
    /// because the budget already sits well under the service limit to absorb it.
    /// </summary>
    internal static long Estimate(TableEntity row)
    {
        // Azure's own accounting: ~88 B of entity overhead plus 8 B per property, before values.
        var bytes = 88L + (row.Count * 8L);
        foreach (var key in row.Keys)
        {
            bytes += key.Length * 2L;
            bytes += row[key] switch
            {
                byte[] binary => binary.Length,
                BinaryData binary => binary.ToMemory().Length,
                string text => text.Length * 2L,
                _ => 8L,
            };
        }

        return bytes;
    }
}
