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

    // The log keeps no request: each one carries its own note (Noted, a property of its HttpMessage),
    // and the log holds only what its questions need, so a call that sends a hundred thousand requests
    // (a partition delete over millions of rows) costs it nothing more than one that sends two.
    private readonly Lock gate = new();
    // Requests noted so far; a request's position is the count when it was first sent.
    private int count;
    // The position of the last write request noted, or -1.
    private int lastWrite = -1;
    // The position of the last write request of which an attempt was sent after an earlier attempt that
    // may have been applied, or -1.
    private int lastWriteRetriedInDoubt = -1;
    // Whether an attempt of any write request may have been applied.
    private bool writeMayHaveBeenApplied;

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

    /// <summary>
    /// Whether a write request sent since <paramref name="mark"/> was seen to be sent again after an
    /// attempt that may have been applied (see <see cref="RetriedInDoubt(int)"/>); false outside a
    /// storage call and on a client without the observation, where nothing is seen.
    /// </summary>
    internal static bool RetriedInDoubtSince(int mark) => Current?.RetriedInDoubt(mark) ?? false;

    /// <summary>The position in the log now.</summary>
    internal int Mark()
    {
        lock (gate)
            return count;
    }

    /// <summary>
    /// Whether a row the service reported in the way of a write sent since <paramref name="mark"/> (a
    /// duplicate key, a failed version check, a row already gone) may be that write's own doing: true
    /// when one of those requests was sent again after an attempt that may have been applied (one that
    /// ended without a response, or with an answer that is no proof of a refusal), and true when no write
    /// request was noted at all (the client has no observation policy). False when every write request
    /// since the mark was sent exactly once, and when every earlier attempt of each was definitely
    /// rejected by the service (throttled, or refused as busy): such an attempt wrote nothing.
    /// </summary>
    internal bool MayBeOwnWrite(int mark)
    {
        lock (gate)
        {
            mark = Math.Max(mark, 0);
            return lastWriteRetriedInDoubt >= mark || lastWrite < mark;
        }
    }

    /// <summary>
    /// Whether a write request sent since <paramref name="mark"/> was sent again after an attempt of it
    /// that may have been applied. Unlike <see cref="MayBeOwnWrite(int)"/> it is false when nothing was
    /// observed: it says what was seen, not what cannot be ruled out.
    /// </summary>
    internal bool RetriedInDoubt(int mark)
    {
        lock (gate)
            return lastWriteRetriedInDoubt >= Math.Max(mark, 0);
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
                return count == 0 || writeMayHaveBeenApplied;
        }
    }

    /// <summary>Notes that an attempt of <paramref name="message"/> is about to be sent; null when this attempt is already being noted.</summary>
    internal Noted? Sending(HttpMessage message)
    {
        lock (gate)
        {
            // Another attempt of a request already noted is the same message, sent again by the retry
            // policy (the SDK's, or one the host put in its place): it carries its note. A note of
            // another log is not this call's (a message is not expected to cross calls).
            if (!message.TryGetProperty(typeof(Noted), out var carried) || carried is not Noted noted || !ReferenceEquals(noted.Log, this))
            {
                noted = new Noted(this, count++, IsRowWrite(message.Request));
                message.SetProperty(typeof(Noted), noted);
                if (noted.Write)
                    lastWrite = noted.Position;
            }
            // The policy added twice to one pipeline sees each attempt twice: counted once.
            if (noted.InFlight)
                return null;
            noted.InFlight = true;
            if (noted.Write && noted.MayHaveBeenApplied)
                lastWriteRetriedInDoubt = Math.Max(lastWriteRetriedInDoubt, noted.Position);
            return noted;
        }
    }

    /// <summary>Notes how the attempt ended: with a response of <paramref name="status"/>, or with none (0).</summary>
    internal void Ended(Noted noted, int status, string? errorCode)
    {
        lock (gate)
        {
            noted.InFlight = false;
            if (DefinitelyRejected(status, errorCode))
                return;
            noted.MayHaveBeenApplied = true;
            if (noted.Write)
                writeMayHaveBeenApplied = true;
        }
    }

    // A request that can change rows: everything that is not proven otherwise. Reads cannot; nor does
    // the lazy creation of the table itself, which every store sends once and which stores no row. That
    // one is recognised exactly: a POST whose last path segment is "Tables" ({account}/Tables, or
    // /{account}/Tables on a path-style endpoint). The service reserves that name, so no table has it,
    // while an insert into a table whose name merely ends that way (POST /TimeTables) is a write.
    private static bool IsRowWrite(Request request)
    {
        var method = request.Method;
        if (method == RequestMethod.Get || method == RequestMethod.Head || method == RequestMethod.Options)
            return false;
        if (method != RequestMethod.Post)
            return true;
        var path = request.Uri.Path.AsSpan().TrimEnd('/');
        return !path[(path.LastIndexOf('/') + 1)..].Equals("Tables", StringComparison.Ordinal);
    }

    // The answers after which a request is known not to have been applied: a client error other than a
    // request timeout (the service refused it: 400, 401, 403, 404, 409, 412, 413, 429, ...), and the
    // service's own "server busy" (503 ServerBusy), which it sends instead of processing the request.
    // Everything else is not proof: no response at all, a success, a server error (the service documents
    // 500 OperationTimedOut as "may or may not have succeeded"), a gateway's 502, 503 or 504.
    private static bool DefinitelyRejected(int status, string? errorCode) =>
        (status is >= 400 and < 500 and not 408)
        || (status == 503 && string.Equals(errorCode, "ServerBusy", StringComparison.Ordinal));

    /// <summary>One request of the call, with what its attempts showed. It travels with its message.</summary>
    internal sealed class Noted(RequestAttempts log, int position, bool write)
    {
        public RequestAttempts Log { get; } = log;

        public int Position { get; } = position;

        public bool Write { get; } = write;

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
    /// request was sent (and when every earlier attempt was definitely rejected: throttled, or refused as
    /// busy), <see cref="TransientStorageException.MayHaveCommitted"/> is false when every attempt was
    /// definitely rejected, and a conditional delete (<see cref="IConditionalDelete{T}"/>) answers
    /// <see cref="DeleteStatus.NotFound"/> for a row that was not there. Without it the attempts cannot
    /// be observed and each of those is answered conservatively (true, true and
    /// <see cref="DeleteStatus.Indeterminate"/>): a store over a client the host built without this call
    /// never answers <see cref="DeleteStatus.NotFound"/> for a delete it sent, and so does not pass the
    /// conditional-delete scenarios of the conformance kit. The policy changes no request and no
    /// response; adding it twice counts each attempt once.
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
