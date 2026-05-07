# OpenAPI 2.0 (Swagger) Guide

This guide covers the requirements and best practices for creating OpenAPI 2.0 definitions for Power Automate custom connectors.

In this repository, new or updated connector definitions should be stored as `apiDefinition.swagger.json`. The examples below are structural guidance only.

---

## ⚠️ Important: OpenAPI 2.0 Only

Power Automate custom connectors **only support OpenAPI 2.0** (formerly known as Swagger). OpenAPI 3.0 is **NOT supported**.

---

## 📋 Required Structure

Every OpenAPI definition must include these sections in order:

```yaml
swagger: "2.0"                    # Required: Must be 2.0

info:                             # Required: Connector metadata
  title: "Connector Name"
  description: "What it does."
  version: "1.0.0"

host: "api.example.com"           # Required: API host
basePath: "/"                     # Optional: Base path
schemes:                          # Required: Protocols
  - https

consumes:                         # Optional: Input content types
  - application/json
produces:                         # Optional: Output content types
  - application/json

securityDefinitions: {}           # Optional: Auth configuration
security: []                      # Optional: Applied security

paths: {}                         # Required: API operations

definitions: {}                   # Optional: Data models
parameters: {}                    # Optional: Reusable parameters
```

---

## 📝 Info Section

### Requirements

| Field | Max Length | Rules |
|-------|------------|-------|
| `title` | 30 characters | No "API" word, no product names |
| `description` | No limit | No Power Platform product mentions |
| `version` | - | Semantic versioning recommended |

### Example

```yaml
info:
  title: "Email Validator"
  description: "Validates email addresses and provides detailed information about email domains."
  version: "1.0.0"
  contact:
    name: "Your Name"
    email: "email@example.com"
```

---

## 🔐 Security Definitions

### API Key Authentication

```yaml
securityDefinitions:
  api_key:
    type: apiKey
    in: header                    # or: query, cookie
    name: X-API-Key               # Header/parameter name

security:
  - api_key: []
```

### Basic Authentication

```yaml
securityDefinitions:
  basic:
    type: basic

security:
  - basic: []
```

### OAuth 2.0

```yaml
securityDefinitions:
  oauth2:
    type: oauth2
    flow: accessCode              # or: implicit, password, application
    authorizationUrl: https://example.com/oauth/authorize
    tokenUrl: https://example.com/oauth/token
    scopes:
      read: "Read access"
      write: "Write access"

security:
  - oauth2:
      - read
      - write
```

---

## 🛤️ Paths (Operations)

### Operation Structure

```yaml
paths:
  /items:
    get:
      operationId: GetItems           # PascalCase, unique
      summary: "Get all items"        # Short, max 80 chars
      description: "Retrieves all items with pagination."  # Ends with period
      x-ms-visibility: important      # Power Platform extension
      tags:
        - Items
      parameters:                     # Input parameters
        - name: limit
          in: query
          type: integer
          required: false
          default: 10
          description: "Maximum items to return."
          x-ms-summary: "Limit"       # Power Platform extension
      responses:                      # Response definitions
        200:
          description: "Success"
          schema:
            $ref: "#/definitions/ItemList"
        400:
          description: "Bad request"
        500:
          description: "Server error"
```

### HTTP Methods

| Method | Usage | Example |
|--------|-------|---------|
| GET | Retrieve data | `GetItems`, `GetUserById` |
| POST | Create new resource | `CreateItem`, `SubmitOrder` |
| PUT | Replace entire resource | `ReplaceItem`, `UpdateUser` |
| PATCH | Partial update | `UpdateItemName`, `PatchUser` |
| DELETE | Remove resource | `DeleteItem`, `RemoveUser` |

### Parameter Locations

| `in` Value | Description | Example |
|------------|-------------|---------|
| `path` | URL path segment | `/items/{id}` |
| `query` | Query string | `?limit=10` |
| `header` | HTTP header | `X-Custom-Header` |
| `body` | Request body | JSON payload |
| `formData` | Form data | File uploads |

---

## 📦 Parameters

### Path Parameters

