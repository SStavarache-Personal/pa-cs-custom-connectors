// ============================================================================
// Power Automate Custom Connector - C# Script Template
// ============================================================================
// IMPORTANT: This script must:
//   - Define a class named "Script" that inherits from "ScriptBase"
//   - Implement the "ExecuteAsync" method
//   - Use only the allowed namespaces (see .ai/INSTRUCTIONS.md)
//   - Complete execution within 2 minutes
//   - Be less than 1 MB in size
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Main script class for the custom connector.
/// Must be named "Script" and inherit from "ScriptBase".
/// </summary>
public class Script : ScriptBase
{
    /// <summary>
    /// Entry point called by Power Platform runtime for each request.
    /// Routes to appropriate handler based on OperationId from Swagger definition.
    /// </summary>
    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        // Decode OperationId (handles base64 encoding issue in some regions)
        string operationId = DecodeOperationId(this.Context.OperationId);

        // Route to appropriate handler based on OperationId
        // OperationId must match the operationId in apiDefinition.swagger.json
        return operationId switch
        {
            "GetItems" => await HandleGetItemsAsync(),
            "GetItemById" => await HandleGetItemByIdAsync(),
            "CreateItem" => await HandleCreateItemAsync(),
            "DeleteItem" => await HandleDeleteItemAsync(),
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
    /// Handles the GetItems operation.
    /// </summary>
    private async Task<HttpResponseMessage> HandleGetItemsAsync()
    {
        // Forward the request to the backend API
        HttpResponseMessage response = await this.Context.SendAsync(
            this.Context.Request,
            this.CancellationToken
        ).ConfigureAwait(false);

        // Transform response if needed
        if (response.IsSuccessStatusCode)
        {
            string content = await response.Content
                .ReadAsStringAsync()
                .ConfigureAwait(false);

            // Parse and potentially transform the response
            JObject responseBody = JObject.Parse(content);

            // Example: Add a custom field
            responseBody["processedAt"] = DateTime.UtcNow.ToString("o");

            response.Content = CreateJsonContent(responseBody.ToString());
        }

        return response;
    }

    /// <summary>
    /// Handles the GetItemById operation.
    /// </summary>
    private async Task<HttpResponseMessage> HandleGetItemByIdAsync()
    {
        // Forward request to backend
        HttpResponseMessage response = await this.Context.SendAsync(
            this.Context.Request,
            this.CancellationToken
        ).ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// Handles the CreateItem operation.
    /// </summary>
    private async Task<HttpResponseMessage> HandleCreateItemAsync()
    {
        // Read and validate request body
        string content = await this.Context.Request.Content
            .ReadAsStringAsync()
            .ConfigureAwait(false);

        JObject requestBody;
        try
        {
            requestBody = JObject.Parse(content);
        }
        catch (JsonException)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "Invalid JSON in request body.",
                "INVALID_JSON"
            );
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(requestBody["name"]?.ToString()))
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "The 'name' field is required.",
                "MISSING_REQUIRED_FIELD"
            );
        }

        // Forward to backend
        HttpResponseMessage response = await this.Context.SendAsync(
            this.Context.Request,
            this.CancellationToken
        ).ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// Handles the DeleteItem operation.
    /// </summary>
    private async Task<HttpResponseMessage> HandleDeleteItemAsync()
    {
        // Forward request to backend
        HttpResponseMessage response = await this.Context.SendAsync(
            this.Context.Request,
            this.CancellationToken
        ).ConfigureAwait(false);

        return response;
    }

    // ========================================================================
    // HELPER METHODS
    // ========================================================================

    /// <summary>
    /// Decodes the OperationId, handling base64 encoding in some regions.
    /// </summary>
    /// <param name="operationId">The raw operation ID from context.</param>
    /// <returns>The decoded operation ID.</returns>
    private static string DecodeOperationId(string operationId)
    {
        try
        {
            byte[] data = Convert.FromBase64String(operationId);
            return Encoding.UTF8.GetString(data);
        }
        catch (FormatException)
        {
            // Not base64 encoded, return as-is
            return operationId;
        }
    }

    /// <summary>
    /// Creates a JSON response with the specified status code and body.
    /// </summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="body">JSON object to return.</param>
    /// <returns>HTTP response message.</returns>
    private HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, JObject body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = CreateJsonContent(body.ToString())
        };
    }

    /// <summary>
    /// Creates a standardized error response.
    /// </summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="message">Error message.</param>
    /// <param name="code">Error code for programmatic handling.</param>
    /// <returns>HTTP response message with error details.</returns>
    private HttpResponseMessage CreateErrorResponse(
        HttpStatusCode statusCode,
        string message,
        string code)
    {
        JObject errorBody = new JObject
        {
            ["error"] = new JObject
            {
                ["message"] = message,
                ["code"] = code
            }
        };

        return CreateJsonResponse(statusCode, errorBody);
    }

    /// <summary>
    /// Creates a success response with data.
    /// </summary>
    /// <param name="data">The data to include in the response.</param>
    /// <returns>HTTP response message with data.</returns>
    private HttpResponseMessage CreateSuccessResponse(JToken data)
    {
        JObject responseBody = new JObject
        {
            ["success"] = true,
            ["data"] = data
        };

        return CreateJsonResponse(HttpStatusCode.OK, responseBody);
    }
}
