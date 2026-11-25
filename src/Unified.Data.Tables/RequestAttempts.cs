using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;

namespace Unified.Data.Tables;

/// <summary>
/// What one storage call saw of its own HTTP requests: how many attempts of each were sent and how each
/// attempt ended. It is what lets a write tell "the service refused my one request" from "an earlier
/// attempt of my request may have been applied, and its response lost".
/// </summary>
/// <remarks>
/// A member of a store that may change stored data opens one (<see cref="Begin"/>) before its first
/// storage call; <see cref="RetryObservationPolicy"/>, which sits behind the SDK's retry policy, notes
/// every attempt of every table request sent inside that call. The log is found through an
/// <see cref="AsyncLocal{T}"/>, so it follows the call and nothing else, and it is read per request: a
/// write takes a <see cref="Mark"/> before one SDK call and asks about the requests noted since
/// (<see cref="MayBeOwnWrite(int)"/>), so an ambiguous request earlier in the same call (a re-planned
/// transaction, a prior read) never labels a later, definite answer.
/// <para>
/// Nothing is noted when the client was built without the policy (a <see cref="TableServiceClient"/> the
/// host constructed itself, without <see cref="TableClientOptionsExtensions.AddRetryObservation"/>). Every
/// question is then answered conservatively: the attempts could not be observed, so an earlier one may
/// have been applied.
/// </para>
/// </remarks>
internal sealed class RequestAttempts
{
    private static readonly AsyncLocal<RequestAttempts?> Ambient = new();

    private readonly Lock gate = new();
    private readonly List<Noted> requests = [];

    /// <summary>The log of the storage call this code runs in, or null outside one.</summary>
    internal static RequestAttempts? Current => Ambient.Value;

    /// <summary>
    /// Opens the log of one storage call. Called from the async member itself, so it is visible to
    /// everything the member awaits and is gone when the member returns.
    /// </summary>
    internal static RequestAttempts Begin()
    {
        var attempts = new RequestAttempts();
        Ambient.Value = attempts;
        return attempts;
    }

    /// <summary>The position in the current call's log; requests noted later are "since" it.</summary>
    internal static int MarkCurrent() => Current?.Mark() ?? 0;

    /// <summary>
    /// Whether a duplicate or a conflict the service reported for the write requests sent since
    /// <paramref name="mark"/> may be this call's own write (see <see cref="MayBeOwnWrite(int)"/>);
    /// true outside a storage call.
    /// </summary>
    internal static bool MayBeOwnWriteSince(int mark) => Current?.MayBeOwnWrite(mark) ?? true;

    /// <summary>The position in the log now.</summary>
    internal int Mark()
    {
        lock (gate)
            return requests.Count;
    }

    /// <summary>
    /// Whether a row the service reported in the way of a write sent since <paramref name="mark"/> (a
    /// duplicate key, a failed version check, a row already gone) may be that write's own doing: true
    /// when an earlier attempt of one of those requests was sent, and true when no write request was
    /// noted at all (the client has no observation policy). False only when every write request since
    /// the mark was sent exactly once.
    /// </summary>
    internal bool MayBeOwnWrite(int mark)
    {
        lock (gate)
        {
            var observed = false;
            for (var i = Math.Max(mark, 0); i < requests.Count; i++)
            {
                if (!requests[i].Write)
                    continue;
                if (requests[i].Attempts > 1)
                    return true;
                observed = true;
            }
            return !observed;
        }
    }

    /// <summary>
    /// Whether the call may have changed stored rows although it failed: false only when requests were
    /// observed and every attempt of every write request among them was definitely rejected by the
    /// service (or no write request was sent at all, the failure came from a read before it).
    /// </summary>
    internal bool MayHaveCommitted
    {
        get
        {
            lock (gate)
            {
                if (requests.Count == 0)
                    return true;
                foreach (var request in requests)
                {
                    if (request.Write && request.MayHaveBeenApplied)
                        return true;
                }
                return false;
            }
        }
    }

    /// <summary>Notes that an attempt of <paramref name="message"/> is about to be sent; null when this attempt is already being noted.</summary>
    internal Noted? Sending(HttpMessage message)
    {
        lock (gate)
        {
            // Another attempt of a request already noted is the same message, sent again by the retry
            // policy (the SDK's, or one the host put in its place).
            Noted? noted = null;
            for (var i = requests.Count - 1; i >= 0 && noted is null; i--)
            {
                if (ReferenceEquals(requests[i].Message.Target, message))
                    noted = requests[i];
            }
            if (noted is null)
            {
                noted = new Noted(message, IsRowWrite(message.Request));
                requests.Add(noted);
            }
            // The policy added twice to one pipeline sees each attempt twice: counted once.
            if (noted.InFlight)
                return null;
            noted.InFlight = true;
            noted.Attempts++;
            return noted;
        }
    }

    /// <summary>Notes how the attempt ended: with a response of <paramref name="status"/>, or with none (0).</summary>
    internal void Ended(Noted noted, int status, string? errorCode)
    {
        lock (gate)
        {
            noted.InFlight = false;
            if (!DefinitelyRejected(status, errorCode))
                noted.MayHaveBeenApplied = true;
        }
    }

