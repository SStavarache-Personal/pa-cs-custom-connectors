# HTTP Request Advanced

Enhanced HTTP request connector for Power Automate with comprehensive redirect handling and full HTTP client capabilities.

## Overview

**HTTP Request Advanced** provides all the functionality of the standard Power Automate HTTP connector plus automatic redirect handling and additional features inspired by the Python `requests` library. This connector gives you complete control over HTTP requests while automatically handling complex scenarios like 3xx redirects.

## Features

### Core Capabilities
- ✅ **All HTTP Methods**: GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS
- ✅ **Automatic Redirect Handling**: Follows 301, 302, 303, 307, 308 redirects
- ✅ **Custom Headers**: Pass headers as JSON object
- ✅ **Query Parameters**: Pass query params as JSON object
- ✅ **Request Body**: Support for JSON, form data, XML, plain text
- ✅ **Multiple Authentication Types**: None, Basic, Bearer
- ✅ **Configurable Timeouts**: Set request timeout in seconds
- ✅ **SSL Validation**: Control SSL certificate validation
- ✅ **Redirect Loop Detection**: Prevents infinite redirect cycles
- ✅ **Detailed Response**: Returns status, headers, body, redirect count, final URL
- ✅ **Batch Requests**: Execute multiple HTTP requests in parallel and combine results

### Advanced Features
- **Redirect Control**: Choose whether to follow redirects and set maximum redirect count
- **Timeout Management**: Configure request timeout to prevent hanging operations
- **Authentication**: Built-in support for Basic and Bearer token authentication
- **Content Type Flexibility**: Specify content type explicitly or via headers
- **Response Metadata**: Get complete response information including all headers and redirect history

## Operations

### 1. Execute HTTP Request (`ExecuteHttpRequest`)

Execute a single HTTP request with full control over method, headers, query parameters, body, and redirect handling.

### 2. Execute Batch HTTP Requests (`ExecuteBatchHttpRequests`)

Execute multiple HTTP requests in parallel and combine all response bodies into a single results array. Designed for combining paginated API results into one dataset, but works for any scenario where multiple requests need to be made and results merged.

## Use Cases

1. **API Integration with Redirects**: Call APIs that use redirects for load balancing or versioning
2. **Web Scraping**: Handle URL redirects when fetching web content
3. **File Downloads**: Follow redirect chains to download files from CDNs
4. **OAuth Flows**: Handle redirect-based authentication flows
5. **Testing & Debugging**: Get detailed information about HTTP interactions
6. **Legacy System Integration**: Work with older APIs that use extensive redirects
7. **Paginated API Aggregation**: Combine results from multiple pages into a single array
8. **Multi-Endpoint Data Collection**: Fetch data from multiple endpoints simultaneously

## Input Parameters

### Execute HTTP Request

#### Required Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `url` | string | Complete URL including protocol (e.g., `https://api.example.com/data`) |
| `method` | string | HTTP method: GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS |

### Optional Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `headers` | string (JSON) | `{}` | Custom headers as JSON object, e.g., `{"Authorization":"Bearer token"}` |
| `queryParameters` | string (JSON) | `{}` | Query parameters as JSON object, e.g., `{"page":"1","limit":"10"}` |
| `body` | string | - | Request body content (JSON, XML, form data, plain text) |
| `followRedirects` | boolean | `true` | Whether to automatically follow HTTP redirects |
| `maxRedirects` | integer | `10` | Maximum number of redirects to follow (0-50) |
| `timeoutSeconds` | integer | `100` | Request timeout in seconds |
| `authenticationType` | string | `None` | Authentication type: None, Basic, Bearer |
| `username` | string | - | Username for Basic authentication |
| `password` | string | - | Password for Basic authentication |
| `bearerToken` | string | - | Token for Bearer authentication |
| `contentType` | string | - | Content-Type header (overrides Content-Type in headers) |
| `validateSSL` | boolean | `true` | Whether to validate SSL certificates |

### Execute Batch HTTP Requests

#### Required Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `requests` | array | Array of request objects, each with `url` (string, required), `method` (string, required), `headers` (JSON string, optional), `queryParameters` (JSON string, optional), `body` (string, optional) |

#### Optional Parameters (Shared)

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `resultsPath` | string | - | JSON property name (dot-notation) to extract from each response and merge, e.g., `items` or `data.results`. If omitted, the entire response body is added to the results array. |
| `authenticationType` | string | `None` | Authentication type applied to all requests: None, Basic, Bearer |
| `username` | string | - | Username for Basic authentication |
| `password` | string | - | Password for Basic authentication |
| `bearerToken` | string | - | Token for Bearer authentication |
| `followRedirects` | boolean | `true` | Whether to follow HTTP redirects |
| `maxRedirects` | integer | `10` | Max redirects per request (0-50) |
| `timeoutSeconds` | integer | `100` | Timeout per individual request in seconds |
| `contentType` | string | - | Default Content-Type header for all requests |
| `validateSSL` | boolean | `true` | Whether to validate SSL certificates |

