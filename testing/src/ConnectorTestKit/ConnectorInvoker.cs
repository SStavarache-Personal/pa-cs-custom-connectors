using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace ConnectorTestKit;

public sealed class ConnectorInvoker
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> MetadataReferences = new(CreateMetadataReferences);
    private static readonly ConcurrentDictionary<string, CompiledConnectorScript> ScriptCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ConnectorInvocationResult> InvokeAsync(
        ConnectorDefinition connector,
        ConnectorInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        var responder = new StubAwareHttpResponder(request.OutboundStubs, request.AllowLiveNetwork);
        HttpRequestMessage inboundRequest = BuildInboundRequest(connector, request);
        var context = new ConnectorRuntimeContext(request.OperationId, inboundRequest, responder.SendAsync);

        CompiledConnectorScript compiledScript = GetCompiledScript(connector.ScriptPath);
        var scriptInstance = (global::ScriptBase)Activator.CreateInstance(compiledScript.ScriptType)!;
        scriptInstance.Initialize(context, cancellationToken);

        HttpResponseMessage response = await scriptInstance.ExecuteAsync().ConfigureAwait(false);
        string bodyText = response.Content == null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return new ConnectorInvocationResult
        {
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase ?? response.StatusCode.ToString(),
            BodyText = bodyText,
            Headers = ExtractHeaders(response),
            OutboundRequests = responder.Exchanges,
            UsedLiveNetwork = responder.UsedLiveNetwork
        };
    }

    private static HttpRequestMessage BuildInboundRequest(ConnectorDefinition connector, ConnectorInvocationRequest request)
    {
        Uri requestUri = BuildRequestUri(connector.BuildBaseUri(), request.RelativePath, request.QueryString);
        var httpRequest = new HttpRequestMessage(new HttpMethod(request.Method), requestUri);

        foreach (var header in request.Headers)
        {
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.RequestBody != null)
        {
            httpRequest.Content = new StringContent(request.RequestBody, System.Text.Encoding.UTF8, "application/json");
        }

        return httpRequest;
    }

    private static Uri BuildRequestUri(Uri baseUri, string relativePath, string? queryString)
    {
        string normalizedPath = string.IsNullOrWhiteSpace(relativePath) ? "/" : relativePath;
        if (normalizedPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || normalizedPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(AppendQueryString(normalizedPath, queryString));
        }

        if (normalizedPath.StartsWith('/'))
        {
            normalizedPath = normalizedPath[1..];
        }

        Uri combined = new(baseUri, normalizedPath);
        return new Uri(AppendQueryString(combined.ToString(), queryString));
    }

    private static string AppendQueryString(string url, string? queryString)
    {
        if (string.IsNullOrWhiteSpace(queryString))
        {
            return url;
        }

        string normalizedQuery = queryString.StartsWith('?') ? queryString : "?" + queryString;
        return url.Contains('?') ? url + "&" + normalizedQuery.TrimStart('?') : url + normalizedQuery;
    }

    private static IReadOnlyDictionary<string, string> ExtractHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        if (response.Content != null)
        {
            foreach (var header in response.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        return headers;
    }

    private static CompiledConnectorScript GetCompiledScript(string scriptPath)
    {
        string cacheKey = scriptPath + "|" + File.GetLastWriteTimeUtc(scriptPath).Ticks;
        return ScriptCache.GetOrAdd(cacheKey, _ => CompileScript(scriptPath));
    }

    private static CompiledConnectorScript CompileScript(string scriptPath)
    {
        string scriptText = File.ReadAllText(scriptPath);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(scriptText, new CSharpParseOptions(LanguageVersion.Latest), path: scriptPath);

        string assemblyName = $"ConnectorScript_{Path.GetFileNameWithoutExtension(scriptPath)}_{Guid.NewGuid():N}";
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntaxTree },
            MetadataReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var assemblyStream = new MemoryStream();
        EmitResult emitResult = compilation.Emit(assemblyStream);
        if (!emitResult.Success)
        {
            string errors = string.Join(Environment.NewLine, emitResult.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString()));
            throw new InvalidOperationException($"Failed to compile '{scriptPath}'.{Environment.NewLine}{errors}");
        }

        assemblyStream.Position = 0;
        Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
        Type scriptType = assembly.GetTypes().Single(type => typeof(global::ScriptBase).IsAssignableFrom(type) && !type.IsAbstract);
        return new CompiledConnectorScript(scriptType);
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences()
    {
        string? tpaValue = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(tpaValue))
        {
            throw new InvalidOperationException("Could not resolve trusted platform assemblies for Roslyn compilation.");
        }

        var references = tpaValue
            .Split(Path.PathSeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(global::ScriptBase).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Newtonsoft.Json.Linq.JObject).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(System.Web.HttpUtility).Assembly.Location));

        return references;
    }

    private sealed record CompiledConnectorScript(Type ScriptType);

    private sealed class StubAwareHttpResponder
    {
        private readonly IReadOnlyList<OutboundHttpStub> _stubs;
        private readonly HttpClient _httpClient;

        public StubAwareHttpResponder(IReadOnlyList<OutboundHttpStub> stubs, bool allowLiveNetwork)
        {
            _stubs = stubs;
            AllowLiveNetwork = allowLiveNetwork;
            _httpClient = new HttpClient();
        }

        public bool AllowLiveNetwork { get; }

        public bool UsedLiveNetwork { get; private set; }

        public IReadOnlyList<OutboundHttpExchange> Exchanges => _exchanges;

        private readonly List<OutboundHttpExchange> _exchanges = new();

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? requestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            OutboundHttpStub? stub = _stubs.FirstOrDefault(candidate => candidate.Matches(request, requestBody));
            if (stub != null)
            {
                var stubbedResponse = new HttpResponseMessage((System.Net.HttpStatusCode)stub.Response.StatusCode)
                {
                    Content = new StringContent(stub.Response.Body, System.Text.Encoding.UTF8, stub.Response.ContentType)
                };

                foreach (var header in stub.Response.Headers)
                {
                    stubbedResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                _exchanges.Add(new OutboundHttpExchange(
                    request.Method.Method,
                    request.RequestUri?.ToString() ?? string.Empty,
                    requestBody,
                    true,
                    stub.Response.StatusCode));

                return stubbedResponse;
            }

            if (!AllowLiveNetwork)
            {
                throw new InvalidOperationException($"No outbound stub matched {request.Method} {request.RequestUri}. Enable live network access or add a stub.");
            }

            UsedLiveNetwork = true;
            HttpResponseMessage liveResponse = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _exchanges.Add(new OutboundHttpExchange(
                request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                requestBody,
                false,
                (int)liveResponse.StatusCode));
            return liveResponse;
        }
    }
}