```yaml
paths:
  /items/{itemId}:
    get:
      parameters:
        - name: itemId
          in: path
          type: string
          required: true              # Always required for path
          description: "The item's unique identifier."
          x-ms-summary: "Item ID"
          x-ms-url-encoding: single   # Prevent double encoding
```

### Query Parameters

```yaml
parameters:
  - name: searchTerm
    in: query
    type: string
    required: false
    description: "Text to search for."
    x-ms-summary: "Search Term"
  - name: pageSize
    in: query
    type: integer
    required: false
    default: 25
    minimum: 1
    maximum: 100
    description: "Number of results per page."
    x-ms-summary: "Page Size"
```

### Body Parameters

```yaml
parameters:
  - name: body
    in: body
    required: true
    description: "The item to create."
    schema:
      $ref: "#/definitions/CreateItemRequest"
```

### Reusable Parameters

Define once, use everywhere:

```yaml
parameters:
  ItemIdInPath:
    name: itemId
    in: path
    type: string
    required: true
    description: "The item's unique identifier."
    x-ms-summary: "Item ID"

paths:
  /items/{itemId}:
    get:
      parameters:
        - $ref: "#/parameters/ItemIdInPath"
    delete:
      parameters:
        - $ref: "#/parameters/ItemIdInPath"
```

---

## 📊 Definitions (Schemas)

### Object Schema

```yaml
definitions:
  Item:
    type: object
    required:
      - id
      - name
    properties:
      id:
        type: string
        description: "Unique identifier."
        x-ms-summary: "ID"
      name:
        type: string
        description: "Item name."
        x-ms-summary: "Name"
      description:
        type: string
        description: "Optional description."
        x-ms-summary: "Description"
      createdAt:
        type: string
        format: date-time
        description: "Creation timestamp."
        x-ms-summary: "Created At"
      isActive:
        type: boolean
        description: "Whether the item is active."
        x-ms-summary: "Is Active"
      count:
        type: integer
        format: int32
        description: "Number of sub-items."
        x-ms-summary: "Count"
      price:
        type: number
        format: double
        description: "Item price."
        x-ms-summary: "Price"
```

### Array Schema

```yaml
definitions:
  ItemList:
    type: object
    properties:
      items:
        type: array
        description: "List of items."
        items:
          $ref: "#/definitions/Item"
      total:
        type: integer
        description: "Total count."
```

### Data Types

| Type | Format | Description | Example |
|------|--------|-------------|---------|
| `string` | - | Text | `"hello"` |
| `string` | `date` | Date only | `"2024-01-15"` |
| `string` | `date-time` | ISO 8601 | `"2024-01-15T10:30:00Z"` |
| `string` | `byte` | Base64 | Binary data |
| `string` | `binary` | Raw binary | File content |
| `string` | `password` | Masked input | Sensitive data |
| `integer` | `int32` | 32-bit integer | `42` |
| `integer` | `int64` | 64-bit integer | Large numbers |
| `number` | `float` | 32-bit decimal | `3.14` |
| `number` | `double` | 64-bit decimal | Precise decimals |
| `boolean` | - | True/false | `true` |
| `array` | - | List | `[1, 2, 3]` |
| `object` | - | JSON object | `{"key": "value"}` |

---

## 🔗 Power Platform Extensions (x-ms-*)

These custom extensions enhance the Power Platform experience:

### Visibility

```yaml
x-ms-visibility: important    # Show prominently
x-ms-visibility: advanced     # Show in advanced options
x-ms-visibility: internal     # Hide from users
```

### Summary (Display Name)

```yaml
x-ms-summary: "Item Name"     # Friendly display name
```

### Dynamic Values

```yaml
x-ms-dynamic-values:
  operationId: GetCategories
  value-path: id
  value-title: name
```

### Dynamic Schema

```yaml
x-ms-dynamic-schema:
  operationId: GetSchema
  parameters:
    type: { parameter: type }
  value-path: schema
```

### URL Encoding

```yaml
x-ms-url-encoding: single     # Prevent double encoding in path
```

---

## ✅ Response Definitions

### Always Define Specific Responses

