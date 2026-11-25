using System.Globalization;
using System.Runtime.CompilerServices;
using Azure;

namespace Unified.Data.Tables;

/// <summary>
/// The one place an Azure SDK failure becomes a provider-neutral <see cref="StorageException"/>. Every
/// member of <c>TableStorage&lt;T&gt;</c>, <c>PolymorphicTableStorage&lt;TBase&gt;</c> and the offload
/// blob store passes what it could not handle itself through <see cref="Map"/> (or
/// <see cref="MapBlob"/>) before it leaves, so no caller sees a <see cref="RequestFailedException"/>;
/// the SDK's exception is always the <see cref="Exception.InnerException"/>.
/// </summary>
/// <remarks>
/// The table of what maps to what:
/// <list type="bullet">
///   <item><description>404 with the code <c>TableNotFound</c>: <see cref="TableNotFoundException"/>, never a missing row.</description></item>
///   <item><description>404 for a row (<c>ResourceNotFound</c>, <c>EntityNotFound</c>, or no code at all): <see cref="EntityNotFoundException"/>.</description></item>
///   <item><description>409 for a row (<c>EntityAlreadyExists</c>, or no code): <see cref="DuplicateKeyException"/>.</description></item>
///   <item><description>412: <see cref="ConcurrencyConflictException"/>.</description></item>
///   <item><description>400 and 413: <see cref="StorageRequestRejectedException"/>.</description></item>
///   <item><description>408, 429, 500, 502, 503, 504, a failure without a response (status 0), the SDK's
///   "retry failed" aggregate and a cancellation the caller did not ask for (the SDK's network timeout):
///   <see cref="TransientStorageException"/>. Every <see cref="OperationCanceledException"/> raised while
///   the caller's token is not cancelled is read as that timeout, whatever raised it: the attempts of a
///   request are observed (<see cref="RequestAttempts"/>) only on a client that has the observation
///   policy, and the table's lazy creation is one request shared by every waiting call, so "no attempt
///   of this call timed out" cannot be told from "this call saw none of them".</description></item>
///   <item><description>Anything else: <see cref="StorageException"/> itself.</description></item>
/// </list>
/// A failure of the blob store never says a row or a table is missing, a duplicate or in conflict: its
/// 404, 409 and 412 are plain <see cref="StorageException"/>s.
/// </remarks>
internal static class AzureStorageErrors
{
    /// <summary>
    /// True when <paramref name="ex"/> is a failure of the Azure SDK that <see cref="Map"/> turns into a
    /// neutral exception: a <see cref="RequestFailedException"/> (a transaction's failure is one), the
    /// aggregate the SDK throws when its retries are used up, or a cancellation while
    /// <paramref name="ct"/>, the caller's token, is not cancelled (the SDK's network timeout). False for
    /// a failure that is neutral already, for the caller's own cancellation and for everything that is
    /// not the provider's (an argument error, an integrity failure, the caller's stream).
    /// </summary>
    internal static bool IsProviderFailure(Exception ex, CancellationToken ct) => ex switch
    {
        StorageException => false,
        RequestFailedException => true,
        OperationCanceledException => !ct.IsCancellationRequested,
        AggregateException aggregate => IsRetriesUsedUp(aggregate),
        _ => false,
    };

    // The SDK's retry policy throws an AggregateException of its attempts' failures when several
    // attempts failed without a response (each one a RequestFailedException with status 0, or the
    // cancellation of its network timeout). An aggregate the library builds itself (an orphan sweep's
    // failures, one per blob or partition) holds neutral exceptions by then and is left as it is.
    private static bool IsRetriesUsedUp(AggregateException aggregate) =>
        aggregate.InnerExceptions.Count > 0
        && aggregate.InnerExceptions.All(inner => inner is RequestFailedException or OperationCanceledException or IOException);