    // A request that can change rows. Reads cannot; nor does the lazy creation of the table itself
    // (POST {account}/Tables), which every store sends once and which stores no row.
    private static bool IsRowWrite(Request request)
    {
        var method = request.Method;
        if (method == RequestMethod.Get || method == RequestMethod.Head || method == RequestMethod.Options)
            return false;
        return !(method == RequestMethod.Post && request.Uri.Path.TrimEnd('/').EndsWith("Tables", StringComparison.Ordinal));
    }

    // The answers after which a request is known not to have been applied: a client error other than a
    // request timeout (the service refused it: 400, 401, 403, 404, 409, 412, 413, 429, ...), and the
    // service's own "server busy" (503 ServerBusy), which it sends instead of processing the request.
    // Everything else is not proof: no response at all, a success, a server error (the service documents
    // 500 OperationTimedOut as "may or may not have succeeded"), a gateway's 502, 503 or 504.
    private static bool DefinitelyRejected(int status, string? errorCode) =>
        (status is >= 400 and < 500 and not 408)
        || (status == 503 && string.Equals(errorCode, "ServerBusy", StringComparison.Ordinal));

    /// <summary>One request of the call, with what its attempts showed.</summary>
    internal sealed class Noted(HttpMessage message, bool write)
    {
        // Weak: the log must not keep a request (and its body) alive for as long as the storage call runs.
        public WeakReference Message { get; } = new(message);

        public bool Write { get; } = write;

        public int Attempts { get; set; }

        public bool InFlight { get; set; }

        public bool MayHaveBeenApplied { get; set; }
    }
}

/// <summary>
/// The pipeline policy that notes every attempt of a table request in the log of the storage call it
/// belongs to (<see cref="RequestAttempts"/>). Added per retry, so it runs once for each attempt the
/// SDK's retry policy sends. It changes nothing about the request or the response, and does nothing for
/// a request sent outside a storage call of this library.
/// </summary>
internal sealed class RetryObservationPolicy : HttpPipelinePolicy
{
    /// <summary>The one instance: it holds no state of its own.</summary>
    internal static readonly RetryObservationPolicy Instance = new();

    private RetryObservationPolicy()
    {
    }

    /// <inheritdoc />
    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        var attempts = RequestAttempts.Current;
        if (attempts?.Sending(message) is not { } noted)
        {
            await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
            return;
        }

        try
        {
            await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No response: the request may have reached the service all the same.
            attempts.Ended(noted, 0, null);
            throw;
        }
        End(attempts, noted, message);
    }

    /// <inheritdoc />
    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        var attempts = RequestAttempts.Current;
        if (attempts?.Sending(message) is not { } noted)
        {
            ProcessNext(message, pipeline);
            return;
        }

        try
        {
            ProcessNext(message, pipeline);
        }
        catch (Exception)
        {
            attempts.Ended(noted, 0, null);
            throw;
        }
        End(attempts, noted, message);
    }

    private static void End(RequestAttempts attempts, RequestAttempts.Noted noted, HttpMessage message)
    {
        if (!message.HasResponse)
        {
            attempts.Ended(noted, 0, null);
            return;
        }
        var response = message.Response;
        attempts.Ended(noted, response.Status, response.Headers.TryGetValue("x-ms-error-code", out var code) ? code : null);
    }
}

/// <summary>
/// Lets a <see cref="TableServiceClient"/> the host builds itself tell the storage layer how many
/// attempts of a request were sent.
/// </summary>
public static class TableClientOptionsExtensions
{
    /// <summary>
    /// Adds the retry observation of the unified storage layer to <paramref name="options"/>: a pipeline
    /// policy that notes each attempt the SDK's retry policy sends. The clients the library builds itself
    /// (the connection-string and credential overloads of <c>AddUnifiedTableStorage</c>) have it already.
    /// </summary>
    /// <remarks>
    /// With it, a store over the client reports what it saw: <see cref="DuplicateKeyException.MayBeOwnWrite"/>
    /// and <see cref="ConcurrencyConflictException.MayBeOwnWrite"/> are false when exactly one attempt of the
    /// request was sent, <see cref="TransientStorageException.MayHaveCommitted"/> is false when every
    /// attempt was definitely rejected, and a conditional delete (<see cref="IConditionalDelete{T}"/>)
    /// answers <see cref="DeleteStatus.NotFound"/> for a row that was not there. Without it the attempts
    /// cannot be observed and each of those is answered conservatively (true, true and
    /// <see cref="DeleteStatus.Indeterminate"/>). The policy changes no request and no response; adding it
    /// twice counts each attempt once.
    /// </remarks>
    /// <param name="options">The options a <see cref="TableServiceClient"/> is about to be built with.</param>
    /// <returns><paramref name="options"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static TableClientOptions AddRetryObservation(this TableClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddPolicy(RetryObservationPolicy.Instance, HttpPipelinePosition.PerRetry);
        return options;
    }
}