```yaml
responses:
  200:
    description: "Successfully retrieved items."
    schema:
      $ref: "#/definitions/ItemList"
  201:
    description: "Item created successfully."
    schema:
      $ref: "#/definitions/Item"
  204:
    description: "Item deleted successfully."
    # No schema for 204
  400:
    description: "Bad request. Invalid parameters."
    schema:
      $ref: "#/definitions/ErrorResponse"
  401:
    description: "Unauthorized. Authentication required."
  403:
    description: "Forbidden. Insufficient permissions."
  404:
    description: "Not found. Resource does not exist."
  500:
    description: "Internal server error."
  default:
    description: "Unexpected error."
    schema:
      $ref: "#/definitions/ErrorResponse"
```

### ❌ Don't Use Only Default

```yaml
# Bad - Don't do this
responses:
  default:
    description: "Response"
    schema:
      type: string
```

---

## 📋 Complete Example

```yaml
swagger: "2.0"
info:
  title: "Task Manager"
  description: "Manage tasks and to-do items."
  version: "1.0.0"

host: "api.taskmanager.com"
basePath: "/v1"
schemes:
  - https
consumes:
  - application/json
produces:
  - application/json

securityDefinitions:
  api_key:
    type: apiKey
    in: header
    name: X-API-Key

security:
  - api_key: []

paths:
  /tasks:
    get:
      operationId: GetTasks
      summary: "Get all tasks"
      description: "Retrieves all tasks for the authenticated user."
      x-ms-visibility: important
      parameters:
        - name: status
          in: query
          type: string
          enum: [pending, completed, all]
          default: all
          description: "Filter by task status."
          x-ms-summary: "Status"
      responses:
        200:
          description: "Tasks retrieved successfully."
          schema:
            $ref: "#/definitions/TaskList"
        401:
          description: "Unauthorized."
        500:
          description: "Server error."

    post:
      operationId: CreateTask
      summary: "Create task"
      description: "Creates a new task."
      parameters:
        - name: body
          in: body
          required: true
          schema:
            $ref: "#/definitions/CreateTaskRequest"
      responses:
        201:
          description: "Task created."
          schema:
            $ref: "#/definitions/Task"
        400:
          description: "Invalid request."

  /tasks/{taskId}:
    get:
      operationId: GetTaskById
      summary: "Get task by ID"
      description: "Retrieves a specific task by its identifier."
      parameters:
        - $ref: "#/parameters/TaskIdInPath"
      responses:
        200:
          description: "Task found."
          schema:
            $ref: "#/definitions/Task"
        404:
          description: "Task not found."

    delete:
      operationId: DeleteTask
      summary: "Delete task"
      description: "Deletes a task by its identifier."
      parameters:
        - $ref: "#/parameters/TaskIdInPath"
      responses:
        204:
          description: "Task deleted."
        404:
          description: "Task not found."

parameters:
  TaskIdInPath:
    name: taskId
    in: path
    type: string
    required: true
    description: "The task's unique identifier."
    x-ms-summary: "Task ID"
    x-ms-url-encoding: single

definitions:
  Task:
    type: object
    properties:
      id:
        type: string
        description: "Unique identifier."
        x-ms-summary: "ID"
      title:
        type: string
        description: "Task title."
        x-ms-summary: "Title"
      status:
        type: string
        enum: [pending, completed]
        description: "Task status."
        x-ms-summary: "Status"
      dueDate:
        type: string
        format: date-time
        description: "Due date."
        x-ms-summary: "Due Date"

  TaskList:
    type: object
    properties:
      tasks:
        type: array
        items:
          $ref: "#/definitions/Task"
      count:
        type: integer

  CreateTaskRequest:
    type: object
    required:
      - title
    properties:
      title:
        type: string
        description: "Task title."
        x-ms-summary: "Title"
      dueDate:
        type: string
        format: date-time
        description: "Optional due date."
        x-ms-summary: "Due Date"
```

---

## 🔗 References

- [OpenAPI 2.0 Specification](https://spec.openapis.org/oas/v2.0.html)
- [Connector coding standards](https://learn.microsoft.com/en-us/connectors/custom-connectors/coding-standards)
- [Create a custom connector from an OpenAPI definition](https://learn.microsoft.com/en-us/connectors/custom-connectors/define-openapi-definition)