    /// <summary>
    /// The neutral exception for a table-side failure (see the table on this class).
    /// </summary>
    /// <param name="ex">The SDK's failure (<see cref="IsProviderFailure"/> is true for it).</param>
    /// <param name="entityType">The entity type name the neutral exception names.</param>
    /// <param name="tableName">The table the request was sent to.</param>
    /// <param name="id">The id of the row the call addressed, when it addressed one.</param>
    /// <param name="write">
    /// True when the call may change stored data. A transient failure of it may have committed, and a
    /// duplicate or a conflict may be the call's own write, applied by an attempt the SDK retried.
    /// </param>
    /// <param name="attempts">
    /// What the call saw of its own requests (<see cref="RequestAttempts"/>), or null when it kept no
    /// log. Without one, and with a log that noted nothing (the client has no observation policy), both
    /// flags of a write are set conservatively. With one, a transient failure of a write has not
    /// committed when every attempt of every write request was definitely rejected, and a duplicate or a
    /// conflict is not the call's own write when no write request of the call was sent again after an
    /// attempt that may have been applied. (A member
    /// that knows which request failed asks about that request alone; this is the answer for a failure
    /// nothing caught on its way out.)
    /// </param>
    internal static StorageException Map(
        Exception ex, string entityType, string tableName, string? id, bool write, RequestAttempts? attempts = null)
    {
        var mayBeOwnWrite = write && (attempts?.MayBeOwnWrite(0) ?? true);
        var mayHaveCommitted = write && (attempts?.MayHaveCommitted ?? true);
        if (ex is not RequestFailedException failure)
            return Transient(ex, null, entityType, write, mayHaveCommitted);

        var code = failure.ErrorCode;
        switch (failure.Status)
        {
            case 404 when string.Equals(code, "TableNotFound", StringComparison.Ordinal):
                return new TableNotFoundException($"Table '{tableName}' does not exist.", tableName, failure) { ProviderErrorCode = code };
            case 404 when IsMissingRow(failure):
                return id is null
                    ? new EntityNotFoundException($"{entityType}: a row the request needs does not exist.", failure) { ProviderErrorCode = code }
                    : new EntityNotFoundException(entityType, id, failure) { ProviderErrorCode = code };
            case 409 when string.IsNullOrEmpty(code) || string.Equals(code, "EntityAlreadyExists", StringComparison.Ordinal):
                return Duplicate(entityType, id, failure, mayBeOwnWrite);
            case 412:
                return Conflict(entityType, id, failure, mayBeOwnWrite);
            case 400 or 413:
                return new StorageRequestRejectedException(
                    $"{entityType}: the storage service rejected the request ({Describe(failure)}). Sending it again fails the same way.", code, failure);
            case 0 or 408 or 429 or 500 or 502 or 503 or 504:
                return Transient(failure, failure, entityType, write, mayHaveCommitted);
            default:
                return new StorageException($"{entityType}: the storage request failed ({Describe(failure)}).", code, failure);
        }
    }

    /// <summary>
    /// The neutral exception for a failure of the blob store that holds offloaded values. Its 404, 409
    /// and 412 say nothing about a row or a table, so they are plain <see cref="StorageException"/>s.
    /// </summary>
    /// <param name="ex">The SDK's failure (<see cref="IsProviderFailure"/> is true for it).</param>
    /// <param name="containerName">The blob container the request was sent to.</param>
    /// <param name="write">True when the call may change a stored blob.</param>
    internal static StorageException MapBlob(Exception ex, string? containerName, bool write)
    {
        var subject = $"Blob container '{containerName}'";
        if (ex is not RequestFailedException failure)
            return Transient(ex, null, subject, write, mayHaveCommitted: write);

        return failure.Status switch
        {
            400 or 413 => new StorageRequestRejectedException(
                $"{subject}: the blob service rejected the request ({Describe(failure)}). Sending it again fails the same way.", failure.ErrorCode, failure),
            0 or 408 or 429 or 500 or 502 or 503 or 504 => Transient(failure, failure, subject, write, mayHaveCommitted: write),
            _ => new StorageException($"{subject}: the blob request failed ({Describe(failure)}).", failure.ErrorCode, failure),
        };
    }

    /// <summary>
    /// A duplicate key the service reported for a write (409, or a transaction's <c>InvalidDuplicateRow</c>).
    /// </summary>
    internal static DuplicateKeyException Duplicate(string entityType, string? id, Exception? inner, bool mayBeOwnWrite)
    {
        var duplicate = id is null
            ? new DuplicateKeyException($"Duplicate key creating {entityType}: a row with this id already exists.", inner)
            : new DuplicateKeyException(entityType, id, inner);
        duplicate.MayBeOwnWrite = mayBeOwnWrite;
        duplicate.ProviderErrorCode = (inner as RequestFailedException)?.ErrorCode;
        return duplicate;
    }

    /// <summary>A failed version check the service reported for a write (412), or a write that kept losing races.</summary>
    internal static ConcurrencyConflictException Conflict(string entityType, string? id, Exception? inner, bool mayBeOwnWrite)
    {
        var conflict = id is null
            ? new ConcurrencyConflictException(
                $"Concurrency conflict updating {entityType}: the row was modified by another writer since it was read.", inner)
            : new ConcurrencyConflictException(entityType, id, inner);
        conflict.MayBeOwnWrite = mayBeOwnWrite;
        conflict.ProviderErrorCode = (inner as RequestFailedException)?.ErrorCode;
        return conflict;
    }

