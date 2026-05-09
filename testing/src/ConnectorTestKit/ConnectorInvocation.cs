using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace ConnectorTestKit;

public sealed class ConnectorInvocationRequest
{
    public required string OperationId { get; init; }

    public string Method { get; init; } = "POST";

    public string RelativePath { get; init; } = "/";

    public string? QueryString { get; init; }

    public string? RequestBody { get; init; }

    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool AllowLiveNetwork { get; init; }

    public IReadOnlyList<OutboundHttpStub> OutboundStubs { get; init; } = Array.Empty<OutboundHttpStub>();
}

public sealed class ConnectorInvocationResult
{
    public required int StatusCode { get; init; }

    public required string ReasonPhrase { get; init; }

    public required string BodyText { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public required IReadOnlyList<OutboundHttpExchange> OutboundRequests { get; init; }

    public required bool UsedLiveNetwork { get; init; }

    public JToken? TryParseJsonBody()
    {
        if (string.IsNullOrWhiteSpace(BodyText))
        {
            return null;
        }

        try
        {
            return JToken.Parse(BodyText);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class OutboundHttpStub
{
    public string? Method { get; init; }

    public string? Url { get; init; }

    public string? UrlContains { get; init; }

    public string? RequestBodyContains { get; init; }

    public required StubbedHttpResponse Response { get; init; }

    public bool Matches(HttpRequestMessage request, string? requestBody)
    {
        if (!string.IsNullOrWhiteSpace(Method) && !string.Equals(Method, request.Method.Method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string requestUrl = request.RequestUri?.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(Url) && !string.Equals(Url, requestUrl, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(UrlContains) && requestUrl.IndexOf(UrlContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(RequestBodyContains) && (requestBody?.IndexOf(RequestBodyContains, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
        {
            return false;
        }

        return true;
    }
}

public sealed class StubbedHttpResponse
{
    public int StatusCode { get; init; } = 200;

    public string ContentType { get; init; } = "application/json";

    public string Body { get; init; } = "{}";

    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record OutboundHttpExchange(
    string Method,
    string Url,
    string? RequestBody,
    bool WasStubbed,
    int StatusCode);

public sealed class ConnectorRuntimeContext
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sender;

    public ConnectorRuntimeContext(
        string operationId,
        HttpRequestMessage request,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sender,
        ILogger? logger = null)
    {
        OperationId = operationId;
        Request = request;
        _sender = sender;
        Logger = logger ?? NullLogger.Instance;
    }

    public string OperationId { get; }

    public HttpRequestMessage Request { get; }

    public ILogger Logger { get; }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _sender(request, cancellationToken);
    }
}