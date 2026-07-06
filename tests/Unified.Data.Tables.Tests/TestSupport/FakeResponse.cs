using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.Core;

namespace Unified.Data.Tables.Tests.TestSupport;

/// <summary>
/// Concrete <see cref="Response"/> that exposes a fixed <c>ETag</c> header, so
/// <see cref="Response.Headers"/>.<see cref="ResponseHeaders.ETag"/> resolves in tests. Azure's
/// SDK reads the ETag from response headers (not from a mutated entity), so a plain substitute
/// <see cref="Response"/> would surface no ETag; this fake fills that gap.
/// </summary>
public sealed class FakeResponse : Response
{
    private readonly string _etag;

    public FakeResponse(string etag = "W/\"etag1\"") => _etag = etag;

    public override int Status => 200;
    public override string ReasonPhrase => "OK";

    public override Stream? ContentStream
    {
        get => null;
        set { }
    }

    public override string ClientRequestId
    {
        get => Guid.Empty.ToString();
        set { }
    }

    public override void Dispose()
    {
    }

    protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
    {
        if (string.Equals(name, "ETag", StringComparison.OrdinalIgnoreCase))
        {
            value = _etag;
            return true;
        }
        value = null;
        return false;
    }

    protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
    {
        if (string.Equals(name, "ETag", StringComparison.OrdinalIgnoreCase))
        {
            values = new[] { _etag };
            return true;
        }
        values = null;
        return false;
    }

    protected override bool ContainsHeader(string name) =>
        string.Equals(name, "ETag", StringComparison.OrdinalIgnoreCase);

    protected override IEnumerable<HttpHeader> EnumerateHeaders()
    {
        yield return new HttpHeader("ETag", _etag);
    }
}
