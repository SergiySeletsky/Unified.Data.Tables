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
