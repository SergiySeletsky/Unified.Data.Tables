using System.Diagnostics.CodeAnalysis;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;

namespace Unified.Data.Tables.Tests.LargeValues;

/// <summary>
/// An HTTP transport that sends nothing: it records every request the real Azure SDK produced (method,
/// URL and <c>If-Match</c>) and answers with what a test scripts for it, so a test can assert exactly
/// what <see cref="TableStorage{T}"/> puts on the wire and how it reads the answer.
/// </summary>
internal sealed class ScriptedTableTransport(Func<ScriptedTableTransport.Sent, ScriptedTableTransport.Answer> respond) : HttpPipelineTransport
{
    /// <summary>A request as the SDK sent it.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The URL path, unescaped (<c>/t(PartitionKey='p',RowKey='r')</c>).</param>
    /// <param name="Query">The URL query, unescaped, without the leading <c>?</c>.</param>
    /// <param name="IfMatch">The <c>If-Match</c> header, or null.</param>
    internal sealed record Sent(string Method, string Path, string Query, string? IfMatch)
    {
        /// <summary>The comma-separated <c>$select</c> list, or null when the request has none.</summary>
        internal string? Select =>
            Query.Split('&').FirstOrDefault(p => p.StartsWith("$select=", StringComparison.Ordinal))?["$select=".Length..];

        /// <summary>True for a request that addresses one entity (<c>/table(PartitionKey=...,RowKey=...)</c>).</summary>
        internal bool IsEntity => Path.Contains("(PartitionKey=", StringComparison.Ordinal);
    }

    /// <summary>The scripted answer to one request.</summary>
    /// <param name="Status">The HTTP status.</param>
    /// <param name="Json">The JSON body, or null for none.</param>
    /// <param name="ETag">The <c>ETag</c> response header, or null for none.</param>
    /// <param name="ErrorCode">The <c>x-ms-error-code</c> response header, or null for none.</param>
    internal sealed record Answer(int Status, string? Json = null, string? ETag = null, string? ErrorCode = null);

    /// <summary>Every request, in the order sent.</summary>
    public List<Sent> Requests { get; } = [];

    /// <summary>The requests that address one entity (not the table creation).</summary>
    public List<Sent> EntityRequests
    {
        get
        {
            lock (Requests) return Requests.Where(r => r.IsEntity).ToList();
        }
    }

    /// <summary>A table client of the real SDK over this transport (no retries, nothing leaves the process).</summary>
    public TableServiceClient Service() =>
        new(new Uri("https://acct.table.core.windows.net/"), new TableSharedKeyCredential("acct", Convert.ToBase64String(new byte[32])),
            new TableClientOptions { Transport = this, Retry = { MaxRetries = 0 } });

    public override Request CreateRequest() => HttpClientTransport.Shared.CreateRequest();

    public override void Process(HttpMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var uri = message.Request.Uri.ToUri();
        var sent = new Sent(
            message.Request.Method.Method,
            Uri.UnescapeDataString(uri.AbsolutePath),
            Uri.UnescapeDataString(uri.Query.TrimStart('?')),
            message.Request.Headers.TryGetValue("If-Match", out var ifMatch) ? ifMatch : null);
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
            if (answer.ETag is not null) headers["ETag"] = answer.ETag;
            if (answer.ErrorCode is not null) headers["x-ms-error-code"] = answer.ErrorCode;
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
