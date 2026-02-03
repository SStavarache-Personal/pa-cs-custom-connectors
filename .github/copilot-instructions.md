# Power Automate C# Custom Connectors - AI Agent Guidelines

## Project Overview

This repository manages Power Automate custom connectors with C# script code. Each connector requires **exactly three files**: `apiDefinition.swagger.yaml` (OpenAPI 2.0), `script.csx` (C# code), and `README.md`. Connectors are manually deployed via copy-paste to Power Automate portal - no automated deployment exists.

## Critical Platform Constraints

Power Automate's C# runtime is **highly restricted**. Code that works in standard .NET will likely fail here:

- **Only OpenAPI 2.0** - OpenAPI 3.0 is not supported
- **Only .NET Standard 2.0 namespaces** - See [.ai/INSTRUCTIONS.md](.ai/INSTRUCTIONS.md) for allowed list (System.Data, System.Reflection, any NuGet packages NOT allowed)
- **Only one script file** per connector (no multi-file scripts)
- **1 MB max script size**, **2 minute execution timeout**
- **Must use `Context.SendAsync`** for HTTP calls (not HttpClient directly)
- **Connector title max 30 characters** in Swagger definition

## Required Class Structure

Every `script.csx` must follow this exact pattern:

```csharp
public class Script : ScriptBase
{
    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);
        
        return operationId switch
        {
            "GetItems" => await HandleGetItemsAsync(),
            _ => CreateErrorResponse(HttpStatusCode.BadRequest, 
                $"Unknown operation: {operationId}")
        };
    }
}
```

Key requirements: class named `Script`, inherits `ScriptBase`, implements `ExecuteAsync()` returning `Task<HttpResponseMessage>`.

## Naming Conventions

- **Connector folders**: PascalCase, 2-4 words (e.g., `EmailValidator`, `JsonTransformer`)
- **Required filenames**: Must be exactly `apiDefinition.swagger.yaml`, `script.csx`, `README.md`
- **C# classes/methods**: PascalCase
- **Async methods**: Must suffix with `Async`
- **Private fields**: camelCase with underscore prefix (`_logger`)

See [docs/NAMING_CONVENTIONS.md](docs/NAMING_CONVENTIONS.md) for complete rules.

## Creating New Connectors

1. Copy `_template/` folder to `connectors/{ConnectorName}/`
2. Update all three required files with implementation
3. Test operations match between `apiDefinition.swagger.yaml` `operationId` fields and C# `switch` cases in `ExecuteAsync()`
4. Verify namespace usage against allowed list in [.ai/INSTRUCTIONS.md](.ai/INSTRUCTIONS.md)
5. Update `connectors/README.md` table with new connector entry

## Common Patterns

**OperationId decoding** (handles base64 encoding in some regions):
```csharp
private string DecodeOperationId(string operationId)
{
    try {
        byte[] data = Convert.FromBase64String(operationId);
        return Encoding.UTF8.GetString(data);
    }
    catch (FormatException) { 
        return operationId; 
    }
}
```

**Error responses** should include status code, message, and error code:
```csharp
private HttpResponseMessage CreateErrorResponse(
    HttpStatusCode statusCode, 
    string message, 
    string errorCode = "ERROR")
{
    var error = new JObject
    {
        ["error"] = new JObject
        {
            ["code"] = errorCode,
            ["message"] = message
        }
    };
    return new HttpResponseMessage(statusCode)
    {
        Content = CreateJsonContent(error.ToString())
    };
}
```

## Key Documentation

- [.ai/INSTRUCTIONS.md](.ai/INSTRUCTIONS.md) - **READ FIRST** for allowed namespaces, class structure, platform APIs
- [docs/PLATFORM_LIMITATIONS.md](docs/PLATFORM_LIMITATIONS.md) - Complete namespace restrictions and quotas
- [docs/SWAGGER_GUIDE.md](docs/SWAGGER_GUIDE.md) - OpenAPI 2.0 requirements and examples
- [docs/DEPLOYMENT_GUIDE.md](docs/DEPLOYMENT_GUIDE.md) - Manual deployment steps
- [_template/](docs/../_template/) - Reference templates for all files

## Architecture Notes

- **No build process**: Scripts are plain C# files deployed as-is via UI
- **No automated testing**: Testing happens manually in Power Automate portal
- **No CI/CD pipeline**: All deployment is manual copy-paste
- **Single-file limitation**: Cannot split C# logic across multiple files
- **Stateless execution**: Each request is independent, no shared state between calls
