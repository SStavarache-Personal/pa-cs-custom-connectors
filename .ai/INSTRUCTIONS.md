# AI Agent Instructions for Power Automate C# Custom Connectors

This document provides comprehensive rules and guidelines for AI agents creating custom connectors for Power Automate with C# script code.

---

## ⚠️ CRITICAL: Read Before Generating Code

Power Automate custom connectors have **strict limitations**. Code that works in standard .NET environments may fail in Power Platform. Always follow these rules.

---

## 🏗️ Required Class Structure

Every C# script **MUST** follow this exact structure:

```csharp
public class Script : ScriptBase
{
    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        // Your implementation here
    }
}
```

### Key Requirements

1. **Class name MUST be `Script`**
2. **Class MUST inherit from `ScriptBase`**
3. **MUST implement `ExecuteAsync()` method**
4. **Method MUST return `Task<HttpResponseMessage>`**

---

## 📦 Allowed Namespaces (Complete List)

You may **ONLY** use the following namespaces. Any other namespace will cause compilation failure:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
```

### ❌ NOT Allowed

- `System.Data` - No database access
- `System.Reflection` - No reflection
- `System.Runtime.InteropServices` - No native interop
- `Microsoft.CSharp` - No dynamic compilation
- `System.Configuration` - No config files
- Any third-party libraries (except Newtonsoft.Json)
- Any NuGet packages

---

## 📝 Available Base Classes and Interfaces

These are provided by the platform and available in your script:

```csharp
public abstract class ScriptBase
{
    // Context object for accessing request, sending HTTP, etc.
    public IScriptContext Context { get; }
    
    // CancellationToken for execution
    public CancellationToken CancellationToken { get; }
    
    // Helper: Creates StringContent from serialized JSON
    public static StringContent CreateJsonContent(string serializedJson);
    
    // Abstract method you must implement
    public abstract Task<HttpResponseMessage> ExecuteAsync();
}

public interface IScriptContext
{
    // Correlation Id for tracking
    string CorrelationId { get; }
    
    // Operation Id from Swagger definition (matches operationId)
    string OperationId { get; }
    
    // The incoming HTTP request
    HttpRequestMessage Request { get; }
    
    // Logger instance (limited functionality)
    ILogger Logger { get; }
    
    // Use this to send HTTP requests (NOT HttpClient directly)
    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken);
}
```

---

## ⚡ Platform Limitations

| Constraint | Limit |
|------------|-------|
| Script file size | **1 MB maximum** |
| Execution timeout | **2 minutes maximum** |
| Scripts per connector | **1 only** |
| .NET version | **.NET Standard 2.0** |
| OpenAPI version | **2.0 only (Swagger)** |
| On-premises data gateway | **Not supported** |
| Virtual Network private endpoints | **Not accessible via SendAsync** |

---

## 🔄 HTTP Request Best Practices

### ✅ DO: Use Context.SendAsync

```csharp
// Correct: Use the context to send requests
HttpResponseMessage response = await this.Context.SendAsync(
    this.Context.Request, 
    this.CancellationToken
).ConfigureAwait(false);
```

### ❌ DON'T: Create your own HttpClient

```csharp
// Wrong: Will be blocked in future versions
using var client = new HttpClient();
var response = await client.SendAsync(request);
```

---

## 🎯 Operation Routing Pattern

Use `OperationId` to route to different handlers:

```csharp
public override async Task<HttpResponseMessage> ExecuteAsync()
{
    // Handle base64 encoding issue in some regions
    string operationId = this.Context.OperationId;
    try 
    {
        byte[] data = Convert.FromBase64String(operationId);
        operationId = Encoding.UTF8.GetString(data);
    }
    catch (FormatException) { }
    
    // Route to appropriate handler
    switch (operationId)
    {
        case "GetItems":
            return await HandleGetItemsAsync();
        case "CreateItem":
            return await HandleCreateItemAsync();
        default:
            return CreateErrorResponse($"Unknown operation: {operationId}");
    }
}
```

---

## 📁 File Naming Conventions

### Connector Folder Names
- Use **PascalCase**
- Be descriptive but concise
- Examples: `EmailValidator`, `JsonTransformer`, `CryptoHelper`

### File Names

| File | Name | Format |
|------|------|--------|
| OpenAPI Definition | `apiDefinition.swagger.yaml` | YAML (preferred) or JSON |
| C# Script | `script.csx` | C# Script |
| Documentation | `README.md` | Markdown |
| Icon | `icon.png` | PNG (32x32 or 64x64) |

---

## 🏷️ C# Naming Conventions

Follow Microsoft's C# naming guidelines:

| Element | Convention | Example |
|---------|------------|---------|
| Classes | PascalCase | `Script`, `ResponseHandler` |
| Methods | PascalCase | `ExecuteAsync`, `TransformResponse` |
| Properties | PascalCase | `OperationId`, `Request` |
| Private fields | camelCase with underscore | `_httpClient`, `_logger` |
| Local variables | camelCase | `response`, `contentString` |
| Constants | PascalCase | `MaxRetryCount`, `DefaultTimeout` |
| Async methods | Suffix with Async | `HandleRequestAsync`, `ProcessDataAsync` |

---

## 📄 Swagger (OpenAPI 2.0) Conventions

### Operation IDs
- Use **PascalCase**
- No hyphens or underscores
- Examples: `GetItems`, `CreateUser`, `DeleteRecord`

```yaml
# ✅ Good
operationId: GetMessages

