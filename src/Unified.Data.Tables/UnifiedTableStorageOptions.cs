namespace Unified.Data.Tables;

/// <summary>
/// Behavioural options for the unified table-storage layer, configured at registration time via
/// <c>AddUnifiedTableStorage(o =&gt; ...)</c>. Cache policy is a DEPLOYMENT concern, not an entity
/// concern — the same entity type may be cached in one host and uncached in another process that
/// shares its tables — which is why it lives here and not in an attribute on the entity.
/// </summary>
public sealed class UnifiedTableStorageOptions
{
    private readonly Dictionary<Type, CachePolicy> overrides = [];

    /// <summary>
    /// Default cache policy for all entity types. Defaults to <c>CachePolicy.Sliding(1h)</c> —
    /// the historical 0.2.x behaviour.
    /// </summary>
    public CachePolicy Cache { get; set; } = CachePolicy.Sliding(TimeSpan.FromHours(1));

    /// <summary>
    /// What happens when a serialized payload exceeds the 64 KB cell cap even compressed —
    /// see <see cref="OversizedCellPolicy"/>. Default: trim and leave a <c>__Truncated</c> marker.
    /// Applied to the (static) serializer at registration time.
    /// </summary>
    public OversizedCellPolicy OversizedCells { get; set; } = OversizedCellPolicy.TrimWithMarker;

    /// <summary>
    /// How ids and partition/prefix arguments are treated before hitting the table — see
    /// <see cref="IdNormalization"/>. Default: <see cref="IdNormalization.Normalized"/> (trim,
    /// spaces to '-', lower-case). Use <see cref="IdNormalization.AsWritten"/> for tables whose
    /// keys are case-sensitive payloads or pre-existing data written by another layer.
    /// </summary>
    public IdNormalization IdNormalization { get; set; } = IdNormalization.Normalized;

    /// <summary>
    /// Override the cache policy for one entity type (e.g. disable for high-churn types or
    /// huge partitions).
    /// </summary>
    public UnifiedTableStorageOptions CacheFor<T>(CachePolicy policy) where T : Entity, new()
    {
        ArgumentNullException.ThrowIfNull(policy);
        overrides[typeof(T)] = policy;
        return this;
    }

    internal CachePolicy ResolveCachePolicy(Type entityType) =>
        overrides.TryGetValue(entityType, out var policy) ? policy : Cache;
}