## Output Schema

### Execute HTTP Request Response

```json
{
  "statusCode": 200,
  "statusDescription": "OK",
  "headers": {
    "Content-Type": "application/json",
    "Content-Length": "1234",
    "Server": "nginx"
  },
  "body": "Response content as string",
  "redirectCount": 2,
  "finalUrl": "https://api.example.com/v2/data",
  "isSuccess": true
}
```

### Output Fields

| Field | Type | Description |
|-------|------|-------------|
| `statusCode` | integer | HTTP status code (e.g., 200, 404, 500) |
| `statusDescription` | string | HTTP status description (e.g., "OK", "Not Found") |
| `headers` | object | Response headers as key-value pairs |
| `body` | string | Response body content |
| `redirectCount` | integer | Number of redirects followed |
| `finalUrl` | string | Final URL after following all redirects |
| `isSuccess` | boolean | True if status code is 2xx |

### Execute Batch HTTP Requests Response

```json
{
  "totalRequests": 3,
  "successCount": 3,
  "failureCount": 0,
  "totalResults": 75,
  "results": [
    { "id": 1, "name": "Item 1" },
    { "id": 2, "name": "Item 2" }
  ],
  "errors": []
}
```

#### Output Fields

| Field | Type | Description |
|-------|------|-------------|
| `totalRequests` | integer | Total number of requests submitted |
| `successCount` | integer | Number of requests that returned 2xx |
| `failureCount` | integer | Number of requests that failed |
| `totalResults` | integer | Total items in the combined results array |
| `results` | array | Combined results from all successful responses |
| `errors` | array | Details of any failed requests (each with `requestIndex`, `url`, `statusCode`, `error`) |

## Usage Examples

### Execute HTTP Request Examples

### Example 1: Simple GET Request

```json
{
  "url": "https://api.example.com/users",
  "method": "GET"
}
```

### Example 2: POST Request with JSON Body

```json
{
  "url": "https://api.example.com/users",
  "method": "POST",
  "headers": "{\"Content-Type\":\"application/json\"}",
  "body": "{\"name\":\"John Doe\",\"email\":\"john@example.com\"}"
}
```

### Example 3: GET Request with Query Parameters

```json
{
  "url": "https://api.example.com/search",
  "method": "GET",
  "queryParameters": "{\"q\":\"power automate\",\"page\":\"1\",\"limit\":\"10\"}"
}
```

### Example 4: Request with Bearer Authentication

```json
{
  "url": "https://api.example.com/protected",
  "method": "GET",
  "authenticationType": "Bearer",
  "bearerToken": "your-access-token-here"
}
```

### Example 5: Request with Basic Authentication

```json
{
  "url": "https://api.example.com/admin",
  "method": "GET",
  "authenticationType": "Basic",
  "username": "admin",
  "password": "secret123"
}
```

### Example 6: Request with Custom Headers and Timeout

```json
{
  "url": "https://api.example.com/slow-endpoint",
  "method": "GET",
  "headers": "{\"User-Agent\":\"PowerAutomate/1.0\",\"X-Custom-Header\":\"value\"}",
  "timeoutSeconds": 30
}
```

### Example 7: Request Without Following Redirects

```json
{
  "url": "https://short.url/abc123",
  "method": "GET",
  "followRedirects": false
}
```

### Example 8: PUT Request with XML Body

```json
{
  "url": "https://api.example.com/resource/123",
  "method": "PUT",
  "contentType": "application/xml",
  "body": "<user><name>John Doe</name><email>john@example.com</email></user>"
}
```

### Example 9: DELETE Request with Custom Headers

```json
{
  "url": "https://api.example.com/resource/456",
  "method": "DELETE",
  "headers": "{\"Authorization\":\"Bearer token123\",\"X-Request-ID\":\"req-789\"}"
}
```

### Example 10: Form Data POST

```json
{
  "url": "https://api.example.com/form",
  "method": "POST",
  "contentType": "application/x-www-form-urlencoded",
  "body": "field1=value1&field2=value2&field3=value3"
}
```

### Execute Batch HTTP Requests Examples

### Example 11: Combine Paginated Results

