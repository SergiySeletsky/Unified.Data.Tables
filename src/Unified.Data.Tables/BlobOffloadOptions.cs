using Azure.Storage.Blobs;

namespace Unified.Data.Tables;

/// <summary>
/// Blob offload for large values: where values that are too large for table rows are stored, and
/// when a value marked <see cref="TableValueSplitAttribute"/> moves there. A <see cref="TableBlobContent"/>
/// property is always stored there. Set <see cref="UnifiedTableStorageOptions.BlobOffload"/> to enable it.
/// The container, the threshold and the cleanup grace are declared on <see cref="OffloadOptions"/>, the
/// provider-neutral part every provider reads; this type adds the Azure blob service that holds the container.
/// </summary>
public sealed class BlobOffloadOptions : OffloadOptions
{
    /// <summary>
    /// The blob service that holds the container. In DI it takes precedence over the host's registered
    /// client (<see cref="UseRegisteredServiceClient"/>) and over the one the connection-string or credential
    /// overload of <c>AddUnifiedTableStorage</c> derives. A <c>TableStorage&lt;T&gt;</c> constructed directly
    /// uses it only when no <see cref="BlobServiceClient"/> is passed to the constructor: an explicit
    /// constructor argument wins. <c>null</c> (the default) leaves the choice to those. The in-memory store
    /// ignores it.
    /// </summary>
    public BlobServiceClient? ServiceClient { get; set; }

    /// <summary>
    /// When <c>true</c>, <c>AddUnifiedTableStorage</c> uses the <see cref="BlobServiceClient"/> the host
    /// registers in DI (before or after the call) for offload, unless <see cref="ServiceClient"/> is set.
    /// Defaults to <c>false</c>: the library then never resolves the host's client and uses only
    /// <see cref="ServiceClient"/> or the client its connection-string or credential overload derives, which it
    /// keeps to itself, so the host's own <see cref="BlobServiceClient"/> registration is never taken or replaced.
    /// </summary>
    /// <remarks>
    /// Register the host's <see cref="BlobServiceClient"/> as a singleton. Every <c>TableStorage&lt;T&gt;</c> is a
    /// singleton that takes the offload client when it is constructed, so with a scoped registration every
    /// store, a plain one too, fails to resolve under <c>ValidateScopes</c> ("Cannot resolve scoped service ...
    /// from root provider"), and without <c>ValidateScopes</c> the stores keep the root scope's instance.
    /// </remarks>
    public bool UseRegisteredServiceClient { get; set; }
}
