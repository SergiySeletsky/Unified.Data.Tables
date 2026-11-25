namespace Unified.Data.Tables;

/// <summary>
/// What a conditional delete (<see cref="IConditionalDelete{T}"/>) found. A plain
/// <see cref="IStorage{T}.DeleteAsync"/> says nothing about the row; this says whether this call removed it.
/// </summary>
public enum DeleteStatus
{
    /// <summary>The row existed and this call removed it.</summary>
    Deleted,

    /// <summary>No row had the id, and no earlier attempt of this call can have removed it.</summary>
    NotFound,

    /// <summary>
    /// The row is gone, but the provider cannot tell whether this call removed it or it was already
    /// missing: an earlier attempt of the same request was sent (the Azure SDK retries after a timeout or
    /// a dropped connection) and its response was lost, so the attempt that answered found nothing. Treat
    /// it as "gone, possibly by me": a caller that runs a side effect only for a delete it performed must
    /// decide by its own record, not by this result. A provider that never retries a request (the
    /// in-memory one) never returns it.
    /// <para>
    /// It is also the answer for a row that is gone when the provider cannot observe the attempts of its
    /// own request, and so cannot rule the lost attempt out: on the Azure provider, a delete request
    /// answered "not found" over a <c>TableServiceClient</c> that was built without the library's retry
    /// observation (<c>TableClientOptionsExtensions.AddRetryObservation</c>; the clients
    /// <c>AddUnifiedTableStorage</c> builds have it). Such a store answers <see cref="NotFound"/> only when
    /// it sent no delete at all.
    /// </para>
    /// </summary>
    Indeterminate,
}