```json
{
  "requests": [
    { "url": "https://api.example.com/items?page=1", "method": "GET" },
    { "url": "https://api.example.com/items?page=2", "method": "GET" },
    { "url": "https://api.example.com/items?page=3", "method": "GET" }
  ],
  "resultsPath": "items",
  "authenticationType": "Bearer",
  "bearerToken": "your-access-token-here"
}
```

### Example 12: Batch GET from Multiple Endpoints

```json
{
  "requests": [
    { "url": "https://api.example.com/users", "method": "GET" },
    { "url": "https://api.example.com/orders", "method": "GET" },
    { "url": "https://api.example.com/products", "method": "GET" }
  ],
  "authenticationType": "Basic",
  "username": "admin",
  "password": "secret123"
}
```

### Example 13: Batch with Per-Request Headers and Query Parameters

```json
{
  "requests": [
    {
      "url": "https://api.example.com/search",
      "method": "GET",
      "queryParameters": "{\"q\":\"power automate\",\"page\":\"1\"}",
      "headers": "{\"X-Custom-Header\":\"value1\"}"
    },
    {
      "url": "https://api.example.com/search",
      "method": "GET",
      "queryParameters": "{\"q\":\"power automate\",\"page\":\"2\"}",
      "headers": "{\"X-Custom-Header\":\"value2\"}"
    }
  ],
  "resultsPath": "data.results",
  "timeoutSeconds": 60
}
```

## Redirect Handling

The connector automatically handles the following HTTP redirect status codes:

- **301 Moved Permanently**: Resource permanently moved to new location
- **302 Found**: Temporary redirect
- **303 See Other**: Redirect with method change to GET
- **307 Temporary Redirect**: Temporary redirect preserving method
- **308 Permanent Redirect**: Permanent redirect preserving method

### Redirect Behavior

1. **Automatic Following**: When `followRedirects` is `true`, the connector automatically follows redirect chains
2. **Method Preservation**: For 301, 302, 307, 308, the original HTTP method is preserved
3. **Method Change**: For 303, the method is changed to GET (per HTTP specification)
4. **Loop Detection**: Prevents infinite loops by tracking visited URLs
5. **Max Redirects**: Stops after reaching `maxRedirects` to prevent excessive chaining
6. **Relative URLs**: Correctly handles both absolute and relative redirect URLs

### Example Redirect Scenario

Request to `https://old.example.com/api/data` might:
1. Return 301 redirect to `https://new.example.com/api/data`
2. Follow to `https://new.example.com/api/data`
3. Return 302 redirect to `https://cdn.example.com/api/data`
4. Follow to `https://cdn.example.com/api/data`
5. Return 200 OK with actual data

Output will show:
- `redirectCount`: 2
- `finalUrl`: "https://cdn.example.com/api/data"
- `statusCode`: 200

## Error Handling

The connector returns structured error responses for various failure scenarios:

### Error Response Format

```json
{
  "error": {
    "code": "ERROR_CODE",
    "message": "Detailed error message"
  }
}
```

### Common Error Codes

| Error Code | Description |
|------------|-------------|
| `MISSING_URL` | URL parameter is required but not provided |
| `INVALID_URL` | URL format is invalid |
| `INVALID_JSON` | Request body or parameter is not valid JSON |
| `INVALID_MAX_REDIRECTS` | Max redirects value is out of allowed range (0-50) |
| `REQUEST_TIMEOUT` | Request exceeded the specified timeout |
| `HTTP_REQUEST_FAILED` | HTTP request failed (network error, DNS failure, etc.) |
| `INTERNAL_ERROR` | Unexpected error occurred during execution |
| `MISSING_REQUESTS` | Batch requests array is missing or empty |
| `BATCH_TOO_LARGE` | Batch size exceeds maximum of 20 requests |
| `INVALID_REQUEST_ITEM` | A request item in the batch array is not a valid object |
| `BATCH_ERROR` | Unexpected error during batch execution |

### Timeout Handling

If a request exceeds the `timeoutSeconds` value, it will be cancelled and return:
- Status Code: 408 (Request Timeout)
- Error Code: `REQUEST_TIMEOUT`

### Redirect Limits

- **Max Redirects**: Can be set between 0 and 50
- **Loop Detection**: Returns error if the same URL is visited twice
- **Exceeded Limit**: Returns error if redirect count exceeds `maxRedirects`

## Best Practices

### 1. Set Appropriate Timeouts
```json
{
  "timeoutSeconds": 30  // Adjust based on expected response time
}
```

### 2. Use Structured Headers
```json
{
  "headers": "{\"Content-Type\":\"application/json\",\"Accept\":\"application/json\"}"
}
```

### 3. Handle Sensitive Data Securely
- Store passwords and tokens in Power Automate secure parameters
- Never log or display authentication credentials

