using Azure;

namespace Unified.Data.Tables;

/// <summary>
/// A key that would change the address of a request that names it in its URL: one with a forward slash
/// (<c>/</c>). The service refuses <c>/</c> in a <c>PartitionKey</c> or a <c>RowKey</c>, so no stored row
/// has such a key, but a point read, delete, upsert or update of one is sent to another address: the
/// emulator answers it with a 500, which reads as a transient failure that may have committed. It is
/// answered before the request instead, as the local providers answer it (<c>ServiceErrors.RefusedKey</c>):
/// a point read finds nothing, a delete deletes nothing, a write is refused as the service refuses a create
/// of the key (400 <c>InvalidInput</c>). The other characters the service refuses in a key (<c>\</c>,
/// <c>#</c>, <c>?</c>, control characters) are escaped in the URL and already answered the same way on
/// every provider, and a request that carries the key in its body (a create, a batch) is refused by the
/// service itself, so neither is checked here.
/// </summary>
internal static class RefusedKeyCharacters
{
    /// <summary>True when either key holds a <c>/</c>.</summary>
    internal static bool In(string partitionKey, string rowKey) => Has(partitionKey) || Has(rowKey);

    /// <summary>Throws the service's refusal (400 <c>InvalidInput</c>) when either key holds a <c>/</c>.</summary>
    /// <exception cref="RequestFailedException">A key holds a <c>/</c>; the store maps it to <see cref="StorageRequestRejectedException"/>.</exception>
    internal static void ThrowIfIn(string partitionKey, string rowKey)
    {
        var refused = Has(partitionKey) ? "PartitionKey" : Has(rowKey) ? "RowKey" : null;
        if (refused is not null)
            throw new RequestFailedException(400,
                $"The '{refused}' holds a character the service refuses in a key ('/').",
                "InvalidInput", null);
    }

    private static bool Has(string key) => key.Contains('/', StringComparison.Ordinal);
}
