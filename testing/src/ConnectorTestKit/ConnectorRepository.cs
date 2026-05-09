using Newtonsoft.Json.Linq;

namespace ConnectorTestKit;

public static class ConnectorRepository
{
    public static string FindRepositoryRoot(string? startDirectory = null)
    {
        string current = startDirectory ?? AppContext.BaseDirectory;
        var directoryInfo = new DirectoryInfo(current);

        while (directoryInfo != null)
        {
            bool hasConnectors = Directory.Exists(Path.Combine(directoryInfo.FullName, "connectors"));
            bool hasAnchor = File.Exists(Path.Combine(directoryInfo.FullName, "AGENTS.md"));
            if (hasConnectors && hasAnchor)
            {
                return directoryInfo.FullName;
            }

            directoryInfo = directoryInfo.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the current execution directory.");
    }

    public static IReadOnlyList<ConnectorDefinition> LoadConnectors(string? repositoryRoot = null)
    {
        string root = repositoryRoot ?? FindRepositoryRoot();
        string connectorsPath = Path.Combine(root, "connectors");

        if (!Directory.Exists(connectorsPath))
        {
            throw new DirectoryNotFoundException($"Connectors directory not found at '{connectorsPath}'.");
        }

        return Directory
            .EnumerateDirectories(connectorsPath)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(directory => LoadConnector(root, Path.GetFileName(directory)!))
            .ToArray();
    }

    public static ConnectorDefinition LoadConnector(string repositoryRoot, string connectorName)
    {
        string connectorDirectory = Path.Combine(repositoryRoot, "connectors", connectorName);
        if (!Directory.Exists(connectorDirectory))
        {
            throw new DirectoryNotFoundException($"Connector directory not found: '{connectorDirectory}'.");
        }

        string swaggerPath = ResolveSwaggerPath(connectorDirectory);
        string apiPropertiesPath = Path.Combine(connectorDirectory, "apiProperties.json");
        string scriptPath = Path.Combine(connectorDirectory, "script.csx");
        string readmePath = Path.Combine(connectorDirectory, "README.md");

        JObject swaggerDocument = JObject.Parse(File.ReadAllText(swaggerPath));
        JObject apiPropertiesDocument = JObject.Parse(File.ReadAllText(apiPropertiesPath));

        string title = swaggerDocument["info"]?["title"]?.ToString() ?? connectorName;
        string swaggerVersion = swaggerDocument["swagger"]?.ToString() ?? string.Empty;
        string? scheme = swaggerDocument["schemes"]?.Values<string>().FirstOrDefault();
        string? host = swaggerDocument["host"]?.ToString();
        string? basePath = swaggerDocument["basePath"]?.ToString();

        IReadOnlyList<ConnectorOperation> operations = ParseOperations(swaggerDocument);
        IReadOnlyList<string> scriptOperations = apiPropertiesDocument["properties"]?["scriptOperations"]?
            .Values<string>()
            .Where(operation => !string.IsNullOrWhiteSpace(operation))
            .Select(operation => operation!)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();

        return new ConnectorDefinition(
            connectorName,
            connectorDirectory,
            swaggerPath,
            apiPropertiesPath,
            scriptPath,
            File.Exists(readmePath) ? readmePath : null,
            title,
            swaggerVersion,
            scheme,
            host,
            basePath,
            operations,
            scriptOperations,
            swaggerDocument,
            apiPropertiesDocument);
    }

    public static IReadOnlyList<ConnectorTestCaseDescriptor> LoadTestCases(ConnectorDefinition connector)
    {
        string testsDirectory = Path.Combine(connector.DirectoryPath, "tests");
        if (!Directory.Exists(testsDirectory))
        {
            return Array.Empty<ConnectorTestCaseDescriptor>();
        }

        return Directory
            .EnumerateFiles(testsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => new ConnectorTestCaseDescriptor(
                path,
                ConnectorTestCase.LoadFromFile(path),
                connector))
            .ToArray();
    }

    private static string ResolveSwaggerPath(string connectorDirectory)
    {
        string jsonPath = Path.Combine(connectorDirectory, "apiDefinition.swagger.json");
        if (File.Exists(jsonPath))
        {
            return jsonPath;
        }

        string yamlPath = Path.Combine(connectorDirectory, "apiDefinition.swagger.yaml");
        if (File.Exists(yamlPath))
        {
            throw new NotSupportedException($"The local test harness currently requires a JSON swagger definition. Missing '{jsonPath}'.");
        }

        throw new FileNotFoundException("Could not locate apiDefinition.swagger.json.", jsonPath);
    }

    private static IReadOnlyList<ConnectorOperation> ParseOperations(JObject swaggerDocument)
    {
        var operations = new List<ConnectorOperation>();

        foreach (var pathProperty in swaggerDocument["paths"]?.Children<JProperty>() ?? Enumerable.Empty<JProperty>())
        {
            foreach (var methodProperty in pathProperty.Value.Children<JProperty>())
            {
                string operationId = methodProperty.Value["operationId"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(operationId))
                {
                    continue;
                }

                operations.Add(new ConnectorOperation(
                    operationId,
                    methodProperty.Name.ToUpperInvariant(),
                    pathProperty.Name,
                    methodProperty.Value["summary"]?.ToString()));
            }
        }

        return operations;
    }
}