### 4. Validate Responses
```json
// Check isSuccess field in output
// Parse body based on Content-Type in response headers
```

### 5. Configure Redirect Limits
```json
{
  "followRedirects": true,
  "maxRedirects": 5  // Set reasonable limit to prevent abuse
}
```

### 6. Use Query Parameters Correctly
```json
// Preferred: Use queryParameters field
{
  "queryParameters": "{\"key\":\"value\"}"
}

// Avoid: Manually building query strings (harder to maintain)
{
  "url": "https://api.example.com?key=value"
}
```

## Limitations

### Platform Constraints
- **Maximum Script Size**: 1 MB
- **Execution Timeout**: 2 minutes maximum
- **Redirect Limit**: 50 maximum redirects per request
- **No Async/Background**: All operations are synchronous

### Not Supported
- ❌ File uploads via multipart/form-data (use string encoding instead)
- ❌ Client certificates for mutual TLS
- ❌ HTTP/2 or HTTP/3 (uses HTTP/1.1)
- ❌ Streaming responses (entire response loaded into memory)
- ❌ Custom SSL certificate validation logic
- ❌ Cookie jar persistence across requests
- ❌ Proxy configuration (handled at platform level)

## Comparison with Standard HTTP Connector

| Feature | Standard HTTP | HTTP Request Advanced |
|---------|---------------|----------------------|
| HTTP Methods | ✅ All | ✅ All |
| Custom Headers | ✅ | ✅ JSON format |
| Query Parameters | ✅ | ✅ JSON format |
| Request Body | ✅ | ✅ |
| Authentication | ✅ | ✅ Basic, Bearer |
| **Redirect Handling** | ❌ No | ✅ **Automatic** |
| **Redirect Count** | ❌ No | ✅ **Yes** |
| **Final URL Tracking** | ❌ No | ✅ **Yes** |
| **Redirect Loop Detection** | ❌ No | ✅ **Yes** |
| Configurable Timeout | ✅ | ✅ |
| Response Headers | ✅ | ✅ Full object |
| Detailed Metadata | Partial | ✅ Complete |

## Troubleshooting

### Request Times Out
- Increase `timeoutSeconds` value
- Check if target server is responsive
- Verify network connectivity

### Invalid JSON Error
- Ensure `headers` and `queryParameters` are valid JSON strings
- Use double quotes for JSON property names and string values
- Escape special characters in JSON values

### Too Many Redirects
- Check `maxRedirects` setting
- Verify the target URL isn't in a redirect loop
- Try setting `followRedirects` to `false` to see initial redirect

### Authentication Fails
- Verify credentials are correct
- Check if authentication type matches API requirements
- Ensure Bearer token hasn't expired

### SSL/TLS Errors
- Set `validateSSL` to `false` for testing (not recommended for production)
- Verify server certificate is valid and not expired
- Check if server supports TLS 1.2 or higher

## Security Considerations

1. **Credentials**: Always use Power Automate's secure parameter storage for sensitive data
2. **SSL Validation**: Keep `validateSSL` set to `true` in production environments
3. **URL Validation**: Validate user-provided URLs to prevent SSRF attacks
4. **Response Size**: Be aware of memory limits when handling large responses
5. **Timeout Settings**: Set reasonable timeouts to prevent resource exhaustion

## Deployment

This connector must be manually deployed via the Power Automate portal:

1. Navigate to Power Automate portal
2. Go to Custom Connectors section
3. Create a new custom connector
4. Copy the content from `apiDefinition.swagger.yaml` into the Swagger editor
5. Copy the content from `script.csx` into the Code section
6. Test the connector with sample requests
7. Save and make available to your organization

For detailed deployment instructions, see [DEPLOYMENT_GUIDE.md](../../docs/DEPLOYMENT_GUIDE.md).

## Version History

- **1.1.0** (2026-02-26): Batch HTTP Requests
  - New `ExecuteBatchHttpRequests` operation for parallel multi-request execution
  - Combine paginated or multi-endpoint results into a single array
  - Configurable `resultsPath` with dot-notation for nested JSON extraction
  - Shared authentication settings across all batch requests
  - Up to 20 parallel requests per batch
- **1.0.0** (2026-02-02): Initial release
  - All HTTP methods support
  - Automatic redirect handling (301, 302, 303, 307, 308)
  - JSON-formatted headers and query parameters
  - Basic and Bearer authentication
  - Configurable timeouts and redirect limits
  - Comprehensive error handling
  - Redirect loop detection

## License

This connector is provided as-is for use with Microsoft Power Automate.

## Support

For issues, questions, or contributions, please refer to the repository documentation.
