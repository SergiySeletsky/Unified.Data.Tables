using Azure.Data.Tables;

namespace Unified.Data.Tables;

// Coalesced lazy CreateIfNotExists, shared by TableStorage<T> and PolymorphicTableStorage<TBase>.
// Three properties make this subtle enough that two hand-maintained copies would drift: no network
// I/O at construction/DI-resolve time; ONE create per store shared by all concurrent callers; and a
// FAILED attempt is forgotten so the next call retries instead of poisoning the store for the
// process lifetime.
internal sealed class TableInitializer(TableClient client)
{
    private readonly object initLock = new();
    private Task? tableInit;

    internal Task EnsureAsync(CancellationToken ct)
    {
        var existing = Volatile.Read(ref tableInit);
        return existing is { IsCompletedSuccessfully: true } ? Task.CompletedTask : EnsureSlowAsync(ct);
    }

    private async Task EnsureSlowAsync(CancellationToken ct)
    {
        Task pending;
        lock (initLock)
        {
            // Reuse an in-flight or succeeded attempt; start fresh after a failed/canceled one.
            pending = tableInit is { IsFaulted: false, IsCanceled: false }
                ? tableInit
                // The shared operation deliberately ignores the first caller's token — a canceled
                // caller must not cancel (and thereby poison) everyone else's init.
                : tableInit = client.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);
        }

        try
        {
            await pending.WaitAsync(ct);
        }
        catch
        {
            if (pending.IsFaulted || pending.IsCanceled)
            {
                lock (initLock)
                {
                    if (ReferenceEquals(tableInit, pending))
                        tableInit = null;
                }
            }
            throw;
        }
    }
}
