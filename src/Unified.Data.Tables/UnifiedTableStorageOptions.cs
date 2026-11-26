using Azure.Data.Tables;

namespace Unified.Data.Tables;

/// <summary>
/// Behavioural options for the unified table-storage layer, configured at registration time via
/// <c>AddUnifiedTableStorage(o =&gt; ...)</c>. The provider-neutral settings (cache policy, oversized
/// cells, id normalization, table names, the type discriminator) are declared on
/// <see cref="StorageOptions"/>, which the other providers take as it is; this type adds what only the
/// Azure provider has.
/// </summary>
public sealed class UnifiedTableStorageOptions : StorageOptions
{
    /// <summary>
    /// Blob offload for large values (<see cref="TableValueSplitAttribute"/> values too large for table
    /// rows, and every <see cref="TableBlobContent"/>). <c>null</c> (the default) disables it: such a
    /// value throws <see cref="OversizedValueException"/> instead. Types with neither are unaffected.
    /// It is the same instance as <see cref="StorageOptions.Offload"/>, under its Azure type.
    /// </summary>
    public BlobOffloadOptions? BlobOffload
    {
        get => (BlobOffloadOptions?)Offload;
        set => Offload = value;
    }

    /// <summary>
    /// Configures the <see cref="TableClientOptions"/> of the <see cref="TableServiceClient"/> the library
    /// builds (the connection-string and credential overloads of <c>AddUnifiedTableStorage</c>): the retry
    /// policy and its delays, a transport, diagnostics, policies of the host. It runs once, when the
    /// container builds the client, before the library adds its own policy (the retry observation of
    /// <see cref="TableClientOptionsExtensions.AddRetryObservation"/>), which it adds whatever this does.
    /// <c>null</c> (the default) leaves the SDK's defaults.
    /// </summary>
    /// <remarks>
    /// Not used for a <see cref="TableServiceClient"/> the host registers itself, which is kept as it is:
    /// configure that client where it is built, and add
    /// <see cref="TableClientOptionsExtensions.AddRetryObservation"/> to its options there. The hook of the
    /// registered options is the one that runs (the first call's, like every other option).
    /// </remarks>
    public Action<TableClientOptions>? ConfigureTableClient { get; set; }

    /// <summary>
    /// Declares what an acknowledged write survives, for <see cref="StorageInfo.Durability"/>, when the
    /// endpoint cannot say it. <c>null</c> (the default) lets the store decide from its endpoint's host:
    /// <see cref="Durability.Replicated"/> for an Azure Storage table host
    /// (<c>{account}.table.core.windows.net</c> and its sovereign-cloud equivalents),
    /// <see cref="Durability.Unknown"/> for any other (Azurite, a custom domain, a proxy, Cosmos DB). Set it
    /// for a storage account behind a custom domain, or for an emulator whose durability the host knows.
    /// The value is reported as declared; nothing checks it.
    /// </summary>
    public Durability? DeclaredDurability { get; set; }

    /// <summary>
    /// Override the cache policy for one entity type (e.g. disable for high-churn types or
    /// huge partitions).
    /// </summary>
    public new UnifiedTableStorageOptions CacheFor<T>(CachePolicy policy) where T : class, IEntity, new()
    {
        base.CacheFor<T>(policy);
        return this;
    }

    /// <summary>
    /// The Azure provider reads its offload settings as <see cref="BlobOffloadOptions"/> (they carry the
    /// blob service client), so a plain <see cref="OffloadOptions"/> is refused here instead of being
    /// silently ignored.
    /// </summary>
    /// <param name="value">The value being assigned; null disables offload.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a <see cref="BlobOffloadOptions"/>.</exception>
    private protected override void ValidateOffload(OffloadOptions? value)
    {
        if (value is not (null or BlobOffloadOptions))
            throw new ArgumentException(
                $"{nameof(UnifiedTableStorageOptions)}.{nameof(Offload)} takes a {nameof(BlobOffloadOptions)} (set {nameof(BlobOffload)}): a plain {nameof(OffloadOptions)} does not say which blob service holds the values.",
                nameof(value));
    }
}
