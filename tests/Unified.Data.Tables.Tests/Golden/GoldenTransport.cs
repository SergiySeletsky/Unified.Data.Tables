using System.Diagnostics.CodeAnalysis;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests.Golden;

/// <summary>
/// An HTTP transport that sends nothing: it records every request the real Azure SDK produced (method,
/// URL and body) and answers with what a test scripts for it. The golden corpus uses it to capture the
/// exact bytes a row is on the wire, and to hand a stored row back to the SDK the way the service would.
/// </summary>
internal sealed class GoldenTransport(Func<GoldenTransport.Sent, GoldenTransport.Answer> respond) : HttpPipelineTransport
{
    /// <summary>The account every golden client addresses; nothing is ever sent to it.</summary>
    internal const string Endpoint = "https://golden.table.core.windows.net/";

    /// <summary>A request as the SDK sent it.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The URL path, unescaped.</param>
    /// <param name="Query">The URL query parameters, unescaped.</param>
    /// <param name="Body">The request body as UTF-8 text; empty when there is none.</param>
    internal sealed record Sent(string Method, string Path, IReadOnlyDictionary<string, string> Query, string Body);

    /// <summary>The scripted answer to one request.</summary>
    /// <param name="Status">The HTTP status.</param>
    /// <param name="Json">The JSON body, or null for none.</param>
    /// <param name="Headers">Response headers, or null for none.</param>
    internal sealed record Answer(int Status, string? Json = null, IReadOnlyDictionary<string, string>? Headers = null);

    /// <summary>The ETag every scripted write answers with.</summary>
    internal const string ETag = "W/\"datetime'2026-01-02T03%3A04%3A05.0000000Z'\"";

    /// <summary>A write accepted with no body, as the service answers an insert sent with <c>Prefer: return-no-content</c>.</summary>
    internal static Answer NoContent { get; } = new(204, Headers: new Dictionary<string, string> { ["ETag"] = ETag });

    /// <summary>Every request, in the order sent.</summary>
    public List<Sent> Requests { get; } = [];

    /// <summary>A table service client of the real SDK over this transport (no retries, nothing leaves the process).</summary>
    public TableServiceClient Service() =>
        new(new Uri(Endpoint), new TableSharedKeyCredential("golden", Convert.ToBase64String(new byte[32])),
            new TableClientOptions { Transport = this, Retry = { MaxRetries = 0 } });

    public override Request CreateRequest() => HttpClientTransport.Shared.CreateRequest();

    public override void Process(HttpMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var body = new MemoryStream();
        message.Request.Content?.WriteTo(body, message.CancellationToken);
        var uri = message.Request.Uri.ToUri();
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            query[Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq])] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        var sent = new Sent(
            message.Request.Method.Method, Uri.UnescapeDataString(uri.AbsolutePath), query, Encoding.UTF8.GetString(body.ToArray()));
        lock (Requests) Requests.Add(sent);
        // The lazy table creation is not what a test scripts: it always succeeds.
        var answer = sent.Method == "POST" && sent.Path.EndsWith("/Tables", StringComparison.Ordinal) ? new Answer(204) : respond(sent);
        message.Response = new Canned(answer, message.Request.ClientRequestId);
    }

    public override ValueTask ProcessAsync(HttpMessage message)
    {
        Process(message);
        return ValueTask.CompletedTask;
    }

    private sealed class Canned : Response
    {
        private readonly Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        private readonly int status;

        public Canned(Answer answer, string clientRequestId)
        {
            status = answer.Status;
            ClientRequestId = clientRequestId;
            foreach (var header in answer.Headers ?? new Dictionary<string, string>())
                headers[header.Key] = header.Value;
            if (answer.Json is not null)
            {
                headers["Content-Type"] = "application/json;odata=minimalmetadata;streaming=true;charset=utf-8";
                ContentStream = new MemoryStream(Encoding.UTF8.GetBytes(answer.Json));
            }
        }

        public override int Status => status;

        public override string ReasonPhrase => status.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; }

        // The pipeline disposes the response with its message, while the SDK still reads the (buffered)
        // body afterwards; like the real transport, a buffered body is left readable.
        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => headers.ContainsKey(name);

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => headers.Select(h => new HttpHeader(h.Key, h.Value));

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value) => headers.TryGetValue(name, out value);

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = headers.TryGetValue(name, out var value) ? [value] : null;
            return values is not null;
        }
    }
}