    /// <summary>A row the service reported as missing (404) to a write that needs it.</summary>
    internal static EntityNotFoundException NotFound(string entityType, string id, RequestFailedException? inner) =>
        new(entityType, id, inner) { ProviderErrorCode = inner?.ErrorCode };

    /// <summary>
    /// A 404 that says a row is gone: the service's code for a missing entity (<c>ResourceNotFound</c>;
    /// <c>EntityNotFound</c> is the other one it documents), or no code at all. A 404 that names anything
    /// else is not a missing row: when the table itself is gone (<c>TableNotFound</c>) reading again
    /// cannot help.
    /// </summary>
    internal static bool IsMissingRow(RequestFailedException ex) =>
        ex.Status == 404 && (string.IsNullOrEmpty(ex.ErrorCode) || ex.ErrorCode is "ResourceNotFound" or "EntityNotFound");

    /// <summary>
    /// <paramref name="source"/>, with every SDK failure its enumeration throws mapped by
    /// <paramref name="map"/>. The caller's token is the one the enumeration is cancelled with (the
    /// method's own, or the one given to <c>WithCancellation</c>): a cancellation under it is left as it is.
    /// </summary>
    internal static async IAsyncEnumerable<TItem> GuardAsync<TItem>(
        IAsyncEnumerable<TItem> source, Func<Exception, StorageException> map, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var items = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                try
                {
                    if (!await items.MoveNextAsync().ConfigureAwait(false))
                        break;
                }
                catch (Exception ex) when (IsProviderFailure(ex, ct))
                {
                    throw map(ex);
                }
                yield return items.Current;
            }
        }
        finally
        {
            await items.DisposeAsync().ConfigureAwait(false);
        }
    }

    // inner: the exception the neutral one wraps (the SDK's own, whatever its type); failure: the same
    // one when it is a RequestFailedException, for its code and its Retry-After header.
    // mayHaveCommitted: false for a read, and for a write whose every attempt was seen to be rejected.
    private static TransientStorageException Transient(
        Exception inner, RequestFailedException? failure, string subject, bool write, bool mayHaveCommitted)
    {
        var what = failure is null
            ? inner is OperationCanceledException ? "the request timed out" : "the request failed after the SDK's retries"
            : failure.Status == 0 ? "the request got no response" : $"the service could not serve the request ({Describe(failure)})";
        var outcome = !write
            ? "It may succeed when tried again."
            : mayHaveCommitted
                ? "It may have been applied: read the row before repeating a write that is not idempotent."
                : "No attempt of it was applied; it may succeed when tried again.";
        return new TransientStorageException($"{subject}: {what}. {outcome}", mayHaveCommitted, RetryAfter(failure), inner)
        {
            ProviderErrorCode = failure?.ErrorCode,
        };
    }

    private static string Describe(RequestFailedException failure) =>
        string.IsNullOrEmpty(failure.ErrorCode)
            ? failure.Status.ToString(CultureInfo.InvariantCulture)
            : $"{failure.Status.ToString(CultureInfo.InvariantCulture)} {failure.ErrorCode}";

    /// <summary>
    /// The wait the service asked for: the millisecond headers Azure services send
    /// (<c>retry-after-ms</c>, <c>x-ms-retry-after-ms</c>) or the standard <c>Retry-After</c>, in seconds
    /// or as an HTTP date. Null when the failure has no response, the response has none of them, or the
    /// value cannot be read.
    /// </summary>
    internal static TimeSpan? RetryAfter(RequestFailedException? failure)
    {
#pragma warning disable CA1031 // a response whose headers cannot be read (a test double) gives no hint; the failure is reported either way
        try
        {
            if (failure?.GetRawResponse() is not { } response)
                return null;
            foreach (var name in MillisecondHeaders)
            {
                if (response.Headers.TryGetValue(name, out var ms)
                    && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
                    && milliseconds >= 0)
                    return TimeSpan.FromMilliseconds(milliseconds);
            }
            if (!response.Headers.TryGetValue("Retry-After", out var value) || string.IsNullOrWhiteSpace(value))
                return null;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                // Counted from the service's own clock when the response carries it.
                var wait = at - (response.Headers.Date ?? DateTimeOffset.UtcNow);
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private static readonly string[] MillisecondHeaders = ["retry-after-ms", "x-ms-retry-after-ms"];
}
