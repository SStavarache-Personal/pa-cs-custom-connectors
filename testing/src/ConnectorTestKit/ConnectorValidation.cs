using System.Text.RegularExpressions;

namespace ConnectorTestKit;

public static class ConnectorValidator
{
    private const int MaxScriptBytes = 1_000_000;

    private static readonly HashSet<string> SupportedNamespaces = new(StringComparer.Ordinal)
    {
        "System",
        "System.Collections",
        "System.Collections.Generic",
        "System.Diagnostics",
        "System.IO",
        "System.IO.Compression",
        "System.Linq",
        "System.Net",
        "System.Net.Http",
        "System.Net.Http.Headers",
        "System.Net.Security",
        "System.Security.Authentication",
        "System.Security.Cryptography",
        "System.Text",
        "System.Text.RegularExpressions",
        "System.Threading",
        "System.Threading.Tasks",
        "System.Web",
        "System.Xml",
        "System.Xml.Linq",
        "System.Drawing",
        "System.Drawing.Drawing2D",
        "System.Drawing.Imaging",
        "Microsoft.Extensions.Logging",
        "Newtonsoft.Json",
        "Newtonsoft.Json.Linq"
    };

    public static ConnectorValidationResult Validate(ConnectorDefinition connector)
    {
        var issues = new List<ConnectorValidationIssue>();

        if (!File.Exists(connector.SwaggerPath))
        {
            issues.Add(new ConnectorValidationIssue(ConnectorValidationSeverity.Error, "swagger.missing", "Missing apiDefinition.swagger.json file."));
        }

        if (!File.Exists(connector.ApiPropertiesPath))
        {
            issues.Add(new ConnectorValidationIssue(ConnectorValidationSeverity.Error, "apiProperties.missing", "Missing apiProperties.json file."));
        }

        if (!File.Exists(connector.ScriptPath))
        {
            issues.Add(new ConnectorValidationIssue(ConnectorValidationSeverity.Error, "script.missing", "Missing script.csx file."));
            return new ConnectorValidationResult(connector, issues);
        }

        var scriptInfo = new FileInfo(connector.ScriptPath);
        if (scriptInfo.Length > MaxScriptBytes)
        {
            issues.Add(new ConnectorValidationIssue(
                ConnectorValidationSeverity.Error,
                "script.size",
                $"script.csx exceeds the 1 MB platform limit ({scriptInfo.Length:N0} bytes)."));
        }

        if (!string.Equals(connector.SwaggerVersion, "2.0", StringComparison.Ordinal))
        {
            issues.Add(new ConnectorValidationIssue(
                ConnectorValidationSeverity.Error,
                "swagger.version",
                $"Swagger version must be 2.0 but was '{connector.SwaggerVersion}'."));
        }

        if (connector.Title.Length > 30)
        {
            issues.Add(new ConnectorValidationIssue(
                ConnectorValidationSeverity.Error,
                "swagger.titleLength",
                $"Swagger info.title must be 30 characters or fewer but was {connector.Title.Length}."));
        }

        var swaggerOperations = connector.Operations.Select(operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);
        var scriptOperations = connector.ScriptOperations.ToHashSet(StringComparer.Ordinal);

        foreach (string operationId in swaggerOperations.Except(scriptOperations, StringComparer.Ordinal))
        {
            issues.Add(new ConnectorValidationIssue(
                ConnectorValidationSeverity.Error,
                "scriptOperations.missing",
                $"Operation '{operationId}' exists in swagger but not in apiProperties.json scriptOperations."));
        }

        foreach (string operationId in scriptOperations.Except(swaggerOperations, StringComparer.Ordinal))
        {
            issues.Add(new ConnectorValidationIssue(
                ConnectorValidationSeverity.Error,
                "swagger.missingOperation",
                $"scriptOperations contains '{operationId}' but swagger does not define it."));
        }

        string scriptText = File.ReadAllText(connector.ScriptPath);
        foreach (string operationId in scriptOperations.Where(operationId => scriptText.IndexOf($"\"{operationId}\"", StringComparison.Ordinal) < 0))
        {
            issues.Add(new ConnectorValidationIssue(
                ConnectorValidationSeverity.Error,
                "script.missingHandler",
                $"script.csx does not appear to reference operation '{operationId}'."));
        }

        var namespaces = Regex.Matches(scriptText, "^\\s*using\\s+(?<ns>[A-Za-z0-9_.]+)\\s*;", RegexOptions.Multiline)
            .Select(match => match.Groups["ns"].Value)
            .Distinct(StringComparer.Ordinal);

        foreach (string @namespace in namespaces.Where(@namespace => !SupportedNamespaces.Contains(@namespace)))
        {
            issues.Add(new ConnectorValidationIssue(
                ConnectorValidationSeverity.Error,
                "script.unsupportedNamespace",
                $"script.csx uses unsupported namespace '{@namespace}' for Power Automate custom code."));
        }

        if (connector.ScriptOperations.Count == 0)
        {
            issues.Add(new ConnectorValidationIssue(
                ConnectorValidationSeverity.Warning,
                "scriptOperations.empty",
                "apiProperties.json does not list any script operations."));
        }

        if (connector.Operations.Count == 0)
        {
            issues.Add(new ConnectorValidationIssue(
                ConnectorValidationSeverity.Warning,
                "swagger.operations.empty",
                "No operations were discovered in apiDefinition.swagger.json."));
        }

        return new ConnectorValidationResult(connector, issues);
    }
}

public sealed class ConnectorValidationResult
{
    public ConnectorValidationResult(ConnectorDefinition connector, IReadOnlyList<ConnectorValidationIssue> issues)
    {
        Connector = connector;
        Issues = issues;
    }

    public ConnectorDefinition Connector { get; }

    public IReadOnlyList<ConnectorValidationIssue> Issues { get; }

    public bool IsValid => Issues.All(issue => issue.Severity != ConnectorValidationSeverity.Error);
}

public sealed record ConnectorValidationIssue(
    ConnectorValidationSeverity Severity,
    string Code,
    string Message);

public enum ConnectorValidationSeverity
{
    Warning,
    Error
}