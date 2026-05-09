using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConnectorTestKit;

public sealed class ConnectorTestCase
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool IncludeInAutomatedRun { get; init; } = true;

    public required string OperationId { get; init; }

    public string Method { get; init; } = "POST";

    public string RelativePath { get; init; } = "/";

    public string? QueryString { get; init; }

    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public JToken? RequestBody { get; init; }

    public bool AllowLiveNetwork { get; init; }

    public IReadOnlyList<OutboundHttpStub> OutboundStubs { get; init; } = Array.Empty<OutboundHttpStub>();

    public required ExpectedConnectorResponse Expected { get; init; }

    public static ConnectorTestCase LoadFromFile(string filePath)
    {
        JsonSerializerSettings settings = CreateSerializerSettings();
        ConnectorTestCase? testCase = JsonConvert.DeserializeObject<ConnectorTestCase>(File.ReadAllText(filePath), settings);
        return testCase ?? throw new InvalidOperationException($"Failed to deserialize test case '{filePath}'.");
    }

    internal static JsonSerializerSettings CreateSerializerSettings()
    {
        return new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };
    }

    public ConnectorInvocationRequest ToInvocationRequest()
    {
        return new ConnectorInvocationRequest
        {
            OperationId = OperationId,
            Method = Method,
            RelativePath = RelativePath,
            QueryString = QueryString,
            Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
            RequestBody = RequestBody?.ToString(Formatting.None),
            AllowLiveNetwork = AllowLiveNetwork,
            OutboundStubs = OutboundStubs
        };
    }
}

public sealed class ExpectedConnectorResponse
{
    public int StatusCode { get; init; } = 200;

    public JToken? BodyJson { get; init; }

    public IDictionary<string, JToken> JsonPathEquals { get; init; } = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);

    public IDictionary<string, string> HeaderEquals { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? BodyTextContains { get; init; }

    public int? OutboundRequestCount { get; init; }
}

public sealed record ConnectorTestCaseDescriptor(
    string FilePath,
    ConnectorTestCase TestCase,
    ConnectorDefinition Connector);