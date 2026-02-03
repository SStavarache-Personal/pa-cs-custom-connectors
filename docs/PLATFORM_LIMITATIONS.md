# Platform Limitations

This document details the constraints and limitations of Power Automate custom connectors with C# script code.

---

## 📊 Summary Table

| Limitation | Value | Notes |
|------------|-------|-------|
| Script file size | **1 MB** | Maximum size of `script.csx` |
| Execution timeout | **2 minutes** | Script must complete within this time |
| Scripts per connector | **1** | Only one script file allowed |
| OpenAPI version | **2.0 only** | OpenAPI 3.0 is NOT supported |
| .NET version | **.NET Standard 2.0** | Limited API surface |
| Connector title length | **30 characters** | Maximum length |

---

## 🔒 Supported Namespaces

You can **ONLY** use functions from the following namespaces. Attempting to use other namespaces will cause compilation failure.

```csharp
// Core System
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

// I/O and Compression
using System.IO;
using System.IO.Compression;

// LINQ
using System.Linq;

// Networking
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;

// Security and Cryptography
using System.Security.Authentication;
using System.Security.Cryptography;

// Text Processing
using System.Text;
using System.Text.RegularExpressions;

// Threading
using System.Threading;
using System.Threading.Tasks;

// Web Utilities
using System.Web;

// XML
using System.Xml;
using System.Xml.Linq;

// Graphics (limited)
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Logging
using Microsoft.Extensions.Logging;

// JSON (Newtonsoft)
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
```

---

## ❌ Unsupported Namespaces

The following are **NOT available**:

| Namespace | Reason |
|-----------|--------|
| `System.Data` | No database access |
| `System.Data.SqlClient` | No SQL Server connections |
| `System.Reflection` | Security restriction |
| `System.Runtime.InteropServices` | No native interop |
| `System.Configuration` | No config file access |
| `Microsoft.CSharp` | No dynamic compilation |
| `System.Dynamic` | No dynamic types |
| `System.Linq.Expressions` (full) | Limited expression support |
| Any third-party NuGet packages | Not supported |

---

## 🔌 HTTP Client Restrictions

### ✅ Recommended: Use Context.SendAsync

```csharp
// Correct approach
HttpResponseMessage response = await this.Context.SendAsync(
    this.Context.Request,
    this.CancellationToken
).ConfigureAwait(false);
```

### ⚠️ Deprecated: Creating HttpClient Directly

```csharp
// This currently works but will be BLOCKED in future versions
using var client = new HttpClient();
var response = await client.SendAsync(request);
```

Microsoft has announced they will block direct `HttpClient` creation in future updates. Always use `Context.SendAsync`.

---

## 🌐 Virtual Network Limitations

When the connector is used in a Power Platform environment linked to a Virtual Network:

- `Context.SendAsync` uses a **public endpoint**
- It **cannot access** data from private endpoints exposed on the Virtual Network
- Private APIs require alternative solutions (Azure Functions, API Management)

---

## 🔧 On-Premises Data Gateway

Custom code is **NOT supported** with the on-premises data gateway.

If you need to access on-premises resources:
1. Use Azure Hybrid Connections
2. Use Azure API Management with VPN
3. Create an Azure Function as an intermediary

---

## 📝 Logging Limitations

- **No tracing support** currently available
- Cannot write custom logs for debugging
- Recommend local testing before deployment
- Use `this.Context.Logger` for basic logging (limited functionality)

---

## 🔍 Known Issues

### OperationId Base64 Encoding

In some regions, the `OperationId` header is returned in base64 encoded format.

**Solution:** Always decode the OperationId:

```csharp
private string DecodeOperationId(string operationId)
{
    try
    {
        byte[] data = Convert.FromBase64String(operationId);
        return Encoding.UTF8.GetString(data);
    }
    catch (FormatException)
    {
        return operationId; // Not encoded, return as-is
    }
}
```

### Compilation Errors

When updating a custom connector, internal server errors often indicate compilation issues.

**Workarounds:**
1. Test code locally first
2. Ensure only supported namespaces are used
3. Verify code compiles against .NET Standard 2.0

---

## 📋 OpenAPI 2.0 Limitations

| Feature | Supported | Notes |
|---------|-----------|-------|
| OpenAPI 2.0 (Swagger) | ✅ Yes | Required format |
| OpenAPI 3.0 | ❌ No | Not supported |
| JSON format | ✅ Yes | Fully supported |
| YAML format | ✅ Yes | Fully supported |
| `$ref` references | ✅ Yes | For definitions and parameters |
| File uploads | ⚠️ Limited | Via `formData` |
| WebSockets | ❌ No | HTTP only |
| Callbacks | ❌ No | Use polling or webhooks |

---

## 🔐 Authentication Support

| Auth Type | Supported | Notes |
|-----------|-----------|-------|
| API Key | ✅ Yes | Header, query, or cookie |
| Basic Auth | ✅ Yes | Username/password |
| OAuth 2.0 | ✅ Yes | Authorization code flow preferred |
| OAuth 2.0 Client Credentials | ⚠️ Limited | Not fully supported in connector creation |
| Azure AD / Entra ID | ✅ Yes | Recommended for Microsoft services |
| Custom Auth | ✅ Yes | Via custom code |

---

## 📦 Response Size Limits

- No explicit documented limit on response size
- Large responses may cause timeout issues
- Recommend pagination for large data sets
- Binary data should be base64 encoded in JSON

---

## 🔗 References

- [Write code in a custom connector](https://learn.microsoft.com/en-us/connectors/custom-connectors/write-code)
- [Custom connector FAQ](https://learn.microsoft.com/en-us/connectors/custom-connectors/faq)
- [.NET Standard 2.0](https://learn.microsoft.com/en-us/dotnet/standard/net-standard)
- [OpenAPI 2.0 Specification](https://spec.openapis.org/oas/v2.0.html)
