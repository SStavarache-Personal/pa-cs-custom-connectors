using System.Text;
using ConnectorTestKit;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => ConnectorRepository.FindRepositoryRoot());
builder.Services.AddSingleton<ConnectorInvoker>();
builder.Services.AddSingleton<ConnectorTestRunner>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/connectors", ([FromServices] string repositoryRoot) =>
{
	return ConnectorRepository.LoadConnectors(repositoryRoot)
		.Select(connector =>
		{
			ConnectorValidationResult validation = ConnectorValidator.Validate(connector);
			IReadOnlyList<ConnectorTestCaseDescriptor> tests = ConnectorRepository.LoadTestCases(connector);

			return new
			{
				connector.Name,
				connector.Title,
				connector.Host,
				connector.BasePath,
				specUrl = $"/api/connectors/{connector.Name}/spec",
				runtimeBaseUrl = $"/runtime/{connector.Name}",
				operations = connector.Operations.Select(operation => new
				{
					operation.OperationId,
					operation.Method,
					operation.Path,
					operation.Summary
				}),
				validation = new
				{
					validation.IsValid,
					issues = validation.Issues.Select(issue => new
					{
						severity = issue.Severity.ToString(),
						issue.Code,
						issue.Message
					})
				},
				tests = tests.Select(test => new
				{
					id = Path.GetFileName(test.FilePath),
					test.TestCase.Name,
					test.TestCase.Description,
					test.TestCase.OperationId,
					test.TestCase.IncludeInAutomatedRun,
					test.TestCase.AllowLiveNetwork
				})
			};
		});
});

app.MapGet("/api/connectors/{connectorName}/tests", ([FromServices] string repositoryRoot, string connectorName) =>
{
	ConnectorDefinition connector = ConnectorRepository.LoadConnector(repositoryRoot, connectorName);
	return ConnectorRepository.LoadTestCases(connector)
		.Select(descriptor => new
		{
			id = Path.GetFileName(descriptor.FilePath),
			descriptor.TestCase.Name,
			descriptor.TestCase.Description,
			descriptor.TestCase.OperationId,
			descriptor.TestCase.Method,
			descriptor.TestCase.RelativePath,
			descriptor.TestCase.QueryString,
			descriptor.TestCase.AllowLiveNetwork,
			descriptor.TestCase.IncludeInAutomatedRun,
			headers = descriptor.TestCase.Headers,
			requestBody = descriptor.TestCase.RequestBody?.ToString(Formatting.Indented),
			outboundStubs = descriptor.TestCase.OutboundStubs,
			expected = new
			{
				descriptor.TestCase.Expected.StatusCode,
				bodyJson = descriptor.TestCase.Expected.BodyJson?.ToString(Formatting.Indented),
				jsonPathEquals = descriptor.TestCase.Expected.JsonPathEquals.ToDictionary(
					pair => pair.Key,
					pair => pair.Value.ToString(Formatting.None),
					StringComparer.OrdinalIgnoreCase),
				descriptor.TestCase.Expected.HeaderEquals,
				descriptor.TestCase.Expected.BodyTextContains,
				descriptor.TestCase.Expected.OutboundRequestCount
			}
		});
});

app.MapGet("/api/connectors/{connectorName}/spec", ([FromServices] string repositoryRoot, HttpRequest request, string connectorName) =>
{
	ConnectorDefinition connector = ConnectorRepository.LoadConnector(repositoryRoot, connectorName);
	IReadOnlyList<ConnectorTestCaseDescriptor> tests = ConnectorRepository.LoadTestCases(connector);
	var spec = WorkbenchOpenApi.BuildLocalSpec(connector, request, tests);
	return Results.Text(spec.ToString(Formatting.None), "application/json");
});

app.MapPost("/api/connectors/{connectorName}/tests/{testId}/run", async (
	string connectorName,
	string testId,
	[FromServices] string repositoryRoot,
	[FromServices] ConnectorTestRunner runner,
	CancellationToken cancellationToken) =>
{
	ConnectorDefinition connector = ConnectorRepository.LoadConnector(repositoryRoot, connectorName);
	ConnectorTestCaseDescriptor descriptor = ConnectorRepository.LoadTestCases(connector)
		.Single(test => string.Equals(Path.GetFileName(test.FilePath), testId, StringComparison.OrdinalIgnoreCase));

	ConnectorTestRunResult result = await runner.RunAsync(connector, descriptor.TestCase, cancellationToken);

	return Results.Ok(new
	{
		result.Passed,
		result.Failures,
		invocation = new
		{
			result.Invocation.StatusCode,
			result.Invocation.ReasonPhrase,
			result.Invocation.BodyText,
			result.Invocation.Headers,
			result.Invocation.OutboundRequests,
			result.Invocation.UsedLiveNetwork
		}
	});
});

app.MapMethods("/runtime/{connectorName}/{**operationPath}", WorkbenchOpenApi.SupportedMethods, async (
	string connectorName,
	string? operationPath,
	HttpRequest request,
	[FromServices] string repositoryRoot,
	[FromServices] ConnectorInvoker invoker,
	CancellationToken cancellationToken) =>
{
	ConnectorDefinition connector = ConnectorRepository.LoadConnector(repositoryRoot, connectorName);
	string relativePath = WorkbenchOpenApi.NormalizeRelativePath(operationPath);
	ConnectorOperation operation = WorkbenchOpenApi.ResolveOperation(connector, request.Method, relativePath);

	string? bodyText = await ReadRequestBodyAsync(request, cancellationToken);
	var invocation = new ConnectorInvocationRequest
	{
		OperationId = operation.OperationId,
		Method = request.Method,
		RelativePath = relativePath,
		QueryString = request.QueryString.HasValue ? request.QueryString.Value?.TrimStart('?') : null,
		Headers = request.Headers.ToDictionary(
			header => header.Key,
			header => string.Join(", ", header.Value.ToArray()),
			StringComparer.OrdinalIgnoreCase),
		RequestBody = bodyText,
		AllowLiveNetwork = true
	};

	ConnectorInvocationResult result = await invoker.InvokeAsync(connector, invocation, cancellationToken);
	string contentType = result.Headers.TryGetValue("Content-Type", out string? headerContentType)
		? headerContentType
		: (result.TryParseJsonBody() != null ? "application/json" : "text/plain; charset=utf-8");

	return Results.Content(result.BodyText, contentType, Encoding.UTF8, result.StatusCode);
});

app.Run();

static async Task<string?> ReadRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
{
	request.EnableBuffering();
	request.Body.Position = 0;

	using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
	string text = await reader.ReadToEndAsync(cancellationToken);
	request.Body.Position = 0;

	return string.IsNullOrWhiteSpace(text) ? null : text;
}
