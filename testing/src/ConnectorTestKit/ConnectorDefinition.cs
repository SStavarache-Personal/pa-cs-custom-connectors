using Newtonsoft.Json.Linq;

namespace ConnectorTestKit;

public sealed class ConnectorDefinition
{
    public ConnectorDefinition(
        string name,
        string directoryPath,
        string swaggerPath,
        string apiPropertiesPath,
        string scriptPath,
        string? readmePath,
        string title,
        string swaggerVersion,
        string? scheme,
        string? host,
        string? basePath,
        IReadOnlyList<ConnectorOperation> operations,
        IReadOnlyList<string> scriptOperations,
        JObject swaggerDocument,
        JObject apiPropertiesDocument)
    {
        Name = name;
        DirectoryPath = directoryPath;
        SwaggerPath = swaggerPath;
        ApiPropertiesPath = apiPropertiesPath;
        ScriptPath = scriptPath;
        ReadmePath = readmePath;
        Title = title;
        SwaggerVersion = swaggerVersion;
        Scheme = scheme;
        Host = host;
        BasePath = string.IsNullOrWhiteSpace(basePath) ? "/" : basePath;
        Operations = operations;
        ScriptOperations = scriptOperations;
        SwaggerDocument = swaggerDocument;
        ApiPropertiesDocument = apiPropertiesDocument;
    }

    public string Name { get; }

    public string DirectoryPath { get; }

    public string SwaggerPath { get; }

    public string ApiPropertiesPath { get; }

    public string ScriptPath { get; }

    public string? ReadmePath { get; }

    public string Title { get; }

    public string SwaggerVersion { get; }

    public string? Scheme { get; }

    public string? Host { get; }

    public string BasePath { get; }

    public IReadOnlyList<ConnectorOperation> Operations { get; }

    public IReadOnlyList<string> ScriptOperations { get; }

    public JObject SwaggerDocument { get; }

    public JObject ApiPropertiesDocument { get; }

    public Uri BuildBaseUri()
    {
        string scheme = string.IsNullOrWhiteSpace(Scheme) ? "https" : Scheme!;
        string host = string.IsNullOrWhiteSpace(Host) ? "api.example.com" : Host!;
        string normalizedBasePath = string.IsNullOrWhiteSpace(BasePath) ? "/" : BasePath;

        if (!normalizedBasePath.StartsWith('/'))
        {
            normalizedBasePath = "/" + normalizedBasePath;
        }

        if (!normalizedBasePath.EndsWith('/'))
        {
            normalizedBasePath += "/";
        }

        return new Uri($"{scheme}://{host}{normalizedBasePath}");
    }
}

public sealed record ConnectorOperation(string OperationId, string Method, string Path, string? Summary);