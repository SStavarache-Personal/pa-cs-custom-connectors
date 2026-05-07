# Naming Conventions

This document defines the naming standards for all files, folders, and code elements in this repository.

---

## 📁 Folder Naming

### Connector Folders

| Rule | Convention | Example |
|------|------------|---------|
| Casing | **PascalCase** | `EmailValidator`, `JsonTransformer` |
| Words | Descriptive, no abbreviations | `CryptoHelper` not `CryptoHlpr` |
| Separators | None (no hyphens, underscores, spaces) | `DataFormatter` not `Data-Formatter` |
| Length | Keep concise but clear | 2-4 words maximum |

**Good Examples:**
- `EmailValidator`
- `JsonTransformer`
- `HttpRequestLogger`
- `DateTimeConverter`
- `CsvParser`

**Bad Examples:**
- `email-validator` (lowercase, hyphen)
- `JSON_Transformer` (underscore)
- `crypto` (too short, unclear)
- `MyAwesomeConnectorThatDoesEverything` (too long)

---

## 📄 File Naming

### Standard Files (Required)

Every connector folder must have these files with exact names:

| File | Name | Format |
|------|------|--------|
| OpenAPI Definition | `apiDefinition.swagger.json` | JSON |
| Connector Metadata | `apiProperties.json` | JSON |
| C# Script | `script.csx` | C# Script |
| Documentation | `README.md` | Markdown |

### Optional Files

| File | Name | Format |
|------|------|--------|
| Connector Icon | `icon.png` | PNG (32x32 or 64x64) |
| Test Data | `testdata.json` | JSON |
| Change Log | `CHANGELOG.md` | Markdown |

---

## 🔤 C# Code Naming Conventions

Follow Microsoft's official C# naming guidelines.

### Classes

| Rule | Convention | Example |
|------|------------|---------|
| Casing | PascalCase | `Script`, `ResponseHandler` |
| Nouns | Use noun or noun phrases | `DataProcessor`, `ItemValidator` |
| Main Class | Must be named `Script` | `public class Script : ScriptBase` |

### Methods

| Rule | Convention | Example |
|------|------------|---------|
| Casing | PascalCase | `ExecuteAsync`, `ProcessRequest` |
| Verbs | Start with verb | `Get`, `Create`, `Validate`, `Transform` |
| Async | Suffix with `Async` | `HandleRequestAsync`, `FetchDataAsync` |

**Examples:**
```csharp
public async Task<HttpResponseMessage> ExecuteAsync()
public async Task<HttpResponseMessage> HandleGetItemsAsync()
private JObject TransformResponse(JObject input)
private bool ValidateInput(string data)
```

### Properties

| Rule | Convention | Example |
|------|------------|---------|
| Casing | PascalCase | `OperationId`, `Request` |
| Nouns | Use noun or noun phrases | `ItemCount`, `MaxRetries` |

### Private Fields

| Rule | Convention | Example |
|------|------------|---------|
| Casing | camelCase with underscore prefix | `_httpClient`, `_logger` |
| Prefix | Start with underscore | `_configuration` |

**Examples:**
```csharp
private readonly ILogger _logger;
private int _retryCount;
private string _baseUrl;
```

### Local Variables and Parameters

| Rule | Convention | Example |
|------|------------|---------|
| Casing | camelCase | `response`, `itemId`, `content` |
| Descriptive | Use meaningful names | `customerName` not `cn` |

**Examples:**
```csharp
string content = await request.Content.ReadAsStringAsync();
JObject responseBody = JObject.Parse(content);
int itemCount = items.Count;
```

### Constants

| Rule | Convention | Example |
|------|------------|---------|
| Casing | PascalCase | `MaxRetryCount`, `DefaultTimeout` |
| Location | Class level, `const` or `static readonly` | - |

**Examples:**
```csharp
private const int MaxRetryCount = 3;
private const string DefaultContentType = "application/json";
private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
```

---

## 🏷️ OpenAPI (Swagger) Naming

### Operation IDs

| Rule | Convention | Example |
|------|------------|---------|
| Casing | **PascalCase** | `GetItems`, `CreateUser` |
| Verbs | Start with HTTP verb concept | `Get`, `Create`, `Update`, `Delete`, `List` |
| No Separators | No hyphens or underscores | `GetItemById` not `Get-Item-By-Id` |

**Good Examples:**
```yaml
operationId: GetItems
operationId: CreateItem
operationId: UpdateUser
operationId: DeleteRecord
operationId: ListAllCategories
operationId: SearchByName
```

**Bad Examples:**
```yaml
operationId: get-items        # lowercase, hyphen
operationId: Get_Items        # underscore
operationId: getItems         # camelCase
operationId: GETITEMS         # all caps
```

### Path Parameters

| Rule | Convention | Example |
|------|------------|---------|
| Casing | camelCase | `{itemId}`, `{userId}` |
| Descriptive | Clear, specific names | `{orderId}` not `{id}` |

**Example:**
```yaml
paths:
  /items/{itemId}:
    get:
      operationId: GetItemById
      parameters:
        - name: itemId
          in: path
          type: string
```

### Query Parameters

| Rule | Convention | Example |
|------|------------|---------|
| Casing | camelCase | `pageSize`, `searchTerm` |
| Common Names | Use industry standards | `limit`, `offset`, `sort`, `filter` |

### Schema/Definition Names

| Rule | Convention | Example |
|------|------------|---------|
| Casing | PascalCase | `ItemResponse`, `CreateUserRequest` |
| Suffix | Use descriptive suffixes | `Request`, `Response`, `Model` |

**Examples:**
```yaml
definitions:
  Item:
    type: object
  ItemListResponse:
    type: object
  CreateItemRequest:
    type: object
  ErrorResponse:
    type: object
```

---

## 📝 Summary and Description Guidelines

### Summaries
- Keep short (max 80 characters)
- Action-oriented phrases
- Title case not required

```yaml
# Good
summary: "Get all items"
summary: "Create a new user"
summary: "Delete item by ID"

# Bad
summary: "This operation gets all the items from the database"  # Too long
summary: "Items"  # Not descriptive
```

### Descriptions
- Complete sentences
- End with a period
- Provide useful context

```yaml
# Good
description: "Retrieves all items with optional pagination support."
description: "Creates a new user account with the provided details."

# Bad
description: "Get items"  # Same as summary, not helpful
description: "items"  # Not a sentence
```

---

## 🔢 Version Naming

For connector versions, use semantic versioning:

| Format | Example | When to Use |
|--------|---------|-------------|
| MAJOR.MINOR.PATCH | `1.0.0` | General use |
| MAJOR.MINOR | `1.0` | When patch isn't needed |

**Examples:**
- `1.0.0` - Initial release
- `1.1.0` - New features added
- `1.1.1` - Bug fixes
- `2.0.0` - Breaking changes

---

## ✅ Naming Checklist

When creating a new connector:

- [ ] Folder name is PascalCase
- [ ] Files use standard names (`apiDefinition.swagger.json`, `apiProperties.json`, `script.csx`, `README.md`)
- [ ] Class is named `Script`
- [ ] All methods are PascalCase
- [ ] Async methods end with `Async`
- [ ] Private fields use underscore prefix
- [ ] Local variables are camelCase
- [ ] Operation IDs are PascalCase (no hyphens/underscores)
- [ ] Path parameters are camelCase
- [ ] Definition names are PascalCase
- [ ] Summaries are under 80 characters
- [ ] Descriptions are complete sentences
