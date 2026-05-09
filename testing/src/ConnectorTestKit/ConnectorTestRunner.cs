using Newtonsoft.Json.Linq;

namespace ConnectorTestKit;

public sealed class ConnectorTestRunner
{
    private readonly ConnectorInvoker _invoker = new();

    public async Task<ConnectorTestRunResult> RunAsync(
        ConnectorDefinition connector,
        ConnectorTestCase testCase,
        CancellationToken cancellationToken = default)
    {
        ConnectorInvocationResult invocation = await _invoker.InvokeAsync(connector, testCase.ToInvocationRequest(), cancellationToken)
            .ConfigureAwait(false);

        var failures = new List<string>();
        if (invocation.StatusCode != testCase.Expected.StatusCode)
        {
            failures.Add($"Expected HTTP {testCase.Expected.StatusCode} but got {invocation.StatusCode}.");
        }

        foreach (var expectedHeader in testCase.Expected.HeaderEquals)
        {
            if (!invocation.Headers.TryGetValue(expectedHeader.Key, out string? actualHeaderValue) ||
                !string.Equals(actualHeaderValue, expectedHeader.Value, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Expected header '{expectedHeader.Key}' to equal '{expectedHeader.Value}'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(testCase.Expected.BodyTextContains) &&
            invocation.BodyText.IndexOf(testCase.Expected.BodyTextContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            failures.Add($"Expected response body to contain '{testCase.Expected.BodyTextContains}'.");
        }

        if (testCase.Expected.OutboundRequestCount.HasValue && invocation.OutboundRequests.Count != testCase.Expected.OutboundRequestCount.Value)
        {
            failures.Add($"Expected {testCase.Expected.OutboundRequestCount.Value} outbound request(s) but got {invocation.OutboundRequests.Count}.");
        }

        JToken? actualJson = invocation.TryParseJsonBody();
        if (testCase.Expected.BodyJson != null)
        {
            if (actualJson == null)
            {
                failures.Add("Expected a JSON response body but the response was not valid JSON.");
            }
            else if (!JsonContains(actualJson, testCase.Expected.BodyJson))
            {
                failures.Add($"Expected JSON subset {testCase.Expected.BodyJson} was not found in actual body {actualJson}.");
            }
        }

        foreach (var assertion in testCase.Expected.JsonPathEquals)
        {
            if (actualJson == null)
            {
                failures.Add($"Expected JSON path '{assertion.Key}' but the response body was not JSON.");
                continue;
            }

            JToken? actualToken = actualJson.SelectToken(assertion.Key);
            if (actualToken == null || !JToken.DeepEquals(actualToken, assertion.Value))
            {
                failures.Add($"Expected JSON path '{assertion.Key}' to equal '{assertion.Value}', but was '{actualToken ?? JValue.CreateNull()}'.");
            }
        }

        return new ConnectorTestRunResult(failures.Count == 0, failures, invocation);
    }

    private static bool JsonContains(JToken actual, JToken expected)
    {
        if (expected.Type == JTokenType.Object)
        {
            if (actual.Type != JTokenType.Object)
            {
                return false;
            }

            foreach (var property in ((JObject)expected).Properties())
            {
                JToken? actualChild = actual[property.Name];
                if (actualChild == null || !JsonContains(actualChild, property.Value))
                {
                    return false;
                }
            }

            return true;
        }

        if (expected.Type == JTokenType.Array)
        {
            if (actual.Type != JTokenType.Array)
            {
                return false;
            }

            JArray actualArray = (JArray)actual;
            JArray expectedArray = (JArray)expected;
            if (actualArray.Count < expectedArray.Count)
            {
                return false;
            }

            for (int i = 0; i < expectedArray.Count; i++)
            {
                if (!JsonContains(actualArray[i]!, expectedArray[i]!))
                {
                    return false;
                }
            }

            return true;
        }

        return JToken.DeepEquals(actual, expected);
    }
}

public sealed record ConnectorTestRunResult(
    bool Passed,
    IReadOnlyList<string> Failures,
    ConnectorInvocationResult Invocation);