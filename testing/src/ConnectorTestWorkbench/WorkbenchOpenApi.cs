using ConnectorTestKit;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json.Linq;

internal static class WorkbenchOpenApi
{
	public static readonly string[] SupportedMethods =
	[
		"GET",
		"POST",
		"PUT",
		"PATCH",
		"DELETE",
		"HEAD",
		"OPTIONS"
	];

	public static JObject BuildLocalSpec(
		ConnectorDefinition connector,
		HttpRequest request,
		IReadOnlyList<ConnectorTestCaseDescriptor> tests)
	{
		var spec = (JObject)connector.SwaggerDocument.DeepClone();
		spec["host"] = request.Host.Value;
		spec["basePath"] = $"/runtime/{connector.Name}";
		spec["schemes"] = new JArray(request.Scheme);

		JObject info = (JObject)(spec["info"] ?? new JObject());
		string description = info["description"]?.ToString() ?? string.Empty;
		string workbenchNote = "Local workbench runtime: Swagger Try it out executes against the local connector harness and allows real outbound network calls, which mirrors manual Power Automate connector testing more closely than the seeded CI-safe scenarios alone.";
		info["description"] = string.IsNullOrWhiteSpace(description)
			? workbenchNote
			: description + Environment.NewLine + Environment.NewLine + workbenchNote;
		spec["info"] = info;

		foreach (var pathProperty in spec["paths"]?.Children<JProperty>() ?? Enumerable.Empty<JProperty>())
		{
			foreach (var methodProperty in pathProperty.Value.Children<JProperty>())
			{
				string operationId = methodProperty.Value["operationId"]?.ToString() ?? string.Empty;
				if (string.IsNullOrWhiteSpace(operationId))
				{
					continue;
				}

				List<ConnectorTestCaseDescriptor> operationTests = tests
					.Where(test => string.Equals(test.TestCase.OperationId, operationId, StringComparison.Ordinal))
					.ToList();

				if (operationTests.Count == 0)
				{
					continue;
				}

				methodProperty.Value["x-workbenchExamples"] = JArray.FromObject(operationTests.Select(test => new
				{
					id = Path.GetFileName(test.FilePath),
					name = test.TestCase.Name,
					includeInAutomatedRun = test.TestCase.IncludeInAutomatedRun,
					allowLiveNetwork = test.TestCase.AllowLiveNetwork
				}));

				ConnectorTestCase preferredExample = operationTests
					.Select(test => test.TestCase)
					.FirstOrDefault(test => test.IncludeInAutomatedRun) ?? operationTests[0].TestCase;

				ApplyDefaults((JObject)methodProperty.Value, preferredExample);
			}
		}

		return spec;
	}

	public static string NormalizeRelativePath(string? operationPath)
	{
		if (string.IsNullOrWhiteSpace(operationPath))
		{
			return "/";
		}

		string normalized = operationPath.StartsWith('/') ? operationPath : "/" + operationPath;
		return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
	}

	public static ConnectorOperation ResolveOperation(ConnectorDefinition connector, string method, string relativePath)
	{
		return connector.Operations.FirstOrDefault(operation =>
				string.Equals(operation.Method, method, StringComparison.OrdinalIgnoreCase) &&
				PathMatches(operation.Path, relativePath))
			?? throw new InvalidOperationException($"No operation matched {method} {relativePath} for connector '{connector.Name}'.");
	}

	private static void ApplyDefaults(JObject operation, ConnectorTestCase testCase)
	{
		if (operation["parameters"] is not JArray parameters)
		{
			return;
		}

		var queryValues = QueryHelpers.ParseQuery(string.IsNullOrWhiteSpace(testCase.QueryString)
			? string.Empty
			: "?" + testCase.QueryString);

		foreach (JObject parameter in parameters.Children<JObject>())
		{
			string location = parameter["in"]?.ToString() ?? string.Empty;
			string name = parameter["name"]?.ToString() ?? string.Empty;

			if (string.Equals(location, "query", StringComparison.OrdinalIgnoreCase) &&
				queryValues.TryGetValue(name, out var queryValue) &&
				!string.IsNullOrWhiteSpace(queryValue.FirstOrDefault()))
			{
				parameter["default"] = queryValue.First();
				continue;
			}

			if (string.Equals(location, "header", StringComparison.OrdinalIgnoreCase) &&
				testCase.Headers.TryGetValue(name, out string? headerValue) &&
				!string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
			{
				parameter["default"] = headerValue;
				continue;
			}

			if (string.Equals(location, "body", StringComparison.OrdinalIgnoreCase) && testCase.RequestBody != null)
			{
				parameter["x-example"] = testCase.RequestBody.DeepClone();
				if (parameter["schema"] is JObject schema)
				{
					schema["example"] = testCase.RequestBody.DeepClone();
				}
			}
		}
	}

	private static bool PathMatches(string template, string actualPath)
	{
		string[] templateSegments = NormalizeRelativePath(template)
			.Trim('/')
			.Split('/', StringSplitOptions.RemoveEmptyEntries);
		string[] actualSegments = NormalizeRelativePath(actualPath)
			.Trim('/')
			.Split('/', StringSplitOptions.RemoveEmptyEntries);

		if (templateSegments.Length == 0 && actualSegments.Length == 0)
		{
			return true;
		}

		if (templateSegments.Length != actualSegments.Length)
		{
			return false;
		}

		for (int i = 0; i < templateSegments.Length; i++)
		{
			string templateSegment = templateSegments[i];
			if (templateSegment.StartsWith('{') && templateSegment.EndsWith('}'))
			{
				continue;
			}

			if (!string.Equals(templateSegment, actualSegments[i], StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}

		return true;
	}
}