using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;

namespace Unified.Data.Tables.Tests.TestSupport;

/// <summary>
/// An HTTP transport that sends nothing: it records each request body the real Azure SDK produced and
/// answers <c>204 No Content</c>, so a test can measure exactly what the SDK would put on the wire.
/// </summary>
internal sealed class CapturingTransport : HttpPipelineTransport
{
    public List<byte[]> Bodies { get; } = [];

    public override Request CreateRequest() => HttpClientTransport.Shared.CreateRequest();

    public override void Process(HttpMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var body = new MemoryStream();
        message.Request.Content?.WriteTo(body, message.CancellationToken);
        lock (Bodies) Bodies.Add(body.ToArray());
        message.Response = new NoContent(message.Request.ClientRequestId);
    }

    public override ValueTask ProcessAsync(HttpMessage message)
    {
        Process(message);
        return ValueTask.CompletedTask;
    }

    private sealed class NoContent(string clientRequestId) : Response
    {
        private const string ETag = "W/\"datetime'2026-11-10T00%3A00%3A00Z'\"";

        public override int Status => 204;

        public override string ReasonPhrase => "No Content";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = clientRequestId;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => IsETag(name);

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
        {
            yield return new HttpHeader("ETag", ETag);
        }

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = IsETag(name) ? ETag : null;
            return value is not null;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = IsETag(name) ? [ETag] : null;
            return values is not null;
        }

        private static bool IsETag(string name) => string.Equals(name, "ETag", StringComparison.OrdinalIgnoreCase);
    }
}