# ❌ Bad
operationId: get-messages
operationId: Get_Messages
operationId: getMessages
```

### Required Swagger Structure

```yaml
swagger: "2.0"
info:
  title: "Connector Name"
  description: "What this connector does."
  version: "1.0.0"
host: "api.example.com"
basePath: "/"
schemes:
  - https
consumes:
  - application/json
produces:
  - application/json
paths:
  /endpoint:
    get:
      operationId: GetEndpoint
      summary: "Short summary"
      description: "Detailed description ending with period."
      responses:
        200:
          description: "Success response."
          schema:
            $ref: "#/definitions/ResponseModel"
        400:
          description: "Bad request."
        500:
          description: "Internal server error."
definitions:
  ResponseModel:
    type: object
    properties:
      id:
        type: string
        description: "The unique identifier."
```

---

## 📋 Creating a New Connector Checklist

When creating a new connector:

- [ ] Create folder under `connectors/` with PascalCase name
- [ ] Create `README.md` with connector purpose and usage
- [ ] Create `apiDefinition.swagger.yaml` in OpenAPI 2.0 format
- [ ] Create `script.csx` with `Script : ScriptBase` class
- [ ] Verify only allowed namespaces are used
- [ ] Ensure all operationIds are PascalCase
- [ ] Add summaries and descriptions to all operations
- [ ] Define proper response schemas
- [ ] Test code compiles with .NET Standard 2.0

---

## 🔧 Script Template

Use this template when creating new connectors:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Script : ScriptBase
{
    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        // Decode OperationId (handles base64 encoding in some regions)
        string operationId = DecodeOperationId(this.Context.OperationId);
        
        // Route to appropriate handler based on operation
        return operationId switch
        {
            "OperationName" => await HandleOperationNameAsync(),
            _ => CreateBadRequestResponse($"Unknown operation: {operationId}")
        };
    }
    
    private async Task<HttpResponseMessage> HandleOperationNameAsync()
    {
        // Read request content
        string content = await this.Context.Request.Content
            .ReadAsStringAsync()
            .ConfigureAwait(false);
        
        // Parse JSON
        JObject requestBody = JObject.Parse(content);
        
        // Process and create response
        JObject responseBody = new JObject
        {
            ["result"] = "success",
            ["data"] = requestBody
        };
        
        return CreateJsonResponse(HttpStatusCode.OK, responseBody);
    }
    
    #region Helper Methods
    
    private string DecodeOperationId(string operationId)
    {
        try
        {
            byte[] data = Convert.FromBase64String(operationId);
            return Encoding.UTF8.GetString(data);
        }
        catch (FormatException)
        {
            return operationId;
        }
    }
    
    private HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, JObject body)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = CreateJsonContent(body.ToString())
        };
        return response;
    }
    
    private HttpResponseMessage CreateBadRequestResponse(string message)
    {
        var body = new JObject
        {
            ["error"] = message
        };
        return CreateJsonResponse(HttpStatusCode.BadRequest, body);
    }
    
    #endregion
}
```

---

## 🚫 Common Mistakes to Avoid

1. **Using unsupported namespaces** - Will fail at compile time
2. **Creating HttpClient directly** - Use `Context.SendAsync` instead
3. **OpenAPI 3.0 format** - Only 2.0 is supported
4. **Multiple script files** - Only one per connector
5. **Exceeding 1 MB script size** - Will be rejected
6. **Operations taking > 2 minutes** - Will timeout
7. **Using `default` as only response** - Define specific HTTP codes
8. **Missing operation descriptions** - Required for good UX
9. **Using underscores/hyphens in operationId** - Use PascalCase only

---

## 📚 Reference Links

- [Write code in a custom connector](https://learn.microsoft.com/en-us/connectors/custom-connectors/write-code)
- [Coding standards](https://learn.microsoft.com/en-us/connectors/custom-connectors/coding-standards)
- [OpenAPI 2.0 Specification](https://spec.openapis.org/oas/v2.0.html)
- [.NET Standard 2.0 APIs](https://learn.microsoft.com/en-us/dotnet/standard/net-standard)
