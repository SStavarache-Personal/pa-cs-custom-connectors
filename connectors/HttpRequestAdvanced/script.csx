// ============================================================================
// HTTP Request Advanced - Custom Connector C# Script
// ============================================================================
// Provides enhanced HTTP request capabilities with redirect handling
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Advanced HTTP request handler with redirect support.
/// </summary>
public class Script : ScriptBase
{
    private const int DEFAULT_MAX_REDIRECTS = 10;
    private const int DEFAULT_TIMEOUT_SECONDS = 100;
    private const int MAX_ALLOWED_REDIRECTS = 50;

    // Binary content type lists (static to avoid repeated allocations)
    private static readonly string[] BinaryContentTypes = new[]
    {
        "application/zip",
        "application/x-zip-compressed",
        "application/octet-stream",
        "application/pdf",
        "application/gzip",
        "application/x-gzip",
        "application/x-tar",
        "application/x-7z-compressed",
        "application/x-rar-compressed",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/bmp",
        "image/webp",
        "image/tiff",
        "audio/mpeg",
        "audio/ogg",
        "audio/wav",
        "video/mp4",
        "video/mpeg",
        "video/quicktime",
        "video/x-msvideo",
        "font/woff",
        "font/woff2",
        "font/ttf",
        "font/otf"
    };

    private static readonly string[] TextContentTypes = new[]
    {
        "image/svg+xml" // SVG is XML-based text
    };

    /// <summary>
    /// Entry point for the custom connector.
    /// </summary>
    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);

        return operationId switch
        {
            "ExecuteHttpRequest" => await HandleExecuteHttpRequestAsync(),
            _ => CreateErrorResponse(
                HttpStatusCode.BadRequest,
                $"Unknown operation: {operationId}",
                "UNKNOWN_OPERATION"
            )
        };
    }

    // ========================================================================
    // OPERATION HANDLERS
    // ========================================================================

    /// <summary>
    /// Handles the ExecuteHttpRequest operation with full redirect support.
    /// </summary>
    private async Task<HttpResponseMessage> HandleExecuteHttpRequestAsync()
    {
        try
        {
            // Read request body
            string content = await this.Context.Request.Content
                .ReadAsStringAsync()
                .ConfigureAwait(false);

            JObject requestBody;
            try
            {
                requestBody = JObject.Parse(content);
            }
            catch (JsonException ex)
            {
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest,
                    $"Invalid JSON in request body: {ex.Message}",
                    "INVALID_JSON"
                );
            }

            // Extract and validate parameters
            string url = requestBody["url"]?.ToString();
            string method = requestBody["method"]?.ToString()?.ToUpperInvariant() ?? "GET";
            string headersJson = requestBody["headers"]?.ToString();
            string queryParamsJson = requestBody["queryParameters"]?.ToString();
            string bodyContent = requestBody["body"]?.ToString();
            bool followRedirects = requestBody["followRedirects"]?.ToObject<bool>() ?? true;
            int maxRedirects = requestBody["maxRedirects"]?.ToObject<int>() ?? DEFAULT_MAX_REDIRECTS;
            int timeoutSeconds = requestBody["timeoutSeconds"]?.ToObject<int>() ?? DEFAULT_TIMEOUT_SECONDS;
            string authenticationType = requestBody["authenticationType"]?.ToString() ?? "None";
            string username = requestBody["username"]?.ToString();
            string password = requestBody["password"]?.ToString();
            string bearerToken = requestBody["bearerToken"]?.ToString();
            string contentType = requestBody["contentType"]?.ToString();
            bool validateSSL = requestBody["validateSSL"]?.ToObject<bool>() ?? true;

            // Validate required parameters
            if (string.IsNullOrWhiteSpace(url))
            {
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest,
                    "URL is required.",
                    "MISSING_URL"
                );
            }

            // Validate URL format
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri targetUri))
            {
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest,
                    "Invalid URL format.",
                    "INVALID_URL"
                );
            }

            // Validate max redirects
            if (maxRedirects < 0 || maxRedirects > MAX_ALLOWED_REDIRECTS)
            {
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest,
                    $"Max redirects must be between 0 and {MAX_ALLOWED_REDIRECTS}.",
                    "INVALID_MAX_REDIRECTS"
                );
            }

            // Parse headers
            Dictionary<string, string> headers = ParseJsonDictionary(headersJson, "headers");

            // Parse query parameters and append to URL
            string finalUrl = BuildUrlWithQueryParams(url, queryParamsJson);

            // Execute request with redirect handling
            HttpRequestResult result = await ExecuteRequestWithRedirectsAsync(
                finalUrl,
                method,
                headers,
                bodyContent,
                followRedirects,
                maxRedirects,
                timeoutSeconds,
                authenticationType,
                username,
                password,
                bearerToken,
                contentType
            ).ConfigureAwait(false);

            // Build response
            JObject responseBody = new JObject
            {
                ["statusCode"] = (int)result.StatusCode,
                ["statusDescription"] = result.StatusCode.ToString(),
                ["headers"] = JObject.FromObject(result.Headers),
                ["body"] = result.Body,
                ["isBase64Encoded"] = result.IsBase64Encoded,
                ["redirectCount"] = result.RedirectCount,
                ["finalUrl"] = result.FinalUrl,
                ["isSuccess"] = result.IsSuccess
            };

            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = CreateJsonContent(responseBody.ToString())
            };

            return response;
        }
        catch (TaskCanceledException ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.RequestTimeout,
                $"Request timed out: {ex.Message}",
                "REQUEST_TIMEOUT"
            );
        }
        catch (HttpRequestException ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadGateway,
                $"HTTP request failed: {ex.Message}",
                "HTTP_REQUEST_FAILED"
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.InternalServerError,
                $"An unexpected error occurred: {ex.Message}",
                "INTERNAL_ERROR"
            );
        }
    }

    // ========================================================================
    // HTTP REQUEST HANDLING
    // ========================================================================

    /// <summary>
    /// Executes an HTTP request with redirect handling.
    /// </summary>
    private async Task<HttpRequestResult> ExecuteRequestWithRedirectsAsync(
        string url,
        string method,
        Dictionary<string, string> headers,
        string body,
        bool followRedirects,
        int maxRedirects,
        int timeoutSeconds,
        string authenticationType,
        string username,
        string password,
        string bearerToken,
        string contentType
    )
    {
        string currentUrl = url;
        int redirectCount = 0;
        HttpResponseMessage response = null;
        HashSet<string> visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Create cancellation token with timeout
        CancellationTokenSource timeoutCts = CancellationTokenSource
            .CreateLinkedTokenSource(this.CancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            while (true)
            {
                // Check for redirect loop
                if (visitedUrls.Contains(currentUrl))
                {
                    throw new InvalidOperationException(
                        $"Redirect loop detected: {currentUrl}"
                    );
                }
                visitedUrls.Add(currentUrl);

                // Create request
                HttpRequestMessage request = CreateHttpRequest(
                    currentUrl,
                    method,
                    headers,
                    body,
                    authenticationType,
                    username,
                    password,
                    bearerToken,
                    contentType
                );

                // Execute request
                response = await this.Context.SendAsync(request, timeoutCts.Token)
                    .ConfigureAwait(false);

                // Check if we should follow redirect
                if (followRedirects && IsRedirectStatusCode(response.StatusCode))
                {
                    if (redirectCount >= maxRedirects)
                    {
                        throw new InvalidOperationException(
                            $"Maximum redirects ({maxRedirects}) exceeded."
                        );
                    }

                    // Get redirect location
                    string location = response.Headers.Location?.ToString();
                    if (string.IsNullOrWhiteSpace(location))
                    {
                        // No location header, stop redirecting
                        break;
                    }

                    // Handle relative URLs
                    if (!Uri.TryCreate(location, UriKind.Absolute, out Uri redirectUri))
                    {
                        Uri baseUri = new Uri(currentUrl);
                        redirectUri = new Uri(baseUri, location);
                    }

                    currentUrl = redirectUri.ToString();
                    redirectCount++;

                    // For 303, change method to GET (per HTTP spec)
                    if (response.StatusCode == HttpStatusCode.SeeOther ||
                        response.StatusCode == HttpStatusCode.RedirectMethod)
                    {
                        method = "GET";
                        body = null; // Clear body for GET requests
                    }

                    // Continue to next iteration
                    continue;
                }

                // Not a redirect or not following redirects, we're done
                break;
            }

            // Extract response headers first (needed to check content type)
            Dictionary<string, string> responseHeaders = ExtractHeaders(response);

            // Check if content is binary
            bool isBinary = IsBinaryContent(responseHeaders);
            string responseBody;
            bool isBase64Encoded = false;

            if (isBinary)
            {
                // Read as bytes and encode as Base64 for binary content
                byte[] responseBytes = await response.Content
                    .ReadAsByteArrayAsync()
                    .ConfigureAwait(false);
                responseBody = Convert.ToBase64String(responseBytes);
                isBase64Encoded = true;
            }
            else
            {
                // Read as string for text content
                responseBody = await response.Content
                    .ReadAsStringAsync()
                    .ConfigureAwait(false);
            }

            return new HttpRequestResult
            {
                StatusCode = response.StatusCode,
                Headers = responseHeaders,
                Body = responseBody,
                IsBase64Encoded = isBase64Encoded,
                RedirectCount = redirectCount,
                FinalUrl = currentUrl,
                IsSuccess = response.IsSuccessStatusCode
            };
        }
        finally
        {
            response?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    /// <summary>
    /// Creates an HTTP request message with all specified parameters.
    /// </summary>
    private HttpRequestMessage CreateHttpRequest(
        string url,
        string method,
        Dictionary<string, string> headers,
        string body,
        string authenticationType,
        string username,
        string password,
        string bearerToken,
        string contentType
    )
    {
        HttpRequestMessage request = new HttpRequestMessage(
            new HttpMethod(method),
            url
        );

        // Add headers
        if (headers != null)
        {
            foreach (var kvp in headers)
            {
                // Skip Content-Type header here, it will be set with content
                if (kvp.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Try to add to request headers, if it fails, it might be a content header
                try
                {
                    request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                }
                catch
                {
                    // Ignore invalid headers
                }
            }
        }

        // Add authentication
        if (authenticationType.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(username))
            {
                string credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{username}:{password ?? string.Empty}")
                );
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }
        }
        else if (authenticationType.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }
        }

        // Add body if present and method supports it
        if (!string.IsNullOrEmpty(body) && 
            !method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            // Determine content type
            string effectiveContentType = contentType;
            if (string.IsNullOrWhiteSpace(effectiveContentType) && headers != null)
            {
                headers.TryGetValue("Content-Type", out effectiveContentType);
            }
            if (string.IsNullOrWhiteSpace(effectiveContentType))
            {
                effectiveContentType = "application/json";
            }

            request.Content = new StringContent(body, Encoding.UTF8, effectiveContentType);
        }

        return request;
    }

    /// <summary>
    /// Checks if a status code is a redirect.
    /// </summary>
    private bool IsRedirectStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.MovedPermanently ||    // 301
               statusCode == HttpStatusCode.Found ||                // 302
               statusCode == HttpStatusCode.SeeOther ||             // 303
               statusCode == HttpStatusCode.TemporaryRedirect ||    // 307
               statusCode == (HttpStatusCode)308;                   // 308 Permanent Redirect
    }

    // ========================================================================
    // HELPER METHODS
    // ========================================================================

    /// <summary>
    /// Parses a JSON string into a dictionary.
    /// </summary>
    private Dictionary<string, string> ParseJsonDictionary(string json, string paramName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            JObject obj = JObject.Parse(json);
            Dictionary<string, string> result = new Dictionary<string, string>();

            foreach (var property in obj.Properties())
            {
                result[property.Name] = property.Value?.ToString() ?? string.Empty;
            }

            return result;
        }
        catch (JsonException)
        {
            throw new ArgumentException($"Invalid JSON format in {paramName}.");
        }
    }

    /// <summary>
    /// Builds a URL with query parameters.
    /// </summary>
    private string BuildUrlWithQueryParams(string baseUrl, string queryParamsJson)
    {
        if (string.IsNullOrWhiteSpace(queryParamsJson))
        {
            return baseUrl;
        }

        Dictionary<string, string> queryParams = ParseJsonDictionary(queryParamsJson, "queryParameters");
        
        if (queryParams.Count == 0)
        {
            return baseUrl;
        }

        UriBuilder uriBuilder = new UriBuilder(baseUrl);
        StringBuilder queryString = new StringBuilder(uriBuilder.Query);

        foreach (var kvp in queryParams)
        {
            if (queryString.Length > 0 && queryString[0] == '?')
            {
                queryString.Append("&");
            }
            else if (queryString.Length > 0)
            {
                queryString.Append("&");
            }
            else
            {
                queryString.Append("?");
            }

            queryString.Append(Uri.EscapeDataString(kvp.Key));
            queryString.Append("=");
            queryString.Append(Uri.EscapeDataString(kvp.Value));
        }

        uriBuilder.Query = queryString.ToString().TrimStart('?');
        return uriBuilder.ToString();
    }

    /// <summary>
    /// Determines if the response content is binary based on Content-Type header.
    /// </summary>
    private bool IsBinaryContent(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Content-Type", out string contentType))
        {
            return false;
        }

        // Normalize content type (remove charset and other parameters)
        string normalizedType = contentType.Split(';')[0].Trim().ToLowerInvariant();

        // Check if it's explicitly a text type
        foreach (string textType in TextContentTypes)
        {
            if (normalizedType == textType)
            {
                return false;
            }
        }

        // Check if content type matches any binary type
        foreach (string binaryType in BinaryContentTypes)
        {
            if (normalizedType == binaryType)
            {
                return true;
            }
        }

        // Check for common binary prefixes
        if (normalizedType.StartsWith("image/") ||
            normalizedType.StartsWith("audio/") ||
            normalizedType.StartsWith("video/") ||
            normalizedType.StartsWith("font/"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts headers from an HTTP response.
    /// </summary>
    private Dictionary<string, string> ExtractHeaders(HttpResponseMessage response)
    {
        Dictionary<string, string> headers = new Dictionary<string, string>();

        // Add response headers
        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        // Add content headers
        if (response.Content != null)
        {
            foreach (var header in response.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        return headers;
    }

    /// <summary>
    /// Decodes an operation ID (handles base64 encoding in some regions).
    /// </summary>
    private string DecodeOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return operationId;
        }

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

    /// <summary>
    /// Creates an error response with a standard format.
    /// </summary>
    private HttpResponseMessage CreateErrorResponse(
        HttpStatusCode statusCode,
        string message,
        string errorCode = "ERROR"
    )
    {
        JObject error = new JObject
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

    /// <summary>
    /// Creates JSON content for HTTP responses.
    /// </summary>
    private StringContent CreateJsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    // ========================================================================
    // HELPER CLASSES
    // ========================================================================

    /// <summary>
    /// Represents the result of an HTTP request execution.
    /// </summary>
    private class HttpRequestResult
    {
        public HttpStatusCode StatusCode { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public string Body { get; set; }
        public bool IsBase64Encoded { get; set; }
        public int RedirectCount { get; set; }
        public string FinalUrl { get; set; }
        public bool IsSuccess { get; set; }
    }
}
